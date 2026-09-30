#!/usr/bin/env python3
"""Map every artist in a YARG song dump to a MusicBrainz ID, then fetch ListenBrainz similar artists.

ListenBrainz data is CC0. Results are cached in cache.json so reruns resume where they stopped.
MusicBrainz asks for at most one request per second and a descriptive User-Agent.
"""
import csv, json, os, re, sys, time, urllib.parse, urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, "cache.json")
UA = "YARG-smart-recommendations/0.1 (https://github.com/adamrb/YARG)"
ALGO = "session_based_days_7500_session_300_contribution_5_threshold_10_limit_100_filter_True_skip_30"

def get(url):
    req = urllib.request.Request(url, headers={"User-Agent": UA, "Accept": "application/json"})
    for attempt in range(5):
        try:
            with urllib.request.urlopen(req, timeout=30) as r:
                return json.load(r)
        except Exception as e:
            if attempt == 4: raise
            time.sleep(3 * (attempt + 1))

def clean(name):
    name = re.sub(r"\([^)]*\)|\[[^\]]*\]", "", name).strip()
    return re.sub(r"\s+", " ", name)

cache = json.load(open(CACHE)) if os.path.exists(CACHE) else {"mbid": {}, "similar": {}}
artists = sorted({clean(r["artist"]) for r in csv.DictReader(open(sys.argv[1]), delimiter="\t") if clean(r["artist"])})
print(len(artists), "artists", flush=True)

def save():
    json.dump(cache, open(CACHE + ".tmp", "w")); os.replace(CACHE + ".tmp", CACHE)

import threading
from concurrent.futures import ThreadPoolExecutor
lock = threading.Lock(); next_slot = [time.time()]

def throttle(interval):
    # Global rate limit shared by all worker threads; latency of one request overlaps the next
    with lock:
        now = time.time()
        wait = max(0, next_slot[0] - now)
        next_slot[0] = max(now, next_slot[0]) + interval
    time.sleep(wait)

def lookup(name):
    throttle(1.05)
    q = urllib.parse.quote(f'artist:"{name}"')
    try:
        data = get(f"https://musicbrainz.org/ws/2/artist/?query={q}&fmt=json&limit=3")
    except Exception:
        return name, "retry"
    for a in data.get("artists", []):
        if int(a.get("score", 0)) >= 90:
            return name, {"id": a["id"], "name": a["name"], "score": int(a["score"])}
    return name, None

todo = [a for a in artists if a not in cache["mbid"]]
done = 0
with ThreadPoolExecutor(max_workers=4) as pool:
    for name, result in pool.map(lookup, todo):
        if result != "retry":
            cache["mbid"][name] = result
        done += 1
        if done % 50 == 0:
            with lock: save()
            print(f"mbid {len(cache['mbid'])}/{len(artists)}", flush=True)
save()

ids = sorted({v["id"] for v in cache["mbid"].values() if v})
def similar(mbid):
    throttle(0.25)
    try:
        data = get(f"https://labs.api.listenbrainz.org/similar-artists/json?artist_mbids={mbid}&algorithm={ALGO}")
    except Exception:
        return mbid, None
    return mbid, [{"id": d["artist_mbid"], "name": d["name"], "score": d["score"]} for d in data][:100]

todo = [m for m in ids if m not in cache["similar"]]
done = 0
with ThreadPoolExecutor(max_workers=4) as pool:
    for mbid, result in pool.map(similar, todo):
        if result is not None:
            cache["similar"][mbid] = result
        done += 1
        if done % 100 == 0:
            with lock: save()
            print(f"similar {done}/{len(todo)}", flush=True)
save()
found = sum(1 for v in cache["mbid"].values() if v)
print(f"done: {found}/{len(artists)} artists matched, {len(cache['similar'])} similarity lists", flush=True)
