using Bifrost.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bifrost.Web.Tests;

/// <summary>Without a back-channel logout URL suspension cannot end live chat sessions, and the host says so once at startup.</summary>
public sealed class OidcLogoutReachWarningTests
{
    private const string Marker = "does not end its existing chat sessions";

    private static int Warnings(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, CapturingLoggerProvider logs)
    {
        _ = factory.Services; // starts the host and its hosted services
        return logs.Lines.Count(x => x.Contains(Marker));
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> WithLogs(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, CapturingLoggerProvider logs) =>
        factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddLogging(l => l.AddProvider(logs))));

    [Fact]
    public void OIDC_configured_without_the_logout_url_warns_exactly_once()
    {
        var logs = new CapturingLoggerProvider();
        using var plain = new OidcWebApplicationFactory();
        using var factory = WithLogs(plain, logs);

        Assert.Equal(1, Warnings(factory, logs));
    }

    [Fact]
    public void OIDC_with_the_logout_url_does_not_warn()
    {
        var logs = new CapturingLoggerProvider();
        using var plain = new OidcLogoutFactory();
        using var factory = WithLogs(plain, logs);

        Assert.Equal(0, Warnings(factory, logs));
    }

    [Fact]
    public void OIDC_unconfigured_does_not_warn()
    {
        var logs = new CapturingLoggerProvider();
        using var plain = new TestWebApplicationFactory();
        using var factory = WithLogs(plain, logs);

        Assert.Equal(0, Warnings(factory, logs));
    }
}
