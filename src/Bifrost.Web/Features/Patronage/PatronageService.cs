using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Shared;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Web.Features.Patronage;

public sealed class PatronageService(
    BifrostDbContext dbContext,
    AuditTrailService auditTrailService,
    TimeProvider timeProvider)
{
    public async Task<PatronSupportEvent> RecordSupportEventAsync(
        Guid actorUserAccountId,
        Guid userAccountId,
        PatronSupportEventKind kind,
        decimal amount,
        string currencyCode,
        string externalSupportId,
        DateTimeOffset supportedAtUtc,
        bool isCurrentRecurringSupport,
        string notes,
        CancellationToken cancellationToken)
    {
        var user = await dbContext.UserAccounts
            .Include(x => x.Membership)
            .SingleAsync(x => x.Id == userAccountId, cancellationToken);

        if (user.Membership is null || user.Membership.Status != MembershipStatus.Active)
        {
            throw new InvalidOperationException("Patron support can only be recorded for an active member.");
        }

        if (amount <= 0)
        {
            throw new InvalidOperationException("Patron support amount must be greater than zero.");
        }

        var now = timeProvider.GetUtcNow();
        var normalizedCurrency = string.IsNullOrWhiteSpace(currencyCode)
            ? "USD"
            : currencyCode.Trim().ToUpperInvariant();

        if (kind == PatronSupportEventKind.RecurringSupportSnapshot && isCurrentRecurringSupport)
        {
            var existingCurrentEvents = await dbContext.PatronSupportEvents
                .Where(x => x.UserAccountId == userAccountId &&
                    x.Kind == PatronSupportEventKind.RecurringSupportSnapshot &&
                    x.IsCurrentRecurringSupport)
                .ToListAsync(cancellationToken);

            foreach (var existing in existingCurrentEvents)
            {
                existing.IsCurrentRecurringSupport = false;
            }
        }

        var supportEvent = new PatronSupportEvent
        {
            UserAccountId = userAccountId,
            Kind = kind,
            ExternalSupportId = externalSupportId.Trim(),
            Amount = amount,
            CurrencyCode = normalizedCurrency,
            IsCurrentRecurringSupport = kind == PatronSupportEventKind.RecurringSupportSnapshot && isCurrentRecurringSupport,
            SupportedAtUtc = supportedAtUtc.ToUniversalTime(),
            RecordedAtUtc = now,
            Notes = notes.Trim()
        };

        dbContext.PatronSupportEvents.Add(supportEvent);
        dbContext.PointTransactions.Add(new PointTransaction
        {
            UserAccountId = userAccountId,
            Type = PointTransactionType.PatronSupport,
            Amount = amount,
            IsDecaying = kind != PatronSupportEventKind.RecurringSupportSnapshot,
            CreatedAtUtc = now,
            Note = $"Recorded {kind} patron support event {supportEvent.ExternalSupportId}."
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            actorUserAccountId,
            nameof(PatronSupportEvent),
            supportEvent.Id,
            "patron-support.recorded",
            $"Recorded {amount:0.##} {normalizedCurrency} {kind} patron support for {user.DisplayName}.",
            cancellationToken);

        await RefreshPatronTierSnapshotAsync(actorUserAccountId, userAccountId, cancellationToken);
        return supportEvent;
    }

    public async Task<PatronPointSummary> RefreshPatronTierSnapshotAsync(
        Guid actorUserAccountId,
        Guid userAccountId,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();

        var user = await dbContext.UserAccounts
            .Include(x => x.Membership)
            .Include(x => x.PatronSupportEvents)
            .SingleAsync(x => x.Id == userAccountId, cancellationToken);

        if (user.Membership is null)
        {
            throw new InvalidOperationException("Cannot refresh patron tier for a user without membership.");
        }

        var summary = CalculatePatronPoints(user.PatronSupportEvents, timeProvider.GetUtcNow());
        var tier = PatronTierPolicy.ResolveTier(summary.EffectivePoints);
        var now = timeProvider.GetUtcNow();

        var currentPatronSnapshots = await dbContext.TierSnapshots
            .Where(x => x.MembershipId == user.Membership.Id &&
                x.Kind == TierSnapshotKind.Patron &&
                x.IsCurrent)
            .ToListAsync(cancellationToken);

        foreach (var snapshot in currentPatronSnapshots)
        {
            snapshot.IsCurrent = false;
        }

        if (tier is not null)
        {
            dbContext.TierSnapshots.Add(new TierSnapshot
            {
                MembershipId = user.Membership.Id,
                Kind = TierSnapshotKind.Patron,
                Label = PatronTierPolicy.SnapshotLabel(tier),
                Weight = tier.VotingWeight,
                IsCurrent = true,
                EffectiveFromUtc = now,
                CapturedAtUtc = now,
                Notes = PatronTierPolicy.SnapshotNote(summary.EffectivePoints, now)
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            actorUserAccountId,
            nameof(TierSnapshot),
            user.Membership.Id,
            "patron-tier.derived",
            tier is null
                ? $"Cleared patron tier for {user.DisplayName}; {summary.EffectivePoints:0.##} effective patron points."
                : $"Derived {tier.Label} patron tier for {user.DisplayName}; {summary.EffectivePoints:0.##} effective patron points.",
            cancellationToken);

        return summary with { TierLabel = tier?.Label ?? string.Empty, VotingWeight = tier?.VotingWeight ?? 0m };
    }

    public static PatronPointSummary CalculatePatronPoints(
        IEnumerable<PatronSupportEvent> supportEvents,
        DateTimeOffset nowUtc)
    {
        var events = supportEvents.ToList();
        var recurringPoints = events
            .Where(x => x.Kind == PatronSupportEventKind.RecurringSupportSnapshot && x.IsCurrentRecurringSupport)
            .Sum(x => Math.Max(0m, x.Amount));

        var historicalPoints = events
            .Where(x => x.Kind != PatronSupportEventKind.RecurringSupportSnapshot || !x.IsCurrentRecurringSupport)
            .Sum(x => PatronTierPolicy.GetHistoricalDonationPoints(x, nowUtc));

        var effectivePoints = recurringPoints + historicalPoints;
        var tier = PatronTierPolicy.ResolveTier(effectivePoints);

        return new PatronPointSummary(
            recurringPoints,
            historicalPoints,
            effectivePoints,
            tier?.Label ?? string.Empty,
            tier?.VotingWeight ?? 0m);
    }
}

public sealed record PatronPointSummary(
    decimal RecurringPoints,
    decimal HistoricalPoints,
    decimal EffectivePoints,
    string TierLabel,
    decimal VotingWeight);
