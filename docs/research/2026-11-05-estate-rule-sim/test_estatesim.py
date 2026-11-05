"""python3 -I test_estatesim.py"""
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
        self.assertEqual(e.strikes[1], 2)                           # revoked: no further strikes, never returns
        self.assertIsNone(e.target[1])

    def test_direct_voters_never_forwarded(self):
        edge(self.e, 1, 0)
        w, _ = self.e.resolve(0, self.voting([0, 1]))
        self.assertNotIn(2, w)
        self.assertEqual(sum(w.values()), 2.0)


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


if __name__ == "__main__":
    unittest.main()
