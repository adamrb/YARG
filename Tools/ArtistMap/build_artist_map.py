#!/usr/bin/env python3
"""Build YARG's artist map from ListenBrainz listening data (CC0).

1. Start from the most-listened artists across all ListenBrainz users (sitewide all-time stats, which
   list the top 1,000), then keep expanding to the artists most often named as similar to the ones
   already fetched, until --top artists have their ListenBrainz "similar artists" list (derived from
   co-listening sessions). Every genre is reached through its neighbors.
3. Build a graph (edge weight = similarity score, normalized per source artist, symmetrized), keep
   artists that are linked often enough to place reliably, weight edges by PPMI, and embed with a
   truncated SVD. Artists that the same people listen to end up close together.
4. Write "normalized name<TAB>values" lines, normalized the way the game normalizes artist names.

Nothing here depends on any particular player's library. Responses are cached in --cache, so the
script can be stopped and rerun.

    uv run --with numpy --with scipy build_artist_map.py --top 10000 --out artist-map.tsv
"""
import argparse, json, math, os, time, unicodedata, urllib.request
from concurrent.futures import ThreadPoolExecutor
import threading

API = "https://api.listenbrainz.org/1/stats/sitewide/artists"
SIMILAR = "https://labs.api.listenbrainz.org/similar-artists/json"
ALGORITHM = "session_based_days_7500_session_300_contribution_5_threshold_10_limit_100_filter_True_skip_30"
UA = "YARG-artist-map/1.0 (https://github.com/YARC-Official/YARG)"

def get(url):
    req = urllib.request.Request(url, headers={"User-Agent": UA, "Accept": "application/json"})
    for attempt in range(5):
        try:
            with urllib.request.urlopen(req, timeout=60) as r:
                return json.load(r)
        except Exception:
            if attempt == 4: raise
            time.sleep(3 * (attempt + 1))

def normalize(name):
    """Mirror of SongNormalizer.Artist in the game: accents removed, brackets removed, lower case,
    anything that is not a letter or digit becomes a space, and a leading "the " is dropped."""
    text = unicodedata.normalize("NFKD", name)
    text = "".join(c for c in text if not unicodedata.combining(c))
    out, depth = [], 0
    for c in text:
        if c in "([{": depth += 1; continue
        if c in ")]}": depth = max(0, depth - 1); continue
        if depth == 0: out.append(c)
    text = " ".join("".join(c if c.isalnum() else " " for c in "".join(out).lower()).split())
    return text[4:] if text.startswith("the ") else text

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--top", type=int, default=10000, help="how many artists to fetch similar lists for")
    ap.add_argument("--dims", type=int, default=32)
    ap.add_argument("--min-links", type=int, default=3, help="drop artists linked fewer times than this")
    ap.add_argument("--cache", default="artist-map-cache.json")
    ap.add_argument("--out", default="artist-map.tsv")
    a = ap.parse_args()

    cache = json.load(open(a.cache)) if os.path.exists(a.cache) else {"top": [], "similar": {}}
    lock = threading.Lock()
    def save():
        json.dump(cache, open(a.cache + ".tmp", "w")); os.replace(a.cache + ".tmp", a.cache)

    if not cache["top"]:
        offset = 0
        while True:
            page = get(f"{API}?count=1000&offset={offset}&range=all_time")["payload"]["artists"]
            if not page: break
            offset += len(page)
            cache["top"] +=[{"id": x["artist_mbid"], "name": x["artist_name"]} for x in page if x.get("artist_mbid")]
        save(); print(f"sitewide top artists: {len(cache['top'])}", flush=True)

    next_slot = [time.time()]
    def similar(mbid):
        with lock:
            wait = max(0, next_slot[0] - time.time()); next_slot[0] = max(time.time(), next_slot[0]) + 0.25
        time.sleep(wait)
        try:
            data = get(f"{SIMILAR}?artist_mbids={mbid}&algorithm={ALGORITHM}")
        except Exception:
            return mbid, None
        return mbid, [{"id": d["artist_mbid"], "name": d["name"], "score": d["score"]} for d in data][:100]

    # Breadth-first expansion: the next wave is the unfetched artists the fetched ones link to most
    fetched = [x["id"] for x in cache["top"]]
    names = {x["id"]: x["name"] for x in cache["top"]}
    with ThreadPoolExecutor(4) as pool:
        while True:
            todo = [m for m in fetched if m not in cache["similar"]]
            for n, (mbid, result) in enumerate(pool.map(similar, todo), 1):
                if result is not None:  # failed requests stay uncached so a rerun retries them
                    cache["similar"][mbid] = result
                if n % 250 == 0:
                    with lock: save()
                    print(f"similar lists: {sum(1 for m in fetched if m in cache['similar'])}/{a.top}", flush=True)
            save()
            if len(fetched) >= a.top:
                break
            counts = {}
            for m in fetched:
                for d in cache["similar"].get(m) or []:
                    names.setdefault(d["id"], d["name"])
                    counts[d["id"]] = counts.get(d["id"], 0) + 1
            known = set(fetched)
            wave = sorted((m for m in counts if m not in known), key=lambda m: -counts[m])
            if not wave:
                break
            fetched += wave[:min(a.top - len(fetched), max(500, len(fetched)))]
    top = [{"id": m, "name": names.get(m, "")} for m in fetched[:a.top]]

    import numpy as np
    from scipy.sparse import coo_matrix
    from scipy.sparse.linalg import svds

    names = {x["id"]: x["name"] for x in top}
    links = {}
    for src in (x["id"] for x in top):
        for d in cache["similar"].get(src) or []:
            names.setdefault(d["id"], d["name"])
            links[d["id"]] = links.get(d["id"], 0) + 1
    keep = {x["id"] for x in top if cache["similar"].get(x["id"])} | {m for m, c in links.items() if c >= a.min_links}
    index = {m: i for i, m in enumerate(sorted(keep))}
    rows, cols, vals = [], [], []
    for src in (x["id"] for x in top):
        lst = cache["similar"].get(src) or []
        if src not in index or not lst: continue
        best = max(d["score"] for d in lst) or 1
        for d in lst:
            if d["id"] not in index: continue
            w = d["score"] / best
            rows += [index[src], index[d["id"]]]; cols += [index[d["id"]], index[src]]; vals += [w, w]
    n = len(index)
    A = coo_matrix((vals, (rows, cols)), shape=(n, n)).tocsr(); A.sum_duplicates(); A = A.tocoo()
    degree = np.asarray(A.tocsr().sum(axis=1)).ravel()
    pmi = np.log(A.data * degree.sum() / (degree[A.row] * degree[A.col]))
    positive = pmi > 0
    M = coo_matrix((pmi[positive], (A.row[positive], A.col[positive])), shape=(n, n)).tocsr()
    U, S, _ = svds(M.astype(np.float64), k=min(a.dims, n - 2))
    order = np.argsort(-S)
    E = U[:, order] * np.sqrt(S[order])
    E /= np.maximum(np.linalg.norm(E, axis=1, keepdims=True), 1e-9)
    print(f"graph: {n} artists, {M.nnz} weighted links", flush=True)

    written = {}
    for mbid, i in index.items():
        key = normalize(names.get(mbid, ""))
        if key and key not in written:
            written[key] = E[i]
    with open(a.out, "w") as f:
        f.write("# YARG artist map: artists people listen to together sit close together. Built from "
                "ListenBrainz data (CC0) by Tools/ArtistMap/build_artist_map.py. "
                f"Columns: normalized artist name, then {E.shape[1]} values.\n")
        for key in sorted(written):
            f.write(key + "\t" + "\t".join(f"{x:.3f}" for x in written[key]) + "\n")
    print(f"wrote {len(written)} artists to {a.out}", flush=True)

main()
