# Bifrost

Bifrost is the planned GameCult member, governance, and labor platform running under the Yggdrasil infrastructure umbrella. It exists to connect projects, contributors, motions, ledgers, and operational decisions in one place instead of leaving them scattered across GitHub, docs, chat, and human memory.

## Status

This repository now includes the first implementation slice of the ASP.NET Core app:

- Razor Pages app scaffold under `src/Bifrost.Web`
- PostgreSQL-backed EF Core domain model for members, projects, work items, motions, and ledgers
- GitHub OAuth wiring plus invite/approval-based membership gating
- initial member console pages for projects, work items, motions, ledger activity, and member approvals
- xUnit integration tests under `tests/Bifrost.Web.Tests`

It is not yet ready for production deployment. The current state is suitable for continued development and, after deploy-foundation work, an internal staging rollout.

## Chosen Stack

- ASP.NET Core 8 LTS
- Razor Pages + HTMX
- PostgreSQL
- GitHub OAuth for sign-in
- GitHub App for issue and pull request integration

## Alpha Scope

- invite-only member portal
- GitHub sign-in only
- GitHub-backed and internal work items
- app-native motions and voting
- internal ledgers for patronage, contribution, and payout eligibility
- no real payout execution in v1

## Hosting Target

- public app hostname: `bifrost.gamecult.org`
- infrastructure context: `Yggdrasil`
- deployment model: nginx + systemd + localhost Postgres, following the existing GameCult ops pattern

## Next Build Target

The next build target is to deepen the first implementation milestone by adding:

- database migrations and bootstrap seed/run instructions
- GitHub issue and pull request sync
- richer membership/admin role management
- work item completion and ledger approval flow
- payout proposal batch generation and review

For the full staged roadmap, including contribution points, revenue share, patron or contributor decay, and labor-platform alignment, see [docs/full-implementation-strategy.md](E:\Projects\Bifrost\docs\full-implementation-strategy.md).

## Local Dev Notes

- set `GitHubOAuth:ClientId` and `GitHubOAuth:ClientSecret` before using sign-in
- set `Bootstrap:AdminGitHubLogins` with at least one GitHub login for the first active admin path
- default connection string points at local PostgreSQL
- build with `dotnet build Bifrost.slnx`
- test with `DOTNET_ROLL_FORWARD=Major dotnet test Bifrost.slnx` if the machine only has the .NET 10 runtime installed

## Read First

Before implementation work starts in a new session, read these files in order:

1. [AGENTS.md](E:\Projects\Bifrost\AGENTS.md)
2. [docs/bifrost-mvp-plan.md](E:\Projects\Bifrost\docs\bifrost-mvp-plan.md)
3. [docs/full-implementation-strategy.md](E:\Projects\Bifrost\docs\full-implementation-strategy.md)
4. [docs/context.md](E:\Projects\Bifrost\docs\context.md)
