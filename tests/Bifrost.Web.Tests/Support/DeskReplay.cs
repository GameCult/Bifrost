using System.Text.Json;
using System.Text.Json.Nodes;
using Bifrost.Web.Features.Desk;

namespace Bifrost.Web.Tests.Support;

// Shared plumbing for replaying CultLib's transport vectors through HTTP.
public static class DeskReplay
{
    public const string ChatFork = "gamecult-chat:question:slot-mark-consent";

    private static JsonNode BaseCard => DeskVectors.Root["scenarios"]![0]!["card"]!;

    public static JsonElement Card(string forkId, string title = "Slot mark consent")
    {
        var card = JsonNode.Parse(BaseCard.ToJsonString())!.AsObject();
        card["fork_id"] = forkId;
        card["title"] = title;
        return card.Deserialize<JsonElement>(DeskJson.Options);
    }

    public static ForkEntry Entry(JsonElement card) => new(
        "gamecult.fork.entry.v0",
        card,
        new ForkRecord("gamecult.fork.record.v0", card.GetProperty("fork_id").GetString()!, card.GetProperty("options_hash").GetString()!, null, []));

    // Every fork the vectors say each Mind holds, on that Mind's socket.
    public static void HoldVectorForks(FakeDeskDoor door)
    {
        var forks = DeskVectors.Root["transport"]!["mindRoutes"]!["forks"]!.AsObject();
        foreach (var (mind, ids) in forks)
        {
            foreach (var id in ids!.AsArray())
            {
                door.Forks[(DeskWebApplicationFactory.SocketOf(mind), (string)id!)] = Entry(Card((string)id!));
            }
        }
    }

    public static byte[] AnswerBody(string forkId, string requestId = "00000001-0000-4000-8000-000000000001")
    {
        var body = JsonNode.Parse(DeskVectors.Root["scenarios"]![0]!["steps"]![0]!["body"]!.ToJsonString())!.AsObject();
        body["fork_id"] = forkId;
        body["request_id"] = requestId;
        return DeskVectors.Bytes(body);
    }

    // The raw segment as it would travel in one path segment: a literal '/' cannot, so it is sent as %2F.
    public static string SegmentOnWire(string rawMind) => rawMind.Replace("/", "%2F", StringComparison.Ordinal);

    public static string Target(string route, string rawMind, string? forkId) => route switch
    {
        "answer" => $"/api/desk/{SegmentOnWire(rawMind)}/answers",
        "list" => $"/api/desk/{SegmentOnWire(rawMind)}/forks",
        "read" => $"/api/desk/{SegmentOnWire(rawMind)}/forks/{Uri.EscapeDataString(forkId!)}",
        _ => throw new ArgumentOutOfRangeException(nameof(route)),
    };

    // Seeds the account the vector token stands for and returns the header value (null for no token).
    public static async Task<string?> TokenAsync(DeskWebApplicationFactory factory, JsonNode? token)
    {
        if (token is null)
        {
            return null;
        }

        var active = (bool)token["active"]!;
        var subject = active ? DeskWebApplicationFactory.Alice : DeskWebApplicationFactory.Member;
        await factory.EnsureAccountAsync(subject, active);
        return factory.Token(subject, (string)token["audience"]!, token["scopes"]!.AsArray().Select(s => (string)s!).ToArray());
    }

    public static bool IsEdgeRefusal(string code) =>
        code is "unauthenticated" or "insufficient_scope" or "forbidden" or "body_forbidden_field" or "body_unknown_field" or "body_invalid";
}
