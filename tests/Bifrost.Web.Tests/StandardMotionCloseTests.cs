using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Motions;
using Bifrost.Web.Features.Shared;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Web.Tests;

public sealed class StandardMotionCloseTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => start;
    }

    [Fact]
    public async Task A_second_simultaneous_close_adopts_the_winners_close_and_records_nothing_more()
    {
        var clock = new Clock(T0);
        var name = $"standard-close-race-{Guid.NewGuid():N}";
        BifrostDbContext NewDb() => new(new DbContextOptionsBuilder<BifrostDbContext>().UseInMemoryDatabase(name).Options);
        MotionGovernanceService NewService(BifrostDbContext db) => new(db, new AuditTrailService(db, clock), clock);

        await using var seed = NewDb();
        var user = new UserAccount { DisplayName = "Mod", GitHubLogin = "mod", BifrostIdentity = "mod", NormalizedBifrostIdentity = "MOD", NormalizedGitHubLogin = "MOD" };
        user.Membership = new Membership { UserAccountId = user.Id, Status = MembershipStatus.Active, CanModerateMotions = true };
        seed.UserAccounts.Add(user);
        await seed.SaveChangesAsync();
        var actor = new CurrentBifrostActor(true, user);
        var motion = await NewService(seed).CreateMotionAsync(
            actor,
            new CreateMotionCommand(MotionScope.Management, null, MotionCategory.Features, "Race", "Two closers.", T0.AddDays(1)),
            default);

        await using var first = NewDb();
        await using var second = NewDb();
        _ = await second.Motions.ToListAsync(); // second now holds a stale, still-Open copy

        await NewService(first).CloseMotionAsync(actor, motion.Id, default);
        await NewService(second).CloseMotionAsync(actor, motion.Id, default); // the Status token refuses this write

        await using var check = NewDb();
        var row = await check.Motions.SingleAsync();
        Assert.Equal(MotionStatus.Closed, row.Status);
        Assert.Equal(1, await check.AuditEvents.CountAsync(x => x.Action == "motion.closed"));
    }
}
