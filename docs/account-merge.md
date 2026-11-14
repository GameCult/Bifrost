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

`ResolveAsync(id)` follows `Folded` links to the end (at most eight hops; each account is folded once). Heimdall patron intake resolves through it, so an event for a folded account's Heimdall id records on the survivor.

`CurrentBifrostActorAccessor` is the one gate for every cookie-authenticated route: a Frozen or Folded account resolves to an authenticated actor with no account (`CurrentBifrostActor.Merge` set), so no route can act as it. The OIDC authorize endpoint denies it with `MergedDescription`, and `ChatRefusalAsync` refuses its token and userinfo, so the logout reconciler ends its chat sessions. The accessor looks an account up by identity, then Heimdall id, then GitHub id; the first hit wins and several matching rows can no longer throw.

## Plan, commit, undo, finalize

`PlanAsync(survivorId, foldedId)` writes nothing and returns the blocks (codes only), the journal entries a commit would write, and counts of rows left untouched. Blocks: `same_account`, `account_missing`, `mind_account`, `native_account` (either side has no Heimdall id: v1 merges Heimdall-backed accounts only), `merge_in_flight`, `survivor_is_folded`, `already_folded`, `folded_survivor_in_grace`, `sealed_ballot_pending`.

`CommitAsync(mergeId)` needs `HeimdallMoved` (Committed and Final return the receipt; any other state throws). It is total: blocks were checked before the move, so it never refuses on them. It applies everything in one `SaveChanges` together with `State = Committed` and `GraceEndsUtc = CommittedUtc + 7 days`; a failed save leaves the merge `HeimdallMoved` and applies nothing, and a retry applies once.

What it reduces:

- **Suspension wins.** A suspended folded account and a survivor that is not suspended: the survivor becomes Suspended with the reason `merged with a suspended account (merge <id>)`. A survivor without a membership row is not given one.
- **Membership.** The folded membership becomes `Merged` (weight 0 by the not-Active rule). Roles and admin flags of the folded membership are never copied.
- **Votes on open standard motions.** Both voted: the folded vote is voided. Only the folded account voted: the vote moves to the survivor if the survivor is Active after the suspension rule, else it is voided. A move keeps the vote's weight and cast time; a later survivor cast updates the moved row (`CastVoteAsync` is per motion and account), never a second row. Closed motions are untouched.
- **Concept appraisals.** The survivor also appraised the subject: the folded one is voided. Otherwise it moves to `account:<survivor>` if the survivor is Active, else it is voided.
- **Patron events** of the folded account move to the survivor, and both tiers are derived again once from the events each account now holds (never tier A plus tier B). This happens only when events moved, so a tier that no event backs is not wiped by a merge with no patron history.

Voiding stamps `VoidedByMergeId` and `VoidedUtc`; the model's query filter on `Vote` and `ConceptAppraisal` then removes the row from every tally, including future ones. Nothing is deleted.

Left untouched (counted by the plan): consent receipts, passkeys, ledger entries, points, work and revenue rows.

`UndoLocalAsync(mergeId)` needs `HeimdallReversed` (Undone is a no-op). In one save it replays the journal backward: it un-voids votes whose motion is still Open and appraisals, moves a moved vote or appraisal back only if the survivor did not re-cast it since, restores the folded status, restores the survivor's status only while it is still Suspended with this merge's reason, moves patron events back and derives both tiers again. What it keeps it records as `KeptAfterUndo`; nothing is updated or deleted in the journal, only appended to.

`FinalizeDueAsync(now)` moves Committed merges whose grace has ended to `Final`; `AccountMergeSweeper` runs it every five minutes. Undo is never allowed past Final (the saga reads `State` and `GraceEndsUtc`).

## Not covered

`MembershipSynchronizationService` still writes the profile of any GitHub-signed-in row it finds, and re-promotes a bootstrap admin; it is not a merge path and was not changed here. Sealed-ballot rows of a folded account on a motion that is already closed stay as they are.
