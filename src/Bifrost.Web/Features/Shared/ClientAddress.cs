using System.Net;

namespace Bifrost.Web.Features.Shared;

public static class ClientAddress
{
    /// <summary>
    /// The partition key for a per-address budget: the connection's address, already corrected by the forwarded-headers
    /// handling that trusts only the local proxy. An IPv6 caller owns its /64, so rotating the low bits buys nothing.
    /// </summary>
    public static string BudgetKey(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes).ToString() + "/64";
    }
}
