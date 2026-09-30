#!/usr/bin/env python3
"""Build artist embeddings from ListenBrainz similar-artist lists (CC0 data).

Artists become nodes of a co-listening graph (edge weight = ListenBrainz similarity score, normalized
per source artist and symmetrized). A truncated SVD of the degree-normalized adjacency matrix places
artists that the same people listen to near each other. Output: one line per library artist,
"<normalized name>\t<d1>...<dK>", with the same name normalization the game uses.
Run: uv run --with numpy --with scipy embed.py cache.json songs.tsv out.tsv
"""
import csv, json, re, sys, unicodedata
import numpy as np
from scipy.sparse import coo_matrix, diags
from scipy.sparse.linalg import svds

K = 32
cache = json.load(open(sys.argv[1]))
songs_tsv, out_path = sys.argv[2], sys.argv[3]

def clean(name):
    return re.sub(r"\s+", " ", re.sub(r"\([^)]*\)|\[[^\]]*\]", "", name).strip())

def normalize(name):
    # Mirrors SongNormalizer.Artist: strip brackets, lowercase, non-alphanumerics to spaces, drop "the "
    # The game looks artists up by SearchStr, which has diacritics removed ("björk" -> "bjork")
    text = unicodedata.normalize("NFKD", name)
    text = "".join(c for c in text if not unicodedata.combining(c))
    text = re.sub(r"\([^)]*\)|\[[^\]]*\]|\{[^}]*\}", "", text).lower()
    text = " ".join("".join(c if c.isalnum() else " " for c in text).split())
    return text[4:] if text.startswith("the ") else text

nodes, index = [], {}
def node(mbid):
    if mbid not in index:
        index[mbid] = len(nodes); nodes.append(mbid)
    return index[mbid]

rows, cols, vals = [], [], []
for src, lst in cache["similar"].items():
    if not lst: continue
    top = max(d["score"] for d in lst) or 1
    i = node(src)
    for d in lst:
        j = node(d["id"]); w = d["score"] / top
        rows += [i, j]; cols += [j, i]; vals += [w, w]
n = len(nodes)
A = coo_matrix((vals, (rows, cols)), shape=(n, n)).tocsr()
A.sum_duplicates()
# PPMI (as in word2vec-style embeddings): how much more two artists co-occur than their overall
# popularity predicts, then a truncated SVD. Unlike the plain normalized adjacency, this does not
# collapse into one trivial dimension per disconnected island.
A = A.tocoo()
deg = np.asarray(A.tocsr().sum(axis=1)).ravel()
total = deg.sum()
pmi = np.log(A.data * total / (deg[A.row] * deg[A.col]))
keep = pmi > 0
M = coo_matrix((pmi[keep], (A.row[keep], A.col[keep])), shape=(n, n)).tocsr()
k = min(K, n - 2)
U, S, _ = svds(M.astype(np.float64), k=k)
order = np.argsort(-S)
E = U[:, order] * np.sqrt(S[order])
E /= np.maximum(np.linalg.norm(E, axis=1, keepdims=True), 1e-9)
print(f"{n} artists in the graph, {A.nnz} edges, top singular values {np.round(S[order][:5], 3)}")

# Every artist in the graph is written under its ListenBrainz name, so the file is general-purpose
# data rather than a list of one player's library. Library spellings are added as aliases when they
# normalize differently, so this library's songs still find their artists.
names = {}
for lst in cache["similar"].values():
    for d in lst:
        names.setdefault(d["id"], d["name"])
for m in cache["mbid"].values():
    if m: names.setdefault(m["id"], m["name"])
rows_out = {}
for mbid, i in index.items():
    key = normalize(names.get(mbid, ""))
    if key and key not in rows_out: rows_out[key] = i
library = {clean(r["artist"]) for r in csv.DictReader(open(songs_tsv), delimiter="\t")}
aliases = 0
for name in library:
    m = cache["mbid"].get(name)
    key = normalize(name)
    if m and m["id"] in index and key and key not in rows_out:
        rows_out[key] = index[m["id"]]; aliases += 1
with open(out_path, "w") as f:
    for key in sorted(rows_out):
        f.write(key + "\t" + "\t".join(f"{x:.4f}" for x in E[rows_out[key]]) + "\n")
print(f"wrote {len(rows_out)} artists ({aliases} spelling aliases) to {out_path}")
