"""Aggregate-support bounds for each rule, all four estates live: lowest weighted For share that can pass,
highest weighted For share that can fail (bloc shares free in [0,1]). Run: python -I bounds.py"""
import itertools
SEEDS = {"equal": [25, 25, 25, 25], "minds-heavy": [40, 30, 20, 10], "members-heavy": [20, 45, 20, 15]}
eps = 1e-6
print("| seed | q | prop min-pass / max-fail | wta min-pass / max-fail | bpt min-pass / max-fail |")
print("|---|---|---|---|---|")
for s, w in SEEDS.items():
    W = sum(w)
    for q in (0.15, 0.5, 0.66):
        res = {}
        for rule, t in (("wta", 0.5), ("bpt", q)):
            mp, mf = 1.0, 0.0
            for S in itertools.product((0, 1), repeat=4):   # S = blocs casting For
                ws = sum(wi for wi, x in zip(w, S) if x)
                passes = ws >= q * W - 1e-9
                # For-blocs need share > t (wta) or >= t (bpt); Against-blocs share < t (wta) or < t (bpt)
                lo = sum(wi * (t + (eps if rule == "wta" else 0)) for wi, x in zip(w, S) if x) / W
                hi = (ws + sum(wi * (t - eps) for wi, x in zip(w, S) if not x)) / W
                if passes: mp = min(mp, lo)
                else: mf = max(mf, hi)
            res[rule] = f"{mp:.3f} / {mf:.3f}"
        print(f"| {s} | {q} | {q:.3f} / <{q:.3f} | {res['wta']} | {res['bpt']} |")
