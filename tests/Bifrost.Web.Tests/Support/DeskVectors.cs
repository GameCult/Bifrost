using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bifrost.Web.Tests.Support;

// CultLib's fork-card vectors, vendored at commit d3025d3c19674803ad32eb2335461b8cbc22bd28 (#118: append-only on 3a51bbab,
// card DE0012 and record 86 unchanged).
public static class DeskVectors
{
    public const string SourceCommit = "d3025d3c19674803ad32eb2335461b8cbc22bd28";

    public const string FileSha256 = "c2b215a81e6708855b2424f8383f1dff0f3877b7fc9264102ca61d302b00c24c";

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

    // The vectors' refusal `error` text is illustrative (Libby): the contract pins the code, the key set, the status
    // (asserted by the caller) and that the text names the fork id, and its order on a list.
    public static bool SameRefusal(JsonNode? actual, JsonNode? vector, string forkId, int? order)
    {
        if (actual is not JsonObject a || vector is not JsonObject v ||
            !a.Select(p => p.Key).SequenceEqual(v.Select(p => p.Key)) ||
            (string?)a["schemaVersion"] != (string?)v["schemaVersion"] || (string?)a["code"] != (string?)v["code"])
        {
            return false;
        }

        var error = (string?)a["error"] ?? string.Empty;
        return error.Contains(forkId, StringComparison.Ordinal) &&
               (order is null || error.Contains($"order {order}", StringComparison.Ordinal));
    }

    public static byte[] Bytes(JsonNode node) => System.Text.Encoding.UTF8.GetBytes(node.ToJsonString());
}
