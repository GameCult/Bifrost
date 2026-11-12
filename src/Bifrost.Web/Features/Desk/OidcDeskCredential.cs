using Microsoft.AspNetCore.Authentication;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Bifrost.Web.Features.Desk;

// The production IDeskCredential: OpenIddict's validation handler against the local server, asked for by its own
// scheme name and no other. It authenticates only. The default scheme stays the cookie, so a bearer token reaches no
// cookie surface, and this credential never reads the cookie, so the session cookie authenticates nothing on the
// desk. Audience, scope and Active membership are the desk's judgments (DeskEndpoints), not this port's.
public sealed class OidcDeskCredential : IDeskCredential
{
    public async ValueTask<DeskToken?> AuthenticateAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var result = await context.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        if (!result.Succeeded || result.Principal is null)
        {
            return null;
        }

        // A token may name several audiences; the desk's own is reported when present so the desk judges it, else the
        // first (or none).
        var audiences = result.Principal.GetAudiences();
        var audience = audiences.Contains(DeskEndpoints.Audience) ? DeskEndpoints.Audience : audiences.FirstOrDefault();
        return new DeskToken(result.Principal.GetClaim(Claims.Subject), audience, [.. result.Principal.GetScopes()]);
    }
}
