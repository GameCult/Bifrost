# Estate-rule sim: report (2026-11-05)

Question: does the unified estate rule (estate votes decide the estate's vote) dampen populism? Spec: `SPEC.md` (written first, three revisions, all before the results they affect). Numbers: `results.md` (127 cells, 40 replicates x 500 motions each, paired draws). Rerun: `python3 -I run.py` (about 3 minutes on 16 cores; `--quick` for a smoke; `python3 -I test_estatesim.py` for the 31 tests). Output is byte-identical across reruns and across `--jobs` values.

## Answer

**Partly, weakly, and at a merit cost. The claim as stated ("dampens populism") is not established, and it fails in the cells that matter most.**

1. UNI lowers populism sensitivity (PS) against a direct tally in every sincere and strategic cell: by 0.02 to 0.07 on a DIRECT baseline of 0.23 to 0.35 (E1 equal weights: 0.297 to 0.275). Small, but outside the intervals.
2. It lowers merit tracking (MT) by more: 0.05 to 0.10 (E1: 0.465 to 0.400; with strategic agents and a 20-person Users faction: 0.381 to 0.304). Dampening populism by losing the signal is not what the claim means. PI = PS/MT, appeal sensitivity per unit of merit, is **worse** under UNI at equal weights (E1: 0.689 vs 0.639; E2 faction 20: 0.900 vs 0.849; E5 Users appeal .4: 0.840 vs 0.691) and flat under minds-heavy weights. The pre-registered verdict (SPEC section 8) therefore reads "supported" in 0 of 127 cells in the final run, "fails" in 2 (Users appeal loading b = .4, with and without delegation: MT drops more than .10), "undecided" in 125. Undecided here mostly means PS fell and MT fell too.
3. UNI passes more motions (0.65 vs 0.60 at equal weights) and passes more *bad popular* motions: BPP (pass rate when merit < -.5 and appeal > .5) is 0.62 vs 0.525 for DIRECT. It blocks fewer good unpopular ones (GBR 0.19 vs 0.23). The cause is the low-threshold categories: at q = .15 an estate casts all its weight For on 15% support, and PS at q = .15 collapses to 0.06 only because nearly everything passes. Per-category tables are in results.md. At q >= .4 UNI is within about .01 of DIRECT. The dampening is concentrated where the threshold was already doing the work.
4. **Winner-take-all does the opposite of dampening:** PS is above DIRECT by 0.03 to 0.06 in most cells (E1 equal: 0.323 vs 0.297; strategic with faction: 0.360 vs 0.323). Collapsing an estate to one bit at 0.5 amplifies a populist estate's swing; only the UNI threshold change (q instead of .5 inside the estate) makes the difference between the two bloc rules. If "unified estate rule" is read as WTA, the claim is **false** in this model.
5. A headcount tally (HEAD, reference only) is far worse (PS 0.43 to 0.52, MT collapses to 0.05 once the Users faction mobilizes). Estate structure of any kind does dampen against a mass-mobilized Users roll; the unified rule adds a little on top.

## Strategic agents

With 25% strategic voters (misreporting, abstaining, pivot-seeking turnout) strategy flips about 2.8% of outcomes under DIRECT, WTA and UNI alike and 1% under HEAD, and does not change the ranking of rules. A coordinated Users faction raises PS under DIRECT and WTA (0.291 at no faction to 0.328 at 40) while UNI stays flat at 0.27, so **the UNI-DIRECT gap widens with organized mobilization** (-0.024 to -0.054). This is the strongest point for the claim: UNI's resistance is to turnout-limited organized blocs, not to diffuse appeal. The MT gap also shrinks with faction size (-0.072 to -0.052), because the faction drags DIRECT's merit tracking down toward UNI's. Sweep E2.

## Delegation (E3, E3b, E3c, E3d, E3e, E4, E5b)

- **Delegation barely moves the claim.** dPS UNI-DIRECT is -0.027 with delegation vs -0.050 without in the matched strategic/faction cell; PS under each rule changes by under .01 (DIRECT .313 vs .323). Delegation makes the claim slightly weaker (the gap shrinks), not stronger.
- **Concentration is real and the cap controls it, but outcomes do not care.** Zipf exponent alpha from 0 to 4 moves the Users top-holder share from 0.045 to 0.19 (no cap) and n_eff from 61 to 26; a cap of .10 pulls that to 0.107, n_eff 44. A cap of .2 does not bind at alpha 1.5 (top share 0.103 with or without it) and binds only near alpha 4 (0.173 vs 0.190). The outcome measures (PS, MT) move by under .005 across the whole alpha x cap grid, because the Users estate's single vote is decided by its resolved majority and delegates are mostly drawn from the same population. Appeal attraction beta (E4) has the same null result. Cap basis (cast weight vs roll) matters only at small c (E3d): a roll-based cap never binds at 10% turnout.
- **Transitive depth 2, 3, 4, 6 and unlimited are indistinguishable** (E3c; PS and MT differ by under .005 across depths, including with 80% peer edges and delegates turning out at 30%). The votes that die do so on scope overlap, dead ends and absent peers (uncast share 0.50 to 0.85), not on length: a long chain is a chain whose scope intersection was already empty. Depth is a safe parameter to set at the placeholder 3 in this model; it does not need to be tuned. Loops are returned uncast and appear as part of the uncast share.
- **A delegation network under the posted revocation rule decays.** With two strikes and no renewal, 13% of delegations are alive after 500 motions (E3e), so the rule as posted cannot sustain liquid delegation unless revoked delegators re-delegate. The steady state depends on the renewal rate: 53% live at renew .02, 81% at .1. This is a design finding independent of the populism claim and was the cause of the first run's inert delegation cells.
- Delegation mostly leaves votes uncast (0.5 uncast with peer edges at the default). Whether delegators who lose a chain should be told is a product question the sim cannot answer.

## Robustness (E5, E5b)

The sign of the PS effect holds across Users appeal 0.4/0.9/1.4, Users turnout .05/.10/.30 and Minds shared taste .3/.9/1.5. Its size scales with Users appeal loading (-0.025 at .4, -0.067 at 1.4): the rule helps in proportion to how appeal-driven the Users estate is, while its merit loss does not go away (MT gap -0.101 at .4 vs -0.063 at 1.4). If Users are not appeal-driven the rule is nearly all cost.

## Limits and honesty

- Every behavioral parameter is an assumption (SPEC section 4), not data. The populism measure rests on an independent merit/appeal draw that the real system cannot observe; PS is an error rate against a modeled channel, not a measurement of any real vote.
- Myopic, single-motion strategy; no repeated game, vote trading, or reputation. Delegators are not strategic. Prior work (../2026-10-29-estate-tally-game-theory/) covers some of those on unitary blocs.
- No Patron/Member inequality inside estates; no real Bifrost data.
- The verdict rule counts MT loss over .05 as "not supported" and over .10 as "fails". These are my pre-registered thresholds; a different pair would change the verdict column but not the PS/MT numbers beside it.
- Sources: SPEC section 10 ([V] fetched, [M] from memory). The delegate-cap "a fifth" has no strong precedent found; DAO caps are rare and the Jupiter 5% proposal is the nearest.

## Deviations from the spec text

- Spec rev 3 added renewal (`renew`) and peer edges (`peer`) after the first full run showed (a) revocation had silently emptied every delegation cell at 500 motions and (b) with only delegates delegating onward, depth was inert. The first run's results were discarded. Renewal and peer defaults are my choices, swept in E3c and E3e.
- Cap basis default is the estate's cast weight, not roll; both are swept.
- Per-category PS tables are printed only for the non-grid experiments (E1, E2, E4, E5).
