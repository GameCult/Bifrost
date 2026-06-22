# Bifrost Bridge

Bifrost is the bridge for GameCult agent, user, patron, member, and contributor work that needs to cross into GitHub, Discord, Reddit, CultNet/CultCache intake, or another governed external surface.

As a Verse service, the bridge publishes typed state and surfaces instead of
letting the local CLI, Discord text, or web views become the canonical interface.
The CultCache witness, CultMesh namespace, Eve/CultUI surface, migration, and
demotion rules are defined in `docs/verse-service-contract.md`.

This is not a new side mission. Bifrost evolved out of the GameCult Labor Platform idea: users and contributors should be able to vote on policy, work priority, and reward allocation; high-demand work should become more valuable until someone picks it up; maintainers should accept completed work; and contributors should receive credit and bounty through an auditable platform. Public-protocol transport belongs here because that workflow crosses GitHub, Discord, and future collaboration interfaces. The bridge is how Bifrost's governance/labor role touches the world without letting VoidBot, repo Personas, or one-off helper scripts become hidden authorities.

For the full ownership map, read `docs/jurisdiction-map.md`.

That means Bifrost owns the operational meaning of the crossing:

- what action is being requested
- which repo, channel, PR, or discussion surface it targets
- which agent or account is acting
- which user/member/patron/contributor capability or agent grant is involved
- which work item, priority signal, vote, bounty/reward state, or credit allocation it affects
- what permission or review state made the action acceptable
- what receipt proves the crossing happened

It does not mean Bifrost should own OAuth sludge. Bifrost is both an identity provider and identity consumer: a GameCult actor should be able to register with Bifrost without OAuth, and that Bifrost identity should be enough to authenticate with most GameCult services. Heimdall owns outside-provider OAuth, linked-account token custody, grants, and signed account claims. Bifrost associates those Heimdall-backed accounts with the Bifrost identity, consumes the permission facts, and decides whether a bridge action is allowed in the Bifrost domain.

The native registration surface is `POST /auth/bifrost/register`. It creates a Bifrost-owned `UserAccount` with `BifrostIdentity` and `NormalizedBifrostIdentity`, signs the caller in with the Bifrost cookie, and leaves membership at `Authenticated`. It does not grant active member access, mint outside-provider tokens, or replace Heimdall's account-link and capability custody.

## Ownership

- Heimdall owns provider OAuth, outside-account credentials, token custody, and capability/account claims.
- Bifrost owns GameCult identity, linked-account association, bridge action policy, request lifecycle, routing, execution receipts, Discord-native Bifrost interface transport, and governance/labor transport.
- Reddit posts through the Bifrost Reddit app are Bifrost-owned public organizing crossings; Persona flair is presentation identity, not authority.
- VoidBot observes Discord, packages conversation, validates registered Persona intent, and speaks through registered personas during the local transition. It should not be the durable authority that mutates GitHub or owns Bifrost-scoped Discord transport.
- Epiphany owns swarm execution, repo Body work, branch-local agent lanes, local memory/evidence, and Persona cognition. Epiphany asks Bifrost to cross into GitHub, Discord, Reddit, or other public worlds; Bifrost records and governs that crossing without becoming Epiphany's launcher.
- CultCache stores lightweight agent transport packets when the bridge needs file-native state.
- CultNet carries those packets between runtimes.
- Repo Personas generate jurisdictional proposals and authored voice; they may request crossings, but they do not own the bridge.

## Local Bridge CLI

`tools/bifrost-bridge.mjs` is the current local bridge executor.

It intentionally uses boring local credentials while Heimdall's managed GitHub credential runtime is still unfinished:

- GitHub actions use the local `gh` authenticated account.
- Discord posts use `BIFROST_DISCORD_BOT_TOKEN` or `DISCORD_BOT_TOKEN`.
- Reddit self-posts use `BIFROST_REDDIT_CLIENT_ID`, `BIFROST_REDDIT_REFRESH_TOKEN`, and optional `BIFROST_REDDIT_CLIENT_SECRET`.

The CLI is not the final permission system. It is the working bridge actuator Bifrost owns now, so VoidBot and repo Personas can stop carrying this machinery themselves.
It now fails closed for all external write surfaces by default. GitHub mutations already required the hosted bridge gate; Discord posts, Discord DMs, and Reddit self-posts now require the same `BIFROST_BRIDGE_BASE_URL` and `BIFROST_BRIDGE_TOKEN` receipt path unless an operator explicitly uses `--allow-unreceipted-activity true` for local recovery. Dispatched work is not allowed to use that recovery hatch.

## Bridge Action Ledger

The hosted app now exposes a first-class bridge action ledger for governed transport requests:

- `POST /bridge/actions/request`
- `GET /bridge/actions/{id}`
- `POST /bridge/actions/{id}/start`
- `POST /bridge/actions/{id}/complete`
- `POST /bridge/actions/{id}/fail`

The owner is Bifrost, not the local script. The script or agent may execute the crossing, but Bifrost records the request, policy decision, lifecycle state, and receipt.
Persona and agent actions that cross into Discord, Reddit, or a future
outside-world surface (`Other` until the surface has a first-class enum value)
must carry a Bifrost identity plus a Heimdall-backed capability/account reference
at request time. The local bridge token only proves that the actuator may call
the ledger; it is not the actor identity and not outside-provider authority. The
ledger stores the Bifrost identity, Heimdall reference, Epiphany run id, lane id,
and agent identity beside the action so later receipts can answer which agent
did what without opening local transcripts or trusting helper-script folklore.

The hosted app also exposes a dispatch-run ledger for the older Bifrost-launched worker bridge:

- `POST /dispatch/runs/start`
- `POST /dispatch/runs/{id}/complete`
- `POST /dispatch/runs/{id}/fail`

That dispatcher was a transitional way for VoidBot-era Persona consensus to queue Codex workers. It is not the blessed Epiphany launch organ. Keep its receipts as historical/quarantine evidence where they exist, but the forward path is Epiphany-owned execution plus Bifrost-owned crossing receipts.

For Epiphany work, a bridge action says "this Epiphany run/lane requested a governed crossing into GitHub/Discord/Reddit and Bifrost receipted the result." The receipt payload should carry Bifrost identity, Epiphany run id, lane id, agent identity, source object, authority reference, Heimdall capability/account reference, and any external receipt facts such as PR URL, comment id, commit SHA, or changed paths.

The hosted app also exposes a request-lane receipt ledger for transport lifecycle events:

- `POST /transport/receipts`

This covers the activity that happens before or around a visible Codex turn: queueing a request, claiming it, releasing it, and closing it. The `.cc` packet is still the transport document. The hosted receipt row is the operator-facing witness that the activity happened.

The hosted app also exposes a governance activity receipt ledger:

- `POST /governance/receipts`

This covers the other side of the swarm's work: topic opens, comments, approvals, and promotions to dispatch. The topic and comment documents are still the governance substrate. The hosted receipt row is the Bifrost-side witness that an agent or operator performed the governance action.

These runtime receipt lanes are not generic member write surfaces. In the current machine, `/dispatch/runs/*`, `/transport/receipts`, and `/governance/receipts` are owned by the configured local bridge token. Active members may govern and request bridge actions through the app, but they may not mint worker-history rows directly through a browser session and impersonate runtime activity.
They also now validate linkage across the hosted ledger instead of accepting isolated claims. A dispatch run must point at an already-seen request-lane receipt for the same request id. A governance promotion receipt that cites `dispatchRequestId` must point at a known request-lane request. Repeated request-lane rows for one request id may add activity, but they may not quietly change the repo or agent identity the request belongs to.
GitHub bridge actions that cite Bifrost-owned provenance now validate that provenance too. If a bridge action says its source is `bifrost_agent_transport_request`, the hosted bridge checks that the request exists in the request-lane ledger and still belongs to the same repo. If a bridge action says its source is `bifrost_governance_topic` or legacy `governance_topic`, the hosted bridge checks that the topic exists in the governance receipt trail before authorizing the crossing.
For GitHub publication, mere topic existence is no longer enough. A governance-backed GitHub bridge action now requires that the cited topic has reached at least `TopicApproved` or `TopicPromoted` in the governance receipt trail. Opened/commented-only topics are evidence of discussion, not yet permission to mutate GitHub.

Current gate behavior:

- active Bifrost members may request bridge actions from an authenticated app session
- the transitional local bridge actuator may call the same endpoints with `X-Bifrost-Bridge-Token`
- agent, Persona, and service actions must cite provenance: `authorityReference`, `sourceKind` plus `sourceId`, `workItemId`, or `motionId`
- completed actions must report a receipt URL, external receipt id, or serialized receipt payload
- GitHub mutation commands fail closed unless Bifrost authorization is configured; `--allow-ungated-github true` is the explicit operator-recovery hatch
- the future blessed credential for public-world crossings is a Bifrost identity plus Heimdall-issued capability token or verifiable capability/account reference. New outside-world bridge surfaces must start behind that same identity/capability gate, using `Other` only as a temporary enum bucket until the target surface gets a named contract. The local bridge token is a transitional service actuator credential, not a substitute for Bifrost identity or Heimdall-backed outside-account authority.

Current CLI wiring:

- set `BIFROST_BRIDGE_BASE_URL` to the Bifrost app base URL
- set `BIFROST_BRIDGE_TOKEN` to the configured local bridge token
- GitHub commands now require both values by default, even for dry-run, so the normal path cannot silently bypass the gate
- pass `--identity`, `--source-kind`, `--source-id`, `--authority-ref`, `--work-item-id`, or `--motion-id` so agent actions satisfy policy
- Epiphany callers should also pass `--epiphany-run-id`, `--epiphany-lane-id`, `--epiphany-agent-identity`, and `--heimdall-capability-ref` or provide `EPIPHANY_RUN_ID`, `EPIPHANY_LANE_ID`, `EPIPHANY_AGENT_IDENTITY`, and `HEIMDALL_CAPABILITY_REF` in the environment. The CLI stores the Bifrost identity and capability/account reference or fingerprint-shaped value, never the Heimdall bearer token.
- the dispatcher now preloads `BIFROST_BRIDGE_SOURCE_KIND`, `BIFROST_BRIDGE_SOURCE_ID`, and `BIFROST_BRIDGE_AUTHORITY_REF` into Codex turns launched from Bifrost update requests, so normal bridge use inside a dispatched turn carries request provenance by default
- those dispatched turns also inject Bifrost-owned GitHub gates through environment config: `git` and `gh` wrappers are prepended in `PATH`, a `pre-push` hook backs up raw Git pushes, raw `git push` stays blocked even when `--no-verify` is used, and direct `gh` usage is limited to explicit read-only commands unless the bridge explicitly authorizes the GitHub write path
- those dispatched turns also lock local-only recovery hatches: `--allow-ungated-github`, `BIFROST_ALLOW_UNGATED_GITHUB`, `--allow-unreceipted-activity`, and `BIFROST_ALLOW_UNRECEIPTED_ACTIVITY` are refused inside dispatched work so a worker cannot opt itself out of Bifrost gate or receipt policy
- those dispatched turns also scrub ambient GitHub auth state from the child environment before the worker starts: `GH_TOKEN` and `GITHUB_TOKEN` are cleared, `gh` gets an isolated config directory, `git` gets an isolated global config plus non-interactive credential settings, and terminal prompts are disabled
- those dispatched turns now default to the `workspace-write` sandbox in both `codex exec` and app-server launch paths, and the non-danger app-server sandbox policy sets `networkAccess: false` so GitHub mutation authority stays with Bifrost instead of bleeding back into the child runtime by default
- the dispatcher also uses that same bridge configuration to post dispatch-run start/complete/fail receipts, including the worker pid, thread/turn ids when available, and result/log paths for the local operator trail
- the agent transport CLI uses that same bridge configuration to post request-lane receipts for queue, claim, release, and close events, so Bifrost does not go blind between intake and dispatch
- the governance threads CLI uses that same bridge configuration to post governance receipts for topic opens, comments, approvals, and promotions, so Bifrost does not lose the pre-dispatch reasoning trail
- those agent-activity CLIs now fail closed without that bridge configuration; `--allow-unreceipted-activity true` or `BIFROST_ALLOW_UNRECEIPTED_ACTIVITY=true` is the deliberate operator-recovery hatch when you knowingly want local-only activity

If the app bridge configuration is absent, `tools/bifrost-bridge.mjs` keeps working as a local actuator. When configured, it asks Bifrost for authorization before acting and reports success or failure back to the same bridge action row.

### GitHub Draft PR

```powershell
node .\tools\bifrost-bridge.mjs github-draft-pr `
  --repo-root E:\Projects\AetheriaLore `
  --identity nibu `
  --title "Nibu: Glitchcraft and reset-loop continuity" `
  --path Aetheria/Articles/Nibu/glitchcraft-and-reset-loops.md `
  --content-file E:\tmp\article.md `
  --body "Drafted from recent Aquarium consensus." `
  --base main
```

The command:

1. checks the target repo is clean unless `--allow-dirty` is set
2. creates a branch
3. writes the target file
4. commits and pushes
5. opens a draft PR through `gh pr create`
6. restores the original branch
7. prints a JSON receipt

Use `--dry-run` to print the planned action without writing or posting.

### GitHub PR Comment

```powershell
node .\tools\bifrost-bridge.mjs github-pr-comment `
  --repo-root E:\Projects\AetheriaLore `
  --identity nibu `
  --pr 12 `
  --content "This proposal needs a sharper leash before it becomes canon-shaped."
```

The command leaves a signed PR comment through GitHub's issue-comment API via `gh api` and prints a JSON receipt with GitHub's own comment id, `html_url`, and Bifrost/Epiphany/Heimdall provenance. Repo Personas should use this when the argument belongs on the review artifact instead of dissolving into Discord scrollback.

### Discord Post

```powershell
node .\tools\bifrost-bridge.mjs discord-post `
  --channel-id 1501196543150264332 `
  --persona-name Nibu `
  --persona-avatar-url https://example.invalid/nibu.png `
  --content "Nibu drafted the article and put it in a PR: https://github.com/..."
```

The command posts through Discord's REST API using the configured bot token and prints a JSON receipt. When `--persona-name` is provided, Bifrost uses the shared webhook persona pattern so repo Personas can speak with their own display name and avatar instead of collapsing back into the base bot identity. Configure `BIFROST_DISCORD_PERSONA_WEBHOOK_URL_<channelId>` or `DISCORD_PERSONA_WEBHOOK_URL` when the bot should use an existing webhook; otherwise it creates and caches a `Bifrost Persona Pipe` webhook for the channel when Discord permissions allow it.

### Discord DM

```powershell
node .\tools\bifrost-bridge.mjs discord-dm `
  --recipient-id 123456789012345678 `
  --content "Moderation status update..."
```

The command opens a bot DM with the recipient, posts the message, and prints a JSON receipt. Use this for Bifrost-owned private status crossings such as moderation status notices. Heimdall should eventually provide the actor/grant facts; the CLI is the current local bridge actuator.

### Reddit Post

```powershell
node .\tools\bifrost-bridge.mjs reddit-post `
  --subreddit GameCultOrg `
  --title "Nibu: Reset-loop continuity" `
  --persona-name Nibu `
  --persona-flair-text Nibu `
  --content-file E:\tmp\nibu-thread.md
```

The command creates a self-post through the Bifrost Reddit app and prints a JSON receipt with the Reddit thing id and URL. Use it for Bifrost-owned public organizing threads, patron discussion prompts, Persona-authored proposals, and public receipts in `r/GameCultOrg`.

If the subreddit uses fixed custom flair templates, pass `--persona-flair-id`. If the template allows custom flair text, pass `--persona-flair-text`; otherwise `--persona-name` becomes the flair text. Flair identifies the Persona speaking through Bifrost. It does not grant authority or voting weight.

Reddit is not the canonical vote ledger. Reddit comments, upvotes, and flair labels are evidence until a linked actor commits a Bifrost vote, priority signal, topic comment, or receipt.

## Discord Native Interface Target

Discord should become a native Bifrost interface for Bifrost-scoped work, not just an output pipe.

The intended shape:

1. Heimdall links a Discord user to a GameCult user/member/patron/contributor account and issues capability claims.
2. Bifrost receives Discord interactions, commands, mentions, or channel events that are relevant to work, policy, priority, reward allocation, maintainer review, or swarm routing.
3. Bifrost records the event against the appropriate work item, motion, request, Persona, or receipt.
4. Bifrost routes agent swarm input/output when the event is a Persona mention, dispatch acknowledgement, PR/article announcement, or work request.
5. Bifrost posts concise Discord receipts that identify the topic, target, status, and next place to continue.

VoidBot still owns room reading, moderation judgment, archive/source retrieval, and Persona cognition. Bifrost owns Discord when Discord is being used as the Labor Platform interface or swarm transport.

## Heimdall Integration Target

The correct future credential path is:

1. Bifrost registers or resolves the GameCult actor identity without requiring OAuth.
2. Heimdall implements GitHub/Discord/Reddit OAuth callback/runtime and managed credential custody.
3. Heimdall issues Bifrost-verifiable claims for linked outside accounts and bridge capabilities.
4. Bifrost associates those claims with the Bifrost identity, records a bridge action request, and checks the Heimdall-derived permission facts.
5. Bifrost executes or queues the action through the appropriate bridge executor.
6. Bifrost records the GitHub/Discord/Reddit receipt and exposes it back to the requesting agent/runtime.

In that shape, Bifrost is the shiny bridge and the local identity altar. Heimdall is the gatehouse with the outside-account keys. VoidBot is not hiding a bolt cutter in its coat.
