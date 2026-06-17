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

It does not mean Bifrost should own OAuth sludge. Heimdall owns OAuth, linked identities, token custody, grants, and signed account claims. Bifrost consumes Heimdall identity and permission facts, then decides whether a bridge action is allowed in the Bifrost domain.

## Ownership

- Heimdall owns provider OAuth and account-linked credentials.
- Bifrost owns bridge action policy, request lifecycle, routing, execution receipts, Discord-native Bifrost interface transport, and governance/labor transport.
- Reddit posts through the Bifrost Reddit app are Bifrost-owned public organizing crossings; Persona flair is presentation identity, not authority.
- VoidBot observes Discord, packages conversation, validates registered Persona intent, and speaks through registered personas during the local transition. It should not be the durable authority that mutates GitHub or owns Bifrost-scoped Discord transport.
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

## Bridge Action Ledger

The hosted app now exposes a first-class bridge action ledger for governed transport requests:

- `POST /bridge/actions/request`
- `GET /bridge/actions/{id}`
- `POST /bridge/actions/{id}/start`
- `POST /bridge/actions/{id}/complete`
- `POST /bridge/actions/{id}/fail`

The owner is Bifrost, not the local script. The script or agent may execute the crossing, but Bifrost records the request, policy decision, lifecycle state, and receipt.

The hosted app also exposes a dispatch-run ledger for launched agent work:

- `POST /dispatch/runs/start`
- `POST /dispatch/runs/{id}/complete`
- `POST /dispatch/runs/{id}/fail`

This is the companion trail to bridge actions. A bridge action says "this crossing into GitHub/Discord/Reddit was requested and receipted." A dispatch run says "this specific Codex worker actually launched, ran, and ended this way." One guards governed mutation. The other proves agent activity.

The hosted app also exposes a request-lane receipt ledger for transport lifecycle events:

- `POST /transport/receipts`

This covers the activity that happens before or around a visible Codex turn: queueing a request, claiming it, releasing it, and closing it. The `.cc` packet is still the transport document. The hosted receipt row is the operator-facing witness that the activity happened.

The hosted app also exposes a governance activity receipt ledger:

- `POST /governance/receipts`

This covers the other side of the swarm's work: topic opens, comments, approvals, and promotions to dispatch. The topic and comment documents are still the governance substrate. The hosted receipt row is the Bifrost-side witness that an agent or operator performed the governance action.

These runtime receipt lanes are not generic member write surfaces. In the current machine, `/dispatch/runs/*`, `/transport/receipts`, and `/governance/receipts` are owned by the configured local bridge token. Active members may govern and request bridge actions through the app, but they may not mint worker-history rows directly through a browser session and impersonate runtime activity.

Current gate behavior:

- active Bifrost members may request bridge actions from an authenticated app session
- the transitional local bridge actuator may call the same endpoints with `X-Bifrost-Bridge-Token`
- agent, Persona, and service actions must cite provenance: `authorityReference`, `sourceKind` plus `sourceId`, `workItemId`, or `motionId`
- completed actions must report a receipt URL, external receipt id, or serialized receipt payload
- GitHub mutation commands fail closed unless Bifrost authorization is configured; `--allow-ungated-github true` is the explicit operator-recovery hatch

Current CLI wiring:

- set `BIFROST_BRIDGE_BASE_URL` to the Bifrost app base URL
- set `BIFROST_BRIDGE_TOKEN` to the configured local bridge token
- GitHub commands now require both values by default, even for dry-run, so the normal path cannot silently bypass the gate
- pass `--source-kind`, `--source-id`, `--authority-ref`, `--work-item-id`, or `--motion-id` so agent actions satisfy policy
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

The command leaves a signed PR comment through GitHub's issue-comment API via `gh api` and prints a JSON receipt with GitHub's own comment id and `html_url`. Repo Personas should use this when the argument belongs on the review artifact instead of dissolving into Discord scrollback.

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

1. Heimdall implements GitHub OAuth callback/runtime and managed credential custody.
2. Heimdall issues Bifrost-verifiable claims for account identity and bridge capabilities.
3. Bifrost records a bridge action request and checks the Heimdall-derived permission facts.
4. Bifrost executes or queues the action through the appropriate bridge executor.
5. Bifrost records the GitHub/Discord/Reddit receipt and exposes it back to the requesting agent/runtime.

In that shape, Bifrost is the shiny bridge. Heimdall is the gatehouse with the keys. VoidBot is not hiding a bolt cutter in its coat.
