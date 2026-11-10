using System.Text.Json;
using System.Text.Json.Nodes;
using Bifrost.Web.Features.Desk;
using Bifrost.Web.Tests.Support;

namespace Bifrost.Web.Tests;

public sealed class DeskScenarioTests
{
    private static readonly string Socket = DeskWebApplicationFactory.SocketOf("gamecult-chat");

    private static readonly string[] ConfiguredDeciders = [DeskWebApplicationFactory.Alice, DeskWebApplicationFactory.Steward];

    // Replays every answer step of every scenario. The door is scripted from the vector's response; what Bifrost
    // must produce by itself (edge refusals, unavailable, the attached card) is compared with the vector.
    private sealed record Counts(int Answers, int Edge, int Unavailable, int Skipped);

    private static async Task<Counts> ReplayAsync(Action<string, DeskReply, DeskWebApplicationFactory, JsonObject>? observe = null)
    {
        int answers = 0, edge = 0, unavailable = 0, skipped = 0;
        foreach (var scenario in DeskVectors.Rows("scenarios"))
        {
            using var factory = new DeskWebApplicationFactory();
            var door = factory.Door;
            door.Forks[(Socket, DeskReplay.ChatFork)] = DeskReplay.Entry(scenario["card"]!.Deserialize<ForkCard>(DeskJson.Options)!);
            var scenarioName = (string)scenario["name"]!;

            foreach (var step in scenario["steps"]!.AsArray().Select(s => s!.AsObject()))
            {
                var kind = (string)step["kind"]!;
                if (kind == "matrix_event")
                {
                    skipped++; // no Matrix ingress exists, so there is no route a Matrix event could take
                    continue;
                }

                if (kind == "card")
                {
                    door.Forks[(Socket, DeskReplay.ChatFork)] = DeskReplay.Entry(step["card"]!.Deserialize<ForkCard>(DeskJson.Options)!);
                    continue;
                }

                var server = step["server"]!.AsObject();
                var response = step["response"]!.AsObject();
                var code = (string?)response["code"];
                var answerer = (string?)server["answerer"];
                var active = (bool?)server["active"] ?? true;

                door.Calls.Clear();
                door.StoredIn.Clear();
                door.Down = false;
                door.ThrowAfterStore = false;
                door.AnswerScript = null;
                if (answerer is not null)
                {
                    await factory.EnsureAccountAsync(answerer, active);
                }

                var isDown = (bool?)server["door_down"] == true;
                var stored = (bool?)server["stored"] == true;
                if (isDown)
                {
                    door.Down = !stored;
                    door.ThrowAfterStore = stored;
                }

                if (code is null)
                {
                    var wire = response.Deserialize<AnswerResult>(DeskJson.Options)!;
                    // Huginn answers without the card; Bifrost attaches the current one for stale_fork.
                    door.AnswerScript = (_, _, _) => new DeskAnswerOutcome.Answered(wire with { Card = null });
                }
                else if (code is "request_id_conflict" or "fork_unknown")
                {
                    door.AnswerScript = (_, _, _) => new DeskAnswerOutcome.NotStored(code);
                }

                var body = DeskVectors.Bytes(step["body"]!);
                var token = answerer is null
                    ? null
                    : factory.Token(answerer, "bifrost-desk", "desk.answer");
                var reply = await factory.SendAsync("POST", DeskReplay.Target("answer", "gamecult-chat", null), body, token);

                var label = $"{scenarioName}";
                if (code == "unavailable")
                {
                    unavailable++;
                    Assert.True(503 == reply.Status, $"{label}: {reply.Status}");
                    Assert.Equal("30", reply.Headers.RetryAfter.ToString());
                    Assert.Equal(stored ? [Socket] : [], door.StoredIn);
                }
                else if (code is not null && DeskReplay.IsEdgeRefusal(code))
                {
                    edge++;
                    Assert.True(DeskRefusals.StatusOf(code) == reply.Status, $"{label}: {reply.Status}");
                    Assert.True(door.Calls.Count == 0, $"{label}: the door was called for an edge refusal");
                }
                else
                {
                    answers++;
                    var call = Assert.Single(door.Calls.Where(c => c.Kind == "answer"));
                    Assert.Equal(body, call.Body);
                    Assert.Equal(answerer, call.Context.Subject);
                    Assert.Equal((string)server["authority"]!, call.Context.Attribution);
                    Assert.Equal(ConfiguredDeciders, call.Context.Deciders);
                    Assert.Equal(Socket, call.Socket);
                }

                Assert.True(
                    DeskVectors.SameShape(JsonNode.Parse(reply.Body), response),
                    $"{label}: response differs\n{reply.Text}\n{response.ToJsonString()}");
                observe?.Invoke(scenarioName, reply, factory, step);
            }
        }

        return new Counts(answers, edge, unavailable, skipped);
    }

    [Fact]
    public async Task Every_scenario_answer_step_replays_through_http()
    {
        var counts = await ReplayAsync();

        Assert.Equal(3, counts.Skipped);
        Assert.Equal(2, counts.Unavailable);
        Assert.Equal(15, counts.Edge);
        Assert.Equal(100, counts.Answers);
    }

    [Fact]
    public async Task Edge_refusals_never_call_the_door()
    {
        var edges = 0;
        await ReplayAsync((_, reply, factory, step) =>
        {
            var code = (string?)step["response"]!["code"];
            if (code is not null && DeskReplay.IsEdgeRefusal(code))
            {
                edges++;
                Assert.Empty(factory.Door.Calls);
            }
        });
        Assert.Equal(15, edges);
    }

    [Fact]
    public async Task Answer_results_pass_through_unchanged()
    {
        var checkedResults = 0;
        foreach (var row in DeskVectors.Rows("results"))
        {
            using var factory = new DeskWebApplicationFactory();
            var message = row["message"]!.AsObject();
            var answer = message["answer"]!.Deserialize<DeskAnswerDocument>(DeskJson.Options)!;
            var result = message.Deserialize<AnswerResult>(DeskJson.Options)!;
            factory.Door.Forks[(Socket, answer.ForkId)] = DeskReplay.Entry(
                message["card"] is { } card ? card.Deserialize<ForkCard>(DeskJson.Options)! : DeskReplay.Card(answer.ForkId));
            factory.Door.AnswerScript = (_, _, _) => new DeskAnswerOutcome.Answered(result with { Card = null });
            await factory.EnsureAccountAsync(DeskWebApplicationFactory.Alice, true);

            var reply = await factory.SendAsync(
                "POST", DeskReplay.Target("answer", "gamecult-chat", null), DeskReplay.AnswerBody(answer.ForkId),
                factory.Token(DeskWebApplicationFactory.Alice, "bifrost-desk", "desk.answer"));

            Assert.True((int)row["httpStatus"]! == reply.Status, $"{row["name"]}: {reply.Status}");
            Assert.True(DeskVectors.SameShape(JsonNode.Parse(reply.Body), message), $"{row["name"]}");
            checkedResults++;
        }

        Assert.Equal(6, checkedResults);
    }

    [Fact]
    public async Task Unavailable_before_and_after_store()
    {
        // The scenarios hold both: door_down before the store, and door_down after it (the retry then replays).
        var seen = new List<(bool Stored, int Status)>();
        await ReplayAsync((_, reply, factory, step) =>
        {
            if ((string?)step["response"]!["code"] == "unavailable")
            {
                seen.Add(((bool?)step["server"]!["stored"] == true, reply.Status));
            }
        });
        Assert.Equal([(false, 503), (true, 503)], seen.OrderBy(s => s.Stored).ToArray());
    }
}
