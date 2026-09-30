#!/usr/bin/env python3
"""Build YARG's song map from a ListenBrainz statistics dump (CC0).

ListenBrainz publishes full data dumps twice a month at
https://data.metabrainz.org/pub/musicbrainz/listenbrainz/fullexport/. The statistics dump contains
recordings_all_time.jsonl: each user's all-time top tracks with listen counts. Songs that the same people
listen to end up close together on the map, which is how the game can tell which songs a player will like
from a handful of plays and ratings.

1. Read every user's tracks, keyed the way the game keys songs (normalized artist and title), keeping
   those they have played at least --min-listens times. Every tenth user is held out: never used for
   training, and written to --eval-out for the recommender's offline evaluation.
2. Keep tracks with at least --min-listeners listeners and factorize the user x track matrix with implicit
   ALS (or a weighted truncated SVD with --method svd). Each track's factor vector, normalized, is its
   position.
3. Place each artist at the listener-weighted mean of its tracks, in the same space, for songs the map has
   no track for.
4. Record how popular each track is overall and within its artist (a hit or a deep cut).
5. Write a gzipped TSV: "a<TAB>artist<TAB>values" and "t<TAB>artist|title<TAB>listeners<TAB>artist
   rank<TAB>values", where artist rank is 0 for the artist's most played track and 1 for the least.

    zstd -dc recordings_all_time.jsonl.zst \\
        | uv run --with numpy --with scipy --with implicit build_song_map.py --source <dump name> -
"""
import argparse, gzip, json, math, os, sys, unicodedata

# ALS brings its own threading; BLAS threads on top slow it down and make results vary between runs
os.environ.setdefault("OPENBLAS_NUM_THREADS", "1")
from array import array
from collections import defaultdict

PLACEHOLDERS = {"unknown name", "unknown artist", "unknown album", "unknown charter", "unknown source"}
ALTERNATE_VERSION = {"demo", "prototype", "beta", "live", "remix", "rehearsal", "alt", "alternate", "cover", "karaoke"}
VERSION_NOTE = ALTERNATE_VERSION | {
    "version", "remaster", "remastered", "mix", "edit", "radio", "single", "album", "feat", "ft", "featuring", "take",
    "retail", "early", "original", "rerecord", "rerecorded", "mono", "stereo", "acoustic", "unplugged", "instrumental",
    "extended", "short", "full", "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec",
}


# The game keys songs by SongNormalizer.Identity of SortString.SearchStr. Keep these functions in step with
# StringTransformations.RemoveDiacritics and SongNormalizer (see SongNormalizerTests).
def search_str(text):
    text = unicodedata.normalize("NFD", text.replace("Æ", "AE"))
    return unicodedata.normalize("NFC", "".join(c for c in text if unicodedata.category(c) not in ("Mn", "Mc", "Cf")).lower())


def collapse(text):
    def keep(c):
        category = unicodedata.category(c)
        return category[0] == "L" or category == "Nd"
    return " ".join("".join(c if keep(c) else " " for c in text.lower()).split())


def bracket_groups(text):
    """Text outside brackets, and the contents of each top-level bracket group."""
    outside, groups, group, depth = [], [], [], 0
    for c in text:
        if c in "([{":
            if depth == 0: group = []
            depth += 1
        elif c in ")]}":
            if depth > 0:
                depth -= 1
                if depth == 0: groups.append("".join(group))
        else:
            (outside if depth == 0 else group).append(c)
    return "".join(outside), groups


def is_version_note(text):
    return any(w in VERSION_NOTE or (len(w) == 4 and w.isdigit()) for w in collapse(text).split())


def known(text):
    collapsed = collapse(text)
    return "" if collapsed in PLACEHOLDERS else collapsed


def artist_key(name):
    outside, _ = bracket_groups(search_str(name))
    words = [w for w in known(outside).split() if w != "and"]
    if len(words) > 1 and words[0] == "the": words = words[1:]
    return " ".join(words)


def title_key(name):
    text = search_str(name)
    # Version notes after dashes: "Song - Live - 2011 Remaster"
    dash = text.rfind(" - ")
    while dash > 0 and is_version_note(text[dash + 3:]):
        text = text[:dash]
        dash = text.rfind(" - ")
    parts = []
    depth, group = 0, []
    for c in text:
        if c in "([{":
            if depth == 0: group = []
            depth += 1
        elif c in ")]}":
            if depth > 0:
                depth -= 1
                if depth == 0 and not is_version_note("".join(group)): parts.append(" " + "".join(group) + " ")
        elif depth == 0:
            parts.append(c)
        else:
            group.append(c)
    return known("".join(parts))


# Bump when song keys change, so a cached matrix from older keys is not reused
CACHE_VERSION = "2"

_song_keys = {}


def song_key(artist_name, track_name):
    """The game's key for a song, or None when the artist or title normalizes to nothing."""
    names = (artist_name, track_name)
    if names not in _song_keys:
        artist, title = artist_key(artist_name), title_key(track_name)
        _song_keys[names] = artist + "|" + title if artist and title else None
    return _song_keys[names]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("input", help="recordings_all_time.jsonl, or - for stdin")
    ap.add_argument("--source", required=True, help="name of the dump, recorded in the output header")
    ap.add_argument("--method", choices=["als", "svd"], default="als")
    ap.add_argument("--dims", type=int, default=32)
    ap.add_argument("--alpha", type=float, default=10.0, help="ALS confidence per unit of log listen count")
    ap.add_argument("--regularization", type=float, default=0.5)
    ap.add_argument("--min-listens", type=int, default=2, help="plays for a track to count for a user")
    ap.add_argument("--min-listeners", type=int, default=5, help="users a track needs to be trained on")
    ap.add_argument("--write-min-listeners", type=int, default=50, help="users a track needs to be written")
    ap.add_argument("--artist-min-listeners", type=int, default=25, help="users an artist needs to be written")
    ap.add_argument("--eval-out", help="write the held-out users' tracks here (JSON lines)")
    ap.add_argument("--matrix-cache", help="save or load the parsed matrix (.npz) to skip parsing")
    ap.add_argument("--out", default="song-map.tsv.gz")
    a = ap.parse_args()

    import numpy as np
    from scipy.sparse import csr_matrix, diags

    # The parsed matrix can be cached for experiments; it is only reused for the same dump and settings
    cache_info = np.array([a.source, str(a.min_listens), CACHE_VERSION])
    if a.matrix_cache and os.path.exists(a.matrix_cache):
        if a.eval_out:
            sys.exit("--eval-out needs the dump itself; it cannot be written from --matrix-cache")
        cache = np.load(a.matrix_cache, allow_pickle=True)
        if "info" not in cache or list(cache["info"]) != list(cache_info):
            sys.exit(f"{a.matrix_cache} was built from other inputs; delete it or pick another --matrix-cache")
        rows, cols, vals = cache["rows"], cache["cols"], cache["vals"]
        keys = list(cache["keys"])
        users = int(cache["users"])
        print(f"loaded {users} users, {len(keys)} tracks from cache", flush=True)
    else:
        index, keys = {}, []
        rows, cols, vals = array("i"), array("i"), array("f")
        users = 0
        eval_file = open(a.eval_out, "w") if a.eval_out else None
        for line in (sys.stdin if a.input == "-" else open(a.input, encoding="utf-8")):
            record = json.loads(line)
            # Versions of a song ("Song", "Song - 2011 Remaster") are merged before the listen minimum
            counts = defaultdict(int)
            for track in record["data"]:
                if track.get("track_name") and track.get("artist_name"):
                    key = song_key(track["artist_name"], track["track_name"])
                    if key:
                        counts[key] += track.get("listen_count", 0)
            counts = {key: count for key, count in counts.items() if count >= a.min_listens}
            if not counts:
                continue
            if record["user_id"] % 10 == 0:
                if eval_file: eval_file.write(json.dumps({"user": record["user_id"], "tracks": counts}) + "\n")
                continue
            for key, count in counts.items():
                if key not in index:
                    index[key] = len(keys)
                    keys.append(key)
                rows.append(users); cols.append(index[key]); vals.append(math.log1p(count))
            users += 1
        if eval_file: eval_file.close()
        rows, cols, vals = np.frombuffer(rows, np.int32), np.frombuffer(cols, np.int32), np.frombuffer(vals, np.float32)
        if a.matrix_cache:
            np.savez(a.matrix_cache, rows=rows, cols=cols, vals=vals, keys=np.array(keys, dtype=object), users=users,
                     info=cache_info)
        print(f"{users} training users, {len(keys)} tracks, {len(vals)} user-track pairs", flush=True)

    X = csr_matrix((vals, (rows, cols)), shape=(users, len(keys)))
    listeners = np.bincount(X.indices, minlength=len(keys))
    keep = np.flatnonzero(listeners >= a.min_listeners)
    X = X[:, keep].tocsr()
    X = X[np.asarray(X.getnnz(axis=1)).ravel() > 0]
    print(f"training on {len(keep)} tracks with at least {a.min_listeners} listeners, {X.shape[0]} users", flush=True)

    if a.method == "svd":
        from scipy.sparse.linalg import svds
        W = diags(1 / np.maximum(np.sqrt(X.multiply(X).sum(axis=1)).A1, 1e-9)) @ X
        W = W @ diags(np.log(X.shape[0] / listeners[keep]).astype(np.float32))
        _, S, Vt = svds(W.astype(np.float64), k=a.dims, random_state=0)
        E = Vt.T[:, np.argsort(-S)] * np.sqrt(np.sort(S)[::-1])
    else:
        import implicit
        # Confidence grows with how much a user plays a track (values are already log listen counts)
        # One thread, so the same dump always gives the same map
        model = implicit.cpu.als.AlternatingLeastSquares(factors=a.dims, regularization=a.regularization, iterations=20,
                                                         alpha=a.alpha, random_state=0, num_threads=1)
        model.fit(X.astype(np.float32))
        E = np.asarray(model.item_factors)
    E /= np.maximum(np.linalg.norm(E, axis=1, keepdims=True), 1e-9)

    track_keys = [keys[i] for i in keep]
    track_listeners = listeners[keep]
    by_artist = defaultdict(list)
    for i, key in enumerate(track_keys):
        by_artist[key.split("|", 1)[0]].append(i)

    # People who listen to any of an artist's songs, each counted once
    artists = sorted(by_artist)
    artist_index = {artist: n for n, artist in enumerate(artists)}
    track_artist = csr_matrix((np.ones(len(track_keys), np.float32),
                               (np.arange(len(track_keys)), [artist_index[k.split("|", 1)[0]] for k in track_keys])),
                              shape=(len(track_keys), len(artists)))
    artist_listeners = np.asarray(((X > 0).astype(np.float32) @ track_artist > 0).sum(axis=0)).ravel()

    written_tracks = 0
    with gzip.open(a.out, "wt", encoding="utf-8", compresslevel=9) as f:
        f.write(f"# YARG song map: songs people listen to together sit close together. Built from {a.source} "
                f"(ListenBrainz, CC0) by Tools/SongMap/build_song_map.py ({a.method}, {a.dims} dimensions).\n")
        for artist in artists:
            tracks = sorted(by_artist[artist], key=lambda i: -track_listeners[i])
            if artist_listeners[artist_index[artist]] >= a.artist_min_listeners:
                position = (E[tracks] * track_listeners[tracks, None]).sum(axis=0)
                position /= max(np.linalg.norm(position), 1e-9)
                f.write("a\t" + artist + "\t" + "\t".join(f"{x:.2f}" for x in position) + "\n")
            for rank, i in enumerate(tracks):
                if track_listeners[i] < a.write_min_listeners:
                    continue
                share = rank / (len(tracks) - 1) if len(tracks) > 1 else 0.0
                f.write(f"t\t{track_keys[i]}\t{track_listeners[i]}\t{share:.2f}\t" + "\t".join(f"{x:.2f}" for x in E[i]) + "\n")
                written_tracks += 1
    print(f"wrote {written_tracks} tracks and their artists to {a.out}", flush=True)


if __name__ == "__main__":
    main()
