# Public registration gates

Meta ruled yes to public registration on 2026-11-10. The gates she required: self-hosted CAPTCHA, email verification,
a training-notice consent receipt, rate limits and a brake, reserved names, and new accounts in Users with no vote.
This page scopes the three that did not exist, says where each lives, and records what is built.

## What exists

| Gate | Where | State |
| --- | --- | --- |
| Rate limits and brake | `OidcGuardMiddleware` (`docs/oidc-issuer.md`) | Merged. Covers `/connect/*` only, not the register endpoint. |
| Reserved names | `BifrostIdentityService.IsReserved`, 28 names | Merged, at creation and at authorize. |
| Chat tier, no vote | `Authenticated` / `PendingApproval` accounts chat and open no member or governance surface | Merged. |
| Suspension | `MembershipSuspensionService` | Merged. |
| CAPTCHA | `Features/Registration/RegistrationChallengeService` | Built here. |
| Consent receipt | `ConsentReceipt`, `TrainingNotice` | Built here. |
| Email verification | nowhere | Scoped below, not built. |

The hole these gates close: `POST /auth/bifrost/register` is anonymous, and since the chat tier merged, an
`Authenticated` account it creates can sign in to chat. Before this cut it took an identity name and nothing else.

## CAPTCHA

Lives in Bifrost, because the register endpoint is Bifrost's. Heimdall authenticates people through providers and has no
registration form.

Design: a hashcash proof of work. `GET /auth/bifrost/challenge` returns a Data Protection-protected token (random id and
expiry, five minutes) and a difficulty in bits. The client finds a decimal `nonce` such that
`SHA-256(challenge + ":" + nonce)` has at least that many leading zero bits, and posts both with the registration. The
server verifies the protection, the expiry and the hash, and spends the id once.

- Self-hosted: no third-party script, request or cookie; nothing to track.
- Accessible: there is nothing to see or hear and nothing to solve by hand. A screen-reader user, a keyboard user and a
  script all pass the same way, at the cost of CPU time on the client.
- Cost: difficulty 18 is about 260,000 hashes on average, well under a second natively and a few seconds in browser
  JavaScript. It prices a bulk registration run in CPU; it does not stop a determined attacker with CPUs, and it is not a
  proof of humanity. The difficulty is embedded in the signed challenge, so a challenge verifies at the difficulty it was issued with.
- Replay: the spent-id set is in memory and bounded (10,000, entries dropped at expiry); a restart forgets it, so a challenge
  can be replayed for at most its five-minute life. When the set is full the endpoint refuses rather than grows. One address
  cannot fill it alone: `GET /auth/bifrost/challenge` and `POST /auth/bifrost/register` share one per-address budget
  (`Bifrost:Registration:Limits`, 10 per minute by default, IPv6 by /64, over budget is 429 with Retry-After). An attacker
  with many addresses can still fill the set; that is the accepted bound, the same as oidc-issuer.md.

### The sign-up form and the no-JS fallback

`GET /auth/bifrost/register` renders the form: identity, optional display name, the notice in full beside an unchecked
consent box (the box is the consent act), and a challenge. With JavaScript the page solves the proof of work in the
background and reports progress in a polite live region. Without JavaScript the form still submits and succeeds, by a
time-lock:

- The challenge carries its issue time. A submission that says it waited is accepted only once `FallbackWaitSeconds`
  (default 30) have passed since issue; earlier, the page comes back saying how many seconds remain and the challenge is
  not spent (a fresh challenge would restart the clock). The same holds for a refused identity (taken, reserved or
  malformed): those rules are checked before any permit or challenge is spent, and the still-valid challenge is kept. Every
  other failure re-renders with a fresh challenge, the consent box unchecked again (tick it again) and the identity kept.
  `FallbackWaitSeconds` is validated at 5 to under 300, since a challenge lives 300 seconds; `Captcha:Difficulty` at 1 to 24.
- Fallback submissions have their own, stricter per-address budget (`Limits:FallbackPermits`, default 3 per
  `FallbackWindow`, default one hour; over it is 429 with Retry-After).
- A submission names a nonce or says it waited, never both.
- The page has a second submit button, always enabled, that abandons a solve in progress and submits on the time-lock, so
  a slow device is never stuck behind the proof of work.
- `POST /auth/bifrost/register/form` refuses a request that did not come from the site itself (`RequestOriginGuard`: `Origin`
  equal to the site origin, else `Sec-Fetch-Site: same-origin`), 403, before it reads the body or spends anything, so a
  third-party page cannot register an account and replace a visitor's session. A request with neither header and no
  session cookie is allowed, because text browsers send neither and are the people the fallback is for. The body is capped
  at 16 KB (413) and the per-address budget is spent before it is read.
- Whether a name is taken is answerable without a proof, bounded by the per-address budget. Names are public (they are
  chat localparts), so that leaks nothing.

Tradeoff, stated plainly. The time-lock is accessible to everyone who can use a browser: nothing to see, hear, read or
solve, no script, no third party. It is also weaker than the proof of work. It costs an attacker wall-clock time, not CPU,
and an attacker can start many challenges at once and wait once, so what bounds the fallback is the per-address budget
and nothing else. An attacker with many addresses passes it at the price of the wait. We accept that because a
registration buys little: the account is `Authenticated`, in Users, with no vote and no member surface, and the other
gates (consent, verification once built, reserved names, suspension) still apply. If fallback registrations are abused,
raise the wait or lower the budget; do not add a visual or audio puzzle, which excludes the people the fallback is for.

## Consent receipt

Lives in Bifrost: the account, the receipt and the registration are one transaction, and chat admission is Bifrost's
decision (`admission-is-bifrost-membership`).

- Notice: `TrainingNotice.Version` (positive integer, no leading zeros, per the version rule in Heimdall's
  `answer-collector-consent.md`) and `TrainingNotice.Text`. Served at `GET /auth/bifrost/notice`. The text in the repo is
  draft wording. Eureka owns the final wording in gamecult-ops; changing the text is a version bump, never an edit in
  place. The text states that no Discord material is used.
- Receipt: `ConsentReceipt` (account, notice version, `AcceptedAtUtc`, `WithdrawnAtUtc`), unique per account and version,
  written in the same save as the account. Registration without `acceptedNoticeVersion` equal to the current version
  creates nothing. A new notice version needs a new acceptance; there is no path yet that asks for one.
- Withdrawal: `POST /auth/bifrost/consent/withdraw`, signed in, sets `WithdrawnAtUtc` once on the account's receipts.
  Withdrawal stops future use and cannot recall past use; the notice says so. The row is one-way and never deleted.
- Consumer: a training export must include an account's data only while a receipt for the notice version in force
  exists with `WithdrawnAtUtc` null. No export exists in Bifrost; this page is the contract it will be built against.

Scope limits: accounts that arrive through Heimdall providers (Discord, Patreon, Twitch, GitHub) are created in
`HeimdallAuthService.UpsertUserAccountAsync` and get no receipt. They reach chat the same way. Closing that needs a
consent step on the completion page and is the next cut. Existing accounts are not re-asked.

## Email verification

Not built. Decided (steward, 2026-11-11): Bifrost keeps a verified address, as sign-in recovery.

Why: a native account has no credential and no sign-in route (`concept-mill-votes-target.md`, claim 12). A person who
loses the session cookie loses the account, and the chat localpart minted from it is permanent. An address that was
verified is the one way back, and the usual way (MAS, Matrix homeservers and most accounts hold one). Hashing it instead
cannot send mail, so it recovers nothing.

Privacy handling, which the build must honour:

- Stored only for native accounts, one verified address per account, unique across accounts (a keyed hash of the
  normalized address is the lookup; the address itself is encrypted with Data Protection, since sending needs it).
- Never in a token or claim (the issuer doc's claim table is unchanged), never in a log line, a dashboard, a Verse
  document, a training export, or Heimdall. The account page shows it only to its owner.
- Used for two things: the verification mail and a single-use sign-in link (15 minutes, spent on use, per-address and
  per-account send budgets). No notices, no newsletters, no sharing.
- The owner can remove it (then there is no recovery) or change it (a new verification first). Deleting the account
  deletes it.
- Existing accounts and provider accounts are not asked.

The build cannot start until ops provides a transport: there is no mail sender anywhere in Bifrost, Heimdall or the chat
stack. Bifrost's side is an `IEmailSender` port with the relay's settings as secret files; the relay, its sending domain
and SPF/DKIM are ops infrastructure.

Shape: registration becomes two steps. Step one takes identity, address, consent and the CAPTCHA, and mails a single-use
token; no account exists yet. Step two redeems the token and creates the account, the receipt and the stored address
together. An unverified account then cannot exist, so it cannot chat.

Failure it prevents: accounts for addresses nobody holds, and unrecoverable accounts.
