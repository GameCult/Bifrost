using System.Security.Claims;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Features.Shared;
using Bifrost.Web.Pages.App.Members;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bifrost.Web.Tests;

/// <summary>Bootstrap promotion never lifts a suspension and leaves a receipt when it changes status; demoting an admin clears the admin flags.</summary>
public sealed class BootstrapAdminTests
{
    private const long BootstrapGitHubId = 4242;

    private static (Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory, CapturingLoggerProvider Logs) NewFactory()
    {
        var logs = new CapturingLoggerProvider();
        var factory = new TestWebApplicationFactory().WithWebHostBuilder(b => b.ConfigureServices(s => s.AddLogging(l => l.AddProvider(logs))));
        return (factory, logs);
    }

    private static async Task<Guid> SeedBootstrapLoginAsync(IServiceProvider services, MembershipStatus status)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var account = new UserAccount
        {
            DisplayName = "Test Admin",
            GitHubUserId = BootstrapGitHubId,
            GitHubLogin = "test-admin",
            NormalizedGitHubLogin = "TEST-ADMIN",
            BifrostIdentity = "testadmin",
            NormalizedBifrostIdentity = "testadmin",
            MemberProfile = new MemberProfile { Nickname = "ta" },
            Membership = new Bifrost.Web.Domain.Membership { Status = status, ApprovedAtUtc = status == MembershipStatus.Suspended ? DateTimeOffset.UtcNow : null }
        };
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private static async Task<(Bifrost.Web.Domain.Membership Membership, List<AuditEvent> Receipts)> ReadAsync(IServiceProvider services, Guid accountId)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var membership = await db.Memberships.AsNoTracking().Include(x => x.RoleAssignments).SingleAsync(x => x.UserAccountId == accountId);
        var receipts = await db.AuditEvents.AsNoTracking().Where(x => x.EntityId == membership.Id.ToString()).ToListAsync();
        return (membership, receipts);
    }

    private static ClaimsPrincipal BootstrapPrincipal() => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, BootstrapGitHubId.ToString()),
        new Claim(ClaimTypes.Name, "test-admin")
    ], "test"));

    [Theory]
    [InlineData("startup")]
    [InlineData("signin")]
    public async Task A_suspended_bootstrap_login_stays_suspended_and_unpromoted(string path)
    {
        var (factory, logs) = NewFactory();
        using var _ = factory;
        var id = await SeedBootstrapLoginAsync(factory.Services, MembershipStatus.Suspended);
        var before = (await ReadAsync(factory.Services, id)).Receipts.Count;

        await RunPathAsync(factory.Services, path);

        var (membership, receipts) = await ReadAsync(factory.Services, id);
        Assert.Equal(MembershipStatus.Suspended, membership.Status);
        Assert.False(membership.IsPlatformAdmin);
        Assert.Empty(membership.RoleAssignments);
        Assert.Equal(before, receipts.Count);
        Assert.Contains(logs.Lines, x => x.Contains("suspended account") && x.Contains(id.ToString()));
    }

    [Theory]
    [InlineData("startup")]
    [InlineData("signin")]
    public async Task A_bootstrap_promotion_that_changes_status_writes_a_receipt(string path)
    {
        var (factory, _) = NewFactory();
        using var _f = factory;
        var id = await SeedBootstrapLoginAsync(factory.Services, MembershipStatus.Authenticated);

        await RunPathAsync(factory.Services, path);

        var (membership, receipts) = await ReadAsync(factory.Services, id);
        Assert.Equal(MembershipStatus.Active, membership.Status);
        Assert.True(membership.IsPlatformAdmin);
        var receipt = Assert.Single(receipts, x => x.Action == "membership.bootstrap-promoted");
        Assert.Contains("Authenticated -> Active", receipt.Detail);

        await RunPathAsync(factory.Services, path); // already Active: no second receipt
        Assert.Single((await ReadAsync(factory.Services, id)).Receipts, x => x.Action == "membership.bootstrap-promoted");
    }

    private static async Task RunPathAsync(IServiceProvider services, string path)
    {
        using var scope = services.CreateScope();
        if (path == "startup")
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationBootstrapper>().RunAsync(CancellationToken.None);
        }
        else
        {
            await scope.ServiceProvider.GetRequiredService<MembershipSynchronizationService>().SynchronizeAsync(BootstrapPrincipal(), CancellationToken.None);
        }
    }

    private sealed class FixedActor(CurrentBifrostActor actor) : ICurrentBifrostActorAccessor
    {
        public Task<CurrentBifrostActor> GetAsync(CancellationToken cancellationToken) => Task.FromResult(actor);
    }

    private static async Task<Guid> SeedAsync(IServiceProvider services, string name, bool admin)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var membership = new Bifrost.Web.Domain.Membership { Status = MembershipStatus.Active, ApprovedAtUtc = DateTimeOffset.UtcNow };
        if (admin)
        {
            membership.IsPlatformAdmin = membership.CanManageProjects = membership.CanManageLedger = membership.CanModerateMotions = true;
            membership.RoleAssignments.Add(new RoleAssignment { Role = MemberRole.PlatformAdmin });
        }

        var account = new UserAccount
        {
            DisplayName = name,
            BifrostIdentity = $"{name}-{Guid.NewGuid():N}"[..20],
            NormalizedBifrostIdentity = $"{name}-{Guid.NewGuid():N}"[..20],
            Membership = membership
        };
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private static async Task ToggleAsync(IServiceProvider services, Guid by, Guid target, MemberRole role)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var actor = new CurrentBifrostActor(true, await db.UserAccounts.Include(x => x.Membership!).ThenInclude(x => x.RoleAssignments).SingleAsync(x => x.Id == by));
        var page = new IndexModel(db, new FixedActor(actor), scope.ServiceProvider.GetRequiredService<AuditTrailService>(),
            scope.ServiceProvider.GetRequiredService<MembershipSuspensionService>(), TimeProvider.System);
        Assert.IsType<RedirectToPageResult>(await page.OnPostToggleRoleAsync(target, role, CancellationToken.None));
    }

    [Fact]
    public async Task Demoting_an_admin_through_the_page_clears_the_admin_flags_and_promoting_sets_them()
    {
        var (factory, _) = NewFactory();
        using var _f = factory;
        var actingAdmin = await SeedAsync(factory.Services, "acting", admin: true);
        var otherAdmin = await SeedAsync(factory.Services, "other", admin: true);
        var member = await SeedAsync(factory.Services, "member", admin: false);

        await ToggleAsync(factory.Services, actingAdmin, otherAdmin, MemberRole.PlatformAdmin);
        var (demoted, _) = await ReadAsync(factory.Services, otherAdmin);
        Assert.False(demoted.IsPlatformAdmin);
        Assert.False(demoted.CanManageProjects);
        Assert.False(demoted.CanManageLedger);
        Assert.False(demoted.CanModerateMotions);
        Assert.DoesNotContain(demoted.RoleAssignments, x => x.Role == MemberRole.PlatformAdmin);

        await ToggleAsync(factory.Services, actingAdmin, member, MemberRole.PlatformAdmin);
        var (promoted, _) = await ReadAsync(factory.Services, member);
        Assert.True(promoted.IsPlatformAdmin);
        Assert.Contains(promoted.RoleAssignments, x => x.Role == MemberRole.PlatformAdmin);
    }
}
