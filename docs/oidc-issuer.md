# Bifrost OIDC issuer

Bifrost is the GameCult identity issuer. It runs an OpenID Connect provider on its public host so that Matrix
Authentication Service (MAS) can sign chat users in with their Bifrost account. Heimdall authenticates people into
Bifrost; it does not issue the identity chat sees.

The provider is OpenIddict (`OpenIddict.AspNetCore` and `OpenIddict.EntityFrameworkCore`, 7.7.1). Bifrost owns two
decisions at the authorize endpoint: who the chat user is, and whether they may get a session.

## Endpoints

The issuer is `Host:PublicBaseUrl`, normalized by .NET into an absolute URI. For a bare host that adds a trailing
slash, so for `https://bifrost.gamecult.org` the issuer string is `https://bifrost.gamecult.org/`. Configure MAS with
that exact string.

| Endpoint | Path |
| --- | --- |
| Discovery | `/.well-known/openid-configuration` |
| JWKS | `/.well-known/jwks` |
| Authorization | `/connect/authorize` |
| Token | `/connect/token` |
| Userinfo | `/connect/userinfo` |

Only the authorization code flow exists, and PKCE (S256) is required. There is no implicit, hybrid, password,
client-credentials or device flow, no refresh token, and no dynamic client registration. The token endpoint takes
`client_secret_basic` only. Scopes are `openid` and `profile`.

## The one client

Exactly one client is registered, from configuration, and seeded or updated at startup; every other application
row (with its authorizations and tokens) is deleted at startup. Any other `client_id`, an unregistered `redirect_uri`, or a request without PKCE is refused by the
provider before any sign-in redirect.

## Configuration

The section is `Bifrost:Oidc`. Absent, the provider surface is not mapped at all and discovery answers 404. Present,
it must be complete: an unreadable or wrong-shaped file stops startup with a message that names the setting and never
echoes its value.

| Setting | Meaning |
| --- | --- |
| `Bifrost:Oidc:SigningKeyPath` | PEM file with an EC P-256 private key. Tokens and id_tokens are signed ES256; JWKS publishes exactly that key. A non-P-256 or public-only key stops startup. |
| `Bifrost:Oidc:EncryptionKeyPath` | File holding 32 random bytes as base64 (`openssl rand -base64 32`). Encrypts the tokens OpenIddict issues, the access token and the parked authorize request among them. |
| `Bifrost:Oidc:Clients:Mas:ClientId` | The client id MAS presents. |
| `Bifrost:Oidc:Clients:Mas:ClientSecretPath` | File holding the client secret. |
| `Bifrost:Oidc:Clients:Mas:RedirectUri` | MAS's callback URL, registered exactly. |
| `Bifrost:Oidc:BrakePath` | Optional. While a file exists at this path, `/connect/authorize`, `/token` and `/userinfo` answer 503 `temporarily_unavailable` and nothing is issued; delete the file to release. Checked per request, so no restart. Unset: no marker brake. |
| `Bifrost:Oidc:Limits:*` | `PerAddressPermits` (30), `Window` (1 min), `MaxTrackedKeys` (10000), `MaxTokenRows` (20000), `PruneInterval` (5 min). |
| `Bifrost:Oidc:Clients:Mas:BackchannelLogoutUri` | Optional. MAS's back-channel logout URL, `https://<mas>/upstream/backchannel-logout/<provider ULID>`; absolute http(s) or startup fails. Unset: no logout is sent and no authorization is revoked. |

There are no development or ephemeral keys in the host. Key files are provisioned like the other
`[workload.secret_files]` paths.

## Claims

| Claim | Value |
| --- | --- |
| `sub` | `UserAccount.Id`, formatted `D` |
| `preferred_username` | `NormalizedBifrostIdentity` (minted once; there is no rename path) |
| `name` | `DisplayName`, else `BifrostIdentity` |

No e-mail, no Heimdall id, no GitHub id, and no membership detail leaves Bifrost.

## Who may sign in

The authorize endpoint reads the cookie session and the same actor facts the `/App` policy reads.

- No session: challenge to `/auth/sign-in`, which resumes the original request after sign-in. OpenIddict parks the
  full request (its request token, in the OpenIddict token table, expiring after ten minutes, spent when used) and
  the return path carries only a short `request_uri`, so `LocalReturnPath.MaxLength` stays 256.
- Signed in, membership `Suspended`: redirect to the client's `redirect_uri` with `error=access_denied` and
  `error_description=The account is suspended.`
- Signed in with no membership, `Authenticated` or `PendingApproval` (the chat tier): treated like an active member for
  sign-in to chat and nothing else. The token carries the same `openid profile` claims; the issuer writes no membership,
  role or vote, and every member or governance surface (`/App`, `/eve/governance`, `/api/sealed-motions`, the fork desk)
  still asks for `Active` itself, so neither the session nor the token opens them.
- Signed in, with an account that can chat, but `NormalizedBifrostIdentity` empty: `error=access_denied` with
  `error_description=The account has no Bifrost identity.`
- Signed in, with an account that can chat, but the identity is a reserved localpart (`BifrostIdentityService.IsReserved`: the 13 Synapse
  accounts and the system names, the same 28 as MAS `banned_usernames`): `error=access_denied` with
  `error_description=The account's Bifrost identity is reserved.` Creation refuses these names, but a row minted before
  that refusal may hold one and there is no rename path, so the issuer never vouches for a Mind's or a system's localpart. The refusal is at authorize only: a code or access token issued for such a row before this check deployed stays redeemable until it expires (minutes), and token and userinfo re-read membership, not the name. Whether any such row exists is not known from the code; one query of `UserAccounts` against the 28 names would say.
- Otherwise: a code is issued.
- Token endpoint and userinfo: suspension is read again; an account suspended or deleted since gets `invalid_grant` (token) or 403 (userinfo). The logout reconciler ends the sessions of those same accounts; a chat-tier account is never logged out for lacking membership.

## Back-channel logout

When `BackchannelLogoutUri` is set, a hosted reconciler (`OidcLogoutReconciler`) runs every 30 seconds. It lists the
subjects holding valid authorizations of the MAS client; for each subject who may no longer chat (the same
reading the token and userinfo endpoints use: membership `Suspended`, a deleted account, or a subject that is not an account id; an
account with no membership or a pending one may chat and is left alone) it POSTs one OIDC back-channel logout token to MAS, and only after a 2xx revokes that subject's authorizations,
which also invalidates their tokens. Membership is never written by this path: any way of being suspended or deleted (admin
action, sync, a database edit) is covered because the work is derived from membership state.

The token is an ES256 JWT signed with the issuer key, header `typ` `logout+jwt`, with `iss` as the id_token carries it,
`aud` the MAS client id, `sub` the account id, `iat`, `exp` two minutes later, a fresh `jti` and the
`http://schemas.openid.net/event/backchannel-logout` event. It carries no `nonce` and no `sid`; MAS finds the sessions
by `sub`. It is sent as `application/x-www-form-urlencoded` field `logout_token` with a 10 second timeout, and the
response body is never logged.

Failure handling is per subject, in memory: a 5xx, a timeout or a network fault backs off exponentially from 30 seconds
to 15 minutes; a 400 (token refused) or 404 (unknown provider ULID) is a configuration fault, logged by status only,
and retried at the 15 minute cap. Nothing is revoked until MAS answers 2xx. A restart loses the backoff and nothing
else: the authorizations table is the queue, so a later-set URI also catches up on earlier demotions.

Tradeoff: delivery latency is up to one tick, and a member demoted and re-promoted inside one tick keeps their sessions.

MAS verified at v1.28.0 (`crates/handlers/src/upstream_oauth2/backchannel_logout.rs`): the token is checked like an
id_token (`iss`, `aud`, `alg`, JWKS signature), `exp` and `iat` are required, `sub` or `sid` is required, `nonce` must be
absent. Owed on the MAS side (gamecult-chat): the provider's `backchannel_logout_uri` and
`on_backchannel_logout: logout_all`. Re-check against the MAS version pinned there.

## MAS configuration

Configure MAS's upstream provider with the issuer above, the client id and secret, the token endpoint auth method
`client_secret_basic`, the id_token signing algorithm `ES256`, scope `openid profile`, and a localpart claim import
from `preferred_username`. MAS mints the Matrix localpart on first sign-in, so the claim must never change for an
account.

## Limits, prune, brake

`OidcGuardMiddleware` sits ahead of OpenIddict and guards the three endpoints, matched by path segment so a trailing slash
is no way around it. In order: the marker brake (503; a marker that cannot be read is treated as engaged); one per-address
budget over the three endpoints together (429 with `Retry-After`; the address is the connection's after forwarded-headers
handling that trusts only the local proxy, IPv6 keyed by /64); and a cap on token rows: a new authorize request (no
`request_uri`) is refused with 503 while the table holds `MaxTokenRows`. Any failure inside the guard is a 503, never a pass.
MAS's flow is a handful of requests per sign-in from one address, far under the defaults (30 a minute is about 15 sign-ins).

There is no per-client budget. The one client's id is public and what a caller names is unverified, so a budget keyed on it
would let anyone spend the real client's permits and lock sign-in out. The bound on the anonymous endpoint is the address
budget plus the row cap, and the cap is itself a switch an attacker with many addresses can flip: it refuses new sign-ins
(never resumed ones) until the next prune frees rows, which is the bounded failure chosen over unbounded parked rows.
Not parking state before the user authenticates (as pushed authorization requests avoid by authenticating the client first)
would remove the switch, and OpenIddict's request caching does not offer that.

`OidcPruner` runs every `PruneInterval` and deletes tokens past expiry (parked authorize requests, codes, access tokens) or no
longer valid, then authorizations no longer valid or ad hoc with no token left (younger than five minutes are left alone).
Live rows are untouched. Plain EF queries rather than OpenIddict's own prune, which uses ExecuteDelete and cannot run on the
in-memory provider the tests use.

`docs/oidc-reserved-accounts.sql` is the read-only query for existing accounts that hold one of the 28 reserved names.

## Not built

Checked against the Window B list (2026-11-11). Present and tested: discovery, JWKS, authorize/token/userinfo, PKCE S256,
the one client, claims to a Matrix-legal localpart (`[a-z0-9._-]`, 3-80, lower case), reserved names at creation and now at
issuance, back-channel logout. Not present:

- **MAS upstream block.** `gamecult-ops` `chat/matrix-auth/config.yaml` has no `upstream_oauth2` (provider ULID,
  `on_conflict: fail`, `on_backchannel_logout: logout_all`, the redirect URI above); that lives in the ops repo.

## Remaining scar

Heimdall still mints its own account id in code (`src/app.ts` `createAccount`). That id is an authenticator handle
and chat does not need it to move.
