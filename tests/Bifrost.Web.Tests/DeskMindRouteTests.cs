using System.Text.Json;
using System.Text.Json.Nodes;
using Bifrost.Web.Features.Desk;
using Bifrost.Web.Tests.Support;

namespace Bifrost.Web.Tests;

public sealed class DeskMindRouteTests
{
    private static async Task<(DeskReply Reply, DeskWebApplicationFactory Factory)> RunAsync(JsonObject row)
    {
        var factory = new DeskWebApplicationFactory();
        DeskReplay.HoldVectorForks(factory.Door);
        var token = await DeskReplay.TokenAsync(factory, row["token"]);
        var route = (string)row["route"]!;
        var forkId = (string?)row["fork_id"];
        var reply = await factory.SendAsync(
            route == "answer" ? "POST" : "GET",
            DeskReplay.Target(route, (string)row["mind"]!, forkId),
            route == "answer" ? DeskReplay.AnswerBody(forkId!) : null,
            token);
        return (reply, factory);
    }

    [Fact]
    public async Task Vector_route_cases()
    {
        var cases = DeskVectors.Rows("transport", "mindRoutes", "cases").ToList();
        Assert.Equal(57, cases.Count);

        foreach (var row in cases)
        {
            var name = (string)row["name"]!;
            var (reply, factory) = await RunAsync(row);
            using (factory)
            {
                Assert.True((int)row["httpStatus"]! == reply.Status, $"{name}: {reply.Status} {reply.Text}");
                var expect = (string)row["expect"]!;
                if (expect != "ok")
                {
                    Assert.True(
                        DeskVectors.SameShape(JsonNode.Parse(reply.Body), JsonNode.Parse(JsonSerializer.Serialize(DeskRefusals.Document(expect), DeskJson.Options))),
                        name);
                }

                var opened = (string?)row["opened"];
                if (opened is null)
                {
                    Assert.True(factory.Door.Calls.Count == 0, $"{name}: opened {factory.Door.Calls.FirstOrDefault()?.Socket}");
                }
                else
                {
                    var call = Assert.Single(factory.Door.Calls);
                    Assert.True(call.Socket.EndsWith("/" + opened, StringComparison.Ordinal), $"{name}: {call.Socket}");
                }

                var storedIn = (string?)row["stored_in"];
                if (storedIn is null)
                {
                    Assert.Empty(factory.Door.StoredIn);
                }
                else
                {
                    Assert.Equal([DeskWebApplicationFactory.SocketOf(storedIn)], factory.Door.StoredIn);
                }
            }
        }
    }

    [Fact]
    public async Task Same_response_pairs_are_byte_equal()
    {
        var cases = DeskVectors.Rows("transport", "mindRoutes", "cases").ToDictionary(r => (string)r["name"]!);
        var pairs = DeskVectors.Array("transport", "mindRoutes", "sameResponse");
        Assert.Equal(4, pairs.Count);

        foreach (var pair in pairs)
        {
            var first = cases[(string)pair![0]!];
            var second = cases[(string)pair[1]!];
            var (a, fa) = await RunAsync(first);
            var (b, fb) = await RunAsync(second);
            using (fa)
            using (fb)
            {
                Assert.Equal(a.Status, b.Status);
                Assert.Equal(a.Body, b.Body);
            }
        }
    }
}
