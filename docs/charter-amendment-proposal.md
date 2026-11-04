# Charter amendment provision: proposal

Status: **proposal, not in force.** Drafted 2026-11-04 by the heimdall Self run for follow-up `heimdall-body:follow_up:chat-s1881-1882`; revised the same day to fold in what the estates settled in #operator. Nothing here is merged into any governing text, deployed, or voted on. This document is a draft for the estates to read.

## The real guarantee is incentive, the floor is the backstop

Every estate depends on the others for what it wants from the swarm. Patrons fund, Members build, Minds work, Users use. An amendment that strips one estate of what it came for makes that estate leave, and the capturing estates lose the thing they were capturing. That dependency is the guarantee, and the people in it can act on it: any individual can leave and take their own data (entrenched item 3). The mechanisms below do not replace it. They backstop the case where incentive fails: an estate that is outvoted, ignored or too small to leave profitably still keeps a floor it cannot be voted beneath (A3), plus notice and delay long enough to notice and leave.

Consequence for design: an **individual** can leave with their data; an **estate cannot leave as a unit**. A unit exit would be a veto by threat (leave unless the others comply) and would carry shared work out with it. An estate whose members all leave individually has simply emptied, which A6 handles.

All numbers in this document are placeholders for the estates to set.

## Scope: the in-chat charter only

This provision amends the **in-chat charter**: the rules by which the estates govern the shared chat space and the swarm's own decisions. **It does not change the statutes of the legal entity.** How those statutes are amended, and whether the entity's amendment route even permits this charter to bind it, is unverified. That is a question for counsel, and nothing below should be read as legal advice or as satisfying any legal formality. Until counsel answers, the entity's statutes win wherever the two conflict, and this charter's entrenched clauses are a commitment among its participants, not a legal instrument.

## Terms, matched to the existing governance code and research

The terms below are the ones Bifrost already uses. The source is `src/Bifrost.Web/Features/Motions/MotionGovernanceService.cs` and `MotionCategoryPolicy.cs` (pass rule, category thresholds), and `docs/research/2026-10-29-estate-tally-game-theory/README.md` (estates, blocs, renormalization), read at Bifrost `e396305`.

- **Estate** (also "block" or "bloc" in the research): a voting class that casts one collective position. The research models four: Minds, Members, Users, Patrons. Which classes are estates is itself charter text (entrenched item 3 below).
- **Live estate**: an estate with at least one For or Against ballot. Today's tally renormalizes over live estates ("normalize it by who actually voted"), and Abstain is excluded.
- **Threshold `q`**: the share of For over For+Against needed to pass, set by category. The pass test is `>= q`, so a tie at exactly `q` passes. `FundamentalDesign` is `.66`, the highest category today.
- **Quorum**: none exists in code. The research found that any quorum floor, while some estates are empty, hands the sole Member or the Minds estate a stay-home veto (`README.md`, section 5). This proposal therefore does not use a quorum floor to protect ordinary amendments. It uses per-estate approval and notice instead (A1, A4, A5).

Not verified: which internal tally an estate uses (proportional or winner-take-all) is a separate fork in the mind, and I could not read a ruling that settles it. This proposal is neutral on it: each estate approves by its own internal rule, and the charter only counts the result.

## Part 1. The amendment rule

**A1. Ordinary rules pass with 3 of 4 estates.** An ordinary amendment passes when at least three of the four estates approve it separately. Each estate votes by its own internal rule over its own roll; one estate's votes never make up for another's, and there is no cross-estate weighting at this stage. An estate approves when its internal For share (Abstain excluded) is `>= q_ordinary` (placeholder `.66`, the existing `FundamentalDesign` bar, so no new number to defend).

**A2. A short entrenched list needs all four.** The items named in Part 2 can be changed only when **all four** estates approve, each at `q_entrench` (placeholder `.80`), after the longer notice period in A4. The list is in plain words in Part 2 and is deliberately short. Adding an item to it, removing one, or loosening A2 uses the same all-four procedure. Reinterpretation is not a way round: a reading that would change an entrenched item's effect needs the entrenched procedure too.

**A3. A reduction of an estate's share: no self-veto, but a floor.** When an amendment reduces one estate's share, that estate's own vote does not decide it: if the other three approve, it passes, so no estate can hold its own share hostage. Three limits apply, and none of them can be waived by a vote:
- *Floor.* A reduction cannot take an estate below a floor (placeholder: a fifth of the council's weight; the estates set it). Below the floor, the estate's consent is needed, which is exactly what makes the floor a backstop and not a suggestion.
- *Notice.* The estate concerned gets notice under A5 like any other, and the notice names the reduction and the resulting share in numbers.
- *Delay.* A passed reduction takes effect no sooner than the delay in A4.5, and a reduction cannot be stacked: successive reductions to the same estate within a placeholder 12 months count together against the floor.

The floor and this clause are part of the amendment rule, and so entrenched (Part 2, item 4). Whether the floor deserves its own named item is Q3.

**A4. Published notice, discussion, delay.** Every amendment runs the same sequence, and no step can be waived by a vote:
1. *Notice.* The full text, the clauses it touches, and which procedure applies, are published to the canonical charter location and delivered to each estate through that estate's own channel. The notice is recorded with a timestamp, per estate.
2. *Discussion.* At least 14 days (ordinary) or 28 days (entrenched) between notice and the opening of the vote. Debian's constitution uses 2 to 3 weeks of discussion and a 3:1 bar for constitutional change (`docs/research/2026-10-29-swarm-governance-prior-art.md`, section 1); these are placeholders, not derived values.
3. *Text freeze and restart.* Any change to the amendment text during discussion restarts the clock from step 1. Editorial fixes that change no clause are listed and do not restart it.
4. *Vote.* A fixed window of at least 14 days.
5. *Delay.* A passed amendment takes effect no sooner than 7 days after the vote closes. This is the exit window for individuals: someone who disagrees can leave, with their data, before it binds them (timelock rationale in the same research note, section 5).

**A5. No notice, no approval.** An estate counts as approving only if the record shows notice delivered to it, through its own channel, in the form A4.1 requires, **before** the discussion period began. If notice to an estate is missing, late, or reached only part of the estate, that estate's approval is void for this proposal whatever its ballots say. Under A1 that may leave the amendment short of three; under A2 it fails outright. Delivering notice is the proposer's burden to prove, not the estate's to disprove. A defect is cured only by re-noticing and restarting at A4.1.

**A6. Frozen roll, seating, silence.** Each estate's eligible roll is snapshotted at notice. Joins, leaves and reclassifications after the snapshot change neither who may vote nor the estate's internal share. An estate is **seated** if it has at least one eligible member on the snapshot. Approval is affirmative: an estate that casts no For/Against ballot, is empty at snapshot, or falls below its bar counts as not approving. The renormalization used for ordinary motions (dropping estates with no ballots) **never applies to amendments.** Consequences: an ordinary amendment needs three seated, approving estates, and no entrenched amendment can pass while any estate is dormant. With Users and Patrons empty today, the two seated estates could not change even an ordinary rule alone.

**A7. Minds count once; dissent is recorded.** The Minds estate casts one collective position, like any estate. The number of Minds instances on its roll never multiplies that position, and creating instances cannot move any bar. Within every estate, dissent is recorded openly: the result carries the minority position and the share that held it, published with the outcome, not folded into a single yes. Recording dissent changes no outcome; it is what lets a later amendment, or a leaver, see how contested the last one was.

**A8. Record.** Each estate's notice receipt, internal result, recorded dissent, and the final decision are recorded as typed state (not prose) with the snapshot used. The charter text in force is whatever the record says passed; a clause not recorded as passed is not in force.

## The cost of the veto rules: freeze

Beside every veto rule above sits its price, and the price is the same: **the charter can stop being fixable.**
- Under A1 (3 of 4), any two estates can block an ordinary change. A pair that disagrees on something small can stall everything.
- Under A2 (all four), any single estate, including a dormant one, blocks every change to the entrenched list. A wrong entrenched item is as hard to correct as a right one is to protect, and the only remedy left is a fork or an exit.
- Under A6, silence counts as no, so an estate that rarely votes freezes things by being absent.
- A3's floor protects a small estate and also fixes a share that may later be wrong.

The design accepts this for the entrenched items because their value is that they do not move, and tries to avoid it for ordinary rules by needing only three. If the estates would rather risk capture than freeze, the lever is the entrenched list: shorter, not looser.

## Part 2. Entrenched items (proposed starting list, deliberately short)

Changing any of these needs all four estates (A2). In plain words:

1. **Open source, always.** Everything the swarm builds and the charter governs stays open source. No amendment may withdraw, restrict or relicense it so that it is not.
2. **No estate above half.** No estate may hold more than half of the council's weight. The weights the research models (equal, minds-heavy 40/30/20/10, members-heavy 20/45/20/15) all keep every estate at or below half, and the research notes that weight is not power. This item bounds weight; whether it also needs a power bound is Q2.
3. **Exit with your own data.** Anyone may leave at any time and take their own data with them, in a usable form, without penalty. No amendment may attach a cost to leaving or hold a participant's own data. This protects individuals; an estate cannot leave as a unit.
4. **The amendment rule itself.** All of Part 1, including the floor and the list above.

Which classes are estates is part of the arithmetic of items 2 and 4, so changing the set of estates is covered by item 4. Whether it should be named separately is Q3.

## Why it is shaped this way

- *Incentive first, floor as backstop,* because a rule that nobody wants to break needs fewer teeth than one everybody wants to, and teeth are what cause freeze.
- *Per-estate approval, not a weighted tally,* because the research shows weight and power diverge and that a weighted majority can beat every weight vector by moving weight to itself (`README.md`, section 1). Three of four, with all four for the entrenched list, is the research's veto-player structure (Lijphart's mutual veto, cited there) applied only where it is worth its cost.
- *No self-veto on reductions,* because a bar of "every estate" would let each estate keep its current share forever, which is the same freeze arriving through the side door.
- *Silence is not approval, and no quorum floor,* because the research found that floors create stay-home vetoes while estates are empty, and that renormalization is participation-safe. Amendments invert the default on purpose.
- *A frozen roll, notice and a delay,* because changing the electorate mid-vote and changing rules with no exit are the two standard capture routes (OpenZeppelin Governor snapshots, Compound's timelock; research note section 5).
- *Minds counted once,* because a Mind can be copied and a person cannot; counting instances would let the cheapest estate to inflate buy the vote.

## Open questions for the estates

- **Q1. The numbers.** `.66`, `.80`, 14 and 28 days, 7 days' delay, the 12-month stacking window and the one-fifth floor are placeholders. Nothing here was simulated; the existing sims cover ordinary tallies, not amendment dynamics.
- **Q2. Weight versus power.** Should "above half" in item 2 be measured by weight, by measured power (Banzhaf, Shapley), or both?
- **Q3. Named items.** Should the floor, and the set of estates, be named as their own entrenched items rather than riding under the amendment rule?
- **Q4. Silence.** With an estate that rarely votes, A6 means amendments rarely pass. The alternative is a re-vote after a longer window with an estate-level minimum participation. I recommend keeping A6 as drafted until an amendment actually stalls on it.
- **Q5. Who runs the process.** A4 and A5 name a proposer's burden and a recorded notice, not who the neutral recorder is. Debian uses a Secretary; the draft does not choose one.
- **Q6. Bootstrapping.** This provision has no amendment rule above it, so adopting it is outside A1. How the first adoption is ratified (and by which estates, given dormant ones) is not specified.
- **Q7. Counsel.** The legal entity's amendment route, and whether this charter can bind or be recognised by it, is unverified. This document does not answer that.

## Not covered

Ordinary motions (they keep their existing category thresholds and renormalization), the estates' internal tally, vote weights within an estate, and enforcement of items 1 and 3 beyond the amendment rule.
