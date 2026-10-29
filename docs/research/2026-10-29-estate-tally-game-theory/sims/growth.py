"""Present case and growth path (operator 2026-10-29: "until we get more people in, I'm the only active member").
Operator = Member 0. S = her share of total Member contribution; the other n-1 follow the Pareto(1.16) ladder.
Empty blocs cast nothing and drop out of the denominator (abstentions excluded). Run: python -I growth.py
"""
import numpy as np
SEEDS = {"equal": [25, 25, 25, 25], "minds-heavy": [40, 30, 20, 10], "members-heavy": [20, 45, 20, 15]}
CURVES = {"one-each": None, "k=1": 1, "k=2": 2, "k=3": 3, "k=5": 5}

def mweights(n, S, k):
    if n == 1: return np.ones(1)
    tail = np.array([i ** (-1 / 1.16) for i in range(1, n)]); tail = tail / tail.sum() * (1 - S)
    c = np.concatenate([[S], tail]); return np.ones(n) if k is None else c ** (1 / k)

def tally(Wb, F, Tot, q, rule):
    Wb = np.array(Wb, float); live = Tot > 0
    if rule == "prop":
        sh = np.divide(F, Tot, out=np.zeros_like(F), where=live); f = (sh * Wb).sum(1); den = (live * Wb).sum(1)
    else:
        st = np.where(live, np.sign(2 * F - Tot), 0); f = (st == 1) @ Wb; den = f + (st == -1) @ Wb
    return (den > 0) & (f >= q * den - 1e-9)

def her_power(wM, Wb, q, users=0, patrons=(), minds_corr=False, T=200_000, seed=5):
    r = np.random.default_rng(seed)
    ws = [np.ones(8), wM, np.ones(users), np.log1p(np.array(patrons, float))]
    F = np.zeros((T, 4)); Tot = np.zeros((T, 4)); her = None
    for b, w in enumerate(ws):
        if len(w) == 0: continue
        if b == 0 and minds_corr: v = np.repeat(r.random((T, 1)) < .5, 8, 1)
        else: v = r.random((T, len(w))) < .5
        if b == 1: her = v[:, 0]
        F[:, b] = v @ w; Tot[:, b] = w.sum()
    res = {}
    for rule in ("prop", "wta"):
        G = F.copy(); G[:, 1] += np.where(her, -wM[0], wM[0])
        o = tally(Wb, F, Tot, q, rule); o2 = tally(Wb, G, Tot, q, rule)
        res[rule] = (np.mean(o != o2), np.mean(o == her))
    return res

if __name__ == "__main__":
    print("## G1. Present case: Members = her alone, Minds 8 (fair coins; 'unan' = Minds fully correlated)")
    print("Cells: P(her vote flips the motion) / P(outcome matches her vote), prop then WTA.")
    print("| seed | q | Users, Patrons | Minds | prop pivot / match | WTA pivot / match |")
    print("|---|---|---|---|---|---|")
    for name, Wb in SEEDS.items():
        for q in (0.15, 0.5, 0.66):
            for users, pat in ((0, ()), (3, (100,))):
                for mc in (False, True):
                    r = her_power(np.ones(1), Wb, q, users, pat, mc, T=100_000)
                    print(f"| {name} | {q} | {users}U, {len(pat)}P | {'unan' if mc else 'indep'} | {r['prop'][0]:.3f} / {r['prop'][1]:.3f} | {r['wta'][0]:.3f} / {r['wta'][1]:.3f} |")

    print("\n## G2. Her weight share inside Members; WTA-internal she alone decides the bloc iff share > 0.5")
    print("| n Members | S | one-each | k=1 | k=2 | k=3 | k=5 |")
    print("|---|---|---|---|---|---|---|")
    for n in (1, 2, 3, 5, 10, 20, 50):
        for S in (0.5, 0.8, 0.95):
            if n == 1 and S != 0.95: continue
            cells = []
            for kn, k in CURVES.items():
                w = mweights(n, S if n > 1 else 1, k); sh = w[0] / w.sum()
                cells.append(f"{sh:.2f}{'*' if sh > 0.5 else ''}")
            print(f"| {n} | {S if n > 1 else 1.0} | " + " | ".join(cells) + " |")
    print("(* = she alone decides the Members bloc under WTA/unitary internal majority)")

    print("\n## G3. Growth path: her pivot over the whole tally (fair coins, Minds indep, Users 10, Patrons [50,500] log), q=0.5")
    print("| seed | n Members | S | curve | prop pivot | WTA pivot |")
    print("|---|---|---|---|---|---|")
    for name in ("equal", "members-heavy"):
        Wb = SEEDS[name]
        for n in (1, 5, 20):
            for S in ((1.0,) if n == 1 else (0.5, 0.8)):
                for kn, k in (CURVES.items() if n > 1 else [("alone", None)]):
                    r = her_power(mweights(n, S, k), Wb, .5, users=10, patrons=(50, 500), T=100_000)
                    print(f"| {name} | {n} | {S} | {kn} | {r['prop'][0]:.3f} | {r['wta'][0]:.3f} |")
