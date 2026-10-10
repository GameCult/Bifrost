using System.Net;
using Bifrost.Web.Features.ConceptVotes;

namespace Bifrost.Web.Tests;

public sealed class NetworkPrefixTests
{
    private const string Secret = "prefix-secret";

    private static string Tag(string address, int v4 = 24, int v6 = 56, string secret = Secret) =>
        NetworkPrefix.Tag(IPAddress.Parse(address), v4, v6, secret);

    [Fact]
    public void Two_addresses_inside_one_prefix_share_a_tag_and_other_prefixes_do_not()
    {
        Assert.Equal(Tag("203.0.113.7"), Tag("203.0.113.200"));
        Assert.NotEqual(Tag("203.0.113.7"), Tag("203.0.114.7"));
        Assert.Equal(Tag("2001:db8:1:ff::1"), Tag("2001:db8:1:ff:aaaa::9"));
        Assert.NotEqual(Tag("2001:db8:1:ff::1"), Tag("2001:db8:2:ff::1"));
        Assert.Equal("203.0.113.0/24", NetworkPrefix.Of(IPAddress.Parse("203.0.113.7"), 24, 56));
        Assert.Equal("2001:db8:1:ff00::/56", NetworkPrefix.Of(IPAddress.Parse("2001:db8:1:ffee::1"), 24, 56));
    }

    [Fact]
    public void Prefix_length_is_the_configured_one()
    {
        Assert.NotEqual(Tag("203.0.113.7", v4: 32), Tag("203.0.113.8", v4: 32));
        Assert.Equal(Tag("2001:db8:1:ff::1", v6: 48), Tag("2001:db8:1:aa::1", v6: 48));
    }

    [Fact]
    public void IPv4_mapped_IPv6_equals_the_IPv4_address()
    {
        Assert.Equal(Tag("203.0.113.7"), Tag("::ffff:203.0.113.7"));
    }

    [Fact]
    public void A_different_secret_gives_a_different_tag_and_the_tag_is_lowercase_hex()
    {
        var tag = Tag("203.0.113.7");

        Assert.NotEqual(tag, Tag("203.0.113.7", secret: "other-secret"));
        Assert.Matches("^[0-9a-f]{64}$", tag);
    }

    [Fact]
    public void A_length_outside_the_family_is_refused_without_echoing_the_address()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => NetworkPrefix.Of(IPAddress.Parse("203.0.113.7"), 33, 56));

        Assert.DoesNotContain("203.0.113.7", ex.Message);
    }
}
