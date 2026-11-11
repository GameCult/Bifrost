using System.Security.Cryptography;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
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
        ["Bifrost:Oidc:Clients:Mas:RedirectUri"] = RedirectUri,
        // The shared test client has no remote address, so every request is one caller: the budgets are out of the way unless a test sets them.
        ["Bifrost:Oidc:Limits:PerAddressPermits"] = "100000",
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

    // The monotonic reading follows the moved time, so a budget's window can be aged the same way as a parked request.
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _now.UtcTicks;

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

    public async Task<MembershipStatus?> MembershipStatusAsync(string identity)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var account = db.UserAccounts.Single(x => x.NormalizedBifrostIdentity == identity);
        return (await db.Memberships.AsNoTracking().SingleOrDefaultAsync(x => x.UserAccountId == account.Id))?.Status;
    }

    public sealed record RowCounts(int Accounts, int Memberships, int Votes, int RoleAssignments);

    /// <summary>What the issuer must leave alone: accounts, memberships, role grants and votes.</summary>
    public async Task<RowCounts> RowCountsAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        return new RowCounts(await db.UserAccounts.CountAsync(), await db.Memberships.CountAsync(), await db.Votes.CountAsync(), await db.RoleAssignments.CountAsync());
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

    /// <summary>Removes the account (and with it its membership), as an owner deleting themselves would.</summary>
    public async Task DeleteAccountAsync(string identity)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        db.UserAccounts.Remove(db.UserAccounts.Single(x => x.NormalizedBifrostIdentity == identity));
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

/// <summary>
/// An OIDC host whose actor accessor reports an Active account holding the given Bifrost identity: how a row minted before
/// reserved names were refused at creation (or any identity the test needs) reaches the authorize endpoint, since
/// registration itself now refuses a reserved name.
/// </summary>
public sealed class OidcFixedIdentityFactory(string identity, MembershipStatus status = MembershipStatus.Active) : OidcWebApplicationFactory
{
    protected override void ConfigureActors(IServiceCollection services)
    {
        services.RemoveAll<ICurrentBifrostActorAccessor>();
        services.AddScoped<ICurrentBifrostActorAccessor>(_ => new FixedIdentity(identity, status));
    }

    private sealed class FixedIdentity(string identity, MembershipStatus status) : ICurrentBifrostActorAccessor
    {
        public Task<CurrentBifrostActor> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CurrentBifrostActor(true, new UserAccount
            {
                Id = Guid.NewGuid(),
                BifrostIdentity = identity,
                NormalizedBifrostIdentity = identity, // as given: the guard, not the fixture, must fold case
                DisplayName = "Fixed Identity",
                Membership = new Bifrost.Web.Domain.Membership { Status = status }
            }));
    }
}

/// <summary>An OIDC host with extra settings (limits, a brake path) applied after the kit's own.</summary>
public sealed class OidcSettingsFactory(IReadOnlyDictionary<string, string?> settings) : OidcWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }
    }

    public Task PruneAsync() => Services.GetRequiredService<Bifrost.Web.Features.Oidc.OidcPruner>().PruneAsync(CancellationToken.None);

    public async Task<List<string?>> TokenTypesAsync()
    {
        using var scope = Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<OpenIddict.Abstractions.IOpenIddictTokenManager>();
        var types = new List<string?>();
        await foreach (var token in manager.ListAsync())
        {
            types.Add(await manager.GetTypeAsync(token));
        }

        return types;
    }

    public async Task<int> AuthorizationRowsAsync()
    {
        using var scope = Services.CreateScope();
        return (int)await scope.ServiceProvider.GetRequiredService<OpenIddict.Abstractions.IOpenIddictAuthorizationManager>().CountAsync();
    }

    /// <summary>Tokens that belong to some authorization (the request and code rows carry none once spent).</summary>
    public async Task<int> TokensOfAuthorizationsAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        return await db.Set<OpenIddictEntityFrameworkCoreToken>().CountAsync(token => token.Authorization != null);
    }

    public async Task RevokeFirstAuthorizationAsync()
    {
        using var scope = Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<OpenIddict.Abstractions.IOpenIddictAuthorizationManager>();
        await foreach (var authorization in manager.ListAsync(count: 1, offset: 0))
        {
            Assert.True(await manager.TryRevokeAsync(authorization));
        }
    }

    public async Task<int> TokenRowsAsync()
    {
        using var scope = Services.CreateScope();
        return (int)await scope.ServiceProvider.GetRequiredService<OpenIddict.Abstractions.IOpenIddictTokenManager>().CountAsync();
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
/// An OIDC host on OpenIddict's own non-bulk deletion path. The in-memory test database cannot run the bulk
/// ExecuteDelete the library otherwise uses to remove an application's tokens and authorizations, so this switches
/// that option and the library's cascade runs unchanged.
/// </summary>
public sealed class OidcCascadeFactory : OidcWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
            services.Configure<OpenIddictEntityFrameworkCoreOptions>(options => options.DisableBulkOperations = true));
    }
}
