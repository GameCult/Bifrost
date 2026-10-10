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

    // The origins whose pages may read the public tally export from a browser (CORS). Each entry is exactly
    // https://host[:non-default-port], lower case, no path; empty allows none. StartupConfigurationValidator
    // refuses anything else outside Development and Testing, because "*" would allow every origin.
    public string[] AllowedOrigins { get; init; } = [];
}
