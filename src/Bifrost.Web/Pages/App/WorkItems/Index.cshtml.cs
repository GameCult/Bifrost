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
            EffortPoints = Input.EffortPoints,
            SourceType = WorkItemSourceType.Internal,
            Status = WorkItemStatus.Open,
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

        if (workItem.Status == WorkItemStatus.Open)
        {
            workItem.Status = WorkItemStatus.Claimed;
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
        if (!Actor.CanManageProjects)
        {
            return Forbid();
        }

        if (actorUser is null)
        {
            return Forbid();
        }

        var workItem = await dbContext.WorkItems
            .Include(x => x.VolunteerClaims)
            .SingleAsync(x => x.Id == workItemId, cancellationToken);

        workItem.AssignedToUserAccountId = userAccountId;
        workItem.Status = WorkItemStatus.InProgress;
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

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);

        ProjectOptions = await dbContext.Projects
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x => new SelectListItem(x.Name, x.Id.ToString()))
            .ToListAsync(cancellationToken);

        WorkItems = await dbContext.WorkItems
            .AsNoTracking()
            .Include(x => x.Project)
            .Include(x => x.AssignedToUserAccount)
            .Include(x => x.VolunteerClaims)
                .ThenInclude(x => x.UserAccount)
            .OrderByDescending(x => x.UpdatedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public sealed class CreateWorkItemInput
    {
        [Required]
        [Display(Name = "Project")]
        public Guid ProjectId { get; set; }

        [Required, StringLength(180)]
        public string Title { get; set; } = string.Empty;

        [Required, StringLength(2000)]
        public string Summary { get; set; } = string.Empty;

        [Range(0, 1000)]
        [Display(Name = "Effort points")]
        public decimal EffortPoints { get; set; } = 1m;
    }
}
