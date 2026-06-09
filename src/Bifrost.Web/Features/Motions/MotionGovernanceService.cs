using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Shared;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Web.Features.Motions;

public sealed class MotionGovernanceService(
    BifrostDbContext dbContext,
    AuditTrailService auditTrailService,
    TimeProvider timeProvider)
{
    public async Task<Motion> CreateMotionAsync(
        CurrentBifrostActor actor,
        CreateMotionCommand command,
        CancellationToken cancellationToken)
    {
        var actorUser = actor.UserAccount ?? throw new UnauthorizedAccessException("A linked Bifrost actor is required.");
        var now = timeProvider.GetUtcNow();
        var motion = new Motion
        {
            ProjectId = command.Scope == MotionScope.Project ? command.ProjectId : null,
            CreatedByUserAccountId = actorUser.Id,
            Scope = command.Scope,
            Category = command.Category,
            Title = command.Title.Trim(),
            Summary = command.Summary.Trim(),
            ApprovalThreshold = MotionCategoryPolicy.GetThreshold(command.Category),
            OpensAtUtc = now,
            ClosesAtUtc = command.ClosesAtUtc.ToUniversalTime(),
            CreatedAtUtc = now
        };

        dbContext.Motions.Add(motion);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            actorUser.Id,
            nameof(Motion),
            motion.Id,
            "motion.created",
            $"Opened motion {motion.Title}.",
            cancellationToken);

        return motion;
    }

    public async Task CastVoteAsync(
        CurrentBifrostActor actor,
        Guid motionId,
        VoteChoice choice,
        string? comment,
        CancellationToken cancellationToken)
    {
        var actorUser = actor.UserAccount ?? throw new UnauthorizedAccessException("A linked Bifrost actor is required.");
        var motion = await dbContext.Motions
            .Include(x => x.Votes)
            .SingleAsync(x => x.Id == motionId, cancellationToken);

        if (motion.Status != MotionStatus.Open)
        {
            return;
        }

        var vote = motion.Votes.SingleOrDefault(x => x.UserAccountId == actorUser.Id);
        if (vote is null)
        {
            dbContext.Votes.Add(new Vote
            {
                MotionId = motionId,
                UserAccountId = actorUser.Id,
                Choice = choice,
                Weight = actor.EffectiveVotingWeight,
                Comment = comment?.Trim() ?? string.Empty,
                CastAtUtc = timeProvider.GetUtcNow()
            });
        }
        else
        {
            vote.Choice = choice;
            vote.Weight = actor.EffectiveVotingWeight;
            vote.Comment = comment?.Trim() ?? string.Empty;
            vote.CastAtUtc = timeProvider.GetUtcNow();
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            actorUser.Id,
            nameof(Motion),
            motion.Id,
            "motion.voted",
            $"{actor.DisplayName} voted {choice} on {motion.Title}.",
            cancellationToken);
    }

    public async Task CloseMotionAsync(
        CurrentBifrostActor actor,
        Guid motionId,
        CancellationToken cancellationToken)
    {
        var actorUser = actor.UserAccount ?? throw new UnauthorizedAccessException("A linked Bifrost actor is required.");
        var motion = await dbContext.Motions
            .Include(x => x.Votes)
            .SingleAsync(x => x.Id == motionId, cancellationToken);

        if (!actor.CanModerateMotions && motion.CreatedByUserAccountId != actorUser.Id)
        {
            throw new UnauthorizedAccessException("Only the motion creator or a moderator can close this motion.");
        }

        var votesFor = motion.Votes.Where(v => v.Choice == VoteChoice.For).Sum(v => v.Weight);
        var votesAgainst = motion.Votes.Where(v => v.Choice == VoteChoice.Against).Sum(v => v.Weight);
        var decisiveVotes = votesFor + votesAgainst;

        motion.Status = decisiveVotes > 0 && votesFor / decisiveVotes >= motion.ApprovalThreshold
            ? MotionStatus.Passed
            : decisiveVotes > 0
                ? MotionStatus.Failed
                : MotionStatus.Closed;
        motion.ResolvedAtUtc = timeProvider.GetUtcNow();
        motion.ResolutionNote = decisiveVotes > 0
            ? $"Closed with {votesFor:0.##} for and {votesAgainst:0.##} against at a {motion.ApprovalThreshold:P0} threshold."
            : "Closed without decisive votes.";

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            actorUser.Id,
            nameof(Motion),
            motion.Id,
            "motion.closed",
            $"Closed motion {motion.Title} with status {motion.Status}.",
            cancellationToken);
    }

    public async Task<MotionGovernanceState> GetStateAsync(
        CurrentBifrostActor actor,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var motions = await dbContext.Motions
            .AsNoTracking()
            .Include(x => x.Project)
            .Include(x => x.Votes)
            .OrderBy(x => x.ClosesAtUtc)
            .ToListAsync(cancellationToken);

        var projects = await dbContext.Projects
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x => new MotionProjectOption(x.Id, x.Name))
            .ToListAsync(cancellationToken);

        var motionItems = motions.Select(x => new MotionListItem(
            x.Id,
            x.Title,
            x.Summary,
            x.Project?.Name ?? "Management-wide",
            x.Scope.ToString(),
            MotionCategoryPolicy.GetLabel(x.Category),
            x.ApprovalThreshold,
            ResolveStatusLabel(x, now),
            ResolveStatusTone(x, now),
            x.Votes.Where(v => v.Choice == VoteChoice.For).Sum(v => v.Weight),
            x.Votes.Where(v => v.Choice == VoteChoice.Against).Sum(v => v.Weight),
            x.Votes.Where(v => v.Choice == VoteChoice.Abstain).Sum(v => v.Weight),
            x.Votes.Where(v => v.UserAccountId == actor.UserAccount?.Id).Select(v => (VoteChoice?)v.Choice).FirstOrDefault(),
            x.ClosesAtUtc,
            x.ResolutionNote)).ToList();

        return new MotionGovernanceState(
            actor.DisplayName,
            actor.EffectiveVotingWeight,
            actor.CanModerateMotions,
            projects,
            motionItems,
            MotionCategoryPolicyRows.All);
    }

    public static string ResolveStatusLabel(Motion motion, DateTimeOffset now)
    {
        if (motion.Status != MotionStatus.Open)
        {
            return motion.Status.ToString();
        }

        if (motion.ClosesAtUtc >= now)
        {
            return "Open";
        }

        var votesFor = motion.Votes.Where(v => v.Choice == VoteChoice.For).Sum(v => v.Weight);
        var votesAgainst = motion.Votes.Where(v => v.Choice == VoteChoice.Against).Sum(v => v.Weight);
        var decisiveVotes = votesFor + votesAgainst;
        if (decisiveVotes <= 0)
        {
            return "Expired";
        }

        return votesFor / decisiveVotes >= motion.ApprovalThreshold ? "Passed" : "Failed";
    }

    public static string ResolveStatusTone(Motion motion, DateTimeOffset now) =>
        ResolveStatusLabel(motion, now) switch
        {
            "Passed" => "success",
            "Failed" => "danger",
            "Expired" => "warning",
            _ => string.Empty
        };
}

public sealed record CreateMotionCommand(
    MotionScope Scope,
    Guid? ProjectId,
    MotionCategory Category,
    string Title,
    string Summary,
    DateTimeOffset ClosesAtUtc);

public sealed record MotionGovernanceState(
    string ActorName,
    decimal EffectiveVotingWeight,
    bool CanModerateMotions,
    IReadOnlyList<MotionProjectOption> Projects,
    IReadOnlyList<MotionListItem> Motions,
    IReadOnlyList<MotionCategoryPolicyRow> CategoryPolicies);

public sealed record MotionProjectOption(Guid Id, string Name);

public sealed record MotionCategoryPolicyRow(string Label, decimal Threshold);

public static class MotionCategoryPolicyRows
{
    public static IReadOnlyList<MotionCategoryPolicyRow> All { get; } =
    [
        new("Bugs", 0.15m),
        new("Cosmetics", 0.30m),
        new("Balance changes", 0.40m),
        new("Features and new content", 0.50m),
        new("Fundamental design changes", 0.66m)
    ];
}

public sealed record MotionListItem(
    Guid Id,
    string Title,
    string Summary,
    string ProjectName,
    string Scope,
    string Category,
    decimal Threshold,
    string StatusLabel,
    string StatusTone,
    decimal VotesFor,
    decimal VotesAgainst,
    decimal VotesAbstain,
    VoteChoice? CurrentUserVote,
    DateTimeOffset ClosesAtUtc,
    string ResolutionNote);
