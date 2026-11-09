using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bifrost.Web.Features.Motions;

/// <summary>
/// Sealed-ballot motions: commit, then reveal, then one atomic publication at close.
/// This is the only code that reads <see cref="SealedBallot.RevealedCandidate"/>, <see cref="SealedBallot.RevealedReason"/>
/// or <see cref="SealedBallot.Salt"/>; a test fails if any other source file mentions them.
/// </summary>
public sealed class SealedBallotService(
    BifrostDbContext dbContext,
    AuditTrailService auditTrailService,
    TimeProvider timeProvider)
{
    public const int MaxReasonLength = 280;
    private static readonly Regex HexSha256 = new("^[0-9a-f]{64}$", RegexOptions.Compiled);
    private static readonly Regex HexSalt = new("^[0-9a-f]{32,128}$", RegexOptions.Compiled);

    public async Task<SealedMotionState> OpenAsync(
        CurrentBifrostActor actor,
        OpenSealedMotionCommand command,
        CancellationToken cancellationToken)
    {
        var user = RequireMember(actor);
        if (!actor.CanModerateMotions)
        {
            throw new SealedBallotException(SealedBallotFailure.Forbidden, "Only a moderator opens a sealed-ballot motion.");
        }

        var now = timeProvider.GetUtcNow();
        var candidates = command.Candidates.Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        if (candidates.Count < 2 || candidates.Distinct(StringComparer.Ordinal).Count() != candidates.Count ||
            candidates.Any(x => x.Contains('\n') || x.Contains('\r')))
        {
            throw new SealedBallotException(SealedBallotFailure.Invalid, "Two or more distinct single-line candidates are required.");
        }

        var commitCloses = command.CommitClosesAtUtc.ToUniversalTime();
        var closes = command.ClosesAtUtc.ToUniversalTime();
        if (commitCloses <= now || closes <= commitCloses)
        {
            throw new SealedBallotException(SealedBallotFailure.Invalid, "The commit window must end in the future and the reveal window must end after it.");
        }

        var motion = new Motion
        {
            CreatedByUserAccountId = user.Id,
            Scope = MotionScope.Management,
            Category = MotionCategory.Features,
            Kind = MotionKind.SealedBallot,
            Title = command.Title.Trim(),
            Summary = command.Summary.Trim(),
            Candidates = string.Join('\n', candidates),
            Round = Math.Max(1, command.Round),
            PriorMotionId = command.PriorMotionId,
            ApprovalThreshold = 0m,
            OpensAtUtc = now,
            CommitClosesAtUtc = commitCloses,
            ClosesAtUtc = closes,
            CreatedAtUtc = now
        };

        dbContext.Motions.Add(motion);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            user.Id, nameof(Motion), motion.Id, "sealed.opened",
            $"Opened sealed-ballot motion {motion.Title} (round {motion.Round}, {candidates.Count} candidates).",
            cancellationToken);

        return BuildState(motion, now, null, user.Id);
    }

    public async Task<SealedBallotReceipt> CommitAsync(
        CurrentBifrostActor actor,
        Guid motionId,
        string commitment,
        CancellationToken cancellationToken)
    {
        var user = RequireMember(actor);
        var motion = await LoadAsync(motionId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (PhaseOf(motion, now) != SealedPhase.Commit)
        {
            throw new SealedBallotException(SealedBallotFailure.WrongPhase, "The commit window is not open.");
        }

        commitment = (commitment ?? string.Empty).Trim().ToLowerInvariant();
        if (!HexSha256.IsMatch(commitment))
        {
            throw new SealedBallotException(SealedBallotFailure.Invalid, "A commitment is 64 lowercase hex characters (SHA-256).");
        }

        var ballot = await dbContext.SealedBallots
            .SingleOrDefaultAsync(x => x.MotionId == motionId && x.UserAccountId == user.Id, cancellationToken);
        if (ballot is null)
        {
            ballot = new SealedBallot { MotionId = motionId, UserAccountId = user.Id };
            dbContext.SealedBallots.Add(ballot);
        }
        else
        {
            ballot.Revision++;
        }

        ballot.Commitment = commitment;
        ballot.CommittedAtUtc = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            user.Id, nameof(Motion), motionId, "sealed.committed",
            $"{actor.DisplayName} committed a sealed ballot (revision {ballot.Revision}), commitment {commitment}.",
            cancellationToken);

        return new SealedBallotReceipt(motionId, user.Id, commitment, ballot.Revision, now, Revealed: false);
    }

    public async Task<SealedBallotReceipt> RevealAsync(
        CurrentBifrostActor actor,
        Guid motionId,
        SealedBallotDocument document,
        CancellationToken cancellationToken)
    {
        var user = RequireMember(actor);
        var motion = await LoadAsync(motionId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (PhaseOf(motion, now) != SealedPhase.Reveal)
        {
            throw new SealedBallotException(SealedBallotFailure.WrongPhase, "The reveal window is not open.");
        }

        var ballot = await dbContext.SealedBallots
            .SingleOrDefaultAsync(x => x.MotionId == motionId && x.UserAccountId == user.Id, cancellationToken)
            ?? throw new SealedBallotException(SealedBallotFailure.NotFound, "No commitment from this member on this motion.");

        var reason = (document.Reason ?? string.Empty).Trim();
        var candidate = (document.Candidate ?? string.Empty).Trim();
        if (document.Schema != SealedBallotDocument.SchemaId || document.MotionId != motionId || document.Round != motion.Round ||
            !CandidatesOf(motion).Contains(candidate, StringComparer.Ordinal) ||
            reason.Length is 0 or > MaxReasonLength || reason.Contains('\n') || reason.Contains('\r') ||
            !HexSalt.IsMatch(document.Salt ?? string.Empty))
        {
            throw new SealedBallotException(SealedBallotFailure.Invalid, "The ballot document does not fit this motion (schema, motion, round, candidate, one-line reason, 128-bit hex salt).");
        }

        var recomputed = SealedBallotDocument.Commit(document with { Candidate = candidate, Reason = reason }, user.Id);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(recomputed), Encoding.ASCII.GetBytes(ballot.Commitment)))
        {
            throw new SealedBallotException(SealedBallotFailure.Invalid, "The ballot does not match the stored commitment.");
        }

        ballot.RevealedCandidate = candidate;
        ballot.RevealedReason = reason;
        ballot.Salt = document.Salt;
        ballot.RevealedAtUtc = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            user.Id, nameof(Motion), motionId, "sealed.revealed",
            $"{actor.DisplayName} submitted a reveal for commitment {ballot.Commitment}; content stays sealed until close.",
            cancellationToken);

        return new SealedBallotReceipt(motionId, user.Id, ballot.Commitment, ballot.Revision, ballot.CommittedAtUtc, Revealed: true);
    }

    /// <summary>Phase, times and the caller's own commitment status. Never another member's ballot, never revealed content.</summary>
    public async Task<SealedMotionState> GetStateAsync(
        CurrentBifrostActor actor,
        Guid motionId,
        CancellationToken cancellationToken)
    {
        var user = RequireMember(actor);
        var motion = await LoadAsync(motionId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var own = await dbContext.SealedBallots
            .AsNoTracking()
            .Where(x => x.MotionId == motionId && x.UserAccountId == user.Id)
            .Select(x => new SealedBallotReceipt(x.MotionId, x.UserAccountId, x.Commitment, x.Revision, x.CommittedAtUtc, x.RevealedAtUtc != null))
            .SingleOrDefaultAsync(cancellationToken);

        return BuildState(motion, now, own, user.Id);
    }

    /// <summary>Finalizes the motion if its deadline has passed, then publishes every ballot at once. Before close it refuses.</summary>
    public async Task<SealedMotionResult> GetResultAsync(
        CurrentBifrostActor actor,
        Guid motionId,
        CancellationToken cancellationToken)
    {
        RequireMember(actor);
        var motion = await LoadAsync(motionId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (PhaseOf(motion, now) != SealedPhase.Closed)
        {
            throw new SealedBallotException(SealedBallotFailure.WrongPhase, "Ballots stay sealed until the motion closes.");
        }

        var ballots = await dbContext.SealedBallots
            .Include(x => x.UserAccount).ThenInclude(x => x.MemberProfile)
            .Where(x => x.MotionId == motionId)
            .ToListAsync(cancellationToken);

        if (motion.Status == MotionStatus.Open)
        {
            await FinalizeAsync(motion, ballots, now, cancellationToken);
        }

        var revealed = ballots.Where(x => x.RevealedAtUtc != null).OrderBy(x => x.Id).ToList();
        var tally = CandidatesOf(motion)
            .Select(c => new SealedTally(c, revealed.Count(x => x.RevealedCandidate == c)))
            .ToList();
        var top = tally.Max(x => x.Votes);
        var leaders = top == 0 ? [] : tally.Where(x => x.Votes == top).Select(x => x.Candidate).ToList();

        var voterIds = ballots.Select(x => x.UserAccountId).ToHashSet();
        var nonVoters = await dbContext.Memberships
            .AsNoTracking()
            .Where(x => x.Status == MembershipStatus.Active && !voterIds.Contains(x.UserAccountId))
            .Select(x => x.UserAccount.MemberProfile != null ? x.UserAccount.MemberProfile.Nickname : x.UserAccount.DisplayName)
            .ToListAsync(cancellationToken);

        return new SealedMotionResult(
            motion.Id,
            motion.Title,
            motion.Round,
            motion.PriorMotionId,
            SealedBallotDocument.SchemaId,
            tally,
            leaders,
            leaders.Count > 1 ? "tie" : leaders.Count == 1 ? "winner" : "no ballots",
            revealed.Select(x => new RevealedBallot(
                NameOf(x.UserAccount), x.UserAccountId, x.RevealedCandidate!, x.RevealedReason!, x.Salt!, x.Commitment)).ToList(),
            ballots.Where(x => x.RevealedAtUtc == null)
                .Select(x => new AbstainedMember(NameOf(x.UserAccount), "committed but did not reveal", x.Commitment)).ToList(),
            nonVoters.Select(n => new AbstainedMember(n, "no commitment", null)).ToList(),
            motion.ResultDigest);
    }

    public static SealedPhase PhaseOf(Motion motion, DateTimeOffset now) =>
        motion.Status != MotionStatus.Open || now >= motion.ClosesAtUtc ? SealedPhase.Closed
        : now >= motion.CommitClosesAtUtc ? SealedPhase.Reveal
        : SealedPhase.Commit;

    /// <summary>
    /// Closes the motion once. Motion.Status is a concurrency token, so of two racing closers exactly one SaveChanges wins;
    /// the loser reloads the winner's result and writes nothing (no second audit event).
    /// </summary>
    private async Task FinalizeAsync(Motion motion, List<SealedBallot> ballots, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var lines = ballots.OrderBy(x => x.UserAccountId)
            .Select(x => $"{x.UserAccountId:D}:{x.Commitment}:{(x.RevealedAtUtc != null ? "revealed" : "unrevealed")}");
        motion.ResultDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines)))).ToLowerInvariant();
        motion.Status = MotionStatus.Closed;
        motion.ResolvedAtUtc = now;
        motion.ResolutionNote = $"Sealed ballots published: {ballots.Count(x => x.RevealedAtUtc != null)} revealed, {ballots.Count(x => x.RevealedAtUtc == null)} committed but unrevealed.";
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException error)
        {
            foreach (var entry in error.Entries)
            {
                await entry.ReloadAsync(cancellationToken);
            }

            return;
        }

        await auditTrailService.RecordAsync(
            null, nameof(Motion), motion.Id, "sealed.closed",
            $"Closed sealed-ballot motion {motion.Title}; commitment digest {motion.ResultDigest}.",
            cancellationToken);
    }

    /// <summary>Closes every sealed-ballot motion whose deadline has passed, so a motion nobody reads still closes.</summary>
    public async Task<int> FinalizeDueAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var due = await dbContext.Motions
            .Where(x => x.Kind == MotionKind.SealedBallot && x.Status == MotionStatus.Open && x.ClosesAtUtc <= now)
            .ToListAsync(cancellationToken);
        foreach (var motion in due)
        {
            var ballots = await dbContext.SealedBallots.Where(x => x.MotionId == motion.Id).ToListAsync(cancellationToken);
            await FinalizeAsync(motion, ballots, now, cancellationToken);
        }

        return due.Count;
    }

    private SealedMotionState BuildState(Motion motion, DateTimeOffset now, SealedBallotReceipt? own, Guid voterId)
    {
        var phase = PhaseOf(motion, now);
        var announcement = phase switch
        {
            SealedPhase.Commit => $"Sealed ballot open: {motion.Title}. Commit by {motion.CommitClosesAtUtc:u}; reveal until {motion.ClosesAtUtc:u}.",
            SealedPhase.Reveal => $"Sealed ballot in reveal: {motion.Title}. Reveal by {motion.ClosesAtUtc:u}.",
            _ => $"Sealed ballot closed: {motion.Title}."
        };
        return new SealedMotionState(
            motion.Id, motion.Title, motion.Summary, motion.Round, motion.PriorMotionId, SealedBallotDocument.SchemaId,
            phase.ToString(), CandidatesOf(motion), motion.CommitClosesAtUtc!.Value, motion.ClosesAtUtc, voterId, own, announcement);
    }

    private async Task<Motion> LoadAsync(Guid motionId, CancellationToken cancellationToken) =>
        await dbContext.Motions.SingleOrDefaultAsync(x => x.Id == motionId && x.Kind == MotionKind.SealedBallot, cancellationToken)
        ?? throw new SealedBallotException(SealedBallotFailure.NotFound, "No such sealed-ballot motion.");

    private static IReadOnlyList<string> CandidatesOf(Motion motion) => motion.Candidates.Split('\n');

    private static string NameOf(UserAccount user) => user.MemberProfile?.Nickname ?? user.DisplayName;

    private static UserAccount RequireMember(CurrentBifrostActor actor) =>
        actor is { IsActiveMember: true, UserAccount: { } user }
            ? user
            : throw new SealedBallotException(SealedBallotFailure.Forbidden, "An active member is required.");
}

public enum SealedPhase { Commit, Reveal, Closed }

public enum SealedBallotFailure { Forbidden, NotFound, WrongPhase, Invalid }

public sealed class SealedBallotException(SealedBallotFailure failure, string message) : Exception(message)
{
    public SealedBallotFailure Failure { get; } = failure;
}

/// <summary>
/// The typed ballot document, schema <c>gamecult.sealed_ballot.v0</c>. A Delphi second round is another motion carrying this
/// same type with a higher Round. The commitment binds the voter's account id, so a commitment cannot be replayed by another member.
/// </summary>
public sealed record SealedBallotDocument(string Schema, Guid MotionId, int Round, string Candidate, string Reason, string Salt)
{
    public const string SchemaId = "gamecult.sealed_ballot.v0";

    public static string Commit(SealedBallotDocument d, Guid voterUserAccountId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{SchemaId}\n{d.MotionId:D}\n{voterUserAccountId:D}\n{d.Round}\n{d.Candidate}\n{d.Reason}\n{d.Salt}"))).ToLowerInvariant();
}

public sealed record OpenSealedMotionCommand(
    string Title,
    string Summary,
    IReadOnlyList<string> Candidates,
    DateTimeOffset CommitClosesAtUtc,
    DateTimeOffset ClosesAtUtc,
    int Round = 1,
    Guid? PriorMotionId = null);

public sealed record SealedBallotReceipt(Guid MotionId, Guid VoterUserAccountId, string Commitment, int Revision, DateTimeOffset CommittedAtUtc, bool Revealed);

public sealed record SealedMotionState(
    Guid Id, string Title, string Summary, int Round, Guid? PriorMotionId, string BallotSchema, string Phase,
    IReadOnlyList<string> Candidates, DateTimeOffset CommitClosesAtUtc, DateTimeOffset ClosesAtUtc,
    Guid YourVoterId, SealedBallotReceipt? YourCommitment, string Announcement);

public sealed record SealedTally(string Candidate, int Votes);

public sealed record RevealedBallot(string Voter, Guid VoterUserAccountId, string Candidate, string Reason, string Salt, string Commitment);

public sealed record AbstainedMember(string Member, string Why, string? Commitment);

public sealed record SealedMotionResult(
    Guid Id, string Title, int Round, Guid? PriorMotionId, string BallotSchema,
    IReadOnlyList<SealedTally> Tally, IReadOnlyList<string> Leaders, string Outcome,
    IReadOnlyList<RevealedBallot> Ballots, IReadOnlyList<AbstainedMember> Unrevealed, IReadOnlyList<AbstainedMember> NonVoters,
    string CommitmentDigest);
public sealed record SealedCommitRequest(string Commitment);

/// <summary>Sweeps due sealed-ballot motions once a minute. Reads also close on demand; this covers a motion nobody reads.</summary>
public sealed class SealedBallotCloser(IServiceScopeFactory scopes, ILogger<SealedBallotCloser> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<SealedBallotService>().FinalizeDueAsync(stoppingToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning(error, "Sealed-ballot close sweep failed; retrying next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
