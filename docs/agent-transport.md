# Agent Transport

Bifrost owns the agent transport contract for GameCult work requests because agent intake is part of Bifrost's public-process jurisdiction. The broader ownership rule lives in `docs/jurisdiction-map.md`: Bifrost governs work crossings; CultCache stores typed packets; CultNet moves them; VoidBot observes Discord and packages consensus; repo Faces claim only work inside their jurisdiction.

This is not a Postgres queue and not a VoidBot side channel. VoidBot may observe Discord and package consensus, but the shared request lane belongs here:

- Bifrost defines the request shape, status rules, and claim semantics.
- CultCache stores the durable request documents.
- CultNet carries snapshots and raw document updates between runtimes.
- Repo agents claim only requests whose target repository matches their jurisdiction.

That ownership split matters. If the queue lives in VoidBot, Discord becomes the hidden source of authority. If it lives in Bifrost but stores through Entity Framework, agents cannot share the same packet through the CultCache/CultNet machinery they already use for state. The coherent machine is smaller: Bifrost says what the request means; CultCache remembers it; CultNet moves it.

An update request is not just a message in a queue. It is a governed handoff:

- what changed in the room, repo, motion, or work context
- which repo or Face has jurisdiction
- why this deserves priority now
- where the claimed work should report back
- which receipt will prove the request was handled

## Document

Agent update requests are stored as CultCache documents with this type:

`bifrost.agent-transport.update-request`

The schema id is:

`bifrost.agent-transport.update-request.v0`

Each request has:

- `id`: stable request id.
- `targetRepoName`: short repository name, such as `AetheriaLore`.
- `targetRepositoryFullName`: optional owner/name form.
- `targetAgentIdentity`: optional Face identity, such as `nibu`.
- `title`: short human label.
- `requestMarkdown`: the actual consensus or task packet.
- `priority`: higher numbers claim first.
- `status`: `queued`, `claimed`, `completed`, or `cancelled`.
- `sourceKind`: where the request came from, such as `discord_consensus`.
- `sourceChannelId`, `sourceMessageIds`, `sourcePacketPath`, `sourcePromptPath`: optional provenance.
- `createdByAgent`, `claimedByAgent`: identity trace.
- `closeNote`: completion/cancellation note.
- `createdAt`, `updatedAt`, `claimedAt`, `closedAt`: ISO timestamps.

## CLI

`tools/agent-transport.mjs` is intentionally local and boring. It imports the sibling `CultCacheTS` and `CultNetTS` builds directly, writes a `.cc` store, and can emit/apply CultNet raw snapshots.

Default store:

`E:\Projects\Bifrost\.bifrost\agent-transport.cc`

Examples:

```powershell
node .\tools\agent-transport.mjs enqueue --repo AetheriaLore --agent nibu --title "Wavecrafter consensus" --request-file E:\path\packet.md --priority 80
node .\tools\agent-transport.mjs list --repo AetheriaLore --status queued
node .\tools\agent-transport.mjs claim --repo AetheriaLore --agent nibu --claimed-by nibu
node .\tools\agent-transport.mjs close --id req_... --status completed --note "Opened AetheriaLore PR."
node .\tools\agent-transport.mjs snapshot --out E:\tmp\bifrost-agent-transport.msgpack
node .\tools\agent-transport.mjs apply-snapshot --in E:\tmp\bifrost-agent-transport.msgpack
```

The `snapshot` command writes a MessagePack-encoded `cultnet.snapshot_response_raw.v0` message. This keeps CultCache payload bytes intact for peers that share the document binding.

## Codex Intake Plugin

`plugins/bifrost-intake` exposes this lane to Codex through direct scripts. It does not own a second queue, and the intake hot path does not depend on MCP tool mounting.

Hot path:

```powershell
node E:\Projects\Bifrost\plugins\bifrost-intake\scripts\intake-context.mjs --repo AetheriaLore --agent nibu
```

That command claims the next matching request for the current repo and prints a context packet immediately. If nothing is queued, it prints a direct no-work message so the agent can stop worrying about intake and continue the live turn.

Lower-level CLI commands:

- `tools/agent-transport.mjs enqueue`
- `tools/agent-transport.mjs list`
- `tools/agent-transport.mjs claim`
- `tools/agent-transport.mjs close`
- `tools/agent-transport.mjs snapshot`
- `tools/agent-transport.mjs apply-snapshot`

The plugin is listed in `.agents/plugins/marketplace.json` as `bifrost-intake`.

## Next Integration Points

- Route claimed agent work that produces reviewable artifacts through `tools/bifrost-bridge.mjs` so Bifrost, not VoidBot, owns GitHub and Discord crossing receipts.
- Use CultNet direct pipes when a long-running agent runtime is available; keep raw snapshot files as the dead-simple bridge until then.
- Surface transport requests inside the Bifrost app as governance/workflow objects instead of leaving them as local CLI state forever.
