using System.Text.Json;
using System.Text.Json.Nodes;
using Bifrost.Web.Features.Desk;
using Bifrost.Web.Tests.Support;

namespace Bifrost.Web.Tests;

public sealed class DeskDocumentTests
{
    private static JsonNode? Write<T>(T document) => JsonSerializer.SerializeToNode(document, DeskJson.Options);

    [Fact]
    public void Refusal_and_unavailable_documents_match_vectors()
    {
        var rows = DeskVectors.Rows("doorRefusals").ToList();
        Assert.Equal(10, rows.Count);
        foreach (var row in rows)
        {
            var code = (string)row["message"]!["code"]!;
            if (code == DeskRefusals.ForkTooLarge)
            {
                // It names its fork, so it is built, not looked up; the row is the list form at order 3.
                Assert.True(DeskVectors.SameShape(row["message"], Write(DeskRefusals.ForkTooLargeDocument("gamecult-chat:question:q3", 3))), $"refusal {code}");
                Assert.Equal((int)row["httpStatus"]!, DeskRefusals.ForkTooLargeStatus);
                continue;
            }

            Assert.True(DeskVectors.SameShape(row["message"], Write(DeskRefusals.Document(code))), $"refusal {code}");
            Assert.Equal((int)row["httpStatus"]!, DeskRefusals.StatusOf(code));
        }

        var unavailable = DeskVectors.Rows("unavailable").ToList();
        Assert.Equal(2, unavailable.Count);
        foreach (var row in unavailable)
        {
            var retry = (int?)row["message"]!["retryAfterSeconds"];
            Assert.True(DeskVectors.SameShape(row["message"], Write(DeskUnavailables.Document(retry))), (string)row["name"]!);
            Assert.Equal(DeskUnavailables.Status, (int)row["httpStatus"]!);
        }
    }

    [Fact]
    public void Every_refusal_code_in_the_schema_has_a_document()
    {
        var codes = DeskVectors.Rows("doorRefusals").Select(row => (string)row["message"]!["code"]!).ToHashSet();
        var written = typeof(DeskRefusals).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToHashSet();
        Assert.Equal(codes, written);
    }

    [Fact]
    public void Answer_results_round_trip_with_their_spelling()
    {
        var rows = DeskVectors.Rows("results").ToList();
        Assert.Equal(6, rows.Count);
        foreach (var row in rows)
        {
            var message = row["message"]!;
            var result = message.Deserialize<AnswerResult>(DeskJson.Options)!;
            Assert.True(DeskVectors.SameShape(message, Write(result)), (string)row["name"]!);
        }
    }

    [Fact]
    public void Cards_round_trip_with_their_spelling()
    {
        var rows = DeskVectors.Rows("card", "valid").ToList();
        Assert.Equal(5, rows.Count);
        foreach (var row in rows)
        {
            var message = row["message"]!;
            var card = message.Deserialize<ForkCard>(DeskJson.Options)!;
            Assert.True(DeskVectors.SameShape(message, Write(card)), (string)row["name"]!);
        }
    }
}
