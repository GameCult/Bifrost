using System.Text.Json.Nodes;
using Bifrost.Web.Features.Desk;
using Bifrost.Web.Tests.Support;

namespace Bifrost.Web.Tests;

// The reply's bytes that JSON cannot spell: a MessagePack bin where the contract has text or nil, and text that is not
// UTF-8. Either is a door failure wherever it sits, never a nil or a replacement character Bifrost made up.
public sealed class UnixSocketDeskDoorBytesTests
{
    private const string Marker = "@@MARK@@";

    // Where a value can stand: a nullable key (note, answerer, next_cursor), a text key, and a card position.
    public static TheoryData<string> Positions() => new()
    {
        "note", "answerer", "next_cursor", "card.raised_at", "card.title", "card.options[0].text", "record.counted_answer_id", "mind", "card_key_name",
    };

    private static JsonObject PageWithMarkerAt(string position)
    {
        var page = DeskServe.PageOf(DeskServe.Entry("entry-open-counted"));
        var entry = page["forks"]![0]!.AsObject();
        switch (position)
        {
            case "note": entry["record"]!["answers"]![0]!["note"] = Marker; break;
            case "answerer": entry["record"]!["answers"]![0]!["answerer"] = Marker; break;
            case "next_cursor": page["next_cursor"] = Marker; break;
            case "card.raised_at": entry["card"]!["raised_at"] = Marker; break;
            case "card.title": entry["card"]!["title"] = Marker; break;
            case "card.options[0].text": entry["card"]!["options"]![0]!["text"] = Marker; break;
            case "record.counted_answer_id": entry["record"]!["counted_answer_id"] = Marker; break;
            case "mind": page["mind"] = Marker; break;
            case "card_key_name":
                var card = entry["card"]!.AsObject();
                var rest = card.ToList();
                card.Clear();
                foreach (var (key, value) in rest)
                {
                    card[key == "title" ? Marker : key] = value;
                }

                break;
            default: throw new ArgumentOutOfRangeException(nameof(position));
        }

        return page;
    }

    [Theory]
    [MemberData(nameof(Positions))]
    public async Task A_binary_in_a_nullable_text_or_card_position_is_a_door_failure(string position)
    {
        var bytes = DeskServe.WithMarkerReplaced(PageWithMarkerAt(position), Marker, DeskServe.Bin(3));

        await Assert.ThrowsAsync<DeskDoorException>(() => DeskServe.ListPayloadAsync(DeskServe.Forks(bytes)));
    }

    [Theory]
    [MemberData(nameof(Positions))]
    public async Task Invalid_utf8_in_text_is_a_door_failure(string position)
    {
        // 0xff 0xfe as a str of 2 bytes; Bifrost writes no replacement character.
        var bytes = DeskServe.WithMarkerReplaced(PageWithMarkerAt(position), Marker, [0xa2, 0xff, 0xfe]);

        await Assert.ThrowsAsync<DeskDoorException>(() => DeskServe.ListPayloadAsync(DeskServe.Forks(bytes)));
    }

    [Fact]
    public async Task The_same_marker_as_valid_text_decodes_so_the_failures_above_are_the_bytes_not_the_page()
    {
        foreach (var position in new[] { "note", "answerer", "next_cursor", "card.raised_at", "card.title" })
        {
            var bytes = DeskServe.WithMarkerReplaced(PageWithMarkerAt(position), Marker, [.. new byte[] { 0xa2 }, .. "ok"u8.ToArray()]);

            Assert.IsType<DeskListOutcome.Listed>(await DeskServe.ListPayloadAsync(DeskServe.Forks(bytes)));
        }
    }

    [Fact]
    public async Task A_binary_or_invalid_utf8_in_a_read_or_an_answer_is_a_door_failure_too()
    {
        var entry = DeskServe.Entry("entry-open-counted");
        entry["record"]!["answers"]![0]!["note"] = Marker;
        foreach (var with in new[] { DeskServe.Bin(2), new byte[] { 0xa2, 0xff, 0xfe } })
        {
            var read = DeskServe.WithMarkerReplaced(entry, Marker, with);
            await Assert.ThrowsAsync<DeskDoorException>(() => DeskServe.ReadPayloadAsync(DeskServe.Fork(read)));

            var answer = DeskServe.Entry("entry-open-counted")["record"]!["answers"]![0]!;
            answer["note"] = Marker;
            var bytes = DeskServe.WithMarkerReplaced(answer, Marker, with);
            await Assert.ThrowsAsync<DeskDoorException>(() => DeskServe.AnswerPayloadAsync(DeskServe.Answered(bytes, "n1")));
        }
    }
}
