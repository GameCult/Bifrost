using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bifrost.Web.Features.Desk;

// The fork-desk documents Bifrost reads or writes, spelled as CultLib's contracts/cultnet/governance/fork-card.md
// (commit d3025d3c) spells them: snake_case keys, every key written (nil where absent), declared in the
// contract's key order. Bifrost authors only DeskRefusal and DeskUnavailable. AnswerRequest is the client's body;
// DeskAnswerDocument, ForkList and AnswerResult are Huginn's documents, typed here so they pass through unchanged.
// The card (gamecult.fork.card.v0) is deliberately NOT typed: the Huginn door owns what a reader's card holds (the
// decider view carries ids, the non-decider view withholds them and keeps decider_count), and Bifrost neither reads
// nor edits it, so it is an opaque JsonElement that keeps every key, key order and integer. The door refuses a float,
// a binary or an extension value anywhere in a reply (the contract has none), so no number is ever rewritten.

public static class DeskSchemas
{
    public const string AnswerRequest = "gamecult.fork.answer_request.v0";
    public const string AnswerResult = "gamecult.fork.answer_result.v0";
    public const string Card = "gamecult.fork.card.v0";
    public const string DeskAnswer = "gamecult.desk_answer.v0";
    public const string Refusal = "gamecult.fork.refusal.v0";
    public const string Unavailable = "gamecult.fork.unavailable.v0";
}

public static class DeskJson
{
    // Defaults write nulls, which map-mode needs ("every key written").
    public static readonly JsonSerializerOptions Options = new()
    {
        // The documents are not HTML: keep apostrophes and non-ASCII text as written.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

public sealed record AnswerObject(
    [property: JsonPropertyName("choice")] string? Choice,
    [property: JsonPropertyName("note")] string? Note);

public sealed record AnswerRequest(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("fork_id")] string ForkId,
    [property: JsonPropertyName("options_hash")] string OptionsHash,
    [property: JsonPropertyName("answer")] AnswerObject? Answer,
    [property: JsonPropertyName("retracted")] bool Retracted,
    [property: JsonPropertyName("retracts")] string? Retracts,
    [property: JsonPropertyName("answered_at")] string AnsweredAt,
    [property: JsonPropertyName("request_id")] string RequestId);

public sealed record DeskRefusal(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("error")] string Error);

public sealed record DeskUnavailable(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("retryAfterSeconds")] int? RetryAfterSeconds);

// One stored answer: Counted, Revision, Retraction or Refused, told apart by Outcome and Code.
public sealed record DeskAnswerDocument(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("answer_id")] string AnswerId,
    [property: JsonPropertyName("fork_id")] string ForkId,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("of")] string? Of,
    [property: JsonPropertyName("retracts")] string? Retracts,
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("options_hash")] string OptionsHash,
    [property: JsonPropertyName("choice")] string? Choice,
    [property: JsonPropertyName("note")] string? Note,
    [property: JsonPropertyName("answered_at")] string AnsweredAt,
    [property: JsonPropertyName("received_at")] string ReceivedAt,
    [property: JsonPropertyName("claim_in_future")] bool ClaimInFuture,
    // Nil in a non-decider's view (Huginn withholds the ids). The key is required, and the door's wire walker (Closed in
    // UnixSocketDeskDoor) is the one owner of that: a missing key is a door fault, never a nil Bifrost invented.
    [property: JsonPropertyName("answerer")] string? Answerer,
    [property: JsonPropertyName("authority")] string Authority,
    [property: JsonPropertyName("request_id")] string RequestId);

public sealed record ForkRecord(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("fork_id")] string ForkId,
    [property: JsonPropertyName("options_hash")] string OptionsHash,
    [property: JsonPropertyName("counted_answer_id")] string? CountedAnswerId,
    [property: JsonPropertyName("answerer_count")] int AnswererCount,
    [property: JsonPropertyName("answers")] DeskAnswerDocument[] Answers);

public sealed record ForkEntry(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("card")] JsonElement Card,
    [property: JsonPropertyName("record")] ForkRecord Record);

public sealed record ForkList(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("mind")] string Mind,
    [property: JsonPropertyName("stamp")] string Stamp,
    [property: JsonPropertyName("open_count")] int OpenCount,
    [property: JsonPropertyName("recorded_count")] int RecordedCount,
    [property: JsonPropertyName("forks")] ForkEntry[] Forks,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

public sealed record AnswerResult(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("answer")] DeskAnswerDocument Answer,
    [property: JsonPropertyName("card")] JsonElement? Card,
    [property: JsonPropertyName("counted_answer_id")] string? CountedAnswerId);

// The refusal codes Bifrost itself writes, each with its one status and message (vectors doorRefusals).
public static class DeskRefusals
{
    public const string Unauthenticated = "unauthenticated";
    public const string BodyForbiddenField = "body_forbidden_field";
    public const string BodyUnknownField = "body_unknown_field";
    public const string BodyInvalid = "body_invalid";
    public const string ForkUnknown = "fork_unknown";
    public const string InsufficientScope = "insufficient_scope";
    public const string Forbidden = "forbidden";
    public const string RequestInvalid = "request_invalid";
    public const string RequestIdConflict = "request_id_conflict";

    private static readonly Dictionary<string, (int Status, string Error)> Table = new(StringComparer.Ordinal)
    {
        [Unauthenticated] = (401, "unauthenticated: a verified session is required"),
        [BodyForbiddenField] = (400, "body_forbidden_field: answerer and authority are set by the receiver"),
        [BodyUnknownField] = (400, "body_unknown_field: the body is a closed shape"),
        [BodyInvalid] = (400, "body_invalid: the body does not satisfy gamecult.fork.answer_request.v0"),
        [ForkUnknown] = (404, "fork_unknown: no such fork"),
        [InsufficientScope] = (403, "insufficient_scope: the token lacks the scope this route needs"),
        [Forbidden] = (403, "forbidden: only Active members may use the desk"),
        [RequestInvalid] = (400, "request_invalid: the request does not satisfy its document"),
        [RequestIdConflict] = (409, "request_id_conflict: this request_id was used for a different body"),
    };

    // fork_too_large names its fork, so it is built here, not read from the table; a bare door code of that name is
    // never a refusal. The texts are the pinned vectors' (alone is past the response budget).
    public const string ForkTooLarge = "fork_too_large";

    public static int ForkTooLargeStatus => 422;

    // A list page whose next fork alone is past the budget: the client continues with cursor c<order>; at order 0
    // it has reached the end.
    public static DeskRefusal ForkTooLargeDocument(string forkId, int order) => new(
        DeskSchemas.Refusal, ForkTooLarge, $"fork_too_large: fork {forkId} (order {order}) alone is past the response budget");

    // A read of such a fork: nothing to page, so no order.
    public static DeskRefusal ForkTooLargeDocument(string forkId) => new(
        DeskSchemas.Refusal, ForkTooLarge, $"fork_too_large: fork {forkId} alone is past the response budget");

    public static bool IsKnown(string code) => Table.ContainsKey(code);

    public static int StatusOf(string code) => Table[code].Status;

    public static DeskRefusal Document(string code) => new(DeskSchemas.Refusal, code, Table[code].Error);
}

public static class DeskUnavailables
{
    public const int Status = 503;

    public static DeskUnavailable Document(int? retryAfterSeconds) => new(
        DeskSchemas.Unavailable,
        "unavailable",
        "unavailable: the outcome is not confirmed, try again with the same request_id",
        retryAfterSeconds);
}
