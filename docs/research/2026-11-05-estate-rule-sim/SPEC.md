# Estate-rule sim harness: spec (2026-11-05)

Serves follow-up `heimdall-body:follow_up:chat-mind-20261105T002133411Z-t0`. Prior work: `../2026-10-29-estate-tally-game-theory/` (README.md plus numpy probes in `sims/`). Facts there are cited as "prior §n". This document is written before the code; `estatesim.py` implements it, and where the code had to deviate the deviation is listed at the bottom of `REPORT.md`, not edited in here. Revision 2 (delegation rules) was made before any result was generated. Revision 3 (renewal, peer edges) was forced by the first full run, whose delegation cells were inert; the first run's results were discarded, not kept.

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
- **E4 appeal attraction:** β sweep at α = 1.5, c = .2.
- **E5 robustness:** Users appeal loading b ∈ {.4, .9, 1.4}; Users base turnout {.05, .10, .30}; Minds ICC (s_Minds ∈ {.3, .9, 1.5}). This is the check on the assumed parameters in section 4.

## 8. Pre-registered falsifiers

The claim is **supported** in a cell if the paired difference PS(UNI) − PS(DIRECT) has a CI entirely below 0 and MT(UNI) ≥ MT(DIRECT) − .05. It **fails** if the CI is entirely above 0 or MT(UNI) falls more than .10 below DIRECT's (dampened by not working). Otherwise **undecided**. The summary reports counts of each across cells and lists the failing cells; no cell is dropped. The report states separately whether delegation shrinks or grows the gap.

## 9. Tests (`test_estatesim.py`)

Pin the rules, not the spelling: rule thresholds including ties; chains within depth, over depth (uncast), unlimited, terminated by an intermediate direct vote, looped, dead-ended; scope as overlap of all links; direct override; two-strike revocation; cap void, cap floor, cap on the final holder, cap basis; determinism (same seed, same bytes); paired draws identical across rules; misreporting never produces an outcome the agent likes less; metrics on hand-built outcome lists.

## 10. Sources

Verified by search this pass [V]; from memory, not fetched [M].

- [V] Mudde 2004, "The Populist Zeitgeist", Government and Opposition 39(4):541–563: ideational (thin-centered) definition, virtuous people against corrupt elite. https://illiberalism.org/cas-mudde-the-populist-zeitgeist (secondary). Mapping to a measure is this document's, not Mudde's.
- [V] Kling, Kunegis, Hartmann, Strohmaier, Staab 2015, "Voting Behaviour and Power in Online Democracy: A Study of LiquidFeedback in Germany's Pirate Party", ICWSM 9. https://arxiv.org/abs/1503.07723. Super-voters hold high theoretical power; empirically they voted with the majority and did not fully use it.
- [V] Gölz, Kahng, Mackenzie, Procaccia 2018/2021, "The Fluid Mechanics of Liquid Democracy", WINE 2018, ACM TEAC 2021. https://arxiv.org/abs/1808.01906. Preferential-attachment delegation model; maximum delegated weight; two delegation choices lower it.
- [V] Caragiannis and Micha 2019, "A Contribution to the Critique of Liquid Democracy", IJCAI. https://www.ijcai.org/proceedings/2019/17. Local delegation can do worse than direct voting or a dictator. Rebuttal: Butterworth and Booth 2022, arXiv 2206.13904, disputes the model assumptions.
- [V] Palfrey and Rosenthal 1983, "A Strategic Calculus of Voting", Public Choice 41(1):7–53; and 1985, "Voter Participation and Strategic Uncertainty", APSR 79(1):62–78. High-turnout equilibria need complete information. Turnout response here is the myopic pivot-seeker, a simplification.
- [V] Delegate-cap practice: no official cap found for Optimism, Arbitrum or Uniswap; Lido and a Jupiter forum proposal (5%) are the examples surfaced (https://discuss.jup.ag/t/delegated-voting-for-asr/34971/32; https://gov.optimism.io/t/can-delegates-become-too-powerful/2919 discusses mitigation without an adopted cap). Weak evidence. The operator's "a fifth" is a design choice, which is why c is swept.
- From the repo: `../2026-10-29-swarm-governance-prior-art.md` (Compound 289 concentration, ~17% of voting tokens delegated and clustering on popular delegates across 18 Ethereum DAOs, Frontiers 2025); prior estate research README §3, §5b, §6, §9, §10.
- [M] Riker and Ordeshook 1968 calculus of voting; Nurmi 1998 referendum paradox; Madison, Federalist 10 (filtering mass sentiment through representatives, the classical form of the dampening claim). Not fetched.
