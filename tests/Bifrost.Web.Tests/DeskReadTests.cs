using System.Text.Json;
using System.Text.Json.Nodes;
using Bifrost.Web.Features.Desk;
using Bifrost.Web.Tests.Support;

namespace Bifrost.Web.Tests;

public sealed class DeskReadTests
{
    private static string ReadToken(DeskWebApplicationFactory f) =>
        f.Token(DeskWebApplicationFactory.Alice, "bifrost-desk", "desk.read");

    private static Uri Unused => new("http://unused");

    // A list_request invalid row names the query that cannot express it; the others are the shapes a query cannot hold.
    private static readonly Dictionary<string, string> InvalidListQueries = new()
    {
        ["limit_51"] = "?limit=51",
        ["limit_negative"] = "?limit=-1",
        ["status_all"] = "?status=all",
        ["cursor_malformed"] = "?cursor=next",
        ["cursor_zero"] = "?cursor=c0",
        ["unknown_key"] = "?sort=oldest",
    };

    [Fact]
    public async Task List_and_read_request_invalid_vectors()
    {
        using var factory = new DeskWebApplicationFactory();
        DeskReplay.HoldVectorForks(factory.Door);
        await factory.EnsureAccountAsync(DeskWebApplicationFactory.Alice, true);
        var token = ReadToken(factory);

        var invalid = DeskVectors.Rows("transport", "listRequest", "invalid").Select(r => (string)r["name"]!).ToList();
        foreach (var name in invalid.Where(n => n != "mind_empty"))
        {
            var reply = await factory.SendAsync("GET", "/api/desk/gamecult-chat/forks" + InvalidListQueries[name], null, token);
            Assert.True(reply.Status == 400, $"{name}: {reply.Status}");
            Assert.Equal("request_invalid", (string?)JsonNode.Parse(reply.Body)!["code"]);
        }

        // Also: repeated key, bad escape, query on a read, empty fork id.
        foreach (var target in new[]
                 {
                     "/api/desk/gamecult-chat/forks?limit=1&limit=2", "/api/desk/gamecult-chat/forks?limit=%zz", "/api/desk/gamecult-chat/forks?limit=",
                     "/api/desk/gamecult-chat/forks?limit=+1", "/api/desk/gamecult-chat/forks?limit=1.0",
                     "/api/desk/gamecult-chat/forks/gamecult-chat%3Aquestion%3Aq1?x=1", "/api/desk/gamecult-chat/forks/", "/api/desk/gamecult-chat/forks/%ff",
                 })
        {
            var reply = await factory.SendAsync("GET", target, null, token);
            Assert.True(reply.Status == 400, $"{target}: {reply.Status}");
        }

        // The invalid request is judged before the Mind: an unknown Mind with a bad query is the query's refusal.
        var order = await factory.SendAsync("GET", "/api/desk/ghost/forks?limit=99", null, token);
        Assert.Equal("request_invalid", (string?)JsonNode.Parse(order.Body)!["code"]);
        Assert.Empty(factory.Door.Calls);
    }

    [Fact]
    public async Task Valid_list_queries_reach_the_door_as_typed_requests()
    {
        using var factory = new DeskWebApplicationFactory();
        await factory.EnsureAccountAsync(DeskWebApplicationFactory.Alice, true);

        foreach (var row in DeskVectors.Rows("transport", "listRequest", "valid"))
        {
            var m = row["message"]!;
            var query = $"?status={(string?)m["status"]}&cursor={(string?)m["cursor"]}&limit={(int)m["limit"]!}";
            var reply = await factory.SendAsync("GET", "/api/desk/gamecult-chat/forks" + query, null, ReadToken(factory));
            Assert.Equal(200, reply.Status);
            var call = factory.Door.Calls[^1];
            Assert.Equal(new DeskListRequest("gamecult-chat", (string?)m["status"], (string?)m["cursor"], (int)m["limit"]!), call.List);
        }

        var bare = await factory.SendAsync("GET", "/api/desk/nibu/forks", null, ReadToken(factory));
        Assert.Equal(new DeskListRequest("nibu", null, null, 50), factory.Door.Calls[^1].List);
        Assert.Equal(200, bare.Status);
    }

    [Fact]
    public async Task List_scenarios_pass_through()
    {
        var steps = 0;
        foreach (var scenario in DeskVectors.Rows("transport", "listScenarios"))
        {
            using var factory = new DeskWebApplicationFactory();
            await factory.EnsureAccountAsync(DeskWebApplicationFactory.Alice, true);
            foreach (var step in scenario["steps"]!.AsArray().Select(s => s!.AsObject()))
            {
                var request = step["request"]!;
                var response = step["response"]!.AsObject();
                var list = response.Deserialize<ForkList>(DeskJson.Options)!;
                factory.Door.ListScript = (_, _) => new DeskListOutcome.Listed(list);
                var query = $"?status={(string?)request["status"]}&cursor={(string?)request["cursor"]}&limit={(int)request["limit"]!}";

                var reply = await factory.SendAsync("GET", $"/api/desk/{(string)request["mind"]!}/forks" + query, null, ReadToken(factory));

                Assert.Equal(200, reply.Status);
                Assert.True(DeskVectors.SameShape(JsonNode.Parse(reply.Body), response), (string)scenario["name"]!);
                steps++;
            }
        }

        Assert.Equal(7, steps);
    }

    [Fact]
    public async Task Read_returns_the_entry_unchanged_and_an_unknown_fork_is_refused()
    {
        using var factory = new DeskWebApplicationFactory();
        DeskReplay.HoldVectorForks(factory.Door);
        await factory.EnsureAccountAsync(DeskWebApplicationFactory.Alice, true);

        var ok = await factory.SendAsync("GET", DeskReplay.Target("read", "gamecult-chat", DeskReplay.ChatFork), null, ReadToken(factory));
        var entry = JsonSerializer.SerializeToNode(DeskReplay.Entry(DeskReplay.Card(DeskReplay.ChatFork)), DeskJson.Options);
        var missing = await factory.SendAsync("GET", DeskReplay.Target("read", "gamecult-chat", "gamecult-chat:question:ghost"), null, ReadToken(factory));

        Assert.Equal(200, ok.Status);
        Assert.True(DeskVectors.SameShape(JsonNode.Parse(ok.Body), entry));
        Assert.Equal(DeskReplay.ChatFork, Assert.IsType<string>(factory.Door.Calls[0].ForkId));
        Assert.Equal(404, missing.Status);
    }

    [Fact]
    public async Task Door_failure_on_list_and_read_is_unavailable()
    {
        using var factory = new DeskWebApplicationFactory();
        await factory.EnsureAccountAsync(DeskWebApplicationFactory.Alice, true);
        factory.Door.Down = true;

        foreach (var target in new[] { "/api/desk/nibu/forks", DeskReplay.Target("read", "nibu", "nibu:question:glow-order") })
        {
            var reply = await factory.SendAsync("GET", target, null, ReadToken(factory));
            Assert.Equal(503, reply.Status);
            Assert.Equal(30, (int?)JsonNode.Parse(reply.Body)!["retryAfterSeconds"]);
        }
    }

    [Fact]
    public async Task Every_door_call_carries_the_configured_deciders_and_nothing_in_the_request_can_change_them()
    {
        using var factory = new DeskWebApplicationFactory();
        DeskReplay.HoldVectorForks(factory.Door);
        await factory.EnsureAccountAsync(DeskWebApplicationFactory.Alice, true);
        var hostile = "deciders=" + DeskWebApplicationFactory.Member;
        Action<Microsoft.AspNetCore.Http.HttpContext> headers = http =>
        {
            http.Request.Headers["X-Deciders"] = DeskWebApplicationFactory.Member;
            http.Request.Headers["Deciders"] = DeskWebApplicationFactory.Member;
        };
        var read = ReadToken(factory);
        var answer = factory.Token(DeskWebApplicationFactory.Alice, "bifrost-desk", "desk.answer");

        await factory.SendAsync("GET", "/api/desk/nibu/forks", null, read, configure: headers);
        var hostileList = await factory.SendAsync("GET", "/api/desk/nibu/forks?" + hostile, null, read, configure: headers);
        Assert.Equal(400, hostileList.Status);
        await factory.SendAsync("GET", DeskReplay.Target("read", "nibu", "nibu:question:glow-order"), null, read, configure: headers);
        await factory.SendAsync("POST", DeskReplay.Target("answer", "nibu", null) + "?" + hostile, DeskReplay.AnswerBody("nibu:question:glow-order"), answer, configure: headers);
        // A body naming deciders never reaches the door.
        var body = JsonNode.Parse(DeskReplay.AnswerBody("nibu:question:glow-order"))!.AsObject();
        body["deciders"] = new JsonArray(DeskWebApplicationFactory.Member);
        var named = await factory.SendAsync("POST", DeskReplay.Target("answer", "nibu", null), DeskVectors.Bytes(body), answer);

        Assert.Equal("body_unknown_field", (string?)JsonNode.Parse(named.Body)!["code"]);
        Assert.Equal(["list", "read", "answer"], factory.Door.Calls.Select(c => c.Kind).ToArray());
        Assert.All(factory.Door.Calls, c => Assert.Equal([DeskWebApplicationFactory.Alice, DeskWebApplicationFactory.Steward], c.Context.Deciders));
    }
}
