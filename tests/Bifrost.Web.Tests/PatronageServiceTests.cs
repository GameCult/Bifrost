using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Patronage;
using Bifrost.Web.Features.Shared;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Web.Tests;

public sealed class PatronageServiceTests
{
    [Fact]
    public void Historical_patron_support_halves_after_first_month_and_decays_weekly()
    {
        var now = new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);
        var supportEvent = new PatronSupportEvent
        {
            Kind = PatronSupportEventKind.OneTimeDonation,
            Amount = 100m,
            SupportedAtUtc = now.AddDays(-44)
        };

        var points = PatronTierPolicy.GetHistoricalDonationPoints(supportEvent, now);

        Assert.Equal(49m, points);
    }

    [Fact]
    public async Task Recording_patron_support_writes_audit_point_transaction_and_derived_tier()
    {
        await using var dbContext = CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var actor = new UserAccount
        {
            DisplayName = "Admin",
            GitHubLogin = "admin",
            NormalizedGitHubLogin = "admin",
            CreatedAtUtc = now,
            LastSeenAtUtc = now
        };
        actor.Membership = new Membership
        {
            UserAccountId = actor.Id,
            Status = MembershipStatus.Active,
            CreatedAtUtc = now
        };
        var patron = new UserAccount
        {
            DisplayName = "Patron",
            GitHubLogin = "patron",
            NormalizedGitHubLogin = "patron",
            CreatedAtUtc = now,
            LastSeenAtUtc = now
        };
        patron.Membership = new Membership
        {
            UserAccountId = patron.Id,
            Status = MembershipStatus.Active,
            CreatedAtUtc = now
        };
        dbContext.UserAccounts.AddRange(actor, patron);
        await dbContext.SaveChangesAsync();

        var auditTrail = new AuditTrailService(dbContext, TimeProvider.System);
        var patronage = new PatronageService(dbContext, auditTrail, TimeProvider.System);

        await patronage.RecordSupportEventAsync(
            actor.Id,
            patron.Id,
            PatronSupportEventKind.RecurringSupportSnapshot,
            125m,
            "usd",
            "patreon-member-1",
            now,
            isCurrentRecurringSupport: true,
            "Imported from Patreon current support.",
            CancellationToken.None);

        var supportEvent = await dbContext.PatronSupportEvents.SingleAsync();
        var pointTransaction = await dbContext.PointTransactions.SingleAsync();
        var tierSnapshot = await dbContext.TierSnapshots.SingleAsync(x => x.Kind == TierSnapshotKind.Patron && x.IsCurrent);

        Assert.Equal(PatronSupportEventKind.RecurringSupportSnapshot, supportEvent.Kind);
        Assert.True(supportEvent.IsCurrentRecurringSupport);
        Assert.Equal("USD", supportEvent.CurrencyCode);
        Assert.Equal(PointTransactionType.PatronSupport, pointTransaction.Type);
        Assert.False(pointTransaction.IsDecaying);
        Assert.Equal("Patron Silver", tierSnapshot.Label);
        Assert.Equal(2m, tierSnapshot.Weight);
        Assert.Contains("125", tierSnapshot.Notes);
        Assert.Contains(dbContext.AuditEvents, x => x.Action == "patron-support.recorded");
        Assert.Contains(dbContext.AuditEvents, x => x.Action == "patron-tier.derived");
    }

    private static BifrostDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<BifrostDbContext>()
            .UseInMemoryDatabase($"patronage-{Guid.NewGuid():N}")
            .Options;

        return new BifrostDbContext(options);
    }
}
