# Feedback receiver

Status: **design, nothing implemented.** Written for `heimdall-body:follow_up:chat-s3533-3533`. It is the receiver half of
Ratatoskr's Mind feedback pages (Ratatoskr PR #24, `docs/mind-feedback-pages.md` at 8e2ace76) under Heimdall's draft
rulings on guests and hiding (`/opt/commons/handoff/heimdall-feedback-pages-rulings.md`, cited as R4a to R6f). Draft; do not merge.

Read at Bifrost `bf8ff62` (main): `tools/persona-matrix-mouth.mjs`, `tools/persona-chat-documents.mjs`,
`tools/persona-feedback*.mjs`, `src/Bifrost.Web/Features/Heimdall/*`, `Features/Shared/CurrentBifrostActor.cs`,
`Program.cs`, `Domain/BifrostModels.cs`; the deployed trees under `/srv/bifrost`. I could not read Eureka's receiver
notes in #minds; the brief's summary of them (receiver verifies the Heimdall session, append-only answer and
close-request records in its own store, delivery as a framed message through the mouth's pending/ack path, the page
polling Huginn Query) is what I designed against. Where source disagrees with it, section 2 says so.

## 1. What the receiver owns

| Concern | Owner | Receiver's part |
|---|---|---|
| A question, its status, hides, rulings | the owning Mind, in her own mind (Huginn) | reads them to build the projection; writes none |
| An answer, a close request, a report | the author's act | verifies who, stamps `answerer`, `kind`, `time`, appends it |
| What `kind` an account is | Bifrost (`Membership`, later `Role`) | reads it at answer time and records it on the record |
| Delivery to the Mind | the receiver, at least once | keeps the record until the Mind acks |
| Hiding, closing, ruling | the owning Mind | **never**; a hide only changes what the projection serves |
| Redaction on erase | Bifrost's erase, executed here | withdraws the text and name from the store (section 6) |

The page and the client write no record that carries identity (PR #24, section 3). A Matrix sender is a different identity
authority and never becomes `answerer`.

## 2. The mouth's pending/ack path, read from source

Verified in `tools/persona-matrix-mouth.mjs` and `tools/persona-chat-documents.mjs`:

- **Pending is Matrix mail only, held in memory.** `pending` (mouth l.112) is filled in one place, `syncOnce`
  (l.303), from Matrix `/sync` events. No function accepts a delivery from elsewhere.
- **A request is non-consuming; an ack consumes.** `chat_pending_request` carries `afterSeq`; the mouth answers
  `pendingAfter(afterSeq)` (l.331-333) paged to 256 KiB (`pageOf`, l.428-445). `chat_ack` carries `throughSeq`; `ack`
  drops every delivery at or below it (l.326-329). The endpoint accepts exactly these kinds (l.139) and answers a
  request for another Persona with a typed `UnknownPersona` refusal.
- **At-least-once.** The Matrix cursor is persisted only when every delivery of a batch is acked (`commit`, l.316-324), so a
  restart before the ack re-delivers from Matrix. `seqCounter` is persisted whenever a round made deliveries (l.311), so
  sequence numbers never repeat across a restart.
- **Backpressure.** At 500 unacked (`MAX_PENDING`, l.45) `syncOnce` stops reading (l.263). A truncated round leaves the
  cursor where it was (l.304).
- **A delivery that will not encode is skipped, not held.** `pageOf` logs and omits it (l.436-440); the caller's
  next ack covers its seq, so it is dropped. Harmless for a chat line, wrong for a durable record.
- **The endpoint is loopback RUDP.** `startChatEndpoint` refuses a non-loopback host (l.124). It is another process
  from Bifrost.Web, which has no way to hand the mouth anything.
- **The delivery tuple is closed.** `deliveryTuple` and `parseDelivery` are exactly eleven fields, with no source or
  kind field (`persona-chat-documents.mjs` l.117-135). A delivery cannot say it is not a Matrix message.
- **The consumer is not in this repo.** The mouth's header (l.8-9) says the heartbeat polls with a non-consuming
  request and decides who runs. I could not confirm that from source.

What follows from it:

1. **Feedback cannot ride `pending` as it is.** There is no inlet, and the cursor logic ties acks to Matrix batches.
2. **A framed text delivery is forgeable.** Any Matrix member can write a line that looks like a frame, and the tuple has no
   field to tell the Mind which one is real. The frame must be a typed difference in the wire, not a convention in `text`.
3. **A record that is not in Matrix is not re-delivered by a restart.** Feedback needs its own durable store and its own
   delivery cursor.

Options considered for delivery:

| | What | For | Against |
|---|---|---|---|
| A | Inject records into `pending` as synthetic Matrix deliveries | no wire change | forgeable (2); the ack couples to Matrix batches; a restart loses them (3) |
| **B** | **New kinds on the same endpoint:** `gamecult.persona.feedback_pending_request.v1`, `feedback_pending.v1`, `feedback_ack.v1`, with their own seq space and cursor, reading the receiver's store | typed, unforgeable, no change to the v1 and v2 chat tuples, the Matrix cursor logic untouched, the existing request/ack shape the consumer already knows | the consumer must learn three kinds; `checkEnvelope`'s closed list (l.139) grows |
| C | The receiver posts to a Matrix room as a bot | no consumer change | Matrix sender is the wrong identity authority; spam and forgery; the Mind sees it as chat |

Recommended: **B**. Confidence medium: B changes the consumer, which I cannot see.

## 3. Records

One store, owned by the receiver, written only by it. Append-only through the API: nothing edits or deletes a record,
except a redaction (section 6). Every record has an `id`, a receiver-assigned `seq` per owning Mind (the delivery
sequence), and `time` (when the receiver accepted it).

| Record | Fields | Notes |
|---|---|---|
| `answer` | `questionId`, `owner`, `optionId?`, `text?` (at most 2000 characters, R4), `answerer` (Bifrost account id), `kind`, `nameAtAnswer`, `clientRequestId` | at least one of `optionId`/`text`; an unknown option id is refused; the client's request id makes a retry idempotent, as `txnId = request_id` does in the mouth |
| `close_request` | `questionId`, `requester`, `kind` | a request only; refused if `kind` is `guest` or the account is not `active` at that moment (R4c) |
| `report` | `targetId`, `reporter`, `kind`, `reason` | delivered to the Mind as input; never shown, never counted (R6b) |
| `redaction` | `targetId`, `cause` (`erased`), `time` | section 6 |
| delivery state | per owning Mind: highest seq acked; per record: `delivered`, `failed` | a cursor, not a record the page reads |

Invariants: `answerer`, `kind`, `time` and `nameAtAnswer` come from the verified session and Bifrost state, never from
the request body. The latest record per account is that account's standing answer, derived at read time. A record is
refused if the question is not on the page, is hidden, or is settled. A refused submission leaves nothing behind.

## 4. Session check

Bifrost already verifies Heimdall tokens: `HeimdallTokenVerifier.VerifyAsync` (`Features/Heimdall/HeimdallTokenVerifier.cs`)
checks EdDSA against Heimdall's JWKS (a cache refreshed every 30 s, valid at most an hour), then `iss` equals Heimdall,
`aud` and `app.slug` equal the app slug, `typ` is `heimdall_access`, `nbf` and `exp` within 60 s of skew, and
`sub == account_id`. The receiver reuses this and checks nothing the client says about itself.

What source does not give me:

- **The audience.** The verifier demands `aud == options.AppSlug`, Bifrost's slug. Whether Ratatoskr can obtain a Heimdall
  access token for that audience is not in this repo. Ratatoskr's chat session is a Matrix and OIDC session (Bifrost is
  the issuer for MAS, `Features/Oidc`), not a Heimdall one.
- **A bearer endpoint.** Bifrost.Web has cookie sign-in (`Program.cs` l.167-173) and no endpoint that takes a Heimdall
  bearer token. `VerifyAsync` also takes a `payloadAccountId` that only the sign-in response supplies, so a bearer path
  needs a small change to how that cross-check is done.
- **Who the browser page is.** If the feedback page is a Ratatoskr view, the question is which credential its fetches carry.

Two routes, for Eureka and Void to rule on: (1) a bearer endpoint family on Bifrost.Web accepting a Heimdall
access token for the Bifrost audience; (2) the OIDC access token Bifrost already issues to chat, verified by Bifrost
itself. Route 2 needs no Heimdall token from Ratatoskr and ties the answer to the same account the chat session already
has. I lean to 2, with low confidence: I did not read the OIDC token's claims for this.

## 5. Account-kind lookup

- The account is found by `UserAccount.HeimdallAccountId` (`HeimdallAuthService.cs` l.278) or, under route 2, by the OIDC
  subject, which is `UserAccount.Id`. **`answerer` is `UserAccount.Id`**, which survives role change and Heimdall unlink; the
  Heimdall account id is only an authenticator handle and is cleared on erase (guest-accounts design, PR #9).
- On main there is only `Membership.Status` (`Authenticated | PendingApproval | Active | Suspended`) and no role
  (`BifrostModels.cs` l.5-10, l.335). The role field and `gamecult.bifrost.account.v1` are in PR #9 and unbuilt.
  Until then, `kind` can only be derived: `Active` is `member`, `Authenticated` and `PendingApproval` are `user`, and
  nobody is a `guest`. That is the PR #9 backfill, repeated here, and R4a-c have no guest to apply to until PR #9 lands.
- **An answer needs `Status == Active`**, read at answer time. Not `IsActiveMember`: under PR #9 that expression also requires
  `Role == Member` and would exclude guests.
- **`kind` is read from Bifrost's database**, never from the token: the session carries identity only.
- **`mind` is not a Bifrost account** in main or PR #9. How a Mind answers (a Heimdall session of its own, or a path through
  the receiver) is open in both specs.
- **The display name must not be `CurrentBifrostActor.DisplayName`.** It falls back to `GitHubLogin`
  (`CurrentBifrostActor.cs` l.12-16) and then to the literal "Guest". The receiver stores `nameAtAnswer` from
  `MemberProfile.Nickname` or `UserAccount.DisplayName` only, and "an account" when both are empty. Never a provider handle,
  GitHub login, Heimdall id or Matrix localpart.

## 6. Delivery as quoted data

- The receiver delivers each record on the typed kinds of section 2 (option B). A feedback record is therefore never a
  `chat_pending` delivery and a Matrix sender cannot produce one.
- The record carries its fields separately: `recordId`, `questionId`, `kind`, `nameAtAnswer`, `answerer`, `time`, `optionId`,
  `text`. The **consumer renders the frame**, not the receiver, and puts `text` in a data position only: attributed,
  quoted, never in an instruction position, with the author's `kind` stated (R4d). The receiver does no keyword screening.
- **Untrusted by kind.** Text from `guest`, `user` and `patron` is untrusted input; so is `member` text, which differs only in
  weight. The Mind records a ruling from an answer, not an action.
- **Ack means delivered to her conversation**, not read and not ruled on. Delivery is at-least-once, so the consumer
  dedupes by `recordId`.
- **A record that will not encode is marked `failed` and stays visible**, never acked past silently (the opposite of
  `pageOf`). The cap and backpressure of the mouth do not apply: the receiver keeps accepting answers while a Mind has a
  backlog, because they are durable.
- Close requests and reports are delivered the same way. A close request changes nothing until the Mind resolves the
  question; a report changes nothing at all.

## 7. Projection read

The page polls the receiver, and the receiver reads the Mind's side through Huginn (RUDP, as PR #24 section 3 says). That
resolves a seam between sources: my brief said the page polls Huginn Query, while PR #24 says the client never talks to
Huginn. I follow PR #24.

- Questions, their audience, status, rulings and hide acts come from the Mind's mind. Answers come from the receiver's store.
- **A hidden item's text is withheld on the server**, not collapsed in the client: author, kind, time and reason
  stay, the text does not leave the receiver (R6d). A hidden question's title, body, options and answers are withheld.
- Questions and answers are filtered by the viewer's `kind` against the question's `audience` at read time (R4b).
- The projection carries its own `as of`, so the page can say when it is stale.

## 7a. Erase redaction

Bifrost's erase (PR #9) blanks the display name and keeps the account id as a tombstone. The receiver's part:

1. Bifrost erase appends `redaction(cause: erased)` for every record the account authored, then the account is tombstoned.
   The two stores are not one transaction, so erase is crash-replayable: the erase intent is recorded in Postgres first and
   the receiver applies it idempotently, as `docs/bridge.md` describes for the persona-feedback pump.
2. A redacted record keeps `id`, `questionId`, `kind`, `time` and, for an option answer, `optionId` (not personal).
   `text`, `nameAtAnswer` and `clientRequestId` are removed from the store and the projection. The row shows
   "erased account".
3. **I propose no content hash.** R4e suggested one. An unsalted hash of a short free-text answer can confirm a guessed
   text, so I withdraw that: keep nothing of the text.
4. The Mind is told by a `redaction` record on the delivery path, a request she may honour in her own memory. Erase does not
   reach what she already holds, the same limit as `guest-accounts.md` states for chat copies.
5. "Append-only" is the receiver's API contract, not a property of the file. Backups and any witness copy need a stated
   purge or expiry, or redaction is a promise the backups break. Unresolved.

## 8. Where it lives

Bifrost.Web is ASP.NET with Postgres as its store of record and no CultCache writer (`CultCache` appears only in a
comment in `BifrostModels.cs`); the mouth and the persona-feedback pump are Node and read CultCache.

| | Storage | Delivery |
|---|---|---|
| R1 (recommended) | Postgres rows in Bifrost.Web, append-only by construction, `.cc` witness owed as for every Bifrost table (`jurisdiction-map.md`) | the Node chat endpoint reads a loopback-only Bifrost.Web endpoint and serves the section 2 kinds |
| R2 | a CultCache `.cc` written from C# | the Node endpoint reads the file read-only with its own cursor, as `persona-feedback serve` does with its delivery store |

R1 matches where session, account and role already are, and adds no new C# dependency. R2 matches the swarm's state
doctrine, and adds a C# CultCache writer nothing here has. Eureka's call; confidence low.

Naming: `persona-feedback` already means the Discord-to-Epiphany ingress (`docs/bridge.md`, "Organizational Persona
Feedback Ingress"). This receiver's records and kinds use `feedback_page`, not `persona_feedback`.

## 9. Gaps, plainly

- `gamecult.bifrost.account.v1` and `Membership.Role` are unbuilt (PR #9, open, design only). Without them `kind` is
  derived from `Status`, and no guest exists.
- **Deploy is behind main.**
  - The deployed mouth is `/srv/bifrost/persona-chat/runtime/current`, release `e396305`. Its `persona-matrix-mouth.mjs` is
    byte-identical to `e396305`'s (sha256 `91f6dd49...`); main adds 30 lines (the media-line change) to the file.
  - The deployed web app is `/srv/bifrost/app/current` at `67072b2`, 129 commits behind main. That includes the OIDC issuer
    and back-channel logout. I cannot tell from here which of those are live beyond that directory.
  - Anything in this design runs on a release that does not exist yet.
- **The Huginn kinds are Eureka's:** the opt-in with `audience`, `closeGrants`, the close request as the Mind's input,
  hide and unhide, and what the receiver reads to build the projection. This doc names what the receiver needs; it proposes no kinds.
- The consumer of `chat_pending` (heartbeat) is outside this repo, and option B needs it to learn three kinds.
- The credential Ratatoskr presents (section 4) is open, as is how a `mind` answers.
- The mapping of Postgres or `.cc` backups to redaction (section 7a, item 5) is open.
- Heimdall PR #6 (the answer-collector consent doc) is not read; whether guest answers are collected for anything is R4f's
  default, "no", unchecked against it.
- A Synapse or Matrix side is not involved: the receiver writes nothing to Matrix.
