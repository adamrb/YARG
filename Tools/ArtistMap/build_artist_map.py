#!/usr/bin/env python3
"""Build YARG's artist map from a ListenBrainz statistics dump (CC0).

ListenBrainz publishes full data dumps twice a month at
https://data.metabrainz.org/pub/musicbrainz/listenbrainz/fullexport/. The statistics dump starts with
artists_all_time.jsonl: each user's all-time top artists with listen counts. Artists that the same people
listen to end up close together on the map.

1. Read every user's artists, keeping those they have played at least --min-listens times.
2. Keep artists with at least --min-listeners listeners, so each one is placed from enough data.
3. Weight the user x artist matrix (log listen count, scaled down for artists everyone plays) and take a
   truncated SVD. Each artist's row, scaled and normalized, is its position.
4. Write gzipped "normalized name<TAB>values" lines, normalized the way the game normalizes artist names.

Only the first file of the archive is needed, so it can be streamed without downloading the rest:

    curl -s <dump>/listenbrainz-statistics-dump-<date>.tar.zst | zstd -dc \\
        | tar -xO --occurrence=1 --wildcards '*/artists_all_time.jsonl' \\
        | uv run --with numpy --with scipy build_artist_map.py --source listenbrainz-statistics-dump-<date> -
"""
import argparse, gzip, json, math, sys, unicodedata
from array import array


def normalize(name):
    """Mirror of SongNormalizer.Artist in the game: accents removed, brackets removed, lower case,
    anything that is not a letter or digit becomes a space, "and" is dropped, and so is a leading "the"."""
    text = unicodedata.normalize("NFKD", name)
    text = "".join(c for c in text if not unicodedata.combining(c))
    out, depth = [], 0
    for c in text:
        if c in "([{": depth += 1; continue
        if c in ")]}": depth = max(0, depth - 1); continue
        if depth == 0: out.append(c)
    words = [w for w in "".join(c if c.isalnum() else " " for c in "".join(out).lower()).split() if w != "and"]
    if len(words) > 1 and words[0] == "the": words = words[1:]
    return " ".join(words)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("input", help="artists_all_time.jsonl, or - for stdin")
    ap.add_argument("--source", required=True, help="name of the dump, recorded in the output header")
    ap.add_argument("--dims", type=int, default=32)
    ap.add_argument("--min-listens", type=int, default=3, help="plays for an artist to count for a user")
    ap.add_argument("--min-listeners", type=int, default=25, help="users an artist needs to be placed")
    ap.add_argument("--out", default="artist-map.tsv.gz")
    a = ap.parse_args()

    import numpy as np
    from scipy.sparse import csr_matrix, diags
    from scipy.sparse.linalg import svds

    index, names = {}, []
    rows, cols, vals = array("i"), array("i"), array("f")
    users = 0
    for line in (sys.stdin if a.input == "-" else open(a.input, encoding="utf-8")):
        seen = False
        for artist in json.loads(line)["data"]:
            mbid, count = artist.get("artist_mbid"), artist.get("listen_count", 0)
            if not mbid or count < a.min_listens:
                continue
            if mbid not in index:
                index[mbid] = len(names)
                names.append(artist.get("artist_name") or "")
            rows.append(users); cols.append(index[mbid]); vals.append(math.log1p(count))
            seen = True
        users += seen
    print(f"{users} users, {len(names)} artists, {len(vals)} user-artist pairs", flush=True)

    X = csr_matrix((np.frombuffer(vals, np.float32), (np.frombuffer(rows, np.int32), np.frombuffer(cols, np.int32))),
                   shape=(users, len(names)))
    listeners = np.bincount(X.indices, minlength=len(names))
    keep = np.flatnonzero(listeners >= a.min_listeners)
    X = X[:, keep]
    X = diags(1 / np.maximum(np.sqrt(X.multiply(X).sum(axis=1)).A1, 1e-9)) @ X  # each user counts once
    X = X @ diags(np.log(users / listeners[keep]).astype(np.float32))              # rarer artists say more
    U, S, Vt = svds(X.astype(np.float64), k=a.dims, random_state=0)
    E = Vt.T[:, np.argsort(-S)] * np.sqrt(np.sort(S)[::-1])
    E /= np.maximum(np.linalg.norm(E, axis=1, keepdims=True), 1e-9)
    print(f"placed {len(keep)} artists with at least {a.min_listeners} listeners", flush=True)

    # When two artists normalize to the same name, the one more people listen to wins
    written = {}
    for i in sorted(range(len(keep)), key=lambda i: -listeners[keep[i]]):
        key = normalize(names[keep[i]])
        if key and key not in written:
            written[key] = E[i]
    # Two decimals place artists as well as three do, and gzip keeps the shipped file small
    with gzip.open(a.out, "wt", encoding="utf-8", compresslevel=9) as f:
        f.write(f"# YARG artist map: artists people listen to together sit close together. Built from {a.source} "
                "(ListenBrainz, CC0) by Tools/ArtistMap/build_artist_map.py. "
                f"Columns: normalized artist name, then {E.shape[1]} values.\n")
        for key in sorted(written):
            f.write(key + "\t" + "\t".join(f"{x:.2f}" for x in written[key]) + "\n")
    print(f"wrote {len(written)} artists to {a.out}", flush=True)


main()
