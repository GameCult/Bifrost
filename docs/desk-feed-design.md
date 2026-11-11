# Desk feed: per-Mind open forks for a signed-in member (design, no code)

Status: proposal, 2026-11-11, Heimdall (Imagination pass) against `origin/main` e8e8fcc. Docs only; nothing here is built, ruled or deployed.
Labels: **FACT** = read at the cited place. **INFERENCE** = my reading. **UNREACHED** = I could not read it.
Reads: `src/Bifrost.Web/Features/Desk/*` (main), `docs/ratatoskr-voting-auth.md` (PR #37, 447a7cb), `/opt/commons/handoff/fork-desk-answer-binding-20261110.md`, Ratatoskr `fbec032` (`src/app/features/desk/*`), `tools/persona-matrix-mouth.mjs`, `docs/bridge.md`. UNREACHED: CultLib `fork-card.md` (no checkout here; I rely on the Desk code that pins it at de8a062a), Huginn's `desk_wire.rs` (PR #31 is merged; the list/read half of the door wire is not defined in anything I read).

## 1. What is asked, and the finding that shapes it

Ratatoskr's Desk row per Mind is empty "until Bifrost feeds it" (FACT `src/app/features/desk/source.ts`: `deskFor(mind): ForkList | undefined`, slice 2 "replaces this with Bifrost's list reader"). The page already decodes `gamecult.fork.list.v0` (`forkContract.ts`).

**Finding: the feed route is already built.** `GET /api/desk/{mind}/forks?status=open&limit=N` is on main (PR #40, hardened #42-#45): list, per-Mind socket door, `open_count`, `stamp`, notes stripped for non-deciders. It serves nothing today because its two ports have no production implementation (`DeskPorts.cs`: `IDeskCredential`, `IDeskDoor`; `MapDesk` fails startup if either is missing). So the smallest honest feed is **not a new endpoint. It is the existing list route, read-only, with its two ports filled and a token that carries only `desk.read`.** A second route would need a second contract, vectors and owner for no gain.

## 2. The endpoint

- `GET https://<bifrost>/api/desk/{mind}/forks?status=open&limit=50[&cursor=c<N>]`, one call per Mind (the client already asks per Mind). `limit=0` returns counts and stamp only (FACT binding file, route table), usable for a badge.
- Response: `gamecult.fork.list.v0` (`ForkList`: `mind`, `stamp`, `open_count`, `recorded_count`, `forks[]` of `ForkEntry`, `next_cursor`). **JSON, and why:** Ratatoskr is a browser client, the xenos boundary; the contract (CultLib `fork-card.md`, Transport) fixes JSON at the HTTP edge with every key written, and the documents are typed C# records (`DeskDocuments.cs`) mirroring CultLib's. Bifrost to Huginn is the typed door, not JSON.
- The row the UI needs is derived by the client from each entry: `card.fork_id`, `card.title`, age (section 5), and the count is `open_count`. The page "derives nothing" (FACT `forkContract.ts` header), so it takes `open_count` as the one count.
- Not offered: any POST (`/api/desk/{mind}/answers` stays unreachable to this client because its token has no `desk.answer`, so it is refused 403 `insufficient_scope` at step 4 of the pipeline, before the body), any vote, any write.

## 3. Auth: what a signed-in member can call today, and what the feed reuses

FACT today: a signed-in member holds only the cookie `__Host-bifrost_session`; the default scheme is Cookie (`Program.cs:178-189`); no `AddValidation` exists, so no access token authenticates any `/api/*` route; the desk handler is `.AllowAnonymous()` and judges its own bearer (`DeskEndpoints.cs`, `MapDesk`), and the cookie scheme cannot authenticate `/api/desk/*`. **A signed-in member can call nothing on the desk today.** Chat.gamecult.org never holds the `__Host-` cookie, so "the Heimdall session" cannot be sent by the browser to Bifrost from chat.

So the session is used as PR #37 uses it: the **sign-in leg of an OIDC code+PKCE flow** (`/connect/authorize` resumes on the Heimdall-backed session, FACT `OidcEndpoints.cs:40-59`) mints a short access token. Reuse, exactly:
1. Token: PR #37's second public PKCE client (Ratatoskr) and `BifrostBearer` OpenIddict validation (PR #37 section 5, handler `IDeskCredential` production impl, labelled H4 in the desk cut map). Request scope `desk.read` only, audience `bifrost-desk`. No `desk.answer`, no `votes.*`.
2. Judgment: unchanged `DeskEndpoints.HandleAsync`: audience, scope, then `OidcEndpoints.ActiveMemberIdAsync(db, sub)` re-read every request (non-Active gets 403 `forbidden`).
3. CORS: existing `DeskCors` (exactly `Desk:AllowedOrigin`, GET and POST, no credentials).
Rejected: a cookie-authenticated same-origin JSON route (works for a Bifrost page, not for the chat origin; a second auth path on the same data), and letting chat present its Matrix/MAS token (Bifrost does not validate it; INFERENCE per PR #37 section 1).

## 4. Where the data comes from (compare, recommend one)

Huginn is the truth. A fork is a question record with options, a derived status, deciders and a server-computed `options_hash` (FACT binding file, "Cards are a read-only projection"). Bifrost holds no fork store, no cache, no hash (FACT cut map authority map). Everything else is a cache or an unrelated plane. Options:

- **(a) A small Huginn endpoint Bifrost calls.** This is already the plan of record, not a new thing: `IDeskDoor.ListAsync(socket, context, request)` over the per-Mind peer-checked unix socket, `context` = {Mind, authority, subject, deciders} built by Bifrost alone (`DeskPorts.cs`). Huginn returns the list "as it projects it for this viewer".
- **(b) A file the persona mouth tails or writes.** FACT the mouth holds one `cursor.cc` per Persona and it stores only the Matrix `/sync` position `next_batch` (`persona-matrix-mouth.mjs` ~:491, :906-912). `chat_pending` is Matrix deliveries to a Persona over loopback RUDP (credential-derived connection id), served *to* Huginn, which is the client; the mouth reads Matrix, not Huginn. It holds no fork, no question and no Huginn client. A file route means a new writer inside the Matrix actuator that copies private records out of the mind into the chat plane, a second owner of "what a member may see", a Node process rewriting Rust's projection, and staleness equal to its write period. The mouth would gain a job it was split away from.
- **(c) Bifrost reads Huginn directly with a CultNet selection** (what `huginn open <campaign>` / `huginn blocked <spec>` do). Rejected: those return every in-force question and follow-up of a campaign (`epiphany.pipeline.question.v2`, `follow_up.v2`), most of which are not forks. Bifrost would need a general read of the mind and would decide fork-hood and visibility itself. Also Bifrost has no Huginn client at all (FACT: no CultNet/RUDP/unix-socket client in C#; `UnixDomainSocketEndPoint` appears nowhere).

**Recommend (a), as the existing desk door, list request only.** Reasons:
- **Auth:** the door is peer-credential checked (the receiver's uid); Bifrost passes the verified subject; the member's token never reaches Huginn.
- **Staleness:** none added. A live read, `Cache-Control: no-store` (FACT), with Huginn's `stamp` (`s<N>`, same stamp same state) as the change signal; a client polls, or `limit=0` for the cheap probe. Bifrost may cache by stamp later; it need not.
- **Failure:** per Mind and per call. A dead socket, timeout or unknown outcome is 503 `unavailable` with `Retry-After` (FACT `WriteUnavailableAsync`); the other Minds' rows are unaffected because the client asks per Mind. The mouth being down is irrelevant. Nothing is served from a stale copy that could mislead.
- **Mouth overlap:** none, which is the point. Chat plane (mouth) and fork plane (door) stay two owners.
- **Not yet existing:** `UnixSocketDeskDoor` (H5) and the Huginn side of the list wire. Eureka owns that wire (binding file section 5, "The door wire is Eureka's"). I found no definition of the list/read request bytes; this note does not invent one.

## 5. Meaning of "open" and "age"

- **open** = the card's `status` is `open` as Huginn derives it (`ForkCard.status`; `?status=open` filter). Bifrost does not decide it. The count is `open_count` of the same response, so the number and the rows come from one stamp. `recorded_count` (closed with a ruling) is not part of the feed.
- **age** = now minus when the question was raised. **The contract cannot give this today.** FACT `ForkCard` has `order` (a position), `raised_in` (a reference) and no timestamp; answers carry `answered_at`/`received_at` but an unanswered fork has none. Options: (i) add `raised_at` to the card (CultLib contract change, Huginn emits the question's admission time, new vectors, Bifrost record gains one field); (ii) ship the feed with `order` and no age. **Recommend (i), decision X1 below; (ii) is the fallback and loses nothing else.** Age is then computed by the client from `raised_at`; Bifrost computes nothing.

## 6. D1 (options_hash) and the feed

Two different "D1"s exist: the ruling cited for this feed is the fork-desk answer-binding D1 (2026-11-10: `options_hash` covers title, context, options with ids, recommendation and depends; `fork_id` is bound beside it). PR #37 also has a D1 (consent). This section is the first; section 7 the second.
- Feed rows carry the card as Huginn returns it, `options_hash` included. Bifrost never computes or corrects a hash (FACT binding file). A row's identity is `(fork_id, options_hash)`: an edit to a shown title changes the hash, so the row changes with it.
- The feed shows title, which the hash covers, so feed and detail cannot disagree about it.
- **Age is not decision content.** It is immutable metadata about the question, so keeping `raised_at` outside the hash needs no re-pin of the hash vectors, but "covers everything shown" read literally would put it inside if the card renders it. X2: steward says whether `raised_at` is a card attribute outside the hash (recommended: it does not change what a decider is deciding) or inside it (re-pin Void's `optionsHash.ts` vectors and CultLib's).

## 7. PR #37 decisions, what this feed needs

| id | decision (brief) | for the feed |
|---|---|---|
| D1 | explicit consent for `votes.cast`, or first-party implicit | **not needed**: a `desk.read` grant casts and changes nothing; implicit read consent (PR #37 section 7) |
| D2 | re-authentication at cast for high-stakes motions | **not needed**: no cast |
| D3 | 10-minute token, no refresh: a redirect to keep going | **needed**: a feed that polls outlives a 10-minute token. Either accept a top-level redirect per expiry (two hops with a live session), or give the desk client a longer read token (still no refresh). Recommend accept the redirect, drop polling to the open page |
| D4 | where a member lists and revokes grants | **not needed** while the grant is read-only; revisit with `desk.answer` |
| D5 | runtime brake on the Ratatoskr client | **needed, small**: the feed shows other people's forks; one switch to withdraw the client without redeploy. Owner Eureka |
| D6 | rate-limit numbers and where they live | **needed**: reads 120/min per account is PR #37's proposal; the desk cut map left the rate limit blocked on a refusal code (a 429 has none in the contract, Q3 there). Either extend the contract or rate-limit at nginx |
| D7 | order against the MAS rollout | **needed**: the feed rides on Bifrost OIDC being live, which it is not (PR #37 section 1: no `Bifrost__Oidc__*` variable, MAS disabled). Recommend independent of MAS; this is the first OIDC consumer |

Already ruled: the answer-binding D1 above, visibility ruling c (any `desk.read` holder sees cards and outcomes "as the card's audience allows"; notes only for deciders), D2/answer-binding (decider list authored in Bifrost admin, Mind read-only).

## 8. Privacy: what a member may see per Mind

- **Filter owner: Huginn.** The door call carries the viewer (`subject`, `authority`, that Mind's `deciders`); Huginn projects "for this viewer" and applies the card's `audience`. Bifrost adds no second filter on which forks appear; two filters would let them drift. Bifrost's own privacy acts are exactly two, both already built: it nils every `note` for a non-decider (`DeskNotes.Strip`), and it resolves `{mind}` only against the configured list (unknown Mind and unknown fork return the same 404 bytes, so Minds are not probeable).
- **Fields safe for a member:** `fork_id`, `title`, `options`, `recommendation`, `depends`, `status`, `order`, `options_hash`, counts, `stamp`, and `raised_at` if X1. **Not safe, and not in the feed:** `note` (stripped), the decider list beyond ids already on the card, anything from the mind that is not a fork card (follow-ups, findings, research, persona state, room text).
- **Leak to close (X3):** `ForkEntry.record.answers[]` carries each answerer's account id (a Bifrost `UserAccount.Id`) to any `desk.read` holder; ruling c permits cards and outcomes, but a member list of who answered what is more than a feed needs. The feed does not need `record` at all. Options: (i) accept; (ii) strip `answerer` for non-deciders at the door like notes (small, consistent with `DeskNotes`); (iii) a list shape without records. Recommend (ii) until a record-free list exists.
- **Content privacy:** a fork's `context` and `title` are Mind-authored prose and may name people or private matter. Huginn's `audience` is the only gate; if a card lacks one, the default must be not-listed, which is Huginn's rule to state (X4). The feed ships no free text Bifrost wrote.
- **Consent and receipts:** reading creates no member record, so no receipt and no consent object; the member's act is the token grant (read-only, implicit). Answer receipts and the append-only record belong to the answer path and stay out. Audit: log client id and `sub`, not the response.

## 9. Failure modes

Bearer unjudgeable: 503 `unavailable` (not a refusal of the caller). Not Active: 403. Unknown Mind: 404 `fork_unknown`. Oversize fork: 422 `fork_too_large` with a cursor to continue (FACT #42/#44; the client must handle it, not render nothing). Token expired: 401; the client restarts the redirect (D3). Door down: 503; the Desk row shows "unavailable, retry", never an empty list (an empty list must mean zero open forks). Huginn emits a decider list outside 1..64 for a configured Mind: 503 (config fault, FACT). Ratatoskr's source is synchronous; it needs a fetch-and-hold layer that keeps the last good `stamp` and marks it stale, which is Void's.

## 10. Tests that would prove it (consumer side)

1. Ratatoskr, against a stubbed list: a `ForkList` fixture from the vendored vectors renders the right rows and count; a 503 renders "unavailable", not an empty desk; `open_count` 0 with an empty page renders "none open".
2. Bifrost, `DeskWebApplicationFactory` + `FakeDeskDoor`: a token with `desk.read` lists (200); no token 401; wrong audience 401; a `desk.read`-only token POSTing an answer is 403 `insufficient_scope` with the door never called; a non-Active subject is 403 with the door never called; a cookie alone gets 401.
3. Privacy: a non-decider's response has every `note` nil (key present); with X3(ii) every `answerer` of a non-decider is masked; a decider keeps both; an unknown Mind and an unknown fork return identical bytes.
4. Hash: a row's `options_hash` equals the vector value, and Ratatoskr's `optionsHash.ts` recomputed over the rendered title/context/options matches it (pins D1 end to end).
5. Probes owed by PR #37 section 11 (items 1, 3, 5): public client exchange, audience+scope enforced, bearer on `/App` still refused.
6. Door contract: a `UnixSocketDeskDoor` against a Huginn double returns `Listed`, `NotListed`, `ForkTooLarge`, and an exception maps to 503.

## 11. Size estimate (lines, include tests)

| file | lines |
|---|---|
| Bifrost `Features/Desk/UnixSocketDeskDoor.cs` (list only, H5; read/answer added by the answer cut) | ~180 |
| Bifrost `Features/Oidc/*` bearer validation, `desk.read` scope, audience, second client seed (shared with PR #37 H4) | ~150 |
| Bifrost `IDeskCredential` production impl + wiring in `Program.cs` | ~60 |
| Bifrost `DeskNotes` extension for X3(ii) | ~15 |
| CultLib `fork-card.md` + schema + vectors for `raised_at` (X1) | ~40 |
| Huginn: list wire handler and `raised_at` (Eureka's) | not estimated |
| Ratatoskr list reader + token redirect + stale holder (Void's) | ~250 |
| Bifrost tests (items 2, 3, 6) | ~250 |
| This doc and `docs/oidc-issuer.md` note | ~40 |

Subtraction: no new route, no store, no migration, no DTO type beyond one field. The two shared ports (door, bearer) are the cost, and the answer path needs them anyway.

## 12. Open decisions

- **X1** (Eureka, CultLib): add `raised_at` to the card for age, or ship order-only.
- **X2** (steward): `raised_at` in or out of `options_hash`.
- **X3** (steward): answerer ids visible to every `desk.read` holder: accept or mask.
- **X4** (Eureka/Huginn): what a card's `audience` defaults to; the default must be not-listed.
- **PR #37 D3, D5, D6, D7** as in section 7; D1, D2, D4 not needed.
- Eureka owns the list/read door wire; this feed is blocked on it and on PR #37's bearer, not on any new Bifrost design.
