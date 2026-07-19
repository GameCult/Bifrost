# Bifrost Verse Service Contract

Bifrost is the GameCult labor, governance, and governed-public-crossing service
for the Verse. Its durable service truth must be typed CultCache state or a
CultCache-compatible store with a `.cc` witness/export. Its presentation truth
must be Eve/CultUI DSL, preferably `gamecult.eve.surface.v1`, published through
CultMesh for Odin discovery.

The existing Razor Pages app, Discord mirrors, Reddit threads, bridge CLI
receipts, and future web/native/TUI clients are lowerings. They may render, command, and inspect
Bifrost state. They do not own Bifrost's canonical product or operator surface.

## Owner Map

| Concern | Owner | Inputs | Outputs |
| --- | --- | --- | --- |
| Labor and governance meaning | Bifrost | members, patrons, contributors, motions, work items, votes, priority, rewards, maintainer decisions | typed Bifrost state, work/motion/ledger receipts, Eve product surfaces |
| Governed public crossings | Bifrost | Heimdall claims, Bifrost policy, source topic/work item, target surface | bridge action state, execution receipts, operator surface events |
| Identity, grants, consent, revocation | Heimdall | OAuth providers, linked accounts, operator grants | signed claims consumed by Bifrost |
| Storage and sync primitives | CultCache/CultNet/CultLib | Bifrost document types and payloads | `.cc` stores, snapshots, raw document updates |
| Service discovery and surface aggregation | Odin | CultMesh namespaces, schema catalogs, service registrations | discoverable Verse routes and surface indexes |
| Room observation and Persona cognition | VoidBot/repo Personas | Discord context, repo state, Persona state | proposed Bifrost topics, comments, dispatch requests |
| Rendering | Eve/browser/native/TUI/Discord/Reddit | `gamecult.eve.surface.v1` compositions, typed state references | product/operator UI lowerings, not canonical state |

## Durable State And Witnesses

Bifrost may keep PostgreSQL as the transactional store for the current ASP.NET
member alpha, but durable Verse state must have typed CultCache witnesses.

Current witnesses:

- `.bifrost/governance-threads.cc` stores `bifrost.governance.topic` and
  `bifrost.governance.topic-comment` documents.
- `.bifrost/agent-transport.cc` stores
  `bifrost.agent-transport.update-request` documents and can emit raw CultNet
  snapshots.
- `.bifrost/bridge-receipts.cc` stores canonical
  `bifrost.crossing_receipt.v1` documents for governed public crossing
  attempts.

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

The operator bridge additionally advertises `commands/epiphany-operator` and `receipts/epiphany-operator`. The command endpoint consumes strict VoidBot inbox documents and emits dedicated Bifrost-signed admissions; the receipt endpoint exposes only Epiphany-origin, command-bound results. The transport adapter is intentionally unresolved until a live authenticated CultNet RUDP request/reply implementation is installed on Yggdrasil.

- `gamecult.bifrost.service` for service registration, health, build/version,
  schema catalog, and command capability discovery.
- `gamecult.bifrost.governance` for motions, topics, comments, votes,
  approvals, objections, and policy receipts.
- `gamecult.bifrost.work` for projects, work items, claims, review state,
  estimates, time logs, blockers, completion artifacts, and maintainer
  acceptance.
- `gamecult.bifrost.economics` for patron pressure, contributor credit, ledger
  entries, payout proposal batches, and revenue-share inputs.
- `gamecult.bifrost.bridge` for GitHub, Discord, Reddit, CultNet/CultCache, and future
  collaboration crossings plus receipts.
- `gamecult.bifrost.surface.product` for member/patron/contributor/user-facing
  Eve product surfaces.
- `gamecult.bifrost.surface.operator` for deploy, readiness, queue, witness,
  bridge, schema, and migration operator surfaces.

Odin discovers Bifrost through the service namespace, then indexes the schema
catalog and surface namespaces. Odin does not scrape Razor pages as truth.

## Codex MCP Boundary

Codex and similar external agents are xeno clients at the Verse boundary. The
MCP surface that gives those clients access to Verse state belongs in Bifrost,
not in an individual daemon package. Bifrost hosts the MCP because it owns the
governed crossing: capability shaping, consent, policy, and the external
protocol contract.

The MCP should expose Verse-level tools rather than daemon-specific shortcuts.
Those tools ask Odin for overview and discovery, then route to known CultMesh
addresses in the local Verse. Odin supplies the current map of daemons,
surfaces, schemas, capabilities, and routes; Bifrost decides what a xeno client
is allowed to see or command across that map.

Brokkr is one daemon that may be present in the local Verse. Its Unity editor
TUI/Eve surface and CultCache mirror are discovered through Odin and addressed
through CultMesh like any other resident service. Brokkr should not host the
Codex MCP merely because it happens to be the daemon of interest for a given
session.

Useful MCP tool shapes are therefore conceptual Verse tools such as
`verse.overview`, `verse.daemons`, `verse.surfaces`, `verse.read`,
`verse.write_intent`, and `verse.poll_events`. Their implementations may call
Odin for discovery and then use CultLib/CultMesh/CultCache primitives to read or
write the addressed service state. Local scripts may still inspect a Brokkr
cache directly for debugging and development, but that is not the xeno boundary.

## Provider Advertisement Export

Bifrost has a read-only first-cut provider advertisement command:

```powershell
node tools/provider-advertisement.mjs export --out .bifrost/provider-store.cc
$env:BIFROST_ODIN_CULTMESH_URI="cultmesh://odin/rendezvous/provider-catalog"; node tools/provider-advertisement.mjs publish-odin
```

It writes Bifrost-owned Eve discovery documents into
`.bifrost/provider-store.cc`:

- `gamecult.eve.provider_advertisement.v1` names Bifrost's Account, Patron,
  Project, Work, Motion, and Operator Verse surfaces; current and planned
  schema ids; `.cc` witness/export paths; command authority boundaries; style
  capabilities; and demoted presentation/probe surfaces.
- `gamecult.eve.surface_state.v1` publishes the live Bifrost operator dashboard
  surface with compact service health, topic/request status, dispatch activity
  by source channel, store presence/freshness, and bridge capability status.
- `gamecult.eve.interface_binding.v1` binds that surface to provider id
  `bifrost` so Odin can lower it into Nightwing, Eve, browser, or future room
  dashboards.

This export does not migrate Postgres state, read secrets, execute bridge
actions, or make Razor Pages, HTTP probes, Discord mirrors, Reddit threads, or
local dispatch JSON canonical. The operator surface may display those probes, but the provider
owned `.cc` witness remains the discovery and dashboard source.
When `BIFROST_ODIN_CULTMESH_URI` or `--odin-cultmesh-uri` is configured, the
same provider advertisement can be published once to Odin's CultMesh rendezvous
URI through the provider-advertisement tool. Concrete RUDP bootstrap endpoints
belong behind CultMesh URI resolution, not in Bifrost provider publication
configuration.

For protocol-debug inspection without writing a witness:

```powershell
node tools/provider-advertisement.mjs print
```

## Eve Product And Operator Surfaces

Product surfaces are the canonical interface compositions for:

- account/membership status;
- patron and contributor standing;
- project and work-item boards;
- motions, topic threads, votes, approvals, and objections;
- work claims, review, completion, receipts, and contributor credit;
- bridge receipts and public handoff targets.

Canonical crossing receipts use `bifrost.crossing_receipt.v1` in
`.bifrost/bridge-receipts.cc`. A receipt records `receiptId`, `commandId`,
`crossingKind`, lifecycle status, Bifrost actor, source provenance, authority
reference, optional Heimdall claim/grant references, optional Epiphany run/lane
identity, target locator, external receipt facts, error details, and related
receipt ids. Surface-specific receipts such as
`bifrost.bridge.discord_post_receipt.v1` may remain as details, but they must
reference the canonical crossing receipt and cannot decide crossing completion.
Heimdall values recorded here are stable references or redacted snapshots:
claim `jti`, `account_id`, `access_revision`, `exp`, grant id/ref, and
revoked/expired behavior. Provider bearer tokens do not belong in Bifrost
receipts.

Operator surfaces are the canonical interface compositions for:

- readiness, build/version, config validation, and deploy target;
- compact service health: daemon readiness, container health, and backing store
  presence without making file size or storage mechanics first-rank operator
  signal;
- CultMesh publication health and Odin discovery status;
- topic/request status, dispatch activity by source channel, failed crossings,
  retry/cancel controls, and receipt gaps;
- schema versions, migration state, and Postgres-to-`.cc` witness drift;
- Discord mirror health, including fail-closed unmirrored governance writes;
- Reddit bridge readiness for `r/GameCultOrg` organizing posts and Persona flair.

The Motion Verse has begun the migration from Razor-owned behavior to Eve-owned
presentation. Bifrost publishes the motion surface and command target as
CultMesh addresses discovered through Odin:

```text
cultmesh://asgard.starfire.bifrost/eve/governance/surface
cultmesh://asgard.starfire.bifrost/commands/motion
```

The transitional browser product may lower motion state for members, but it is
not a Verse command transport. `motion.create`, `motion.vote`, and
`motion.close` are command documents for the CultMesh route, then commit through
the same Bifrost motion governance service used by the Razor forms.

The current Razor Pages app is now a transitional browser lowering for motions,
not the behavior owner. Existing health/readiness endpoints lower operator
state into HTTP probes. Both should be fed by the same typed service state that
backs Eve surfaces.

## CultMesh Address Shape

Bifrost publishes semantic CultMesh addresses before transport routes. The
canonical service name survives host moves:

```text
asgard.bifrost
```

The current located instance is:

```text
asgard.starfire.bifrost
```

The planned hosted location is:

```text
asgard.yggdrasil.bifrost
```

Surface resources hang under the located service. TUI and GUI are sibling
lowerings, not one endpoint wearing two costumes:

```text
asgard.starfire.bifrost/eve/tui
asgard.starfire.bifrost/eve/gui
```

CultNet routes are transport metadata for resolving those names. WebSocket and
HTTP URLs are compatibility bridges or probes, not native Bifrost addresses.

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
   crossing receipts, Discord mirrors, Reddit receipts, webhook cache, and app
   readiness state.
2. Define schema ids for missing Bifrost documents: work item, motion, vote,
   ledger entry, bridge action, crossing receipt, member capability snapshot,
   service registration, and Eve surface publication.
3. Add Postgres-to-CultCache witness export for alpha entities before replacing
   the transactional store.
4. Extend the existing Odin provider-advertisement publication into
   `gamecult.bifrost.service` and schema catalog publication through CultMesh.
5. Publish operator surfaces first: readiness, witness freshness, bridge queue,
   failed crossings, and migration drift.
6. Publish product surfaces for account, project, work, motion, patron, and
   contributor views as `gamecult.eve.surface.v1`. Motion governance has its
   first product surface and command endpoint.
7. Lower the existing Razor Pages app from the Eve/product surface contract
   where practical, leaving direct Razor composition only as transitional UI.
8. Route Discord and Reddit native interactions through Bifrost commands that
   update typed state and emit public receipts from the same commit path.
9. Let Odin discover Bifrost surfaces and nested Verses through CultMesh, then
   retire any scraping or private summary adapters.

## Demotion Line

Demoted surfaces and stores:

- Razor Pages views are no longer the canonical Bifrost presentation owner; they
  are browser lowerings of Bifrost product/operator surfaces.
- `/healthz`, `/readyz`, and any future HTTP dashboard/status JSON are product
  smoke probes or xenos-boundary exports, not service truth. Provider
  advertisements and Odin-facing operator readiness must reject HTTP probe
  configuration and derive status from typed CultMesh/Idunn state.
- `.bifrost/discord-webhook-cache.json` is webhook address cache only; it does
  not prove governance, speech authority, or receipt completion.
- `bifrost.bridge.discord_post_receipt.v1` and other surface receipts are
  surface-specific evidence only. `bifrost.crossing_receipt.v1` is the
  canonical crossing receipt owner.
- `.bifrost/agent-dispatch/**/request.json`, `dispatch.json`, `result.json`, and
  app-server protocol JSON are local dispatch/protocol artifacts. They may be
  evidence attached to typed receipts, but they cannot decide request status,
  work authority, or bridge success.
- Discord `#bifrost` messages are mirrors and linked-user input surfaces. They
  are not canonical governance threads unless Bifrost commits the corresponding
  topic/comment/vote document.
- Reddit `r/GameCultOrg` threads are public organizing surfaces and linked-user
  input surfaces. They are not voting power unless Bifrost commits a typed vote
  or priority signal from a linked actor.

The live owner is Bifrost typed state. Everything else either lowers it,
commands it, witnesses it, or provides external evidence for a typed receipt.
