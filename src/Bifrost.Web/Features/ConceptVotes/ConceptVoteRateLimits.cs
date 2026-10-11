using System.Net;
using Bifrost.Web.Configuration;
using Bifrost.Web.Features.Shared;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Features.ConceptVotes;

// The one owner of the vote budgets. Cast is spent by every cast and retract; mint is spent additionally by a cast that
// is about to mint a device cookie. A denial says why and how long to wait, and writes nothing. The partition is the
// account when the voter is signed in, otherwise the network prefix (NetworkPrefix.Of) of the address the
// forwarded-headers middleware left on the connection. No header and no client input ever becomes a key. Accounts
// have a table of their own, so a flood of anonymous sources cannot crowd a signed-in voter out of it.
public sealed class ConceptVoteRateLimits(IOptions<ConceptVoteOptions> options, TimeProvider clock)
{
    private readonly ConceptVoteOptions opts = options.Value;
    private readonly FixedWindowBudget castAccounts = new(options.Value.CastPermits, options.Value.CastWindow, options.Value.MaxTrackedKeys, clock);
    private readonly FixedWindowBudget castNetworks = new(options.Value.CastPermits, options.Value.CastWindow, options.Value.MaxTrackedKeys, clock);
    private readonly FixedWindowBudget mint = new(options.Value.MintPermits, options.Value.MintWindow, options.Value.MaxTrackedKeys, clock);

    public BudgetDenial? DenyCast(IPAddress? client, VoterIdentity? known) =>
        known?.UserAccountId is { } account
            ? castAccounts.Spend(account.ToString("N"))
            : castNetworks.Spend(NetKey(client));

    public BudgetDenial? DenyMint(IPAddress? client) => mint.Spend(NetKey(client));

    private string NetKey(IPAddress? client) =>
        client is null ? "unknown" : NetworkPrefix.Of(client, opts.IPv4PrefixLength, opts.IPv6PrefixLength);
}
