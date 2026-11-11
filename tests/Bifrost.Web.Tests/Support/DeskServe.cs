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
        var outcome = await new UnixSocketDeskDoor().ReadAsync(huginn.Path, Context, "gamecult-chat:question:slot-mark-consent", CancellationToken.None);
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
}
