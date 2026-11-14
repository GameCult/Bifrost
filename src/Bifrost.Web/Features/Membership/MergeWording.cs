using System.Globalization;
using Bifrost.Web.Domain;

namespace Bifrost.Web.Features.Membership;

/// <summary>
/// The one closed set of words the merge pages say, so no page ever prints what the store holds: a state, a Heimdall refusal code, a
/// saga outcome and an undo answer each map to a sentence here, and anything this class does not know gets a generic line, never an
/// echo. Dates are UTC.
/// </summary>
public static class MergeWording
{
    public static string State(AccountMergeState state) => state switch
    {
        AccountMergeState.Proposed => "Proposed, not yet confirmed.",
        AccountMergeState.Confirmed => "Confirmed; Heimdall has not yet moved the logins.",
        AccountMergeState.HeimdallMoved => "The logins have moved; Bifrost is finishing the merge.",
        AccountMergeState.Committed => "Merged. You can still undo it.",
        AccountMergeState.Final => "Merged for good; the undo window has ended.",
        AccountMergeState.UndoRequested => "Undo requested; the logins are being given back.",
        AccountMergeState.HeimdallReversed => "The logins are back; Bifrost is restoring the accounts.",
        AccountMergeState.Undone => "Undone; the accounts are separate again.",
        AccountMergeState.Failed => "The merge did not happen and nothing was merged.",
        _ => "In an unknown state."
    };

    /// <summary>Why Heimdall refused a merge, by its refusal code; null for a code this class does not know.</summary>
    public static string? Failure(string code) => code switch
    {
        "app_auth_required" or "not_allowed_for_app" => "Heimdall would not take Bifrost's request.",
        "not_reversible" => "Heimdall says those logins cannot be moved back.",
        "proof_invalid" => "Heimdall did not accept a sign-in as proof.",
        "same_account" => "Both sides were the same Heimdall account.",
        "proof_stale" => "A sign-in was too old by the time Heimdall saw it.",
        "identity_not_proved" => "A login was not proved by a fresh sign-in.",
        "already_moved" => "Heimdall had already moved one of those logins.",
        "provider_conflict" => "Both accounts hold a login of the same provider, which Heimdall will not combine.",
        "identity_not_found" => "A login was no longer on its account.",
        "invalid_request" => "Heimdall could not read the request.",
        _ => null
    };

    /// <summary>The saga's last note on a merge, in words. Always a sentence: an outcome this class does not know is a retry line, never its own text.</summary>
    public static string Outcome(string lastOutcome) => lastOutcome switch
    {
        "confirmed" => "Waiting for Heimdall to move the logins.",
        "moved" => "Heimdall moved the logins.",
        "refused" => "Heimdall refused the merge.",
        "undo_requested" => "Waiting for Heimdall to give the logins back.",
        "receipt_mismatch" => "Heimdall's answer did not match the merge. An operator must look at it.",
        "proofs_unreadable" => "The stored proofs cannot be read. An operator must look at it.",
        "transport" or "move_id_conflict" or "malformed or mismatched receipt" => "Heimdall could not be reached or gave an answer Bifrost could not use. It will try again.",
        _ when lastOutcome.StartsWith("undo_", StringComparison.Ordinal) && Failure(lastOutcome["undo_".Length..]) is { } reason =>
            "Heimdall refused the undo, so the merge stands. " + reason,
        _ => "Waiting to try again."
    };

    /// <summary>The words for a closed undo code (<see cref="UndoCode"/> or <c>logins_unproved</c>); empty for anything else.</summary>
    public static string Undo(string code) => code switch
    {
        "requested" => "Undo requested. The logins are being given back.",
        "not_committed" => "This merge is not in a state that can be undone.",
        "grace_over" => "The undo window has ended.",
        "not_survivor" => "Only the account the merge kept can undo it.",
        "proof_account_mismatch" => "That sign-in was not for this account. Nothing was changed.",
        "logins_unproved" => "Every login that moved needs a fresh sign-in first. Nothing was changed.",
        _ => string.Empty
    };

    public static string UndoCode(UndoOutcome outcome) => outcome switch
    {
        UndoOutcome.Requested => "requested",
        UndoOutcome.NotCommitted => "not_committed",
        UndoOutcome.GraceOver => "grace_over",
        UndoOutcome.NotSurvivor => "not_survivor",
        UndoOutcome.ProofAccountMismatch => "proof_account_mismatch",
        _ => "not_committed"
    };

    public static string TierNote(string? fromTier, DateTimeOffset graceEnds) => fromTier == "strong"
        ? $"Approved with a passkey; you have until {Date(graceEnds)} to undo."
        : $"Approved with ordinary sign-ins; you have until {Date(graceEnds)} to undo.";

    public static string DaysLeft(int days) => days == 1 ? "1 day left" : $"{days} days left";

    public static string SurvivorNotice(int days) => $"A merge into your account can still be undone: {DaysLeft(days)}. Review it on Merge status.";

    public static string FrozenNotice() => "A merge is in progress; you cannot act until it settles.";

    public static string FoldedNotice(DateTimeOffset mergedOn) =>
        $"This account was merged on {Date(mergedOn)}; sign in with a login you had on it to reach the account that holds them.";

    public static string Date(DateTimeOffset when) => when.UtcDateTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture) + " UTC";
}
