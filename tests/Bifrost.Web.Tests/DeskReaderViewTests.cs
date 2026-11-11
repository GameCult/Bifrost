using System.Text.Json;
using System.Text.Json.Nodes;
using Bifrost.Web.Features.Desk;
using Bifrost.Web.Tests.Support;

namespace Bifrost.Web.Tests;

// CultLib d3025d3c's reader views (append-only on 3a51bbab), as Bifrost meets them: the 18-key card, DE0012 (raised_at, decider_count) stays opaque,
// the record is typed with answerer_count, and answerer is nil in a non-decider's view. Entries, records, list pages and
// stored answers are replayed from their wire bytes through the real door in DeskVectorWireTests.
public sealed class DeskReaderViewTests
{
    private static readonly string[] CardKeys =
    [
        "schemaVersion", "fork_id", "title", "context", "options", "recommendation", "depends", "leans", "flag", "raised_in",
        "raised_at", "deciders", "decider_count", "status", "ruling", "ruled_from", "order", "options_hash",
    ];

    [Fact]
    public void Valid_cards_are_the_17_key_card_with_both_views_and_pass_through_unchanged()
    {
        var rows = DeskVectors.Rows("card", "valid").ToList();
        Assert.Equal(7, rows.Count);
        foreach (var row in rows)
        {
            var message = row["message"]!.AsObject();
            Assert.Equal(CardKeys, message.Select(p => p.Key));
            Assert.StartsWith("DE0012", (string)row["messagePackHex"]!, StringComparison.Ordinal);
            Assert.True(message["raised_at"] is null || message["raised_at"]!.GetValueKind() == JsonValueKind.String);
            var count = (int)message["decider_count"]!;
            Assert.InRange(count, 1, 64);
            var deciders = message["deciders"]!.AsArray();
            Assert.True(deciders.Count == 0 || deciders.Count == count, (string)row["name"]!);

            var card = message.Deserialize<JsonElement>(DeskJson.Options);
            Assert.True(DeskVectors.SameShape(message, JsonSerializer.SerializeToNode(card, DeskJson.Options)), (string)row["name"]!);
        }

        Assert.Contains(rows, r => r["message"]!["raised_at"] is null);
        Assert.Contains(rows, r => r["message"]!["deciders"]!.AsArray().Count == 0 && (string)r["name"]! == "withheld_deciders");
    }

    [Fact]
    public void The_options_hash_vectors_are_the_15_and_Bifrost_computes_no_hash()
    {
        Assert.Equal(15, DeskVectors.Array("optionsHash").Count);
        Assert.Equal(9, DeskVectors.Array("bodyDigests").Count);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Bifrost.Web", "Features", "Desk")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        foreach (var file in Directory.GetFiles(Path.Combine(directory.FullName, "src", "Bifrost.Web", "Features", "Desk"), "*.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("System.Security.Cryptography", text, StringComparison.Ordinal);
            Assert.DoesNotContain("SHA256", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Stored_answers_round_trip_and_every_variant_carries_a_nil_answerer_with_the_key_written()
    {
        var rows = DeskVectors.Rows("deskAnswer", "valid").ToList();
        Assert.Equal(8, rows.Count);
        Assert.Contains(rows, r => (string)r["name"]! == "withheld_answerer");
        Assert.Contains(rows, r => (string)r["name"]! == "withheld_answerer_revision" && (string)r["message"]!["outcome"]! == "Revision");
        Assert.Contains(rows, r => (string)r["name"]! == "withheld_answerer_retraction" && (string)r["message"]!["outcome"]! == "Retraction");
        var outcomes = new HashSet<string>();
        foreach (var row in rows)
        {
            var message = row["message"]!.AsObject();
            Assert.StartsWith("DE0010", (string)row["messagePackHex"]!, StringComparison.Ordinal);
            outcomes.Add((string)message["outcome"]!);
            Assert.True(DeskVectors.SameShape(message, JsonSerializer.SerializeToNode(message.Deserialize<DeskAnswerDocument>(DeskJson.Options), DeskJson.Options)), (string)row["name"]!);

            var withheld = JsonNode.Parse(message.ToJsonString())!.AsObject();
            withheld["answerer"] = null;
            var back = JsonSerializer.SerializeToNode(withheld.Deserialize<DeskAnswerDocument>(DeskJson.Options), DeskJson.Options)!.AsObject();
            Assert.True(back.ContainsKey("answerer"));
            Assert.Null(back["answerer"]);
            Assert.True(DeskVectors.SameShape(withheld, back), (string)row["name"]!);
        }

        Assert.Superset(new HashSet<string> { "Counted", "Revision", "Retraction", "Refused" }, outcomes);
    }
}
