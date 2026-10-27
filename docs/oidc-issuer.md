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
- Signed in, membership not `Active`: redirect to the client's `redirect_uri` with `error=access_denied` and
  `error_description=The account is not an active GameCult member.`
- Signed in, `Active`, but `NormalizedBifrostIdentity` empty: `error=access_denied` with
  `error_description=The account has no Bifrost identity.`
- Signed in and `Active`: a code is issued.
- Token endpoint: membership is read again; an account no longer `Active` gets `invalid_grant` and no tokens.

## Back-channel logout

When `BackchannelLogoutUri` is set, a hosted reconciler (`OidcLogoutReconciler`) runs every 30 seconds. It lists the
subjects holding valid authorizations of the MAS client; for each subject whose membership is not `Active` (the same
reading the token and userinfo endpoints use; a deleted account or a subject that is not an account id counts as not
`Active`) it POSTs one OIDC back-channel logout token to MAS, and only after a 2xx revokes that subject's authorizations,
which also invalidates their tokens. Membership is never written by this path: any way of leaving `Active` (admin
action, sync, a database edit, a future suspend) is covered because the work is derived from membership state.

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

## Remaining scar

Heimdall still mints its own account id in code (`src/app.ts` `createAccount`). That id is an authenticator handle
and chat does not need it to move.
