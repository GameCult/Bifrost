using System.ComponentModel.DataAnnotations;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Patronage;
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
    PatronageService patronageService,
    TimeProvider timeProvider) : PageModel
{
    [BindProperty]
    public CreateLedgerEntryInput Input { get; set; } = new();

    [BindProperty]
    public RecordPatronSupportInput PatronSupport { get; set; } = new()
    {
        SupportedAtUtc = DateTimeOffset.UtcNow
    };

    public CurrentBifrostActor Actor { get; private set; } = CurrentBifrostActor.Anonymous;

    public IReadOnlyList<SelectListItem> MemberOptions { get; private set; } = [];

    public IReadOnlyList<SelectListItem> ProjectOptions { get; private set; } = [];

    public IReadOnlyList<SelectListItem> WorkItemOptions { get; private set; } = [];

    public IReadOnlyList<LedgerEntry> LedgerEntries { get; private set; } = [];

    public IReadOnlyList<PatronSupportListItem> PatronSupportEvents { get; private set; } = [];

    public IReadOnlyList<PayoutPreviewItem> PayoutPreview { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);
        if (!Actor.CanManageLedger || Actor.UserAccount is null)
        {
            return Forbid();
        }

        ModelState.Clear();
        if (!TryValidateModel(Input, nameof(Input)))
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
            CreatedByUserAccountId = Actor.UserAccount.Id,
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

    public async Task<IActionResult> OnPostRecordPatronSupportAsync(CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);
        if (!Actor.CanManageLedger || Actor.UserAccount is null)
        {
            return Forbid();
        }

        ModelState.Clear();
        if (!TryValidateModel(PatronSupport, nameof(PatronSupport)))
        {
            await LoadAsync(cancellationToken);
            return Page();
        }

        await patronageService.RecordSupportEventAsync(
            Actor.UserAccount.Id,
            PatronSupport.UserAccountId,
            PatronSupport.Kind,
            PatronSupport.Amount,
            PatronSupport.CurrencyCode,
            PatronSupport.ExternalSupportId ?? string.Empty,
            PatronSupport.SupportedAtUtc,
            PatronSupport.IsCurrentRecurringSupport,
            PatronSupport.Notes ?? string.Empty,
            cancellationToken);

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRefreshPatronTierAsync(Guid userAccountId, CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);
        if (!Actor.CanManageLedger || Actor.UserAccount is null)
        {
            return Forbid();
        }

        await patronageService.RefreshPatronTierSnapshotAsync(Actor.UserAccount.Id, userAccountId, cancellationToken);
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

        var allSupportEvents = await dbContext.PatronSupportEvents
            .AsNoTracking()
            .Include(x => x.UserAccount)
            .OrderByDescending(x => x.RecordedAtUtc)
            .ToListAsync(cancellationToken);

        PatronSupportEvents = allSupportEvents
            .Take(20)
            .Select(x =>
            {
                var summary = PatronageService.CalculatePatronPoints(
                    allSupportEvents.Where(item => item.UserAccountId == x.UserAccountId),
                    timeProvider.GetUtcNow());

                return new PatronSupportListItem(
                    x.UserAccountId,
                    x.UserAccount.DisplayName,
                    x.Kind,
                    x.Amount,
                    x.CurrencyCode,
                    x.IsCurrentRecurringSupport,
                    x.SupportedAtUtc,
                    x.RecordedAtUtc,
                    x.ExternalSupportId,
                    summary.EffectivePoints,
                    summary.TierLabel,
                    summary.VotingWeight);
            })
            .ToList();

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

    public sealed class RecordPatronSupportInput
    {
        [Required]
        [Display(Name = "Member")]
        public Guid UserAccountId { get; set; }

        [Display(Name = "Support kind")]
        public PatronSupportEventKind Kind { get; set; } = PatronSupportEventKind.OneTimeDonation;

        [Range(0.01, 100000000)]
        public decimal Amount { get; set; }

        [Display(Name = "Currency")]
        [StringLength(12)]
        public string CurrencyCode { get; set; } = "USD";

        [Display(Name = "External support id")]
        [StringLength(240)]
        public string? ExternalSupportId { get; set; }

        [Display(Name = "Current recurring support")]
        public bool IsCurrentRecurringSupport { get; set; }

        [Display(Name = "Supported at (UTC)")]
        public DateTimeOffset SupportedAtUtc { get; set; }

        [StringLength(3000)]
        public string? Notes { get; set; }
    }

    public sealed record PayoutPreviewItem(string MemberName, decimal Points, decimal NominalAmount);

    public sealed record PatronSupportListItem(
        Guid UserAccountId,
        string MemberName,
        PatronSupportEventKind Kind,
        decimal Amount,
        string CurrencyCode,
        bool IsCurrentRecurringSupport,
        DateTimeOffset SupportedAtUtc,
        DateTimeOffset RecordedAtUtc,
        string ExternalSupportId,
        decimal EffectivePoints,
        string TierLabel,
        decimal VotingWeight);
}
