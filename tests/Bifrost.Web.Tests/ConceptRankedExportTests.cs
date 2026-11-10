using System.Net;
using System.Text;
using System.Text.Json;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.ConceptVotes;
using Bifrost.Web.Features.Shared;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.Web.Tests;

public sealed class ConceptRankedExportTests : IClassFixture<ConceptRankedExportTests.RankedFactory>
{
    private const string AllowedOrigin = "https://aetheria.gamecult.org";
    private static readonly DateTimeOffset Epoch = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly RankedFactory _factory;

    public ConceptRankedExportTests(RankedFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Net_ranks_up_minus_down_and_ties_break_by_key()
    {
        var c = await Seed();

        var keys = Keys(await GetOk(c, "?sort=net"));

        Assert.Equal(["bravo", "alpha", "charlie", "delta", "echo"], keys);
    }

    [Fact]
    public async Task Default_order_is_net_and_the_default_page_is_everything_under_the_cap()
    {
        var c = await Seed();

        var body = await GetOk(c, "");

        Assert.Equal(Keys(await GetOk(c, "?sort=net")), Keys(body));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("next_cursor").ValueKind);
    }

    [Fact]
    public async Task Total_ranks_up_plus_down()
    {
        var c = await Seed();

        Assert.Equal(["alpha", "bravo", "charlie", "delta", "echo"], Keys(await GetOk(c, "?sort=total")));
    }

    [Fact]
    public async Task Newest_ranks_by_registration_time()
    {
        var c = await Seed();

        Assert.Equal(["echo", "delta", "charlie", "bravo", "alpha"], Keys(await GetOk(c, "?sort=newest")));
    }

    [Fact]
    public async Task Items_carry_counts_and_title_only()
    {
        var c = await Seed();

        var first = (await GetOk(c, "?limit=1")).GetProperty("items")[0];

        Assert.Equal(["down", "key", "members", "title", "up"], first.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(["down", "up"], first.GetProperty("members").EnumerateObject().Select(p => p.Name).Order().ToArray());
    }

    [Theory]
    [InlineData("net")]
    [InlineData("total")]
    [InlineData("newest")]
    public async Task Paging_walks_every_subject_once_in_the_same_order_as_one_big_page(string sort)
    {
        var c = await Seed();
        var whole = Keys(await GetOk(c, $"?sort={sort}"));

        var walked = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var body = await GetOk(c, $"?sort={sort}&limit=2" + (cursor is null ? "" : "&cursor=" + cursor));
            walked.AddRange(Keys(body));
            cursor = body.GetProperty("next_cursor").ValueKind == JsonValueKind.Null ? null : body.GetProperty("next_cursor").GetString();
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(whole, walked);
        Assert.Equal(3, pages);
    }

    [Fact]
    public async Task A_vote_between_pages_never_repeats_the_cursors_own_row()
    {
        var c = await Seed();
        var first = await GetOk(c, "?limit=2");
        var seen = Keys(first);

        await Cast(c, "echo", "mover-1", ConceptStance.Up);
        await Cast(c, "echo", "mover-2", ConceptStance.Up);
        await Cast(c, "echo", "mover-3", ConceptStance.Up);
        await Cast(c, "echo", "mover-4", ConceptStance.Up);
        var second = Keys(await GetOk(c, "?limit=5&cursor=" + first.GetProperty("next_cursor").GetString()));

        Assert.Empty(seen.Intersect(second));
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=101")]
    [InlineData("?limit=-1")]
    [InlineData("?limit=+5")]
    [InlineData("?limit=05")]
    [InlineData("?limit=5.0")]
    [InlineData("?limit=%205")]
    [InlineData("?limit=")]
    [InlineData("?limit=abc")]
    [InlineData("?limit=99999999999999999999")]
    [InlineData("?sort=")]
    [InlineData("?sort=oldest")]
    [InlineData("?sort=Net")]
    [InlineData("?sort=net&sort=total")]
    [InlineData("?limit=1&limit=2")]
    [InlineData("?unvoted=")]
    [InlineData("?unvoted=all")]
    [InlineData("?unvoted=network&unvoted=network")]
    [InlineData("?Sort=net")]
    [InlineData("?tag=abc")]
    [InlineData("?networkTag=abc")]
    [InlineData("?page=2")]
    public async Task A_query_outside_the_closed_shape_is_a_400(string query)
    {
        var c = await Seed();

        using var response = await Get(c, query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("request_invalid", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task The_page_cap_is_one_hundred_and_one_hundred_is_allowed()
    {
        var c = await Seed();

        using var ok = await Get(c, "?limit=100");
        using var over = await Get(c, "?limit=101");

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
    }

    public static IEnumerable<object[]> HostileCursors()
    {
        static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        yield return ["x"];
        yield return ["%00"];
        yield return ["not base64!"];
        yield return ["AAAA"];
        yield return [new string('A', 201)];
        yield return [new string('A', 100_000)];
        yield return [B64("")];
        yield return [B64("1|0|0|5")];
        yield return [B64("1|0|0|5|alpha|extra")];
        yield return [B64("2|0|0|5|alpha")];
        yield return [B64("1|3|0|5|alpha")];
        yield return [B64("1|-1|0|5|alpha")];
        yield return [B64("1|0|2|5|alpha")];
        yield return [B64("1|0|0|abc|alpha")];
        yield return [B64("1|0|0|+5|alpha")];
        yield return [B64("1|0|0|-0|alpha")];
        yield return [B64("1|0|0|05|alpha")];
        yield return [B64("1|0|0|99999999999999999999|alpha")];
        yield return [B64("1|0|0|5|Alpha")];
        yield return [B64("1|0|0|5|al pha")];
        yield return [B64("1|0|0|5|alpha\n")];
        yield return [B64("1|0|0|5|'; drop table")];
        yield return [B64("1|0|0|5|../x")];
        yield return [B64("1|0|0|5|" + new string('a', 81))];
        yield return [B64("1|0|0|5|")];
        yield return [Convert.ToBase64String(Encoding.UTF8.GetBytes("1|0|0|5|alpha"))];
        yield return [B64("1|0|0|5|alpha") + "="];
        yield return [Convert.ToBase64String([0xFF, 0xFE, 0xFD, 0xFC]).Replace('+', '-').Replace('/', '_').TrimEnd('=')];
    }

    [Theory]
    [MemberData(nameof(HostileCursors))]
    public async Task A_hostile_cursor_is_a_400_and_never_a_query(string cursor)
    {
        var c = await Seed();

        using var response = await Get(c, "?cursor=" + Uri.EscapeDataString(cursor));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_cursor_is_bound_to_the_sort_and_the_filter_it_was_issued_for()
    {
        var c = await Seed();
        var net = (await GetOk(c, "?limit=1")).GetProperty("next_cursor").GetString()!;
        var unvoted = (await GetOk(c, "?limit=1&unvoted=network", client: "198.51.100.9")).GetProperty("next_cursor").GetString()!;

        using var otherSort = await Get(c, "?sort=total&cursor=" + net);
        using var dropsFilter = await Get(c, "?cursor=" + unvoted);
        using var addsFilter = await Get(c, "?unvoted=network&cursor=" + net, client: "198.51.100.9");
        using var same = await Get(c, "?limit=1&cursor=" + net);

        Assert.Equal(HttpStatusCode.BadRequest, otherSort.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, dropsFilter.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, addsFilter.StatusCode);
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
    }

    [Fact]
    public async Task A_well_formed_cursor_past_every_row_is_an_empty_last_page()
    {
        var c = await Seed();
        var past = new RankCursor(RankOrder.Net, false, long.MinValue, "a").Encode();

        var body = await GetOk(c, "?cursor=" + past);

        Assert.Empty(Keys(body));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("next_cursor").ValueKind);
    }

    [Theory]
    [InlineData("Alpha")]
    [InlineData("a_b")]
    [InlineData("..")]
    [InlineData("a%2Fb")]
    [InlineData("a%0A")]
    [InlineData("%27%3B--")]
    [InlineData("nope")]
    public async Task A_hostile_or_unknown_collection_is_a_404(string collection)
    {
        using var response = await Get(collection, "");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_collection_over_the_key_grammar_is_a_404()
    {
        using var response = await Get(new string('a', 81), "");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Retired_subjects_are_left_out()
    {
        var c = await Seed();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
            (await db.ConceptSubjects.SingleAsync(x => x.Collection == c && x.Key == "bravo")).RetiredAtUtc = Epoch;
            await db.SaveChangesAsync();
        }

        Assert.DoesNotContain("bravo", Keys(await GetOk(c, "")));
    }

    [Fact]
    public async Task Unvoted_leaves_out_what_the_callers_network_voted_and_not_what_others_did()
    {
        var c = await Seed();

        var fromVoter = Keys(await GetOk(c, "?unvoted=network", client: Voter));
        var fromStranger = Keys(await GetOk(c, "?unvoted=network", client: "198.51.100.77"));

        Assert.Equal(["charlie", "delta", "echo"], fromVoter.Order().ToArray());
        Assert.Equal(["alpha", "bravo", "charlie", "delta", "echo"], fromStranger.Order().ToArray());
    }

    [Fact]
    public async Task Unvoted_pages_and_ranks_the_same_way()
    {
        var c = await Seed();

        var walked = new List<string>();
        string? cursor = null;
        do
        {
            var body = await GetOk(c, "?unvoted=network&sort=newest&limit=1" + (cursor is null ? "" : "&cursor=" + cursor), client: "198.51.100.77");
            walked.AddRange(Keys(body));
            cursor = body.GetProperty("next_cursor").ValueKind == JsonValueKind.Null ? null : body.GetProperty("next_cursor").GetString();
        }
        while (cursor is not null && walked.Count < 10);

        Assert.Equal(["echo", "delta", "charlie", "bravo", "alpha"], walked);
    }

    [Fact]
    public async Task Unvoted_without_a_client_address_filters_nothing_and_matches_no_untagged_row()
    {
        var c = await Seed();
        await CastUntagged(c, "echo", "ghost-device");

        var keys = Keys(await GetOk(c, "?unvoted=network", client: null));

        Assert.Equal(5, keys.Length);
    }

    [Fact]
    public async Task Unvoted_and_plain_responses_expose_no_tag_or_voter_identity()
    {
        var c = await Seed();
        string[] tags;
        using (var scope = _factory.Services.CreateScope())
        {
            tags = await scope.ServiceProvider.GetRequiredService<BifrostDbContext>()
                .ConceptAppraisals.Where(x => x.NetworkTag != "").Select(x => x.NetworkTag).Distinct().ToArrayAsync();
        }

        Assert.NotEmpty(tags);
        foreach (var query in new[] { "", "?unvoted=network", "?limit=2&unvoted=network" })
        {
            using var response = await Get(c, query, client: Voter);
            var text = await response.Content.ReadAsStringAsync();
            foreach (var forbidden in tags.Concat(["voter-", "device:", "account:", "networktag", "voterkey", Voter]))
            {
                Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
            }
        }

        // The cursor is position only: it carries no tag either.
        var cursor = (await GetOk(c, "?limit=1&unvoted=network", client: Voter)).GetProperty("next_cursor").GetString()!;
        var decoded = Encoding.UTF8.GetString(Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(cursor));
        foreach (var tag in tags)
        {
            Assert.DoesNotContain(tag, decoded);
        }
    }

    [Fact]
    public async Task Nothing_in_the_request_can_name_a_tag_or_a_client()
    {
        var c = await Seed();
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/public/concepts/{c}/ranked?unvoted=network");
        request.Headers.Add("X-Test-Anonymous", "true");
        request.Headers.Add("X-Test-Client", "198.51.100.77");
        request.Headers.Add("X-Network-Tag", "anything");
        request.Headers.Add("X-Forwarded-For", Voter);

        using var response = await client.SendAsync(request);

        // The test connection address wins; the forwarded header is the proxy's to set, not the request's.
        Assert.Equal(5, Keys(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement).Length);
    }

    [Fact]
    public async Task Cache_control_is_public_for_the_plain_read_and_never_cached_for_the_unvoted_one()
    {
        var c = await Seed();

        using var plain = await Get(c, "");
        using var unvoted = await Get(c, "?unvoted=network", client: Voter);

        Assert.True(plain.Headers.CacheControl!.Public);
        Assert.Equal(TimeSpan.FromSeconds(60), plain.Headers.CacheControl.MaxAge);
        Assert.True(unvoted.Headers.CacheControl!.Private);
        Assert.True(unvoted.Headers.CacheControl.NoStore);
        Assert.False(unvoted.Headers.CacheControl.Public);
    }

    [Fact]
    public async Task Cors_and_vary_follow_the_tally_export_policy_and_the_read_sets_no_cookie_and_writes_nothing()
    {
        var c = await Seed();
        int rows, audits;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
            rows = db.ConceptAppraisals.Count();
            audits = db.AuditEvents.Count();
        }

        using var allowed = await Get(c, "", origin: AllowedOrigin);
        using var other = await Get(c, "", origin: "https://evil.example");
        using var bad = await Get(c, "?limit=0", origin: AllowedOrigin);

        Assert.Equal([AllowedOrigin], allowed.Headers.GetValues("Access-Control-Allow-Origin").ToArray());
        Assert.False(allowed.Headers.Contains("Access-Control-Allow-Credentials"));
        Assert.False(other.Headers.Contains("Access-Control-Allow-Origin"));
        foreach (var response in new[] { allowed, other, bad })
        {
            Assert.Contains("Origin", response.Headers.Vary.ToArray());
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }

        using var after = _factory.Services.CreateScope();
        var dbAfter = after.ServiceProvider.GetRequiredService<BifrostDbContext>();
        Assert.Equal(rows, dbAfter.ConceptAppraisals.Count());
        Assert.Equal(audits, dbAfter.AuditEvents.Count());
    }

    // The network that cast the votes in Seed.
    private const string Voter = "203.0.113.7";

    // alpha 3 up 2 down (net 1, total 5), bravo 2 up (net 2, total 2), charlie 1 up 1 down (net 0, total 2),
    // delta and echo unvoted (net 0, total 0). Registered alpha first, echo last. Voter's network cast alpha and
    // bravo; charlie's votes came from a second network.
    private async Task<string> Seed()
    {
        var c = "c" + Guid.NewGuid().ToString("N");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
            var i = 0;
            foreach (var key in new[] { "alpha", "bravo", "charlie", "delta", "echo" })
            {
                db.ConceptSubjects.Add(new ConceptSubject { Collection = c, Key = key, Title = key.ToUpperInvariant(), RegisteredAtUtc = Epoch.AddDays(i++) });
            }

            await db.SaveChangesAsync();
        }

        await Cast(c, "alpha", "voter-1", ConceptStance.Up);
        await Cast(c, "alpha", "voter-2", ConceptStance.Up);
        await Cast(c, "alpha", "voter-3", ConceptStance.Up);
        await Cast(c, "alpha", "voter-4", ConceptStance.Down);
        await Cast(c, "alpha", "voter-5", ConceptStance.Down);
        await Cast(c, "bravo", "voter-1", ConceptStance.Up);
        await Cast(c, "bravo", "voter-2", ConceptStance.Up);
        await Cast(c, "charlie", "other-1", ConceptStance.Up, "192.0.2.50");
        await Cast(c, "charlie", "other-2", ConceptStance.Down, "192.0.2.50");
        return c;
    }

    private async Task Cast(string collection, string key, string device, ConceptStance stance, string client = Voter)
    {
        using var scope = _factory.Services.CreateScope();
        var voter = VoterIdentity.For(CurrentBifrostActor.Anonymous, device)!;
        var result = await scope.ServiceProvider.GetRequiredService<ConceptAppraisalService>()
            .CastAsync(collection, key, voter, stance, IPAddress.Parse(client), default);
        Assert.Equal(CastResult.Recorded, result);
    }

    private async Task CastUntagged(string collection, string key, string device)
    {
        using var scope = _factory.Services.CreateScope();
        var voter = VoterIdentity.For(CurrentBifrostActor.Anonymous, device)!;
        await scope.ServiceProvider.GetRequiredService<ConceptAppraisalService>()
            .CastAsync(collection, key, voter, ConceptStance.Up, null, default);
    }

    private async Task<HttpResponseMessage> Get(string collection, string query, string? client = null, string? origin = null)
    {
        using var http = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/public/concepts/{collection}/ranked{query}");
        request.Headers.Add("X-Test-Anonymous", "true");
        if (client is not null)
        {
            request.Headers.Add("X-Test-Client", client);
        }

        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        return await http.SendAsync(request);
    }

    private async Task<JsonElement> GetOk(string collection, string query, string? client = null)
    {
        using var response = await Get(collection, query, client);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static string[] Keys(JsonElement body) =>
        body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetString()!).ToArray();

    // The test host has no connection address; a header stands in for the one the proxy path would have resolved.
    private sealed class ClientAddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue("X-Test-Client", out var address))
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(address.ToString());
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }

    public sealed class RankedFactory : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.AddTransient<IStartupFilter, ClientAddressFilter>());
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConceptVotes:AllowedOrigins:0"] = AllowedOrigin,
                    ["ConceptVotes:NetworkTagSecret"] = "ranked-test-tag-secret-0123456789-abcdef"
                }));
        }
    }
}
