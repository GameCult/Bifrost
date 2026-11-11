using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bifrost.Web.Features.Desk;
using Bifrost.Web.Tests.Support;

namespace Bifrost.Web.Tests;

public sealed class DeskAnswerTests
{
    private static readonly string Socket = DeskWebApplicationFactory.SocketOf("gamecult-chat");

    private static async Task<(DeskWebApplicationFactory Factory, string Token)> ArrangeAsync()
    {
        var factory = new DeskWebApplicationFactory();
        await factory.EnsureAccountAsync(DeskWebApplicationFactory.Alice, true);
        return (factory, factory.Token(DeskWebApplicationFactory.Alice, "bifrost-desk", "desk.answer"));
    }

    private static AnswerResult Stale(string forkId) => new(
        DeskSchemas.AnswerResult,
        new DeskAnswerDocument(
            DeskSchemas.DeskAnswer, "n1", forkId, "Refused", null, null, "stale_fork", "oldhash", "no", null,
            "2026-11-12T19:02:00Z", "2026-11-12T19:02:05Z", false, DeskWebApplicationFactory.Alice, "Operator",
            "00000001-0000-4000-8000-000000000001"),
        null,
        null);

    [Fact]
    public async Task Stale_fork_attaches_current_card_fresh_and_replayed()
    {
        var (factory, token) = await ArrangeAsync();
        using (factory)
        {
            factory.Door.AnswerScript = (_, _, _) => new DeskAnswerOutcome.Answered(Stale(DeskReplay.ChatFork));
            var target = DeskReplay.Target("answer", "gamecult-chat", null);

            factory.Door.Forks[(Socket, DeskReplay.ChatFork)] = DeskReplay.Entry(DeskReplay.Card(DeskReplay.ChatFork, "first card"));
            var fresh = await factory.SendAsync("POST", target, DeskReplay.AnswerBody(DeskReplay.ChatFork), token);

            // The same stored answer is replayed after the card changed: nothing is stored for the card, so the reply carries the new one.
            factory.Door.Forks[(Socket, DeskReplay.ChatFork)] = DeskReplay.Entry(DeskReplay.Card(DeskReplay.ChatFork, "second card"));
            var replayed = await factory.SendAsync("POST", target, DeskReplay.AnswerBody(DeskReplay.ChatFork), token);

            Assert.Equal(409, fresh.Status);
            Assert.Equal("first card", (string?)JsonNode.Parse(fresh.Body)!["card"]!["title"]);
            Assert.Equal(409, replayed.Status);
            Assert.Equal("second card", (string?)JsonNode.Parse(replayed.Body)!["card"]!["title"]);
        }
    }

    [Fact]
    public async Task A_refusal_that_is_not_stale_carries_no_card_and_asks_no_read()
    {
        var (factory, token) = await ArrangeAsync();
        using (factory)
        {
            var closed = Stale(DeskReplay.ChatFork);
            factory.Door.AnswerScript = (_, _, _) => new DeskAnswerOutcome.Answered(closed with { Answer = closed.Answer with { Code = "fork_closed" } });
            var reply = await factory.SendAsync("POST", DeskReplay.Target("answer", "gamecult-chat", null), DeskReplay.AnswerBody(DeskReplay.ChatFork), token);

            Assert.Equal(409, reply.Status);
            Assert.Null(JsonNode.Parse(reply.Body)!["card"]);
            Assert.DoesNotContain(factory.Door.Calls, c => c.Kind == "read");
        }
    }

    [Fact]
    public async Task A_stale_answers_card_read_of_an_oversize_fork_is_422_fork_too_large_naming_the_fork()
    {
        var (factory, token) = await ArrangeAsync();
        using (factory)
        {
            factory.Door.AnswerScript = (_, _, _) => new DeskAnswerOutcome.Answered(Stale(DeskReplay.ChatFork));
            factory.Door.ReadScript = (_, id) => new DeskReadOutcome.ForkTooLarge(id);

            var reply = await factory.SendAsync("POST", DeskReplay.Target("answer", "gamecult-chat", null), DeskReplay.AnswerBody(DeskReplay.ChatFork), token);

            var doc = JsonNode.Parse(reply.Body)!;
            Assert.Equal(422, reply.Status);
            Assert.Equal("fork_too_large", (string?)doc["code"]);
            Assert.Contains(DeskReplay.ChatFork, (string)doc["error"]!);
            Assert.Null(doc["card"]);
        }
    }

    [Fact]
    public async Task Card_read_failure_is_unavailable()
    {
        foreach (var failure in new[] { "throws", "refuses" })
        {
            var (factory, token) = await ArrangeAsync();
            using (factory)
            {
                factory.Door.AnswerScript = (_, _, _) => new DeskAnswerOutcome.Answered(Stale(DeskReplay.ChatFork));
                factory.Door.ThrowOnRead = failure == "throws";
                // "refuses": no fork held, so the read answers fork_unknown

                var reply = await factory.SendAsync("POST", DeskReplay.Target("answer", "gamecult-chat", null), DeskReplay.AnswerBody(DeskReplay.ChatFork), token);

                Assert.True(503 == reply.Status, failure);
                Assert.Equal("unavailable", (string?)JsonNode.Parse(reply.Body)!["code"]);
            }
        }
    }

    [Fact]
    public async Task The_body_reaches_the_door_byte_for_byte()
    {
        var (factory, token) = await ArrangeAsync();
        using (factory)
        {
            DeskReplay.HoldVectorForks(factory.Door);
            // Whitespace, escapes and key spacing a re-serialiser would normalise.
            var compact = Encoding.UTF8.GetString(DeskReplay.AnswerBody(DeskReplay.ChatFork));
            var odd = compact.Replace("{", "{ \n\t", StringComparison.Ordinal).Replace(",\"", " ,\r\n \"", StringComparison.Ordinal)
                .Replace("Keep", "\\u004beep", StringComparison.Ordinal) + "  \n";
            var bytes = Encoding.UTF8.GetBytes(odd);

            var reply = await factory.SendAsync("POST", DeskReplay.Target("answer", "gamecult-chat", null), bytes, token);

            Assert.Equal(200, reply.Status);
            Assert.Equal(bytes, Assert.Single(factory.Door.Calls).Body);
        }
    }

    [Fact]
    public async Task The_door_context_comes_from_the_account_and_the_configuration_never_the_request()
    {
        var (factory, _) = await ArrangeAsync();
        using (factory)
        {
            DeskReplay.HoldVectorForks(factory.Door);
            await factory.EnsureAccountAsync(DeskWebApplicationFactory.Steward, true);
            var token = factory.Token(DeskWebApplicationFactory.Steward.ToUpperInvariant(), "bifrost-desk", "desk.answer");

            // Query strings and headers that name a decider or an answerer change nothing.
            var reply = await factory.SendAsync(
                "POST", DeskReplay.Target("answer", "nibu", null) + "?deciders=" + DeskWebApplicationFactory.Member + "&answerer=x",
                DeskReplay.AnswerBody("nibu:question:glow-order"), token,
                configure: http => http.Request.Headers["X-Deciders"] = DeskWebApplicationFactory.Member);

            Assert.Equal(200, reply.Status);
            var call = Assert.Single(factory.Door.Calls);
            Assert.Equal(DeskWebApplicationFactory.Steward, call.Context.Subject);
            Assert.Equal("Steward", call.Context.Authority);
            Assert.Equal("nibu", call.Context.Mind);
            Assert.Equal(DeskWebApplicationFactory.DecidersOf("nibu"), call.Context.Deciders);
        }
    }

    [Fact]
    public async Task Body_is_judged_after_membership_and_before_the_mind()
    {
        var (factory, token) = await ArrangeAsync();
        using (factory)
        {
            // A bad body for an unknown Mind is the body's refusal; a non-Active member's bad body is forbidden.
            var bad = Encoding.UTF8.GetBytes("{\"answerer\":\"x\"}");
            var reply = await factory.SendAsync("POST", DeskReplay.Target("answer", "ghost", null), bad, token);
            Assert.Equal("body_forbidden_field", (string?)JsonNode.Parse(reply.Body)!["code"]);

            await factory.EnsureAccountAsync(DeskWebApplicationFactory.Alice, false);
            var forbidden = await factory.SendAsync("POST", DeskReplay.Target("answer", "ghost", null), bad, token);
            Assert.Equal("forbidden", (string?)JsonNode.Parse(forbidden.Body)!["code"]);
            Assert.Empty(factory.Door.Calls);
        }
    }

    [Fact]
    public async Task Membership_is_re_read_on_every_request()
    {
        var (factory, token) = await ArrangeAsync();
        using (factory)
        {
            DeskReplay.HoldVectorForks(factory.Door);
            var target = DeskReplay.Target("answer", "gamecult-chat", null);
            Assert.Equal(200, (await factory.SendAsync("POST", target, DeskReplay.AnswerBody(DeskReplay.ChatFork), token)).Status);
            await factory.EnsureAccountAsync(DeskWebApplicationFactory.Alice, false);
            Assert.Equal(403, (await factory.SendAsync("POST", target, DeskReplay.AnswerBody(DeskReplay.ChatFork), token)).Status);
        }
    }

    [Fact]
    public async Task An_oversized_body_is_refused_before_it_is_read()
    {
        var (factory, token) = await ArrangeAsync();
        using (factory)
        {
            var big = Encoding.UTF8.GetBytes("{\"x\":\"" + new string('a', 17 * 1024) + "\"}");
            var reply = await factory.SendAsync("POST", DeskReplay.Target("answer", "gamecult-chat", null), big, token);
            Assert.Equal("body_invalid", (string?)JsonNode.Parse(reply.Body)!["code"]);

            // Chunked: no Content-Length to judge, so the cap applies while reading.
            var chunked = await factory.SendAsync(
                "POST", DeskReplay.Target("answer", "gamecult-chat", null), big, token,
                configure: http => http.Request.ContentLength = null);
            Assert.Equal("body_invalid", (string?)JsonNode.Parse(chunked.Body)!["code"]);
            Assert.Empty(factory.Door.Calls);
        }
    }

    [Fact]
    public async Task A_door_that_sends_a_code_the_contract_does_not_name_is_unavailable()
    {
        var (factory, token) = await ArrangeAsync();
        using (factory)
        {
            foreach (var script in new Func<DeskAnswerOutcome>[]
                     {
                         () => new DeskAnswerOutcome.NotStored("something_internal"),
                         () => new DeskAnswerOutcome.NotStored("forbidden"),
                         () => new DeskAnswerOutcome.Answered(Stale("x") with { Answer = Stale("x").Answer with { Code = "made_up" } }),
                     })
            {
                factory.Door.AnswerScript = (_, _, _) => script();
                var reply = await factory.SendAsync("POST", DeskReplay.Target("answer", "gamecult-chat", null), DeskReplay.AnswerBody(DeskReplay.ChatFork), token);
                Assert.Equal(503, reply.Status);
            }
        }
    }
}
