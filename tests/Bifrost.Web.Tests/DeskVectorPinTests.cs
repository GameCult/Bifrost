using System.Security.Cryptography;
using Bifrost.Web.Tests.Support;

namespace Bifrost.Web.Tests;

public sealed class DeskVectorPinTests
{
    [Fact]
    public void Vectors_are_cultlib_f6c9bf5f()
    {
        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(DeskVectors.Path))).ToLowerInvariant();

        Assert.True(
            DeskVectors.FileSha256 == sha,
            $"fork-card-vectors.json is not CultLib {DeskVectors.SourceCommit}:contracts/cultnet/governance/fork-card-vectors.json (sha256 {sha}). " +
            "Re-vendor from that commit or move the pin on purpose, with the whole contract.");
    }
}
