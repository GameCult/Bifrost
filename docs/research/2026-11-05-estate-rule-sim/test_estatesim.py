"""python3 -I test_estatesim.py"""
import math
import os
import random
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import estatesim as E  # noqa: E402

W = (25, 25, 25, 25)


def est(cap=None, name="Users"):
    cfg = E.Config(delegation=True, cap=cap)
    e = E.Estate(name, cfg, random.Random(1))
    e.target = [None] * e.n
    e.scope = [None] * e.n
    return e


def edge(e, i, t, scope=(0, 1, 2, 3, 4)):
    e.target[i], e.scope[i] = t, frozenset(scope)


class RuleTests(unittest.TestCase):
    def test_threshold_tie_passes(self):
        # one live estate, exactly q For
        self.assertTrue(E.passes("DIRECT", [.5, 0, 0, 0], [.5, 0, 0, 0], W, .5))
        self.assertFalse(E.passes("DIRECT", [.4, 0, 0, 0], [.6, 0, 0, 0], W, .5))

    def test_wta_tie_drops_estate_out(self):
        # Minds tie drops out; Members 3-1 For decides alone
        self.assertTrue(E.passes("WTA", [4, 3, 0, 0], [4, 1, 0, 0], W, .66))
        # tied estate alone casts nothing: nothing passes
        self.assertFalse(E.passes("WTA", [4, 0, 0, 0], [4, 0, 0, 0], W, .15))

    def test_uni_uses_category_threshold_inside_estate(self):
        # 40% For inside each estate: UNI at q=.3 casts all For, WTA casts all Against
        F, A = [4, 4, 4, 4], [6, 6, 6, 6]
        self.assertTrue(E.passes("UNI", F, A, W, .3))
        self.assertFalse(E.passes("WTA", F, A, W, .3))
        self.assertFalse(E.passes("UNI", F, A, W, .5))

    def test_uni_two_of_four_blocs_at_half(self):
        self.assertTrue(E.passes("UNI", [9, 9, 1, 1], [1, 1, 9, 9], W, .5))   # 50% of weight
        self.assertFalse(E.passes("UNI", [9, 1, 1, 1], [1, 9, 9, 9], W, .5))

    def test_renormalizes_over_live_estates(self):
        self.assertTrue(E.passes("UNI", [5, 0, 0, 0], [1, 0, 0, 0], W, .66))   # empty estates not counted
        self.assertFalse(E.passes("DIRECT", [0] * 4, [0] * 4, W, .15))

    def test_head_ignores_estate_weights(self):
        self.assertTrue(E.passes("HEAD", [1, 0, 9, 0], [9, 0, 0, 0], W, .5))
        self.assertFalse(E.passes("DIRECT", [1, 0, 9, 0], [9, 0, 0, 0], W, .6))


class DelegationTests(unittest.TestCase):
    def setUp(self):
        self.e = est()
        self.n = self.e.n

    def voting(self, ids):
        v = [False] * self.n
        for i in ids:
            v[i] = True
        return v

    def test_delegated_weight_goes_to_delegate(self):
        edge(self.e, 1, 0)
        w, dest = self.e.resolve(0, self.voting([0]))
        self.assertEqual(w, {0: 2.0})
        self.assertEqual(dest, {1: 0})

    def test_direct_vote_overrides_delegation(self):
        edge(self.e, 1, 0)
        w, dest = self.e.resolve(0, self.voting([0, 1]))
        self.assertEqual(w, {0: 1.0, 1: 1.0})
        self.assertEqual(dest, {})

    def test_scope_out_of_category_is_ignored(self):
        edge(self.e, 1, 0, scope=(2,))
        w, _ = self.e.resolve(0, self.voting([0]))
        self.assertEqual(w, {0: 1.0})
        w, _ = self.e.resolve(2, self.voting([0]))
        self.assertEqual(w, {0: 2.0})

    def chain_estate(self, depth, n_links):
        e = est()
        e.cfg = E.Config(delegation=True, depth=depth)
        for k in range(1, n_links + 1):                   # n_links -> ... -> 1 -> 0
            edge(e, k, k - 1)
        return e

    def test_chain_within_depth_reaches_holder(self):
        e = self.chain_estate(2, 2)                        # 2 -> 1 -> 0, two edges
        w, dest = e.resolve(0, self.voting([0]))
        self.assertEqual(w, {0: 3.0})
        self.assertEqual(dest, {1: 0, 2: 0})

    def test_over_depth_is_uncast_not_redirected(self):
        e = self.chain_estate(2, 3)                        # 3 needs three edges
        w, dest = e.resolve(0, self.voting([0]))
        self.assertNotIn(3, dest)
        self.assertEqual(w, {0: 3.0})
        self.assertEqual(e.last_uncast, 1)

    def test_unlimited_depth_follows_long_chain(self):
        e = self.chain_estate(None, 9)
        w, _ = e.resolve(0, self.voting([0]))
        self.assertEqual(w, {0: 10.0})

    def test_intermediate_direct_vote_terminates_chain(self):
        e = self.chain_estate(None, 3)                     # 3 -> 2 -> 1 -> 0 ; 1 votes itself
        w, dest = e.resolve(0, self.voting([0, 1]))
        self.assertEqual(w, {0: 1.0, 1: 3.0})
        self.assertEqual(dest, {2: 1, 3: 1})

    def test_loop_is_uncast(self):
        e = est()
        e.cfg = E.Config(delegation=True, depth=None)
        edge(e, 1, 2)
        edge(e, 2, 1)
        edge(e, 3, 1)                                      # feeds the loop: also void
        w, dest = e.resolve(0, self.voting([0]))
        self.assertEqual((w, dest), ({0: 1.0}, {}))
        self.assertEqual(e.last_uncast, 3)

    def test_scope_is_overlap_of_all_links(self):
        e = est()
        e.cfg = E.Config(delegation=True, depth=None)
        edge(e, 2, 1, scope=(0, 1))                        # 2 -> 1 -> 0
        edge(e, 1, 0, scope=(1, 2))
        for cat, expect in ((0, {}), (1, {2: 0, 1: 0}), (2, {1: 0})):
            _, dest = e.resolve(cat, self.voting([0]))
            self.assertEqual(dest, expect, cat)

    def test_dead_end_is_uncast(self):
        e = est()
        edge(e, 2, 1)                                      # 1 does not vote and delegates nowhere
        w, dest = e.resolve(0, self.voting([0]))
        self.assertEqual((w, dest), ({0: 1.0}, {}))
        self.assertEqual(e.last_uncast, 1)

    def test_cap_voids_excess_and_floors_at_one(self):
        e = est(cap=.5)                                    # cast weight 12, cap 6
        for i in range(1, 11):
            edge(e, i, 0)
        voting = [i in (0, 20) for i in range(e.n)]
        w, _ = e.resolve(0, voting)
        self.assertEqual(w, {0: 6.0, 20: 1.0})             # 11 delegated+own capped to 6, excess void
        e = est(cap=.001, name="Patrons")                  # below one voter: floor is 1
        w, _ = e.resolve(0, [i == 0 for i in range(e.n)])
        self.assertEqual(w, {0: 1.0})

    def test_cap_applies_to_final_holder_not_each_link(self):
        e = est(cap=.4)
        e.cfg = E.Config(delegation=True, depth=None, cap=.4)
        for i in range(1, 6):
            edge(e, i, 6)                                  # five delegators to 6 ...
        edge(e, 6, 0)                                      # ... who delegates to 0
        w, _ = e.resolve(0, self.voting([0, 20]))          # 0 holds 1+6, cast 8 -> cap 3.2
        self.assertEqual(w[0], 3.2)

    def test_cap_basis_roll(self):
        e = est()
        e.cfg = E.Config(delegation=True, cap=.1, cap_basis="roll")   # 300 * .1 = 30
        for i in range(1, 41):
            edge(e, i, 0)
        w, _ = e.resolve(0, self.voting([0]))
        self.assertEqual(w, {0: 30.0})

    def contradicting_estate(self):
        e = est()
        edge(e, 1, 0)
        e.turn[0], e.kap[0], e.a[0], e.b[0], e.sig[0] = 1.0, 0.0, 1.0, 0.0, .01
        e.turn[1], e.kap[1], e.a[1], e.b[1], e.sig[1] = 0.0, 0.0, -3.0, 0.0, .01
        return e

    def test_contradiction_forces_direct_vote_then_revokes_at_second_strike(self):
        e = self.contradicting_estate()
        dt = e.motion(0, 1.0, 0.0, random.Random(1), 0.0)
        self.assertIn(1, [t for t, _, _ in dt["holders"]])          # overrides this motion
        self.assertEqual(e.target[1], 0)                            # one strike: still delegated
        self.assertEqual(e.strikes[1], 1)
        e.motion(0, 1.0, 0.0, random.Random(2), 0.0)
        self.assertIsNone(e.target[1])                              # second strike revokes
        e.motion(0, 1.0, 0.0, random.Random(3), 0.0)
        self.assertEqual(e.strikes[1], 2)                           # revoked: no further strikes while unrenewed
        self.assertIsNone(e.target[1])

    def test_renewal_restores_an_edge_with_fresh_strikes(self):
        e = self.contradicting_estate()
        e.cfg = E.Config(delegation=True, renew=1.0, peer=0.0)
        e.pool, e.delegators = ([0], [1.0]), [1]
        e.target[1], e.revoked, e.strikes[1] = None, [1], 2
        e.motion(0, 0.0, 0.0, random.Random(1), 0.0)
        self.assertEqual((e.target[1], e.strikes[1], e.revoked), (0, 0, []))

    def test_direct_voters_never_forwarded(self):
        edge(self.e, 1, 0)
        w, _ = self.e.resolve(0, self.voting([0, 1]))
        self.assertNotIn(2, w)
        self.assertEqual(sum(w.values()), 2.0)


class CopyTests(unittest.TestCase):
    """SPEC section 5b: copy-the-ballot."""

    def setup(self, copy, cap=.2, followers=10):
        e = est(cap)
        e.cfg = E.Config(delegation=True, cap=cap, copy=copy)
        for k in range(1, followers + 1):
            edge(e, k, 0)
        voting = [False] * e.n
        voting[0] = True
        sb = [0] * e.n
        sb[0] = 1
        w, dest = e.resolve(0, voting)
        return e, voting, sb, w, dest

    def test_off_by_default_draws_nothing(self):
        e, v, sb, w, dest = self.setup(0.0)
        state = e.rng.getstate()
        self.assertIsNone(e.copy_step(0, v, sb, dest))
        self.assertEqual(e.rng.getstate(), state)
        self.assertEqual(w[0], 2.2)                        # cap .2 of 11 cast weight; excess void

    def test_full_copying_evades_the_cap(self):
        e, v, sb, w, dest = self.setup(1.0)
        copiers, w2, _ = e.copy_step(0, v, sb, dest)
        self.assertEqual(len(copiers), 10)
        self.assertEqual(w2[0], 1.0)                        # the holder keeps only its own vote
        self.assertTrue(all(w2[c] == 1.0 and sb[c] == 1 and v[c] for c in copiers))
        self.assertEqual(sum(w2.values()), 11.0)            # nothing voided
        self.assertEqual(e.effective(w2, copiers), {0: 11.0})

    def test_copiers_cast_the_holders_ballot_not_their_own(self):
        e, v, sb, w, dest = self.setup(1.0)
        sb[0] = -1
        copiers, w2, _ = e.copy_step(0, v, sb, dest)
        self.assertTrue(all(sb[c] == -1 for c in copiers))

    def test_no_copying_under_a_holder_within_the_cap(self):
        e, v, sb, w, dest = self.setup(1.0, cap=1.0, followers=3)   # cap 4.0 of 4 cast, raw 4.0: not over
        self.assertIsNone(e.copy_step(0, v, sb, dest))

    def test_no_copying_without_a_cap(self):
        e, v, sb, w, dest = self.setup(1.0, cap=None)
        self.assertIsNone(e.copy_step(0, v, sb, dest))

    def test_partial_copy_rate_is_between(self):
        e, v, sb, w, dest = self.setup(.5, followers=40)
        copiers, w2, _ = e.copy_step(0, v, sb, dest)
        self.assertTrue(8 <= len(copiers) <= 32)
        self.assertEqual(e.effective(w2, copiers)[0], sum(w2.values()) - sum(
            x for t, x in w2.items() if t != 0 and t not in copiers))

    def test_followers_of_a_copier_are_counted_through_it(self):
        e, v, sb, w, dest = self.setup(1.0, followers=10)
        edge(e, 20, 5)                                      # 20 delegates to 5, who delegates to 0
        w, dest = e.resolve(0, v)
        copiers, w2, dest2 = e.copy_step(0, v, sb, dest)
        self.assertEqual(sum(w2.values()), 12.0)            # all twelve ballots are cast, none void
        self.assertEqual(e.effective(w2, copiers), {0: 12.0})

    def test_copying_run_is_deterministic_and_leaves_paired_draws(self):
        cfg = E.Config(delegation=True, alpha=4.0, cap=.1, copy=.5, rho=.25, faction=10, motions=30)
        self.assertEqual(E.run_replicate(cfg, 2), E.run_replicate(cfg, 2))
        a, _ = E.run_replicate(E.Config(motions=30), 2)
        b, _ = E.run_replicate(cfg, 2)
        self.assertEqual([r[:3] for r in a], [r[:3] for r in b])

    def test_copy_off_matches_the_no_copy_config_byte_for_byte(self):
        base = dict(delegation=True, alpha=2.5, cap=.15, rho=.25, faction=20, motions=40)
        self.assertEqual(E.run_replicate(E.Config(**base), 4), E.run_replicate(E.Config(copy=0.0, **base), 4))

    def test_effective_concentration_exceeds_capped_formal_weight(self):
        cfg = E.Config(delegation=True, alpha=4.0, cap=.1, copy=1.0, motions=60)
        _, conc = E.run_replicate(cfg, 0)
        self.assertGreater(sum(c[7] for c in conc), sum(c[0] for c in conc))
        self.assertGreater(sum(c[9] for c in conc), 0)
        _, conc = E.run_replicate(E.Config(delegation=True, alpha=4.0, cap=.1, motions=60), 0)
        self.assertEqual(sum(c[9] for c in conc), 0)
        self.assertAlmostEqual(sum(c[7] for c in conc), sum(c[0] for c in conc))


class StakeTests(unittest.TestCase):
    """SPEC section 5c: purchased stake, splitting, the half-influence cap."""
    P = E.ESTATES.index("Patrons")
    ALL = (0, 1, 2, 3)

    def cfg(self):
        return E.STAKE_BASE["Patrons"]

    def test_weight_is_one_at_unit_zero_below_and_logarithmic(self):
        c = self.cfg()
        b, u = c["euro_b"], c["euro_unit"]
        self.assertEqual(E.log_weight(u, b, u), 1.0)
        self.assertEqual(E.log_weight(u * .99, b, u), 0.0)                 # below unit: no weight, not negative
        self.assertAlmostEqual(E.log_weight(u * b ** 3, b, u), 4.0)        # each factor b adds one
        self.assertLess(E.log_weight(10 * u, b, u), 10)                     # sub-linear

    def test_conversion_is_per_estate_and_overridable(self):
        ov = (("Patrons", "euro_b", 2.0), ("Patrons", "euro_unit", 5.0), ("Members", "euro_b", 100.0))
        self.assertAlmostEqual(E.stake_weight("Patrons", euros=40, overrides=ov), 1 + math.log(8, 2))
        self.assertAlmostEqual(E.stake_weight("Patrons", euros=100), 1 + math.log(10, 10))
        self.assertEqual(E.stake_params("Members", ov)["euro_b"], 100.0)
        self.assertEqual(E.stake_params("Patrons", ov)["euro_b"], 2.0)
        self.assertEqual(E.stake_weight("Users", euros=1e6), 1.0)           # no stake channel: one-each
        self.assertEqual(E.stake_weight("Minds", hours=1e6), 1.0)

    def test_hours_add_to_euros_with_their_own_conversion(self):
        ov = (("Members", "hours_b", 2.0), ("Members", "hours_unit", 4.0))
        h = E.stake_weight("Members", hours=32, overrides=ov)
        self.assertAlmostEqual(h, 1 + math.log(8, 2))
        self.assertAlmostEqual(E.stake_weight("Members", euros=100, hours=32, overrides=ov), h + 2.0)

    def test_million_euro_buyer_share_is_derived_from_config(self):
        c = self.cfg()
        wb = E.stake_weight("Patrons", euros=1e6)
        n = E.ROLL["Patrons"]                       # the harness's patron roll, each at exactly the unit
        others = n * E.log_weight(c["euro_unit"], c["euro_b"], c["euro_unit"])
        s_star = wb / (wb + others)
        for wn, W in E.WEIGHTS.items():
            # all estates live: the buyer's council share cannot exceed the Patrons estate weight, below half by the charter
            r = E.influence_cap(wb, others, W, self.P, self.ALL)
            self.assertAlmostEqual(r["s"], s_star)
            self.assertAlmostEqual(r["x"], W[self.P] / sum(W) * s_star)
            self.assertLess(r["x"], W[self.P] / sum(W) + 1e-12)
            self.assertFalse(r["binds"], wn)
            # Patrons the only live estate: the cap binds exactly when the buyer outweighs the rest
            r = E.influence_cap(wb, others, W, self.P, (self.P,))
            self.assertEqual(r["binds"], s_star >= E.HALF)
            self.assertEqual(r["binds"], wb >= others)
            if r["binds"]:
                self.assertLess(r["x_capped"], E.HALF)
                self.assertAlmostEqual(r["x_capped"], E.HALF, places=6)

    def test_log_weight_gives_a_smaller_share_than_linear_but_still_a_near_total_one_in_a_tiny_estate(self):
        c = self.cfg()
        rest_amounts = [10.0, 50.0, 200.0, 5000.0]
        W = E.WEIGHTS["equal"]
        lg = E.council_share(E.stake_weight("Patrons", euros=1e6),
                             sum(E.stake_weight("Patrons", euros=a) for a in rest_amounts), W, self.P, (self.P,))[1]
        li = E.council_share(1e6 / c["euro_unit"], sum(a / c["euro_unit"] for a in rest_amounts), W, self.P, (self.P,))[1]
        self.assertLess(lg, li)
        self.assertGreater(li, .99)

    def test_cap_is_unenforceable_when_nobody_else_holds_weight(self):
        r = E.influence_cap(5.0, 0.0, E.WEIGHTS["equal"], self.P, (self.P,))
        self.assertTrue(r["binds"])
        self.assertFalse(r["enforceable"])

    def test_splitting_multiplier_matches_the_formula_and_exceeds_one(self):
        c = self.cfg()
        b, u, a, k = c["euro_b"], c["euro_unit"], 1e6, 100
        tot, each = E.split_weight(a, k, b, u)
        self.assertAlmostEqual(each, 1 + math.log(a / (k * u), b))
        self.assertAlmostEqual(tot, k * (1 + math.log(a / (k * u), b)))
        self.assertGreater(tot, E.log_weight(a, b, u))
        self.assertEqual(E.split_weight(a, int(a / u) + 1, b, u)[0], 0.0)   # every fake below unit: nothing
        best, bk = E.best_split(a, b, u)
        self.assertGreaterEqual(best, tot)
        self.assertAlmostEqual(best, a / u)                                  # b > e: best is k = a/unit, weight 1 each

    def test_per_person_cap_and_unit_floor_below_amount_do_not_neutralise_splitting(self):
        c = self.cfg()
        b, u, a = c["euro_b"], c["euro_unit"], 1e6
        single = E.log_weight(a, b, u)
        for wc in (1.0, 3.0, single):
            best, k = E.best_split(a, b, u, person_cap=wc)
            self.assertGreater(best, min(single, wc))
        self.assertGreater(E.best_split(a, b, 1000.0)[1], 1)                 # raised unit still splits
        self.assertEqual(E.best_split(a, b, a)[1], 1)                        # unit = amount leaves nothing to split

    def test_identity_cost_neutralises_only_at_a_large_share_of_the_budget(self):
        c = self.cfg()
        b, u, a = c["euro_b"], c["euro_unit"], 1e6
        cost = E.min_neutralising_cost(a, b, u)
        self.assertIsNotNone(cost)
        self.assertEqual(E.best_split(a, b, u, identity_cost=cost)[1], 1)
        self.assertGreater(E.best_split(a, b, u, identity_cost=cost / 2)[1], 1)
        self.assertGreater(cost, a / 4)
        self.assertAlmostEqual(cost / a, .5, delta=.01)                      # bisection value: just under half the budget
        self.assertLess(cost, a / 2 + 1e-9)

    def test_headline_numbers_are_pinned(self):
        """The figures REPORT.md quotes, under the placeholder defaults and the fixed seeds (40, STAKE_SEED)."""
        W = E.WEIGHTS["equal"]
        r = E.buyer_runs(1e6, E.ROLL["Patrons"], 40, W, (self.P,))
        m, hw = E.mean_ci([x["s"] for x in r])
        self.assertEqual(round(m, 3), .524)
        self.assertEqual(round(hw, 3), .008)
        self.assertEqual(sum(x["s"] > .5 for x in r) / 40, .85)
        c = self.cfg()
        w1 = E.log_weight(1e6, c["euro_b"], c["euro_unit"])
        for k, want in ((2, 1.9), (10, 8.3), (100, 66.7)):
            self.assertEqual(round(E.split_weight(1e6, k, c["euro_b"], c["euro_unit"])[0] / w1, 1), want)
        for n in (40, 400):                                                  # the headline is a small-roll fact
            self.assertLess(max(x["s"] for x in E.buyer_runs(1e6, n, 40, W, (self.P,))), .5)

    def test_per_identity_cap_is_evaded_by_splitting(self):
        c = self.cfg()
        W = E.WEIGHTS["equal"]
        rest = E.ROLL["Patrons"] * 1.0
        single = E.stake_weight("Patrons", euros=1e6)
        self.assertTrue(E.influence_cap(single, rest, W, self.P, (self.P,))["binds"])
        k = 10
        tot, wi = E.split_weight(1e6, k, c["euro_b"], c["euro_unit"])
        self.assertFalse(E.influence_cap(wi, rest + tot - wi, W, self.P, (self.P,))["binds"])
        self.assertGreaterEqual(E.council_share(tot, rest, W, self.P, (self.P,))[1], E.HALF)

    def test_pareto_ladder_is_deterministic_and_above_floor(self):
        a, b = E.pareto_ladder(50, random.Random(7)), E.pareto_ladder(50, random.Random(7))
        self.assertEqual(a, b)
        self.assertTrue(all(x >= E.PARETO_XM for x in a))


class StrategyTests(unittest.TestCase):
    def test_deviation_never_makes_agent_worse_off(self):
        rng = random.Random(3)
        for _ in range(300):
            q = rng.choice(E.CATS)
            F = [float(rng.randrange(0, 6)) for _ in range(4)]
            A = [float(rng.randrange(0, 6)) for _ in range(4)]
            rule = rng.choice(E.RULES)
            e = rng.randrange(4)
            for b in (1, -1):
                F0, A0 = list(F), list(A)
                E._move(F0, A0, e, 0, b, 1.0)
                pref = b == 1
                ag = dict(e=e, w=1.0, sincere=b, cur=b, pref=pref, opts=[b] + [x for x in (1, -1, 0) if x != b])
                sincere_ok = E.passes(rule, F0, A0, W, q) == pref
                E.strategize(rule, W, q, F0, A0, [ag], 7)
                if sincere_ok:
                    self.assertTrue(E.passes(rule, F0, A0, W, q) == pref)

    def test_strategic_joiner_joins_only_when_pivotal(self):
        F, A = [3.0, 0, 0, 0], [3.0, 0, 0, 0]               # tie, q=.5 passes under UNI already
        ag = dict(e=0, w=1.0, sincere=0, cur=0, pref=True, opts=[0, 1])
        E.strategize("UNI", W, .5, F, A, [ag], 1)
        self.assertEqual(ag["cur"], 0)                      # already passes: stays home
        F, A = [2.0, 0, 0, 0], [3.0, 0, 0, 0]               # 40% For: fails at .5, joining makes 3/6
        ag = dict(e=0, w=1.0, sincere=0, cur=0, pref=True, opts=[0, 1])
        E.strategize("UNI", W, .5, F, A, [ag], 1)
        self.assertEqual(ag["cur"], 1)

    def test_faction_votes_agenda_regardless_of_preference(self):
        cfg = E.Config(faction=20)
        e = E.Estate("Users", cfg, random.Random(2))
        self.assertEqual(sum(e.faction), 20)
        dt = e.motion(0, -2.0, 2.0, random.Random(9), 0.0)  # d - m > 0: faction votes For
        ballots = {t: b for t, b, _ in dt["holders"]}
        for i in range(e.n):
            if e.faction[i]:
                self.assertEqual(ballots[i], 1)


class HarnessTests(unittest.TestCase):
    CFG = E.Config(delegation=True, alpha=1.5, cap=.2, rho=.25, faction=10, motions=30)

    def test_deterministic(self):
        self.assertEqual(E.run_replicate(self.CFG, 3), E.run_replicate(self.CFG, 3))

    def test_draws_paired_across_rules_and_configs(self):
        a, _ = E.run_replicate(E.Config(motions=30), 1)
        b, _ = E.run_replicate(self.CFG, 1)
        self.assertEqual([r[:3] for r in a], [r[:3] for r in b])    # same (category, merit, appeal)

    def test_sincere_run_has_no_strategy_flips(self):
        r, _ = E.run_replicate(E.Config(motions=60), 0)
        self.assertTrue(all(x[3] == x[4] for x in r))

    def test_metrics_on_known_outcomes(self):
        # outcome == (d > 0): PS = 1, MT ~ 0 (independent), BPP = 1, GBR = 1 - 0 = 1
        rng = random.Random(1)
        recs = []
        for _ in range(4000):
            m, d = rng.gauss(0, 1), rng.gauss(0, 1)
            recs.append((0, m, d, (d > 0,) * 4, (d > 0,) * 4))
        mm = E.rep_metrics(recs, 0)
        self.assertEqual(mm["PS"], 1.0)
        self.assertLess(abs(mm["MT"]), .1)
        self.assertEqual(mm["BPP"], 1.0)
        self.assertEqual(mm["GBR"], 1.0)
        # outcome == (m > 0): PS ~ 0, MT = 1, BPP = 0, GBR = 0
        recs = [(0, m, d, (m > 0,) * 4, (m > 0,) * 4) for (_, m, d, _, _) in recs]
        mm = E.rep_metrics(recs, 0)
        self.assertEqual(mm["MT"], 1.0)
        self.assertEqual(mm["BPP"], 0.0)
        self.assertEqual(mm["GBR"], 0.0)
        self.assertLess(abs(mm["PS"]), .1)

    def test_concentration(self):
        top, gini, neff, tot = E.concentration([1, 1, 1, 1])
        self.assertEqual((top, round(gini, 9), neff, tot), (.25, 0.0, 4.0, 4))
        top, gini, neff, _ = E.concentration([1, 1, 1, 97])
        self.assertGreater(gini, .7)
        self.assertLess(neff, 1.1)

    def test_verdict_rules(self):
        self.assertEqual(E.verdict((-.1, .02), (0.0, .02)), "supported")
        self.assertEqual(E.verdict((.1, .02), (0.0, .02)), "fails")
        self.assertEqual(E.verdict((-.1, .02), (-.12, .02)), "fails")     # dampened by not working
        self.assertEqual(E.verdict((-.01, .05), (0.0, .02)), "undecided")



class StakeVectors(unittest.TestCase):
    """Pins the arm's log_weight / split_weight to CultLib heimdall-stake's golden vectors (PR #9, head 84f932ec,
    copied unmodified to vectors/stake_golden.csv). The arm is Libby's function with min_amount_per_identity = unit,
    so rows at or above the unit must match; a row below the unit must be 0 in the arm (the cliff) while the
    vectors, which set no minimum, give the continuous value. Vectors are integer cents and micro-weights, the
    arm is float and unit-agnostic: tolerance is 1 micro-weight per identity (Libby rounds each identity, the arm the total)."""

    @staticmethod
    def rows():
        path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "vectors", "stake_golden.csv")
        with open(path) as fh:
            lines = [l.strip() for l in fh if l.strip() and not l.startswith("#")]
        out = [l.split(",") for l in lines]
        assert len(out) == 26
        return [(k, int(b), int(u), int(a), int(n), int(e)) for k, b, u, a, n, e in out]

    def test_every_vector_row(self):
        for kind, b, u, a, k, expected in self.rows():
            with self.subTest(kind=kind, b=b, unit=u, amount=a, k=k):
                if kind == "single":
                    got = E.log_weight(a, b, u) * 1e6
                    if a < u:
                        self.assertEqual(got, 0.0)      # arm's cliff = min_amount_per_identity = unit
                        continue
                else:
                    got = E.split_weight(a, k, b, u)[0] * 1e6
                    self.assertGreaterEqual(a // k, u)  # every split row keeps each identity at or above unit
                self.assertLessEqual(abs(got - expected), max(1, k))


if __name__ == "__main__":
    unittest.main()
