using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Bifrost.Web.Features.ConceptVotes;

public static class NetworkPrefix
{
    // The only prefix computation: IPv4-mapped IPv6 folds to IPv4, then the address is masked. Returns CIDR text.
    public static string Of(IPAddress address, int ipv4Length, int ipv6Length)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        var length = bytes.Length == 4 ? ipv4Length : ipv6Length;
        if (length < 0 || length > bytes.Length * 8)
        {
            throw new ArgumentOutOfRangeException(nameof(ipv4Length), "Prefix length is outside the address family.");
        }

        for (var i = 0; i < bytes.Length; i++)
        {
            var kept = Math.Clamp(length - i * 8, 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - kept));
        }

        return new IPAddress(bytes) + "/" + length;
    }

    // Lowercase hex HMAC-SHA256 over the prefix text. Never reversible to an address; audit and purge only.
    public static string Tag(IPAddress address, int ipv4Length, int ipv6Length, string secret)
    {
        var prefix = Of(address, ipv4Length, ipv6Length);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(prefix));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }
}
