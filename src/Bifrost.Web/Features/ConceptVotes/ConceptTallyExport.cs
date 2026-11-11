using Bifrost.Web.Configuration;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Bifrost.Web.Features.ConceptVotes;

// The public tally export and the one cross-origin policy in Bifrost. It owns nothing: counts are derived by
// ConceptAppraisalService, origins come from ConceptVoteOptions.
public static class ConceptTallyExport
{
    public const string CorsPolicy = "ConceptTallies";
    private const string RoutePrefix = "/public/concepts";

    // GET only, no credentials, and required by the export route alone: every other route carries no CORS
    // metadata and gets no CORS headers.
    public static IServiceCollection AddConceptTallyCors(this IServiceCollection services)
    {
        services.AddCors();
        services
            .AddOptions<CorsOptions>()
            .Configure<IOptions<ConceptVoteOptions>>((cors, conceptVotes) =>
                cors.AddPolicy(
                    CorsPolicy,
                    policy => policy.WithOrigins(conceptVotes.Value.AllowedOrigins).WithMethods("GET")));
        return services;
    }

    // Replaces app.UseCors(): the one place the origin decision is made. Every response under the export route
    // (200, 404, 304, preflight, refusal) varies by Origin, appended to any Vary already present, because the CORS
    // headers differ by request Origin and a URL-keyed cache must not serve one variant to another. ASP.NET Core
    // adds Vary: Origin itself only for policies of more than one origin; this policy has one, and a repeated token is harmless.
    public static IApplicationBuilder UseConceptTallyCors(this IApplicationBuilder app)
    {
        app.Use((context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(RoutePrefix))
            {
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers.Append(HeaderNames.Vary, "Origin");
                    return Task.CompletedTask;
                });
            }

            return next(context);
        });
        return app.UseCors();
    }

    // Counts only, per live subject: ConceptTally carries no voter key, account id or network tag.
    public static IEndpointConventionBuilder MapConceptTallyExport(this IEndpointRouteBuilder app) =>
        app.MapGet(RoutePrefix + "/{collection}/tallies", async (
            string collection,
            ConceptAppraisalService appraisals,
            HttpResponse response,
            CancellationToken cancellationToken) =>
        {
            var tallies = await appraisals.GetTalliesAsync(collection, cancellationToken);
            if (tallies.Count == 0)
            {
                return Results.NotFound();
            }

            response.Headers.CacheControl = "public, max-age=60";
            return Results.Json(tallies);
        }).AllowAnonymous().RequireCors(CorsPolicy);

    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    // The ranked, paged read beside the map: GET /public/concepts/{collection}/ranked
    //   ?sort=net|total|newest (default net) &limit=1..100 (default 50) &cursor=<opaque> &unvoted=network
    // returns { items: [{ key, title, up, down, members: { up, down } }], next_cursor }. The query is a closed
    // shape: an unknown or repeated key, an out-of-range value or a cursor that is not exactly one this route issued
    // for the same sort and filter is a 400. unvoted=network leaves out the concepts the caller's own network tag
    // has already voted on; the tag is derived from the connection and compared inside the query, never accepted
    // from the request and never returned. That answer depends on the caller, so it is never cached.
    public static IEndpointConventionBuilder MapConceptRankedExport(this IEndpointRouteBuilder app) =>
        app.MapGet(RoutePrefix + "/{collection}/ranked", async (
            string collection,
            HttpContext http,
            ConceptAppraisalService appraisals,
            CancellationToken cancellationToken) =>
        {
            if (!ConceptKey.IsValid(collection))
            {
                return Results.NotFound();
            }

            if (!TryReadRankedQuery(http.Request.Query, out var order, out var limit, out var cursor, out var unvoted))
            {
                return JsonText(new { error = "request_invalid" }, StatusCodes.Status400BadRequest);
            }

            var page = await appraisals.GetRankedAsync(
                collection, order, limit, cursor, unvoted, http.Connection.RemoteIpAddress, cancellationToken);
            if (page is null)
            {
                return Results.NotFound();
            }

            http.Response.Headers.CacheControl = unvoted ? "private, no-store" : "public, max-age=60";
            return JsonText(new
            {
                items = page.Items.Select(i => new
                {
                    key = i.Key,
                    title = i.Title,
                    up = i.Tally.Up,
                    down = i.Tally.Down,
                    members = new { up = i.Tally.Members.Up, down = i.Tally.Members.Down }
                }),
                next_cursor = page.NextCursor
            }, StatusCodes.Status200OK);
        }).AllowAnonymous().RequireCors(CorsPolicy);

    private static IResult JsonText(object body, int status) =>
        Results.Text(System.Text.Json.JsonSerializer.Serialize(body), "application/json", statusCode: status);

    internal static bool TryReadRankedQuery(
        IQueryCollection query, out RankOrder order, out int limit, out RankCursor? cursor, out bool unvoted)
    {
        order = RankOrder.Net;
        limit = DefaultPageSize;
        cursor = null;
        unvoted = false;
        foreach (var (key, values) in query)
        {
            if (values.Count != 1 || values[0] is not { } value)
            {
                return false;
            }

            switch (key)
            {
                case "sort" when value == "net": order = RankOrder.Net; break;
                case "sort" when value == "total": order = RankOrder.Total; break;
                case "sort" when value == "newest": order = RankOrder.Newest; break;
                case "limit" when int.TryParse(value, System.Globalization.NumberStyles.None,
                                       System.Globalization.CultureInfo.InvariantCulture, out var n) &&
                                   n is >= 1 and <= MaxPageSize && value == n.ToString(System.Globalization.CultureInfo.InvariantCulture):
                    limit = n;
                    break;
                case "cursor" when RankCursor.TryDecode(value, out var decoded): cursor = decoded; break;
                case "unvoted" when value == "network": unvoted = true; break;
                default: return false;
            }
        }

        return cursor is null || (cursor.Order == order && cursor.Unvoted == unvoted);
    }
}
