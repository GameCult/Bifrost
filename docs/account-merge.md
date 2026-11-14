# Account merge ledger

What a merge of two Bifrost accounts does to Bifrost records, who owns it, and how an account stands under one. Design: `/opt/commons/handoff/heimdall-account-merge-design-20261113.md` sections 3, 5 and 7. The ledger owns what a merge does to Bifrost records; the saga (below) owns the merge's lifecycle around it and every Heimdall call. The member's pages are in Pages and flags below; with `AccountMerge:Enabled` false (the default) no page can start a merge.

## Owner

`Features/Membership/AccountMergeLedger.cs` is the one owner of merge effects. Outside it nothing writes `VoidedByMergeId`, `MembershipStatus.Merged`, `AccountMergeEntry`, or the states Committed, Final and Undone, and nothing but the ledger calls `IgnoreQueryFilters` on `Vote` or `ConceptAppraisal`. The ledger never reads an email, display name or login string; accounts are matched by id.

## States

`AccountMerge.State` (stored as a string) declares the whole lifecycle once: `Proposed`, `Confirmed`, `HeimdallMoved`, `Committed`, `Final`, `UndoRequested`, `HeimdallReversed`, `Undone`, `Failed`. Later cuts add columns, not states.

An account's standing is derived from that state on every request; there is no `MergedInto` column and `Membership.Status = Merged` is never read to decide it.

| State of the merge the account is folded in | `StandingAsync` |
| --- | --- |
| Confirmed, HeimdallMoved | `Frozen(mergeId)` |
| Committed, Final, UndoRequested, HeimdallReversed | `Folded(mergeId, survivorId)` |
| Proposed, Failed, Undone | none |

An alias is one hop: a landed merge's survivor never folds into a third account (the `folded_is_survivor` block), so a folded account's survivor is always the account that carries the person. Every patron event is recorded through `PatronageService.RecordSupportEventAsync`, which writes through the gate below: an event for a folded account (Heimdall intake, Stripe webhook, the admin Ledger's manual record) records on the survivor and is journalled in the same save, so an undo moves it back; a provider retry returns the stored event and journals it once if it sits on the survivor.

`CurrentBifrostActorAccessor` is the one gate for every cookie-authenticated route: a Frozen or Folded account resolves to an authenticated actor with no account (`CurrentBifrostActor.Merge` set), so no route can act as it. The OIDC authorize endpoint denies it with `MergedDescription`, and `ChatRefusalAsync` refuses its token and userinfo, so the logout reconciler ends its chat sessions. The accessor looks an account up by identity, then Heimdall id, then GitHub id; the first hit wins and several matching rows can no longer throw. The same standing is read at the other doors: the desk bearer door (`ActiveMemberIdAsync`) refuses a Frozen or Folded subject, the linked-accounts page and the link step of Heimdall completion refuse one (no login linked or unlinked, no passkey enrolled while the saga moves them), and every writer reads it again at the write, through the gate below, so an actor resolved before the merge froze the account writes nothing after it.

## Plan, commit, undo, finalize

`PlanAsync(survivorId, foldedId)` writes nothing and returns the blocks (codes only), the journal entries a commit would write, and counts of rows left untouched. Blocks: `same_account`, `account_missing`, `mind_account`, `native_account` (either side has no Heimdall id: v1 merges Heimdall-backed accounts only), `merge_in_flight`, `survivor_is_folded`, `already_folded`, `folded_is_survivor`, `sealed_ballot_pending`. The database backs the two overlap blocks the plan checks first (check-then-act): a partial unique index lets an account be folded by one live merge only (Confirmed through HeimdallReversed), and a trigger refuses a live merge whose survivor is another live merge's folded account, taking advisory locks on both accounts so racing transactions serialise.

`CommitAsync(mergeId)` needs `HeimdallMoved` (Committed and Final return the receipt; any other state throws). It is total: blocks were checked before the move, so it never refuses on them. It applies everything in one `SaveChanges` together with `State = Committed` and `GraceEndsUtc = CommittedUtc + 7 days`; a failed save leaves the merge `HeimdallMoved` and applies nothing, and a retry applies once.

What it reduces:

- **Suspension wins.** A suspended folded account and a survivor that is not suspended: the survivor becomes Suspended with the reason `merged with a suspended account (merge <id>)`. A survivor without a membership row (the chat tier holds none) is given a Suspended one, journalled as `Standing`/`Created`, because an absent membership chats and the next member-access claim would create an Active one; undo removes it unless an admin has taken it over since.
- **Membership.** The folded membership becomes `Merged` (weight 0 by the not-Active rule). Roles and admin flags of the folded membership are never copied.
- **Votes on open standard motions.** Both voted: the folded vote is voided. Only the folded account voted: the vote moves to the survivor if the survivor is Active after the suspension rule, else it is voided. A move keeps the vote's cast time and takes the survivor's weight, derived from the survivor's tiers after the patron events moved (the folded weight is journalled for undo); a later survivor cast updates the moved row (`CastVoteAsync` is per motion and account), never a second row. Closed motions are untouched.
- **Concept appraisals.** The survivor also appraised the subject: the folded one is voided. Otherwise it moves to `account:<survivor>` as a Member voter if the survivor is Active, else it is voided (the prior class and key are journalled).
- **Patron events** of the folded account move to the survivor, and both tiers are derived again once from the events each account now holds (never tier A plus tier B). This happens only when events moved, so a tier that no event backs is not wiped by a merge with no patron history (only `PatronageService` writes Patron snapshots, so a merge with no folded events leaves both tiers as they were).

Voiding stamps `VoidedByMergeId` and `VoidedUtc`; the model's query filter on `Vote` and `ConceptAppraisal` then removes the row from every tally, including future ones. Nothing is deleted.

Left untouched (counted by the plan): consent receipts, passkeys, ledger entries, points, work and revenue rows.

`UndoLocalAsync(mergeId)` needs `HeimdallReversed` (Undone is a no-op). In one save it replays the journal backward: it un-voids votes whose motion is still Open and appraisals, moves a moved vote or appraisal back only if the survivor did not re-cast it since, restores the folded status, restores the survivor's status only while it is still Suspended with this merge's reason, moves patron events back and derives both tiers again. What it keeps it records as `KeptAfterUndo`; nothing is updated or deleted in the journal, only appended to.

`FinalizeDueAsync(now)` moves Committed merges whose grace has ended to `Final`, one save per merge; `AccountMergeSweeper` runs it every 30 seconds, after the saga's due steps (see Saga). `State` is the merge's concurrency token, so a transition computed from a stale read (the sweeper's Final over the saga's UndoRequested, or the reverse) fails its save and the other transition stands; the sweep skips a merge it lost. `UndoLocalAsync` also refuses a merge with `FinalizedUtc` set: undo is never allowed past Final.

## Saga

`Features/Membership/AccountMergeSaga.cs` is the one owner of the merge row's lifecycle outside the ledger's Committed, Final and Undone, and the only caller of `HeimdallIdentityMoveClient.MoveAsync` (whose closed `Moved`/`Refused`/`Unknown` answer it consumes as is; the client retries nothing). It writes Confirmed, HeimdallMoved, Failed, UndoRequested and HeimdallReversed, the return UndoRequested to Committed, the move ids and the sealed proofs; it never writes a vote, membership or journal row (it calls `CommitAsync` and `UndoLocalAsync`), reads no standing of its own, and never judges a proof's tier or freshness (Heimdall's).

| State | Written by | Key and proofs | A crash here |
| --- | --- | --- | --- |
| Confirmed | `ConfirmAsync`: one insert, the consent record | `HeimdallMoveId` minted (random, never derived from the merge id); both sides' Heimdall bearers sealed | Before the insert nothing exists. After it the sweep calls Heimdall with the stored id; a lost answer replays to the same receipt, because Heimdall answers a replay before it judges liveness. |
| HeimdallMoved | `AdvanceAsync` on `Moved`, one save | proofs wiped; receipt tiers and moved login ids stored | `CommitAsync` is total and idempotent; the sweep retries it. |
| Failed | `AdvanceAsync` on any `Refused` | proofs wiped; `FailureCode` is Heimdall's code | Terminal. Heimdall applied nothing, Bifrost changed nothing, the folded account stands as before. |
| Committed | ledger `CommitAsync`, one save with every reduction | grace 7 days | A crash before the save leaves HeimdallMoved. |
| UndoRequested | `RequestUndoAsync`: the survivor, fresh survivor proofs, inside the grace | `HeimdallReverseMoveId` minted; survivor bearers sealed | The sweep replays the reversal (past the grace too). A refused reversal returns the merge to Committed, proofs wiped, `LastOutcome` `undo_<code>`. |
| HeimdallReversed | `AdvanceAsync` on the reversal's `Moved` | proofs wiped | `UndoLocalAsync` is retried. |
| Undone, Final | ledger | | |

`AdvanceAsync(mergeId)` is the one step function: the confirm's inline advance, the sweeper's resume and an undo request all run it, forward move and reversal alike (same seal, same outcome handling, same backoff). Every transition is one save guarded by `State`, the concurrency token; the loser of a race clears its tracker, reloads and continues from the stored state, so two advances make one Heimdall move and one commit. `Proposed` is declared by the ledger and written by nobody in this cut.

`ConfirmAsync` refuses (writing nothing) a third-party actor (`NotParty`), a plan with blocks (`Blocked`, codes only), a plan other than the one the member saw (`PreviewStale`: `MergePlan.Digest(survivorId, foldedId)`, a SHA-256 over a length-prefixed canonical text of the plan), and any proof of another Heimdall account (`ProofAccountMismatch`). A second live merge folding the same account, or a chain, is refused by the database (the live-merge unique index and the chain trigger) and answered `Blocked(merge_in_flight)`. Confirm and the undo request run through `AccountMergeLedger.WriteAsAsync` for the acting member, like every writer that acts for an account.

**Heimdall down.** `Unknown` (a timeout, a 5xx, a 429, an unrecognised 4xx, `move_id_conflict`) never fails a merge: the saga counts `Attempts`, sets `NextAttemptUtc` (15 s, 1 min, 5 min, then 15 min) and replays the same stored id; the folded account stays Frozen until Heimdall answers. Heimdall's answer to a replay of a move it already stored does not depend on the proofs' age, but a move that never reached Heimdall does: an outage longer than the proofs' freshness (300 s for a login's weak proof, 600 s for a passkey's strong one) ends, when Heimdall is back, in `Refused proof_stale` and a Failed merge; the member confirms again with fresh sign-ins. A replay whose proofs no longer verify (Heimdall's key regenerated) answers `move_id_conflict`, stays Unknown and can never settle: that is follow-up `merge-stuck-operator-path`, as is a receipt that names an account other than the destination (`receipt_mismatch`: proofs wiped, no retry) and a sealed payload the key ring can no longer open (`proofs_unreadable`: retried in an hour, state unchanged, because Heimdall may already have moved).

**Sealed proofs.** The saga holds the two Heimdall bearers (`ruling merge-sealed-proofs-until-move-final`) only between Confirmed (or UndoRequested) and Heimdall's final answer: protected with Data Protection (purpose `Bifrost.AccountMerge.Proofs.v1`), never logged, wiped in the save that records Moved or Refused. Logs name the merge id, state, outcome class and refusal code; never a token, account id, identity id or provider user id. Proof coverage is Heimdall's rule (`move-proof-strict-passkey`): a proof through one login moves only that login, and only an asserted passkey speaks for the whole account, so a folded account's every login must be proved (or a passkey asserted) or Heimdall answers `identity_not_proved`. A `moveId` replayed with other identities returns the first receipt (`move-replay-first-receipt`).

`AccountMergeSweeper` is the one merge loop (30 s): per tick, in one scope, `AccountMergeSaga.AdvanceDueAsync` (every in-flight merge whose `NextAttemptUtc` is null or due, oldest first, a failure logged and skipped), then `FinalizeDueAsync`.

## Pages and flags

`Pages/Membership/MergeAccounts` is the one page that creates a merge. It is `[Authorize]`, no-store, a 404 unless `AccountMergeOptions.Enabled` (section `AccountMerge`, default false; environment `AccountMerge__Enabled`) and forbidden to a frozen or folded account, as Linked accounts is. Linked accounts shows one "Merge two accounts" link, only when the flag is on and the session is Heimdall-backed.

Its stage is derived on every request from `MergeProofs.Snapshot` and the `keep` query; no cookie or form field carries progress except the chosen direction and the preview digest, which the saga re-checks.

1. Prove: the member names a provider for this account (`MergeProveSelf`) and for the other (`MergeProveOther`). Nothing about the other account is rendered until both proofs are held.
2. Choose: both accounts are named by display name only, and `keep=self` or `keep=other` shows the preview. No survivor is preselected (the chat-history default needs `ChatFirstSignInUtc`, follow-up `merge-chat-default-survivor`; the chat paragraph carries the warning meanwhile).
3. Prove the rest: one fresh sign-in for each login of the account that would be folded and is not yet proved (`ProveRemaining`). Proofs older than `MergeProofs.ProofLifetime` fall out and are asked again.
4. Confirm: two checkboxes, one per account, the direction and the digest. `AccountMergeSaga.ConfirmAsync` inserts the Confirmed row, which is the consent record (actor, time, digest, proof providers); the page then runs `AdvanceAsync` once (a throw is logged and the sweeper resumes) and sends the member to Merge status. `MergeProofs.Clear` runs after a Confirmed outcome only: Blocked, PreviewStale, NotParty and ProofAccountMismatch come back as closed codes (`?merge=`), keep the proofs and write nothing.

`MergePreview` is the one wording of the plan (counts by entry kind and action, the nine block codes, the static statements about chat, connections, StreamPixels, roles, one vote per motion, the 7-day undo and ordinary sign-ins); the digest is `MergePlan.Digest` of the plan just rendered. `MergeWording.Confirm` holds the refusal words. The page writes no merge, vote, membership or journal row.

Deploy: set `AccountMerge__Enabled` only after Heimdall's identity-moves route is deployed and probed. A Confirmed merge against a Heimdall without it stays Unknown and keeps the folded account frozen.

## Merged is terminal

Only the ledger writes `Membership.Status = Merged` or leaves it. The other status writers refuse a merged row (`Membership.IsMerged`): the bootstrap-admin promotion (startup and every GitHub sign-in, which is the one status write sign-in sync makes on an existing account) skips it, the Heimdall member-access claim does not activate it, member approval refuses it and the suspension service reports `NotApplicable`. Folded standing itself is still derived from the merge state, never from this status.

## Rollback

Bifrost is **roll-forward only** once a merge exists (`heimdall-body:ruling:merge-roll-forward-only`). A build older than `AddAccountMerges` cannot read the `Merged` membership status (EF throws on `/App/Members` and on the folded account's sign-in) and has no query filter, so it counts voided votes and appraisals again. Deploy the merge migration knowing that a code rollback past it is not available after the first merge lands; fix forward. The migration's `Down` refuses while any `AccountMerges` row exists, whatever state, so it cannot drop the merge record and journal under a `Merged` membership. No tombstone table is built: it would fix only the recount, not the enum.

## Not covered

`MembershipSynchronizationService` still writes the profile of a GitHub-signed-in row that is not merged. Sealed-ballot rows of a folded account on a motion that is already closed stay as they are. Every writer that acts for an account (votes, concept appraisals, sealed-ballot commit and reveal, patron events, the Heimdall sign-in's membership) passes through `AccountMergeLedger.WriteAsAsync`. On Postgres it opens a transaction, takes a shared advisory lock on the account (`pg_advisory_xact_lock_shared(hashtextextended(id::text, 0))`, the key the chain trigger takes exclusively when a merge row is inserted or changes state), reads standing, runs the writer's save and commits. `CommitAsync` takes the exclusive lock on the folded account before it reduces, then re-reads the merge. So: a writer whose standing read found no merge saves before a merge can freeze or fold the account, and the commit's reduction sees what it saved; a merge that is already frozen or folded when the writer reads is refused (votes, appraisals, ballots, sign-in membership) or routed (patron events). Guaranteed only for writes made through the gate, and only on Postgres: the in-memory provider has no lock or transaction (tests that rest on the lock run on live Postgres), and a writer that catches a database error inside the gate cannot continue, because the transaction is aborted. Not closed: writers outside the gate (nothing in the code base today; a new one must use it), and a writer's own stale reads taken before the gate opened (the motion a vote was loaded with), which concern the motion, not the account's standing. No operator void tool exists.

`UX_TierSnapshots_MembershipId_CurrentPatron` allows one current Patron snapshot per membership, so two restages of one survivor racing cannot both leave a current snapshot: the loser's save fails on the unique index, a merge commit rolls back whole and is retried by the saga, and a patron refresh re-reads and derives again.
