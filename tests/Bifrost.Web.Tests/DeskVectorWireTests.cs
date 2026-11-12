using System.Text.Json.Nodes;
using Bifrost.Web.Features.Desk;
using Bifrost.Web.Tests.Support;

namespace Bifrost.Web.Tests;

// CultLib's vectors, replayed from their WIRE form (responseHex / messagePackHex) through the real UnixSocketDeskDoor on
// a socket double, then compared with the vector's `message`: same keys in the same order, integers unchanged
// (compared as JSON text; the vector side is parsed JSON, so an escaped apostrophe cannot differ). Only what Huginn
// sends is served here. The refusals, 503 documents and edge-authored results in the vectors are Bifrost's own
// output and are replayed on the edge (DeskReadTests, DeskAnswerTests, DeskDocumentTests).
//
// What the door judges of a reply (the single owner is the wire walker's Closed): every contract document is a closed
// map with every key present and typed; the card inside an entry is an opaque map. What it does not judge: every
// rule a schema cannot say (R-card, R-record, R-entry, R-list, R-answer-result, option and hash rules, ordering,
// the reader-view agreement between deciders and answerers). Those belong to CultLib's validators and to Huginn's door;
// Bifrost holds no audience rule and masks nothing it cannot read.
public sealed class DeskVectorWireTests
{
    private static string Mind(JsonNode request) => (string)request["mind"]!;

    private static IEnumerable<(string Name, JsonNode Request, JsonNode Response, string Hex)> ListSteps()
    {
        foreach (var scenario in DeskVectors.Rows("transport", "listScenarios").Concat(DeskVectors.Rows("transport", "listShortPages")))
        {
            foreach (var step in scenario["steps"]!.AsArray().Select(s => s!.AsObject()).Where(s => s.ContainsKey("responseHex")))
            {
                yield return ((string)scenario["name"]!, step["request"]!, step["response"]!, (string)step["responseHex"]!);
            }
        }
    }

    [Fact]
    public async Task Every_list_page_in_the_vectors_leaves_the_door_as_the_vectors_message()
    {
        var steps = 0;
        foreach (var (name, request, response, hex) in ListSteps())
        {
            var list = await DeskServe.ListAsync(Convert.FromHexString(hex), Mind(request));

            Assert.True(DeskServe.Text(response) == DeskServe.Text(list), name);
            steps++;
        }

        Assert.Equal(16, steps);
    }

    [Fact]
    public async Task Every_list_page_in_the_vectors_leaves_the_edge_as_the_vectors_message_for_its_reader()
    {
        var steps = 0;
        foreach (var (name, request, response, hex) in ListSteps())
        {
            var scenario = DeskVectors.Rows("transport", "listScenarios").FirstOrDefault(s => (string)s["name"]! == name);
            var nonDecider = scenario is not null && scenario.ContainsKey("twin");
            using var huginn = DeskServe.Socket(DeskServe.Forks(Convert.FromHexString(hex)));
            using var factory = new DeskWebApplicationFactory(DeskServe.ConfigurationOn(Mind(request), huginn.Path), fakeDoor: false);
            var reader = nonDecider ? DeskWebApplicationFactory.Member : DeskWebApplicationFactory.Alice;
            await factory.EnsureAccountAsync(reader, true);
            var query = $"?status={(string?)request["status"]}&cursor={(string?)request["cursor"]}&limit={(int)request["limit"]!}";

            var reply = await factory.SendAsync("GET", $"/api/desk/{Mind(request)}/forks" + query, null, factory.Token(reader, "bifrost-desk", "desk.read"));

            // The edge's one change for a non-decider: every note nil, the key kept.
            var expected = JsonNode.Parse(response.ToJsonString())!;
            if (nonDecider)
            {
                foreach (var answer in expected["forks"]!.AsArray().SelectMany(f => f!["record"]!["answers"]!.AsArray()))
                {
                    answer!["note"] = null;
                }
            }

            Assert.Equal(200, reply.Status);
            Assert.True(DeskServe.Text(expected) == DeskServe.Text(JsonNode.Parse(reply.Body)!), name);
            steps++;
        }

        Assert.Equal(16, steps);
    }

    [Fact]
    public async Task Every_valid_entry_leaves_a_read_unchanged_in_both_views()
    {
        var rows = DeskVectors.Rows("transport", "entry", "valid").ToList();
        Assert.Equal(8, rows.Count);
        foreach (var row in rows)
        {
            var entry = await DeskServe.ReadAsync(Convert.FromHexString((string)row["messagePackHex"]!));

            Assert.True(DeskServe.Text(row["message"]!) == DeskServe.Text(entry), (string)row["name"]!);
        }
    }

    [Fact]
    public async Task Every_valid_card_leaves_the_door_inside_an_entry_unchanged()
    {
        var rows = DeskVectors.Rows("card", "valid").ToList();
        Assert.Equal(7, rows.Count);
        foreach (var row in rows)
        {
            var card = row["message"]!;
            var record = DeskServe.FromJson(JsonNode.Parse($$"""{"schemaVersion":"gamecult.fork.record.v0","fork_id":"{{(string)card["fork_id"]!}}","options_hash":"{{(string)card["options_hash"]!}}","counted_answer_id":null,"answerer_count":0,"answers":[]}""")!);

            var entry = await DeskServe.ReadAsync(DeskServe.EntryOf(Convert.FromHexString((string)row["messagePackHex"]!), record));

            Assert.True(DeskServe.Text(card) == DeskServe.Text(entry.Card), (string)row["name"]!);
        }
    }

    [Fact]
    public async Task Every_scenario_record_leaves_the_door_unchanged_the_6_key_record()
    {
        var count = 0;
        foreach (var scenario in DeskVectors.Rows("scenarios"))
        {
            var hex = (string)scenario["recordHex"]!;
            Assert.StartsWith("86", hex, StringComparison.Ordinal);

            var entry = await DeskServe.ReadAsync(DeskServe.EntryOf(DeskServe.FromJson(scenario["card"]!), Convert.FromHexString(hex)));

            Assert.True(DeskServe.Text(scenario["record"]!) == DeskServe.Text(entry.Record), (string)scenario["name"]!);
            count++;
        }

        Assert.Equal(63, count);
    }

    [Fact]
    public async Task Every_valid_stored_answer_leaves_an_answered_reply_unchanged_the_withheld_variants_included()
    {
        var rows = DeskVectors.Rows("deskAnswer", "valid").ToList();
        Assert.Equal(8, rows.Count);
        foreach (var row in rows)
        {
            var answer = await DeskServe.AnswerAsync(Convert.FromHexString((string)row["messagePackHex"]!));

            Assert.True(DeskServe.Text(row["message"]!) == DeskServe.Text(answer), (string)row["name"]!);
        }

        Assert.Contains(rows, r => (string)r["name"]! == "withheld_answerer_revision" && (string)r["message"]!["outcome"]! == "Revision" && r["message"]!["answerer"] is null);
        Assert.Contains(rows, r => (string)r["name"]! == "withheld_answerer_retraction" && (string)r["message"]!["outcome"]! == "Retraction" && r["message"]!["answerer"] is null);
    }

    // The entry negatives on the wire. The door refuses the four that break its closed-map typing: an entry without its
    // record, and a missing, negative or nil answerer_count (never a count Bifrost may invent). The other fourteen are
    // rules a schema cannot say (R-record, R-entry, the options-hash and derivation rules; the two new ones pin a
    // withheld card beside a named Retraction and a count over the answers): CultLib's validators and Huginn's door
    // judge them, Bifrost reads a closed map and passes it, and a change to either side shows here.
    private static readonly HashSet<string> EntryNegativesTheDoorRefuses =
        ["entry_without_record", "answerer_count_missing", "answerer_count_negative", "answerer_count_null"];

    [Fact]
    public async Task The_door_refuses_the_entry_negatives_that_break_its_typing_and_judges_no_rule_of_the_contract()
    {
        var rows = DeskVectors.Rows("transport", "entry", "invalid").ToList();
        Assert.Equal(18, rows.Count);
        Assert.Contains(rows, r => (string)r["name"]! == "withheld_revision_and_retraction_beside_a_named_retraction");
        Assert.Contains(rows, r => (string)r["name"]! == "withheld_revision_and_retraction_count_over_the_answers");
        foreach (var row in rows)
        {
            var name = (string)row["name"]!;
            var served = Task.Run(() => DeskServe.ReadAsync(Convert.FromHexString((string)row["messagePackHex"]!)));

            if (EntryNegativesTheDoorRefuses.Contains(name))
            {
                await Assert.ThrowsAsync<DeskDoorException>(() => served);
            }
            else
            {
                var entry = await served;
                Assert.True(DeskServe.Text(row["message"]!) == DeskServe.Text(entry), name);
            }
        }
    }

    // The list negatives on the wire. The door refuses count_negative (a count is an unsigned integer in its closed typing).
    // The others are rules a schema cannot say or text grammars the door does not hold: a cursor is c<N> and a stamp is s<N>
    // (Bifrost passes both on, opaque), a page is one reader's view (R-list), and an order is unique among a Mind's cards.
    // CultLib's validators and Huginn's door judge them.
    [Fact]
    public async Task The_door_refuses_a_negative_count_and_judges_no_other_list_rule()
    {
        var rows = DeskVectors.Rows("transport", "listInvalid").ToList();
        Assert.Equal(5, rows.Count);
        foreach (var row in rows)
        {
            var name = (string)row["name"]!;
            var served = Task.Run(() => DeskServe.ListAsync(Convert.FromHexString((string)row["messagePackHex"]!)));

            if (name == "count_negative")
            {
                await Assert.ThrowsAsync<DeskDoorException>(() => served);
            }
            else
            {
                Assert.True(DeskServe.Text(row["message"]!) == DeskServe.Text(await served), name);
            }
        }
    }

    // The stored-answer negatives and the answer_result negatives. The door judges that an answer is a closed 16-key map with
    // every key typed; all twelve deskAnswer negatives have the 16 keys, so none is refused here. They are rules (outcome
    // and code pairing, targets, case, and "no_answerer": a nil answerer on a Counted answer is a withheld view only a
    // reader can tell from a fault) that the contract's validators and Huginn's door own. A MISSING answerer key is the
    // door's fault, pinned in UnixSocketDeskDoorWireTests. The three resultsInvalid vectors are R-answer-result (the
    // sender's own answer carries their account, never nil): Huginn's rule; for Bifrost a nil answerer in an Answered
    // reply is a string-or-nil key like any other. Whether the edge should add that check is not decided here.
    [Fact]
    public async Task The_door_judges_no_deskAnswer_rule_of_the_contract_only_its_closed_typed_shape()
    {
        var rows = DeskVectors.Rows("deskAnswer", "invalid").ToList();
        Assert.Equal(12, rows.Count);
        foreach (var row in rows)
        {
            var answer = await DeskServe.AnswerAsync(Convert.FromHexString((string)row["messagePackHex"]!));

            Assert.True(DeskServe.Text(row["message"]!) == DeskServe.Text(answer), (string)row["name"]!);
        }
    }

    [Fact]
    public async Task The_three_answer_result_negatives_carry_an_answer_the_door_reads_as_any_answer()
    {
        var rows = DeskVectors.Rows("resultsInvalid").ToList();
        Assert.Equal(3, rows.Count);
        foreach (var row in rows)
        {
            var answer = row["message"]!["answer"]!;
            var served = await DeskServe.AnswerAsync(DeskServe.FromJson(answer));

            Assert.Null(served.Answerer);
            Assert.True(DeskServe.Text(answer) == DeskServe.Text(served), (string)row["name"]!);
        }
    }
}
