using System.Net;
using Bifrost.Web.Configuration;
using Bifrost.Web.Features.Shared;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Features.ConceptVotes;

// The one owner of the vote budgets. Cast is spent by every cast and retract; mint is spent additionally by a cast that
// is about to mint a device cookie. A denial returns how long to wait and writes nothing. The partition is the account
// when the voter is signed in, otherwise the network prefix (NetworkPrefix.Of) of the address the forwarded-headers
// middleware left on the connection. No header and no client input ever becomes a key.
public sealed class ConceptVoteRateLimits(IOptions<ConceptVoteOptions> options, TimeProvider clock)
{
    private readonly ConceptVoteOptions opts = options.Value;
    private readonly FixedWindowBudget cast = new(options.Value.CastPermits, options.Value.CastWindow, clock);
    private readonly FixedWindowBudget mint = new(options.Value.MintPermits, options.Value.MintWindow, clock);

    public TimeSpan? DenyCast(IPAddress? client, VoterIdentity? known) =>
        cast.TryAcquire(known?.UserAccountId is { } account ? "account:" + account.ToString("N") : NetKey(client), out var wait)
            ? null
            : wait;

    public TimeSpan? DenyMint(IPAddress? client) =>
        mint.TryAcquire(NetKey(client), out var wait) ? null : wait;

    private string NetKey(IPAddress? client) =>
        "net:" + (client is null ? "unknown" : NetworkPrefix.Of(client, opts.IPv4PrefixLength, opts.IPv6PrefixLength));
}
