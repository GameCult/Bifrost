"""Estate-rule simulation core. Pure stdlib. Spec: SPEC.md in this directory.

One replicate = one population (delegation network, strategic flags, faction)
plus a sequence of motions. Every random draw comes from two Random objects
seeded from integers, so a (Config, rep) pair is a pure function.
"""
import math
import random
from dataclasses import dataclass

ESTATES = ("Minds", "Members", "Users", "Patrons")
ROLL = {"Minds": 8, "Members": 15, "Users": 300, "Patrons": 4}
BASE = {
    "Minds": dict(a=1.0, b=.15, s=.9, sigma=.7, turnout=1.0, kappa=0.0),
    "Members": dict(a=.8, b=.4, s=.5, sigma=1.0, turnout=.8, kappa=1.0),
    "Users": dict(a=.4, b=.9, s=.5, sigma=1.2, turnout=.10, kappa=1.0),
    "Patrons": dict(a=.7, b=.5, s=.5, sigma=1.0, turnout=.8, kappa=1.0),
}
DELEG_SHARE = {"Minds": 0.0, "Members": .2, "Users": .5, "Patrons": .2}
WEIGHTS = {"equal": (25, 25, 25, 25), "minds-heavy": (40, 30, 20, 10)}
CATS = (.15, .30, .40, .50, .66)
RULES = ("DIRECT", "HEAD", "WTA", "UNI")
ABSTAIN_BAND = .1
STRONG = .5          # |u| above which a final holder's opposing ballot is a strike
STRIKES = 2          # strikes that revoke a delegation for the rest of the replicate
SCOPE_P = .6
DELEGATE_FRAC = .1
POPULIST_DELEGATE_P = .25
EPS = 1e-9


@dataclass(frozen=True)
class Config:
    weights: str = "equal"
    delegation: bool = False
    alpha: float = 1.0       # Zipf exponent of delegate choice (concentration)
    beta: float = 0.0        # extra attraction of populist delegates
    cap: float = None        # per-final-holder cap as a fraction of cap_basis; None = no cap
    cap_basis: str = "cast"  # "cast": estate weight resolved before capping; "roll": estate roll size
    depth: int = 3           # max delegation edges a vote may traverse; None = unlimited
    renew: float = .1        # per-motion probability a revoked delegator picks a new delegate (0: revocation is final)
    peer: float = .3         # probability a delegator's edge goes to another delegator, not an active delegate
    dturn: float = .95       # delegates' own turnout; a delegate who stays home passes weight onward
    onward: float = .3       # probability an active delegate itself delegates onward
    copy: float = 0.0        # probability a delegator under an over-cap holder copies that holder's ballot instead (SPEC section 5b)
    rho: float = 0.0         # strategic fraction
    faction: int = 0         # coordinated Users faction size
    motions: int = 400
    overrides: tuple = ()    # ((estate, key, value), ...)


# ---------------------------------------------------------------- rules

def passes(rule, F, A, W, q):
    """Does the motion pass? F, A: per-estate resolved For/Against weight; W: estate weights."""
    if rule == "HEAD":
        f, a = sum(F), sum(A)
        return f + a > 0 and f >= q * (f + a) - EPS
    fo = tot = 0.0
    for e in range(len(F)):
        n = F[e] + A[e]
        if n <= 0:
            continue
        if rule == "DIRECT":
            fo += W[e] * F[e] / n
        elif rule == "WTA":
            if F[e] == A[e]:
                continue                      # tied estate casts nothing and drops out
            if F[e] > A[e]:
                fo += W[e]
        elif rule == "UNI":
            if F[e] >= q * n - EPS:
                fo += W[e]
        else:
            raise ValueError(rule)
        tot += W[e]
    return tot > 0 and fo >= q * tot - EPS


# ---------------------------------------------------------------- population

class Estate:
    def __init__(self, name, cfg, rng):
        n = ROLL[name]
        p = dict(BASE[name])
        for en, k, v in cfg.overrides:
            if en == name:
                p[k] = v
        self.name, self.n, self.cfg = name, n, cfg
        self.a = [p["a"]] * n
        self.b = [p["b"]] * n
        self.s = p["s"]
        self.sig = [p["sigma"]] * n
        self.turn = [p["turnout"]] * n
        self.kap = [p["kappa"]] * n
        self.target = [None] * n
        self.scope = [None] * n
        self.strikes = [0] * n
        self.revoked = []
        self.delegators = []
        self.pool = None
        self.rng = random.Random(rng.getrandbits(32))
        self.is_delegate = [False] * n
        self.populist = [False] * n
        self.faction = [False] * n
        self.strategic = [rng.random() < cfg.rho for _ in range(n)]
        ids = list(range(n))
        rng.shuffle(ids)
        rest = ids
        share = DELEG_SHARE[name]
        if cfg.delegation and share > 0:
            nd = max(1, round(DELEGATE_FRAC * n))
            delegates, rest = ids[:nd], ids[nd:]
            for d in delegates:
                self.is_delegate[d] = True
                self.populist[d] = rng.random() < POPULIST_DELEGATE_P
                if self.populist[d]:
                    self.a[d], self.b[d], self.sig[d] = .2, 1.2, .7
                else:
                    self.a[d], self.b[d], self.sig[d] = .7, .5, .7
                self.turn[d], self.kap[d] = cfg.dturn, 0.0
            wts = [(k + 1) ** -cfg.alpha * ((1 + cfg.beta) if self.populist[d] else 1.0)
                   for k, d in enumerate(delegates)]
            self.pool = (delegates, wts)
            nde = min(len(rest), round(share * n))
            delegators, rest = rest[:nde], rest[nde:]
            self.delegators = delegators
            for i in delegators:
                self.target[i] = self._pick(i, rng)
                self.scope[i] = self._scope(rng)
            if len(delegates) > 1:
                for k, d in enumerate(delegates):
                    if rng.random() < cfg.onward:
                        others = [(x, w) for x, w in zip(delegates, wts) if x != d]
                        self.target[d] = rng.choices([x for x, _ in others], [w for _, w in others])[0]
                        self.scope[d] = self._scope(rng)
        if name == "Users":
            for i in rest[:cfg.faction]:
                self.faction[i] = True
        self.ubar = [.8 * math.sqrt(self.a[i] ** 2 + self.b[i] ** 2 + self.s ** 2 + self.sig[i] ** 2)
                     for i in range(n)]

    def _pick(self, i, rng):
        """A new delegation target for delegator i: a peer delegator, or an active delegate by Zipf rank."""
        if len(self.delegators) > 1 and rng.random() < self.cfg.peer:
            return rng.choice([x for x in self.delegators if x != i])
        return rng.choices(*self.pool)[0]

    @staticmethod
    def _scope(rng):
        sc = frozenset(c for c in range(len(CATS)) if rng.random() < SCOPE_P)
        return sc or frozenset((rng.randrange(len(CATS)),))

    def chain(self, cat, voting):
        """Final holder of every non-voting node's weight this motion.

        Follows delegation edges until a node that votes directly. The chain is
        void (vote uncast) when any link's scope excludes cat (the usable scope
        is the overlap of all links), it loops, it exceeds cfg.depth edges, or it
        ends at a node with no edge. Returns (dest, uncast): dest maps node ->
        final holder, uncast counts non-voting nodes that hold an edge but are void.
        """
        D = self.cfg.depth
        res = {}                      # node -> (holder, edges) or None for void
        for i in range(self.n):
            if voting[i] or self.target[i] is None or i in res:
                continue
            path, seen, cur = [], set(), i
            out = None
            while True:
                if voting[cur]:
                    out = (cur, 0)
                    break
                if cur in res:
                    out = res[cur]
                    break
                if cur in seen or self.target[cur] is None or cat not in self.scope[cur]:
                    break                          # loop, dead end or out of scope
                seen.add(cur)
                path.append(cur)
                cur = self.target[cur]
            for node in reversed(path):            # void propagates upstream; distances add
                out = (out[0], out[1] + 1) if out else None
                res[node] = out
        dest, uncast = {}, 0
        for i, r in res.items():
            if voting[i] or self.target[i] is None:
                continue
            if r is not None and (D is None or r[1] <= D):
                dest[i] = r[0]
            else:
                uncast += 1
        return dest, uncast

    def resolve(self, cat, voting):
        dest, uncast = self.chain(cat, voting)
        w = {}
        for i in range(self.n):
            if voting[i]:
                w[i] = w.get(i, 0.0) + 1.0
        for i, h in dest.items():
            w[h] = w.get(h, 0.0) + 1.0
        self.last_raw, self.last_cv = dict(w), None
        if self.cfg.cap is not None:
            base = self.n if self.cfg.cap_basis == "roll" else sum(w.values())
            cv = self.last_cv = max(1.0, self.cfg.cap * base)
            for t in w:
                if w[t] > cv:
                    w[t] = cv                          # excess is void, not redistributed
        self.last_uncast = uncast
        return w, dest

    def copy_step(self, cat, voting, sb, dest):
        """Copy-the-ballot (SPEC section 5b), run once on the final resolution.

        Each delegator whose final holder is over the cap copies that holder's
        ballot with probability cfg.copy: it becomes a direct voter (weight 1,
        own cap exposure only) casting the holder's ballot. Mutates voting and sb.
        Returns (copiers {delegator: holder}, w, dest) re-resolved, or None when
        nobody copies. Draws from self.rng only when copying is on and a holder is over cap.
        """
        raw, cv = self.last_raw, self.last_cv
        if not self.cfg.copy or cv is None:
            return None
        copiers = {}
        for i in sorted(dest):
            h = dest[i]
            if raw[h] > cv + EPS and self.rng.random() < self.cfg.copy:
                copiers[i] = h
        if not copiers:
            return None
        for i, h in copiers.items():
            voting[i] = True
            sb[i] = sb[h]
        w, dest = self.resolve(cat, voting)
        return copiers, w, dest

    @staticmethod
    def effective(w, copiers):
        """Weight by whose ballot it follows: a copier's weight counts to the voter it copied."""
        eff = dict(w)
        for c, h in copiers.items():
            eff[h] = eff.get(h, 0.0) + eff.pop(c, 0.0)
        return eff

    def motion(self, cat, m, d, rng, z):
        """Draw this estate's motion state; returns dict with resolved holders and joiners."""
        n = self.n
        if self.revoked:
            keep = []
            for i in self.revoked:
                if self.rng.random() < self.cfg.renew:
                    self.target[i] = self._pick(i, self.rng)
                    self.scope[i] = self._scope(self.rng)
                    self.strikes[i] = 0
                else:
                    keep.append(i)
            self.revoked = keep
        u = [self.a[i] * m + self.b[i] * d + self.s * z + self.sig[i] * rng.gauss(0, 1) for i in range(n)]
        un = [rng.random() for _ in range(n)]
        agenda = d - m > 0
        voting, sb = [False] * n, [0] * n
        for i in range(n):
            if self.kap[i]:
                p = self.turn[i] * (1 + self.kap[i] * (abs(u[i]) - self.ubar[i]))
                p = min(1.0, max(0.0, p))
            else:
                p = self.turn[i]
            voting[i] = un[i] < p
            sb[i] = 1 if u[i] > ABSTAIN_BAND else (-1 if u[i] < -ABSTAIN_BAND else 0)
            if self.faction[i]:
                voting[i] = True
                sb[i] = 1 if agenda else -1
        w, dest = self.resolve(cat, voting)
        forced = False
        for i, dd in dest.items():
            if abs(u[i]) > STRONG and sb[dd] * u[i] < 0:
                voting[i] = True
                forced = True
                self.strikes[i] += 1
                if self.strikes[i] >= STRIKES:
                    self.target[i] = None              # revoked until renewed (cfg.renew)
                    self.revoked.append(i)
        if forced:
            w, dest = self.resolve(cat, voting)
        copiers = {}
        cs = self.copy_step(cat, voting, sb, dest)
        if cs:
            copiers, w, dest = cs
        holders = [(t, sb[t], w[t]) for t in w]
        joiners = [(i, sb[i]) for i in range(n)
                   if not voting[i] and i not in dest and sb[i] != 0]
        return dict(u=u, holders=holders, joiners=joiners, weights=sorted(w.values()),
                copiers=copiers, eff_weights=sorted(self.effective(w, copiers).values()),
                uncast=self.last_uncast, delegated=len(dest) + self.last_uncast,
                live=sum(1 for i in self.delegators if self.target[i] is not None) / max(1, len(self.delegators)))


def concentration(weights):
    """(top share, gini, kish n_eff, total) of resolved holder weights."""
    tot = sum(weights)
    if not weights or tot <= 0:
        return (0.0, 0.0, 0.0, 0.0)
    ws = sorted(weights)
    n = len(ws)
    cum = sum((k + 1) * x for k, x in enumerate(ws))
    gini = (2 * cum) / (n * tot) - (n + 1) / n
    neff = tot * tot / sum(x * x for x in ws)
    return (ws[-1] / tot, gini, neff, tot)


# ---------------------------------------------------------------- one motion

def strategize(rule, W, q, F, A, agents, order_seed):
    """Myopic best response of strategic agents, three rounds. agents: dicts with
    e, w, sincere, cur, pref(want pass), opts. Mutates F, A, agent['cur']."""
    rng = random.Random(order_seed)
    for _ in range(3):
        order = list(range(len(agents)))
        rng.shuffle(order)
        changed = False
        for k in order:
            ag = agents[k]
            e, w = ag["e"], ag["w"]
            _move(F, A, e, ag["cur"], 0, w)
            pick = ag["sincere"]
            for o in ag["opts"]:
                _move(F, A, e, 0, o, w)
                ok = passes(rule, F, A, W, q) == ag["pref"]
                _move(F, A, e, o, 0, w)
                if ok:
                    pick = o
                    break
            _move(F, A, e, 0, pick, w)
            if pick != ag["cur"]:
                changed = True
                ag["cur"] = pick
        if not changed:
            break


def _move(F, A, e, old, new, w):
    if old == 1:
        F[e] -= w
    elif old == -1:
        A[e] -= w
    if new == 1:
        F[e] += w
    elif new == -1:
        A[e] += w


def run_replicate(cfg, rep, base_seed=20261105):
    """Returns (records, conc). records[k] = (cat_idx, m, d, strat_bits, sincere_bits)."""
    pop_rng = random.Random(base_seed * 1_000_003 + rep)
    mot_rng = random.Random(base_seed * 7_919 + rep + 17)
    estates = [Estate(nm, cfg, pop_rng) for nm in ESTATES]
    W = WEIGHTS[cfg.weights]
    records, conc = [], []
    for _ in range(cfg.motions):
        ci = mot_rng.randrange(len(CATS))
        q = CATS[ci]
        m, d = mot_rng.gauss(0, 1), mot_rng.gauss(0, 1)
        zs = [mot_rng.gauss(0, 1) for _ in ESTATES]
        sseed = mot_rng.getrandbits(32)
        data = [est.motion(ci, m, d, mot_rng, zs[k]) for k, est in enumerate(estates)]
        F0, A0 = [0.0] * 4, [0.0] * 4
        for e, dt in enumerate(data):
            for _, b, w in dt["holders"]:
                _move(F0, A0, e, 0, b, w)
        conc.append(concentration(data[2]["weights"]) + (data[2]["uncast"], data[2]["delegated"], data[2]["live"])
                    + concentration(data[2]["eff_weights"])[:3:2] + (len(data[2]["copiers"]),))
        sinc, strat = [], []
        for rule in RULES:
            sinc.append(passes(rule, F0, A0, W, q))
            if cfg.rho <= 0:
                strat.append(sinc[-1])
                continue
            F, A = list(F0), list(A0)
            agents = []
            for e, (est, dt) in enumerate(zip(estates, data)):
                for t, b, w in dt["holders"]:
                    if est.strategic[t] and not est.faction[t] and t not in dt["copiers"]:
                        opts = [x for x in (1, -1, 0) if x != b]
                        agents.append(dict(e=e, w=w, sincere=b, cur=b, pref=dt["u"][t] > 0, opts=[b] + opts))
                for i, b in dt["joiners"]:
                    if est.strategic[i]:
                        agents.append(dict(e=e, w=1.0, sincere=0, cur=0, pref=dt["u"][i] > 0, opts=[0, b]))
            # opts[0] is the sincere option (for joiners: stay out), evaluated first
            strategize(rule, W, q, F, A, agents, sseed)
            strat.append(passes(rule, F, A, W, q))
        records.append((ci, m, d, tuple(strat), tuple(sinc)))
    return records, conc


# ---------------------------------------------------------------- metrics

def _mean(xs):
    return sum(xs) / len(xs) if xs else float("nan")


def rep_metrics(records, rule_idx, which=3, cats=None):
    """PS, MT, BPP, GBR, pass rate for one replicate and rule. which: 3 strategic, 4 sincere."""
    rows = [(m, d, rec[which][rule_idx]) for rec in records
            for (ci, m, d) in [rec[:3]] if cats is None or ci in cats]
    def diff(sel_pos, sel_neg):
        p = [o for (m, d, o) in rows if sel_pos(m, d)]
        n = [o for (m, d, o) in rows if sel_neg(m, d)]
        return _mean(p) - _mean(n) if p and n else float("nan")
    ps = diff(lambda m, d: d > 0, lambda m, d: d < 0)
    mt = diff(lambda m, d: m > 0, lambda m, d: m < 0)
    bad = [o for (m, d, o) in rows if m < -.5 and d > .5]
    good = [o for (m, d, o) in rows if m > .5 and d < -.5]
    return dict(PS=ps, MT=mt, BPP=_mean(bad) if bad else float("nan"),
                GBR=1 - _mean(good) if good else float("nan"),
                PASS=_mean([o for (_, _, o) in rows]) if rows else float("nan"))


def mean_ci(xs):
    xs = [x for x in xs if x == x]
    if not xs:
        return (float("nan"), float("nan"))
    mu = _mean(xs)
    if len(xs) < 2:
        return (mu, float("nan"))
    var = sum((x - mu) ** 2 for x in xs) / (len(xs) - 1)
    return (mu, 1.96 * math.sqrt(var / len(xs)))


def verdict(dps, dmt):
    """SPEC section 8. dps, dmt: (mean, ci) of PS(UNI)-PS(DIRECT) and MT(UNI)-MT(DIRECT)."""
    if dps[0] != dps[0]:
        return "n/a"
    if (dps[0] - dps[1] > 0) or dmt[0] < -.10:
        return "fails"
    if dps[0] + dps[1] < 0 and dmt[0] >= -.05:
        return "supported"
    return "undecided"


# ---------------------------------------------------------------- purchased stake (SPEC section 5c)
#
# A static weight analysis, not a motion simulation: it asks what share of the
# council a purchase buys, not how the buyer votes. The estate weights W and the
# set of live estates come from the same tables the rules above use.

STAKE_KEYS = ("euro_b", "euro_unit", "hours_b", "hours_unit")
# PLACEHOLDER: every number in STAKE_BASE, HALF excepted, is my assumption (SPEC section 5c), not a ruling
# or data. Results are conditional on them; run.py sweeps b and unit, not hours.
# Per-estate conversion of stake to weight. Only Patrons (euros) and Members (contribution hours) carry stake; Minds are
# counted once and Users are one-each, so they have no entry and weigh 1.
STAKE_BASE = {
    "Patrons": dict(euro_b=10.0, euro_unit=10.0, hours_b=10.0, hours_unit=10.0),
    "Members": dict(euro_b=10.0, euro_unit=10.0, hours_b=10.0, hours_unit=10.0),
}
HALF = .5            # RULED, not a placeholder: operator-influence cap: council weight share strictly below this (prior section 10)
STRICT = 1e-9        # "strictly below": a capped holder sits at c*(1-STRICT)
PARETO_ALPHA = 1.16  # exponent from prior section 4 (Pareto(1.16) ladder); the roll sizes below are PLACEHOLDER
PARETO_XM = 10.0     # PLACEHOLDER: EUR scale, mine; prior section 4's ladder starts at 10
STAKE_SEED = 20261105


def stake_params(name, overrides=()):
    """Per-estate stake parameters; Config.overrides entries (estate, key, value) with a STAKE_KEYS key apply."""
    p = dict(STAKE_BASE.get(name, {}))
    if p:
        for en, k, v in overrides:
            if en == name and k in STAKE_KEYS:
                p[k] = v
    return p


def log_weight(x, b, unit):
    """w = max(0, 1 + log_b(x/unit)): 1 at x = unit, continuous below it, 0 (not negative) once x <= unit/b.

    Equals CultLib heimdall-stake `weight` (PR #9, 84f932ec) with floor 0, cap None and no minimum: no cliff
    at unit. Pinned to its golden vectors by test_estatesim.StakeVectors (vectors/stake_golden.csv).

    This is the weight of ONE IDENTITY. It is not sybil-safe: log is sub-linear, so the same x split over
    k identities weighs more (split_weight). Whether the log bounds a purchase depends on identities being
    scarce, which nothing in this module supplies. b and unit are PLACEHOLDER values (STAKE_BASE)."""
    if b <= 1 or unit <= 0:
        raise ValueError("log base must exceed 1 and unit must be positive")
    return max(0.0, 1.0 + math.log(x / unit, b)) if x > 0 else 0.0


def stake_weight(name, euros=0.0, hours=0.0, overrides=()):
    """One identity's weight: euro channel plus contribution-hours channel, each with the estate's own (b, unit).

    Per identity, not per person: splitting evades it (see log_weight). The hours channel is UNANALYSED:
    it is added here, but no scenario in run.py gives a buyer hours, so nothing below tests whether hours
    can be farmed, bought or split. Its (b, unit) are PLACEHOLDER."""
    p = stake_params(name, overrides)
    if not p:
        return 1.0
    return (log_weight(euros, p["euro_b"], p["euro_unit"])
            + log_weight(hours, p["hours_b"], p["hours_unit"]))


def pareto_ladder(n, rng):
    """n ordinary-patron amounts in EUR, Pareto(PARETO_ALPHA) from PARETO_XM."""
    return [PARETO_XM * (1.0 - rng.random()) ** (-1.0 / PARETO_ALPHA) for _ in range(n)]


def council_share(w, rest, W, e, live):
    """(within-estate share s, council weight share x) of a holder with weight w in estate e;
    rest is everyone else's weight in e; live is the tuple of live estate indices (renormalization)."""
    s = w / (w + rest) if w + rest > 0 else 0.0
    return s, W[e] * s / sum(W[i] for i in live)


def influence_cap(w, rest, W, e, live, c=HALF):
    """Operator-influence cap (prior section 10, ruling operator-influence-below-half): a holder's
    council weight share must be strictly below c. Binds iff x >= c; the holder's internal weight is
    then cut until x = c*(1-STRICT). Unenforceable when nobody else holds weight in the estate.

    The cap is applied to ONE IDENTITY's weight w. A holder who splits across identities is never cut
    (cap evasion, results.md), so this is a bound on a ledger entry, not on a person."""
    s, x = council_share(w, rest, W, e, live)
    out = dict(s=s, x=x, binds=x >= c - EPS, w_capped=w, s_capped=s, x_capped=x, enforceable=True)
    if not out["binds"]:
        return out
    s_cap = c * (1 - STRICT) * sum(W[i] for i in live) / W[e]
    if rest <= 0 or s_cap >= 1:
        out["enforceable"] = False
        return out
    wc = s_cap * rest / (1 - s_cap)
    out.update(w_capped=wc, s_capped=s_cap, x_capped=W[e] * s_cap / sum(W[i] for i in live))
    return out


def split_weight(amount, k, b, unit, identity_cost=0.0, person_cap=None):
    """Total weight of `amount` EUR split evenly over k identities. identity_cost is spent per identity
    out of the budget; person_cap limits each identity's weight. Returns (total, per identity)."""
    each = (amount - k * identity_cost) / k
    wi = log_weight(each, b, unit) if each > 0 else 0.0
    if person_cap is not None:
        wi = min(wi, person_cap)
    return k * wi, wi


def best_split(amount, b, unit, identity_cost=0.0, person_cap=None):
    """(best total weight, k) over a geometric grid of k up to amount/unit. A grid maximum: a lower bound on the true one."""
    kmax = max(1, int(amount // unit))
    ks, k = {1, kmax}, 1.0
    while k < kmax:
        ks.add(max(1, round(k)))
        k *= 1.05
    best = max((split_weight(amount, k, b, unit, identity_cost, person_cap)[0], -k) for k in ks)
    return best[0], -best[1]


def min_neutralising_cost(amount, b, unit, person_cap=None, tol=1e-3):
    """Smallest per-identity cost at which no split beats one identity, found by bisection to relative
    tol (a bisection value, not a grid point). Neutral = best split is k = 1 and one identity still has
    weight. A doubling search from unit brackets the first neutral cost; assumes the predicate is
    monotone inside the bracket. Returns None if no cost below the amount is neutral."""
    def neutral(c):
        return (split_weight(amount, 1, b, unit, c, person_cap)[0] > 0
                and best_split(amount, b, unit, c, person_cap)[1] == 1)
    hi = unit
    while hi < amount and not neutral(hi):
        hi *= 2
    if hi >= amount:
        return None
    lo = hi / 2 if hi > unit else 0.0
    while hi - lo > tol * hi:
        mid = (lo + hi) / 2
        lo, hi = (lo, mid) if neutral(mid) else (mid, hi)
    return hi


def stake_ladder(n, s):
    """Seed s's roll of n ordinary patrons: a pure function of (n, s)."""
    return pareto_ladder(n, random.Random(STAKE_SEED + 1000 * n + s))


def buyer_runs(amount, n, seeds, W, live, overrides=()):
    """influence_cap result for a buyer of `amount` EUR against each of `seeds` rolls of n ordinary patrons."""
    wb = stake_weight("Patrons", euros=amount, overrides=overrides)
    return [influence_cap(wb, sum(stake_weight("Patrons", euros=x, overrides=overrides) for x in stake_ladder(n, s)),
                          W, ESTATES.index("Patrons"), live) for s in range(seeds)]


# ---------------------------------------------------------------- contribution credit (SPEC section 5d)
#
# Credit is tallied per person from receipts (hours or credited changes, one linear unit each) and capped per
# person. The tally and the half-influence cap are the ones above (council_share, influence_cap). Credit is
# measured here as a candidate weight source only; this module never turns it into a vote.

CREDIT_SEED = 20261106
CREDIT_ESTATE = "Members"      # PLACEHOLDER: where contribution credit would weigh; the hours channel above lives here


def credit_weight(credit, cap=None):
    """One person's weight from credit: linear in receipts, min(credit, cap). One function for every person,
    a Meta-sized contributor included; there is no per-person exemption or schedule."""
    return credit if cap is None else min(credit, cap)


def credit_ladder(n, s):
    """Seed s's n ordinary members' credit (hours): Pareto(PARETO_ALPHA) from PARETO_XM (prior section 4's
    contribution curve; the scale is PLACEHOLDER)."""
    return pareto_ladder(n, random.Random(CREDIT_SEED + 1000 * n + s))


def credit_cap_value(ladder, mult):
    """Per-person cap as a multiple of the ordinary members' median credit (None = no cap)."""
    if mult is None:
        return None
    xs = sorted(ladder)
    return mult * xs[len(xs) // 2]


def credit_run(phi, n, s, W, live, cap_mult=None, k=1):
    """Half-cap result for a contributor holding fraction `phi` of the estate's TOTAL raw credit, against n
    ordinary members (seed s), the credit split evenly over k identities. Returns dict: the usual
    influence_cap fields for ONE identity (`one`), the joint share (`joint_x`), `evaded` iff the joint
    council share is >= HALF while no identity's is, and `w_total`."""
    e = ESTATES.index(CREDIT_ESTATE)
    lad = credit_ladder(n, s)
    cap = credit_cap_value(lad, cap_mult)
    rest = sum(credit_weight(x, cap) for x in lad)
    big = phi / (1 - phi) * sum(lad)
    wi = credit_weight(big / k, cap)
    tot = k * wi
    one = influence_cap(wi, rest + tot - wi, W, e, live)
    joint = council_share(tot, rest, W, e, live)[1]
    return dict(one=one, joint_x=joint, binds=one["binds"], joint_binds=joint >= HALF - EPS,
                evaded=joint >= HALF - EPS and not one["binds"], w_total=tot, rest=rest, cap=cap)
