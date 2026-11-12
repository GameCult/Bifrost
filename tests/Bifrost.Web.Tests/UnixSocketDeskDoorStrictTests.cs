using System.Text.Json;
using System.Text.Json.Nodes;
using Bifrost.Web.Features.Desk;
using Bifrost.Web.Tests.Support;
using MessagePack;

namespace Bifrost.Web.Tests;

// The door reads Huginn's reply strictly: text survives whatever it holds, every contract map is closed and typed,
// a key written twice or a float is a failure, and every malformed reply is a DeskDoorException (never a null
// reference, never an invalid-operation).
public sealed class UnixSocketDeskDoorStrictTests
{
    private static readonly DeskDoorContext Context = new("eureka", "Member", "5f0c2b1e-8a4d-4c3b-9e7f-1a2b3c4d5e6f", ["5f0c2b1e-8a4d-4c3b-9e7f-1a2b3c4d5e6f", "0b9d7e3a-6c21-4f58-a0d4-9e8f7a6b5c4d"]);

    private const string Card = """{"schemaVersion":"gamecult.fork.card.v0","fork_id":"x:question:a","title":"t","order":1,"options_hash":"ab"}""";

    private static string AnswerJson(string note = "null", string answerer = "\"a1\"") =>
        $$$"""{"schemaVersion":"gamecult.desk_answer.v0","answer_id":"n1","fork_id":"x:question:a","outcome":"Counted","of":null,"retracts":null,"code":null,"options_hash":"ab","choice":"a","note":{{{note}}},"answered_at":"2026-11-11T09:00:00Z","received_at":"2026-11-11T09:00:01Z","claim_in_future":false,"answerer":{{{answerer}}},"authority":"Member","request_id":"r"}""";

    private static string RecordJson(string answers = "[]", string extra = "") =>
        $$$"""{"schemaVersion":"gamecult.fork.record.v0","fork_id":"x:question:a","options_hash":"ab","counted_answer_id":null,"answerer_count":0,"answers":{{{answers}}}{{{extra}}}}""";

    private static string EntryJson(string card = Card, string? record = null, string extra = "") =>
        $$$"""{"schemaVersion":"gamecult.fork.entry.v0","card":{{{card}}},"record":{{{record ?? RecordJson()}}}{{{extra}}}}""";

    private static string ListJson(string forks, string mind = "\"eureka\"", string keys = "\"open_count\":1,\"recorded_count\":0,") =>
        $$$"""{"schemaVersion":"gamecult.fork.list.v0","mind":{{{mind}}},"stamp":"s1",{{{keys}}}"forks":{{{forks}}},"next_cursor":null}""";

    private static string Forks(string list) => $$$"""{"Forks":{"list":{{{list}}},"as_of":1}}""";

    private static Task<DeskListOutcome> List(FakeHuginnSocket huginn) =>
        new UnixSocketDeskDoor(() => "m-1").ListAsync(huginn.Path, Context, new DeskListRequest("eureka", "open", null, 50), CancellationToken.None);

    private static Task<DeskReadOutcome> Read(FakeHuginnSocket huginn) =>
        new UnixSocketDeskDoor(() => "m-1").ReadAsync(huginn.Path, Context, "x:question:a", CancellationToken.None);

    private static FakeHuginnSocket Replying(string variantJson, string status = "accepted") =>
        new(FakeHuginnSocket.Answer((_, _) => variantJson, status));

    private static FakeHuginnSocket ReplyingBytes(byte[] payload, Action<List<(string Key, string Value)>>? mutate = null, byte[]? trailing = null, string operation = "forks") =>
        new(FakeHuginnSocket.Build((id, _) => DeskWire.ResponseFrame(id, operation, "accepted", payload, mutate, trailing)));

    private static async Task Faults(Func<Task> call) => await Assert.ThrowsAsync<DeskDoorException>(call);

    // ---- F1: control characters in text ----

    [Fact]
    public async Task Text_with_control_characters_survives_the_door_and_the_edge()
    {
        const string Note = "n\\u0000\\u0007\\u000b\\u001b\\u001f\\u0008\\t\\n\\\"\\\\ end";
        var card = Card.Replace("\"t\"", "\"title \\u001b\\u0000\"", StringComparison.Ordinal);
        var entry = EntryJson(card, RecordJson($"[{AnswerJson($"\"{Note}\"")}]").Replace("\"answerer_count\":0", "\"answerer_count\":1", StringComparison.Ordinal));
        using var huginn = Replying(Forks(ListJson($"[{entry}]")));

        var listed = Assert.IsType<DeskListOutcome.Listed>(await List(huginn));

        var expected = "n\0\a\v\u001b\u001f\b\t\n\"\\ end";
        Assert.Equal(expected, Assert.Single(Assert.Single(listed.List.Forks).Record.Answers).Note);
        Assert.Equal("title \u001b\0", Assert.Single(listed.List.Forks).Card.GetProperty("title").GetString());

        // The edge writes it back as JSON a client can parse, the decider's note whole.
        using var chat = Replying(Forks(ListJson($"[{entry}]", "\"gamecult-chat\"")));
        var config = DeskWebApplicationFactory.DefaultConfiguration();
        foreach (var key in config.Keys.Where(k => k.EndsWith(":Id", StringComparison.Ordinal)).ToList())
        {
            config[key[..^2] + "Socket"] = config[key] == "nibu" ? FakeHuginnSocket.Absent() : chat.Path;
        }

        using var factory = new DeskWebApplicationFactory(config, fakeDoor: false);
        await factory.EnsureAccountAsync(DeskWebApplicationFactory.Alice, true);
        var reply = await factory.SendAsync("GET", "/api/desk/gamecult-chat/forks", token: factory.Token(DeskWebApplicationFactory.Alice, "bifrost-desk", "desk.read"));

        Assert.Equal(200, reply.Status);
        Assert.Equal(expected, (string?)JsonNode.Parse(reply.Text)!["forks"]![0]!["record"]!["answers"]![0]!["note"]);
    }

    // ---- F2 / F3: strict documents ----

    public static TheoryData<string, string> MalformedLists()
    {
        var one = EntryJson();
        return new TheoryData<string, string>
        {
            { "missing open_count", Forks(ListJson($"[{one}]", keys: "\"recorded_count\":0,")) },
            { "missing recorded_count", Forks(ListJson($"[{one}]", keys: "\"open_count\":1,")) },
            { "missing both counts", Forks(ListJson($"[{one}]", keys: "")) },
            { "negative count", Forks(ListJson($"[{one}]", keys: "\"open_count\":-1,\"recorded_count\":0,")) },
            { "mind is another Mind's", Forks(ListJson($"[{one}]", "\"nibu\"")) },
            { "mind is not text", Forks(ListJson($"[{one}]", "7")) },
            { "missing next_cursor", Forks(ListJson($"[{one}]").Replace(",\"next_cursor\":null", "", StringComparison.Ordinal)) },
            { "unknown list key", Forks(ListJson($"[{one}]").Replace("\"stamp\"", "\"extra\":1,\"stamp\"", StringComparison.Ordinal)) },
            { "forks is null", Forks(ListJson("null")) },
            { "forks:[null]", Forks(ListJson("[null]")) },
            { "forks:[5]", Forks(ListJson("[5]")) },
            { "card is not a map", Forks(ListJson($"[{EntryJson("[]")}]")) },
            { "card is null", Forks(ListJson($"[{EntryJson("null")}]")) },
            { "record missing schemaVersion", Forks(ListJson($"[{EntryJson(record: RecordJson().Replace("\"schemaVersion\":\"gamecult.fork.record.v0\",", "", StringComparison.Ordinal))}]")) },
            { "record missing fork_id", Forks(ListJson($"[{EntryJson(record: RecordJson().Replace("\"fork_id\":\"x:question:a\",", "", StringComparison.Ordinal))}]")) },
            { "record missing options_hash", Forks(ListJson($"[{EntryJson(record: RecordJson().Replace("\"options_hash\":\"ab\",", "", StringComparison.Ordinal))}]")) },
            { "record missing answerer_count", Forks(ListJson($"[{EntryJson(record: RecordJson().Replace("\"answerer_count\":0,", "", StringComparison.Ordinal))}]")) },
            { "record answerer_count is a string", Forks(ListJson($"[{EntryJson(record: RecordJson().Replace("\"answerer_count\":0", "\"answerer_count\":\"0\"", StringComparison.Ordinal))}]")) },
            { "answers:[null]", Forks(ListJson($"[{EntryJson(record: RecordJson("[null]"))}]")) },
            { "answers is null", Forks(ListJson($"[{EntryJson(record: RecordJson("null"))}]")) },
            { "answer with only schemaVersion", Forks(ListJson($"[{EntryJson(record: RecordJson("[{\"schemaVersion\":\"gamecult.desk_answer.v0\"}]"))}]")) },
            { "answer missing answerer", Forks(ListJson($"[{EntryJson(record: RecordJson($"[{AnswerJson().Replace(",\"answerer\":\"a1\"", "", StringComparison.Ordinal)}]"))}]")) },
            { "answer claim_in_future is text", Forks(ListJson($"[{EntryJson(record: RecordJson($"[{AnswerJson().Replace("\"claim_in_future\":false", "\"claim_in_future\":\"no\"", StringComparison.Ordinal)}]"))}]")) },
            { "unknown record key", Forks(ListJson($"[{EntryJson(record: RecordJson(extra: ",\"viewer\":1"))}]")) },
            { "unknown entry key", Forks(ListJson($"[{EntryJson(extra: ",\"viewer\":1")}]")) },
            { "unknown answer key", Forks(ListJson($"[{EntryJson(record: RecordJson($"[{AnswerJson().TrimEnd('}')},\"viewer\":1}}]"))}]")) },
            { "duplicate key in the record", Forks(ListJson($"[{EntryJson(record: RecordJson(extra: ",\"fork_id\":\"other\""))}]")) },
            { "duplicate key in the card", Forks(ListJson($"[{EntryJson(Card.TrimEnd('}') + ",\"title\":\"again\"}")}]")) },
            { "duplicate key in the list", Forks(ListJson($"[{one}]").TrimEnd('}') + ",\"stamp\":\"s2\"}") },
            { "float in the card", Forks(ListJson($"[{EntryJson(Card.TrimEnd('}') + ",\"weight\":1.0}")}]")) },
            { "float in the record", Forks(ListJson($"[{EntryJson(record: RecordJson(extra: ",\"weight\":1.5"))}]")) },
            { "Forks value is a bare number", """{"Forks":5}""" },
            { "Forks value is null", """{"Forks":null}""" },
            { "Forks without list", """{"Forks":{"as_of":1}}""" },
            { "NotStored is text", """{"NotStored":"x"}""" },
            { "NotStored is null", """{"NotStored":null}""" },
            { "NotStored refusal has an extra key", """{"NotStored":{"schemaVersion":"gamecult.fork.refusal.v0","code":"fork_unknown","error":"x","z":1}}""" },
        };
    }

    [Theory]
    [MemberData(nameof(MalformedLists))]
    public async Task A_malformed_list_reply_is_a_door_failure(string label, string reply)
    {
        _ = label;
        using var huginn = Replying(reply, reply.StartsWith("{\"NotStored", StringComparison.Ordinal) ? "rejected" : "accepted");
        await Faults(() => List(huginn));
    }

    public static TheoryData<string, string> MalformedReads() => new()
    {
        { "Fork is a number", """{"Fork":5}""" },
        { "Fork is null", """{"Fork":null}""" },
        { "Fork without entry", """{"Fork":{"as_of":1}}""" },
        { "entry holds answers:[null]", $$$"""{"Fork":{"entry":{{{EntryJson(record: RecordJson("[null]"))}}},"as_of":1}}""" },
        { "entry with a nil card", $$$"""{"Fork":{"entry":{{{EntryJson("null")}}},"as_of":1}}""" },
    };

    [Theory]
    [MemberData(nameof(MalformedReads))]
    public async Task A_malformed_read_reply_is_a_door_failure(string label, string reply)
    {
        _ = label;
        using var huginn = Replying(reply);
        await Faults(() => Read(huginn));
    }

    [Fact]
    public async Task Answered_with_only_a_schemaVersion_or_a_missing_counted_id_is_a_failure()
    {
        foreach (var reply in new[]
        {
            """{"Answered":{"answer":{"schemaVersion":"gamecult.desk_answer.v0"},"counted_answer_id":null,"as_of":1}}""",
            """{"Answered":{"answer":null,"counted_answer_id":null,"as_of":1}}""",
            """{"Answered":5}""",
            $$$"""{"Answered":{"answer":{{{AnswerJson()}}},"as_of":1}}""",
        })
        {
            using var huginn = Replying(reply);
            await Faults(() => new UnixSocketDeskDoor().AnswerAsync(huginn.Path, Context, DeskReplay.AnswerBody("x:question:a"), CancellationToken.None));
        }
    }

    [Fact]
    public async Task fork_too_large_is_never_an_answer_refusal()
    {
        using var huginn = Replying("""{"NotStored":{"schemaVersion":"gamecult.fork.refusal.v0","code":"fork_too_large","error":"fork_too_large: fork x:question:a is past the response bound by itself"}}""", "rejected");

        await Faults(() => new UnixSocketDeskDoor().AnswerAsync(huginn.Path, Context, DeskReplay.AnswerBody("x:question:a"), CancellationToken.None));
    }

    [Fact]
    public async Task A_nil_answerer_and_big_integers_come_through_whole()
    {
        var card = Card.TrimEnd('}') + ",\"big\":18446744073709551615,\"low\":-9223372036854775808,\"n\":null}";
        var entry = EntryJson(card, RecordJson($"[{AnswerJson(answerer: "null")}]").Replace("\"answerer_count\":0", "\"answerer_count\":1", StringComparison.Ordinal));
        using var huginn = Replying(Forks(ListJson($"[{entry}]")));

        var listed = Assert.IsType<DeskListOutcome.Listed>(await List(huginn));

        var fork = Assert.Single(listed.List.Forks);
        Assert.Equal(card, JsonSerializer.Serialize(fork.Card, DeskJson.Options));
        Assert.Null(Assert.Single(fork.Record.Answers).Answerer);
    }

    // ---- F4 / the survivors: the envelope and the bytes around it ----

    private static byte[] ListPayload() => MessagePackSerializer.ConvertFromJson(Forks(ListJson($"[{EntryJson()}]")));

    [Fact]
    public async Task A_well_formed_reply_built_the_same_way_decodes()
    {
        using var huginn = ReplyingBytes(ListPayload());
        Assert.IsType<DeskListOutcome.Listed>(await List(huginn));
    }

    [Fact]
    public async Task Payload_base64_must_be_strict_standard_base64()
    {
        foreach (var mutate in new Action<List<(string Key, string Value)>>[]
        {
            fields => fields[fields.FindIndex(f => f.Key == "payload")] = ("payload", Insert(fields, "\n")),
            fields => fields[fields.FindIndex(f => f.Key == "payload")] = ("payload", Insert(fields, " ")),
            fields => fields[fields.FindIndex(f => f.Key == "payload")] = ("payload", fields.First(f => f.Key == "payload").Value.Replace('+', '-').Replace('/', '_') + "A"),
            fields => fields[fields.FindIndex(f => f.Key == "payload")] = ("payload", fields.First(f => f.Key == "payload").Value.TrimEnd('=')),
        })
        {
            using var huginn = ReplyingBytes(ListPayload(), mutate);
            await Faults(() => List(huginn));
        }
    }

    private static string Insert(List<(string Key, string Value)> fields, string text)
    {
        var value = fields.First(f => f.Key == "payload").Value;
        return value[..8] + text + value[8..];
    }

    [Fact]
    public async Task Bytes_after_the_payload_message_or_after_the_frame_message_are_a_failure()
    {
        using var afterPayload = ReplyingBytes([.. ListPayload(), 0xc0]);
        await Faults(() => List(afterPayload));

        using var afterFrame = ReplyingBytes(ListPayload(), trailing: [0xc0]);
        await Faults(() => List(afterFrame));
    }

    [Fact]
    public async Task A_key_written_twice_in_the_envelope_or_the_variant_is_a_failure()
    {
        using var envelope = ReplyingBytes(ListPayload(), fields => fields.Add(("status", "rejected")));
        await Faults(() => List(envelope));

        using var variant = ReplyingBytes(MessagePackSerializer.ConvertFromJson(Forks(ListJson("[]")).Replace("\"as_of\":1", "\"as_of\":1,\"as_of\":2", StringComparison.Ordinal)));
        await Faults(() => List(variant));
    }

    // The payload is one tagged variant. A second key beside it, whatever it is called, is not a reply the door reads.
    [Theory]
    [InlineData("X")]
    [InlineData("Refused")]
    [InlineData("Forks2")]
    public async Task A_payload_with_a_second_key_beside_the_variant_is_a_failure(string second)
    {
        var twoKeys = Forks(ListJson($"[{EntryJson()}]"))[..^1] + $",\"{second}\":\"Unavailable\"}}";
        using var huginn = ReplyingBytes(MessagePackSerializer.ConvertFromJson(twoKeys));

        await Faults(() => List(huginn));
    }

    // The envelope is an operation response or nothing: a different schemaVersion is not read as one.
    [Theory]
    [InlineData("cultnet.operation_response.v1")]
    [InlineData("cultnet.operation_response.v")]
    [InlineData("")]
    [InlineData("CULTNET.OPERATION_RESPONSE.V0")]
    public async Task An_envelope_with_another_schemaVersion_is_a_failure(string schemaVersion)
    {
        using var huginn = ReplyingBytes(ListPayload(), fields =>
        {
            var at = fields.FindIndex(f => f.Key == "schemaVersion");
            fields[at] = ("schemaVersion", schemaVersion);
        });

        await Faults(() => List(huginn));
    }

    // The route names the fork; an entry for another fork is not the answer to the question asked, however well formed.
    [Fact]
    public async Task A_read_that_returns_another_forks_entry_is_a_failure()
    {
        var other = Card.Replace("x:question:a", "x:question:b", StringComparison.Ordinal);
        var record = RecordJson().Replace("\"fork_id\":\"x:question:a\"", "\"fork_id\":\"x:question:b\"", StringComparison.Ordinal);
        using var huginn = Replying($$$"""{"Fork":{"entry":{{{EntryJson(other, record)}}},"as_of":1}}""");

        await Faults(() => Read(huginn));
    }

    [Fact]
    public async Task A_read_whose_record_disagrees_with_its_card_is_not_the_doors_rule()
    {
        var record = RecordJson().Replace("\"fork_id\":\"x:question:a\"", "\"fork_id\":\"x:question:b\"", StringComparison.Ordinal);
        using var huginn = Replying($$$"""{"Fork":{"entry":{{{EntryJson(record: record)}}},"as_of":1}}""");

        Assert.IsType<DeskReadOutcome.Found>(await Read(huginn));
    }

    [Fact]
    public async Task A_read_of_the_named_fork_is_found()
    {
        using var huginn = Replying($$$"""{"Fork":{"entry":{{{EntryJson()}}},"as_of":1}}""");

        Assert.IsType<DeskReadOutcome.Found>(await Read(huginn));
    }

    private static byte[] BinaryList()
    {
        var w = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(w);
        writer.WriteMapHeader(1);
        writer.Write("Forks");
        writer.WriteMapHeader(2);
        writer.Write("list");
        writer.Write(new byte[] { 1, 2, 3 });
        writer.Write("as_of");
        writer.Write(1);
        writer.Flush();
        return w.WrittenSpan.ToArray();
    }

    [Fact]
    public async Task A_binary_anywhere_is_a_failure_not_a_rewritten_value()
    {
        using var bin = ReplyingBytes(BinaryList());
        await Faults(() => List(bin));
    }

    // ---- the request, value by value ----

    [Fact]
    public async Task An_answer_request_carries_the_contexts_mind_subject_authority_and_deciders_and_the_body_as_a_map()
    {
        using var huginn = Replying("""{"NotStored":{"schemaVersion":"gamecult.fork.refusal.v0","code":"fork_unknown","error":"x"}}""", "rejected");

        await new UnixSocketDeskDoor().AnswerAsync(huginn.Path, Context, DeskReplay.AnswerBody("x:question:a"), CancellationToken.None);

        using var envelope = DeskWire.Envelope(Assert.Single(huginn.Requests));
        using var payload = DeskWire.Payload(envelope);
        var answer = payload.RootElement.GetProperty("Answer");
        Assert.Equal(("eureka", "5f0c2b1e-8a4d-4c3b-9e7f-1a2b3c4d5e6f", "Member"), (answer.GetProperty("mind").GetString(), answer.GetProperty("subject").GetString(), answer.GetProperty("authority").GetString()));
        Assert.Equal(Context.Deciders, answer.GetProperty("deciders").EnumerateArray().Select(d => d.GetString()));
        Assert.Equal("x:question:a", answer.GetProperty("request").GetProperty("fork_id").GetString());
    }
}
