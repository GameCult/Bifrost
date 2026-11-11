using System.Net;
using Bifrost.Web.Configuration;
using Bifrost.Web.Data;
using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;
using Bifrost.Web.Features.Shared;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;
using OpenIddict.Server.AspNetCore;

namespace Bifrost.Web.Features.Oidc;

/// <summary>
/// The issuer's front door, ahead of OpenIddict: the marker brake, the per-address budget, and the bound
/// on token rows. The address is the connection's, already corrected by the forwarded-headers handling that trusts only
/// the local proxy, so a caller cannot pick its own. It fails closed: anything this middleware cannot decide is a 503, never a pass.
/// </summary>
public sealed class OidcGuardMiddleware(RequestDelegate next, OidcIssuerOptions options, OidcRateLimit limit, ILogger<OidcGuardMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext http, IOpenIddictTokenManager tokens)
    {
        var endpoint = EndpointOf(http.Request.Path);
        if (endpoint is null)
        {
            await next(http);
            return;
        }

        try
        {
            if (BrakeEngaged(options.BrakePath))
            {
                await RefuseAsync(http, StatusCodes.Status503ServiceUnavailable, "temporarily_unavailable", TimeSpan.FromMinutes(1));
                return;
            }

            if (limit.Spend(ClientAddress.BudgetKey(http.Connection.RemoteIpAddress)) is { } byAddress)
            {
                await RefuseAsync(http, StatusCodes.Status429TooManyRequests, "too_many_requests", byAddress.RetryAfter);
                return;
            }

            // A new authorize request parks a row for anyone who can name the (public) client; a resumed one carries request_uri and parks none.
            if (endpoint == Authorize && !http.Request.Query.ContainsKey("request_uri") && await tokens.CountAsync(http.RequestAborted) >= options.Limits.MaxTokenRows)
            {
                await RefuseAsync(http, StatusCodes.Status503ServiceUnavailable, "temporarily_unavailable", options.Limits.PruneInterval);
                return;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("OIDC guard could not decide ({ExceptionType}); the request is refused.", exception.GetType().Name);
            await RefuseAsync(http, StatusCodes.Status503ServiceUnavailable, "temporarily_unavailable", TimeSpan.FromMinutes(1));
            return;
        }

        await next(http);
    }

    private const string Authorize = "authorize";

    // OpenIddict answers these paths with or without a trailing slash, so the match is by segments, never exact equality.
    private static string? EndpointOf(PathString path) =>
        path.StartsWithSegments(OidcServiceCollectionExtensions.AuthorizationPath, StringComparison.OrdinalIgnoreCase) ? Authorize
        : path.StartsWithSegments(OidcServiceCollectionExtensions.TokenPath, StringComparison.OrdinalIgnoreCase) ? "token"
        : path.StartsWithSegments(OidcServiceCollectionExtensions.UserInfoPath, StringComparison.OrdinalIgnoreCase) ? "userinfo"
        : null;

    // A marker that cannot be told apart from an absent one (a permission or I/O error) throws, and the guard answers 503: the brake never releases by accident.
    private static bool BrakeEngaged(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static Task RefuseAsync(HttpContext http, int status, string error, TimeSpan retryAfter)
    {
        http.Response.StatusCode = status;
        http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        http.Response.ContentType = "application/json";
        return http.Response.WriteAsync($"{{\"error\":\"{error}\"}}", http.RequestAborted);
    }
}

/// <summary>
/// The issuer's per-address permits on the shared fixed-window budget. There is no per-client budget: the one client's id is
/// public and what a caller names unverified, so a budget on it would let anyone spend the real client's permits.
/// </summary>
public sealed class OidcRateLimit(OidcIssuerOptions options, TimeProvider clock)
{
    private readonly FixedWindowBudget addresses = new(options.Limits.PerAddressPermits, options.Limits.Window, options.Limits.MaxTrackedKeys, clock);

    public BudgetDenial? Spend(string address) => addresses.Spend(address);
}

/// <summary>
/// Deletes expired and orphaned authorizations and tokens on a schedule: tokens past their expiry (parked authorize
/// requests, codes, access tokens) or no longer valid, then authorizations that are no longer valid or are ad hoc with no
/// token left. Live rows are never touched. Plain queries, not OpenIddict's ExecuteDelete prune, which the in-memory test provider
/// cannot run; an authorization takes its remaining tokens with it, because the foreign key has no cascade on Postgres.
/// </summary>
public sealed class OidcPruner(IServiceScopeFactory scopes, OidcIssuerOptions options, TimeProvider clock, ILogger<OidcPruner> logger) : BackgroundService
{
    private const int Batch = 500;

    // A fresh authorization briefly has no token yet; leave anything younger than this alone.
    private static readonly TimeSpan AuthorizationGrace = TimeSpan.FromMinutes(5);

    public async Task PruneAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var now = clock.GetUtcNow();
        var young = now - AuthorizationGrace;

        int removed;
        do
        {
            var tokens = await db.Set<OpenIddictEntityFrameworkCoreToken>()
                .Where(token => token.ExpirationDate < now || (token.Status != Statuses.Valid && token.CreationDate < young))
                .Take(Batch).ToListAsync(cancellationToken);
            db.RemoveRange(tokens);
            await db.SaveChangesAsync(cancellationToken);
            removed = tokens.Count;
        }
        while (removed == Batch);

        do
        {
            // A token row references its authorization (no cascade in the database), so the tokens go with it.
            var authorizations = await db.Set<OpenIddictEntityFrameworkCoreAuthorization>()
                .Include(authorization => authorization.Tokens)
                .Where(authorization => authorization.CreationDate < young
                    && (authorization.Status != Statuses.Valid || (authorization.Type == AuthorizationTypes.AdHoc && !authorization.Tokens.Any())))
                .Take(Batch).ToListAsync(cancellationToken);
            db.RemoveRange(authorizations.SelectMany(authorization => authorization.Tokens));
            db.RemoveRange(authorizations);
            await db.SaveChangesAsync(cancellationToken);
            removed = authorizations.Count;
        }
        while (removed == Batch);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Limits.PruneInterval, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await PruneAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError("OIDC prune failed ({ExceptionType}); the next tick tries again.", exception.GetType().Name);
            }
        }
    }
}
