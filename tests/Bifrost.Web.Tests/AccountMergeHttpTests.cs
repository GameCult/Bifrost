using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.Web.Tests;

/// <summary>A folded member's browser and Heimdall's patron intake, over HTTP: the merge ledger is the single gate behind both.</summary>
public sealed class AccountMergeHttpTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private sealed record Seeded(Guid Survivor, Guid Folded, Guid Motion, string FoldedHeimdallId, long SurvivorGitHubId, long FoldedGitHubId);

    private async Task<Seeded> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var seed = Random.Shared.NextInt64(1_000_000, 900_000_000);
        UserAccount Make(string name, long gitHubId) => new()
        {
            DisplayName = name, BifrostIdentity = name, NormalizedBifrostIdentity = name, GitHubUserId = gitHubId, GitHubLogin = name, NormalizedGitHubLogin = name,
            HeimdallAccountId = "hd-" + name,
            MemberProfile = new MemberProfile { Nickname = name, Headline = "seed", UpdatedAtUtc = DateTimeOffset.UtcNow },
            Membership = new Bifrost.Web.Domain.Membership { Status = MembershipStatus.Active, ApprovedAtUtc = DateTimeOffset.UtcNow }
        };
        var survivor = Make($"surv-{seed}", seed);
        var folded = Make($"fold-{seed}", seed + 1);
        db.UserAccounts.AddRange(survivor, folded);
        var motion = new Motion
        {
            CreatedByUserAccountId = survivor.Id, Title = "Open motion", Status = MotionStatus.Open, OpensAtUtc = DateTimeOffset.UtcNow.AddHours(-1),
            ClosesAtUtc = DateTimeOffset.UtcNow.AddDays(2), CreatedAtUtc = DateTimeOffset.UtcNow
        };
        db.Motions.Add(motion);
        await db.SaveChangesAsync();
        return new Seeded(survivor.Id, folded.Id, motion.Id, folded.HeimdallAccountId, seed, seed + 1);
    }

    private HttpClient ClientFor(long gitHubId, string login)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost"), HandleCookies = true });
        client.DefaultRequestHeaders.Add("X-Test-GitHub-Id", gitHubId.ToString());
        client.DefaultRequestHeaders.Add("X-Test-GitHub-Login", login);
        return client;
    }

    private static async Task<string> TokenAsync(HttpClient client)
    {
        using var page = await client.GetAsync("/App/Motions");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        return token;
    }

    private static Task<HttpResponseMessage> VoteAsync(HttpClient client, Guid motion, string token) =>
        client.PostAsync("/App/Motions?handler=Vote", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["motionId"] = motion.ToString(), ["choice"] = "For", ["comment"] = "yes", ["__RequestVerificationToken"] = token
        }));

    private async Task<T> WithDb<T>(Func<BifrostDbContext, Task<T>> read)
    {
        using var scope = factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<BifrostDbContext>());
    }

    private Task<int> VoteRowsAsync(Guid motion) => WithDb(db => db.Votes.IgnoreQueryFilters().CountAsync(x => x.MotionId == motion));

    private async Task<Guid> MergeAsync(Seeded s, AccountMergeState state)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var merge = new AccountMerge { SurvivorUserAccountId = s.Survivor, FoldedUserAccountId = s.Folded, State = state, CreatedUtc = DateTimeOffset.UtcNow };
        db.AccountMerges.Add(merge);
        await db.SaveChangesAsync();
        return merge.Id;
    }

    private async Task SetStateAsync(Guid mergeId, AccountMergeState state) =>
        await WithDb(async db =>
        {
            (await db.AccountMerges.SingleAsync(x => x.Id == mergeId)).State = state;
            await db.SaveChangesAsync();
            return 0;
        });

    [Fact]
    public async Task A_folded_session_cookie_acts_as_nobody_while_Frozen_and_after_Committed_and_the_survivor_still_votes()
    {
        var s = await SeedAsync();
        using var folded = ClientFor(s.FoldedGitHubId, $"fold-{s.SurvivorGitHubId}");
        var token = await TokenAsync(folded); // a live, valid form from before the merge: the cookie outlives the merge
        var merge = await MergeAsync(s, AccountMergeState.Confirmed);

        using (var whileFrozen = await VoteAsync(folded, s.Motion, token))
        {
            Assert.NotEqual(HttpStatusCode.OK, whileFrozen.StatusCode);
        }

        Assert.Equal(0, await VoteRowsAsync(s.Motion));
        using (var page = await folded.GetAsync("/App/Motions"))
        {
            Assert.NotEqual(HttpStatusCode.OK, page.StatusCode);
        }

        await SetStateAsync(merge, AccountMergeState.HeimdallMoved);
        await SetStateAsync(merge, AccountMergeState.Committed);
        using (var afterCommit = await VoteAsync(folded, s.Motion, token))
        {
            Assert.NotEqual(HttpStatusCode.OK, afterCommit.StatusCode);
        }

        Assert.Equal(0, await VoteRowsAsync(s.Motion));

        using var survivor = ClientFor(s.SurvivorGitHubId, $"surv-{s.SurvivorGitHubId}");
        using var control = await VoteAsync(survivor, s.Motion, await TokenAsync(survivor));
        Assert.Equal(HttpStatusCode.Redirect, control.StatusCode);
        Assert.Equal(1, await VoteRowsAsync(s.Motion));
    }

    private async Task<HttpStatusCode> IntakeAsync(Seeded s, string eventId)
    {
        var payload = JsonSerializer.Serialize(new
        {
            heimdallAccountId = s.FoldedHeimdallId, provider = "PayPal", providerEventId = eventId, kind = "OneTimeDonation",
            amount = 25m, currencyCode = "USD", externalSupportId = "CAPTURE-1", supportedAtUtc = DateTimeOffset.UtcNow, isCurrentRecurringSupport = false
        });
        using var hasher = new HMACSHA256(Encoding.UTF8.GetBytes("test-heimdall-intake-secret"));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/heimdall/patron-support/events");
        request.Headers.Add("X-Heimdall-Signature-256", "sha256=" + Convert.ToHexString(hasher.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant());
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var client = factory.CreateClient();
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private Task<List<Guid>> EventOwnersAsync(Seeded s) =>
        WithDb(db => db.PatronSupportEvents.Where(x => x.UserAccountId == s.Survivor || x.UserAccountId == s.Folded).Select(x => x.UserAccountId).ToListAsync());

    /// <summary>Lands the merge through the ledger's own commit, as the saga will: the move done, then the Bifrost records reduced.</summary>
    private async Task<Guid> CommitMergeAsync(Seeded s)
    {
        var merge = await MergeAsync(s, AccountMergeState.HeimdallMoved);
        await WithDb(async db =>
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<Bifrost.Web.Features.Membership.AccountMergeLedger>().CommitAsync(merge, default);
            return 0;
        });
        return merge;
    }

    [Fact]
    public async Task Patron_intake_for_a_folded_heimdall_id_records_on_the_survivor()
    {
        var s = await SeedAsync();
        await MergeAsync(s, AccountMergeState.Final);

        Assert.Equal(HttpStatusCode.Accepted, await IntakeAsync(s, $"WH-{Guid.NewGuid():N}"));

        Assert.Equal([s.Survivor], await EventOwnersAsync(s));
    }

    [Fact]
    public async Task A_patron_event_that_lands_during_grace_is_journalled_once_and_undo_moves_it_back_to_the_folded_account()
    {
        var s = await SeedAsync();
        var merge = await CommitMergeAsync(s);
        var eventId = $"WH-{Guid.NewGuid():N}";

        Assert.Equal(HttpStatusCode.Accepted, await IntakeAsync(s, eventId));
        Assert.Equal(HttpStatusCode.Accepted, await IntakeAsync(s, eventId)); // the provider retries: nothing doubles
        Assert.Equal([s.Survivor], await EventOwnersAsync(s));
        Assert.Equal(1, await WithDb(db => db.AccountMergeEntries.CountAsync(x => x.MergeId == merge && x.Kind == AccountMergeEntryKind.PatronSupportEvent)));

        await SetStateAsync(merge, AccountMergeState.HeimdallReversed);
        await WithDb(async db =>
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<Bifrost.Web.Features.Membership.AccountMergeLedger>().UndoLocalAsync(merge, default);
            return 0;
        });

        Assert.Equal([s.Folded], await EventOwnersAsync(s));
    }

    private Task<List<AccountMergeEntry>> PatronJournalAsync(Guid merge) =>
        WithDb(db => db.AccountMergeEntries.Where(x => x.MergeId == merge).OrderBy(x => x.Seq).ToListAsync());

    [Fact]
    public async Task A_single_patron_event_during_grace_is_journalled_by_its_own_delivery_and_undo_moves_it_back()
    {
        var s = await SeedAsync();
        var merge = await CommitMergeAsync(s);

        Assert.Equal(HttpStatusCode.Accepted, await IntakeAsync(s, $"WH-{Guid.NewGuid():N}")); // no retry follows

        var journal = await PatronJournalAsync(merge);
        Assert.Single(journal, x => x.Kind == AccountMergeEntryKind.PatronSupportEvent);
        Assert.Equal(journal.Count, journal.Select(x => x.Seq).Distinct().Count()); // the new entry takes its own Seq
        await SetStateAsync(merge, AccountMergeState.HeimdallReversed);
        await WithDb(async db =>
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<Bifrost.Web.Features.Membership.AccountMergeLedger>().UndoLocalAsync(merge, default);
            return 0;
        });
        Assert.Equal([s.Folded], await EventOwnersAsync(s));
    }

    [Fact]
    public async Task A_provider_retry_journals_an_event_the_first_delivery_left_unjournalled()
    {
        var s = await SeedAsync();
        var merge = await CommitMergeAsync(s);
        var eventId = $"WH-{Guid.NewGuid():N}";
        await WithDb(async db => // the first delivery recorded on the survivor, then died before journalling
        {
            db.PatronSupportEvents.Add(new PatronSupportEvent
            {
                UserAccountId = s.Survivor, Provider = ExternalPatronProvider.PayPal, ProviderEventId = eventId, ExternalSupportId = "CAPTURE-1",
                Kind = PatronSupportEventKind.OneTimeDonation, Amount = 25m, SupportedAtUtc = DateTimeOffset.UtcNow, RecordedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            return 0;
        });
        Assert.DoesNotContain(await PatronJournalAsync(merge), x => x.Kind == AccountMergeEntryKind.PatronSupportEvent);

        Assert.Equal(HttpStatusCode.Accepted, await IntakeAsync(s, eventId));

        Assert.Single(await PatronJournalAsync(merge), x => x.Kind == AccountMergeEntryKind.PatronSupportEvent);
        Assert.Equal([s.Survivor], await EventOwnersAsync(s));
    }

    [Fact]
    public async Task A_stripe_checkout_opened_by_an_account_that_folded_pays_the_survivor_and_is_journalled()
    {
        var s = await SeedAsync();
        var merge = await CommitMergeAsync(s);
        var payload = JsonSerializer.Serialize(new
        {
            id = $"evt_{Guid.NewGuid():N}", type = "checkout.session.completed", created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            data = new { @object = new { id = $"cs_{Guid.NewGuid():N}", customer = "cus_1", currency = "usd", amount_total = 1900, payment_status = "paid",
                metadata = new Dictionary<string, string> { ["bifrost_user_account_id"] = s.Folded.ToString("N") } } }
        });
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        using var hasher = new HMACSHA256(Encoding.UTF8.GetBytes("whsec_test_webhook_secret"));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/patronage/stripe/webhook");
        request.Headers.Add("Stripe-Signature", $"t={timestamp},v1={Convert.ToHexString(hasher.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{payload}"))).ToLowerInvariant()}");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal([s.Survivor], await EventOwnersAsync(s));
        Assert.Single(await PatronJournalAsync(merge), x => x.Kind == AccountMergeEntryKind.PatronSupportEvent);
    }

    [Fact]
    public async Task A_folded_bootstrap_admins_sign_in_does_not_reactivate_the_merged_membership_and_an_approval_is_refused()
    {
        var s = await SeedAsync();
        await CommitMergeAsync(s);
        using var folded = ClientFor(s.FoldedGitHubId, "test-admin"); // the bootstrap login: every request would re-promote it

        using (await folded.GetAsync("/"))
        {
        }

        Assert.Equal((MembershipStatus.Merged, false), await WithDb(async db =>
        {
            var membership = await db.Memberships.SingleAsync(x => x.UserAccountId == s.Folded);
            return (membership.Status, membership.IsPlatformAdmin);
        }));

        using var admin = ClientFor(Random.Shared.NextInt64(1_000_000_000, 2_000_000_000), "test-admin");
        var token = await TokenAsync(admin);
        using var approve = await admin.PostAsync("/App/Members?handler=Approve", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["userAccountId"] = s.Folded.ToString(), ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.Redirect, approve.StatusCode);
        Assert.Equal(MembershipStatus.Merged, await WithDb(async db => (await db.Memberships.SingleAsync(x => x.UserAccountId == s.Folded)).Status));
    }

    /// <summary>Forbid answers 403 or sends the browser to the access-denied page; either way the page and its Heimdall flows are not served.</summary>
    private static void AssertRefused(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Found, response.StatusCode.ToString());
        Assert.DoesNotContain("heimdall", response.Headers.Location?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(AccountMergeState.Confirmed)]
    [InlineData(AccountMergeState.Committed)]
    public async Task A_frozen_or_folded_cookie_cannot_reach_the_linked_accounts_page_or_start_one_of_its_flows(AccountMergeState state)
    {
        var s = await SeedAsync();
        using var folded = ClientFor(s.FoldedGitHubId, $"fold-{s.SurvivorGitHubId}");
        var token = await TokenAsync(folded);
        using (var control = await folded.GetAsync("/Membership/LinkedAccounts"))
        {
            Assert.Equal(HttpStatusCode.OK, control.StatusCode); // before the merge the page is theirs
        }

        await MergeAsync(s, state);

        using var page = await folded.GetAsync("/Membership/LinkedAccounts");
        AssertRefused(page);
        using var refresh = await folded.PostAsync("/Membership/LinkedAccounts?handler=Refresh", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["via"] = "google", ["__RequestVerificationToken"] = token
        }));
        AssertRefused(refresh);

        using var survivor = ClientFor(s.SurvivorGitHubId, $"surv-{s.SurvivorGitHubId}");
        using var other = await survivor.GetAsync("/Membership/LinkedAccounts");
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }
}

/// <summary>The desk's bearer door judges standing the way the cookie gate and the chat refusal do.</summary>
public sealed class AccountMergeDeskTests
{
    [Theory]
    [InlineData(AccountMergeState.Confirmed)]
    [InlineData(AccountMergeState.Committed)]
    public async Task A_frozen_or_folded_subjects_live_desk_token_is_forbidden(AccountMergeState state)
    {
        using var factory = new DeskWebApplicationFactory();
        DeskReplay.HoldVectorForks(factory.Door);
        await factory.EnsureAccountAsync(DeskWebApplicationFactory.Alice, true);
        var token = factory.Token(DeskWebApplicationFactory.Alice, "bifrost-desk", "desk.read");
        Task<DeskReply> ListAsync() => factory.SendAsync("GET", DeskReplay.Target("list", "gamecult-chat", null), null, token);

        Assert.Equal(200, (await ListAsync()).Status); // the live member, before the merge

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
            var survivor = new UserAccount { BifrostIdentity = "desk-survivor", NormalizedBifrostIdentity = "desk-survivor", GitHubLogin = "desk-survivor", NormalizedGitHubLogin = "desk-survivor" };
            db.UserAccounts.Add(survivor);
            db.AccountMerges.Add(new AccountMerge
            {
                SurvivorUserAccountId = survivor.Id, FoldedUserAccountId = Guid.Parse(DeskWebApplicationFactory.Alice), State = state, CreatedUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(403, (await ListAsync()).Status);
    }
}
