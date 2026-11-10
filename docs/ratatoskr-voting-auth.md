# How a Ratatoskr voting module authenticates to Bifrost (design, no code)

Status: proposal, 2026-11-10, written by Heimdall (Self) against `origin/main` 276f611. Docs only; nothing here is built, ruled or deployed.
Labels: **FACT** = read at the cited `file:line` at 276f611 (paths under `src/Bifrost.Web/` unless written otherwise). **INFERENCE** = my reading, not stated by code. **EXTERNAL** = OpenIddict/ASP.NET behaviour I know from its documentation and did not run here; each one needs a probe before the cut (section 11).
Companion reads: `/opt/commons/handoff/bifrost-browser-voting-facts.md` (Eyes, at 416f11a; its Program.cs/OIDC lines still hold at 276f611, only `Features/Motions/MotionGovernanceService.cs` changed in between, in the close path), `heimdall-ratatoskr-sso-plan*.md`.

## 1. The problem in four facts

- FACT: a member's only Bifrost credential is the cookie `__Host-bifrost_session` (`Program.cs:178-189`; `Features/Shared/BifrostCookies.cs:10`). `__Host-` makes it host-only: chat.gamecult.org never holds it.
- FACT: the member API carries no CORS: the single CORS policy is the anonymous tally GET (`Features/ConceptVotes/ConceptTallyExport.cs:8-27`), so a JSON POST from the chat origin is blocked at preflight.
- FACT: Bifrost is an OIDC issuer with exactly one client, MAS, confidential, secret basic auth only, scopes `openid profile` (`Features/Oidc/OidcServiceCollectionExtensions.cs:39-46`; `Features/Oidc/OidcClientSeeder.cs:113,116-124`; startup deletes every other client, `:143-149`). No OpenIddict validation handler is registered (grep for `AddValidation` under `src` returns nothing), so no access token authenticates any `/api/*` route.
- FACT: there is no JSON read or cast API for Standard motions (Razor form POST with 302 and no receipt: `Features/Motions/MotionGovernanceService.cs:47-86`, `Pages/App/Motions/Index.cshtml.cs`); sealed-ballot motions have JSON endpoints but no list (`Program.cs:551-596`).
- INFERENCE: the access token chat holds today is Matrix's, minted by MAS after MAS authenticates the person at Bifrost (SSO plan section D). Bifrost's own access token goes to MAS, never to the browser. PR #24's "the OIDC access token Bifrost already issues to chat" is therefore not true as written: this design makes Bifrost issue a token to chat directly.
- FACT (deployment): Bifrost has no `Bifrost__Oidc__*` variable live, MAS is disabled, Synapse is its own password authority (`heimdall-ratatoskr-sso-plan-live-read-20261110.md` section 1). Everything below is for after OIDC is enabled; `Bifrost:Oidc` absent means the provider surface is not mapped (`docs/oidc-issuer.md:36`).

## 2. Proposal in one paragraph

Ratatoskr's voting module is a second OIDC client of Bifrost: **public, PKCE (S256), no secret**, redirect URI exactly one URL on https://chat.gamecult.org. It runs authorization-code + PKCE in a top-level redirect to Bifrost (so the person's existing `__Host-` session cookie is used first-party; chat never sees it), receives a **10-minute access token for audience `bifrost-votes` and scopes `votes.read` / `votes.cast`**, keeps it in memory only, and sends it as `Authorization: Bearer` to a **new `/api/votes` surface that accepts bearer tokens and refuses cookies**. CORS on exactly two route groups (`/connect/token`, `/api/votes`) allows exactly `https://chat.gamecult.org`, no credentials. Everything else in Bifrost keeps its current cookie-only behaviour. The Persona/Mind signed path is untouched.

## 3. Client registration

- A second client entry beside MAS, from configuration (`Bifrost:Oidc:Clients:Ratatoskr:{ClientId,RedirectUri}`), seeded by the same startup seeder. FACT the seeder today builds one descriptor and deletes all others (`OidcClientSeeder.cs:107-149`); it needs to build two and delete the rest.
- `ClientType = Public`, no `ClientSecret`; permissions: authorization endpoint, token endpoint, authorization_code grant, response type `code`, scopes `profile`, `openid`, `votes.read`, `votes.cast`. PKCE is already required server-wide (`OidcServiceCollectionExtensions.cs:39`).
- `RedirectUri` is one absolute URL, compared exactly (OpenIddict does exact match; EXTERNAL). No wildcard, no localhost entry in production configuration; a dev client is a separate config file never deployed.
- FACT the token endpoint accepts only `client_secret_basic` (`OidcServiceCollectionExtensions.cs:45-46` clears and sets the list). EXTERNAL/needs probe: a public client authenticates by `client_id` alone and OpenIddict still accepts it with that list; if it does not, add `none` to the list for public clients only.
- Not in scope: dynamic registration, implicit/hybrid flows, a client secret in the browser. The MAS client and its flow do not change.

## 4. Scopes, audience, what a token carries

| Scope | Grants | Never grants |
|---|---|---|
| `openid`, `profile` | `sub` = `UserAccount.Id`, `preferred_username`, `name` (FACT `OidcEndpoints.cs:61-66`, `docs/oidc-issuer.md:52-60`) | email, Heimdall id, membership detail |
| `votes.read` | read motions the member may see, their own ballot status, results after close | other members' sealed ballots; weights of other members |
| `votes.cast` | cast/change a Standard vote; sealed commit and reveal for the token's own `sub` | anything in the next list |

Audience: access tokens for these scopes carry audience `bifrost-votes` (INFERENCE: OpenIddict `SetResources`/audience per request; EXTERNAL detail). `/api/votes` requires the audience **and** a vote scope. This stops the MAS client's access token (same issuer, same signing key, scopes `openid profile`) from being accepted on `/api/votes`, and a Ratatoskr token from being accepted by anything else.

**A token may never:** grant or change a role (`RoleAssignments`); set, read-modify or choose a weight (weight is `Membership.EffectiveVotingWeight` computed server-side at cast, `Domain/BifrostModels.cs:357-361`; the API request has no weight field and the receipt does not echo it); open, close or edit a motion; register or revoke Mind keys (`Program.cs:587-596`, moderator, cookie only); reach `/App/*`, `/eve/*`, `/connect/userinfo` beyond the three profile claims; mint another token (no refresh token, `docs/oidc-issuer.md:25`; no token-exchange); act for a different account (the voter is always `sub`, never a body field). These are enforced by structure, not by a deny list: the vote endpoints take the account from the principal and expose no parameter for the rest.

## 5. The bearer surface

- Register OpenIddict validation (local server) as a **named, non-default** authentication scheme `BifrostBearer`. FACT the default scheme is Cookie (`Program.cs:178-189`), so every existing endpoint stays cookie-only and a bearer token cannot reach `/App`, `/api/sealed-motions` or `/eve` (a test pins it).
- New group `app.MapGroup("/api/votes")` with a policy `ActiveMember + scope + audience` whose `AuthenticationSchemes` is `BifrostBearer` only. A request that also carries the Bifrost session cookie is judged on the bearer alone, so the surface has no ambient credential: this is what removes CSRF from it (section 9).
- **Actor resolution is a new job.** FACT `CurrentBifrostActorAccessor` builds the actor from cookie claims (`BifrostIdentity`, `HeimdallAccountId`, GitHub `NameIdentifier`) and returns Anonymous when none are present (`Features/Shared/CurrentBifrostActorAccessor.cs:33-46`). A bearer principal has only `sub`, so existing services would see an anonymous actor. FACT `OidcEndpoints.IsStillActiveMemberAsync` already resolves by `sub` and re-reads membership (`OidcEndpoints.cs:121-132`). Proposal: one accessor path "principal with `sub` and `BifrostBearer` → load `UserAccount` + `Membership` by id", reusing that reading, and the `/api/votes` handlers call the existing services (`MotionGovernanceService.CastVoteAsync`, `SealedBallotService.CommitAsync/RevealAsync`) with that actor. No second vote implementation.
- Endpoints (names are a proposal, shapes belong to the cut spec): `GET /api/votes/motions`, `GET /api/votes/motions/{id}`, `PUT /api/votes/motions/{id}/ballot` `{choice, comment?}` (Standard), `POST …/commit` and `…/reveal` for sealed, `GET …/result`. FACT today's Standard cast returns silently when the motion is not open and throws on an unknown id (`MotionGovernanceService.cs:60-68`); the API wraps it with typed errors (404, 409 not open) rather than copying that.
- Errors are JSON 401/403/404/409/429, never a 302 to a sign-in page (FACT the cookie challenge redirects, `Program.cs:186`; a `fetch` client must get a status it can act on, e.g. 401 with `WWW-Authenticate: Bearer error="invalid_token"`).
- Persona/Mind path: FACT `POST /api/sealed-motions/{id}/mind` is anonymous, Ed25519-signed, rate-limited at 30/min/source and 4096 bytes (`Program.cs:600-642`). Unchanged. Minds never use this bearer flow; `/api/votes` has no Mind route, and a Persona's account is not a Ratatoskr login.

## 6. The flow (browser)

1. Ratatoskr voting module starts login: generates `state`, `nonce`, PKCE verifier/challenge (S256), stores them in `sessionStorage` for the one tab, navigates to `GET /connect/authorize` with `client_id`, exact `redirect_uri`, `scope=openid profile votes.read votes.cast`, `response_type=code`.
2. FACT authorize without a session challenges to `/auth/sign-in` and resumes via a short `request_uri` (`OidcEndpoints.cs:40-47`); with a session and an Active member with an identity it proceeds, otherwise `access_denied` (`:49-59`). Heimdall sign-in and the Bifrost session happen on the Bifrost host exactly as for MAS.
3. Consent (section 7), then 302 to `https://chat.gamecult.org/<redirect path>?code=…&state=…`.
4. The module checks `state`, immediately removes `code` and `state` from the address bar (`history.replaceState`), POSTs `/connect/token` (form: `grant_type=authorization_code`, `code`, `code_verifier`, `client_id`, `redirect_uri`) cross-origin. FACT the token endpoint re-reads membership and refuses a no-longer-Active subject with `invalid_grant` (`OidcEndpoints.cs:81-94`).
5. The access token lives in a JS variable. Not `localStorage`, not `sessionStorage`, not IndexedDB. A page reload logs the module out of Bifrost votes (not out of chat) and the next vote action restarts step 1; with an existing Bifrost session it is two redirects and no typing.

INFERENCE on re-auth without refresh tokens: FACT none exist (`docs/oidc-issuer.md:25`). EXTERNAL: `prompt=none` in a hidden iframe is dead (third-party cookie rules), so renewal is a top-level redirect. That is the cost of a no-refresh-token design; see D3.

## 7. Consent screen: needed, because one hop is cheap and a vote is not

- FACT Bifrost has no consent UI (authorize issues a code directly, `OidcEndpoints.cs:49-78`). MAS needs none (it is the chat's own login service).
- Reasoning (INFERENCE): `votes.cast` lets whoever holds the token change the member's recorded vote. The member is already redirected to Bifrost, so a consent step costs one click and makes the grant visible and revocable in Bifrost, which is the only place it can be. Recommend **explicit consent for `votes.cast`, implicit for `votes.read`** (OpenIddict `ConsentType` per client or per authorization; EXTERNAL).
- Page content: client name ("Ratatoskr, chat.gamecult.org"), the account being used, the two powers in plain words ("see motions and your own ballots", "cast and change your votes"), Allow / Deny, a note that the grant lasts until revoked and tokens last 10 minutes. Served with `frame-ancestors 'none'` and `X-Frame-Options: DENY` (clickjacking). The consent wording for the chat side is `ratatoskr-consent-wording-draft-20261109.md` and is separate from this one.
- A stored authorization (OpenIddict permanent authorization) lets the next login skip the consent page; it is the object revoked in section 8.

## 8. Token lifetime, revocation, receipts, rate limits

**Lifetime.** FACT the code sets no access-token lifetime (only `RequestTokenLifetime` 10 min, `OidcServiceCollectionExtensions.cs:47`); INFERENCE/EXTERNAL OpenIddict's defaults apply (about 1 hour for access tokens, 5 min for codes). Set explicitly: **access token 10 minutes**, authorization code 60 seconds, id_token short. No refresh token. Because every `/api/votes` call re-reads the account and membership (section 5), the lifetime bounds token theft, not demotion.

**Revocation.**
- Demotion is immediate: each request re-checks Active (the reading in `OidcEndpoints.cs:121-132`). A token held by a suspended member returns 403.
- Authorization cleanup: FACT `OidcLogoutReconciler` finds non-Active subjects holding authorizations of the MAS client and tells MAS (`docs/oidc-issuer.md:76-96`). Extend the same derived-from-membership sweep to revoke the Ratatoskr client's authorizations for non-Active subjects (no outbound POST needed; the client is ours). Membership is never written by it.
- Member-initiated revoke: needs a place to list and revoke grants. Not built; D4.
- Operator kill: removing the client from configuration deletes it and its tokens at next start (FACT `OidcClientSeeder.cs:143-149`); a runtime brake is D5.

**Receipts.** Every cast returns a JSON receipt, since Standard votes return none today (facts file section 5): `{motionId, choice, revision, castAtUtc, receiptId}`; sealed commit/reveal keep their existing receipt (`SealedBallotReceipt`, `SealedBallotService.cs`). Receipt contains no weight. The audit trail already records casts (`MotionGovernanceService.cs:85`); the audit row gains the client id and `jti` so a vote can be traced to the grant that cast it (INFERENCE: this is what "receipts" is for after an XSS: the member can see which client cast it).

**Rate limits.** FACT none on the member API (facts file section 5); the primitives exist: `FixedWindowBudget` keyed by account (`Features/ConceptVotes/ConceptVoteRateLimits.cs:14-24`) and a 429 + Retry-After pattern (`Program.cs:603-606`). Proposal: per-account budgets — casts 30 per 10 min (the Mill's number), reads 120 per minute; token endpoint per source address; 429 with `Retry-After`. Numbers are D6.

## 9. Threat list

| Threat | Why it matters here | Answer |
|---|---|---|
| **Token theft by XSS in chat** | Ratatoskr renders other people's Matrix messages; a script in the chat origin can read an in-memory token or call the module's own functions | Token memory-only, 10 min, scopes limited to votes, audience-bound, no refresh. Honest limit: an XSS can **cast during the page's life** whatever the token allows; shortening and scoping bound it, they do not stop it. Mitigations to require: strict CSP on chat.gamecult.org, message sanitisation, and an in-module confirmation step for `votes.cast` that is rendered by the module itself (Ratatoskr's cut). D2 asks whether casting should demand a fresh re-authentication (`prompt=login`, `max_age`) for high-stakes motions. Sealed motions are better off: the salt is generated and held client-side, but a compromised page holds it too. |
| **CSRF** | A third-party page makes the member's browser vote | `/api/votes` accepts no cookie (section 5), so a forged cross-site request carries no credential. CORS has no `AllowCredentials`, so a credentialed cross-origin read is unreadable. The cookie surfaces keep their current protection (FACT: Razor antiforgery, `Index.cshtml:127,139`; the sealed API relies on SameSite and JSON binding, `Program.cs:551-596`) and are untouched. |
| **Authorization-code interception / injection** | Code arrives in a URL on the chat origin | PKCE S256 required (FACT `:39`), exact redirect match, codes single-use and 60 s, `state` checked, URL cleaned at once, `Referrer-Policy: no-referrer` on the callback page. |
| **Login CSRF / mix-up** | Attacker finishes a flow with their own code | `state` bound to the tab, `nonce` checked in the id_token, one issuer only. |
| **Replay** | A captured request resent | Token expiry; codes single-use; Standard cast is `PUT` and idempotent by `(motion, sub)` (FACT one row per member per motion, `MotionGovernanceService.cs:71-86`); sealed commit/reveal already carry `revision` and phase checks; the commitment binds the voter id (`docs/sealed-ballot-motion.md:22-26`). Replaying the same cast cannot double a vote. |
| **Token substitution** | MAS-bound token used at `/api/votes`, or a Ratatoskr token elsewhere | Audience + scope + named scheme (sections 4-5). |
| **Open redirect / redirect abuse** | | One exact redirect URI; Bifrost local return paths unchanged (FACT `Features/Shared/LocalReturnPath.cs:13-31`). |
| **Overreach by a demoted or compromised account** | Membership changes after issue | Per-request Active check; sweep revokes authorizations. |
| **CORS misread as security** | CORS stops browsers only | Not relied on for authorisation; it is an availability measure. Tokens authenticate; non-browser callers need a token. |
| **Consent phishing** | Look-alike client asks for `votes.cast` | One registered client; consent page names it and shows the redirect host. |
| **Scraping / vote spam** | | Rate limits (section 8); no list of other members' ballots exists. |
| **Clickjacking of consent/sign-in** | | `frame-ancestors 'none'` on Bifrost auth pages. |
| **Logout gap** | The Bifrost session ends but a token lives 10 min | Accepted; bounded by lifetime and the Active check. |

## 10. CORS, exactly

- A **second named policy**, separate from `ConceptTallies` (FACT that policy reads `ConceptVotes:AllowedOrigins`, `ConceptTallyExport.cs:13-27`, so reusing it would couple two features' origin lists): `WithOrigins("https://chat.gamecult.org")`, methods `GET, POST, PUT, OPTIONS`, headers `Authorization, Content-Type`, **no `AllowCredentials`**, no exposed headers except `Retry-After`, preflight cache bounded. Origin comes from configuration as one exact value; no wildcard, no subdomain pattern, no `null`.
- Applied by route metadata to `/connect/token` and `/api/votes/*` only. `/connect/authorize` is a top-level navigation and needs none; `/.well-known/*` is not fetched by the module (the client config is static). Every response under those routes carries `Vary: Origin` (the pattern the tally export already follows).
- Bifrost still sends no CORS headers for `/api/sealed-motions/*`, `/App`, `/eve`, `/connect/userinfo` (the facts file's blocker 1 stays closed there on purpose).

## 11. Probes owed before the cut spec (each is an EXTERNAL claim above)

1. A public client with no secret completes code exchange when `ClientAuthenticationMethods` holds only `client_secret_basic`.
2. Per-client explicit consent works with the request-caching path (`request_uri`) used at `OidcEndpoints.cs:42-47`.
3. Audience and scope reach the access token and the validation handler enforces them; the MAS client's token is refused at `/api/votes`.
4. Default token lifetimes before the explicit settings.
5. OpenIddict validation as a non-default scheme leaves cookie-default endpoints unchanged (test: bearer on `/App` and `/api/sealed-motions` is 302/401, as now).
6. CORS preflight on `/connect/token` through OpenIddict's passthrough (the endpoint is mapped as a minimal API at `OidcEndpoints.cs:31`).

## 12. Open decisions

For **Meta**:
- D1 Is explicit consent for `votes.cast` wanted (recommended) or is a first-party client implicit?
- D2 Should a vote on a high-stakes motion demand re-authentication (`prompt=login`) at cast time? Recommend yes for sealed commit and for any motion whose result moves money or roles; no for ordinary reads.
- D3 Accept that a 10-minute token with no refresh means a redirect round-trip to keep voting, or allow a refresh token (it widens theft impact; I recommend against).

For **Eureka** (platform/ops, MAS and deploy order):
- D4 Where does a member list and revoke grants (a Bifrost `/App` page)? Needed before `votes.cast` ships if consent is explicit.
- D5 Is a runtime brake wanted on the Ratatoskr client (disable without redeploy), and who holds it?
- D6 Rate-limit numbers, and whether they belong in configuration (recommended) or code.
- D7 Order against the MAS rollout: this design needs Bifrost OIDC live (it is not, section 1), and the two clients can ship separately; do voting tokens wait for MAS, or does voting go first as the first OIDC consumer? Recommend independent; the collision-and-import hazards in the SSO live read concern MAS only.

For **Ratatoskr (Void)**: the module owns the CSP, the in-module confirmation step, the callback page behaviour (section 6) and memory-only storage; this doc cannot enforce them from Bifrost's side.

## 13. What this deliberately does not do

No new identity: `sub` stays `UserAccount.Id`. No change to Heimdall, MAS, or the Mind key path. No weights or roles in tokens. No second vote implementation: the bearer surface is a thin projection of the existing services. No cookie sharing between chat and Bifrost, and no proxy on the chat origin pretending to hold one.
