using System.ComponentModel.DataAnnotations;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Web.Pages.App.WorkItems;

public sealed class IndexModel(
    BifrostDbContext dbContext,
    ICurrentBifrostActorAccessor actorAccessor,
    AuditTrailService auditTrailService,
    TimeProvider timeProvider) : PageModel
{
    [BindProperty]
    public CreateWorkItemInput Input { get; set; } = new();

    public CurrentBifrostActor Actor { get; private set; } = CurrentBifrostActor.Anonymous;

    public IReadOnlyList<SelectListItem> ProjectOptions { get; private set; } = [];

    public IReadOnlyList<SelectListItem> MemberOptions { get; private set; } = [];

    public IReadOnlyList<WorkItem> WorkItems { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);
        var actorUser = Actor.UserAccount;

        if (actorUser is null)
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync(cancellationToken);
            return Page();
        }

        var now = timeProvider.GetUtcNow();
        var workItem = new WorkItem
        {
            ProjectId = Input.ProjectId,
            RequestedByUserAccountId = actorUser.Id,
            Title = Input.Title.Trim(),
            Summary = Input.Summary.Trim(),
            Category = Input.Category.Trim(),
            SkillLevel = Input.SkillLevel,
            EstimatedHours = Input.EstimatedHours,
            ContributionPoints = Input.ContributionPoints,
            TargetDateUtc = Input.TargetDateUtc?.ToUniversalTime(),
            SourceType = WorkItemSourceType.Internal,
            Status = Actor.CanManageProjects ? WorkItemStatus.Open : WorkItemStatus.Proposed,
            ReviewStatus = WorkReviewStatus.Pending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        dbContext.WorkItems.Add(workItem);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            actorUser.Id,
            nameof(WorkItem),
            workItem.Id,
            "work-item.created",
            $"Created work item {workItem.Title}.",
            cancellationToken);

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostVolunteerAsync(Guid workItemId, CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);
        var actorUser = Actor.UserAccount;
        if (actorUser is null)
        {
            return Forbid();
        }

        var workItem = await dbContext.WorkItems
            .Include(x => x.VolunteerClaims)
            .SingleAsync(x => x.Id == workItemId, cancellationToken);

        var existingClaim = workItem.VolunteerClaims.SingleOrDefault(x => x.UserAccountId == actorUser.Id);
        if (existingClaim is null)
        {
            dbContext.VolunteerClaims.Add(new VolunteerClaim
            {
                WorkItemId = workItemId,
                UserAccountId = actorUser.Id,
                CreatedAtUtc = timeProvider.GetUtcNow()
            });
        }
        else
        {
            existingClaim.Status = VolunteerClaimStatus.Active;
        }

        if (workItem.Status is WorkItemStatus.Proposed or WorkItemStatus.Open)
        {
            workItem.Status = WorkItemStatus.Volunteered;
            workItem.UpdatedAtUtc = timeProvider.GetUtcNow();
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            actorUser.Id,
            nameof(WorkItem),
            workItem.Id,
            "work-item.volunteered",
            $"{Actor.DisplayName} volunteered for {workItem.Title}.",
            cancellationToken);

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAssignAsync(Guid workItemId, Guid userAccountId, CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);
        var actorUser = Actor.UserAccount;
        if (!Actor.CanAssignWork || actorUser is null)
        {
            return Forbid();
        }

        var workItem = await dbContext.WorkItems
            .Include(x => x.VolunteerClaims)
            .SingleAsync(x => x.Id == workItemId, cancellationToken);

        workItem.AssignedToUserAccountId = userAccountId;
        workItem.Status = WorkItemStatus.Assigned;
        workItem.UpdatedAtUtc = timeProvider.GetUtcNow();

        var claim = workItem.VolunteerClaims.SingleOrDefault(x => x.UserAccountId == userAccountId);
        if (claim is not null)
        {
            claim.Status = VolunteerClaimStatus.Accepted;
        }

        dbContext.Assignments.Add(new Assignment
        {
            WorkItemId = workItemId,
            UserAccountId = userAccountId,
            AssignedByUserAccountId = actorUser.Id,
            AssignedAtUtc = timeProvider.GetUtcNow()
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            actorUser.Id,
            nameof(WorkItem),
            workItem.Id,
            "work-item.assigned",
            $"Assigned work item {workItem.Title}.",
            cancellationToken);

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostStartAsync(Guid workItemId, CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);
        var workItem = await dbContext.WorkItems.SingleAsync(x => x.Id == workItemId, cancellationToken);
        if (!CanWorkOnItem(Actor, workItem))
        {
            return Forbid();
        }

        workItem.Status = WorkItemStatus.InProgress;
        workItem.UpdatedAtUtc = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditTrailService.RecordAsync(
            Actor.UserAccount!.Id,
            nameof(WorkItem),
            workItem.Id,
            "work-item.started",
            $"Started work on {workItem.Title}.",
            cancellationToken);

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostLogTimeAsync(Guid workItemId, decimal hours, string note, CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);
        var actorUser = Actor.UserAccount;
        if (actorUser is null)
        {
            return Forbid();
        }

        var workItem = await dbContext.WorkItems.SingleAsync(x => x.Id == workItemId, cancellationToken);
        if (!CanWorkOnItem(Actor, workItem))
        {
            return Forbid();
        }

        dbContext.WorkLogs.Add(new WorkLog
        {
            WorkItemId = workItemId,
            UserAccountId = actorUser.Id,
            Hours = hours,
            Note = note?.Trim() ?? string.Empty,
            ApprovalStatus = WorkLogApprovalStatus.Submitted,
            CreatedAtUtc = timeProvider.GetUtcNow()
        });

        workItem.UpdatedAtUtc = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditTrailService.RecordAsync(
            actorUser.Id,
            nameof(WorkItem),
            workItem.Id,
            "work-log.created",
            $"Logged {hours:0.##}h on {workItem.Title}.",
            cancellationToken);

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostModerateLogAsync(
        Guid workLogId,
        WorkLogApprovalStatus approvalStatus,
        CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);
        if (!Actor.CanReviewWork || Actor.UserAccount is null)
        {
            return Forbid();
        }

        var workLog = await dbContext.WorkLogs
            .Include(x => x.WorkItem)
            .SingleAsync(x => x.Id == workLogId, cancellationToken);

        workLog.ApprovalStatus = approvalStatus;
        workLog.ReviewedByUserAccountId = Actor.UserAccount.Id;
        workLog.ReviewedAtUtc = timeProvider.GetUtcNow();
        workLog.WorkItem.UpdatedAtUtc = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            Actor.UserAccount.Id,
            nameof(WorkLog),
            workLog.Id,
            "work-log.moderated",
            $"{approvalStatus} work log for {workLog.WorkItem.Title}.",
            cancellationToken);

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSubmitAsync(Guid workItemId, CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);
        var workItem = await dbContext.WorkItems.SingleAsync(x => x.Id == workItemId, cancellationToken);
        if (!CanWorkOnItem(Actor, workItem))
        {
            return Forbid();
        }

        workItem.Status = WorkItemStatus.SubmittedForReview;
        workItem.ReviewStatus = WorkReviewStatus.Pending;
        workItem.SubmittedAtUtc = timeProvider.GetUtcNow();
        workItem.UpdatedAtUtc = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            Actor.UserAccount!.Id,
            nameof(WorkItem),
            workItem.Id,
            "work-item.submitted",
            $"Submitted {workItem.Title} for review.",
            cancellationToken);

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostReviewAsync(
        Guid workItemId,
        WorkReviewStatus status,
        string note,
        CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);
        if (!Actor.CanReviewWork || Actor.UserAccount is null)
        {
            return Forbid();
        }

        var workItem = await dbContext.WorkItems.SingleAsync(x => x.Id == workItemId, cancellationToken);
        workItem.ReviewStatus = status;
        workItem.Status = status == WorkReviewStatus.Approved
            ? WorkItemStatus.Approved
            : WorkItemStatus.ChangesRequested;
        workItem.UpdatedAtUtc = timeProvider.GetUtcNow();

        dbContext.WorkReviews.Add(new WorkReview
        {
            WorkItemId = workItemId,
            ReviewerUserAccountId = Actor.UserAccount.Id,
            ReviewerName = Actor.DisplayName,
            Status = status,
            Note = note?.Trim() ?? string.Empty,
            ReviewedAtUtc = timeProvider.GetUtcNow()
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            Actor.UserAccount.Id,
            nameof(WorkItem),
            workItem.Id,
            "work-item.reviewed",
            $"Reviewed {workItem.Title} as {status}.",
            cancellationToken);

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCompleteAsync(Guid workItemId, CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);
        if (!Actor.CanReviewWork || Actor.UserAccount is null)
        {
            return Forbid();
        }

        var workItem = await dbContext.WorkItems.SingleAsync(x => x.Id == workItemId, cancellationToken);
        workItem.Status = WorkItemStatus.Completed;
        workItem.CompletedAtUtc = timeProvider.GetUtcNow();
        workItem.UpdatedAtUtc = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            Actor.UserAccount.Id,
            nameof(WorkItem),
            workItem.Id,
            "work-item.completed",
            $"Completed {workItem.Title}.",
            cancellationToken);

        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);

        ProjectOptions = await dbContext.Projects
            .AsNoTracking()
            .Where(x => x.Status == ProjectStatus.Active)
            .OrderBy(x => x.Name)
            .Select(x => new SelectListItem(x.Name, x.Id.ToString()))
            .ToListAsync(cancellationToken);

        MemberOptions = await dbContext.UserAccounts
            .AsNoTracking()
            .Include(x => x.Membership)
            .Where(x => x.Membership != null && x.Membership.Status == MembershipStatus.Active)
            .OrderBy(x => x.DisplayName)
            .Select(x => new SelectListItem(x.DisplayName, x.Id.ToString()))
            .ToListAsync(cancellationToken);

        WorkItems = await dbContext.WorkItems
            .AsNoTracking()
            .Include(x => x.Project)
            .Include(x => x.AssignedToUserAccount)
            .Include(x => x.VolunteerClaims)
                .ThenInclude(x => x.UserAccount)
            .Include(x => x.WorkLogs)
                .ThenInclude(x => x.UserAccount)
            .Include(x => x.WorkReviews)
            .Include(x => x.GitHubIssueLink)
            .Include(x => x.GitHubPullRequestLinks)
            .OrderByDescending(x => x.UpdatedAtUtc)
            .ToListAsync(cancellationToken);
    }

    private static bool CanWorkOnItem(CurrentBifrostActor actor, WorkItem workItem)
    {
        var actorId = actor.UserAccount?.Id;
        if (actorId is null)
        {
            return false;
        }

        return workItem.AssignedToUserAccountId == actorId ||
               workItem.RequestedByUserAccountId == actorId ||
               workItem.VolunteerClaims.Any(x => x.UserAccountId == actorId && x.Status == VolunteerClaimStatus.Active);
    }

    public sealed class CreateWorkItemInput
    {
        [Required]
        [Display(Name = "Project")]
        public Guid ProjectId { get; set; }

        [Required, StringLength(180)]
        public string Title { get; set; } = string.Empty;

        [Required, StringLength(120)]
        public string Category { get; set; } = string.Empty;

        [Display(Name = "Skill level")]
        public WorkItemSkillLevel SkillLevel { get; set; } = WorkItemSkillLevel.Routine;

        [Range(0, 1000)]
        [Display(Name = "Estimated hours")]
        public decimal EstimatedHours { get; set; } = 1m;

        [Range(0, 1000)]
        [Display(Name = "Contribution points")]
        public decimal ContributionPoints { get; set; } = 1m;

        [Display(Name = "Target date (UTC)")]
        public DateTimeOffset? TargetDateUtc { get; set; }

        [Required, StringLength(2000)]
        public string Summary { get; set; } = string.Empty;
    }
}
