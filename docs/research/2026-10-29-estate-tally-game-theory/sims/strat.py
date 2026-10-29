"""Strategic probes. Run: python -I strat.py
S1 exhaustive: can any voter gain by misreporting or abstaining on one binary motion? (with and without a quorum)
S2 logrolling among 4 blocs over a 6-motion agenda: unitary blocs (whole weight deliverable) vs
   proportional blocs whose leadership can move only a fraction delta of the bloc's share.
"""
import itertools
import numpy as np

def tally(ws, blocs, ballots, q, rule, quorum=0.0):
    """ballots: tuple of +1 For, -1 Against, 0 abstain, one per voter; blocs: bloc index per voter."""
    F = [0] * len(ws); A = [0] * len(ws)
    for b, v in zip(blocs, ballots):
        if v == 1: F[b] += 1
        elif v == -1: A[b] += 1
    f = a = 0.0
    for b, w in enumerate(ws):
        if F[b] + A[b] == 0: continue
        if rule == "prop": f += w * F[b] / (F[b] + A[b]); a += w * A[b] / (F[b] + A[b])
        elif F[b] > A[b]: f += w
        elif A[b] > F[b]: a += w
    if f + a < quorum * sum(ws) - 1e-9 or f + a == 0: return False
    return f >= q * (f + a) - 1e-9

def s1():
    blocs = [0, 0, 0, 1, 1, 1, 2, 2, 3, 3]
    print("| weights | q | quorum | rule | profiles x voters | profitable deviations | of which abstention |")
    print("|---|---|---|---|---|---|---|")
    for ws in ([25, 25, 25, 25], [40, 30, 20, 10]):
        for q in (0.15, 0.5, 0.66):
            for quorum in (0.0, 0.5):
                for rule in ("prop", "wta"):
                    cache = {}
                    def out(bal):
                        if bal not in cache: cache[bal] = tally(ws, blocs, bal, q, rule, quorum)
                        return cache[bal]
                    gain = gab = n = 0
                    for bal in itertools.product((1, -1), repeat=10):  # sincere profiles, no sincere abstainers
                        o = out(bal)
                        for i, v in enumerate(bal):
                            n += 1; want = v == 1
                            if o == want: continue
                            for d in (-v, 0):
                                b2 = bal[:i] + (d,) + bal[i + 1:]
                                if out(b2) == want: gain += 1; gab += d == 0; break
                    print(f"| {ws} | {q} | {quorum} | {rule} | {n} | {gain} | {gab} |")

def s2(ws=(25, 25, 25, 25), q=0.5, M=6, T=1_500, seed=3):
    rng = np.random.default_rng(seed); W = np.array(ws, float)
    print(f"\n### weights {list(ws)}, q={q}, {M} motions, {T} agendas; bloc utility u[b,m]~N(0,1) per motion passed")
    print("| bloc type | delta | P(>=1 trade) | mean trades | mean welfare change (sum u) | P(welfare falls) | P(a non-trader loses) | P(a trader ends worse than sincere) |")
    print("|---|---|---|---|---|---|---|---|")
    for kind, delta in (("unitary", 1.0), ("proportional", 0.5), ("proportional", 0.2)):
        stats = np.zeros(6)
        for t in range(T):
            u = rng.standard_normal((4, M))
            share0 = 1 / (1 + np.exp(-2 * u)) if kind == "proportional" else (u > 0).astype(float)
            dev = np.zeros((4, M))  # signed share shift each bloc has committed to
            def outcome(dv):
                s = np.clip(share0 + dv, 0, 1); return (W @ s) >= q * W.sum() - 1e-9
            def util(o): return u @ o
            o_s = outcome(dev); U_s = util(o_s); cur = U_s.copy(); trades = 0; traders = set()
            while True:
                best = None
                for A, B in itertools.permutations(range(4), 2):
                    if A > B: continue
                    for x in range(M):
                        if dev[A, x] != 0: continue
                        for y in range(M):
                            if y == x or dev[B, y] != 0: continue
                            d2 = dev.copy(); d2[A, x] = -np.sign(u[A, x]) * delta; d2[B, y] = -np.sign(u[B, y]) * delta
                            Un = util(outcome(d2)); g = Un - cur
                            if g[A] > 1e-9 and g[B] > 1e-9 and (best is None or g[A] + g[B] > best[0]):
                                best = (g[A] + g[B], d2, Un, A, B)
                if best is None or trades >= 6: break
                _, dev, cur, A, B = best; trades += 1; traders |= {A, B}
            dW = cur.sum() - U_s.sum(); non = [b for b in range(4) if b not in traders]
            stats += [trades > 0, trades, dW, dW < -1e-9, any(cur[b] < U_s[b] - 1e-9 for b in non),
                      any(cur[b] < U_s[b] - 1e-9 for b in traders)]
        s = stats / T
        print(f"| {kind} | {delta} | {s[0]:.3f} | {s[1]:.2f} | {s[2]:+.3f} | {s[3]:.3f} | {s[4]:.3f} | {s[5]:.3f} |")

if __name__ == "__main__":
    print("## S1. Profitable misreport/abstention on a single binary motion (sizes 3/3/2/2, exhaustive)")
    s1()
    print("\n## S2. Logrolling (greedy best mutually profitable pairwise trade, repeated, max 6 trades)")
    s2(); s2(ws=(40, 30, 20, 10))
