using System.ComponentModel.DataAnnotations;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Web.Pages.App.Members;

public sealed class IndexModel(
    BifrostDbContext dbContext,
    ICurrentBifrostActorAccessor actorAccessor,
    AuditTrailService auditTrailService,
    TimeProvider timeProvider) : PageModel
{
    [BindProperty]
    public InviteMemberInput Input { get; set; } = new();

    public CurrentBifrostActor Actor { get; private set; } = CurrentBifrostActor.Anonymous;

    public IReadOnlyList<UserAccount> Users { get; private set; } = [];

    public IReadOnlyList<MembershipInvitation> Invitations { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostInviteAsync(CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);
        if (!Actor.CanManageMembership)
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync(cancellationToken);
            return Page();
        }

        var normalizedLogin = Input.GitHubLogin.Trim().ToUpperInvariant();
        var invitation = await dbContext.MembershipInvitations
            .SingleOrDefaultAsync(x => x.NormalizedGitHubLogin == normalizedLogin, cancellationToken);

        if (invitation is null)
        {
            invitation = new MembershipInvitation
            {
                GitHubLogin = Input.GitHubLogin.Trim(),
                NormalizedGitHubLogin = normalizedLogin,
                IssuedByUserAccountId = Actor.UserAccount!.Id,
                IssuedAtUtc = timeProvider.GetUtcNow(),
                Notes = Input.Notes.Trim()
            };

            dbContext.MembershipInvitations.Add(invitation);
        }
        else
        {
            invitation.RevokedAtUtc = null;
            invitation.Notes = Input.Notes.Trim();
        }

        var existingUser = await dbContext.UserAccounts
            .Include(x => x.Membership)
            .SingleOrDefaultAsync(x => x.NormalizedGitHubLogin == normalizedLogin, cancellationToken);

        if (existingUser?.Membership is not null && existingUser.Membership.Status == MembershipStatus.Authenticated)
        {
            existingUser.Membership.Status = MembershipStatus.PendingApproval;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            Actor.UserAccount!.Id,
            nameof(MembershipInvitation),
            invitation.Id,
            "membership.invited",
            $"Issued invite for {invitation.GitHubLogin}.",
            cancellationToken);

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostApproveAsync(Guid userAccountId, CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);
        if (!Actor.CanManageMembership)
        {
            return Forbid();
        }

        var user = await dbContext.UserAccounts
            .Include(x => x.Membership)
            .SingleAsync(x => x.Id == userAccountId, cancellationToken);

        user.Membership!.Status = MembershipStatus.Active;
        user.Membership.ApprovedAtUtc = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            Actor.UserAccount!.Id,
            nameof(Membership),
            user.Membership.Id,
            "membership.approved",
            $"Approved {user.DisplayName} as an active member.",
            cancellationToken);

        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);

        Users = await dbContext.UserAccounts
            .AsNoTracking()
            .Include(x => x.Membership)
            .OrderBy(x => x.DisplayName)
            .ToListAsync(cancellationToken);

        Invitations = await dbContext.MembershipInvitations
            .AsNoTracking()
            .Where(x => x.RevokedAtUtc == null)
            .OrderByDescending(x => x.IssuedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public sealed class InviteMemberInput
    {
        [Required, StringLength(120)]
        [Display(Name = "GitHub login")]
        public string GitHubLogin { get; set; } = string.Empty;

        [StringLength(500)]
        public string Notes { get; set; } = string.Empty;
    }
}
