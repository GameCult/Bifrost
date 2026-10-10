using System.Globalization;
using System.Text;
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
    Retire,
    Retired
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

    // Defaulted bounds (a Mill manifest is tens of ships): the whole file is refused above either.
    public const int MaxManifestBytes = 1024 * 1024;

    public const int MaxShips = 1000;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    // The Mill manifest is JSON from outside the Verse. Only ships[].id and ships[].name are read.
    // This is the one input boundary: every malformed input leaves as a ManifestRefusedException.
    public static IReadOnlyList<ManifestShip> ParseManifest(ReadOnlyMemory<byte> utf8)
    {
        using var document = ParseDocument(utf8);
        var ships = ShipsOf(document.RootElement);
        if (ships.ValueKind != JsonValueKind.Array)
        {
            throw new ManifestRefusedException("ships", "Missing");
        }

        if (ships.GetArrayLength() > MaxShips)
        {
            throw new ManifestRefusedException("ships", "TooMany");
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

            if (!IsValidTitle(title))
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

    private static JsonElement ShipsOf(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        return root.TryGetProperty("ships", out var ships) ? ships : default;
    }

    // A displayed title: no control, format (bidi, zero-width, BOM), line or paragraph separator
    // character, no leading or trailing whitespace, and at least one letter or digit. This is the
    // character-class rule of the mind schema's Title type, kept here as the one copy in Bifrost.
    private static bool IsValidTitle(string title)
    {
        if (title.Length == 0 || title != title.Trim())
        {
            return false;
        }

        var hasLetterOrDigit = false;
        foreach (var rune in title.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                return false;
            }

            hasLetterOrDigit |= Rune.IsLetterOrDigit(rune);
        }

        return hasLetterOrDigit;
    }

    private static JsonDocument ParseDocument(ReadOnlyMemory<byte> utf8)
    {
        if (utf8.Length > MaxManifestBytes)
        {
            throw new ManifestRefusedException("manifest", "TooLarge");
        }

        try
        {
            StrictUtf8.GetCharCount(utf8.Span);
        }
        catch (DecoderFallbackException)
        {
            throw new ManifestRefusedException("manifest", "NotUtf8");
        }

        try
        {
            RejectDuplicateProperties(utf8.Span);
            return JsonDocument.Parse(utf8);
        }
        catch (JsonException)
        {
            throw new ManifestRefusedException("manifest", "NotJson");
        }
        catch (InvalidOperationException)
        {
            // A property name holding a lone surrogate escape: not text.
            throw new ManifestRefusedException("manifest", "BadText");
        }
    }

    // System.Text.Json (net8.0) keeps the last of two equal property names; a manifest that says
    // one thing twice is refused instead.
    private static void RejectDuplicateProperties(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8);
        var scopes = new Stack<HashSet<string>?>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.StartArray:
                    scopes.Push(null);
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    scopes.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    if (!scopes.Peek()!.Add(reader.GetString()!))
                    {
                        throw new ManifestRefusedException("manifest", "DuplicateProperty");
                    }

                    break;
            }
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

        // A named key that an earlier run already retired is done: a rerun converges.
        foreach (var key in named.OrderBy(x => x, StringComparer.Ordinal).ToList())
        {
            if (registered.TryGetValue(key, out var done) && done.RetiredAtUtc is not null && !inManifest.Contains(key))
            {
                entries.Add(new SubjectPlanEntry(SubjectChange.Retired, key, done.Title));
                named.Remove(key);
            }
        }

        // Whatever is still named is not a registered key missing from the manifest.
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
            if (entry.Change is SubjectChange.Unchanged or SubjectChange.Absent or SubjectChange.Retired)
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
        if (ship.ValueKind != JsonValueKind.Object)
        {
            throw new ManifestRefusedException($"{at}.{name}", "Missing");
        }

        if (!ship.TryGetProperty(name, out var value))
        {
            throw new ManifestRefusedException($"{at}.{name}", "Missing");
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ManifestRefusedException($"{at}.{name}", "NotString");
        }

        try
        {
            return value.GetString()!;
        }
        catch (InvalidOperationException)
        {
            // A lone surrogate escape: not text.
            throw new ManifestRefusedException($"{at}.{name}", "BadText");
        }
    }
}
