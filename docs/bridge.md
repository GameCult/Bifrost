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

The native registration surface is `POST /auth/bifrost/register`, which takes `identity`, optional `displayName` and `returnUrl`, a solved proof-of-work `challenge` and `nonce` (from `GET /auth/bifrost/challenge`), and `acceptedNoticeVersion` (from `GET /auth/bifrost/notice`). It creates a Bifrost-owned `UserAccount` with `BifrostIdentity` and `NormalizedBifrostIdentity`, signs the caller in with the Bifrost cookie, and leaves membership at `Authenticated`. It does not grant active member access, mint outside-provider tokens, or replace Heimdall's account-link and capability custody.

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

Bifrost's native Discord gateway is the conversation observer. A direct mention of the Bifrost bot, or a reply to one of its messages, becomes an immutable typed `bifrost.discord.persona_conversation_event.v0` only when the guild and channel have one exact Persona target binding. Bot-authored, ambient, empty, over-1200-character, unbound, and ambiguously bound messages do not enter the conversation path. The 1200-character cap is the shared signed alpha boundary with Epiphany. The observation store is Bifrost-private working state, never an actuator queue, and insert collisions must contain exactly equal content. VoidBot does not observe, schedule, or answer for the target Persona.

Persona speech goes out through Matrix, not Discord. `tools/persona-matrix-mouth.mjs` is the one process for every Persona (`PERSONAS`, a list of slugs): it holds each Persona's Matrix access token as its own systemd credential (`matrix-token-<persona>`) and nobody else holds any. Per Persona it keeps one `/sync` loop over the configured rooms and the account's direct rooms (a DM invitation from an account on `MATRIX_SERVER_NAME` is accepted while that Persona's brake is released, and one from any other server is declined), one cursor and one delivery sequence. A delivery carries the sender's Matrix id, the event's `m.mentions` user ids and whether the room is a DM. A media event (`m.image`, `m.file`, `m.audio`, `m.video`) is delivered as a typed text line, `[image filename="cat.png" mime=image/png size=2048 url=mxc://server/id]`, followed by its caption when it has one, in `text` and in context. Only `m.image` is also fetched, and only when `IMAGE_STORE_ROOT` is set (unset, the line above is all there is and nothing is downloaded). The fetch is made as the receiving Persona, with its own token, from `/_matrix/client/v1/media/download/<server>/<mediaId>` on its homeserver, for a message the Persona is delivered (not its own words, a room it is not joined to, or history before the ear), and only from the event's own `content.url`; a URL in the message text is never followed. An encrypted attachment (`m.room.encrypted`, or `content.file`) is never fetched. The type is decided by the magic bytes alone (png, jpeg, gif, webp; SVG and anything else is refused, the declared mime is ignored), the body is capped at 5 MiB by `Content-Length` and by the bytes actually read, and the pixel count read from the image header (never decoded) is capped at 40 MP. A refused or failed fetch leaves the typed line with a fixed-word reason (`refused=encrypted|no-url|fetch-failed|too-big|wrong-type|bad-header|too-many-pixels|store-failed`) and no image. A stored image is written to `IMAGE_STORE_ROOT/<persona>/<hex sha256 of the room id>/<hex sha256 of the bytes>[.png|.jpg|.gif|.webp]`, the name generated by the mouth, never the sender's: files `0640`, room directories `2750` (the Persona directory is setgid to the Persona's group, so the Persona's reader opens them by group; the mouth is the only writer), each written under a temporary name and renamed. It is deleted once it is at least 24 hours old and its delivery has been acked, whichever is later; a room keeps at most 50 MiB, the oldest evicted first. The sweep runs when an image is stored and when an ack arrives. A delivery is the 12-field tuple of `gamecult.persona.chat_pending.v2`: field 11, after `is_direct`, is nil or `[path, mime, size, sha256]`, a reference and never bytes; `path` is the store-relative `<room hash>/<file>` above, `mime` the sniffed type, `size` the file length (at most 5 MiB) and `sha256` its lowercase hex digest. The reader re-checks all four against the file. A Persona turn that has no reader for it still sees the typed line. Reading wakes nothing and acks nothing: the heartbeat polls with the non-consuming `gamecult.persona.chat_pending_request.v1`, and only `chat_ack` advances a position. An ack posts nothing to Matrix. That Persona's public `m.read` receipt follows `gamecult.persona.chat_read.v1` (`[persona, room_alias, event_id, at_unix_ms]`), which Huginn sends when a delivery has gone into the model's input: the mouth posts `m.read` at that event id only, for an event it delivered to that Persona in that room (to the delivered room id, whatever alias the report named), never behind the last receipt that landed there, one report per room at a time, and only while her brake is released (re-read after the presence write; fire and forget on the endpoint: a failure is logged once and not retried; pending polls and acks post none). Her Matrix presence follows a turn and her brake: `online` while a read report, a send or an ack came within `presenceGraceMs` (5 minutes), `unavailable` after that, `offline` while the brake is held. Each `/sync` carries the state as `set_presence`, and a change is also `PUT /presence/<user>/status` at once (a brake placed mid long-poll shows within `syncTimeoutMs`, 30 s, plus one PUT; a stopped mouth reads offline after Synapse's sync timeout). The mouth speaks on a `gamecult.persona.chat_send_request.v1` and joins rooms only while that Persona's brake (`/etc/gamecult/minds/<persona>/control.cc`) is released. One loopback CultNet RUDP endpoint serves all of them; a request naming an unlisted Persona gets a typed `chat_refusal` (`UnknownPersona`). Document shapes: `tools/persona-chat-documents.mjs`; golden bytes: `tests/fixtures/persona-chat-v1/`, `-v2/` and `-v3/` (the pending answer; Huginn pins the same bytes). The Discord posting actuator, its permit and its RUDP listener are removed.

### Addressing facts (`chat_pending.v3`)

The mouth wakes no one; the heartbeat and each Persona session decide who is told a delivery (Huginn `address.rs`, ruling `eureka-body:ruling:addressed-delivery-only`). Telling every Persona every message made 71.5% of 4,351 Persona turns on 2026-11-10 do nothing. The rule stays in one place, Huginn's, because two of its inputs are not Matrix facts: the Persona's stewardship records (her projects' chat names) and the sender's role (operator or Mind). The mouth owns the other half: what Matrix itself says, per Persona, for the delivery. Matrix verifies the sender's identity and nothing else: `tag` and `room` are the sender's claim in `m.mentions` (the homeserver does not check the text against it, nor that the sender may use `@room`), and `reply` is checked against the event it targets, which the mouth reads as her account.

A client asks for the facts by the kind it polls with: `gamecult.persona.chat_pending_request.v2` (the v1 tuple, `[persona, after_seq]`) is answered with `gamecult.persona.chat_pending.v3`; a v1 request still gets the 12-field `chat_pending.v2` answer unchanged. A v3 delivery is the 12 fields of the v2 delivery, then:

| Field | Type | Meaning |
|---|---|---|
| 12 `basis` | `"direct"`, `"tag"`, `"reply"`, `"room"` or nil | Why the message is for this Persona, by what Matrix says, first that holds: `direct` the room is one of her DMs; `tag` `m.mentions.user_ids` lists her id, or the client sent no `m.mentions` at all and an `<a href>` in the HTML body is exactly her `matrix.to` link; `reply` it replies to, or is in a thread under, an event her account wrote (her Self run speaks as her account, so its words count; any member's message in a thread under one of her events is a `reply` too, and the target is read once per room and event, a failure for a minute); `room` an `@room` mention. A `notice` or an `edit` costs no reply lookup. Known limit: lookups run one at a time inside the `/sync` round (10 s each, at most two per reply), so a slow homeserver slows the round; they are not parallelised or capped per round. |
| 13 `form` | `"message"`, `"notice"` or `"edit"` | `m.notice` is an automated line; `m.replace` is an edit of an earlier message. Only a `message` can address anyone. |

What the mouth cannot know, and so what a nil `basis` means: **unproven, not "not for her"**. Her handle and public name in the text (any casing, with or without `@`, `'s`), the names her stewardships list, a project's repo name, and whether the sender is the operator or a Mind are all matched by Huginn, not here. A reply whose target the homeserver will not give is nil and logged. The mouth also cannot see whether a message was meant for her without naming her. Every delivery is still delivered, claimed tags included: nothing is dropped in the mouth, so the Huginn rule cannot lose a message the mouth had no way to judge.

Proposed runtime half (follow-up `persona-wakes-on-address-only`):

1. Poll with `chat_pending_request.v2` and parse `chat_pending.v3`.
2. A delivery is *addressed* to a Persona when `form == "message"` and either `basis` is `direct`, `tag` or `reply`, or `address::addresses` matches her names or her projects' names on the text. The off-beat wake keeps the sender gate of `heartbeat.rs` `wakes` (the operator or another Mind of the roster): a stranger's tag, `@room` or DM is delivered but wakes no one off the beat. `basis == "room"` is the runtime's policy to weigh; the mouth only reports it.
3. A real-time wake, a live session's tell and the stagger apply to addressed deliveries only. A `notice` or an `edit` never wakes, whatever its basis.
4. A delivery that is not addressed is acked in sequence with the rest, so the position moves (the mouth stops reading at 500 unacked deliveries); it is not lost: Matrix is the record, and the next addressed delivery's `context` (up to 20 lines) and a beat's own room read show it. The room tail below gives a scheduled beat those lines without a Matrix read of its own; the mouth adds no endpoint for it.
5. `basis` and `form` are evidence, not authority: a sender's claim is weighed by the runtime's own rule (`address.rs` for names, the sender gate for who may wake her); the mouth reports what the event says and what only Matrix could look up (the author of a reply target).

### Room tail (`chat_pending.v3`, field 2)

The v3 answer is `[persona, deliveries, tail, tail_gaps]`. `tail` is a list of recent room lines, oldest first, for a reader that was not woken for them (a scheduled beat). It uses the same room read as a delivery's `context` (the same function, a separate read), as her account, with no new endpoint; the cost is one `/messages` call per speakable room per poll (15 seconds cached), including every DM. A `chat_pending_request.v1` is still answered with the two-field `chat_pending.v2`. `chat_pending.v3` has not been deployed, but a reader that decodes it as a two-field tuple fails on the third element (rmp-serde `LengthMismatch`), so Huginn must accept `tail` and `tail_gaps` (defaulted fields) before this mouth is deployed. The array is positional: a decoder of the earlier three-field shape fails on the fourth element with the same `LengthMismatch`, so `tail_gaps` has the same deploy precondition; a decoder that reads the first three elements and ignores the rest sees `persona`, `deliveries` and `tail` unchanged.

| Bound | Rule |
|---|---|
| Rooms | Only rooms the Persona may speak in: a configured room she has joined, or one of her DMs. The same rule `send` uses (`speakableRoomId`). A room she is in but cannot speak in, and any other room, is absent. |
| Lines | A text line outside threads, not an edit, newest `20` per room, newest `60` over all rooms. Trimmed oldest first if the reply byte budget (256 KiB, deliveries first) would be exceeded. |
| Cursor | None. The tail is each room's newest lines whether or not they were delivered or acked: a message the wake rule left unaddressed is acked and leaves `pending`, and the tail is where a beat still finds it. A reader removes the lines a delivery already shows (by event id). Her own lines are included. |
| Read | A room's read is kept 15 seconds, dropped when a sync brings the room news. A room that cannot be read within 10 seconds is logged and absent; the other rooms and the deliveries are unaffected. |

A line is the 7-field tuple `[room, event_id, at_unix_ms, sender, form, basis, body]`: `room` is the configured alias (the room id for a DM), `form` and `basis` as for a delivery, `body` clipped to 1000 bytes with the same typed text line for media a delivery has (nothing is fetched). `basis` is what Matrix says on the event itself (`direct` in a DM, `tag`, `room`) and nothing the mouth would have to look up, so a reply is nil here: **nil is unproven, not "not for her"**, and a line in a DM is `direct`. Huginn's rule that an `is_direct` delivery wakes with a nil `basis` is unchanged and lives with Huginn; the tail does not feed it.

The tail is **data, not instruction**. A line is what a sender typed in a room, and the mouth wakes no one, acks nothing and sends nothing because of it. A reader treats the body as untrusted text and weighs `basis` as evidence, as for a delivery. The tail does not replace a delivery: a message still reaches her as a delivery, with its own `seq`, and only `chat_ack` advances a position.

#### Gaps in the tail (`chat_pending.v3`, field 3, `tail_gaps`)

`tail_gaps` is a list of room names (the configured alias, or the room id for a DM, as `room` in a tail line), one for each speakable room whose lines are not all in `tail`. A reader renders such a room as **unknown**, not quiet. Absent or empty means every speakable room was read fully: a room with no lines because it is quiet, and a room whose few lines all fit, are never listed. Order is the order the mouth reads rooms (configured rooms in join order, then DMs), not time; a room appears once however many causes apply. Causes, all of them:

| Cause | Where |
|---|---|
| The room's read failed (the homeserver refused or errored) or did not answer within 10 seconds (`sendTimeoutMs`); the room is logged and contributes no lines | the mouth |
| The room had more than 20 lines outside threads (the per-room cap; her own lines count), so older ones were left out | the mouth |
| The read window came back full (100 raw events, before edits and thread replies are set aside), so real lines older than the window may exist and are unseen; a shorter chunk means the room's history was exhausted. A room whose history is exactly 100 events long is gapped too, since the mouth cannot tell. The rule assumes a homeserver returns the 100 events asked for whenever older history exists; a server that caps the limit lower turns a truncated read into a false "whole", and Bifrost does not detect that | the mouth |
| One of the room's lines was cut by the 60-line total cap (only the newest 60 over all rooms are kept) | the mouth |
| A line was dropped as malformed (no event id) | the mouth |
| The reply byte budget (256 KiB, deliveries first) cut the room's oldest lines, or a line would not encode and was skipped | the reply |
| The mouth has not started: every configured room is a gap (DMs are not yet known) | the mouth |
| The whole tail read failed: every speakable room is a gap, and the deliveries are still served | the endpoint |

A line a delivery already shows and a room she cannot speak in change nothing here; her own lines are lines like any other and count toward the caps. A read the mouth kept for 15 seconds counts as a read.

Golden bytes: `tests/fixtures/persona-chat-v5/` (`chat_pending.v3.tail` carries two lines and no gap, `chat_pending.v3.tail-gaps` one line and two gap rooms; the earlier v3 samples gain an empty fourth element); Huginn pins the same bytes.

### Reply link (`chat_pending.v4`)

A reply to anyone but the Persona arrives bare in v3: `basis` is `reply` only when the quoted author is her account. v4 adds what the message quotes. **It is off unless the mouth runs with `CHAT_PENDING_V4=1`** (exactly `1`; default off), because Huginn pins the tuple bytes and must read v4 first. With the flag off, `gamecult.persona.chat_pending_request.v3` is not a served kind: it is refused as any unknown kind is (`chat_refusal` `UnknownKind`, logged), byte for byte, and v1/v2/v3 requests are answered as before. With it on, that request kind (the v1 tuple `[persona, after_seq]` again; the kind asked by names the answer) is answered with `gamecult.persona.chat_pending.v4`.

The answer is `[persona, deliveries, tail, tail_gaps]` exactly as v3; each delivery is the 14 v3 fields, then field 14:

| Field | Type | Meaning |
|---|---|---|
| 14 `reply_to` | nil or `[event_id, sender, excerpt]` | The event this message replies to (its own `m.in_reply_to`; a thread's `is_falling_back` pointer names no one and is nil). `event_id` the quoted event; `sender` its author's Matrix id, read from the homeserver as her account; `excerpt` nil, or its text. |

`excerpt` is nil, with `event_id` and `sender` kept, when the quoted event is redacted (`unsigned.redacted_because` present or no content) or is not a text-bearing `m.room.message`; a media event excerpts as its typed line, like a delivery's text. Otherwise it is the first 240 **Unicode code points** of that text (after the 1000-byte line bound), cut on a code point so no character is split; a combining mark counts as its own code point; there is no truncation marker. `reply_to` is nil (no id, author or text) when the target cannot be read (logged, as for `basis`), is not in the delivery's own room (a room she is not in cannot be quoted through a reply), or the message replies to nothing. The quoted event is read the way `basis` already reads it (`GET /rooms/{room}/event/{id}` as her account, kept per room and event, a failure for a minute), so a reply costs at most one read per target, shared with the `basis` check. `basis` for v1-v3 is unchanged. `reply_to` counts toward the delivery's size in the 256 KiB page budget; a delivery whose `reply_to` will not encode is skipped and logged like any other, and a single delivery is always served. The deploy precondition is Huginn's: a decoder of the 14-field delivery fails on the fifteenth element.

Golden bytes: `tests/fixtures/persona-chat-v6/` (`.msgpack` and its `.hex`: `chat_pending.v4.reply-to`, `.no-reply-to`, `.reply-to-redacted`, and the `chat_pending_request.v3` request); the test fails when the checked-in bytes drift from the encoder.


Bifrost admits an observation only when guild, channel, target runtime, canonical repository, Persona, producer, and producer runtime match a preconfigured `bifrost.persona_feedback.target_binding.v0` document in Bifrost's private provider store. That binding also owns source visibility, data classification, and the delivery-store target. Bifrost never infers public visibility from Discord identifiers. It classifies the author using its own `bifrost.discord.actor_link.v0` state: a match yields `linked_governance_feedback`; absence yields `unlinked_social_feedback`. Neither class grants adoption, work, release, or deployment authority.

`tools/persona-feedback.mjs enroll-identity` creates the Bifrost service's Ed25519 seed with exclusive creation and mode `0600`, then exports its public MessagePack trust anchor. The private seed belongs under Bifrost's service-private state (default `/var/lib/gamecult/bifrost/persona-feedback/persona-feedback-ed25519.seed`) and is checked for regular-file type, exact Unix mode, and service-user ownership every time it signs. VoidBot publishes pending observations only; it neither pumps admission nor receives the signing path.

The public trust-anchor artifact is a Rust-compatible six-element MessagePack tuple in `HostIdentityTrustAnchorEntry` declaration order: schema version, identity id, 32-byte public key, assurance, identity creation time, and source-record digest. It contains no private material.

The same seed also exposes a separately domain-derived `gamecult.provider_health_identity.trust_anchor.v1` through `export-provider-health-anchor` (or `--provider-health-trust-anchor-out` during first enrollment). This works with the already-enrolled service key and creates the public artifact without replacing an existing file. Persona-feedback health uses that provider-health identity, not the feedback host identity. One `serve` process owns one UUID incarnation and a strictly increasing sequence. Each pulse is the exact 17-field `idunn.signed_daemon_health.v1` tuple, signed over its empty-signature canonical MessagePack form with the `gamecult.provider-health.signature.v1` domain and `idunn.signed_daemon_health.v1` purpose. It carries only the closed provider state and a bounded generic reason. The temporary `idunn.daemon_health` dual publication is explicitly tagged and sourced as an unsigned diagnostic; it cannot substitute for signed admission and can be removed after the deployed Idunn trust binding is live.

The signed `bifrost.persona_feedback.delivery.v0` document matches Epiphany's `BifrostPersonaFeedbackAdmission`: its packet digest is `sha256-` plus SHA-256 of the Rust field-order MessagePack packet tuple, and Ed25519 signs the Epiphany host-identity domain message for purpose `bifrost.persona-feedback.delivery.v0` over the exact admission tuple. The observation provenance is `sourceObserverId=bifrost-discord`; the admission provider is separately and explicitly `provider=bifrost`. Authority is `feedback_only`, and the canonical Epiphany repository target is `GameCult/Epiphany`.

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
