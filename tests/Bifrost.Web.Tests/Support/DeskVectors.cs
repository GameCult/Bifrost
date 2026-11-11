using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bifrost.Web.Tests.Support;

// CultLib's fork-card vectors, vendored at commit 89755b08b5dd4bf72ed8e5907b5ff4717e51da12.
public static class DeskVectors
{
    public const string SourceCommit = "89755b08b5dd4bf72ed8e5907b5ff4717e51da12";

    public const string FileSha256 = "3387debd16059621202b706761519231e7ad52d0d4bd071a32b80c0d916e42fa";

    public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "fork-card-vectors.json");

    public static JsonNode Root { get; } = JsonNode.Parse(File.ReadAllBytes(Path))!;

    public static JsonArray Array(params string[] path)
    {
        JsonNode node = Root;
        foreach (var step in path)
        {
            node = node[step]!;
        }

        return node.AsArray();
    }

    public static IEnumerable<JsonObject> Rows(params string[] path) => Array(path).Select(row => row!.AsObject());

    // True when both are the same JSON with the same keys in the same order.
    public static bool SameShape(JsonNode? left, JsonNode? right)
    {
        switch (left, right)
        {
            case (null, null):
                return true;
            case (JsonObject a, JsonObject b):
                return a.Select(p => p.Key).SequenceEqual(b.Select(p => p.Key)) &&
                       a.All(p => SameShape(p.Value, b[p.Key]));
            case (JsonArray a, JsonArray b):
                return a.Count == b.Count && a.Zip(b).All(pair => SameShape(pair.First, pair.Second));
            case (JsonValue, JsonValue):
                return JsonNode.DeepEquals(left, right);
            default:
                return false;
        }
    }

    public static byte[] Bytes(JsonNode node) => System.Text.Encoding.UTF8.GetBytes(node.ToJsonString());
}
