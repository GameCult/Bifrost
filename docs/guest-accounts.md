# Guest accounts

Status: **design, nothing implemented.** Written for `eureka-body:follow_up:bifrost-guest-accounts` under
`eureka-body:ruling:guest-arrival-bifrost` (Meta: guests need no Matrix account of their own; they get a GameCult
account through Bifrost with a guest role, and move to member, user or patron on the same account seamlessly),
building on `eureka-body:ruling:bifrost-owns-accounts-invite-only`. I could not `view` either ruling from the
Heimdall mind, so their text here is the follow-up brief's wording. Eureka's review (PR comment, 2026-11-07) checked
it against the rulings and found guest-arrival matches; the conflicts it found are folded in below. First guest:
sletz (Faust developer). Aqua decides on `#aquasynth` access, so the first grant list is hers to name.

Read at Bifrost `bf8ff62` (`src/Bifrost.Web/Domain/BifrostModels.cs`, `Features/Membership`, `Features/Heimdall`,
`Features/Oidc`, `Features/Motions`, `Features/ConceptVotes`, `Pages/App/Members`) and Heimdall `575be9c`
(`src/app-profiles.ts`, `docs/app-profiles/bifrost.md`).

## The shape in one paragraph

A guest is a `UserAccount` like any other: same `Id` (the OIDC `sub`), same `NormalizedBifrostIdentity` (the Matrix
localpart, minted once). What is new is one field, `Membership.Role`, with four values: `guest`, `user`, `patron`,
`member`. Role is the single place that says what an account may do. A role change rewrites that field and writes a
receipt; it never creates a second account, never re-mints the identity, so chat history, grants and the Matrix
user stay attached.

## What exists today

- `UserAccount.Id` is the OIDC subject; `NormalizedBifrostIdentity` is `preferred_username` and, through MAS, the
  Matrix localpart (`docs/oidc-issuer.md`). `HeimdallAccountId` is only an authenticator handle.
- `Membership.Status` is `Authenticated | PendingApproval | Active | Suspended`. `CurrentBifrostActor.IsActiveMember`
  (`Status == Active`) is the one test behind `/App`, bridge transport (`BridgeActionService` l.492), motion votes
  (`EffectiveVotingWeight`, `MotionGovernanceService` l.72), concept votes (`VoterIdentity.For`, member versus
  account class) and the OIDC authorize, token and userinfo endpoints (`OidcEndpoints` l.50, l.131) and the
  back-channel logout reconciler.
- `MemberRole` (`StandardMember`, `Producer`, `Maintainer`, `LedgerReviewer`, `PlatformAdmin`) are capability
  roles a member holds in addition. They are not the new field.
- Invites are `MembershipInvitation`, keyed by GitHub login (`MembershipSynchronizationService` l.37,
  `Members/Index.cshtml.cs` l.34). A guest may have no GitHub login at all.
- Heimdall sign-in (`HeimdallAuthService.StartAsync`) with `requireMemberAccess = false` already creates an
  identity-only account at `Authenticated`; with member access it creates `Active`.
- Nothing exists for room grants, leaving, erasing, or any "estate" in code (estates are rulings and research
  only, `docs/charter-amendment-proposal.md`).

## Decision: one role field, status stays lifecycle

Two authorities cannot both say "may this account vote". Today `Status == Active` says it. The cut:

- `Membership.Status` keeps its meaning as lifecycle of the account: may it hold a session. Add `Left` and
  `Erased` (see Leave and erase). `Active` means admitted and in good standing, for any role.
- `Membership.Role` (new, non-null, enum `AccountRole`) decides rights. `IsActiveMember` becomes
  `Status == Active && Role == Member`, and is the only place that expression exists. Every current
  `IsActiveMember` caller keeps working unchanged and a guest fails all of them without a per-site edit.
- OIDC authorize, token, userinfo and the logout reconciler change their test from "Active member" to
  "`Status == Active`" (any role), so a guest, user or patron can hold a chat session and a member demoted to
  `Suspended` still loses it. This is the one behavioural change outside new code, and it is the line to review.
- Backfill: `Active` becomes `member`; `Authenticated` and `PendingApproval` become `user`; an account with a
  current `Patron` tier snapshot and not `Active` becomes `patron`. Nobody starts as `guest`.

Naming risk: `MemberRole` already exists for capability roles. If the collision bites, call the new field
`Class`; the published field below follows whichever name is chosen. I use `Role` because that is the ruling's word.

Alternative considered and not recommended: a boolean `IsGuest` on `Membership` with guests kept at `Active`.
Smaller to land, but `user` and `patron` stay implicit in status and tier snapshots, so "the role field Personas
read" would have to be derived in three places. One stored field is one reading.

## Invite shape

New table `GuestInvitation`, separate from `MembershipInvitation` (which is GitHub-login-keyed and leads to
approval, not to a guest). Fields:

| field | rule |
|---|---|
| `Id` | Guid. |
| `TokenHash` | SHA-256 of a 256-bit random token. The token itself is shown to the issuer once, as a link, and never stored. |
| `IntendedIdentity` | the localpart the issuer picks (for example `sletz`), validated by `BifrostIdentityService.NormalizeIdentity`, reserved while the invite is open. Minted once on accept; a collision gets the usual `-2` suffix. |
| `Label` | free note for the issuer ("sletz, Faust developer"). |
| `RoomGrants` | the rooms to grant on accept (below), as a list of grant requests. |
| `IssuedByUserAccountId`, `IssuedAtUtc` | issuer must be a platform admin (`CanManageMembership`), as for member invites. |
| `ExpiresAtUtc` | default seven days. |
| `AcceptedByUserAccountId`, `AcceptedAtUtc`, `RevokedAtUtc` | one use; revoke before accept ends it. |

The link is a bearer: whoever signs in first with it is the guest. That is the cost of not needing sletz's
provider handle in advance, and it is why the token is single-use and short-lived and why the accept is shown
to the issuer. Recommended over binding the invite to a provider handle (which needs a Discord or Google id the
issuer may not have). The issuer may still restrict `ExpectedProvider`; open fork, see Questions.

## Sign-in via Heimdall

1. Guest opens `/invite/guest/<token>`. Bifrost checks hash, expiry, unused, unrevoked, and stores the invite id in
   the existing Heimdall attempt (`HeimdallAuthService` protects attempt state with the data protector).
2. Bifrost starts Heimdall sign-in with `requireMemberAccess = false` (identity only, no entitlement policy).
   Heimdall authenticates; it does not issue the identity (`docs/jurisdiction-map.md`).
3. On the callback, in one transaction: resolve `HeimdallAccountId`.
   - No account: create `UserAccount` with `IntendedIdentity`, `Membership { Status = Active, Role = Guest }`, write
     the room grants, mark the invite accepted, write the receipt.
   - Account exists: the invite never lowers an account. A `member`, `patron` or `user` keeps their role; the grants
     are added and the invite is spent. If the existing account is `Left` or `Erased`, refuse.
4. Bifrost issues its own cookie as for any account. From there the guest reaches chat through the one OIDC client.

Gap, drift found while reading: Bifrost's `AllowedProviders` is `discord, patreon, twitch`
(`HeimdallAuthService.AllowedProviders`), but Heimdall's `bifrost` app profile allows `discord, patreon, google`
(`app-profiles.ts`, `bifrostProfile`). Twitch cannot start in Heimdall and Google is refused by Bifrost. A guest with
neither Discord, Patreon nor a Google account cannot sign in, and GitHub is not a Heimdall sign-in provider for
this app. Which provider sletz uses is unknown to me; align the two lists before the first invite.

## Guest rights

A guest may: sign in, hold a Bifrost cookie, hold a chat session through OIDC, see and edit their own profile
(`MemberProfile`), be in the rooms granted to them, and leave or erase (below). A guest may not:

- vote on motions: `EffectiveVotingWeight` is `0` for any role but `member` (today it is `Max(1, ...)` for
  `Active`, which would give every guest weight 1; that line must read the role);
- cast concept-mill appraisals: `VoterIdentity.For` returns no voter for a guest (not even `VoterClass.Account`);
- appear on any estate roll or in any tally. The charter proposal snapshots each estate's roll at notice (A6);
  `guest` is on no roll, so a guest cannot be counted, cannot make a quorum, and reclassifying a guest after a
  snapshot changes nothing for that snapshot;
- use bridge transport, claim or log work, earn points or ledger credit, or hold tier snapshots (those make them
  a patron or member by changing role, not by decorating a guest);
- hold a `MemberRole` or any of the `CanManage*` flags;
- invite anyone.

This list is enforced by role in one place (`CurrentBifrostActor`), not by a check at each use.

## Per-room grants

Rooms are Matrix rooms in chat, not Bifrost objects. Bifrost owns the decision, so a grant is Bifrost state:

`GuestRoomGrant { Id, UserAccountId, RoomRef, GrantedByUserAccountId, GrantedAtUtc, ExpiresAtUtc?, RevokedAtUtc? }`

`RoomRef` is a room id or alias as chat names it. Aqua's `#aquasynth` is the first one if she says yes.

**Scope (per `guest-room-moderation-ruled`, `guest-room-authority-ruled`).** Project rooms are public-join and
hand-moderated by the steward, with Eureka as backstop, so a guest who can reach a public room needs no grant. A
grant exists only for a room its steward chooses to keep closed, and its grantor is that room's steward, not a
platform admin (the invite itself is still issued by a platform admin; `GrantedByUserAccountId` records the
steward). Room entry for public rooms is not Bifrost's decision.

The grant has to reach Matrix to mean anything. Today Bifrost's only Matrix writer is the Persona mouth, with
per-Persona access tokens (`docs/bridge.md`), and it holds no admin or invite right.

Gap (largest in this doc): something must perform "invite this Matrix user to that room" on a grant and "remove
them" on revoke or expiry. Candidates: a Bifrost-owned bot user that is a member with invite power in the granted
rooms (reuses the Matrix client API, no Synapse admin credential), or the Synapse admin API. I recommend the bot
user: narrower credential, and the room's power levels remain the real gate. Until it exists, the grant is a
record, a human invites the guest by hand, and the receipt says `delivered_by: manual`. Whether the closed rooms a
steward keeps are invite-only in Synapse is a chat configuration fact I have not verified.

What Matrix takes from the Bifrost account is exactly this effector: invite or remove on a grant, and the power
level the effector sets on invite. Role never enters OIDC, so Matrix reads no role from the issuer
(`bifrost-owns-accounts-invite-only`: grants flow from the account through the effector, not through claims).

**Guest DMs (`guest-dms-ruled`) conflict, open.** The mouth declines invites from other servers and accepts
same-server DM invites (`tools/persona-matrix-mouth.mjs` `answerInvites`). The ruling's decline-for-now assumed
federated guests; a Bifrost guest is a local `@sletz:<server>`, so the mouth would accept their DM. Either the mouth
declines DMs from role `guest` (using the account document below), or the ruling is revisited. That is Eureka's and
the ruling's owner's call, not this doc's; until it is made, the mouth's behaviour is the ruling's opposite for local
guests.

Second gap: a guest has a full Matrix account once MAS provisions the localpart, so Matrix itself will let them
create rooms or DM unless Synapse's room-creation and invite settings (`room_list_publication_rules`,
`allow_public_rooms_*`, a spam-checker or module) say otherwise. These are global in Synapse, not per user, so a
guest limit may have to be power levels in every room they are in. I did not find a per-user switch.

## Role change on the same account id, with a receipt

`ChangeRole(accountId, to, actor, reason)` is the only writer of `Membership.Role`. It runs in one transaction:

1. refuse `Erased` and `Left`; refuse a no-op;
2. write `Role`, and for `member` also set `Status = Active`, `ApprovedAtUtc`, and the default `StandardMember`
   capability role (the same effects as `OnPostApproveAsync`); for `patron`, nothing beyond the role (tier
   snapshots come from support events as before);
3. write `AccountRoleReceipt { Id, UserAccountId, From, To, ChangedAtUtc, ChangedByUserAccountId, Reason }`;
4. leave `UserAccount.Id`, `NormalizedBifrostIdentity`, `HeimdallAccountId`, grants and profile untouched.

Who may change: a platform admin (`CanManageMembership`). Promoting to `member` is the existing approve path;
this design routes it through `ChangeRole` so one writer exists. Promoting to `member` also applies whatever the
charter says about joining an estate (not decided; see Questions). Demotion (member to guest) is allowed and
receipted; it also removes the `MemberRole`s and capability flags in the same transaction.

The receipt is a Postgres row today, like `AuditEvent`. Bifrost's rule is that durable state has a CultCache `.cc`
witness (`docs/jurisdiction-map.md`, Design Rules); the witness is owed (Gaps).

Seamless means: the guest's session cookie, OIDC subject, Matrix id, display name and room memberships do not
change on promotion. A cookie issued before promotion carries identity claims only, never the role, so it needs
no refresh; the actor is read from the database each request (`CurrentBifrostActorAccessor`).

## Leave and erase

Neither exists in Bifrost today. Two different acts:

**Leave** (guest or any role, self-service): sets `Status = Left`, revokes every grant, and expels the account
from granted rooms through the same effector as grants. The OIDC reconciler already notices a non-`Active`
subject within 30 seconds and logs MAS out, and the token and userinfo endpoints refuse. The account row and
`Id` stay, so a later invite can restore it (a `Left` account re-accepting is a role change from itself, receipted).
Nothing else about the person is deleted by leaving.

**Erase** (self-service, or admin on request): irreversible. In one transaction: blank `DisplayName`, `AvatarUrl`,
`MemberProfile`, `GitHubLogin`/`GitHubUserId`, clear `HeimdallAccountId` (the authenticator link), set
`Status = Erased`, keep `Id` and `NormalizedBifrostIdentity` as a tombstone so the localpart is never reissued to
someone else (OIDC says it is minted once), keep receipts but with names stripped. The reason to keep a tombstone
row is that receipts and audit events point at the id.

An erasing guest has no votes, ledger entries or points, because guests cannot create them. A member or patron
who erases does, and those are accounting records: their erase is a separate question (Questions). Guest erase
is the case this design covers.

Outside Bifrost, erase is incomplete without two effectors:

- Matrix: Synapse's deactivate-with-erase admin call and MAS deactivation for the user. Federation is disabled in
  the chat map (`gamecult-ops/docs/gamecult-chat-map.md`: federation paths 404), which helps: messages are
  not copied to other servers. Other members' clients, and any export or training copy, are not reached by an
  erase. Whether chat content is training data is `heimdall-body`'s consent rulings (Heimdall PR #6), which already
  define exit by redaction; a guest's messages follow whatever that doc says, not this one.
- Heimdall: I found no account-delete or unlink path in Heimdall `src`. Clearing `HeimdallAccountId` on the Bifrost
  side leaves the Heimdall account and its provider link alive. Heimdall needs an erase call keyed by the account id
  before "erased" is true end to end.

## What Eureka reads

Personas read the role, not the account. Do not put it in OIDC: the issuer promises "no membership detail leaves
Bifrost" (`docs/oidc-issuer.md`) and chat does not need it. Publish a typed CultMesh document under the
`gamecult.bifrost.governance` family (the nearest existing schema namespace, `verse-service-contract.md` l.60), id
`gamecult.bifrost.account.v1`, key = account id:

| field | type | note |
|---|---|---|
| `account_id` | uuid | OIDC `sub`; never changes. |
| `identity` | string | `NormalizedBifrostIdentity`; the Matrix localpart. |
| `matrix_id` | string | the full mxid `@<identity>:<server>`, server name taken from MAS config at publish time so no reader assumes it. The lookup key for a Persona, which holds the Matrix sender id and not the account uuid. |
| `display_name` | string | blank once erased. |
| `role` | `guest` \| `user` \| `patron` \| `member` | the field Personas read. |
| `status` | `authenticated` \| `pending_approval` \| `active` \| `suspended` \| `left` \| `erased` | |
| `role_since` | UTC ms | from the latest `AccountRoleReceipt`. |
| `rooms` | list of room refs | only for `guest`: the live grants. Others get rooms by estate. |

Contract the document carries, not left to each reader:

- **Lookup.** The document is queryable by `matrix_id`.
- **Absence is `unknown`.** A sender with no document is not a Bifrost account (a bridge ghost or an unlinked user).
  Readers must treat it as `unknown`, never default it to `guest`, `user` or `member`. This reverses today's Persona
  reading (everyone unlisted is `member`), which narrows to the Bifrost `member` role.
- **Status gates the role.** Only `status = active` yields a usable role; `suspended`, `left`, `erased`,
  `authenticated` and `pending_approval` yield none.
- **Not Bifrost accounts.** Operator, the Persona's own id and Minds are typed by the reader's config, which takes
  precedence over the document. Minds are not Bifrost accounts and must not fall to `unknown`.
- **Freshness.** A reader loads the document when it builds each turn, never once at start, so a role change
  applies from the next message; past messages keep the role they were read under. `role_since` lets a trace say
  which role was read. Publisher bound: a role or status change is published within 30 seconds, the same bound the
  OIDC reconciler already keeps for a non-`active` subject. No publisher exists, so this is the requirement it is
  built to, not a measurement.

Bifrost does not decide Persona behavior; it states the role.

No such publisher or witness exists; Bifrost has no `.cc` export for accounts today.

## Cut sequence (not specs)

1. `Membership.Role`, backfill, `IsActiveMember` and the OIDC tests moved to the new reading, vote and concept-vote
   exclusions, with tests that fail if a guest gets weight or a voter key.
2. `GuestInvitation`, the accept path, the Heimdall provider list aligned.
3. `AccountRoleReceipt` and `ChangeRole`, with `OnPostApproveAsync` routed through it.
4. `GuestRoomGrant` and the Matrix effector.
5. Leave, then erase (Bifrost side), then the Matrix and Heimdall effectors.
6. The account document and its `.cc` witness.

## Gaps, plainly

- The Matrix effector for grants, leave and erase does not exist, and no credential for it is provisioned.
- Synapse cannot cap what a guest does per user; room-creation and invite limits are global (unverified: I did not
  read the live Synapse config).
- Heimdall has no erase path. Bifrost and Heimdall disagree on provider lists today.
- No `.cc` witness or CultMesh publisher for accounts or receipts. Bifrost's account state is Postgres only.
- Matrix id shape (`@<identity>:<server>`) depends on MAS importing `preferred_username` as localpart; the OIDC doc
  says so, I did not check the deployed MAS.
- Tie to the answer-collector consent doc (Heimdall PR #6): a guest posting in a room may be posting to a sample
  collector. That doc keys on `member_id`, and says a sender with no linked member is dropped. Whether a guest has a
  `member_id`, and so whether their answers are collected at all, is not decided here. Default: not collected.
- Erase for member and patron roles (ledger and vote records) is unmapped.

## Questions for the deciders

1. **Is `Role` the right name** given `MemberRole`, and is one stored role (this doc) preferred over a boolean
   `IsGuest`? Recommendation: stored role. Confidence: medium; the boolean is cheaper if user and patron are not
   yet needed.
2. **Bearer link or provider-bound invite?** Recommendation: bearer, single use, seven days, optional
   `ExpectedProvider`. Confidence: medium; this is a trust call about how sletz will be sent the link.
3. **Which Heimdall provider will sletz sign in with?** Needed before the lists are aligned.
4. **Do guests get a `member_id` for the answer collector?** Default no. Eureka agrees from the chat side: the
   Persona turn is not the collector, and presence in a room is the room's decision (Heimdall PR #6 owns the
   collector).
5. **Does promotion to member join the Members estate immediately**, or does the charter's notice and snapshot
   rule apply to the roll? The charter text is a proposal, not in force.
