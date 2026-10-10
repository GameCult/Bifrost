using Bifrost.Web.Configuration;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

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
    // adds Vary: Origin itself only for policies of more than one origin; this policy has one.
    public static IApplicationBuilder UseConceptTallyCors(this IApplicationBuilder app)
    {
        app.Use((context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(RoutePrefix))
            {
                context.Response.OnStarting(() =>
                {
                    var vary = context.Response.Headers.Vary;
                    var varies = vary.Any(v => v is not null
                        && v.Split(',').Any(t => t.Trim() is "*" || t.Trim().Equals("Origin", StringComparison.OrdinalIgnoreCase)));
                    if (!varies)
                    {
                        context.Response.Headers.Vary = StringValues.Concat(vary, "Origin");
                    }

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
}
