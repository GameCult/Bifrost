"""Within-bloc vote-power curves (operator addition 2026-10-29).
Members: w = contribution^(1/k). Patrons: w = log(1+d) or d^(1/k). Minds, Users: one each.
Assumptions (stated, not data): 15 Members; operator (member 0) holds share S of all contribution
(S=0.5 or 0.8); the other 14 follow a Pareto(alpha=1.16, "80/20") quantile ladder c_i = i^(-1/1.16).
4 Patrons with donations [10, 50, 200, 5000]. Run: python -I weights.py
"""
import itertools, math
import numpy as np
rng = np.random.default_rng(29)

def members(S):
    tail = np.array([i ** (-1 / 1.16) for i in range(1, 15)]); tail /= tail.sum()
    return np.concatenate([[S / (1 - S)], tail])  # operator / tail-sum = S/(1-S)

PATRON_D = np.array([10, 50, 200, 5000.0])
CURVES_M = {"one-each": lambda c: np.ones_like(c), "k=1": lambda c: c, "k=2": np.sqrt,
            "k=3": lambda c: c ** (1 / 3), "k=5": lambda c: c ** 0.2}
CURVES_P = {"one-each": lambda d: np.ones_like(d), "k=1": lambda d: d, "log(1+d)": np.log1p,
            "k=3": lambda d: d ** (1 / 3), "k=5": lambda d: d ** 0.2, "k=10": lambda d: d ** 0.1}

def kish(w): return w.sum() ** 2 / (w ** 2).sum()

def internal_banzhaf(w, i=0):
    """P(voter i decides the weighted bloc majority), others fair coins; tie casts nothing (counts as change)."""
    others = np.delete(w, i); n = len(others); tot = 0
    for bits in itertools.product([0, 1], repeat=n):
        f = np.dot(bits, others); a = others.sum() - f
        sF = np.sign(f + w[i] - a); sA = np.sign(f - a - w[i])
        tot += sF != sA
    return tot / 2 ** n

def tally(Wb, F, Tot, q, rule):
    """F, Tot: (T,4) weighted For and cast weight per bloc."""
    Wb = np.array(Wb, float)
    if rule == "prop":
        return (F / Tot) @ Wb >= q * Wb.sum() - 1e-9
    st = np.sign(2 * F - Tot); f = (st == 1) @ Wb; a = (st == -1) @ Wb
    return (f + a > 0) & (f >= q * (f + a) - 1e-9)

def sim(wM, wP, Wb=(25, 25, 25, 25), q=0.5, T=200_000, model="coin", minds_r=0.44, seed=1):
    r = np.random.default_rng(seed)
    if model == "coin": p = np.full((T, 4), .5)
    else:
        m = r.standard_normal(T); p = 1 / (1 + np.exp(-1.7 * (0.6 * m[:, None] + 0.8 * r.standard_normal((T, 4)))))
    ws = [np.ones(8), wM, np.ones(30), wP]; F = np.zeros((T, 4)); Tot = np.zeros((T, 4)); V = []
    for b, w in enumerate(ws):
        pb = p[:, b:b + 1]
        if b == 0 and model != "coin" and minds_r > 0:
            a = p[:, 0] * (1 - minds_r) / minds_r; c = (1 - p[:, 0]) * (1 - minds_r) / minds_r
            pb = r.beta(a, c)[:, None]
        v = r.random((T, len(w))) < pb; V.append(v)
        F[:, b] = v @ w; Tot[:, b] = w.sum()
    return ws, V, F, Tot

def pivot(ws, V, F, Tot, b, i, Wb, q, rule):
    G = F.copy(); G[:, b] += np.where(V[b][:, i], -ws[b][i], ws[b][i])
    return np.mean(tally(Wb, F, Tot, q, rule) != tally(Wb, G, Tot, q, rule))

if __name__ == "__main__":
    print("## W1. Effective voters (Kish = inverse Herfindahl) and top share")
    print("| bloc | curve | operator S | Kish n_eff | top voter share | internal Banzhaf of top (WTA bloc) |")
    print("|---|---|---|---|---|---|")
    for S in (0.5, 0.8):
        for name, fn in CURVES_M.items():
            w = fn(members(S)); print(f"| Members (15) | {name} | {S} | {kish(w):.2f} | {w[0] / w.sum():.3f} | {internal_banzhaf(w):.3f} |")
    for name, fn in CURVES_P.items():
        w = fn(PATRON_D); print(f"| Patrons (4) | {name} | - | {kish(w):.2f} | {w.max() / w.sum():.3f} | {internal_banzhaf(w, int(np.argmax(w))):.3f} |")

    print("\n## W2. Operator's power over the whole tally (two-tier Banzhaf, all voters fair coins, 200k draws)")
    print("Comparators: a one-each Member; a Mind (8); a User (30); top Patron under log(1+d).")
    print("| bloc weights | q | Members curve | S | op pivot prop | op pivot WTA | one-each Member prop/WTA | Mind prop/WTA | User prop/WTA |")
    print("|---|---|---|---|---|---|---|---|---|")
    wP = np.log1p(PATRON_D)
    for Wb, lab in (((25, 25, 25, 25), "equal"), ((20, 45, 20, 15), "members-heavy")):
        for q in (0.5, 0.66):
            for S in (0.5, 0.8):
                for name in ("one-each", "k=1", "k=2", "k=3", "k=5"):
                    if name == "one-each" and S == 0.8: continue
                    wM = CURVES_M[name](members(S)); ws, V, F, Tot = sim(wM, wP, Wb, q)
                    pr = [pivot(ws, V, F, Tot, b, i, Wb, q, "prop") for b, i in ((1, 0), (0, 0), (2, 0))]
                    pw = [pivot(ws, V, F, Tot, b, i, Wb, q, "wta") for b, i in ((1, 0), (0, 0), (2, 0))]
                    base = "" if name != "one-each" else " (baseline)"
                    print(f"| {lab} | {q} | {name}{base} | {S if name != 'one-each' else '-'} | {pr[0]:.4f} | {pw[0]:.4f} | - | {pr[1]:.4f}/{pw[1]:.4f} | {pr[2]:.4f}/{pw[2]:.4f} |")

    print("\n## W3. Exponent sensitivity under the motion model (Minds ICC .44, equal bloc weights, q=0.5, S=0.5)")
    print("P(motion outcome differs) between Members curve k and k=3 baseline, Patron curve log(1+d); and Patron curve vs log(1+d) with Members k=3.")
    print("| varied | curve | P(differ) prop | P(differ) WTA | operator pivot prop | operator pivot WTA |")
    print("|---|---|---|---|---|---|")
    Wb = (25, 25, 25, 25); q = .5
    def outcomes(wM, wP):
        ws, V, F, Tot = sim(wM, wP, Wb, q, T=200_000, model="motion", seed=7)
        return ws, V, F, Tot, tally(Wb, F, Tot, q, "prop"), tally(Wb, F, Tot, q, "wta")
    *_, bp, bw = outcomes(CURVES_M["k=3"](members(.5)), wP)
    for name in ("one-each", "k=1", "k=2", "k=3", "k=5"):
        ws, V, F, Tot, op, ow = outcomes(CURVES_M[name](members(.5)), wP)
        print(f"| Members | {name} | {np.mean(op != bp):.3f} | {np.mean(ow != bw):.3f} | {pivot(ws, V, F, Tot, 1, 0, Wb, q, 'prop'):.4f} | {pivot(ws, V, F, Tot, 1, 0, Wb, q, 'wta'):.4f} |")
    for name in ("one-each", "k=1", "log(1+d)", "k=3", "k=5", "k=10"):
        ws, V, F, Tot, op, ow = outcomes(CURVES_M["k=3"](members(.5)), CURVES_P[name](PATRON_D))
        print(f"| Patrons | {name} | {np.mean(op != bp):.3f} | {np.mean(ow != bw):.3f} | - | - |")
