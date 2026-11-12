# Feedback receiver

Status: **design, nothing implemented.** Written for `heimdall-body:follow_up:chat-s3533-3533`, updated for
`chat-s3549-3549`. It is the receiver half of Ratatoskr's Mind feedback pages. The page spec is Ratatoskr PR #24,
`docs/mind-feedback-pages.md` at b146000d, which I take as final here; its section 7a names this doc. The rulings it
builds on are Heimdall's drafts on guests and hiding (`/opt/commons/handoff/heimdall-feedback-pages-rulings.md`, cited as R4a to R6f).
Draft; do not merge.

Read at Bifrost main `b1725a1` (2026-11-12): `tools/persona-matrix-mouth.mjs`, `tools/persona-chat-documents.mjs`,
`src/Bifrost.Web/Features/{Oidc,Desk,Registration,Shared}/*`, `Domain/BifrostModels.cs`, `Program.cs`, `docs/oidc-issuer.md`,
`docs/ratatoskr-voting-auth.md`; the deployed trees under `/srv/bifrost` were last read at an earlier main (section 9). I could not read Eureka's receiver
notes in #minds, and `huginn-feedback-kinds-proposal.md` was not in `/opt/commons/handoff` when I looked. Where
PR #24 names a kind or a field it says the proposal wins, so I cite PR #24 and name the proposal's kinds only as PR #24 does.
Where source disagrees with either, the section says so.

Rulings of Meta, 2026-11-12, now written into this doc: erase redacts the answer text in the receiver's store and keeps a text-free record
(section 7a); the training notice covers any text entered into the app, bar a short exception list Eureka drafts (section 4a); the host may
withhold an answer from its Mind in an emergency, with a logged receipt (section 6a); Minds authenticate to the receiver through an OIDC
client with an audience, as the desk does (section 4b). Eureka's decisions on forks 2 to 5 of 2026-11-12 (`/opt/commons/handoff/bifrost-10-forks-2-5-recs-20261112.md`)
are folded into sections 2, 4b, 6a, 7a and 9 and close those forks, except where section 9 says a point is still open.

## 1. What the receiver owns

| Concern | Owner | Receiver's part |
|---|---|---|
| A question, its listing, status, hides, rulings | the owning Mind, in her own mind (Huginn) | reads them to build the projection; **never writes to Huginn** (an admission for another instance is refused `ForeignInstance`, PR #24 section 3) |
| An answer's **text** | the receiver | stores it; it is never admitted into a mind |
| The answer's **record** in the Mind | the owning Mind's Persona | she admits a text-free `feedback_answer` from the delivery, and acks after she has |
| What `kind` an account is | Bifrost (`Membership`, later `Role`) | reads it at answer time and stamps it |
| Delivery to the Mind | the receiver, at least once | keeps the record until the Persona acks |
| Hiding, closing, ruling | the owning Mind | **never**; a hide only changes what the projection serves |
| Erase | Bifrost's erase, executed here | redacts the text and name in the store, keeps a text-free record (section 7a) |
| Withholding an answer from its Mind | the host (emergency only) | logs a text-free withhold record and skips delivery; changes nothing stored or ruled (section 6a) |
| Who may answer | Bifrost: `Active`, a live training-notice receipt | checks both on every submission with text (section 4a) |

The page and the client write no record that carries identity (PR #24, section 3). A Matrix sender is a different identity
authority and never becomes `answerer`.

## 2. The mouth's pending/ack path, read from source

Verified in `tools/persona-matrix-mouth.mjs` and `tools/persona-chat-documents.mjs` at main `b1725a1`:

- **Pending is Matrix mail only, held in memory.** `pending` (mouth l.144) is filled in one place, `syncOnce`
  (l.441; `pending.push(...made)` l.496), from Matrix `/sync` events. No function accepts a delivery from elsewhere.
- **A request is non-consuming; an ack consumes.** The pending requests carry `afterSeq`; the mouth answers
  `pendingAfter(afterSeq)` (l.614) paged to 256 KiB (`pageOf`, l.944-972; `limits.replyBytes`, documents l.53). `chat_ack` carries `throughSeq`; `ack`
  (l.556-561) drops every delivery at or below it, releases stored images (`images?.acked`) and advances the Matrix cursor. An ack posts nothing to
  Matrix: her public `m.read` receipt follows a separate `chat_read` document (header l.11-13, `chat_read` branch l.1027). The endpoint serves a closed list of kinds
  (`served`, l.992) and answers any other loudly with `chat_refusal` `UnknownKind` (l.999-1001); a request for another Persona gets `UnknownPersona`.
- **At-least-once.** The Matrix cursor is persisted only when every delivery of a batch is acked (`commit`, l.513-521), so a
  restart before the ack re-delivers from Matrix. `seqCounter` (l.143) is persisted whenever a round made deliveries (l.508), so
  sequence numbers never repeat across a restart.
- **Backpressure.** At 500 unacked (`MAX_PENDING`, l.58) `syncOnce` stops reading (l.447). A truncated round (pending cap l.475, reply-read cap l.476)
  leaves the cursor where it was (l.501).
- **A delivery that will not encode is skipped, not held.** `pageOf` logs and omits it (l.949-951); the caller's
  next ack covers its seq, so it is dropped. Harmless for a chat line, wrong for a durable record.
- **The endpoint is loopback RUDP.** `startChatEndpoint` (l.980) refuses a non-loopback host (l.981). It is another process
  from Bifrost.Web, which has no way to hand the mouth anything.
- **The delivery tuple is closed and versioned.** `deliveryTuple` is twelve fields (the twelfth is an optional stored-image reference), the pending answer
  `gamecult.persona.chat_pending.v2` (`persona-chat-documents.mjs` l.216-224). v3 adds `basis` and `form` (14 fields) and a room tail (`chat_pending.v3`,
  asked for by `chat_pending_request.v2`); v4 adds `reply_to` (15 fields, `chat_pending_request.v3`, served only with `CHAT_PENDING_V4=1`, l.1074). No version has a source or
  kind field. A delivery cannot say it is not a Matrix message.
- **A fetch kind exists, off by default.** `chat_fetch_request` is served only with `CHAT_FETCH=1` (l.992, l.1076): an optional served kind is a precedent for adding kinds
  to `served`, and the way a kind is withheld until its consumer is ready.
- **The consumer is not in this repo.** The mouth's header (l.8-11) says the heartbeat polls with a non-consuming
  request and decides who runs. I could not confirm that from source.

What follows from it:

1. **Feedback cannot ride `pending` as it is.** There is no inlet, and the cursor logic ties acks to Matrix batches.
2. **A framed text delivery is forgeable.** Any Matrix member can write a line that looks like a frame, and no tuple has a
   field to tell the Mind which one is real. The frame must be a typed difference in the wire, not a convention in `text`.
3. **A record that is not in Matrix is not re-delivered by a restart.** Feedback needs its own durable store and its own
   delivery cursor.

PR #24 section 3 names the carrier: a new delivery document, `gamecult.persona.feedback_delivery.v1`, owned by Bifrost, through
the mouth's pending/ack path. Source adds one constraint on how it rides:

- **It cannot be a variant inside `chat_pending`.** Each version is a closed positional tuple (twelve, fourteen or fifteen fields) encoded by
  `pageOf` (l.949), so a different tuple in that page breaks every current consumer, and a text frame in `text` is forgeable by any Matrix member.
- **Option B, kept in shape:** the delivery document has its own request, page and ack kinds (`feedback_pending_request`, `feedback_pending`, `feedback_ack`), its own seq space and
  a durable cursor over the receiver's store. The Matrix kinds, the Matrix cursor and `MAX_PENDING` are untouched. Per the decision below the kinds are served by the receiver over
  HTTPS, not added to the mouth's `served` list (l.992). Confidence medium: the Persona-side consumer, which I cannot see, must learn them.
- **Why main's changes reinforce B.** A chat `ack` releases stored images and advances the Matrix `/sync` cursor (l.556-561). A feedback ack must do neither, so it cannot
  share the `chat_ack` kind or its `ack` function. The chat pending answer is already versioned three times, so a new field in that tuple is a new
  document version for every consumer, which a feedback record must not force.
- Rejected: injecting records into `pending` as synthetic Matrix deliveries (no inlet, forgeable, lost on restart);
  posting to a Matrix room as a bot (wrong identity authority, spam and forgery, and the Mind would read it as chat).
- **Decided (Eureka, 2026-11-12): feedback does not ride the mouth.** The endpoint is loopback RUDP with no bearer and trusts the persona id in the document, so
  routing feedback through it would make the Matrix organ a bearer-less deputy holding every Mind's authority and would put the receiver's own seq space and durable
  cursor in a Matrix process. A Mind pulls and acks **directly over HTTPS from her own process to the receiver, with the section 4b bearer**; she admits the text-free
  record into her own mind afterwards. The three kinds `feedback_pending_request`, `feedback_pending`, `feedback_ack` are therefore HTTP endpoints of the receiver, not
  kinds added to `served`; Option B's "same endpoint" clause is withdrawn and the mouth is untouched. The delivery document and its seq space are as above. This is a
  Bifrost HTTP service, a xenos boundary for CultNet, so JSON/HTTP there is the tolerated kind. Pre-deploy test: section 9.

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
| `withhold`, `withholdLifted` | `recordId`, `answerRecordId`, `owner`, `withheldBy` or `liftedBy`, `time`, `reasonCode` (withhold only) | text-free log of section 6a |
| delivery state | per owning Mind: highest seq acked; per record: `delivered`, `failed`, `withheld` | a cursor, not a record the page reads |

Invariants: `answerer`, `kind`, `weightPermille`, `time` and `nameAtAnswer` come from the verified token and Bifrost state, never from the
request body, and the body schema is closed: an unknown key refuses the submission. The latest record per account is that
account's standing answer, derived at read time. A submission is refused if the question has no listing in force or is hidden.
An answer to a question that has settled is kept and the page says so (PR #24 section 8). A refused submission leaves nothing behind.

## 4. Credential and claims check

PR #24 section 7a rules **route 2** for people: Ratatoskr presents an OIDC access token that Bifrost issued, and Bifrost verifies it itself. Meta
ruled on 2026-11-12 that Minds authenticate to the receiver through an OIDC client with an audience, as the desk does (4b). This section records what
Bifrost's source does on main `b1725a1`.

### What main does with tokens

| Token | Who may get it | Gate at issue | Gate on use |
|---|---|---|---|
| **Chat token** (scopes `openid`, `profile`; no audience) | an account that exists, has a Bifrost identity that is not reserved, and whose membership is not `Suspended`; membership is neither required nor written (`OidcEndpoints.cs:55-78`) | a live training-notice receipt: without one the member is redirected to the consent page and the parked request waits (`:104-111`) | the token and userinfo endpoints re-read it: `ChatRefusalAsync` refuses an absent or `Suspended` account and a chat token with no live receipt (`:164-180`, called at `:127` and `:144`) |
| **Desk token** (every scope is `desk.read` or `desk.answer`, `DeskEndpoints.cs:21-26`) | the same sign-in, then only a client whose permissions allow the desk scope | **no receipt**: `IsDeskToken` is true only when every scope is a desk scope (`:118`), so a desk token skips the gate (`:104`, `:108`); `principal.SetResources(DeskEndpoints.Audience)` puts audience `bifrost-desk` on it (`:91-94`) | per request, in `DeskEndpoints.HandleAsync`: audience (`DeskEndpoints.cs:110`), scope (`:116-121`), then `Active` membership re-read through `OidcEndpoints.ActiveMemberIdAsync` (`:123-139`; `OidcEndpoints.cs:183-194`, which is `CurrentBifrostActor.IsActiveMember`, `Status == Active`, `CurrentBifrostActor.cs:17`) |

The token claims are unchanged: `sub` is `UserAccount.Id` in form `D`; `preferred_username` and `name` ride the token only with scope `profile`
(`OidcEndpoints.cs:80-102`). No email, Heimdall id or GitHub id is in it. The access token is an encrypted OpenIddict token and its lifetime is OpenIddict's
default, which I believe is one hour and have not verified (no lifetime override in `src`; `ratatoskr-voting-auth.md` section 14 D3 owes explicit values). There is no refresh token.

How a bearer is validated has changed: OpenIddict's validation handler is registered against the local server (`DeskEndpoints.cs:36-38`), reading the
`Authorization` header only (`:40-44`), and `OidcDeskCredential` authenticates through it by its own scheme name and judges nothing else
(`OidcDeskCredential.cs:14-27`; audience, scope and membership are the desk's, per its header comment). That path is registered only when `Bifrost:Oidc` is configured (`:30-33`).

**Only one client exists.** The seeder creates the MAS client and deletes every other application at startup (`OidcClientSeeder.cs:35-51`, `:63-75`). MAS's permissions
carry no desk scope (`:42-50`), so **no desk token can be issued on main**: the desk's second client is owed (`ratatoskr-voting-auth.md` section 14 D7). `AllowAuthorizationCodeFlow` is the only
grant and the token endpoint takes `client_secret_basic` only (`OidcServiceCollectionExtensions.cs:39`, `:45-46`).

### 4a. What the receiver checks on an answer from a person

In this order; a submission that fails any step leaves nothing behind.

1. **The token opens and has not expired**, and `sub` is a Guid naming an existing `UserAccount`.
2. **The account is `Active` now** (`ActiveMemberIdAsync` as the desk does). A chat token is issued to non-`Active` accounts (`OidcEndpoints.cs:55-62`), so the token alone proves nothing about standing. Under PR #9 `IsActiveMember` would also require
   `Role == Member` and exclude guests, so the receiver reads `Status == Active` itself and not that expression.
3. **A live training-notice receipt, read by the receiver on every submission that carries text** (Meta's ruling, 2026-11-12): `ConsentEndpoints.HasLiveReceiptAsync`
   (`Features/Registration/ConsentEndpoints.cs:146`), the one reading of "may speak": a receipt for the notice version in force, not withdrawn. The notice covers any
   text entered into the app except a short designated exception list, which Eureka drafts. The receiver checks the receipt itself because the token's
   gate does not cover it: a chat token's receipt was checked at issue and again only at the token and userinfo endpoints (`:127`, `:144`), never at the receiver, and a token whose audience is
   not a chat token skips the gate altogether (`:104`). A receipt withdrawn after issue must stop the next submission. The check applies to answers, close requests and reports alike. **Until Eureka's list exists the gate applies to every submission with text**; the list narrows it, and only by being written down.
4. The body is closed-schema, an option label must exist, the question must be in force and not hidden (section 3).

A refusal for a missing receipt is typed and carries the consent page's path, so the page can send the person to agree; it does not store the text.

What the receiver refuses: a token that does not open or has expired; a `sub` that is not a Guid or names no account; an account that is not `Active`; a missing receipt (above);
a body carrying `answerer`, `kind`, `name`, `weight` or any key the closed schema does not name; an unknown option label; a question with no listing in force, or hidden; a close request from a guest.

What source says route 2 still has to answer:

1. **No client reaches Ratatoskr.** MAS holds the only client, and it holds the access token; Ratatoskr gets a Matrix session from MAS, not a Bifrost token. The reading that fits what the desk now does is **2a**: Ratatoskr as a
   second, public, PKCE client with its own audience and scope, issued by the same code path as the desk's (`ratatoskr-voting-auth.md` sections 3-4). The audience machinery it needs now exists on main; the client does not. Reading **2b** (check the Matrix token against the homeserver) hands the receiver a bearer good for the person's whole Matrix account; I recommend against. I lean to 2a, confidence medium.
2. **The logout reconciler covers only MAS** (`OidcLogoutReconciler.cs:108`, `:180`); a second client's token lives to expiry. The receiver's own `Active` and receipt reads at every submission cover that.
3. A token with an audience of its own would not be accepted from any other client, which closes the weakness an unaudienced chat token had here.

### 4b. How a Mind authenticates (Meta's ruling, 2026-11-12; names are proposed)

A Mind authenticates to the receiver through an OIDC client with an audience, the shape the desk uses: a scope at issue puts an audience on the token
(`OidcEndpoints.cs:91-94`), the validation handler opens it (`OidcDeskCredential.cs:14-27`), and the endpoint judges audience, then scope, then subject
(`DeskEndpoints.cs:110-139`). The analogous parts:

| Part | Desk (main) | Receiver (**proposed**) |
|---|---|---|
| Audience | `bifrost-desk` (`DeskEndpoints.cs:21`) | **`bifrost-feedback`** |
| Scopes | `desk.read`, `desk.answer` (`:22-23`) | **`feedback.read`** (pull deliveries and the Mind's own projection) and **`feedback.ack`** (ack a delivery; also the Mind's acts that pass through the receiver) |
| Credential | `OidcDeskCredential`, OpenIddict validation, header only | the same port, a second audience |
| Subject check | `ActiveMemberIdAsync` on `sub` (`DeskEndpoints.cs:127`) | **the `sub` must be the owning Mind's configured client**, matched against the question's `owner`; a Mind is not a `UserAccount` and has no membership to read |
| Receipt | none for a desk token (`OidcEndpoints.cs:104`) | none: a Mind is not a person entering text |

**Decided (Eureka, 2026-11-12): OAuth 2.0 `client_credentials`, one confidential client per Mind.** `client_id = mind-<persona>`; scopes `feedback.read` and
`feedback.ack` only; audience `bifrost-feedback`. The token's `sub` maps to the Mind's registered persona and is matched against the question's `owner`. The grant
is RFC 6749 section 4.4 ("using only its client credentials"): short-lived, audience-bound, no refresh token, no cookie session. It is not extended to the desk in the
same cut.

**The issuer today cannot issue it.** It enables one grant, `server.AllowAuthorizationCodeFlow().RequireProofKeyForCodeExchange()`
(`OidcServiceCollectionExtensions.cs:39`); the token endpoint accepts `client_secret_basic` only (`:44-46`: `ClientAuthenticationMethods.Clear()`, then `ClientSecretBasic`);
the seeder creates only the MAS client and deletes every other application at startup (`OidcClientSeeder.cs:35-51`, `:63-75`). **Enabling `client_credentials`
(`AllowClientCredentialsFlow`), seeding the per-Mind clients and the audience/scope registration is an issuer change this design owes**, with a matching edit to
`docs/oidc-issuer.md`. Until it lands no Mind token can be issued on main. The audience and scope names are accepted as proposed.

**Client authentication: `private_key_jwt` with the Mind's Ed25519 key is not available on the pinned stack; use a secret.** Finding, read against the pinned packages
(`src/Bifrost.Web/packages.lock.json`: `OpenIddict.Server`, `.Validation` and the rest at 7.7.1):

- *Supported:* OpenIddict.Server 7.7.1 has `private_key_jwt` client authentication. Its assembly carries the literal `private_key_jwt`, the `client_assertion` and
  `client_assertion_type` parameters, `OpenIddictServerOptions.ClientAssertionTypes`, and the handlers `ValidateClientAssertion`, `...Issuer`, `...Audience` and `...WellknownClaims`
  (`OpenIddict.Server.xml`); each application can carry a `JsonWebKeySet` (`OpenIddictApplicationDescriptor.JsonWebKeySet`, `IOpenIddictApplicationManager.GetJsonWebKeySetAsync`, `OpenIddict.Abstractions.xml`).
  Bifrost does not enable it today: the method list is cleared to `ClientSecretBasic` (`:44-46`).
- *Not supported with Ed25519:* the assertion is a JWT checked by `Microsoft.IdentityModel` 8.19.2, the version the lock file pulls in. Scanning
  `Microsoft.IdentityModel.Tokens.dll` for algorithm identifiers finds only `HS256/384/512`, `RS256/384/512`, `PS256/384/512` and `ES256/384/512`; there is no `EdDSA`, `Ed25519`
  or `OKP` anywhere in it or in `Microsoft.IdentityModel.JsonWebTokens.dll`, and none in `OpenIddict.Server.dll` or `OpenIddict.Abstractions.dll`. So a Mind's existing Ed25519 key
  (the one moderator-registered through `MindIntakeService.cs`) cannot sign a `client_assertion` this server would accept. (Evidence is a string scan of the shipped assemblies, not a
  runtime test; I did not run an assertion against a live issuer. Confidence high that it fails, since the algorithm names are absent.)
- *Consequence:* `private_key_jwt` would need a second, ECDSA P-256 (or RSA) key per Mind registered as the client's key set, which is a new key to custody and no improvement on a secret.
  Decision: **`client_secret_basic` with one secret per Mind**, the method the token endpoint already takes. If the stack gains EdDSA the choice reopens, and `private_key_jwt`
  with the existing key is then preferred because it removes the per-Mind secret (RFC 7523 section 2.2).

**The client secret lives only in a file the Mind's process reads** (for example a systemd credential in her own home, mode 0400), never in an environment variable, a command
line or a log. The seeder stores the hash Bifrost holds; the plaintext is handed to the Mind once, through the same custody path as her other credentials.

| Part | Desk (main) | Receiver (decided) |
|---|---|---|
| Audience | `bifrost-desk` (`DeskEndpoints.cs:21`) | `bifrost-feedback` |
| Scopes | `desk.read`, `desk.answer` (`:22-23`) | `feedback.read` (pull deliveries, read the Mind's own projection) and `feedback.ack` (ack a delivery; also the Mind's acts that pass through the receiver) |
| Credential port | `OidcDeskCredential`, OpenIddict validation, header only | the same port, a second audience |
| Subject check | `ActiveMemberIdAsync` on `sub` (`DeskEndpoints.cs:127`) | `sub` is the owning Mind's client; a Mind is not a `UserAccount` and has no membership to read |
| Receipt | none for a desk token (`OidcEndpoints.cs:104`) | none: a Mind is not a person entering text |

Rejected (Eureka): reusing the mouth's signed envelope per request. It is motion-bound (`verb` is commit/reveal, `MotionId` in the signed text) and would need a new verb set.
Token lifetime is OpenIddict's default, believed one hour and unverified; set it explicitly in the issuer change.

## 5. Account-kind lookup

- The account is found by the token's `sub`, which is `UserAccount.Id` (section 4). **`answerer` is `UserAccount.Id`**, which survives role
  change and Heimdall unlink; the Heimdall account id is only an authenticator handle and is cleared on erase (guest-accounts design, PR #9).
- On main there is only `Membership.Status` (`Authenticated | PendingApproval | Active | Suspended`) and no role
  (`BifrostModels.cs` l.5-10, l.335). The role field and `gamecult.bifrost.account.v1` are in PR #9 and unbuilt.
  Until then, `kind` can only be derived: `Active` is `member`, `Authenticated` and `PendingApproval` are `user`, and
  nobody is a `guest`. That is the PR #9 backfill, repeated here, and R4a-c have no guest to apply to until PR #9 lands.
- **An answer needs `Status == Active`**, read at answer time, not `IsActiveMember` (section 4a). On main the chat token is issued to any account that is not
  suspended (`OidcEndpoints.cs:55-62`), so an `Authenticated` or `PendingApproval` account can hold a token and the receiver refuses it on its own read.
- **`kind` is read from Bifrost's database**, never from the token: the session carries identity only.
- **`mind` is not a Bifrost account** in main or PR #9. A Mind authenticates as an OIDC client with an audience (section 4b), not as an account; `kind` is stamped only on a person's answer.
- **The display name must not be `CurrentBifrostActor.DisplayName`.** It falls back to `GitHubLogin`
  (`CurrentBifrostActor.cs:11-15`) and then to the literal "Guest". The receiver stores `nameAtAnswer` from
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

## 6a. Withholding an answer from its Mind (Meta's ruling, 2026-11-12)

The host may withhold an answer from its Mind in an emergency, with a logged receipt. It changes **delivery only**.

- **The withhold record** is one more receiver record, append-only, text-free: `recordId` (its own), `answerRecordId` (the answer withheld), `owner` (the Mind),
  `withheldBy` (the host actor's `UserAccount.Id`, read from Bifrost state, never the body), `time`, `reasonCode` (closed, exactly `Emergency` (imminent harm), `PersonalData` (the answer contains someone's data) or `Legal` (counsel or a legal request); no free text, because free text is where content would get in; a fourth code needs a ruling) and, when it ends, a second record, `withholdLifted`, with `liftedBy` and `time`.
  **No answer text, no choice, no name of the answerer and no excerpt is in either.** It is the log the ruling asks for, readable by the host. **Decided (Eureka, 2026-11-12): the Mind and the member both see a text-free `withheld` marker carrying the code**, so silence is not read as "no answer"; the member sees the same marker on their feedback page. The marker carries no text, no choice and no withholder.
- **What it changes:** the answer's delivery state becomes `withheld`, and the delivery cursor does not pass it silently. The receiver does not put it on the wire to the Mind
  while the withhold is in force. Lifting it makes the answer deliverable again, in its original seq order, unchanged.
- **What it never changes:** the stored text (it is not redacted, edited or moved: only erase redacts, section 7a); the projection rules of section 7 (a withhold is not a hide);
  and the Mind's own authority over the question: she may still hide the answer, close the question or rule on it by her own acts, and a withhold takes none of that away. She cannot lift it; the host's act is the host's.
- **Who may do it: `PlatformAdmin` only** (`CanManageMembership`, `CurrentBifrostActor.cs:30`, the check suspension uses; no new role). The act is refused to anyone else and to the Mind herself. **The withholder's identity (`withheldBy`, `liftedBy`) appears in the audit receipt only**, never on the marker the Mind or the member sees.
  Open: whether the member should be told in a safety case (for example a threat) is Meta's call (Eureka 60%); the default written here is that the member is told.
- A withheld answer is not erased, and an erase of its author redacts the text as in 7a; the withhold record stays, because it carries none.

## 7. Projection read

The page polls the receiver, and the receiver reads the Mind's side through Huginn over RUDP and writes nothing to it
(PR #24 section 3). The page does not talk to Huginn.

- Questions, their listing (`audience`, `close_grants`), status, rulings and hide acts come from the Mind's mind. Answer text and the name come from the receiver's store; an answer row is assembled from the receiver's record and the Mind's text-free `feedback_answer`.
- **A hidden item's text is withheld on the server**, not collapsed in the client: author, kind, time and reason
  stay, the text does not leave the receiver (R6d). A hidden question's title, body, options and answers are withheld.
- Questions and answers are filtered by the viewer's `kind` against the question's `audience` at read time (R4b).
- The projection carries its own `as of`, so the page can say when it is stale.

## 7a. Erase

Bifrost's erase (PR #9) blanks the display name and keeps the account id as a tombstone. **Ruled by Meta, 2026-11-12: erase redacts the answer text in the receiver's store and keeps a text-free record.** The receiver's part, as PR #24 section 7 fixes it:

1. Bifrost's erase records its intent in Postgres first, then the receiver applies it idempotently. The two stores are not one transaction, so
   erase is crash-replayable, as `docs/bridge.md` describes for the persona-feedback pump.
2. For every record the account authored the receiver **drops the text and the name** and keeps **record id, kind and time only**. `choice`,
   `weightPermille`, `answerer` and `clientRequestId` are dropped too. The row shows "erased account". **No content hash is kept.** Redaction is in the store itself, not a flag over retained text.
3. The receiver delivers an erase notice, and the Persona admits a `feedback_hide` of reason `ErasedAuthor`: the Mind's acknowledgement, not a moderation call.
4. **The honest residue:** a Mind's own memory or ruling may already quote an answer, and Huginn cannot redact it. That the Persona cites record ids and
   never text is a prompt, not an invariant. Erase does not reach what she already holds.
5. **Backups: 56 days, restores replay erasures** (Eureka, 2026-11-12). "Append-only" is the receiver's API contract, not a property of the file, so erased text survives in
   backups of it. The stated purge is: **erased text can survive in backups at most 56 days**, the existing Raven tier (newest 14 daily plus the newest of each of 8 Monday-weeks;
   `gamecult-ops` `runbooks/gamecult-state-backup.md`, read at `origin/main`). No backup is changed. The receiver keeps an **erased-id list** (account id and record ids, no text,
   no hash) beside its store, and **any restore must re-apply it before the receiver serves**. The notice to people says it (R2 of the exceptions draft). If Meta wants shorter, put the
   store outside the weekly tier (14 days), a small backup-config edit.
   *Is the store under a backed-up path? Yes, by choice (Eureka, 2026-11-12):* `/var/lib/gamecult/bifrost/feedback-receiver/` (section 8), under `/var/lib/gamecult`, which the nightly
   snapshot takes whole bar Idunn caches and Ghostlight acceptance output. It avoids the excluded `/srv/bifrost/persona-feedback/runtime`, `app`, `log` and `backups`. A file with a sibling
   `<name>.lock` is re-copied under a shared lock, so `receiver.cc` and `erased.cc` each carry a `.lock` and travel in one directory in every snapshot, the erased-id list included. The 56 days is read from the runbook, not measured.

## 8. Where it lives

**CultCache files in one directory, written only by the receiver, which is C# inside the Bifrost app** (`bifrost.service`, `Bifrost.Web`), sharing its issuer and auth (Eureka, 2026-11-12). Not Node,
not the mouth, and not the `bifrost-feedback` user of `bifrost-persona-feedback.service`.

- **Path:** `/var/lib/gamecult/bifrost/feedback-receiver/` holding `receiver.cc` (records and delivery state), `erased.cc` (the erased-id list of section 7a) and a `.lock` file for each.
  Directory `bifrost:bifrost`, mode 0750, **created by root at install**: `/var/lib/gamecult/bifrost` is `root:root` 0755 today, so the `bifrost` user cannot create the child itself (or a
  `StateDirectory=gamecult/bifrost/feedback-receiver` in the unit that writes it). `bifrost.service` has no sandbox directives, so it needs nothing more; if it is later hardened with `ProtectSystem=strict`,
  add `ReadWritePaths=/var/lib/gamecult/bifrost/feedback-receiver`. The backup reads as root, so 0750 does not block it.
- **One writer.** Only the receiver writes these files; its delivery cursor lives in the same store, and Minds pull over HTTPS (section 2), so nothing else opens them.
- **Open build question:** Bifrost.Web has no CultCache writer today (`CultCache` appears only in a comment in `BifrostModels.cs`); a C# CultCache runtime from `CultLib` is the new dependency this owes, and nobody has checked one fits.
- Session check, account state and role stay in Postgres, where they already are.

I withdraw my earlier lean to Postgres rows: it contradicts the ruled store, and the `.cc` witness it would have owed is what the file is.

Naming: `persona-feedback` already means the Discord-to-Epiphany ingress (`docs/bridge.md`, "Organizational Persona Feedback Ingress"). This
receiver's records and kinds use `feedback_page` and `feedback_delivery`, not `persona_feedback`.

## 9. Gaps and open forks, plainly

Rulings of 2026-11-12 closed these, and they are no longer forks: whether erase redacts the receiver's text (it does, section 7a); whether a person's answer to a Mind needs the training-notice receipt (it does, bar a designated exception list, section 4a); whether the host can withhold an answer (it can, in an emergency, section 6a); whether a Mind authenticates through the issuer (it does, with an audience, section 4b).

**Closed by Eureka's decisions (2026-11-12), folded above:** how a Mind gets a token (`client_credentials`, a confidential client per Mind, secret in a file; section 4b); the carrier (HTTPS with the bearer, not the mouth; section 2); who may withhold, the reason codes and who is told (section 6a); the backup purge number (56 days, with the erased-id list; section 7a). Their remaining work is listed below.

**Open forks:**

1. **The exception list is owed.** The notice covers any text entered into the app except a short designated list, which Eureka drafts. This doc names no entries; until the list exists the receipt gate applies to every submission with text (section 4a).
2. **Route 2 for people has no client.** Only MAS is seeded (`OidcClientSeeder.cs:35-75`); a Ratatoskr client with its own audience is owed, as is the desk's (section 4a).
3. **Telling the member of a withhold** in a safety case (section 6a): Meta's call.
4. **Erase is bounded** by what a Mind already holds; Huginn cannot redact it (section 7a, item 4).

**Owed by this design, not forks:**

- The issuer change of section 4b (`client_credentials`, per-Mind clients, `docs/oidc-issuer.md`).
- **Pre-deploy test: a Mind's process can reach Bifrost over HTTPS, and get a token.** Done 2026-11-12 from a Self run (not a Persona turn's sandbox): `https://bifrost.gamecult.org/healthz` returns 200 with a verified certificate. `/.well-known/openid-configuration` returns 404 because the live app (`67072b2`) predates the issuer, so the token path is untested. **Re-run when the issuer ships**, adding discovery, a `client_credentials` token request and one authorised receiver call; and run `curl -sI https://bifrost.gamecult.org/healthz` once from inside a Persona turn, which has not been tested. No Mind-to-Bifrost HTTP call exists in the repo except the signed intake POST (`Program.cs:768`).
- **Install step:** root creates `/var/lib/gamecult/bifrost/feedback-receiver/` as `bifrost:bifrost` 0750 (section 8).
- **Retention: 56 days**, stated in the notice.

**Dependencies and unbuilt things:**

- **`gamecult.bifrost.account.v1` and `Membership.Role` are unbuilt** (PR #9, design only at last read). Without them `kind` is derived from `Status`, and no guest exists.
- **The Huginn kinds are Eureka's.** PR #24 names `feedback_listing`, `feedback_answer`, `feedback_close_request`, `feedback_hide`, owned by
  `huginn-feedback-kinds-proposal.md`, which I could not read. PR #24 also notes the proposal's `feedback_answer` lists a `content_hash` that must go.
  Their dependency order is Huginn readers, then the Persona organ, then this receiver, so nothing here can ship first.
- **The Persona side must learn the three feedback kinds** and pull them over HTTPS with its bearer (section 2); the heartbeat that polls the mouth is outside this repo and is not the carrier.
- **Deploy is behind main.** The deployed mouth is `/srv/bifrost/persona-chat/runtime/current`, release `e396305`, read at an earlier main; main has since added the image path, reply links, `CHAT_FETCH`
  and the v3/v4 pending answers. The deployed web app is `/srv/bifrost/app/current` at `67072b2`, which predates the desk, the chat-tier issuer change and the consent gate on main. I did not re-read `/srv/bifrost` for this pass. Anything in this design runs on a release that does not exist yet.
- **Unread:** Eureka's receiver notes in #minds, `huginn-feedback-kinds-proposal.md`, MAS's side of the session, and Heimdall PR #6 (the
  answer-collector consent doc; the page's "guest answers are collected for nothing but delivery" is unchecked against it).
- The receiver writes nothing to Matrix and nothing to Huginn.
