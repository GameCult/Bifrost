using System.Text.Json.Nodes;
using Bifrost.Web.Features.Desk;
using Bifrost.Web.Tests.Support;

namespace Bifrost.Web.Tests;

public sealed class DeskAuthCaseTests
{
    [Fact]
    public async Task Vector_auth_cases()
    {
        var cases = DeskVectors.Rows("transport", "authCases").ToList();
        Assert.Equal(22, cases.Count);

        foreach (var row in cases.Where(r => (string)r["route"]! == "answer"))
        {
            using var factory = new DeskWebApplicationFactory();
            DeskReplay.HoldVectorForks(factory.Door);
            factory.Door.Down = (bool)row["huginn_down"]!;
            var token = await DeskReplay.TokenAsync(factory, row["token"]);

            var reply = await factory.SendAsync(
                "POST", DeskReplay.Target("answer", "gamecult-chat", DeskReplay.ChatFork),
                DeskReplay.AnswerBody(DeskReplay.ChatFork), token);

            var name = (string)row["name"]!;
            var expect = (string)row["expect"]!;
            var body = JsonNode.Parse(reply.Body);
            switch (expect)
            {
                case "ok":
                    Assert.True(200 == reply.Status, $"{name}: {reply.Status} {reply.Text}");
                    Assert.Single(factory.Door.Calls);
                    break;
                case "unavailable":
                    Assert.True(503 == reply.Status, $"{name}: {reply.Status}");
                    Assert.True(DeskVectors.SameShape(body, JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(DeskUnavailables.Document(30), DeskJson.Options))), name);
                    break;
                default:
                    Assert.True(DeskRefusals.StatusOf(expect) == reply.Status, $"{name}: {reply.Status}");
                    Assert.True(DeskVectors.SameShape(body, JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(DeskRefusals.Document(expect), DeskJson.Options))), name);
                    // Auth, scope and membership are judged before Huginn is asked.
                    Assert.True(factory.Door.Calls.Count == 0, $"{name}: the door was called");
                    break;
            }
        }
    }
}
