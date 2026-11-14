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

    [Fact]
    public async Task Patron_intake_for_a_folded_heimdall_id_records_on_the_survivor()
    {
        var s = await SeedAsync();
        await MergeAsync(s, AccountMergeState.Final);
        var payload = JsonSerializer.Serialize(new
        {
            heimdallAccountId = s.FoldedHeimdallId, provider = "PayPal", providerEventId = $"WH-{Guid.NewGuid():N}", kind = "OneTimeDonation",
            amount = 25m, currencyCode = "USD", externalSupportId = "CAPTURE-1", supportedAtUtc = DateTimeOffset.UtcNow, isCurrentRecurringSupport = false
        });
        using var hasher = new HMACSHA256(Encoding.UTF8.GetBytes("test-heimdall-intake-secret"));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/heimdall/patron-support/events");
        request.Headers.Add("X-Heimdall-Signature-256", "sha256=" + Convert.ToHexString(hasher.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant());
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var owners = await WithDb(db => db.PatronSupportEvents.Where(x => x.UserAccountId == s.Survivor || x.UserAccountId == s.Folded).Select(x => x.UserAccountId).ToListAsync());
        Assert.Equal([s.Survivor], owners);
    }
}
