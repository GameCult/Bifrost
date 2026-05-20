# Bifrost

Bifrost is the planned GameCult member, governance, and labor platform running under the Yggdrasil infrastructure umbrella. It exists to connect projects, contributors, motions, ledgers, and operational decisions in one place instead of leaving them scattered across GitHub, docs, chat, and human memory.

## Status

This repository now includes a viable alpha foundation slice of the ASP.NET Core app:

- Razor Pages app under `src/Bifrost.Web`
- PostgreSQL-backed EF Core model with an initial migration under `src/Bifrost.Web/Data/Migrations`
- GitHub OAuth sign-in plus invite/approval-based membership gating
- explicit member roles and admin-managed patron or contributor tier snapshots
- shared work board with estimates, actual time logs, review flow, and GitHub issue or PR links
- member-facing motions with category thresholds aligned to the labor-platform doc
- health and readiness endpoints plus startup config validation
- GitHub App webhook ingestion for issue, pull request, and review sync
- local bridge tooling for agent-owned GitHub draft PRs and Discord posts
- Docker image and local Compose stack for containerized smoke testing
- xUnit integration tests under `tests/Bifrost.Web.Tests`
- matching deploy artifacts and runbooks in `E:\Projects\gamecult-ops`

It is closer to deployable now, but it is still not a finished internet-facing member alpha. The current state is suitable for a private deploy candidate on Yggdrasil while we finish the remaining operational hardening and post-MVP economics work.

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
- deployment model: GHCR image + Docker Compose behind nginx, with the older systemd path kept as rollback until the container path is boring

## Next Build Target

The next build target is to deepen the alpha candidate by adding:

- GitHub App installation/admin guidance and live webhook verification against the real repo
- richer workflow polish around work review, blocking, and closure ergonomics
- payout proposal batch generation and tighter ledger approval workflow
- staged deploy validation on Yggdrasil, including backup/restore rehearsal
- post-MVP economics automation for point transactions, decay, and revenue-share calculation

For the full staged roadmap, including contribution points, revenue share, patron or contributor decay, and labor-platform alignment, see [docs/full-implementation-strategy.md](E:\Projects\Bifrost\docs\full-implementation-strategy.md).

## Local Dev Notes

- set `GitHubOAuth:ClientId` and `GitHubOAuth:ClientSecret` before using sign-in
- set `Bootstrap:AdminGitHubLogins` with at least one GitHub login for the first active admin path
- default connection string points at local PostgreSQL
- build with `dotnet build Bifrost.slnx`
- test with `DOTNET_ROLL_FORWARD=Major dotnet test Bifrost.slnx` if the machine only has the .NET 10 runtime installed
- container smoke test with `docker compose -f compose.local.yaml up --build`
- container health checks live at `http://127.0.0.1:5080/healthz` and `http://127.0.0.1:5080/readyz`

## Read First

Before implementation work starts in a new session, read these files in order:

1. [AGENTS.md](E:\Projects\Bifrost\AGENTS.md)
2. [docs/bifrost-mvp-plan.md](E:\Projects\Bifrost\docs\bifrost-mvp-plan.md)
3. [docs/full-implementation-strategy.md](E:\Projects\Bifrost\docs\full-implementation-strategy.md)
4. [docs/context.md](E:\Projects\Bifrost\docs\context.md)
