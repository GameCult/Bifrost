# Estate-rule sim harness: spec (2026-11-05)

Serves follow-up `heimdall-body:follow_up:chat-mind-20261105T002133411Z-t0`. Prior work: `../2026-10-29-estate-tally-game-theory/` (README.md plus numpy probes in `sims/`). Facts there are cited as "prior §n". This document is written before the code; `estatesim.py` implements it, and where the code had to deviate the deviation is listed at the bottom of `REPORT.md`, not edited in here. Revision 2 (delegation rules) was made before any result was generated. Revision 4 (copy-the-ballot, section 5b) was added after the first reported results and before its own. Revision 5 (purchased stake, section 5c) was written before its results and touches no earlier section. Revision 3 (renewal, peer edges) was forced by the first full run, whose delegation cells were inert; the first run's results were discarded, not kept.

## 1. Claim under test

> The unified estate rule (an estate's own vote decides the estate's vote) dampens populism relative to a direct tally.

The claim is stated by the operator and has not been tested. The sim is allowed to refute it. Falsifiers are fixed in advance (section 8).

## 2. Rules compared

All rules aggregate the same ballots. After the estate's ballots are resolved (section 5) each live estate e has resolved weighted counts For F_e and Against A_e (abstain excluded). Live = F_e + A_e > 0. Estate weights W_e renormalize over live estates (prior, pass rule and renormalization ruling). Motion threshold q comes from the motion category (prior §5b: Bugs .15, Cosmetics .30, Balance .40, Features/NewContent .50, FundamentalDesign .66). The motion passes iff council For weight / (For + Against weight) >= q; ties at q pass (prior).

| id | name | estate e casts |
|---|---|---|
| DIRECT | direct tally | W_e × F_e/(F_e+A_e) For, remainder Against (weighted popular share; prior "prop") |
| HEAD | headcount tally | reference only: pooled ΣF/Σ(F+A), estate weights ignored |
| WTA | winner-take-all | all of W_e For if F_e/(F_e+A_e) > .5, all Against if < .5, nothing on an exact tie (prior WTA) |
| UNI | unified estate rule | all of W_e For iff F_e/(F_e+A_e) >= q, else all Against (prior §9 `bloc-parliament-at-threshold`) |

Ambiguity recorded: "unified estate rule" and "winner-take-all" both describe an estate casting a single vote. They differ only in the internal threshold (.5 vs the category threshold q), and prior §9 shows that is the only difference. UNI is the reading in which the estate's decision applies the same threshold as the council. Every result is reported for both, so the claim can be read either way.

## 3. Populism measure

Definition used (after the ideational definition, Mudde 2004: politics as direct expression of the people's will against the judgment of those who know, applied here to outcomes, not rhetoric): **populism is the degree to which the outcome follows mass appeal that is independent of merit.**

Each motion draws merit m ~ N(0,1) and demagogic appeal d ~ N(0,1), independent. Merit is what informed voters track; appeal is the part of a motion's attractiveness that carries no information (framing, grievance, flattery). Because d is independent of m, outcome dependence on d is by construction an error.

- **PS, populism sensitivity (headline):** P(pass | d > 0) − P(pass | d < 0).
- **MT, merit tracking (the cost side):** P(pass | m > 0) − P(pass | m < 0). A rule can reduce PS by ignoring everything; MT shows if it did.
- **PI = PS / MT**, appeal sensitivity per unit of merit sensitivity. Reported with MT; ignored when MT < .05.
- **BPP, bad-popular pass rate:** P(pass | m < −.5, d > .5). **GBR, good-unpopular block rate:** P(fail | m > .5, d < −.5).
- Every number is reported pooled over categories and per q, because q moves base rates and compresses PS at the ends (q = .15 passes almost everything).

Measure limits: d and m are drawn, not observed; a real populism measure would need an independent merit label (Bifrost has none). The measure says what the rule does to a modeled appeal channel, no more.

Concentration measures (delegation runs): top-delegate share of the Users estate's resolved weight, Gini of resolved weights over ballot holders, and Kish n_eff = (Σw)²/Σw² per estate. Prior §4 uses Kish n_eff for the same purpose.

## 4. Population and ballots

Sizes follow prior: Minds 8, Members 15, Users roll 300, Patrons 4. Estate weights: `equal` 25/25/25/25 and `minds-heavy` 40/30/20/10 (prior seeds). Member one-each within estate; within-bloc donation or contribution curves (prior §4) are out of scope, so Patron and Member power inequality is not tested.

Voter i of estate e on one motion: u_i = a_e·m + b_e·d + s_e·z_e + σ_e·ε_i, with z_e ~ N(0,1) the estate's shared taste, ε_i ~ N(0,1).

| estate | a (merit) | b (appeal) | s (shared) | σ (noise) | base turnout |
|---|---|---|---|---|---|
| Minds | 1.0 | .15 | .9 | .7 | 1.0 |
| Members | .8 | .4 | .5 | 1.0 | .8 |
| Users | .4 | .9 | .5 | 1.2 | .10 |
| Patrons | .7 | .5 | .5 | 1.0 | .8 |

These are assumptions, not data. They encode: Minds are accurate and correlated (ICC of the noise part ≈ .62, between the prior's .44 and .8; prior §3 Kish n_eff calibration from Kohli 2026); Users are mass-appeal-driven, noisy, and mostly absent. The result depends on them, so section 7 sweeps the Users appeal loading b.

Sincere ballot: For if u > .1, Against if u < −.1, otherwise abstain.

**Turnout response (intensity):** voter turns out with p_i = clamp(base_e × (1 + κ(|u_i| − ū_e)), 0, 1), κ = 1, ū_e = .8·sqrt(a²+b²+s²+σ²) the mean |u|. Aggrieved or enthused voters show up. This is the standard mobilization asymmetry behind low-turnout populist surges and is the channel through which d reaches the Users estate even at 10% turnout.

## 5. Delegation (operator's posted rules, revision 2)

Revision 2 (same day, brief `heimdall-body:follow_up:chat-s1938-1941`) replaces depth-2 with transitive chains and sweeps the depth limit. Rules as posted, each with its realization:

1. **Within-estate:** an edge joins two members of the same estate. Minds do not delegate (eight accountable Personas delegating to each other would only reduce n_eff). Delegator shares of the roll: Members .2, Users .5, Patrons .2.
2. **Scoped:** each edge covers a set of motion categories, each of the five included with probability .6 (never empty).
3. **Revocable:** a delegator whose final holder's ballot opposes the delegator's strong sincere preference (|u| > .5) takes a strike; at `STRIKES` = 2 the edge is revoked. A revoked delegator re-delegates with probability `renew` per motion (default .1; swept {0, .02, .1, .5}) to a freshly drawn target with a fresh scope and zero strikes. Without renewal (renew = 0) revocation is final and the network decays to nothing over a long horizon (found in the first full run at 500 motions: revocation alone had emptied the network, so every delegation cell had silently become a no-delegation cell). The `live edges` column reports the share of delegators still holding an edge. The strike count is the only delegation memory.
4. **Direct overrides:** a member who votes directly casts weight 1 and is never forwarded. A delegator with a strike-worthy contradiction turns out regardless of the turnout draw (the delegation-driven turnout response); the motion is then re-resolved, so chains that ran through that member now end at it.
5. **Transitive chains, depth D:** weight follows edges from the delegator until it reaches a member who votes directly (the final holder). An intermediate member who votes directly ends the chain there. D is the maximum number of edges; swept over {2, 3 (placeholder, default), 4, 6, unlimited}. **Scope is the overlap of all links:** the chain is void when any link's scope excludes the motion's category. **A loop, a chain longer than D, a chain that ends at a member who neither votes nor delegates, or a chain with an out-of-scope link returns the vote uncast** (the delegator counts as an abstainer, not redirected). The `uncast` column in results is the share of delegated votes left uncast.
6. **Per-final-holder cap c:** a final holder's total resolved weight (own vote plus everything that reached it) is capped at max(1, c × basis). Basis is "cast" (the estate's total resolved weight before capping, default) or "roll" (estate roll size, an alternative swept in E3d). A fifth is read as a fifth of what the estate actually casts, because Users turnout is about 10% and a roll-based fifth (60 of 300) would never bind; E3d shows both. c ∈ {.10, .15, .20, .25, .30} and none. Excess is void, not redistributed.
7. **Estate rule after resolution:** F_e and A_e are computed from capped resolved weights; section 2 then applies unchanged.

Delegate pool: each delegating estate has 10% of its roll (at least one) as active delegates with turnout `dturn` (default .95; swept {.95, .6, .3}, because when delegates always vote chains never run past one edge and depth is inert), 25% of them populist (a = .2, b = 1.2, σ = .7, otherwise a = .7, b = .5, σ = .7). A delegate delegates onward to another delegate with probability `onward` (.3). A delegator's edge goes to another ordinary delegator with probability `peer` (default .3; swept {.3, .8}) and otherwise to a delegate; peer edges are what make chains longer than two (the first full run, where only delegates delegated onward, made depth 2 to unlimited indistinguishable). Loops arise naturally. Delegator and onward targets are Zipf over a fixed delegate ranking with exponent α, multiplied by (1+β) for populist delegates. α ∈ {0, .5, 1, 1.5, 2.5, 4}, β ∈ {0, 1, 3}. α=0 is uniform choice; larger α is guru formation (Kling et al. 2015 observed super-voters in LiquidFeedback; Gölz et al. 2018 show maximum delegated weight grows with concentration).

Not modeled: vote selling, delegation to non-members, delegators choosing delegates by track record.

## 5b. Copy-the-ballot (revision 4, written before its results)

Serves `heimdall-body:follow_up:chat-s1946-1946`.

**Source.** Gölz, Kahng, Mackenzie, Procaccia 2018 (arXiv 1808.01906, introduction, "Just how can the maximum weight be reduced?"): capping delegated weight is an option, "however, as argued by Behrens et al. [3], delegation is always possible by coordinating outside of the system and copying the desired delegate's ballot. Pushing delegations outside of the system would not alleviate the problem of super-voters, just reduce transparency." [3] is Behrens, Kistner, Nitsche, Swierczek, *The Principles of LiquidFeedback* (Interaktive Demokratie, 2014), not fetched here. What the source is: a one-paragraph argument, quoted second-hand by Gölz et al. to motivate their own voluntary approach. It gives no model, rate or measurement of copying, so the copy rate below is an assumption and is swept, not calibrated. Verified by fetching the ar5iv text of the paper this pass.

**Agents.** A copier is a delegator in a delegating estate (Members, Users, Patrons) with the following behavior, per motion, after the final resolution of section 5 (strikes, direct overrides and the cap already applied):

- Trigger: the delegator's final holder h has raw resolved weight above the cap value (the weight the cap would void). Only delegators whose weight is routed to an over-cap holder consider copying. There is no copying under a holder within the cap, and none when there is no cap. The agent does not know whether its own weight is among the voided excess (the cap voids "excess", not named delegators), so every delegator under an over-cap holder is exposed.
- Action: with probability `copy` (friction: the effort of reading and re-entering the ballot; swept {0, .5, 1}) the delegator becomes a direct voter casting weight 1 and holder h's ballot (h's sincere ballot, as it stands before any strategic misreport). Direct voters are never forwarded (section 5 rule 4), so the copied weight leaves h's chain and the cap no longer applies to it. Delegators of the copier now end at the copier, who is itself subject to the cap as a final holder.
- Copiers are not strategic and do not take strikes for the copy (it is voluntary and aligned with h by construction).
- One round per motion. The decision is not remembered: a copier delegates again next motion, and copies again if the holder is still over cap. Delegators do not copy as a standing policy and do not copy unconditionally; copy-everything-always would remove delegation altogether and is out of scope.
- Copy requires that h votes directly this motion, which holds by construction (a final holder is a direct voter).
- Not modeled: the copy being visible to the mechanism (the real system sees only a direct ballot, which is the point of the source's transparency objection), coordination cost that grows with the number of copiers, and delegators choosing to copy a different, trusted voter than their delegate.

**Measures.** Formal concentration is the section 3 measure on the resolved weights (copiers appear as separate weight-1 holders, which is what the mechanism sees). Effective concentration counts each copier's weight to the voter it copied, so it measures whose ballot the weight follows; the cap's job is to limit that. Also reported: copiers as a share of delegated votes. Outcomes (PS, MT, dPS UNI-DIRECT) are reported alongside.

**Experiment E3f.** The delegation cap sweep with copying: alpha {0, .5, 1, 1.5, 2.5, 4} x cap {.10, .15, .20, .25, .30, none} x copy {0, .5, 1}, depth 3, strategic, F = 20, as E3. `copy = 0` reproduces E3 exactly. The depth sweep E3c is unchanged and runs with `copy = 0`; so does every other experiment. Falsifier fixed in advance: the cap "reduces effective concentration once copying is available" if the paired-by-replicate difference (effective Users top-holder share at cap .10 minus no cap) has a 95% interval entirely below -.01 (a drop under one point of share is not called a reduction). Evaluated per alpha and per copy rate by `run.py` and printed at the top of E3f in `results.md`; the cap reduces effective concentration under copying only if this reads "reduces" at `copy = 1`. The first full E3f run printed only means; the interval was added before the final run, and the numbers did not change.

## 5c. Purchased stake (revision 5, written before its results)

Serves the purchased-stake brief (operator relayed: Patron vote power is logarithmic in donation, prior section 4; operator influence strictly below half, ruling `operator-influence-below-half`, prior section 10). Not a motion simulation: a static weight analysis, so it measures weight share and decisiveness, not PS or MT.

**Weight.** A purchase of `amount` EUR weighs w = 1 + log_b(amount/unit): 1 at amount = unit, 0 below unit (below the unit buys no weight, never negative). Each estate has its own conversion, set through `Config.overrides` entries `(estate, key, value)` with keys `euro_b`, `euro_unit`, `hours_b`, `hours_unit` (defaults in `STAKE_BASE`). Only Patrons (euros) and Members (hours) carry stake; Minds are counted once and Users are one-each, so both weigh 1.

**Defaults chosen (assumptions, not rulings; the brief gave none).** Patrons and Members alike: euro b = 10, unit = 10 EUR; hours b = 10, unit = 10 hours. b = 10 reads as "each tenfold purchase adds one vote of weight", the simplest reading of the operator's "logarithmic"; 10 EUR is the smallest ordinary Patron donation in the prior ladder (prior section 4: 10, 50, 200, 5000); 10 hours is roughly a working day or so of contribution. The sweep varies b over {2, 10, 100} and unit over {1, 10, 100} EUR; hours are not swept because no buyer outcome depends on them (see below).

**Contribution hours.** The harness had no contribution mechanism (Members were one-each; prior section 4's Pareto contribution curve was left out of scope in section 4 above). Hours are therefore a new per-agent input with their own per-estate conversion: an estate member's weight is the euro channel plus the hours channel, each through its own (b, unit).

**Electorate.** The buyer joins the Patrons estate as an additional member; the other patrons are `ROLL["Patrons"]` = 4 members (also 40 and 400 as a sensitivity) holding Pareto(1.16) donations from 10 EUR (prior section 4's exponent; the scale is mine). 40 seeds, each a fixed integer. Estate weights are the section 4 weights; the live-estate set is a scenario: all four live, Patrons + Minds live, Patrons alone live (renormalization over live estates, section 2).

**Influence.** Council weight share x = W_Patrons x s / (sum of live W), s the buyer's share of Patrons weight; this is the "weight share" of prior section 10. Decisiveness is reported only at the estate level (buyer alone decides the Patrons estate's vote when s > .5 at q = .5); prior section 10 shows weight share is not decisiveness, and no council-level pivot is computed here.

**Half-influence cap.** The cap named in the brief is the prior section 10 operator cap (weight share strictly below one half), which is *not* the `cap` of section 5 (a per-final-holder delegation cap, a fraction of cast weight, with void excess). The harness had no implementation of the operator cap; `influence_cap` is it: binds iff x >= .5, then cuts the holder's internal weight until x = .5(1 - 1e-9), and reports "unenforceable" when no one else holds weight to cut against. It applies per identity.

**Splitting adversary.** The same total split evenly over k identities: weight k(1 + log_b(amount/(k unit))) against 1 + log_b(amount/unit). Splitting wins because the log is sub-linear: the multiplier approaches (amount/unit)/(1 + log_b(amount/unit)) at k = amount/unit. The sweep reports the multiplier by k, the best k on a geometric grid (a lower bound on the maximum), and a table of defences. **SPEC names no sybil defence** (the charter's frozen roll, A6, stops joins after notice, not the buying of many identities beforehand). Three candidates are quantified, none adopted or chosen: a per-person weight cap, a raised unit floor, a per-identity cost paid out of the budget. They are parameters of `split_weight`, off by default.

**Falsifiers fixed in advance.** (1) Working hypothesis "logarithmic weight bounds buyout power": refuted if a one-million-euro buyer's within-estate share s exceeds 1/2 at the harness's own roll, or if splitting multiplies weight by more than 2 at any k. (2) The half-influence cap is "binding" in a (cell, seed) iff x >= .5; the rate over the sweep is reported per live-estate scenario. (3) The cap is "evaded" by splitting iff the joint share is >= .5 while no single identity's share is.

## 5d. Sybil policy (ruling, 2026-11-07; not simulated)

Ruled by the operator (Meta, #operator, 2026-11-07), option D of the sybil pick on the purchased-stake arm:

> One account per person. If we determine a person to have broken the policy, their identities are consolidated and decisions they influenced are subject to a recount.

This is a rule enforced after the fact, not a weight formula, so section 5c's weight analysis does not model it and nothing in `estatesim.py` implements it. It names what the estate vote must keep. Each item is a requirement on the real system, not a design:

1. **Durable per-decision vote ledger.** Every cast ballot is kept as a row: decision id, voter identity, estate, weight at the time of the vote, timestamp, and the ballot itself. Append-only. Weight is stored as it was computed then (rule version included), never recomputed from current state, or a recount cannot reproduce the original tally.
2. **Identity consolidation operation.** Merges the identities found to belong to one person into one account. It writes an audit receipt: who decided, the evidence cited, the identities merged, the surviving account, the time. It does not edit the ledger; it adds a mapping from old identity to person that a recount reads. Reversible by a later receipt that supersedes the first.
3. **Deterministic recount.** For each decision the consolidated person influenced (any ledger row from any merged identity), re-tally from the ledger under the mapping: the person's rows count as one voter with weight set by the same rule applied to the person (the log of the combined purchase, not the sum of split weights), estates and thresholds as at the decision. The same ledger, mapping and rule version always give the same tally, and the output states which decisions flip. No clock, no randomness, no read of live state.
4. **Who determines a violation, and appeal: OPEN QUESTION.** The ruling does not say who finds that a person broke the policy, on what evidence, or how the accused can contest it. Needed before the operation in item 2 can run: the deciding body (an estate, the council, a named steward), the standard of evidence, notice to the accused, an appeal path with a different decider, and whether a consolidation takes effect before or after appeal. Not decided here; to be put to the operator.
5. **Not covered by the ruling: the window before detection.** Decisions stand until a recount flips them; whether a flipped decision is reversed, re-voted or only annotated is also open.

Section 5c's numbers show why enforcement after the fact is the only lever on offer: weight is per identity, so a split buyer holds 0.676 of Patrons weight at k = 2 and 0.901 at k = 10, with no identity over the half cap.

## 6. Strategic and adaptive agents

A fraction ρ = .25 of ballot holders and of absent undelegated roll members are strategic. Each is myopic about the motion at hand and best-responds to everyone else's current ballots over three rounds in random order:

- **Misreporting:** a strategic ballot holder picks among For/Against/Abstain (its whole resolved weight, so a capped delegate misreports the weight it carries) the ballot whose resulting outcome it prefers (pass iff its u > 0); it keeps the sincere ballot on ties of outcome.
- **Turnout response:** a strategic absent member joins with the sincere ballot iff joining flips the outcome to the one it prefers (the pivot-seeking participant of Palfrey and Rosenthal 1983). Strategic ballot holders may also abstain (Abstain option above).
- **Coalition shifts:** a coordinated faction of F Users (default F = 20, 6.7% of the roll, sweep {0, 10, 20, 40}) turns out with certainty and votes For iff d − m > 0 (the populist agenda), Against otherwise. It does not best-respond; it models the organized actor that turnout-limited estates attract. Faction members are not delegators.

Strategy is best response per rule: each rule is run separately from the same sincere draws, so a rule that rewards misreporting is not credited with sincere behavior.

Limits: delegators are not strategic (their ballot is their delegate's); single-motion myopia; no repeated-game or reputation effects; no cross-estate vote trading (prior §6 S2 covers logrolling on unitary blocs).

## 7. Experiments

Paired across rules: one set of random draws per motion feeds all rules (common random numbers). Replicates R = 40 (a fresh delegation network per replicate), M = 500 motions each, categories uniform. Seeds are fixed integers; no wall-clock or hash use. Confidence intervals are 1.96 standard errors over replicates; paired differences are taken within replicate.

- **E1 baseline:** sincere, no delegation, no faction. Rules × weight seeds × q.
- **E2 strategic:** E1 with ρ = .25 strategic agents and faction sweep.
- **E3 delegation:** concentration α × cap c (36 cells) at depth 3, strategic on, F = 20; **E3b** the same sincere with no faction (isolates delegation).
- **E3c transitive depth:** depth {2, 3, 4, 6, unlimited} × delegate turnout {.95, .3} × peer share {.3, .8}, α = 1.5, c = .2.
- **E3e renewal:** renew {0, .02, .1, .5}, depth 3.
- **E3d cap basis:** cast vs roll × c ∈ {.05, .10, .20, .30}, depth 3, α = 1.5.
- **E3f copy-the-ballot:** section 5b.
- **E4 appeal attraction:** β sweep at α = 1.5, c = .2.
- **E6 purchased stake:** section 5c. b x unit x Patrons roll x amount x estate weights x live-estate scenario, 40 seeds; splitting k sweep; candidate defences.
- **E5 robustness:** Users appeal loading b ∈ {.4, .9, 1.4}; Users base turnout {.05, .10, .30}; Minds ICC (s_Minds ∈ {.3, .9, 1.5}). This is the check on the assumed parameters in section 4.

## 8. Pre-registered falsifiers

The claim is **supported** in a cell if the paired difference PS(UNI) − PS(DIRECT) has a CI entirely below 0 and MT(UNI) ≥ MT(DIRECT) − .05. It **fails** if the CI is entirely above 0 or MT(UNI) falls more than .10 below DIRECT's (dampened by not working). Otherwise **undecided**. The summary reports counts of each across cells and lists the failing cells; no cell is dropped. The report states separately whether delegation shrinks or grows the gap.

## 9. Tests (`test_estatesim.py`)

Pin the rules, not the spelling: rule thresholds including ties; chains within depth, over depth (uncast), unlimited, terminated by an intermediate direct vote, looped, dead-ended; scope as overlap of all links; direct override; two-strike revocation; cap void, cap floor, copy-the-ballot (off draws nothing, full copying evades the cap, copied ballot is the holder's, within-cap and no-cap holders see no copying, effective weight follows the copied voter), cap on the final holder, cap basis; purchased stake (weight 1 at unit and 0 below, per-estate conversion, hours channel, the million-euro buyer's share derived from the config, log versus linear, the operator cap's bind and unenforceable cases, the splitting multiplier against its formula, defences that do and do not neutralise, per-identity cap evasion); determinism (same seed, same bytes); paired draws identical across rules; misreporting never produces an outcome the agent likes less; metrics on hand-built outcome lists.

## 10. Sources

Verified by search this pass [V]; from memory, not fetched [M].

- [V] Mudde 2004, "The Populist Zeitgeist", Government and Opposition 39(4):541–563: ideational (thin-centered) definition, virtuous people against corrupt elite. https://illiberalism.org/cas-mudde-the-populist-zeitgeist (secondary). Mapping to a measure is this document's, not Mudde's.
- [V] Kling, Kunegis, Hartmann, Strohmaier, Staab 2015, "Voting Behaviour and Power in Online Democracy: A Study of LiquidFeedback in Germany's Pirate Party", ICWSM 9. https://arxiv.org/abs/1503.07723. Super-voters hold high theoretical power; empirically they voted with the majority and did not fully use it.
- [V] Gölz, Kahng, Mackenzie, Procaccia 2018/2021, "The Fluid Mechanics of Liquid Democracy", WINE 2018, ACM TEAC 2021. https://arxiv.org/abs/1808.01906. Preferential-attachment delegation model; maximum delegated weight; two delegation choices lower it.
- [V] Gölz et al. 2018 introduction, second-hand citing Behrens, Kistner, Nitsche, Swierczek 2014, "The Principles of LiquidFeedback" [M], for copy-the-ballot as the answer to caps (section 5b).
- [V] Caragiannis and Micha 2019, "A Contribution to the Critique of Liquid Democracy", IJCAI. https://www.ijcai.org/proceedings/2019/17. Local delegation can do worse than direct voting or a dictator. Rebuttal: Butterworth and Booth 2022, arXiv 2206.13904, disputes the model assumptions.
- [V] Palfrey and Rosenthal 1983, "A Strategic Calculus of Voting", Public Choice 41(1):7–53; and 1985, "Voter Participation and Strategic Uncertainty", APSR 79(1):62–78. High-turnout equilibria need complete information. Turnout response here is the myopic pivot-seeker, a simplification.
- [V] Delegate-cap practice: no official cap found for Optimism, Arbitrum or Uniswap; Lido and a Jupiter forum proposal (5%) are the examples surfaced (https://discuss.jup.ag/t/delegated-voting-for-asr/34971/32; https://gov.optimism.io/t/can-delegates-become-too-powerful/2919 discusses mitigation without an adopted cap). Weak evidence. The operator's "a fifth" is a design choice, which is why c is swept.
- From the repo: `../2026-10-29-swarm-governance-prior-art.md` (Compound 289 concentration, ~17% of voting tokens delegated and clustering on popular delegates across 18 Ethereum DAOs, Frontiers 2025); prior estate research README §3, §5b, §6, §9, §10.
- [M] Riker and Ordeshook 1968 calculus of voting; Nurmi 1998 referendum paradox; Madison, Federalist 10 (filtering mass sentiment through representatives, the classical form of the dampening claim). Not fetched.
