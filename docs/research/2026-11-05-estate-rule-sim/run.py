"""One-command rerun: python3 -I run.py [--quick] [--out results.md] [--jobs N]

Writes a deterministic markdown results file (no timestamps). See SPEC.md section 7.
"""
import argparse
import os
import random
import sys
from multiprocessing import Pool

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import estatesim as E  # noqa: E402

REPS, MOTIONS = 40, 500
QUICK_REPS, QUICK_MOTIONS = 4, 100
METRICS = ("PS", "MT", "BPP", "GBR", "PASS")
PAIRS = (("UNI", "DIRECT"), ("WTA", "DIRECT"), ("UNI", "WTA"))


def _job(args):
    cfg, rep = args
    return E.run_replicate(cfg, rep)


def evaluate(cfg_list, reps, jobs):
    """Run every (cfg, rep); returns list of per-config result dicts."""
    tasks = [(cfg, r) for cfg in cfg_list for r in range(reps)]
    if jobs > 1:
        with Pool(jobs) as p:
            out = p.map(_job, tasks, chunksize=2)
    else:
        out = [_job(t) for t in tasks]
    res = []
    for k, cfg in enumerate(cfg_list):
        chunk = out[k * reps:(k + 1) * reps]
        res.append(summarize(cfg, chunk))
    return res


def summarize(cfg, chunk):
    recs = [c[0] for c in chunk]
    per = {}   # rule -> metric -> list over reps (strategic) ; plus sincere PS
    for ri, rule in enumerate(E.RULES):
        per[rule] = {m: [] for m in METRICS}
        per[rule]["PS_sincere"] = []
        per[rule]["FLIP"] = []
        for rec in recs:
            mm = E.rep_metrics(rec, ri)
            for m in METRICS:
                per[rule][m].append(mm[m])
            per[rule]["PS_sincere"].append(E.rep_metrics(rec, ri, which=4)["PS"])
            per[rule]["FLIP"].append(sum(1 for r in rec if r[3][ri] != r[4][ri]) / len(rec))
    percat = {}
    for ci in range(len(E.CATS)):
        for ri, rule in enumerate(E.RULES):
            percat[(ci, rule)] = [E.rep_metrics(rec, ri, cats={ci})["PS"] for rec in recs]
    paired = {}
    for a, b in PAIRS:
        for m in ("PS", "MT"):
            paired[(a, b, m)] = E.mean_ci([x - y for x, y in zip(per[a][m], per[b][m])])
    conc = {}
    allc = [c for ch in chunk for c in ch[1]]
    for k, nm in enumerate(("top_share", "gini", "n_eff", "weight")):
        conc[nm] = sum(c[k] for c in allc) / len(allc)
    dl = sum(c[5] for c in allc)
    conc["eff_top"] = sum(c[7] for c in allc) / len(allc)             # top share counting copiers to the voter they copied
    conc["eff_neff"] = sum(c[8] for c in allc) / len(allc)
    conc["copied"] = sum(c[9] for c in allc) / dl if dl else 0.0       # copiers as a share of delegated votes
    conc["eff_top_reps"] = [sum(c[7] for c in ch[1]) / len(ch[1]) for ch in chunk]   # per replicate, for paired contrasts
    conc["live"] = sum(c[6] for c in allc) / len(allc)                # share of delegators still holding an edge
    conc["uncast"] = sum(c[4] for c in allc) / dl if dl else 0.0       # share of delegated votes left uncast
    return dict(cfg=cfg, per=per, percat=percat, paired=paired, conc=conc,
                verdict=E.verdict(paired[("UNI", "DIRECT", "PS")], paired[("UNI", "DIRECT", "MT")]))


def f(x, ci=None, d=3):
    if x != x:
        return "n/a"
    s = f"{x:.{d}f}"
    return s + (f" ±{ci:.{d}f}" if ci is not None and ci == ci else "")


def rule_table(r):
    lines = ["| rule | PS | MT | PI=PS/MT | BPP | GBR | pass | PS sincere | outcome flipped by strategy |",
             "|---|---|---|---|---|---|---|---|---|"]
    for rule in E.RULES:
        p = r["per"][rule]
        ps, mt = E.mean_ci(p["PS"]), E.mean_ci(p["MT"])
        pi = ps[0] / mt[0] if mt[0] >= .05 else float("nan")
        lines.append(f"| {rule} | {f(*ps)} | {f(*mt)} | {f(pi)} | {f(*E.mean_ci(p['BPP']))} | "
                     f"{f(*E.mean_ci(p['GBR']))} | {f(*E.mean_ci(p['PASS']))} | "
                     f"{f(*E.mean_ci(p['PS_sincere']))} | {f(*E.mean_ci(p['FLIP']))} |")
    return lines


def pair_line(r):
    pr = r["paired"]
    parts = []
    for a, b in PAIRS:
        parts.append(f"{a}-{b}: dPS {f(*pr[(a, b, 'PS')])}, dMT {f(*pr[(a, b, 'MT')])}")
    return "; ".join(parts) + f"; claim (UNI vs DIRECT): **{r['verdict']}**"


def percat_table(r):
    lines = ["| q | " + " | ".join(E.RULES) + " |", "|---|" + "---|" * len(E.RULES)]
    for ci, q in enumerate(E.CATS):
        lines.append(f"| {q} | " + " | ".join(f(*E.mean_ci(r["percat"][(ci, ru)]), d=2) for ru in E.RULES) + " |")
    return lines


def experiments(quick):
    X = []
    if quick:
        alphas, caps, facs, betas = (0.0, 1.5), (.2, None), (0, 20), (0.0, 3.0)
    else:
        alphas, caps, facs, betas = (0.0, .5, 1.0, 1.5, 2.5, 4.0), (.10, .15, .20, .25, .30, None), (0, 10, 20, 40), (0.0, 1.0, 3.0)
    X.append(("E1 baseline (sincere, no delegation, no faction)",
              [(w, E.Config(weights=w)) for w in ("equal", "minds-heavy")], True))
    X.append(("E2 strategic agents (rho=.25) and Users faction size",
              [(f"faction={k}", E.Config(rho=.25, faction=k)) for k in facs], True))
    X.append(("E3 delegation: concentration alpha x per-delegate cap (depth 3, strategic, faction 20)",
              [(f"alpha={a} cap={c}", E.Config(delegation=True, alpha=a, cap=c, rho=.25, faction=20))
               for a in alphas for c in caps], False))
    X.append(("E3b same grid, no faction, sincere (isolates delegation)",
              [(f"alpha={a} cap={c}", E.Config(delegation=True, alpha=a, cap=c))
               for a in alphas for c in caps], False))
    depths = (2, None) if quick else (2, 3, 4, 6, None)
    X.append(("E3c transitive depth x delegate turnout x peer-delegation share (alpha=1.5, cap=.2, strategic, faction 20)",
              [(f"depth={d} dturn={t} peer={p}", E.Config(delegation=True, alpha=1.5, depth=d, dturn=t, peer=p, cap=.2, rho=.25, faction=20))
               for d in depths for t in (.95, .3) for p in (.3, .8)], False))
    copies = (0.0, 1.0) if quick else (0.0, .5, 1.0)
    X.append(("E3f copy-the-ballot: alpha x cap x copy rate (depth 3, strategic, faction 20)",
              [(f"alpha={a} cap={c} copy={p}", E.Config(delegation=True, alpha=a, cap=c, copy=p, rho=.25, faction=20))
               for a in alphas for p in copies for c in caps], "copy"))
    X.append(("E3e revocation renewal: how fast revoked delegators re-delegate (alpha=1.5, cap=.2, depth 3, strategic, faction 20)",
              [(f"renew={n}", E.Config(delegation=True, alpha=1.5, cap=.2, renew=n, rho=.25, faction=20))
               for n in (0.0, .02, .1, .5)], False))
    X.append(("E3d cap basis: share of cast weight vs share of roll (alpha=1.5, depth=3, strategic, faction 20)",
              [(f"basis={bs} cap={c}", E.Config(delegation=True, alpha=1.5, cap=c, cap_basis=bs, rho=.25, faction=20))
               for bs in ("cast", "roll") for c in ((.1, .2) if quick else (.05, .1, .2, .3))], False))
    X.append(("E4 appeal attraction beta (alpha=1.5, cap=.2, strategic, faction 20)",
              [(f"beta={b}", E.Config(delegation=True, alpha=1.5, cap=.2, beta=b, rho=.25, faction=20))
               for b in betas], True))
    rob = [("users appeal b=.4", (("Users", "b", .4),)), ("users appeal b=.9 (default)", ()),
           ("users appeal b=1.4", (("Users", "b", 1.4),)),
           ("users turnout .05", (("Users", "turnout", .05),)), ("users turnout .30", (("Users", "turnout", .30),)),
           ("minds shared s=.3", (("Minds", "s", .3),)), ("minds shared s=1.5", (("Minds", "s", 1.5),))]
    X.append(("E5 robustness to assumed parameters (no delegation, strategic, faction 20)",
              [(nm, E.Config(rho=.25, faction=20, overrides=ov)) for nm, ov in rob], True))
    X.append(("E5b same, with delegation (alpha=1.5, cap=.2)",
              [(nm, E.Config(delegation=True, alpha=1.5, cap=.2, rho=.25, faction=20, overrides=ov))
               for nm, ov in rob], False))
    return X


def copy_tables(cells, res):
    """Pivots for E3f: rows (alpha, copy), columns cap. Effective = copiers counted to the voter they copied."""
    caps = []
    for _, c in cells:
        if c.cap not in caps:
            caps.append(c.cap)
    rows = {}
    for (_, c), r in zip(cells, res):
        rows.setdefault((c.alpha, c.copy), {})[c.cap] = r
    hdr = ["| alpha | copy | " + " | ".join("none" if c is None else f"{c}" for c in caps) + " |",
           "|---|---|" + "---|" * len(caps)]
    L = []
    for title, get in (
            ("Users top-holder share, formal (copiers are separate weight-1 ballots)", lambda r: f"{r['conc']['top_share']:.3f}"),
            ("Users top-holder share, effective (copiers counted to the voter they copied)", lambda r: f"{r['conc']['eff_top']:.3f}"),
            ("Users n_eff, effective", lambda r: f"{r['conc']['eff_neff']:.1f}"),
            ("copiers as a share of delegated votes", lambda r: f"{r['conc']['copied']:.2f}"),
            ("dPS UNI-DIRECT", lambda r: f(*r["paired"][("UNI", "DIRECT", "PS")])),
            ("PS UNI", lambda r: f(*E.mean_ci(r["per"]["UNI"]["PS"])))):
        L += [f"**{title}**", ""] + hdr
        for (a, p), byc in rows.items():
            L.append(f"| {a} | {p} | " + " | ".join(get(byc[c]) for c in caps) + " |")
        L.append("")
    return L


CAP_MATERIAL = .01   # effective top-share drop below this is not called a reduction (SPEC section 5b)


def copy_reading(cells, res):
    """SPEC section 5b falsifier, computed: does the tightest cap reduce effective Users top-holder share?

    Paired by replicate (same population draw): top share at cap c minus top share with no cap.
    "reduces" iff the 95% interval lies entirely below -CAP_MATERIAL; "does not reduce" otherwise.
    """
    by = {(c.alpha, c.copy, c.cap): r for (_, c), r in zip(cells, res)}
    tight = min(c.cap for _, c in cells if c.cap is not None)
    tally = {}
    L = [f"**Does the cap reduce effective concentration? Effective Users top-holder share at cap {tight} minus no cap, "
         f"paired by replicate; 'reduces' iff the 95% interval is entirely below -{CAP_MATERIAL}.**", "",
         "| alpha | copy | effective top share, cap - none | verdict | formal top share, cap - none |", "|---|---|---|---|---|"]
    for (a, p, c) in by:
        if c != tight:
            continue
        r, n = by[(a, p, c)], by[(a, p, None)]
        d = E.mean_ci([x - y for x, y in zip(r["conc"]["eff_top_reps"], n["conc"]["eff_top_reps"])])
        v = "reduces" if d[0] + d[1] < -CAP_MATERIAL else "does not reduce"
        L.append(f"| {a} | {p} | {f(*d)} | {v} | {r['conc']['top_share'] - n['conc']['top_share']:+.3f} |")
        tally.setdefault(p, []).append(v == "reduces")
    L += ["", "Reading, computed from the rows above: the cap reduces effective concentration in " + "; ".join(
        f"{sum(t)} of {len(t)} alpha values at copy rate {p}" for p, t in tally.items()) +
        ". Where it reduces at copy 0 and not at copy 1, the formal top share falls (last column) while the weight follows "
        "the same voters through their copiers, so the cap moves weight from the holder's ledger to its followers' ballots "
        "without changing whose ballot it is."]
    return L + [""]


PATRONS = E.ESTATES.index("Patrons")
LIVE = {"all four estates": (0, 1, 2, 3), "Patrons + Minds": (0, PATRONS), "Patrons alone": (PATRONS,)}


def _stake_rest(overrides, ladder):
    """Weight of the ordinary patrons under the estate's own euro conversion."""
    return sum(E.stake_weight("Patrons", euros=x, overrides=overrides) for x in ladder)


def stake_section(quick):
    """E6 purchased stake (SPEC section 5c). Pure functions of fixed seeds; no wall clock."""
    seeds = 4 if quick else 40
    amounts = (1e6,) if quick else (1e4, 1e5, 1e6)
    bs, units, rolls = ((10.0,), (10.0,), (4, 40)) if quick else ((2.0, 10.0, 100.0), (1.0, 10.0, 100.0), (4, 40, 400))
    EUR = 1e6
    L = ["## E6 purchased stake: logarithmic weight, splitting, and the half-influence cap (SPEC section 5c)", "",
         "Static weight analysis (no motions are simulated, so no PS or MT): a buyer joins the Patrons estate with one purchase; "
         f"the {E.ROLL['Patrons']}-patron roll of the harness (and 40, 400) holds Pareto({E.PARETO_ALPHA}) donations from {E.PARETO_XM:.0f} EUR, "
         f"{seeds} seeds each. Weight w = 1 + log_b(amount/unit), 0 at amount <= unit/b; defaults b = {E.STAKE_BASE['Patrons']['euro_b']:.0f}, "
         f"unit = {E.STAKE_BASE['Patrons']['euro_unit']:.0f} EUR. Share s is the buyer's share of Patrons weight; x is its share of council weight "
         "(estate weight W x s over the live estates' weights); the cap binds iff x >= .5.", ""]
    ladders = {(n, s): E.stake_ladder(n, s) for n in rolls for s in range(seeds)}
    # headline: the million-euro buyer, defaults, roll of the harness
    n0 = E.ROLL["Patrons"]
    L += ["**Million-euro buyer, defaults, Patrons roll " + str(n0) + " (mean over seeds, 95% interval); linear = weight proportional to amount, the counterfactual the log is meant to beat**", "",
          "| weights | live estates | s log | x log | x linear | cap binds (log) | cap binds (linear) | estate decided by buyer alone (s > .5) |",
          "|---|---|---|---|---|---|---|---|"]
    for wn, W in E.WEIGHTS.items():
        for ln, live in LIVE.items():
            rows = []
            for s in range(seeds):
                lad = ladders[(n0, s)]
                wb = E.stake_weight("Patrons", euros=EUR)
                lo = E.influence_cap(wb, _stake_rest((), lad), W, PATRONS, live)
                li = E.influence_cap(EUR / 10.0, sum(x / 10.0 for x in lad), W, PATRONS, live)
                rows.append((lo["s"], lo["x"], li["x"], lo["binds"], li["binds"], lo["s"] > .5))
            m = [E.mean_ci([r[k] for r in rows]) for k in range(3)]
            fr = [sum(r[k] for r in rows) / seeds for k in (3, 4, 5)]
            L.append(f"| {wn} | {ln} | {f(*m[0])} | {f(*m[1])} | {f(*m[2])} | {fr[0]:.2f} | {fr[1]:.2f} | {fr[2]:.2f} |")
    L.append("")
    # the headline share is one (unit, roll) cell: sweep both, state the interval
    L += ["**Million-euro buyer's share of Patrons weight, b = 10, by unit and roll (mean over seeds, 95% interval; share of seeds in which the buyer alone decides the estate)**", "",
          "| unit EUR | " + " | ".join(f"roll {n}" for n in rolls) + " |", "|---|" + "---|" * len(rolls)]
    cells = []
    for u in units:
        cols = []
        for n in rolls:
            r = E.buyer_runs(EUR, n, seeds, E.WEIGHTS["equal"], (PATRONS,), (("Patrons", "euro_b", 10.0), ("Patrons", "euro_unit", u)))
            m = E.mean_ci([x["s"] for x in r])
            cells.append(m[0])
            cols.append(f"{f(*m)} ({sum(x['s'] > .5 for x in r) / seeds:.2f})")
        L.append(f"| {u:g} | " + " | ".join(cols) + " |")
    L += ["", f"Interval over the swept cells: s from {min(cells):.3f} to {max(cells):.3f}. The 0.524 headline is the harness's own cell (roll {n0}, unit 10), "
          "not a typical one: the buyer's majority of the estate depends on how few other patrons there are.", ""]
    # cap-binding rate across the whole sweep
    tally = {}
    for wn, W in E.WEIGHTS.items():
        for ln, live in LIVE.items():
            for b in bs:
                for u in units:
                    ov = (("Patrons", "euro_b", b), ("Patrons", "euro_unit", u))
                    for n in rolls:
                        for a in amounts:
                            hit = unf = 0
                            for s in range(seeds):
                                lad = ladders[(n, s)]
                                r = E.influence_cap(E.stake_weight("Patrons", euros=a, overrides=ov), _stake_rest(ov, lad), W, PATRONS, live)
                                hit += r["binds"]
                                unf += r["binds"] and not r["enforceable"]
                            tally.setdefault(ln, []).append((hit, unf, seeds, n, b, a))
    L += ["**Cap-binding rate over the sweep (b, unit, roll, amount, weights x live set; every cell run on every seed)**", "",
          "| live estates | cells | cells x seeds | binding (cell, seed) | cells binding in every seed | cells never binding | binding but unenforceable |",
          "|---|---|---|---|---|---|---|"]
    for ln, t in tally.items():
        tot = sum(x[2] for x in t)
        L.append(f"| {ln} | {len(t)} | {tot} | {sum(x[0] for x in t) / tot:.3f} | "
                 f"{sum(1 for x in t if x[0] == x[2]) / len(t):.3f} | {sum(1 for x in t if x[0] == 0) / len(t):.3f} | "
                 f"{sum(x[1] for x in t) / tot:.3f} |")
    L += ["", "Binding rate in the Patrons-alone scenario by Patrons roll and by log base (fraction of cells x seeds):", "",
          "| roll | " + " | ".join(f"b={b:g}" for b in bs) + " |", "|---|" + "---|" * len(bs)]
    for n in rolls:
        cols = []
        for b in bs:
            t = [x for x in tally["Patrons alone"] if x[3] == n and x[4] == b]
            cols.append(f"{sum(x[0] for x in t) / sum(x[2] for x in t):.3f}")
        L.append(f"| {n} | " + " | ".join(cols) + " |")
    L.append("")
    # splitting
    L += ["**Splitting: the same total split over k identities (defaults, one honest identity = k 1). Multiplier = total weight of the split / weight of the single purchase**", "",
          "| amount EUR | single weight | " + " | ".join(f"k={k}" for k in (2, 10, 100, 1000)) + " | best k (grid) | best multiplier |",
          "|---|---|" + "---|" * 6]
    b0, u0 = E.STAKE_BASE["Patrons"]["euro_b"], E.STAKE_BASE["Patrons"]["euro_unit"]
    for a in amounts:
        w1 = E.log_weight(a, b0, u0)
        bt, bk = E.best_split(a, b0, u0)
        L.append(f"| {a:g} | {w1:.2f} | " + " | ".join(
            f"{E.split_weight(a, k, b0, u0)[0] / w1:.1f}" for k in (2, 10, 100, 1000)) +
            f" | {bk} | {bt / w1:.0f} |")
    L += ["", "Best multiplier at 1,000,000 EUR by log base and unit (b rows, unit columns):", "",
          "| b | " + " | ".join(f"unit={u:g}" for u in units) + " |", "|---|" + "---|" * len(units)]
    for b in bs:
        cols = []
        for u in units:
            bt, bk = E.best_split(EUR, b, u)
            cols.append(f"{bt / E.log_weight(EUR, b, u):.0f} (k={bk})")
        L.append(f"| {b:g} | " + " | ".join(cols) + " |")
    # defences, candidates only
    L += ["", "**Candidate defences, none adopted by SPEC (best multiplier at 1,000,000 EUR, defaults)**", "",
          "| defence | setting | best multiplier | neutralises (best split is k = 1) |", "|---|---|---|---|"]
    w1 = E.log_weight(EUR, b0, u0)
    for wc in (1.0, 3.0, w1):
        s1 = min(w1, wc)
        bt, bk = E.best_split(EUR, b0, u0, person_cap=wc)
        L.append(f"| per-person weight cap | {wc:.1f} | {bt / s1:.0f} | {'yes' if bk == 1 else 'no'} |")
    for uf in (10.0, 1e3, 1e5, 5e5, 1e6):
        s1 = E.log_weight(EUR, b0, uf)
        bt, bk = E.best_split(EUR, b0, uf)
        L.append(f"| unit floor | {uf:g} EUR | {bt / s1:.1f} | {'yes' if bk == 1 else 'no'} |")
    for a in amounts:
        mc = E.min_neutralising_cost(a, b0, u0)
        L.append(f"| identity cost | smallest cost by bisection, amount {a:g} EUR | | "
                 + (f"yes at {mc:.0f} EUR per identity ({mc / a:.1%} of the budget, bisection to 0.1%)" if mc else "no cost below the amount") + " |")
    # cap evasion by splitting, Patrons alone
    L += ["", "**Cap evasion: the cap is per identity. Patrons alone, equal weights, roll " + str(n0) + ", defaults, 1,000,000 EUR over k identities**", "",
          "| k | joint share x | share of one identity | cap binds on an identity | joint x >= .5 (evaded) |", "|---|---|---|---|---|"]
    W = E.WEIGHTS["equal"]
    for k in (1, 2, 10, 100):
        rows = []
        for s in range(seeds):
            rest = _stake_rest((), ladders[(n0, s)])
            tot, wi = E.split_weight(EUR, k, b0, u0)
            joint = E.influence_cap(tot, rest, W, PATRONS, (3,))
            one = E.influence_cap(wi, rest + tot - wi, W, PATRONS, (3,))
            rows.append((joint["x"], one["x"], one["binds"], joint["x"] >= E.HALF and not one["binds"]))
        L.append(f"| {k} | {f(*E.mean_ci([r[0] for r in rows]))} | {f(*E.mean_ci([r[1] for r in rows]))} | "
                 f"{sum(r[2] for r in rows) / seeds:.2f} | {sum(r[3] for r in rows) / seeds:.2f} |")
    # hours channel
    L += ["", "**Contribution hours: Members weight from hours alone (no mechanism for contribution existed in the harness; new per-agent input, own per-estate conversion)**", "",
          "| hours | " + " | ".join(f"{h:g}" for h in (5, 10, 100, 1000, 10000)) + " |", "|---|---|---|---|---|---|",
          "| weight | " + " | ".join(f"{E.stake_weight('Members', hours=h):.2f}" for h in (5, 10, 100, 1000, 10000)) + " |", ""]
    return L


MEMBERS = E.ESTATES.index(E.CREDIT_ESTATE)
CLIVE = {"all four estates": (0, 1, 2, 3), "Members + Minds": (0, MEMBERS), "Members alone": (MEMBERS,)}
CREDIT_CAPS = (None, 100.0, 20.0, 5.0)     # per-person credit cap, multiples of the ordinary members' median credit


def _capname(m):
    return "none" if m is None else f"{m:g}x median"


def credit_section(quick):
    """E7 contribution-weighted arm (SPEC section 5d). Pure functions of fixed seeds; no wall clock."""
    seeds = 4 if quick else 40
    phis = (.1, .5, .9) if quick else (.05, .1, .25, .4, .5, .6, .75, .9)
    rolls = (E.ROLL["Members"],) if quick else (E.ROLL["Members"], 150)
    ks = (1, 2, 10, 100)
    L = ["## E7 contribution-weighted arm: linear credit, per-person cap, the half-influence cap (SPEC section 5d)", "",
         f"Static weight analysis. One contributor holds a fraction phi of the {E.CREDIT_ESTATE} estate's total raw credit (receipts: hours or credited "
         f"changes, one linear unit); {rolls[0]}" + (f" and {rolls[1]}" if len(rolls) > 1 else "") +
         f" ordinary members hold Pareto({E.PARETO_ALPHA}) credit from {E.PARETO_XM:.0f}, {seeds} seeds each. Weight = min(credit, per-person cap), the same function "
         "for the contributor and everyone else. The tally and the half cap are E6's: council share x = W x s over the live estates; the cap binds iff x >= .5. "
         "Credit is not vote weight anywhere in this module; this table only says how often the half cap would have to act.", ""]
    for ln, live in CLIVE.items():
        L += [f"**Half cap binds, {ln}: fraction of (weights x roll x seed) by contributor share phi (rows) and per-person credit cap (columns); one identity, k = 1**", "",
              "| phi | " + " | ".join(_capname(m) for m in CREDIT_CAPS) + " |", "|---|" + "---|" * len(CREDIT_CAPS)]
        for phi in phis:
            cols = []
            for m in CREDIT_CAPS:
                hit = tot = 0
                for W in E.WEIGHTS.values():
                    for n in rolls:
                        for s in range(seeds):
                            hit += E.credit_run(phi, n, s, W, live, m)["binds"]
                            tot += 1
                cols.append(f"{hit / tot:.3f}")
            L.append(f"| {phi:g} | " + " | ".join(cols) + " |")
        L.append("")
    L += ["**Splitting: the contributor's credit split evenly over k identities. Members alone, equal weights, roll "
          f"{rolls[0]}, mean over seeds. binds = the half cap acts on an identity; joint >= .5 = the combined share would have needed cutting; "
          "evaded = joint >= .5 and no identity binds; weight x = total weight / weight at k = 1**", "",
          "| phi | cap | " + " | ".join(f"k={k}: binds / joint>=.5 / evaded / weight x" for k in ks) + " |", "|---|---|" + "---|" * len(ks)]
    W = E.WEIGHTS["equal"]
    for phi in (.25, .5, .9):
        for m in CREDIT_CAPS:
            cols = []
            for k in ks:
                rs = [E.credit_run(phi, rolls[0], s, W, (MEMBERS,), m, k) for s in range(seeds)]
                base = [E.credit_run(phi, rolls[0], s, W, (MEMBERS,), m, 1)["w_total"] for s in range(seeds)]
                cols.append(f"{sum(r['binds'] for r in rs) / seeds:.2f} / {sum(r['joint_binds'] for r in rs) / seeds:.2f} / "
                            f"{sum(r['evaded'] for r in rs) / seeds:.2f} / {sum(r['w_total'] / b for r, b in zip(rs, base)) / seeds:.2f}")
            L.append(f"| {phi:g} | {_capname(m)} | " + " | ".join(cols) + " |")
    L += ["", "Evasion rate over the sweep (phi x cap x k in 2, 10, 100 x live set x weights x roll x seed), cases where the joint share needed cutting:", "",
          "| live estates | joint share >= .5 (k > 1) | of which no identity binds (evaded) |", "|---|---|---|"]
    for ln, live in CLIVE.items():
        j = ev = 0
        for phi in phis:
            for m in CREDIT_CAPS:
                for k in ks[1:]:
                    for Wt in E.WEIGHTS.values():
                        for n in rolls:
                            for s in range(seeds):
                                r = E.credit_run(phi, n, s, Wt, live, m, k)
                                j += r["joint_binds"]
                                ev += r["evaded"]
        L.append(f"| {ln} | {j} | {ev} ({ev / j:.3f}) |" if j else f"| {ln} | 0 | 0 |")
    return L + [""]


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("--quick", action="store_true")
    ap.add_argument("--out", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "results.md"))
    ap.add_argument("--jobs", type=int, default=min(os.cpu_count() or 1, 16))
    a = ap.parse_args(argv)
    reps, motions = (QUICK_REPS, QUICK_MOTIONS) if a.quick else (REPS, MOTIONS)
    L = [f"# Estate-rule sim results ({'quick' if a.quick else 'full'}: {reps} replicates x {motions} motions per cell)",
         "", "Generated by `run.py`; spec in SPEC.md. +/- is a 95% interval over replicates. PS populism sensitivity, "
         "MT merit tracking, BPP bad-popular pass rate, GBR good-unpopular block rate (SPEC section 3).", ""]
    tally = {"supported": 0, "fails": 0, "undecided": 0, "n/a": 0}
    failing = []
    for title, cells, detail in experiments(a.quick):
        cfgs = [E.Config(**{**c.__dict__, "motions": motions}) for _, c in cells]
        res = evaluate(cfgs, reps, a.jobs)
        L += [f"## {title}", ""]
        summ = ["| cell | dPS UNI-DIRECT | dMT UNI-DIRECT | dPS WTA-DIRECT | PS DIRECT | PS WTA | PS UNI | Users top share | Users n_eff | uncast | live edges | claim |",
                "|---|---|---|---|---|---|---|---|---|---|---|---|"]
        for (name, _), r in zip(cells, res):
            tally[r["verdict"]] += 1
            if r["verdict"] == "fails":
                failing.append(f"{title.split(' ')[0]} {name}")
            pr, pe = r["paired"], r["per"]
            summ.append(f"| {name} | {f(*pr[('UNI','DIRECT','PS')])} | {f(*pr[('UNI','DIRECT','MT')])} | "
                        f"{f(*pr[('WTA','DIRECT','PS')])} | {f(*E.mean_ci(pe['DIRECT']['PS']))} | "
                        f"{f(*E.mean_ci(pe['WTA']['PS']))} | {f(*E.mean_ci(pe['UNI']['PS']))} | "
                        f"{r['conc']['top_share']:.3f} | {r['conc']['n_eff']:.1f} | {r['conc']['uncast']:.2f} | {r['conc']['live']:.2f} | {r['verdict']} |")
        L += summ + [""]
        if detail == "copy":
            L += copy_reading(cells, res) + copy_tables(cells, res)
        elif detail:
            for (name, _), r in zip(cells, res):
                L += [f"### {title.split(' ')[0]} / {name}", ""] + rule_table(r) + [""]
                L += [pair_line(r), "", "PS by category threshold:", ""] + percat_table(r) + [""]
    L += stake_section(a.quick)
    L += credit_section(a.quick)
    L += ["## Claim tally over all cells (SPEC section 8)", "",
          f"supported {tally['supported']}, fails {tally['fails']}, undecided {tally['undecided']}, n/a {tally['n/a']}", ""]
    if failing:
        L += ["Failing cells:", ""] + [f"- {x}" for x in failing] + [""]
    with open(a.out, "w") as fh:
        fh.write("\n".join(L) + "\n")
    print(f"wrote {a.out}: {tally}")


if __name__ == "__main__":
    main()
