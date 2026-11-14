using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Features.Shared;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Web.Features.Patronage;

public sealed class PatronageService(
    BifrostDbContext dbContext,
    AuditTrailService auditTrailService,
    TimeProvider timeProvider)
{
    private const string CurrentPatronIndex = "UX_TierSnapshots_MembershipId_CurrentPatron";

    /// <summary>
    /// Records a patron event for an account, through the merge ledger's write gate: an event for a folded account lands on its survivor
    /// and is journalled in the same save (an undo moves it back); an event for a frozen account lands on it, where the commit's
    /// reduction finds it. A provider retry returns the stored event and, if it sits on a folded account's survivor, journals it once.
    /// </summary>
    public async Task<PatronSupportEvent> RecordSupportEventAsync(
        Guid? actorUserAccountId,
        Guid userAccountId,
        PatronSupportEventKind kind,
        decimal amount,
        string currencyCode,
        string externalSupportId,
        ExternalPatronProvider provider,
        string providerEventId,
        string providerPayerId,
        string providerSubscriptionId,
        DateTimeOffset supportedAtUtc,
        bool isCurrentRecurringSupport,
        string notes,
        CancellationToken cancellationToken,
        Guid? projectId = null)
    {
        var normalizedProviderEventId = providerEventId.Trim();
        var (supportEvent, recordedOn, isNew, recordedFor) = await AccountMergeLedger.WriteAsAsync(dbContext, userAccountId, async standing =>
        {
            var folded = standing as MergeStanding.Folded;
            var recordOnId = folded?.SurvivorId ?? userAccountId;
            var now = timeProvider.GetUtcNow();

            var existingProviderEvent = string.IsNullOrWhiteSpace(normalizedProviderEventId)
                ? null
                : await dbContext.PatronSupportEvents
                    .SingleOrDefaultAsync(x => x.Provider == provider && x.ProviderEventId == normalizedProviderEventId, cancellationToken);

            if (existingProviderEvent is not null)
            {
                if (folded is not null && existingProviderEvent.UserAccountId == folded.SurvivorId)
                {
                    await AccountMergeLedger.StageIntakeJournalAsync(dbContext, folded.MergeId, userAccountId, existingProviderEvent.Id, now, cancellationToken);
                    await dbContext.SaveChangesAsync(cancellationToken);
                }

                return (existingProviderEvent, recordOnId, false, string.Empty);
            }

            var user = await dbContext.UserAccounts
                .Include(x => x.Membership)
                .SingleAsync(x => x.Id == recordOnId, cancellationToken);

            if (user.Membership is null || user.Membership.Status == MembershipStatus.Suspended)
            {
                throw new PatronSupportRefusedException();
            }

            if (amount <= 0 && kind != PatronSupportEventKind.SupportAdjustment)
            {
                throw new InvalidOperationException("Patron support amount must be greater than zero.");
            }

            if (amount == 0)
            {
                throw new InvalidOperationException("Patron support amount must not be zero.");
            }

            if (kind == PatronSupportEventKind.SupportAdjustment && amount > 0)
            {
                throw new InvalidOperationException("Patron support adjustments must reduce support.");
            }

            if (provider != ExternalPatronProvider.Manual && string.IsNullOrWhiteSpace(normalizedProviderEventId))
            {
                throw new InvalidOperationException("External patron provider events require a provider event id.");
            }

            var normalizedCurrency = string.IsNullOrWhiteSpace(currencyCode)
                ? "USD"
                : currencyCode.Trim().ToUpperInvariant();

            if (kind == PatronSupportEventKind.RecurringSupportSnapshot && isCurrentRecurringSupport)
            {
                var existingCurrentEvents = await dbContext.PatronSupportEvents
                    .Where(x => x.UserAccountId == recordOnId &&
                        x.Kind == PatronSupportEventKind.RecurringSupportSnapshot &&
                        x.IsCurrentRecurringSupport)
                    .ToListAsync(cancellationToken);

                foreach (var existing in existingCurrentEvents)
                {
                    existing.IsCurrentRecurringSupport = false;
                }
            }

            var created = new PatronSupportEvent
            {
                UserAccountId = recordOnId,
                ProjectId = projectId,
                Kind = kind,
                ExternalSupportId = externalSupportId.Trim(),
                Provider = provider,
                ProviderEventId = normalizedProviderEventId,
                ProviderPayerId = providerPayerId.Trim(),
                ProviderSubscriptionId = providerSubscriptionId.Trim(),
                Amount = amount,
                CurrencyCode = normalizedCurrency,
                IsCurrentRecurringSupport = kind == PatronSupportEventKind.RecurringSupportSnapshot && isCurrentRecurringSupport,
                SupportedAtUtc = supportedAtUtc.ToUniversalTime(),
                RecordedAtUtc = now,
                Notes = notes.Trim()
            };

            dbContext.PatronSupportEvents.Add(created);
            dbContext.PointTransactions.Add(new PointTransaction
            {
                UserAccountId = recordOnId,
                ProjectId = projectId,
                Type = PointTransactionType.PatronSupport,
                Amount = amount,
                IsDecaying = kind == PatronSupportEventKind.OneTimeDonation,
                CreatedAtUtc = now,
                Note = $"Recorded {provider} {kind} patron support event {created.ExternalSupportId}."
            });

            if (folded is not null)
            {
                await AccountMergeLedger.StageIntakeJournalAsync(dbContext, folded.MergeId, userAccountId, created.Id, now, cancellationToken);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return (created, recordOnId, true, user.DisplayName);
        }, cancellationToken);

        if (isNew)
        {
            await auditTrailService.RecordAsync(
                actorUserAccountId,
                nameof(PatronSupportEvent),
                supportEvent.Id,
                "patron-support.recorded",
                $"Recorded {amount:0.##} {supportEvent.CurrencyCode} {provider} {kind} patron support for {recordedFor}.",
                cancellationToken);

            await RefreshPatronTierSnapshotAsync(actorUserAccountId, recordedOn, cancellationToken);
        }

        return supportEvent;
    }

    public async Task<PatronPointSummary> RefreshPatronTierSnapshotAsync(
        Guid? actorUserAccountId,
        Guid userAccountId,
        CancellationToken cancellationToken)
    {
        // One current Patron snapshot per membership is a database invariant (UX_TierSnapshots_MembershipId_CurrentPatron). A restage that
        // lost a race to another restage of the same membership reads the winner's snapshot and derives again from the events it now sees.
        UserAccount user;
        PatronPointSummary summary;
        PatronTierDefinition? tier;
        for (var attempt = 0; ; attempt++)
        {
            dbContext.ChangeTracker.Clear();

            user = await dbContext.UserAccounts
                .Include(x => x.Membership)
                .Include(x => x.PatronSupportEvents)
                .SingleAsync(x => x.Id == userAccountId, cancellationToken);

            if (user.Membership is null)
            {
                throw new InvalidOperationException("Cannot refresh patron tier for a user without membership.");
            }

            (summary, tier) = await StageTierSnapshotAsync(user.Membership, user.PatronSupportEvents, timeProvider.GetUtcNow(), cancellationToken);

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                break;
            }
            catch (DbUpdateException error) when (attempt < 3 &&
                error.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation, ConstraintName: CurrentPatronIndex })
            {
            }
        }

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

    /// <summary>
    /// Derives the membership's patron tier from the events once and stages it on the tracker: demotes the current Patron snapshots
    /// and adds the new one. It never saves and never clears the tracker, so a caller can stage it inside a larger single save
    /// (the account merge ledger does).
    /// </summary>
    public async Task<(PatronPointSummary Summary, PatronTierDefinition? Tier)> StageTierSnapshotAsync(
        Bifrost.Web.Domain.Membership membership,
        IEnumerable<PatronSupportEvent> events,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var summary = CalculatePatronPoints(events, now);
        var tier = PatronTierPolicy.ResolveTier(summary.EffectivePoints);

        var currentPatronSnapshots = await dbContext.TierSnapshots
            .Where(x => x.MembershipId == membership.Id &&
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
                MembershipId = membership.Id,
                Kind = TierSnapshotKind.Patron,
                Label = PatronTierPolicy.SnapshotLabel(tier),
                Weight = tier.VotingWeight,
                IsCurrent = true,
                EffectiveFromUtc = now,
                CapturedAtUtc = now,
                Notes = PatronTierPolicy.SnapshotNote(summary.EffectivePoints, now)
            });
        }

        return (summary, tier);
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

/// <summary>The account patron support would record on has no membership or is suspended.</summary>
public sealed class PatronSupportRefusedException() : InvalidOperationException("Patron support can only be recorded for an unsuspended Bifrost account.");

public sealed record PatronPointSummary(
    decimal RecurringPoints,
    decimal HistoricalPoints,
    decimal EffectivePoints,
    string TierLabel,
    decimal VotingWeight);
