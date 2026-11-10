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
        var token = await credential.AuthenticateAsync(http, cancellationToken);
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
        var accountId = await OidcEndpoints.ActiveMemberIdAsync(db, token.Subject, cancellationToken);
        if (accountId is null)
        {
            await WriteRefusalAsync(http, DeskRefusals.Forbidden);
            return;
        }

        // 6. The body's shape. The bytes read are the bytes forwarded.
        byte[] body = [];
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

        // 7. The {mind} lookup: a miss is the same document as an unknown fork.
        var mind = minds.Resolve(route.RawMind);
        if (mind is null)
        {
            await WriteRefusalAsync(http, DeskRefusals.ForkUnknown);
            return;
        }

        // 8-10. The door.
        var subject = accountId.Value.ToString("D");
        var context = new DeskDoorContext(subject, desk.Attribution.AuthorityOf(subject), mind.Deciders);
        try
        {
            var outcome = await door.AnswerAsync(mind.Socket, context, body, cancellationToken);
            switch (outcome)
            {
                case DeskAnswerOutcome.NotStored(var code) when DoorMayRefuse(code):
                    await WriteRefusalAsync(http, code);
                    return;
                case DeskAnswerOutcome.Answered(var result) when DeskStatus.Of(result) is { } status:
                    if (string.Equals(result.Answer.Code, DeskStatus.StaleFork, StringComparison.Ordinal))
                    {
                        var current = await door.ReadAsync(mind.Socket, context, result.Answer.ForkId, cancellationToken);
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
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await WriteUnavailableAsync(http, desk);
        }
    }

    // Codes the door may hand back for nothing stored. The three the edge alone judges are never the door's.
    private static bool DoorMayRefuse(string code) =>
        code is not (DeskRefusals.Unauthenticated or DeskRefusals.InsufficientScope or DeskRefusals.Forbidden) &&
        DeskRefusals.IsKnown(code);

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
    Answer,
}

public sealed record DeskRoute(DeskRouteKind Kind, string RawMind)
{
    // The raw request target against the three routes. Segments stay percent-encoded: {mind} is decoded once, by
    // the resolver, and nothing here normalises a dot segment. Any other shape is not a desk route.
    public static DeskRoute? Parse(string method, string rawTarget)
    {
        var path = rawTarget;
        var query = path.IndexOf('?');
        if (query >= 0)
        {
            path = path[..query];
        }

        if (!path.StartsWith(DeskEndpoints.RoutePrefix + "/", StringComparison.Ordinal))
        {
            return null;
        }

        var segments = path[(DeskEndpoints.RoutePrefix.Length + 1)..].Split('/');
        return method switch
        {
            "POST" when segments is [var mind, "answers"] => new DeskRoute(DeskRouteKind.Answer, mind),
            _ => null,
        };
    }
}
