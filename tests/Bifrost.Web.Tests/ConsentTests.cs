using System.Net;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Registration;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.Web.Tests;

// The consent page and its posts, through HTTP, against receipt rows in the database.
public sealed class ConsentTests(RegistrationGateFactory factory) : IClassFixture<RegistrationGateFactory>
{
    private const string Origin = "https://localhost";
    private static readonly DateTimeOffset Agreed = new(2026, 11, 1, 12, 30, 5, TimeSpan.Zero);
    private static readonly DateTimeOffset Withdrew = new(2026, 11, 2, 8, 0, 9, TimeSpan.Zero);

    private static string NewIdentity() => $"consent-{Guid.NewGuid():N}";

    private HttpClient Client() => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private void Seed(string identity, params (int Version, DateTimeOffset Accepted, DateTimeOffset? Withdrawn)[] receipts)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var account = new UserAccount { BifrostIdentity = identity, NormalizedBifrostIdentity = identity, DisplayName = identity };
        db.UserAccounts.Add(account);
        foreach (var (version, accepted, withdrawn) in receipts)
        {
            db.ConsentReceipts.Add(new ConsentReceipt { UserAccount = account, NoticeVersion = version, AcceptedAtUtc = accepted, WithdrawnAtUtc = withdrawn });
        }

        db.SaveChanges();
    }

    private List<ConsentReceipt> Receipts(string identity)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<BifrostDbContext>().ConsentReceipts.AsNoTracking()
            .Where(x => x.UserAccount!.NormalizedBifrostIdentity == identity).OrderBy(x => x.AcceptedAtUtc).ToList();
    }

    private async Task<HttpResponseMessage> GetPageAsync(HttpClient client, string? identity)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/bifrost/consent");
        Sign(request, identity);
        return await client.SendAsync(request);
    }

    private static void Sign(HttpRequestMessage request, string? identity)
    {
        if (identity is null)
        {
            request.Headers.Add("X-Test-Anonymous", "true");
        }
        else
        {
            request.Headers.Add("X-Test-Bifrost-Identity", identity);
        }
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string action, string? identity, Dictionary<string, string>? fields = null, string? origin = Origin, string? fetchSite = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/bifrost/consent/" + action)
        {
            Content = new FormUrlEncodedContent(fields ?? new Dictionary<string, string> { ["version"] = TrainingNotice.Version.ToString() })
        };
        Sign(request, identity);
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        if (fetchSite is not null)
        {
            request.Headers.Add("Sec-Fetch-Site", fetchSite);
        }

        return await client.SendAsync(request);
    }

    private async Task<string> PageHtmlAsync(string identity)
    {
        using var client = Client();
        using var response = await GetPageAsync(client, identity);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task Anonymous_is_sent_to_the_sign_in_chooser_with_the_page_as_return_target()
    {
        using var client = Client();
        using var response = await GetPageAsync(client, null);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/auth/sign-in?returnUrl=%2Fauth%2Fbifrost%2Fconsent", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task The_page_shows_the_notice_in_force_and_is_plain_server_html()
    {
        var identity = NewIdentity();
        Seed(identity);
        using var client = Client();
        using var response = await GetPageAsync(client, identity);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        foreach (var paragraph in TrainingNotice.Text.Split("\n\n"))
        {
            Assert.Contains(WebUtility.HtmlEncode(paragraph), html);
        }

        Assert.Contains("<html lang=\"en\">", html);
        Assert.Contains("aria-live=\"polite\"", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotMatch("(src|href)=\"https?:", html);
    }

    [Fact]
    public async Task No_agreement_shows_that_state_and_only_the_agree_button()
    {
        var identity = NewIdentity();
        Seed(identity);
        var html = await PageHtmlAsync(identity);

        Assert.Contains("You have not agreed", html);
        Assert.Contains("action=\"/auth/bifrost/consent/accept\"", html);
        Assert.DoesNotContain("/consent/withdraw", html);
        Assert.Contains($"name=\"version\" value=\"{TrainingNotice.Version}\"", html);
    }

    [Fact]
    public async Task Agreement_to_the_current_version_shows_its_time_and_only_the_withdraw_button()
    {
        var identity = NewIdentity();
        Seed(identity, (TrainingNotice.Version, Agreed, null));
        var html = await PageHtmlAsync(identity);

        Assert.Contains($"You agreed to version {TrainingNotice.Version} on 2026-11-01T12:30:05Z", html);
        Assert.Contains("action=\"/auth/bifrost/consent/withdraw\"", html);
        Assert.DoesNotContain("/consent/accept", html);
    }

    [Fact]
    public async Task Agreement_to_an_older_version_says_it_no_longer_counts_and_offers_both_agree_and_withdraw()
    {
        var identity = NewIdentity();
        Seed(identity, (1, Agreed, null));
        var html = await PageHtmlAsync(identity);

        Assert.Contains($"You agreed to version 1 on 2026-11-01T12:30:05Z; version {TrainingNotice.Version} is in force and your messages are not used until you agree to it", html);
        Assert.Contains("action=\"/auth/bifrost/consent/accept\"", html);
        Assert.Contains("action=\"/auth/bifrost/consent/withdraw\"", html);
    }

    [Fact]
    public async Task A_live_receipt_for_the_version_in_force_outranks_an_older_live_one()
    {
        var identity = NewIdentity();
        Seed(identity, (1, Agreed, null), (TrainingNotice.Version, Agreed.AddDays(1), null));
        var html = await PageHtmlAsync(identity);

        Assert.Contains($"You agreed to version {TrainingNotice.Version} on 2026-11-02T12:30:05Z", html);
        Assert.Contains("action=\"/auth/bifrost/consent/withdraw\"", html);
    }

    [Fact]
    public async Task A_withdrawal_shows_its_time_and_offers_agree()
    {
        var identity = NewIdentity();
        Seed(identity, (TrainingNotice.Version, Agreed, Withdrew));
        var html = await PageHtmlAsync(identity);

        Assert.Contains("You withdrew on 2026-11-02T08:00:09Z", html);
        Assert.DoesNotContain("You agreed to version", html);
        Assert.Contains("action=\"/auth/bifrost/consent/accept\"", html);
        Assert.DoesNotContain("/consent/withdraw", html);
    }

    [Fact]
    public async Task Accept_creates_one_live_receipt_and_a_second_accept_adds_none()
    {
        var identity = NewIdentity();
        Seed(identity);
        using var client = Client();

        using var first = await PostAsync(client, "accept", identity);
        factory.Time.Advance(TimeSpan.FromHours(1));
        using var second = await PostAsync(client, "accept", identity);

        Assert.Equal(HttpStatusCode.SeeOther, first.StatusCode);
        Assert.Equal("/auth/bifrost/consent", first.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.SeeOther, second.StatusCode);
        var receipt = Assert.Single(Receipts(identity));
        Assert.Equal(TrainingNotice.Version, receipt.NoticeVersion);
        Assert.Null(receipt.WithdrawnAtUtc);
        Assert.Contains("You agreed to version", await PageHtmlAsync(identity));
    }

    [Fact]
    public async Task Withdraw_stamps_once_and_a_second_withdraw_changes_nothing()
    {
        var identity = NewIdentity();
        Seed(identity, (TrainingNotice.Version, Agreed, null));
        using var client = Client();

        using var first = await PostAsync(client, "withdraw", identity);
        var stamped = Assert.Single(Receipts(identity)).WithdrawnAtUtc;
        factory.Time.Advance(TimeSpan.FromHours(1));
        using var second = await PostAsync(client, "withdraw", identity);

        Assert.Equal(HttpStatusCode.SeeOther, first.StatusCode);
        Assert.Equal(HttpStatusCode.SeeOther, second.StatusCode);
        Assert.NotNull(stamped);
        Assert.Equal(stamped, Assert.Single(Receipts(identity)).WithdrawnAtUtc);
    }

    [Fact]
    public async Task Withdraw_stamps_every_live_receipt_including_an_older_version()
    {
        var identity = NewIdentity();
        Seed(identity, (1, Agreed, null), (TrainingNotice.Version, Agreed.AddDays(1), null));
        using var client = Client();

        using var response = await PostAsync(client, "withdraw", identity);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.All(Receipts(identity), x => Assert.NotNull(x.WithdrawnAtUtc));
    }

    [Fact]
    public async Task Withdraw_with_no_receipt_changes_nothing()
    {
        var identity = NewIdentity();
        Seed(identity);
        using var client = Client();

        using var response = await PostAsync(client, "withdraw", identity);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Empty(Receipts(identity));
    }

    [Fact]
    public async Task Accept_after_withdraw_makes_a_new_live_receipt_and_the_old_one_stays_withdrawn()
    {
        var identity = NewIdentity();
        Seed(identity, (TrainingNotice.Version, Agreed, Withdrew));
        using var client = Client();

        using var response = await PostAsync(client, "accept", identity);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        var receipts = Receipts(identity);
        Assert.Equal(2, receipts.Count);
        Assert.Single(receipts, x => x.WithdrawnAtUtc == Withdrew && x.AcceptedAtUtc == Agreed);
        Assert.Single(receipts, x => x.WithdrawnAtUtc is null && x.NoticeVersion == TrainingNotice.Version);
    }

    [Fact]
    public async Task Accept_next_to_an_older_live_receipt_adds_one_for_the_version_in_force()
    {
        var identity = NewIdentity();
        Seed(identity, (1, Agreed, null));
        using var client = Client();

        using var response = await PostAsync(client, "accept", identity);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal([1, TrainingNotice.Version], Receipts(identity).Select(x => x.NoticeVersion).Order().ToArray());
    }

    [Theory]
    [InlineData("1")]
    [InlineData("3")]
    [InlineData("abc")]
    [InlineData("")]
    public async Task Accept_with_the_wrong_version_is_refused_and_creates_nothing(string version)
    {
        var identity = NewIdentity();
        Seed(identity);
        using var client = Client();

        using var response = await PostAsync(client, "accept", identity, new Dictionary<string, string> { ["version"] = version });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("role=\"alert\"", await response.Content.ReadAsStringAsync());
        Assert.Empty(Receipts(identity));
    }

    [Fact]
    public async Task Accept_with_no_version_field_is_refused_and_creates_nothing()
    {
        var identity = NewIdentity();
        Seed(identity);
        using var client = Client();

        using var response = await PostAsync(client, "accept", identity, new Dictionary<string, string> { ["other"] = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(Receipts(identity));
    }

    [Theory]
    [InlineData("https://evil.example", null)]
    [InlineData(null, "cross-site")]
    [InlineData(null, "same-site")]
    [InlineData(null, null)]
    public async Task Accept_from_another_origin_or_with_no_fetch_metadata_is_forbidden_and_changes_nothing(string? origin, string? fetchSite)
    {
        var identity = NewIdentity();
        Seed(identity);
        using var client = Client();

        using var response = await PostAsync(client, "accept", identity, origin: origin, fetchSite: fetchSite);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(Receipts(identity));
    }

    [Theory]
    [InlineData("https://evil.example", null)]
    [InlineData(null, "cross-site")]
    [InlineData(null, "same-site")]
    [InlineData(null, null)]
    public async Task Withdraw_from_another_origin_or_with_no_fetch_metadata_is_forbidden_and_changes_nothing(string? origin, string? fetchSite)
    {
        var identity = NewIdentity();
        Seed(identity, (TrainingNotice.Version, Agreed, null));
        using var client = Client();

        using var response = await PostAsync(client, "withdraw", identity, origin: origin, fetchSite: fetchSite);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(Assert.Single(Receipts(identity)).WithdrawnAtUtc);
    }

    [Fact]
    public async Task Same_origin_by_fetch_metadata_alone_is_allowed()
    {
        var identity = NewIdentity();
        Seed(identity);
        using var client = Client();

        using var response = await PostAsync(client, "accept", identity, origin: null, fetchSite: "same-origin");

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Single(Receipts(identity));
    }

    [Theory]
    [InlineData("accept")]
    [InlineData("withdraw")]
    public async Task Anonymous_posts_are_unauthorized(string action)
    {
        using var client = Client();

        using var response = await PostAsync(client, action, null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task One_account_changing_its_consent_leaves_every_other_account_untouched()
    {
        var mine = NewIdentity();
        var theirs = NewIdentity();
        Seed(mine, (TrainingNotice.Version, Agreed, null));
        Seed(theirs, (TrainingNotice.Version, Agreed, null));
        using var client = Client();

        (await PostAsync(client, "withdraw", mine)).Dispose();
        (await PostAsync(client, "accept", mine)).Dispose();

        Assert.Null(Assert.Single(Receipts(theirs)).WithdrawnAtUtc);
        Assert.Equal(2, Receipts(mine).Count);
    }

    [Fact]
    public async Task A_hostile_identity_is_not_echoed_unescaped_on_the_page()
    {
        // The page shows no account name at all, so a hostile display name cannot reach it.
        var identity = NewIdentity();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
            db.UserAccounts.Add(new UserAccount { BifrostIdentity = identity, NormalizedBifrostIdentity = identity, DisplayName = "<script>alert(1)</script>" });
            db.SaveChanges();
        }

        var html = await PageHtmlAsync(identity);

        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("alert(1)", html);
    }

    [Fact]
    public async Task Registering_with_consent_then_withdrawing_and_agreeing_again_works_end_to_end()
    {
        using var client = Client();
        var identity = NewIdentity();
        using var registered = await RegistrationKit.RegisterAsync(client, identity, anonymous: true);
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);

        (await PostAsync(client, "withdraw", identity)).Dispose();
        (await PostAsync(client, "accept", identity)).Dispose();

        var receipts = Receipts(identity);
        Assert.Equal(2, receipts.Count);
        Assert.Single(receipts, x => x.WithdrawnAtUtc is null);
    }
}
