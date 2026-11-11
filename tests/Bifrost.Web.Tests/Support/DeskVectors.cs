using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bifrost.Web.Tests.Support;

// CultLib's fork-card vectors, vendored at commit f6c9bf5f975177d2033578e9b51b4d21930b728a.
public static class DeskVectors
{
    public const string SourceCommit = "f6c9bf5f975177d2033578e9b51b4d21930b728a";

    public const string FileSha256 = "cc15fc9e36e9c1f7d77b42279dc0f9a72c392adb251cc6ae3a59b79b7a004619";

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
