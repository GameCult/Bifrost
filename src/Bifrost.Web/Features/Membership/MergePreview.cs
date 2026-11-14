using Bifrost.Web.Domain;

namespace Bifrost.Web.Features.Membership;

/// <summary>
/// The only wording of what a merge would do, in closed vocabulary: counts by entry kind and action from <see cref="MergePlan"/>, the
/// sentence for each block code, and the statements every merge carries. It decides nothing, and names no account: the plan holds none.
/// </summary>
public static class MergePreview
{
    public static IReadOnlyList<string> Lines(MergePlan plan)
    {
        var lines = new List<string>();
        foreach (var group in plan.Entries.GroupBy(x => (x.Kind, x.Action)))
        {
            var n = group.Count();
            lines.Add(group.Key switch
            {
                (AccountMergeEntryKind.Vote, AccountMergeEntryAction.Moved) =>
                    $"{Count(n, "open vote")} of the other account on motions you did not vote on move to the kept account.",
                (AccountMergeEntryKind.Vote, AccountMergeEntryAction.Voided) =>
                    $"{Count(n, "open vote")} of the other account are set aside: the kept account already voted on that motion or cannot vote.",
                (AccountMergeEntryKind.ConceptAppraisal, AccountMergeEntryAction.Moved) =>
                    $"{Count(n, "appraisal")} of the other account move to the kept account.",
                (AccountMergeEntryKind.ConceptAppraisal, AccountMergeEntryAction.Voided) =>
                    $"{Count(n, "appraisal")} of the other account are set aside: the kept account already appraised the same thing or cannot.",
                (AccountMergeEntryKind.Membership, AccountMergeEntryAction.Closed) =>
                    "The other account's membership closes; the kept account's membership stays.",
                (AccountMergeEntryKind.Standing, _) =>
                    "The kept account is suspended too, because the other account is.",
                (AccountMergeEntryKind.PatronSupportEvent, AccountMergeEntryAction.Moved) =>
                    $"{Count(n, "patron event")} of the other account count for the kept account.",
                _ => $"{Count(n, "other record")} change."
            });
        }

        foreach (var (key, n) in plan.Left.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (n > 0 && Remains(key) is { } noun)
            {
                lines.Add($"{Count(n, noun)} stay{(n == 1 ? "s" : string.Empty)} where {(n == 1 ? "it is" : "they are")}.");
            }
        }

        if (lines.Count == 0)
        {
            lines.Add("No votes, appraisals or patron events change.");
        }

        return lines;
    }

    /// <summary>Why a pair cannot be merged, by the plan's block code. No account is named; a code this class does not know gets a generic line, never an echo.</summary>
    public static string Block(string code) => code switch
    {
        "same_account" => "These are the same account.",
        "account_missing" => "One of the accounts no longer exists.",
        "mind_account" => "An account that belongs to a Mind cannot be merged.",
        "native_account" => "An account without a Heimdall sign-in cannot be merged.",
        "merge_in_flight" => "One of the accounts is already part of a merge that has not finished.",
        "survivor_is_folded" => "The account you would keep was already merged into another account.",
        "already_folded" => "The other account was already merged into another account.",
        "folded_is_survivor" => "Another account was merged into the other account, so it cannot be folded in turn.",
        "sealed_ballot_pending" => "The other account has a sealed ballot on an open motion; wait until it is revealed.",
        _ => "These accounts cannot be merged now."
    };

    /// <summary>The block for two accounts that both hold a login of one provider, which Heimdall will not combine. Providers only: no account is named.</summary>
    public static string ProviderConflict(IEnumerable<string> providerNames) =>
        $"Both accounts hold a {string.Join(" and ", providerNames)} login, and Heimdall will not combine two logins of one provider. Unlink one of them first, then prove that account again: the sign-in you gave still carries the old list of its logins.";

    /// <summary>What every merge says, whatever the plan: what it cannot carry over, what it asks again, and how long it can be undone.</summary>
    public static IReadOnlyList<string> Statements { get; } =
    [
        "Chat: your chat history, rooms and keys belong to one chat id and cannot be moved. Keep the account you use in chat. Whichever you keep, the other chat id stays behind: rooms you were in on it must be joined again with the kept one, and messages encrypted for it cannot be read on the kept one.",
        "Your connections (Twitch, Discord and others) will ask for consent again.",
        "If you stream or moderate with StreamPixels and one of these connections is the one it uses for your channel, that connection stops until you reconnect it.",
        "Roles and admin rights of the other account end; a steward must grant them again.",
        "You will have one membership and one vote per motion: where both accounts voted on an open motion, the kept account's vote counts and the other is set aside. Nothing is added together.",
        "You can undo this for 7 days, from Merge status. After that it is final.",
        "The merge is approved with ordinary sign-ins, which is the weaker proof, and a notice stays on your account until the 7 days end."
    ];

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static string? Remains(string key) => key switch
    {
        "ConsentReceipts" => "consent record",
        "PasskeyCredentials" => "passkey",
        "LedgerEntries" => "ledger entry",
        _ => null
    };
}
