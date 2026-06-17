using Bifrost.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Web.Data;

public sealed class BifrostDbContext(DbContextOptions<BifrostDbContext> options) : DbContext(options)
{
    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();

    public DbSet<MemberProfile> MemberProfiles => Set<MemberProfile>();

    public DbSet<Membership> Memberships => Set<Membership>();

    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();

    public DbSet<TierSnapshot> TierSnapshots => Set<TierSnapshot>();

    public DbSet<MembershipInvitation> MembershipInvitations => Set<MembershipInvitation>();

    public DbSet<Project> Projects => Set<Project>();

    public DbSet<WorkItem> WorkItems => Set<WorkItem>();

    public DbSet<VolunteerClaim> VolunteerClaims => Set<VolunteerClaim>();

    public DbSet<Assignment> Assignments => Set<Assignment>();

    public DbSet<WorkLog> WorkLogs => Set<WorkLog>();

    public DbSet<WorkReview> WorkReviews => Set<WorkReview>();

    public DbSet<GitHubIssueLink> GitHubIssueLinks => Set<GitHubIssueLink>();

    public DbSet<GitHubPullRequestLink> GitHubPullRequestLinks => Set<GitHubPullRequestLink>();

    public DbSet<Motion> Motions => Set<Motion>();

    public DbSet<Vote> Votes => Set<Vote>();

    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    public DbSet<PayoutProposalBatch> PayoutProposalBatches => Set<PayoutProposalBatch>();

    public DbSet<PointTransaction> PointTransactions => Set<PointTransaction>();

    public DbSet<PatronSupportEvent> PatronSupportEvents => Set<PatronSupportEvent>();

    public DbSet<DecayRun> DecayRuns => Set<DecayRun>();

    public DbSet<RevenueEvent> RevenueEvents => Set<RevenueEvent>();

    public DbSet<RevenueShareBatch> RevenueShareBatches => Set<RevenueShareBatch>();

    public DbSet<RevenueShareLine> RevenueShareLines => Set<RevenueShareLine>();

    public DbSet<BridgeAction> BridgeActions => Set<BridgeAction>();

    public DbSet<AgentDispatchRun> AgentDispatchRuns => Set<AgentDispatchRun>();

    public DbSet<AgentTransportReceipt> AgentTransportReceipts => Set<AgentTransportReceipt>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserAccount>(entity =>
        {
            entity.HasIndex(x => x.GitHubUserId).IsUnique().HasFilter("\"GitHubUserId\" IS NOT NULL");
            entity.HasIndex(x => x.HeimdallAccountId).IsUnique().HasFilter("\"HeimdallAccountId\" <> ''");
            entity.HasIndex(x => x.NormalizedGitHubLogin).IsUnique().HasFilter("\"NormalizedGitHubLogin\" <> ''");
            entity.Property(x => x.HeimdallAccountId).HasMaxLength(160);
            entity.Property(x => x.GitHubLogin).HasMaxLength(120);
            entity.Property(x => x.NormalizedGitHubLogin).HasMaxLength(120);
            entity.Property(x => x.DisplayName).HasMaxLength(200);
            entity.Property(x => x.AvatarUrl).HasMaxLength(500);
        });

        modelBuilder.Entity<MemberProfile>(entity =>
        {
            entity.HasOne(x => x.UserAccount)
                .WithOne(x => x.MemberProfile)
                .HasForeignKey<MemberProfile>(x => x.UserAccountId);

            entity.Property(x => x.Nickname).HasMaxLength(120);
            entity.Property(x => x.Headline).HasMaxLength(240);
            entity.Property(x => x.PortfolioUrl).HasMaxLength(500);
        });

        modelBuilder.Entity<Membership>(entity =>
        {
            entity.HasOne(x => x.UserAccount)
                .WithOne(x => x.Membership)
                .HasForeignKey<Membership>(x => x.UserAccountId);

            entity.Property(x => x.Status).HasConversion<string>();
        });

        modelBuilder.Entity<RoleAssignment>(entity =>
        {
            entity.HasIndex(x => new { x.MembershipId, x.Role }).IsUnique();
            entity.Property(x => x.Role).HasConversion<string>();
            entity.HasOne(x => x.Membership)
                .WithMany(x => x.RoleAssignments)
                .HasForeignKey(x => x.MembershipId);
            entity.HasOne(x => x.AssignedByUserAccount)
                .WithMany()
                .HasForeignKey(x => x.AssignedByUserAccountId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<TierSnapshot>(entity =>
        {
            entity.Property(x => x.Kind).HasConversion<string>();
            entity.Property(x => x.Label).HasMaxLength(120);
            entity.Property(x => x.Weight).HasPrecision(18, 2);
            entity.HasIndex(x => new { x.MembershipId, x.Kind, x.IsCurrent });
            entity.HasOne(x => x.Membership)
                .WithMany(x => x.TierSnapshots)
                .HasForeignKey(x => x.MembershipId);
        });

        modelBuilder.Entity<MembershipInvitation>(entity =>
        {
            entity.HasIndex(x => x.NormalizedGitHubLogin).IsUnique();
            entity.Property(x => x.GitHubLogin).HasMaxLength(120);
            entity.Property(x => x.NormalizedGitHubLogin).HasMaxLength(120);
            entity.HasOne(x => x.IssuedByUserAccount)
                .WithMany()
                .HasForeignKey(x => x.IssuedByUserAccountId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(x => x.AcceptedByUserAccount)
                .WithMany()
                .HasForeignKey(x => x.AcceptedByUserAccountId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasIndex(x => x.Slug).IsUnique();
            entity.HasIndex(x => x.GitHubRepository);
            entity.Property(x => x.Name).HasMaxLength(180);
            entity.Property(x => x.Slug).HasMaxLength(120);
            entity.Property(x => x.GitHubRepository).HasMaxLength(240);
            entity.Property(x => x.Status).HasConversion<string>();
            entity.HasOne(x => x.OwnerUserAccount)
                .WithMany(x => x.OwnedProjects)
                .HasForeignKey(x => x.OwnerUserAccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorkItem>(entity =>
        {
            entity.Property(x => x.SourceType).HasConversion<string>();
            entity.Property(x => x.Status).HasConversion<string>();
            entity.Property(x => x.ReviewStatus).HasConversion<string>();
            entity.Property(x => x.SkillLevel).HasConversion<string>();
            entity.Property(x => x.Title).HasMaxLength(180);
            entity.Property(x => x.Category).HasMaxLength(120);
            entity.Property(x => x.ExternalSourceId).HasMaxLength(240);
            entity.Property(x => x.EstimatedHours).HasPrecision(18, 2);
            entity.Property(x => x.ContributionPoints).HasPrecision(18, 2);
            entity.HasOne(x => x.Project)
                .WithMany(x => x.WorkItems)
                .HasForeignKey(x => x.ProjectId);
            entity.HasOne(x => x.RequestedByUserAccount)
                .WithMany(x => x.RequestedWorkItems)
                .HasForeignKey(x => x.RequestedByUserAccountId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.AssignedToUserAccount)
                .WithMany(x => x.AssignedWorkItems)
                .HasForeignKey(x => x.AssignedToUserAccountId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<VolunteerClaim>(entity =>
        {
            entity.HasIndex(x => new { x.WorkItemId, x.UserAccountId }).IsUnique();
            entity.Property(x => x.Status).HasConversion<string>();
            entity.HasOne(x => x.WorkItem)
                .WithMany(x => x.VolunteerClaims)
                .HasForeignKey(x => x.WorkItemId);
            entity.HasOne(x => x.UserAccount)
                .WithMany(x => x.VolunteerClaims)
                .HasForeignKey(x => x.UserAccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Assignment>(entity =>
        {
            entity.HasOne(x => x.WorkItem)
                .WithMany(x => x.Assignments)
                .HasForeignKey(x => x.WorkItemId);
            entity.HasOne(x => x.UserAccount)
                .WithMany(x => x.Assignments)
                .HasForeignKey(x => x.UserAccountId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.AssignedByUserAccount)
                .WithMany()
                .HasForeignKey(x => x.AssignedByUserAccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorkLog>(entity =>
        {
            entity.Property(x => x.Hours).HasPrecision(18, 2);
            entity.Property(x => x.ApprovalStatus).HasConversion<string>();
            entity.HasOne(x => x.WorkItem)
                .WithMany(x => x.WorkLogs)
                .HasForeignKey(x => x.WorkItemId);
            entity.HasOne(x => x.UserAccount)
                .WithMany(x => x.WorkLogs)
                .HasForeignKey(x => x.UserAccountId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.ReviewedByUserAccount)
                .WithMany()
                .HasForeignKey(x => x.ReviewedByUserAccountId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<WorkReview>(entity =>
        {
            entity.Property(x => x.Status).HasConversion<string>();
            entity.Property(x => x.ReviewerName).HasMaxLength(200);
            entity.HasOne(x => x.WorkItem)
                .WithMany(x => x.WorkReviews)
                .HasForeignKey(x => x.WorkItemId);
            entity.HasOne(x => x.ReviewerUserAccount)
                .WithMany(x => x.WorkReviews)
                .HasForeignKey(x => x.ReviewerUserAccountId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<GitHubIssueLink>(entity =>
        {
            entity.HasIndex(x => x.WorkItemId).IsUnique();
            entity.HasIndex(x => new { x.RepositoryFullName, x.IssueNumber }).IsUnique();
            entity.Property(x => x.RepositoryFullName).HasMaxLength(240);
            entity.Property(x => x.State).HasConversion<string>();
            entity.Property(x => x.IssueUrl).HasMaxLength(500);
            entity.Property(x => x.TitleSnapshot).HasMaxLength(240);
            entity.HasOne(x => x.WorkItem)
                .WithOne(x => x.GitHubIssueLink)
                .HasForeignKey<GitHubIssueLink>(x => x.WorkItemId);
        });

        modelBuilder.Entity<GitHubPullRequestLink>(entity =>
        {
            entity.HasIndex(x => new { x.RepositoryFullName, x.PullRequestNumber }).IsUnique();
            entity.Property(x => x.RepositoryFullName).HasMaxLength(240);
            entity.Property(x => x.State).HasConversion<string>();
            entity.Property(x => x.ReviewDecision).HasConversion<string>();
            entity.Property(x => x.PullRequestUrl).HasMaxLength(500);
            entity.Property(x => x.TitleSnapshot).HasMaxLength(240);
            entity.HasOne(x => x.WorkItem)
                .WithMany(x => x.GitHubPullRequestLinks)
                .HasForeignKey(x => x.WorkItemId);
        });

        modelBuilder.Entity<Motion>(entity =>
        {
            entity.Property(x => x.Scope).HasConversion<string>();
            entity.Property(x => x.Category).HasConversion<string>();
            entity.Property(x => x.Status).HasConversion<string>();
            entity.Property(x => x.ApprovalThreshold).HasPrecision(6, 3);
            entity.HasOne(x => x.Project)
                .WithMany(x => x.Motions)
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(x => x.CreatedByUserAccount)
                .WithMany(x => x.CreatedMotions)
                .HasForeignKey(x => x.CreatedByUserAccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Vote>(entity =>
        {
            entity.HasIndex(x => new { x.MotionId, x.UserAccountId }).IsUnique();
            entity.Property(x => x.Choice).HasConversion<string>();
            entity.Property(x => x.Weight).HasPrecision(18, 2);
            entity.HasOne(x => x.Motion)
                .WithMany(x => x.Votes)
                .HasForeignKey(x => x.MotionId);
            entity.HasOne(x => x.UserAccount)
                .WithMany(x => x.Votes)
                .HasForeignKey(x => x.UserAccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LedgerEntry>(entity =>
        {
            entity.Property(x => x.EntryType).HasConversion<string>();
            entity.Property(x => x.Status).HasConversion<string>();
            entity.Property(x => x.Points).HasPrecision(18, 2);
            entity.Property(x => x.NominalAmount).HasPrecision(18, 2);
            entity.HasOne(x => x.UserAccount)
                .WithMany(x => x.LedgerEntries)
                .HasForeignKey(x => x.UserAccountId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.CreatedByUserAccount)
                .WithMany()
                .HasForeignKey(x => x.CreatedByUserAccountId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Project)
                .WithMany(x => x.LedgerEntries)
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(x => x.WorkItem)
                .WithMany(x => x.LedgerEntries)
                .HasForeignKey(x => x.WorkItemId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<PayoutProposalBatch>(entity =>
        {
            entity.Property(x => x.Status).HasConversion<string>();
            entity.HasOne(x => x.CreatedByUserAccount)
                .WithMany()
                .HasForeignKey(x => x.CreatedByUserAccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PointTransaction>(entity =>
        {
            entity.Property(x => x.Type).HasConversion<string>();
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.HasOne(x => x.UserAccount)
                .WithMany(x => x.PointTransactions)
                .HasForeignKey(x => x.UserAccountId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Project)
                .WithMany(x => x.PointTransactions)
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(x => x.WorkItem)
                .WithMany(x => x.PointTransactions)
                .HasForeignKey(x => x.WorkItemId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<PatronSupportEvent>(entity =>
        {
            entity.Property(x => x.Kind).HasConversion<string>();
            entity.Property(x => x.Provider).HasConversion<string>();
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.Property(x => x.ExternalSupportId).HasMaxLength(240);
            entity.Property(x => x.ProviderEventId).HasMaxLength(240);
            entity.Property(x => x.ProviderPayerId).HasMaxLength(240);
            entity.Property(x => x.ProviderSubscriptionId).HasMaxLength(240);
            entity.Property(x => x.CurrencyCode).HasMaxLength(12);
            entity.HasIndex(x => new { x.UserAccountId, x.Kind, x.IsCurrentRecurringSupport });
            entity.HasIndex(x => new { x.Provider, x.ProviderEventId })
                .IsUnique()
                .HasFilter("\"ProviderEventId\" <> ''");
            entity.HasOne(x => x.UserAccount)
                .WithMany(x => x.PatronSupportEvents)
                .HasForeignKey(x => x.UserAccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DecayRun>(entity =>
        {
            entity.Property(x => x.Rate).HasPrecision(8, 4);
        });

        modelBuilder.Entity<RevenueEvent>(entity =>
        {
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.Property(x => x.CurrencyCode).HasMaxLength(12);
            entity.HasOne(x => x.Project)
                .WithMany(x => x.RevenueEvents)
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<RevenueShareBatch>(entity =>
        {
            entity.HasOne(x => x.RevenueEvent)
                .WithMany(x => x.RevenueShareBatches)
                .HasForeignKey(x => x.RevenueEventId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(x => x.CreatedByUserAccount)
                .WithMany()
                .HasForeignKey(x => x.CreatedByUserAccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RevenueShareLine>(entity =>
        {
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.Property(x => x.Basis).HasMaxLength(240);
            entity.HasOne(x => x.RevenueShareBatch)
                .WithMany(x => x.Lines)
                .HasForeignKey(x => x.RevenueShareBatchId);
            entity.HasOne(x => x.UserAccount)
                .WithMany()
                .HasForeignKey(x => x.UserAccountId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(x => x.Project)
                .WithMany(x => x.RevenueShareLines)
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<BridgeAction>(entity =>
        {
            entity.Property(x => x.ActorKind).HasConversion<string>();
            entity.Property(x => x.TargetSurface).HasConversion<string>();
            entity.Property(x => x.ActionKind).HasConversion<string>();
            entity.Property(x => x.Status).HasConversion<string>();
            entity.Property(x => x.ActorName).HasMaxLength(160);
            entity.Property(x => x.TargetRepositoryFullName).HasMaxLength(240);
            entity.Property(x => x.TargetLocator).HasMaxLength(500);
            entity.Property(x => x.SourceKind).HasMaxLength(120);
            entity.Property(x => x.SourceId).HasMaxLength(240);
            entity.Property(x => x.AuthorityReference).HasMaxLength(240);
            entity.Property(x => x.PolicyDecision).HasMaxLength(500);
            entity.Property(x => x.Title).HasMaxLength(240);
            entity.Property(x => x.ReceiptUrl).HasMaxLength(500);
            entity.Property(x => x.ExternalReceiptId).HasMaxLength(240);
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => new { x.TargetSurface, x.ActionKind, x.Status });
            entity.HasIndex(x => new { x.ActorKind, x.ActorName });
            entity.HasIndex(x => new { x.SourceKind, x.SourceId });
            entity.HasOne(x => x.ActorUserAccount)
                .WithMany(x => x.BridgeActions)
                .HasForeignKey(x => x.ActorUserAccountId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(x => x.WorkItem)
                .WithMany()
                .HasForeignKey(x => x.WorkItemId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(x => x.Motion)
                .WithMany()
                .HasForeignKey(x => x.MotionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AgentDispatchRun>(entity =>
        {
            entity.Property(x => x.RequestId).HasMaxLength(240);
            entity.Property(x => x.TargetRepoName).HasMaxLength(120);
            entity.Property(x => x.TargetRepositoryFullName).HasMaxLength(240);
            entity.Property(x => x.TargetAgentIdentity).HasMaxLength(160);
            entity.Property(x => x.LaunchMode).HasMaxLength(40);
            entity.Property(x => x.Status).HasConversion<string>();
            entity.Property(x => x.ThreadId).HasMaxLength(120);
            entity.Property(x => x.TurnId).HasMaxLength(120);
            entity.Property(x => x.LogPath).HasMaxLength(500);
            entity.Property(x => x.ResultPath).HasMaxLength(500);
            entity.HasIndex(x => x.RequestId);
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => new { x.TargetRepoName, x.TargetAgentIdentity, x.Status });
            entity.HasOne(x => x.StartedByUserAccount)
                .WithMany(x => x.AgentDispatchRuns)
                .HasForeignKey(x => x.StartedByUserAccountId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AgentTransportReceipt>(entity =>
        {
            entity.Property(x => x.RequestId).HasMaxLength(240);
            entity.Property(x => x.Title).HasMaxLength(240);
            entity.Property(x => x.TargetRepoName).HasMaxLength(120);
            entity.Property(x => x.TargetRepositoryFullName).HasMaxLength(240);
            entity.Property(x => x.TargetAgentIdentity).HasMaxLength(160);
            entity.Property(x => x.ActivityKind).HasConversion<string>();
            entity.Property(x => x.Status).HasMaxLength(40);
            entity.Property(x => x.ActorName).HasMaxLength(160);
            entity.HasIndex(x => x.RequestId);
            entity.HasIndex(x => x.OccurredAtUtc);
            entity.HasIndex(x => new { x.TargetRepoName, x.TargetAgentIdentity, x.ActivityKind });
            entity.HasOne(x => x.ActorUserAccount)
                .WithMany(x => x.AgentTransportReceipts)
                .HasForeignKey(x => x.ActorUserAccountId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AuditEvent>(entity =>
        {
            entity.Property(x => x.EntityType).HasMaxLength(120);
            entity.Property(x => x.EntityId).HasMaxLength(120);
            entity.Property(x => x.Action).HasMaxLength(120);
            entity.HasOne(x => x.ActorUserAccount)
                .WithMany(x => x.AuditEvents)
                .HasForeignKey(x => x.ActorUserAccountId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
