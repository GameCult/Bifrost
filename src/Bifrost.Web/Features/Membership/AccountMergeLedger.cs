using System.Globalization;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Patronage;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Web.Features.Membership;

/// <summary>How an account stands under a merge: the folded account of an unfinished merge is Frozen, of a landed one Folded.</summary>
public abstract record MergeStanding(Guid MergeId)
{
    public sealed record Frozen(Guid MergeId) : MergeStanding(MergeId);

    public sealed record Folded(Guid MergeId, Guid SurvivorId) : MergeStanding(MergeId);
}

public sealed record MergePlanEntry(
    AccountMergeEntryKind Kind,
    AccountMergeEntryAction Action,
    Guid RecordId,
    Guid? PriorUserAccountId = null,
    string? PriorText = null,
    DateTimeOffset? PriorAtUtc = null);

/// <summary>A dry run: the block codes (never account names), the journal entries a commit would write, and the record counts left untouched.</summary>
public sealed record MergePlan(
    IReadOnlyList<string> Blocks,
    IReadOnlyList<MergePlanEntry> Entries,
    IReadOnlyDictionary<string, int> Left)
{
    /// <summary>
    /// Lowercase hex SHA-256 over a canonical text of the plan, in plan order (Left by key): what the member saw, comparable at confirm.
    /// Every field is length-prefixed, so no value can imitate a separator. The plan holds no account ids of its own, so the caller
    /// names the pair the plan was made for.
    /// </summary>
    public string Digest(Guid survivorId, Guid foldedId)
    {
        var text = new System.Text.StringBuilder();
        void Field(string? value) => text.Append(value is null ? "-" : $"{value.Length}:{value}").Append('|');
        Field(survivorId.ToString("N"));
        Field(foldedId.ToString("N"));
        foreach (var entry in Entries)
        {
            Field(entry.Kind.ToString());
            Field(entry.Action.ToString());
            Field(entry.RecordId.ToString("N"));
            Field(entry.PriorUserAccountId?.ToString("N"));
            Field(entry.PriorText);
            Field(entry.PriorAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            text.Append('\n');
        }

        text.Append("left\n");
        foreach (var (key, count) in Left.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            Field(key);
            Field(count.ToString(CultureInfo.InvariantCulture));
        }

        text.Append("\nblocks\n");
        foreach (var block in Blocks)
        {
            Field(block);
        }

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }
}

/// <summary>
/// The one owner of what an account merge does to Bifrost records and of how an account stands under one: the plan, the Committed
/// save, the undo save, the Final transition and StandingAsync. A merge only reduces: nothing is deleted, votes and
/// appraisals are stamped void (the model's query filter then removes them from every tally) or re-pointed, and every change is
/// journalled in AccountMergeEntry. Design: /opt/commons/handoff/heimdall-account-merge-design-20261113.md sections 3, 5, 7.
/// </summary>
public sealed class AccountMergeLedger(
    BifrostDbContext dbContext,
    PatronageService patronageService,
    TimeProvider timeProvider)
{
    public static readonly TimeSpan Grace = TimeSpan.FromDays(7);

    private static readonly AccountMergeState[] FrozenStates = [AccountMergeState.Confirmed, AccountMergeState.HeimdallMoved];

    private static readonly AccountMergeState[] FoldedStates =
        [AccountMergeState.Committed, AccountMergeState.Final, AccountMergeState.UndoRequested, AccountMergeState.HeimdallReversed];

    private static readonly AccountMergeState[] InFlightStates =
        [AccountMergeState.Confirmed, AccountMergeState.HeimdallMoved, AccountMergeState.UndoRequested, AccountMergeState.HeimdallReversed];

    public static string SuspensionReason(Guid mergeId) => $"merged with a suspended account (merge {mergeId})";

    public Task<MergeStanding?> StandingAsync(Guid userAccountId, CancellationToken cancellationToken) =>
        StandingAsync(dbContext, userAccountId, cancellationToken);

    /// <summary>The single reading of standing, shared by the actor accessor and the OIDC refusal, which holds only a context. Derived from the merge state on every call.</summary>
    public static async Task<MergeStanding?> StandingAsync(BifrostDbContext db, Guid userAccountId, CancellationToken cancellationToken)
    {
        var merge = await db.AccountMerges.AsNoTracking()
            .Where(x => x.FoldedUserAccountId == userAccountId &&
                (FrozenStates.Contains(x.State) || FoldedStates.Contains(x.State)))
            .OrderByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return merge is null
            ? null
            : FrozenStates.Contains(merge.State)
                ? new MergeStanding.Frozen(merge.Id)
                : new MergeStanding.Folded(merge.Id, merge.SurvivorUserAccountId);
    }

    /// <summary>
    /// The one gate every writer that acts for an account passes through (votes, appraisals, sealed ballots, patron events, the Heimdall
    /// sign-in's membership). On Postgres it holds a shared advisory lock on the account, the key the chain trigger locks exclusively
    /// when a merge row is inserted or changes state and that <see cref="CommitAsync"/> takes exclusively before it reduces, across the
    /// standing read and everything <paramref name="write"/> saves, then commits. So a merge cannot freeze or fold the account between a
    /// writer's standing read and its save: it waits for the writer, and a writer that starts after it reads the new standing. The
    /// lock covers writes made through this gate and nothing else; it is held until the transaction ends, so a writer must not
    /// catch a database error inside <paramref name="write"/> and carry on (the transaction is already aborted). On the in-memory
    /// provider there is no lock and no transaction: the standing is read, then the writer saves.
    /// </summary>
    public static async Task<T> WriteAsAsync<T>(BifrostDbContext db, Guid userAccountId, Func<MergeStanding?, Task<T>> write, CancellationToken cancellationToken)
    {
        await using var transaction = await BeginAsync(db, cancellationToken);
        await LockAsync(db, userAccountId, exclusive: false, cancellationToken);
        var result = await write(await StandingAsync(db, userAccountId, cancellationToken));
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return result;
    }

    /// <summary>A transaction for the caller to commit, or null where there is none to open: not Postgres, or one already open (its owner commits).</summary>
    private static async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginAsync(BifrostDbContext db, CancellationToken cancellationToken) =>
        db.Database.IsNpgsql() && db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;

    /// <summary>The account's lock, on the key the chain trigger uses (hashtextextended of the id's text). A no-op off Postgres.</summary>
    private static async Task LockAsync(BifrostDbContext db, Guid userAccountId, bool exclusive, CancellationToken cancellationToken)
    {
        if (!db.Database.IsNpgsql())
        {
            return;
        }

        var key = userAccountId.ToString();
        await (exclusive
            ? db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken)
            : db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock_shared(hashtextextended({key}, 0))", cancellationToken));
    }

    /// <summary>Writes nothing. <paramref name="exceptMergeId"/> lets the saga re-plan a merge it already holds without it blocking itself.</summary>
    public async Task<MergePlan> PlanAsync(Guid survivorId, Guid foldedId, CancellationToken cancellationToken, Guid? exceptMergeId = null)
    {
        var blocks = await BlocksAsync(survivorId, foldedId, exceptMergeId, cancellationToken);
        var entries = new List<MergePlanEntry>();
        var left = new Dictionary<string, int>();

        if (!blocks.Contains("account_missing"))
        {
            var reduction = await ReduceAsync(survivorId, foldedId, cancellationToken);
            entries.AddRange(reduction.Entries);
            left["ConsentReceipts"] = await dbContext.ConsentReceipts.CountAsync(x => x.UserAccountId == foldedId, cancellationToken);
            left["PasskeyCredentials"] = await dbContext.PasskeyCredentials.CountAsync(x => x.UserAccountId == foldedId, cancellationToken);
            left["LedgerEntries"] = await dbContext.LedgerEntries.CountAsync(x => x.UserAccountId == foldedId, cancellationToken);
        }

        return new MergePlan(blocks, entries, left);
    }

    private async Task<IReadOnlyList<string>> BlocksAsync(Guid survivorId, Guid foldedId, Guid? exceptMergeId, CancellationToken cancellationToken)
    {
        var blocks = new List<string>();
        if (survivorId == foldedId)
        {
            blocks.Add("same_account");
        }

        var accounts = await dbContext.UserAccounts.AsNoTracking()
            .Where(x => x.Id == survivorId || x.Id == foldedId)
            .Select(x => new { x.Id, x.HeimdallAccountId })
            .ToListAsync(cancellationToken);
        if (accounts.Count < (survivorId == foldedId ? 1 : 2))
        {
            blocks.Add("account_missing");
            return blocks;
        }

        if (await dbContext.MindPersonas.AnyAsync(x => x.UserAccountId == survivorId || x.UserAccountId == foldedId, cancellationToken))
        {
            blocks.Add("mind_account");
        }

        if (accounts.Any(x => string.IsNullOrWhiteSpace(x.HeimdallAccountId)))
        {
            blocks.Add("native_account");
        }

        if (await dbContext.AccountMerges.AnyAsync(
                x => x.Id != exceptMergeId &&
                    InFlightStates.Contains(x.State) &&
                    (x.SurvivorUserAccountId == survivorId || x.FoldedUserAccountId == survivorId ||
                     x.SurvivorUserAccountId == foldedId || x.FoldedUserAccountId == foldedId),
                cancellationToken))
        {
            blocks.Add("merge_in_flight");
        }

        if (await FoldedLinkAsync(survivorId, exceptMergeId, cancellationToken))
        {
            blocks.Add("survivor_is_folded");
        }

        if (await FoldedLinkAsync(foldedId, exceptMergeId, cancellationToken))
        {
            blocks.Add("already_folded");
        }

        // One hop only: a landed merge's survivor never folds into a third account, so an alias is never a chain.
        if (await dbContext.AccountMerges.AnyAsync(
                x => x.Id != exceptMergeId && FoldedStates.Contains(x.State) && x.SurvivorUserAccountId == foldedId,
                cancellationToken))
        {
            blocks.Add("folded_is_survivor");
        }

        if (await dbContext.SealedBallots.AnyAsync(
                x => x.UserAccountId == foldedId && x.Motion.Status == MotionStatus.Open,
                cancellationToken))
        {
            blocks.Add("sealed_ballot_pending");
        }

        return blocks;
    }

    private Task<bool> FoldedLinkAsync(Guid accountId, Guid? exceptMergeId, CancellationToken cancellationToken) =>
        dbContext.AccountMerges.AnyAsync(
            x => x.Id != exceptMergeId && x.FoldedUserAccountId == accountId && FoldedStates.Contains(x.State),
            cancellationToken);

    /// <summary>
    /// Applies the merge to Bifrost records in one save, together with State=Committed. Total: blocks were checked before the move
    /// and a refusal now would strand moved logins, so it recomputes the reductions from current rows and applies them. Committed
    /// and Final are no-ops returning the receipt; any other state but HeimdallMoved is an error.
    /// </summary>
    public async Task<AccountMerge> CommitAsync(Guid mergeId, CancellationToken cancellationToken)
    {
        var merge = await dbContext.AccountMerges.SingleAsync(x => x.Id == mergeId, cancellationToken);

        // The exclusive lock first, so no writer that read the folded account's standing is still to save when the reduction reads its
        // rows, then the state again: a commit that waited here found the merge landed and returns its receipt.
        await using var transaction = await BeginAsync(dbContext, cancellationToken);
        await LockAsync(dbContext, merge.FoldedUserAccountId, exclusive: true, cancellationToken);
        await dbContext.Entry(merge).ReloadAsync(cancellationToken);
        if (merge.State is AccountMergeState.Committed or AccountMergeState.Final)
        {
            return merge;
        }

        if (merge.State != AccountMergeState.HeimdallMoved)
        {
            throw new InvalidOperationException($"Merge {mergeId} is {merge.State}; only a HeimdallMoved merge can be committed.");
        }

        try
        {
            var reduction = await ReduceAsync(merge.SurvivorUserAccountId, merge.FoldedUserAccountId, cancellationToken, mergeId);
            var now = timeProvider.GetUtcNow();
            var seq = 0;
            foreach (var entry in reduction.Entries)
            {
                dbContext.AccountMergeEntries.Add(Journal(merge.Id, ++seq, entry, now));
            }

            reduction.Apply(now);
            if (reduction.MovedPatronEvents)
            {
                await RestageTiersAsync(reduction.Survivor, reduction.Folded, now, cancellationToken);
            }

            reduction.ReweighMovedVotes();
            merge.State = AccountMergeState.Committed;
            merge.CommittedUtc = now;
            merge.GraceEndsUtc = now + Grace;
            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return merge;
        }
        catch
        {
            // Nothing was saved; drop what was staged so a retry on this context starts from the stored rows.
            dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>Replays the journal backward in one save with State=Undone. Total, like commit. Undone is a no-op; any other state but HeimdallReversed is an error.</summary>
    public async Task<AccountMerge> UndoLocalAsync(Guid mergeId, CancellationToken cancellationToken)
    {
        var merge = await dbContext.AccountMerges.SingleAsync(x => x.Id == mergeId, cancellationToken);
        if (merge.State == AccountMergeState.Undone)
        {
            return merge;
        }

        if (merge.State != AccountMergeState.HeimdallReversed)
        {
            throw new InvalidOperationException($"Merge {mergeId} is {merge.State}; only a HeimdallReversed merge can be undone locally.");
        }

        if (merge.FinalizedUtc is not null)
        {
            throw new InvalidOperationException($"Merge {mergeId} is Final; a merge is never undone past Final.");
        }

        try
        {
            var now = timeProvider.GetUtcNow();
            var journal = await dbContext.AccountMergeEntries.Where(x => x.MergeId == mergeId).OrderByDescending(x => x.Seq).ToListAsync(cancellationToken);
            var seq = journal.Count == 0 ? 0 : journal.Max(x => x.Seq);
            var survivor = await dbContext.UserAccounts.Include(x => x.Membership).Include(x => x.PatronSupportEvents)
                .SingleAsync(x => x.Id == merge.SurvivorUserAccountId, cancellationToken);
            var folded = await dbContext.UserAccounts.Include(x => x.Membership).Include(x => x.PatronSupportEvents)
                .SingleAsync(x => x.Id == merge.FoldedUserAccountId, cancellationToken);
            var survivorKey = AppraisalKey(survivor.Id);
            var restagePatron = false;

            void Record(AccountMergeEntry original, bool restored) =>
                dbContext.AccountMergeEntries.Add(new AccountMergeEntry
                {
                    MergeId = mergeId,
                    Seq = ++seq,
                    Kind = original.Kind,
                    RecordId = original.RecordId,
                    Action = restored ? AccountMergeEntryAction.Restored : AccountMergeEntryAction.KeptAfterUndo,
                    PriorUserAccountId = original.PriorUserAccountId,
                    AtUtc = now
                });

            foreach (var entry in journal.Where(x => x.Action is AccountMergeEntryAction.Voided or AccountMergeEntryAction.Moved or AccountMergeEntryAction.Closed or AccountMergeEntryAction.Suspended or AccountMergeEntryAction.Created))
            {
                switch (entry.Kind, entry.Action)
                {
                    case (AccountMergeEntryKind.Vote, AccountMergeEntryAction.Voided):
                    {
                        var vote = await dbContext.Votes.IgnoreQueryFilters().Include(x => x.Motion).SingleOrDefaultAsync(x => x.Id == entry.RecordId, cancellationToken);
                        var restore = vote?.VoidedByMergeId == mergeId && vote.Motion.Status == MotionStatus.Open;
                        if (restore)
                        {
                            vote!.VoidedByMergeId = null;
                            vote.VoidedUtc = null;
                        }

                        Record(entry, restore);
                        break;
                    }
                    case (AccountMergeEntryKind.Vote, AccountMergeEntryAction.Moved):
                    {
                        var vote = await dbContext.Votes.SingleOrDefaultAsync(x => x.Id == entry.RecordId, cancellationToken);
                        var restore = vote is not null && vote.UserAccountId == survivor.Id && vote.CastAtUtc == entry.PriorAtUtc;
                        if (restore)
                        {
                            vote!.UserAccountId = entry.PriorUserAccountId!.Value;
                            if (decimal.TryParse(entry.PriorText, NumberStyles.Number, CultureInfo.InvariantCulture, out var priorWeight))
                            {
                                vote.Weight = priorWeight;
                            }
                        }

                        Record(entry, restore);
                        break;
                    }
                    case (AccountMergeEntryKind.ConceptAppraisal, AccountMergeEntryAction.Voided):
                    {
                        var row = await dbContext.ConceptAppraisals.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == entry.RecordId, cancellationToken);
                        var restore = row?.VoidedByMergeId == mergeId;
                        if (restore)
                        {
                            row!.VoidedByMergeId = null;
                            row.VoidedUtc = null;
                        }

                        Record(entry, restore);
                        break;
                    }
                    case (AccountMergeEntryKind.ConceptAppraisal, AccountMergeEntryAction.Moved):
                    {
                        var row = await dbContext.ConceptAppraisals.SingleOrDefaultAsync(x => x.Id == entry.RecordId, cancellationToken);
                        var restore = row is not null && row.UserAccountId == survivor.Id && row.VoterKey == survivorKey && row.CastAtUtc == entry.PriorAtUtc;
                        if (restore)
                        {
                            row!.UserAccountId = entry.PriorUserAccountId!.Value;
                            var (priorClass, priorKey) = ParseAppraisalPrior(entry.PriorText);
                            row.VoterKey = priorKey ?? row.VoterKey;
                            row.VoterClass = priorClass ?? row.VoterClass;
                        }

                        Record(entry, restore);
                        break;
                    }
                    case (AccountMergeEntryKind.Membership, AccountMergeEntryAction.Closed):
                    {
                        var restore = folded.Membership is { Status: MembershipStatus.Merged } && Enum.TryParse<MembershipStatus>(entry.PriorText, out _);
                        if (restore)
                        {
                            folded.Membership!.Status = Enum.Parse<MembershipStatus>(entry.PriorText!);
                        }

                        Record(entry, restore);
                        break;
                    }
                    case (AccountMergeEntryKind.Standing, AccountMergeEntryAction.Suspended):
                    {
                        // Only while the survivor still carries exactly this merge's suspension; an admin who changed it since owns the standing now.
                        var restore = survivor.Membership is { Status: MembershipStatus.Suspended } member &&
                            member.SuspensionReason == SuspensionReason(mergeId) &&
                            Enum.TryParse<MembershipStatus>(entry.PriorText, out _);
                        if (restore)
                        {
                            survivor.Membership!.Status = Enum.Parse<MembershipStatus>(entry.PriorText!);
                            survivor.Membership.SuspensionReason = null;
                        }

                        Record(entry, restore);
                        break;
                    }
                    case (AccountMergeEntryKind.Standing, AccountMergeEntryAction.Created):
                    {
                        // The membership this merge made to carry a suspension goes with the merge, unless an admin has taken it over since.
                        var created = await dbContext.Memberships.SingleOrDefaultAsync(x => x.Id == entry.RecordId, cancellationToken);
                        var restore = created is { Status: MembershipStatus.Suspended } && created.SuspensionReason == SuspensionReason(mergeId);
                        if (restore)
                        {
                            dbContext.Memberships.Remove(created!);
                        }

                        Record(entry, restore);
                        break;
                    }
                    case (AccountMergeEntryKind.PatronSupportEvent, AccountMergeEntryAction.Moved):
                    {
                        var row = await dbContext.PatronSupportEvents.SingleOrDefaultAsync(x => x.Id == entry.RecordId, cancellationToken);
                        var restore = row is not null && row.UserAccountId == survivor.Id;
                        if (restore)
                        {
                            row!.UserAccountId = entry.PriorUserAccountId!.Value;
                            restagePatron = true;
                        }

                        Record(entry, restore);
                        break;
                    }
                }
            }

            if (restagePatron)
            {
                await RestageTiersAsync(survivor, folded, now, cancellationToken);
            }

            merge.State = AccountMergeState.Undone;
            merge.UndoneUtc = now;
            await dbContext.SaveChangesAsync(cancellationToken);
            return merge;
        }
        catch
        {
            dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// Committed merges whose grace has ended become Final; returns how many this call finalized. Undo is never allowed past Final.
    /// State is the merge's concurrency token, so a merge another writer moved first (UndoRequested) fails its save and is left to that
    /// transition: a stale Committed read can never overwrite it.
    /// </summary>
    public async Task<int> FinalizeDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var dueIds = await dbContext.AccountMerges.AsNoTracking()
            .Where(x => x.State == AccountMergeState.Committed && x.GraceEndsUtc <= now)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var finalized = 0;
        foreach (var id in dueIds)
        {
            var merge = await dbContext.AccountMerges.SingleAsync(x => x.Id == id, cancellationToken);
            if (merge.State != AccountMergeState.Committed)
            {
                continue;
            }

            merge.State = AccountMergeState.Final;
            merge.FinalizedUtc = now;
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                finalized++;
            }
            catch (DbUpdateConcurrencyException)
            {
                dbContext.ChangeTracker.Clear();
            }
        }

        return finalized;
    }

    /// <summary>
    /// Stages the journal entry for a patron event that is on a folded account's survivor, so undo moves it back with the rest; saved
    /// with the event by <see cref="PatronageService.RecordSupportEventAsync"/>, the only caller. Idempotent: a provider retry stages nothing.
    /// </summary>
    public static async Task StageIntakeJournalAsync(BifrostDbContext db, Guid mergeId, Guid foldedUserAccountId, Guid supportEventId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await db.AccountMergeEntries.AnyAsync(
                x => x.MergeId == mergeId && x.Kind == AccountMergeEntryKind.PatronSupportEvent && x.RecordId == supportEventId,
                cancellationToken))
        {
            return;
        }

        var seq = await db.AccountMergeEntries.Where(x => x.MergeId == mergeId).MaxAsync(x => (int?)x.Seq, cancellationToken) ?? 0;
        db.AccountMergeEntries.Add(Journal(
            mergeId,
            seq + 1,
            new MergePlanEntry(AccountMergeEntryKind.PatronSupportEvent, AccountMergeEntryAction.Moved, supportEventId, foldedUserAccountId),
            now));
    }

    private sealed class Reduction(
        UserAccount survivor,
        UserAccount folded,
        List<MergePlanEntry> entries,
        List<Action<Guid, DateTimeOffset>> effects,
        List<Vote> movedVotes,
        bool movedPatronEvents)
    {
        public UserAccount Survivor { get; } = survivor;

        public UserAccount Folded { get; } = folded;

        public IReadOnlyList<MergePlanEntry> Entries => entries;

        public bool MovedPatronEvents { get; } = movedPatronEvents;

        public Guid MergeId { get; set; }

        /// <summary>A moved vote carries the survivor's weight, derived once from the survivor's tiers as they stand after the restage, never the folded account's.</summary>
        public void ReweighMovedVotes()
        {
            foreach (var vote in movedVotes)
            {
                vote.Weight = Survivor.Membership!.EffectiveVotingWeight;
            }
        }

        public void Apply(DateTimeOffset now)
        {
            foreach (var effect in effects)
            {
                effect(MergeId, now);
            }
        }
    }

    /// <summary>
    /// What a merge does to records, computed from current rows without writing: the journal entries and the effects that apply them.
    /// Rules (design 5): suspension wins; the folded membership is closed and its roles never copied; open standard motions and
    /// concept appraisals keep one voice per person (the survivor's), a lone folded one moving only to an Active survivor; patron
    /// events follow the person.
    /// </summary>
    private async Task<Reduction> ReduceAsync(Guid survivorId, Guid foldedId, CancellationToken cancellationToken, Guid? mergeId = null)
    {
        var survivor = await dbContext.UserAccounts.Include(x => x.Membership!).ThenInclude(x => x.TierSnapshots).Include(x => x.PatronSupportEvents)
            .SingleAsync(x => x.Id == survivorId, cancellationToken);
        var folded = await dbContext.UserAccounts.Include(x => x.Membership!).ThenInclude(x => x.TierSnapshots).Include(x => x.PatronSupportEvents)
            .SingleAsync(x => x.Id == foldedId, cancellationToken);

        var entries = new List<MergePlanEntry>();
        var effects = new List<Action<Guid, DateTimeOffset>>();
        var movedVotes = new List<Vote>();

        var foldedSuspended = folded.Membership?.Status == MembershipStatus.Suspended;
        var survivorSuspendedNow = survivor.Membership?.Status == MembershipStatus.Suspended;
        var survivorStanding = survivor.Membership?.Status;
        if (foldedSuspended && !survivorSuspendedNow)
        {
            if (survivor.Membership is { } survivorMembership)
            {
                entries.Add(new MergePlanEntry(AccountMergeEntryKind.Standing, AccountMergeEntryAction.Suspended, survivorMembership.Id, null, survivorMembership.Status.ToString()));
                effects.Add((id, _) =>
                {
                    survivorMembership.Status = MembershipStatus.Suspended;
                    survivorMembership.SuspensionReason = SuspensionReason(id);
                });
            }
            else
            {
                // The chat tier holds no membership, and an absent one chats: without a row the suspension would be gone. The row is journalled so undo removes it.
                var created = new Bifrost.Web.Domain.Membership { UserAccountId = survivor.Id, Status = MembershipStatus.Suspended };
                entries.Add(new MergePlanEntry(AccountMergeEntryKind.Standing, AccountMergeEntryAction.Created, created.Id));
                effects.Add((id, now) =>
                {
                    created.SuspensionReason = SuspensionReason(id);
                    created.CreatedAtUtc = now;
                    created.Notes = "Created by an account merge to carry the folded account's suspension.";
                    dbContext.Memberships.Add(created);
                });
            }

            survivorStanding = MembershipStatus.Suspended;
        }

        var survivorActive = survivorStanding == MembershipStatus.Active;

        if (folded.Membership is { } foldedMembership)
        {
            entries.Add(new MergePlanEntry(AccountMergeEntryKind.Membership, AccountMergeEntryAction.Closed, foldedMembership.Id, null, foldedMembership.Status.ToString()));
            effects.Add((_, _) => foldedMembership.Status = MembershipStatus.Merged);
        }

        var foldedVotes = await dbContext.Votes
            .Where(x => x.UserAccountId == foldedId && x.Motion.Status == MotionStatus.Open && x.Motion.Kind == MotionKind.Standard)
            .ToListAsync(cancellationToken);
        var survivorVotedMotions = (await dbContext.Votes
            .Where(x => x.UserAccountId == survivorId && x.Motion.Status == MotionStatus.Open && x.Motion.Kind == MotionKind.Standard)
            .Select(x => x.MotionId)
            .ToListAsync(cancellationToken)).ToHashSet();
        foreach (var vote in foldedVotes)
        {
            if (survivorVotedMotions.Contains(vote.MotionId) || !survivorActive)
            {
                entries.Add(new MergePlanEntry(AccountMergeEntryKind.Vote, AccountMergeEntryAction.Voided, vote.Id, vote.UserAccountId));
                effects.Add((id, now) =>
                {
                    vote.VoidedByMergeId = id;
                    vote.VoidedUtc = now;
                });
            }
            else
            {
                entries.Add(new MergePlanEntry(AccountMergeEntryKind.Vote, AccountMergeEntryAction.Moved, vote.Id, vote.UserAccountId, vote.Weight.ToString(CultureInfo.InvariantCulture), vote.CastAtUtc));
                movedVotes.Add(vote);
                effects.Add((_, _) => vote.UserAccountId = survivorId);
            }
        }

        var survivorKey = AppraisalKey(survivorId);
        var foldedAppraisals = await dbContext.ConceptAppraisals.Where(x => x.UserAccountId == foldedId).ToListAsync(cancellationToken);
        var survivorSubjects = (await dbContext.ConceptAppraisals
            .Where(x => x.UserAccountId == survivorId || x.VoterKey == survivorKey)
            .Select(x => x.SubjectId)
            .ToListAsync(cancellationToken)).ToHashSet();
        foreach (var appraisal in foldedAppraisals)
        {
            if (survivorSubjects.Contains(appraisal.SubjectId) || !survivorActive)
            {
                entries.Add(new MergePlanEntry(AccountMergeEntryKind.ConceptAppraisal, AccountMergeEntryAction.Voided, appraisal.Id, appraisal.UserAccountId));
                effects.Add((id, now) =>
                {
                    appraisal.VoidedByMergeId = id;
                    appraisal.VoidedUtc = now;
                });
            }
            else
            {
                entries.Add(new MergePlanEntry(AccountMergeEntryKind.ConceptAppraisal, AccountMergeEntryAction.Moved, appraisal.Id, appraisal.UserAccountId, $"{appraisal.VoterClass}|{appraisal.VoterKey}", appraisal.CastAtUtc));
                effects.Add((_, _) =>
                {
                    appraisal.UserAccountId = survivorId;
                    appraisal.VoterKey = survivorKey;
                    appraisal.VoterClass = VoterClass.Member; // the survivor is Active here, which is what makes an account a Member voter
                });
            }
        }

        var foldedEvents = folded.PatronSupportEvents.ToList();
        foreach (var supportEvent in foldedEvents)
        {
            entries.Add(new MergePlanEntry(AccountMergeEntryKind.PatronSupportEvent, AccountMergeEntryAction.Moved, supportEvent.Id, supportEvent.UserAccountId));
            effects.Add((_, _) => supportEvent.UserAccountId = survivorId);
        }

        var reduction = new Reduction(survivor, folded, entries, effects, movedVotes, foldedEvents.Count > 0);
        if (mergeId is { } m)
        {
            reduction.MergeId = m;
        }

        return reduction;
    }

    // Tiers derive once from all the events now on each account, never tier A plus tier B. Ownership is read from the
    // tracked rows, so it holds before the save as well as after it.
    private async Task RestageTiersAsync(UserAccount survivor, UserAccount folded, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var events = survivor.PatronSupportEvents.Concat(folded.PatronSupportEvents).DistinctBy(x => x.Id).ToList();
        await StageIfMembershipAsync(survivor.Membership, events.Where(x => x.UserAccountId == survivor.Id).ToList(), now, cancellationToken);
        await StageIfMembershipAsync(folded.Membership, events.Where(x => x.UserAccountId == folded.Id).ToList(), now, cancellationToken);
    }

    private async Task StageIfMembershipAsync(Bifrost.Web.Domain.Membership? membership, IEnumerable<PatronSupportEvent> events, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (membership is not null && dbContext.Entry(membership).State != EntityState.Deleted)
        {
            await patronageService.StageTierSnapshotAsync(membership, events, now, cancellationToken);
        }
    }

    private static AccountMergeEntry Journal(Guid mergeId, int seq, MergePlanEntry entry, DateTimeOffset now) => new()
    {
        MergeId = mergeId,
        Seq = seq,
        Kind = entry.Kind,
        RecordId = entry.RecordId,
        Action = entry.Action,
        PriorUserAccountId = entry.PriorUserAccountId,
        PriorText = entry.PriorText,
        PriorAtUtc = entry.PriorAtUtc,
        AtUtc = now
    };

    private static string AppraisalKey(Guid accountId) => "account:" + accountId.ToString("N");

    private static (VoterClass? Class, string? Key) ParseAppraisalPrior(string? prior)
    {
        var split = prior?.IndexOf('|') ?? -1;
        return split > 0 && Enum.TryParse<VoterClass>(prior![..split], out var voterClass) ? (voterClass, prior[(split + 1)..]) : (null, null);
    }
}

/// <summary>
/// The one merge loop, every 30 seconds: the saga advances every due in-flight merge (confirm, replay, undo), then the ledger finalizes
/// merges past their grace. A new scope per tick, a warning on failure, the SealedBallotCloser shape. No second merge hosted service exists.
/// </summary>
public sealed class AccountMergeSweeper(IServiceScopeFactory scopes, TimeProvider clock, ILogger<AccountMergeSweeper> logger) : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var now = clock.GetUtcNow();
                await scope.ServiceProvider.GetRequiredService<AccountMergeSaga>().AdvanceDueAsync(now, stoppingToken);
                await scope.ServiceProvider.GetRequiredService<AccountMergeLedger>().FinalizeDueAsync(now, stoppingToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning(error, "Account merge sweep failed; retrying next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
