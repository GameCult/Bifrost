"""Operator cap (her words: "I would limit my influence by half, just so I'm not deciding everything").
Cap c on her WEIGHT SHARE of the renormalized tally: her share = (her internal share of Members) * W_Me / W_live.
If she is the sole Member, the cap shrinks the Members estate weight to c/(1-c) * (other live weight);
otherwise it shrinks her internal weight until her share = c. Then measure DECISIVENESS:
pivot rate (coin voters; and motion model, Minds ICC .44), unilateral pass (her For passes with everyone else
Against) and unilateral block (her Against fails with everyone else For). Ties pass (>=). Run: python -I cap.py
"""
import numpy as np
SEEDS = {"equal": [25, 25, 25, 25], "minds-heavy": [40, 30, 20, 10], "members-heavy": [20, 45, 20, 15]}

def tally(Wb, F, Tot, q, rule):
    Wb = np.asarray(Wb, float); live = Tot > 0; sh = np.where(live, F / np.where(live, Tot, 1), 0)
    if rule == "prop": f = (sh * Wb).sum(-1); den = (live * Wb).sum(-1)
    elif rule == "wta":
        st = np.where(live, np.sign(2 * F - Tot), 0); f = (st == 1) @ Wb; den = f + (st == -1) @ Wb
    else: f = ((sh >= q - 1e-9) & live) @ Wb; den = live @ Wb
    return (den > 0) & (f >= q * den - 1e-9)

def mweights(n, S=.8, k=3):
    if n == 1: return np.ones(1)
    tail = np.array([i ** (-1 / 1.16) for i in range(1, n)]); tail = tail / tail.sum() * (1 - S)
    return np.concatenate([[S], tail]) ** (1 / k)

def apply_cap(Wb, wM, sizes, c):
    Wb = np.array(Wb, float); wM = wM.copy(); live = np.array([s > 0 for s in sizes])
    if c is None: return Wb, wM
    L = (Wb * live).sum(); x = wM[0] / wM.sum()
    if x * Wb[1] <= c * L + 1e-12: return Wb, wM
    if len(wM) == 1:
        Wb[1] = c / (1 - c) * (L - Wb[1])
    else:
        x_new = c * L / Wb[1]; o = wM[1:].sum(); wM[0] = x_new * o / (1 - x_new)
    return Wb, wM

def run(Wb, wM, sizes, q, rule, model, T=100_000, seed=9):
    r = np.random.default_rng(seed)
    ws = [np.ones(sizes[0]), wM, np.ones(sizes[2]), np.log1p(np.array([50, 500.0])[:sizes[3]])]
    if model == "coin": p = np.full((T, 4), .5)
    else:
        m = r.standard_normal(T); p = 1 / (1 + np.exp(-1.7 * (0.6 * m[:, None] + 0.8 * r.standard_normal((T, 4)))))
    F = np.zeros((T, 4)); Tot = np.zeros((T, 4)); her = None
    for b, w in enumerate(ws):
        if len(w) == 0: continue
        pb = p[:, b:b + 1]
        if b == 0 and model == "motion":
            a = p[:, 0] * .56 / .44; cc = (1 - p[:, 0]) * .56 / .44; pb = r.beta(a, cc)[:, None]
        v = r.random((T, len(w))) < pb
        if b == 1: her = v[:, 0]
        F[:, b] = v @ w; Tot[:, b] = w.sum()
    G = F.copy(); G[:, 1] += np.where(her, -wM[0], wM[0])
    return np.mean(tally(Wb, F, Tot, q, rule) != tally(Wb, G, Tot, q, rule))

def unilateral(Wb, wM, sizes, q, rule):
    ws = [np.ones(sizes[0]), wM, np.ones(sizes[2]), np.log1p(np.array([50, 500.0])[:sizes[3]])]
    Tot = np.array([[w.sum() for w in ws]])
    Fp = np.array([[0, wM[0], 0, 0]], float); Fb = np.array([[w.sum() for w in ws]], float); Fb[0, 1] -= wM[0]
    return bool(tally(Wb, Fp, Tot, q, rule)[0]), not bool(tally(Wb, Fb, Tot, q, rule)[0])

CAPS = (None, .5, .49, .4, .33)
CASES = (("present 8/1/0/0", (8, 1, 0, 0)), ("growth n=5 k=3 S=.8 +U10 P2", (8, 5, 10, 2)), ("growth n=20 k=3 S=.8 +U10 P2", (8, 20, 10, 2)))
print("Cells: pivot coin / pivot motion / U+ U- (unilateral pass, unilateral block; Y/N). Her weight share shown as x.")
for cname, sizes in CASES:
    print(f"\n### {cname}")
    print("| seed | q | cap | her share x | prop | wta | bpt |"); print("|---|---|---|---|---|---|---|")
    for s, W0 in SEEDS.items():
        for q in (0.15, 0.5, 0.66):
            for c in CAPS:
                Wb, wM = apply_cap(W0, mweights(sizes[1]), sizes, c)
                live = np.array([n > 0 for n in sizes]); x = wM[0] / wM.sum() * Wb[1] / (Wb * live).sum()
                cells = []
                for rule in ("prop", "wta", "bpt"):
                    pc = run(Wb, wM, sizes, q, rule, "coin"); pm = run(Wb, wM, sizes, q, rule, "motion")
                    up, ub = unilateral(Wb, wM, sizes, q, rule)
                    cells.append(f"{pc:.3f} / {pm:.3f} / {'Y' if up else 'N'}{'Y' if ub else 'N'}")
                print(f"| {s} | {q} | {c if c else 'none'} | {x:.3f} | " + " | ".join(cells) + " |")
