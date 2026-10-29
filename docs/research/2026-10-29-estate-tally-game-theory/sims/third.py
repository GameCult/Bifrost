"""Third rule `bpt` (bloc-parliament-at-threshold): a live bloc casts its whole weight For iff its internal
weighted For share (abstain excluded) >= q, else Against. Council: For weight / live weight >= q (renormalized).
Compared with prop and wta (wta: internal majority, tie casts nothing). Run: python -I third.py
"""
import itertools, math
import numpy as np
SEEDS = {"equal": [25, 25, 25, 25], "minds-heavy": [40, 30, 20, 10], "members-heavy": [20, 45, 20, 15]}
QS = (0.15, 0.5, 0.66)

def tally(Wb, F, Tot, q, rule):
    Wb = np.array(Wb, float); live = Tot > 0; sh = np.divide(F, np.where(live, Tot, 1)); sh = np.where(live, sh, 0)
    if rule == "prop": f = (sh * Wb).sum(-1); den = (live * Wb).sum(-1)
    elif rule == "wta":
        st = np.where(live, np.sign(2 * F - Tot), 0); f = (st == 1) @ Wb; den = f + (st == -1) @ Wb
    else: f = ((sh >= q - 1e-9) & live) @ Wb; den = live @ Wb
    return (den > 0) & (f >= q * den - 1e-9)

def motion(T, sizes, minds_r, seed, coin=False, wvec=None):
    r = np.random.default_rng(seed)
    if coin: p = np.full((T, 4), .5)
    else:
        m = r.standard_normal(T); p = 1 / (1 + np.exp(-1.7 * (0.6 * m[:, None] + 0.8 * r.standard_normal((T, 4)))))
    ws = wvec or [np.ones(n) for n in sizes]; F = np.zeros((T, 4)); Tot = np.zeros((T, 4)); V = []
    for b, w in enumerate(ws):
        if len(w) == 0: V.append(np.zeros((T, 0), bool)); continue
        pb = p[:, b:b + 1]
        if b == 0 and minds_r >= 1: pb = (r.random((T, 1)) < pb).astype(float)
        elif b == 0 and minds_r > 0:
            a = p[:, 0] * (1 - minds_r) / minds_r; c = (1 - p[:, 0]) * (1 - minds_r) / minds_r; pb = r.beta(a, c)[:, None]
        v = r.random((T, len(w))) < pb; V.append(v); F[:, b] = v @ w; Tot[:, b] = w.sum()
    return ws, V, F, Tot

def piv(ws, V, F, Tot, b, Wb, q, rule):
    G = F.copy(); G[:, b] += np.where(V[b][:, 0], -ws[b][0], ws[b][0])
    return np.mean(tally(Wb, F, Tot, q, rule) != tally(Wb, G, Tot, q, rule))

R = ("prop", "wta", "bpt")
print("## T1. Motion model (Minds ICC .44; 8/15/30/4): P(pass) and pairwise P(outcomes differ)")
print("| seed | q | P(pass) prop/wta/bpt | differ bpt-prop | differ bpt-wta | differ prop-wta |")
print("|---|---|---|---|---|---|")
ws, V, F, Tot = motion(200_000, (8, 15, 30, 4), .44, 1)
for s, Wb in SEEDS.items():
    for q in QS:
        o = {r: tally(Wb, F, Tot, q, r) for r in R}
        print(f"| {s} | {q} | " + "/".join(f"{o[r].mean():.3f}" for r in R) +
              f" | {np.mean(o['bpt'] != o['prop']):.3f} | {np.mean(o['bpt'] != o['wta']):.3f} | {np.mean(o['prop'] != o['wta']):.3f} |")

print("\n## T2. Single-voter pivot M/Me/U/P (same draws), equal seed")
print("| q | prop | wta | bpt |"); print("|---|---|---|---|")
for q in QS:
    print(f"| {q} | " + " | ".join("/".join(f"{piv(ws, V, F, Tot, b, SEEDS['equal'], q, r):.4f}" for b in range(4)) for r in R) + " |")

print("\n## T3. Single-Persona pivot vs Minds ICC (equal seed)")
print("| ICC | q=.15 prop/wta/bpt | q=.5 prop/wta/bpt | q=.66 prop/wta/bpt |"); print("|---|---|---|---|")
for icc in (0, .44, .8, 1):
    w2, V2, F2, T2 = motion(100_000, (8, 15, 30, 4), icc, 2)
    print(f"| {icc} | " + " | ".join("/".join(f"{piv(w2, V2, F2, T2, 0, SEEDS['equal'], q, r):.4f}" for r in R) for q in QS) + " |")

print("\n## T4. Users faction capture: population support .3, 9 random voters + k coordinated For")
print("P(Users bloc casts For), wta vs bpt; prop share in brackets")
print("| k | faction share of voters | wta | bpt q=.15 | bpt q=.5 | bpt q=.66 | prop share |"); print("|---|---|---|---|---|---|---|")
rr = np.random.default_rng(4)
for k in (0, 1, 2, 3, 6, 9, 12, 18):
    f = (rr.random((100_000, 9)) < .3).sum(1) + k; n = 9 + k; sh = f / n
    print(f"| {k} | {k / n:.2f} | {np.mean(2 * f > n):.3f} | " + " | ".join(f"{np.mean(sh >= q - 1e-9):.3f}" for q in QS) + f" | {sh.mean():.3f} |")

print("\n## T5. Logrolling (6 motions, 800 agendas, greedy mutually profitable pairwise trades, max 6)")
print("| seed | q | rule | P(>=1 trade) | mean trades | mean welfare change | P(non-trader loses) | P(trader ends worse) |")
print("|---|---|---|---|---|---|---|---|")
def logroll(Wb, q, kind, T=800, M=6, seed=3):
    rng = np.random.default_rng(seed); W = np.array(Wb, float); st = np.zeros(5)
    for _ in range(T):
        u = rng.standard_normal((4, M)); share = 1 / (1 + np.exp(-2 * u))
        base = {"wta": (u > 0).astype(float), "bpt": (share >= q).astype(float), "prop": share}[kind]
        cast = base.copy()
        def out(c): return (W @ c) >= q * W.sum() - 1e-9
        U0 = u @ out(cast); cur = U0.copy(); used = set(); n = 0; traders = set()
        while n < 6:
            best = None
            for A, B in itertools.combinations(range(4), 2):
                for x, y in itertools.permutations(range(M), 2):
                    if (A, x) in used or (B, y) in used: continue
                    c2 = cast.copy()
                    for b, mm in ((A, x), (B, y)):
                        c2[b, mm] = (0.0 if u[b, mm] > 0 else 1.0) if kind != "prop" else np.clip(base[b, mm] - np.sign(u[b, mm]) * .5, 0, 1)
                    g = u @ out(c2) - cur
                    if g[A] > 1e-9 and g[B] > 1e-9 and (best is None or g[A] + g[B] > best[0]): best = (g[A] + g[B], c2, A, B, x, y)
            if best is None: break
            _, cast, A, B, x, y = best; used |= {(A, x), (B, y)}; traders |= {A, B}; n += 1; cur = u @ out(cast)
        non = [b for b in range(4) if b not in traders]
        st += [n > 0, n, cur.sum() - U0.sum(), any(cur[b] < U0[b] - 1e-9 for b in non), any(cur[b] < U0[b] - 1e-9 for b in traders)]
    return st / T
for s in ("equal", "minds-heavy"):
    for q in (0.15, 0.5, 0.66):
        for kind in ("wta", "bpt", "prop"):
            v = logroll(SEEDS[s], q, kind)
            print(f"| {s} | {q} | {kind if kind != 'prop' else 'prop delta .5'} | {v[0]:.3f} | {v[1]:.2f} | {v[2]:+.3f} | {v[3]:.3f} | {v[4]:.3f} |")

print("\n## T6. Lone Minds (she absent) by category: P(pass), motion model, ICC 0/.44/1")
print("| category | q | prop | wta | bpt |"); print("|---|---|---|---|---|")
lone = {icc: motion(200_000, (8, 0, 0, 0), icc, 5) for icc in (0, .44, 1)}
for c, q in {"Bugs": .15, "Cosmetics": .30, "Balance": .40, "Features/NewContent": .50, "FundamentalDesign": .66}.items():
    print(f"| {c} | {q} | " + " | ".join("/".join(f"{tally(SEEDS['equal'], lone[i][2], lone[i][3], q, r).mean():.3f}" for i in (0, .44, 1)) for r in R) + " |")

print("\n## T7. Present case (Minds 8 coin-flip, her alone in Members, Users/Patrons empty): P(pass | she For / Against / home)")
print("| seed | q | prop | wta | bpt |"); print("|---|---|---|---|---|")
from math import comb
for s, Wb in SEEDS.items():
    for q in QS:
        cells = []
        for r in R:
            vals = []
            for hv in (1, -1, 0):
                F_ = np.array([[k, 1.0 if hv == 1 else 0, 0, 0] for k in range(9)]); T_ = np.array([[8, 1.0 if hv else 0, 0, 0]] * 9)
                pk = np.array([comb(8, k) / 256 for k in range(9)]); vals.append(float(pk @ tally(Wb, F_, T_, q, r)))
            cells.append("/".join(f"{x:.3f}" for x in vals))
        print(f"| {s} | {q} | " + " | ".join(cells) + " |")

print("\n## T8. Operator pivot over the whole tally (coin voters, Users 10, Patrons [50,500] log), S=.8")
def mweights(n, S, k):
    if n == 1: return np.ones(1)
    tail = np.array([i ** (-1 / 1.16) for i in range(1, n)]); tail = tail / tail.sum() * (1 - S)
    c = np.concatenate([[S], tail]); return np.ones(n) if k is None else c ** (1 / k)
print("| seed | q | n | curve | prop | wta | bpt |"); print("|---|---|---|---|---|---|---|")
for s in ("equal", "members-heavy"):
    for q in QS:
        for n, kn, k in ((1, "alone", None), (5, "one-each", None), (5, "k=3", 3), (20, "one-each", None), (20, "k=3", 3), (20, "k=5", 5)):
            wv = [np.ones(8), mweights(n, .8, k), np.ones(10), np.log1p(np.array([50, 500.0]))]
            w8, V8, F8, T8 = motion(100_000, None, 0, 6, coin=True, wvec=wv)
            print(f"| {s} | {q} | {n} | {kn} | " + " | ".join(f"{piv(w8, V8, F8, T8, 1, SEEDS[s], q, r):.3f}" for r in R) + " |")

print("\n## T9. Exhaustive profitable misreport/abstention/stay-home under bpt (renormalized, no quorum)")
def tl(ws_, sizes, bal, q):
    F = [0] * 4; A = [0] * 4; i = 0
    for b, n in enumerate(sizes):
        for v in bal[i:i + n]: F[b] += v == 1; A[b] += v == -1
        i += n
    live = [b for b in range(4) if F[b] + A[b]]; f = sum(ws_[b] for b in live if F[b] / (F[b] + A[b]) >= q - 1e-9)
    den = sum(ws_[b] for b in live); return den > 0 and f >= q * den - 1e-9
print("| sizes | seed | q | profiles | profitable indiv deviations | estate stay-home profitable |"); print("|---|---|---|---|---|---|")
for sizes in ((8, 1, 0, 0), (3, 3, 2, 2)):
    for s, Wb in SEEDS.items():
        for q in QS:
            st = [sum(sizes[:b]) for b in range(4)]; ind = est = n = 0
            for bal in itertools.product((1, -1), repeat=sum(sizes)):
                n += 1; o = tl(Wb, sizes, bal, q)
                for i, v in enumerate(bal):
                    if o != (v == 1) and any(tl(Wb, sizes, bal[:i] + (d,) + bal[i + 1:], q) == (v == 1) for d in (-v, 0)): ind += 1
                for b in range(4):
                    if not sizes[b]: continue
                    seg = bal[st[b]:st[b] + sizes[b]]; pref = seg.count(1) / len(seg) >= q  # bloc's own position under bpt
                    if o != pref and tl(Wb, sizes, bal[:st[b]] + (0,) * sizes[b] + bal[st[b] + sizes[b]:], q) == pref: est += 1
            print(f"| {'/'.join(map(str, sizes))} | {s} | {q} | {n} | {ind} | {est} |")
