# Agent Transport

Bifrost owns the agent transport contract for GameCult work requests.

This is not a Postgres queue and not a VoidBot side channel. VoidBot may observe Discord and package consensus, but the shared request lane belongs here:

- Bifrost defines the request shape, status rules, and claim semantics.
- CultCache stores the durable request documents.
- CultNet carries snapshots and raw document updates between runtimes.
- Repo agents claim only requests whose target repository matches their jurisdiction.

That ownership split matters. If the queue lives in VoidBot, Discord becomes the hidden source of authority. If it lives in Bifrost but stores through Entity Framework, agents cannot share the same packet through the CultCache/CultNet machinery they already use for state. The coherent machine is smaller: Bifrost says what the request means; CultCache remembers it; CultNet moves it.

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

`plugins/bifrost-intake` exposes this lane to Codex as an MCP plugin. It does not own a second queue. Its server wraps `tools/agent-transport.mjs`.

Tools:

- `get_intake_context`
- `enqueue_update_request`
- `list_update_requests`
- `claim_update_request`
- `close_update_request`
- `format_claimed_request`
- `create_transport_snapshot`
- `apply_transport_snapshot`

The plugin is listed in `.agents/plugins/marketplace.json` as `bifrost-intake`.

`get_intake_context` is the normal Codex-facing entry point. It claims the next matching request for the current repo and returns a context packet immediately. If nothing is queued, it returns a direct no-work message so the agent can stop worrying about intake and continue the live turn.

## Next Integration Points

- Teach the VoidBot consensus feeder to enqueue Bifrost update requests after it writes a packet.
- Let repo Faces claim by `targetRepoName`, then inject the claimed packet into Codex only when the repo matches their jurisdiction.
- Use CultNet direct pipes when a long-running agent runtime is available; keep raw snapshot files as the dead-simple bridge until then.
