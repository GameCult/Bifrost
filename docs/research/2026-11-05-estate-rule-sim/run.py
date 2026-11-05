"""One-command rerun: python3 -I run.py [--quick] [--out results.md] [--jobs N]

Writes a deterministic markdown results file (no timestamps). See SPEC.md section 7.
"""
import argparse
import os
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
    L += ["## Claim tally over all cells (SPEC section 8)", "",
          f"supported {tally['supported']}, fails {tally['fails']}, undecided {tally['undecided']}, n/a {tally['n/a']}", ""]
    if failing:
        L += ["Failing cells:", ""] + [f"- {x}" for x in failing] + [""]
    with open(a.out, "w") as fh:
        fh.write("\n".join(L) + "\n")
    print(f"wrote {a.out}: {tally}")


if __name__ == "__main__":
    main()
