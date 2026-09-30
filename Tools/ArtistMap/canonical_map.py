#!/usr/bin/env python3
"""Map library artist names to MusicBrainz artist IDs using the MusicBrainz canonical data dump (CC0).
Streams the CSV on stdin; for each library artist keeps the single-artist credit with the most
recordings. Writes the mappings into cache.json (keeps any existing API lookups)."""
import csv, json, re, sys, collections
csv.field_size_limit(10**9)
def clean(name): return re.sub(r"\s+", " ", re.sub(r"\([^)]*\)|\[[^\]]*\]", "", name).strip())
def norm(name):
    t = re.sub(r"\([^)]*\)|\[[^\]]*\]|\{[^}]*\}", "", name).lower()
    t = " ".join("".join(c if c.isalnum() else " " for c in t).split())
    return t[4:] if t.startswith("the ") else t
library = {clean(r["artist"]) for r in csv.DictReader(open(sys.argv[1]), delimiter="\t")}
wanted = collections.defaultdict(list)
for name in library: wanted[norm(name)].append(name)
counts = collections.defaultdict(collections.Counter)
reader = csv.reader(sys.stdin)
next(reader)
for n, row in enumerate(reader):
    mbids, credit = row[2], row[3]
    if "," in mbids: continue  # skip collaborations
    key = norm(credit)
    if key in wanted: counts[key][mbids] += 1
    if n % 5_000_000 == 0: print(f"{n:,} rows, {len(counts)} artists matched", flush=True)
cache = json.load(open("cache.json"))
added = 0
for key, names in wanted.items():
    if key in counts:
        mbid, c = counts[key].most_common(1)[0]
        for name in names:
            if not cache["mbid"].get(name):
                cache["mbid"][name] = {"id": mbid, "name": name, "score": 100, "source": "canonical"}
                added += 1
json.dump(cache, open("cache.json.tmp", "w")); import os; os.replace("cache.json.tmp", "cache.json")
print(f"matched {len(counts)}/{len(wanted)} normalized artists, added {added} new mappings")
