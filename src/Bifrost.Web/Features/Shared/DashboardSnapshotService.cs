using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Web.Features.Shared;

public sealed class DashboardSnapshotService(BifrostDbContext dbContext, TimeProvider timeProvider)
{
    public async Task<DashboardSnapshot> GetAsync(Guid? currentUserAccountId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var recentWorkItems = await dbContext.WorkItems
            .AsNoTracking()
            .Include(x => x.Project)
            .Include(x => x.AssignedToUserAccount)
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Take(6)
            .Select(x => new DashboardWorkItem(
                x.Id,
                x.Title,
                x.Project.Name,
                x.AssignedToUserAccount != null ? x.AssignedToUserAccount.DisplayName : "Unassigned",
                x.Status.ToString(),
                x.EstimatedHours,
                x.WorkLogs.Sum(log => log.Hours)))
            .ToListAsync(cancellationToken);

        var openMotions = await dbContext.Motions
            .AsNoTracking()
            .Include(x => x.Votes)
            .OrderBy(x => x.ClosesAtUtc)
            .Take(5)
            .Select(x => new DashboardMotion(
                x.Id,
                x.Title,
                x.Scope.ToString(),
                x.Category.ToString(),
                x.Votes.Sum(v => v.Choice == VoteChoice.For ? v.Weight : 0),
                x.Votes.Sum(v => v.Choice == VoteChoice.Against ? v.Weight : 0),
                x.ApprovalThreshold,
                x.ClosesAtUtc))
            .ToListAsync(cancellationToken);

        var outstandingApprovals = await dbContext.Memberships
            .AsNoTracking()
            .CountAsync(x => x.Status == MembershipStatus.PendingApproval, cancellationToken);

        var assignedToMe = currentUserAccountId is null
            ? []
            : await dbContext.WorkItems
                .AsNoTracking()
                .Include(x => x.Project)
                .Where(x => x.AssignedToUserAccountId == currentUserAccountId &&
                            x.Status != WorkItemStatus.Completed &&
                            x.Status != WorkItemStatus.Archived)
                .OrderByDescending(x => x.UpdatedAtUtc)
                .Take(5)
                .Select(x => new DashboardLaneItem(x.Id, x.Title, x.Project.Name, x.Status.ToString(), x.TargetDateUtc))
                .ToListAsync(cancellationToken);

        var volunteeredByMe = currentUserAccountId is null
            ? []
            : await dbContext.VolunteerClaims
                .AsNoTracking()
                .Where(x => x.UserAccountId == currentUserAccountId && x.Status == VolunteerClaimStatus.Active)
                .OrderByDescending(x => x.CreatedAtUtc)
                .Take(5)
                .Select(x => new DashboardLaneItem(
                    x.WorkItemId,
                    x.WorkItem.Title,
                    x.WorkItem.Project.Name,
                    x.WorkItem.Status.ToString(),
                    x.WorkItem.TargetDateUtc))
                .ToListAsync(cancellationToken);

        var submittedByMe = currentUserAccountId is null
            ? []
            : await dbContext.WorkItems
                .AsNoTracking()
                .Include(x => x.Project)
                .Where(x => x.AssignedToUserAccountId == currentUserAccountId &&
                            (x.Status == WorkItemStatus.SubmittedForReview ||
                             x.Status == WorkItemStatus.ChangesRequested ||
                             x.Status == WorkItemStatus.Approved))
                .OrderByDescending(x => x.UpdatedAtUtc)
                .Take(5)
                .Select(x => new DashboardLaneItem(x.Id, x.Title, x.Project.Name, x.Status.ToString(), x.TargetDateUtc))
                .ToListAsync(cancellationToken);

        var recentActivity = await dbContext.AuditEvents
            .AsNoTracking()
            .Include(x => x.ActorUserAccount)
            .OrderByDescending(x => x.OccurredAtUtc)
            .Take(8)
            .Select(x => new DashboardActivity(
                x.Action,
                x.Detail,
                x.ActorUserAccount != null ? x.ActorUserAccount.DisplayName : "System",
                x.OccurredAtUtc))
            .ToListAsync(cancellationToken);

        var recentBridgeActions = await dbContext.BridgeActions
            .AsNoTracking()
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Take(6)
            .Select(x => new DashboardBridgeAction(
                x.Id,
                x.ActorName,
                x.ActorKind.ToString(),
                x.TargetSurface.ToString(),
                x.ActionKind.ToString(),
                x.Status.ToString(),
                x.Title,
                x.TargetRepositoryFullName,
                string.IsNullOrWhiteSpace(x.ReceiptUrl) ? x.ExternalReceiptId : x.ReceiptUrl,
                x.UpdatedAtUtc))
            .ToListAsync(cancellationToken);

        var recentDispatchRuns = await dbContext.AgentDispatchRuns
            .AsNoTracking()
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Take(6)
            .Select(x => new DashboardDispatchRun(
                x.Id,
                x.RequestId,
                x.TargetRepoName,
                x.TargetAgentIdentity,
                x.LaunchMode,
                x.Status.ToString(),
                x.ThreadId,
                x.TurnId,
                string.IsNullOrWhiteSpace(x.Note) ? x.Error : x.Note,
                x.UpdatedAtUtc))
            .ToListAsync(cancellationToken);

        var payoutPreviewNominal = await dbContext.LedgerEntries
            .AsNoTracking()
            .Where(x => x.Status == LedgerEntryStatus.Approved)
            .SumAsync(x => x.NominalAmount, cancellationToken);

        return new DashboardSnapshot(
            ActiveMembers: await dbContext.Memberships.AsNoTracking().CountAsync(x => x.Status == MembershipStatus.Active, cancellationToken),
            OpenProjects: await dbContext.Projects.AsNoTracking().CountAsync(x => x.Status == ProjectStatus.Active, cancellationToken),
            OpenWorkItems: await dbContext.WorkItems.AsNoTracking().CountAsync(
                x => x.Status != WorkItemStatus.Completed && x.Status != WorkItemStatus.Archived,
                cancellationToken),
            OpenMotions: await dbContext.Motions.AsNoTracking().CountAsync(x => x.Status == MotionStatus.Open && x.ClosesAtUtc >= now, cancellationToken),
            PendingMembershipApprovals: outstandingApprovals,
            AssignedToCurrentMember: assignedToMe.Count,
            ActiveBridgeActions: await dbContext.BridgeActions.AsNoTracking().CountAsync(
                x => x.Status == BridgeActionStatus.Authorized || x.Status == BridgeActionStatus.InProgress,
                cancellationToken),
            ActiveDispatchRuns: await dbContext.AgentDispatchRuns.AsNoTracking().CountAsync(
                x => x.Status == AgentDispatchRunStatus.Started,
                cancellationToken),
            ApprovedNominalPayoutValue: payoutPreviewNominal,
            RecentWorkItems: recentWorkItems,
            OpenMotionHighlights: openMotions,
            MyAssignedWork: assignedToMe,
            MyVolunteeredWork: volunteeredByMe,
            MySubmittedWork: submittedByMe,
            RecentActivity: recentActivity,
            RecentBridgeActions: recentBridgeActions,
            RecentDispatchRuns: recentDispatchRuns);
    }
}

public sealed record DashboardSnapshot(
    int ActiveMembers,
    int OpenProjects,
    int OpenWorkItems,
    int OpenMotions,
    int PendingMembershipApprovals,
    int AssignedToCurrentMember,
    int ActiveBridgeActions,
    int ActiveDispatchRuns,
    decimal ApprovedNominalPayoutValue,
    IReadOnlyList<DashboardWorkItem> RecentWorkItems,
    IReadOnlyList<DashboardMotion> OpenMotionHighlights,
    IReadOnlyList<DashboardLaneItem> MyAssignedWork,
    IReadOnlyList<DashboardLaneItem> MyVolunteeredWork,
    IReadOnlyList<DashboardLaneItem> MySubmittedWork,
    IReadOnlyList<DashboardActivity> RecentActivity,
    IReadOnlyList<DashboardBridgeAction> RecentBridgeActions,
    IReadOnlyList<DashboardDispatchRun> RecentDispatchRuns);

public sealed record DashboardWorkItem(
    Guid WorkItemId,
    string Title,
    string ProjectName,
    string AssigneeName,
    string Status,
    decimal EstimatedHours,
    decimal ActualHours);

public sealed record DashboardMotion(
    Guid MotionId,
    string Title,
    string Scope,
    string Category,
    decimal VotesFor,
    decimal VotesAgainst,
    decimal Threshold,
    DateTimeOffset ClosesAtUtc);

public sealed record DashboardLaneItem(
    Guid WorkItemId,
    string Title,
    string ProjectName,
    string Status,
    DateTimeOffset? TargetDateUtc);

public sealed record DashboardActivity(
    string Action,
    string Detail,
    string ActorName,
    DateTimeOffset OccurredAtUtc);

public sealed record DashboardBridgeAction(
    Guid BridgeActionId,
    string ActorName,
    string ActorKind,
    string TargetSurface,
    string ActionKind,
    string Status,
    string Title,
    string TargetRepositoryFullName,
    string ReceiptLabel,
    DateTimeOffset UpdatedAtUtc);

public sealed record DashboardDispatchRun(
    Guid AgentDispatchRunId,
    string RequestId,
    string TargetRepoName,
    string TargetAgentIdentity,
    string LaunchMode,
    string Status,
    string ThreadId,
    string TurnId,
    string Note,
    DateTimeOffset UpdatedAtUtc);
