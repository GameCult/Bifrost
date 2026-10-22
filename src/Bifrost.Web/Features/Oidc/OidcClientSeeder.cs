using Bifrost.Web.Configuration;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Bifrost.Web.Features.Oidc;

/// <summary>
/// Makes the provider's one client match configuration at startup, creating it or updating it in place.
/// Nothing else registers clients: any other client_id is refused by OpenIddict.
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
            ConsentType = ConsentTypes.Implicit,
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
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange }
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
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
