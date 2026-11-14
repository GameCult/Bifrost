using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Web.Features.Membership;

/// <summary>What the chat host is told about one merge. Derived from the merge row on every read; never stored.</summary>
public sealed record MergeNotice(
    AccountMergeNoticeKind Kind,
    Guid MergeId,
    DateTimeOffset OccurredAt,
    DateTimeOffset? GraceEndsAt,
    Guid OldSubject,
    Guid NewSubject,
    string OldPreferredUsername,
    string NewPreferredUsername);

/// <summary>
/// The one owner of what a notice reader is told about merges: merged (CommittedUtc), undone (UndoneUtc) and final (FinalizedUtc) are read off
/// the AccountMerges row on every call, so a notice cannot disagree with the merge, and the reader's ack row is the only thing stored.
/// Reads only AccountMerges and UserAccounts; writes only AccountMergeNoticeAck.
/// </summary>
public sealed class MergeNoticeFeed(BifrostDbContext dbContext, TimeProvider timeProvider)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    private static readonly AccountMergeNoticeKind[] Kinds = [AccountMergeNoticeKind.Merged, AccountMergeNoticeKind.Undone, AccountMergeNoticeKind.Final];

    private static DateTimeOffset? OccurredAt(AccountMerge merge, AccountMergeNoticeKind kind) => kind switch
    {
        AccountMergeNoticeKind.Merged => merge.CommittedUtc,
        AccountMergeNoticeKind.Undone => merge.UndoneUtc,
        _ => merge.FinalizedUtc
    };

    /// <summary>Unacked notices for <paramref name="target"/>, oldest first by (occurred_at, merge_id, kind), strictly later than <paramref name="after"/> when given. Acks are the cursor; <c>after</c> only narrows the scan.</summary>
    public async Task<IReadOnlyList<MergeNotice>> ReadAsync(AccountMergeNoticeTarget target, DateTimeOffset? after, int? limit, CancellationToken cancellationToken)
    {
        var take = limit is > 0 ? Math.Min(limit.Value, MaxLimit) : DefaultLimit;
        var picked = new List<(AccountMerge Merge, AccountMergeNoticeKind Kind, DateTimeOffset At)>();
        // Each kind's own oldest `take` rows contain every row of the overall oldest `take`, so merging three bounded lists is exact.
        foreach (var kind in Kinds)
        {
            var rows = await dbContext.AccountMerges.AsNoTracking()
                .Where(x => (kind == AccountMergeNoticeKind.Merged ? x.CommittedUtc : kind == AccountMergeNoticeKind.Undone ? x.UndoneUtc : x.FinalizedUtc) != null)
                .Where(x => after == null || (kind == AccountMergeNoticeKind.Merged ? x.CommittedUtc : kind == AccountMergeNoticeKind.Undone ? x.UndoneUtc : x.FinalizedUtc) > after)
                .Where(x => !dbContext.AccountMergeNoticeAcks.Any(a => a.MergeId == x.Id && a.Kind == kind && a.Target == target))
                .OrderBy(x => kind == AccountMergeNoticeKind.Merged ? x.CommittedUtc : kind == AccountMergeNoticeKind.Undone ? x.UndoneUtc : x.FinalizedUtc)
                .ThenBy(x => x.Id)
                .Take(take)
                .ToListAsync(cancellationToken);
            picked.AddRange(rows.Select(x => (x, kind, OccurredAt(x, kind)!.Value)));
        }

        var page = picked.OrderBy(x => x.At).ThenBy(x => x.Merge.Id).ThenBy(x => x.Kind).Take(take).ToList();
        var ids = page.SelectMany(x => new[] { x.Merge.FoldedUserAccountId, x.Merge.SurvivorUserAccountId }).Distinct().ToList();
        var names = await dbContext.UserAccounts.AsNoTracking().Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, x.NormalizedBifrostIdentity }).ToDictionaryAsync(x => x.Id, x => x.NormalizedBifrostIdentity, cancellationToken);
        return [.. page.Select(x => new MergeNotice(
            x.Kind, x.Merge.Id, x.At, x.Merge.GraceEndsUtc, x.Merge.FoldedUserAccountId, x.Merge.SurvivorUserAccountId,
            names.GetValueOrDefault(x.Merge.FoldedUserAccountId, string.Empty), names.GetValueOrDefault(x.Merge.SurvivorUserAccountId, string.Empty)))];
    }

    /// <summary>Records that the reader has seen (merge, kind). False when the feed would not emit that notice now (no such merge, or its timestamp unset); true otherwise, a repeat included.</summary>
    public async Task<bool> AckAsync(AccountMergeNoticeTarget target, Guid mergeId, AccountMergeNoticeKind kind, CancellationToken cancellationToken)
    {
        var merge = await dbContext.AccountMerges.AsNoTracking().SingleOrDefaultAsync(x => x.Id == mergeId, cancellationToken);
        if (merge is null || OccurredAt(merge, kind) is null)
        {
            return false;
        }

        if (await dbContext.AccountMergeNoticeAcks.AnyAsync(x => x.MergeId == mergeId && x.Kind == kind && x.Target == target, cancellationToken))
        {
            return true;
        }

        dbContext.AccountMergeNoticeAcks.Add(new AccountMergeNoticeAck { MergeId = mergeId, Kind = kind, Target = target, AckedUtc = timeProvider.GetUtcNow() });
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent ack of the same key won the unique index; the notice is acked, which is what the caller asked for.
            dbContext.ChangeTracker.Clear();
            if (!await dbContext.AccountMergeNoticeAcks.AnyAsync(x => x.MergeId == mergeId && x.Kind == kind && x.Target == target, cancellationToken))
            {
                throw;
            }
        }

        return true;
    }
}
