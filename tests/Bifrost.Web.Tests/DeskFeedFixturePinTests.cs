using System.Security.Cryptography;

namespace Bifrost.Web.Tests;

// Huginn's desk-feed golden bytes, vendored byte for byte from
// crates/huginn-mind/tests/fixtures/desk-feed/ at Huginn commit 79d954c (docs/desk-feed-door.md is the contract).
// A silent re-vendor fails here: move the pin on purpose, with the contract and the door's tests.
public sealed class DeskFeedFixturePinTests
{
    public const string HuginnCommit = "79d954c";

    public static readonly string Directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "desk-feed");

    private static readonly Dictionary<string, string> Sha256 = new(StringComparer.Ordinal)
    {
        [".gitattributes"] = "171872d6445cb1ffef0263b613d1ac318a60398449831d4cc4f772f78a7ebcff",
        ["README.md"] = "441636e7b122289181ff999f2de51e8ca59fbab5782b7e05e1002bcca9657c48",
        ["card-viewers-decider.synthetic.payload.hex"] = "13e0d20ed4c07d638417a39237f17d96c5decd659be47b0d022d26712980ec15",
        ["card-viewers-decider.synthetic.payload.msgpack"] = "c5c2d9b1f4f08b0049227a9cb9deb850bc9a7a74acd7ad6c85b15d2433133dee",
        ["card-viewers-non-decider.synthetic.payload.hex"] = "e59938dff11230981bf51241d89091df76d2ca36b563f2a65dea58873844bf8c",
        ["card-viewers-non-decider.synthetic.payload.msgpack"] = "003fe7bb3ac93dc5cdf56fa1eca003f720d423fb1260778882ce5354729a1027",
        ["error.response.frame.bin"] = "fa03f44b82d7c4796f45dd36086a54342dc1de7e660a58cf3ddff4ff59d85a4c",
        ["error.response.frame.hex"] = "21c75b6e1d000ddc1a5455a9cc4d6c5b37bd1640c22e1055e96ff85bdff461bb",
        ["failure.response.frame.bin"] = "edc2277943334744d3057902800c25bbc913ff8ed336b4612f1a19772ec46dc9",
        ["failure.response.frame.hex"] = "b3c262ac506b51529e8063b2d38cc8c0047b431b75fe6ede1a91adebefd014d3",
        ["failure.response.payload.hex"] = "13ca85b5868451892746d2531833d394b66d25b5069cd0208169c5fcc94bf350",
        ["failure.response.payload.msgpack"] = "4bf7c80eb2d75b9559ab430757a98ebfd424745ed3fc1888d21bc6e41ed99403",
        ["list-counts.request.frame.bin"] = "be338fc06f127f9c20f82952708ffe95f8b6b8de7c5098834eb8cb257893637f",
        ["list-counts.request.frame.hex"] = "496bbc58292a1d029361bd553eef2ccf786cfa6520b1a3ad97c831893c36cf50",
        ["list-counts.request.payload.hex"] = "5ec076a5d8006a0290ad269deffb2bfe45ac7ee8b6a11ea2d4968a6ac8603e4b",
        ["list-counts.request.payload.msgpack"] = "52f244c3d704067f79561c8d46f4069ac3f4452a73d916fb92154d28b28ea6c7",
        ["list-counts.response.frame.bin"] = "bf51c933fbbaa61934907cb66f5e07ac813de48e849ac80bdf3ba4f75d3952a3",
        ["list-counts.response.frame.hex"] = "3f395ae2302d0882fb2456fe94512bba045a1c967f98b4ebf6b306dd5e3d74a3",
        ["list-counts.response.payload.hex"] = "9a6f6b0be985d6ff8d313fa9e12ffe6662a59ff7305e574f4bfe8bf9a3ce2291",
        ["list-counts.response.payload.msgpack"] = "1f83a7c9ab1ce1af7c245a8f11f54ea67ef273fc2533554db3a1711e3f650c13",
        ["list-empty.response.frame.bin"] = "faf3c5008bd50c7f76121a3f27c5c4f944d1c7ad32d8bbd4e67d025c45b3f4f1",
        ["list-empty.response.frame.hex"] = "9b0f15bf8948e44f9e43718914f6621402c98c06e0d21bf90640c521ab7f1a45",
        ["list-empty.response.payload.hex"] = "d93cd65c1d9f4fc052b9c7f17d16ea6e468a7af296cafd6a31d671ae855d2860",
        ["list-empty.response.payload.msgpack"] = "7b1a26f303d3bdd0317d24a0eb4a2d9144ada0a878c927905a1a345efabf3a9f",
        ["list-open-cursor.request.frame.bin"] = "13671e1c83c10350d86b149a86a98b69763b9a3e40eae0024c10f3e70b96ef8b",
        ["list-open-cursor.request.frame.hex"] = "e2270f26d122374554855bf60686edbc5d4abfd63374cde08ab479a5f310aa12",
        ["list-open-cursor.request.payload.hex"] = "437f4c0cbdc712e9b3f8e1f8666cf8ffe88061cce8d0da3a64e648eb7b6bb27f",
        ["list-open-cursor.request.payload.msgpack"] = "05889e90f8143aa1a1375539f6e76593a682413b70cb40a8aaa3c7db2882622a",
        ["list-open.request.frame.bin"] = "2af34627a45d7bfca034b880bf61f5dfd78022a8df425f418c0a084181663903",
        ["list-open.request.frame.hex"] = "03f74ba78996b2ccb3fcf5fb712ae1d19f7594099ba04250ee0223c8631b9a24",
        ["list-open.request.payload.hex"] = "a58e8583d41d4a04cbd983c6b5a8bdae26105f2eb7f25dfb7d5f86aa81847e93",
        ["list-open.request.payload.msgpack"] = "431e000c3f002c254adc57273557c8111e815ca42a8dd4272762d3e7697d7f0a",
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
        ["read.request.frame.bin"] = "3a9bc16ce4ce706e8c839bf4cfe65d3e2fdf759992555e68be6fc7bcc15653ea",
        ["read.request.frame.hex"] = "3c3d9540c4af789d230b7108f98d5d5f3dd9b291ff618064ddc374eb2a7bb082",
        ["read.request.payload.hex"] = "48aed263254eac7bc156c1af1f2ee210a08dda2b453a5be2dc17083ab73800b2",
        ["read.request.payload.msgpack"] = "c5447f462247703af4329e071bc0ac14dce1cb94a63c85517400791d022263ca",
        ["record-non-decider.proposed.payload.hex"] = "15350e035612b380b55811c90b218afb7444214a2965deae35076aabe78206e5",
        ["record-non-decider.proposed.payload.msgpack"] = "5095dfb375613a5f80a4471e1d5b68c77890b0732dc03cc11e52d9e769406afc",
        ["record-whole.payload.hex"] = "f70d1a81a4f1031c04d5e1750f9162afc859a7b4d5b4cacf3e562d60ac00f531",
        ["record-whole.payload.msgpack"] = "3ad3926b024fb76178cd68cbdab6a28da1ddef9a3aca6b66050488213145d496",
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
    public void Fixtures_are_huginn_79d954c_byte_for_byte()
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
