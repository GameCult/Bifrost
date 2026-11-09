# Feedback receiver

Status: **design, nothing implemented.** Written for `heimdall-body:follow_up:chat-s3533-3533`, updated for
`chat-s3549-3549`. It is the receiver half of Ratatoskr's Mind feedback pages. The page spec is Ratatoskr PR #24,
`docs/mind-feedback-pages.md` at b146000d, which I take as final here; its section 7a names this doc. The rulings it
builds on are Heimdall's drafts on guests and hiding (`/opt/commons/handoff/heimdall-feedback-pages-rulings.md`, cited as R4a to R6f).
Draft; do not merge.

Read at Bifrost `bf8ff62` (main), then section 2 and the gaps re-verified at main `55b74275` (the mouth line numbers, the delivery tuple, the ack path and the pending kind all moved; the OIDC, account and `CurrentBifrostActor` files did not): `tools/persona-matrix-mouth.mjs`, `tools/persona-chat-documents.mjs`,
`tools/persona-feedback*.mjs`, `src/Bifrost.Web/Features/Heimdall/*`, `Features/Shared/CurrentBifrostActor.cs`,
`Program.cs`, `Domain/BifrostModels.cs`; the deployed trees under `/srv/bifrost`. I could not read Eureka's receiver
notes in #minds, and `huginn-feedback-kinds-proposal.md` was not in `/opt/commons/handoff` when I looked. Where
PR #24 names a kind or a field it says the proposal wins, so I cite PR #24 and name the proposal's kinds only as PR #24 does.
Where source disagrees with either, the section says so.

## 1. What the receiver owns

| Concern | Owner | Receiver's part |
|---|---|---|
| A question, its listing, status, hides, rulings | the owning Mind, in her own mind (Huginn) | reads them to build the projection; **never writes to Huginn** (an admission for another instance is refused `ForeignInstance`, PR #24 section 3) |
| An answer's **text** | the receiver | stores it; it is never admitted into a mind |
| The answer's **record** in the Mind | the owning Mind's Persona | she admits a text-free `feedback_answer` from the delivery, and acks after she has |
| What `kind` an account is | Bifrost (`Membership`, later `Role`) | reads it at answer time and stamps it |
| Delivery to the Mind | the receiver, at least once | keeps the record until the Persona acks |
| Hiding, closing, ruling | the owning Mind | **never**; a hide only changes what the projection serves |
| Erase | Bifrost's erase, executed here | drops the text and name (section 7a) |

The page and the client write no record that carries identity (PR #24, section 3). A Matrix sender is a different identity
authority and never becomes `answerer`.

## 2. The mouth's pending/ack path, read from source

Verified in `tools/persona-matrix-mouth.mjs` and `tools/persona-chat-documents.mjs`:

- **Pending is Matrix mail only, held in memory.** `pending` (mouth l.123) is filled in one place, `syncOnce`
  (l.285), from Matrix `/sync` events. No function accepts a delivery from elsewhere.
- **A request is non-consuming; an ack consumes.** `chat_pending_request` carries `afterSeq`; the mouth answers
  `pendingAfter(afterSeq)` (l.383) paged to 256 KiB (`pageOf`, l.483-498). `chat_ack` carries `throughSeq`; `ack`
  drops every delivery at or below it (l.374-381). The endpoint accepts exactly these kinds (l.520) and answers a
  request for another Persona with a typed `UnknownPersona` refusal.
- **At-least-once.** The Matrix cursor is persisted only when every delivery of a batch is acked (`commit`, l.351-359), so a
  restart before the ack re-delivers from Matrix. `seqCounter` is persisted whenever a round made deliveries (l.346), so
  sequence numbers never repeat across a restart.
- **Backpressure.** At 500 unacked (`MAX_PENDING`, l.53) `syncOnce` stops reading (l.294). A truncated round leaves the
  cursor where it was (l.305, l.339).
- **A delivery that will not encode is skipped, not held.** `pageOf` logs and omits it (l.489-491); the caller's
  next ack covers its seq, so it is dropped. Harmless for a chat line, wrong for a durable record.
- **The endpoint is loopback RUDP.** `startChatEndpoint` refuses a non-loopback host (l.505). It is another process
  from Bifrost.Web, which has no way to hand the mouth anything.
- **The delivery tuple is closed.** `deliveryTuple` and `parseDelivery` are exactly twelve fields (the twelfth, added since `bf8ff62`, is an optional stored-image reference), with no source or
  kind field (`persona-chat-documents.mjs` l.120-160). The pending answer is now `gamecult.persona.chat_pending.v2`. A delivery cannot say it is not a Matrix message.
- **The consumer is not in this repo.** The mouth's header (l.8-9) says the heartbeat polls with a non-consuming
  request and decides who runs. I could not confirm that from source.

What follows from it:

1. **Feedback cannot ride `pending` as it is.** There is no inlet, and the cursor logic ties acks to Matrix batches.
2. **A framed text delivery is forgeable.** Any Matrix member can write a line that looks like a frame, and the tuple has no
   field to tell the Mind which one is real. The frame must be a typed difference in the wire, not a convention in `text`.
3. **A record that is not in Matrix is not re-delivered by a restart.** Feedback needs its own durable store and its own
   delivery cursor.

PR #24 section 3 names the carrier: a new delivery document, `gamecult.persona.feedback_delivery.v1`, owned by Bifrost, through
the mouth's pending/ack path. Source adds one constraint on how it rides:

- **It cannot be a variant inside `chat_pending`.** `parseDelivery` accepts exactly eleven fields
  (`persona-chat-documents.mjs` l.149, twelve fields) and `pageOf` encodes each delivery with `deliveryTuple` (l.120), so a different
  tuple in that page breaks every current consumer, and a text frame in `text` is forgeable by any Matrix member.
- **Option B, kept:** the delivery document rides its own request, page and ack kinds on the same endpoint
  (`feedback_pending_request`, `feedback_pending`, `feedback_ack`), with its own seq space and a durable cursor over the
  receiver's store. The Matrix kinds, the Matrix cursor and `MAX_PENDING` are untouched. The endpoint's closed kind list
  (l.520) grows by three. Confidence medium: the consumer, which I cannot see, must learn them.
- **Why main's changes reinforce B (re-verified at `55b74275`).** A chat `ack` now has Matrix side effects: it posts the Persona's public `m.read`
  receipt per room (`readUpTo`, l.372-381) and releases stored images (`images?.acked`). A feedback ack must do neither, so it cannot share the
  `chat_ack` kind or its `ack` function. The chat pending answer is already versioned (`chat_pending.v2`, twelve-field tuple), so the lesson of the
  image change is that a new field in that tuple is a new document version for every consumer, which a feedback record must not force.
- Rejected: injecting records into `pending` as synthetic Matrix deliveries (no inlet, forgeable, lost on restart);
  posting to a Matrix room as a bot (wrong identity authority, spam and forgery, and the Mind would read it as chat).

## 3. Records

One CultCache file, owned and written only by the receiver (section 8). Append-only through the API: nothing edits or
deletes a record except the redaction of section 7a. Every record has a `recordId` and a receiver-assigned `seq`
per owning Mind (the delivery sequence). PR #24 names the answer row `<question-label>.a<N>`, N the receiver's per-question
sequence; I use that for answers and the same shape for the others.

| Record | Fields | Notes |
|---|---|---|
| `answer` | `recordId`, `questionId`, `owner`, `choice?` (an option label), `text?` (at most 2000 characters), `answerer`, `kind`, `weightPermille`, `nameAtAnswer`, `clientRequestId`, `time` | at least one of `choice`/`text`; an unknown option label is refused; the request id makes a retry idempotent, as `txnId = request_id` does in the mouth; `weightPermille` is 0 for a guest and 1000 otherwise, stamped, and the page sums nothing |
| `close_request` | `recordId`, `questionId`, `requester`, `kind`, `time` | refused for a guest and for an account not `active` at that moment (R4c) |
| `report` | `recordId`, `targetId`, `reporter`, `kind`, `reason`, `time` | delivered to the Mind as input; never shown, never counted (R6b) |
| delivery state | per owning Mind: highest seq acked; per record: `delivered`, `failed` | a cursor, not a record the page reads |

Invariants: `answerer`, `kind`, `weightPermille`, `time` and `nameAtAnswer` come from the verified token and Bifrost state, never from the
request body, and the body schema is closed: an unknown key refuses the submission. The latest record per account is that
account's standing answer, derived at read time. A submission is refused if the question has no listing in force or is hidden.
An answer to a question that has settled is kept and the page says so (PR #24 section 8). A refused submission leaves nothing behind.

## 4. Credential and claims check

PR #24 section 7a rules **route 2**: Ratatoskr presents the OIDC access token Bifrost already issues to chat, and Bifrost
verifies it itself. This section records what Bifrost source says about that token, because PR #24 lists the claims check as open.

### What the token carries (`OidcEndpoints.cs` l.35-79, `docs/oidc-issuer.md`)

| Claim | Value | In the access token | Use by the receiver |
|---|---|---|---|
| `sub` | `UserAccount.Id`, formatted `D` | always | **`answerer`**, and the only claim the receiver reads |
| `preferred_username` | `NormalizedBifrostIdentity` (the Matrix localpart, minted once) | with scope `profile` | never: it is the chat handle |
| `name` | `DisplayName`, else `BifrostIdentity` | with scope `profile` | never: it is stale at issue time and can be a handle. The name comes from account state (section 5) |
| scopes | copied from the request: `openid`, `profile` | yes | ignored |

No email, no Heimdall id, no GitHub id and no membership detail is in it. The GitHub login and the Heimdall account id are
not claims, so neither can become `answerer` or a display name by way of the token.

### What the receiver checks

- **Validity and expiry.** The access token is an encrypted OpenIddict token (`AddEncryptionKey`, `OidcServiceCollectionExtensions.cs` l.51;
  no reference tokens, no lifetime override anywhere in `src`). Only Bifrost can open it, so validity, issuer binding and expiry are
  OpenIddict's own checks at validation time. The lifetime is OpenIddict's default, which I believe is one hour but have not
  verified. There is no refresh token (`oidc-issuer.md`), so a session ends at expiry and the client signs in again.
- **Issuer.** `server.Issuer` is `Host:PublicBaseUrl` (l.60-68). A token from any other issuer cannot be opened, so there is nothing to compare.
- **Audience.** None is set: no `SetAudiences` or `SetResources` appears in `src`, so the token carries no `aud` of Bifrost's choosing. The receiver
  cannot check an audience, and a token is not bound to the feedback endpoint. **That is a weakness of route 2 as it stands:** any access token
  this issuer has given any client would be accepted here. A scope for this purpose (below) narrows it.
- **The subject is an active account, read now.** `sub` must parse as a Guid, name an existing `UserAccount`, and that account's `Status` must be
  `Active` at answer time. This is the reading `IsStillActiveMemberAsync` makes (`OidcEndpoints.cs` l.121-132), but **not** through
  `IsActiveMember`: under PR #9 that expression also requires `Role == Member` and would exclude guests, and PR #9 itself moves the OIDC tests
  to `Status == Active` for the same reason.

### What the receiver refuses

A token that does not open or has expired; a `sub` that is not a Guid or names no account; an account that is not `Active`;
a submission whose body carries `answerer`, `kind`, `name`, `weight` or any key the closed schema does not name; an unknown option label;
a question with no listing in force, or hidden; a close request from a guest.

### What source says that route 2 has to answer

1. **The token reaches only MAS.** Exactly one OIDC client is registered, a confidential one for MAS, authorization code with PKCE and a
   client secret (`OidcClientSeeder.cs` l.28-44), and every other application row is deleted at startup (l.56-68). MAS holds the access token;
   Ratatoskr, a Matrix client, gets a Matrix session from MAS, not a Bifrost token. Nothing in this repo gives Ratatoskr a Bifrost access token.
2. **Possible readings of "the token chat already has":**
   - **2a. Ratatoskr as a second client of Bifrost's issuer,** public, authorization code with PKCE, and its own scope (for example `feedback`).
     Same `sub`, same checks, a purpose-limited scope the receiver can require. It changes the one-client rule in `oidc-issuer.md`, the seeder's delete
     of other clients, the need to validate access tokens outside the userinfo endpoint (the web project references only `OpenIddict.Server.AspNetCore`
     and `OpenIddict.EntityFrameworkCore`, not the validation package), and gives sign-in every hour with no refresh.
   - **2b. The Matrix token Ratatoskr already holds,** checked by asking the homeserver who it is. No new client, but it hands the receiver a bearer
     token good for the person's whole Matrix account. I recommend against.
   - I lean to **2a**, confidence low: I could not read MAS's side, and `chat-s3549` says route 2 without naming which.
3. **The logout reconciler does not cover a second client.** It lists the authorizations of the MAS client (`oidc-issuer.md`), so a demoted
   member's feedback token would live to its expiry. The receiver's own `Status == Active` read at every submission is the check that covers it.

## 5. Account-kind lookup

- The account is found by the token's `sub`, which is `UserAccount.Id` (section 4). **`answerer` is `UserAccount.Id`**, which survives role
  change and Heimdall unlink; the Heimdall account id is only an authenticator handle and is cleared on erase (guest-accounts design, PR #9).
- On main there is only `Membership.Status` (`Authenticated | PendingApproval | Active | Suspended`) and no role
  (`BifrostModels.cs` l.5-10, l.335). The role field and `gamecult.bifrost.account.v1` are in PR #9 and unbuilt.
  Until then, `kind` can only be derived: `Active` is `member`, `Authenticated` and `PendingApproval` are `user`, and
  nobody is a `guest`. That is the PR #9 backfill, repeated here, and R4a-c have no guest to apply to until PR #9 lands.
- **An answer needs `Status == Active`**, read at answer time, not `IsActiveMember` (section 4). On main the authorize endpoint
  already refuses a non-Active account a chat token (`OidcEndpoints.cs` l.50-53), so under route 2 only an Active account can answer
  until PR #9 changes that test: `Authenticated` and `PendingApproval` accounts have no token to answer with.
- **`kind` is read from Bifrost's database**, never from the token: the session carries identity only.
- **`mind` is not a Bifrost account** in main or PR #9. How a Mind authenticates to answer (a credential of its own, or a path through
  the receiver) is open in both specs.
- **The display name must not be `CurrentBifrostActor.DisplayName`.** It falls back to `GitHubLogin`
  (`CurrentBifrostActor.cs` l.12-16) and then to the literal "Guest". The receiver stores `nameAtAnswer` from
  `MemberProfile.Nickname` or `UserAccount.DisplayName` only, and "an account" when both are empty. Never a provider handle,
  GitHub login, Heimdall id or Matrix localpart.

## 6. Delivery as quoted data

- The receiver delivers each record as a `gamecult.persona.feedback_delivery.v1` document on the typed kinds of section 2. A feedback
  record is therefore never a `chat_pending` delivery and a Matrix sender cannot produce one.
- The delivery carries its fields separately: `recordId`, `questionId`, `kind`, `nameAtAnswer`, `answerer`, `time`, `choice`, `text`. The
  **Persona's side renders the frame**, not the receiver, and puts `text` in a data position only: attributed, quoted, never in an instruction
  position, with the author's `kind` stated (R4d). The receiver does no keyword screening.
- **She admits the text-free `feedback_answer` into her own mind and acks only after that** (PR #24 section 3). The text reaches her turn once, in the
  delivery, and is never admitted; she cites the record id, not the text. A restart redelivers the same `recordId`, which Huginn answers
  `AlreadyAdmitted`. So an ack means *admitted*, a stronger meaning than the mouth's chat ack, which means only *taken*.
- **Untrusted by kind.** Text from `guest`, `user` and `patron` is untrusted input; so is `member` text, which differs only in weight. She records a
  ruling from an answer, never an action.
- **A record that will not encode, or that she cannot admit, is marked `failed` and stays visible**, never acked past silently (the opposite of
  `pageOf`). The mouth's cap and backpressure do not apply: the receiver keeps accepting answers while a Mind has a backlog, because they are durable.
- Close requests and reports are delivered the same way. A close request changes nothing until the Mind resolves the question; a report changes nothing.
- **An erase notice is delivered the same way** (section 7a), and she answers it with a `feedback_hide` of reason `ErasedAuthor`.

## 7. Projection read

The page polls the receiver, and the receiver reads the Mind's side through Huginn over RUDP and writes nothing to it
(PR #24 section 3). The page does not talk to Huginn.

- Questions, their listing (`audience`, `close_grants`), status, rulings and hide acts come from the Mind's mind. Answer text and the name come from the receiver's store; an answer row is assembled from the receiver's record and the Mind's text-free `feedback_answer`.
- **A hidden item's text is withheld on the server**, not collapsed in the client: author, kind, time and reason
  stay, the text does not leave the receiver (R6d). A hidden question's title, body, options and answers are withheld.
- Questions and answers are filtered by the viewer's `kind` against the question's `audience` at read time (R4b).
- The projection carries its own `as of`, so the page can say when it is stale.

## 7a. Erase

Bifrost's erase (PR #9) blanks the display name and keeps the account id as a tombstone. The receiver's part, as PR #24 section 7 fixes it:

1. Bifrost's erase records its intent in Postgres first, then the receiver applies it idempotently. The two stores are not one transaction, so
   erase is crash-replayable, as `docs/bridge.md` describes for the persona-feedback pump.
2. For every record the account authored the receiver **drops the text and the name** and keeps **record id, kind and time only**. `choice`,
   `weightPermille`, `answerer` and `clientRequestId` are dropped too. The row shows "erased account". **No content hash is kept.**
3. The receiver delivers an erase notice, and the Persona admits a `feedback_hide` of reason `ErasedAuthor`: the Mind's acknowledgement, not a moderation call.
4. **The honest residue:** a Mind's own memory or ruling may already quote an answer, and Huginn cannot redact it. That the Persona cites record ids and
   never text is a prompt, not an invariant. Erase does not reach what she already holds.
5. **Backups.** "Append-only" is the receiver's API contract, not a property of the file; erasing the live file leaves the text in every backup of it.
   **A stated purge or expiry for the receiver's backups is required before any deploy**, so erased text does not survive in them. The number is
   Eureka's and Heimdall's to set; this doc names none. Until it exists, redaction is a promise the backups break.

## 8. Where it lives

**A CultCache file**, owned and written only by the receiver (Eureka's pick, PR #24 section 7a). Bifrost.Web is ASP.NET with Postgres as its
store of record and has no CultCache writer (`CultCache` appears only in a comment in `BifrostModels.cs`); the mouth and the persona-feedback pump
are Node and read CultCache. So the shape that fits what exists:

- the receiver writes the file (a C# CultCache writer is new to Bifrost.Web, or a Node writer behind a loopback call; I did not decide which, and
  it is Eureka's), only it writes, and the Node chat endpoint reads it read-only with its own delivery cursor in a separate file, the way
  `persona-feedback serve` reads its delivery store and the mouth keeps `cursor.cc`;
- session check, account state and role stay in Bifrost.Web (Postgres), where they already are.

I withdraw my earlier lean to Postgres rows: it contradicts the ruled store, and the `.cc` witness it would have owed is what the file is.

Naming: `persona-feedback` already means the Discord-to-Epiphany ingress (`docs/bridge.md`, "Organizational Persona Feedback Ingress"). This
receiver's records and kinds use `feedback_page` and `feedback_delivery`, not `persona_feedback`.

## 9. Gaps, plainly

- **`gamecult.bifrost.account.v1` and `Membership.Role` are unbuilt** (PR #9, open, design only). Without them `kind` is derived from `Status`, and no
  guest exists.
- **Route 2 has no client yet.** The one OIDC client is MAS, so no Bifrost access token reaches Ratatoskr today (section 4). Route 2 needs a second
  client or another reading; the token has no audience; a validation package is not referenced; there is no refresh.
- **Deploy is behind main.**
  - The deployed mouth is `/srv/bifrost/persona-chat/runtime/current`, release `e396305`. Its `persona-matrix-mouth.mjs` is byte-identical to
    `e396305`'s (sha256 `91f6dd49...`); main has since added the image path (cut A), a public `m.read` receipt posted on every chat ack, and presence set from the brake, which are 82 changed lines in the mouth and a twelfth delivery field.
  - The deployed web app is `/srv/bifrost/app/current` at `67072b2`, 129 commits behind main. That includes the OIDC issuer and back-channel logout.
    I cannot tell from here which of those are live beyond that directory.
  - Anything in this design runs on a release that does not exist yet.
- **The Huginn kinds are Eureka's.** PR #24 names `feedback_listing`, `feedback_answer`, `feedback_close_request`, `feedback_hide`, owned by
  `huginn-feedback-kinds-proposal.md`, which I could not read. PR #24 also notes the proposal's `feedback_answer` lists a `content_hash` that must go.
  Their dependency order is Huginn readers, then the Persona organ, then this receiver, so nothing here can ship first.
- **How a Mind answers is open.** Bifrost has no `Mind` role; neither main nor PR #9 gives a Mind an account.
- **The consumer of the mouth's pending path** (the heartbeat) is outside this repo, and option B needs the Persona side to learn three kinds.
- **Backup purge or expiry** is required before deploy and unset (section 7a, item 5).
- **Erase is bounded** by what a Mind already holds (section 7a, item 4).
- **Unread:** Eureka's receiver notes in #minds, `huginn-feedback-kinds-proposal.md`, MAS's side of the session, and Heimdall PR #6 (the
  answer-collector consent doc; the page's "guest answers are collected for nothing but delivery" is unchecked against it).
- The receiver writes nothing to Matrix and nothing to Huginn.
