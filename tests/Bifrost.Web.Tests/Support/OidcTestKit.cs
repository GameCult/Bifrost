using System.Security.Cryptography;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using OpenIddict.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bifrost.Web.Tests.Support;

/// <summary>What is wrong with the signing key file a test host is started with.</summary>
public enum OidcKeyFault
{
    None,
    MissingFile,
    RsaKey,
    PublicKeyOnly,
    NotPem
}

/// <summary>Key files and client settings for an OIDC test host. Everything is generated per kit; nothing is shared or committed.</summary>
public sealed class OidcTestKit : IDisposable
{
    public const string ClientId = "mas-test";
    public const string RedirectUri = "https://chat.example.test/_synapse/client/oidc/callback";

    public OidcTestKit(OidcKeyFault fault = OidcKeyFault.None)
    {
        // The directory name is a canary: no error may echo any part of a configured path.
        Directory = Path.Combine(Path.GetTempPath(), "canary-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);

        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        SigningKeyPem = ec.ExportPkcs8PrivateKeyPem();
        SigningPublicKeyPem = ec.ExportSubjectPublicKeyInfoPem();
        ClientSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

        SigningKeyPath = Path.Combine(Directory, "signing.pem");
        EncryptionKeyPath = Path.Combine(Directory, "encryption.key");
        ClientSecretPath = Path.Combine(Directory, "mas.secret");

        switch (fault)
        {
            case OidcKeyFault.None:
                File.WriteAllText(SigningKeyPath, SigningKeyPem);
                break;
            case OidcKeyFault.RsaKey:
                using (var rsa = RSA.Create(2048))
                {
                    File.WriteAllText(SigningKeyPath, rsa.ExportPkcs8PrivateKeyPem());
                }

                break;
            case OidcKeyFault.PublicKeyOnly:
                File.WriteAllText(SigningKeyPath, SigningPublicKeyPem);
                break;
            case OidcKeyFault.NotPem:
                File.WriteAllText(SigningKeyPath, "not a key");
                break;
            case OidcKeyFault.MissingFile:
                break;
        }

        File.WriteAllText(EncryptionKeyPath, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) + "\n");
        File.WriteAllText(ClientSecretPath, ClientSecret + "\n");
    }

    public string Directory { get; }

    public string SigningKeyPath { get; }

    public string EncryptionKeyPath { get; }

    public string ClientSecretPath { get; }

    public string ClientSecret { get; }

    public string SigningKeyPem { get; }

    public string SigningPublicKeyPem { get; }

    public IReadOnlyDictionary<string, string?> Configuration => new Dictionary<string, string?>
    {
        ["Bifrost:Oidc:SigningKeyPath"] = SigningKeyPath,
        ["Bifrost:Oidc:EncryptionKeyPath"] = EncryptionKeyPath,
        ["Bifrost:Oidc:Clients:Mas:ClientId"] = ClientId,
        ["Bifrost:Oidc:Clients:Mas:ClientSecretPath"] = ClientSecretPath,
        ["Bifrost:Oidc:Clients:Mas:RedirectUri"] = RedirectUri
    };

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>A clock the test moves, so a parked authorize request can be aged past its lifetime.</summary>
public sealed class OidcTestClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// A test host with the OIDC provider configured from generated key files and cookie authentication as the real
/// sign-in state (the shared test host signs everyone in as an admin through a header-driven handler instead).
/// </summary>
public class OidcWebApplicationFactory : TestWebApplicationFactory
{
    public OidcWebApplicationFactory() : this(OidcKeyFault.None)
    {
    }

    protected OidcWebApplicationFactory(OidcKeyFault fault) => Kit = new(fault);

    public OidcTestKit Kit { get; }

    public OidcTestClock Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // Program decides at registration time whether the provider exists, so these must be host settings:
        // configuration added with ConfigureAppConfiguration reaches the app only at Build().
        foreach (var (key, value) in Kit.Configuration)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices(services =>
        {
            services.Configure<AuthenticationOptions>(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            });
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            ConfigureActors(services);
        });
    }

    /// <summary>A hook for tests that replace the seam Bifrost reads the signed-in account through.</summary>
    protected virtual void ConfigureActors(IServiceCollection services)
    {
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Kit.Dispose();
        }
    }

    /// <summary>Sets the membership of the account registered under <paramref name="identity"/>.</summary>
    public async Task SetMembershipAsync(string identity, MembershipStatus status)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var account = db.UserAccounts.Single(x => x.NormalizedBifrostIdentity == identity);
        db.Memberships.Single(x => x.UserAccountId == account.Id).Status = status;
        await db.SaveChangesAsync();
    }

    /// <summary>Renames the account's display name, as the owner would between two sign-ins.</summary>
    public async Task SetDisplayNameAsync(string identity, string displayName)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        db.UserAccounts.Single(x => x.NormalizedBifrostIdentity == identity).DisplayName = displayName;
        await db.SaveChangesAsync();
    }

    public Guid AccountId(string identity)
    {
        using var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<BifrostDbContext>()
            .UserAccounts.Single(x => x.NormalizedBifrostIdentity == identity).Id;
    }
}

/// <summary>An OIDC host whose signing key file is unusable, so startup must refuse it.</summary>
public sealed class OidcKeyFaultFactory(OidcKeyFault fault) : OidcWebApplicationFactory(fault);

/// <summary>An OIDC host whose actor accessor reports an Active account that has no Bifrost identity, which sign-in itself never produces.</summary>
public sealed class OidcNoIdentityFactory : OidcWebApplicationFactory
{
    protected override void ConfigureActors(IServiceCollection services)
    {
        services.RemoveAll<ICurrentBifrostActorAccessor>();
        services.AddScoped<ICurrentBifrostActorAccessor, ActiveWithoutIdentity>();
    }

    private sealed class ActiveWithoutIdentity : ICurrentBifrostActorAccessor
    {
        public Task<CurrentBifrostActor> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CurrentBifrostActor(true, new UserAccount
            {
                NormalizedBifrostIdentity = string.Empty,
                DisplayName = "No Identity",
                Membership = new Bifrost.Web.Domain.Membership { Status = MembershipStatus.Active }
            }));
    }
}

/// <summary>An OIDC host whose public base URL differs from the origin its requests arrive on, so the issuer cannot be request-derived.</summary>
public sealed class OidcForeignIssuerFactory : OidcWebApplicationFactory
{
    public const string PublicBaseUrl = "https://id.example.test/";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Host:PublicBaseUrl"] = PublicBaseUrl }));
    }
}

/// <summary>
/// An OIDC host whose application store removes a client row without OpenIddict's bulk delete of its tokens and
/// authorizations, which the in-memory test database cannot run (the cascade is the store's, exercised on Postgres only).
/// What the seeder decides to remove is what the host then loses.
/// </summary>
public sealed class OidcInMemoryStoreFactory : OidcWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<OpenIddict.Abstractions.IOpenIddictApplicationStore<OpenIddictEntityFrameworkCoreApplication>>();
            services.AddScoped<OpenIddict.Abstractions.IOpenIddictApplicationStore<OpenIddictEntityFrameworkCoreApplication>, RowOnlyApplicationStore>();
        });
    }

    private sealed class RowOnlyApplicationStore(
        IMemoryCache cache,
        BifrostDbContext context,
        IOptionsMonitor<OpenIddictEntityFrameworkCoreOptions> options)
        : OpenIddictEntityFrameworkCoreApplicationStore<
            OpenIddictEntityFrameworkCoreApplication,
            OpenIddictEntityFrameworkCoreAuthorization,
            OpenIddictEntityFrameworkCoreToken,
            BifrostDbContext>(cache, context, options)
    {
        public override async ValueTask DeleteAsync(OpenIddictEntityFrameworkCoreApplication application, CancellationToken cancellationToken)
        {
            context.Remove(application);
            await context.SaveChangesAsync(cancellationToken);
        }
    }
}
