using Bifrost.Web.Configuration;
using Bifrost.Web.Data;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Bifrost.Web.Features.Oidc;

public static class OidcServiceCollectionExtensions
{
    public const string AuthorizationPath = "/connect/authorize";
    public const string TokenPath = "/connect/token";
    public const string UserInfoPath = "/connect/userinfo";

    /// <summary>How long a parked authorize request (OpenIddict's request token) can wait for the user to sign in.</summary>
    private static readonly TimeSpan ParkedRequestLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Registers Bifrost as an OIDC provider whose issuer is <see cref="BifrostHostOptions.PublicBaseUrl"/>: authorization-code flow with
    /// PKCE, ES256 id_tokens, scopes openid and profile, one configured client. Keys come from files, never from
    /// generated development keys. The authorize request is parked by OpenIddict's own request caching, so the
    /// sign-in return path carries only a short request_uri.
    /// </summary>
    public static IServiceCollection AddBifrostOidc(this IServiceCollection services, OidcIssuerOptions options)
    {
        var material = OidcKeyMaterial.Load(options);
        services.AddSingleton(material);
        services.AddSingleton(options);

        services.AddOpenIddict()
            .AddCore(core => core.UseEntityFrameworkCore().UseDbContext<BifrostDbContext>())
            .AddServer(server =>
            {
                server.SetAuthorizationEndpointUris(AuthorizationPath)
                    .SetTokenEndpointUris(TokenPath)
                    .SetUserInfoEndpointUris(UserInfoPath);

                server.AllowAuthorizationCodeFlow().RequireProofKeyForCodeExchange();
                server.RegisterScopes(Scopes.OpenId, Scopes.Profile);
                server.RegisterClaims(Claims.Subject, Claims.PreferredUsername, Claims.Name);
                server.EnableAuthorizationRequestCaching();
                server.Configure(opts =>
                {
                    opts.ClientAuthenticationMethods.Clear();
                    opts.ClientAuthenticationMethods.Add(ClientAuthenticationMethods.ClientSecretBasic);
                    opts.RequestTokenLifetime = ParkedRequestLifetime;
                });

                server.AddSigningKey(material.SigningKey);
                server.AddEncryptionKey(material.EncryptionKey);

                server.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough()
                    .EnableUserInfoEndpointPassthrough();
            });

        // The issuer is the public host, read from the bound options so it has one source.
        services.AddOptions<OpenIddictServerOptions>().Configure<IOptions<BifrostHostOptions>>((server, host) =>
        {
            if (!Uri.TryCreate(host.Value.PublicBaseUrl, UriKind.Absolute, out var issuer))
            {
                throw new OidcConfigurationException("Host:PublicBaseUrl is required when Bifrost:Oidc is configured.");
            }

            server.Issuer = issuer;
        });

        services.AddHostedService<OidcClientSeeder>();
        if (!string.IsNullOrWhiteSpace(options.Clients.Mas.BackchannelLogoutUri))
        {
            // MAS answers the logout POST itself: a redirect is a non-delivery, never followed to some other 2xx.
            services.AddHttpClient(OidcLogoutReconciler.HttpClientName, client => client.Timeout = OidcLogoutReconciler.RequestTimeout)
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
            services.AddHostedService<OidcLogoutReconciler>();
        }

        return services;
    }
}
