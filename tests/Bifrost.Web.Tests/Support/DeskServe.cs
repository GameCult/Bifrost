using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bifrost.Web.Features.Desk;
using MessagePack;

namespace Bifrost.Web.Tests.Support;

// Serves a vector's own wire form (messagePackHex) through the real UnixSocketDeskDoor on a socket double: Huginn's
// externally tagged DeskResponse with the vector's bytes spliced in unchanged as the document.
public static class DeskServe
{
    public static byte[] Hex(JsonNode? hex) => Convert.FromHexString((string)hex!);

    // {variant: {key: <raw bytes>, ..., "as_of": asOf}}: the raw bytes ride as they are.
    public static byte[] Variant(string variant, (string Key, byte[] Raw)[] fields, ulong asOf = 1)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var w = new MessagePackWriter(buffer);
        w.WriteMapHeader(1);
        w.Write(variant);
        w.WriteMapHeader(fields.Length + 1);
        foreach (var (key, raw) in fields)
        {
            w.Write(key);
            w.WriteRaw(raw);
        }

        w.Write("as_of");
        w.Write(asOf);
        w.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] Str(string? value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var w = new MessagePackWriter(buffer);
        w.Write(value);
        w.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] Forks(byte[] list) => Variant("Forks", [("list", list)]);

    public static byte[] Fork(byte[] entry) => Variant("Fork", [("entry", entry)]);

    public static byte[] Answered(byte[] answer, string? counted = null) =>
        Variant("Answered", [("answer", answer), ("counted_answer_id", Str(counted))]);

    // A socket that answers every request with these payload bytes, built into a frame from the request's id and operation.
    public static FakeHuginnSocket Socket(byte[] payload, string status = "accepted") =>
        new(FakeHuginnSocket.Build((id, operation) => DeskWire.ResponseFrame(id, operation, status, payload)));

    private static readonly DeskDoorContext Context = new("gamecult-chat", "Member", "5f0c2b1e-8a4d-4c3b-9e7f-1a2b3c4d5e6f", ["5f0c2b1e-8a4d-4c3b-9e7f-1a2b3c4d5e6f"]);

    public static async Task<ForkList> ListAsync(byte[] listBytes, string mind = "gamecult-chat")
    {
        using var huginn = Socket(Forks(listBytes));
        var outcome = await new UnixSocketDeskDoor().ListAsync(huginn.Path, Context with { Mind = mind }, new DeskListRequest(mind, "open", null, 50), CancellationToken.None);
        return Assert.IsType<DeskListOutcome.Listed>(outcome).List;
    }

    public static async Task<ForkEntry> ReadAsync(byte[] entryBytes)
    {
        using var huginn = Socket(Fork(entryBytes));
        // The route names the fork the entry's card names, which is what a real caller's route is.
        var forkId = JsonNode.Parse(MessagePackSerializer.ConvertToJson(entryBytes)) is JsonObject { } entry
            && entry["card"] is JsonObject { } card && (string?)card["fork_id"] is { } named ? named : OpenCountedFork;
        var outcome = await new UnixSocketDeskDoor().ReadAsync(huginn.Path, Context, forkId, CancellationToken.None);
        return Assert.IsType<DeskReadOutcome.Found>(outcome).Entry;
    }

    public static async Task<DeskAnswerDocument> AnswerAsync(byte[] answerBytes)
    {
        using var huginn = Socket(Answered(answerBytes));
        var outcome = await new UnixSocketDeskDoor().AnswerAsync(huginn.Path, Context, DeskReplay.AnswerBody(DeskReplay.ChatFork), CancellationToken.None);
        return Assert.IsType<DeskAnswerOutcome.Answered>(outcome).Result.Answer;
    }

    // What leaves the door as JSON text, written the way the edge writes it.
    public static string Text<T>(T value) => JsonSerializer.SerializeToNode(value, DeskJson.Options)!.ToJsonString(DeskJson.Options);

    public static string Text(JsonNode node) => node.ToJsonString(DeskJson.Options);

    // The app's own configuration with one Mind on the socket double and every other Mind on a path no one listens on.
    public static Dictionary<string, string?> ConfigurationOn(string mind, string socket)
    {
        var configuration = DeskWebApplicationFactory.DefaultConfiguration();
        foreach (var key in configuration.Keys.Where(k => k.EndsWith(":Id", StringComparison.Ordinal)).ToList())
        {
            configuration[key[..^2] + "Socket"] = configuration[key] == mind ? socket : FakeHuginnSocket.Absent();
        }

        return configuration;
    }

    // An entry map whose card and record bytes are the given ones, untouched.
    public static byte[] EntryOf(byte[] card, byte[] record)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var w = new MessagePackWriter(buffer);
        w.WriteMapHeader(3);
        w.Write("schemaVersion");
        w.Write("gamecult.fork.entry.v0");
        w.Write("card");
        w.WriteRaw(card);
        w.Write("record");
        w.WriteRaw(record);
        w.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] FromJson(JsonNode node) => MessagePackSerializer.ConvertFromJson(node.ToJsonString(DeskJson.Options));

    // A list page of one entry, as JSON (the shape the door takes), from a vendored CultLib entry file.
    public static JsonObject PageOf(JsonNode entry, string mind = "gamecult-chat") =>
        JsonNode.Parse($$"""{"schemaVersion":"gamecult.fork.list.v0","mind":"{{mind}}","stamp":"s7","open_count":1,"recorded_count":0,"forks":[{{entry.ToJsonString(DeskJson.Options)}}],"next_cursor":null}""")!.AsObject();

    public static JsonObject Entry(string file) =>
        JsonNode.Parse(MessagePackSerializer.ConvertToJson(File.ReadAllBytes(Path.Combine(DeskFeedFixturePinTests.Directory, "cultlib-d3025d3c", file + ".msgpack"))))!.AsObject();

    // The reply payload exactly as given, asked as a list for "gamecult-chat"; throws what the door throws.
    public static async Task<DeskListOutcome> ListPayloadAsync(byte[] payload)
    {
        using var huginn = Socket(payload);
        return await new UnixSocketDeskDoor().ListAsync(huginn.Path, Context, new DeskListRequest("gamecult-chat", "open", null, 50), CancellationToken.None);
    }

    // The reply payload exactly as given, asked as a read.
    public static async Task<DeskReadOutcome> ReadPayloadAsync(byte[] payload)
    {
        using var huginn = Socket(payload);
        return await new UnixSocketDeskDoor().ReadAsync(huginn.Path, Context, OpenCountedFork, CancellationToken.None);
    }

    // The fork the vectors' entry-open-counted names: the route of a read whose reply is a canned payload.
    private static string OpenCountedFork => (string)Entry("entry-open-counted")["card"]!["fork_id"]!;

    public static async Task<DeskAnswerOutcome> AnswerPayloadAsync(byte[] payload)
    {
        using var huginn = Socket(payload);
        return await new UnixSocketDeskDoor().AnswerAsync(huginn.Path, Context, DeskReplay.AnswerBody(DeskReplay.ChatFork), CancellationToken.None);
    }

    // A JSON text becomes MessagePack with the one str `marker` (at most 31 bytes) replaced by `with`: a bin, bytes that
    // are not UTF-8. JSON cannot spell either, so the marker stands where the test wants one.
    public static byte[] WithMarkerReplaced(JsonNode json, string marker, byte[] with)
    {
        var bytes = MessagePackSerializer.ConvertFromJson(json.ToJsonString(DeskJson.Options));
        var needle = new byte[] { (byte)(0xa0 | marker.Length) }.Concat(System.Text.Encoding.ASCII.GetBytes(marker)).ToArray();
        var at = bytes.AsSpan().IndexOf(needle);
        Assert.True(at >= 0 && bytes.AsSpan(at + 1).IndexOf(needle) < 0, "the marker is in the bytes exactly once");
        return [.. bytes[..at], .. with, .. bytes[(at + needle.Length)..]];
    }

    public static byte[] Bin(int length) => [0xc4, (byte)length, .. Enumerable.Repeat((byte)'x', length)];
}
