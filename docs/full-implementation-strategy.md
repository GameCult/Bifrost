# Bifrost Full Implementation Strategy

This document aligns the current Bifrost application work with the broader GameCult labor platform concept documented in `E:\Projects\gamecult-site\GameCult\Docs\labor-platform.md`.

The goal is to keep Bifrost implementation boring, auditable, and staged while still preserving the actual GameCult model: contributor history, governance, work priority, reward pressure, contribution points, patron points, decay, and revenue share.

## Current Readiness

Bifrost is not ready for production deployment to `bifrost.gamecult.org` yet.

It is currently a strong first application slice, not an internet-facing alpha. The app can compile, render pages, enforce GitHub sign-in plus membership gating, and model the broad entities we need. It does not yet have the deployment and product completeness needed for a real Yggdrasil rollout.

### What Exists Already

- ASP.NET Core 8 Razor Pages app
- PostgreSQL-backed EF Core model plus initial migration
- GitHub OAuth wiring and startup config validation
- invite and approval gate with explicit member roles
- member profile editing plus patron or contributor tier snapshots
- project, work item, motion, member, and ledger UI
- patron support event recording with derived patron tier snapshot refresh
- work logs, work reviews, and completion flow
- GitHub App webhook ingestion for issue, pull request, and review sync
- health and readiness endpoints plus request logging
- deployment artifacts and runbooks in `gamecult-ops`
- integration tests covering console access, health/readiness, and GitHub webhook sync

### What Is Still Missing Before Deployment

- live GitHub App installation validation against the real `GameCult/Bifrost` repository
- deploy rehearsal on Yggdrasil with real secrets and a real PostgreSQL database
- backup and restore rehearsal for PostgreSQL and app secrets
- fuller workflow polish around blockers, archival, and review ergonomics
- production-safe payout proposal batching and finance review workflow
- external patron provider ingestion, full contribution point rules, scheduled decay jobs, and revenue share calculation

## Product Alignment With Labor Platform

The labor platform concept doc establishes the social and economic model. Bifrost should implement that model with a few v1 clarifications:

- keep GitHub sign-in only for Bifrost v1 even though the concept doc allows GitHub or GameCult accounts
- keep the app invite-only during alpha
- keep payout execution out of scope; only compute, review, and audit payout proposals
- treat contribution points, patron points, decay, and revenue share as first-class product systems, not spreadsheet afterthoughts
- preserve the Labor Platform loop where eligible demand raises priority/reward pressure, contributors claim valuable work, maintainers accept completion artifacts, and accepted work becomes contributor credit plus reward allocation
- preserve the concept doc's emphasis on continuous care work, not only ticket-shaped labor

## Systems That Must Exist

### Identity And Membership

- GitHub-authenticated `UserAccount`
- member profile with nickname, skills, availability, portfolio link, and internal payout metadata
- invitation, approval, suspension, and role history
- explicit admin, producer, maintainer, and finance capabilities

### Work And Review

- GitHub-backed work items and internal work items in one queue
- work categories, skill level, estimated hours, deadline, and review state
- transparent priority signals and reward pressure attached to work before it is claimed
- volunteer, assignment, review, completion, and approval transitions
- maintainer-accepted completion artifacts, usually GitHub PRs, before contributor credit/reward finalization
- support for continuous roles such as maintainer, producer, community manager, and social media work

### Governance

- management motions for policy, thresholds, and role changes
- project motions for work proposals and scoped decisions
- motion categories with category-specific thresholds taken from the labor platform concept:
  - bugs: 15%
  - cosmetics: 30%
  - balance changes: 40%
  - features: 50%
  - new content: 50%
  - fundamental design changes: 66%
- Discord-native and Reddit-native voting or priority prompts should eventually write into the same governance/priority records as the web app, after Heimdall-backed identity and capability claims exist.
- Reddit organizing threads in `r/GameCultOrg` may host public discussion and Persona-authored prompts, but Reddit reactions are evidence until Bifrost commits a linked vote or priority signal.

### Accounting

- append-only ledger entries for patron credit, contribution credit, nominal compensation, decay, and adjustment activity
- payout proposal batches that stay internal and review-driven
- audit events for every override, approval, and manual correction

## Points, Tiers, Decay, And Revenue Share

These concepts need a proper implementation strategy rather than a single `Points` field.

### Patron Points

Patron points are now derived from support events and support state, not edited freehand as a main workflow. Manual patron tier edits remain an admin override/fallback, not the normal voting-power owner.

Implementation requirements:

- store recurring support state and historical donation events separately: implemented as `PatronSupportEventKind.RecurringSupportSnapshot` and `OneTimeDonation`
- calculate patron points from current recurring support plus historical donations: implemented by `PatronageService`
- apply the concept-doc rule that historical donations are halved after one month and then continue decaying: implemented for effective patron point calculation
- expose both raw support history and current effective patron points: implemented on the ledger surface
- ingest Patreon/Heimdall provider events into the same service path: implemented through `/heimdall/patron-support/events`
- ingest PayPal/Heimdall checkout, subscription, refund, reversal, and chargeback facts into the same service path: implemented as provider metadata plus support adjustments

External provider support facts are not payment webhooks in Bifrost. Heimdall owns provider webhook verification and account linking. Bifrost accepts only Heimdall-signed support facts, resolves the linked `HeimdallAccountId`, deduplicates by provider event id, records support through `PatronageService`, and refreshes the derived patron tier.

### Contributor Points

Contributor points should be granted from approved work outcomes and approved continuous-role allocations.

Implementation requirements:

- track estimated hours, skill level, category, and approval outcome on work items
- store both the rule inputs and the awarded point transaction
- keep a small automatic starter allotment for new contributors
- split contributor points into:
  - global contributor points
  - project-specific contributor points

### Decay

Decay should be implemented as an auditable ledger process, not a destructive rewrite of history.

Implementation requirements:

- run a scheduled weekly decay job
- apply 1% weekly decay on decaying historical balances, rounded down
- do not decay project-specific contribution points
- persist each decay action as its own ledger transaction
- show members both lifetime totals and current effective totals

### Tiers

Tiers should be computed from effective point balances and snapshot when needed for voting or payout decisions.

Patron ladder:

- Bronze: 10 points
- Silver: 100 points
- Gold: 1,000 points
- Platinum: 10,000 points
- Unobtanium: 100,000 points

Contributor ladder:

- Postulate: 10 points
- Initiate: 100 points
- Novice: 1,000 points
- Adept: 10,000 points
- Master: 100,000 points

### Revenue Share

Revenue share should be implemented as proposal generation plus statements, not direct disbursement.

Implementation requirements:

- record revenue events by project and period
- compute the labor platform split:
  - one third by total contribution points
  - one third by contribution points for the revenue-earning project
  - one third reserved for budget, expenses, and future development
- produce an admin-reviewed `RevenueShareBatch` or equivalent payout proposal artifact
- keep every line item explainable from the underlying point ledgers and revenue event inputs

## Recommended Delivery Phases

### Phase 0: Deployable Foundation

Goal: make the current slice safe to stand up on Yggdrasil as a private environment.

Deliver:

- initial migrations
- bootstrap seed path for first admin and baseline data
- config validation for OAuth, database, and host settings
- health endpoint and startup checks
- structured logging
- deployment runbook plus nginx and systemd config in `gamecult-ops`
- backup and restore runbook

Exit criteria:

- clean host can provision database, apply migrations, start the app, and survive restart
- private staging deployment works behind nginx

### Phase 1: Member Alpha Core

Goal: make invite-only member operations actually usable.

Deliver:

- full member profile fields
- explicit role management
- GitHub App integration and issue or pull request sync
- work item lifecycle with deadlines and review state
- membership and audit admin pages
- better authorization coverage and test depth

Exit criteria:

- invited members can sign in, browse work, volunteer, be assigned, and participate in motions
- admins can review membership and core platform activity

### Phase 2: Labor And Points Engine

Goal: encode the labor model itself.

Deliver:

- point transaction ledger model
- contributor point award rules
- continuous role accounting
- patron support history and patron point calculation
- Reddit account linking for discussion attribution, without using Reddit karma or upvotes as patron voting weight
- weekly decay job
- tier snapshots and effective voting weight calculation from tier state

Exit criteria:

- every effective point balance can be reconstructed from ledger events
- point changes are auditable and test-covered

### Phase 3: Governance And Revenue Share

Goal: turn Bifrost into the real internal operating layer.

Deliver:

- management and project motion templates
- category-specific thresholds
- revenue event ingestion
- revenue share calculation
- payout proposal batch generation
- member-facing statements explaining how shares were derived

Exit criteria:

- governance and payout recommendation flows can run without resorting to external spreadsheets

### Phase 4: Continuous Care Work And Refinement

Goal: support real organizational labor that does not map cleanly to single tickets.

Deliver:

- recurring role assignments
- periodic care-work credits
- better dashboards and reporting
- exception handling for disputes, reversals, and manual corrections

Exit criteria:

- maintainers, producers, community, and social labor are accounted for without pretending only GitHub tickets matter

## Domain Additions To Plan For

The current domain is a good start, but the following models should be expected:

- `WorkItemCategory`
- `WorkReview`
- `WorkCompletion`
- `PrioritySignal`
- `RewardAllocation`
- `CompletionArtifact`
- `PointTransaction`
- `PointBalanceSnapshot`
- `PatronSupportEvent`
- `DecayRun`
- `TierSnapshot`
- `RevenueEvent`
- `RevenueShareBatch`
- `RevenueShareLine`
- `RoleAssignment`
- `RoleContributionWindow`

## Deploy Decision

The current recommendation is:

- do not deploy publicly yet
- do deploy to a private or admin-only staging environment after Phase 0 work is complete
- treat public or member-alpha rollout as gated on Phase 1 plus the core parts of Phase 2

That keeps the rollout honest. We can stand something up on Yggdrasil soon, but it should be a controlled internal environment until the labor and accounting rules are implemented well enough to trust.
