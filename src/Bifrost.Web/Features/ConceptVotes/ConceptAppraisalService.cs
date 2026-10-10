using System.Net;
using Bifrost.Web.Configuration;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
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

        var existing = await FindAppraisalAsync(subject.Id, voter.Key, cancellationToken);
        if (existing is not null)
        {
            return await ApplyStanceAsync(existing, stance, tag, cancellationToken);
        }

        var row = new ConceptAppraisal
        {
            SubjectId = subject.Id,
            VoterKey = voter.Key,
            Stance = stance,
            VoterClass = voter.Class,
            UserAccountId = voter.UserAccountId,
            NetworkTag = tag,
            CastAtUtc = timeProvider.GetUtcNow()
        };
        dbContext.ConceptAppraisals.Add(row);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return CastResult.Recorded;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            // A concurrent cast by the same voter won the insert: apply this stance to their row, once.
            dbContext.Entry(row).State = EntityState.Detached;
            var winner = await FindAppraisalAsync(subject.Id, voter.Key, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return await ApplyStanceAsync(winner, stance, tag, cancellationToken);
        }
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
