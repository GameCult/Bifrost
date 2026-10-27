using System.Net;
using Bifrost.Web.Configuration;
using Bifrost.Web.Data;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Bifrost.Web.Features.Oidc;

/// <summary>
/// Ends the chat sessions of a subject who is no longer an Active member. Each tick it lists the subjects holding valid
/// authorizations of the configured client; for each one the shared active-member reading refuses, it POSTs one signed
/// OIDC back-channel logout token to MAS and, only after a 2xx, revokes that subject's authorizations. The
/// authorizations table is the queue: nothing is stored, so a restart, a MAS outage or a later-set URI resumes from it.
/// Per-subject backoff lives in memory only. It never writes membership.
/// </summary>
public sealed class OidcLogoutReconciler(
    IServiceScopeFactory scopeFactory,
    OidcIssuerOptions options,
    OidcKeyMaterial material,
    IOptions<OpenIddictServerOptions> serverOptions,
    IHttpClientFactory httpClients,
    TimeProvider time,
    ILogger<OidcLogoutReconciler> logger) : BackgroundService
{
    public const string HttpClientName = "bifrost-oidc-logout";
    public const string LogoutEvent = "http://schemas.openid.net/event/backchannel-logout";
    public const string LogoutTokenType = "logout+jwt";

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(15);

    private readonly Dictionary<string, Backoff> _backoff = new(StringComparer.Ordinal);

    private sealed record Backoff(int Failures, DateTimeOffset NotBefore);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileOnceAsync(stoppingToken);
                await Task.Delay(Interval, time, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                // The type only: messages can carry connection strings or URLs.
                logger.LogError("Back-channel logout reconcile failed: {ErrorType}", error.GetType().Name);
                try
                {
                    await Task.Delay(Interval, time, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>One reconcile pass. Subjects are independent: one that cannot be delivered never blocks another.</summary>
    public async Task ReconcileOnceAsync(CancellationToken cancellationToken)
    {
        var subjects = await ListSubjectsAsync(cancellationToken);
        foreach (var gone in _backoff.Keys.Except(subjects).ToArray())
        {
            _backoff.Remove(gone);
        }

        foreach (var subject in subjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ReconcileSubjectAsync(subject, cancellationToken);
        }
    }

    private async Task<HashSet<string>> ListSubjectsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();

        var subjects = new HashSet<string>(StringComparer.Ordinal);
        var application = await applications.FindByClientIdAsync(options.Clients.Mas.ClientId, cancellationToken);
        if (application is null || await applications.GetIdAsync(application, cancellationToken) is not { } applicationId)
        {
            return subjects;
        }

        await foreach (var authorization in authorizations.FindByApplicationIdAsync(applicationId, cancellationToken))
        {
            if (await authorizations.HasStatusAsync(authorization, Statuses.Valid, cancellationToken) &&
                await authorizations.GetSubjectAsync(authorization, cancellationToken) is { Length: > 0 } subject)
            {
                subjects.Add(subject);
            }
        }

        return subjects;
    }

    private async Task ReconcileSubjectAsync(string subject, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        if (await OidcEndpoints.IsStillActiveMemberAsync(db, subject, cancellationToken))
        {
            _backoff.Remove(subject);
            return;
        }

        // A subject that recovered is forgotten at once; one still owed a logout waits out its backoff.
        _backoff.TryGetValue(subject, out var previous);
        if (previous is not null && time.GetUtcNow() < previous.NotBefore)
        {
            return;
        }

        var failures = (previous?.Failures ?? 0) + 1;
        var status = await SendLogoutAsync(subject, cancellationToken);
        if (status is { } code && (int)code is >= 200 and < 300)
        {
            var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
            await foreach (var authorization in authorizations.FindBySubjectAsync(subject, cancellationToken))
            {
                if (await authorizations.HasStatusAsync(authorization, Statuses.Valid, cancellationToken))
                {
                    await authorizations.TryRevokeAsync(authorization, cancellationToken);
                }
            }

            _backoff.Remove(subject);
            return;
        }

        // 400 (token refused) and 404 (unknown provider) are configuration faults: retrying sooner cannot fix them.
        var fault = status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound;
        var delay = fault ? MaxBackoff : BackoffFor(failures);
        _backoff[subject] = new Backoff(failures, time.GetUtcNow() + delay);
        logger.LogWarning(
            "Back-channel logout to MAS failed with {Status}; retrying in {Delay}",
            status is { } s ? (int)s : "no response",
            delay);
    }

    internal static TimeSpan BackoffFor(int failures)
    {
        var seconds = Interval.TotalSeconds * Math.Pow(2, Math.Min(failures, 16) - 1);
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
    }

    /// <summary>The status MAS answered, or null on a network fault or timeout. The response body is never read.</summary>
    private async Task<HttpStatusCode?> SendLogoutAsync(string subject, CancellationToken cancellationToken)
    {
        var token = BuildLogoutToken(subject);
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Clients.Mas.BackchannelLogoutUri)
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("logout_token", token)])
        };
        try
        {
            using var response = await httpClients.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.StatusCode;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// The OIDC back-channel logout token MAS verifies like an id_token: iss and aud as the id_token carries them, exp
    /// and iat both present, an events member, and neither nonce nor sid (MAS finds the session by sub).
    /// </summary>
    private string BuildLogoutToken(string subject)
    {
        var now = time.GetUtcNow();
        var descriptor = new SecurityTokenDescriptor
        {
            TokenType = LogoutTokenType,
            Issuer = serverOptions.Value.Issuer!.AbsoluteUri,
            Audience = options.Clients.Mas.ClientId,
            IssuedAt = now.UtcDateTime,
            Expires = now.UtcDateTime + TokenLifetime,
            SigningCredentials = new SigningCredentials(material.SigningKey, SecurityAlgorithms.EcdsaSha256),
            Claims = new Dictionary<string, object>
            {
                [Claims.Subject] = Guid.TryParse(subject, out var id) ? id.ToString("D") : subject,
                [Claims.JwtId] = Guid.NewGuid().ToString("D"),
                ["events"] = new Dictionary<string, object> { [LogoutEvent] = new Dictionary<string, object>() }
            }
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
