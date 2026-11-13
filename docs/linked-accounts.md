# Linked accounts

`/Membership/LinkedAccounts` (`src/Bifrost.Web/Pages/Membership/LinkedAccounts.cshtml[.cs]`) is where a signed-in member sees the provider logins attached to their Heimdall account, links another one, and unlinks one. Heimdall owns the logins and every rule about them; this page starts the flows and shows what Heimdall answered. A member reaches it from the membership status page. A session with no Heimdall account (a Bifrost-native GitHub sign-in) gets a note instead of controls.

## What it does

| Action | What Bifrost does | Heimdall call |
|---|---|---|
| Show my logins | A fresh ordinary Heimdall sign-in as the session's account, through the proof provider (below); inside its completion, the list | `GET /v1/me/identities` with that sign-in's bearer |
| Link Discord, Patreon or Google | The same fresh sign-in as proof, then the link start for the chosen provider with its bearer. Needs a current list. Discord and Patreon attach in Heimdall's callback and return a link completion; Google writes a pending link, and a second fresh sign-in confirms it | `POST /v1/oauth/{provider}/start` `mode: "link"`, then for Google `POST /v1/apps/bifrost/links/confirm` |
| Unlink | A sign-in through Google with `stepUp: true` (app secret); inside its completion, the unlink | `POST /v1/me/identities/{id}/unlink` with that sign-in's bearer |

The linkable providers are the ones Heimdall's `bifrost` profile admits (`identityProviders` in Heimdall `src/app-profiles.ts`): Discord, Patreon, Google. Twitch is offered for sign-in elsewhere in Bifrost but the profile refuses it, so it is not offered here.

## How the bearer is kept: it is not

Bifrost keeps no Heimdall token. Each action is an attempt: a Data Protection cookie (`__Host-bifrost_heimdall_<slot>`, HttpOnly, Secure, ten minutes) holding a purpose (`ListProof`, `UnlinkProof`, `LinkStart`, `LinkAwaitGoogle`, `LinkConfirm`, `LinkAwaitDirect`), the account the session must be, and a target (the provider to link, or the identity to unlink). The browser returns from Heimdall to `/auth/heimdall/complete`; Bifrost redeems the code, verifies the token, requires that the proven account is the attempt's account and that the browser's session is that same account, calls Heimdall with the bearer inside that one request, and drops it. The bearer is never stored, logged, put in a cookie or a redirect, or rendered. Heimdall's member routes go through an HTTP client that carries no app secret (`HeimdallMemberClient`), so a Bifrost fault cannot call them without a member present.

The list is kept for five minutes in this process only (`LinkedAccountsSnapshots`), keyed by the Heimdall account id, so the page can render after the completion's redirect: identity id, provider, display name (or username when there is none) and link date. Never the bearer, the email, the provider user id or the scopes. It is not given to the browser. A restart or the five minutes empties it and the page offers "Show my linked logins" again.

Every action is a POST with antiforgery; a GET starts nothing. The completion route's answers are `Cache-Control: no-store`. The page and every redirect carry only a code from a closed set (`HeimdallLinkOutcomes`, `?result=`); the wording is Bifrost's, never Heimdall's text or the query's.

## Which provider proves the account

An ordinary proof sign-in through a provider the account no longer holds makes Heimdall mint a new, unrelated account that holds that login, and the freed login then answers `identity_linked_elsewhere` when the member tries to link it back. So Bifrost never guesses a provider, and it does not use the "provider you signed in with" claim in the session (that records how you signed in once, not what the account holds now):

- With a current list (the five-minute snapshot), the proof goes through a provider on it: Discord, else Patreon, else Google.
- With no list (first visit, five minutes passed, a restart), the page offers only "Show my logins through Discord / Patreon / Google" and says plainly that Bifrost cannot tell what the account holds, so the member picks a login they know it holds. A named provider the account turns out not to hold still mints at Heimdall: that is the member's choice and the page says so beforehand. A refresh with no list and no named provider starts nothing (`provider_needed`).
- Link buttons appear only with a current list; a posted link with none starts nothing (`list_expired`). Unlink needs the list too.

## The proof limit

Unlinking needs fresh proof: Heimdall refuses `403 step_up_required` unless the access token carries `step_up_at` from a step-up sign-in within 600 seconds (`docs/service-contract.md`, `POST /v1/me/identities/{identityId}/unlink`, in the Heimdall repository). Only Google can give that to a `bifrost` sign-in: Discord and Patreon have no re-authentication parameter (an open provider session completes without credentials), and Twitch and YouTube, which do, are not in the `bifrost` profile. So:

- An account with no Google login linked cannot unlink anything from here. The page says so and shows no Unlink button; a posted unlink starts nothing.
- Heimdall answers `503 provider_not_configured` for Google until a Google OAuth client is configured on the Heimdall host (follow-up `heimdall-body:follow_up:google-console-client-registration`; not set live as of 2026-11-13). The step-up start is then refused and the page says fresh proof could not be started. Nothing loops: the refusal sets no attempt cookie.
- Linking Google needs the same client. Until then the Google link button ends in "That could not be completed. Nothing was changed."
- A Google step-up proof is as strong as Google's `prompt=login`; Heimdall says "asked", not "verified" (`stepUpRequested`).

The unlink completion must also say a step-up was requested (`stepUpRequested`, set by Heimdall when the start asked for one); one that does not is refused without calling Heimdall. Heimdall's other answers are shown as plain outcomes with nothing changed: `409 last_identity` (the last login is never removed), `404 identity_not_found` (the list is dropped as stale), `401 sign_in_required`, `403 step_up_required`, and any failure to reach Heimdall.

A link refused because the login already belongs to another account (`409 identity_linked_elsewhere` from `links/confirm`; for Discord and Patreon the callback's `link_initiator_mismatch`, carried on by the complete page as a fixed result) is shown as "That login belongs to another account." The other account is never named.

## Not here

- Merging two accounts. A refused link ends there; nothing is moved or merged.
- Step-up through Discord, Patreon or Twitch. Heimdall does not offer it to the `bifrost` profile.
- Linking Twitch or GitHub: not admitted by the `bifrost` profile, and Heimdall's GitHub runtime is not implemented.
- A list of other apps' logins, grants or consent receipts (Heimdall's grants page).

## Where the code is

- `Pages/Membership/LinkedAccounts.cshtml[.cs]`: the page and its three POST handlers.
- `Features/Heimdall/HeimdallAuthService.cs`: the attempt machinery (`StartListAsync`, `StartLinkAsync`, `StartUnlinkAsync`, `ContinueLinkAsync`), shared with sign-in.
- `Features/Heimdall/HeimdallMemberClient.cs`: the two member calls and their outcome mapping.
- `Features/Heimdall/LinkedAccountsSnapshots.cs`: the five-minute list.
- Tests: `tests/Bifrost.Web.Tests/LinkedAccountsTests.cs`, `HeimdallGoogleLinkTests.cs`, with the fake Heimdall in `Support/HeimdallTestKit.cs`.

## Known leftover

When Discord or Patreon's callback refuses a link because the login belongs to another account, the complete page's script redirects to the page with the fixed result and does not post anything. The held attempt cookie (`__Host-bifrost_heimdall_<slot>`, HttpOnly, so script cannot clear it) stays until its ten minutes end and occupies one of the two slots meanwhile; a new start reuses a free or the oldest slot, so nothing blocks. Clearing it would need a server route reached by GET or a post from the failure path, which the hardened script deliberately does not do.
