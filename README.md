# Bifrost

Bifrost is the planned GameCult member, governance, labor, and public-process platform running under the Yggdrasil infrastructure umbrella. It exists to connect projects, users, patrons, contributors, motions, ledgers, work requests, reward pressure, public receipts, and operational decisions in one place instead of leaving them scattered across GitHub, docs, chat, and human memory.

Bifrost also owns the governed transport layer for GameCult work that crosses public or collaboration protocols: GitHub proposal/review surfaces, Discord-native work and swarm interfaces, Reddit organizing threads in `r/GameCultOrg`, CultNet/CultCache intake packets, and future collaboration interfaces. That bridge role follows from the Labor Platform role; it is not a separate bot utility. See [docs/jurisdiction-map.md](E:\Projects\Bifrost\docs\jurisdiction-map.md).

## Status

This repository now includes a viable alpha foundation slice of the ASP.NET Core app:

- Razor Pages app under `src/Bifrost.Web`
- PostgreSQL-backed EF Core model with an initial migration under `src/Bifrost.Web/Data/Migrations`
- GitHub OAuth sign-in plus invite/approval-based membership gating
- explicit member roles and admin-managed patron or contributor tier snapshots
- patron support event recording with derived patron point summaries and tier refresh for voting weight
- shared work board with estimates, actual time logs, review flow, and GitHub issue or PR links
- planned priority/reward pressure loop where demand can raise a work item's value before a contributor claims it
- member-facing motions with category thresholds aligned to the labor-platform doc
- health and readiness endpoints plus startup config validation
- GitHub App webhook ingestion for issue, pull request, and review sync
- Heimdall-signed external patron support intake for Patreon and PayPal support facts
- Eve Motion Verse surface and command endpoint for governance participation, with Razor motion forms demoted to a transitional browser lowering
- local bridge tooling for agent-owned GitHub draft PRs and Discord posts
- local bridge tooling for Reddit self-posts in `r/GameCultOrg`, including Persona flair labels through the Bifrost Reddit app
- CultCache-backed governance topic threads for feature requests, discussion comments, Persona approvals, and dispatch promotion
- CultCache/CultNet-backed agent intake tooling for repo Persona update requests
- a Verse service contract for CultCache witnesses, CultMesh namespaces, and Eve/CultUI product and operator surfaces
- a read-only Eve provider advertisement export command for Bifrost Verse surface discovery
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
- internal ledgers for patronage, contribution, point evidence, and payout eligibility
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
- set `Bridge:LocalBridgeToken` to allow the transitional local bridge actuator to register and receipt governed actions through `/bridge/actions/*`
- set `BIFROST_BRIDGE_BASE_URL` and `BIFROST_BRIDGE_TOKEN` when `tools/bifrost-bridge.mjs` should round-trip every action through the app ledger before mutating GitHub, Discord, or Reddit
- the same bridge URL/token pair lets `tools/dispatch-agent-requests.mjs` record `/dispatch/runs/*` receipts for launched Codex work, so Bifrost keeps a live run trail even when a turn fails
- those hosted runtime receipt lanes are local-bridge-owned writes, not ordinary member-session writes: `/dispatch/runs/*`, `/transport/receipts`, and `/governance/receipts` now require the configured bridge token and do not inherit browser-user identity into agent activity rows
- those hosted runtime receipts also validate linkage instead of accepting free-floating text: dispatch runs require an existing request-lane receipt for the same request id, governance promotion receipts require a known promoted dispatch request, and request-lane rows cannot silently drift to a different repo or agent identity midstream
- GitHub bridge actions that claim Bifrost-owned provenance now validate that provenance too: a `bifrost_agent_transport_request` source must point at a known request-lane request for the same repo, and governance-backed bridge actions must point at a known Bifrost topic instead of a decorative source string
- governance-backed GitHub publication is also no longer allowed to jump the queue: a Bifrost governance topic must have been approved or promoted before it can justify a GitHub bridge action
- non-GitHub bridge activity is no longer allowed to drift off-book either: `discord-post`, `discord-dm`, and `reddit-post` now require `BIFROST_BRIDGE_BASE_URL` and `BIFROST_BRIDGE_TOKEN` by default, with `--allow-unreceipted-activity true` reserved for explicit local recovery and refused inside dispatched work
- Bifrost-dispatched Codex turns also inject Bifrost-owned GitHub gates through environment config: `git` and `gh` wrappers are prepended in `PATH`, a `pre-push` hook backs up raw Git pushes, raw `git push` stays blocked, and direct `gh` usage is limited to explicit read-only commands unless `tools/bifrost-bridge.mjs` authorizes the bridge-owned GitHub write path
- dispatched Codex turns now default to `workspace-write` instead of full machine access, and the app-server sandbox policy disables network access unless the operator explicitly chooses a broader sandbox
- `tools/agent-transport.mjs` also uses that bridge configuration to record `/transport/receipts` entries for queue, claim, release, and close events on the request lane
- `tools/agent-transport.mjs apply-snapshot` is also treated as a mutating import path now: it fails closed without the bridge receipt config and derives request-lane receipts from the imported state delta
- `tools/governance-threads.mjs` uses that same bridge configuration to record `/governance/receipts` entries for topic opens, comments, approvals, and promotions
- those agent activity scripts now fail closed without the bridge URL/token pair; `--allow-unreceipted-activity true` or `BIFROST_ALLOW_UNRECEIPTED_ACTIVITY=true` is the explicit operator-recovery hatch
- GitHub bridge mutations now fail closed without those values; `--allow-ungated-github true` is reserved for explicit operator recovery
- dispatched Codex turns now lock those recovery hatches too: local-only `--allow-ungated-github` and `--allow-unreceipted-activity` escapes are refused inside dispatched work so a worker cannot silently downgrade itself out of Bifrost receipts
- dispatched Codex turns also now scrub ambient GitHub auth state from the child environment: `GH_TOKEN` / `GITHUB_TOKEN` are cleared, `gh` gets an isolated config directory, `git` gets an isolated global config plus non-interactive credential settings, and terminal prompting is disabled before the worker starts
- set `BIFROST_REDDIT_CLIENT_ID`, `BIFROST_REDDIT_REFRESH_TOKEN`, and optionally `BIFROST_REDDIT_CLIENT_SECRET` before posting Reddit organizing threads
- set `Heimdall:PatronSupportIntakeSecret` before enabling Heimdall patron support intake in production
- default connection string points at local PostgreSQL
- build with `dotnet build Bifrost.slnx`
- test with `DOTNET_ROLL_FORWARD=Major dotnet test Bifrost.slnx` if the machine only has the .NET 10 runtime installed
- dry-run a Reddit organizing thread with `node tools/bifrost-bridge.mjs reddit-post --title "Thread title" --persona-name Bifrost --content "Thread body" --dry-run true`
- print the Eve provider advertisement with `node tools/provider-advertisement.mjs print`
- export the Eve provider advertisement witness with `node tools/provider-advertisement.mjs export --out .bifrost/provider-advertisement.cc`
- inspect the interactive Motion Verse surface at `/eve/governance/surface` while signed in as an active member
- container smoke test with `docker compose -f compose.local.yaml up --build`
- container health checks live at `http://127.0.0.1:5080/healthz` and `http://127.0.0.1:5080/readyz`

## Read First

Before implementation work starts in a new session, read these files in order:

1. [AGENTS.md](E:\Projects\Bifrost\AGENTS.md)
2. [docs/bifrost-mvp-plan.md](E:\Projects\Bifrost\docs\bifrost-mvp-plan.md)
3. [docs/jurisdiction-map.md](E:\Projects\Bifrost\docs\jurisdiction-map.md)
4. [docs/bridge.md](E:\Projects\Bifrost\docs\bridge.md)
5. [docs/verse-service-contract.md](E:\Projects\Bifrost\docs\verse-service-contract.md)
6. [docs/agent-transport.md](E:\Projects\Bifrost\docs\agent-transport.md)
7. [docs/reddit.md](E:\Projects\Bifrost\docs\reddit.md)
8. [docs/paypal-patronage.md](E:\Projects\Bifrost\docs\paypal-patronage.md)
9. [docs/full-implementation-strategy.md](E:\Projects\Bifrost\docs\full-implementation-strategy.md)
10. [docs/context.md](E:\Projects\Bifrost\docs\context.md)
