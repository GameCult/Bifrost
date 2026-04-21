# AGENTS.md

## Repo Purpose

- Bifrost is the GameCult member alpha platform for tasks, governance, contributor accounting, and operational legibility.
- It sits under the Yggdrasil infrastructure umbrella and should connect GameCult services and workflows without importing crypto ideology or platform theater.

## V1 Non-Goals

- no Ethereum, crypto rails, wallets, or DAO framing
- no tokenized governance
- no payout execution from the app
- no public/open signup community launch
- no separate SPA frontend in v1

## Locked Product Decisions

- product name: `Bifrost`
- v1 audience: invite-only member alpha
- auth model: GitHub sign-in only
- membership model: invite + approval gate before active participation
- task sources: GitHub issues plus internal tasks
- governance model: app-native motions and voting
- payout model: internal ledger first, with admin-reviewed payout proposal batches only

## Locked Technical Defaults

- stack: ASP.NET Core 8 LTS
- UI: Razor Pages + HTMX
- database: PostgreSQL
- hosting target: `bifrost.gamecult.org` on Yggdrasil
- deployment pattern: nginx + systemd + localhost-only Postgres, matching `E:\Projects\gamecult-ops`

## Naming Defaults

- app/repo/workspace: `Bifrost`
- public hostname: `bifrost.gamecult.org`
- infrastructure/server context: `Yggdrasil`

## Ops Assumptions

- follow the GameCult ops pattern already documented in `E:\Projects\gamecult-ops`
- prefer one web app process for v1
- keep Postgres bound to localhost on the host
- add deployment runbooks to `gamecult-ops` when app work begins

## Working Notes

- Read existing docs before reopening settled product decisions.
- Keep implementation boring where possible; the product idea is ambitious enough already.
- If a question is about trust, accountability, or payout fairness, prefer auditability over automation.
- Before debugging UI/layout, inspect the live DOM and computed styles instead of guessing from templates or CSS intent.

## Starting Point For The Next Session

Read these files first:

1. `AGENTS.md`
2. `README.md`
3. `docs/bifrost-mvp-plan.md`
4. `docs/context.md`

Then start the first implementation milestone:

- GitHub auth
- membership gating
- project and member models
- work items
- motions
- ledgers
