using System.Net;
using System.Text.RegularExpressions;
using Bifrost.Web.Features.Heimdall;
using Bifrost.Web.Features.Shared;
using Bifrost.Web.Tests.Support;

namespace Bifrost.Web.Tests;

/// <summary>
/// The ordinary sign-in start as a browser meets it. A navigation starts directly only when that harms nothing;
/// when it would replace a signed-in session or overwrite a live attempt it answers a confirmation page, and only
/// a POST carrying the page's antiforgery token starts. Soul's finding on PR #84 (follow-up bifrost-signin-start-get).
/// </summary>
public sealed class HeimdallSignInStartTests
{
    private const string Start = "/auth/heimdall/discord?returnUrl=/App/Mill";

    private static async Task<HttpResponseMessage> GetAsync(HeimdallHost host, string url, string? cookieHeader = null, params (string, string)[] headers)
    {
        using var client = host.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (cookieHeader is not null)
        {
            request.Headers.Add("Cookie", cookieHeader);
        }

        foreach (var (name, value) in headers)
        {
            request.Headers.Add(name, value);
        }

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostAsync(HeimdallHost host, string url, IDictionary<string, string> form, string? cookieHeader)
    {
        using var client = host.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        if (cookieHeader is not null)
        {
            request.Headers.Add("Cookie", cookieHeader);
        }

        return await client.SendAsync(request);
    }

    private static string Field(string html, string name) =>
        WebUtility.HtmlDecode(Regex.Match(html, $"name=\"{Regex.Escape(name)}\" value=\"([^\"]*)\"").Groups[1].Value);

    /// <summary>The confirmation page as a browser reads it: the form it would post, and the cookie jar the page set.</summary>
    private static async Task<(Dictionary<string, string> Form, string Cookies, string Html)> ReadConfirmationAsync(HttpResponseMessage page, string existingCookies)
    {
        var html = await page.Content.ReadAsStringAsync();
        var form = new Dictionary<string, string>
        {
            ["returnUrl"] = Field(html, "returnUrl"),
            ["access"] = Field(html, "access"),
            ["__RequestVerificationToken"] = Field(html, "__RequestVerificationToken")
        };
        var antiforgery = HeimdallHost.CookieValue(page, BifrostCookies.Antiforgery);
        return (form, existingCookies + "; " + BifrostCookies.Antiforgery + "=" + antiforgery, html);
    }

    [Fact]
    public async Task An_anonymous_navigation_still_starts_a_sign_in_directly()
    {
        using var host = new HeimdallHost(realSessions: true);

        var attempt = await host.StartAtAsync(Start);

        Assert.Equal(HttpStatusCode.Redirect, attempt.Response.StatusCode);
        Assert.NotNull(attempt.Cookie);
        Assert.Single(host.Heimdall.StartBodies);
    }

    [Fact]
    public async Task The_chooser_and_the_index_links_are_still_plain_links_that_start_anonymously()
    {
        using var host = new HeimdallHost(realSessions: true);

        using var chooser = await GetAsync(host, "/auth/sign-in?returnUrl=/App");
        Assert.Contains("href=\"/auth/heimdall/discord?returnUrl=", await chooser.Content.ReadAsStringAsync());
        using var viaProvider = await GetAsync(host, "/auth/sign-in?provider=discord&returnUrl=/App");
        Assert.Equal("/auth/heimdall/discord?returnUrl=%2FApp", viaProvider.Headers.Location!.OriginalString);
        var memberLink = await host.StartAtAsync("/auth/heimdall/discord?access=member&returnUrl=/App");

        Assert.NotNull(memberLink.Cookie);
        Assert.Contains("entitlementPolicy", host.Heimdall.StartBodies[^1]);
    }

    [Fact]
    public async Task A_signed_in_browsers_navigation_starts_nothing_and_cannot_replace_its_session()
    {
        using var host = new HeimdallHost(realSessions: true);
        var session = await host.SignInSessionAsync(HeimdallShapes.NewAccountId());
        var before = host.Heimdall.StartBodies.Count;

        using var response = await GetAsync(host, Start, $"{BifrostCookies.Session}={session}", ("Sec-Fetch-Site", "cross-site"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, host.Heimdall.StartBodies.Count);
        Assert.Null(HeimdallHost.SetCookieHeader(response, HeimdallAttemptCookie.SlotNames[0]));
        Assert.Null(HeimdallHost.SetCookieHeader(response, HeimdallAttemptCookie.SlotNames[1]));
        Assert.Null(HeimdallHost.CookieValue(response, BifrostCookies.Session));
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("action=\"/auth/heimdall/discord\"", html);
        Assert.Contains("method=\"post\"", html);
    }

    [Fact]
    public async Task A_navigation_that_would_overwrite_a_live_attempt_starts_nothing_and_both_attempts_still_complete()
    {
        using var host = new HeimdallHost(realSessions: true);
        var jar = new List<string>();
        var started = new List<StartedAttempt>();
        for (var i = 0; i < HeimdallAttemptCookie.SlotCount; i++)
        {
            host.Clock.Advance(TimeSpan.FromSeconds(1));
            var attempt = await host.StartAtAsync(Start, jar.Count == 0 ? null : string.Join("; ", jar));
            jar.Add($"{attempt.CookieName}={attempt.Cookie}");
            started.Add(attempt);
        }

        var before = host.Heimdall.StartBodies.Count;
        using var response = await GetAsync(host, Start, string.Join("; ", jar));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, host.Heimdall.StartBodies.Count);
        Assert.All(HeimdallAttemptCookie.SlotNames, name => Assert.Null(HeimdallHost.SetCookieHeader(response, name)));
        foreach (var attempt in started)
        {
            var done = await host.CompleteWithHeaderAsync(
                attempt.Nonce, host.CodeFor(attempt, HeimdallShapes.NewAccountId()), string.Join("; ", jar));
            Assert.True(done.SignedIn);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_confirmation_form_starts_the_sign_in_with_the_return_path_and_member_access_it_carried(bool member)
    {
        using var host = new HeimdallHost(realSessions: true);
        var session = await host.SignInSessionAsync(HeimdallShapes.NewAccountId());
        var cookies = $"{BifrostCookies.Session}={session}";
        using var page = await GetAsync(host, "/auth/heimdall/discord?returnUrl=/App/Mill" + (member ? "&access=member" : ""), cookies);
        var (form, withToken, _) = await ReadConfirmationAsync(page, cookies);
        var before = host.Heimdall.StartBodies.Count;

        using var started = await PostAsync(host, "/auth/heimdall/discord", form, withToken);

        Assert.Equal(HttpStatusCode.Redirect, started.StatusCode);
        Assert.Equal(before + 1, host.Heimdall.StartBodies.Count);
        Assert.Equal(member, host.Heimdall.StartBodies[^1].Contains("entitlementPolicy"));
        var attempt = host.AttemptFrom(started, before);
        Assert.NotNull(attempt.Cookie);
        if (!member)
        {
            var done = await host.CompleteAsync(attempt, host.CodeFor(attempt, HeimdallShapes.NewAccountId()));
            Assert.True(done.SignedIn);
            Assert.Equal("/App/Mill", done.Response.Headers.Location!.OriginalString);
        }
    }

    [Fact]
    public async Task A_post_without_the_pages_token_starts_nothing()
    {
        using var host = new HeimdallHost(realSessions: true);
        var session = await host.SignInSessionAsync(HeimdallShapes.NewAccountId());
        var cookies = $"{BifrostCookies.Session}={session}";
        using var page = await GetAsync(host, Start, cookies);
        var (form, withToken, _) = await ReadConfirmationAsync(page, cookies);
        var before = host.Heimdall.StartBodies.Count;
        var forged = new Dictionary<string, string> { ["returnUrl"] = "/App", ["access"] = "" };
        var wrongToken = new Dictionary<string, string>(form) { ["__RequestVerificationToken"] = "x" + form["__RequestVerificationToken"] };

        foreach (var (body, jar) in new[] { (forged, cookies), (forged, withToken), (form, cookies), (wrongToken, withToken) })
        {
            using var response = await PostAsync(host, "/auth/heimdall/discord", body, jar);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.All(HeimdallAttemptCookie.SlotNames, name => Assert.Null(HeimdallHost.SetCookieHeader(response, name)));
        }

        Assert.Equal(before, host.Heimdall.StartBodies.Count);
    }

    [Fact]
    public async Task The_confirmation_page_echoes_neither_a_provider_nor_a_hostile_return_path()
    {
        using var host = new HeimdallHost(realSessions: true);
        var session = await host.SignInSessionAsync(HeimdallShapes.NewAccountId());
        var cookies = $"{BifrostCookies.Session}={session}";
        var before = host.Heimdall.StartBodies.Count;

        using var unknown = await GetAsync(host, "/auth/heimdall/%3Cscript%3Ealert(1)%3C%2Fscript%3E?returnUrl=/App", cookies);
        Assert.Equal(HttpStatusCode.Redirect, unknown.StatusCode);
        Assert.Equal(HeimdallAuthService.ErrorRedirect, unknown.Headers.Location!.OriginalString);

        using var hostile = await GetAsync(host, "/auth/heimdall/discord?returnUrl=/App%22%3E%3Cscript%3Ealert(1)%3C/script%3E", cookies);
        var html = await hostile.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, hostile.StatusCode);
        Assert.DoesNotContain("<script>", html);
        Assert.Equal(before, host.Heimdall.StartBodies.Count);

        using var upper = await GetAsync(host, "/auth/heimdall/DISCORD?returnUrl=/App", cookies);
        Assert.Contains("action=\"/auth/heimdall/discord\"", await upper.Content.ReadAsStringAsync());
    }
}
