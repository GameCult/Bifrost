# Passkeys: strong proof for unlinking a login

A Heimdall-backed member can enrol WebAuthn passkeys on `/Membership/LinkedAccounts` and use one as the strong proof that unlinking a provider login needs. Bifrost verifies the passkey; Heimdall accepts Bifrost's word through its app-asserted step-up route (`POST /v1/apps/bifrost/step-up`, Heimdall `docs/service-contract.md`, `docs/app-profiles/bifrost.md`). Scoping and the option comparison are in the handoff `strong-proof-scoping-20261113.md` (option 1a with the S1 sink).

## Scope

- Accounts that have a `HeimdallAccountId`: the accounts that can unlink at all. A Bifrost-native-only account (a native `BifrostIdentity` or GitHub sign-in) has no logins to unlink and gets no passkeys from this cut; the page shows it nothing.
- Unlink only. A passkey is not a sign-in method here.

## The library: Fido2NetLib (`Fido2` 4.3.3)

Decision: use `Fido2` 4.3.3 (NuGet id `Fido2`, repository `passwordless-lib/fido2-net-lib`) for every byte of WebAuthn parsing and verification. Bifrost hand-rolls no CBOR, COSE or signature code.

What was checked on 2026-11-13, against the package and the repository, not from memory:

| Question | Finding |
|---|---|
| Licence | MIT, in the nuspec (`<license type="expression">MIT</license>`) and in the repository's `LICENSE.txt` (GitHub reports `MIT`). Its closure: `Fido2.Models` MIT; `NSec.Cryptography` 25.4.0 MIT; `libsodium` 1.0.20.1 ISC; `System.Formats.Cbor` 9.0.24, `Microsoft.IdentityModel.JsonWebTokens` 8.2.0, `Microsoft.Extensions.Http` 9.0.0, `Microsoft.Bcl.Memory` 9.0.14 all Microsoft MIT. All open source. |
| Maintained | Not archived; repository pushed 2026-11-11; 4.3.3 released 2026-11-11 and 4.3.2 on 2026-10-29; about 1,460 stars. |
| net8.0 | The 4.3.3 nuspec has a `net8.0` dependency group (and `net10.0`); it targets the framework this project targets. |
| Packed attestation | `Src/Fido2/AttestationFormat/` holds `Packed`, `None`, `FidoU2f`, `Tpm`, `AndroidKey`, `AndroidSafetyNet`, `Apple`, `AppleAppAttest`. Bifrost asks for `none` attestation and does not use a metadata service, so it does not depend on any of them being trusted; a client that sends another format still has it parsed and its statement verified structurally by the library. |
| What verification covers | Read in `AuthenticatorAssertionResponse.cs`: challenge, origin (against `Fido2Configuration.Origins`), `webauthn.get` type, RP ID hash, user-present and user-verified flags (when required), signature over `authData || SHA-256(clientDataJSON)` with the stored COSE key, user handle ownership, and the sign count (see "Sign count" below). Enrolment verifies the attestation, the credential id and the key. |
| Transitive weight | Heavier than the repository's other packages: `Microsoft.Extensions.Http` 9.0.0 pulls `Microsoft.Extensions.*` 9.0.0, so existing 8.x entries move to 9.0.0 (seven in `src/Bifrost.Web/packages.lock.json`; twelve `Microsoft.Extensions.*` entries plus `System.Diagnostics.DiagnosticSource` in the test project's lockfile, which sees them too; app-local on top of the net8 shared framework, a supported combination), and the lockfiles gain `Fido2`, `Fido2.Models`, `Microsoft.Bcl.Memory`, `Microsoft.Extensions.Http`, `Microsoft.Extensions.Diagnostics[.Abstractions]`, `Microsoft.Extensions.Configuration[.Binder]`, `Microsoft.Extensions.Options.ConfigurationExtensions`, `NSec.Cryptography`, `System.Formats.Cbor` and the native `libsodium` package. `Microsoft.IdentityModel.JsonWebTokens` is requested at 8.2.0 and already resolved to 8.19.2 (through OpenIddict) before this change; it still does (the Linux x64 glibc and musl binaries ship in the package's `runtimes/`). `packages.lock.json` in both projects is regenerated and committed. |
| Alternatives | Not needed: the candidate passed. |

Not verified: that the 9.0.0 `Microsoft.Extensions.*` assemblies behave on the live host's ASP.NET Core 8 runtime beyond the test host (the suite runs the real app under it); that `libsodium` loads on Yggdrasil (the EdDSA test loads it here, on Debian x64).

The library is used through four calls: `RequestNewCredential`, `MakeNewCredentialAsync`, `GetAssertionOptions`, `MakeAssertionAsync`. `PasskeyRelyingParty` builds it from `Host:PublicBaseUrl`.

## Sign count

The stored sign count is updated on every successful assertion, and the library refuses a counter that does not increase once either side is non-zero. Authenticators that always report zero (most synced passkeys) are accepted every time and get no clone detection from this: both counters stay zero. An authenticator that reported a non-zero count and then reports zero is refused. Clone detection is therefore real only for hardware keys and other counting authenticators.

## Relying party

RP ID is the host of `Host:PublicBaseUrl`, the one origin is its scheme, host and port. They come from configuration and from nothing the browser sends. Passkeys are unavailable, with a warning logged once at start, when `Host:PublicBaseUrl` is not https or its host is not `Host:ExpectedHost`: the page offers none and every ceremony endpoint refuses (`passkeys_unavailable`). `PasskeyRelyingParty` is resolved at startup, so a misconfiguration is logged once at boot (not at the first ceremony). `Host:ExpectedHost` must be a bare host name: one written with a port (`host:443`) is reported as such and also makes passkeys unavailable, because it is not the RP ID's host. No new configuration key exists.

If passkeys are unavailable while an account holds a strong passkey, the page shows no unlink form and says so: unlinking needs the passkey, the Google route stays closed (`passkey_required`), and the member can revoke their passkeys on the page (revoking does not need the relying party) to fall back to the Google proof, or wait for the operator to fix the server. This is a lockout until the operator or the member acts; it fails closed.

## State

- `PasskeyCredentials` (migration `20261113120000_AddPasskeyCredentials`, additive, applied at boot like the others): per `UserAccount` id, `CredentialId` (unique), `PublicKey`, `SignCount`, `Transports`, `AaGuid`, `Label`, `CreatedUtc`, `StrongFromUtc` (`CreatedUtc` + 48 h), `RevokedUtc`, `LastUsedUtc`. No secret exists to store: the private key never leaves the authenticator. Rows cascade with the account.
- `PasskeyCeremonies` (in memory, this process only, like the Linked accounts list): the short-lived single-use records of the flows, each bound to a Heimdall account id and a kind (and to an identity id for unlink). A restart empties it and every flow is begun again; with more than one Bifrost instance a flow must finish on the instance that began it, otherwise it is refused and begun again. Both fail closed (a lost ticket, challenge or fact refuses; it never grants). It is chosen over a sealed cookie because a cookie cannot be spent: a copied cookie would replay.

| Kind | Lifetime | Spent |
|---|---|---|
| enrolment ticket | 5 min | by the registration post |
| enrolment challenge | 2 min | by the registration post |
| unlink challenge | 2 min | by the assertion post (success or not) |
| unlink assertion fact | 5 min | by the completion that asks Heimdall for the step-up |

A challenge kind has one live record per account: a newer one replaces the older. The table holds at most 2048 records and refuses past that.

A caller's own preconditions are checked before anything is spent. A registration needs the live ticket cookie (a session thief has none), a decodable body and room under the cap; an assertion needs a decodable body naming one of the session account's own strong, un-revoked credentials. Only then are the verification permit, the challenge and (for enrolment) the ticket taken. So junk posted by someone who holds only the session burns neither the member's ticket and challenge nor the 20 verification permits per 10 minutes. Asking for options spends a separate options budget (20 per 10 minutes), which a session holder can exhaust: that blocks new options for a while and nothing else. A session holder can also replace the member's pending challenge by asking for new options, which makes the member's assertion answer `passkey_expired` and the member tries again; that session can already revoke passkeys, so it is not a new capability. A thief who has read a credential id from the options can still post a bad signature for it and burn the challenge once; the member retries.

## Enrolment

1. The member is on the page with a current list (Bifrost will not guess a proof provider) and presses "Sign in again to add a passkey". Bifrost starts a fresh ordinary Heimdall sign-in through a provider the account holds (an attempt with purpose `EnrolProof`, the same machinery as the list and link proofs, `ExpectedAccountId` the session's account).
2. In that sign-in's completion, when the proven account is the session's own, Bifrost issues an enrolment ticket and sets its id in `__Host-bifrost_passkey_ticket` (HttpOnly, Secure, 5 minutes). The ticket exists only because a fresh sign-in completed in this browser: a stolen session cookie alone has no ticket and cannot get registration options.
3. The page's script asks for creation options (antiforgery header), calls `navigator.credentials.create` (user verification required, resident key preferred, `none` attestation, existing credentials excluded) and posts the base64url result. Bifrost spends the ticket and the challenge, verifies, and stores the credential with `StrongFromUtc` = now + 48 hours.

A label is limited to 40 text elements (cut by rune and grapheme, never inside a surrogate pair) and loses control and format characters (bidi overrides, joiners, zero-width marks).

Cap: five un-revoked passkeys per account (`passkey_limit`; a sixth starts no sign-in). Every options request, enrolment, assertion and enrolment start spends a permit of one `FixedWindowBudget` per account (20 per 10 minutes, `rate_limited`).

## Cooling-off

A passkey is strong from `StrongFromUtc` (at that instant and after), until revoked. Until then it is listed on the page with its time and cannot unlink anything. So someone who controls both a member's session and a provider login long enough to add a key of their own still waits 48 hours with the key visible, and the member can revoke it from the page. Revoking needs no strong proof (it only removes capability) and is shown.

## Unlink with a passkey

When the account has a strong passkey, the page's Unlink button runs a WebAuthn assertion instead of the Google step-up, and the Google route (`Unlink` handler) answers `passkey_required` and starts nothing: a member who holds a strong passkey is not offered the weaker proof. With no strong passkey the Google step-up stays as it was, and when neither exists the page says what to do (add a passkey, with the time a cooling-off one becomes strong).

1. The script asks for request options for the chosen identity (`allowCredentials` is the account's strong passkeys; user verification required). One live challenge per account, bound to the identity.
2. The browser's assertion is posted. Bifrost spends the challenge, requires the credential to be the session account's own, not revoked and past `StrongFromUtc`, and has the library verify origin, RP ID, challenge, user verification, signature and sign count. On success it records the new sign count and last use, and issues the single-use fact "passkey assertion verified" for this account and this identity (5 minutes; no credential, no bearer).
3. Bifrost starts a fresh ordinary Heimdall sign-in for the account (attempt purpose `UnlinkPasskeyProof`; proof provider as on the page: one the current list shows, never a guess). The attempt carries the fact's id.
4. In the completion, which is the only place the bearer exists: the fact is taken (missing, expired, spent, for another account or identity: no Heimdall step-up call and `passkey_expired`); the credential the fact was made with must still be the account's own, un-revoked and strong (a passkey revoked since the assertion: no step-up call, `passkey_failed`); the app secret, the bearer and `{method: "bifrost-passkey", nonce}` go to `POST /v1/apps/bifrost/step-up` (nonce: 32 fresh random bytes, base64url, 43 characters); the returned `accessToken` goes to the existing unlink call; everything is dropped. The bearer and the minted token are never stored, logged, put in a cookie or a redirect, or rendered.

Heimdall's refusals are shown as closed codes and nothing is retried in the flow: `401 sign_in_required` is `sign_in_required`; `403`/`400`/`401 app_auth_required` is `step_up_unavailable`; `409 step_up_nonce_replayed` is `step_up_replayed`; `429` is `step_up_rate_limited`; anything else, or Heimdall down, is `error`. A member whose attempt ends this way starts the unlink again (a new assertion).

## Abuse cases and what stops them

Each is a test in `tests/Bifrost.Web.Tests/PasskeyTests.cs` unless noted.

| Case | Defence |
|---|---|
| Stolen session cookie tries to enrol | No ticket without a fresh Heimdall sign-in completed in the victim's own browser. A ticket is per account, spent once, expires in 5 minutes, and is held by id in an HttpOnly cookie |
| Stolen session tries to unlink | Needs an assertion by a strong passkey of the account; the Google route is closed while one exists; no step-up is asked of Heimdall without the fact |
| Attacker who controls the provider session enrols their own key | Cooling-off: 48 hours before it counts, shown on the page, revocable by the member |
| Replay of an assertion, double submit | The challenge is single-use; two concurrent posts start one sign-in |
| Assertion for another account or identity | The credential must belong to the session's account; the challenge, the fact and the attempt are bound to the account and the chosen identity |
| Phishing site | The library checks origin and RP ID hash against the configured values; a registration or assertion with another origin or RP ID is refused |
| Cloned authenticator | A sign count that does not increase is refused; the stored count is updated on every use |
| Cooling-off boundary | Strong at exactly `StrongFromUtc`, not one second before |
| Revoked credential | Never strong; a revoke between challenge and assertion also refuses, and so does one between the assertion and the completion |
| Junk from a session thief | Preconditions first, nothing spent before they hold (see State) |
| No user verification | Required at enrolment and at assertion |
| Cross-site POST | Antiforgery on every page POST including the JSON option requests |
| Bearer anywhere | Only in the completion request's stack; tests look in logs (with HttpClient trace logging on), cookies, redirects, the page and the decrypted attempt |
| Reused or replayed step-up | A fresh nonce each call; Heimdall's 409 is shown, not retried |
| Brute force of ceremonies | One `FixedWindowBudget` per account |

## Account-scoped lookup and the allow list

An assertion is accepted only for a credential of the session's own account (strong, un-revoked), and the user handle, when the authenticator sends one, must be that account's id. The request options also carry the account's strong credentials as `allowCredentials`, which the library enforces, so a credential of another account is refused twice. The test for the account-scoped lookup (`The_credential_lookup_is_scoped_to_the_session_account_even_when_the_allow_list_is_empty`) takes the allow list out of the other account's challenge through the in-process ceremony store, which production code cannot do, so the lookup is exercised alone without weakening the server.

## Content Security Policy

Bifrost sends no `Content-Security-Policy` header (nothing in the application sets one; this predates passkeys). The passkey script is first-party and external so that a policy of `script-src 'self'` would admit it, but nothing enforces that today.

## The script

`wwwroot/js/passkeys.js` is first-party, loaded by the page with an external `src` (nothing inline, no third-party asset, no `eval`, no `innerHTML`). It asks for options with the antiforgery header, calls `navigator.credentials`, and submits a same-origin form of base64url strings (`id`, `rawId`, `attestationObject` or `authenticatorData`, `clientDataJson`, `signature`, `userHandle`, `transports`). On any failure it navigates to this page with a result code from a closed set; nothing from a response is shown. `PasskeyScriptTests` runs it under Node against a simulated browser and pins what it posts.

## What it does not do

- No passkey sign-in; passkeys prove an already signed-in session only.
- No native-only accounts (no Heimdall account, nothing to unlink).
- No recovery without mail: a member who loses every passkey revokes them and falls back to the Google step-up if a Google login is linked, or waits for the mail gate (`docs/registration-gates.md`) and the email-code factor that follows it. The email code is not built.
- Named limit and next hardening: enrolling a second passkey should require an assertion by an existing strong passkey, and enrolment should use a Google step-up when a Google login is held. Today enrolment needs only the fresh ordinary sign-in and the cooling-off, so an attacker who controls a provider session can add a key that becomes strong after 48 hours unless the member sees and revokes it. Not built; a follow-up is filed.
- Revoking needs no strong proof, so a stolen session can revoke a member's passkeys and fall back to the Google proof: no weaker than the account was before it had a passkey.
- A passkey synced through a cloud account the attacker also controls is a residual risk, as in the scoping note.
- Heimdall cannot tell that a passkey was checked: `bifrost-passkey` is Bifrost's word, gated by the app secret and a live bearer (Heimdall `docs/app-profiles/bifrost.md`).
- Not verified here: a real authenticator or browser. Tests use a software authenticator built from the WebAuthn specification and the script runs under Node against a simulated browser.

## Where the code is

- `Features/Passkeys/PasskeyService.cs`: enrol, assert, revoke, list; `PasskeyCeremonies.cs`: the single-use records and the rate limit; `PasskeyRelyingParty.cs`: RP ID, origin, library construction.
- `Features/Heimdall/HeimdallAuthService.cs`: the `EnrolProof` and `UnlinkPasskeyProof` attempts, `StepUpAsync`.
- `Pages/Membership/LinkedAccounts.cshtml[.cs]`, `wwwroot/js/passkeys.js`.
- `Domain/BifrostModels.cs` (`PasskeyCredential`), `Data/Migrations/20261113120000_AddPasskeyCredentials.cs`.
- Tests: `PasskeyTests.cs`, `PasskeyScriptTests.cs`, `Support/SoftwareAuthenticator.cs`, and the step-up route of the fake Heimdall in `Support/HeimdallTestKit.cs`.
