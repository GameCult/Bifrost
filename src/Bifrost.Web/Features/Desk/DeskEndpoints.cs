using System.Text.Json;
using Bifrost.Web.Configuration;
using Bifrost.Web.Data;
using Bifrost.Web.Features.Oidc;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Bifrost.Web.Features.Desk;

// The fork desk's HTTP edge (fork-card.md, Transport). One catch-all under /api/desk judges, in this order and no
// other: the raw target, the bearer, its audience, its scope, Active membership (the body is unread), the body's
// shape, the {mind} lookup, the door. Every refusal is a contract document. Bifrost holds no fork state: it passes
// Huginn's outcome through, attaches the current card to a stale_fork answer, and says `unavailable` when the door
// cannot confirm an outcome.
public static class DeskEndpoints
{
    public const string RoutePrefix = "/api/desk";
    public const string Audience = "bifrost-desk";
    public const string ReadScope = "desk.read";
    public const string AnswerScope = "desk.answer";
    public const string CorsPolicy = "DeskCors";

    public static IServiceCollection AddBifrostDesk(this IServiceCollection services)
    {
        services.AddSingleton(provider => new DeskMindRoutes(provider.GetRequiredService<IOptions<DeskOptions>>().Value.Minds));
        services.AddCors();
        // Exactly the configured origin, no credentials, and only on the routes that require this policy.
        services
            .AddOptions<CorsOptions>()
            .Configure<IOptions<DeskOptions>>((cors, desk) =>
                cors.AddPolicy(
                    CorsPolicy,
                    policy => policy
                        .WithOrigins(desk.Value.AllowedOrigin)
                        .WithMethods("GET", "POST")
                        .WithHeaders(HeaderNames.Authorization, HeaderNames.ContentType)));
        return services;
    }

    public static IEndpointConventionBuilder MapDesk(this IEndpointRouteBuilder app)
    {
        // A configured desk with no credential or door port would serve nothing true: stop startup instead.
        app.ServiceProvider.GetRequiredService<IDeskCredential>();
        app.ServiceProvider.GetRequiredService<IDeskDoor>();

        return app.MapMethods(RoutePrefix + "/{**rest}", ["GET", "POST"], HandleAsync)
            .RequireCors(CorsPolicy)
            .AllowAnonymous();
    }

    private static async Task HandleAsync(
        HttpContext http,
        IDeskCredential credential,
        IDeskDoor door,
        DeskMindRoutes minds,
        IOptions<DeskOptions> options,
        BifrostDbContext db)
    {
        var cancellationToken = http.RequestAborted;
        var desk = options.Value;
        http.Response.Headers.Append(HeaderNames.Vary, "Origin");
        http.Response.Headers.CacheControl = "no-store";

        // 1. The raw target, never the decoded Request.Path.
        var rawTarget = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget ?? string.Empty;
        var route = DeskRoute.Parse(http.Request.Method, rawTarget);
        if (route is null)
        {
            await WriteRefusalAsync(http, DeskRefusals.ForkUnknown);
            return;
        }

        // 2-4. Bearer, audience, scope.
        DeskToken? token;
        try
        {
            token = await credential.AuthenticateAsync(http, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The bearer could not be judged: neither a pass nor a refusal of the caller.
            await WriteUnavailableAsync(http, desk);
            return;
        }

        if (token is null || !string.Equals(token.Audience, Audience, StringComparison.Ordinal))
        {
            await WriteRefusalAsync(http, DeskRefusals.Unauthenticated);
            return;
        }

        var scope = route.Kind == DeskRouteKind.Answer ? AnswerScope : ReadScope;
        if (!token.Scopes.Contains(scope, StringComparer.Ordinal))
        {
            await WriteRefusalAsync(http, DeskRefusals.InsufficientScope);
            return;
        }

        // 5. Active membership, re-read now; the body has not been touched.
        Guid? accountId;
        try
        {
            accountId = await OidcEndpoints.ActiveMemberIdAsync(db, token.Subject, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await WriteUnavailableAsync(http, desk);
            return;
        }

        if (accountId is null)
        {
            await WriteRefusalAsync(http, DeskRefusals.Forbidden);
            return;
        }

        // 6. The request's shape. An answer's bytes are the bytes forwarded; list and read judge their query.
        byte[] body = [];
        DeskListRequest? listRequest = null;
        var forkId = string.Empty;
        if (route.Kind == DeskRouteKind.Answer)
        {
            var read = await ReadBodyAsync(http.Request, desk.MaxBodyBytes, cancellationToken);
            var verdict = read is null
                ? DeskBodyVerdict.Refuse(DeskRefusals.BodyInvalid)
                : DeskBodyShape.Judge(read, desk.MaxBodyBytes);
            if (verdict.RefusalCode is not null)
            {
                await WriteRefusalAsync(http, verdict.RefusalCode);
                return;
            }

            body = read!;
        }
        else if (route.Kind == DeskRouteKind.Read
                     ? !DeskQuery.TryRead(route.RawQuery, route.RawFork!, out forkId)
                     : !DeskQuery.TryList(route.RawQuery, string.Empty, out listRequest))
        {
            await WriteRefusalAsync(http, DeskRefusals.RequestInvalid);
            return;
        }

        // 7. The {mind} lookup: a miss is the same document as an unknown fork.
        var mind = minds.Resolve(route.RawMind);
        if (mind is null)
        {
            await WriteRefusalAsync(http, DeskRefusals.ForkUnknown);
            return;
        }

        // 8-10. The door. A decider list outside 1..64 unique ids is a configuration fault: nothing is sent.
        if (!DeskMindOptions.DecidersWithinBounds(mind.Deciders))
        {
            await WriteUnavailableAsync(http, desk);
            return;
        }

        var subject = accountId.Value.ToString("D");
        var context = new DeskDoorContext(mind.Id, desk.Attribution.AuthorityOf(subject), subject, mind.Deciders.ToArray());
        try
        {
            switch (route.Kind)
            {
                case DeskRouteKind.List:
                    var listed = await door.ListAsync(mind.Socket, context, listRequest! with { Mind = mind.Id }, cancellationToken);
                    if (listed is DeskListOutcome.Listed(var list))
                    {
                        await WriteJsonAsync(http, 200, IsDecider(context) ? list : DeskNotes.Strip(list));
                    }
                    else if (listed is DeskListOutcome.ForkTooLarge(var largeId, var largeOrder))
                    {
                        await WriteJsonAsync(http, DeskRefusals.ForkTooLargeStatus, DeskRefusals.ForkTooLargeDocument(largeId, largeOrder));
                    }
                    else if (listed is DeskListOutcome.NotListed(var listCode) && DoorMayRefuse(listCode) && listCode != DeskRefusals.RequestIdConflict)
                    {
                        await WriteRefusalAsync(http, ReadListCode(listCode));
                    }
                    else
                    {
                        await WriteUnavailableAsync(http, desk);
                    }

                    return;
                case DeskRouteKind.Read:
                    var found = await door.ReadAsync(mind.Socket, context, forkId, cancellationToken);
                    if (found is DeskReadOutcome.Found(var entry))
                    {
                        await WriteJsonAsync(http, 200, IsDecider(context) ? entry : DeskNotes.Strip(entry));
                    }
                    else if (found is DeskReadOutcome.ForkTooLarge(var largeFork))
                    {
                        await WriteJsonAsync(http, DeskRefusals.ForkTooLargeStatus, DeskRefusals.ForkTooLargeDocument(largeFork));
                    }
                    else if (found is DeskReadOutcome.NotFound(var readCode) && DoorMayRefuse(readCode) && readCode != DeskRefusals.RequestIdConflict)
                    {
                        await WriteRefusalAsync(http, ReadListCode(readCode));
                    }
                    else
                    {
                        await WriteUnavailableAsync(http, desk);
                    }

                    return;
                default:
                    await AnswerAsync(http, door, mind, context, body, desk);
                    return;
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await WriteUnavailableAsync(http, desk);
        }
    }

    private static async Task AnswerAsync(
        HttpContext http, IDeskDoor door, DeskMindOptions mind, DeskDoorContext context, byte[] body, DeskOptions desk)
    {
        var cancellationToken = http.RequestAborted;
        var outcome = await door.AnswerAsync(mind.Socket, context, body, cancellationToken);
        switch (outcome)
        {
            case DeskAnswerOutcome.NotStored(var code) when DoorMayRefuse(code):
                await WriteRefusalAsync(http, code);
                return;
            case DeskAnswerOutcome.Answered(var result) when DeskStatus.Of(result) is { } status:
                // A stale_fork answer, fresh or replayed, carries the CURRENT card: nothing is stored for it.
                if (string.Equals(result.Answer.Code, DeskStatus.StaleFork, StringComparison.Ordinal))
                {
                    var current = await door.ReadAsync(mind.Socket, context, result.Answer.ForkId, cancellationToken);
                    if (current is DeskReadOutcome.ForkTooLarge(var largeFork))
                    {
                        // Nothing is recorded for a stale answer, and the card it would carry cannot be returned: permanent.
                        await WriteJsonAsync(http, DeskRefusals.ForkTooLargeStatus, DeskRefusals.ForkTooLargeDocument(largeFork));
                        return;
                    }

                    if (current is not DeskReadOutcome.Found(var entry))
                    {
                        await WriteUnavailableAsync(http, desk);
                        return;
                    }

                    result = result with { Card = entry.Card };
                }

                await WriteJsonAsync(http, status, result);
                return;
            default:
                await WriteUnavailableAsync(http, desk);
                return;
        }
    }

    // A note is a decider's own words: only a caller on the admin-set list reads them (visibility ruling c).
    private static bool IsDecider(DeskDoorContext context) =>
        context.Deciders.Contains(context.Subject, StringComparer.Ordinal);

    // Codes the door may hand back for nothing stored. The three the edge alone judges are never the door's.
    private static bool DoorMayRefuse(string code) =>
        code is not (DeskRefusals.Unauthenticated or DeskRefusals.InsufficientScope or DeskRefusals.Forbidden) &&
        DeskRefusals.IsKnown(code);

    // request_id_conflict is the answer's alone (Libby): a read or list carries no request_id, so the door saying it
    // there is a door fault and the edge says `unavailable`.
    // A read or list sends no body. A door that refuses one for its body (an `authority` field among them) is
    // refusing the request, which is request_invalid; it is not an outage.
    private static string ReadListCode(string code) =>
        code is DeskRefusals.BodyForbiddenField or DeskRefusals.BodyUnknownField or DeskRefusals.BodyInvalid
            ? DeskRefusals.RequestInvalid
            : code;

    // At most `cap` bytes. Null when the body is longer, judged by Content-Length first and then while reading.
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, int cap, CancellationToken cancellationToken)
    {
        if (request.ContentLength > cap)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int count;
        while ((count = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + count > cap)
            {
                return null;
            }

            buffer.Write(chunk, 0, count);
        }

        return buffer.ToArray();
    }

    private static Task WriteRefusalAsync(HttpContext http, string code) =>
        WriteJsonAsync(http, DeskRefusals.StatusOf(code), DeskRefusals.Document(code));

    private static async Task WriteUnavailableAsync(HttpContext http, DeskOptions desk)
    {
        http.Response.Headers.RetryAfter = desk.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await WriteJsonAsync(http, DeskUnavailables.Status, DeskUnavailables.Document(desk.RetryAfterSeconds));
    }

    private static async Task WriteJsonAsync<T>(HttpContext http, int status, T document)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(http.Response.Body, document, DeskJson.Options, http.RequestAborted);
    }
}

// The one place a stored outcome becomes an HTTP status. fork-card.md prose says 200 for any stored outcome; the
// vectors (results[].httpStatus) say a Refused answer carries its refusal's status, and the vectors are the check.
public static class DeskStatus
{
    public const string StaleFork = "stale_fork";

    private static readonly Dictionary<string, int> RefusedStatus = new(StringComparer.Ordinal)
    {
        [StaleFork] = 409,
        ["not_a_decider"] = 403,
        ["fork_closed"] = 409,
        ["choice_invalid"] = 400,
        ["bad_retraction"] = 400,
        ["already_retracted"] = 409,
    };

    // Null when the door sent an outcome or a refusal code this contract does not name: Bifrost then cannot vouch
    // for it and says `unavailable`.
    public static int? Of(AnswerResult result) => result.Answer.Outcome switch
    {
        "Counted" or "Revision" or "Retraction" => 200,
        "Refused" when result.Answer.Code is { } code && RefusedStatus.TryGetValue(code, out var status) => status,
        _ => null,
    };
}

public enum DeskRouteKind
{
    List,
    Read,
    Answer,
}

public sealed record DeskRoute(DeskRouteKind Kind, string RawMind, string? RawFork, string RawQuery)
{
    // The raw request target against the three routes. Segments stay percent-encoded: {mind} is decoded once, by
    // the resolver, and nothing here normalises a dot segment. Any other shape is not a desk route.
    public static DeskRoute? Parse(string method, string rawTarget)
    {
        var path = rawTarget;
        var query = string.Empty;
        var split = path.IndexOf('?');
        if (split >= 0)
        {
            query = path[(split + 1)..];
            path = path[..split];
        }

        if (!path.StartsWith(DeskEndpoints.RoutePrefix + "/", StringComparison.Ordinal))
        {
            return null;
        }

        var segments = path[(DeskEndpoints.RoutePrefix.Length + 1)..].Split('/');
        return (method, segments) switch
        {
            ("GET", [var mind, "forks"]) => new DeskRoute(DeskRouteKind.List, mind, null, query),
            ("GET", [var mind, "forks", var fork]) => new DeskRoute(DeskRouteKind.Read, mind, fork, query),
            ("POST", [var mind, "answers"]) => new DeskRoute(DeskRouteKind.Answer, mind, null, query),
            _ => null,
        };
    }
}

// The list and read requests the query string and path stand for (fork-card.md, List and Read). Nothing in a
// query sets a decider, an answerer or a Mind; an unknown key, a repeated key or a bad value is request_invalid.
public static class DeskQuery
{
    public const int MaxLimit = 50;

    public static bool TryList(string rawQuery, string mind, out DeskListRequest request)
    {
        request = new DeskListRequest(mind, null, null, MaxLimit);
        if (!TryPairs(rawQuery, out var pairs))
        {
            return false;
        }

        string? status = null, cursor = null;
        var limit = MaxLimit;
        foreach (var (key, value) in pairs)
        {
            switch (key)
            {
                case "status" when value is "open" or "recorded":
                    status = value;
                    break;
                case "status" when value.Length == 0:
                    break;
                case "cursor" when IsCursor(value):
                    cursor = value;
                    break;
                case "cursor" when value.Length == 0:
                    break;
                case "limit" when int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n <= MaxLimit:
                    limit = n;
                    break;
                default:
                    return false;
            }
        }

        request = new DeskListRequest(mind, status, cursor, limit);
        return true;
    }

    // The read request has no query at all.
    public static bool TryRead(string rawQuery, string rawFork, out string forkId)
    {
        forkId = string.Empty;
        return rawQuery.Length == 0 && DeskMindRoutes.TryDecode(rawFork, out forkId) && forkId.Length > 0;
    }

    // c<N>, N a positive integer without a leading zero.
    private static bool IsCursor(string value) =>
        value.Length > 1 && value[0] == 'c' && value[1] is >= '1' and <= '9' && value.Skip(2).All(char.IsAsciiDigit);

    private static bool TryPairs(string rawQuery, out List<(string Key, string Value)> pairs)
    {
        pairs = [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (rawQuery.Length == 0)
        {
            return true;
        }

        foreach (var part in rawQuery.Split('&'))
        {
            var eq = part.IndexOf('=');
            var rawKey = eq < 0 ? part : part[..eq];
            var rawValue = eq < 0 ? string.Empty : part[(eq + 1)..];
            if (!DeskMindRoutes.TryDecode(rawKey, out var key) || !DeskMindRoutes.TryDecode(rawValue, out var value) || !seen.Add(key))
            {
                return false;
            }

            pairs.Add((key, value));
        }

        return true;
    }
}
