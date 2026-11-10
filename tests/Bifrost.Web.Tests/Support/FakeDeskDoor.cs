using System.Text.Json.Nodes;
using Bifrost.Web.Features.Desk;

namespace Bifrost.Web.Tests.Support;

public sealed record DeskDoorCall(string Kind, string Socket, DeskDoorContext Context, byte[]? Body, string? ForkId);

// The door as a test double: it records every call, holds the forks each socket's Mind holds, and by default stores
// an answer to a held fork as Counted. A test may script the outcome or make the door fail before or after storing.
public sealed class FakeDeskDoor : IDeskDoor
{
    private readonly object gate = new();

    public List<DeskDoorCall> Calls { get; } = [];

    public List<string> StoredIn { get; } = [];

    public Dictionary<(string Socket, string ForkId), ForkEntry> Forks { get; } = [];

    public Func<string, DeskDoorContext, byte[], DeskAnswerOutcome>? AnswerScript { get; set; }

    // Every call throws (Huginn is down).
    public bool Down { get; set; }

    // The answer is stored, then the door throws (a timeout after send).
    public bool ThrowAfterStore { get; set; }

    public bool ThrowOnRead { get; set; }

    public Task<DeskAnswerOutcome> AnswerAsync(
        string socket, DeskDoorContext context, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        var bytes = body.ToArray();
        lock (gate)
        {
            Calls.Add(new DeskDoorCall("answer", socket, context, bytes, null));
        }

        if (Down)
        {
            throw new IOException("huginn is down");
        }

        var outcome = AnswerScript?.Invoke(socket, context, bytes) ?? HeldForkOutcome(socket, context, bytes);
        if (outcome is DeskAnswerOutcome.Answered)
        {
            lock (gate)
            {
                StoredIn.Add(socket);
            }
        }

        if (ThrowAfterStore)
        {
            throw new TimeoutException("the door timed out after send");
        }

        return Task.FromResult(outcome);
    }

    public Task<DeskReadOutcome> ReadAsync(
        string socket, DeskDoorContext context, string forkId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            Calls.Add(new DeskDoorCall("read", socket, context, null, forkId));
        }

        if (Down || ThrowOnRead)
        {
            throw new IOException("huginn is down");
        }

        return Task.FromResult<DeskReadOutcome>(Forks.TryGetValue((socket, forkId), out var entry)
            ? new DeskReadOutcome.Found(entry)
            : new DeskReadOutcome.NotFound(DeskRefusals.ForkUnknown));
    }

    private DeskAnswerOutcome HeldForkOutcome(string socket, DeskDoorContext context, byte[] body)
    {
        var request = JsonNode.Parse(body)!.AsObject();
        var forkId = (string)request["fork_id"]!;
        if (!Forks.ContainsKey((socket, forkId)))
        {
            return new DeskAnswerOutcome.NotStored(DeskRefusals.ForkUnknown);
        }

        var answer = request["answer"]?.AsObject();
        return new DeskAnswerOutcome.Answered(new AnswerResult(
            DeskSchemas.AnswerResult,
            new DeskAnswerDocument(
                DeskSchemas.DeskAnswer, "n1", forkId, "Counted", null, null, null,
                (string)request["options_hash"]!, (string?)answer?["choice"], (string?)answer?["note"],
                (string)request["answered_at"]!, "2026-11-12T19:02:05Z", false,
                context.Subject, context.Attribution, (string)request["request_id"]!),
            null,
            "n1"));
    }
}
