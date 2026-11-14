using System.Security.Claims;
using Microsoft.AspNetCore;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Desk;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Features.Registration;
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
    public const string SuspendedDescription = "The account is suspended.";
    public const string MergedDescription = "The account is part of an account merge and can no longer sign in.";
    public const string NoIdentityDescription = "The account has no Bifrost identity.";
    public const string ReservedIdentityDescription = "The account's Bifrost identity is reserved.";
    public const string ConsentRequiredDescription = "Agreement to the training notice in force is needed to speak in a recorded room.";

    private const string OpenIddictScheme = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme;
    private const string IdentityAuthenticationType = "Bifrost.Oidc";

    public static void MapBifrostOidc(this IEndpointRouteBuilder app)
    {
        app.MapMethods(OidcServiceCollectionExtensions.AuthorizationPath, ["GET", "POST"], (Delegate)AuthorizeAsync).AllowAnonymous();
        app.MapPost(OidcServiceCollectionExtensions.TokenPath, (Delegate)ExchangeAsync).AllowAnonymous();
        app.MapMethods(OidcServiceCollectionExtensions.UserInfoPath, ["GET", "POST"], (Delegate)UserInfoAsync).AllowAnonymous();
    }

    private static async Task<IResult> AuthorizeAsync(HttpContext http, ICurrentBifrostActorAccessor actors, BifrostDbContext db)
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
        // Chat tier: any account that is not suspended may sign in to chat. Membership is neither required nor written;
        // every member or governance surface keeps asking for Active itself (the Active-member policy, IsActiveMember, ActiveMemberIdAsync).
        if (actor.UserAccount is null)
        {
            return Deny(Errors.AccessDenied, actor.Merge is not null ? MergedDescription : NoIdentityDescription);
        }

        if (actor.Membership?.Status == MembershipStatus.Suspended)
        {
            return Deny(Errors.AccessDenied, SuspendedDescription);
        }

        var account = actor.UserAccount;
        if (string.IsNullOrWhiteSpace(account.NormalizedBifrostIdentity))
        {
            return Deny(Errors.AccessDenied, NoIdentityDescription);
        }

        // The identity becomes the Matrix localpart. Creation refuses reserved names (BifrostIdentityService), but a row minted
        // before that refusal may hold one, and there is no rename path: the issuer never vouches for a Mind's or a system's localpart.
        if (BifrostIdentityService.IsReserved(account.NormalizedBifrostIdentity))
        {
            return Deny(Errors.AccessDenied, ReservedIdentityDescription);
        }

        var identity = new ClaimsIdentity(IdentityAuthenticationType, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, account.Id.ToString("D"));
        identity.SetClaim(Claims.PreferredUsername, account.NormalizedBifrostIdentity);
        identity.SetClaim(
            Claims.Name,
            string.IsNullOrWhiteSpace(account.DisplayName) ? account.BifrostIdentity : account.DisplayName);

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(request.GetScopes());
        // A desk scope is what makes a token a desk token: its audience is the desk's. The client's permissions already
        // decided whether it may ask for the scope (the MAS client may not), so this branch is reached only by one that may.
        if (request.GetScopes().Any(DeskEndpoints.IsDeskScope))
        {
            principal.SetResources(DeskEndpoints.Audience);
        }

        principal.SetDestinations(claim => claim.Type switch
        {
            Claims.Subject => [Destinations.AccessToken, Destinations.IdentityToken],
            Claims.PreferredUsername or Claims.Name when principal.HasScope(Scopes.Profile) =>
                [Destinations.AccessToken, Destinations.IdentityToken],
            _ => [Destinations.AccessToken]
        });

        // The training notice is the chat's: a desk token is not a chat token and needs no receipt (Meta's ruling, 2026-11-11).
        // Last, so a refusal that no agreement can cure (suspended, no identity, reserved) is never answered with an invitation to agree.
        // Not an OIDC error: the member is sent to the consent page with this request's resume path, and the parked request
        // (single use, ten minutes) is still there when they come back, because only an OpenIddict response spends it.
        if (!IsDeskToken(principal) && !await ConsentEndpoints.HasLiveReceiptAsync(db, account.Id, http.RequestAborted))
        {
            return Results.Redirect(ConsentEndpoints.PathFor(http.Request.PathBase + http.Request.Path + http.Request.QueryString));
        }

        return Results.SignIn(principal, properties: null, OpenIddictScheme);
    }

    /// <summary>A token whose scopes include a desk scope is a desk token. Only the chat token requires the training-notice receipt.</summary>
    // Only a token whose every scope is a desk scope skips the consent gate: a client that may hold chat scopes too cannot ride a desk scope past the receipt.
    private static bool IsDeskToken(ClaimsPrincipal? principal) => principal is not null && principal.GetScopes() is { Length: > 0 } scopes && scopes.All(DeskEndpoints.IsDeskScope);

    private static async Task<IResult> ExchangeAsync(HttpContext http, BifrostDbContext db)
    {
        // Only the authorization code grant is enabled, so OpenIddict has refused every other grant already.
        // The code's own principal carries the claims chosen at authorize time; suspension and agreement are read again here,
        // because the code outlives the authorize decision (an account suspended or withdrawn since gets no tokens).
        var result = await http.AuthenticateAsync(OpenIddictScheme);
        var subject = result.Principal?.GetClaim(Claims.Subject);
        if (await ChatRefusalAsync(db, subject, http.RequestAborted, IsDeskToken(result.Principal)) is { } refusal)
        {
            return Deny(Errors.InvalidGrant, refusal);
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

        // An access token outlives the authorize decision too: an account suspended since, or one whose agreement has gone, is no longer answered.
        if (await ChatRefusalAsync(db, result.Principal.GetClaim(Claims.Subject), http.RequestAborted, IsDeskToken(result.Principal)) is { } refusal)
        {
            return Deny(Errors.InsufficientAccess, refusal);
        }

        var claims = new Dictionary<string, object?> { [Claims.Subject] = result.Principal.GetClaim(Claims.Subject) };
        if (result.Principal.HasScope(Scopes.Profile))
        {
            claims[Claims.PreferredUsername] = result.Principal.GetClaim(Claims.PreferredUsername);
            claims[Claims.Name] = result.Principal.GetClaim(Claims.Name);
        }

        return Results.Json(claims);
    }

    /// <summary>
    /// The one reading of "this subject may still chat", shared by the token and userinfo endpoints and the logout reconciler:
    /// null when they may, else the description of why not. The account exists, its membership is not Suspended (absent, pending
    /// or Active all chat), and, for a chat token (not a desk token), it holds a live receipt for the notice version in force. Re-read on every call.
    /// </summary>
    internal static async Task<string?> ChatRefusalAsync(BifrostDbContext db, string? subject, CancellationToken cancellationToken, bool deskToken = false)
    {
        if (!Guid.TryParse(subject, out var accountId))
        {
            return SuspendedDescription;
        }

        var account = await db.UserAccounts.AsNoTracking()
            .Include(x => x.Membership)
            .SingleOrDefaultAsync(x => x.Id == accountId, cancellationToken);
        if (account is null || account.Membership?.Status == MembershipStatus.Suspended)
        {
            return SuspendedDescription;
        }

        // A folded or frozen subject's chat sessions end: token, userinfo and the logout reconciler all read this one refusal.
        if (await AccountMergeLedger.StandingAsync(db, accountId, cancellationToken) is not null)
        {
            return MergedDescription;
        }

        return deskToken || await ConsentEndpoints.HasLiveReceiptAsync(db, accountId, cancellationToken) ? null : ConsentRequiredDescription;
    }

    /// <summary>The account id of the subject when it is an Active member now, else null. The door to the fork desk (cookie surfaces check IsActiveMember); a chat-tier token does not pass it. Re-read on every call.</summary>
    internal static async Task<Guid?> ActiveMemberIdAsync(BifrostDbContext db, string? subject, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(subject, out var accountId))
        {
            return null;
        }

        var account = await db.UserAccounts.AsNoTracking()
            .Include(x => x.Membership)
            .SingleOrDefaultAsync(x => x.Id == accountId, cancellationToken);
        return new CurrentBifrostActor(true, account).IsActiveMember ? accountId : null;
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
