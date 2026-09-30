#!/usr/bin/env python3
"""Checks that the song map builder keys songs exactly as the game does, using the cases YARG.Core's
SongNormalizerTests also run. Run from the YARG repo: python3 Tools/SongMap/test_build_song_map.py"""
import os, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from build_song_map import song_key

CASES = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "YARG.Core", "YARG.Core.UnitTests",
                     "Song", "Recommendations", "song-keys.tsv")

failures = 0
for line in open(CASES, encoding="utf-8"):
    line = line.rstrip("\n")
    if not line or line.startswith("#"):
        continue
    artist, title, *expected = line.split("\t")
    expected = expected[0] if expected else ""
    actual = song_key(artist, title) or ""
    if actual != expected:
        failures += 1
        print(f"FAIL {artist!r} / {title!r}: expected {expected!r}, got {actual!r}")
print("all song keys match" if failures == 0 else f"{failures} mismatches")
sys.exit(1 if failures else 0)
