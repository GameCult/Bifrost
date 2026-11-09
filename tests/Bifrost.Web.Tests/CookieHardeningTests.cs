using Bifrost.Web.Features.ConceptVotes;
using Bifrost.Web.Features.Heimdall;
using Bifrost.Web.Features.Shared;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Tests;

// Every cookie is host-only: __Host- name, Secure, Path=/, no Domain. These pin the literals on purpose, so removing
// the prefix, Secure or Path=/ from any of them fails here and not only in a test that reads the same constant.
public sealed class CookieHardeningTests(CookieHardeningTests.OAuthConfiguredFactory factory)
    : IClassFixture<CookieHardeningTests.OAuthConfiguredFactory>
{
    // Program reads GitHubOAuth at startup, before the base factory's in-memory settings land, so the GitHub scheme
    // is only registered when the settings arrive as host settings.
    public sealed class OAuthConfiguredFactory : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("GitHubOAuth:ClientId", "configured-for-tests");
            builder.UseSetting("GitHubOAuth:ClientSecret", "configured-for-tests");
            base.ConfigureWebHost(builder);
        }
    }

    [Fact]
    public void Cookie_names_carry_the_host_prefix_and_live_at_the_root()
    {
        Assert.Equal("__Host-bifrost_session", BifrostCookies.Session);
        Assert.Equal("__Host-bifrost_oauth_correlation", BifrostCookies.OAuthCorrelation);
        Assert.Equal("__Host-bifrost_antiforgery", BifrostCookies.Antiforgery);
        Assert.Equal("__Host-bifrost_voter", VoterKeyResolver.CookieName);
        Assert.Equal("__Host-bifrost_heimdall_", HeimdallAttemptCookie.Prefix);
        Assert.All(HeimdallAttemptCookie.SlotNames, x => Assert.StartsWith("__Host-", x, StringComparison.Ordinal));
        Assert.Equal("/", BifrostCookies.Path);
        Assert.Equal("/", VoterKeyResolver.CookiePath);
    }

    [Fact]
    public void Session_cookie_is_host_only()
    {
        var cookie = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme).Cookie;
        AssertHostOnly(cookie.Name, cookie.SecurePolicy, cookie.Path, cookie.Domain);
        Assert.True(cookie.HttpOnly);
    }

    [Fact]
    public void Oauth_correlation_cookie_is_host_only()
    {
        var cookie = factory.Services.GetRequiredService<IOptionsMonitor<OAuthOptions>>().Get("GitHub").CorrelationCookie;
        AssertHostOnly(cookie.Name, cookie.SecurePolicy, cookie.Path, cookie.Domain);
        Assert.True(cookie.HttpOnly);
    }

    [Fact]
    public void Antiforgery_cookie_is_host_only()
    {
        var cookie = factory.Services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value.Cookie;
        AssertHostOnly(cookie.Name, cookie.SecurePolicy, cookie.Path, cookie.Domain);
    }

    [Fact]
    public void Voter_and_attempt_cookies_set_secure_root_path_and_no_domain()
    {
        var context = new DefaultHttpContext();
        new VoterKeyResolver(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()).Mint(context);
        context.Response.Cookies.Append("__Host-bifrost_heimdall_0", "v", HeimdallAttemptCookie.Options());
        var deletion = new DefaultHttpContext();
        deletion.Response.Cookies.Delete("__Host-bifrost_heimdall_0", HeimdallAttemptCookie.DeleteOptions());

        var headers = context.Response.Headers.SetCookie.Concat(deletion.Response.Headers.SetCookie).ToArray();
        Assert.Equal(3, headers.Length);
        foreach (var header in headers)
        {
            var attributes = header!.Split(';').Select(x => x.Trim().ToLowerInvariant()).ToArray();
            Assert.StartsWith("__host-", attributes[0], StringComparison.Ordinal);
            Assert.Contains("path=/", attributes);
            Assert.DoesNotContain(attributes, x => x.StartsWith("domain", StringComparison.Ordinal));
        }

        // A browser ignores a __Host- Set-Cookie that lacks Secure, a deletion included.
        Assert.All(headers, header => Assert.Contains("secure", header!.Split(';').Select(x => x.Trim().ToLowerInvariant())));
        Assert.All(headers.Take(2), header =>
            Assert.Contains("httponly", header!.Split(';').Select(x => x.Trim().ToLowerInvariant())));
    }

    private static void AssertHostOnly(string? name, CookieSecurePolicy secure, string? path, string? domain)
    {
        Assert.StartsWith("__Host-", name, StringComparison.Ordinal);
        Assert.Equal(CookieSecurePolicy.Always, secure);
        Assert.Equal("/", path);
        Assert.Null(domain);
    }
}
