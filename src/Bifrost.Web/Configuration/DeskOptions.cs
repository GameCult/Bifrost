namespace Bifrost.Web.Configuration;

// The fork desk's configuration (CultLib fork-card.md, Transport; M1 and M5). Minds is an ARRAY, never a map keyed
// by Mind id: configuration keys are case-insensitive, so a keyed section would let GAMECULT-CHAT reach
// gamecult-chat. Account ids are lowercase D-format Guids. An empty Minds list means the desk is not configured.
public sealed class DeskOptions
{
    public const string SectionName = "Desk";

    public List<DeskMindOptions> Minds { get; init; } = [];

    public DeskAttributionOptions Attribution { get; init; } = new();

    // The one origin allowed to call /api/desk from a browser (exactly this, no credentials).
    public string AllowedOrigin { get; init; } = string.Empty;

    // The cap on an answer body, in bytes, judged before it is parsed.
    public int MaxBodyBytes { get; init; } = 16 * 1024;

    // What an `unavailable` document tells the client to wait.
    public int RetryAfterSeconds { get; init; } = 30;

    public bool IsConfigured => Minds.Count > 0;
}

public sealed class DeskMindOptions
{
    public string Id { get; init; } = string.Empty;

    // The full path of this Mind's socket on the peer-checked door. Bifrost only connects to it.
    public string Socket { get; init; } = string.Empty;

    // Accounts that may answer this Mind's forks. Sent to Huginn with every call; no request sets or extends it.
    public List<string> Deciders { get; init; } = [];

    // Huginn's bound on the list a call carries (fork-card.md: one to 64 ids).
    public const int MaxDeciders = 64;

    // One to 64 ids, none repeated. The startup validator and the request path both ask this one question.
    public static bool DecidersWithinBounds(IReadOnlyCollection<string> deciders) =>
        deciders.Count is >= 1 and <= MaxDeciders && deciders.Distinct(StringComparer.Ordinal).Count() == deciders.Count;
}

// The authority a stamped answer carries (fork-card.md: Operator, Steward or Member), from configuration because
// Bifrost has no such roles: Operator is one account, Stewards a list, everyone else a Member.
public sealed class DeskAttributionOptions
{
    public string? Operator { get; init; }

    public List<string> Stewards { get; init; } = [];

    public string AuthorityOf(string accountId)
    {
        if (!string.IsNullOrEmpty(Operator) && string.Equals(accountId, Operator, StringComparison.Ordinal))
        {
            return DeskAuthority.Operator;
        }

        return Stewards.Contains(accountId, StringComparer.Ordinal) ? DeskAuthority.Steward : DeskAuthority.Member;
    }
}

public static class DeskAuthority
{
    public const string Operator = "Operator";
    public const string Steward = "Steward";
    public const string Member = "Member";
}
