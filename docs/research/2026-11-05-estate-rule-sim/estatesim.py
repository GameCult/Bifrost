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
        if self.cfg.cap is not None:
            base = self.n if self.cfg.cap_basis == "roll" else sum(w.values())
            cv = max(1.0, self.cfg.cap * base)
            for t in w:
                if w[t] > cv:
                    w[t] = cv                          # excess is void, not redistributed
        self.last_uncast = uncast
        return w, dest

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
        holders = [(t, sb[t], w[t]) for t in w]
        joiners = [(i, sb[i]) for i in range(n)
                   if not voting[i] and i not in dest and sb[i] != 0]
        return dict(u=u, holders=holders, joiners=joiners, weights=sorted(w.values()),
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
        conc.append(concentration(data[2]["weights"]) + (data[2]["uncast"], data[2]["delegated"], data[2]["live"]))
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
                    if est.strategic[t] and not est.faction[t]:
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
