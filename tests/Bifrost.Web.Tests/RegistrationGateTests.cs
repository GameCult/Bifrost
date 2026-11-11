using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bifrost.Web.Configuration;
using Bifrost.Web.Data;
using Bifrost.Web.Features.Registration;
using Bifrost.Web.Features.Shared;
using Bifrost.Web.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Tests;

public sealed class RegistrationGateFactory : TestWebApplicationFactory
{
    public ManualClock Time { get; } = new();

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
        });
    }
}

// The anonymous register is gated by consent and a proof of work; every refusal must create nothing.
public sealed class RegistrationGateTests(RegistrationGateFactory factory) : IClassFixture<RegistrationGateFactory>
{
    private static string NewIdentity() => $"gate-{Guid.NewGuid():N}";

    private HttpClient Client() => factory.CreateClient();

    private (int Accounts, int Receipts) Counts()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        return (db.UserAccounts.Count(), db.ConsentReceipts.Count());
    }

    private async Task<(string Challenge, string Nonce)> SolvedAsync(HttpClient client) =>
        await RegistrationKit.SolveAsync(client);

    private async Task AssertRefusedCreatesNothingAsync(HttpClient client, object body, string? expectedFragment = null)
    {
        var before = Counts();
        using var response = await RegistrationKit.PostAsync(client, body, anonymous: true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        if (expectedFragment is not null)
        {
            Assert.Contains(expectedFragment, await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(before, Counts());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_consent_registers_the_account_with_no_receipt(bool explicitNull)
    {
        using var client = Client();
        var (challenge, nonce) = await SolvedAsync(client);
        var identity = NewIdentity();
        object body = explicitNull
            ? new { identity, challenge, nonce, acceptedNoticeVersion = (int?)null }
            : new { identity, challenge, nonce };

        using var response = await RegistrationKit.PostAsync(client, body, anonymous: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var account = db.UserAccounts.Single(x => x.NormalizedBifrostIdentity == identity);
        Assert.Empty(db.ConsentReceipts.Where(x => x.UserAccountId == account.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task A_wrong_notice_version_is_refused_and_creates_nothing(int version)
    {
        using var client = Client();
        var (challenge, nonce) = await SolvedAsync(client);

        await AssertRefusedCreatesNothingAsync(
            client,
            new { identity = NewIdentity(), challenge, nonce, acceptedNoticeVersion = version });
    }

    [Fact]
    public async Task A_missing_challenge_is_refused_and_creates_nothing()
    {
        using var client = Client();

        await AssertRefusedCreatesNothingAsync(
            client,
            new { identity = NewIdentity(), acceptedNoticeVersion = TrainingNotice.Version });
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    public async Task A_garbage_challenge_is_refused_and_creates_nothing(string challenge)
    {
        using var client = Client();

        await AssertRefusedCreatesNothingAsync(
            client,
            new { identity = NewIdentity(), challenge, nonce = "1", acceptedNoticeVersion = TrainingNotice.Version });
    }

    [Fact]
    public async Task A_tampered_challenge_is_refused_and_creates_nothing()
    {
        using var client = Client();
        var (challenge, _) = await SolvedAsync(client);
        var tampered = challenge[..^2] + (challenge[^2] == 'A' ? 'B' : 'A') + challenge[^1];
        var nonce = RegistrationKit.Solve(tampered, 4);

        await AssertRefusedCreatesNothingAsync(
            client,
            new { identity = NewIdentity(), challenge = tampered, nonce, acceptedNoticeVersion = TrainingNotice.Version });
    }

    [Fact]
    public async Task A_wrong_nonce_is_refused_and_creates_nothing()
    {
        using var client = Client();
        var issued = await client.GetFromJsonAsync<JsonElement>("/auth/bifrost/challenge");
        var challenge = issued.GetProperty("challenge").GetString()!;
        var difficulty = issued.GetProperty("difficulty").GetInt32();
        var correct = RegistrationKit.Solve(challenge, difficulty);
        // The next nonce that does not meet the difficulty.
        var wrong = Enumerable.Range(0, int.MaxValue)
            .Select(i => i.ToString())
            .First(n => n != correct && RegistrationChallengeService.LeadingZeroBits(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(challenge + ":" + n))) < difficulty);

        await AssertRefusedCreatesNothingAsync(
            client,
            new { identity = NewIdentity(), challenge, nonce = wrong, acceptedNoticeVersion = TrainingNotice.Version });
    }

    // Each malformed shape is padded until it meets the difficulty, so only the nonce-format guard can refuse it.
    [Theory]
    [InlineData("x", "")]
    [InlineData("-", "")]
    [InlineData("", "e")]
    [InlineData("000000000000000000000", "")]
    public async Task A_malformed_nonce_is_refused_even_when_its_hash_would_pass(string prefix, string suffix)
    {
        using var client = Client();
        var (challenge, _) = await SolvedAsync(client);
        var nonce = Enumerable.Range(0, int.MaxValue)
            .Select(i => prefix + i + suffix)
            .First(n => RegistrationChallengeService.LeadingZeroBits(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(challenge + ":" + n))) >= 4);

        await AssertRefusedCreatesNothingAsync(
            client,
            new { identity = NewIdentity(), challenge, nonce, acceptedNoticeVersion = TrainingNotice.Version });
    }

    [Fact]
    public async Task An_expired_challenge_is_refused_and_creates_nothing()
    {
        using var client = Client();
        var (challenge, nonce) = await SolvedAsync(client);
        factory.Time.Advance(RegistrationChallengeService.Lifetime + TimeSpan.FromSeconds(1));

        await AssertRefusedCreatesNothingAsync(
            client,
            new { identity = NewIdentity(), challenge, nonce, acceptedNoticeVersion = TrainingNotice.Version });
    }

    [Fact]
    public async Task A_consumed_challenge_cannot_be_replayed()
    {
        using var client = Client();
        var (challenge, nonce) = await SolvedAsync(client);
        var first = NewIdentity();
        var body = (string identity) => new { identity, challenge, nonce, acceptedNoticeVersion = TrainingNotice.Version };

        using var accepted = await RegistrationKit.PostAsync(client, body(first), anonymous: true);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var before = Counts();

        await AssertRefusedCreatesNothingAsync(client, body(NewIdentity()));
        Assert.Equal(before, Counts());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        Assert.Single(db.UserAccounts.Where(x => x.NormalizedBifrostIdentity == first));
    }

    [Fact]
    public async Task A_nonce_solved_for_one_challenge_does_not_pass_another()
    {
        using var client = Client();
        var (_, nonceForFirst) = await SolvedAsync(client);
        var issued = await client.GetFromJsonAsync<JsonElement>("/auth/bifrost/challenge");
        var second = issued.GetProperty("challenge").GetString()!;
        var difficulty = issued.GetProperty("difficulty").GetInt32();
        // Skip the rare case where the nonce happens to also solve the second challenge.
        if (RegistrationChallengeService.LeadingZeroBits(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(second + ":" + nonceForFirst))) >= difficulty)
        {
            return;
        }

        await AssertRefusedCreatesNothingAsync(
            client,
            new { identity = NewIdentity(), challenge = second, nonce = nonceForFirst, acceptedNoticeVersion = TrainingNotice.Version });
    }

    [Fact]
    public async Task Success_stores_the_receipt_with_the_account()
    {
        using var client = Client();
        var identity = NewIdentity();

        using var response = await RegistrationKit.RegisterAsync(client, identity, anonymous: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var account = db.UserAccounts.Single(x => x.NormalizedBifrostIdentity == identity);
        var receipt = db.ConsentReceipts.Single(x => x.UserAccountId == account.Id);
        Assert.Equal(2, receipt.NoticeVersion);
        Assert.Equal(TrainingNotice.Version, receipt.NoticeVersion);
        Assert.Null(receipt.WithdrawnAtUtc);
        Assert.InRange((factory.Time.GetUtcNow() - receipt.AcceptedAtUtc).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task The_notice_endpoint_names_the_version_and_the_discord_exclusion()
    {
        using var client = Client();

        var notice = await client.GetFromJsonAsync<JsonElement>("/auth/bifrost/notice");

        Assert.Equal(2, notice.GetProperty("version").GetInt32());
        Assert.Contains("Discord", notice.GetProperty("text").GetString());
    }

    [Fact]
    public async Task The_challenge_response_is_not_cached()
    {
        using var client = Client();

        using var response = await client.GetAsync("/auth/bifrost/challenge");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(18, true)]
    [InlineData(24, true)]
    [InlineData(25, false)]
    public void Difficulty_outside_one_to_twenty_four_is_refused_at_startup(int difficulty, bool valid)
    {
        var options = new RegistrationOptions { Captcha = new RegistrationCaptchaOptions { Difficulty = difficulty } };
        Action validate = () => new StartupConfigurationValidator(
            new ConfigurationBuilder().Build(),
            new TestingEnvironment(),
            Options.Create(new GitHubOAuthOptions()),
            Options.Create(new GitHubAppOptions()),
            Options.Create(new HeimdallOptions()),
            Options.Create(new StripeOptions()),
            Options.Create(new BifrostHostOptions()),
            Options.Create(new ConceptVoteOptions()),
            Options.Create(new DeskOptions()),
            Options.Create(options)).Validate();

        if (valid)
        {
            validate();
        }
        else
        {
            Assert.Contains("Difficulty", Assert.Throws<InvalidOperationException>(validate).Message);
        }

        Assert.Equal(18, new RegistrationOptions().Captcha.Difficulty);
    }

    private sealed class TestingEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Bifrost.Web";
        public string ContentRootPath { get; set; } = string.Empty;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
