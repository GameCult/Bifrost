"""Lone estate bounds (operator 2026-10-29: "they totally should be able to make decisions without me if I'm not there to vote").
Present case with her absent: Minds (8) are the only live estate; renormalization makes Minds the whole tally.
L1 quorum floors that a lone Minds turnout meets; L2 category thresholds vs Personas needed and pass rates;
L3 delay-plus-cancel window: P(she returns before effect) for exponential absence. Run: python -I lone.py
"""
import math
import numpy as np
CATS = {"Bugs": .15, "Cosmetics": .30, "Balance": .40, "Features": .50, "NewContent": .50, "FundamentalDesign": .66}

print("## L1. Quorum floors a lone Minds turnout (8 of 8) meets, she absent")
print("| floor type | Minds-alone value | equal | minds-heavy | members-heavy |")
print("|---|---|---|---|---|")
print("| headcount fraction of eligible (8 Minds + her) | 8/9 = 0.889 | same | same | same |")
for lab in ("estate-weight fraction of live-eligible estates",):
    vals = [wm_ / (wm_ + wme_) for wm_, wme_ in ((25, 25), (40, 30), (20, 45))]
    print(f"| {lab} | w_M/(w_M+w_Me) | {vals[0]:.3f} | {vals[1]:.3f} | {vals[2]:.3f} |")
print("A floor above these values requires her turnout; at or below, Minds alone can decide.")

print("\n## L2. Lone Minds: Personas For needed and P(pass), motion model, by category")
r = np.random.default_rng(11); T = 400_000
m = r.standard_normal(T); p = 1 / (1 + np.exp(-1.7 * (0.6 * m + 0.8 * r.standard_normal(T))))
def minds_for(icc):
    if icc >= 1: return np.where(r.random(T) < p, 8, 0)
    if icc <= 0: return r.binomial(8, p)
    a = p * (1 - icc) / icc; b = (1 - p) * (1 - icc) / icc; return r.binomial(8, r.beta(a, b))
print("| category | q | Personas needed prop | Personas needed WTA | P(pass) prop ICC 0 / .44 / 1 | P(pass) WTA ICC 0 / .44 / 1 |")
print("|---|---|---|---|---|---|")
draws = {icc: minds_for(icc) for icc in (0, .44, 1)}
for c, q in CATS.items():
    need = math.ceil(q * 8 - 1e-9)
    pp = [np.mean(draws[i] / 8 >= q - 1e-9) for i in (0, .44, 1)]
    pw = [np.mean(draws[i] >= 5) for i in (0, .44, 1)]
    print(f"| {c} | {q} | {need} | 5 (any q) | " + " / ".join(f"{x:.3f}" for x in pp) + " | " + " / ".join(f"{x:.3f}" for x in pw) + " |")
print("Under WTA a lone estate casts 100% For or 100% Against, so For/(For+Against) is 1 or 0 and category thresholds are inert.")

print("\n## L3. Delay-plus-cancel window: P(she is back before the decision takes effect), absence ~ Exponential(mean A)")
print("| delay D | A=1 day | A=3 days | A=7 days | A=14 days |")
print("|---|---|---|---|---|")
for D in (1, 2, 3, 7, 10, 14):
    print(f"| {D} d | " + " | ".join(f"{1 - math.exp(-D / A):.2f}" for A in (1, 3, 7, 14)) + " |")
print("Memoryless assumption; real absences are not exponential. P = chance a cancel opportunity exists, not that it is used.")
