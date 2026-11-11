using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bifrost.Web.Features.Desk;

// The edge's judgment of an answer body (fork-card.md, answer_request, steps 1 and 2). It checks the shape only:
// whether the answer is legal (a retraction's target, the choice, the fork) is Huginn's. A body that passes is
// forwarded as the bytes received; the parsed request is returned for the door's context and for tests.
public sealed record DeskBodyVerdict(AnswerRequest? Request, string? RefusalCode)
{
    public static DeskBodyVerdict Pass(AnswerRequest request) => new(request, null);

    public static DeskBodyVerdict Refuse(string code) => new(null, code);
}

public static partial class DeskBodyShape
{
    public const int MaxNoteBytes = 4000;

    // Two keys the receiver alone stamps. Refused whole at any depth, never stripped.
    private static readonly string[] ForbiddenKeys = ["answerer", "authority"];

    private static readonly string[] TopKeys =
        ["schemaVersion", "fork_id", "options_hash", "answer", "retracted", "retracts", "answered_at", "request_id"];

    private static readonly string[] AnswerKeys = ["choice", "note"];

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = 32 };

    public static DeskBodyVerdict Judge(ReadOnlySpan<byte> body, int maxBodyBytes)
    {
        if (body.Length > maxBodyBytes)
        {
            return DeskBodyVerdict.Refuse(DeskRefusals.BodyInvalid);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body.ToArray(), ParseOptions);
        }
        catch (JsonException)
        {
            return DeskBodyVerdict.Refuse(DeskRefusals.BodyInvalid);
        }

        using (document)
        {
            try
            {
                return JudgeParsed(document.RootElement);
            }
            catch (InvalidOperationException)
            {
                // A string the parser accepted but cannot decode: invalid UTF-8 or a lone surrogate.
                return DeskBodyVerdict.Refuse(DeskRefusals.BodyInvalid);
            }
        }
    }

    private static DeskBodyVerdict JudgeParsed(JsonElement root)
    {
        if (HasForbiddenKey(root))
        {
            return DeskBodyVerdict.Refuse(DeskRefusals.BodyForbiddenField);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return DeskBodyVerdict.Refuse(DeskRefusals.BodyInvalid);
        }

        var top = Members(root);
        if (top is null)
        {
            return DeskBodyVerdict.Refuse(DeskRefusals.BodyInvalid);
        }

        // Unknown keys, including inside the answer object, before any value is judged.
        if (top.Keys.Any(key => !TopKeys.Contains(key, StringComparer.Ordinal)))
        {
            return DeskBodyVerdict.Refuse(DeskRefusals.BodyUnknownField);
        }

        Dictionary<string, JsonElement>? answerMembers = null;
        if (top.TryGetValue("answer", out var answerElement) && answerElement.ValueKind == JsonValueKind.Object)
        {
            answerMembers = Members(answerElement);
            if (answerMembers is not null &&
                answerMembers.Keys.Any(key => !AnswerKeys.Contains(key, StringComparer.Ordinal)))
            {
                return DeskBodyVerdict.Refuse(DeskRefusals.BodyUnknownField);
            }
        }

        return Shape(top, answerMembers);
    }

    private static DeskBodyVerdict Shape(Dictionary<string, JsonElement> top, Dictionary<string, JsonElement>? answerMembers)
    {
        var invalid = DeskBodyVerdict.Refuse(DeskRefusals.BodyInvalid);

        // Map-mode: every key is written.
        if (TopKeys.Any(key => !top.ContainsKey(key)))
        {
            return invalid;
        }

        if (!IsString(top["schemaVersion"], out var schemaVersion) || schemaVersion != DeskSchemas.AnswerRequest ||
            !IsString(top["fork_id"], out var forkId) || forkId.Length == 0 ||
            !IsString(top["options_hash"], out var optionsHash) || !Hex64().IsMatch(optionsHash) ||
            !IsString(top["answered_at"], out var answeredAt) || !Instant().IsMatch(answeredAt) ||
            !IsString(top["request_id"], out var requestId) || !RequestId().IsMatch(requestId))
        {
            return invalid;
        }

        var retractedElement = top["retracted"];
        if (retractedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return invalid;
        }

        var retracted = retractedElement.ValueKind == JsonValueKind.True;

        string? retracts = null;
        if (top["retracts"].ValueKind != JsonValueKind.Null)
        {
            if (!IsString(top["retracts"], out var target) || !AnswerId().IsMatch(target))
            {
                return invalid;
            }

            retracts = target;
        }

        AnswerObject? answer = null;
        if (top["answer"].ValueKind != JsonValueKind.Null)
        {
            if (answerMembers is null || AnswerKeys.Any(key => !answerMembers.ContainsKey(key)))
            {
                return invalid;
            }

            string? choice = null;
            if (answerMembers["choice"].ValueKind != JsonValueKind.Null)
            {
                if (!IsString(answerMembers["choice"], out var picked) || picked.Length == 0)
                {
                    return invalid;
                }

                choice = picked;
            }

            string? note = null;
            if (answerMembers["note"].ValueKind != JsonValueKind.Null)
            {
                if (!IsString(answerMembers["note"], out var words) || !IsWords(words))
                {
                    return invalid;
                }

                note = words;
            }

            if (choice is null && note is null)
            {
                return invalid;
            }

            answer = new AnswerObject(choice, note);
        }

        // No answer and no retraction is no request. The illegal retraction forms (a target-less retraction, a
        // retraction carrying an answer, a target on a plain answer) pass here: Huginn stores them as bad_retraction.
        if (answer is null && !retracted)
        {
            return invalid;
        }

        return DeskBodyVerdict.Pass(new AnswerRequest(
            schemaVersion, forkId, optionsHash, answer, retracted, retracts, answeredAt, requestId));
    }

    // A note is 1 to 4000 UTF-8 bytes and not whitespace only. Bytes, not characters or UTF-16 units.
    private static bool IsWords(string value)
    {
        int bytes;
        try
        {
            bytes = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        return bytes is >= 1 and <= MaxNoteBytes && !value.All(IsSchemaSpace);
    }

    // The whitespace set of the schema's `words` pattern.
    private static bool IsSchemaSpace(char c) =>
        c is (>= (char)0x09 and <= (char)0x0d) or (char)0x20 or (char)0x85 or (char)0xa0 or (char)0x1680
            or (>= (char)0x2000 and <= (char)0x200a) or (char)0x2028 or (char)0x2029 or (char)0x202f or (char)0x205f or (char)0x3000;

    private static bool IsString(JsonElement element, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return true;
    }

    // Object members by unescaped name; null when a name repeats, because the forwarded bytes would then mean
    // different things to different parsers.
    private static Dictionary<string, JsonElement>? Members(JsonElement element)
    {
        var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!members.TryAdd(property.Name, property.Value))
            {
                return null;
            }
        }

        return members;
    }

    private static bool HasForbiddenKey(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (ForbiddenKeys.Contains(property.Name, StringComparer.Ordinal) || HasForbiddenKey(property.Value))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.Array:
                return element.EnumerateArray().Any(HasForbiddenKey);
            default:
                return false;
        }
    }

    [GeneratedRegex(@"^[0-9a-f]{64}\z")]
    private static partial Regex Hex64();

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?Z\z")]
    private static partial Regex Instant();

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z")]
    private static partial Regex RequestId();

    [GeneratedRegex(@"^n[1-9][0-9]*\z")]
    private static partial Regex AnswerId();
}
