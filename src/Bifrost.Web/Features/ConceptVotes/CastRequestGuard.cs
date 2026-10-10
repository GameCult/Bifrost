using Bifrost.Web.Configuration;
using Microsoft.AspNetCore.Http;

namespace Bifrost.Web.Features.ConceptVotes;

// The one place that decides whether a state-changing request to the vote page came from the site itself. The anonymous
// vote flow carries no antiforgery token and no antiforgery cookie, so a cross-site form post is refused here instead
// (OWASP Fetch Metadata): the Origin header must equal the configured site origin byte for byte, or, when the browser
// sent no Origin, Sec-Fetch-Site must be exactly same-origin. Same-site, cross-site, none, absent and a repeated header
// are all refused. Header values are compared, never echoed.
public static class CastRequestGuard
{
    public static bool Allows(HttpRequest request, BifrostHostOptions host)
    {
        if (!Uri.TryCreate(host.PublicBaseUrl, UriKind.Absolute, out var site))
        {
            return false;
        }

        var origin = request.Headers.Origin;
        if (origin.Count > 0)
        {
            return origin.Count == 1 && string.Equals(origin[0], site.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal);
        }

        var fetchSite = request.Headers["Sec-Fetch-Site"];
        return fetchSite.Count == 1 && string.Equals(fetchSite[0], "same-origin", StringComparison.Ordinal);
    }
}
