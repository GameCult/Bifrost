using System.Net;
using System.Text;
using Bifrost.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Bifrost.Web.Tests.AccountMergeSagaTests;

namespace Bifrost.Web.Tests;

/// <summary>Who may pull merge notices: the chat host's own bearer and nobody else, whatever else the request carries (the test host signs every request in as an admin).</summary>
public sealed class MergeNoticeAuthTests
{
    private const string Token = "chat-notices-test-token-0123456789";
    private const string Get = "/api/merge-notices/chat";
    private const string Ack = "/api/merge-notices/chat/ack";

    private static HttpRequestMessage Request(HttpMethod method, string url, string? authorization, string? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static string AckBody(Guid id) => $"{{\"merge_id\":\"{id}\",\"type\":\"merged\"}}";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task With_no_token_configured_both_routes_are_404_even_for_a_bearer_that_looks_right(string? token)
    {
        using var rig = new Rig(chatNoticesToken: token);
        using var client = rig.Host.NewClient();

        foreach (var authorization in new[] { null, "Bearer " + Token, "Bearer " })
        {
            using var read = await client.SendAsync(Request(HttpMethod.Get, Get, authorization));
            using var ack = await client.SendAsync(Request(HttpMethod.Post, Ack, authorization, AckBody(Guid.NewGuid())));
            Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, ack.StatusCode);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Bearer wrong-wrong-wrong-wrong-wrong-wrong")]
    [InlineData("Bearer chat-notices-test-token-012345678")] // one short
    [InlineData("Bearer chat-notices-test-token-01234567890")] // one long
    [InlineData("Bearer c")]
    [InlineData("Bearer chat-notices-test-token-0123456789 ")]
    [InlineData("chat-notices-test-token-0123456789")]
    [InlineData("Basic chat-notices-test-token-0123456789")]
    [InlineData("Bearer chat-notices-test-token-0123456789, Bearer other")]
    public async Task A_missing_wrong_short_or_long_token_is_401_with_no_body_on_both_routes_and_acks_nothing(string? authorization)
    {
        using var rig = new Rig(chatNoticesToken: Token);
        var (merge, _, _) = await Committed(rig);
        using var client = rig.Host.NewClient();

        using var read = await client.SendAsync(Request(HttpMethod.Get, Get, authorization));
        using var ack = await client.SendAsync(Request(HttpMethod.Post, Ack, authorization, AckBody(merge)));

        foreach (var response in new[] { read, ack })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsStringAsync());
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        }

        Assert.Equal(0, await rig.InScopeAsync(async sp => await sp.GetRequiredService<BifrostDbContext>().AccountMergeNoticeAcks.CountAsync()));
    }

    [Fact]
    public async Task A_token_in_the_query_or_a_cookie_is_not_a_bearer()
    {
        using var rig = new Rig(chatNoticesToken: Token);
        using var client = rig.Host.NewClient();

        using var query = await client.SendAsync(Request(HttpMethod.Get, $"{Get}?token={Token}&access_token={Token}", null));
        using var cookie = Request(HttpMethod.Get, Get, null);
        cookie.Headers.Add("Cookie", $"token={Token}");
        using var withCookie = await client.SendAsync(cookie);

        Assert.Equal(HttpStatusCode.Unauthorized, query.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, withCookie.StatusCode);
    }

    [Fact]
    public async Task The_right_token_reads_and_acks_with_no_store_and_no_cors_or_cookie()
    {
        using var rig = new Rig(chatNoticesToken: Token);
        var (merge, _, _) = await Committed(rig);
        using var client = rig.Host.NewClient();

        using var read = await client.SendAsync(Request(HttpMethod.Get, Get, "Bearer " + Token));
        using var ack = await client.SendAsync(Request(HttpMethod.Post, Ack, "Bearer " + Token, AckBody(merge)));

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, ack.StatusCode);
        foreach (var response in new[] { read, ack })
        {
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }

        Assert.Contains("application/json", read.Content.Headers.ContentType?.ToString());
    }

    internal static async Task<(Guid Merge, Party Survivor, Party Folded)> Committed(Rig rig)
    {
        var survivor = await rig.AccountAsync("survivor", "twitch");
        var folded = await rig.AccountAsync("folded", "discord");
        var id = await rig.ConfirmedAsync(survivor, folded, rig.Weak(survivor, "twitch"), rig.Weak(folded, "discord"));
        Assert.Equal(Bifrost.Web.Domain.AccountMergeState.Committed, (await rig.AdvanceAsync(id)).State);
        return (id, survivor, folded);
    }
}
