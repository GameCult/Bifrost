using Bifrost.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Web.Data;

public sealed class BifrostDbContext(DbContextOptions<BifrostDbContext> options) : DbContext(options)
{
    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();

    public DbSet<MemberProfile> MemberProfiles => Set<MemberProfile>();

    public DbSet<Membership> Memberships => Set<Membership>();

    public DbSet<MembershipInvitation> MembershipInvitations => Set<MembershipInvitation>();

    public DbSet<Project> Projects => Set<Project>();

    public DbSet<WorkItem> WorkItems => Set<WorkItem>();

    public DbSet<VolunteerClaim> VolunteerClaims => Set<VolunteerClaim>();

    public DbSet<Assignment> Assignments => Set<Assignment>();

    public DbSet<Motion> Motions => Set<Motion>();

    public DbSet<Vote> Votes => Set<Vote>();

    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    public DbSet<PayoutProposalBatch> PayoutProposalBatches => Set<PayoutProposalBatch>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserAccount>(entity =>
        {
            entity.HasIndex(x => x.GitHubUserId).IsUnique();
            entity.HasIndex(x => x.NormalizedGitHubLogin).IsUnique();
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
        });

        modelBuilder.Entity<Membership>(entity =>
        {
            entity.HasOne(x => x.UserAccount)
                .WithOne(x => x.Membership)
                .HasForeignKey<Membership>(x => x.UserAccountId);

            entity.Property(x => x.Status).HasConversion<string>();
            entity.Property(x => x.PatronWeight).HasPrecision(18, 2);
            entity.Property(x => x.ContributorWeight).HasPrecision(18, 2);
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
            entity.Property(x => x.Name).HasMaxLength(180);
            entity.Property(x => x.Slug).HasMaxLength(120);
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
            entity.Property(x => x.Title).HasMaxLength(180);
            entity.Property(x => x.ExternalSourceId).HasMaxLength(120);
            entity.Property(x => x.EffortPoints).HasPrecision(18, 2);
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

        modelBuilder.Entity<Motion>(entity =>
        {
            entity.Property(x => x.Scope).HasConversion<string>();
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
