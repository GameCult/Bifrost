"""Monte Carlo: proportional vs winner-take-all estate tallies under a motion model.
Motion model: common merit m~N(0,1), bloc taste e_b~N(0,1), bloc mean support p_b=logistic(1.7*(0.6m+0.8e_b)).
Within-bloc correlation: individual propensities ~ Beta with mean p_b and ICC r_b (beta-binomial).
Users: roll 300, a random sample of round(300*turnout) votes. Run: python -I mc.py
"""
import numpy as np
rng = np.random.default_rng(20261029)
BLOCS = ["Minds", "Members", "Users", "Patrons"]
WEIGHTS = {"equal": [25, 25, 25, 25], "minds-heavy": [40, 30, 20, 10], "members-heavy": [20, 45, 20, 15]}
T = 200_000

def draw(minds_r=0.44, human_r=0.0, turnout=0.1, T=T):
    sizes = [8, 15, max(1, round(300 * turnout)), 4]
    rs = [minds_r, human_r, human_r, human_r]
    m = rng.standard_normal(T)
    p = 1 / (1 + np.exp(-1.7 * (0.6 * m[:, None] + 0.8 * rng.standard_normal((T, 4)))))
    F = np.zeros((T, 4), int); first = np.zeros((T, 4), int)
    for b, (n, r) in enumerate(zip(sizes, rs)):
        if r <= 0: probs = np.repeat(p[:, b:b + 1], n, 1)
        elif r >= 1: probs = np.repeat((rng.random((T, 1)) < p[:, b:b + 1]).astype(float), n, 1)
        else:
            a = p[:, b] * (1 - r) / r; c = (1 - p[:, b]) * (1 - r) / r
            probs = np.repeat(rng.beta(a, c)[:, None], n, 1)  # one shared propensity per bloc-motion = beta-binomial ICC r
        v = rng.random((T, n)) < probs
        F[:, b] = v.sum(1); first[:, b] = v[:, 0]
    return np.array(sizes), p, F, first

def out_prop(ws, sizes, F, q):
    s = (F / sizes) @ np.array(ws, float); return s >= q * sum(ws) - 1e-9

def out_wta(ws, sizes, F, q):
    ws = np.array(ws, float); st = np.sign(2 * F - sizes)  # +1 For, -1 Against, 0 tie casts nothing
    f = (st == 1) @ ws; a = (st == -1) @ ws
    return (f + a > 0) & (f >= q * (f + a) - 1e-9)

def flip(F, first, b):
    G = F.copy(); G[:, b] += np.where(first[:, b] == 1, -1, 1); return G

def fmt(v): return " | ".join(f"{x:.3f}" for x in v)

if __name__ == "__main__":
    print("## A. P(outcome differs, prop vs WTA) and single-voter pivot rates (Minds ICC .44, humans 0, Users turnout 10% = 30 voters)")
    sizes, p, F, first = draw()
    print("| weights | q | P(pass) prop | P(pass) WTA | P(differ) | pivot prop M/Me/U/P | pivot WTA M/Me/U/P |")
    print("|---|---|---|---|---|---|---|")
    for name, ws in WEIGHTS.items():
        for q in (0.15, 0.5, 0.66):
            op, ow = out_prop(ws, sizes, F, q), out_wta(ws, sizes, F, q)
            pp = [np.mean(op != out_prop(ws, sizes, flip(F, first, b), q)) for b in range(4)]
            pw = [np.mean(ow != out_wta(ws, sizes, flip(F, first, b), q)) for b in range(4)]
            print(f"| {name} | {q} | {op.mean():.3f} | {ow.mean():.3f} | {np.mean(op != ow):.3f} | "
                  + "/".join(f"{x:.4f}" for x in pp) + " | " + "/".join(f"{x:.4f}" for x in pw) + " |")

    print("\n## B. Minds within-bloc correlation (equal weights, q=0.5)")
    print("| Minds ICC | Kish n_eff of 8 | P(differ) | Minds |share-0.5| mean | Minds pivot prop | Minds pivot WTA | P(Minds tie) |")
    print("|---|---|---|---|---|---|---|")
    for r in (0.0, 0.2, 0.44, 0.8, 1.0):
        sizes, p, F, first = draw(minds_r=r, T=100_000); ws = WEIGHTS["equal"]
        op, ow = out_prop(ws, sizes, F, .5), out_wta(ws, sizes, F, .5)
        pp = np.mean(op != out_prop(ws, sizes, flip(F, first, 0), .5)); pw = np.mean(ow != out_wta(ws, sizes, flip(F, first, 0), .5))
        print(f"| {r} | {8 / (1 + 7 * r):.2f} | {np.mean(op != ow):.3f} | {np.mean(abs(F[:, 0] / 8 - .5)):.3f} | {pp:.4f} | {pw:.4f} | {np.mean(2 * F[:, 0] == 8):.3f} |")

    print("\n## C. Users turnout (equal weights, q=0.5, Users roll 300)")
    print("| turnout | Users voters | P(Users WTA cast != population majority) | mean |prop share - p_U| | P(motion != full-turnout result) prop | same, WTA |")
    print("|---|---|---|---|---|---|")
    for t in (0.01, 0.03, 0.1, 0.3):
        sizes, p, F, first = draw(turnout=t, T=100_000); ws = WEIGHTS["equal"]
        st = np.sign(2 * F[:, 2] - sizes[2]); pop = np.sign(p[:, 2] - .5)
        Ffull = F.astype(float).copy(); Ffull[:, 2] = p[:, 2] * sizes[2]
        sfull = sizes.copy()
        opf = out_prop(ws, sfull, Ffull, .5)
        stf = np.sign(2 * F - sizes).astype(float); stf[:, 2] = pop
        wsA = np.array(ws, float); owf = ((stf == 1) @ wsA) >= .5 * (((stf == 1) | (stf == -1)) @ wsA)
        print(f"| {t} | {sizes[2]} | {np.mean(st != pop):.3f} | {np.mean(abs(F[:, 2] / sizes[2] - p[:, 2])):.3f} | "
              f"{np.mean(out_prop(ws, sizes, F, .5) != opf):.3f} | {np.mean(out_wta(ws, sizes, F, .5) != owf):.3f} |")

    print("\n## D. Coordinated Users faction of k voters, Users population support p_U=0.3, turnout 3% (9 random voters + k)")
    print("| k | P(Users bloc cast For) WTA | mean Users For share prop |")
    print("|---|---|---|")
    for k in (0, 3, 6, 9, 12):
        rnd = rng.random((100_000, 9)) < 0.3; f = rnd.sum(1) + k; n = 9 + k
        print(f"| {k} | {np.mean(2 * f > n):.3f} | {np.mean(f / n):.3f} |")
