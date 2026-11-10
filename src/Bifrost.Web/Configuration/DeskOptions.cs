namespace Bifrost.Web.Configuration;

// The fork desk's configuration (CultLib fork-card.md, Transport; M1 and M5). Minds is an ARRAY, never a map keyed
// by Mind id: configuration keys are case-insensitive, so a keyed section would let GAMECULT-CHAT reach
// gamecult-chat. Account ids are lowercase D-format Guids. An empty Minds list means the desk is not configured.
public sealed class DeskOptions
{
    public const string SectionName = "Desk";

    public List<DeskMindOptions> Minds { get; init; } = [];

    public DeskAttributionOptions Attribution { get; init; } = new();

    public bool IsConfigured => Minds.Count > 0;
}

public sealed class DeskMindOptions
{
    public string Id { get; init; } = string.Empty;

    // The full path of this Mind's socket on the peer-checked door. Bifrost only connects to it.
    public string Socket { get; init; } = string.Empty;

    // Accounts that may answer this Mind's forks. Sent to Huginn on every call; no request sets or extends it.
    public List<string> Deciders { get; init; } = [];
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
