"""A priori power of estate blocs and of individuals inside them.
Pass rule (Bifrost MotionGovernanceService): For / (For + Against) >= q, abstain excluded.
Blocs: Minds, Members, Users, Patrons. Run: python -I power.py
"""
import itertools, math
from math import comb

BLOCS = ["Minds", "Members", "Users", "Patrons"]
WEIGHTS = {"equal": [25, 25, 25, 25], "minds-heavy": [40, 30, 20, 10], "members-heavy": [20, 45, 20, 15]}
QS = [0.15, 0.5, 0.66]
SIZES = [8, 15, 30, 4]  # voting individuals (Users = active turnout, not roll)

def wins(ws, S, q):
    return sum(ws[i] for i in S) >= q * sum(ws) - 1e-12

def banzhaf(ws, q):
    n = len(ws); sw = [0] * n
    for i in range(n):
        others = [j for j in range(n) if j != i]
        for r in range(len(others) + 1):
            for S in itertools.combinations(others, r):
                if not wins(ws, S, q) and wins(ws, S + (i,), q):
                    sw[i] += 1
    t = sum(sw); return [s / t for s in sw], sw

def shapley(ws, q):
    n = len(ws); piv = [0] * n
    for perm in itertools.permutations(range(n)):
        acc = 0
        for i in perm:
            acc += ws[i]
            if acc >= q * sum(ws) - 1e-12:
                piv[i] += 1; break
    t = sum(piv); return [p / t for p in piv]

def veto(ws, q):
    W = sum(ws); return [BLOCS[i] for i in range(len(ws)) if W - ws[i] < q * W - 1e-12]

def binom(n):  # P(k For among n coin-flip voters)
    return [comb(n, k) / 2 ** n for k in range(n + 1)]

def wta_state(k, n):  # +1 For, -1 Against, 0 tie casts nothing
    return 1 if 2 * k > n else (-1 if 2 * k < n else 0)

def outcome_wta(ws, states, q):
    f = sum(w for w, s in zip(ws, states) if s == 1); a = sum(w for w, s in zip(ws, states) if s == -1)
    return f + a > 0 and f / (f + a) >= q - 1e-12

def bloc_state_dist(n):
    d = {1: 0.0, -1: 0.0, 0: 0.0}
    for k, p in enumerate(binom(n)): d[wta_state(k, n)] += p
    return d

def indiv_power_wta(ws, q, sizes, b):
    """P(one voter in bloc b changes the motion outcome), others independent fair coins."""
    others = [j for j in range(len(ws)) if j != b]
    dists = [bloc_state_dist(sizes[j]) for j in others]
    tot = 0.0
    for k, pk in enumerate(binom(sizes[b] - 1)):
        sF, sA = wta_state(k + 1, sizes[b]), wta_state(k, sizes[b])
        if sF == sA: continue
        for combo in itertools.product([1, -1, 0], repeat=len(others)):
            p = pk
            for d, s in zip(dists, combo): p *= d[s]
            if p == 0: continue
            st = [0] * len(ws)
            for j, s in zip(others, combo): st[j] = s
            stF = st[:]; stF[b] = sF; stA = st[:]; stA[b] = sA
            if outcome_wta(ws, stF, q) != outcome_wta(ws, stA, q): tot += p
    return tot

def indiv_power_prop(ws, q, sizes, b):
    others = [j for j in range(len(ws)) if j != b]
    W = sum(ws); tot = 0.0
    dists = [binom(sizes[j]) for j in others]
    for ks in itertools.product(*[range(sizes[j] + 1) for j in others]):
        p = 1.0; base = 0.0
        for j, k, d in zip(others, ks, dists): p *= d[k]; base += ws[j] * k / sizes[j]
        for k, pk in enumerate(binom(sizes[b] - 1)):
            vF = base + ws[b] * (k + 1) / sizes[b]; vA = base + ws[b] * k / sizes[b]
            if (vF >= q * W - 1e-12) != (vA >= q * W - 1e-12): tot += p * pk
    return tot

if __name__ == "__main__":
    print("## Unitary blocs: normalized Banzhaf / Shapley-Shubik, veto blocs")
    print("| weights | q | Banzhaf M/Me/U/P | Shapley M/Me/U/P | veto blocs |")
    print("|---|---|---|---|---|")
    for name, ws in WEIGHTS.items():
        for q in QS:
            bz, _ = banzhaf(ws, q); ss = shapley(ws, q)
            f = lambda v: "/".join(f"{x:.2f}" for x in v)
            print(f"| {name} {ws} | {q} | {f(bz)} | {f(ss)} | {', '.join(veto(ws, q)) or 'none'} |")
    print("\n## Individual power (P one voter flips the motion), sizes", dict(zip(BLOCS, SIZES)))
    print("| weights | q | rule | Minds | Members | Users | Patrons | max/min |")
    print("|---|---|---|---|---|---|---|---|")
    for name, ws in WEIGHTS.items():
        for q in QS:
            for rule, fn in (("WTA", indiv_power_wta), ("prop", indiv_power_prop)):
                v = [fn(ws, q, SIZES, b) for b in range(4)]
                r = max(v) / min(v) if min(v) > 0 else float("inf")
                print(f"| {name} | {q} | {rule} | " + " | ".join(f"{x:.4f}" for x in v) + f" | {r:.1f} |")
    print("\n## Penrose square-root weights (WTA individual-power equalizer, independent voters)")
    sq = [math.sqrt(n) for n in SIZES]; print("sqrt(n) shares:", [round(100 * s / sum(sq), 1) for s in sq])
    neff = [8 / (1 + 7 * 0.44)] + SIZES[1:]
    sq2 = [math.sqrt(n) for n in neff]; print("with Minds n_eff=8/(1+7*0.44)=%.2f:" % neff[0], [round(100 * s / sum(sq2), 1) for s in sq2])
    pr = [100 * n / sum(SIZES) for n in SIZES]; print("headcount shares (prop equalizer):", [round(x, 1) for x in pr])
    for ws, lab in ((sq, "sqrt"), (SIZES, "headcount")):
        for q in (0.5,):
            v = [indiv_power_wta(ws, q, SIZES, b) for b in range(4)]; u = [indiv_power_prop(ws, q, SIZES, b) for b in range(4)]
            print(f"{lab} weights q={q}: WTA indiv", [round(x, 4) for x in v], " prop indiv", [round(x, 4) for x in u])
