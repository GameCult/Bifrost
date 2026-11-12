using System.Security.Cryptography;

namespace Bifrost.Web.Tests;

// Huginn's desk-feed golden bytes, vendored byte for byte from crates/huginn-mind/tests/fixtures/desk-feed/ at Huginn
// main 232764bf78fa93c807d2e18b1d77f45c36159be0 (docs/desk-feed-door.md is the contract): 99 files, the
// subdirectory cultlib-d3025d3c/ (fourteen CultLib vectors, unedited) included. The pin is the exact file list and each
// sha256; a changed byte, an extra file or a missing file fails, and the verifier is proved to catch each.
// A silent re-vendor fails here: move the pin on purpose, with the contract and the door's tests.
public sealed class DeskFeedFixturePinTests
{
    public const string HuginnCommit = "232764bf78fa93c807d2e18b1d77f45c36159be0";

    public static readonly string Directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "desk-feed");

    public static readonly IReadOnlyDictionary<string, string> Sha256 = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [".gitattributes"] = "171872d6445cb1ffef0263b613d1ac318a60398449831d4cc4f772f78a7ebcff",
        ["README.md"] = "fd3429ee796ac1c3de9571bc2d09e24f0f91aae48d461f1c8eefa51c6b50a3bc",
        ["cultlib-d3025d3c/README.md"] = "b0f6ce43f4c5aa9ae263071b814e2f71445bd50db1d55c6fbcdaf2714166b47a",
        ["cultlib-d3025d3c/answer-counted.msgpack"] = "55d69ae3cc7ebbeeae7d501e8641640e115c3fb5aac399ea760588bca343b9e6",
        ["cultlib-d3025d3c/answer-refused-stale.msgpack"] = "3183bc9c483f4a625332cda11ef22a9c8560868a7728b77daa63a1960a3cd3d6",
        ["cultlib-d3025d3c/answer-retraction.msgpack"] = "89156f41ec3ea54f5d47bd3318913ff013785f4727916875be97785935cb7735",
        ["cultlib-d3025d3c/answer-revision.msgpack"] = "19d7289395ae68cbaf6de1c0a1ef6c843211b2caf9462e1d2268cd0883386315",
        ["cultlib-d3025d3c/answer-withheld-answerer-retraction.msgpack"] = "7d0cc555bf9b6f7c345b0da7c504bcd1ed7b46040ff51d1e57c84b2f56655213",
        ["cultlib-d3025d3c/answer-withheld-answerer-revision.msgpack"] = "93f7c875f963a7cfeab2a5980b6d5ce81e6753fc9f3f8bf0d7353f3f388144b7",
        ["cultlib-d3025d3c/answer-withheld-answerer.msgpack"] = "b66b4f921cdd374be04af3de34c59e254a5e542027ae8c57f196a3ea3406e427",
        ["cultlib-d3025d3c/card-operator-fork.msgpack"] = "56bfdb65ded60b562001d09d0b8da6e092900f404bf5f68f7084dff29bcc34f5",
        ["cultlib-d3025d3c/card-raised-at-nil.msgpack"] = "b81bb825790ffe03fabe63c3f8a61c36bd50ca268aa6dff67b5bcfad3957e58c",
        ["cultlib-d3025d3c/card-withheld-deciders.msgpack"] = "d13b9b77461cfdf78f943ef56c6aeab852ab6b6f0b355e26c5b361f99c79c7f7",
        ["cultlib-d3025d3c/entry-closed-with-ruling-and-a-refusal.msgpack"] = "f7859c7d04d338d6b1c634308b08ae58ae77b0ff459767859a5e40f0af2f16e5",
        ["cultlib-d3025d3c/entry-open-counted.msgpack"] = "b51f499e8dd0465cf5df9d68d8fccbe94dd1339fd3157969693dd0d4ecb2549b",
        ["cultlib-d3025d3c/entry-withheld-closed-with-ruling-and-a-refusal.msgpack"] = "daaa819bfa2612616826ef3217dfcf8d8d8cb71d86547d7106426c779134f827",
        ["cultlib-d3025d3c/entry-withheld-open-counted.msgpack"] = "62ccad7a9f41262d6e35d0247315d0bb75d1313b3c9dabe8a99d1aa1b844607f",
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
        ["list-non-decider.request.frame.bin"] = "ebf8664681fcf624e4c6b82eb5e23e4c31b56d0f8a03f1cfbadc766408eaa18e",
        ["list-non-decider.request.frame.hex"] = "f0d7f662425ca1c16dea4f5eb78d894898d0dda2ff04f9614663c74620371d26",
        ["list-non-decider.request.payload.hex"] = "383122413f2bd276d27c53bdfcac9f65ba704ea2b1e1c18e72b89b164aad2c2d",
        ["list-non-decider.request.payload.msgpack"] = "9915249f377a6afe4dda2f007c23e8f2598f759bd9d3991834c9ee162b0d6a5b",
        ["list-open-cursor.request.frame.bin"] = "13671e1c83c10350d86b149a86a98b69763b9a3e40eae0024c10f3e70b96ef8b",
        ["list-open-cursor.request.frame.hex"] = "e2270f26d122374554855bf60686edbc5d4abfd63374cde08ab479a5f310aa12",
        ["list-open-cursor.request.payload.hex"] = "437f4c0cbdc712e9b3f8e1f8666cf8ffe88061cce8d0da3a64e648eb7b6bb27f",
        ["list-open-cursor.request.payload.msgpack"] = "05889e90f8143aa1a1375539f6e76593a682413b70cb40a8aaa3c7db2882622a",
        ["list-open-non-decider.response.frame.bin"] = "8bf4539f305a9530ac88090bdb2f3a3c851f14f66bd2c69f8009ad4934aec3ad",
        ["list-open-non-decider.response.frame.hex"] = "d377e096ce781b0fb86398a58e69ee86205770c298120facac9e739937e88e7d",
        ["list-open-non-decider.response.payload.hex"] = "c99b932fb7c19fc28a8fb04f44b80951a55722cbab9c32114ca41c1cd13ef84e",
        ["list-open-non-decider.response.payload.msgpack"] = "14616093522771954b34b7414849c2896b0d328c8cf0feac6d8038116d813d8c",
        ["list-open.request.frame.bin"] = "2af34627a45d7bfca034b880bf61f5dfd78022a8df425f418c0a084181663903",
        ["list-open.request.frame.hex"] = "03f74ba78996b2ccb3fcf5fb712ae1d19f7594099ba04250ee0223c8631b9a24",
        ["list-open.request.payload.hex"] = "a58e8583d41d4a04cbd983c6b5a8bdae26105f2eb7f25dfb7d5f86aa81847e93",
        ["list-open.request.payload.msgpack"] = "431e000c3f002c254adc57273557c8111e815ca42a8dd4272762d3e7697d7f0a",
        ["list-open.response.frame.bin"] = "82d7b9bd8a5d3a98f2d5780b3cb59fb75b598a333069a03243c1ec83faa6f099",
        ["list-open.response.frame.hex"] = "ebc75b6ae8d16b1cbe0bece1ed3fa32d0ead42fc39ca939e542a8fa45e1dadee",
        ["list-open.response.payload.hex"] = "618918c8020e7bf673758a7e6a58b78469449838b287402bd383a7d1258adc57",
        ["list-open.response.payload.msgpack"] = "f16c74f6340054e40d0d0862381da717a1fa651a67f3250b7bd555cea2834a6e",
        ["list-response-too-large.response.frame.bin"] = "b95218e48c1f8e4162f515b97a85c324b9ccfe2b3216780ed0eca1d2e2066f19",
        ["list-response-too-large.response.frame.hex"] = "b4bedb2c59b1362e5059caf6dbea63952b8fe9c47b681da3825422bc677f8632",
        ["list-response-too-large.response.payload.hex"] = "4f5f18915e0e536c387e659eb2021570a5b0bc5e0f530521ca226dc687d6780d",
        ["list-response-too-large.response.payload.msgpack"] = "9749f0dc7a3ec69aba7f45ba62ef61332461747cf42b188b84998da7bbd4d5d9",
        ["list-too-large.response.frame.bin"] = "1210fb82189a23d7ee4eb3851d4b83013f4f2a2298533734df348156097b5c60",
        ["list-too-large.response.frame.hex"] = "aad762b1e1deea39330223eb9ea7ebd2bb0fdd3326e259383b90846c1310456f",
        ["list-too-large.response.payload.hex"] = "880ae2c51454a3d6b06d2e827e44b6990365d8cfa304b55e25eb898641117762",
        ["list-too-large.response.payload.msgpack"] = "710d145c1ded7e7d02343342becd61e4520da979580f13dbebe6f0eda5aabec6",
        ["read-non-decider.request.frame.bin"] = "3f2729eca79e3c21feebc763f22b5ee86803b333a22b39d5cc1635e2fae245b6",
        ["read-non-decider.request.frame.hex"] = "9dce0664c145dff66d52e2c2d21cf12b187884392908f031a310534716646aa5",
        ["read-non-decider.request.payload.hex"] = "32d77b81c10bc8accbc31a667d2ad631bf5e3404a485d3d3bed3889c50571f8e",
        ["read-non-decider.request.payload.msgpack"] = "c2ec1ecb06123fbb6292ed7a409035f7cc58408885886259131226a8c0aabc08",
        ["read-non-decider.response.frame.bin"] = "15da06225cdd950bf0fe61e49a7ba18d6117b073fdaa6b9732f902564e47d574",
        ["read-non-decider.response.frame.hex"] = "9cdb0efaaf52f8234ea4ad9c357d563f134cedc8dc7f94ae1b297a30670dd013",
        ["read-non-decider.response.payload.hex"] = "0ae37b5f6920c4a41f4f8fe7c45f03c90ec74e384991920811273a0ef4250f9c",
        ["read-non-decider.response.payload.msgpack"] = "41e947355eac70e948633986bc19506fef5c2f92d7fc4242a1783dc1aa688890",
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
        ["read.response.frame.bin"] = "34acacf58f3bd10e86bad6b10f3f14b6a263709b54ff81873af7dcfaf3d95a06",
        ["read.response.frame.hex"] = "600848ae27be452d9a7845c5d822c8eabc1df07b163071d35cc38f6f2ec330bb",
        ["read.response.payload.hex"] = "c3322aa9989a47b9b255cd8d20d6026a159c6b9a8a5ee22f135407bf96cfd2d3",
        ["read.response.payload.msgpack"] = "79fb2f9dd3fde3335bca5cc4337abcf0d7e195afc341352087e6432b7f5ae0a7",
        ["record-whole.payload.hex"] = "871ee32813f46656aa372f6d791d36f885e83194294d3a9fa1fcb906cab73eb4",
        ["record-whole.payload.msgpack"] = "e85e420e7450cfecc698268299b1a26e1ed256d0420143cbad74f80b5a52731d",
        ["record-withheld.payload.hex"] = "a0e7afd9131387cc25d5a2ff5b5f12d546ddd38dfe2bc0d74d3c55a4439048d6",
        ["record-withheld.payload.msgpack"] = "aff799fe787258c35e658cf4d6dea33b95938c47f2d4e72fcdf194f0b537bfed",
        ["refused-unit.response.frame.bin"] = "4f86943a6947dd3c6041ee7d1b52d3f73697604dd224ca5dcf5b66064b0de1f1",
        ["refused-unit.response.frame.hex"] = "f25f35911d242d6fdb7ac4e1ecb3fe01901aa4b816d957104bde6f21b29a1bed",
        ["refused-unit.response.payload.hex"] = "858d49c8c66ce1b9c32160e2aecb320a8c00166728c80f5a02d62ec90b4f410d",
        ["refused-unit.response.payload.msgpack"] = "f2eedbece230b164adf1897c3a047317a4e3074b2d5fd4c5abef3fbb98336c32",
        ["unavailable.response.frame.bin"] = "0cb241568bb20d0ccf62f337f4daa01ca3dd82cedd476fb4c1f9e7dc3272cabe",
        ["unavailable.response.frame.hex"] = "86f0e5bb2d3e0ea39ae2f0dbc2c1b62e5a77a35bffc6f4adfa87d280d718bf44",
        ["unavailable.response.payload.hex"] = "5a8e7d6e3cd7abf3649941d6b55a511d025c2baf2d8dde08e505e57e9cb4fd22",
        ["unavailable.response.payload.msgpack"] = "dfa613abaf5f23e6aff5bcb0a59b3a1784f2fbf6d12cbdfa82f10e80963f0a4e",
    };

    // Every way the directory differs from the pin, by relative path (forward slashes).
    public static List<string> Problems(string directory)
    {
        var present = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var name = Path.GetRelativePath(directory, file.FullName).Replace('\\', '/');
            present[name] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.FullName))).ToLowerInvariant();
        }

        var problems = new List<string>();
        problems.AddRange(Sha256.Keys.Except(present.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(n => $"missing: {n}"));
        problems.AddRange(present.Keys.Except(Sha256.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(n => $"extra: {n}"));
        problems.AddRange(Sha256.Where(p => present.TryGetValue(p.Key, out var actual) && actual != p.Value).Select(p => $"changed: {p.Key}"));
        return problems;
    }

    [Fact]
    public void Fixtures_are_huginn_232764bf_byte_for_byte()
    {
        Assert.Equal(99, Sha256.Count);
        Assert.True(Problems(Directory).Count == 0,
            $"not Huginn {HuginnCommit}:crates/huginn-mind/tests/fixtures/desk-feed/: {string.Join("; ", Problems(Directory))}. " +
            "Re-vendor from that commit or move the pin on purpose.");
    }

    private static string Copy()
    {
        var copy = System.IO.Directory.CreateTempSubdirectory("pin").FullName;
        foreach (var file in new DirectoryInfo(Directory).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(copy, Path.GetRelativePath(Directory, file.FullName));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            file.CopyTo(to);
        }

        return copy;
    }

    [Theory]
    [InlineData("list-open.request.frame.bin", "changed")]
    [InlineData("cultlib-d3025d3c/entry-open-counted.msgpack", "changed")]
    [InlineData("README.md", "changed")]
    public void A_changed_byte_fails_the_pin(string name, string kind)
    {
        var copy = Copy();
        try
        {
            var path = Path.Combine(copy, name);
            var bytes = File.ReadAllBytes(path);
            bytes[^1] ^= 1;
            File.WriteAllBytes(path, bytes);

            Assert.Equal([$"{kind}: {name}"], Problems(copy));
        }
        finally
        {
            System.IO.Directory.Delete(copy, true);
        }
    }

    [Fact]
    public void An_extra_or_missing_file_fails_the_pin_at_either_level()
    {
        var copy = Copy();
        try
        {
            File.WriteAllText(Path.Combine(copy, "extra.hex"), "00\n");
            File.WriteAllText(Path.Combine(copy, "cultlib-d3025d3c", "extra.msgpack"), "x");
            File.Delete(Path.Combine(copy, "read.request.frame.hex"));
            File.Delete(Path.Combine(copy, "cultlib-d3025d3c", "answer-counted.msgpack"));

            Assert.Equal(
                ["missing: cultlib-d3025d3c/answer-counted.msgpack", "missing: read.request.frame.hex", "extra: cultlib-d3025d3c/extra.msgpack", "extra: extra.hex"],
                Problems(copy));
        }
        finally
        {
            System.IO.Directory.Delete(copy, true);
        }
    }
}
