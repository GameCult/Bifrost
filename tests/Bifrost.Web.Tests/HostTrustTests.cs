using System.Net;
using Bifrost.Web.Configuration;
using Bifrost.Web.Features.Shared;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Tests;

public sealed class HostTrustTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public HostTrustTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task Forwarded_for_from_loopback_peer_becomes_remote_ip(string peer)
    {
        var context = await RunForwardedHeaders(peer, "203.0.113.7", "https");

        Assert.Equal(IPAddress.Parse("203.0.113.7"), context.Connection.RemoteIpAddress);
        Assert.Equal("https", context.Request.Scheme);
    }

    [Fact]
    public async Task Forwarded_for_from_non_loopback_peer_is_ignored()
    {
        var context = await RunForwardedHeaders("198.51.100.9", "203.0.113.7", "https");

        Assert.Equal(IPAddress.Parse("198.51.100.9"), context.Connection.RemoteIpAddress);
        Assert.Equal("http", context.Request.Scheme);
    }

    [Fact]
    public async Task Only_the_rightmost_forwarded_entry_is_used()
    {
        var context = await RunForwardedHeaders("127.0.0.1", "6.6.6.6, 203.0.113.7", null);

        Assert.Equal(IPAddress.Parse("203.0.113.7"), context.Connection.RemoteIpAddress);
    }

    [Fact]
    public async Task A_loopback_forwarded_entry_does_not_chain_to_earlier_entries()
    {
        var context = await RunForwardedHeaders("127.0.0.1", "6.6.6.6, 127.0.0.1", null);

        Assert.Equal(IPAddress.Loopback, context.Connection.RemoteIpAddress);
    }

    [Fact]
    public async Task Only_the_exact_loopback_addresses_are_trusted_not_the_loopback_range()
    {
        var context = await RunForwardedHeaders("127.0.0.2", "203.0.113.7", null);

        Assert.Equal(IPAddress.Parse("127.0.0.2"), context.Connection.RemoteIpAddress);
    }

    [Fact]
    public void Payload_protected_under_one_content_root_unprotects_under_another()
    {
        var root = NewRoot();
        var keys = Path.Combine(root, "keys");
        try
        {
            using var hostA = WithContentRoot(MakeRelease(root, "release-a"), keys);
            using var hostB = WithContentRoot(MakeRelease(root, "release-b"), keys);

            var sealedPayload = Protect(hostA, "payload");

            Assert.Equal("payload", Unprotect(hostB, sealedPayload));
            Assert.NotEmpty(Directory.GetFiles(keys, "key-*.xml"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_different_keys_path_does_not_unprotect_the_payload()
    {
        var root = NewRoot();
        try
        {
            using var hostA = WithContentRoot(MakeRelease(root, "release-a"), Path.Combine(root, "keys-a"));
            using var hostB = WithContentRoot(MakeRelease(root, "release-b"), Path.Combine(root, "keys-b"));

            var sealedPayload = Protect(hostA, "payload");

            Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
                () => Unprotect(hostB, sealedPayload));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_container_image_does_not_set_the_forwarded_headers_switch()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Dockerfile")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        Assert.DoesNotContain(
            "ASPNETCORE_FORWARDEDHEADERS_ENABLED",
            File.ReadAllText(Path.Combine(directory!.FullName, "Dockerfile")));
    }

    [Fact]
    public void Production_without_keys_path_fails_startup_validation()
    {
        var withoutKey = Assert.Throws<InvalidOperationException>(() => Validate(keysPath: null));
        Assert.Contains("DataProtection:KeysPath", withoutKey.Message);

        Validate(keysPath: "/not/echoed/anywhere");
    }

    [Fact]
    public void Startup_refusal_names_the_key_and_never_echoes_configured_values()
    {
        var canary = "canary-secret-4f1e";
        var refusal = Assert.Throws<InvalidOperationException>(
            () => Validate(keysPath: null, connectionString: canary));

        Assert.Contains("DataProtection:KeysPath", refusal.Message);
        Assert.DoesNotContain(canary, refusal.Message);
    }

    [Fact]
    public void Production_without_network_tag_secret_fails_and_never_echoes_values()
    {
        var canary = "canary-keys-path-77aa";
        var refusal = Assert.Throws<InvalidOperationException>(
            () => Validate(keysPath: canary, tagSecret: " "));

        Assert.Contains("ConceptVotes:NetworkTagSecret", refusal.Message);
        Assert.DoesNotContain(canary, refusal.Message);
        Assert.DoesNotContain("DataProtection:KeysPath", refusal.Message);
    }

    [Theory]
    [InlineData("short-secret")]
    [InlineData("0123456789012345678901234567890")]
    public void Production_refuses_a_short_network_tag_secret_without_echoing_it(string secret)
    {
        var refusal = Assert.Throws<InvalidOperationException>(
            () => Validate(keysPath: "/keys", tagSecret: secret));

        Assert.Contains("ConceptVotes:NetworkTagSecret", refusal.Message);
        Assert.DoesNotContain(secret, refusal.Message);
        Validate(keysPath: "/keys", tagSecret: "01234567890123456789012345678901");
    }

    [Theory]
    [InlineData(33, 56, "ConceptVotes:IPv4PrefixLength")]
    [InlineData(-1, 56, "ConceptVotes:IPv4PrefixLength")]
    [InlineData(32, 129, "ConceptVotes:IPv6PrefixLength")]
    [InlineData(32, -1, "ConceptVotes:IPv6PrefixLength")]
    public void Production_refuses_prefix_lengths_outside_the_address_family(int v4, int v6, string key)
    {
        var refusal = Assert.Throws<InvalidOperationException>(
            () => Validate(keysPath: "/keys", ipv4Length: v4, ipv6Length: v6));

        Assert.Contains(key, refusal.Message);
        Validate(keysPath: "/keys", ipv4Length: 32, ipv6Length: 128);
        Validate(keysPath: "/keys", ipv4Length: 0, ipv6Length: 0);
    }

    [Fact]
    public void Development_does_not_require_keys_path_or_tag_secret()
    {
        Validate(keysPath: null, environment: Environments.Development, tagSecret: null);
    }

    private async Task<HttpContext> RunForwardedHeaders(string peer, string forwardedFor, string? forwardedProto)
    {
        var options = _factory.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>();
        var middleware = new ForwardedHeadersMiddleware(
            _ => Task.CompletedTask,
            NullLoggerFactory.Instance,
            options);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        if (forwardedProto is not null)
        {
            context.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
        }

        await middleware.Invoke(context);
        return context;
    }

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "bifrost-dp-" + Guid.NewGuid().ToString("N"));

    private static string MakeRelease(string root, string name) =>
        Directory.CreateDirectory(Path.Combine(root, name)).FullName;

    private static string Protect(TestWebApplicationFactory host, string value) =>
        host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("P").Protect(value);

    private static string Unprotect(TestWebApplicationFactory host, string value) =>
        host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("P").Unprotect(value);

    private TestWebApplicationFactory WithContentRoot(string contentRoot, string keysPath) =>
        new KeyRingFactory(contentRoot, keysPath);

    private sealed class KeyRingFactory(string contentRoot, string keysPath) : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseContentRoot(contentRoot);
            builder.UseSetting("DataProtection:KeysPath", keysPath);
        }
    }

    private const string ValidTagSecret = "tag-secret-0123456789-0123456789-abcdef";

    private static void Validate(
        string? keysPath,
        string environment = "Production",
        string? connectionString = "Host=db",
        string? tagSecret = ValidTagSecret,
        int ipv4Length = 32,
        int ipv6Length = 56)
    {
        var values = new Dictionary<string, string?>
        {
            ["AllowedHosts"] = "bifrost.example",
            ["ConnectionStrings:Bifrost"] = connectionString,
            ["DataProtection:KeysPath"] = keysPath,
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var hostEnvironment = new StubHostEnvironment(environment);

        new StartupConfigurationValidator(
            configuration,
            hostEnvironment,
            Options.Create(new GitHubOAuthOptions { ClientId = "id", ClientSecret = "secret" }),
            Options.Create(new GitHubAppOptions { EnableWebhookSync = false }),
            Options.Create(new HeimdallOptions { EnablePatronSupportIntake = false }),
            Options.Create(new StripeOptions()),
            Options.Create(new BifrostHostOptions
            {
                PublicBaseUrl = "https://bifrost.example",
                ExpectedHost = "bifrost.example"
            }),
            Options.Create(new ConceptVoteOptions
            {
                NetworkTagSecret = tagSecret ?? string.Empty,
                IPv4PrefixLength = ipv4Length,
                IPv6PrefixLength = ipv6Length
            })).Validate();
    }

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Bifrost.Web";
        public string ContentRootPath { get; set; } = string.Empty;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
