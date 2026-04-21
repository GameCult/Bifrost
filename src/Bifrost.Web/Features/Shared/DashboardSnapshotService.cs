using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Web.Features.Shared;

public sealed class DashboardSnapshotService(BifrostDbContext dbContext, TimeProvider timeProvider)
{
    public async Task<DashboardSnapshot> GetAsync(Guid? currentUserAccountId, CancellationToken cancellationToken)
    {
        var recentWorkItems = await dbContext.WorkItems
            .AsNoTracking()
            .Include(x => x.Project)
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Take(5)
            .Select(x => new DashboardWorkItem(
                x.Title,
                x.Project.Name,
                x.Status.ToString(),
                x.Id))
            .ToListAsync(cancellationToken);

        var openMotions = await dbContext.Motions
            .AsNoTracking()
            .Include(x => x.Votes)
            .OrderBy(x => x.ClosesAtUtc)
            .Take(5)
            .Select(x => new DashboardMotion(
                x.Title,
                x.Scope.ToString(),
                x.Votes.Sum(v => v.Choice == VoteChoice.For ? v.Weight : 0),
                x.Votes.Sum(v => v.Choice == VoteChoice.Against ? v.Weight : 0),
                x.ClosesAtUtc,
                x.Id))
            .ToListAsync(cancellationToken);

        var outstandingApprovals = await dbContext.Memberships
            .AsNoTracking()
            .CountAsync(x => x.Status == MembershipStatus.PendingApproval, cancellationToken);

        var assignedToMe = currentUserAccountId is null
            ? 0
            : await dbContext.WorkItems
                .AsNoTracking()
                .CountAsync(
                    x => x.AssignedToUserAccountId == currentUserAccountId &&
                         x.Status != WorkItemStatus.Done &&
                         x.Status != WorkItemStatus.Archived,
                    cancellationToken);

        var payoutPreviewNominal = await dbContext.LedgerEntries
            .AsNoTracking()
            .Where(x => x.Status == LedgerEntryStatus.Approved)
            .SumAsync(x => x.NominalAmount, cancellationToken);

        return new DashboardSnapshot(
            ActiveMembers: await dbContext.Memberships.AsNoTracking().CountAsync(x => x.Status == MembershipStatus.Active, cancellationToken),
            OpenProjects: await dbContext.Projects.AsNoTracking().CountAsync(x => x.Status == ProjectStatus.Active, cancellationToken),
            OpenWorkItems: await dbContext.WorkItems.AsNoTracking().CountAsync(x => x.Status == WorkItemStatus.Open || x.Status == WorkItemStatus.Claimed || x.Status == WorkItemStatus.InProgress, cancellationToken),
            OpenMotions: await dbContext.Motions.AsNoTracking().CountAsync(x => x.Status == MotionStatus.Open && x.ClosesAtUtc >= timeProvider.GetUtcNow(), cancellationToken),
            PendingMembershipApprovals: outstandingApprovals,
            AssignedToCurrentMember: assignedToMe,
            ApprovedNominalPayoutValue: payoutPreviewNominal,
            RecentWorkItems: recentWorkItems,
            OpenMotionHighlights: openMotions);
    }
}

public sealed record DashboardSnapshot(
    int ActiveMembers,
    int OpenProjects,
    int OpenWorkItems,
    int OpenMotions,
    int PendingMembershipApprovals,
    int AssignedToCurrentMember,
    decimal ApprovedNominalPayoutValue,
    IReadOnlyList<DashboardWorkItem> RecentWorkItems,
    IReadOnlyList<DashboardMotion> OpenMotionHighlights);

public sealed record DashboardWorkItem(
    string Title,
    string ProjectName,
    string Status,
    Guid WorkItemId);

public sealed record DashboardMotion(
    string Title,
    string Scope,
    decimal VotesFor,
    decimal VotesAgainst,
    DateTimeOffset ClosesAtUtc,
    Guid MotionId);
