using Bifrost.Web.Configuration;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Features.Shared;

public sealed class ApplicationBootstrapper(
    BifrostDbContext dbContext,
    IOptions<BootstrapOptions> bootstrapOptions,
    TimeProvider timeProvider,
    ILogger<ApplicationBootstrapper> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (dbContext.Database.IsRelational() && bootstrapOptions.Value.ApplyMigrationsOnStartup)
        {
            logger.LogInformation("Applying database migrations on startup.");
            await dbContext.Database.MigrateAsync(cancellationToken);
        }

        var adminLogins = bootstrapOptions.Value.AdminGitHubLogins
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToUpperInvariant())
            .Distinct()
            .ToArray();

        if (adminLogins.Length == 0)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var adminUsers = await dbContext.UserAccounts
            .Include(x => x.Membership!)
                .ThenInclude(x => x.RoleAssignments)
            .Where(x => adminLogins.Contains(x.NormalizedGitHubLogin))
            .ToListAsync(cancellationToken);

        foreach (var adminUser in adminUsers.Where(x => x.Membership is not null))
        {
            var membership = adminUser.Membership!;
            membership.Status = MembershipStatus.Active;
            membership.IsPlatformAdmin = true;
            membership.CanManageProjects = true;
            membership.CanManageLedger = true;
            membership.CanModerateMotions = true;
            membership.ApprovedAtUtc ??= now;

            EnsureRole(membership, MemberRole.PlatformAdmin, null, now, "Bootstrap admin");
            EnsureRole(membership, MemberRole.StandardMember, null, now, "Default active member role");
        }

        if (dbContext.ChangeTracker.HasChanges())
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public static void EnsureRole(
        Bifrost.Web.Domain.Membership membership,
        MemberRole role,
        Guid? actorUserAccountId,
        DateTimeOffset assignedAtUtc,
        string note)
    {
        if (membership.RoleAssignments.Any(x => x.Role == role))
        {
            return;
        }

        membership.RoleAssignments.Add(new RoleAssignment
        {
            Role = role,
            AssignedByUserAccountId = actorUserAccountId,
            AssignedAtUtc = assignedAtUtc,
            Notes = note
        });
    }
}
