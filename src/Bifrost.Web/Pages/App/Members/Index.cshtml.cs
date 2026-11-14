using System.ComponentModel.DataAnnotations;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Features.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Web.Pages.App.Members;

public sealed class IndexModel(
    BifrostDbContext dbContext,
    ICurrentBifrostActorAccessor actorAccessor,
    AuditTrailService auditTrailService,
    MembershipSuspensionService suspension,
    TimeProvider timeProvider) : PageModel
{
    [BindProperty]
    public InviteMemberInput Input { get; set; } = new();

    [BindProperty]
    public UpdateProfileInput Profile { get; set; } = new();

    /// <summary>Why the last membership action did nothing; one of <see cref="RefusalNotices"/>, carried across the redirect.</summary>
    [TempData]
    public string? Notice { get; set; }

    private static readonly IReadOnlyDictionary<SuspensionOutcome, string> RefusalNotices = new Dictionary<SuspensionOutcome, string>
    {
        [SuspensionOutcome.NoReason] = "Nothing changed: a reason is required to suspend or unsuspend an account.",
        [SuspensionOutcome.ProtectedAccount] = "Nothing changed: you cannot suspend yourself or another platform admin.",
        [SuspensionOutcome.NoSuchAccount] = "Nothing changed: that account or its membership does not exist.",
        [SuspensionOutcome.NotApplicable] = "Nothing changed: the account is already in that state."
    };

    internal const string ApproveSuspendedNotice = "Nothing changed: a suspended account can only be lifted with Unsuspend.";
    /// <summary>Admin-only text for a Suspended row whose reason predates the SuspensionReason column; the old text is never read back from the audit trail.</summary>
    internal const string LegacySuspensionText = "Suspended before reasons were recorded";
    internal const string ApproveMergedNotice = "Nothing changed: that account was merged into another and cannot be approved.";

    internal const string NoMembershipNotice = "Nothing changed: that account has no membership yet.";
    internal const string OwnAdminRoleNotice = "Nothing changed: you cannot remove or change your own platform admin role.";
    internal const string RoleOnSuspendedNotice = "Nothing changed: a suspended account's roles cannot be changed until it is unsuspended.";

    public CurrentBifrostActor Actor { get; private set; } = CurrentBifrostActor.Anonymous;

    public IReadOnlyList<MemberDirectoryItem> Users { get; private set; } = [];

    public IReadOnlyList<MembershipInvitation> Invitations { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostInviteAsync(CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);
        if (!Actor.CanManageMembership || Actor.UserAccount is null)
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
                IssuedByUserAccountId = Actor.UserAccount.Id,
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
            Actor.UserAccount.Id,
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
        if (!Actor.CanManageMembership || Actor.UserAccount is null)
        {
            return Forbid();
        }

        var user = await dbContext.UserAccounts
            .Include(x => x.Membership!)
                .ThenInclude(x => x.RoleAssignments)
            .SingleOrDefaultAsync(x => x.Id == userAccountId, cancellationToken);

        // The chat tier holds no membership by design (docs/oidc-issuer.md); approval refuses rather than inventing one.
        if (user is null || user.Membership is null)
        {
            Notice = user is null ? RefusalNotices[SuspensionOutcome.NoSuchAccount] : NoMembershipNotice;
            return RedirectToPage();
        }

        if (user.Membership.Status == MembershipStatus.Suspended)
        {
            Notice = ApproveSuspendedNotice; // only Unsuspend lifts a suspension
            return RedirectToPage();
        }

        if (user.Membership.IsMerged)
        {
            Notice = ApproveMergedNotice; // only the merge ledger leaves Merged
            return RedirectToPage();
        }

        user.Membership.Status = MembershipStatus.Active;
        user.Membership.ApprovedAtUtc = timeProvider.GetUtcNow();
        ApplicationBootstrapper.EnsureRole(
            user.Membership,
            MemberRole.StandardMember,
            Actor.UserAccount.Id,
            timeProvider.GetUtcNow(),
            "Approved into the member alpha.");
        ApplyMembershipFlags(user.Membership);

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            Actor.UserAccount.Id,
            nameof(Membership),
            user.Membership.Id,
            "membership.approved",
            $"Approved {user.DisplayName} as an active member.",
            cancellationToken);

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSuspendAsync(Guid userAccountId, string? reason, CancellationToken cancellationToken) =>
        await ChangeSuspensionAsync(userAccountId, reason, suspend: true, cancellationToken);

    public async Task<IActionResult> OnPostUnsuspendAsync(Guid userAccountId, string? reason, CancellationToken cancellationToken) =>
        await ChangeSuspensionAsync(userAccountId, reason, suspend: false, cancellationToken);

    private async Task<IActionResult> ChangeSuspensionAsync(Guid userAccountId, string? reason, bool suspend, CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);
        var outcome = suspend
            ? await suspension.SuspendAsync(Actor, userAccountId, reason, cancellationToken)
            : await suspension.UnsuspendAsync(Actor, userAccountId, reason, cancellationToken);
        if (outcome == SuspensionOutcome.NotAdmin)
        {
            return Forbid();
        }

        Notice = RefusalNotices.GetValueOrDefault(outcome);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleRoleAsync(Guid userAccountId, MemberRole role, CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);
        if (!Actor.CanManageMembership || Actor.UserAccount is null)
        {
            return Forbid();
        }

        var user = await dbContext.UserAccounts
            .Include(x => x.Membership!)
                .ThenInclude(x => x.RoleAssignments)
            .SingleOrDefaultAsync(x => x.Id == userAccountId, cancellationToken);

        if (user is null)
        {
            Notice = RefusalNotices[SuspensionOutcome.NoSuchAccount];
            return RedirectToPage();
        }

        if (user.Membership is null)
        {
            Notice = NoMembershipNotice;
            return RedirectToPage();
        }

        if (user.Id == Actor.UserAccount.Id && role == MemberRole.PlatformAdmin)
        {
            Notice = OwnAdminRoleNotice;
            return RedirectToPage();
        }

        if (user.Membership.Status == MembershipStatus.Suspended)
        {
            Notice = RoleOnSuspendedNotice;
            return RedirectToPage();
        }

        var existingAssignment = user.Membership.RoleAssignments.SingleOrDefault(x => x.Role == role);
        if (existingAssignment is null)
        {
            user.Membership.RoleAssignments.Add(new RoleAssignment
            {
                Role = role,
                AssignedByUserAccountId = Actor.UserAccount.Id,
                AssignedAtUtc = timeProvider.GetUtcNow(),
                Notes = "Assigned from the member admin page."
            });
        }
        else
        {
            // Out of the collection too, so the flags below derive from the roles as they will stand after the save.
            user.Membership.RoleAssignments.Remove(existingAssignment);
            dbContext.RoleAssignments.Remove(existingAssignment);
        }

        if (user.Membership.Status == MembershipStatus.Active)
        {
            ApplicationBootstrapper.EnsureRole(
                user.Membership,
                MemberRole.StandardMember,
                Actor.UserAccount.Id,
                timeProvider.GetUtcNow(),
                "Default active member role.");
        }

        ApplyMembershipFlags(user.Membership);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            Actor.UserAccount.Id,
            nameof(Membership),
            user.Membership.Id,
            "membership.role-toggled",
            $"Toggled role {role} for {user.DisplayName}.",
            cancellationToken);

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSetTierAsync(
        Guid userAccountId,
        TierSnapshotKind kind,
        string label,
        decimal weight,
        string notes,
        CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);
        if (!Actor.CanManageMembership || Actor.UserAccount is null)
        {
            return Forbid();
        }

        var user = await dbContext.UserAccounts
            .Include(x => x.Membership!)
                .ThenInclude(x => x.TierSnapshots)
            .SingleOrDefaultAsync(x => x.Id == userAccountId, cancellationToken);

        if (user is null)
        {
            Notice = RefusalNotices[SuspensionOutcome.NoSuchAccount];
            return RedirectToPage();
        }

        if (user.Membership is null)
        {
            return RedirectToPage();
        }

        var now = timeProvider.GetUtcNow();
        foreach (var snapshot in user.Membership.TierSnapshots.Where(x => x.Kind == kind && x.IsCurrent))
        {
            snapshot.IsCurrent = false;
        }

        if (!string.IsNullOrWhiteSpace(label) || weight > 0)
        {
            user.Membership.TierSnapshots.Add(new TierSnapshot
            {
                Kind = kind,
                Label = string.IsNullOrWhiteSpace(label) ? "Tier snapshot" : label.Trim(),
                Weight = weight,
                IsCurrent = true,
                EffectiveFromUtc = now,
                CapturedAtUtc = now,
                Notes = notes.Trim()
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            Actor.UserAccount.Id,
            nameof(TierSnapshot),
            user.Membership.Id,
            "membership.tier-updated",
            $"Updated {kind} tier snapshot for {user.DisplayName}.",
            cancellationToken);

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUpdateProfileAsync(CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);
        if (Actor.UserAccount is null)
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync(cancellationToken);
            return Page();
        }

        var user = await dbContext.UserAccounts
            .Include(x => x.MemberProfile)
            .SingleAsync(x => x.Id == Actor.UserAccount.Id, cancellationToken);

        user.DisplayName = Profile.DisplayName.Trim();
        user.MemberProfile ??= new MemberProfile
        {
            UserAccountId = user.Id,
            UpdatedAtUtc = timeProvider.GetUtcNow()
        };

        user.MemberProfile.Nickname = Profile.Nickname.Trim();
        user.MemberProfile.Headline = Profile.Headline.Trim();
        user.MemberProfile.PortfolioUrl = Profile.PortfolioUrl.Trim();
        user.MemberProfile.Skills = Profile.Skills.Trim();
        user.MemberProfile.Availability = Profile.Availability.Trim();
        user.MemberProfile.Notes = Profile.Notes.Trim();
        user.MemberProfile.UpdatedAtUtc = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            Actor.UserAccount.Id,
            nameof(MemberProfile),
            user.MemberProfile.Id,
            "member-profile.updated",
            $"Updated member profile for {user.DisplayName}.",
            cancellationToken);

        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);

        var users = await dbContext.UserAccounts
            .AsNoTracking()
            .Include(x => x.MemberProfile)
            .Include(x => x.Membership!)
                .ThenInclude(x => x.RoleAssignments)
            .Include(x => x.Membership!)
                .ThenInclude(x => x.TierSnapshots)
            .OrderBy(x => x.DisplayName)
            .ToListAsync(cancellationToken);

        Users = users.Select(user =>
        {
            var patronTier = user.Membership?.TierSnapshots
                .Where(x => x.Kind == TierSnapshotKind.Patron && x.IsCurrent)
                .OrderByDescending(x => x.CapturedAtUtc)
                .FirstOrDefault();
            var contributorTier = user.Membership?.TierSnapshots
                .Where(x => x.Kind == TierSnapshotKind.Contributor && x.IsCurrent)
                .OrderByDescending(x => x.CapturedAtUtc)
                .FirstOrDefault();

            return new MemberDirectoryItem(
                user.Id,
                user.DisplayName,
                user.GitHubLogin,
                user.MemberProfile?.Nickname ?? string.Empty,
                user.MemberProfile?.Headline ?? string.Empty,
                user.MemberProfile?.PortfolioUrl ?? string.Empty,
                user.MemberProfile?.Skills ?? string.Empty,
                user.MemberProfile?.Availability ?? string.Empty,
                user.Membership?.Status ?? MembershipStatus.Authenticated,
                Actor.CanManageMembership ? user.Membership?.SuspensionReason ?? string.Empty : string.Empty,
                user.Membership?.RoleAssignments.Select(x => x.Role).OrderBy(x => x.ToString()).ToArray() ?? [],
                patronTier?.Label ?? string.Empty,
                patronTier?.Weight ?? 0m,
                contributorTier?.Label ?? string.Empty,
                contributorTier?.Weight ?? 0m);
        }).ToList();

        Invitations = await dbContext.MembershipInvitations
            .AsNoTracking()
            .Where(x => x.RevokedAtUtc == null)
            .OrderByDescending(x => x.IssuedAtUtc)
            .ToListAsync(cancellationToken);

        if (Actor.UserAccount is not null)
        {
            var currentUser = users.SingleOrDefault(x => x.Id == Actor.UserAccount.Id);
            if (currentUser is not null)
            {
                Profile = new UpdateProfileInput
                {
                    DisplayName = currentUser.DisplayName,
                    Nickname = currentUser.MemberProfile?.Nickname ?? string.Empty,
                    Headline = currentUser.MemberProfile?.Headline ?? string.Empty,
                    PortfolioUrl = currentUser.MemberProfile?.PortfolioUrl ?? string.Empty,
                    Skills = currentUser.MemberProfile?.Skills ?? string.Empty,
                    Availability = currentUser.MemberProfile?.Availability ?? string.Empty,
                    Notes = currentUser.MemberProfile?.Notes ?? string.Empty
                };
            }
        }
    }

    private static void ApplyMembershipFlags(Bifrost.Web.Domain.Membership membership)
    {
        var roles = membership.RoleAssignments.Select(x => x.Role).ToHashSet();
        membership.IsPlatformAdmin = roles.Contains(MemberRole.PlatformAdmin);
        membership.CanManageProjects = membership.IsPlatformAdmin || roles.Contains(MemberRole.Producer);
        membership.CanManageLedger = membership.IsPlatformAdmin || roles.Contains(MemberRole.LedgerReviewer);
        membership.CanModerateMotions = membership.IsPlatformAdmin || roles.Contains(MemberRole.Producer) || roles.Contains(MemberRole.Maintainer);
    }

    public sealed class InviteMemberInput
    {
        [Required, StringLength(120)]
        [Display(Name = "GitHub login")]
        public string GitHubLogin { get; set; } = string.Empty;

        [StringLength(500)]
        public string Notes { get; set; } = string.Empty;
    }

    public sealed class UpdateProfileInput
    {
        [Required, StringLength(200)]
        [Display(Name = "Display name")]
        public string DisplayName { get; set; } = string.Empty;

        [StringLength(120)]
        public string Nickname { get; set; } = string.Empty;

        [StringLength(240)]
        public string Headline { get; set; } = string.Empty;

        [Display(Name = "Portfolio URL")]
        [StringLength(500)]
        public string PortfolioUrl { get; set; } = string.Empty;

        [StringLength(2000)]
        public string Skills { get; set; } = string.Empty;

        [StringLength(500)]
        public string Availability { get; set; } = string.Empty;

        [StringLength(2000)]
        public string Notes { get; set; } = string.Empty;
    }

    public sealed record MemberDirectoryItem(
        Guid UserAccountId,
        string DisplayName,
        string GitHubLogin,
        string Nickname,
        string Headline,
        string PortfolioUrl,
        string Skills,
        string Availability,
        MembershipStatus Status,
        string SuspensionReason,
        IReadOnlyList<MemberRole> Roles,
        string PatronTierLabel,
        decimal PatronTierWeight,
        string ContributorTierLabel,
        decimal ContributorTierWeight);
}
