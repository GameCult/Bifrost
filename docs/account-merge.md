# Account merge ledger

What a merge of two Bifrost accounts does to Bifrost records, who owns it, and how an account stands under one. Design: `/opt/commons/handoff/heimdall-account-merge-design-20261113.md` sections 3, 5 and 7. This is the ledger only: no page, no Heimdall call, and nothing calls `CommitAsync` or `UndoLocalAsync` yet (the saga cut does).

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

An alias is one hop: a landed merge's survivor never folds into a third account (the `folded_is_survivor` block), so a folded account's survivor is always the account that carries the person. Heimdall patron intake reads the standing: an event for a folded account's Heimdall id records on the survivor and is journalled on the merge (`JournalIntakeAsync`, idempotent for a provider's retry), so an undo moves it back.

`CurrentBifrostActorAccessor` is the one gate for every cookie-authenticated route: a Frozen or Folded account resolves to an authenticated actor with no account (`CurrentBifrostActor.Merge` set), so no route can act as it. The OIDC authorize endpoint denies it with `MergedDescription`, and `ChatRefusalAsync` refuses its token and userinfo, so the logout reconciler ends its chat sessions. The accessor looks an account up by identity, then Heimdall id, then GitHub id; the first hit wins and several matching rows can no longer throw. The same standing is read at the other doors: the desk bearer door (`ActiveMemberIdAsync`) refuses a Frozen or Folded subject, the linked-accounts page and the link step of Heimdall completion refuse one (no login linked or unlinked, no passkey enrolled while the saga moves them), and the vote and appraisal writers read it again at the write, so an actor resolved before the merge froze the account writes nothing after it.

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

`FinalizeDueAsync(now)` moves Committed merges whose grace has ended to `Final`, one save per merge; `AccountMergeSweeper` runs it every five minutes. `State` is the merge's concurrency token, so a transition computed from a stale read (the sweeper's Final over the saga's UndoRequested, or the reverse) fails its save and the other transition stands; the sweep skips a merge it lost. `UndoLocalAsync` also refuses a merge with `FinalizedUtc` set: undo is never allowed past Final.

## Merged is terminal

Only the ledger writes `Membership.Status = Merged` or leaves it. The other status writers refuse a merged row (`Membership.IsMerged`): the bootstrap-admin promotion (startup and every GitHub sign-in, which is the one status write sign-in sync makes on an existing account) skips it, the Heimdall member-access claim does not activate it, member approval refuses it and the suspension service reports `NotApplicable`. Folded standing itself is still derived from the merge state, never from this status.

## Rollback

Bifrost is **roll-forward only** once a merge exists (`heimdall-body:ruling:merge-roll-forward-only`). A build older than `AddAccountMerges` cannot read the `Merged` membership status (EF throws on `/App/Members` and on the folded account's sign-in) and has no query filter, so it counts voided votes and appraisals again. Deploy the merge migration knowing that a code rollback past it is not available after the first merge lands; fix forward. The migration's `Down` refuses while any `AccountMerges` row exists, whatever state, so it cannot drop the merge record and journal under a `Merged` membership. No tombstone table is built: it would fix only the recount, not the enum.

## Not covered

`MembershipSynchronizationService` still writes the profile of a GitHub-signed-in row that is not merged. Sealed-ballot rows of a folded account on a motion that is already closed stay as they are. Every writer that acts for an account (votes, concept appraisals, sealed-ballot commit and reveal) reads standing through `AccountMergeLedger.MayWriteAsync` after loading what it changes and before it mutates, and an account with a standing writes nothing. Patron events (the Heimdall intake and the Stripe webhook) route through `AccountMergeLedger.SupportTargetAsync` immediately before they record: a folded account's event lands on the survivor and is journalled, so undo moves it back. Each of these is a read followed by a save, so a request whose read precedes the commit by microseconds and whose save follows it lands on the folded account: that window is not closed. Closing it needs a lock held across the read and the save, or the tally-time voiding of design section 7; neither is built, and no operator void tool exists. A patron event or vote that falls in it stays on the folded account until it is corrected by hand in the database.

`UX_TierSnapshots_MembershipId_CurrentPatron` allows one current Patron snapshot per membership, so two restages of one survivor racing cannot both leave a current snapshot: the loser's save fails on the unique index, a merge commit rolls back whole and is retried by the saga, and a patron refresh re-reads and derives again.
