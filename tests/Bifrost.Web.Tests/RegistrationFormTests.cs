using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bifrost.Web.Configuration;
using Bifrost.Web.Data;
using Bifrost.Web.Features.ConceptVotes;
using Bifrost.Web.Features.Registration;
using Bifrost.Web.Features.Shared;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Tests;

public class RegistrationFormFactory : TestWebApplicationFactory
{
    public ManualClock Time { get; } = new();

    protected virtual string FallbackPermits => "100";

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bifrost:Registration:Captcha:FallbackWaitSeconds"] = "30",
                ["Bifrost:Registration:Limits:FallbackPermits"] = FallbackPermits
            }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
        });
    }
}

public sealed class RegistrationFallbackBudgetFactory : RegistrationFormFactory
{
    protected override string FallbackPermits => "2";
}

internal static class FormKit
{
    public static string Hidden(string html, string name)
    {
        var match = Regex.Match(html, $"<input type=\"hidden\" name=\"{name}\" value=\"([^\"]*)\"");
        Assert.True(match.Success, $"no hidden field {name}");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    public static async Task<string> PageAsync(HttpClient client, string query = "")
    {
        using var response = await client.GetAsync("/auth/bifrost/register" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    public const string SiteOrigin = "https://localhost";

    // A browser on the site's own page sends its Origin; the guard refuses a post without one.
    public static Task<HttpResponseMessage> PostAsync(HttpClient client, Dictionary<string, string> fields, string? origin = SiteOrigin, string? peer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/bifrost/register/form") { Content = new FormUrlEncodedContent(fields) };
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        if (peer is not null)
        {
            request.Headers.Add("X-Test-Peer", peer);
        }

        return client.SendAsync(request);
    }

    public static Dictionary<string, string> Fields(string html, string identity, bool consent = true, string? nonce = null, bool waited = false, string? returnUrl = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["identity"] = identity,
            ["displayName"] = "",
            ["challenge"] = Hidden(html, "challenge"),
            ["acceptedNoticeVersion"] = Hidden(html, "acceptedNoticeVersion"),
            ["returnUrl"] = returnUrl ?? Hidden(html, "returnUrl")
        };
        if (consent)
        {
            fields["consent"] = "yes";
        }

        if (nonce is not null)
        {
            fields["nonce"] = nonce;
        }

        if (waited)
        {
            fields["waited"] = "true";
        }

        return fields;
    }

    /// <summary>A nonce that provably fails this page's challenge: its hash lacks the leading zero bits the difficulty needs.</summary>
    public static string Unsolved(string html)
    {
        var challenge = Hidden(html, "challenge");
        var difficulty = int.Parse(Regex.Match(html, "data-difficulty=\"(\\d+)\"").Groups[1].Value);
        // Starts at a long marker value so a test can check the nonce is never echoed.
        for (var nonce = 7777777777L; ; nonce++)
        {
            var text = nonce.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (RegistrationChallengeService.LeadingZeroBits(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(challenge + ":" + text))) < difficulty)
            {
                return text;
            }
        }
    }

    public static string Solved(string html) => RegistrationKit.Solve(Hidden(html, "challenge"), int.Parse(Regex.Match(html, "data-difficulty=\"(\\d+)\"").Groups[1].Value));
}

public sealed class RegistrationFormTests(RegistrationFormFactory factory) : IClassFixture<RegistrationFormFactory>
{
    private static string NewIdentity() => $"form-{Guid.NewGuid():N}";

    private HttpClient Client() => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private (int Accounts, int Receipts) Counts()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        return (db.UserAccounts.Count(), db.ConsentReceipts.Count());
    }

    private bool Exists(string identity)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<BifrostDbContext>().UserAccounts.Any(x => x.NormalizedBifrostIdentity == identity);
    }

    private (bool Account, bool Receipt) Created(string identity)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var account = db.UserAccounts.SingleOrDefault(x => x.NormalizedBifrostIdentity == identity);
        return (account is not null, account is not null && db.ConsentReceipts.Any(x => x.UserAccountId == account.Id && x.NoticeVersion == TrainingNotice.Version));
    }

    [Fact]
    public async Task The_page_shows_the_whole_notice_beside_an_unchecked_consent_and_touches_nothing_outside()
    {
        using var client = Client();
        using var response = await client.GetAsync("/auth/bifrost/register");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        foreach (var paragraph in TrainingNotice.Text.Split("\n\n"))
        {
            Assert.Contains(WebUtility.HtmlEncode(paragraph), html);
        }

        Assert.Contains($"version {TrainingNotice.Version}", html);
        Assert.Contains("<html lang=\"en\">", html);
        Assert.Matches("<input type=\"checkbox\" id=\"consent\" name=\"consent\" value=\"yes\" [^>]*/>", html);
        Assert.DoesNotMatch("<input type=\"checkbox\"[^>]*\\bchecked\\b", html);
        Assert.Contains("I agree to this notice (optional)", html);
        Assert.DoesNotMatch("<input type=\"checkbox\"[^>]*\\brequired\\b", html);
        foreach (var id in new[] { "identity", "displayName", "consent" })
        {
            Assert.Matches($"<label[^>]*for=\"{id}\"", html);
        }

        Assert.Contains("<fieldset>", html);
        Assert.Contains("<legend>", html);
        Assert.Contains("aria-live=\"polite\"", html);
        Assert.Equal(TrainingNotice.Version.ToString(), FormKit.Hidden(html, "acceptedNoticeVersion"));
        Assert.False(string.IsNullOrWhiteSpace(FormKit.Hidden(html, "challenge")));
        Assert.Matches("wait \\d+ seconds", html);
        Assert.Matches("<button type=\"submit\" id=\"skip\"[^>]*>Skip the automatic check and wait \\d+ seconds instead</button>", html);
        Assert.DoesNotContain("a few seconds", html);
        Assert.DoesNotContain("nothing is lost", html);
        Assert.Contains("tick it again if you want to agree", html);
        Assert.DoesNotContain("<noscript", html);
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
        foreach (Match url in Regex.Matches(html, "(?:src|href|action)=\"([^\"]*)\""))
        {
            Assert.Matches("^(/(?!/)|#)", url.Groups[1].Value);
        }
    }

    [Fact]
    public async Task A_solved_form_creates_the_account_and_receipt_signs_in_and_redirects_to_the_return_path()
    {
        using var client = Client();
        var html = await FormKit.PageAsync(client, "?returnUrl=%2FTasks%3Fx%3D1");
        Assert.Equal("/Tasks?x=1", FormKit.Hidden(html, "returnUrl"));
        var identity = NewIdentity();

        using var response = await FormKit.PostAsync(client, FormKit.Fields(html, identity, nonce: FormKit.Solved(html)));

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/Tasks?x=1", response.Headers.Location!.OriginalString);
        Assert.True(response.Headers.Contains("Set-Cookie"));
        Assert.Equal((true, true), Created(identity));
    }

    [Fact]
    public async Task The_default_return_path_is_App()
    {
        using var client = Client();
        var html = await FormKit.PageAsync(client);

        using var response = await FormKit.PostAsync(client, FormKit.Fields(html, NewIdentity(), nonce: FormKit.Solved(html), returnUrl: ""));

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/App", response.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("//evil.example")]
    [InlineData("https://evil.example/App")]
    [InlineData("/\\evil.example")]
    [InlineData("\\\\evil.example")]
    public async Task A_return_path_that_leaves_the_site_lands_on_App(string probe)
    {
        using var client = Client();
        var page = await FormKit.PageAsync(client, "?returnUrl=" + Uri.EscapeDataString(probe));
        Assert.Equal("/App", FormKit.Hidden(page, "returnUrl"));

        var html = await FormKit.PageAsync(client);
        using var response = await FormKit.PostAsync(client, FormKit.Fields(html, NewIdentity(), nonce: FormKit.Solved(html), returnUrl: probe));

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/App", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task A_non_ASCII_return_path_never_reaches_the_Location_header()
    {
        using var client = Client();
        var html = await FormKit.PageAsync(client);

        using var response = await FormKit.PostAsync(client, FormKit.Fields(html, NewIdentity(), nonce: FormKit.Solved(html), returnUrl: "/Tasks\u00e9"));

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/App", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Without_the_consent_box_the_account_is_created_with_no_receipt()
    {
        using var client = Client();
        var html = await FormKit.PageAsync(client);
        var identity = NewIdentity();

        using var response = await FormKit.PostAsync(client, FormKit.Fields(html, identity, consent: false, nonce: FormKit.Solved(html)));

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.True(Exists(identity));
        Assert.False(Created(identity).Receipt);
        using var scope = factory.Services.CreateScope();
        Assert.Empty(scope.ServiceProvider.GetRequiredService<BifrostDbContext>().ConsentReceipts.Where(x => x.UserAccount!.NormalizedBifrostIdentity == identity));
    }

    [Fact]
    public async Task The_consent_box_with_a_stale_version_returns_the_page_with_an_error_and_nothing_created()
    {
        using var client = Client();
        var html = await FormKit.PageAsync(client);
        var challenge = FormKit.Hidden(html, "challenge");
        var identity = NewIdentity();
        var before = Counts();
        var fields = FormKit.Fields(html, identity, nonce: FormKit.Solved(html));
        fields["acceptedNoticeVersion"] = "1";

        using var response = await FormKit.PostAsync(client, fields);
        var again = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Matches("role=\"alert\"", again);
        Assert.Matches("id=\"consent\"[^>]*aria-invalid=\"true\" aria-describedby=\"errors\"", again);
        Assert.Contains("<a href=\"#consent\">", again);
        Assert.DoesNotMatch("<input type=\"checkbox\"[^>]*\\bchecked\\b", again);
        Assert.Contains($"value=\"{identity}\"", again);
        Assert.NotEqual(challenge, FormKit.Hidden(again, "challenge"));
        Assert.Equal(before, Counts());
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("02")]
    [InlineData("002")]
    [InlineData("+2")]
    [InlineData(" 2")]
    public async Task The_consent_box_with_a_version_that_is_not_the_canonical_decimal_is_refused_not_read_as_no_consent(string posted)
    {
        using var client = Client();
        var html = await FormKit.PageAsync(client);
        var identity = NewIdentity();
        var fields = FormKit.Fields(html, identity, nonce: FormKit.Solved(html));
        fields["acceptedNoticeVersion"] = posted;

        using var response = await FormKit.PostAsync(client, fields);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(Exists(identity));
    }

    [Fact]
    public async Task A_wrong_nonce_fails_the_challenge_and_the_error_never_echoes_the_challenge_or_nonce()
    {
        using var client = Client();
        var html = await FormKit.PageAsync(client);
        var challenge = FormKit.Hidden(html, "challenge");
        var identity = NewIdentity();

        var nonce = FormKit.Unsolved(html);

        using var response = await FormKit.PostAsync(client, FormKit.Fields(html, identity, nonce: nonce));
        var again = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Registration challenge failed.", again);
        Assert.DoesNotContain(challenge, again);
        Assert.DoesNotContain(nonce, again);
        Assert.False(Exists(identity));
    }

    [Fact]
    public async Task An_identity_with_markup_is_escaped_when_the_page_comes_back()
    {
        using var client = Client();
        var html = await FormKit.PageAsync(client);
        const string hostile = "<script>alert(1)</script>\"><img src=x>";

        using var response = await FormKit.PostAsync(client, FormKit.Fields(html, hostile, consent: false));
        var again = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("<script>alert(1)", again);
        Assert.DoesNotContain("<img src=x>", again);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", again);
        Assert.Single(Regex.Matches(again, "<script>"));
    }

    [Fact]
    public async Task A_taken_identity_marks_the_identity_field_invalid()
    {
        using var client = Client();
        var identity = NewIdentity();
        var first = await FormKit.PageAsync(client);
        using (await FormKit.PostAsync(client, FormKit.Fields(first, identity, nonce: FormKit.Solved(first)))) { }

        var second = await FormKit.PageAsync(client);
        using var response = await FormKit.PostAsync(client, FormKit.Fields(second, identity, nonce: FormKit.Solved(second)));
        var again = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("already registered", again);
        Assert.Matches("id=\"identity\"[^>]*aria-invalid=\"true\" aria-describedby=\"errors\"", again);
        Assert.Contains($"value=\"{identity}\"", again);
        Assert.Contains("<a href=\"#identity\">", again);
        Assert.Equal(FormKit.Hidden(second, "challenge"), FormKit.Hidden(again, "challenge"));
    }

    [Fact]
    public async Task The_time_lock_refuses_before_the_wait_with_the_seconds_left_keeps_the_challenge_and_accepts_after()
    {
        using var client = Client();
        var html = await FormKit.PageAsync(client);
        var challenge = FormKit.Hidden(html, "challenge");
        var identity = NewIdentity();
        var before = Counts();

        using var early = await FormKit.PostAsync(client, FormKit.Fields(html, identity, waited: true));
        var earlyPage = await early.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, early.StatusCode);
        Assert.Contains("30 more seconds", earlyPage);
        Assert.Equal(challenge, FormKit.Hidden(earlyPage, "challenge"));
        Assert.Equal(before, Counts());

        factory.Time.Advance(TimeSpan.FromSeconds(10));
        using var stillEarly = await FormKit.PostAsync(client, FormKit.Fields(earlyPage, identity, waited: true));
        Assert.Contains("20 more seconds", await stillEarly.Content.ReadAsStringAsync());
        Assert.Equal(before, Counts());

        factory.Time.Advance(TimeSpan.FromSeconds(20));
        using var ready = await FormKit.PostAsync(client, FormKit.Fields(earlyPage, identity, waited: true));

        Assert.Equal(HttpStatusCode.SeeOther, ready.StatusCode);
        Assert.Equal((true, true), Created(identity));
    }

    [Fact]
    public async Task A_spent_time_lock_challenge_cannot_be_used_again_by_either_path()
    {
        using var client = Client();
        var html = await FormKit.PageAsync(client);
        factory.Time.Advance(TimeSpan.FromSeconds(31));
        using (var first = await FormKit.PostAsync(client, FormKit.Fields(html, NewIdentity(), waited: true)))
        {
            Assert.Equal(HttpStatusCode.SeeOther, first.StatusCode);
        }

        var replayWaited = NewIdentity();
        using var again = await FormKit.PostAsync(client, FormKit.Fields(html, replayWaited, waited: true));
        var replayPow = NewIdentity();
        using var viaWork = await FormKit.PostAsync(client, FormKit.Fields(html, replayPow, nonce: FormKit.Solved(html)));

        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, viaWork.StatusCode);
        Assert.False(Exists(replayWaited));
        Assert.False(Exists(replayPow));
    }

    [Fact]
    public async Task A_nonce_together_with_waited_is_refused_and_spends_nothing()
    {
        using var client = Client();
        var html = await FormKit.PageAsync(client);
        var identity = NewIdentity();
        factory.Time.Advance(TimeSpan.FromSeconds(31));
        var nonce = FormKit.Solved(html);

        using var both = await FormKit.PostAsync(client, FormKit.Fields(html, identity, nonce: nonce, waited: true));
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        Assert.Contains("not both", await both.Content.ReadAsStringAsync());
        Assert.False(Exists(identity));

        using var json = await RegistrationKit.PostAsync(client, new
        {
            identity,
            challenge = FormKit.Hidden(html, "challenge"),
            nonce,
            waited = true,
            acceptedNoticeVersion = TrainingNotice.Version
        }, anonymous: true);
        Assert.Equal(HttpStatusCode.BadRequest, json.StatusCode);
        Assert.False(Exists(identity));

        // The challenge was not spent by either refusal.
        using var ok = await RegistrationKit.PostAsync(client, new
        {
            identity,
            challenge = FormKit.Hidden(html, "challenge"),
            nonce,
            acceptedNoticeVersion = TrainingNotice.Version
        }, anonymous: true);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task The_JSON_endpoint_takes_waited_true_with_the_same_time_lock()
    {
        using var client = Client();
        var issued = await client.GetFromJsonAsync<JsonElement>("/auth/bifrost/challenge");
        var challenge = issued.GetProperty("challenge").GetString()!;
        var identity = NewIdentity();
        object Body() => new { identity, challenge, waited = true, acceptedNoticeVersion = TrainingNotice.Version };

        using var early = await RegistrationKit.PostAsync(client, Body(), anonymous: true);
        Assert.Equal(HttpStatusCode.BadRequest, early.StatusCode);
        Assert.Contains("more second", await early.Content.ReadAsStringAsync());
        Assert.False(Exists(identity));

        factory.Time.Advance(TimeSpan.FromSeconds(30));
        using var ready = await RegistrationKit.PostAsync(client, Body(), anonymous: true);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal((true, true), Created(identity));
    }

    [Fact]
    public async Task A_tampered_challenge_fails_the_time_lock()
    {
        using var client = Client();
        var html = await FormKit.PageAsync(client);
        var challenge = FormKit.Hidden(html, "challenge");
        var fields = FormKit.Fields(html, NewIdentity(), waited: true);
        fields["challenge"] = challenge[..^2] + (challenge[^2] == 'A' ? 'B' : 'A') + challenge[^1];
        factory.Time.Advance(TimeSpan.FromSeconds(31));

        using var response = await FormKit.PostAsync(client, fields);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Registration challenge failed.", await response.Content.ReadAsStringAsync());
        Assert.False(Exists(fields["identity"]));
    }

    [Fact]
    public async Task A_form_must_be_urlencoded()
    {
        using var client = Client();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/bifrost/register/form") { Content = JsonContent.Create(new { identity = "x" }) };
        request.Headers.Add("Origin", FormKit.SiteOrigin);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    public static TheoryData<string?, string?> CrossSite => new()
    {
        { "https://evil.example", null },
        { "https://evil.example", "same-origin" },
        { "null", null },
        { "https://localhost.evil.example", null },
        { null, "cross-site" },
        { null, "same-site" },
        { null, "none" }
    };

    [Theory]
    [MemberData(nameof(CrossSite))]
    public async Task A_cross_site_or_unattributed_post_is_refused_before_anything_is_read_or_spent(string? origin, string? fetchSite)
    {
        using var client = Client();
        var html = await FormKit.PageAsync(client);
        var identity = NewIdentity();
        var before = Counts();
        factory.Time.Advance(TimeSpan.FromSeconds(31));
        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/bifrost/register/form")
        {
            Content = new FormUrlEncodedContent(FormKit.Fields(html, identity, waited: true))
        };
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        if (fetchSite is not null)
        {
            request.Headers.Add("Sec-Fetch-Site", fetchSite);
        }

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(before, Counts());
        // The challenge was not spent by the refusal.
        using var ok = await FormKit.PostAsync(client, FormKit.Fields(html, identity, waited: true));
        Assert.Equal(HttpStatusCode.SeeOther, ok.StatusCode);
    }

    private async Task<HttpResponseMessage> PostWithAsync(HttpClient client, string html, string identity, string? origin, string? fetchSite, string? cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/bifrost/register/form")
        {
            Content = new FormUrlEncodedContent(FormKit.Fields(html, identity, nonce: FormKit.Solved(html)))
        };
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (fetchSite is not null) request.Headers.Add("Sec-Fetch-Site", fetchSite);
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task A_text_browser_sending_neither_header_is_accepted_only_without_a_session_cookie()
    {
        const string session = "__Host-bifrost_session=abc";
        using var client = Client();

        var html = await FormKit.PageAsync(client);
        var plain = NewIdentity();
        using (var ok = await PostWithAsync(client, html, plain, null, null, null))
        {
            Assert.Equal(HttpStatusCode.SeeOther, ok.StatusCode);
            Assert.Equal((true, true), Created(plain));
        }

        var second = await FormKit.PageAsync(client);
        var withSession = NewIdentity();
        using (var refused = await PostWithAsync(client, second, withSession, null, null, session))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.False(refused.Headers.Contains("Set-Cookie"));
            Assert.False(Exists(withSession));
        }

        // Any header present is judged as before, cookie or not; same-origin with a session passes.
        using (var crossNoCookie = await PostWithAsync(client, second, withSession, null, "cross-site", null))
        {
            Assert.Equal(HttpStatusCode.Forbidden, crossNoCookie.StatusCode);
        }

        using var sameOrigin = await PostWithAsync(client, second, withSession, FormKit.SiteOrigin, null, session);
        Assert.Equal(HttpStatusCode.SeeOther, sameOrigin.StatusCode);
    }

    [Fact]
    public async Task An_identity_error_carries_the_challenge_on_only_while_it_is_live()
    {
        using var client = Client();
        var html = await FormKit.PageAsync(client);
        var live = FormKit.Hidden(html, "challenge");

        // Live: kept.
        using (var kept = await FormKit.PostAsync(client, FormKit.Fields(html, "a!", nonce: "1")))
        {
            Assert.Equal(live, FormKit.Hidden(await kept.Content.ReadAsStringAsync(), "challenge"));
        }

        // Garbage: replaced by a fresh live one, never echoed.
        var garbage = FormKit.Fields(html, "a!", nonce: "1");
        garbage["challenge"] = "garbage-token";
        using (var replaced = await FormKit.PostAsync(client, garbage))
        {
            var page = await replaced.Content.ReadAsStringAsync();
            Assert.DoesNotContain("garbage-token", page);
            Assert.True(factory.Services.GetRequiredService<RegistrationChallengeService>().IsLive(FormKit.Hidden(page, "challenge")));
        }

        // Spent: replaced.
        var spentPage = await FormKit.PageAsync(client);
        using (var spend = await FormKit.PostAsync(client, FormKit.Fields(spentPage, NewIdentity(), nonce: FormKit.Solved(spentPage))))
        {
            Assert.Equal(HttpStatusCode.SeeOther, spend.StatusCode);
        }

        using (var afterSpend = await FormKit.PostAsync(client, FormKit.Fields(spentPage, "a!", nonce: "1")))
        {
            Assert.NotEqual(FormKit.Hidden(spentPage, "challenge"), FormKit.Hidden(await afterSpend.Content.ReadAsStringAsync(), "challenge"));
        }

        // Expired: replaced.
        factory.Time.Advance(RegistrationChallengeService.Lifetime + TimeSpan.FromSeconds(1));
        using var afterExpiry = await FormKit.PostAsync(client, FormKit.Fields(html, "a!", nonce: "1"));
        var expiredPage = await afterExpiry.Content.ReadAsStringAsync();
        Assert.NotEqual(live, FormKit.Hidden(expiredPage, "challenge"));
        Assert.True(factory.Services.GetRequiredService<RegistrationChallengeService>().IsLive(FormKit.Hidden(expiredPage, "challenge")));
    }

    [Fact]
    public async Task A_same_origin_post_without_an_Origin_header_is_accepted_on_Sec_Fetch_Site()
    {
        using var client = Client();
        var html = await FormKit.PageAsync(client);
        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/bifrost/register/form")
        {
            Content = new FormUrlEncodedContent(FormKit.Fields(html, NewIdentity(), nonce: FormKit.Solved(html)))
        };
        request.Headers.Add("Sec-Fetch-Site", "same-origin");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
    }

    [Fact]
    public async Task A_form_body_over_the_cap_is_refused_with_413_and_creates_nothing()
    {
        using var client = Client();
        var html = await FormKit.PageAsync(client);
        var fields = FormKit.Fields(html, NewIdentity(), nonce: FormKit.Solved(html));
        fields["padding"] = new string('x', 17 * 1024);
        var before = Counts();

        using var response = await FormKit.PostAsync(client, fields);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(before, Counts());
    }

    [Theory]
    [InlineData("support", "reserved")]
    [InlineData("a!", "3-80 characters")]
    public async Task A_bad_identity_is_refused_without_spending_the_challenge(string identity, string fragment)
    {
        using var client = Client();
        var html = await FormKit.PageAsync(client);
        var nonce = FormKit.Solved(html);

        using var bad = await FormKit.PostAsync(client, FormKit.Fields(html, identity, nonce: nonce));
        var again = await bad.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains(fragment, again);
        Assert.Equal(FormKit.Hidden(html, "challenge"), FormKit.Hidden(again, "challenge"));

        var good = NewIdentity();
        using var ok = await FormKit.PostAsync(client, FormKit.Fields(html, good, nonce: nonce));
        Assert.Equal(HttpStatusCode.SeeOther, ok.StatusCode);
        Assert.Equal((true, true), Created(good));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public async Task A_challenge_payload_of_another_shape_is_refused(int parts)
    {
        using var client = Client();
        var protector = factory.Services.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>()
            .CreateProtector(RegistrationChallengeService.Purpose);
        var now = factory.Time.GetUtcNow();
        var expiry = (now + TimeSpan.FromMinutes(1)).ToUnixTimeMilliseconds();
        var issued = (now - TimeSpan.FromMinutes(1)).ToUnixTimeMilliseconds();
        var fields = new[] { Guid.NewGuid().ToString("N"), expiry.ToString(), "1", issued.ToString(), "extra" };
        var challenge = protector.Protect(string.Join('.', fields.Take(parts)));
        var identity = NewIdentity();

        using var pow = await RegistrationKit.PostAsync(client, new { identity, challenge, nonce = RegistrationKit.Solve(challenge, 1), acceptedNoticeVersion = TrainingNotice.Version }, anonymous: true);
        using var waited = await RegistrationKit.PostAsync(client, new { identity, challenge, waited = true, acceptedNoticeVersion = TrainingNotice.Version }, anonymous: true);

        Assert.Equal(HttpStatusCode.BadRequest, pow.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, waited.StatusCode);
        Assert.False(Exists(identity));
    }

    [Fact]
    public async Task The_page_solver_script_finds_a_nonce_the_server_accepts_under_node()
    {
        using var client = Client();
        var html = await FormKit.PageAsync(client);
        var pagePath = Path.Combine(Path.GetTempPath(), $"bifrost-register-{Guid.NewGuid():N}.html");
        await File.WriteAllTextAsync(pagePath, html);
        try
        {
            var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Fixtures", "registration-page-harness.cjs"));
            start.ArgumentList.Add(pagePath);
            Process process;
            try
            {
                process = Process.Start(start)!;
            }
            catch (Win32Exception error)
            {
                throw new InvalidOperationException("Node must be on PATH to run the sign-up page's script: " + error.Message, error);
            }

            using (process)
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await process.WaitForExitAsync(timeout.Token);
                Assert.True(process.ExitCode == 0, await errors);
                var runs = JsonDocument.Parse(await output).RootElement.EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!);

                var solved = runs["solves"];
                Assert.Equal(1, solved.GetProperty("submitted").GetInt32());
                Assert.True(solved.GetProperty("waitedDisabled").GetBoolean());
                var first = solved.GetProperty("statuses")[0].GetString()!;
                Assert.StartsWith("Checking.", first);
                Assert.Contains("Skip the automatic check", first);
                Assert.Contains("slow device", first);
                Assert.DoesNotContain("a few seconds", first);
                var nonce = solved.GetProperty("nonce").GetString()!;
                var challenge = FormKit.Hidden(html, "challenge");
                Assert.True(RegistrationChallengeService.LeadingZeroBits(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(challenge + ":" + nonce))) >= int.Parse(solved.GetProperty("difficulty").GetString()!));

                // The server accepts the script's own nonce.
                var identity = NewIdentity();
                using var response = await FormKit.PostAsync(client, FormKit.Fields(html, identity, nonce: nonce));
                Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
                Assert.Equal((true, true), Created(identity));

                var skip = runs["skip-during-solve"];
                Assert.Equal(0, skip.GetProperty("submitted").GetInt32());
                Assert.Equal("", skip.GetProperty("nonce").GetString());
                Assert.False(skip.GetProperty("waitedDisabled").GetBoolean());
                Assert.Equal(0, skip.GetProperty("prevented").GetArrayLength());

                Assert.Equal(1, runs["double-press"].GetProperty("submitted").GetInt32());
                Assert.False(runs["no-subtle"].GetProperty("listening").GetBoolean());
            }
        }
        finally
        {
            File.Delete(pagePath);
        }
    }
}

public sealed class RegistrationFallbackBudgetTests(RegistrationFallbackBudgetFactory factory) : IClassFixture<RegistrationFallbackBudgetFactory>
{
    [Fact]
    public async Task Past_the_fallback_budget_the_time_lock_gets_429_with_retry_after_and_work_still_passes()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var pages = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            pages.Add(await FormKit.PageAsync(client));
        }

        factory.Time.Advance(TimeSpan.FromSeconds(31));
        var created = new List<string>();
        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? third = null;
        for (var i = 0; i < 3; i++)
        {
            var identity = $"fb-{Guid.NewGuid():N}";
            created.Add(identity);
            var response = await FormKit.PostAsync(client, FormKit.Fields(pages[i], identity, waited: true));
            statuses.Add(response.StatusCode);
            third = response;
        }

        Assert.Equal([HttpStatusCode.SeeOther, HttpStatusCode.SeeOther, HttpStatusCode.TooManyRequests], statuses);
        Assert.True(third!.Headers.RetryAfter is not null);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
            Assert.Equal(2, db.UserAccounts.Count(x => created.Contains(x.NormalizedBifrostIdentity!)));
        }

        // The refused challenge was not spent, and proof of work is not under the fallback budget.
        var viaWork = $"fb-{Guid.NewGuid():N}";
        using var ok = await FormKit.PostAsync(client, FormKit.Fields(pages[2], viaWork, nonce: FormKit.Solved(pages[2])));
        Assert.Equal(HttpStatusCode.SeeOther, ok.StatusCode);
    }
}

public sealed class RegistrationPageBudgetTests(RegistrationBudgetFactory factory) : IClassFixture<RegistrationBudgetFactory>
{
    [Fact]
    public async Task The_page_spends_the_same_per_address_budget_as_the_challenge_endpoint()
    {
        using var client = factory.CreateClient();
        var statuses = new List<HttpStatusCode>();
        foreach (var path in new[] { "/auth/bifrost/register", "/auth/bifrost/challenge", "/auth/bifrost/register", "/auth/bifrost/register" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("X-Test-Peer", "203.0.113.99");
            using var response = await client.SendAsync(request);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
    }
}

public sealed class RegistrationFallbackOptionsTests
{
    private sealed class StubEnvironment(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Bifrost.Web";
        public string ContentRootPath { get; set; } = string.Empty;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static void Validate(RegistrationOptions options) =>
        new StartupConfigurationValidator(
            new ConfigurationBuilder().Build(),
            new StubEnvironment("Testing"),
            Options.Create(new GitHubOAuthOptions()),
            Options.Create(new GitHubAppOptions()),
            Options.Create(new HeimdallOptions()),
            Options.Create(new StripeOptions()),
            Options.Create(new BifrostHostOptions()),
            Options.Create(new ConceptVoteOptions()),
            Options.Create(new DeskOptions()),
            Options.Create(options)).Validate();

    [Fact]
    public void The_defaults_pass() => Validate(new RegistrationOptions());

    [Theory]
    [InlineData(4)]
    [InlineData(301)]
    [InlineData(300)]
    public void A_wait_outside_5_to_299_seconds_is_refused(int seconds)
    {
        var options = new RegistrationOptions { Captcha = new RegistrationCaptchaOptions { FallbackWaitSeconds = seconds } };

        Assert.Contains("FallbackWaitSeconds", Assert.Throws<InvalidOperationException>(() => Validate(options)).Message);
    }

    [Theory]
    [InlineData(0, 3600)]
    [InlineData(3, 0)]
    public void A_fallback_budget_that_cannot_be_spent_is_refused(int permits, int windowSeconds)
    {
        var options = new RegistrationOptions { Limits = new RegistrationLimitsOptions { FallbackPermits = permits, FallbackWindow = TimeSpan.FromSeconds(windowSeconds) } };

        Assert.Contains("FallbackPermits", Assert.Throws<InvalidOperationException>(() => Validate(options)).Message);
    }
}

public sealed class RegistrationValidationBudgetFactory : RegistrationFormFactory
{
    protected override string FallbackPermits => "2";
}

public sealed class RegistrationValidationBudgetTests(RegistrationValidationBudgetFactory factory) : IClassFixture<RegistrationValidationBudgetFactory>
{
    [Fact]
    public async Task Identity_refusals_spend_neither_a_fallback_permit_nor_the_challenge()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var first = await FormKit.PageAsync(client);
        var second = await FormKit.PageAsync(client);
        var third = await FormKit.PageAsync(client);
        factory.Time.Advance(TimeSpan.FromSeconds(31));
        var taken = $"vb-{Guid.NewGuid():N}";

        using (var one = await FormKit.PostAsync(client, FormKit.Fields(first, taken, waited: true)))
        {
            Assert.Equal(HttpStatusCode.SeeOther, one.StatusCode);
        }

        foreach (var bad in new[] { taken, "support", "a!" })
        {
            using var refused = await FormKit.PostAsync(client, FormKit.Fields(second, bad, waited: true));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        using (var fresh = await FormKit.PostAsync(client, FormKit.Fields(second, $"vb-{Guid.NewGuid():N}", waited: true)))
        {
            Assert.Equal(HttpStatusCode.SeeOther, fresh.StatusCode);
        }

        // Two permits, two registrations: the three refusals above cost nothing.
        using var over = await FormKit.PostAsync(client, FormKit.Fields(third, $"vb-{Guid.NewGuid():N}", waited: true));
        Assert.Equal(HttpStatusCode.TooManyRequests, over.StatusCode);
    }
}

public sealed class RegistrationFormBudgetTests(RegistrationBudgetFactory factory) : IClassFixture<RegistrationBudgetFactory>
{
    [Fact]
    public async Task The_general_budget_is_spent_before_the_body_is_read_and_cross_site_posts_spend_nothing()
    {
        using var client = factory.CreateClient();
        const string peer = "203.0.113.150";
        var fields = new Dictionary<string, string> { ["identity"] = "x", ["padding"] = new string('x', 17 * 1024) };

        for (var i = 0; i < 5; i++)
        {
            using var crossSite = await FormKit.PostAsync(client, fields, origin: "https://evil.example", peer: peer);
            Assert.Equal(HttpStatusCode.Forbidden, crossSite.StatusCode);
        }

        // Three permits: the over-cap posts spend them (413), the next is 429 whatever its body.
        for (var i = 0; i < 3; i++)
        {
            using var big = await FormKit.PostAsync(client, fields, peer: peer);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.StatusCode);
        }

        using var refused = await FormKit.PostAsync(client, fields, peer: peer);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }
}
