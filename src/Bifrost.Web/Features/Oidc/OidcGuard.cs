using System.Net;
using System.Text;
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
/// The issuer's front door, ahead of OpenIddict: the marker brake, the per-address and per-client budgets, and the bound
/// on token rows. The address is the connection's, already corrected by the forwarded-headers handling that trusts only
/// the local proxy, so a caller cannot pick its own. It fails closed: anything this middleware cannot decide is a 503, never a pass.
/// </summary>
public sealed class OidcGuardMiddleware(RequestDelegate next, OidcIssuerOptions options, OidcRateLimit limit, ILogger<OidcGuardMiddleware> logger)
{
    private const int MaxClientIdLength = 128;

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

            if (limit.Spend($"address:{AddressKey(http.Connection.RemoteIpAddress)}") is { } byAddress)
            {
                await RefuseAsync(http, StatusCodes.Status429TooManyRequests, "too_many_requests", byAddress.RetryAfter);
                return;
            }

            if (limit.Spend($"client:{endpoint}:{await ClientKeyAsync(http, endpoint)}") is { } byClient)
            {
                await RefuseAsync(http, StatusCodes.Status429TooManyRequests, "too_many_requests", byClient.RetryAfter);
                return;
            }

            // A new authorize request parks a row for anyone who can name the (public) client; a resumed one carries request_uri and parks none.
            if (endpoint == "authorize" && !http.Request.Query.ContainsKey("request_uri") && await tokens.CountAsync(http.RequestAborted) >= options.Limits.MaxTokenRows)
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

    private static string? EndpointOf(PathString path) =>
        path.Equals(OidcServiceCollectionExtensions.AuthorizationPath, StringComparison.OrdinalIgnoreCase) ? "authorize"
        : path.Equals(OidcServiceCollectionExtensions.TokenPath, StringComparison.OrdinalIgnoreCase) ? "token"
        : path.Equals(OidcServiceCollectionExtensions.UserInfoPath, StringComparison.OrdinalIgnoreCase) ? "userinfo"
        : null;

    private static bool BrakeEngaged(string path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    // An IPv6 caller owns its /64, so rotating the low bits buys nothing.
    private static string AddressKey(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes).ToString() + "/64";
    }

    // The client the caller names, not one it proved: authorize names it in the query or form, token in its Basic header.
    // An unreadable value shares one key, so garbage cannot mint keys.
    private static async Task<string> ClientKeyAsync(HttpContext http, string endpoint)
    {
        string? clientId = null;
        if (endpoint == "authorize")
        {
            clientId = http.Request.Query["client_id"].FirstOrDefault()
                ?? (http.Request.HasFormContentType ? (await http.Request.ReadFormAsync(http.RequestAborted))["client_id"].FirstOrDefault() : null);
        }
        else if (endpoint == "token" && http.Request.Headers.Authorization.FirstOrDefault() is { } header
            && header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
                clientId = Uri.UnescapeDataString(decoded.Split(':', 2)[0]);
            }
            catch (FormatException)
            {
            }
        }

        return string.IsNullOrEmpty(clientId) || clientId.Length > MaxClientIdLength ? "unnamed" : clientId;
    }

    private static Task RefuseAsync(HttpContext http, int status, string error, TimeSpan retryAfter)
    {
        http.Response.StatusCode = status;
        http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        http.Response.ContentType = "application/json";
        return http.Response.WriteAsync($"{{\"error\":\"{error}\"}}", http.RequestAborted);
    }
}

/// <summary>The issuer's per-address and per-client permits, on the shared fixed-window budget.</summary>
public sealed class OidcRateLimit(OidcIssuerOptions options, TimeProvider clock)
{
    private readonly FixedWindowBudget addresses = new(options.Limits.PerAddressPermits, options.Limits.Window, options.Limits.MaxTrackedKeys, clock);
    private readonly FixedWindowBudget clients = new(options.Limits.PerClientPermits, options.Limits.Window, options.Limits.MaxTrackedKeys, clock);

    public BudgetDenial? Spend(string key) => key.StartsWith("address:", StringComparison.Ordinal) ? addresses.Spend(key) : clients.Spend(key);
}

/// <summary>
/// Deletes expired and orphaned authorizations and tokens on a schedule: tokens past their expiry (parked authorize
/// requests, codes, access tokens) or no longer valid, then authorizations that are no longer valid or are ad hoc with no
/// token left. Live rows are never touched. Plain queries, not OpenIddict's ExecuteDelete prune, so the same code runs on
/// Postgres and on the in-memory test provider.
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
            var authorizations = await db.Set<OpenIddictEntityFrameworkCoreAuthorization>()
                .Where(authorization => authorization.CreationDate < young
                    && (authorization.Status != Statuses.Valid || (authorization.Type == AuthorizationTypes.AdHoc && !authorization.Tokens.Any())))
                .Take(Batch).ToListAsync(cancellationToken);
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
