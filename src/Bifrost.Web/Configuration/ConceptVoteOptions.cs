namespace Bifrost.Web.Configuration;

public sealed class ConceptVoteOptions
{
    public const string SectionName = "ConceptVotes";

    // HMAC-SHA256 output is 32 bytes (RFC 2104 advises a key at least as long as the hash output), so a shorter
    // secret makes the tag brute-forceable over the IPv4 space if the table leaks.
    public const int MinNetworkTagSecretLength = 32;

    // Keys the network tag. Required outside Development and Testing, at least MinNetworkTagSecretLength
    // characters (StartupConfigurationValidator).
    public string NetworkTagSecret { get; init; } = string.Empty;

    // The research note (docs/research/anonymous-vote-stuffing-prior-art.md:57-61) finds no source for a
    // coarser IPv4 partition than the address itself, so the default is the exact address.
    public int IPv4PrefixLength { get; init; } = 32;

    // /64 is bypassed by RFC 8981 privacy rotation and households can hold a /56 or /48; /56 is the practitioner
    // default weighed in the note, not a standard. Tune on observed abuse.
    public int IPv6PrefixLength { get; init; } = 56;

    // Vote budgets (Features/ConceptVotes/ConceptVoteRateLimits). The research note
    // (docs/research/anonymous-vote-stuffing-prior-art.md section 3) gives no numbers, so these are first values to tune on
    // observed abuse. Cast: one visitor reading through the Mill can vote on a few dozen concepts in a sitting, so 30 per
    // 10 minutes per account or network prefix does not trouble a person and caps a script at 180 an hour.
    public int CastPermits { get; init; } = 30;

    public TimeSpan CastWindow { get; init; } = TimeSpan.FromMinutes(10);

    // Mint: a new device cookie is a new voter identity, the cheapest stuffing lever. Anonymous visitors share an
    // address behind carrier, school and office NAT (the research note records it), so 30 new devices per hour per
    // address (operator ruling concept-mill-votes:ruling:mint-budget-30-per-hour); per-device and per-prefix cast
    // limits still bound stuffing.
    public int MintPermits { get; init; } = 30;

    public TimeSpan MintWindow { get; init; } = TimeSpan.FromHours(1);

    // The most partitions each budget holds at once. At the cap a partition not yet held is refused with a 429 until
    // the oldest window ends; held partitions are never evicted, so a rotating caller cannot escape a spent budget.
    // 100000 partitions is a few tens of MB; a real audience is far below it.
    public int MaxTrackedKeys { get; init; } = 100_000;

    // The origins whose pages may read the public tally export from a browser (CORS). Each entry is exactly
    // https://host[:non-default-port], lower case, no path; empty allows none. StartupConfigurationValidator
    // refuses anything else outside Development and Testing, because "*" would allow every origin.
    public string[] AllowedOrigins { get; init; } = [];

    // The Mill page the vote page links back to; the concept's key is appended as the anchor. Production is the
    // Aetheria site's page, so a deploy overrides nothing unless the page moves (ConceptVotes__MillPageUrl).
    public string MillPageUrl { get; init; } = "https://aetheria.gamecult.org/Game-Design/Concept-Mill";
}
