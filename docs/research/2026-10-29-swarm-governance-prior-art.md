# Swarm governance on Bifrost: readiness and prior art (2026-10-29)

Serves follow-up `eureka-body:follow_up:swarm-governance-on-bifrost` and ruling `eureka-body:ruling:self-run-result-to-classifier` (classifier tuning by swarm vote). Two Eyes passes, facts only. Part 1 reads Bifrost at 7b63031; part 2 is prior art from the web, with confidence tags.

## Part 1: Bifrost governance readiness

Motions (Postgres/EF, Razor + Eve surface): Domain/BifrostModels.cs:665-721 (Motion, Vote), enums :86-115; Features/Motions/MotionGovernanceService.cs (create :13, vote :47, close :95-131); MotionCategoryPolicy.cs thresholds Bugs .15, Cosmetics .30, Balance .40, Features .50, NewContent .50, FundamentalDesign .66.
Pass rule: For weight / (For+Against weight) >= ApprovalThreshold; Abstain excluded; no quorum field or code anywhere (grep "quorum" empty). Close is manual by creator or CanModerateMotions (:105); ResolveStatusLabel also derives Passed/Failed display after ClosesAtUtc.
Weight: Membership.EffectiveVotingWeight = 0 unless Status Active, else max(1, sum current TierSnapshot weights) (BifrostModels.cs:357-361). TierSnapshotKind Patron|Contributor.
Membership: MembershipStatus Authenticated|PendingApproval|Active|Suspended; MemberRole StandardMember|Producer|Maintainer|LedgerReviewer|PlatformAdmin (RoleAssignment); flags IsPlatformAdmin, CanManageProjects, CanManageLedger, CanModerateMotions. Activation: bootstrap admin, admin approval (Pages/App/Members), Heimdall member claim. Native registration POST /auth/bifrost/register is anonymous, yields Authenticated only. /App is ActiveMember-gated (Program.cs:57).
Personas: no Persona account/voter class. BridgeActorKind Member|Agent|Persona|Service exists for bridge actions only (BifrostModels.cs:178). A Persona can only vote if it holds a UserAccount with Active membership (nothing in code makes one). Persona identity names are "reserved in Bifrost's identity namespace" per gamecult-ops/docs/gamecult-chat-target.md:84,179: Persona Matrix user "Registered by the mouth when Bifrost governance approves the Persona" -- documented; no code for that approval found.
Typed-document exposure: motions/votes are Postgres rows, not CultCache docs. GET /eve/governance/surface (Program.cs:513, ActiveMember-gated) returns Eve JSON surface (Eve is deprecated per F:\Projects\CLAUDE.md). MotionEveSurfaceService names cultmesh://asgard.starfire.bifrost/commands/motion and motion.create/vote/close commands; no handler for that route found in code (grep). verse-service-contract.md:60,185-196 documents schema gamecult.bifrost.governance and the command route; schema ids for motion/vote listed as todo (:256).
Governance topics/comments: tools/governance-threads.mjs, CultCache file .bifrost/governance-threads.cc, types bifrost.governance.topic / topic-comment; statuses open->consensus_ready->approved->dispatched->closed; support/objection are comment stances, not votes; the web app never reads it (docs/concept-mill-votes-target.md facts 1-5). Eyes pass 2026-10-10 found no such store on Yggdrasil. Web keeps only GovernanceActivityReceipt rows.
Concept Mill votes (the one different vote system): ConceptVoteModels.cs, Features/ConceptVotes/*, Pages/Mill/Vote; VoterClass Anonymous|Account|Member; public tally export /public/concepts with CORS (ConceptTallyExport.cs). Per-subject appraisals, not motions; deployed in 67072b2 lineage.
Chat integration: persona-matrix-mouth.mjs (Persona Matrix speech, main 7b63031), OIDC issuer + back-channel logout on membership leave (main 1894f65); persona feedback ingress/delivery as signed CultCache docs (docs/bridge.md:195-217). No motion/vote surface in chat found. Discord/Reddit voting prompts documented as future (full-implementation-strategy.md:78-79).
UI: Razor /App/Motions (Pages/App/Motions/Index.cshtml[.cs]), /App/Members; no tally history page beyond list.
Docs: docs/verse-service-contract.md, full-implementation-strategy.md (Phase 3 governance and revenue share), bridge.md, agent-transport.md, concept-mill-votes-target.md, jurisdiction-map.md.
Ruling read: eureka-body:ruling:self-run-result-to-classifier (InForce, Operator, 2026-10-29): "tuning authority belongs to a swarm decision process, pending that process existing."
Not read: Bifrost tests, jurisdiction-map.md, live DB contents (motion count unknown), campaign rulings beyond the one above.

## Part 2: Prior art

## 1. Quorum and close timing

Apache (https://www.apache.org/foundation/voting.html) [P]
- Votes should generally run at least 72 hours so all time zones can take part.
- Lazy consensus = silence gives assent; not usable for code changes under review-then-commit unless declared at vote start.
- Types: procedural = simple majority (more +1 than -1); code modification = consensus approval (3 +1, no -1) unless lazy consensus declared; release = majority approval (3 binding +1, more +1 than -1).
- Veto only on code modifications: a -1 needs technical justification, else invalid; it blocks until withdrawn. Releases cannot be vetoed.
- Binding votes: PMC members; others advisory. Only explicit votes count.
- Page does not define "lazy majority" or "2/3 majority" (those terms are in other ASF docs, not checked).

IETF rough consensus, RFC 7282 (https://www.rfc-editor.org/rfc/rfc7282.html) [P]
- Explicitly not voting; the chair judges whether every technical objection was considered and answered; call is appealable.
- Named failure modes: capitulation (objector gives up unanswered), horse-trading, vote stuffing (recruiting voices to swamp objections), polls treated as decisions, ignored objections.

Loomio (https://www.loomio.com/docs/en/user_manual/polls/vote_share_requirements, https://help.loomio.com/en/user_manual/polls/quorum) [P/S]
- Quorum is optional, a percentage of eligible voters; set per poll or per poll template.
- Vote-share requirements are separate: threshold measured against eligible voters or votes cast; weighted voting uses weight instead of headcount; a Block option with "no more than 0%" lets one block vote prevent passage.
- Docs example: 5 eligible, 75% agreement of eligible plus 60% quorum; 2 agreeing votes (40% of eligible) fail.
- Closing: deadline can be extended or proposal closed early; no built-in minimum open period found; group-written rules vary (one group: >=50% of reps participate, open two weeks) (https://www.loomio.com/d/tmaQYcM5/proposed-changes-to-the-loomio-voting-process-for-e-nable-proposals) [S].

Debian constitution (https://www.debian.org/devel/constitution) [P]
- GR: discussion period min 2 weeks, max 3 (App. A.1.1); voting period 2 weeks, Leader may vary by 1 week (4.2.3); quorum 3Q where Q = half sqrt(number of Developers); casting vote for Leader; default option "None of the above" (4.2.4); ranked ballot, Condorcet/Schwartz-set counting (App. A.5).
- Sponsorship needed to propose a GR: K = min(Q, 5) (4.2.1, 4.2.7). 2K sponsors can put a Leader/Delegate decision on hold (4.2.2).
- Supermajorities: 3:1 for constitutional amendments and foundation documents (4.1.2, 4.1.5.3); 2:1 to override a Technical Committee decision (4.1.4).

Robert's Rules small boards [S]
- Default quorum is a majority of the membership unless bylaws say otherwise (https://www.dummies.com/article/business-careers-money/business/general-business/roberts-rules-for-defining-a-quorum-189712, https://mtas.tennessee.edu/system/files/mrln/mknowledge/main/Quorum_Hodge_2018_public_legop.pdf).
- "Small board" relaxed procedure applies to bodies of about 12 or fewer (https://www.wv-wmd.org/polity/smallboards.html). The search did not confirm any finer threshold.

Decidim (https://docs.decidim.org/en/develop/admin/components/proposals) [S]
- Proposals component settings include per-participant vote cap, minimum votes per user (below it votes do not count), acceptance threshold per proposal, and an option to keep collecting votes after threshold. Version-dependent. Decidim's closing/quorum semantics for formal decisions were not found.

Low-turnout evidence (DAOs, large N, not small groups):
- Average DAO participation 6.3% (review, source study unnamed) and ~17% of voting tokens delegated, clustered on popular delegates, across 18 Ethereum DAOs (https://www.frontiersin.org/journals/blockchain/articles/10.3389/fbloc.2025.1598283/epub, https://arxiv.org/pdf/2510.05830) [S].
- Feichtinger, Fritsch, Vonlanthen, Wattenhofer, "The Hidden Shortcomings of (D)AOs", 21 DAOs: high voting-right concentration (https://arxiv.org/pdf/2302.12125v2.pdf) [S].
- Compound Proposal 289 (July 2024) passed 682,191 to 633,636 with <7% of COMP supply; an earlier proposal 279 failed with 88% against; reported as an alleged governance attack, resolved by a negotiated cancellation (https://thedefiant.io/news/defi/compound-governance-attack-reveals-inherent-vulnerabilities-of-daos, https://www.theblock.co/post/307943/24-million-compound-finance-proposal-passed-by-whale-over-dao-objections, https://coindesk.com/business/2024/07/30/comp-token-rises-as-whale-backs-down-on-supposed-governance-attack-on-compound) [S].
- No source located that specifically studies stalled/quorum-busting motions in groups of ~10-20. Not found, not proven absent.

## 2. Vote weighting; mixed human/AI voting

One-member-one-vote vs weighted:
- Beanstalk (Apr 2022): flash-loaned voting power passed BIP-18/19 and drained ~$76M actually stolen (headline $182M); causes cited: no flash-loan-resistant vote measurement, no execution delay (https://www.bleepingcomputer.com/news/security/beanstalk-defi-platform-loses-182-million-in-flash-loan-attack/, https://rekt.news/beanstalk-rekt, https://www.merklescience.com/blog/hack-track-analysis-of-beanstalk-flash-loan-attack) [S].
- OpenZeppelin Governor: voting power read from a past snapshot (ERC20Votes checkpoints), voting delay lets members exit before the snapshot matters; quorum as a fraction of supply at snapshot, only For+Abstain count (https://docs.openzeppelin.com/contracts/5.x/governance) [P].

Quadratic voting:
- Lalley and Weyl, "Quadratic Voting: How Mechanism Design Can Radicalize Democracy" (https://papers.ssrn.com; also https://bfi.uchicago.edu/wp-content/uploads/2016-14-1.pdf); Posner and Weyl 2017 survey (https://www.Gwern.net/doc/economics/mechanism-design/quadratic-voting/2017-posner.pdf) [S].
- Weyl argues collusion/fraud is deterred except at very large scale (https://www.microsoft.com/en-us/research/?p=893088) [S]; Rivest's note argues QV is inherently susceptible to collusion [S].
- Sybil/wallet splitting defeats the quadratic cost without proof of personhood (https://www.spark.money/glossary/quadratic-voting) [S].
- "Concave is the New Linear: The Impossibility of Anti-Plutocratic DAO Governance" (https://arxiv.org/pdf/2605.18990) [A, 2026]: title states an impossibility claim; body not read.

AI agents in governance (2025-2026):
- DAO-AI, arXiv 2510.21117 (https://arxiv.org/html/2510.21117v2) [A]: multi-agent analysis of proposals/forum/voting; reports interpretable, auditable signals; agentic AI as decision-maker study.
- QOC DAO, arXiv 2511.08641 (https://arxiv.org/html/2511.08641v1) [A]: roadmap human-only -> agents simulate stakeholders with humans holding final authority -> AI-driven DAO.
- "Who Controls the Collective AI Vote? Real-World Evidence and Adversarial Testing of Multi-Agent Delegation in DAO Governance" (https://neurips.cc/virtual/2026/165281) [A]: several visible agents may share one actor controlling instructions, evidence, software, signing or reversal; combines analytical model, public-record audit of three deployed systems, tests of three voting rules, open-model replay; reports separating untrusted proposal text from governing instructions cut targeted vote changes in one fixed model; controller separation reduced common-input attacks but not direct compromise of one agent; layered safeguards lowered all four channels.
- Blockful ENS AI Delegate experiment: agents on own servers vote from bias prompt + DAO constitution + proposal (https://blockful.notion.site/AI-Delegate-8296ff74623a4078b1ab7e7536adfabf) [S].
- Arbitrum RFC on "Agentic Governance Pivot": community asked for how agents work and a public vote-history dashboard (https://forum.arbitrum.foundation/t/rfc-proposal-to-adjust-the-voting-power-of-the-arbitrum-community-pool-ratifying-the-agentic-governance-pivot/29280/11) [S].
- Epistemic delegation paper mentions a possible block of AI "Condorcet agents" as imagination-stage (https://arxiv.org/pdf/2505.04136) [S].
- Trustless Autonomy, arXiv 2505.09757: interview study, governance dilemma of unreliable agents (https://www.arxiv.org/pdf/2505.09757) [S].
- No study found of small mixed human/AI-agent boards voting on operational settings of their own system.

## 3. Sybil and correlated voters

- Kohli, "Nine Judges, Two Effective Votes: Correlated Errors Undermine LLM Evaluation Panels", arXiv 2605.29800 (https://arxiv.org/abs/2605.29800; Apple ML research page https://machinelearning.apple.com/research/correlated-llm-evaluation-panels) [P abstract]: 9 frontier LLMs from 7 families on three NLI datasets carry ~2 independent votes (Kish n_eff); panel accuracy 8-22 points below independent-voting expectation; best single judge matches or beats panel; aggregation methods close at most 11% of the gap; holds across prompts, temperature, CoT, a pairwise-preference task. Abstract does not split same-family vs cross-family; narrow task domain; preprint, review status unverified.
- STRIVE, arXiv 2608.04567 (https://arxiv.org/pdf/2608.04567) [S]: failing-group jury 33% vs best member 47%; some items missed by every configuration.
- Theory: Condorcet jury theorem assumes independent voters; Austen-Smith and Banks (1996) showed independence is necessary for the aggregation benefit (cited in Kohli) [S]. Explainer: https://towardsdatascience.com/your-six-ai-models-are-really-two-the-math-of-correlated-mistakes/ [S].
- Controller concentration across visible agents: the NeurIPS "Who Controls..." abstract above [A].
- IETF names vote stuffing as a failure mode of voting-style consensus (RFC 7282, above).
- Compound 289: delegated voting power concentrated in one delegate authored successive proposals (sources in section 1) [S].
- Not found: any study of one operator running several personas on the same base model as voters specifically (closest: Kohli, NeurIPS abstract).

## 4. Delegated or tiered authority

Apache (voting.html above) [P]: PMC binding; non-PMC advisory; decision type determines threshold; code vetoes need technical justification.

Rust:
- RFC 1068 governance (https://github.com/rust-lang/rfcs/blob/master/text/1068-rust-governance.md) [P via summary]: subteams own RFCs in their area (shepherd, accept/reject, policy on what needs an RFC); core team sets direction, creates/shuts subteams; consensus is the norm; for minor disputes shepherd or leader decides; deep disagreement: leader decides after consulting core team; final comment period one week in that RFC text; new technical arguments raised in FCP trigger another comment period; each subteam sets its own rules for when an RFC is required.
- Compiler FCP page (https://forge.rust-lang.org/compiler/proposals-and-stabilization.html) [P]: FCP needs team sign-off, then a ten-day wait; outstanding concerns block completion (`@rustbot concern` / `resolve`), countdown restarts when concerns resolve; FCP can be skipped for easily reversed MCPs. The page did not describe the disposition (merge/close/postpone) or majority-of-team checkbox mechanism; those were not verified.

Debian (constitution above) [P]:
- Individual developers decide their own work (3); Leader delegates (5.1.1, 8); delegation irrevocable once a specific decision is made (5.1.1); Leader cannot override a Delegate's made decision (8.2); GR can override Leader/Delegate (4.1.3) and TC (2:1, 4.1.4); TC decides last-resort, overrule Developer by 3:1 (6.1.4, 6.3.5); Secretary runs votes and interprets (7.1).

Kubernetes KEP (https://www.k8s.dev/resources/keps/5241/, https://kubernetes.io/blog/2020/08/21/moving-forward-from-beta/) [S]: owning-SIG approvers move a KEP provisional -> implementable; significant later changes need every approver; graduation criteria are written into the KEP; stages alpha (off by default), beta (usually on, can be disabled), GA (always on).

Loomio: block option as per-option veto (above).

## 5. Binding a decision to a parameter change

- Compound Governor (https://docs.compound.finance/v2/governance/, https://wiki.tally.xyz/docs/compound-governor) [S]: passed proposal queued in a Timelock, executable after 2 days; voting delay and period are themselves parameters changeable through governance; a Pause Guardian can act without a vote; Olympus's Bravo deployment adds a veto guardian who may veto any time before execution (https://docs.olympusdao.finance/main/contracts/docs/src/external/governance/GovernorBravoDelegate.sol/contract.GovernorBravoDelegate) [S].
- OpenZeppelin Governor + TimelockController [P]: delay "allows users to exit the system if they disagree with a decision before it is executed"; roles proposer (the Governor only), executor (may be anyone), canceller, admin (to be renounced after setup); extra proposers/cancellers flagged as a risk of bypassing or blocking governance; optional proposal threshold limits who can propose. Guardian extension lets a guardian cancel at any stage (https://docs.openzeppelin.com/contracts/api/governance) [S].
- Beanstalk had no execution delay (section 2 sources) [S].
- MakerDAO Governance Security Module delay and executive-vs-poll split: fetch redirected to https://developers.skyeco.com/ and was not read; not verified here.
- Compound 290 plan: team multisig deploying a new Governor to strip a delegate's power (reported intent, later cancelled) (sources in section 1) [S].
- Kubernetes: parameters change via feature gates whose default flips at KEP graduation stages, decided by SIG approvers, not vote (section 4) [S].
- Feature-flag change-control (LaunchDarkly approvals, etc.): not searched.

## Not covered or unverified
- Maker GSM, ASF "lazy majority"/2:3 definitions, Rust FCP disposition and majority-checkbox rule, Decidim formal-decision close semantics, feature-flag approval workflows, full text of every [A] paper, same-family LLM correlation numbers, anything on stalled-motion rates in small boards.
