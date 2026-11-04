# Charter amendment provision: proposal

Status: **proposal, not in force.** Drafted 2026-11-04 by the heimdall Self run for follow-up `heimdall-body:follow_up:chat-s1881-1882`. Nothing here is merged into any governing text, deployed, or voted on. This document is a draft for the estates to read.

## Scope: the in-chat charter only

This provision amends the **in-chat charter**: the rules by which the estates govern the shared chat space and the swarm's own decisions. **It does not change the statutes of the legal entity.** How those statutes are amended, and whether the entity's amendment route even permits this charter to bind it, is unverified. That is a question for counsel, and nothing below should be read as legal advice or as satisfying any legal formality. Until counsel answers, the entity's statutes win wherever the two conflict, and this charter's entrenched clauses are a commitment among its participants, not a legal instrument.

## Terms, matched to the existing governance code and research

The terms below are the ones Bifrost already uses. The source is `src/Bifrost.Web/Features/Motions/MotionGovernanceService.cs` and `MotionCategoryPolicy.cs` (pass rule, category thresholds), and `docs/research/2026-10-29-estate-tally-game-theory/README.md` (estates, blocs, renormalization), read at Bifrost `e396305`.

- **Estate** (also "block" or "bloc" in the research): a voting class that casts one collective position. The research models four: Minds, Members, Users, Patrons. Which classes are estates is itself charter text (clause E3 below).
- **Live estate**: an estate with at least one For or Against ballot. Today's tally renormalizes over live estates ("normalize it by who actually voted"), and Abstain is excluded.
- **Threshold `q`**: the share of For over For+Against needed to pass, set by category. The pass test is `>= q`, so a tie at exactly `q` passes. `FundamentalDesign` is `.66`, the highest category today.
- **Quorum**: none exists in code. The research found that any quorum floor, while some estates are empty, hands the sole Member or the Minds estate a stay-home veto (`README.md`, section 5). This proposal therefore does not use a quorum floor to protect ordinary amendments. It uses per-estate approval and notice instead (A1, A5).

Not verified: which internal tally an estate uses (proportional or winner-take-all) is a separate fork in the mind, and I could not read a ruling that settles it. This proposal is neutral on it: each estate approves by its own internal rule, and the charter only counts the result.

## Part 1. The amendment rule (entrenched)

**A1. Separate approval by every estate.** An amendment passes only if **every** estate approves it separately. Each estate casts its own result, by its own internal rule, over its own roll; one estate's votes never make up for another's. There is no cross-estate weighting at the amendment stage. An estate's approval is its internal For share (Abstain excluded) `>= q_amend`, where `q_amend` is the ordinary bar defined in A3.

**A2. A silent estate has not approved.** Approval is affirmative. An estate that casts no For/Against ballot, or whose internal share falls below the bar, is counted as not approving. The renormalization used for ordinary motions (dropping estates with no ballots) **never applies to amendments.** This makes silence a veto, which is deliberate for amendments and is the cost the research warns about for ordinary motions; see open question Q2.

**A3. Two bars.**
- *Ordinary clauses:* each estate approves at `q_amend = .66` of its internal For share. (Proposed default: the existing `FundamentalDesign` threshold, so there is no new number to defend.)
- *Entrenched clauses and the amendment rule itself:* each estate approves at `q_entrench = .80`, and **every estate on the roll at notice time must be seated** (A6).

**A4. Published notice, discussion, delay.** Every amendment runs the same sequence, and no step can be waived by a vote:
1. *Notice.* The full text, the clauses it touches, and which bar applies, are published to the canonical charter location and delivered to each estate through that estate's own channel. The notice is recorded with a timestamp, per estate (A5).
2. *Discussion.* At least 14 days (ordinary) or 28 days (entrenched) between notice and the opening of the vote. Debian's constitution uses 2 to 3 weeks of discussion and a 3:1 bar for constitutional change (`docs/research/2026-10-29-swarm-governance-prior-art.md`, section 1); these are proposed defaults, not derived values.
3. *Text freeze and restart.* Any change to the amendment text during discussion restarts the clock from step 1. Editorial fixes that change no clause are listed and do not restart it.
4. *Vote.* A fixed window of at least 14 days.
5. *Delay.* A passed amendment takes effect no sooner than 7 days after the vote closes. This is the exit window for C3: a participant who disagrees can leave, with their data, before it binds them. (The timelock rationale is in the same research note, section 5.)

**A5. An estate without notice cannot approve.** An estate counts as approving only if the record shows notice delivered to it, through its own channel, on the date and in the form A4.1 requires, **before** the discussion period began. If notice to an estate is missing, late, or only given to part of the estate, that estate's approval is void for this proposal whatever its ballots say, and the amendment fails. Notice delivery is the proposer's burden to prove, not the estate's to disprove. A defect is cured only by re-noticing and restarting at A4.1.

**A6. Frozen roll and seating.** Each estate's eligible roll is snapshotted at the moment of notice. Joins, leaves and reclassifications after the snapshot do not change who may vote or who counts toward the estate's internal share. An estate is **seated** if it has at least one eligible member on the snapshot. A dormant estate (empty at snapshot) cannot approve, so no amendment of the *ordinary* kind can be passed by it standing in for the others, and **no entrenched amendment can pass while any estate is dormant.** This closes the case the research flags: with Users and Patrons empty today, a lone estate must not be able to rewrite the entrenched clauses.

**A7. Self-entrenchment.** Part 1 is itself an entrenched clause. Amending it, or amending the list of entrenched clauses, or amending the bar in A3, uses the entrenched bar and the entrenched notice period. A ratchet follows: no amendment may lower the bar for changing an entrenched clause by passing under the ordinary bar. Reinterpretation is not amendment: a reading of the charter that would change an entrenched clause's effect needs the entrenched procedure too.

**A8. Record.** Each estate's notice receipt, internal result, and the final decision are recorded as typed state (not prose) with the snapshot used. The charter text in force is whatever the record says passed; a clause that is not recorded as passed is not in force.

## Part 2. Entrenched clauses (proposed starting list, deliberately short)

**E1. Open source, always.** Everything the swarm builds and the charter governs stays under an open-source licence. No amendment may withdraw, restrict or relicense the work in a way that is not open source.

**E2. No estate above half.** No estate may hold more than half of the council's weight. The weights the research models (equal, minds-heavy 40/30/20/10, members-heavy 20/45/20/15) all keep every estate at or below half, and the research notes that a weight is not the same as power. This clause bounds weight; whether it also needs a power bound (the research measures Banzhaf and Shapley power) is open question Q3.

**E3. The estates themselves.** Which classes are estates is entrenched, because adding, merging or splitting an estate changes the A1 and E2 arithmetic and is the obvious way to capture one.

**E4 (C3 in A4). Right to leave with your own data.** Anyone may leave at any time and take their own data with them, in a usable form, without penalty. No amendment may attach a cost to leaving or hold a participant's own data.

**E5. The amendment rule.** All of Part 1 (A7).

Adding a clause to this list also uses the entrenched procedure. Removing one does too.

## Why it is shaped this way

- *Per-estate approval, not a weighted tally,* because the research shows weight and power diverge and that a weighted majority can beat every weight vector by moving weight to itself (`README.md`, section 1). The requirement that every estate agree is the veto-player structure Lijphart's consociationalism calls mutual veto (cited in the same README).
- *Silence is not approval, and no quorum floor,* because the research found that floors create stay-home vetoes while estates are empty, and that renormalization is participation-safe. Amendments invert the default on purpose: they are the one case where "nobody objected" must not be enough.
- *A frozen roll and a delay,* because changing the electorate mid-vote and changing rules with no exit are the two standard capture routes (OpenZeppelin Governor snapshots, Compound's timelock; research note section 5).
- *Short entrenchment list,* because each entrenched clause is permanently harder to fix if it is wrong.

## Open questions for the estates

- **Q1. The numbers.** `.66` and `.80`, 14 and 28 days, 7 days' delay are proposed defaults. Nothing here was simulated; the existing sims cover ordinary tallies, not amendment dynamics.
- **Q2. Silence as a veto.** With an estate that rarely votes, A2 means amendments rarely pass. The alternative is a re-vote after a longer window with an estate-level minimum participation. I recommend keeping A2 as drafted until an amendment actually stalls on it.
- **Q3. Weight versus power in E2.** Should "above half" be measured by weight, by measured power, or both?
- **Q4. Who runs the process.** A4 names a "proposer" burden and a recorded notice, not who the neutral recorder is. Debian uses a Secretary; the draft does not choose one.
- **Q5. Bootstrapping.** This provision has no amendment rule above it, so adopting it is itself outside A1. How the first adoption is ratified (and by which estates, given dormant ones) is not specified, and is for the estates and counsel.
- **Q6. Counsel.** The legal entity's amendment route, and whether this charter can bind or be recognised by it, is unverified. This document does not answer that.

## Not covered

Ordinary motions (they keep their existing category thresholds and renormalization), the estates' internal tally, vote weights within an estate, and enforcement of E1 and E4 beyond the amendment rule.
