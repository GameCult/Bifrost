# Concept Mill votes through Bifrost: map

Status: Imagination mapping pass, 2026-10-08, revised after the rulings `aetheria-release:ruling:concept-mill-votes-open` and `aetheria-release:ruling:concept-mill-votes-anonymous`. Committed 2026-10-10 as the target of the `concept-mill-votes` campaign; its invariants, rulings and cut specs are typed state in the eureka mind, and this file keeps the body facts and rationale. Where they differ, the mind is right.

Operator's words: "I want to track upvotes and downvotes on there, if possible." Then: "Yggdrasil is the obvious place. Doesn't Bifrost have discussions and voting?" Rulings: "anyone can vote, votes are public (we can filter by members if we wanted to)", then "anonymous, too, I mean anyone".

Short answer: Bifrost has two voting-like things, and neither fits the Mill as built. Governance topics take support and objection only as free-text comment stances. Those live in a command-line CultCache file the web app never reads, and they are not tied to accounts. Motions have real one-account-one-vote records, but they are tier-weighted decisions that close by deadline. Bifrost is the right home, but the Mill needs a small new record in it that admits anonymous voters, which nothing in Bifrost does today. It also needs an anti-stuffing defence, a public read endpoint and a vote page. The live deploy is off-main, and that has to be settled first.

## Body facts

Bifrost repo head: `160fac2` (2026-10-01). Paths are relative to `F:\Projects\Bifrost` unless named.

### What a topic is in code

1. Topics and comments exist only in a Node CLI, `tools/governance-threads.mjs`. Its store is a CultCache file, `.bifrost/governance-threads.cc` (`tools/governance-threads.mjs:18`), and its types are `bifrost.governance.topic` and `bifrost.governance.topic-comment` (`:48-111`). The commands are `open`, `comment`, `approve`, `promote`, `list`, `show` and `digest` (`:113-155`, `:582`).
2. A topic is an agent-dispatch work thread. It has `jurisdictionRepoName` (required), `jurisdictionAgentIdentity`, a status lifecycle `open → consensus_ready → approved → dispatched → closed` (`:52`), and `promote`, which renders the topic into an agent update request (`:324-405`).
3. Support and objection are comment stances, not votes. The valid set is `comment, proposal, support, objection, question, approval, summary, receipt` (`:53`). A comment has `authorKind` (default `"agent"`) and `authorId`, a free string from `--author` (`:232-241`). Nothing enforces one stance per author: each call writes a new `comment_<uuid>` (`:233`). Retraction is not possible. `authorId` is not tied to a Bifrost `UserAccount`.
4. The web app has no topic type, no topic page and no topic store. It knows topics only as `GovernanceActivityReceipt` rows in Postgres, with kinds `TopicOpened/Commented/Approved/Promoted` (`src/Bifrost.Web/Domain/BifrostModels.cs:235-240`, `:1050-1066`; `Features/Bridge/GovernanceActivityReceiptService.cs`).
5. No topic store exists anywhere I looked. `F:\Projects\Bifrost\.bifrost\` holds only `bridge-receipts.cc`. A read-only `find` on Yggdrasil under `/srv /home /var/lib` found no `governance-threads.cc`. That `find` ran under `sudo -n` with errors silenced, so the absence there is likely but not proven.

### How motions and votes differ

6. Motions and votes are EF Core entities in Postgres: `Motion` (`BifrostModels.cs:665-700`) and `Vote` (`:702-721`, with `VoteChoice For/Against/Abstain` at `:110-115`).
7. A vote is one per account per motion: there is a unique index on `(MotionId, UserAccountId)` (`Data/BifrostDbContext.cs:280-283`). Recasting a vote upserts it (`Features/Motions/MotionGovernanceService.cs:63-82`).
8. Votes are weighted. `Weight = actor.EffectiveVotingWeight` (`MotionGovernanceService.cs:72,80`). That weight is 0 unless the membership is `Active`, and otherwise `max(1, sum of current tier snapshot weights)` (`BifrostModels.cs:357-361`). Patron tier is the voting weight.
9. A motion has a category with an approval threshold, `ClosesAtUtc`, and a pass/fail resolution (`MotionGovernanceService.cs:95-131`, `MotionCategoryPolicy.cs`). It is a decision, not a standing rating.

### Who can act

10. Every member surface is behind `ActiveMember`: `AuthorizeFolder("/App", ActiveMemberRequirement.PolicyName)` (`src/Bifrost.Web/Program.cs:57`). The handler succeeds only when `Membership.Status == Active` (`Features/Membership/ActiveMemberAuthorizationHandler.cs`, `Features/Shared/CurrentBifrostActor.cs:17`). The motions page and its vote handler are at `Pages/App/Motions/Index.cshtml.cs:56`.
11. An account becomes `Active` in one of three ways:
    - bootstrap admin GitHub logins (`Features/Shared/ApplicationBootstrapper.cs:44`, `MembershipSynchronizationService.cs:133`);
    - admin approval on the Members page (`Pages/App/Members/Index.cshtml.cs:92-105`);
    - a Heimdall member-access claim, meaning the Discord cult-member role or an active Patreon tier (`Features/Heimdall/HeimdallAuthService.cs:158,186`).

    GitHub sign-in with an invitation lands in `PendingApproval`, and without one in `Authenticated` (`MembershipSynchronizationService.cs:61`).
12. Anyone can register natively, and doing so proves nothing. `POST /auth/bifrost/register` is anonymous (`Program.cs:286-316`). It takes an identity name and a display name, creates an `Authenticated` account and sets the cookie (`Features/Membership/BifrostIdentityService.cs:15-58`). It asks for no credential, and there is no native sign-in route. A native account is a single cookie: as a voter it is indistinguishable from anonymous, and it is lost when the cookie expires.
13. No public read path exists:
    - Anonymous routes are only health, readiness, auth, webhooks, the patron intake and the patronage checkout (`Program.cs:211-522`).
    - The Eve governance surface requires `ActiveMember` (`Program.cs:473-488`).
    - No CORS policy is configured anywhere in `src/Bifrost.Web` (grep for `Cors` returns nothing).

### Deployed versus repo

14. The live release is `/srv/bifrost/app/releases/20260723-daedcc4`, and `bifrost.service` is active. I checked both read-only over SSH alias `yggdrasil` on 2026-10-08. `https://bifrost.gamecult.org/healthz` and `/readyz` both return 200.
15. `daedcc4` ("Fix EF migration discovery metadata", 2026-07-15) is not on `main`. It exists only on `origin/codex/bifrost-migration-metadata`, which branched from `bd3a5a9`. Compared with `daedcc4`, `main` has `cde3a78`, which deletes 369 lines of HTTP bridge endpoints from `Program.cs`. `main` also lacks the migration-metadata attribute lines `daedcc4` added to seven migrations. Production therefore runs code that `main` has deleted, and `main` may still carry the EF migration-discovery bug that `daedcc4` fixed.
16. Deploys are manual. `gamecult-ops/scripts/deploy-bifrost-alpha.sh` creates a timestamped release directory, and Idunn does not manage Bifrost (`gamecult-ops/runbooks/bifrost-alpha-deploy.md:162-164`). The inventory entry (`gamecult-ops/inventory.md:941-952`) records no revision.

### The Mill page

17. The Mill is being built uncommitted in worktree `F:\Projects\AetheriaLore-wt-concept-mill`. It consists of `Aetheria/Game Design/Concept Mill.md` (397 lines, hand-authored HTML in Markdown), `Aetheria/media/concept-mill/` (41 files) and a `site/quartz/styles/custom.scss` change.
18. Each card is `<article class="mill-sheet ..." id="<slug>">`, and there are 19 today (`Concept Mill.md:12` for the first, `bison`). The slug is already independent of the display name: for example, `vasuki2` is shown as "Vasuki". The ship roll links to `#<slug>`.
19. The site has a pattern for client script. A Quartz component sets `afterDOMLoaded` (`site/quartz/components/AetheriaEpubReader.tsx:110`) and re-runs on the `nav` event (`site/quartz/components/scripts/aetheriaEpubReader.inline.ts:228`). Deploys go through `.github/workflows/deploy-quartz.yml` to GitHub Pages.

### Anonymous-voter substrate

20. Bifrost has no anonymous actor. Every write path resolves a `CurrentBifrostActor` with a `UserAccount` and throws without one (`MotionGovernanceService.cs:18,54`). Anonymous votes therefore need a voter identity that is not a `UserAccount`.
21. Bifrost sees no client IP. nginx sends `X-Real-IP` and `X-Forwarded-For` (`gamecult-ops/nginx/bifrost.gamecult.org.conf:15-16`), but `Program.cs` never calls `UseForwardedHeaders`, so `RemoteIpAddress` is the loopback proxy for every request. Until that is fixed, any per-IP limit puts every visitor in one bucket.
22. Data Protection keys can be persisted (`Program.cs:113-117`, `DataProtection:KeysPath`). A device token signed with them would stay valid across restarts and releases, provided the live env sets the path. Cut 0 checks this.

## Rulings in force

- **Who may vote:** anyone, including anonymous visitors. There is no membership gate.
- **Votes are public:** up and down counts are shown, so downvotes are visible.
- **Members are a filter, not a separate system.** Each vote records the voter's class at cast time (anonymous, account, or Active member). Member-only views are a filter over the same rows.

Derived from these rulings, not asked separately:

- **Counts are raw: one voter, one mark.** Patron weight cannot apply to anonymous voters, and the member filter replaces it as the "members' opinion" view.
- **Concept art stays out of governance.** Mill votes are their own record, not governance topics or motions. Topics are agent-dispatch threads, and motions are tier-weighted, deadline-closing decisions (facts 2, 8, 9). Neither can admit an anonymous voter (fact 20).
- **Substrate is Postgres beside `Vote`,** to keep the optional foreign key to `UserAccount` in one transaction. "Bifrost web state is off CultCache" remains the standing doctrine debt it already is. This feature does not pay it down.

Interpretation for Self to confirm, not a fork: "votes are public" is read as public counts, split by voter class. The export carries no per-voter identities. Anonymous voters have none to show, and publishing account names was not asked for.

## Authority map

- **Concept identity.** Owner: the Mill page in AetheriaLore. The card `id` slug is the key (fact 18). Bifrost stores keys and never invents one. Renaming a ship changes its title, never its key.
- **Concept subjects.** Owner: a Bifrost `ConceptSubject` record: `(collection, key)` unique, plus `title`, `registeredAt` and `retiredAt`. Writer: one idempotent admin registration command, fed from the Mill's card list and run by the render-round agent. The vote path cannot create subjects, because otherwise anonymous visitors could mint keys.
- **Voter identity.** Owner: a Bifrost `VoterKey` resolver, the one place that answers "who is casting this?":
  - signed in: `account:<UserAccountId>`;
  - otherwise: `device:<id>`, where the id comes from a signed, HttpOnly, first-party device-token cookie that Bifrost issues on the vote page.

  The `VoterKey` resolver is not an owner of membership status. It reads `CurrentBifrostActor.IsActiveMember` at cast time.
- **Votes.** Owner: a `ConceptAppraisal` record with these fields:
  - `subjectId` and `voterKey`, with `(subjectId, voterKey)` unique;
  - `stance` (`Up` or `Down`) and `castAt`;
  - `voterClass` (`Anonymous`, `Account` or `Member`), stamped at cast;
  - `userAccountId`, nullable;
  - `networkTag`, an HMAC of the client's /24 (IPv4) or /64 (IPv6) prefix, stored for audit and purge.

  The one writer is `ConceptAppraisalService` (Cast, Change, Retract, and a Purge for admins). Rate limiting happens before the service, and uniqueness is enforced inside it and by the index.
- **Tallies.** Derived only. Per subject, per stance, per voter class, they are computed from rows and never stored as counters. The "all" and "members" views are filters over the same rows.
- **Public tally export.** This is the JSON xenos boundary. It is one anonymous `GET /public/concepts/{collection}/tallies` returning `{ key: { up, down, members: { up, down } } }` for every live subject:
  - no voter keys, account ids or network tags;
  - CORS allows exactly `https://aetheria.gamecult.org`, `GET` only, without credentials;
  - `Cache-Control` is about 60 s.

  The export owns nothing.
- **The Mill page.** Read only. One fetch fills each card's counts, and a "Vote on Bifrost" link goes to `https://bifrost.gamecult.org/mill/aetheria-concept-mill/<slug>`. Voting happens on Bifrost's own origin, which keeps the device cookie first-party. That means no `SameSite=None`, no cross-site POST and no exposure to browser third-party-cookie blocking.
- **Demotions:**
  - Governance topic support and objection are not vote owners.
  - `Vote.Weight` and patron tiers stay governance-only.
  - The static page owns no count.
  - The client IP is not an identity: it is a rate-limit bucket and an audit tag, never a vote key.

## Anti-stuffing defence (implementation duty)

Prior art on lightweight anonymous voting, from memory and not freshly searched:

- Strawpoll-style polls offer duplicate checks by IP, by browser cookie, or by sign-in. IP-only checks wrongly block shared networks, and cookie-only checks are reset by a private window.
- Proof-of-work challenges (Hashcash; today ALTCHA, mCaptcha, Friendly Captcha) make each vote cost CPU without any third party. They slow bulk scripts and barely touch a determined attacker with hardware.
- Third-party challenges (Cloudflare Turnstile, reCAPTCHA) are strong. They also send visitor data to an outside service and add an external dependency to the vote path.
- Large sites rely on after-the-fact detection more than on gates: they look for clusters by network, timing and new devices, then discount or purge.

Recommendation: a layered defence that lives entirely inside Bifrost, with proof-of-work held back as an escalation.

1. **Device token: one vote per device per concept.** On the first vote-page visit, Bifrost mints a random device id, signs it with Data Protection, and sets it as `bifrost_voter`: HttpOnly, Secure, SameSite=Lax, valid for one year. The vote key is `device:<id>`. Clearing cookies resets it, and that is accepted and bounded by layer 2.
2. **Network rate limits.** Configure `UseForwardedHeaders`, trusting only loopback (fact 21 makes this a prerequisite). Then put the in-box ASP.NET Core rate limiter (`Microsoft.AspNetCore.RateLimiting`) on two paths, partitioned by network prefix:
   - token minting: a small number of new devices per prefix per hour;
   - vote casts: a modest number per prefix per minute.

   Signed-in accounts are partitioned by account, not by network. The limits are configuration values, and the first values are Hands' to propose and Soul's to probe.
3. **Audit and purge.** Every anonymous row carries `networkTag` and `castAt`. An admin view lists subjects whose recent votes cluster on one tag. A Purge command removes rows by tag and time window, leaving an audit event. Public counts can then be corrected after the fact, without a gate that blocks honest shared networks.
4. **Escalation, not built now:** a self-hosted proof-of-work challenge on token minting, using an ALTCHA-style library served from Bifrost. It is switched on by configuration only if layer 3 shows stuffing. It needs no third party, so votes stay Bifrost-owned. Turnstile and reCAPTCHA are refused, because they add an outside owner to the vote path.

Where it lives:

- Layers 1 to 3 are Bifrost web code: the `VoterKey` resolver, the rate-limiter policies in `Program.cs`, and the service's Purge.
- nginx gets nothing new, and `limit_req` is not used. Edge limits would be a second, unobservable owner of the same budget.
- The static site gets nothing at all.

## Cuts, in order

Each cut is one Hands brief. Bifrost .NET builds and tests run on Yggdrasil per the workstation load budget.

**Cut 0: Reconcile the Bifrost deploy with `main`.** Repos: Bifrost and gamecult-ops.

- Land `daedcc4`'s migration-metadata fix on `main`.
- Confirm nothing live still calls the bridge endpoints that `cde3a78` deleted (fact 15).
- Confirm that the live env sets `DataProtection__KeysPath` (fact 22). This is read-only; if it is unset, Cut 6 adds it.
- Deploy `main` per the runbook and record the revision in `inventory.md`.
- Verify:
  - migrations list completely against a scratch database;
  - `/healthz` returns 200;
  - the admin can load the motions page;
  - the inventory names the revision.

**Cut 1: Forwarded headers.** Repo: Bifrost.

- Call `UseForwardedHeaders` with `X-Forwarded-For` and `X-Forwarded-Proto`, trusting `127.0.0.1` and `::1` only.
- Tests:
  - behind a loopback proxy, `RemoteIpAddress` is the forwarded client;
  - a forwarded header from a non-loopback peer is ignored, which is the negative check against spoofing.

**Cut 2: Subjects, voter keys and appraisals.** Repo: Bifrost.

- Add `ConceptSubject`, `ConceptAppraisal`, a migration, the `VoterKey` resolver with the device token, and `ConceptAppraisalService` (Cast, Change, Retract, Purge, Tally).
- Tests:
  - a second cast by the same voter key changes the stance and adds no row;
  - Retract removes the row;
  - a vote on an unknown or retired key is refused;
  - `voterClass` and `userAccountId` are stamped correctly for anonymous, account and Active-member actors;
  - a tampered or unsigned device cookie is rejected and a new one is minted;
  - Purge removes only rows in the tag and window;
  - the tally by class equals the row counts.
- Negative check: nothing outside the service writes `ConceptAppraisal`.

**Cut 3: Subject registration.** Repo: Bifrost.

- Add an idempotent admin command that upserts `(collection, key, title)` and can retire a key. Use whichever existing admin idiom Hands finds, `/App` page handler or `tools/` CLI.
- Tests: a rerun is a no-op; renaming a title keeps the votes; a retired key keeps its rows but leaves the export.
- Seed it with the 19 slugs.

**Cut 4: Public export and rate limits.** Repo: Bifrost.

- Add the anonymous tally `GET` with the one-origin CORS policy.
- Add the two rate-limiter policies, for minting and for casting.
- Tests:
  - an anonymous request gets 200;
  - CORS headers are present only for `https://aetheria.gamecult.org`;
  - the payload contains no voter keys, account ids or tags;
  - a burst from one prefix gets 429 past the limit while another prefix is unaffected;
  - signed-in accounts are limited per account.

**Cut 5: The vote page.** Repo: Bifrost.

- Add `/mill/{collection}/{key}`, outside `/App`. It shows the title, the counts (all and members), a back link to the card, and Up, Down and Retract buttons for everyone, as HTMX posts with antiforgery. A first visit mints the device token.
- Tests:
  - an anonymous visitor can cast, change and retract;
  - a signed-in visitor's vote is keyed to the account;
  - a cast without the antiforgery token is refused.

**Cut 6: Deploy.** Repo: gamecult-ops.

- Deploy per the runbook. Set `DataProtection__KeysPath` if Cut 0 found it unset, and update the inventory.
- Verify from outside:
  - the export, requested with `Origin: https://aetheria.gamecult.org`, returns CORS headers and 19 keys;
  - an anonymous vote from a clean browser shows in the export after the cache expires;
  - a scripted burst gets 429.

**Cut 7: The Mill reads the counts.** Repo: AetheriaLore.

- On each card, add `data-concept-key="<slug>"`, a count slot and the vote link.
- Add one Quartz component script (`afterDOMLoaded`, re-run on `nav`, fact 19) that fetches the export once and fills the slots. On failure the slots stay hidden and the links still work.
- Add "register new slugs with the Cut 3 command" to the render-round routine.
- Verify:
  - the Quartz build passes;
  - the counts render in a browser;
  - with JavaScript disabled the links still work;
  - with Bifrost blocked there are no layout holes.

This cut lands with or after the Mill page's own commit.

## Open questions

None remain from the operator. Self should confirm one interpretation, that the export carries counts by class and no voter names. If Hands finds that the live env lacks persisted Data Protection keys, that is a deploy fix inside Cut 6, not a fork.

