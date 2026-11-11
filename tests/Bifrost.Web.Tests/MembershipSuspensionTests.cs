using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Features.Shared;
using Bifrost.Web.Pages.App.Members;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.Web.Tests;

/// <summary>A suspension is an admin's decision: only the admin path sets or lifts it, with a receipt, and the members page's Approve cannot lift it.</summary>
public sealed class MembershipSuspensionTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public MembershipSuspensionTests(TestWebApplicationFactory factory) => _factory = factory;

    private sealed record Seeded(Guid AdminId, Guid MemberId, Guid OrdinaryId, Guid OtherAdminId);

    private async Task<Seeded> SeedAsync(MembershipStatus memberStatus = MembershipStatus.Active, bool approved = true)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        UserAccount Account(string name, MembershipStatus status, bool admin, bool wasApproved) => new()
        {
            DisplayName = name,
            BifrostIdentity = $"{name}-{Guid.NewGuid():N}"[..20],
            NormalizedBifrostIdentity = $"{name}-{Guid.NewGuid():N}"[..20],
            Membership = new Bifrost.Web.Domain.Membership
            {
                Status = status,
                IsPlatformAdmin = admin,
                ApprovedAtUtc = wasApproved ? DateTimeOffset.UtcNow : null
            }
        };

        var admin = Account("admin", MembershipStatus.Active, true, true);
        var otherAdmin = Account("admin2", MembershipStatus.Active, true, true);
        var member = Account("member", memberStatus, false, approved);
        var ordinary = Account("ordinary", MembershipStatus.Active, false, true);
        db.UserAccounts.AddRange(admin, otherAdmin, member, ordinary);
        await db.SaveChangesAsync();
        return new Seeded(admin.Id, member.Id, ordinary.Id, otherAdmin.Id);
    }

    private async Task<T> InScopeAsync<T>(Func<IServiceProvider, BifrostDbContext, Task<T>> body)
    {
        using var scope = _factory.Services.CreateScope();
        return await body(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<BifrostDbContext>());
    }

    private Task<CurrentBifrostActor> ActorAsync(Guid id) => InScopeAsync(async (_, db) =>
        new CurrentBifrostActor(true, await db.UserAccounts.Include(x => x.Membership!).ThenInclude(x => x.RoleAssignments).SingleAsync(x => x.Id == id)));

    private Task<MembershipStatus> StatusAsync(Guid id) => InScopeAsync(async (_, db) =>
        (await db.Memberships.AsNoTracking().SingleAsync(x => x.UserAccountId == id)).Status);

    private Task<List<AuditEvent>> AuditAsync(string action) => InScopeAsync((_, db) =>
        db.AuditEvents.AsNoTracking().Where(x => x.Action == action).ToListAsync());

    private Task<SuspensionOutcome> SuspendAsync(Guid by, Guid target, string? reason) => InScopeAsync(async (sp, _) =>
        await sp.GetRequiredService<MembershipSuspensionService>().SuspendAsync(await ActorAsync(by), target, reason, CancellationToken.None));

    private Task<SuspensionOutcome> UnsuspendAsync(Guid by, Guid target, string? reason) => InScopeAsync(async (sp, _) =>
        await sp.GetRequiredService<MembershipSuspensionService>().UnsuspendAsync(await ActorAsync(by), target, reason, CancellationToken.None));

    [Fact]
    public async Task Only_a_platform_admin_can_suspend_and_the_receipt_names_who_and_why()
    {
        var seed = await SeedAsync();

        Assert.Equal(SuspensionOutcome.NotAdmin, await SuspendAsync(seed.OrdinaryId, seed.MemberId, "because"));
        Assert.Equal(MembershipStatus.Active, await StatusAsync(seed.MemberId));
        Assert.Equal(SuspensionOutcome.NoReason, await SuspendAsync(seed.AdminId, seed.MemberId, "  "));
        Assert.Equal(MembershipStatus.Active, await StatusAsync(seed.MemberId));

        Assert.Equal(SuspensionOutcome.Done, await SuspendAsync(seed.AdminId, seed.MemberId, "spam in chat"));
        Assert.Equal(MembershipStatus.Suspended, await StatusAsync(seed.MemberId));
        var receipt = Assert.Single(await AuditAsync("membership.suspended"), x => x.Detail.Contains("spam in chat"));
        Assert.Equal(seed.AdminId, receipt.ActorUserAccountId);
        Assert.Contains("Active -> Suspended", receipt.Detail);
    }

    [Fact]
    public async Task An_admin_cannot_suspend_themselves_or_another_platform_admin()
    {
        var seed = await SeedAsync();

        Assert.Equal(SuspensionOutcome.ProtectedAccount, await SuspendAsync(seed.AdminId, seed.AdminId, "oops"));
        Assert.Equal(SuspensionOutcome.ProtectedAccount, await SuspendAsync(seed.AdminId, seed.OtherAdminId, "coup"));
        Assert.Equal(MembershipStatus.Active, await StatusAsync(seed.AdminId));
        Assert.Equal(MembershipStatus.Active, await StatusAsync(seed.OtherAdminId));
    }

    [Theory]
    [InlineData(true, MembershipStatus.Active)]
    [InlineData(false, MembershipStatus.Authenticated)]
    public async Task Unsuspend_works_only_by_the_admin_path_and_with_a_reason(bool approved, MembershipStatus restored)
    {
        var seed = await SeedAsync(MembershipStatus.Suspended, approved);

        Assert.Equal(SuspensionOutcome.NotAdmin, await UnsuspendAsync(seed.OrdinaryId, seed.MemberId, "please"));
        Assert.Equal(SuspensionOutcome.NoReason, await UnsuspendAsync(seed.AdminId, seed.MemberId, null));
        Assert.Equal(MembershipStatus.Suspended, await StatusAsync(seed.MemberId));

        Assert.Equal(SuspensionOutcome.Done, await UnsuspendAsync(seed.AdminId, seed.MemberId, "appeal upheld"));
        Assert.Equal(restored, await StatusAsync(seed.MemberId));
        Assert.Contains(await AuditAsync("membership.unsuspended"), x => x.Detail.Contains("appeal upheld") && x.ActorUserAccountId == seed.AdminId);
        Assert.Equal(SuspensionOutcome.NotApplicable, await UnsuspendAsync(seed.AdminId, seed.MemberId, "again"));
    }

    [Fact]
    public async Task The_members_page_Approve_cannot_lift_a_suspension()
    {
        var seed = await SeedAsync(MembershipStatus.Suspended);

        var result = await InScopeAsync(async (sp, db) =>
        {
            var page = new IndexModel(
                db, new FixedActor(await ActorAsync(seed.AdminId)), sp.GetRequiredService<AuditTrailService>(),
                sp.GetRequiredService<MembershipSuspensionService>(), TimeProvider.System);
            return await page.OnPostApproveAsync(seed.MemberId, CancellationToken.None);
        });

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(MembershipStatus.Suspended, await StatusAsync(seed.MemberId));
    }

    [Fact]
    public async Task The_members_page_suspend_handler_forbids_a_non_admin_and_changes_nothing()
    {
        var seed = await SeedAsync();

        var result = await InScopeAsync(async (sp, db) =>
        {
            var page = new IndexModel(
                db, new FixedActor(await ActorAsync(seed.OrdinaryId)), sp.GetRequiredService<AuditTrailService>(),
                sp.GetRequiredService<MembershipSuspensionService>(), TimeProvider.System);
            return await page.OnPostSuspendAsync(seed.MemberId, "nope", CancellationToken.None);
        });

        Assert.IsType<ForbidResult>(result);
        Assert.Equal(MembershipStatus.Active, await StatusAsync(seed.MemberId));
    }

    private sealed class FixedActor(CurrentBifrostActor actor) : ICurrentBifrostActorAccessor
    {
        public Task<CurrentBifrostActor> GetAsync(CancellationToken cancellationToken) => Task.FromResult(actor);
    }
}
