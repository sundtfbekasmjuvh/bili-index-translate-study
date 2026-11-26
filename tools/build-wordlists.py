#!/usr/bin/env python3
"""Regenerate bili-index-translate/data/{hsk30,freq-en,freq-es}.txt and print stats.

Dependencies: Python 3.9+, `pip install wordfreq`, network access (HSK CSV is downloaded
from GitHub at a pinned commit).

Usage:
  python3 -m venv /tmp/wl-venv && /tmp/wl-venv/bin/pip install wordfreq
  /tmp/wl-venv/bin/python tools/build-wordlists.py [--cache DIR] [--n 50000]

  --cache DIR  where to keep the downloaded CSV (default: a temp dir). Never inside the repo.
  --n N        words kept per frequency list (default 50000).

Output format (see docs: bili-annotate-design.md section 3):
  hsk30.txt    "#1".."#7" heading lines, then one word per line (level 7 = HSK 7-9).
               A word in several levels keeps the lowest level.
  freq-*.txt   one word per line, line number = frequency rank (wordfreq.top_n_list).
               English keeps letters with at most one inner apostrophe; Spanish keeps letters
               (accents and n-tilde allowed). Digits and punctuation are dropped.
"""
import argparse
import csv
import io
import os
import re
import sys
import tempfile
import urllib.request

HSK_COMMIT = "4ff9e3915ce87baaecd7ebe263085573a4ea3192"
HSK_URL = "https://raw.githubusercontent.com/ivankra/hsk30/%s/hsk30-expanded.csv" % HSK_COMMIT
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "bili-index-translate", "data")

RE_EN = re.compile(r"^[a-z]+(?:'[a-z]+)?$")
RE_ES = re.compile(r"^[a-záéíóúüñ]+$")
RE_HAN = re.compile(r"^[㐀-鿿]+$")


def build_hsk(cache):
    path = os.path.join(cache, "hsk30-expanded-%s.csv" % HSK_COMMIT[:7])
    if not os.path.exists(path):
        with urllib.request.urlopen(HSK_URL, timeout=60) as r:
            data = r.read()
        with open(path, "wb") as f:
            f.write(data)
    best = {}
    order = []
    with open(path, encoding="utf-8", newline="") as f:
        for row in csv.DictReader(f):
            if row.get("Example"):  # prefix/suffix example words are not real entries
                continue
            word = row["Simplified"].strip()
            lv = 7 if row["Level"].strip() == "7-9" else int(row["Level"])
            if not RE_HAN.match(word):
                continue
            if word not in best:
                order.append(word)
                best[word] = lv
            elif lv < best[word]:
                best[word] = lv
    lines = []
    for lv in range(1, 8):
        lines.append("#%d" % lv)
        lines.extend(w for w in order if best[w] == lv)
    return "\n".join(lines) + "\n", len(best)


def build_freq(lang, n, rx):
    from wordfreq import top_n_list
    # ask for extra so filtering still leaves n
    words = []
    seen = set()
    for w in top_n_list(lang, n * 2):
        if rx.match(w) and w not in seen:
            seen.add(w)
            words.append(w)
            if len(words) == n:
                break
    return "\n".join(words) + "\n", len(words)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cache", default=None)
    ap.add_argument("--n", type=int, default=50000)
    a = ap.parse_args()
    cache = a.cache or tempfile.mkdtemp(prefix="hsk30-")
    os.makedirs(cache, exist_ok=True)
    os.makedirs(OUT, exist_ok=True)
    jobs = [
        ("hsk30.txt", lambda: build_hsk(cache)),
        ("freq-en.txt", lambda: build_freq("en", a.n, RE_EN)),
        ("freq-es.txt", lambda: build_freq("es", a.n, RE_ES)),
    ]
    for name, fn in jobs:
        text, count = fn()
        with open(os.path.join(OUT, name), "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
        print("%s: %d words, %d bytes" % (name, count, len(text.encode("utf-8"))))
    try:
        from importlib.metadata import version
        print("wordfreq", version("wordfreq"))
    except Exception:
        pass
    if count < a.n:
        print("WARNING: fewer than %d words in the last list" % a.n, file=sys.stderr)


if __name__ == "__main__":
    main()
