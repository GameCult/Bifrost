using System.Net;
using System.Text.RegularExpressions;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bifrost.Web.Tests;

public sealed partial class ConceptVotePageTests : IClassFixture<TestWebApplicationFactory>
{
    private const string CookieName = "bifrost_voter";
    private const string Purpose = "Bifrost.ConceptVotes.DeviceToken.v1";

    private readonly TestWebApplicationFactory _factory;

    public ConceptVotePageTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Anonymous_visitor_can_cast_change_and_retract()
    {
        var collection = NewCollection();
        await AddSubject(collection, "alpha");
        var visitor = NewVisitor(anonymous: true);

        using var cast = await visitor.Post(collection, "alpha", "Up");

        Assert.Equal(HttpStatusCode.Redirect, cast.StatusCode);
        var setCookie = SetCookieHeader(cast, CookieName);
        Assert.NotNull(setCookie);
        var attributes = setCookie!.Split(';').Select(x => x.Trim().ToLowerInvariant()).ToArray();
        Assert.Contains("httponly", attributes);
        Assert.Contains("secure", attributes);
        Assert.Contains("samesite=lax", attributes);
        Assert.Contains("path=/mill", attributes);
        Assert.Contains("max-age=31536000", attributes);

        using (var page = await visitor.Get(collection, "alpha"))
        {
            var (everyone, members) = Counts(await page.Content.ReadAsStringAsync());
            Assert.Equal("1 up, 0 down", everyone);
            Assert.Equal("0 up, 0 down", members);
        }

        var firstRow = Assert.Single(await Rows(collection, "alpha"));
        Assert.Equal(ConceptStance.Up, firstRow.Stance);
        Assert.Equal(VoterClass.Anonymous, firstRow.VoterClass);

        // The cookie from the first cast is the voter: casting again changes the row and mints nothing.
        using var change = await visitor.Post(collection, "alpha", "Down");
        Assert.Equal(HttpStatusCode.Redirect, change.StatusCode);
        Assert.Null(SetCookieHeader(change, CookieName));
        var changed = Assert.Single(await Rows(collection, "alpha"));
        Assert.Equal(ConceptStance.Down, changed.Stance);
        Assert.Equal(firstRow.VoterKey, changed.VoterKey);

        using var retract = await visitor.Post(collection, "alpha", "Retract");
        Assert.Equal(HttpStatusCode.Redirect, retract.StatusCode);
        Assert.Null(SetCookieHeader(retract, CookieName));
        Assert.Empty(await Rows(collection, "alpha"));
    }

    [Fact]
    public async Task Get_never_sets_the_device_cookie()
    {
        var collection = NewCollection();
        await AddSubject(collection, "alpha");
        var visitor = NewVisitor(anonymous: true);

        using (var first = await visitor.Get(collection, "alpha"))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Null(SetCookieHeader(first, CookieName));
        }

        using (var retract = await visitor.Post(collection, "alpha", "Retract"))
        {
            Assert.Equal(HttpStatusCode.Redirect, retract.StatusCode);
            Assert.Null(SetCookieHeader(retract, CookieName));
        }

        Assert.False(visitor.Jar.ContainsKey(CookieName));
        Assert.Empty(await Rows(collection, "alpha"));

        using var cast = await visitor.Post(collection, "alpha", "Up");
        Assert.NotNull(SetCookieHeader(cast, CookieName));
        using var later = await visitor.Get(collection, "alpha");
        Assert.Null(SetCookieHeader(later, CookieName));
    }

    [Fact]
    public async Task Signed_in_vote_is_keyed_to_the_account()
    {
        var collection = NewCollection();
        await AddSubject(collection, "alpha");
        var visitor = NewVisitor(anonymous: false);

        using var cast = await visitor.Post(collection, "alpha", "Up");

        Assert.Equal(HttpStatusCode.Redirect, cast.StatusCode);
        Assert.Null(SetCookieHeader(cast, CookieName));
        var row = Assert.Single(await Rows(collection, "alpha"));
        Assert.StartsWith("account:", row.VoterKey, StringComparison.Ordinal);
        Assert.Equal(VoterClass.Member, row.VoterClass);
        Assert.NotNull(row.UserAccountId);

        using var page = await visitor.Get(collection, "alpha");
        var (everyone, members) = Counts(await page.Content.ReadAsStringAsync());
        Assert.Equal("1 up, 0 down", everyone);
        Assert.Equal("1 up, 0 down", members);
        Assert.Null(SetCookieHeader(page, CookieName));
    }

    private const string SiteOrigin = "https://localhost";

    [Fact]
    public async Task Cast_from_the_sites_own_origin_is_accepted_and_writes_one_row()
    {
        var collection = NewCollection();
        await AddSubject(collection, "alpha");
        var visitor = NewVisitor(anonymous: true);

        using var byOrigin = await visitor.Post(collection, "alpha", "Up", origin: SiteOrigin);
        Assert.Equal(HttpStatusCode.Redirect, byOrigin.StatusCode);
        Assert.Single(await Rows(collection, "alpha"));

        using var byFetchSite = await visitor.Post(collection, "alpha", "Down", origin: null, fetchSite: "same-origin");
        Assert.Equal(HttpStatusCode.Redirect, byFetchSite.StatusCode);
        Assert.Equal(ConceptStance.Down, Assert.Single(await Rows(collection, "alpha")).Stance);
    }

    [Theory]
    [InlineData("https://localhost.evil.example")]
    [InlineData("https://evil-localhost")]
    [InlineData("https://sub.localhost")]
    [InlineData("https://LOCALHOST")]
    [InlineData("HTTPS://localhost")]
    [InlineData("https://localhost:8443")]
    [InlineData("http://localhost")]
    [InlineData("null")]
    [InlineData("https://localhost/")]
    [InlineData("https://evil.example")]
    public async Task Cast_from_any_other_origin_is_refused_even_when_fetch_site_says_same_origin(string origin)
    {
        var collection = NewCollection();
        await AddSubject(collection, "alpha");
        var visitor = NewVisitor(anonymous: true);

        foreach (var handler in new[] { "Up", "Down", "Retract" })
        {
            using var response = await visitor.Post(collection, "alpha", handler, origin: origin, fetchSite: "same-origin");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Null(SetCookieHeader(response, CookieName));
            Assert.False(response.Headers.Contains("Set-Cookie"));
            Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        }

        Assert.Empty(await Rows(collection, "alpha"));
    }

    [Fact]
    public async Task Cast_with_the_origin_header_repeated_is_refused_even_when_every_value_is_the_sites()
    {
        var collection = NewCollection();
        await AddSubject(collection, "alpha");
        var visitor = NewVisitor(anonymous: true);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/mill/{collection}/alpha?handler=Up")
        {
            Content = new FormUrlEncodedContent([])
        };
        request.Headers.TryAddWithoutValidation("Origin", [SiteOrigin, SiteOrigin]);

        using var response = await visitor.Send(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await Rows(collection, "alpha"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("same-origin")]
    public async Task Cast_with_an_empty_origin_header_is_refused_whatever_fetch_site_says(string? fetchSite)
    {
        var collection = NewCollection();
        await AddSubject(collection, "alpha");

        // HttpClient drops a header whose value is empty, so the request is built on the server itself, where an
        // Origin header with an empty value is present in the dictionary as a browser's would be.
        var context = await _factory.Server.SendAsync(http =>
        {
            http.Request.Method = HttpMethods.Post;
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("localhost");
            http.Request.Path = $"/mill/{collection}/alpha";
            http.Request.QueryString = new QueryString("?handler=Up");
            http.Request.ContentType = "application/x-www-form-urlencoded";
            http.Request.Headers["X-Test-Anonymous"] = "true";
            http.Request.Headers["Origin"] = string.Empty;
            if (fetchSite is not null)
            {
                http.Request.Headers["Sec-Fetch-Site"] = fetchSite;
            }
        });

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Set-Cookie"));
        Assert.Empty(await Rows(collection, "alpha"));
    }

    [Theory]
    [InlineData("same-origin", true)]
    [InlineData("same-site", false)]
    [InlineData("cross-site", false)]
    [InlineData("none", false)]
    [InlineData("Same-Origin", false)]
    [InlineData("same-origin, cross-site", false)]
    [InlineData(null, false)]
    public async Task Cast_without_an_origin_header_needs_fetch_site_same_origin(string? fetchSite, bool accepted)
    {
        var collection = NewCollection();
        await AddSubject(collection, "alpha");
        var visitor = NewVisitor(anonymous: true);

        using var response = await visitor.Post(collection, "alpha", "Up", origin: null, fetchSite: fetchSite);

        Assert.Equal(accepted ? HttpStatusCode.Redirect : HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(accepted ? 1 : 0, (await Rows(collection, "alpha")).Count);
    }

    [Fact]
    public async Task Refusal_never_echoes_the_origin_or_fetch_site_values()
    {
        const string canary = "https://CANARY-origin-5e1c.example";
        var logs = new CapturingLoggerProvider();
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddLogging(l => l.AddProvider(logs))));
        var collection = NewCollection();
        await AddSubject(collection, "alpha");
        var visitor = NewVisitor(anonymous: true, factory);

        using var byOrigin = await visitor.Post(collection, "alpha", "Up", origin: canary);
        using var byFetchSite = await visitor.Post(collection, "alpha", "Up", origin: null, fetchSite: "CANARY-site-5e1c");

        foreach (var response in new[] { byOrigin, byFetchSite })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var seen = await response.Content.ReadAsStringAsync()
                + string.Join("\n", response.Headers.Select(h => h.Key + ":" + string.Join(",", h.Value)));
            Assert.DoesNotContain("CANARY", seen, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain(logs.Lines, line => line.Contains("CANARY", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_page_view_sets_no_cookie_at_all_and_a_cast_sets_only_the_device_cookie()
    {
        var collection = NewCollection();
        await AddSubject(collection, "alpha");
        var visitor = NewVisitor(anonymous: true);

        using (var view = await visitor.Get(collection, "alpha"))
        {
            Assert.Equal(HttpStatusCode.OK, view.StatusCode);
            Assert.False(view.Headers.Contains("Set-Cookie"));
            var html = await view.Content.ReadAsStringAsync();
            Assert.DoesNotContain("__RequestVerificationToken", html, StringComparison.Ordinal);
        }

        using var cast = await visitor.Post(collection, "alpha", "Up");
        var cookies = cast.Headers.GetValues("Set-Cookie").ToArray();
        Assert.Single(cookies);
        Assert.StartsWith(CookieName + "=", cookies[0], StringComparison.Ordinal);

        using var later = await visitor.Get(collection, "alpha");
        Assert.False(later.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task A_request_that_selects_no_handler_is_a_client_error_and_never_a_500()
    {
        var collection = NewCollection();
        await AddSubject(collection, "alpha");
        var visitor = NewVisitor(anonymous: true);

        using var noHandler = await visitor.Post(collection, "alpha", handler: null);
        using var bogus = await visitor.Post(collection, "alpha", "CANARYQ9");
        using var put = await visitor.Post(collection, "alpha", "Up", method: HttpMethod.Put);
        using var delete = await visitor.Post(collection, "alpha", null, method: HttpMethod.Delete);
        using var getBogus = await visitor.Send(new HttpRequestMessage(HttpMethod.Get, $"/mill/{collection}/alpha?handler=CANARYQ9"));
        using var getUp = await visitor.Send(new HttpRequestMessage(HttpMethod.Get, $"/mill/{collection}/alpha?handler=Up"));

        Assert.Equal(HttpStatusCode.BadRequest, noHandler.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bogus.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, put.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, getBogus.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, getUp.StatusCode);
        foreach (var response in new[] { noHandler, bogus, put, delete, getBogus, getUp })
        {
            var status = (int)response.StatusCode;
            Assert.InRange(status, 400, 499);
            Assert.DoesNotContain("CANARYQ9", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }

        Assert.Empty(await Rows(collection, "alpha"));
    }

    public enum Forgery
    {
        OneCharacterChanged,
        BareDeviceId,
        SameKeyRingOtherPurpose,
        OtherKeyRingSamePurpose
    }

    [Theory]
    [InlineData(Forgery.OneCharacterChanged)]
    [InlineData(Forgery.BareDeviceId)]
    [InlineData(Forgery.SameKeyRingOtherPurpose)]
    [InlineData(Forgery.OtherKeyRingSamePurpose)]
    public async Task Tampered_or_foreign_cookie_is_ignored_and_a_new_one_minted(Forgery kind)
    {
        var collection = NewCollection();
        await AddSubject(collection, "alpha");
        var victim = NewVisitor(anonymous: true);
        using (await victim.Post(collection, "alpha", "Up"))
        {
        }

        var genuine = victim.Jar[CookieName];
        var victimRow = Assert.Single(await Rows(collection, "alpha"));
        var deviceId = victimRow.VoterKey["device:".Length..];
        var forged = Forge(kind, genuine, deviceId);
        Assert.NotEqual(genuine, forged);
        if (kind is not Forgery.BareDeviceId)
        {
            Assert.Equal(genuine.Length, forged.Length);
            Assert.Matches("^[A-Za-z0-9_-]+$", forged);
        }

        var attacker = NewVisitor(anonymous: true);
        attacker.Jar[CookieName] = forged;
        using var cast = await attacker.Post(collection, "alpha", "Down");

        Assert.Equal(HttpStatusCode.Redirect, cast.StatusCode);
        Assert.NotNull(SetCookieHeader(cast, CookieName));
        Assert.NotEqual(forged, attacker.Jar[CookieName]);
        Assert.NotEqual(genuine, attacker.Jar[CookieName]);
        var rows = await Rows(collection, "alpha");
        Assert.Equal(2, rows.Count);
        var victimNow = Assert.Single(rows, x => x.VoterKey == victimRow.VoterKey);
        Assert.Equal(ConceptStance.Up, victimNow.Stance);
        var attackerRow = Assert.Single(rows, x => x.VoterKey != victimRow.VoterKey);
        Assert.Equal(ConceptStance.Down, attackerRow.Stance);
        Assert.Equal(VoterClass.Anonymous, attackerRow.VoterClass);
    }

    [Fact]
    public async Task A_forged_cookie_value_is_echoed_by_no_response_and_no_log_line()
    {
        const string canary = "CANARY-forged-voter-cookie-7f3a";
        var logs = new CapturingLoggerProvider();
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddLogging(l => l.AddProvider(logs))));
        var collection = NewCollection();
        await AddSubject(collection, "alpha");
        var visitor = NewVisitor(anonymous: true, factory);
        visitor.Jar[CookieName] = canary;

        using var page = await visitor.Get(collection, "alpha");
        using var cast = await visitor.Post(collection, "alpha", "Up");

        foreach (var response in new[] { page, cast })
        {
            Assert.DoesNotContain(canary, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.DoesNotContain(
                canary,
                string.Join("\n", response.Headers.Select(h => h.Key + ":" + string.Join(",", h.Value))),
                StringComparison.Ordinal);
        }

        Assert.NotEmpty(logs.Lines);
        Assert.DoesNotContain(logs.Lines, line => line.Contains(canary, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unknown_or_retired_key_is_404()
    {
        var collection = NewCollection();
        await AddSubject(collection, "alpha");
        await AddSubject(collection, "gone");
        await Retire(collection, "gone");
        var visitor = NewVisitor(anonymous: true);

        foreach (var (c, k) in new[] { (collection, "gone"), (collection, "never-registered"), (NewCollection(), "alpha") })
        {
            using var get = await visitor.Get(c, k);
            Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
            foreach (var handler in new[] { "Up", "Down", "Retract" })
            {
                using var post = await visitor.Post(c, k, handler);
                Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
                Assert.Null(SetCookieHeader(post, CookieName));
            }
        }

        Assert.Empty(await Rows(collection, "gone"));
        Assert.Empty(await Rows(collection, "alpha"));
        Assert.False(visitor.Jar.ContainsKey(CookieName));
    }

    [Fact]
    public async Task Page_shows_counts_by_class_and_no_voter_identity()
    {
        var collection = NewCollection();
        await AddSubject(collection, "alpha");
        var memberId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        await AddRows(collection, "alpha",
            Row("device:canary-device-id-4b1d", VoterClass.Anonymous, ConceptStance.Up, null, "canarytag-aaaa"),
            Row("device:canary-device-id-5c2e", VoterClass.Anonymous, ConceptStance.Down, null, "canarytag-bbbb"),
            Row("account:" + memberId.ToString("N"), VoterClass.Member, ConceptStance.Up, memberId, "canarytag-cccc"),
            Row("account:" + accountId.ToString("N"), VoterClass.Account, ConceptStance.Down, accountId, "canarytag-dddd"));
        var visitor = NewVisitor(anonymous: true);
        using (await visitor.Post(collection, "alpha", "Up"))
        {
        }

        using var page = await visitor.Get(collection, "alpha");
        var html = await page.Content.ReadAsStringAsync();

        var (everyone, members) = Counts(html);
        Assert.Equal("3 up, 2 down", everyone);
        Assert.Equal("1 up, 0 down", members);
        Assert.Contains("Your vote: up.", html, StringComparison.Ordinal);

        var visitorRow = Assert.Single(await Rows(collection, "alpha"), x => x.UserAccountId is null && !x.VoterKey.Contains("canary"));
        var forbidden = new List<string>
        {
            "device:", "account:", "canary-device-id", "canarytag", visitorRow.VoterKey,
            visitor.Jar[CookieName],
            memberId.ToString("N"), memberId.ToString("D"), accountId.ToString("N"), accountId.ToString("D")
        };
        foreach (var value in forbidden)
        {
            Assert.DoesNotContain(value, html, StringComparison.OrdinalIgnoreCase);
        }

        Assert.True(page.Headers.CacheControl is { NoStore: true } or { NoCache: true });

        var signedIn = NewVisitor(anonymous: false);
        using var signedInPage = await signedIn.Get(collection, "alpha");
        var signedInHtml = await signedInPage.Content.ReadAsStringAsync();
        using var signedInCast = await signedIn.Post(collection, "alpha", "Down");
        var accountRow = Assert.Single(
            await Rows(collection, "alpha"),
            x => x.UserAccountId is { } id && id != memberId && id != accountId);
        using var afterCast = await signedIn.Get(collection, "alpha");
        var afterHtml = await afterCast.Content.ReadAsStringAsync();
        Assert.Contains("Your vote: down.", afterHtml, StringComparison.Ordinal);
        foreach (var text in new[] { signedInHtml, afterHtml })
        {
            Assert.DoesNotContain("account:", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(accountRow.UserAccountId!.Value.ToString("N"), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(accountRow.UserAccountId!.Value.ToString("D"), text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Cookie_notice_is_shown_to_anonymous_visitors_and_the_page_links_back_to_the_mill()
    {
        var collection = NewCollection();
        await AddSubject(collection, "alpha");

        using var anonymousPage = await NewVisitor(anonymous: true).Get(collection, "alpha");
        var html = await anonymousPage.Content.ReadAsStringAsync();
        using var signedInPage = await NewVisitor(anonymous: false).Get(collection, "alpha");
        var signedInHtml = await signedInPage.Content.ReadAsStringAsync();

        Assert.Contains("only purpose is to keep your one vote yours", html, StringComparison.Ordinal);
        Assert.Contains("lasts a year", html, StringComparison.Ordinal);
        Assert.Contains("carries nothing else", html, StringComparison.Ordinal);
        Assert.DoesNotContain("only purpose is to keep your one vote yours", signedInHtml, StringComparison.Ordinal);
        Assert.Contains(
            "href=\"https://aetheria.gamecult.org/Game-Design/Concept-Mill#alpha\"",
            html,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Page_loads_no_script_and_posts_with_plain_forms()
    {
        var collection = NewCollection();
        await AddSubject(collection, "alpha");

        using var page = await NewVisitor(anonymous: true).Get(collection, "alpha");
        var html = await page.Content.ReadAsStringAsync();
        var main = html[html.IndexOf("<main", StringComparison.Ordinal)..html.IndexOf("</main>", StringComparison.Ordinal)];

        Assert.DoesNotContain("<script", main, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hx-post", main, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hx-get", main, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("method=\"post\"", main, StringComparison.Ordinal);
    }

    private string Forge(Forgery kind, string genuine, string deviceId) => kind switch
    {
        Forgery.OneCharacterChanged => genuine[..(genuine.Length / 2)]
            + (genuine[genuine.Length / 2] == 'A' ? 'B' : 'A')
            + genuine[(genuine.Length / 2 + 1)..],
        Forgery.BareDeviceId => deviceId,
        Forgery.SameKeyRingOtherPurpose => _factory.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(Purpose + ".other").Protect(deviceId),
        Forgery.OtherKeyRingSamePurpose => new EphemeralDataProtectionProvider()
            .CreateProtector(Purpose).Protect(deviceId),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string NewCollection() => "c" + Guid.NewGuid().ToString("N");

    private Visitor NewVisitor(bool anonymous, WebApplicationFactory<Program>? factory = null) =>
        new((factory ?? _factory).CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost")
        }), anonymous);

    private static string? SetCookieHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(x => x.StartsWith(name + "=", StringComparison.Ordinal))
            : null;

    private static (string Everyone, string Members) Counts(string html)
    {
        var cells = DefinitionCell().Matches(html).Select(m => m.Groups[1].Value.Trim()).ToArray();
        Assert.Equal(2, cells.Length);
        return (cells[0], cells[1]);
    }

    [GeneratedRegex("<dd>(.*?)</dd>", RegexOptions.Singleline)]
    private static partial Regex DefinitionCell();

    private async Task AddSubject(string collection, string key)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        db.ConceptSubjects.Add(new ConceptSubject
        {
            Collection = collection,
            Key = key,
            Title = key,
            RegisteredAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task Retire(string collection, string key)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var subject = await db.ConceptSubjects.SingleAsync(x => x.Collection == collection && x.Key == key);
        subject.RetiredAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private static ConceptAppraisal Row(string key, VoterClass voterClass, ConceptStance stance, Guid? accountId, string tag) =>
        new()
        {
            VoterKey = key,
            VoterClass = voterClass,
            Stance = stance,
            UserAccountId = accountId,
            NetworkTag = tag,
            CastAtUtc = DateTimeOffset.UtcNow
        };

    private async Task AddRows(string collection, string key, params ConceptAppraisal[] rows)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var subject = await db.ConceptSubjects.SingleAsync(x => x.Collection == collection && x.Key == key);
        foreach (var row in rows)
        {
            row.SubjectId = subject.Id;
            db.ConceptAppraisals.Add(row);
        }

        await db.SaveChangesAsync();
    }

    // A fresh DbContext per read: what a later request would see, not what this test's tracker remembers.
    private async Task<List<ConceptAppraisal>> Rows(string collection, string key)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        return await db.ConceptAppraisals.AsNoTracking()
            .Where(x => x.Subject!.Collection == collection && x.Subject.Key == key)
            .ToListAsync();
    }

    private sealed class Visitor(HttpClient client, bool anonymous)
    {
        public Dictionary<string, string> Jar { get; } = new(StringComparer.Ordinal);

        public Task<HttpResponseMessage> Get(string collection, string key) =>
            Send(new HttpRequestMessage(HttpMethod.Get, $"/mill/{collection}/{key}"));

        // A cookieless POST as a browser form would send it: Origin the site's own by default, null to omit it.
        public Task<HttpResponseMessage> Post(
            string collection,
            string key,
            string? handler,
            string? origin = SiteOrigin,
            string? fetchSite = null,
            HttpMethod? method = null)
        {
            var request = new HttpRequestMessage(
                method ?? HttpMethod.Post,
                $"/mill/{collection}/{key}" + (handler is null ? string.Empty : "?handler=" + handler))
            {
                Content = new FormUrlEncodedContent([])
            };
            if (origin is not null)
            {
                request.Headers.TryAddWithoutValidation("Origin", origin);
            }

            if (fetchSite is not null)
            {
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", fetchSite);
            }

            return Send(request);
        }

        public async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            if (anonymous)
            {
                request.Headers.Add("X-Test-Anonymous", "true");
            }

            if (Jar.Count > 0)
            {
                request.Headers.TryAddWithoutValidation(
                    "Cookie",
                    string.Join("; ", Jar.Select(x => x.Key + "=" + x.Value)));
            }

            var response = await client.SendAsync(request);
            if (response.Headers.TryGetValues("Set-Cookie", out var values))
            {
                foreach (var header in values)
                {
                    var pair = header.Split(';')[0];
                    var split = pair.IndexOf('=');
                    Jar[pair[..split]] = pair[(split + 1)..];
                }
            }

            return response;
        }
    }
}
