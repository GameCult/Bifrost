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

Exactly one client is registered, from configuration, and seeded or updated at startup. Consent is implicit
(first-party). Any other `client_id`, an unregistered `redirect_uri`, or a request without PKCE is refused by the
provider before any sign-in redirect.

## Configuration

The section is `Bifrost:Oidc`. Absent, the provider surface is not mapped at all and discovery answers 404. Present,
it must be complete: an unreadable or wrong-shaped file stops startup with a message that names the setting and never
echoes its value.

| Setting | Meaning |
| --- | --- |
| `Bifrost:Oidc:SigningKeyPath` | PEM file with an EC P-256 private key. Tokens and id_tokens are signed ES256; JWKS publishes exactly that key. A non-P-256 or public-only key stops startup. |
| `Bifrost:Oidc:EncryptionKeyPath` | File holding 32 random bytes as base64 (`openssl rand -base64 32`). Encrypts the access token and the parked authorize request. |
| `Bifrost:Oidc:Clients:Mas:ClientId` | The client id MAS presents. |
| `Bifrost:Oidc:Clients:Mas:ClientSecretPath` | File holding the client secret. |
| `Bifrost:Oidc:Clients:Mas:RedirectUri` | MAS's callback URL, registered exactly. |

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

## MAS configuration

Configure MAS's upstream provider with the issuer above, the client id and secret, the token endpoint auth method
`client_secret_basic`, the id_token signing algorithm `ES256`, scope `openid profile`, and a localpart claim import
from `preferred_username`. MAS mints the Matrix localpart on first sign-in, so the claim must never change for an
account.

## Remaining scar

Heimdall still mints its own account id in code (`src/app.ts` `createAccount`). That id is an authenticator handle
and chat does not need it to move.
