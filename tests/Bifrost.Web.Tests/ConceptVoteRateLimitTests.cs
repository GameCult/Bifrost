using System.Net;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.ConceptVotes;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Bifrost.Web.Tests;

// The vote budgets through the full pipeline: a loopback peer (the proxy) forwards the client address in
// X-Forwarded-For, the clock is the test's own, and Cast is 3 per 10 minutes, Mint 2 per hour.
public sealed class ConceptVoteRateLimitTests : IClassFixture<ConceptVoteRateLimitTests.LimitedFactory>
{
    private const string Site = "https://localhost";
    private const string CookieName = "bifrost_voter";

    private readonly LimitedFactory _factory;

    public ConceptVoteRateLimitTests(LimitedFactory factory) => _factory = factory;

    [Fact]
    public async Task Burst_from_one_prefix_gets_429_while_another_prefix_is_unaffected()
    {
        var collection = await NewSubject();
        var device = new Client(_factory, "203.0.113.10");
        foreach (var handler in new[] { "Up", "Down", "Up" })
        {
            using var ok = await device.Post(collection, handler);
            Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        }

        using var denied = await device.Post(collection, "Down");
        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);

        var other = new Client(_factory, "203.0.113.11");
        using var unaffected = await other.Post(collection, "Up");
        Assert.Equal(HttpStatusCode.Redirect, unaffected.StatusCode);
    }

    [Fact]
    public async Task Retract_spends_the_cast_budget()
    {
        var collection = await NewSubject();
        var device = new Client(_factory, "203.0.113.12");
        await device.Post(collection, "Up");
        await device.Post(collection, "Retract");
        await device.Post(collection, "Retract");

        using var denied = await device.Post(collection, "Retract");

        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
    }

    [Fact]
    public async Task Addresses_in_one_ipv6_prefix_share_a_bucket_and_another_prefix_does_not()
    {
        var collection = await NewSubject();
        var first = new Client(_factory, "2001:db8:1:100::1");
        var sameBlock = new Client(_factory, "2001:db8:1:1ff::2");
        await first.Post(collection, "Up");
        await first.Post(collection, "Down");
        using var third = await sameBlock.Post(collection, "Up");
        Assert.Equal(HttpStatusCode.Redirect, third.StatusCode);

        using var denied = await sameBlock.Post(collection, "Down");
        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);

        var elsewhere = new Client(_factory, "2001:db8:1:200::1");
        using var unaffected = await elsewhere.Post(collection, "Up");
        Assert.Equal(HttpStatusCode.Redirect, unaffected.StatusCode);
    }

    [Fact]
    public async Task The_window_ends_exactly_at_its_length_and_retry_after_counts_down_to_it()
    {
        var collection = await NewSubject();
        var device = new Client(_factory, "203.0.113.13");
        for (var i = 0; i < 3; i++)
        {
            await device.Post(collection, "Up");
        }

        using (var first = await device.Post(collection, "Up"))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, first.StatusCode);
            Assert.Equal("600", RetryAfter(first));
        }

        // 499.5 s remain: Retry-After rounds up, so a client told to wait never retries early.
        _factory.Clock.Advance(TimeSpan.FromMilliseconds(100_500));
        using (var later = await device.Post(collection, "Up"))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, later.StatusCode);
            Assert.Equal("500", RetryAfter(later));
        }

        _factory.Clock.Advance(TimeSpan.FromMilliseconds(498_500));
        using (var last = await device.Post(collection, "Up"))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);
            Assert.Equal("1", RetryAfter(last));
        }

        _factory.Clock.Advance(TimeSpan.FromSeconds(1));
        using var reopened = await device.Post(collection, "Up");
        Assert.Equal(HttpStatusCode.Redirect, reopened.StatusCode);
    }

    [Fact]
    public async Task Forged_forwarded_header_from_non_loopback_peer_does_not_move_the_bucket()
    {
        var collection = await NewSubject();
        var device = new Client(_factory, "198.51.100.9") { Peer = "198.51.100.9" };
        for (var i = 0; i < 3; i++)
        {
            using var ok = await device.Post(collection, "Up", forwardedFor: $"192.0.2.{i + 1}");
            Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        }

        using var denied = await device.Post(collection, "Up", forwardedFor: "192.0.2.200");
        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
    }

    [Fact]
    public async Task Signed_in_accounts_are_limited_per_account()
    {
        var collection = await NewSubject();
        var alice = new Client(_factory, "203.0.113.20") { Account = ("7001", "alice-limits") };
        var aliceElsewhere = new Client(_factory, "203.0.113.21") { Account = ("7001", "alice-limits") };
        var bob = new Client(_factory, "203.0.113.20") { Account = ("7002", "bob-limits") };
        var visitor = new Client(_factory, "203.0.113.20");

        await alice.Post(collection, "Up");
        await alice.Post(collection, "Down");
        await aliceElsewhere.Post(collection, "Up");
        using var denied = await aliceElsewhere.Post(collection, "Down");
        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);

        var aliceAgain = new Client(_factory, "203.0.113.22") { Account = ("7001", "alice-limits") };
        using var sameAccountNewPrefix = await aliceAgain.Post(collection, "Down");
        Assert.Equal(HttpStatusCode.TooManyRequests, sameAccountNewPrefix.StatusCode);

        using var bobOk = await bob.Post(collection, "Up");
        Assert.Equal(HttpStatusCode.Redirect, bobOk.StatusCode);
        using var visitorOk = await visitor.Post(collection, "Up");
        Assert.Equal(HttpStatusCode.Redirect, visitorOk.StatusCode);
    }

    [Fact]
    public async Task Retract_by_a_signed_in_account_spends_the_account_budget_from_any_prefix()
    {
        var collection = await NewSubject();
        var home = new Client(_factory, "203.0.113.60") { Account = ("7003", "carol-limits") };
        var away = new Client(_factory, "203.0.113.61") { Account = ("7003", "carol-limits") };
        var elsewhere = new Client(_factory, "203.0.113.62") { Account = ("7003", "carol-limits") };
        await home.Post(collection, "Up");
        await home.Post(collection, "Down");
        using var third = await away.Post(collection, "Retract");
        Assert.Equal(HttpStatusCode.Redirect, third.StatusCode);

        using var denied = await elsewhere.Post(collection, "Retract");

        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
    }

    [Fact]
    public async Task Mint_budget_is_spent_only_by_minting_casts()
    {
        var collection = await NewSubject();
        const string prefix = "203.0.113.30";
        var first = new Client(_factory, prefix);
        using (var mint = await first.Post(collection, "Up"))
        {
            Assert.NotNull(SetCookie(mint));
        }

        // Recasts by the existing device spend cast permits only; they would exhaust a budget of 2 if they spent mint.
        using (var recast = await first.Post(collection, "Down"))
        {
            Assert.Equal(HttpStatusCode.Redirect, recast.StatusCode);
            Assert.Null(SetCookie(recast));
        }

        using (var recastAgain = await first.Post(collection, "Up"))
        {
            Assert.Equal(HttpStatusCode.Redirect, recastAgain.StatusCode);
        }

        _factory.Clock.Advance(TimeSpan.FromMinutes(10));
        var second = new Client(_factory, prefix);
        using (var secondMint = await second.Post(collection, "Up"))
        {
            Assert.Equal(HttpStatusCode.Redirect, secondMint.StatusCode);
            Assert.NotNull(SetCookie(secondMint));
        }

        var rowsBefore = (await Rows(collection)).Count;
        var third = new Client(_factory, prefix);
        using var denied = await third.Post(collection, "Up");
        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
        Assert.Null(SetCookie(denied));
        Assert.Equal(rowsBefore, (await Rows(collection)).Count);
        Assert.StartsWith("text/plain", denied.Content.Headers.ContentType?.MediaType);
        var refusal = await denied.Content.ReadAsStringAsync();
        Assert.Contains("sign in", refusal, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("203.0.113", refusal);

        // An hour after the first mint the budget is whole again.
        _factory.Clock.Advance(TimeSpan.FromMinutes(50));
        using var reopened = await third.Post(collection, "Up");
        Assert.Equal(HttpStatusCode.Redirect, reopened.StatusCode);
    }

    [Fact]
    public async Task Anonymous_cast_under_the_limit_succeeds()
    {
        var collection = await NewSubject();
        var visitor = new Client(_factory, "203.0.113.40");

        using var response = await visitor.Post(collection, "Up");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var row = Assert.Single(await Rows(collection));
        Assert.Equal(VoterClass.Anonymous, row.VoterClass);
        Assert.Equal(ConceptStance.Up, row.Stance);
    }

    [Fact]
    public async Task Rejection_writes_nothing_says_when_to_retry_and_echoes_no_address()
    {
        var logs = new CapturingLoggerProvider();
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddLogging(l => l.AddProvider(logs))));
        var collection = await NewSubject();
        var visitor = new Client(factory, "203.0.113.77");
        for (var i = 0; i < 3; i++)
        {
            await visitor.Post(collection, i == 1 ? "Down" : "Up");
        }

        var before = Assert.Single(await Rows(collection));
        using var denied = await visitor.Post(collection, "Down");

        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
        Assert.NotNull(RetryAfter(denied));
        Assert.Null(SetCookie(denied));
        var after = Assert.Single(await Rows(collection));
        Assert.Equal(before.Stance, after.Stance);
        var body = await denied.Content.ReadAsStringAsync();
        Assert.Contains("too fast", body);
        var seen = body
            + string.Join("\n", denied.Headers.Select(h => h.Key + ":" + string.Join(",", h.Value)));
        Assert.DoesNotContain("203.0.113", seen);
        Assert.DoesNotContain(logs.Lines, line => line.Contains("203.0.113", StringComparison.Ordinal));
    }

    [Fact]
    public void Defaults_allow_thirty_new_devices_an_hour_per_address()
    {
        var defaults = new Bifrost.Web.Configuration.ConceptVoteOptions();

        Assert.Equal(30, defaults.MintPermits);
        Assert.Equal(TimeSpan.FromHours(1), defaults.MintWindow);
        Assert.Equal(100_000, defaults.MaxTrackedKeys);
    }

    [Fact]
    public void Every_table_holds_at_most_the_configured_keys_and_accounts_have_a_table_of_their_own()
    {
        var limits = new Bifrost.Web.Features.ConceptVotes.ConceptVoteRateLimits(
            Microsoft.Extensions.Options.Options.Create(
                new Bifrost.Web.Configuration.ConceptVoteOptions { MaxTrackedKeys = 1, CastPermits = 1, MintPermits = 1 }),
            new TestClock());
        var account = new VoterIdentity("account:test", VoterClass.Account, Guid.NewGuid());

        Assert.Null(limits.DenyCast(IPAddress.Parse("198.51.100.1"), null));
        Assert.True(limits.DenyCast(IPAddress.Parse("198.51.100.2"), null)!.Value.TableFull);
        Assert.Null(limits.DenyCast(IPAddress.Parse("198.51.100.2"), account));
        var otherAccount = new VoterIdentity("account:other", VoterClass.Account, Guid.NewGuid());
        Assert.True(limits.DenyCast(IPAddress.Parse("198.51.100.2"), otherAccount)!.Value.TableFull);
        Assert.Null(limits.DenyMint(IPAddress.Parse("198.51.100.1")));
        Assert.True(limits.DenyMint(IPAddress.Parse("198.51.100.2"))!.Value.TableFull);
    }

    [Fact]
    public async Task Filling_the_table_with_unspent_keys_locks_out_neither_a_fresh_visitor_nor_a_fresh_account()
    {
        var factory = Capped(4);
        var collection = await NewSubject();
        for (var i = 0; i < 4; i++)
        {
            using var filler = await new Client(factory, $"203.0.113.{80 + i}").Post(collection, "Up");
            Assert.Equal(HttpStatusCode.Redirect, filler.StatusCode);
        }

        using var visitor = await new Client(factory, "203.0.113.90").Post(collection, "Up");
        using var account = await new Client(factory, "203.0.113.91") { Account = ("7101", "fresh-account") }
            .Post(collection, "Up");

        Assert.Equal(HttpStatusCode.Redirect, visitor.StatusCode);
        Assert.NotNull(SetCookie(visitor));
        Assert.Equal(HttpStatusCode.Redirect, account.StatusCode);
    }

    [Fact]
    public async Task A_spent_key_survives_eviction_pressure_and_stays_refused_while_rotating_callers_succeed()
    {
        var factory = Capped(4);
        var collection = await NewSubject();
        var spender = new Client(factory, "203.0.113.100");
        for (var i = 0; i < 3; i++)
        {
            using var ok = await spender.Post(collection, "Up");
            Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        }

        for (var i = 0; i < 12; i++)
        {
            using var rotating = await new Client(factory, $"203.0.114.{i + 1}").Post(collection, "Up");
            Assert.Equal(HttpStatusCode.Redirect, rotating.StatusCode);
        }

        using var denied = await spender.Post(collection, "Up");

        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
        Assert.Contains("too fast", await denied.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_rotating_caller_wins_back_only_a_forgotten_keys_unspent_budget()
    {
        var factory = Capped(2);
        var collection = await NewSubject();
        var forgotten = new Client(factory, "203.0.113.110");
        await forgotten.Post(collection, "Up");
        await forgotten.Post(collection, "Up");
        var recent = new Client(factory, "203.0.113.111");
        await recent.Post(collection, "Up");
        await new Client(factory, "203.0.113.112").Post(collection, "Up");

        // forgotten was least recently used and had one permit left: it was evicted, so it has all three again, no more.
        for (var i = 0; i < 3; i++)
        {
            using var ok = await forgotten.Post(collection, "Up");
            Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        }

        using var denied = await forgotten.Post(collection, "Up");
        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
    }

    [Fact]
    public async Task A_table_full_of_spent_keys_refuses_a_new_visitor_with_its_true_reason_and_leaves_accounts_alone()
    {
        var factory = Capped(4);
        var collection = await NewSubject();
        for (var i = 0; i < 4; i++)
        {
            var spender = new Client(factory, $"203.0.113.{120 + i}");
            for (var cast = 0; cast < 3; cast++)
            {
                using var ok = await spender.Post(collection, "Up");
                Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
            }
        }

        var rowsBefore = (await Rows(collection)).Count;
        using var visitor = await new Client(factory, "203.0.113.130").Post(collection, "Up");
        using var account = await new Client(factory, "203.0.113.131") { Account = ("7102", "spared-account") }
            .Post(collection, "Up");

        Assert.Equal(HttpStatusCode.TooManyRequests, visitor.StatusCode);
        Assert.NotNull(RetryAfter(visitor));
        Assert.Null(SetCookie(visitor));
        var body = await visitor.Content.ReadAsStringAsync();
        Assert.Contains("busy", body);
        Assert.DoesNotContain("too fast", body);
        Assert.Contains("sign in", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("203.0.113", body + string.Join(";", visitor.Headers.Select(h => string.Join(",", h.Value))));
        Assert.Equal(HttpStatusCode.Redirect, account.StatusCode);
        Assert.Equal(rowsBefore + 1, (await Rows(collection)).Count);
    }

    [Fact]
    public async Task A_full_account_table_refuses_a_new_account_as_busy_without_offering_sign_in()
    {
        var factory = Capped(4);
        var collection = await NewSubject();
        for (var i = 0; i < 4; i++)
        {
            var spender = new Client(factory, $"203.0.113.{150 + i}") { Account = ($"72{i:00}", $"spent-account-{i}") };
            for (var cast = 0; cast < 3; cast++)
            {
                using var ok = await spender.Post(collection, "Up");
                Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
            }
        }

        using var fresh = await new Client(factory, "203.0.113.160") { Account = ("7299", "fresh-signed-in") }
            .Post(collection, "Up");

        Assert.Equal(HttpStatusCode.TooManyRequests, fresh.StatusCode);
        var body = await fresh.Content.ReadAsStringAsync();
        Assert.Contains("busy", body);
        Assert.DoesNotContain("sign in", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Posts_to_a_missing_subject_spend_no_budget_and_mint_no_device()
    {
        var collection = await NewSubject();
        var sender = new Client(_factory, "203.0.113.140");
        foreach (var handler in new[] { "Up", "Down", "Retract" })
        {
            for (var i = 0; i < 4; i++)
            {
                using var missing = await sender.Post("c" + Guid.NewGuid().ToString("N"), handler);
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
                Assert.Null(SetCookie(missing));
            }
        }

        for (var i = 0; i < 3; i++)
        {
            using var ok = await sender.Post(collection, "Up");
            Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        }

        using var denied = await sender.Post(collection, "Up");
        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
    }

    [Fact]
    public void A_request_with_no_client_address_shares_one_bucket_apart_from_every_address()
    {
        var limits = new Bifrost.Web.Features.ConceptVotes.ConceptVoteRateLimits(
            Microsoft.Extensions.Options.Options.Create(new Bifrost.Web.Configuration.ConceptVoteOptions { CastPermits = 1 }),
            new TestClock());

        Assert.Null(limits.DenyCast(null, null));
        Assert.NotNull(limits.DenyCast(null, null));
        Assert.Null(limits.DenyCast(IPAddress.Parse("198.51.100.1"), null));
    }

    [Fact]
    public async Task Reads_are_not_under_the_vote_budgets()
    {
        var collection = await NewSubject();
        var visitor = new Client(_factory, "203.0.113.50");
        for (var i = 0; i < 4; i++)
        {
            await visitor.Post(collection, "Up");
        }

        using var page = await visitor.Get($"/mill/{collection}/alpha");
        using var export = await visitor.Get($"/public/concepts/{collection}/tallies");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
    }

    private WebApplicationFactory<Program> Capped(int maxTrackedKeys) =>
        _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConceptVotes:MaxTrackedKeys"] = maxTrackedKeys.ToString(System.Globalization.CultureInfo.InvariantCulture)
            })));

    private static string? RetryAfter(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Retry-After", out var values) ? values.Single() : null;

    private static string? SetCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(x => x.StartsWith(CookieName + "=", StringComparison.Ordinal))
            : null;

    private async Task<string> NewSubject()
    {
        var collection = "c" + Guid.NewGuid().ToString("N");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        db.ConceptSubjects.Add(new ConceptSubject
        {
            Collection = collection,
            Key = "alpha",
            Title = "alpha",
            RegisteredAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return collection;
    }

    private async Task<List<ConceptAppraisal>> Rows(string collection)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        return await db.ConceptAppraisals.AsNoTracking()
            .Where(x => x.Subject!.Collection == collection)
            .ToListAsync();
    }

    // One browser: a client address that reaches the app through the proxy, a cookie jar, optionally an account.
    private sealed class Client(WebApplicationFactory<Program> factory, string address)
    {
        private readonly HttpClient _http = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri(Site)
        });

        private string? _cookie;

        public string Peer { get; init; } = "127.0.0.1";

        public (string Id, string Login)? Account { get; init; }

        public Task<HttpResponseMessage> Post(string collection, string handler, string? forwardedFor = null) =>
            Send(
                new HttpRequestMessage(HttpMethod.Post, $"/mill/{collection}/alpha?handler={handler}")
                {
                    Content = new FormUrlEncodedContent([])
                },
                forwardedFor);

        public Task<HttpResponseMessage> Get(string path) => Send(new HttpRequestMessage(HttpMethod.Get, path), null);

        private async Task<HttpResponseMessage> Send(HttpRequestMessage request, string? forwardedFor)
        {
            request.Headers.TryAddWithoutValidation("Origin", Site);
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor ?? address);
            request.Headers.TryAddWithoutValidation("X-Test-Peer", Peer);
            if (Account is { } account)
            {
                request.Headers.TryAddWithoutValidation("X-Test-GitHub-Id", account.Id);
                request.Headers.TryAddWithoutValidation("X-Test-GitHub-Login", account.Login);
            }
            else
            {
                request.Headers.TryAddWithoutValidation("X-Test-Anonymous", "true");
            }

            if (_cookie is not null)
            {
                request.Headers.TryAddWithoutValidation("Cookie", _cookie);
            }

            var response = await _http.SendAsync(request);
            if (SetCookie(response) is { } set)
            {
                _cookie = set.Split(';')[0];
            }

            return response;
        }
    }

    public sealed class LimitedFactory : TestWebApplicationFactory
    {
        public TestClock Clock { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConceptVotes:CastPermits"] = "3",
                    ["ConceptVotes:CastWindow"] = "00:10:00",
                    ["ConceptVotes:MintPermits"] = "2",
                    ["ConceptVotes:MintWindow"] = "01:00:00",
                    ["ConceptVotes:NetworkTagSecret"] = "rate-limit-test-tag-secret-0123456789"
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
                services.AddTransient<IStartupFilter, PeerFilter>();
            });
        }
    }

    public sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        private long _ticks = 5_000_000;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _now;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by)
        {
            _now += by;
            _ticks += by.Ticks;
        }
    }

    // The proxy's connection: TestServer has no socket, so the peer address is set ahead of the app pipeline.
    private sealed class PeerFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress =
                    IPAddress.Parse(context.Request.Headers["X-Test-Peer"].FirstOrDefault() ?? "127.0.0.1");
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
