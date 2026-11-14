using Bifrost.Web.Domain;

namespace Bifrost.Web.Features.Membership;

/// <summary>Skeleton: the closed words the merge pages say. Filled in by the cut's next commit.</summary>
public static class MergeWording
{
    public static string State(AccountMergeState state) => string.Empty;

    public static string? Failure(string code) => null;

    public static string? Outcome(string lastOutcome) => null;

    public static string Undo(string code) => string.Empty;

    public static string UndoCode(UndoOutcome outcome) => string.Empty;

    public static string TierNote(string? fromTier, DateTimeOffset graceEnds) => string.Empty;

    public static string DaysLeft(int days) => string.Empty;

    public static string SurvivorNotice(int days) => string.Empty;

    public static string FrozenNotice() => string.Empty;

    public static string FoldedNotice(DateTimeOffset mergedOn) => string.Empty;
}
