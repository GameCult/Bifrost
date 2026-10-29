"""No-show incentives under the renormalization ruling (operator 2026-10-29: "normalize it by who actually voted").
Estate weights renormalize over estates where at least one For/Against ballot was cast; WTA tie casts nothing.
A motion failing its quorum fails (status quo). Exhaustive over sincere For/Against profiles.
Deviations: one voter switches or abstains; or a whole estate stays home (estate as unitary actor).
Quorum floors: none | head = cast ballots >= half of eligible individuals | two = at least two estates voting.
Run: python -I noshow.py
"""
import itertools

def tally(ws, sizes, ballots, q, rule, quorum):
    F = [0] * 4; A = [0] * 4; idx = 0
    for b, n in enumerate(sizes):
        for v in ballots[idx:idx + n]:
            F[b] += v == 1; A[b] += v == -1
        idx += n
    live = [b for b in range(4) if F[b] + A[b] > 0]
    if quorum == "head" and sum(F) + sum(A) < sum(sizes) / 2: return False
    if quorum == "two" and len(live) < 2: return False
    f = a = 0.0
    for b in live:
        if rule == "prop": f += ws[b] * F[b] / (F[b] + A[b]); a += ws[b] * A[b] / (F[b] + A[b])
        elif F[b] > A[b]: f += ws[b]
        elif A[b] > F[b]: a += ws[b]
    return f + a > 0 and f >= q * (f + a) - 1e-9

def probe(ws, sizes, q, rule, quorum):
    N = sum(sizes); starts = [sum(sizes[:b]) for b in range(4)]
    ind = est = her = 0; prof = 0
    for bal in itertools.product((1, -1), repeat=N):
        prof += 1; o = tally(ws, sizes, bal, q, rule, quorum)
        for i, v in enumerate(bal):
            want = v == 1
            if o != want and any(tally(ws, sizes, bal[:i] + (d,) + bal[i + 1:], q, rule, quorum) == want for d in (-v, 0)):
                ind += 1
                if sizes[1] == 1 and i == starts[1]: her += 1
        for b in range(4):
            if sizes[b] == 0: continue
            seg = bal[starts[b]:starts[b] + sizes[b]]; pos = sum(seg) > 0 if 2 * sum(x == 1 for x in seg) != len(seg) else None
            if pos is None or o == pos: continue
            home = bal[:starts[b]] + (0,) * sizes[b] + bal[starts[b] + sizes[b]:]
            est += tally(ws, sizes, home, q, rule, quorum) == pos
    return prof, ind, her, est

if __name__ == "__main__":
    print("## N1. Profitable deviations (count of profile x actor cases where deviating gets the actor's sincere preference)")
    print("Present case sizes Minds 8 / Members 1 (her) / Users 0 / Patrons 0; mixed case 3/1/2/1.")
    print("| sizes | weights | q | rule | quorum | profiles | indiv profitable dev | of which hers | estate stay-home profitable |")
    print("|---|---|---|---|---|---|---|---|---|")
    for sizes in ((8, 1, 0, 0), (3, 1, 2, 1)):
        for ws in ((25, 25, 25, 25), (40, 30, 20, 10), (20, 45, 20, 15)):
            for q in (0.5, 0.66):
                for rule in ("prop", "wta"):
                    for quorum in ("none", "head", "two"):
                        p, i, h, e = probe(ws, sizes, q, rule, quorum)
                        print(f"| {'/'.join(map(str, sizes))} | {ws} | {q} | {rule} | {quorum} | {p} | {i} | {h} | {e} |")

    print("\n## N2. Present case outcome map (Minds 8 independent fair coins, her vote fixed), renormalized")
    print("P(pass | she votes For) / P(pass | she votes Against) / P(pass | she stays home)")
    print("| weights | q | rule | quorum | For | Against | home |")
    print("|---|---|---|---|---|---|---|")
    from math import comb
    for ws in ((25, 25, 25, 25), (40, 30, 20, 10), (20, 45, 20, 15)):
        for q in (0.15, 0.5, 0.66):
            for rule in ("prop", "wta"):
                for quorum in ("none", "two"):
                    row = []
                    for hv in (1, -1, 0):
                        pp = sum(comb(8, k) / 256 * tally(ws, (8, 1, 0, 0), (1,) * k + (-1,) * (8 - k) + (hv,), q, rule, quorum) for k in range(9))
                        row.append(f"{pp:.3f}")
                    print(f"| {ws[:2]} | {q} | {rule} | {quorum} | " + " | ".join(row) + " |")
