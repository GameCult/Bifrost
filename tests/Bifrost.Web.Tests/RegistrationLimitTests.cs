using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bifrost.Web.Configuration;
using Bifrost.Web.Data;
using Bifrost.Web.Features.Registration;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Tests;

// The challenge service on its own: single use under contention, the bounded set, the purpose, and the difficulty it issued.
public sealed class RegistrationChallengeServiceTests
{
    private sealed class MutableOptions(int difficulty) : IOptions<RegistrationOptions>
    {
        public RegistrationOptions Value { get; set; } = With(difficulty);
        public static RegistrationOptions With(int d) => new() { Captcha = new RegistrationCaptchaOptions { Difficulty = d } };
    }

    private static RegistrationChallengeService NewService(IDataProtectionProvider provider, IOptions<RegistrationOptions> options, TimeProvider clock) =>
        new(provider, options, clock);

    [Fact]
    public async Task Concurrent_consumes_of_one_challenge_yield_exactly_one_success()
    {
        var service = NewService(new EphemeralDataProtectionProvider(), new MutableOptions(1), new ManualClock());
        for (var round = 0; round < 300; round++)
        {
            var challenge = service.Issue()!;
            var nonce = RegistrationKit.Solve(challenge.Challenge, challenge.Difficulty);
            using var start = new ManualResetEventSlim();

            var tasks = Enumerable.Range(0, 16).Select(_ => Task.Factory.StartNew(
                () => { start.Wait(); return service.TryConsume(challenge.Challenge, nonce); },
                TaskCreationOptions.LongRunning)).ToArray();
            start.Set();
            var results = await Task.WhenAll(tasks);

            Assert.Equal(1, results.Count(x => x));
        }
    }

    [Fact]
    public void A_full_consumed_set_refuses_to_issue_and_to_consume_until_entries_expire()
    {
        var clock = new ManualClock();
        var service = NewService(new EphemeralDataProtectionProvider(), new MutableOptions(1), clock);
        var pending = new List<(string, string)>();
        for (var i = 0; i < RegistrationChallengeService.MaxConsumed + 1; i++)
        {
            var c = service.Issue()!;
            pending.Add((c.Challenge, RegistrationKit.Solve(c.Challenge, c.Difficulty)));
        }

        foreach (var (c, n) in pending.Take(RegistrationChallengeService.MaxConsumed))
        {
            Assert.True(service.TryConsume(c, n));
        }

        Assert.Null(service.Issue());
        Assert.False(service.TryConsume(pending[^1].Item1, pending[^1].Item2));

        clock.Advance(RegistrationChallengeService.Lifetime + TimeSpan.FromSeconds(1));
        Assert.NotNull(service.Issue());
    }

    [Fact]
    public void A_token_protected_for_another_purpose_is_refused()
    {
        var provider = new EphemeralDataProtectionProvider();
        var clock = new ManualClock();
        var service = NewService(provider, new MutableOptions(1), clock);
        var expiry = (clock.GetUtcNow() + TimeSpan.FromMinutes(1)).ToUnixTimeMilliseconds();
        var forged = provider.CreateProtector("Bifrost.Something.Else.v1").Protect($"{Guid.NewGuid():N}.{expiry}.1");
        var nonce = RegistrationKit.Solve(forged, 1);

        Assert.False(service.TryConsume(forged, nonce));
    }

    [Fact]
    public void A_challenge_verifies_at_the_difficulty_it_was_issued_with()
    {
        var options = new MutableOptions(1);
        var service = NewService(new EphemeralDataProtectionProvider(), options, new ManualClock());
        var easy = service.Issue()!;
        var easyNonce = RegistrationKit.Solve(easy.Challenge, 1);
        // A nonce that meets 1 bit but not 12: only the embedded difficulty lets it through after the option rises.
        var weak = Enumerable.Range(0, int.MaxValue).Select(i => i.ToString()).First(n =>
        {
            var bits = RegistrationChallengeService.LeadingZeroBits(SHA256.HashData(Encoding.UTF8.GetBytes(easy.Challenge + ":" + n)));
            return bits >= 1 && bits < 12;
        });

        options.Value = MutableOptions.With(12);

        Assert.Equal(1, easy.Difficulty);
        Assert.True(service.TryConsume(easy.Challenge, weak));
        Assert.Equal(12, service.Issue()!.Difficulty);
        _ = easyNonce;
    }
}

public sealed class RegistrationBudgetFactory : TestWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bifrost:Registration:Limits:PerAddressPermits"] = "3"
            }));
        builder.ConfigureServices(services => services.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, PeerFilter>());
    }

    private sealed class PeerFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers["X-Test-Peer"].FirstOrDefault() ?? "198.51.100.1");
                return nextMiddleware();
            });
            next(app);
        };
    }
}

public sealed class RegistrationBudgetTests(RegistrationBudgetFactory factory) : IClassFixture<RegistrationBudgetFactory>
{
    private static async Task<HttpResponseMessage> ChallengeAsync(HttpClient client, string peer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/bifrost/challenge");
        request.Headers.Add("X-Test-Peer", peer);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task The_request_past_the_address_budget_gets_429_and_another_address_is_unaffected()
    {
        using var client = factory.CreateClient();
        for (var i = 0; i < 3; i++)
        {
            using var ok = await ChallengeAsync(client, "203.0.113.5");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var refused = await ChallengeAsync(client, "203.0.113.5");
        using var other = await ChallengeAsync(client, "203.0.113.6");

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.True(refused.Headers.RetryAfter is not null);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task An_IPv6_caller_owns_its_slash_64_and_a_mapped_IPv4_shares_its_IPv4_key()
    {
        using var client = factory.CreateClient();
        string[] sameSlash64 = ["2001:db8:1:2::1", "2001:db8:1:2::2", "2001:db8:1:2:aaaa::3", "2001:db8:1:2:ffff:ffff:ffff:ffff", "2001:db8:1:2::5"];
        var statuses = new List<HttpStatusCode>();
        foreach (var peer in sameSlash64)
        {
            using var response = await ChallengeAsync(client, peer);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests],
            statuses);
        using var otherSlash64 = await ChallengeAsync(client, "2001:db8:1:3::1");
        Assert.Equal(HttpStatusCode.OK, otherSlash64.StatusCode);

        for (var i = 0; i < 3; i++)
        {
            using var ok = await ChallengeAsync(client, "203.0.113.50");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var mapped = await ChallengeAsync(client, "::ffff:203.0.113.50");
        Assert.Equal(HttpStatusCode.TooManyRequests, mapped.StatusCode);
    }

    [Fact]
    public async Task Challenge_and_register_spend_one_budget_and_a_refused_register_creates_nothing()
    {
        using var client = factory.CreateClient();
        const string peer = "203.0.113.7";
        for (var i = 0; i < 3; i++)
        {
            using var ok = await ChallengeAsync(client, peer);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/bifrost/register")
        {
            Content = JsonContent.Create(new { identity = $"budget-{Guid.NewGuid():N}", acceptedNoticeVersion = TrainingNotice.Version })
        };
        request.Headers.Add("X-Test-Anonymous", "true");
        request.Headers.Add("X-Test-Peer", peer);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.DoesNotContain(scope.ServiceProvider.GetRequiredService<BifrostDbContext>().UserAccounts, x => x.NormalizedBifrostIdentity!.StartsWith("budget-"));
    }
}

public sealed class RegistrationFullSetTests(RegistrationGateFactory factory) : IClassFixture<RegistrationGateFactory>
{
    [Fact]
    public async Task A_full_consumed_set_makes_the_challenge_endpoint_answer_503()
    {
        var service = factory.Services.GetRequiredService<RegistrationChallengeService>();
        for (var i = 0; i < RegistrationChallengeService.MaxConsumed; i++)
        {
            var c = service.Issue()!;
            Assert.True(service.TryConsume(c.Challenge, RegistrationKit.Solve(c.Challenge, c.Difficulty)));
        }

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/auth/bifrost/challenge");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        factory.Time.Advance(RegistrationChallengeService.Lifetime + TimeSpan.FromSeconds(1));
        using var after = await client.GetAsync("/auth/bifrost/challenge");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }
}
