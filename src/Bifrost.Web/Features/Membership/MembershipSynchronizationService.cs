using System.Security.Claims;
using Bifrost.Web.Configuration;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Features.Membership;

public sealed class MembershipSynchronizationService(
    BifrostDbContext dbContext,
    IOptions<BootstrapOptions> bootstrapOptions,
    TimeProvider timeProvider)
{
    public async Task SynchronizeAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (!TryReadGitHubIdentity(principal, out var gitHubUserId, out var gitHubLogin))
        {
            return;
        }

        var displayName = principal.FindFirstValue(BifrostClaimTypes.DisplayName);
        var avatarUrl = principal.FindFirstValue(BifrostClaimTypes.AvatarUrl);
        var normalizedLogin = Normalize(gitHubLogin);
        var now = timeProvider.GetUtcNow();

        var userAccount = await dbContext.UserAccounts
            .Include(x => x.MemberProfile)
            .Include(x => x.Membership!)
                .ThenInclude(x => x.RoleAssignments)
            .Include(x => x.Membership!)
                .ThenInclude(x => x.TierSnapshots)
            .SingleOrDefaultAsync(x => x.GitHubUserId == gitHubUserId, cancellationToken);

        var invitation = await dbContext.MembershipInvitations
            .SingleOrDefaultAsync(
                x => x.NormalizedGitHubLogin == normalizedLogin && x.RevokedAtUtc == null,
                cancellationToken);

        if (userAccount is null)
        {
            userAccount = new UserAccount
            {
                GitHubUserId = gitHubUserId,
                GitHubLogin = gitHubLogin,
                NormalizedGitHubLogin = normalizedLogin,
                DisplayName = displayName ?? gitHubLogin,
                AvatarUrl = avatarUrl ?? string.Empty,
                CreatedAtUtc = now,
                LastSeenAtUtc = now,
                MemberProfile = new MemberProfile
                {
                    Nickname = displayName ?? gitHubLogin,
                    Headline = "New member record",
                    UpdatedAtUtc = now
                },
                Membership = new Bifrost.Web.Domain.Membership
                {
                    Status = invitation is null ? MembershipStatus.Authenticated : MembershipStatus.PendingApproval,
                    CreatedAtUtc = now
                }
            };

            dbContext.UserAccounts.Add(userAccount);
        }
        else
        {
            userAccount.GitHubLogin = gitHubLogin;
            userAccount.NormalizedGitHubLogin = normalizedLogin;
            userAccount.DisplayName = string.IsNullOrWhiteSpace(displayName) ? gitHubLogin : displayName;
            userAccount.AvatarUrl = avatarUrl ?? userAccount.AvatarUrl;
            userAccount.LastSeenAtUtc = now;

            userAccount.MemberProfile ??= new MemberProfile
            {
                UserAccountId = userAccount.Id,
                Nickname = userAccount.DisplayName,
                Headline = "Imported from GitHub sign-in",
                UpdatedAtUtc = now
            };

            userAccount.Membership ??= new Bifrost.Web.Domain.Membership
            {
                UserAccountId = userAccount.Id,
                CreatedAtUtc = now
            };

            if (invitation is not null && userAccount.Membership.Status == MembershipStatus.Authenticated)
            {
                userAccount.Membership.Status = MembershipStatus.PendingApproval;
            }
        }

        if (string.IsNullOrWhiteSpace(userAccount.MemberProfile!.Nickname))
        {
            userAccount.MemberProfile.Nickname = userAccount.DisplayName;
        }

        if (invitation is not null && invitation.AcceptedByUserAccountId is null)
        {
            invitation.AcceptedByUserAccountId = userAccount.Id;
            invitation.AcceptedAtUtc = now;
        }

        if (IsBootstrapAdmin(gitHubLogin, bootstrapOptions.Value))
        {
            PromoteToBootstrapAdmin(userAccount.Membership!, now);
        }
        else if (userAccount.Membership!.Status == MembershipStatus.Active)
        {
            ApplicationBootstrapper.EnsureRole(
                userAccount.Membership,
                MemberRole.StandardMember,
                null,
                now,
                "Default active member role");
        }

        userAccount.MemberProfile!.UpdatedAtUtc = now;

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static void PromoteToBootstrapAdmin(Bifrost.Web.Domain.Membership membership, DateTimeOffset now)
    {
        membership.Status = MembershipStatus.Active;
        membership.IsPlatformAdmin = true;
        membership.CanManageProjects = true;
        membership.CanManageLedger = true;
        membership.CanModerateMotions = true;
        membership.ApprovedAtUtc ??= now;

        ApplicationBootstrapper.EnsureRole(membership, MemberRole.PlatformAdmin, null, now, "Bootstrap admin");
        ApplicationBootstrapper.EnsureRole(membership, MemberRole.StandardMember, null, now, "Default active member role");
    }

    private static bool TryReadGitHubIdentity(
        ClaimsPrincipal principal,
        out long gitHubUserId,
        out string gitHubLogin)
    {
        gitHubLogin = principal.FindFirstValue(BifrostClaimTypes.GitHubLogin)
            ?? principal.FindFirstValue(ClaimTypes.Name)
            ?? string.Empty;

        var idClaim = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!long.TryParse(idClaim, out gitHubUserId) || string.IsNullOrWhiteSpace(gitHubLogin))
        {
            gitHubUserId = 0;
            return false;
        }

        return true;
    }

    private static string Normalize(string gitHubLogin) =>
        gitHubLogin.Trim().ToUpperInvariant();

    private static bool IsBootstrapAdmin(string gitHubLogin, BootstrapOptions options) =>
        options.AdminGitHubLogins.Any(login =>
            string.Equals(login, gitHubLogin, StringComparison.OrdinalIgnoreCase));
}
