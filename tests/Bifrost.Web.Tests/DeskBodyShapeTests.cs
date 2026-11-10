using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bifrost.Web.Features.Desk;
using Bifrost.Web.Tests.Support;

namespace Bifrost.Web.Tests;

public sealed class DeskBodyShapeTests
{
    private const int Cap = 16 * 1024;

    private static DeskBodyVerdict Judge(JsonNode body, int cap = Cap) => DeskBodyShape.Judge(DeskVectors.Bytes(body), cap);

    private static DeskBodyVerdict Judge(string json, int cap = Cap) => DeskBodyShape.Judge(Encoding.UTF8.GetBytes(json), cap);

    private static JsonObject ValidBody() =>
        DeskVectors.Rows("request", "valid").First(row => (string)row["name"]! == "choice_and_note")["message"]!.DeepClone().AsObject();

    private static string ExpectedCode(string name) => name switch
    {
        "answerer_key" or "authority_key" => DeskRefusals.BodyForbiddenField,
        "answer_object_with_a_key" => DeskRefusals.BodyUnknownField,
        _ => DeskRefusals.BodyInvalid,
    };

    public static IEnumerable<object[]> ValidRows() =>
        DeskVectors.Rows("request", "valid").Select(row => new object[] { (string)row["name"]! });

    public static IEnumerable<object[]> InvalidRows() =>
        DeskVectors.Rows("request", "invalid").Select(row => new object[] { (string)row["name"]! });

    [Theory]
    [MemberData(nameof(ValidRows))]
    public void Valid_request_vectors_pass(string name)
    {
        var message = DeskVectors.Rows("request", "valid").First(row => (string)row["name"]! == name)["message"]!;

        var verdict = Judge(message);

        Assert.Null(verdict.RefusalCode);
        Assert.NotNull(verdict.Request);
        // The parsed request is the document: same keys, same order, same values.
        var echoed = JsonSerializer.SerializeToNode(verdict.Request, DeskJson.Options);
        Assert.True(DeskVectors.SameShape(message, echoed), $"{name} did not round-trip: {echoed}");
    }

    [Theory]
    [MemberData(nameof(InvalidRows))]
    public void Invalid_request_vectors_refuse_with_their_code(string name)
    {
        var message = DeskVectors.Rows("request", "invalid").First(row => (string)row["name"]! == name)["message"]!;

        var verdict = Judge(message);

        Assert.Null(verdict.Request);
        Assert.Equal(ExpectedCode(name), verdict.RefusalCode);
    }

    [Fact]
    public void Forbidden_key_at_any_depth_beats_unknown_key()
    {
        foreach (var key in new[] { "answerer", "authority" })
        {
            var nested = ValidBody();
            nested["answer"]!["detail"] = new JsonObject { ["deep"] = new JsonArray(new JsonObject { [key] = "x" }) };
            nested["extra"] = 1;
            Assert.Equal(DeskRefusals.BodyForbiddenField, Judge(nested).RefusalCode);

            var top = ValidBody();
            top["unknown"] = 1;
            top[key] = "someone";
            Assert.Equal(DeskRefusals.BodyForbiddenField, Judge(top).RefusalCode);

            // Written with an escape: the name is the same once decoded, and still refused whole.
            var escaped = DeskVectors.Bytes(ValidBody()).ToArray();
            var text = Encoding.UTF8.GetString(escaped).TrimEnd('}') + $",\"{key[..3]}\\u{(int)key[3]:x4}{key[4..]}\":\"x\"}}";
            Assert.Equal(DeskRefusals.BodyForbiddenField, Judge(text).RefusalCode);
        }

        // The key name is matched exactly: a near miss is only an unknown key.
        var near = ValidBody();
        near["Answerer"] = "x";
        Assert.Equal(DeskRefusals.BodyUnknownField, Judge(near).RefusalCode);
    }

    [Fact]
    public void Mind_and_deciders_in_the_body_are_unknown_fields()
    {
        foreach (var key in new[] { "mind", "deciders" })
        {
            var body = ValidBody();
            body[key] = "x";
            Assert.Equal(DeskRefusals.BodyUnknownField, Judge(body).RefusalCode);
        }
    }

    [Fact]
    public void Illegal_retraction_forms_reach_the_door()
    {
        var forms = new (string Answer, string Retracted, string Retracts)[]
        {
            ("null", "true", "null"),                                    // a retraction with no target
            ("{\"choice\":\"no\",\"note\":null}", "true", "\"n1\""),     // a retraction carrying an answer
            ("{\"choice\":\"no\",\"note\":null}", "false", "\"n1\""),    // a plain answer with a target
            ("null", "true", "\"n1\""),                                  // the legal retraction
        };

        foreach (var (answer, retracted, retracts) in forms)
        {
            var body = ValidBody();
            body["answer"] = JsonNode.Parse(answer);
            body["retracted"] = JsonNode.Parse(retracted);
            body["retracts"] = JsonNode.Parse(retracts);

            var verdict = Judge(body);

            Assert.True(verdict.RefusalCode is null, $"answer={answer} retracted={retracted} retracts={retracts}: {verdict.RefusalCode}");
            Assert.NotNull(verdict.Request);
        }
    }

    [Fact]
    public void Neither_answer_nor_retraction_is_body_invalid()
    {
        foreach (var retracts in new[] { "null", "\"n1\"" })
        {
            var body = ValidBody();
            body["answer"] = null;
            body["retracted"] = false;
            body["retracts"] = JsonNode.Parse(retracts);

            Assert.Equal(DeskRefusals.BodyInvalid, Judge(body).RefusalCode);
        }
    }

    [Fact]
    public void Note_limits_are_utf8_bytes()
    {
        string Note(string text) => Judge(WithNote(text)).RefusalCode ?? "pass";

        string Repeat(string unit, int count) => string.Concat(Enumerable.Repeat(unit, count));
        var astral = char.ConvertFromUtf32(0x1F600);                  // 4 bytes
        var combining = "e" + (char)0x0301;                           // 3 bytes
        var twoByte = ((char)0xe9).ToString();                        // 2 bytes
        var spaces = new string(new[] { (char)9, (char)0xa0, (char)0x3000, (char)0x2028 });

        Assert.Equal("pass", Note(new string('x', 4000)));
        Assert.Equal(DeskRefusals.BodyInvalid, Note(new string('x', 4001)));
        Assert.Equal("pass", Note(Repeat(astral, 1000)));
        Assert.Equal(DeskRefusals.BodyInvalid, Note(Repeat(astral, 1001)));
        Assert.Equal("pass", Note(Repeat(combining, 1333) + "x"));
        Assert.Equal(DeskRefusals.BodyInvalid, Note(Repeat(combining, 1334)));
        Assert.Equal(DeskRefusals.BodyInvalid, Note(Repeat(twoByte, 1999) + "xxx"));
        Assert.Equal("pass", Note("x"));
        Assert.Equal(DeskRefusals.BodyInvalid, Note(""));
        Assert.Equal(DeskRefusals.BodyInvalid, Note(spaces));
        Assert.Equal("pass", Note(" a "));

        static JsonNode WithNote(string text)
        {
            var body = ValidBody();
            body["answer"] = new JsonObject { ["choice"] = null, ["note"] = text };
            return body;
        }
    }

    [Fact]
    public void Lone_surrogate_in_a_note_is_body_invalid()
    {
        var json = DeskVectors.Bytes(ValidBody()).ToArray();
        var text = Encoding.UTF8.GetString(json).Replace("\"note\":\"", "\"note\":\"\\ud800");
        Assert.Equal(DeskRefusals.BodyInvalid, Judge(text).RefusalCode);
    }

    [Fact]
    public void Over_cap_refused_before_parse()
    {
        var body = ValidBody();
        var bytes = DeskVectors.Bytes(body);

        Assert.Null(DeskBodyShape.Judge(bytes, bytes.Length).RefusalCode);
        Assert.Equal(DeskRefusals.BodyInvalid, DeskBodyShape.Judge(bytes, bytes.Length - 1).RefusalCode);

        // Over the cap the forbidden key is never found, because the body is never parsed.
        body["answerer"] = "x";
        var forbidden = DeskVectors.Bytes(body);
        Assert.Equal(DeskRefusals.BodyForbiddenField, DeskBodyShape.Judge(forbidden, forbidden.Length).RefusalCode);
        Assert.Equal(DeskRefusals.BodyInvalid, DeskBodyShape.Judge(forbidden, forbidden.Length - 1).RefusalCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    [InlineData("{")]
    public void Non_object_or_broken_json_is_body_invalid(string json) =>
        Assert.Equal(DeskRefusals.BodyInvalid, Judge(json).RefusalCode);

    [Fact]
    public void Invalid_utf8_is_body_invalid()
    {
        var bytes = DeskVectors.Bytes(ValidBody());
        var bad = bytes.Take(bytes.Length - 2).Concat(new byte[] { 0xff }).Concat(bytes.Skip(bytes.Length - 2)).ToArray();
        Assert.Equal(DeskRefusals.BodyInvalid, DeskBodyShape.Judge(bad, Cap).RefusalCode);
    }

    [Fact]
    public void Repeated_key_is_body_invalid()
    {
        var text = Encoding.UTF8.GetString(DeskVectors.Bytes(ValidBody())).TrimEnd('}') + ",\"fork_id\":\"other\"}";
        Assert.Equal(DeskRefusals.BodyInvalid, Judge(text).RefusalCode);

        var answer = Encoding.UTF8.GetString(DeskVectors.Bytes(ValidBody())).Replace("\"choice\":\"no\"", "\"choice\":\"no\",\"choice\":\"yes\"");
        Assert.Equal(DeskRefusals.BodyInvalid, Judge(answer).RefusalCode);
    }

    [Fact]
    public void Trailing_newline_does_not_rescue_a_bad_pattern()
    {
        var body = ValidBody();
        body["request_id"] = ((string)body["request_id"]!) + "\n";
        Assert.Equal(DeskRefusals.BodyInvalid, Judge(body).RefusalCode);

        body = ValidBody();
        body["options_hash"] = ((string)body["options_hash"]!) + "\n";
        Assert.Equal(DeskRefusals.BodyInvalid, Judge(body).RefusalCode);
    }

    [Fact]
    public void Wrong_schema_version_and_bad_retracts_are_body_invalid()
    {
        var body = ValidBody();
        body["schemaVersion"] = "gamecult.fork.answer_request.v1";
        Assert.Equal(DeskRefusals.BodyInvalid, Judge(body).RefusalCode);

        foreach (var retracts in new[] { "n0", "n01", "x1", "n" })
        {
            body = ValidBody();
            body["answer"] = null;
            body["retracted"] = true;
            body["retracts"] = retracts;
            Assert.Equal(DeskRefusals.BodyInvalid, Judge(body).RefusalCode);
        }
    }

    [Fact]
    public void Missing_key_is_body_invalid_for_each_key()
    {
        foreach (var key in ValidBody().Select(p => p.Key).ToList())
        {
            var body = ValidBody();
            body.Remove(key);
            Assert.Equal(DeskRefusals.BodyInvalid, Judge(body).RefusalCode);
        }
    }

    [Fact]
    public void Answer_object_must_hold_both_keys()
    {
        var body = ValidBody();
        body["answer"] = new JsonObject { ["choice"] = "no" };
        Assert.Equal(DeskRefusals.BodyInvalid, Judge(body).RefusalCode);
    }
}
