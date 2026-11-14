namespace Bifrost.Web.Domain;

/// <summary>The whole lifecycle of an account merge, declared once; later cuts add columns, not states. Stored as a string.</summary>
public enum AccountMergeState
{
    Proposed,
    Confirmed,
    HeimdallMoved,
    Committed,
    Final,
    UndoRequested,
    HeimdallReversed,
    Undone,
    Failed
}

public enum AccountMergeEntryKind
{
    Vote,
    ConceptAppraisal,
    Membership,
    Standing,
    PatronSupportEvent
}

public enum AccountMergeEntryAction
{
    Voided,
    Moved,
    Closed,
    Suspended,
    Created,
    Restored,
    KeptAfterUndo
}

/// <summary>
/// The merge receipt: two Heimdall-backed accounts, one survivor. Whether an account is folded or frozen is derived from this row's
/// State on every request (AccountMergeLedger.StandingAsync); no column on the account says so.
/// </summary>
public sealed class AccountMerge
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public AccountMergeState State { get; set; } = AccountMergeState.Proposed;

    public Guid SurvivorUserAccountId { get; set; }

    public Guid FoldedUserAccountId { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset? CommittedUtc { get; set; }

    public DateTimeOffset? GraceEndsUtc { get; set; }

    public DateTimeOffset? FinalizedUtc { get; set; }

    public DateTimeOffset? UndoneUtc { get; set; }

    public DateTimeOffset? FailedUtc { get; set; }

    public string FailureCode { get; set; } = string.Empty;

    public ICollection<AccountMergeEntry> Entries { get; set; } = [];
}

/// <summary>Append-only journal of what a merge did to one record; undo and the later preview read it, tallies and gates never do.</summary>
public sealed class AccountMergeEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid MergeId { get; set; }

    public int Seq { get; set; }

    public AccountMergeEntryKind Kind { get; set; }

    public Guid RecordId { get; set; }

    public AccountMergeEntryAction Action { get; set; }

    public Guid? PriorUserAccountId { get; set; }

    /// <summary>What the record held before: the prior status; for a moved vote its weight; for a moved appraisal its voter class and key (class|key).</summary>
    public string? PriorText { get; set; }

    /// <summary>The cast time of a moved vote or appraisal, so undo can tell whether the survivor re-cast it.</summary>
    public DateTimeOffset? PriorAtUtc { get; set; }

    public DateTimeOffset AtUtc { get; set; }

    public AccountMerge Merge { get; set; } = null!;
}
