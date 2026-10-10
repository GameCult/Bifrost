using System.Text.Json;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Shared;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Web.Features.ConceptVotes;

public enum SubjectChange
{
    Add,
    Rename,
    Unchanged,
    Revive,
    Absent,
    Retire
}

public sealed record ManifestShip(string Key, string Title);

public sealed record SubjectPlanEntry(SubjectChange Change, string Key, string Title);

public sealed record SubjectPlan(string Collection, IReadOnlyList<SubjectPlanEntry> Entries);

// A refusal names the field and a code, never the offending value.
public sealed class ManifestRefusedException(string field, string code)
    : Exception($"{field}: {code}")
{
    public string Field { get; } = field;

    public string Code { get; } = code;
}

// The only writer of ConceptSubject: decides which concept keys exist, their titles and their retirement.
public sealed class ConceptSubjectRegistry(
    BifrostDbContext dbContext,
    AuditTrailService auditTrail,
    TimeProvider timeProvider)
{
    public const int MaxTitleLength = 240;

    // The Mill manifest is JSON from outside the Verse. Only ships[].id and ships[].name are read.
    public static IReadOnlyList<ManifestShip> ParseManifest(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new ManifestRefusedException("manifest", "NotJson");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("ships", out var ships) ||
                ships.ValueKind != JsonValueKind.Array)
            {
                throw new ManifestRefusedException("ships", "Missing");
            }

            var result = new List<ManifestShip>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ship in ships.EnumerateArray())
            {
                var at = $"ships[{result.Count}]";
                var key = ReadString(ship, "id", at);
                var title = ReadString(ship, "name", at);
                if (!ConceptKey.IsValid(key))
                {
                    throw new ManifestRefusedException($"{at}.id", "InvalidKey");
                }

                if (!seen.Add(key))
                {
                    throw new ManifestRefusedException($"{at}.id", "Duplicate");
                }

                if (string.IsNullOrWhiteSpace(title) || title.Any(char.IsControl))
                {
                    throw new ManifestRefusedException($"{at}.name", "InvalidTitle");
                }

                if (title.Length > MaxTitleLength)
                {
                    throw new ManifestRefusedException($"{at}.name", "TooLong");
                }

                result.Add(new ManifestShip(key, title));
            }

            return result;
        }
    }

    public async Task<SubjectPlan> PlanAsync(
        string collection,
        IReadOnlyList<ManifestShip> ships,
        IReadOnlyCollection<string> retire,
        CancellationToken cancellationToken)
    {
        if (!ConceptKey.IsValid(collection))
        {
            throw new ManifestRefusedException("collection", "InvalidKey");
        }

        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in retire)
        {
            if (!ConceptKey.IsValid(key))
            {
                throw new ManifestRefusedException("retire", "InvalidKey");
            }

            named.Add(key);
        }

        var registered = await dbContext.ConceptSubjects.AsNoTracking()
            .Where(x => x.Collection == collection)
            .ToDictionaryAsync(x => x.Key, StringComparer.Ordinal, cancellationToken);

        var entries = new List<SubjectPlanEntry>();
        foreach (var ship in ships)
        {
            var change = !registered.TryGetValue(ship.Key, out var row)
                ? SubjectChange.Add
                : row.RetiredAtUtc is not null
                    ? SubjectChange.Revive
                    : row.Title != ship.Title ? SubjectChange.Rename : SubjectChange.Unchanged;
            entries.Add(new SubjectPlanEntry(change, ship.Key, ship.Title));
        }

        var inManifest = ships.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var absent = registered.Values
            .Where(x => x.RetiredAtUtc is null && !inManifest.Contains(x.Key))
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToList();
        foreach (var subject in absent)
        {
            entries.Add(new SubjectPlanEntry(
                named.Remove(subject.Key) ? SubjectChange.Retire : SubjectChange.Absent,
                subject.Key,
                subject.Title));
        }

        // Whatever is still named is not a registered, live key missing from the manifest.
        if (named.Count > 0)
        {
            throw new ManifestRefusedException("retire", "NotAbsent");
        }

        return new SubjectPlan(collection, entries);
    }

    public async Task<SubjectPlan> ApplyAsync(
        string collection,
        IReadOnlyList<ManifestShip> ships,
        IReadOnlyCollection<string> retire,
        CancellationToken cancellationToken)
    {
        var plan = await PlanAsync(collection, ships, retire, cancellationToken);
        var rows = await dbContext.ConceptSubjects
            .Where(x => x.Collection == collection)
            .ToDictionaryAsync(x => x.Key, StringComparer.Ordinal, cancellationToken);

        foreach (var entry in plan.Entries)
        {
            if (entry.Change is SubjectChange.Unchanged or SubjectChange.Absent)
            {
                continue;
            }

            ConceptSubject subject;
            if (entry.Change == SubjectChange.Add)
            {
                subject = new ConceptSubject
                {
                    Collection = collection,
                    Key = entry.Key,
                    Title = entry.Title,
                    RegisteredAtUtc = timeProvider.GetUtcNow()
                };
                dbContext.ConceptSubjects.Add(subject);
            }
            else
            {
                subject = rows[entry.Key];
                if (entry.Change == SubjectChange.Retire)
                {
                    subject.RetiredAtUtc = timeProvider.GetUtcNow();
                }
                else
                {
                    subject.Title = entry.Title;
                    subject.RetiredAtUtc = null;
                }
            }

            // The audit write saves the subject change in the same SaveChanges.
            await auditTrail.RecordAsync(
                null,
                nameof(ConceptSubject),
                subject.Id,
                entry.Change.ToString(),
                $"actor=host-command collection={collection} key={entry.Key}",
                cancellationToken);
        }

        return plan;
    }

    private static string ReadString(JsonElement ship, string name, string at)
    {
        if (ship.ValueKind != JsonValueKind.Object || !ship.TryGetProperty(name, out var value))
        {
            throw new ManifestRefusedException($"{at}.{name}", "Missing");
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new ManifestRefusedException($"{at}.{name}", "NotString");
    }
}
