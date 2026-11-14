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

    // ---- the saga's columns (AccountMergeSaga is their one writer; docs/account-merge.md, Saga) ----

    /// <summary>Heimdall's idempotency key of the forward move: random, minted in the save that enters Confirmed, never derived from <see cref="Id"/>, never re-minted.</summary>
    public string? HeimdallMoveId { get; set; }

    /// <summary>The same for the reversal, minted in the save that enters UndoRequested.</summary>
    public string? HeimdallReverseMoveId { get; set; }

    /// <summary>The Heimdall bearers the move or reversal will replay, protected; non-null only from Confirmed/UndoRequested until Heimdall's final answer.</summary>
    public string? SealedProofs { get; set; }

    /// <summary>Digest of the plan the member saw when confirming (MergePlan.Digest); with the next four columns it is the consent record.</summary>
    public string PreviewDigest { get; set; } = string.Empty;

    public Guid? ConfirmedByUserAccountId { get; set; }

    public DateTimeOffset? ConfirmedUtc { get; set; }

    public string SurvivorProofProvider { get; set; } = string.Empty;

    /// <summary>The providers of the folded side's proofs, sorted and comma-joined.</summary>
    public string FoldedProofProviders { get; set; } = string.Empty;

    /// <summary>The proof tiers Heimdall's receipt named (weak or strong); ProofToTier is null for a receipt without one.</summary>
    public string? ProofFromTier { get; set; }

    public string? ProofToTier { get; set; }

    /// <summary>The logins the forward move carried, a JSON array of Heimdall identity ids, from the receipt.</summary>
    public string? MovedIdentityIds { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset? NextAttemptUtc { get; set; }

    /// <summary>The last Heimdall answer's class or refusal code, or the saga's own stall code; never an input value.</summary>
    public string LastOutcome { get; set; } = string.Empty;

    public DateTimeOffset? UndoRequestedUtc { get; set; }

    public DateTimeOffset? ReversedUtc { get; set; }

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

/// <summary>What a merge notice tells its reader: the merge committed, was undone, or can no longer be undone. Stored as a string.</summary>
public enum AccountMergeNoticeKind
{
    Merged,
    Undone,
    Final
}

/// <summary>Who reads the notice feed. A closed set: a new reader is a new constant (and its own token), never a string from a request. Stored as a string.</summary>
public enum AccountMergeNoticeTarget
{
    Chat
}

/// <summary>
/// The one fact a notice reader adds: it has seen this notice. The notice itself is derived from the merge row on every read
/// (MergeNoticeFeed) and has no stored status; nothing but the feed writes this row.
/// </summary>
public sealed class AccountMergeNoticeAck
{
    public Guid MergeId { get; set; }

    public AccountMergeNoticeKind Kind { get; set; }

    public AccountMergeNoticeTarget Target { get; set; }

    public DateTimeOffset AckedUtc { get; set; }
}
