using System.ComponentModel.DataAnnotations.Schema;

namespace Bifrost.Web.Domain;

public enum MembershipStatus
{
    Authenticated,
    PendingApproval,
    Active,
    Suspended
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
    Backlog,
    Open,
    Claimed,
    InProgress,
    Done,
    Blocked,
    Archived
}

public enum VolunteerClaimStatus
{
    Active,
    Withdrawn,
    Accepted
}

public enum MotionScope
{
    Management,
    Project
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

public sealed class UserAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public long GitHubUserId { get; set; }

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

    public ICollection<Motion> CreatedMotions { get; set; } = [];

    public ICollection<Vote> Votes { get; set; } = [];

    public ICollection<LedgerEntry> LedgerEntries { get; set; } = [];

    public ICollection<AuditEvent> AuditEvents { get; set; } = [];
}

public sealed class MemberProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserAccountId { get; set; }

    public string Headline { get; set; } = string.Empty;

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

    public decimal PatronWeight { get; set; }

    public decimal ContributorWeight { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? ApprovedAtUtc { get; set; }

    public string Notes { get; set; } = string.Empty;

    [NotMapped]
    public decimal EffectiveVotingWeight => Math.Max(1m, PatronWeight + ContributorWeight);

    public UserAccount UserAccount { get; set; } = null!;
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

    public ProjectStatus Status { get; set; } = ProjectStatus.Active;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public UserAccount OwnerUserAccount { get; set; } = null!;

    public ICollection<WorkItem> WorkItems { get; set; } = [];

    public ICollection<Motion> Motions { get; set; } = [];

    public ICollection<LedgerEntry> LedgerEntries { get; set; } = [];
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

    public WorkItemStatus Status { get; set; } = WorkItemStatus.Open;

    public decimal EffortPoints { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public Project Project { get; set; } = null!;

    public UserAccount RequestedByUserAccount { get; set; } = null!;

    public UserAccount? AssignedToUserAccount { get; set; }

    public ICollection<VolunteerClaim> VolunteerClaims { get; set; } = [];

    public ICollection<Assignment> Assignments { get; set; } = [];

    public ICollection<LedgerEntry> LedgerEntries { get; set; } = [];
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

public sealed class Motion
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? ProjectId { get; set; }

    public Guid CreatedByUserAccountId { get; set; }

    public MotionScope Scope { get; set; } = MotionScope.Project;

    public MotionStatus Status { get; set; } = MotionStatus.Open;

    public string Title { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public decimal ApprovalThreshold { get; set; } = 0.60m;

    public DateTimeOffset OpensAtUtc { get; set; }

    public DateTimeOffset ClosesAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

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
