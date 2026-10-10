using System.Net;
using System.Text.RegularExpressions;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.DataProtection;
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

    [Fact]
    public async Task Cast_without_antiforgery_token_is_refused()
    {
        var collection = NewCollection();
        await AddSubject(collection, "alpha");
        var visitor = NewVisitor(anonymous: true);

        using var noToken = await visitor.Post(collection, "alpha", "Up", token: Token.Omitted);
        using var noCookie = await visitor.Post(collection, "alpha", "Up", token: Token.WithoutAntiforgeryCookie);
        using var garbage = await visitor.Post(collection, "alpha", "Up", token: Token.Garbage);

        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noCookie.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
        Assert.Null(SetCookieHeader(noToken, CookieName));
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
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(logs)));
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
                using var post = await visitor.Post(c, k, handler, tokenFrom: (collection, "alpha"));
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

        var visitorRow = Assert.Single(await Rows(collection, "alpha"), x => !x.VoterKey.Contains("canary"));
        var forbidden = new List<string>
        {
            "device:", "account:", "canary-device-id", "canarytag", visitorRow.VoterKey, visitorRow.NetworkTag,
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
            Assert.DoesNotContain(accountRow.NetworkTag, text, StringComparison.OrdinalIgnoreCase);
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

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex RequestToken();

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

    public enum Token
    {
        Valid,
        Omitted,
        WithoutAntiforgeryCookie,
        Garbage
    }

    private sealed class Visitor(HttpClient client, bool anonymous)
    {
        public Dictionary<string, string> Jar { get; } = new(StringComparer.Ordinal);

        public Task<HttpResponseMessage> Get(string collection, string key) =>
            Send(new HttpRequestMessage(HttpMethod.Get, $"/mill/{collection}/{key}"));

        // Reads a token from the page of tokenFrom (default: the page being posted to), then posts the handler.
        public async Task<HttpResponseMessage> Post(
            string collection,
            string key,
            string handler,
            Token token = Token.Valid,
            (string Collection, string Key)? tokenFrom = null)
        {
            var from = tokenFrom ?? (collection, key);
            string value;
            using (var page = await Get(from.Collection, from.Key))
            {
                var match = RequestToken().Match(await page.Content.ReadAsStringAsync());
                Assert.True(match.Success);
                value = match.Groups[1].Value;
            }

            var antiforgery = Jar
                .Where(x => x.Key.StartsWith(".AspNetCore.Antiforgery", StringComparison.Ordinal))
                .ToList();
            if (token is Token.WithoutAntiforgeryCookie)
            {
                foreach (var cookie in antiforgery)
                {
                    Jar.Remove(cookie.Key);
                }
            }

            var form = new List<KeyValuePair<string, string>>();
            if (token is not Token.Omitted)
            {
                form.Add(new("__RequestVerificationToken", token is Token.Garbage ? "garbage" : value));
            }

            var request = new HttpRequestMessage(HttpMethod.Post, $"/mill/{collection}/{key}?handler={handler}")
            {
                Content = new FormUrlEncodedContent(form)
            };
            var response = await Send(request);
            foreach (var cookie in antiforgery)
            {
                Jar.TryAdd(cookie.Key, cookie.Value);
            }

            return response;
        }

        private async Task<HttpResponseMessage> Send(HttpRequestMessage request)
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

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return _lines.ToArray();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Capture(this);

        public void Dispose()
        {
        }

        private sealed class Capture(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner._lines)
                {
                    owner._lines.Add(formatter(state, exception) + (exception is null ? string.Empty : "\n" + exception));
                }
            }
        }
    }
}
