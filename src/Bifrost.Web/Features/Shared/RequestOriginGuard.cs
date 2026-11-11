using Bifrost.Web.Configuration;
using Microsoft.AspNetCore.Http;

namespace Bifrost.Web.Features.Shared;

// The one place that decides whether a state-changing request to an anonymous form (the vote page, the sign-up form) came from
// the site itself. These flows carry no antiforgery token and no antiforgery cookie, so a cross-site form post is refused here instead
// (OWASP Fetch Metadata): the Origin header must equal the configured site origin byte for byte, or, when the browser
// sent no Origin, Sec-Fetch-Site must be exactly same-origin. Same-site, cross-site, none, absent and a repeated header
// are all refused. Header values are compared, never echoed.
public static class RequestOriginGuard
{
    public static bool Allows(HttpRequest request, BifrostHostOptions host)
    {
        if (!Uri.TryCreate(host.PublicBaseUrl, UriKind.Absolute, out var site))
        {
            return false;
        }

        var origin = request.Headers.Origin;
        if (request.Headers.ContainsKey("Origin"))
        {
            return origin.Count == 1 && string.Equals(origin[0], site.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal);
        }

        var fetchSite = request.Headers["Sec-Fetch-Site"];
        return fetchSite.Count == 1 && string.Equals(fetchSite[0], "same-origin", StringComparison.Ordinal);
    }

    /// <summary>
    /// For the no-script sign-up form only. Text browsers (Lynx, w3m, Links) send neither Origin nor Sec-Fetch-Site, so a
    /// request with neither is let through when it carries no Bifrost session cookie: there is no session to replace.
    /// Any Origin or Sec-Fetch-Site present is judged exactly as <see cref="Allows"/> judges it, and a headerless request
    /// that does carry a session cookie is refused.
    /// </summary>
    public static bool AllowsFormWithoutFetchMetadata(HttpRequest request, BifrostHostOptions host) =>
        Allows(request, host) ||
        (!request.Headers.ContainsKey("Origin") &&
         !request.Headers.ContainsKey("Sec-Fetch-Site") &&
         !request.Cookies.Keys.Any(name => name.StartsWith(BifrostCookies.Session, StringComparison.Ordinal)));
}
