using System.Security.Cryptography;

namespace Bifrost.Web.Tests;

// Huginn's desk-feed golden bytes, vendored byte for byte from
// crates/huginn-mind/tests/fixtures/desk-feed/ at Huginn commit 9dde93e (docs/desk-feed-door.md is the contract).
// A silent re-vendor fails here: move the pin on purpose, with the contract and the door's tests.
public sealed class DeskFeedFixturePinTests
{
    public const string HuginnCommit = "9dde93e";

    public static readonly string Directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "desk-feed");

    private static readonly Dictionary<string, string> Sha256 = new(StringComparer.Ordinal)
    {
        [".gitattributes"] = "171872d6445cb1ffef0263b613d1ac318a60398449831d4cc4f772f78a7ebcff",
        ["README.md"] = "e2529f2bfedaf986a3f0df100c60ca2a4c46a74c83845d4d9c02d5c9a35dcb88",
        ["error.response.frame.bin"] = "fa03f44b82d7c4796f45dd36086a54342dc1de7e660a58cf3ddff4ff59d85a4c",
        ["error.response.frame.hex"] = "21c75b6e1d000ddc1a5455a9cc4d6c5b37bd1640c22e1055e96ff85bdff461bb",
        ["failure.response.frame.bin"] = "edc2277943334744d3057902800c25bbc913ff8ed336b4612f1a19772ec46dc9",
        ["failure.response.frame.hex"] = "b3c262ac506b51529e8063b2d38cc8c0047b431b75fe6ede1a91adebefd014d3",
        ["failure.response.payload.hex"] = "13ca85b5868451892746d2531833d394b66d25b5069cd0208169c5fcc94bf350",
        ["failure.response.payload.msgpack"] = "4bf7c80eb2d75b9559ab430757a98ebfd424745ed3fc1888d21bc6e41ed99403",
        ["list-counts.request.frame.bin"] = "1c04651e0b5b00c1ea955c19a07694c887010a5dbed3c56c745eb08b2e86a525",
        ["list-counts.request.frame.hex"] = "38f70d81ad9d6622e259147f8d092e1d1a506dbff41509736a78b514fd87c33b",
        ["list-counts.request.payload.hex"] = "b4ed23985d7b2ff45710fce0f0969cc9a8e421d1f7f01c5d8bc0e1a4f9c014bf",
        ["list-counts.request.payload.msgpack"] = "738646e54e0ec239d309b3bc3d8513e73db9dd2e7ea9df0a6a29ad999d3e2fa8",
        ["list-counts.response.frame.bin"] = "bf51c933fbbaa61934907cb66f5e07ac813de48e849ac80bdf3ba4f75d3952a3",
        ["list-counts.response.frame.hex"] = "3f395ae2302d0882fb2456fe94512bba045a1c967f98b4ebf6b306dd5e3d74a3",
        ["list-counts.response.payload.hex"] = "9a6f6b0be985d6ff8d313fa9e12ffe6662a59ff7305e574f4bfe8bf9a3ce2291",
        ["list-counts.response.payload.msgpack"] = "1f83a7c9ab1ce1af7c245a8f11f54ea67ef273fc2533554db3a1711e3f650c13",
        ["list-empty.response.frame.bin"] = "faf3c5008bd50c7f76121a3f27c5c4f944d1c7ad32d8bbd4e67d025c45b3f4f1",
        ["list-empty.response.frame.hex"] = "9b0f15bf8948e44f9e43718914f6621402c98c06e0d21bf90640c521ab7f1a45",
        ["list-empty.response.payload.hex"] = "d93cd65c1d9f4fc052b9c7f17d16ea6e468a7af296cafd6a31d671ae855d2860",
        ["list-empty.response.payload.msgpack"] = "7b1a26f303d3bdd0317d24a0eb4a2d9144ada0a878c927905a1a345efabf3a9f",
        ["list-open-cursor.request.frame.bin"] = "c73283d1e1e774abf66a6e4011ccc5c3f723a81cc2a4d3eb39575411479e7051",
        ["list-open-cursor.request.frame.hex"] = "b8a1716d9a1d6c1ece46dc8cdafa8a9efaa5474596ae41facd95fe8efcbad6f5",
        ["list-open-cursor.request.payload.hex"] = "0901e276fa578bb30792d925a0a2d0b65a09629d7b2493d4e666c714127df531",
        ["list-open-cursor.request.payload.msgpack"] = "5fd85c8418ea8f15313178c3410f9d21a8f0cc0bdbadbf5c56f3fa825a9869b9",
        ["list-open.request.frame.bin"] = "30ca39b84d3fa4c0562ad4707db765f35d2e0f2bcd42695e2595b6f8fafb3866",
        ["list-open.request.frame.hex"] = "ca4b065102699f7998040b5d0a396a088b2f7254ff1a6801fa1d648746453d3f",
        ["list-open.request.payload.hex"] = "960f570d6f76691ba75e4adb81b80e836612282ee0f88993be4754f60b00d461",
        ["list-open.request.payload.msgpack"] = "4f6ca021ff72d59869359ffb296bd93706ef13d86e8aea4efe5bcf11f4b59bd6",
        ["list-response-too-large.response.frame.bin"] = "b95218e48c1f8e4162f515b97a85c324b9ccfe2b3216780ed0eca1d2e2066f19",
        ["list-response-too-large.response.frame.hex"] = "b4bedb2c59b1362e5059caf6dbea63952b8fe9c47b681da3825422bc677f8632",
        ["list-response-too-large.response.payload.hex"] = "4f5f18915e0e536c387e659eb2021570a5b0bc5e0f530521ca226dc687d6780d",
        ["list-response-too-large.response.payload.msgpack"] = "9749f0dc7a3ec69aba7f45ba62ef61332461747cf42b188b84998da7bbd4d5d9",
        ["list-too-large.response.frame.bin"] = "1210fb82189a23d7ee4eb3851d4b83013f4f2a2298533734df348156097b5c60",
        ["list-too-large.response.frame.hex"] = "aad762b1e1deea39330223eb9ea7ebd2bb0fdd3326e259383b90846c1310456f",
        ["list-too-large.response.payload.hex"] = "880ae2c51454a3d6b06d2e827e44b6990365d8cfa304b55e25eb898641117762",
        ["list-too-large.response.payload.msgpack"] = "710d145c1ded7e7d02343342becd61e4520da979580f13dbebe6f0eda5aabec6",
        ["read-too-large.response.frame.bin"] = "5995c03914851ea95aaff4b75fa3ebaee4f19a2c7ce101eab85b7475515690bd",
        ["read-too-large.response.frame.hex"] = "b150aa192bf3fa415b5175f8b9f43731c5b73c2a2153c87da2dc3a4b6d986ee2",
        ["read-too-large.response.payload.hex"] = "18b7ad6c617ee1d9a3aa726a012e9a3af8ca9c14b8af774d082cf17879c85b90",
        ["read-too-large.response.payload.msgpack"] = "4a9135ede7d6588ba65e49b372b99d5da956fab9c693f1aa018e0ebe1406246a",
        ["read-unknown.response.frame.bin"] = "a62293ff79c0bbe626d020140f4c8cbe3f62a87ac0f730f3ba3c2a7acd3d49a5",
        ["read-unknown.response.frame.hex"] = "97ab11a04a2d62917186b23666ed26ef09df220ffb1c3e188d919985085733e2",
        ["read-unknown.response.payload.hex"] = "ca8d4d1fa4d40915b6e062767967e62343b360ce669801da96d99509e24787fb",
        ["read-unknown.response.payload.msgpack"] = "0d4fe47159db88b5048bc1f42a0e72857b568eac48572777a0c896ee94f7c208",
        ["read.request.frame.bin"] = "702d5a0a80bf05b75b24eff5a3f889ab0c96e819b8ca172e77bae05d74895662",
        ["read.request.frame.hex"] = "79c951722dd939212cb33635270dec11f47debd979960e8d4f74f2ecfd83708a",
        ["read.request.payload.hex"] = "3202f08616589a3d56bc36299b9915d1f8de8d2cd5573d21bca9115d30d9d968",
        ["read.request.payload.msgpack"] = "c10a27f67a4a2512d06c01528e1ee59754f9cc2e5a1b244b9982268bc8b4d2e0",
        ["record-whole.payload.hex"] = "f1fcee6923dbed3b49153e552e045af0e5be415fd857583e52ea519553784c46",
        ["record-whole.payload.msgpack"] = "1687ef965066e1a7e264ccd10760449ce14f28b2042c67f7d7944b0db2275581",
        ["refused-unit.response.frame.bin"] = "4f86943a6947dd3c6041ee7d1b52d3f73697604dd224ca5dcf5b66064b0de1f1",
        ["refused-unit.response.frame.hex"] = "f25f35911d242d6fdb7ac4e1ecb3fe01901aa4b816d957104bde6f21b29a1bed",
        ["refused-unit.response.payload.hex"] = "858d49c8c66ce1b9c32160e2aecb320a8c00166728c80f5a02d62ec90b4f410d",
        ["refused-unit.response.payload.msgpack"] = "f2eedbece230b164adf1897c3a047317a4e3074b2d5fd4c5abef3fbb98336c32",
        ["unavailable.response.frame.bin"] = "0cb241568bb20d0ccf62f337f4daa01ca3dd82cedd476fb4c1f9e7dc3272cabe",
        ["unavailable.response.frame.hex"] = "86f0e5bb2d3e0ea39ae2f0dbc2c1b62e5a77a35bffc6f4adfa87d280d718bf44",
        ["unavailable.response.payload.hex"] = "5a8e7d6e3cd7abf3649941d6b55a511d025c2baf2d8dde08e505e57e9cb4fd22",
        ["unavailable.response.payload.msgpack"] = "dfa613abaf5f23e6aff5bcb0a59b3a1784f2fbf6d12cbdfa82f10e80963f0a4e",
    };

    [Fact]
    public void Fixtures_are_huginn_9dde93e_byte_for_byte()
    {
        var present = new DirectoryInfo(Directory).GetFiles().Select(f => f.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(Sha256.Keys.Order(StringComparer.Ordinal).ToArray(), present);

        foreach (var (name, expected) in Sha256)
        {
            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Directory, name)))).ToLowerInvariant();
            Assert.True(
                expected == actual,
                $"{name} is not Huginn {HuginnCommit}:crates/huginn-mind/tests/fixtures/desk-feed/{name} (sha256 {actual}). " +
                "Re-vendor from that commit or move the pin on purpose.");
        }
    }
}
