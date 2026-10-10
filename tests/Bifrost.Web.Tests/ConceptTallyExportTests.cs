using System.Net;
using System.Text.Json;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.ConceptVotes;
using Bifrost.Web.Features.Shared;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.Web.Tests;

public sealed class ConceptTallyExportTests : IClassFixture<ConceptTallyExportTests.ExportFactory>
{
    private const string AllowedOrigin = "https://aetheria.gamecult.org";
    private const string Client = "203.0.113.7";

    private readonly ExportFactory _factory;

    public ConceptTallyExportTests(ExportFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Anonymous_request_gets_the_public_counts()
    {
        var collection = NewCollection();
        await AddSubject(collection, "alpha");
        await AddSubject(collection, "beta");
        await AddSubject(collection, "untouched");
        await Cast(collection, "alpha", DeviceVoter("dev-one"), ConceptStance.Up);
        await Cast(collection, "alpha", DeviceVoter("dev-two"), ConceptStance.Up);
        await Cast(collection, "alpha", DeviceVoter("dev-three"), ConceptStance.Down);
        await Cast(collection, "alpha", await AccountVoter(MembershipStatus.Active), ConceptStance.Up);
        await Cast(collection, "alpha", await AccountVoter(MembershipStatus.Authenticated), ConceptStance.Down);
        await Cast(collection, "beta", DeviceVoter("dev-one"), ConceptStance.Down);

        using var response = await Get(collection);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(["alpha", "beta", "untouched"], body.EnumerateObject().Select(p => p.Name).Order().ToArray());
        AssertCounts(body.GetProperty("alpha"), up: 3, down: 2, memberUp: 1, memberDown: 0);
        AssertCounts(body.GetProperty("beta"), up: 0, down: 1, memberUp: 0, memberDown: 0);
        AssertCounts(body.GetProperty("untouched"), up: 0, down: 0, memberUp: 0, memberDown: 0);
    }

    [Fact]
    public async Task Payload_carries_no_voter_identity()
    {
        var collection = NewCollection();
        await AddSubject(collection, "alpha");
        var account = await AccountVoter(MembershipStatus.Authenticated);
        var member = await AccountVoter(MembershipStatus.Active);
        await Cast(collection, "alpha", DeviceVoter("canary-device-id-9c2e"), ConceptStance.Up);
        await Cast(collection, "alpha", account, ConceptStance.Down);
        await Cast(collection, "alpha", member, ConceptStance.Up);

        using var response = await Get(collection);

        var text = await response.Content.ReadAsStringAsync();
        var alpha = JsonDocument.Parse(text).RootElement.GetProperty("alpha");
        Assert.Equal(["down", "members", "up"], alpha.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(["down", "up"], alpha.GetProperty("members").EnumerateObject().Select(p => p.Name).Order().ToArray());

        string[] tags;
        using (var scope = _factory.Services.CreateScope())
        {
            tags = await scope.ServiceProvider.GetRequiredService<BifrostDbContext>()
                .ConceptAppraisals.Select(x => x.NetworkTag).Distinct().ToArrayAsync();
        }

        Assert.All(tags, tag => Assert.NotEmpty(tag));
        var forbidden = new List<string> { "canary-device-id-9c2e", "device:", "account:", "voterkey", "networktag", "useraccount" };
        forbidden.AddRange(tags);
        foreach (var voter in new[] { account, member })
        {
            forbidden.Add(voter.UserAccountId!.Value.ToString("N"));
            forbidden.Add(voter.UserAccountId!.Value.ToString("D"));
        }

        foreach (var value in forbidden)
        {
            Assert.DoesNotContain(value, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Cors_allows_only_the_configured_origin_without_credentials()
    {
        var collection = NewCollection();
        await AddSubject(collection, "alpha");

        using var allowed = await Get(collection, AllowedOrigin);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal([AllowedOrigin], allowed.Headers.GetValues("Access-Control-Allow-Origin").ToArray());
        Assert.False(allowed.Headers.Contains("Access-Control-Allow-Credentials"));

        using var otherHost = await Get(collection, "https://evil.example");
        Assert.False(otherHost.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(otherHost.Headers.Contains("Access-Control-Allow-Credentials"));

        using var preflightGet = await Preflight(collection, AllowedOrigin, "GET");
        Assert.Equal([AllowedOrigin], preflightGet.Headers.GetValues("Access-Control-Allow-Origin").ToArray());
        Assert.False(preflightGet.Headers.Contains("Access-Control-Allow-Credentials"));

        using var preflightPost = await Preflight(collection, AllowedOrigin, "POST");
        Assert.False(preflightPost.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(preflightPost.Headers.Contains("Access-Control-Allow-Methods"));
    }

    [Theory]
    [InlineData("https://aetheria.gamecult.org.evil.example")]
    [InlineData("https://aetheria.gamecult.orgx")]
    [InlineData("https://evilaetheria.gamecult.org")]
    [InlineData("https://www.aetheria.gamecult.org")]
    [InlineData("HTTPS://AETHERIA.GAMECULT.ORG")]
    [InlineData("https://Aetheria.gamecult.org")]
    [InlineData("https://aetheria.gamecult.org/")]
    [InlineData("https://aetheria.gamecult.org:8443")]
    [InlineData("http://aetheria.gamecult.org")]
    [InlineData("null")]
    [InlineData("*")]
    [InlineData("")]
    public async Task Cors_refuses_origins_that_only_resemble_the_configured_one(string origin)
    {
        var collection = NewCollection();
        await AddSubject(collection, "alpha");

        using var response = await Get(collection, origin);
        using var preflight = await Preflight(collection, origin, "GET");

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(preflight.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Other_routes_carry_no_cors_headers()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.Add("Origin", AllowedOrigin);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Retired_subject_leaves_the_export()
    {
        var collection = NewCollection();
        await AddSubject(collection, "alpha");
        await AddSubject(collection, "gone");
        await Cast(collection, "gone", DeviceVoter("dev-one"), ConceptStance.Up);
        await Retire(collection, "gone");

        using var response = await Get(collection);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(["alpha"], body.EnumerateObject().Select(p => p.Name).ToArray());
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        Assert.Single(db.ConceptAppraisals.Where(x => x.Subject!.Key == "gone" && x.Subject.Collection == collection));
    }

    [Fact]
    public async Task Cache_control_is_public_sixty_seconds()
    {
        var collection = NewCollection();
        await AddSubject(collection, "alpha");

        using var response = await Get(collection);

        var cache = response.Headers.CacheControl;
        Assert.NotNull(cache);
        Assert.True(cache!.Public);
        Assert.Equal(TimeSpan.FromSeconds(60), cache.MaxAge);
    }

    [Fact]
    public async Task Unknown_collection_is_not_found()
    {
        using var response = await Get(NewCollection());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_export_only_reads()
    {
        var collection = NewCollection();
        await AddSubject(collection, "alpha");

        int rowsBefore, auditBefore;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
            rowsBefore = db.ConceptAppraisals.Count();
            auditBefore = db.AuditEvents.Count();
        }

        using var response = await Get(collection);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        using var after = _factory.Services.CreateScope();
        var dbAfter = after.ServiceProvider.GetRequiredService<BifrostDbContext>();
        Assert.Equal(rowsBefore, dbAfter.ConceptAppraisals.Count());
        Assert.Equal(auditBefore, dbAfter.AuditEvents.Count());
    }

    private async Task<HttpResponseMessage> Get(string collection, string? origin = null)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/public/concepts/{collection}/tallies");
        request.Headers.Add("X-Test-Anonymous", "true");
        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> Preflight(string collection, string origin, string method)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, $"/public/concepts/{collection}/tallies");
        request.Headers.Add("X-Test-Anonymous", "true");
        request.Headers.TryAddWithoutValidation("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        return await client.SendAsync(request);
    }

    private static void AssertCounts(JsonElement tally, int up, int down, int memberUp, int memberDown)
    {
        Assert.Equal(up, tally.GetProperty("up").GetInt32());
        Assert.Equal(down, tally.GetProperty("down").GetInt32());
        Assert.Equal(memberUp, tally.GetProperty("members").GetProperty("up").GetInt32());
        Assert.Equal(memberDown, tally.GetProperty("members").GetProperty("down").GetInt32());
    }

    private static string NewCollection() => "c" + Guid.NewGuid().ToString("N");

    private static VoterIdentity DeviceVoter(string deviceId) =>
        VoterIdentity.For(CurrentBifrostActor.Anonymous, deviceId)!;

    private async Task<VoterIdentity> AccountVoter(MembershipStatus status)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var now = DateTimeOffset.UtcNow;
        var user = new UserAccount
        {
            DisplayName = "voter",
            GitHubLogin = "voter",
            NormalizedGitHubLogin = "voter",
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            Membership = new Membership { Status = status, CreatedAtUtc = now }
        };
        user.Membership.UserAccountId = user.Id;
        db.UserAccounts.Add(user);
        await db.SaveChangesAsync();
        return VoterIdentity.For(new CurrentBifrostActor(true, user), null)!;
    }

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

    private async Task Cast(string collection, string key, VoterIdentity voter, ConceptStance stance)
    {
        using var scope = _factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ConceptAppraisalService>()
            .CastAsync(collection, key, voter, stance, IPAddress.Parse(Client), default);
        Assert.Equal(CastResult.Recorded, result);
    }

    public sealed class ExportFactory : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConceptVotes:AllowedOrigins:0"] = AllowedOrigin,
                    ["ConceptVotes:NetworkTagSecret"] = "export-test-tag-secret-0123456789-abcdef"
                }));
        }
    }
}
