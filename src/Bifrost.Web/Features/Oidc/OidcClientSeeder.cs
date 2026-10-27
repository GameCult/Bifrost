using Bifrost.Web.Configuration;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Bifrost.Web.Features.Oidc;

/// <summary>
/// Makes the provider's one client match configuration at startup, creating it or updating it in place, and removes
/// every other application (with its authorizations and tokens), so a client left by an older configuration cannot get codes.
/// </summary>
public sealed class OidcClientSeeder(
    IServiceScopeFactory scopeFactory,
    OidcIssuerOptions options,
    OidcKeyMaterial material,
    IOptions<OpenIddictServerOptions> serverOptions) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Reading the server options now runs their configuration, so a bad issuer stops startup rather than the first request.
        _ = serverOptions.Value;

        await using var scope = scopeFactory.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var mas = options.Clients.Mas;

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = mas.ClientId,
            ClientSecret = material.ClientSecret,
            ClientType = ClientTypes.Confidential,
            DisplayName = "Matrix Authentication Service",
            RedirectUris = { new Uri(mas.RedirectUri, UriKind.Absolute) },
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.ResponseTypes.Code,
                Permissions.Scopes.Profile,
                Permissions.Prefixes.Scope + Scopes.OpenId
            }
        };

        var existing = await manager.FindByClientIdAsync(mas.ClientId, cancellationToken);
        if (existing is null)
        {
            await manager.CreateAsync(descriptor, cancellationToken);
        }
        else
        {
            await manager.UpdateAsync(existing, descriptor, cancellationToken);
        }

        var applications = new List<object>();
        await foreach (var application in manager.ListAsync(cancellationToken: cancellationToken))
        {
            applications.Add(application);
        }

        foreach (var application in applications)
        {
            if (!string.Equals(await manager.GetClientIdAsync(application, cancellationToken), mas.ClientId, StringComparison.Ordinal))
            {
                await manager.DeleteAsync(application, cancellationToken);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
