using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessagePack;
using Bifrost.Web.Features.Desk;
using Bifrost.Web.Tests.Support;

namespace Bifrost.Web.Tests;

// The production door against a real unix socket and Huginn's golden frames (fixtures at Huginn 79d954c).
public sealed class UnixSocketDeskDoorTests
{
    private const string Fork = "eureka-substrate:question:desk-listing-authority";

    // The fixtures' account ids: lowercase D-format UUIDs, synthetic.
    private const string Decider = "5f0c2b1e-8a4d-4c3b-9e7f-1a2b3c4d5e6f";

    private static readonly DeskDoorContext Context = new("eureka", "Member", Decider, [Decider, "0b9d7e3a-6c21-4f58-a0d4-9e8f7a6b5c4d"]);

    private static UnixSocketDeskDoor DoorWith(string messageId) => new(() => messageId);

    private static Task<DeskListOutcome> List(UnixSocketDeskDoor door, string socket, string? status = "open", string? cursor = null, int limit = 50) =>
        door.ListAsync(socket, Context, new DeskListRequest("eureka", status, cursor, limit), CancellationToken.None);

    private static Task<DeskReadOutcome> Read(UnixSocketDeskDoor door, string socket, string forkId = Fork) =>
        door.ReadAsync(socket, Context, forkId, CancellationToken.None);

    private const string UnknownFork =
        """{"NotStored":{"schemaVersion":"gamecult.fork.refusal.v0","code":"fork_unknown","error":"fork_unknown: no such fork"}}""";

    // ---- (a) the request frames, byte for byte ----

    [Theory]
    [InlineData("list-open.request", "m-1")]
    [InlineData("list-open-cursor.request", "m-2")]
    [InlineData("list-counts.request", "m-3")]
    [InlineData("read.request", "m-4")]
    public async Task Request_frames_are_the_golden_bytes(string fixture, string messageId)
    {
        using var huginn = new FakeHuginnSocket(FakeHuginnSocket.Answer((_, _) => UnknownFork, "rejected"));
        var door = DoorWith(messageId);

        _ = fixture switch
        {
            "list-open.request" => await List(door, huginn.Path),
            "list-open-cursor.request" => await List(door, huginn.Path, cursor: "c42"),
            "list-counts.request" => await List(door, huginn.Path, limit: 0),
            _ => (object)await Read(door, huginn.Path),
        };

        Assert.Equal(FakeHuginnSocket.Fixture(fixture), Assert.Single(huginn.Requests));
    }

    [Fact]
    public async Task An_answer_request_is_the_Answer_variant_with_the_clients_body_as_a_map_in_its_own_order()
    {
        using var huginn = new FakeHuginnSocket(FakeHuginnSocket.Answer((_, _) => UnknownFork, "rejected"));
        var body = DeskReplay.AnswerBody(DeskReplay.ChatFork);
        var context = new DeskDoorContext("gamecult-chat", "Steward", "9f8e", ["9f8e", "aa"]);

        var outcome = await new UnixSocketDeskDoor().AnswerAsync(huginn.Path, context, body, CancellationToken.None);

        Assert.Equal(new DeskAnswerOutcome.NotStored("fork_unknown"), outcome);
        using var envelope = DeskWire.Envelope(Assert.Single(huginn.Requests));
        Assert.Equal("answer", envelope.RootElement.GetProperty("operation").GetString());
        using var payload = DeskWire.Payload(envelope);
        var answer = payload.RootElement.GetProperty("Answer");
        Assert.Equal(["mind", "subject", "authority", "deciders", "request"], answer.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Steward", answer.GetProperty("authority").GetString());
        // Every key, the nulls too, in the order the client wrote them.
        using var sent = JsonDocument.Parse(body);
        Assert.Equal(sent.RootElement.EnumerateObject().Select(p => p.Name), answer.GetProperty("request").EnumerateObject().Select(p => p.Name));
        foreach (var property in sent.RootElement.EnumerateObject())
        {
            var back = answer.GetProperty("request").GetProperty(property.Name);
            Assert.True(
                System.Text.Json.Nodes.JsonNode.DeepEquals(
                    System.Text.Json.Nodes.JsonNode.Parse(property.Value.GetRawText()),
                    System.Text.Json.Nodes.JsonNode.Parse(back.GetRawText())),
                property.Name);
        }
    }

    // ---- (b) each golden reply ----

    private static (string Kind, string MessageId) Shape(string fixture)
    {
        using var envelope = DeskWire.Envelope(FakeHuginnSocket.Fixture(fixture));
        var root = envelope.RootElement;
        return root.TryGetProperty("operation", out var operation)
            ? (operation.GetString() == "fork" ? "read" : "list", root.GetProperty("messageId").GetString()!)
            : ("list", "m-1"); // a CultNet Error message has neither
    }

    private static async Task<object> Replayed(string fixture, string? readForkId = null)
    {
        var (kind, messageId) = Shape(fixture);
        using var huginn = new FakeHuginnSocket(FakeHuginnSocket.Replay(FakeHuginnSocket.Fixture(fixture)));
        var door = DoorWith(messageId);
        return kind == "read"
            ? await Read(door, huginn.Path, readForkId ?? Fork)
            : await List(door, huginn.Path);
    }

    [Fact]
    public async Task An_empty_list_is_an_empty_list()
    {
        var listed = Assert.IsType<DeskListOutcome.Listed>(await Replayed("list-empty.response"));
        Assert.Equal((0, 0, 0, "s10190", "eureka"), (listed.List.OpenCount, listed.List.RecordedCount, listed.List.Forks.Length, listed.List.Stamp, listed.List.Mind));
        Assert.Null(listed.List.NextCursor);
    }

    [Fact]
    public async Task The_counts_reply_carries_the_counts()
    {
        var listed = Assert.IsType<DeskListOutcome.Listed>(await Replayed("list-counts.response"));
        Assert.Equal((3, 5, 0), (listed.List.OpenCount, listed.List.RecordedCount, listed.List.Forks.Length));
    }

    [Fact]
    public async Task An_unknown_fork_is_not_found()
    {
        Assert.Equal(new DeskReadOutcome.NotFound("fork_unknown"), await Replayed("read-unknown.response"));
    }

    [Fact]
    public async Task A_list_too_large_names_its_fork_and_order_from_the_refusal_text()
    {
        Assert.Equal(new DeskListOutcome.ForkTooLarge(Fork, 41), await Replayed("list-too-large.response"));
    }

    [Fact]
    public async Task A_read_too_large_names_the_routes_fork_not_the_refusal_text()
    {
        // The refusal text names the fixture's fork; the route asked for another, and the route's id is what returns.
        Assert.Equal(
            new DeskReadOutcome.ForkTooLarge("route:question:asked"),
            await Replayed("read-too-large.response", "route:question:asked"));
    }

    [Theory]
    [InlineData("list-response-too-large.response")]
    [InlineData("refused-unit.response")]
    [InlineData("unavailable.response")]
    [InlineData("failure.response")]
    [InlineData("error.response")]
    public async Task A_refusal_a_failure_or_an_error_is_a_door_failure(string fixture)
    {
        var failure = await Assert.ThrowsAsync<DeskDoorException>(() => Replayed(fixture));
        Assert.DoesNotContain("deadline", failure.Message);
    }

    [Fact]
    public async Task A_list_refusal_text_that_does_not_parse_exactly_is_a_failure_never_a_guess()
    {
        foreach (var text in new[]
        {
            "fork_too_large: fork a:question:b is past the page budget by itself",
            "fork_too_large: fork a:question:b at order 4x is past the page budget by itself",
            "fork_too_large: fork at order 4 is past the page budget by itself",
            "fork_too_large: fork a:question:b at order 04 is past the page budget by itself",
            "oops fork_too_large: fork a:question:b at order 4 is past the page budget by itself",
        })
        {
            using var huginn = new FakeHuginnSocket(FakeHuginnSocket.Answer((_, _) =>
                $$$"""{"NotStored":{"schemaVersion":"gamecult.fork.refusal.v0","code":"fork_too_large","error":"{{{text}}}"}}""", "rejected"));
            await Assert.ThrowsAsync<DeskDoorException>(() => List(new UnixSocketDeskDoor(), huginn.Path));
        }
    }

    [Fact]
    public async Task A_one_key_refusal_an_unknown_variant_and_an_unknown_code_are_failures()
    {
        foreach (var (variant, status) in new[]
        {
            ("""{"Refused":{"Unavailable":{"detail":"x"}}}""", "rejected"),
            ("""{"Refused":"MissingIdentity"}""", "rejected"),
            ("""{"Mystery":{"x":1}}""", "accepted"),
            ("""{"NotStored":{"schemaVersion":"gamecult.fork.refusal.v0","code":"nope","error":"x"}}""", "rejected"),
            ("""{"NotStored":{"schemaVersion":"gamecult.fork.refusal.v9","code":"fork_unknown","error":"x"}}""", "rejected"),
            ("""{"Fork":{"entry":{},"as_of":1}}""", "accepted"),
            ("""{"Forks":{"list":{"schemaVersion":"gamecult.fork.list.v0","mind":"eureka","stamp":"s1","open_count":0,"recorded_count":0,"forks":[],"next_cursor":null},"as_of":1}}""", "rejected"),
        })
        {
            using var huginn = new FakeHuginnSocket(FakeHuginnSocket.Answer((_, _) => variant, status));
            await Assert.ThrowsAsync<DeskDoorException>(() => List(new UnixSocketDeskDoor(), huginn.Path));
        }
    }

    [Fact]
    public async Task A_reply_for_another_message_or_operation_is_a_failure()
    {
        using var huginn = new FakeHuginnSocket(FakeHuginnSocket.Replay(FakeHuginnSocket.Fixture("list-empty.response")));
        await Assert.ThrowsAsync<DeskDoorException>(() => List(DoorWith("m-9"), huginn.Path));

        using var other = new FakeHuginnSocket(FakeHuginnSocket.Replay(FakeHuginnSocket.Fixture("list-empty.response")));
        await Assert.ThrowsAsync<DeskDoorException>(() => Read(DoorWith("m-1"), other.Path));
    }

    // ---- (c) entries pass through, the card untouched ----

    private const string CardWithRaisedAt =
        """{"schemaVersion":"gamecult.fork.card.v0","fork_id":"x:question:a","title":"It's é","raised_at":"2026-11-11T09:00:00Z","options":[{"id":"a","text":"A"}],"order":7,"options_hash":"ab"}""";

    // Reordered, no raised_at, a key no contract names yet.
    private const string CardReordered =
        """{"order":8,"future_key":{"z":[3,null,100],"a":true},"options_hash":"cd","schemaVersion":"gamecult.fork.card.v0","fork_id":"x:question:b","title":"B","leans":null}""";

    private static string EntryJson(string card, string forkId) =>
        $$$"""{"schemaVersion":"gamecult.fork.entry.v0","card":{{{card}}},"record":{"schemaVersion":"gamecult.fork.record.v0","fork_id":"{{{forkId}}}","options_hash":"ab","counted_answer_id":null,"answerer_count":0,"answers":[]}}""";

    [Fact]
    public async Task Cards_in_a_list_and_a_read_come_through_byte_faithful()
    {
        var list = $$$"""{"Forks":{"list":{"schemaVersion":"gamecult.fork.list.v0","mind":"eureka","stamp":"s7","open_count":2,"recorded_count":0,"forks":[{{{EntryJson(CardWithRaisedAt, "x:question:a")}}},{{{EntryJson(CardReordered, "x:question:b")}}}],"next_cursor":"c8"},"as_of":7}}""";
        using var huginn = new FakeHuginnSocket(FakeHuginnSocket.Answer((operation, _) =>
            operation == "forks" ? list : $$$"""{"Fork":{"entry":{{{EntryJson(CardReordered, "x:question:b")}}},"as_of":7}}"""));
        var door = new UnixSocketDeskDoor();

        var listed = Assert.IsType<DeskListOutcome.Listed>(await List(door, huginn.Path));
        Assert.Equal(["x:question:a", "x:question:b"], listed.List.Forks.Select(f => f.Record.ForkId));
        Assert.Equal(CardWithRaisedAt, JsonSerializer.Serialize(listed.List.Forks[0].Card, DeskJson.Options));
        Assert.Equal(CardReordered, JsonSerializer.Serialize(listed.List.Forks[1].Card, DeskJson.Options));
        Assert.Equal("c8", listed.List.NextCursor);

        var found = Assert.IsType<DeskReadOutcome.Found>(await Read(door, huginn.Path, "x:question:b"));
        Assert.Equal(CardReordered, JsonSerializer.Serialize(found.Entry.Card, DeskJson.Options));
    }

    [Fact]
    public async Task An_answered_reply_becomes_the_answer_result_without_a_card()
    {
        var answered = """{"Answered":{"answer":{"schemaVersion":"gamecult.desk_answer.v0","answer_id":"n1","fork_id":"x:question:a","outcome":"Counted","of":null,"retracts":null,"code":null,"options_hash":"ab","choice":"a","note":null,"answered_at":"2026-11-11T09:00:00Z","received_at":"2026-11-11T09:00:01Z","claim_in_future":false,"answerer":"7","authority":"Member","request_id":"r"},"counted_answer_id":"n1","as_of":9}}""";
        using var huginn = new FakeHuginnSocket(FakeHuginnSocket.Answer((_, _) => answered));

        var outcome = await new UnixSocketDeskDoor().AnswerAsync(huginn.Path, Context, DeskReplay.AnswerBody("x:question:a"), CancellationToken.None);

        var result = Assert.IsType<DeskAnswerOutcome.Answered>(outcome).Result;
        Assert.Equal((DeskSchemas.AnswerResult, "n1", "Counted", "n1", null), (result.SchemaVersion, result.Answer.AnswerId, result.Answer.Outcome, result.CountedAnswerId, result.Card));
    }

    // ---- (c2) both reader views through the door and the edge ----

    // Huginn's fragments at 79d954c, as JSON. record-whole is the decider's view as Huginn sent it before
    // answerer_count existed; record-non-decider.proposed is a PROPOSED mirror type built in Huginn's test (the
    // non-decider view, every answerer nil, answerer_count 2); card-viewers-*.synthetic are SYNTHETIC two-key
    // fragments, not cards. Bifrost reads none of them as anything but what they are: a record, and two keys a card
    // carries.
    private static JsonObject Fragment(string name) =>
        JsonNode.Parse(MessagePackSerializer.ConvertToJson(File.ReadAllBytes(
            Path.Combine(DeskFeedFixturePinTests.Directory, name + ".payload.msgpack"))))!.AsObject();

    // The decider's view as the CultLib 3a51bbab contract spells it: record-whole with answerer_count (the number of
    // distinct ids over its two answers) in the contract's place, after counted_answer_id. Huginn's record-whole does
    // not carry it yet.
    private static JsonObject DeciderRecord()
    {
        var whole = Fragment("record-whole");
        var record = new JsonObject();
        foreach (var (key, value) in whole.ToList())
        {
            whole.Remove(key);
            record[key] = value;
            if (key == "counted_answer_id")
            {
                record["answerer_count"] = 2;
            }
        }

        return record;
    }

    // The vector card with its two reader-view keys replaced by a fragment's, in the card's own key order.
    private static string ViewCard(string fragment)
    {
        var card = JsonNode.Parse(DeskVectors.Rows("card", "valid").First(r => (string)r["name"]! == "operator_fork")["message"]!.ToJsonString())!.AsObject();
        var view = Fragment(fragment);
        card["fork_id"] = Fork;
        card["deciders"] = view["deciders"]!.DeepClone();
        card["decider_count"] = view["decider_count"]!.DeepClone();
        return card.ToJsonString(DeskJson.Options);
    }

    private static string EntryOf(string card, JsonObject record) =>
        $$$"""{"schemaVersion":"gamecult.fork.entry.v0","card":{{{card}}},"record":{{{record.ToJsonString(DeskJson.Options)}}}}""";

    private static string ListOf(string entry) =>
        $$$"""{"Forks":{"list":{"schemaVersion":"gamecult.fork.list.v0","mind":"gamecult-chat","stamp":"s7","open_count":1,"recorded_count":0,"forks":[{{{entry}}}],"next_cursor":null},"as_of":7}}""";

    [Fact]
    public void Every_account_id_in_the_fixtures_is_a_lowercase_D_format_uuid_and_the_non_decider_fragments_name_none()
    {
        var grammar = new System.Text.RegularExpressions.Regex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$");
        foreach (var id in Fragment("card-viewers-decider.synthetic")["deciders"]!.AsArray().Select(d => (string)d!)
                     .Concat(Fragment("record-whole")["answers"]!.AsArray().Select(a => (string)a!["answerer"]!)))
        {
            Assert.Matches(grammar, id);
        }

        var nonDeciderCard = Fragment("card-viewers-non-decider.synthetic");
        Assert.Empty(nonDeciderCard["deciders"]!.AsArray());
        Assert.Equal(2, (int)nonDeciderCard["decider_count"]!);
        Assert.All(Fragment("record-non-decider.proposed")["answers"]!.AsArray(), a => Assert.Null(a!["answerer"]));
    }

    [Theory]
    [InlineData("card-viewers-decider.synthetic")]
    [InlineData("card-viewers-non-decider.synthetic")]
    public async Task A_card_in_either_view_comes_through_the_door_byte_for_byte(string view)
    {
        var card = ViewCard(view);
        var nonDecider = view.Contains("non-decider", StringComparison.Ordinal);
        var record = nonDecider ? Fragment("record-non-decider.proposed") : DeciderRecord();
        using var huginn = new FakeHuginnSocket(FakeHuginnSocket.Answer((_, _) => ListOf(EntryOf(card, record))));

        var listed = Assert.IsType<DeskListOutcome.Listed>(await List(new UnixSocketDeskDoor(), huginn.Path));

        var entry = Assert.Single(listed.List.Forks);
        // The card is opaque: raised_at (a string), decider_count and the withheld or whole deciders leave as they came.
        Assert.Equal(card, JsonSerializer.Serialize(entry.Card, DeskJson.Options));
        Assert.Equal(nonDecider, entry.Card.GetProperty("deciders").GetArrayLength() == 0);
        Assert.Equal(2, entry.Card.GetProperty("decider_count").GetInt32());
        Assert.Equal(JsonValueKind.String, entry.Card.GetProperty("raised_at").ValueKind);
        Assert.Equal(2, entry.Record.AnswererCount);
        Assert.Equal(record.ToJsonString(DeskJson.Options), JsonSerializer.Serialize(entry.Record, DeskJson.Options));
        Assert.All(entry.Record.Answers, a => Assert.Equal(nonDecider, a.Answerer is null));
    }

    [Fact]
    public async Task A_decider_record_keeps_its_ids_and_a_non_decider_record_comes_through_with_nil_answerers_and_the_count()
    {
        var chat = DeskWebApplicationFactory.SocketOf("gamecult-chat");
        _ = chat; // the edge below talks to a real socket, not the fake door's table
        var nonDecider = Fragment("record-non-decider.proposed");
        using var huginn = new FakeHuginnSocket(FakeHuginnSocket.Answer((_, _) => ListOf(EntryOf(ViewCard("card-viewers-non-decider.synthetic"), nonDecider))));
        using var factory = new DeskWebApplicationFactory(ConfigurationWith(huginn.Path, FakeHuginnSocket.Absent()), fakeDoor: false);
        await factory.EnsureAccountAsync(DeskWebApplicationFactory.Member, true);
        var reply = await factory.SendAsync("GET", "/api/desk/gamecult-chat/forks", token: factory.Token(DeskWebApplicationFactory.Member, "bifrost-desk", "desk.read"));

        // Huginn withheld the ids; Bifrost invents none, and nils the notes (the one thing it strips).
        Assert.Equal(200, reply.Status);
        var forks = JsonNode.Parse(reply.Text)!["forks"]!.AsArray();
        var record = forks[0]!["record"]!;
        Assert.Equal(2, (int)record["answerer_count"]!);
        Assert.All(record["answers"]!.AsArray(), a =>
        {
            Assert.True(a!.AsObject().ContainsKey("answerer"));
            Assert.Null(a["answerer"]);
            Assert.Null(a["note"]);
        });
        Assert.Empty(forks[0]!["card"]!["deciders"]!.AsArray());
        Assert.Equal(2, (int)forks[0]!["card"]!["decider_count"]!);
        Assert.DoesNotContain("The asker lists her own forks.", reply.Text);

        // The decider's view, as the contract spells it, keeps every id and every note.
        var decider = DeciderRecord();
        using var huginn2 = new FakeHuginnSocket(FakeHuginnSocket.Answer((_, _) => ListOf(EntryOf(ViewCard("card-viewers-decider.synthetic"), decider))));
        using var factory2 = new DeskWebApplicationFactory(ConfigurationWith(huginn2.Path, FakeHuginnSocket.Absent()), fakeDoor: false);
        await factory2.EnsureAccountAsync(DeskWebApplicationFactory.Alice, true);
        var own = await factory2.SendAsync("GET", "/api/desk/gamecult-chat/forks", token: factory2.Token(DeskWebApplicationFactory.Alice, "bifrost-desk", "desk.read"));

        Assert.Equal(200, own.Status);
        var shown = JsonNode.Parse(own.Text)!["forks"]![0]!["record"]!;
        Assert.Equal(decider.ToJsonString(DeskJson.Options), shown.ToJsonString(DeskJson.Options));
    }

    // Huginn's record-whole at 79d954c has no answerer_count, which the 3a51bbab contract requires. The door refuses it
    // as a reply that does not decode; it does not write the 0 it could have made up. When Huginn's fixture gains the
    // key this test is the one to retire.
    [Fact]
    public async Task Huginns_record_without_answerer_count_is_a_fault_not_a_zero()
    {
        var entry = EntryOf(ViewCard("card-viewers-decider.synthetic"), Fragment("record-whole"));
        using var huginn = new FakeHuginnSocket(FakeHuginnSocket.Answer((_, _) => ListOf(entry)));

        var failure = await Assert.ThrowsAsync<DeskDoorException>(() => List(new UnixSocketDeskDoor(), huginn.Path));

        Assert.Contains("does not decode", failure.Message);
    }

    // ---- (d) one dead Mind fails only itself, through the HTTP edge ----

    private static Dictionary<string, string?> ConfigurationWith(string chatSocket, string nibuSocket)
    {
        var configuration = DeskWebApplicationFactory.DefaultConfiguration();
        foreach (var key in configuration.Keys.Where(k => k.EndsWith(":Id", StringComparison.Ordinal)).ToList())
        {
            var prefix = key[..^2];
            configuration[prefix + "Socket"] = configuration[key] == "nibu" ? nibuSocket : chatSocket;
        }

        return configuration;
    }

    [Fact]
    public async Task A_dead_Minds_socket_is_503_for_that_Mind_only_and_the_live_one_answers()
    {
        var list = $$$"""{"Forks":{"list":{"schemaVersion":"gamecult.fork.list.v0","mind":"gamecult-chat","stamp":"s7","open_count":1,"recorded_count":0,"forks":[{{{EntryJson(CardWithRaisedAt, "x:question:a")}}}],"next_cursor":null},"as_of":7}}""";
        using var huginn = new FakeHuginnSocket(FakeHuginnSocket.Answer((_, _) => list));
        using var factory = new DeskWebApplicationFactory(ConfigurationWith(huginn.Path, FakeHuginnSocket.Absent()), fakeDoor: false);
        await factory.EnsureAccountAsync(DeskWebApplicationFactory.Alice, true);
        var token = factory.Token(DeskWebApplicationFactory.Alice, "bifrost-desk", "desk.read");

        var live = await factory.SendAsync("GET", "/api/desk/gamecult-chat/forks", token: token);
        var dead = await factory.SendAsync("GET", "/api/desk/nibu/forks", token: token);
        var liveAgain = await factory.SendAsync("GET", "/api/desk/gamecult-chat/forks", token: token);

        Assert.Equal(200, live.Status);
        Assert.Contains("\"card\":" + CardWithRaisedAt + ",", live.Text);
        Assert.Equal(503, dead.Status);
        Assert.Equal("30", dead.Headers.RetryAfter.ToString());
        Assert.Contains("\"code\":\"unavailable\"", dead.Text);
        Assert.Equal(200, liveAgain.Status);
        Assert.Equal(2, huginn.Requests.Count);
    }

    [Fact]
    public async Task A_silent_door_is_503_with_retry_after_not_a_hang()
    {
        using var huginn = new FakeHuginnSocket(async (_, _, token) => await Task.Delay(Timeout.Infinite, token));
        using var factory = new DeskWebApplicationFactory(ConfigurationWith(FakeHuginnSocket.Absent(), huginn.Path), fakeDoor: false);
        await factory.EnsureAccountAsync(DeskWebApplicationFactory.Alice, true);

        var clock = Stopwatch.StartNew();
        var reply = await factory.SendAsync("GET", "/api/desk/nibu/forks", token: factory.Token(DeskWebApplicationFactory.Alice, "bifrost-desk", "desk.read"));

        Assert.Equal(503, reply.Status);
        Assert.True(reply.Headers.ContainsKey("Retry-After"));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"took {clock.Elapsed}");
    }

    // ---- (e) deadline, bounds, partial frames ----

    [Fact]
    public async Task A_server_that_accepts_and_never_replies_fails_at_the_deadline()
    {
        using var huginn = new FakeHuginnSocket(async (_, _, token) => await Task.Delay(Timeout.Infinite, token));

        var clock = Stopwatch.StartNew();
        var failure = await Assert.ThrowsAsync<DeskDoorException>(() => List(new UnixSocketDeskDoor(), huginn.Path));

        Assert.Contains("deadline", failure.Message);
        Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(900), TimeSpan.FromSeconds(2.5));
    }

    [Fact]
    public async Task A_caller_that_gives_up_is_cancelled_not_a_door_failure()
    {
        using var huginn = new FakeHuginnSocket(async (_, _, token) => await Task.Delay(Timeout.Infinite, token));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new UnixSocketDeskDoor().ListAsync(huginn.Path, Context, new DeskListRequest("eureka", null, null, 50), cancel.Token));
    }

    [Fact]
    public async Task A_length_prefix_over_the_bound_is_refused_without_reading_the_body()
    {
        // The server sends only the prefix, then stalls: a client that waited for the body would run out the deadline.
        using var huginn = new FakeHuginnSocket(async (socket, _, token) =>
        {
            await socket.SendAsync(new byte[] { 0, 1, 0, 1 }, SocketFlags.None, token); // 65537
            await Task.Delay(Timeout.Infinite, token);
        });

        var clock = Stopwatch.StartNew();
        var failure = await Assert.ThrowsAsync<DeskDoorException>(() => List(new UnixSocketDeskDoor(), huginn.Path));

        Assert.Contains("frame bound", failure.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(700), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task A_frame_exactly_at_the_bound_is_read_whole()
    {
        // A prefix of 65536 is within the bound; the body that follows is not a message, so it fails on content, not size.
        using var huginn = new FakeHuginnSocket(async (socket, _, token) =>
        {
            var frame = new byte[4 + 65536];
            frame[1] = 1;
            await socket.SendAsync(frame, SocketFlags.None, token);
        });

        var failure = await Assert.ThrowsAsync<DeskDoorException>(() => List(new UnixSocketDeskDoor(), huginn.Path));
        Assert.DoesNotContain("frame bound", failure.Message);
    }

    [Fact]
    public async Task A_request_over_the_bound_is_never_sent()
    {
        var deciders = Enumerable.Range(0, 64).Select(i => i.ToString("D4") + new string('d', 1100)).ToArray();
        var context = new DeskDoorContext("eureka", "Member", "7", deciders);
        using var huginn = new FakeHuginnSocket(FakeHuginnSocket.Answer((_, _) => UnknownFork, "rejected"));

        var failure = await Assert.ThrowsAsync<DeskDoorException>(() =>
            new UnixSocketDeskDoor().ListAsync(huginn.Path, context, new DeskListRequest("eureka", null, null, 50), CancellationToken.None));

        Assert.Contains("frame bound", failure.Message);
        Assert.Empty(huginn.Requests);
    }

    [Fact]
    public async Task A_truncated_frame_or_an_early_close_is_a_failure()
    {
        var golden = FakeHuginnSocket.Fixture("list-empty.response");
        foreach (var cut in new[] { 0, 2, 4, 10, golden.Length - 1 })
        {
            using var huginn = new FakeHuginnSocket(FakeHuginnSocket.Replay(golden[..cut]));
            var failure = await Assert.ThrowsAsync<DeskDoorException>(() => List(DoorWith("m-1"), huginn.Path));
            Assert.Contains("closed", failure.Message);
        }
    }

    [Fact]
    public async Task A_frame_that_arrives_in_pieces_is_read_whole()
    {
        var golden = FakeHuginnSocket.Fixture("list-empty.response");
        using var huginn = new FakeHuginnSocket(async (socket, _, token) =>
        {
            for (var at = 0; at < golden.Length; at += 7)
            {
                await socket.SendAsync(golden.AsMemory(at, Math.Min(7, golden.Length - at)), SocketFlags.None, token);
                await Task.Delay(2, token);
            }
        });

        Assert.IsType<DeskListOutcome.Listed>(await List(DoorWith("m-1"), huginn.Path));
    }

    [Fact]
    public async Task Absent_and_refusing_sockets_are_failures()
    {
        await Assert.ThrowsAsync<DeskDoorException>(() => List(new UnixSocketDeskDoor(), FakeHuginnSocket.Absent()));

        // A socket file with nobody listening: ECONNREFUSED.
        var stale = FakeHuginnSocket.Absent();
        using (var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            socket.Bind(new UnixDomainSocketEndPoint(stale));
        }

        await Assert.ThrowsAsync<DeskDoorException>(() => List(new UnixSocketDeskDoor(), stale));
    }
}
