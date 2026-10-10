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
    public void Payload_protected_under_one_content_root_unprotects_under_another()
    {
        var root = Path.Combine(Path.GetTempPath(), "bifrost-dp-" + Guid.NewGuid().ToString("N"));
        var keys = Path.Combine(root, "keys");
        var releaseA = Directory.CreateDirectory(Path.Combine(root, "release-a")).FullName;
        var releaseB = Directory.CreateDirectory(Path.Combine(root, "release-b")).FullName;
        try
        {
            using var hostA = WithContentRoot(releaseA, keys);
            using var hostB = WithContentRoot(releaseB, keys);

            var protectedPayload = hostA.Services
                .GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("P")
                .Protect("payload");
            var opened = hostB.Services
                .GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("P")
                .Unprotect(protectedPayload);

            Assert.Equal("payload", opened);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Production_without_keys_path_fails_startup_validation()
    {
        var withoutKey = Assert.Throws<InvalidOperationException>(() => Validate(keysPath: null));
        Assert.Contains("DataProtection:KeysPath", withoutKey.Message);

        Validate(keysPath: "/not/echoed/anywhere");
    }

    [Fact]
    public void Startup_refusal_names_the_key_and_never_echoes_its_value()
    {
        var canary = "canary-secret-path-4f1e";
        var refusal = Assert.Throws<InvalidOperationException>(
            () => Validate(keysPath: canary, connectionString: null));

        Assert.DoesNotContain(canary, refusal.Message);
    }

    [Fact]
    public void Development_does_not_require_keys_path()
    {
        Validate(keysPath: null, environment: Environments.Development);
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

    private TestWebApplicationFactory WithContentRoot(string contentRoot, string keysPath) =>
        new KeyRingFactory(contentRoot, keysPath);

    private sealed class KeyRingFactory(string contentRoot, string keysPath) : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseContentRoot(contentRoot);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DataProtection:KeysPath"] = keysPath
                }));
        }
    }

    private static void Validate(
        string? keysPath,
        string environment = "Production",
        string? connectionString = "Host=db")
    {
        var values = new Dictionary<string, string?>
        {
            ["AllowedHosts"] = "bifrost.example",
            ["ConnectionStrings:Bifrost"] = connectionString,
            ["DataProtection:KeysPath"] = keysPath
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
