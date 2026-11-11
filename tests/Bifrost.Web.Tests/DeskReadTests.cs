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

        Assert.Equal(9, steps);
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
    public async Task A_fork_too_large_for_a_page_is_a_422_that_names_the_fork_and_its_order()
    {
        using var factory = new DeskWebApplicationFactory();
        await factory.EnsureAccountAsync(DeskWebApplicationFactory.Alice, true);

        factory.Door.ListScript = (_, _) => new DeskListOutcome.ForkTooLarge("nibu:question:huge", 7);
        var reply = await factory.SendAsync("GET", "/api/desk/nibu/forks", null, ReadToken(factory));
        var doc = JsonNode.Parse(reply.Body)!;
        Assert.Equal(422, reply.Status);
        Assert.Equal("fork_too_large", (string?)doc["code"]);
        var error = (string)doc["error"]!;
        Assert.Contains("nibu:question:huge", error);
        Assert.Contains("order 7", error);

        // At order 0 the text still names the fork and its order: the client reads "below 0" as the end.
        factory.Door.ListScript = (_, _) => new DeskListOutcome.ForkTooLarge("nibu:question:first", 0);
        var end = await factory.SendAsync("GET", "/api/desk/nibu/forks", null, ReadToken(factory));
        var endError = (string)JsonNode.Parse(end.Body)!["error"]!;
        Assert.Equal(422, end.Status);
        Assert.Contains("nibu:question:first", endError);
        Assert.Contains("order 0", endError);
    }

    [Fact]
    public async Task fork_too_large_as_a_bare_door_code_is_unavailable_only_the_typed_outcomes_are_the_refusal()
    {
        using var factory = new DeskWebApplicationFactory();
        await factory.EnsureAccountAsync(DeskWebApplicationFactory.Alice, true);

        factory.Door.ReadScript = (_, _) => new DeskReadOutcome.NotFound("fork_too_large");
        factory.Door.ListScript = (_, _) => new DeskListOutcome.NotListed("fork_too_large");
        foreach (var target in new[] { "/api/desk/nibu/forks", DeskReplay.Target("read", "nibu", "nibu:question:glow-order") })
        {
            var reply = await factory.SendAsync("GET", target, null, ReadToken(factory));
            Assert.Equal(503, reply.Status);
        }
    }

    [Fact]
    public async Task A_read_of_an_oversize_fork_is_the_same_422_naming_the_fork_with_no_order_or_cursor()
    {
        using var factory = new DeskWebApplicationFactory();
        await factory.EnsureAccountAsync(DeskWebApplicationFactory.Alice, true);
        factory.Door.ReadScript = (_, id) => new DeskReadOutcome.ForkTooLarge(id);

        var reply = await factory.SendAsync("GET", DeskReplay.Target("read", "nibu", "nibu:question:huge"), null, ReadToken(factory));

        var doc = JsonNode.Parse(reply.Body)!;
        Assert.Equal(422, reply.Status);
        Assert.Equal("fork_too_large", (string?)doc["code"]);
        var error = (string)doc["error"]!;
        Assert.Contains("nibu:question:huge", error);
        Assert.DoesNotContain("order", error);
        Assert.DoesNotContain("cursor", error);
    }

    [Fact]
    public async Task List_short_pages_and_oversize_forks_follow_the_vectors()
    {
        var steps = 0;
        foreach (var scenario in DeskVectors.Rows("transport", "listShortPages"))
        {
            using var factory = new DeskWebApplicationFactory();
            await factory.EnsureAccountAsync(DeskWebApplicationFactory.Alice, true);
            foreach (var step in scenario["steps"]!.AsArray().Select(s => s!.AsObject()))
            {
                var request = step["request"]!;
                var name = (string)scenario["name"]!;
                if (step["refusal"] is { } refusal)
                {
                    factory.Door.ListScript = (_, _) => new DeskListOutcome.ForkTooLarge((string)step["fork_id"]!, (int)step["order"]!);
                }
                else
                {
                    var list = step["response"]!.Deserialize<ForkList>(DeskJson.Options)!;
                    factory.Door.ListScript = (_, _) => new DeskListOutcome.Listed(list);
                }

                var query = $"?status={(string?)request["status"]}&cursor={(string?)request["cursor"]}&limit={(int)request["limit"]!}";
                var reply = await factory.SendAsync("GET", $"/api/desk/{(string)request["mind"]!}/forks" + query, null, ReadToken(factory));

                Assert.Equal(step["refusal"] is null ? 200 : (int)step["httpStatus"]!, reply.Status);
                Assert.True(DeskVectors.SameShape(JsonNode.Parse(reply.Body), step["refusal"] ?? step["response"]), name);
                steps++;
            }
        }

        Assert.True(steps >= 4);
    }

    [Fact]
    public async Task A_read_past_the_cap_follows_the_vectors()
    {
        using var factory = new DeskWebApplicationFactory();
        await factory.EnsureAccountAsync(DeskWebApplicationFactory.Alice, true);

        foreach (var row in DeskVectors.Rows("transport", "readTooLarge"))
        {
            var forkId = (string)row["fork_id"]!;
            factory.Door.ReadScript = (_, id) => new DeskReadOutcome.ForkTooLarge(id);
            var reply = await factory.SendAsync("GET", DeskReplay.Target("read", (string)row["request"]!["mind"]!, forkId), null, ReadToken(factory));

            Assert.Equal((int)row["httpStatus"]!, reply.Status);
            Assert.True(DeskVectors.SameShape(JsonNode.Parse(reply.Body), row["refusal"]), (string)row["name"]!);
        }
    }

    [Fact]
    public async Task Door_faults_follow_the_vectors_per_route()
    {
        using var factory = new DeskWebApplicationFactory();
        await factory.EnsureAccountAsync(DeskWebApplicationFactory.Alice, true);

        foreach (var row in DeskVectors.Rows("transport", "doorFaults"))
        {
            var code = (string)row["doorCode"]!;
            string method = "GET", target;
            string? bearer = null;
            byte[]? body = null;
            switch ((string)row["route"]!)
            {
                case "read":
                    factory.Door.ReadScript = (_, _) => new DeskReadOutcome.NotFound(code);
                    target = DeskReplay.Target("read", "nibu", "nibu:question:glow-order");
                    bearer = ReadToken(factory);
                    break;
                case "list":
                    factory.Door.ListScript = (_, _) => new DeskListOutcome.NotListed(code);
                    target = "/api/desk/nibu/forks";
                    bearer = ReadToken(factory);
                    break;
                default:
                    method = "POST";
                    factory.Door.AnswerScript = (_, _, _) => new DeskAnswerOutcome.NotStored(code);
                    target = DeskReplay.Target("answer", "nibu", null);
                    body = DeskReplay.AnswerBody("nibu:question:glow-order");
                    bearer = factory.Token(DeskWebApplicationFactory.Alice, "bifrost-desk", "desk.answer");
                    break;
            }

            var reply = await factory.SendAsync(method, target, body, bearer);

            Assert.Equal((int)row["httpStatus"]!, reply.Status);
            Assert.True(DeskVectors.SameShape(JsonNode.Parse(reply.Body), row["response"]), (string)row["name"]!);
        }
    }

    [Fact]
    public async Task A_short_page_with_a_cursor_below_its_last_entry_passes_through_unchanged()
    {
        using var factory = new DeskWebApplicationFactory();
        await factory.EnsureAccountAsync(DeskWebApplicationFactory.Alice, true);
        factory.Door.ListScript = (_, r) => new DeskListOutcome.Listed(new ForkList("gamecult.fork.list.v0", r.Mind, "s1", 3, 0, [], "c2"));

        var reply = await factory.SendAsync("GET", "/api/desk/nibu/forks?limit=10", null, ReadToken(factory));

        Assert.Equal(200, reply.Status);
        Assert.Equal("c2", (string?)JsonNode.Parse(reply.Body)!["next_cursor"]);
    }

    [Fact]
    public async Task A_door_that_refuses_a_read_or_list_for_a_body_field_such_as_authority_is_request_invalid_not_unavailable()
    {
        using var factory = new DeskWebApplicationFactory();
        await factory.EnsureAccountAsync(DeskWebApplicationFactory.Alice, true);

        foreach (var code in new[] { "body_forbidden_field", "body_unknown_field", "body_invalid" })
        {
            factory.Door.ReadScript = (_, _) => new DeskReadOutcome.NotFound(code);
            factory.Door.ListScript = (_, _) => new DeskListOutcome.NotListed(code);
            foreach (var target in new[] { "/api/desk/nibu/forks", DeskReplay.Target("read", "nibu", "nibu:question:glow-order") })
            {
                var reply = await factory.SendAsync("GET", target, null, ReadToken(factory));
                Assert.Equal(400, reply.Status);
                Assert.Equal("request_invalid", (string?)JsonNode.Parse(reply.Body)!["code"]);
            }
        }
    }

    [Fact]
    public async Task request_id_conflict_is_the_answers_alone_on_read_and_list_it_is_a_door_fault()
    {
        using var factory = new DeskWebApplicationFactory();
        await factory.EnsureAccountAsync(DeskWebApplicationFactory.Alice, true);

        factory.Door.ReadScript = (_, _) => new DeskReadOutcome.NotFound(DeskRefusals.RequestIdConflict);
        factory.Door.ListScript = (_, _) => new DeskListOutcome.NotListed(DeskRefusals.RequestIdConflict);
        foreach (var target in new[] { "/api/desk/nibu/forks", DeskReplay.Target("read", "nibu", "nibu:question:glow-order") })
        {
            var reply = await factory.SendAsync("GET", target, null, ReadToken(factory));
            Assert.Equal(503, reply.Status);
            Assert.Equal("unavailable", (string?)JsonNode.Parse(reply.Body)!["code"]);
        }
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
        Assert.All(factory.Door.Calls, c => Assert.Equal(DeskWebApplicationFactory.DecidersOf("nibu"), c.Context.Deciders));
    }
    private const string Secret = "the decider's own words";

    private static DeskAnswerDocument Doc(string id, string outcome, string? of, string? retracts, string? note) => new(
        DeskSchemas.DeskAnswer, id, DeskReplay.ChatFork, outcome, of, retracts, null, "h", note == null ? null : "a", note,
        "2026-11-12T19:00:00Z", "2026-11-12T19:00:01Z", false, DeskWebApplicationFactory.Alice, "Steward", "r-" + id);

    // Counted, Revision, Retraction and Refused documents, every one carrying the decider's note.
    private static ForkEntry NotedEntry()
    {
        var entry = DeskReplay.Entry(DeskReplay.Card(DeskReplay.ChatFork));
        DeskAnswerDocument[] answers =
        [
            Doc("n1", "Counted", null, null, Secret),
            Doc("n2", "Revision", "n1", null, Secret + " 2"),
            Doc("n3", "Retraction", null, "n2", Secret + " 3"),
            Doc("n4", "Refused", null, null, Secret + " 4"),
        ];
        return entry with { Record = entry.Record with { CountedAnswerId = "n1", Answers = answers } };
    }

    private static bool AnyNote(JsonNode? node) => node switch
    {
        JsonObject o => o.Any(kv => (kv.Key == "note" && kv.Value is not null) || AnyNote(kv.Value)),
        JsonArray a => a.Any(AnyNote),
        _ => false,
    };

    private static async Task<(string Read, string List)> BodiesAsync(DeskWebApplicationFactory factory, string account)
    {
        var entry = NotedEntry();
        factory.Door.ReadScript = (_, _) => new DeskReadOutcome.Found(entry);
        factory.Door.ListScript = (_, r) => new DeskListOutcome.Listed(
            new ForkList("gamecult.fork.list.v0", r.Mind, "s1", 1, 0, [entry, entry], null));
        var token = factory.Token(account, "bifrost-desk", "desk.read");
        var read = await factory.SendAsync("GET", DeskReplay.Target("read", "gamecult-chat", DeskReplay.ChatFork), null, token);
        var list = await factory.SendAsync("GET", DeskReplay.Target("list", "gamecult-chat", null), null, token);
        Assert.Equal(200, read.Status);
        Assert.Equal(200, list.Status);
        return (System.Text.Encoding.UTF8.GetString(read.Body), System.Text.Encoding.UTF8.GetString(list.Body));
    }

    [Fact]
    public async Task A_non_decider_reads_and_lists_no_notes_anywhere_and_the_body_keeps_the_contract_shape()
    {
        using var factory = new DeskWebApplicationFactory();
        await factory.EnsureAccountAsync(DeskWebApplicationFactory.Member, true);
        Assert.DoesNotContain(DeskWebApplicationFactory.Member, DeskWebApplicationFactory.DecidersOf("gamecult-chat"));

        var (read, list) = await BodiesAsync(factory, DeskWebApplicationFactory.Member);

        Assert.DoesNotContain("decider's own words", read);
        Assert.DoesNotContain("decider's own words", list);
        Assert.False(AnyNote(JsonNode.Parse(read)));
        Assert.False(AnyNote(JsonNode.Parse(list)));
        // Outcomes and cards stay; the key stays written as nil, so the body is still the contract's shape.
        var expected = JsonSerializer.SerializeToNode(NotedEntry() with
        {
            Record = NotedEntry().Record with { Answers = [.. NotedEntry().Record.Answers.Select(a => a with { Note = null })] },
        }, DeskJson.Options);
        Assert.True(DeskVectors.SameShape(JsonNode.Parse(read), expected));
        var answers = JsonNode.Parse(read)!["record"]!["answers"]!.AsArray();
        Assert.Equal(new[] { "Counted", "Revision", "Retraction", "Refused" }, answers.Select(a => (string)a!["outcome"]!).ToArray());
        Assert.All(answers, a => Assert.True(a!.AsObject().ContainsKey("note")));
        Assert.Equal(2, JsonNode.Parse(list)!["forks"]!.AsArray().Count);
    }

    [Fact]
    public async Task A_decider_reads_and_lists_the_notes()
    {
        using var factory = new DeskWebApplicationFactory();
        await factory.EnsureAccountAsync(DeskWebApplicationFactory.Alice, true);

        var (read, list) = await BodiesAsync(factory, DeskWebApplicationFactory.Alice);

        Assert.Contains(Secret + " 3", read);
        Assert.Equal(8, JsonNode.Parse(list)!["forks"]!.AsArray().Sum(f => f!["record"]!["answers"]!.AsArray().Count(a => a!["note"] is not null)));
        Assert.True(DeskVectors.SameShape(JsonNode.Parse(read), JsonSerializer.SerializeToNode(NotedEntry(), DeskJson.Options)));
    }

    [Fact]
    public async Task Decider_status_is_per_mind_the_same_account_is_stripped_where_it_is_not_on_the_list()
    {
        using var factory = new DeskWebApplicationFactory();
        await factory.EnsureAccountAsync(DeskWebApplicationFactory.Member, true);
        Assert.Contains(DeskWebApplicationFactory.Member, DeskWebApplicationFactory.DecidersOf("nibu"));
        factory.Door.ListScript = (_, r) => new DeskListOutcome.Listed(
            new ForkList("gamecult.fork.list.v0", r.Mind, "s1", 1, 0, [NotedEntry()], null));
        var token = factory.Token(DeskWebApplicationFactory.Member, "bifrost-desk", "desk.read");

        var onNibu = await factory.SendAsync("GET", "/api/desk/nibu/forks", null, token);
        var onChat = await factory.SendAsync("GET", "/api/desk/gamecult-chat/forks", null, token);

        Assert.True(AnyNote(JsonNode.Parse(onNibu.Body)));
        Assert.False(AnyNote(JsonNode.Parse(onChat.Body)));
    }
}
