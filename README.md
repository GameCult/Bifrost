# Bifrost

Bifrost is the planned GameCult member, governance, and labor platform running under the Yggdrasil infrastructure umbrella. It exists to connect projects, contributors, motions, ledgers, and operational decisions in one place instead of leaving them scattered across GitHub, docs, chat, and human memory.

## Status

This repository is in planning/bootstrap only. It is intentionally a docs-first repo shell with no ASP.NET application code yet.

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

The first implementation milestone should cover:

- authentication and membership gating
- project and member models
- work items from GitHub and internal sources
- motions and votes
- internal ledgers and payout proposal batches

## Read First

Before implementation work starts in a new session, read these files in order:

1. [AGENTS.md](E:\Projects\Bifrost\AGENTS.md)
2. [docs/bifrost-mvp-plan.md](E:\Projects\Bifrost\docs\bifrost-mvp-plan.md)
3. [docs/context.md](E:\Projects\Bifrost\docs\context.md)
