# Bifrost Verse Service Contract

Bifrost is the GameCult labor, governance, and governed-public-crossing service
for the Verse. Its durable service truth must be typed CultCache state or a
CultCache-compatible store with a `.cc` witness/export. Its presentation truth
must be Eve/CultUI DSL, preferably `gamecult.eve.surface.v1`, published through
CultMesh for Odin discovery.

The existing Razor Pages app, Discord mirrors, bridge CLI receipts, and future
web/native/TUI clients are lowerings. They may render, command, and inspect
Bifrost state. They do not own Bifrost's canonical product or operator surface.

## Owner Map

| Concern | Owner | Inputs | Outputs |
| --- | --- | --- | --- |
| Labor and governance meaning | Bifrost | members, patrons, contributors, motions, work items, votes, priority, rewards, maintainer decisions | typed Bifrost state, work/motion/ledger receipts, Eve product surfaces |
| Governed public crossings | Bifrost | Heimdall claims, Bifrost policy, source topic/work item, target surface | bridge action state, execution receipts, operator surface events |
| Identity, grants, consent, revocation | Heimdall | OAuth providers, linked accounts, operator grants | signed claims consumed by Bifrost |
| Storage and sync primitives | CultCache/CultNet/CultLib | Bifrost document types and payloads | `.cc` stores, snapshots, raw document updates |
| Service discovery and surface aggregation | Odin | CultMesh namespaces, schema catalogs, service registrations | discoverable Verse routes and surface indexes |
| Room observation and Face cognition | VoidBot/repo Faces | Discord context, repo state, Persona state | proposed Bifrost topics, comments, dispatch requests |
| Rendering | Eve/browser/native/TUI/Discord | `gamecult.eve.surface.v1` compositions, typed state references | product/operator UI lowerings, not canonical state |

## Durable State And Witnesses

Bifrost may keep PostgreSQL as the transactional store for the current ASP.NET
member alpha, but durable Verse state must have typed CultCache witnesses.

Current witnesses:

- `.bifrost/governance-threads.cc` stores `bifrost.governance.topic` and
  `bifrost.governance.topic-comment` documents.
- `.bifrost/agent-transport.cc` stores
  `bifrost.agent-transport.update-request` documents and can emit raw CultNet
  snapshots.

Required witness path for the hosted service:

- every Bifrost-owned work item, motion, ledger entry, bridge action, receipt,
  member capability snapshot, and surface publication must either live directly
  as typed CultCache documents or be exported as typed `.cc` witnesses from the
  transactional store;
- witness exports must carry stable ids, source owner, schema id, generated-at
  timestamp, and enough provenance to prove which Postgres row, external receipt,
  or provider event produced the document;
- JSON remains acceptable for schema publication, protocol debug, migration
  fixtures, webhook cache, and xenos-boundary exchange. JSON must not become
  load-bearing Bifrost state.

## CultMesh Namespaces

Bifrost publishes under these CultMesh namespaces:

- `gamecult.bifrost.service` for service registration, health, build/version,
  schema catalog, and command capability discovery.
- `gamecult.bifrost.governance` for motions, topics, comments, votes,
  approvals, objections, and policy receipts.
- `gamecult.bifrost.work` for projects, work items, claims, review state,
  estimates, time logs, blockers, completion artifacts, and maintainer
  acceptance.
- `gamecult.bifrost.economics` for patron pressure, contributor credit, ledger
  entries, payout proposal batches, and revenue-share inputs.
- `gamecult.bifrost.bridge` for GitHub, Discord, CultNet/CultCache, and future
  collaboration crossings plus receipts.
- `gamecult.bifrost.surface.product` for member/patron/contributor/user-facing
  Eve product surfaces.
- `gamecult.bifrost.surface.operator` for deploy, readiness, queue, witness,
  bridge, schema, and migration operator surfaces.

Odin discovers Bifrost through the service namespace, then indexes the schema
catalog and surface namespaces. Odin does not scrape Razor pages as truth.

## Eve Product And Operator Surfaces

Product surfaces are the canonical interface compositions for:

- account/membership status;
- patron and contributor standing;
- project and work-item boards;
- motions, topic threads, votes, approvals, and objections;
- work claims, review, completion, receipts, and contributor credit;
- bridge receipts and public handoff targets.

Operator surfaces are the canonical interface compositions for:

- readiness, build/version, config validation, and deploy target;
- CultCache witness freshness and export errors;
- CultMesh publication health and Odin discovery status;
- bridge queues, failed crossings, retry/cancel controls, and receipt gaps;
- schema versions, migration state, and Postgres-to-`.cc` witness drift;
- Discord mirror health, including fail-closed unmirrored governance writes.

The current Razor Pages app lowers product surfaces into browser UI. Existing
health/readiness endpoints lower operator state into HTTP probes. Both should
be fed by the same typed service state that backs Eve surfaces.

## Nested Verses

Bifrost exposes nested Verses where local ownership matters:

- Patron Verse: patron identity projection, priority pressure, pledge/reward
  influence, receipts, and standing.
- Work Verse: work items, claims, estimates, review, blockers, completion, and
  maintainer acceptance.
- Motion Verse: motions, topic threads, comments, votes, approvals, objections,
  policy decisions, and dispatch promotion.
- Project Verse: project membership, repository links, maintainer authority,
  work boards, and public receipts.
- Account Verse: Heimdall-linked actor projections, membership state,
  contributor tier snapshots, grants consumed by Bifrost, and audit trails.

Nested Verses do not own separate databases. They are CultMesh/Eve projections
of Bifrost-owned typed state with local command affordances and clear handoff
to Heimdall, project maintainers, or bridge executors where authority leaves
Bifrost.

## Migration Order

1. Catalog current Bifrost state owners: EF/Postgres tables, `.cc` stores,
   bridge receipts, Discord mirrors, webhook cache, and app readiness state.
2. Define schema ids for missing Bifrost documents: work item, motion, vote,
   ledger entry, bridge action, bridge receipt, member capability snapshot,
   service registration, and Eve surface publication.
3. Add Postgres-to-CultCache witness export for alpha entities before replacing
   the transactional store.
4. Publish `gamecult.bifrost.service` and schema catalog through CultMesh.
5. Publish operator surfaces first: readiness, witness freshness, bridge queue,
   failed crossings, and migration drift.
6. Publish product surfaces for account, project, work, motion, patron, and
   contributor views as `gamecult.eve.surface.v1`.
7. Lower the existing Razor Pages app from the Eve/product surface contract
   where practical, leaving direct Razor composition only as transitional UI.
8. Route Discord native interactions through Bifrost commands that update typed
   state and emit Discord receipts from the same commit path.
9. Let Odin discover Bifrost surfaces and nested Verses through CultMesh, then
   retire any scraping or private summary adapters.

## Demotion Line

Demoted surfaces and stores:

- Razor Pages views are no longer the canonical Bifrost presentation owner; they
  are browser lowerings of Bifrost product/operator surfaces.
- `/healthz`, `/readyz`, and any future HTTP dashboard/status JSON are probes or
  xenos-boundary exports, not service truth.
- `.bifrost/discord-webhook-cache.json` is webhook address cache only; it does
  not prove governance, speech authority, or receipt completion.
- `.bifrost/agent-dispatch/**/request.json`, `dispatch.json`, `result.json`, and
  app-server protocol JSON are local dispatch/protocol artifacts. They may be
  evidence attached to typed receipts, but they cannot decide request status,
  work authority, or bridge success.
- Discord `#bifrost` messages are mirrors and linked-user input surfaces. They
  are not canonical governance threads unless Bifrost commits the corresponding
  topic/comment/vote document.

The live owner is Bifrost typed state. Everything else either lowers it,
commands it, witnesses it, or provides external evidence for a typed receipt.
