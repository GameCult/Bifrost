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
- Future outside-world requests must have a named typed command/receipt contract before they get a surface actuator.

The CLI is not the final permission system. It is the working bridge actuator Bifrost owns now, so VoidBot and repo Personas can stop carrying this machinery themselves.
It now fails closed for external write surfaces unless the action carries typed CultMesh command provenance such as `BIFROST_CULTMESH_COMMAND_ID`. The old HTTP bridge ledger environment (`BIFROST_BRIDGE_BASE_URL` / `BIFROST_BRIDGE_TOKEN`) is removed from live CLI transport.
`other-request` fails closed by default. It does not execute a transport; it must become a typed Bifrost command/receipt document under a named surface before anyone writes a surface-specific actuator.

## Crossing Receipts

The owner is Bifrost, not the local script. The script or agent may execute the crossing, but Bifrost records the request, policy decision, lifecycle state, and receipt in typed CultCache/CultMesh documents.
Persona and agent actions that cross into Discord, Reddit, or a future
outside-world surface (`Other` until the surface has a first-class enum value)
must carry a Bifrost identity plus a Heimdall-backed capability/account reference
at request time. The command document stores the Bifrost identity, Heimdall reference, Epiphany run id, lane id,
and agent identity beside the action so later receipts can answer which agent
did what without opening local transcripts or trusting helper-script folklore.

The canonical receipt document is `bifrost.crossing_receipt.v1`, stored in
`.bifrost/bridge-receipts.cc`. It records one crossing attempt with command,
source, authority, actor, target, external proof, lifecycle status, and error
details. Surface-specific receipts, including
`bifrost.bridge.discord_post_receipt.v1`, are details that reference the
canonical receipt. They do not own crossing completion.

The old hosted dispatch-run and bridge-action ledgers are historical/quarantine evidence where records already exist. The forward path is Epiphany-owned execution plus Bifrost-owned typed crossing receipts.

For Epiphany work, a bridge action says "this Epiphany run/lane requested a governed crossing into GitHub/Discord/Reddit and Bifrost receipted the result." The receipt payload should carry Bifrost identity, Epiphany run id, lane id, agent identity, source object, authority reference, Heimdall capability/account reference, and any external receipt facts such as PR URL, comment id, commit SHA, or changed paths.

Request-lane receipts are canonical crossing receipts for typed request-store mutations: queue, claim, release, close, and snapshot import. Governance receipts are canonical crossing receipts for typed governance-store mutations: topic opens, comments, approvals, and promotions. Runtime HTTP receipt posts are not a live authority.

Current gate behavior:

- active Bifrost members may request bridge actions from an authenticated app session
- daemon and agent crossings must use typed CultMesh command/receipt documents
- agent, Persona, and service actions must cite provenance: `authorityReference`, `sourceKind` plus `sourceId`, `workItemId`, or `motionId`
- completed visible actions must write `bifrost.crossing_receipt.v1` with a receipt URL, external receipt id, or serialized receipt payload where the target surface provides one
- GitHub mutation commands fail closed unless typed Bifrost CultMesh command provenance is present
- the future blessed credential for public-world crossings is a Bifrost identity plus Heimdall-issued capability reference or verifiable capability/account reference. New outside-world bridge surfaces must start behind that same identity/capability gate. Bifrost records claim `jti`, `account_id`, `access_revision`, `exp`, grant id/ref, and revoked/expired behavior as references or redacted snapshots, not bearer tokens.

Current CLI wiring:

- provide `BIFROST_CULTMESH_COMMAND_ID` or `--cultmesh-command-id` from the typed command document being processed
- GitHub commands require typed CultMesh command provenance by default, even for dry-run, so the normal path cannot silently bypass the gate
- pass `--identity`, `--source-kind`, `--source-id`, `--authority-ref`, `--work-item-id`, or `--motion-id` so agent actions satisfy policy
- Epiphany callers should also pass `--epiphany-run-id`, `--epiphany-lane-id`, `--epiphany-agent-identity`, and `--heimdall-capability-ref` or provide `EPIPHANY_RUN_ID`, `EPIPHANY_LANE_ID`, `EPIPHANY_AGENT_IDENTITY`, and `HEIMDALL_CAPABILITY_REF` in the environment. The CLI stores the Bifrost identity and capability/account reference or fingerprint-shaped value, never the Heimdall bearer token.
- the dispatcher now preloads `BIFROST_BRIDGE_SOURCE_KIND`, `BIFROST_BRIDGE_SOURCE_ID`, and `BIFROST_BRIDGE_AUTHORITY_REF` into Codex turns launched from Bifrost update requests, so normal bridge use inside a dispatched turn carries request provenance by default
- those dispatched turns also inject Bifrost-owned GitHub gates through environment config: `git` and `gh` wrappers are prepended in `PATH`, a `pre-push` hook backs up raw Git pushes, raw `git push` stays blocked even when `--no-verify` is used, and direct `gh` usage is limited to explicit read-only commands unless the bridge explicitly authorizes the GitHub write path
- those dispatched turns inherit the same gate and receipt invariant, so a worker cannot opt itself out of Bifrost gate or receipt policy
- those dispatched turns also scrub ambient GitHub auth state from the child environment before the worker starts: `GH_TOKEN` and `GITHUB_TOKEN` are cleared, `gh` gets an isolated config directory, `git` gets an isolated global config plus non-interactive credential settings, and terminal prompts are disabled
- those dispatched turns now default to the `workspace-write` sandbox in both `codex exec` and app-server launch paths, and the non-danger app-server sandbox policy sets `networkAccess: false` so GitHub mutation authority stays with Bifrost instead of bleeding back into the child runtime by default
- the dispatcher writes local run artifacts and request-store state for start/complete/fail visibility
- the agent transport CLI writes canonical crossing receipts for queue, claim, release, close, and snapshot import events to `.bifrost/bridge-receipts.cc`
- the governance threads CLI writes canonical crossing receipts for topic opens, comments, approvals, and promotions to `.bifrost/bridge-receipts.cc`
- those agent-activity CLIs now fail closed without that bridge configuration

If hosted app authorization is absent, `tools/bifrost-bridge.mjs` keeps working as a local actuator only when typed CultMesh command provenance is present. It writes requested, running, completed, failed, or cancelled crossing receipts directly to the canonical receipt store.

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

### Future Surface Request

```powershell
node .\tools\bifrost-bridge.mjs other-request `
  --identity epiphany.Persona `
  --surface-name bluesky `
  --target-locator at://did:example/app.bsky.feed.post/123 `
  --heimdall-capability-ref heimdall:bluesky:capability:epiphany-persona `
  --content "Persona asks Bifrost to authorize this public crossing."
```

The command is a dry-run shape probe for public surfaces that are real enough to need identity and receipts, but not yet stable enough to deserve a first-class enum, actuator, or UI. Live writes fail closed until that surface has a typed Bifrost command/receipt contract. Do not put provider bearer tokens in `--heimdall-capability-ref`; that value is the Heimdall-issued account/capability reference or a verifiable fingerprint-shaped reference.

## Discord Native Interface Target

### Organizational Persona Feedback Ingress

VoidBot remains the Discord observer and mention scheduler. A Face registry entry may opt into a remote `remotePersonaFeedbackTarget`; only then does VoidBot insert an immutable typed `voidbot.discord.persona_feedback_event.v0` observation into the dedicated `BIFROST_PERSONA_FEEDBACK_OBSERVATION_STORE_PATH`. This is an observation inbox, never an actuator queue. Insert collisions must contain exactly equal content. Its canonical path and physical file identity must differ from the outbound Discord-command provider store and all Bifrost-private state. Local Persona mention handling is committed first; observation export runs independently with three bounded idempotent attempts and cannot suppress the local response path.

Discord operator control is a separate authority. VoidBot may insert only a strict `voidbot.discord.epiphany_operator_request.v1` document containing one of `status`, `sleep`, `wake`, `directive`, `reviews`, or `review`, a fixed target runtime, Discord provenance, and a positive expiry of at most 60 seconds. Bifrost independently requires the exact configured Discord owner and the exact persisted guild/actor mapping from its private actor-link store. The actor link is the admitted mapping authority; its Heimdall reference remains provenance until a signed live Heimdall claim surface exists and is not represented as authorization. Bifrost derives the Rust field-order packet tuple, hashes its canonical MessagePack bytes, and signs the exact `BifrostOperatorCommandAdmission` tuple for purpose `bifrost.operator-command.delivery.v1` with a dedicated operator Ed25519 seed. The feedback seed is not reused.

Bifrost does not mount an Epiphany store and has no argv or shell escape hatch. One supervised worker serializes requests across the CultNet RUDP boundary and requires the trusted Epiphany executor anchor. Success requires a host-signed `epiphany.operator_command.sealed_result.v1` whose payload digest, command id, packet digest, target runtime, completion time, and executor identity verify exactly; a transport acknowledgement is not success. Admissions and sealed results are persisted in the typed Bifrost operator ledger. Permanent pre-admission refusals are separately terminalized there, while transport failures remain retryable. An expired request cannot mint a new admission, but an exact admission already durably recorded before expiry may be retransmitted to recover Epiphany's one idempotent result without depending on mutable actor-link or signer state. The worker publishes a separate immutable Discord-delivery projection bound to the original request digest and interaction id; it exposes only bounded result/refusal fields and sealed-result fingerprints, never raw signatures or private state. VoidBot reads that outbox and owns Discord response/checkpoint state. Bifrost never manufactures success. Ordinary Persona messages remain `feedback_only` and cannot enter this path. Durable v0 admissions/results are replay-only and cannot acquire the review vocabulary.

Bifrost admits an observation only when guild, channel, target runtime, canonical repository, Persona, producer, and producer runtime match a preconfigured `bifrost.persona_feedback.target_binding.v0` document in Bifrost's private provider store. That binding also owns source visibility, data classification, and the delivery-store target. Bifrost never infers public visibility from Discord identifiers. It classifies the author using its own `bifrost.discord.actor_link.v0` state: a match yields `linked_governance_feedback`; absence yields `unlinked_social_feedback`. Neither class grants adoption, work, release, or deployment authority.

`tools/persona-feedback.mjs enroll-identity` creates the Bifrost service's Ed25519 seed with exclusive creation and mode `0600`, then exports its public MessagePack trust anchor. The private seed belongs under Bifrost's service-private state (default `/var/lib/gamecult/bifrost/persona-feedback/persona-feedback-ed25519.seed`) and is checked for regular-file type, exact Unix mode, and service-user ownership every time it signs. VoidBot publishes pending observations only; it neither pumps admission nor receives the signing path.

The public trust-anchor artifact is a Rust-compatible six-element MessagePack tuple in `HostIdentityTrustAnchorEntry` declaration order: schema version, identity id, 32-byte public key, assurance, identity creation time, and source-record digest. It contains no private material.

The same seed also exposes a separately domain-derived `gamecult.provider_health_identity.trust_anchor.v1` through `export-provider-health-anchor` (or `--provider-health-trust-anchor-out` during first enrollment). This works with the already-enrolled service key and creates the public artifact without replacing an existing file. Persona-feedback health uses that provider-health identity, not the feedback host identity. One `serve` process owns one UUID incarnation and a strictly increasing sequence. Each pulse is the exact 17-field `idunn.signed_daemon_health.v1` tuple, signed over its empty-signature canonical MessagePack form with the `gamecult.provider-health.signature.v1` domain and `idunn.signed_daemon_health.v1` purpose. It carries only the closed provider state and a bounded generic reason. The temporary `idunn.daemon_health` dual publication is explicitly tagged and sourced as an unsigned diagnostic; it cannot substitute for signed admission and can be removed after the deployed Idunn trust binding is live.

The signed `bifrost.persona_feedback.delivery.v0` document matches Epiphany's `BifrostPersonaFeedbackAdmission`: its packet digest is `sha256-` plus SHA-256 of the Rust field-order MessagePack packet tuple, and Ed25519 signs the Epiphany host-identity domain message for purpose `bifrost.persona-feedback.delivery.v0` over the exact admission tuple. The observation provenance is `sourceObserverId=voidbot`; the admission provider is separately and explicitly `provider=bifrost`. Authority is `feedback_only`, and the canonical Epiphany repository target is `GameCult/Epiphany`.

Each binding selects a dedicated provider-owned CultCache store containing only signed delivery documents. The canonical Epiphany path is `/srv/bifrost/persona-feedback/deliveries.cc`; Windows development defaults to `.bifrost/epiphany-persona-feedback.cc`. Observation inbox, Bifrost-private provider state, and delivery store must have distinct canonical paths and distinct existing file identities. Publication is crash-replayable, not cross-store transactional: the signed delivery is inserted idempotently first, then Bifrost records its private delivery copy and immutable receipt. A crash between those writes leaves the source observation unchanged and eligible for replay. Epiphany may consume the delivery store read-only without gaining access to bindings, actor links, receipts, or signing state. CultNet snapshot export remains available.

The Bifrost service runs `tools/persona-feedback.mjs serve` with separate `--observation-store`, private `--store`, private-key, and public `--delivery-store` paths. This bounded resident pump scans immutable source events in observation order, derives unprocessed state solely from absence of an immutable Bifrost receipt, processes each through the same exact binding/signing primitive, and sleeps for `--interval-ms` (default five seconds). Failed events remain eligible for retry; admitted events are never rewritten, deleted, or re-granted. VoidBot does not invoke this mode and requires no read access to Bifrost's private provider state. `--once true` exists for service probes and fixtures.

The deploy contract is one ordinary Bifrost sidecar process with Node.js and the built CultLib CultCache/CultMesh/CultNet runtimes. Provision three separately owned paths: a VoidBot-writable/Bifrost-readable observation inbox, a Bifrost-only private provider store, and the Bifrost-writable/Epiphany-readable delivery store at `/srv/bifrost/persona-feedback/deliveries.cc`. Enroll the Bifrost-only seed once, export its six-field public trust-anchor tuple for Epiphany, create exact target bindings (including visibility, data classification, and delivery path), then start `serve`. The service needs no Discord credential and no Eve surface; it transforms typed organizational feedback observations into authenticated cognition pressure.

`tools/persona-feedback.mjs status` is the non-actuating container readiness probe. It loads the runtime, checks store path and existing-file identity separation, verifies the service-owned key identity, reads bindings without modifying them, and checks each binding's delivery parent/write posture. It emits `bifrost.persona_feedback.readiness.v0` with provider identity, binding count, readiness status, reasons, and `privateStateExposed=false`. It never processes an observation and is not deployment admission.

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
