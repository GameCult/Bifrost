using System.Text.Json.Nodes;
using MessagePack;
using Bifrost.Web.Features.Desk;
using Bifrost.Web.Tests.Support;

namespace Bifrost.Web.Tests;

// Huginn's DeskResponse variants are maps with a fixed key set (desk_wire.rs): Answered { answer, counted_answer_id,
// as_of: u64 }, Forks { list, as_of: u64 }, Fork { entry, as_of: u64 }. The door closes them like every other contract map:
// every key present, no other key, as_of an unsigned integer. (Huginn's own golden replies, list-empty and list-counts,
// carry exactly list and as_of.)
public sealed class UnixSocketDeskDoorWrapperTests
{
    private static string Entry => DeskServe.Entry("entry-open-counted").ToJsonString(DeskJson.Options);

    private static string Page => DeskServe.PageOf(DeskServe.Entry("entry-open-counted")).ToJsonString(DeskJson.Options);

    private static string Answer => DeskServe.Entry("entry-open-counted")["record"]!["answers"]![0]!.ToJsonString(DeskJson.Options);

    // variant, call, and a function from the wrapper's tail to the reply JSON.
    public static TheoryData<string> Variants() => ["Forks", "Fork", "Answered"];

    private static string Reply(string variant, string tail) => variant switch
    {
        "Forks" => "{\"Forks\":{\"list\":" + Page + tail + "}}",
        "Fork" => "{\"Fork\":{\"entry\":" + Entry + tail + "}}",
        _ => "{\"Answered\":{\"answer\":" + Answer + ",\"counted_answer_id\":\"n1\"" + tail + "}}",
    };

    private static async Task<object> Serve(string variant, byte[] payload) => variant switch
    {
        "Forks" => await DeskServe.ListPayloadAsync(payload),
        "Fork" => await DeskServe.ReadPayloadAsync(payload),
        _ => await DeskServe.AnswerPayloadAsync(payload),
    };

    private static byte[] Pack(string json) => MessagePackSerializer.ConvertFromJson(json);

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task A_wrapper_with_as_of_alone_beside_its_value_decodes_and_as_of_may_be_any_u64(string variant)
    {
        await Serve(variant, Pack(Reply(variant, ",\"as_of\":0")));
        await Serve(variant, Pack(Reply(variant, ",\"as_of\":4294967296")));
        await Serve(variant, Pack(Reply(variant, ",\"as_of\":18446744073709551615")));
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task A_wrapper_without_as_of_or_with_one_that_is_not_an_unsigned_integer_is_a_door_failure(string variant)
    {
        foreach (var tail in new[]
        {
            "", ",\"as_of\":null", ",\"as_of\":-1", ",\"as_of\":\"1\"", ",\"as_of\":true", ",\"as_of\":1.5", ",\"as_of\":[1]", ",\"as_of\":{}",
        })
        {
            await Assert.ThrowsAsync<DeskDoorException>(() => Serve(variant, Pack(Reply(variant, tail))));
        }
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task A_wrapper_with_a_key_beyond_the_variants_declared_ones_is_a_door_failure(string variant)
    {
        foreach (var tail in new[] { ",\"as_of\":1,\"viewer\":\"x\"", ",\"viewer\":1,\"as_of\":1", ",\"as_of\":1,\"as_of_extra\":null" })
        {
            await Assert.ThrowsAsync<DeskDoorException>(() => Serve(variant, Pack(Reply(variant, tail))));
        }
    }
}
