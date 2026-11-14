using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bifrost.Web.Configuration;
using Bifrost.Web.Domain;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Features.Membership;

/// <summary>
/// The chat host's pull of merge notices: GET the unacked feed, POST an ack. JSON only because the reader is the chat host's script (a xenos
/// boundary). The bearer is the chat host's own secret (ChatNotices:Token); an empty token turns both routes off. No cookie, no CORS, never stored.
/// </summary>
public static class MergeNoticeEndpoints
{
    private const int MaxAckBodyBytes = 1024;

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/merge-notices/chat", async (HttpContext http, IOptions<ChatNoticesOptions> options, MergeNoticeFeed feed, CancellationToken ct) =>
        {
            if (Gate(http, options.Value) is { } refused)
            {
                return refused;
            }

            DateTimeOffset? after = null;
            if (http.Request.Query.TryGetValue("after", out var raw) && raw.Count > 0)
            {
                if (!DateTimeOffset.TryParse(raw[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                {
                    return Results.StatusCode(StatusCodes.Status400BadRequest);
                }

                after = parsed;
            }

            int? limit = http.Request.Query.TryGetValue("limit", out var rawLimit) && int.TryParse(rawLimit[0], CultureInfo.InvariantCulture, out var n) ? n : null;
            var notices = await feed.ReadAsync(AccountMergeNoticeTarget.Chat, after, limit, ct);
            return Results.Text(JsonSerializer.Serialize(notices.Select(Wire)), "application/json");
        }).AllowAnonymous();

        app.MapPost("/api/merge-notices/chat/ack", async (HttpContext http, IOptions<ChatNoticesOptions> options, MergeNoticeFeed feed, CancellationToken ct) =>
        {
            if (Gate(http, options.Value) is { } refused)
            {
                return refused;
            }

            if (await ReadAckAsync(http.Request, ct) is not var (mergeId, kind))
            {
                return Results.StatusCode(StatusCodes.Status400BadRequest);
            }

            return await feed.AckAsync(AccountMergeNoticeTarget.Chat, mergeId, kind, ct)
                ? Results.StatusCode(StatusCodes.Status204NoContent)
                : Results.StatusCode(StatusCodes.Status404NotFound);
        }).AllowAnonymous();
    }

    private static IResult? Gate(HttpContext http, ChatNoticesOptions options)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (string.IsNullOrEmpty(options.Token))
        {
            return Results.StatusCode(StatusCodes.Status404NotFound);
        }

        var header = http.Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        var presented = header.StartsWith(scheme, StringComparison.Ordinal) ? header[scheme.Length..] : string.Empty;
        // Both sides are hashed first, so the comparison is over equal-length digests and says nothing about the token's length.
        var matches = CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(presented)), SHA256.HashData(Encoding.UTF8.GetBytes(options.Token)));
        return matches && presented.Length > 0 ? null : Results.StatusCode(StatusCodes.Status401Unauthorized);
    }

    private static async Task<(Guid MergeId, AccountMergeNoticeKind Kind)?> ReadAckAsync(HttpRequest request, CancellationToken ct)
    {
        var buffer = new byte[MaxAckBodyBytes + 1];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = await request.Body.ReadAsync(buffer.AsMemory(read), ct)) > 0)
        {
            read += n;
        }

        if (read == 0 || read > MaxAckBodyBytes)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, read));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("merge_id", out var id) || id.ValueKind != JsonValueKind.String || !Guid.TryParse(id.GetString(), out var mergeId) ||
                !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            AccountMergeNoticeKind? kind = type.GetString() switch
            {
                "merged" => AccountMergeNoticeKind.Merged,
                "undone" => AccountMergeNoticeKind.Undone,
                "final" => AccountMergeNoticeKind.Final,
                _ => null
            };
            return kind is { } k ? (mergeId, k) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The wire shape, exactly these keys: ids in format D, times ISO 8601 UTC, no email, display name, Heimdall id or Matrix id.</summary>
    private static Dictionary<string, object?> Wire(MergeNotice notice) => new()
    {
        ["type"] = notice.Kind.ToString().ToLowerInvariant(),
        ["merge_id"] = notice.MergeId.ToString("D"),
        ["occurred_at"] = notice.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        ["grace_ends_at"] = notice.GraceEndsAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        ["old_subject"] = notice.OldSubject.ToString("D"),
        ["new_subject"] = notice.NewSubject.ToString("D"),
        ["old_preferred_username"] = notice.OldPreferredUsername,
        ["new_preferred_username"] = notice.NewPreferredUsername
    };
}
