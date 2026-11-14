# AGENTS.md

## Repo Purpose

- Bifrost is the GameCult member alpha platform for tasks, governance, contributor accounting, and operational legibility.
- Bifrost also owns GameCult public-protocol transport for governed work crossings: GitHub proposal/review surfaces, Discord dispatch receipts, Reddit organizing threads, CultNet/CultCache intake packets, and future collaboration interfaces.
- It sits under the Yggdrasil infrastructure umbrella and should connect GameCult services and workflows without importing crypto ideology or platform theater.
- The transport role follows from Bifrost's Labor Platform role. Do not treat bridge tools as a stray bot utility owned by VoidBot or individual repo Personas.
- The product center is the governance/labor loop: users, patrons, members, and contributors express policy and work priorities; reward pressure rises around work that matters; contributors claim valuable work; maintainers accept completion artifacts; Bifrost credits the contributor and records the reward allocation.
- Discord and Reddit should become native Bifrost interfaces for Bifrost-scoped work, voting/priority prompts, reward discussion, maintainer notices, public organizing threads, and agent swarm routing. This does not make Bifrost responsible for generic room cognition, moderation judgment, archive retrieval, subreddit moderation taste, or Persona personality.

## V1 Non-Goals

- no Ethereum, crypto rails, wallets, or DAO framing
- no tokenized governance
- no payout execution from the app
- no public/open signup community launch
- no separate SPA frontend in v1

## Locked Product Decisions

- product name: `Bifrost`
- v1 audience: invite-only member alpha
- auth model: Bifrost-native identity registration, with GitHub and Heimdall-linked provider accounts attached as external identities
- membership model: invite + approval gate before active participation
- task sources: GitHub issues plus internal tasks
- governance model: app-native motions and voting
- payout model: internal ledger first, with admin-reviewed payout proposal batches only

## Locked Technical Defaults

- stack: ASP.NET Core 8 LTS
- UI: Razor Pages
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
- Treat `docs/jurisdiction-map.md` as the ownership map for Bifrost's bridge/governance/auth boundaries.
- Keep implementation boring where possible; the product idea is ambitious enough already.
- Push changes after every implementation pass.
- If a question is about trust, accountability, or payout fairness, prefer auditability over automation.
- If a question is about public work crossing GitHub, Discord, Reddit, CultNet/CultCache intake, or future collaboration surfaces, prefer Bifrost-owned requests, receipts, and routing over local helper sprawl.
- Before debugging UI/layout, inspect the live DOM and computed styles instead of guessing from templates or CSS intent.

## Starting Point For The Next Session

Read these files first:

1. `AGENTS.md`
2. `README.md`
3. `docs/jurisdiction-map.md`
4. `docs/bridge.md`
5. `docs/agent-transport.md`
6. `docs/reddit.md`
7. `docs/bifrost-mvp-plan.md`
8. `docs/full-implementation-strategy.md`
9. `docs/context.md`

Then start the first implementation milestone:

- GitHub auth
- membership gating
- project and member models
- work items
- motions
- ledgers

## Live Postgres tests

Most tests run on the in-memory EF provider, which enforces no unique index, transaction, concurrency token or trigger. The tests that need a real server (`tests/Bifrost.Web.Tests/AccountMergePostgresTests.cs`: commit atomicity, the `(MergeId, Seq)` unique index, the sweeper racing the saga, overlapping merges refused by the database, the one-current-Patron-snapshot index under racing restages, the intake journal `Seq`, the write gate's shared lock racing a merge's freeze and commit, the migration's roll-forward-only guard) run only when `BIFROST_TEST_PG` holds a connection string with no `Database=`, for a role that may create databases; otherwise they are skipped with that reason. Each test creates its own database, builds it through the real migrations and the class drops it, so point the variable at a disposable server:

```bash
BIFROST_TEST_PG='Host=127.0.0.1;Port=54329;Username=postgres' dotnet test tests/Bifrost.Web.Tests --filter FullyQualifiedName~AccountMergePostgresTests
```

## Account merges are roll-forward only

Once any `AccountMerges` row exists, do not deploy a build older than the `AddAccountMerges` migration, and do not revert that migration: an older build cannot read the `Merged` membership status and counts voided votes again. `Down` refuses while any merge row exists. Fix forward (`heimdall-body:ruling:merge-roll-forward-only`, `docs/account-merge.md` Rollback). Any deploy list that names a rollback must say so.
