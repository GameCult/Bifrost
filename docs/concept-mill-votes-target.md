# Concept Mill votes through Bifrost: map

Status: Imagination mapping pass 2026-10-08, revised after the rulings `aetheria-release:ruling:concept-mill-votes-open` and `aetheria-release:ruling:concept-mill-votes-anonymous`. Committed 2026-10-10 as the target of the `concept-mill-votes` campaign. Body facts re-verified and cut specs admitted 2026-10-10 by `imagination-mill-votes` against Bifrost `bf83a93`. Its invariants, rulings, questions and cut specs are typed state in the eureka mind; this file keeps the body facts and rationale. Where they differ, the mind is right.

Operator's words: "I want to track upvotes and downvotes on there, if possible." Then: "Yggdrasil is the obvious place. Doesn't Bifrost have discussions and voting?" Rulings: "anyone can vote, votes are public (we can filter by members if we wanted to)", then "anonymous, too, I mean anyone".

Short answer: Bifrost has two voting-like things, and neither fits the Mill. Governance topics take support and objection only as free-text comment stances, in a command-line CultCache file the web app never reads, not tied to accounts. Motions have real one-account-one-vote records, but they are tier-weighted decisions that close by deadline. Bifrost is the right home, but the Mill needs a small new record in it that admits anonymous voters, which nothing in Bifrost does today, plus an anti-stuffing defence, a public read endpoint and a vote page. The live deploy is off-main, and that is settled first.

## Body facts

Bifrost head `bf83a93` (2026-10-10; `20d5efa` plus the prior-art note, docs only). Paths are relative to `F:\Projects\Bifrost` unless named. Facts 1-13 were taken at `160fac2` on 2026-10-08; the source files they cite are unchanged at `bf83a93` (the two commits since touch only `docs/`). Facts 14 onward were re-probed on 2026-10-10 by `imagination-mill-votes`; scripts are in that agent's scratch directory (`ygg-probe*.sh`).

### What a topic is in code

1. Topics and comments exist only in a Node CLI, `tools/governance-threads.mjs`. Its store is a CultCache file, `.bifrost/governance-threads.cc` (`tools/governance-threads.mjs:18`), and its types are `bifrost.governance.topic` and `bifrost.governance.topic-comment` (`:48-111`). The commands are `open`, `comment`, `approve`, `promote`, `list`, `show` and `digest` (`:113-155`, `:582`).
2. A topic is an agent-dispatch work thread. It has `jurisdictionRepoName` (required), `jurisdictionAgentIdentity`, a status lifecycle `open → consensus_ready → approved → dispatched → closed` (`:52`), and `promote`, which renders the topic into an agent update request (`:324-405`).
3. Support and objection are comment stances, not votes. The valid set is `comment, proposal, support, objection, question, approval, summary, receipt` (`:53`). A comment has `authorKind` (default `"agent"`) and `authorId`, a free string from `--author` (`:232-241`). Nothing enforces one stance per author: each call writes a new `comment_<uuid>` (`:233`). Retraction is not possible. `authorId` is not tied to a Bifrost `UserAccount`.
4. The web app has no topic type, page or store. It knows topics only as `GovernanceActivityReceipt` rows in Postgres, with kinds `TopicOpened/Commented/Approved/Promoted` (`src/Bifrost.Web/Domain/BifrostModels.cs:235-240`, `:1050-1066`; `Features/Bridge/GovernanceActivityReceiptService.cs`).
5. No topic store exists anywhere looked. `.bifrost\` holds only `bridge-receipts.cc`. A read-only `find` on Yggdrasil under `/srv /home /var/lib` found no `governance-threads.cc` (ran under `sudo -n` with errors silenced, so likely but not proven).

### How motions and votes differ

6. Motions and votes are EF Core entities in Postgres: `Motion` (`BifrostModels.cs:665-700`) and `Vote` (`:702-721`, with `VoteChoice For/Against/Abstain` at `:110-115`).
7. A vote is one per account per motion: unique index on `(MotionId, UserAccountId)` (`Data/BifrostDbContext.cs:280-283`). Recasting upserts it (`Features/Motions/MotionGovernanceService.cs:63-82`).
8. Votes are weighted: `Weight = actor.EffectiveVotingWeight` (`MotionGovernanceService.cs:72,80`), 0 unless the membership is `Active`, otherwise `max(1, sum of current tier snapshot weights)` (`BifrostModels.cs:357-361`).
9. A motion has a category with an approval threshold, `ClosesAtUtc`, and a pass/fail resolution (`MotionGovernanceService.cs:95-131`, `MotionCategoryPolicy.cs`). It is a decision, not a standing rating.

### Who can act

10. Every member surface is behind `ActiveMember`: `AuthorizeFolder("/App", ActiveMemberRequirement.PolicyName)` (`src/Bifrost.Web/Program.cs:57`). The handler succeeds only when `Membership.Status == Active` (`Features/Membership/ActiveMemberAuthorizationHandler.cs`, `Features/Shared/CurrentBifrostActor.cs:17`). Admin-only page handlers additionally check `Actor.CanManageMembership` and return `Forbid()` (`Pages/App/Members/Index.cshtml.cs:37-39,95-97`).
11. An account becomes `Active` by bootstrap admin GitHub login (`Features/Shared/ApplicationBootstrapper.cs:44`, `MembershipSynchronizationService.cs:133`), admin approval (`Pages/App/Members/Index.cshtml.cs:92-105`), or a Heimdall member-access claim (`Features/Heimdall/HeimdallAuthService.cs:158,186`). GitHub sign-in with an invitation lands in `PendingApproval`, without one in `Authenticated` (`MembershipSynchronizationService.cs:61`).
12. Anyone can register natively, and doing so proves nothing: `POST /auth/bifrost/register` is anonymous (`Program.cs:286-316`), takes an identity name and display name, creates an `Authenticated` account and sets the cookie (`Features/Membership/BifrostIdentityService.cs:15-58`). No credential, no native sign-in route. As a voter a native account is indistinguishable from anonymous.
13. No public read path exists: anonymous routes are only health, readiness, auth, webhooks, patron intake and patronage checkout (`Program.cs:211-522`); the Eve governance surface requires `ActiveMember` (`Program.cs:473-488`); no CORS and no rate limiter are configured (`git grep -i -E 'AddCors|UseCors|RateLimit|ForwardedHeaders' bf83a93 -- 'src/*.cs'` returns nothing).

### Deployed versus repo

14. Live release `/srv/bifrost/app/releases/20260723-daedcc4` (`readlink -f /srv/bifrost/app/current`, 2026-10-10 02:58Z), `bifrost.service` active since 2026-08-21 10:51 UTC, `ExecStart=/usr/bin/dotnet /srv/bifrost/app/current/Bifrost.Web.dll`, `EnvironmentFiles=/srv/bifrost/env/bifrost.env`, `ASPNETCORE_ENVIRONMENT=Production`, listening on `127.0.0.1:5080`. Earlier releases are bare UTC timestamps (`20260610073418` … `20260618144854`); the `-daedcc4` suffix was added by hand, the deploy script does not write it (fact 16).
15. `daedcc4` ("Fix EF migration discovery metadata", 2026-07-15) is not on main: `git branch -a --contains daedcc4` lists only `origin/codex/bifrost-migration-metadata`, which is `bd3a5a9` (an ancestor of main) plus `daedcc4` alone. `git merge-tree --write-tree bf83a93 origin/codex/bifrost-migration-metadata` is clean. Main has `cde3a78`, which deleted the HTTP bridge routes `/bridge/actions/*`, `/dispatch/runs/*`, `/transport/receipts`, `/governance/receipts` and `POST /eve/governance/commands` (369 lines of `Program.cs`); main's `PlatformEndpointsTests.Removed_http_bridge_routes_are_not_mapped` pins their absence.
16. Deploys are manual: `gamecult-ops/scripts/deploy-bifrost-alpha.sh` (33 lines, gamecult-ops `1e95674`; the deploy files are unchanged since `44889b1`) unpacks `/srv/bifrost/bifrost-publish.tar` into `releases/$(date -u +%Y%m%d%H%M%S)`, repoints `current`, restarts. It takes no revision and records none. `runbooks/bifrost-alpha-deploy.md` step 3 publishes with `dotnet publish` from the operator's checkout `F:\Projects\Bifrost` (`:101-115`), step 2 lists the env keys (`:63-89`). Idunn does not manage Bifrost (`:162-164`). The inventory's "Bifrost alpha live state" block (`gamecult-ops/inventory.md:923-933`) records no revision.
17. No caller of the routes `cde3a78` deleted, over the whole retained nginx access log (2026-09-26 00:46 to 2026-10-10 02:58, 473,581 lines, all vhosts): zero requests to `/bridge/actions`, `/dispatch/runs`, `/transport/receipts`, `/governance/receipts` or `/eve/governance/commands`. The only `/governance/*` hits were ten scanner GETs on 2026-10-07 (`/governance/providers`, `/governance/budgets`, …, all 400). No systemd unit, timer or crontab on Yggdrasil names the bridge tools or `127.0.0.1:5080` except `bifrost.service` itself, and no non-nginx connection to `:5080` was established at probe time. The bifrost journal holds 1 line in 30 days, so the app's own logs cannot witness traffic.

### Data Protection on the live host

18. The live env file sets no `DataProtection__KeysPath` (key names only: `AllowedHosts`, `Bootstrap__*`, `ConnectionStrings__Bifrost`, `GitHubApp__EnableWebhookSync`, `GitHubOAuth__*`, `Heimdall__*`, `Host__*`, `Stripe__*`). So `Program.cs:113-119` never calls `PersistKeysToFileSystem`, and ASP.NET Core's default applies: the service runs as `bifrost` with `HOME=/srv/bifrost`, and the key ring is `/srv/bifrost/.aspnet/DataProtection-Keys` with two keys (created 2026-04-22 and 2026-07-22, the 90-day roll; prior-art note `docs/research/anonymous-vote-stuffing-prior-art.md:65-69`). A third, unused directory `/srv/bifrost/data-protection-keys` holds one key from 2026-07-15. Keys therefore persist across restarts today by accident of `HOME`, not by configuration.
19. The service's working directory resolves to the release directory: `readlink /proc/<MainPID>/cwd` = `/srv/bifrost/app/releases/20260723-daedcc4`. Data Protection's default application discriminator is the content-root path (prior-art note `:67`), so every deploy into a new release directory isolates payloads protected by the previous release: auth cookies, antiforgery tokens and any future device token stop decrypting after each deploy. Mechanism from source docs plus this probe; not observed as logged-out users. `SetApplicationName` is not called anywhere (`git grep SetApplicationName bf83a93` is empty).

### Proxy and client address

20. nginx sends `X-Real-IP`, `X-Forwarded-For` (`$proxy_add_x_forwarded_for`), `X-Forwarded-Proto` and `X-Forwarded-Host` (live `/etc/nginx/sites-enabled/bifrost*:12-19`), and proxies to `127.0.0.1:5080`. `Program.cs` never calls `UseForwardedHeaders` (fact 13 grep), so `RemoteIpAddress` is `127.0.0.1` for every request and any per-client limit puts every visitor in one bucket.

### Code and test substrate

21. Main lacks EF migration discovery metadata on seven migrations: `20260622142000_AddBridgeActionIdentityProvenance`, `20260622202000_AddBridgeActionTargetSurfaceName` and `20260705190000_AddPatronSupportProjectAttribution` have neither `[DbContext]` nor `[Migration]`; `20260608210000_AddHeimdallUserIdentity`, `20260609143000_AddPatronSupportDerivation`, `20260609160000_AddExternalPatronProviderIntake` and `20260622165000_AddBifrostNativeIdentity` have `[Migration]` only; none of the seven has a `.Designer.cs`. EF discovers a migration only with both attributes, so `MigrateAsync` from main would skip them on a fresh database. `daedcc4` adds exactly those attributes (17 lines). The other five migrations have Designer files and are discovered. Migrations run at startup when `Bootstrap:ApplyMigrationsOnStartup` is true (`Features/Shared/ApplicationBootstrapper.cs:17-21`); the live env sets that key.
22. Tests use EF InMemory (`tests/Bifrost.Web.Tests/Support/TestWebApplicationFactory.cs:52-58`), which enforces no unique index; a uniqueness rule must be proven in service code and in the model/migration metadata, not by an InMemory insert failing. `TestAuthHandler` offers two actors only: the bootstrap admin `test-admin`, or anonymous via header `X-Test-Anonymous` (`Support/TestAuthHandler.cs:17-37`). `CurrentBifrostActor` is a public record with a public constructor (`Features/Shared/CurrentBifrostActor.cs:5`), so service tests can build account and member actors directly. `Program` is `public partial` (`Program.cs:582`).
23. Startup configuration rules live in one owner, `Features/Shared/StartupConfigurationValidator.cs`, which skips Development and Testing and throws on a missing production key (`:15-74`).
24. The shared layout loads HTMX from `https://unpkg.com/htmx.org@1.9.12` (`Pages/Shared/_Layout.cshtml:65`); no page posts with `hx-post` today. Admin and vote handlers use plain Razor page handlers with `RedirectToPage()` (`Pages/App/Motions/Index.cshtml.cs:56-61`).
25. Verification on Yggdrasil: `ygg-verify.sh`'s `dotnet` alias is `mcr.microsoft.com/dotnet/sdk:10.0`, which has no .NET 8 runtime for this `net8.0` test project; passing the image name `mcr.microsoft.com/dotnet/sdk:8.0` works (any other value is used as an image name). At `bf83a93`, `dotnet test tests/Bifrost.Web.Tests/Bifrost.Web.Tests.csproj -c Release --filter "FullyQualifiedName!~BridgeCliTests&FullyQualifiedName!~ProviderAdvertisementTests"` passes 29 of 29 (scratch log `bifrost-base-test-2.log`; that run's filter excluded only `BridgeCliTests`, and the two `ProviderAdvertisementTests` failed for want of Node). The other 37 tests shell out to Node: with no Node they fail to start the process; with Debian's Node 18 they fail on newer Node APIs (`bifrost-base-test-3.log`, 30 pass, 36 fail). Starfire has Node 24 and SDK 10.0.202 only. None of the cuts touch the Node tools.

### The Mill page

26. The Mill is committed and merged: AetheriaLore `origin/main` `6980087` ("Merge concept-mill-spa"). `Aetheria/Game Design/Concept Mill.md` is now 10 lines of front matter and prose; the viewer is the Quartz component `site/quartz/components/AetheriaConceptMill.tsx` with `scripts/aetheriaConceptMill.inline.ts` (417 lines) and `styles/aetheriaConceptMill.scss`. Its data source is `scripts/concept-mill/ships.yaml` (581 lines); `node scripts/concept-mill/build.mjs` writes the manifest `site/quartz/components/data/conceptMill.json` (embedded in the page) and render derivatives, skipping derivatives that already exist unless `--force` (`build.mjs:51-60`), and loads `js-yaml` and `sharp` from the sibling `F:\Projects\GameCult-Quartz` checkout (`:22-25`).
27. Concept identity is the ship `id` in `ships.yaml`; there are 27 ships today (`bison vasuki squatter raft gentle-giant shellback lamp-shy zera nakovalnya oboz shchit glushitel key-light shepherd rake dine-and-dash party-favors finders-keepers tattletale ghost-fare odd-job one-pass bulldog breakup-song 50-f live-wire homestead`). The build refuses duplicate ship ids (`build.mjs:94-96`). Each ship has one or more render variants with their own ids; the URL hash is `#ship` or `#ship/variant` (`aetheriaConceptMill.inline.ts:3,178-192`). The 2026-10-08 "19 card slugs" are gone.
28. The vote slot already exists and is switched off. `ships.yaml:22-27` holds `votes: { enabled: false, endpoint: null, bifrostUrl: null }`, copied into the manifest (`conceptMill.json:16-19`). When enabled, the script fetches `endpoint` once with `credentials: "omit"`, reads `tallies[ship.id]` as `{ up, down, members?: { up, down } }`, shows `▲ up ▼ down` (members not shown), and links `${bifrostUrl}/${ship.id}` as "Vote on Bifrost" (`inline.ts:163-174,284-287`). On fetch failure the slot shows `—`. Tallies are per ship, not per render.
29. The Aetheria site's origin is `https://aetheria.gamecult.org` (`site/quartz.config.ts:66` `baseUrl: "aetheria.gamecult.org"`).

## Rulings in force

The rulings are typed state: `aetheria-release:ruling:concept-mill-votes-open`, `aetheria-release:ruling:concept-mill-votes-anonymous`, and `concept-mill-votes:ruling:public-means-counts` (Self's defaulted reading that "public" means counts split by voter class, no per-voter identity).

Derived from them, not asked separately:

- **Counts are raw: one voter, one mark.** Patron weight cannot apply to anonymous voters, and the member filter replaces it as the "members' opinion" view.
- **Concept art stays out of governance.** Mill votes are their own record, not topics or motions (facts 2, 8, 9). Neither can admit an anonymous voter.
- **Substrate is Postgres beside `Vote`,** to keep the optional foreign key to `UserAccount` in one transaction. "Bifrost web state is off CultCache" remains standing doctrine debt; this feature does not pay it down.

## Authority map

- **Concept identity.** Owner: the Mill's data in AetheriaLore, `scripts/concept-mill/ships.yaml`, ship `id` (fact 27). Bifrost stores keys and never invents one. Renaming a ship changes its name, never its key. A render variant is not a subject.
- **Concept subjects.** Owner: Bifrost `ConceptSubject` (`(Collection, Key)` unique, plus `Title`, `RegisteredAtUtc`, `RetiredAtUtc`). One writer, `ConceptSubjectRegistry`, reached from one admin page that takes the Mill's generated manifest (the owner's artifact) and applies an idempotent plan: add, rename, retire-on-explicit-choice, revive. The vote path cannot create subjects.
- **Voter identity.** Owner: `VoterIdentity.For(actor, deviceId)`, a pure function, plus a `VoterKeyResolver` that only reads or mints the device cookie and calls it:
  - signed in: key `account:<UserAccountId>`, class `Member` if `CurrentBifrostActor.IsActiveMember` at cast time, else `Account`;
  - otherwise: key `device:<id>`, class `Anonymous`, where the id is a random GUID protected by Data Protection in an HttpOnly, Secure, SameSite=Lax, first-party cookie on `bifrost.gamecult.org`.
  The resolver does not own membership; it reads it. When the cookie is minted is the open question `concept-mill-votes:question:device-cookie-consent`.
- **Votes.** Owner: `ConceptAppraisal` rows: `SubjectId`, `VoterKey` (`(SubjectId, VoterKey)` unique), `Stance` (`Up`/`Down`), `VoterClass` stamped at cast, `UserAccountId` nullable, `NetworkTag` and `CastAtUtc`. One writer, `ConceptAppraisalService` (Cast, Retract, Purge, plus read-only Tally and subject view). Rate limiting stands in front of it; uniqueness is enforced inside it and by the unique index, with a unique-violation on insert retried as a stance change.
- **Network prefix.** Owner: one function, `NetworkPrefix`, mapping a client address to its configured IPv4/IPv6 prefix (IPv4-mapped IPv6 folded to IPv4). The audit `NetworkTag` (HMAC-SHA256 of the prefix under a configured secret) and the rate-limit partition both read it; no second prefix computation exists.
- **Tallies.** Derived only: per live subject, per stance, all and members, computed from rows and never stored as counters.
- **Public tally export.** The JSON xenos boundary: anonymous `GET /public/concepts/{collection}/tallies` returning `{ key: { up, down, members: { up, down } } }` for every live subject; no voter keys, account ids or tags; CORS for the configured origins only (`https://aetheria.gamecult.org`), `GET`, no credentials; `Cache-Control: public, max-age=60`. It owns nothing.
- **The Mill page.** Read only. Its existing vote slot (fact 28) fills from the export and links to `https://bifrost.gamecult.org/mill/aetheria-concept-mill/<ship id>`. Voting happens on Bifrost's own origin, which keeps the device cookie first-party.
- **Client address.** `UseForwardedHeaders` trusting loopback only makes `RemoteIpAddress` the real client. It is a rate-limit bucket and an audit tag, never a vote key.
- **Key ring.** Data Protection keys live at a configured path with a fixed application name, so tokens survive restarts and deploys; production refuses to start without the path.
- **Demotions:**
  - Governance topic support and objection are not vote owners.
  - `Vote.Weight` and patron tiers stay governance-only.
  - The static page owns no count.
  - The client IP is not an identity.
  - The content-root path is no longer the Data Protection discriminator; the fixed application name is.
  - The `HOME`-default key ring is no longer an owner; the configured `DataProtection:KeysPath` is (pointed at the same directory, so existing keys are retained).

## Anti-stuffing defence (implementation duty)

Prior art: `docs/research/anonymous-vote-stuffing-prior-art.md` (Eyes, 2026-10-10, Bifrost `bf83a93`). It replaces the from-memory list this section used to carry. Its section "Where the current design's assumptions are contradicted" (`:92-98`) changed the design as follows:

1. **Device token: one vote per device per concept.** Kept. Its one-year life depends on persisted, retained keys and a stable discriminator (note `:65-69,95`; facts 18-19), so Cut 1 makes both configuration. Whether the cookie needs a notice or consent is the operator's question `device-cookie-consent` (note `:74-77,97`); the recommended option mints it only when a visitor casts a vote, with a one-line notice.
2. **Network rate limits.** Kept, in-box ASP.NET Core rate limiter, partitioned by `NetworkPrefix` for anonymous voters and by account when signed in. The prefix lengths are configuration, not design: the note found no evidence for a /24 IPv4 bucket, and per-/64 IPv6 buckets are spread across by delegated prefixes and privacy rotation while larger buckets catch mobile users (`:57-61,96`). Hands proposes defaults and limits; Soul probes them. Partition keys come only from `RemoteIpAddress` after loopback-only forwarded headers or from an account id, never from raw client input (note `:45`).
3. **Audit and purge.** Kept. The note confirms large sites rely on after-the-fact detection and also documents their false positives and unpublished thresholds (`:14-16,98`); Purge leaves an audit event so a correction is itself reviewable.
4. **Escalation, not built now:** proof of work. ALTCHA has no official .NET server, only community packages that need a production replay store (`:27,94`), and PoW is weak against determined attackers (`:34-37`). Not specified until audit shows stuffing. Turnstile and reCAPTCHA stay refused (third party on the vote path).

Vote fuzzing and voter-history weighting (note `:98`) are not adopted: raw public counts are the ruling.

Where it lives: Bifrost web code only. nginx gets nothing new (`limit_req` would be a second, unobservable owner of the same budget). The static site gets nothing.

## Cut order and rationale

The cut specs are typed state (`concept-mill-votes:cut_spec:cut-<label>.r1`); this section keeps only why the order is what it is.

- **0a, 0b: subtraction first.** Production runs `daedcc4`, which main does not contain. Merging that branch (not cherry-picking) makes the live revision an ancestor of main, so the branch can be retired and the live release is on main without redeploying anything new; then 0b deploys main through a revision-recording deploy and names the revision in the inventory. Behaviour cuts come after, so their deploy (Cut 6) starts from a known base.
- **1: host trust plumbing.** Forwarded headers and the key ring are prerequisites of the rate limits (fact 20) and the device token (facts 18-19). Its runbook consequence (the env key) is written by Cut 6, which is the only cut that deploys behaviour.
- **2: the record.** Subjects, appraisals, the pure voter-identity function, the network prefix and tag, and the one writer. No HTTP surface, so it is testable without the page.
- **3: registration**, the only subject writer, before anything can be voted on.
- **4: the public export and CORS.** Rate limits moved out of this cut (the 2026-10-08 map had them here) to land with the path they guard.
- **5: the vote page and voter key resolver**, blocked on `device-cookie-consent` for when the cookie is minted and what the page says.
- **5b: rate limits on the cast path**, after 5 because the policies attach to its handlers; the end-to-end limiter test also proves Cut 1's forwarded-header wiring.
- **6: deploy** with the new env keys, registration seeded from the Mill manifest.
- **7: the Mill turns its vote slot on**, after the endpoint is live.

## Open questions

`concept-mill-votes:question:device-cookie-consent`, raised in Cut 5. Nothing else is open with the operator.
