using System.Security.Claims;
using Microsoft.AspNetCore;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Bifrost.Web.Features.Oidc;

/// <summary>
/// The decisions Bifrost owns at the OIDC provider: who the chat user is (sub = UserAccount.Id,
/// preferred_username = NormalizedBifrostIdentity) and whether they may get a session (membership Active).
/// OpenIddict does the protocol; these endpoints read the cookie session and the same actor facts the /App policy reads.
/// </summary>
public static class OidcEndpoints
{
    public const string NotActiveDescription = "The account is not an active GameCult member.";
    public const string NoIdentityDescription = "The account has no Bifrost identity.";

    private const string OpenIddictScheme = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme;
    private const string IdentityAuthenticationType = "Bifrost.Oidc";

    public static void MapBifrostOidc(this IEndpointRouteBuilder app)
    {
        app.MapMethods(OidcServiceCollectionExtensions.AuthorizationPath, ["GET", "POST"], (Delegate)AuthorizeAsync).AllowAnonymous();
        app.MapPost(OidcServiceCollectionExtensions.TokenPath, (Delegate)ExchangeAsync).AllowAnonymous();
        app.MapMethods(OidcServiceCollectionExtensions.UserInfoPath, ["GET", "POST"], (Delegate)UserInfoAsync).AllowAnonymous();
    }

    private static async Task<IResult> AuthorizeAsync(HttpContext http, ICurrentBifrostActorAccessor actors)
    {
        var request = http.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict request could not be retrieved.");

        if (http.User.Identity?.IsAuthenticated != true)
        {
            // OpenIddict has already replaced the request with a short request_uri, so this URL fits a sign-in
            // return path (LocalReturnPath.MaxLength); a test pins that, because Parse would otherwise fall back to /App.
            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = http.Request.PathBase + http.Request.Path + http.Request.QueryString },
                [CookieAuthenticationDefaults.AuthenticationScheme]);
        }

        var actor = await actors.GetAsync(http.RequestAborted);
        if (actor.UserAccount is null || !actor.IsActiveMember)
        {
            return Deny(Errors.AccessDenied, NotActiveDescription);
        }

        var account = actor.UserAccount;
        if (string.IsNullOrWhiteSpace(account.NormalizedBifrostIdentity))
        {
            return Deny(Errors.AccessDenied, NoIdentityDescription);
        }

        var identity = new ClaimsIdentity(IdentityAuthenticationType, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, account.Id.ToString("D"));
        identity.SetClaim(Claims.PreferredUsername, account.NormalizedBifrostIdentity);
        identity.SetClaim(
            Claims.Name,
            string.IsNullOrWhiteSpace(account.DisplayName) ? account.BifrostIdentity : account.DisplayName);

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(request.GetScopes());
        principal.SetDestinations(claim => claim.Type switch
        {
            Claims.Subject => [Destinations.AccessToken, Destinations.IdentityToken],
            Claims.PreferredUsername or Claims.Name when principal.HasScope(Scopes.Profile) =>
                [Destinations.AccessToken, Destinations.IdentityToken],
            _ => [Destinations.AccessToken]
        });

        return Results.SignIn(principal, properties: null, OpenIddictScheme);
    }

    private static async Task<IResult> ExchangeAsync(HttpContext http, BifrostDbContext db)
    {
        // Only the authorization code grant is enabled, so OpenIddict has refused every other grant already.
        // The code's own principal carries the claims chosen at authorize time; membership is read again here,
        // because the code outlives the authorize decision (a member suspended since gets no tokens).
        var result = await http.AuthenticateAsync(OpenIddictScheme);
        var subject = result.Principal?.GetClaim(Claims.Subject);
        if (!await IsStillActiveMemberAsync(db, subject, http.RequestAborted))
        {
            return Deny(Errors.InvalidGrant, NotActiveDescription);
        }

        return Results.SignIn(result.Principal!, properties: null, OpenIddictScheme);
    }

    private static async Task<IResult> UserInfoAsync(HttpContext http, BifrostDbContext db)
    {
        var result = await http.AuthenticateAsync(OpenIddictScheme);
        if (!result.Succeeded || result.Principal is null)
        {
            return Results.Challenge(authenticationSchemes: [OpenIddictScheme]);
        }

        // An access token outlives the authorize decision too: a member suspended since is no longer answered.
        if (!await IsStillActiveMemberAsync(db, result.Principal.GetClaim(Claims.Subject), http.RequestAborted))
        {
            return Deny(Errors.InsufficientAccess, NotActiveDescription);
        }

        var claims = new Dictionary<string, object?> { [Claims.Subject] = result.Principal.GetClaim(Claims.Subject) };
        if (result.Principal.HasScope(Scopes.Profile))
        {
            claims[Claims.PreferredUsername] = result.Principal.GetClaim(Claims.PreferredUsername);
            claims[Claims.Name] = result.Principal.GetClaim(Claims.Name);
        }

        return Results.Json(claims);
    }

    /// <summary>The one reading of "this subject is still an Active member", shared by the token and userinfo endpoints and the logout reconciler.</summary>
    internal static async Task<bool> IsStillActiveMemberAsync(BifrostDbContext db, string? subject, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(subject, out var accountId))
        {
            return false;
        }

        var account = await db.UserAccounts.AsNoTracking()
            .Include(x => x.Membership)
            .SingleOrDefaultAsync(x => x.Id == accountId, cancellationToken);
        return new CurrentBifrostActor(true, account).IsActiveMember;
    }

    private static IResult Deny(string error, string description) =>
        Results.Forbid(
            new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description
            }),
            [OpenIddictScheme]);
}
