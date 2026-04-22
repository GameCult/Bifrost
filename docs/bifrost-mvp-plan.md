# Bifrost Member Alpha Plan

## Summary

- Create Bifrost as a separate GameCult app repo and deploy it on Yggdrasil.
- Build a member alpha, not a public launch.
- Use ASP.NET Core 8 with Razor Pages + HTMX and PostgreSQL.
- Support invite-only GitHub-linked members, GitHub-backed and internal work items, app-native governance, and internal ledgers.
- Do not include Ethereum, wallets, DAO mechanics, or payout execution in v1.
- Align the implementation with the labor platform model in `E:\Projects\gamecult-site\GameCult\Docs\labor-platform.md`.

## System Shape

- One ASP.NET web app process for v1.
- Server-rendered UI with HTMX for progressive interactions.
- PostgreSQL as the system of record.
- GitHub OAuth for login.
- GitHub App plus webhooks for issue and pull request lifecycle sync.
- Yggdrasil-hosted deployment behind nginx with systemd-managed app service.

## Core Models

- `User`: authenticated app identity linked to one GitHub account
- `MemberProfile`: profile, skills, availability, links, visibility
- `Membership`: invite state, approval state, active/suspended state, roles
- `Project`: GameCult project container for tasks, motions, and ledgers
- `WorkItem`: shared model for `GitHubIssue` and `Internal` task sources
- `VolunteerClaim`: member intent to work on an item
- `Assignment`: approved responsibility for a work item
- `Motion`: `Management` or `Project`
- `Vote`: per-member vote tied to effective voting weight
- `LedgerEntry`: immutable patron/contributor/accounting record
- `PayoutProposalBatch`: admin-reviewed payout recommendation batch only
- `AuditEvent`: append-only activity and override log

## Product Rules

- Invite-only member alpha.
- GitHub sign-in only in v1.
- Authentication is separate from membership approval.
- Work items may come from GitHub issues or internal platform-native tasks.
- Voting is app-native and off-chain.
- Voting weight derives from patron tier plus contributor tier.
- Compensation is modeled internally, not executed by the platform.
- Human approval remains in the loop for governance-sensitive and payout-sensitive steps.

## Labor Platform Alignment

- Contribution points and patron points are separate systems that both matter to tiering and governance.
- Effective balances decay over time where the labor platform rules call for decay.
- Project-specific contribution points stay distinct from global contribution points.
- Revenue share should be implemented as an internal calculation and review flow, not direct payout execution.
- Continuous care work such as maintainer, producer, and community labor must be accounted for alongside ticket-shaped work.

## Deployment Assumptions

- public hostname: `bifrost.gamecult.org`
- host: Yggdrasil
- app root target: `/srv/bifrost/app`
- env dir target: `/srv/bifrost/env`
- database: local PostgreSQL on Yggdrasil
- reverse proxy: nginx
- service supervision: systemd

## First Implementation Milestone

Build the minimum vertical slice for:

- GitHub authentication
- invite and membership gating
- project management
- work items from GitHub and internal sources
- volunteering and assignment flow
- motions and voting
- ledger entry creation
- payout proposal batch review without disbursement

## Test And Acceptance Scenarios

- invited GitHub user signs in and becomes an active member
- uninvited GitHub user cannot participate fully
- GitHub issue sync creates or updates a work item
- internal task can be created and assigned without GitHub backing
- member volunteers for a task and a producer/admin assigns it
- completed work generates ledger entries after approval
- motion creation, voting, closure, and threshold logic work correctly
- payout proposal batch can be generated from approved ledger data without sending money
- app survives restart on Yggdrasil without data loss

## Assumptions And Defaults

- Bifrost now has an initial application slice, but it is not yet production-ready for deployment.
- v1 optimizes for legibility, auditability, and workflow proof.
- Future sessions should treat this document and `AGENTS.md` as the durable source of truth for settled decisions.
- The full staged roadmap lives in `docs/full-implementation-strategy.md`.
