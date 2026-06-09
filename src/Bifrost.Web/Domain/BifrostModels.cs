using System.ComponentModel.DataAnnotations.Schema;

namespace Bifrost.Web.Domain;

public enum MembershipStatus
{
    Authenticated,
    PendingApproval,
    Active,
    Suspended
}

public enum MemberRole
{
    StandardMember,
    Producer,
    Maintainer,
    LedgerReviewer,
    PlatformAdmin
}

public enum TierSnapshotKind
{
    Patron,
    Contributor
}

public enum ProjectStatus
{
    Proposed,
    Active,
    Paused,
    Archived
}

public enum WorkItemSourceType
{
    GitHubIssue,
    Internal
}

public enum WorkItemStatus
{
    Proposed,
    Open,
    Volunteered,
    Assigned,
    InProgress,
    SubmittedForReview,
    ChangesRequested,
    Approved,
    Completed,
    Blocked,
    Archived
}

public enum WorkItemSkillLevel
{
    Routine,
    Specialized,
    Senior,
    Lead
}

public enum VolunteerClaimStatus
{
    Active,
    Withdrawn,
    Accepted
}

public enum WorkLogApprovalStatus
{
    Submitted,
    Approved,
    Rejected
}

public enum WorkReviewStatus
{
    Pending,
    ChangesRequested,
    Approved
}

public enum MotionScope
{
    Management,
    Project
}

public enum MotionCategory
{
    Bugs,
    Cosmetics,
    BalanceChanges,
    Features,
    NewContent,
    FundamentalDesignChanges
}

public enum MotionStatus
{
    Open,
    Passed,
    Failed,
    Closed
}

public enum VoteChoice
{
    For,
    Against,
    Abstain
}

public enum LedgerEntryType
{
    PatronCredit,
    ContributionCredit,
    NominalCompensation,
    Adjustment
}

public enum LedgerEntryStatus
{
    Draft,
    Approved,
    IncludedInProposal
}

public enum PayoutProposalBatchStatus
{
    Draft,
    Reviewed,
    Closed
}

public enum GitHubEntityState
{
    Open,
    Closed,
    Merged
}

public enum GitHubReviewDecision
{
    None,
    Commented,
    ChangesRequested,
    Approved
}

public enum PointTransactionType
{
    ManualAdjustment,
    ApprovedWork,
    ContinuousRoleCredit,
    PatronSupport,
    Decay
}

public enum PatronSupportEventKind
{
    OneTimeDonation,
    RecurringSupportSnapshot
}

public sealed class UserAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public long? GitHubUserId { get; set; }

    public string HeimdallAccountId { get; set; } = string.Empty;

    public string GitHubLogin { get; set; } = string.Empty;

    public string NormalizedGitHubLogin { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string AvatarUrl { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset LastSeenAtUtc { get; set; }

    public MemberProfile? MemberProfile { get; set; }

    public Membership? Membership { get; set; }

    public ICollection<Project> OwnedProjects { get; set; } = [];

    public ICollection<WorkItem> RequestedWorkItems { get; set; } = [];

    public ICollection<WorkItem> AssignedWorkItems { get; set; } = [];

    public ICollection<VolunteerClaim> VolunteerClaims { get; set; } = [];

    public ICollection<Assignment> Assignments { get; set; } = [];

    public ICollection<WorkLog> WorkLogs { get; set; } = [];

    public ICollection<WorkReview> WorkReviews { get; set; } = [];

    public ICollection<Motion> CreatedMotions { get; set; } = [];

    public ICollection<Vote> Votes { get; set; } = [];

    public ICollection<LedgerEntry> LedgerEntries { get; set; } = [];

    public ICollection<PointTransaction> PointTransactions { get; set; } = [];

    public ICollection<PatronSupportEvent> PatronSupportEvents { get; set; } = [];

    public ICollection<AuditEvent> AuditEvents { get; set; } = [];
}

public sealed class MemberProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserAccountId { get; set; }

    public string Nickname { get; set; } = string.Empty;

    public string Headline { get; set; } = string.Empty;

    public string PortfolioUrl { get; set; } = string.Empty;

    public string Skills { get; set; } = string.Empty;

    public string Availability { get; set; } = string.Empty;

    public string Notes { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public UserAccount UserAccount { get; set; } = null!;
}

public sealed class Membership
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserAccountId { get; set; }

    public MembershipStatus Status { get; set; } = MembershipStatus.Authenticated;

    public bool IsPlatformAdmin { get; set; }

    public bool CanManageProjects { get; set; }

    public bool CanManageLedger { get; set; }

    public bool CanModerateMotions { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? ApprovedAtUtc { get; set; }

    public string Notes { get; set; } = string.Empty;

    public UserAccount UserAccount { get; set; } = null!;

    public ICollection<RoleAssignment> RoleAssignments { get; set; } = [];

    public ICollection<TierSnapshot> TierSnapshots { get; set; } = [];

    [NotMapped]
    public decimal EffectiveVotingWeight =>
        Status != MembershipStatus.Active
            ? 0m
            : Math.Max(1m, TierSnapshots.Where(x => x.IsCurrent).Sum(x => x.Weight));
}

public sealed class RoleAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid MembershipId { get; set; }

    public MemberRole Role { get; set; } = MemberRole.StandardMember;

    public Guid? AssignedByUserAccountId { get; set; }

    public DateTimeOffset AssignedAtUtc { get; set; }

    public string Notes { get; set; } = string.Empty;

    public Membership Membership { get; set; } = null!;

    public UserAccount? AssignedByUserAccount { get; set; }
}

public sealed class TierSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid MembershipId { get; set; }

    public TierSnapshotKind Kind { get; set; }

    public string Label { get; set; } = string.Empty;

    public decimal Weight { get; set; }

    public bool IsCurrent { get; set; } = true;

    public DateTimeOffset EffectiveFromUtc { get; set; }

    public DateTimeOffset CapturedAtUtc { get; set; }

    public string Notes { get; set; } = string.Empty;

    public Membership Membership { get; set; } = null!;
}

public sealed class MembershipInvitation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string GitHubLogin { get; set; } = string.Empty;

    public string NormalizedGitHubLogin { get; set; } = string.Empty;

    public Guid? IssuedByUserAccountId { get; set; }

    public Guid? AcceptedByUserAccountId { get; set; }

    public DateTimeOffset IssuedAtUtc { get; set; }

    public DateTimeOffset? AcceptedAtUtc { get; set; }

    public DateTimeOffset? RevokedAtUtc { get; set; }

    public string Notes { get; set; } = string.Empty;

    public UserAccount? IssuedByUserAccount { get; set; }

    public UserAccount? AcceptedByUserAccount { get; set; }
}

public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OwnerUserAccountId { get; set; }

    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string GitHubRepository { get; set; } = string.Empty;

    public ProjectStatus Status { get; set; } = ProjectStatus.Active;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public UserAccount OwnerUserAccount { get; set; } = null!;

    public ICollection<WorkItem> WorkItems { get; set; } = [];

    public ICollection<Motion> Motions { get; set; } = [];

    public ICollection<LedgerEntry> LedgerEntries { get; set; } = [];

    public ICollection<PointTransaction> PointTransactions { get; set; } = [];

    public ICollection<RevenueEvent> RevenueEvents { get; set; } = [];

    public ICollection<RevenueShareLine> RevenueShareLines { get; set; } = [];
}

public sealed class WorkItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }

    public Guid RequestedByUserAccountId { get; set; }

    public Guid? AssignedToUserAccountId { get; set; }

    public WorkItemSourceType SourceType { get; set; } = WorkItemSourceType.Internal;

    public string ExternalSourceId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public WorkItemSkillLevel SkillLevel { get; set; } = WorkItemSkillLevel.Routine;

    public WorkItemStatus Status { get; set; } = WorkItemStatus.Open;

    public WorkReviewStatus ReviewStatus { get; set; } = WorkReviewStatus.Pending;

    public decimal EstimatedHours { get; set; }

    public decimal ContributionPoints { get; set; }

    public DateTimeOffset? TargetDateUtc { get; set; }

    public DateTimeOffset? SubmittedAtUtc { get; set; }

    public DateTimeOffset? CompletedAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public Project Project { get; set; } = null!;

    public UserAccount RequestedByUserAccount { get; set; } = null!;

    public UserAccount? AssignedToUserAccount { get; set; }

    public ICollection<VolunteerClaim> VolunteerClaims { get; set; } = [];

    public ICollection<Assignment> Assignments { get; set; } = [];

    public ICollection<WorkLog> WorkLogs { get; set; } = [];

    public ICollection<WorkReview> WorkReviews { get; set; } = [];

    public ICollection<LedgerEntry> LedgerEntries { get; set; } = [];

    public ICollection<PointTransaction> PointTransactions { get; set; } = [];

    public GitHubIssueLink? GitHubIssueLink { get; set; }

    public ICollection<GitHubPullRequestLink> GitHubPullRequestLinks { get; set; } = [];
}

public sealed class VolunteerClaim
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkItemId { get; set; }

    public Guid UserAccountId { get; set; }

    public VolunteerClaimStatus Status { get; set; } = VolunteerClaimStatus.Active;

    public string Note { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public WorkItem WorkItem { get; set; } = null!;

    public UserAccount UserAccount { get; set; } = null!;
}

public sealed class Assignment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkItemId { get; set; }

    public Guid UserAccountId { get; set; }

    public Guid AssignedByUserAccountId { get; set; }

    public string Note { get; set; } = string.Empty;

    public DateTimeOffset AssignedAtUtc { get; set; }

    public WorkItem WorkItem { get; set; } = null!;

    public UserAccount UserAccount { get; set; } = null!;

    public UserAccount AssignedByUserAccount { get; set; } = null!;
}

public sealed class WorkLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkItemId { get; set; }

    public Guid UserAccountId { get; set; }

    public decimal Hours { get; set; }

    public string Note { get; set; } = string.Empty;

    public WorkLogApprovalStatus ApprovalStatus { get; set; } = WorkLogApprovalStatus.Submitted;

    public Guid? ReviewedByUserAccountId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? ReviewedAtUtc { get; set; }

    public WorkItem WorkItem { get; set; } = null!;

    public UserAccount UserAccount { get; set; } = null!;

    public UserAccount? ReviewedByUserAccount { get; set; }
}

public sealed class WorkReview
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkItemId { get; set; }

    public Guid? ReviewerUserAccountId { get; set; }

    public string ReviewerName { get; set; } = string.Empty;

    public WorkReviewStatus Status { get; set; }

    public string Note { get; set; } = string.Empty;

    public DateTimeOffset ReviewedAtUtc { get; set; }

    public WorkItem WorkItem { get; set; } = null!;

    public UserAccount? ReviewerUserAccount { get; set; }
}

public sealed class GitHubIssueLink
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkItemId { get; set; }

    public string RepositoryFullName { get; set; } = string.Empty;

    public int IssueNumber { get; set; }

    public GitHubEntityState State { get; set; } = GitHubEntityState.Open;

    public string IssueUrl { get; set; } = string.Empty;

    public string TitleSnapshot { get; set; } = string.Empty;

    public DateTimeOffset LastSynchronizedAtUtc { get; set; }

    public WorkItem WorkItem { get; set; } = null!;
}

public sealed class GitHubPullRequestLink
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkItemId { get; set; }

    public string RepositoryFullName { get; set; } = string.Empty;

    public int PullRequestNumber { get; set; }

    public GitHubEntityState State { get; set; } = GitHubEntityState.Open;

    public bool IsMerged { get; set; }

    public string PullRequestUrl { get; set; } = string.Empty;

    public string TitleSnapshot { get; set; } = string.Empty;

    public GitHubReviewDecision ReviewDecision { get; set; } = GitHubReviewDecision.None;

    public DateTimeOffset LastSynchronizedAtUtc { get; set; }

    public WorkItem WorkItem { get; set; } = null!;
}

public sealed class Motion
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? ProjectId { get; set; }

    public Guid CreatedByUserAccountId { get; set; }

    public MotionScope Scope { get; set; } = MotionScope.Project;

    public MotionCategory Category { get; set; } = MotionCategory.Features;

    public MotionStatus Status { get; set; } = MotionStatus.Open;

    public string Title { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public decimal ApprovalThreshold { get; set; } = 0.50m;

    public DateTimeOffset OpensAtUtc { get; set; }

    public DateTimeOffset ClosesAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? ResolvedAtUtc { get; set; }

    public string ResolutionNote { get; set; } = string.Empty;

    public Project? Project { get; set; }

    public UserAccount CreatedByUserAccount { get; set; } = null!;

    public ICollection<Vote> Votes { get; set; } = [];
}

public sealed class Vote
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid MotionId { get; set; }

    public Guid UserAccountId { get; set; }

    public VoteChoice Choice { get; set; }

    public decimal Weight { get; set; }

    public string Comment { get; set; } = string.Empty;

    public DateTimeOffset CastAtUtc { get; set; }

    public Motion Motion { get; set; } = null!;

    public UserAccount UserAccount { get; set; } = null!;
}

public sealed class LedgerEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserAccountId { get; set; }

    public Guid? ProjectId { get; set; }

    public Guid? WorkItemId { get; set; }

    public Guid CreatedByUserAccountId { get; set; }

    public LedgerEntryType EntryType { get; set; } = LedgerEntryType.ContributionCredit;

    public LedgerEntryStatus Status { get; set; } = LedgerEntryStatus.Draft;

    public decimal Points { get; set; }

    public decimal NominalAmount { get; set; }

    public string Note { get; set; } = string.Empty;

    public DateTimeOffset EffectiveAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public UserAccount UserAccount { get; set; } = null!;

    public UserAccount CreatedByUserAccount { get; set; } = null!;

    public Project? Project { get; set; }

    public WorkItem? WorkItem { get; set; }
}

public sealed class PayoutProposalBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CreatedByUserAccountId { get; set; }

    public PayoutProposalBatchStatus Status { get; set; } = PayoutProposalBatchStatus.Draft;

    public string Summary { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public UserAccount CreatedByUserAccount { get; set; } = null!;
}

public sealed class PointTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserAccountId { get; set; }

    public Guid? ProjectId { get; set; }

    public Guid? WorkItemId { get; set; }

    public PointTransactionType Type { get; set; } = PointTransactionType.ManualAdjustment;

    public decimal Amount { get; set; }

    public bool IsDecaying { get; set; } = true;

    public string Note { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public UserAccount UserAccount { get; set; } = null!;

    public Project? Project { get; set; }

    public WorkItem? WorkItem { get; set; }
}

public sealed class PatronSupportEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserAccountId { get; set; }

    public string ExternalSupportId { get; set; } = string.Empty;

    public PatronSupportEventKind Kind { get; set; } = PatronSupportEventKind.OneTimeDonation;

    public decimal Amount { get; set; }

    public string CurrencyCode { get; set; } = "USD";

    public bool IsCurrentRecurringSupport { get; set; }

    public DateTimeOffset SupportedAtUtc { get; set; }

    public DateTimeOffset RecordedAtUtc { get; set; }

    public string Notes { get; set; } = string.Empty;

    public UserAccount UserAccount { get; set; } = null!;
}

public sealed class DecayRun
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public decimal Rate { get; set; }

    public string Notes { get; set; } = string.Empty;

    public DateTimeOffset RanAtUtc { get; set; }
}

public sealed class RevenueEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? ProjectId { get; set; }

    public decimal Amount { get; set; }

    public string CurrencyCode { get; set; } = "USD";

    public string Notes { get; set; } = string.Empty;

    public DateTimeOffset ReceivedAtUtc { get; set; }

    public Project? Project { get; set; }

    public ICollection<RevenueShareBatch> RevenueShareBatches { get; set; } = [];
}

public sealed class RevenueShareBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? RevenueEventId { get; set; }

    public Guid CreatedByUserAccountId { get; set; }

    public string Summary { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public RevenueEvent? RevenueEvent { get; set; }

    public UserAccount CreatedByUserAccount { get; set; } = null!;

    public ICollection<RevenueShareLine> Lines { get; set; } = [];
}

public sealed class RevenueShareLine
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RevenueShareBatchId { get; set; }

    public Guid? UserAccountId { get; set; }

    public Guid? ProjectId { get; set; }

    public decimal Amount { get; set; }

    public string Basis { get; set; } = string.Empty;

    public string Notes { get; set; } = string.Empty;

    public RevenueShareBatch RevenueShareBatch { get; set; } = null!;

    public UserAccount? UserAccount { get; set; }

    public Project? Project { get; set; }
}

public sealed class AuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? ActorUserAccountId { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public string EntityId { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    public DateTimeOffset OccurredAtUtc { get; set; }

    public UserAccount? ActorUserAccount { get; set; }
}
