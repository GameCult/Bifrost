using Bifrost.Web.Domain;

namespace Bifrost.Web.Features.Patronage;

public sealed record PatronTierDefinition(string Label, decimal MinimumPoints, decimal VotingWeight);

public static class PatronTierPolicy
{
    public static IReadOnlyList<PatronTierDefinition> Tiers { get; } =
    [
        new("Bronze", 10m, 1m),
        new("Silver", 100m, 2m),
        new("Gold", 1_000m, 3m),
        new("Platinum", 10_000m, 4m),
        new("Unobtanium", 100_000m, 5m)
    ];

    public static PatronTierDefinition? ResolveTier(decimal effectivePoints) =>
        Tiers
            .Where(x => effectivePoints >= x.MinimumPoints)
            .OrderByDescending(x => x.MinimumPoints)
            .FirstOrDefault();

    public static string SnapshotNote(decimal effectivePoints, DateTimeOffset calculatedAtUtc) =>
        $"Derived from patron support events: {effectivePoints:0.##} effective patron points at {calculatedAtUtc:O}.";

    public static string SnapshotLabel(PatronTierDefinition tier) =>
        $"Patron {tier.Label}";

    public static decimal GetHistoricalDonationPoints(PatronSupportEvent supportEvent, DateTimeOffset nowUtc)
    {
        if (supportEvent.Kind == PatronSupportEventKind.RecurringSupportSnapshot &&
            supportEvent.IsCurrentRecurringSupport)
        {
            return Math.Max(0m, supportEvent.Amount);
        }

        if (supportEvent.Kind == PatronSupportEventKind.SupportAdjustment)
        {
            return supportEvent.Amount;
        }

        var amount = Math.Max(0m, supportEvent.Amount);
        var age = nowUtc - supportEvent.SupportedAtUtc.ToUniversalTime();
        if (age < TimeSpan.FromDays(30))
        {
            return amount;
        }

        var weeksAfterFirstMonth = Math.Max(0, (int)Math.Floor((age.TotalDays - 30) / 7));
        var decayed = amount * 0.5m;
        for (var index = 0; index < weeksAfterFirstMonth; index += 1)
        {
            decayed *= 0.99m;
        }

        return Math.Floor(decayed);
    }
}
