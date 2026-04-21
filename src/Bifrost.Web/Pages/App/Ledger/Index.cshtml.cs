using System.ComponentModel.DataAnnotations;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Web.Pages.App.Ledger;

public sealed class IndexModel(
    BifrostDbContext dbContext,
    ICurrentBifrostActorAccessor actorAccessor,
    AuditTrailService auditTrailService,
    TimeProvider timeProvider) : PageModel
{
    [BindProperty]
    public CreateLedgerEntryInput Input { get; set; } = new();

    public CurrentBifrostActor Actor { get; private set; } = CurrentBifrostActor.Anonymous;

    public IReadOnlyList<SelectListItem> MemberOptions { get; private set; } = [];

    public IReadOnlyList<SelectListItem> ProjectOptions { get; private set; } = [];

    public IReadOnlyList<SelectListItem> WorkItemOptions { get; private set; } = [];

    public IReadOnlyList<LedgerEntry> LedgerEntries { get; private set; } = [];

    public IReadOnlyList<PayoutPreviewItem> PayoutPreview { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);

        if (!ModelState.IsValid)
        {
            await LoadAsync(cancellationToken);
            return Page();
        }

        var now = timeProvider.GetUtcNow();
        var entry = new LedgerEntry
        {
            UserAccountId = Input.UserAccountId,
            ProjectId = Input.ProjectId,
            WorkItemId = Input.WorkItemId,
            CreatedByUserAccountId = Actor.UserAccount!.Id,
            EntryType = Input.EntryType,
            Status = Input.Status,
            Points = Input.Points,
            NominalAmount = Input.NominalAmount,
            Note = Input.Note.Trim(),
            EffectiveAtUtc = now,
            CreatedAtUtc = now
        };

        dbContext.LedgerEntries.Add(entry);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            Actor.UserAccount.Id,
            nameof(LedgerEntry),
            entry.Id,
            "ledger-entry.created",
            $"Created {entry.EntryType} ledger entry.",
            cancellationToken);

        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);

        MemberOptions = await dbContext.UserAccounts
            .AsNoTracking()
            .Include(x => x.Membership)
            .Where(x => x.Membership != null && x.Membership.Status == MembershipStatus.Active)
            .OrderBy(x => x.DisplayName)
            .Select(x => new SelectListItem(x.DisplayName, x.Id.ToString()))
            .ToListAsync(cancellationToken);

        ProjectOptions = await dbContext.Projects
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x => new SelectListItem(x.Name, x.Id.ToString()))
            .ToListAsync(cancellationToken);

        WorkItemOptions = await dbContext.WorkItems
            .AsNoTracking()
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Select(x => new SelectListItem(x.Title, x.Id.ToString()))
            .ToListAsync(cancellationToken);

        LedgerEntries = await dbContext.LedgerEntries
            .AsNoTracking()
            .Include(x => x.UserAccount)
            .Include(x => x.Project)
            .Include(x => x.WorkItem)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(20)
            .ToListAsync(cancellationToken);

        PayoutPreview = await dbContext.LedgerEntries
            .AsNoTracking()
            .Where(x => x.Status == LedgerEntryStatus.Approved)
            .Include(x => x.UserAccount)
            .GroupBy(x => x.UserAccount.DisplayName)
            .Select(group => new PayoutPreviewItem(
                group.Key,
                group.Sum(x => x.Points),
                group.Sum(x => x.NominalAmount)))
            .OrderByDescending(x => x.NominalAmount)
            .ToListAsync(cancellationToken);
    }

    public sealed class CreateLedgerEntryInput
    {
        [Required]
        [Display(Name = "Member")]
        public Guid UserAccountId { get; set; }

        [Display(Name = "Project")]
        public Guid? ProjectId { get; set; }

        [Display(Name = "Work item")]
        public Guid? WorkItemId { get; set; }

        [Display(Name = "Entry type")]
        public LedgerEntryType EntryType { get; set; } = LedgerEntryType.ContributionCredit;

        public LedgerEntryStatus Status { get; set; } = LedgerEntryStatus.Draft;

        [Range(0, 100000)]
        public decimal Points { get; set; }

        [Display(Name = "Nominal amount")]
        [Range(0, 100000000)]
        public decimal NominalAmount { get; set; }

        [Required, StringLength(3000)]
        public string Note { get; set; } = string.Empty;
    }

    public sealed record PayoutPreviewItem(string MemberName, decimal Points, decimal NominalAmount);
}
