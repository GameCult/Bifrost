using System.Text.Json.Nodes;
using Bifrost.Web.Features.Desk;
using Bifrost.Web.Tests.Support;

namespace Bifrost.Web.Tests;

// The wire walker's closed maps (Closed in UnixSocketDeskDoor) are the single owner of "every contract key present with
// its type": the typed records carry no JsonRequired of their own. Each key of each contract document, taken from a real
// CultLib entry, is removed and, where the contract does not allow nil, nulled: every one is a door failure.
public sealed class UnixSocketDeskDoorRequiredKeysTests
{
    private static readonly HashSet<string> Nullable = ["next_cursor", "counted_answer_id", "of", "retracts", "code", "choice", "note", "answerer"];

    private static JsonObject Level(JsonObject page, string level) => level switch
    {
        "list" => page,
        "entry" => page["forks"]![0]!.AsObject(),
        "record" => page["forks"]![0]!["record"]!.AsObject(),
        "answer" => page["forks"]![0]!["record"]!["answers"]![0]!.AsObject(),
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    public static TheoryData<string, string> Keys()
    {
        var page = DeskServe.PageOf(DeskServe.Entry("entry-open-counted"));
        var data = new TheoryData<string, string>();
        foreach (var level in new[] { "list", "entry", "record", "answer" })
        {
            foreach (var key in Level(page, level).Select(p => p.Key))
            {
                data.Add(level, key);
            }
        }

        return data;
    }

    private static JsonObject Page() => DeskServe.PageOf(DeskServe.Entry("entry-open-counted"));

    [Theory]
    [MemberData(nameof(Keys))]
    public async Task A_missing_required_key_is_a_door_failure(string level, string key)
    {
        var page = Page();
        Level(page, level).Remove(key);

        await Assert.ThrowsAsync<DeskDoorException>(() => DeskServe.ListPayloadAsync(DeskServe.Forks(DeskServe.FromJson(page))));
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public async Task A_nil_where_the_contract_has_no_nil_is_a_door_failure(string level, string key)
    {
        var page = Page();
        Level(page, level)[key] = null;
        var failing = !Nullable.Contains(key);

        var served = DeskServe.ListPayloadAsync(DeskServe.Forks(DeskServe.FromJson(page)));

        if (failing)
        {
            await Assert.ThrowsAsync<DeskDoorException>(() => served);
        }
        else
        {
            Assert.IsType<DeskListOutcome.Listed>(await served);
        }
    }

    [Theory]
    [InlineData("answerer")]
    [InlineData("answerer_count_is_not_an_answer_key")]
    public async Task A_stored_answer_in_an_answered_reply_needs_the_same_keys(string key)
    {
        var answer = DeskServe.Entry("entry-open-counted")["record"]!["answers"]![0]!.AsObject();
        if (answer.ContainsKey(key))
        {
            answer.Remove(key);
        }
        else
        {
            answer[key] = 1;
        }

        await Assert.ThrowsAsync<DeskDoorException>(() => DeskServe.AnswerPayloadAsync(DeskServe.Answered(DeskServe.FromJson(answer), "n1")));
    }

    [Fact]
    public void The_typed_records_declare_no_required_keys_of_their_own()
    {
        var typed = new[] { typeof(DeskAnswerDocument), typeof(ForkRecord), typeof(ForkEntry), typeof(ForkList), typeof(AnswerResult) };
        var required = typed.SelectMany(t => t.GetProperties()).Where(p => p.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonRequiredAttribute), true).Length > 0);

        Assert.Empty(required.Select(p => p.DeclaringType!.Name + "." + p.Name));
    }
}
