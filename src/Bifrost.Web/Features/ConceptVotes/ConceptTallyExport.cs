using Bifrost.Web.Configuration;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Features.ConceptVotes;

// The public tally export and the one cross-origin policy in Bifrost. It owns nothing: counts are derived by
// ConceptAppraisalService, origins come from ConceptVoteOptions.
public static class ConceptTallyExport
{
    public const string CorsPolicy = "ConceptTallies";

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

    // Counts only, per live subject: ConceptTally carries no voter key, account id or network tag.
    public static IEndpointConventionBuilder MapConceptTallyExport(this IEndpointRouteBuilder app) =>
        app.MapGet("/public/concepts/{collection}/tallies", async (
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
