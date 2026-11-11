using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using MessagePack;

namespace Bifrost.Web.Tests.Support;

// Huginn's desk door as a test double on a real unix socket: it accepts connections, reads each request frame,
// records it, and lets the test decide what happens next (reply, dribble, stall, close).
public sealed class FakeHuginnSocket : IDisposable
{
    private readonly Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly CancellationTokenSource stop = new();
    private readonly string directory = Directory.CreateTempSubdirectory("hd").FullName;
    private readonly Func<Socket, byte[], CancellationToken, Task> handler;

    public FakeHuginnSocket(Func<Socket, byte[], CancellationToken, Task> handler)
    {
        this.handler = handler;
        Path = System.IO.Path.Combine(directory, "d.sock");
        listener.Bind(new UnixDomainSocketEndPoint(Path));
        listener.Listen(16);
        _ = Task.Run(AcceptLoopAsync);
    }

    public string Path { get; }

    // The whole request frame of every connection, header included.
    public List<byte[]> Requests { get; } = [];

    // A path no one listens on.
    public static string Absent() => System.IO.Path.Combine(Directory.CreateTempSubdirectory("hd").FullName, "absent.sock");

    public static byte[] Fixture(string name) =>
        File.ReadAllBytes(System.IO.Path.Combine(DeskFeedFixturePinTests.Directory, name + ".frame.bin"));

    // Writes the bytes and closes.
    public static Func<Socket, byte[], CancellationToken, Task> Replay(byte[] frame) =>
        async (socket, _, token) => await socket.SendAsync(frame, SocketFlags.None, token);

    // Answers each request by its own message id and operation: the reply is built from the request.
    public static Func<Socket, byte[], CancellationToken, Task> Answer(Func<string, JsonDocument, string> variantJson, string status = "accepted") =>
        async (socket, request, token) =>
        {
            using var envelope = DeskWire.Envelope(request);
            var root = envelope.RootElement;
            var operation = root.GetProperty("operation").GetString()!;
            var reply = DeskWire.ResponseFrame(root.GetProperty("messageId").GetString()!, operation, status, variantJson(operation, envelope));
            await socket.SendAsync(reply, SocketFlags.None, token);
        };

    private async Task AcceptLoopAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(stop.Token);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        var header = new byte[4];
                        await client.ReceiveAllAsync(header, stop.Token);
                        var body = new byte[BinaryPrimitives.ReadUInt32BigEndian(header)];
                        await client.ReceiveAllAsync(body, stop.Token);
                        var request = header.Concat(body).ToArray();
                        lock (Requests)
                        {
                            Requests.Add(request);
                        }

                        await handler(client, request, stop.Token);
                    }
                    catch (Exception)
                    {
                        // The client went away, or the test ended.
                    }
                }
            });
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        listener.Dispose();
        try
        {
            Directory.Delete(directory, true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class SocketReceive
{
    public static async Task ReceiveAllAsync(this Socket socket, byte[] buffer, CancellationToken token)
    {
        for (var read = 0; read < buffer.Length;)
        {
            var count = await socket.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None, token);
            if (count == 0)
            {
                throw new EndOfStreamException();
            }

            read += count;
        }
    }
}

// Frames and envelopes as the golden fixtures spell them, for building replies and reading requests.
public static class DeskWire
{
    public static JsonDocument Envelope(byte[] frame)
    {
        var reader = new MessagePackReader(frame.AsMemory(4));
        var json = new StringWriter();
        MessagePackSerializer.ConvertToJson(ref reader, json);
        return JsonDocument.Parse(json.ToString());
    }

    // The base64 payload of an envelope, decoded to JSON.
    public static JsonDocument Payload(JsonDocument envelope)
    {
        var bytes = Convert.FromBase64String(envelope.RootElement.GetProperty("payload").GetString()!);
        var reader = new MessagePackReader(bytes);
        var json = new StringWriter();
        MessagePackSerializer.ConvertToJson(ref reader, json);
        return JsonDocument.Parse(json.ToString());
    }

    // A reply frame in operation-message key order. `mutate` edits the text fields before they are written.
    public static byte[] ResponseFrame(
        string messageId, string operation, string status, string variantJson, Action<SortedDictionary<string, string>>? mutate = null)
    {
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["messageId"] = messageId,
            ["operation"] = operation,
            ["payload"] = Convert.ToBase64String(MessagePackSerializer.ConvertFromJson(variantJson)),
            ["payloadEncoding"] = "messagepack-base64",
            ["payloadSchema"] = "huginn.desk_response.v1",
            ["schemaVersion"] = "cultnet.operation_response.v0",
            ["serviceId"] = "huginn.desk",
            ["sourceRuntimeId"] = "huginn-test",
            ["status"] = status,
        };
        mutate?.Invoke(fields);
        var buffer = new ArrayBufferWriter<byte>();
        var w = new MessagePackWriter(buffer);
        w.WriteMapHeader(fields.Count);
        foreach (var (key, value) in fields)
        {
            w.Write(key);
            w.Write(value);
        }

        w.Flush();
        return Frame(buffer.WrittenSpan);
    }

    public static byte[] Frame(ReadOnlySpan<byte> message)
    {
        var frame = new byte[4 + message.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)message.Length);
        message.CopyTo(frame.AsSpan(4));
        return frame;
    }
}
