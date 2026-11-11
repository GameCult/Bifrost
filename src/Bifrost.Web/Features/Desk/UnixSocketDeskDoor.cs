using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MessagePack;

namespace Bifrost.Web.Features.Desk;

// The door could not confirm an outcome: no connection, no whole reply in time, or a reply that is not the contract's
// (an error, a failure, a Mind refusal, an unknown variant). The desk edge maps any exception from the door to 503
// `unavailable`; this is the one type the client throws for every one of those, so a failure is never an empty page.
public sealed class DeskDoorException(string message, Exception? inner = null) : Exception(message, inner);

// The production IDeskDoor: a client of Huginn's desk door (Huginn docs/desk-feed-door.md at b299d6f7). One new
// connection per call, one request frame, one reply frame, closed after, all inside one deadline. The client holds no
// connection state, so a dead socket fails only the calls addressed to that Mind.
//
//   frame   = u32 big-endian length || CultNet message (MessagePack map), at most 65536 bytes each way
//   payload = standard base64 of the named MessagePack of Huginn's externally tagged DeskRequest / DeskResponse
//
// The reply is decoded by key, never by position, and strictly: every contract document is a closed map whose keys are
// all present with the right type (the variants' own maps too: as_of is an unsigned integer, as Huginn's DeskResponse
// declares), a key written twice anywhere is a failure, and so is a float, a binary or an extension value (the
// contract has none; integers keep every digit). The card inside an entry is not read beyond
// being a map: it stays an opaque JsonElement, so whatever keys and integers Huginn wrote leave unchanged.
public sealed partial class UnixSocketDeskDoor : IDeskDoor
{
    public const int MaxFrameBytes = 65536;
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(1);

    private const string ServiceId = "huginn.desk";
    private const string RequestSchema = "huginn.desk_request.v1";
    private const string ResponseSchema = "huginn.desk_response.v1";
    private const string Encoding64 = "messagepack-base64";
    private const int MaxMessageIdBytes = 256;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly Func<string> messageIds;

    public UnixSocketDeskDoor()
        : this(() => Guid.NewGuid().ToString("N"))
    {
    }

    public UnixSocketDeskDoor(Func<string> messageIds) => this.messageIds = messageIds;

    public async Task<DeskAnswerOutcome> AnswerAsync(
        string socket, DeskDoorContext context, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        byte[] request;
        try
        {
            // Every key, null and key order of the client's body, as MessagePack.
            request = MessagePackSerializer.ConvertFromJson(StrictUtf8.GetString(body.Span));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new DeskDoorException("the answer body does not convert to the request map", e);
        }

        var payload = Payload("Answer", 5, (ref MessagePackWriter w) =>
        {
            w.Write("mind");
            w.Write(context.Mind);
            w.Write("subject");
            w.Write(context.Subject);
            w.Write("authority");
            w.Write(context.Authority);
            w.Write("deciders");
            Strings(ref w, context.Deciders);
            w.Write("request");
            w.WriteRaw(request);
        });

        var (variant, value) = await ExchangeAsync(socket, "answer", payload, cancellationToken);
        switch (variant)
        {
            case "Answered":
                Closed(value, "Answered", string.Empty, ("answer", Kind.Map), ("counted_answer_id", Kind.TextOrNull), ("as_of", Kind.Unsigned));
                var answer = AnswerDocument(value, "answer");

                // Huginn's reply carries no card: a stale_fork answer gets the current card from the edge's read.
                return new DeskAnswerOutcome.Answered(new AnswerResult(
                    DeskSchemas.AnswerResult, answer, null, OptionalString(value, "counted_answer_id")));
            case "NotStored":
                var code = RefusalCode(value);
                return code is DeskRefusals.ForkTooLarge
                    ? throw new DeskDoorException("fork_too_large is not an answer refusal")
                    : new DeskAnswerOutcome.NotStored(code);
            default:
                throw Unexpected(variant);
        }
    }

    public async Task<DeskReadOutcome> ReadAsync(
        string socket, DeskDoorContext context, string forkId, CancellationToken cancellationToken)
    {
        var payload = Payload("Fork", 4, (ref MessagePackWriter w) =>
        {
            w.Write("mind");
            w.Write(context.Mind);
            w.Write("subject");
            w.Write(context.Subject);
            w.Write("deciders");
            Strings(ref w, context.Deciders);
            w.Write("fork_id");
            w.Write(forkId);
        });

        var (variant, value) = await ExchangeAsync(socket, "fork", payload, cancellationToken);
        switch (variant)
        {
            case "Fork":
                Closed(value, "Fork", string.Empty, ("entry", Kind.Map), ("as_of", Kind.Unsigned));
                return new DeskReadOutcome.Found(Entry(Required(value, "entry"), "entry"));
            case "NotStored":
                var code = RefusalCode(value);
                // The route's fork id, never the refusal's informational text.
                return code == DeskRefusals.ForkTooLarge
                    ? new DeskReadOutcome.ForkTooLarge(forkId)
                    : new DeskReadOutcome.NotFound(code);
            default:
                throw Unexpected(variant);
        }
    }

    public async Task<DeskListOutcome> ListAsync(
        string socket, DeskDoorContext context, DeskListRequest request, CancellationToken cancellationToken)
    {
        var payload = Payload("Forks", 6, (ref MessagePackWriter w) =>
        {
            w.Write("mind");
            w.Write(context.Mind);
            w.Write("subject");
            w.Write(context.Subject);
            w.Write("deciders");
            Strings(ref w, context.Deciders);
            w.Write("status");
            w.Write(request.Status);
            w.Write("cursor");
            w.Write(request.Cursor);
            w.Write("limit");
            w.Write(request.Limit);
        });

        var (variant, value) = await ExchangeAsync(socket, "forks", payload, cancellationToken);
        switch (variant)
        {
            case "Forks":
                Closed(value, "Forks", string.Empty, ("list", Kind.Map), ("as_of", Kind.Unsigned));
                var list = List(Required(value, "list"));
                return list.Mind == context.Mind
                    ? new DeskListOutcome.Listed(list)
                    : throw new DeskDoorException("the list is for another Mind");
            case "NotStored":
                var code = RefusalCode(value);
                if (code != DeskRefusals.ForkTooLarge)
                {
                    return new DeskListOutcome.NotListed(code);
                }

                // The fork and its order exist only in the informational text; anything but the exact form is a fault.
                var text = RequiredString(value, "error");
                var match = ListTooLarge().Match(text);
                if (!match.Success || !int.TryParse(match.Groups["order"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var order))
                {
                    throw new DeskDoorException("fork_too_large does not name its fork and order in the list form");
                }

                return new DeskListOutcome.ForkTooLarge(match.Groups["id"].Value, order);
            default:
                throw Unexpected(variant);
        }
    }

    // fork_too_large: fork <id> at order <N> is past the page budget by itself; ...
    [GeneratedRegex(@"\Afork_too_large: fork (?<id>\S+) at order (?<order>0|[1-9][0-9]*) is past the page budget(?:[ ;][^\r\n]*)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex ListTooLarge();

    // ---- strict documents: a closed map, every key present with its type, then bound to the typed record ----

    private enum Kind
    {
        Text,
        TextOrNull,
        Flag,
        Count,
        Unsigned,
        Map,
        Items,
    }

    private static readonly (string, Kind)[] AnswerKeys =
    [
        ("schemaVersion", Kind.Text), ("answer_id", Kind.Text), ("fork_id", Kind.Text), ("outcome", Kind.Text),
        ("of", Kind.TextOrNull), ("retracts", Kind.TextOrNull), ("code", Kind.TextOrNull), ("options_hash", Kind.Text),
        ("choice", Kind.TextOrNull), ("note", Kind.TextOrNull), ("answered_at", Kind.Text), ("received_at", Kind.Text),
        ("claim_in_future", Kind.Flag), ("answerer", Kind.TextOrNull), ("authority", Kind.Text), ("request_id", Kind.Text),
    ];

    private static void Closed(JsonElement map, string what, string schema, params (string Key, Kind Kind)[] keys)
    {
        if (map.ValueKind != JsonValueKind.Object || map.EnumerateObject().Count() != keys.Length)
        {
            throw new DeskDoorException($"{what} is not a closed map of {keys.Length} keys");
        }

        foreach (var (key, kind) in keys)
        {
            var ok = Property(map, key) is { } v && kind switch
            {
                Kind.Text => v.ValueKind == JsonValueKind.String,
                Kind.TextOrNull => v.ValueKind is JsonValueKind.String or JsonValueKind.Null,
                Kind.Flag => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
                Kind.Count => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n >= 0,
                Kind.Unsigned => v.ValueKind == JsonValueKind.Number && v.TryGetUInt64(out _),
                Kind.Map => v.ValueKind == JsonValueKind.Object,
                _ => v.ValueKind == JsonValueKind.Array,
            };
            if (!ok)
            {
                throw new DeskDoorException($"{what}.{key} is missing or not the contract's type");
            }
        }

        if (schema.Length > 0 && Property(map, "schemaVersion")!.Value.GetString() != schema)
        {
            throw new DeskDoorException($"{what} is not {schema}");
        }
    }

    private static DeskAnswerDocument AnswerDocument(JsonElement value, string what)
    {
        var map = Required(value, what);
        Closed(map, what, DeskSchemas.DeskAnswer, AnswerKeys);
        return Bind<DeskAnswerDocument>(map, what);
    }

    private static ForkEntry Entry(JsonElement map, string what)
    {
        Closed(map, what, "gamecult.fork.entry.v0", ("schemaVersion", Kind.Text), ("card", Kind.Map), ("record", Kind.Map));
        var record = Property(map, "record")!.Value;
        Closed(record, what + ".record", "gamecult.fork.record.v0",
            ("schemaVersion", Kind.Text), ("fork_id", Kind.Text), ("options_hash", Kind.Text), ("counted_answer_id", Kind.TextOrNull),
            ("answerer_count", Kind.Count), ("answers", Kind.Items));
        foreach (var answer in Property(record, "answers")!.Value.EnumerateArray())
        {
            Closed(answer, what + ".record.answers[]", DeskSchemas.DeskAnswer, AnswerKeys);
        }

        return Bind<ForkEntry>(map, what);
    }

    private static ForkList List(JsonElement map)
    {
        Closed(map, "list", "gamecult.fork.list.v0",
            ("schemaVersion", Kind.Text), ("mind", Kind.Text), ("stamp", Kind.Text), ("open_count", Kind.Count),
            ("recorded_count", Kind.Count), ("forks", Kind.Items), ("next_cursor", Kind.TextOrNull));
        foreach (var entry in Property(map, "forks")!.Value.EnumerateArray())
        {
            Entry(entry, "list.forks[]");
        }

        return Bind<ForkList>(map, "list");
    }

    private static T Bind<T>(JsonElement map, string what)
    {
        try
        {
            return map.Deserialize<T>(DeskJson.Options) ?? throw new DeskDoorException($"{what} does not decode");
        }
        catch (JsonException e)
        {
            throw new DeskDoorException($"{what} does not decode", e);
        }
    }

    private static JsonElement Required(JsonElement map, string name) =>
        Property(map, name) ?? throw new DeskDoorException($"{name} is missing");

    private static DeskDoorException Unexpected(string variant) =>
        new($"the door answered with {(variant == "Refused" ? "a Mind refusal" : "a variant this call does not take")}");

    private static string RefusalCode(JsonElement refusal)
    {
        Closed(refusal, "the refusal", DeskSchemas.Refusal, ("schemaVersion", Kind.Text), ("code", Kind.Text), ("error", Kind.Text));
        var code = RequiredString(refusal, "code");
        return code is DeskRefusals.ForkUnknown or DeskRefusals.BodyInvalid or DeskRefusals.RequestIdConflict or DeskRefusals.ForkTooLarge
            ? code
            : throw new DeskDoorException("the refusal code is not one the contract names");
    }

    // ---- request ----

    private delegate void FieldWriter(ref MessagePackWriter writer);

    private static byte[] Payload(string variant, int fields, FieldWriter write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var w = new MessagePackWriter(buffer);
        w.WriteMapHeader(1);
        w.Write(variant);
        w.WriteMapHeader(fields);
        write(ref w);
        w.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static void Strings(ref MessagePackWriter w, IReadOnlyList<string> values)
    {
        w.WriteArrayHeader(values.Count);
        foreach (var value in values)
        {
            w.Write(value);
        }
    }

    // Operation messages write their keys sorted, absent optionals omitted.
    private static byte[] RequestMessage(string messageId, string operation, byte[] payload)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var w = new MessagePackWriter(buffer);
        w.WriteMapHeader(7);
        w.Write("messageId");
        w.Write(messageId);
        w.Write("operation");
        w.Write(operation);
        w.Write("payload");
        w.Write(Convert.ToBase64String(payload));
        w.Write("payloadEncoding");
        w.Write(Encoding64);
        w.Write("payloadSchema");
        w.Write(RequestSchema);
        w.Write("schemaVersion");
        w.Write("cultnet.operation_request.v0");
        w.Write("serviceId");
        w.Write(ServiceId);
        w.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    // ---- exchange ----

    private async Task<(string Variant, JsonElement Value)> ExchangeAsync(
        string socketPath, string operation, byte[] payload, CancellationToken cancellationToken)
    {
        var messageId = messageIds();
        if (messageId.Length == 0 || StrictUtf8.GetByteCount(messageId) > MaxMessageIdBytes)
        {
            throw new DeskDoorException("the message id is empty or over 256 bytes");
        }

        var message = RequestMessage(messageId, operation, payload);
        if (message.Length > MaxFrameBytes)
        {
            throw new DeskDoorException("the request is past the frame bound");
        }

        var frame = new byte[4 + message.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)message.Length);
        message.CopyTo(frame, 4);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Deadline);
        byte[] reply;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), deadline.Token);
            for (var sent = 0; sent < frame.Length;)
            {
                sent += await socket.SendAsync(frame.AsMemory(sent), SocketFlags.None, deadline.Token);
            }

            var header = new byte[4];
            await ReadWholeAsync(socket, header, deadline.Token);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (length == 0 || length > MaxFrameBytes)
            {
                // Refused on the prefix alone: the body is never read.
                throw new DeskDoorException("the reply's length prefix is outside the frame bound");
            }

            reply = new byte[length];
            await ReadWholeAsync(socket, reply, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DeskDoorException("no whole reply within the deadline");
        }
        catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException or ArgumentException)
        {
            throw new DeskDoorException("the desk socket failed", e);
        }

        return Decode(reply, messageId, operation);
    }

    // Loops until the buffer is whole: a stream socket may hand it over in pieces. A peer closing early is a failure.
    private static async Task ReadWholeAsync(Socket socket, byte[] buffer, CancellationToken cancellationToken)
    {
        for (var read = 0; read < buffer.Length;)
        {
            var count = await socket.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None, cancellationToken);
            if (count == 0)
            {
                throw new DeskDoorException("the peer closed before a whole reply frame");
            }

            read += count;
        }
    }

    // ---- reply ----

    // The envelope judged by key; the payload's one variant returned with its value. Everything else throws.
    private static (string Variant, JsonElement Value) Decode(byte[] frame, string messageId, string operation)
    {
        try
        {
            using var envelope = ToJson(frame);
            var root = envelope.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new DeskDoorException("the reply is not a map");
            }

            // A CultNet Error message is not an operation reply (and its keys are not in operation order).
            if (RequiredString(root, "schemaVersion") != "cultnet.operation_response.v0")
            {
                throw new DeskDoorException("the reply is not an operation response");
            }

            if (RequiredString(root, "serviceId") != ServiceId
                || RequiredString(root, "operation") != operation
                || RequiredString(root, "messageId") != messageId)
            {
                throw new DeskDoorException("the reply is not for this request");
            }

            var status = RequiredString(root, "status");
            if (RequiredString(root, "payloadSchema") != ResponseSchema || RequiredString(root, "payloadEncoding") != Encoding64)
            {
                // Including gamecult.cultnet.operation_failure.v1: the envelope or payload did not decode.
                throw new DeskDoorException("the reply carries no desk response");
            }

            // Strict standard base64: Convert would skip whitespace, so the text must be the canonical encoding of what it decodes to.
            var text = RequiredString(root, "payload");
            byte[] payload;
            try
            {
                payload = Convert.FromBase64String(text);
            }
            catch (FormatException e)
            {
                throw new DeskDoorException("the payload is not base64", e);
            }

            if (Convert.ToBase64String(payload) != text)
            {
                throw new DeskDoorException("the payload is not strict standard base64");
            }

            using var body = ToJson(payload);
            if (body.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new DeskDoorException("the payload is not a tagged variant");
            }

            var variants = body.RootElement.EnumerateObject().ToList();
            if (variants.Count != 1)
            {
                throw new DeskDoorException("the payload is not a one-key variant");
            }

            var (variant, expectedStatus) = (variants[0].Name, variants[0].Name switch
            {
                "Answered" or "Forks" or "Fork" => "accepted",
                "NotStored" or "Refused" => "rejected",
                _ => null,
            });
            var takesVariant = operation switch
            {
                "answer" => variant == "Answered",
                "forks" => variant == "Forks",
                "fork" => variant == "Fork",
                _ => false,
            } || variant is "NotStored" or "Refused";
            if (expectedStatus is null || !takesVariant || status != expectedStatus)
            {
                throw new DeskDoorException("the reply's variant or status does not fit the operation");
            }

            return (variant, variants[0].Value.Clone());
        }
        catch (Exception e) when (e is not (DeskDoorException or OperationCanceledException))
        {
            throw new DeskDoorException("the reply does not decode", e);
        }
    }

    // MessagePack to JSON text by walking the reader: the JSON writer escapes every control character, keys keep their
    // order, integers keep every digit. A map key that is not text, a key written twice, a float, a binary, an
    // extension, invalid UTF-8 or nesting past 64 is a failure; none is in the contract.
    private static JsonDocument ToJson(ReadOnlyMemory<byte> messagePack)
    {
        var reader = new MessagePackReader(messagePack);
        var json = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(json, new JsonWriterOptions { MaxDepth = 64 }))
        {
            Walk(ref reader, writer, 0);
        }

        if (!reader.End)
        {
            throw new DeskDoorException("bytes follow the message");
        }

        return JsonDocument.Parse(json.WrittenMemory, new JsonDocumentOptions { MaxDepth = 64 });
    }

    private static void Walk(ref MessagePackReader reader, Utf8JsonWriter writer, int depth)
    {
        if (depth > 64)
        {
            throw new DeskDoorException("the message nests past 64");
        }

        switch (reader.NextMessagePackType)
        {
            case MessagePackType.Nil:
                reader.ReadNil();
                writer.WriteNullValue();
                break;
            case MessagePackType.Boolean:
                writer.WriteBooleanValue(reader.ReadBoolean());
                break;
            case MessagePackType.Integer:
                if (reader.NextCode == MessagePackCode.UInt64)
                {
                    writer.WriteNumberValue(reader.ReadUInt64());
                }
                else
                {
                    writer.WriteNumberValue(reader.ReadInt64());
                }

                break;
            case MessagePackType.String:
                writer.WriteStringValue(Text(ref reader));
                break;
            case MessagePackType.Array:
                var items = reader.ReadArrayHeader();
                writer.WriteStartArray();
                for (var i = 0; i < items; i++)
                {
                    Walk(ref reader, writer, depth + 1);
                }

                writer.WriteEndArray();
                break;
            case MessagePackType.Map:
                var pairs = reader.ReadMapHeader();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                writer.WriteStartObject();
                for (var i = 0; i < pairs; i++)
                {
                    if (reader.NextMessagePackType != MessagePackType.String)
                    {
                        throw new DeskDoorException("a map key is not text");
                    }

                    var key = Text(ref reader);
                    if (!seen.Add(key))
                    {
                        throw new DeskDoorException($"{key} is written twice");
                    }

                    writer.WritePropertyName(key);
                    Walk(ref reader, writer, depth + 1);
                }

                writer.WriteEndObject();
                break;
            default:
                throw new DeskDoorException("the message holds a float, binary or extension value, which the contract does not use");
        }
    }

    private static string Text(ref MessagePackReader reader) =>
        StrictUtf8.GetString(reader.ReadStringSequence() ?? throw new DeskDoorException("text is nil"));

    // A key's value by name; a key written twice never gets here (the walk refuses it).
    private static JsonElement? Property(JsonElement map, string name) =>
        map.ValueKind == JsonValueKind.Object && map.TryGetProperty(name, out var value) ? value : null;

    private static string RequiredString(JsonElement map, string name) =>
        Property(map, name) is { ValueKind: JsonValueKind.String } value
            ? value.GetString()!
            : throw new DeskDoorException($"{name} is missing or not text");

    private static string? OptionalString(JsonElement map, string name) =>
        Property(map, name) switch
        {
            { ValueKind: JsonValueKind.String } value => value.GetString(),
            { ValueKind: JsonValueKind.Null } => null,
            _ => throw new DeskDoorException($"{name} is missing or not text"),
        };
}
