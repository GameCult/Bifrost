using System.Net;
using Bifrost.Web.Configuration;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Features.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Bifrost.Web.Features.ConceptVotes;

public enum CastResult
{
    Recorded,
    Changed,
    Unchanged,
    UnknownSubject,
    RetiredSubject,
    NoVoter
}

public sealed record StanceCounts(int Up, int Down);

public sealed record ConceptTally(int Up, int Down, StanceCounts Members);

public enum RankOrder
{
    Net,
    Total,
    Newest
}

// One row of the ranked read: counts and the subject's title only, never a voter key, account id or network tag.
public sealed record RankedSubject(string Key, string Title, ConceptTally Tally);

public sealed record RankedPage(IReadOnlyList<RankedSubject> Items, string? NextCursor);

public sealed record ConceptSubjectView(
    string Collection,
    string Key,
    string Title,
    ConceptTally Tally,
    ConceptStance? YourStance);

// The only writer of ConceptAppraisal rows. Tallies are derived from rows on every read.
public sealed class ConceptAppraisalService(
    BifrostDbContext dbContext,
    AuditTrailService auditTrail,
    TimeProvider timeProvider,
    IOptions<ConceptVoteOptions> options)
{
    private const string UniqueViolation = "23505";

    public async Task<CastResult> CastAsync(
        string collection,
        string key,
        VoterIdentity? voter,
        ConceptStance stance,
        IPAddress? client,
        CancellationToken cancellationToken)
    {
        if (voter is null)
        {
            return CastResult.NoVoter;
        }

        var subject = await FindSubjectAsync(collection, key, cancellationToken);
        if (subject is null)
        {
            return CastResult.UnknownSubject;
        }

        if (subject.RetiredAtUtc is not null)
        {
            return CastResult.RetiredSubject;
        }

        var opts = options.Value;
        var tag = client is null
            ? string.Empty
            : NetworkPrefix.Tag(client, opts.IPv4PrefixLength, opts.IPv6PrefixLength, opts.NetworkTagSecret);

        // A voter resolved before a merge froze the account must not write an appraisal after it: the write gate reads standing at the write.
        Task<CastResult> Write() => voter.UserAccountId is { } voterAccountId
            ? AccountMergeLedger.WriteAsAsync(
                dbContext,
                voterAccountId,
                standing => standing is null ? WriteAppraisalAsync(subject, voter, stance, tag, cancellationToken) : Task.FromResult(CastResult.NoVoter),
                cancellationToken)
            : WriteAppraisalAsync(subject, voter, stance, tag, cancellationToken);

        try
        {
            return await Write();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            // A concurrent cast by the same voter won the insert (and the failed save aborted the gate's transaction): go through the
            // gate again, which now finds their row and applies this stance to it, once.
            dbContext.ChangeTracker.Clear();
            if (await FindAppraisalAsync(subject.Id, voter.Key, cancellationToken) is null)
            {
                throw;
            }

            return await Write();
        }
    }

    private async Task<CastResult> WriteAppraisalAsync(ConceptSubject subject, VoterIdentity voter, ConceptStance stance, string tag, CancellationToken cancellationToken)
    {
        var existing = await FindAppraisalAsync(subject.Id, voter.Key, cancellationToken);
        if (existing is not null)
        {
            return await ApplyStanceAsync(existing, stance, tag, cancellationToken);
        }

        dbContext.ConceptAppraisals.Add(new ConceptAppraisal
        {
            SubjectId = subject.Id,
            VoterKey = voter.Key,
            Stance = stance,
            VoterClass = voter.Class,
            UserAccountId = voter.UserAccountId,
            NetworkTag = tag,
            CastAtUtc = timeProvider.GetUtcNow()
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return CastResult.Recorded;
    }

    public async Task<bool> RetractAsync(
        string collection,
        string key,
        VoterIdentity? voter,
        CancellationToken cancellationToken)
    {
        if (voter is null)
        {
            return false;
        }

        var subject = await FindSubjectAsync(collection, key, cancellationToken);
        if (subject is null)
        {
            return false;
        }

        var row = await FindAppraisalAsync(subject.Id, voter.Key, cancellationToken);
        if (row is null)
        {
            return false;
        }

        dbContext.ConceptAppraisals.Remove(row);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<int> PurgeAsync(
        CurrentBifrostActor actor,
        string networkTag,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        if (!actor.CanManageMembership || actor.UserAccount is null)
        {
            throw new UnauthorizedAccessException("Only a platform administrator can purge appraisals.");
        }

        if (string.IsNullOrEmpty(networkTag))
        {
            return 0;
        }

        var rows = await dbContext.ConceptAppraisals
            .Where(x => x.NetworkTag == networkTag && x.CastAtUtc >= fromUtc && x.CastAtUtc < toUtc)
            .ToListAsync(cancellationToken);
        dbContext.ConceptAppraisals.RemoveRange(rows);

        // The audit write saves the removals in the same SaveChanges. The member dashboard renders Detail, so it
        // carries the window and count only: no network tag and no voter keys. The actor and window audit the purge.
        await auditTrail.RecordAsync(
            actor.UserAccount.Id,
            nameof(ConceptAppraisal),
            Guid.Empty,
            "Purge",
            $"from={fromUtc:O} to={toUtc:O} count={rows.Count}",
            cancellationToken);
        return rows.Count;
    }

    public async Task<IReadOnlyDictionary<string, ConceptTally>> GetTalliesAsync(
        string collection,
        CancellationToken cancellationToken)
    {
        var live = await dbContext.ConceptSubjects.AsNoTracking()
            .Where(x => x.Collection == collection && x.RetiredAtUtc == null)
            .Select(x => new { x.Id, x.Key })
            .ToListAsync(cancellationToken);
        var ids = live.Select(x => x.Id).ToList();

        var groups = await dbContext.ConceptAppraisals.AsNoTracking()
            .Where(x => ids.Contains(x.SubjectId))
            .GroupBy(x => new { x.SubjectId, x.Stance, IsMember = x.VoterClass == VoterClass.Member })
            .Select(g => new { g.Key.SubjectId, g.Key.Stance, g.Key.IsMember, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return live.ToDictionary(
            s => s.Key,
            s => Tally(groups.Where(g => g.SubjectId == s.Id).Select(g => (g.Stance, g.IsMember, g.Count))),
            StringComparer.Ordinal);
    }

    // The ranked read: live subjects of one collection ordered by net (up minus down), total (up plus down) or
    // registration time, largest first, ties by key ascending, so the order is total and a cursor names a position.
    // The cursor is keyset: the next page is whatever sorts after the last row's (value, key), so a vote landing
    // between pages can move rows but never makes a page repeat the cursor's own row. With unvoted and a client
    // address, subjects whose stored tag equals the client's are left out (no address filters nothing) ("not yet voted by this network"); the tag is only compared
    // inside the query and is never returned. Null when the collection has no live subject.
    public async Task<RankedPage?> GetRankedAsync(
        string collection,
        RankOrder order,
        int limit,
        RankCursor? after,
        bool unvoted,
        IPAddress? client,
        CancellationToken cancellationToken)
    {
        var live = await dbContext.ConceptSubjects.AsNoTracking()
            .Where(x => x.Collection == collection && x.RetiredAtUtc == null)
            .Select(x => new { x.Id, x.Key, x.Title, x.RegisteredAtUtc })
            .ToListAsync(cancellationToken);
        if (live.Count == 0)
        {
            return null;
        }

        var ids = live.Select(x => x.Id).ToList();
        var groups = await dbContext.ConceptAppraisals.AsNoTracking()
            .Where(x => ids.Contains(x.SubjectId))
            .GroupBy(x => new { x.SubjectId, x.Stance, IsMember = x.VoterClass == VoterClass.Member })
            .Select(g => new { g.Key.SubjectId, g.Key.Stance, g.Key.IsMember, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var voted = new HashSet<Guid>();
        if (unvoted && client is not null)
        {
            var opts = options.Value;
            var tag = NetworkPrefix.Tag(client, opts.IPv4PrefixLength, opts.IPv6PrefixLength, opts.NetworkTagSecret);
            voted = (await dbContext.ConceptAppraisals.AsNoTracking()
                .Where(x => ids.Contains(x.SubjectId) && x.NetworkTag == tag)
                .Select(x => x.SubjectId)
                .ToListAsync(cancellationToken)).ToHashSet();
        }

        var rows = live
            .Where(s => !voted.Contains(s.Id))
            .Select(s =>
            {
                var tally = Tally(groups.Where(g => g.SubjectId == s.Id).Select(g => (g.Stance, g.IsMember, g.Count)));
                var value = order switch
                {
                    RankOrder.Net => (long)tally.Up - tally.Down,
                    RankOrder.Total => (long)tally.Up + tally.Down,
                    _ => s.RegisteredAtUtc.UtcTicks
                };
                return (Value: value, s.Key, s.Title, Tally: tally);
            })
            .Where(r => after is null || r.Value < after.Value || (r.Value == after.Value && string.CompareOrdinal(r.Key, after.Key) > 0))
            .OrderByDescending(r => r.Value)
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .Take(limit + 1)
            .ToList();

        var page = rows.Take(limit).ToList();
        var next = rows.Count > limit
            ? new RankCursor(order, unvoted, page[^1].Value, page[^1].Key).Encode()
            : null;
        return new RankedPage(page.Select(r => new RankedSubject(r.Key, r.Title, r.Tally)).ToList(), next);
    }

    public async Task<ConceptSubjectView?> GetSubjectAsync(
        string collection,
        string key,
        VoterIdentity? voter,
        CancellationToken cancellationToken)
    {
        var subject = await FindSubjectAsync(collection, key, cancellationToken);
        if (subject is null || subject.RetiredAtUtc is not null)
        {
            return null;
        }

        var groups = await dbContext.ConceptAppraisals.AsNoTracking()
            .Where(x => x.SubjectId == subject.Id)
            .GroupBy(x => new { x.Stance, IsMember = x.VoterClass == VoterClass.Member })
            .Select(g => new { g.Key.Stance, g.Key.IsMember, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var yours = voter is null
            ? null
            : (await FindAppraisalAsync(subject.Id, voter.Key, cancellationToken))?.Stance;

        return new ConceptSubjectView(
            subject.Collection,
            subject.Key,
            subject.Title,
            Tally(groups.Select(g => (g.Stance, g.IsMember, g.Count))),
            yours);
    }

    private static ConceptTally Tally(IEnumerable<(ConceptStance Stance, bool IsMember, int Count)> groups)
    {
        int up = 0, down = 0, memberUp = 0, memberDown = 0;
        foreach (var (stance, isMember, count) in groups)
        {
            if (stance == ConceptStance.Up)
            {
                up += count;
                memberUp += isMember ? count : 0;
            }
            else
            {
                down += count;
                memberDown += isMember ? count : 0;
            }
        }

        return new ConceptTally(up, down, new StanceCounts(memberUp, memberDown));
    }

    private async Task<CastResult> ApplyStanceAsync(
        ConceptAppraisal row,
        ConceptStance stance,
        string tag,
        CancellationToken cancellationToken)
    {
        if (row.Stance == stance)
        {
            return CastResult.Unchanged;
        }

        row.Stance = stance;
        row.NetworkTag = tag;
        row.CastAtUtc = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);
        return CastResult.Changed;
    }

    private Task<ConceptSubject?> FindSubjectAsync(string collection, string key, CancellationToken cancellationToken) =>
        ConceptKey.IsValid(collection) && ConceptKey.IsValid(key)
            ? dbContext.ConceptSubjects
                .SingleOrDefaultAsync(x => x.Collection == collection && x.Key == key, cancellationToken)
            : Task.FromResult<ConceptSubject?>(null);

    private Task<ConceptAppraisal?> FindAppraisalAsync(Guid subjectId, string voterKey, CancellationToken cancellationToken) =>
        dbContext.ConceptAppraisals
            .SingleOrDefaultAsync(x => x.SubjectId == subjectId && x.VoterKey == voterKey, cancellationToken);
}
