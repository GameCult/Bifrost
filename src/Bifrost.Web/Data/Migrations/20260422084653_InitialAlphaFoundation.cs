using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialAlphaFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DecayRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Rate = table.Column<decimal>(type: "numeric(8,4)", precision: 8, scale: 4, nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: false),
                    RanAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DecayRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GitHubUserId = table.Column<long>(type: "bigint", nullable: false),
                    GitHubLogin = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    NormalizedGitHubLogin = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AvatarUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorUserAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    EntityType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    EntityId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Action = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Detail = table.Column<string>(type: "text", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditEvents_UserAccounts_ActorUserAccountId",
                        column: x => x.ActorUserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "MemberProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Nickname = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Headline = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    PortfolioUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Skills = table.Column<string>(type: "text", nullable: false),
                    Availability = table.Column<string>(type: "text", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemberProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MemberProfiles_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MembershipInvitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GitHubLogin = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    NormalizedGitHubLogin = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    IssuedByUserAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    AcceptedByUserAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    IssuedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AcceptedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MembershipInvitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MembershipInvitations_UserAccounts_AcceptedByUserAccountId",
                        column: x => x.AcceptedByUserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MembershipInvitations_UserAccounts_IssuedByUserAccountId",
                        column: x => x.IssuedByUserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Memberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    IsPlatformAdmin = table.Column<bool>(type: "boolean", nullable: false),
                    CanManageProjects = table.Column<bool>(type: "boolean", nullable: false),
                    CanManageLedger = table.Column<bool>(type: "boolean", nullable: false),
                    CanModerateMotions = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ApprovedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Memberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Memberships_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PatronSupportEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalSupportId = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    SupportedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatronSupportEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatronSupportEvents_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PayoutProposalBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayoutProposalBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PayoutProposalBatches_UserAccounts_CreatedByUserAccountId",
                        column: x => x.CreatedByUserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Name = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    GitHubRepository = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Projects_UserAccounts_OwnerUserAccountId",
                        column: x => x.OwnerUserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RoleAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    AssignedByUserAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RoleAssignments_Memberships_MembershipId",
                        column: x => x.MembershipId,
                        principalTable: "Memberships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RoleAssignments_UserAccounts_AssignedByUserAccountId",
                        column: x => x.AssignedByUserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "TierSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Weight = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                    EffectiveFromUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CapturedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TierSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TierSnapshots_Memberships_MembershipId",
                        column: x => x.MembershipId,
                        principalTable: "Memberships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Motions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByUserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Scope = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    ApprovalThreshold = table.Column<decimal>(type: "numeric(6,3)", precision: 6, scale: 3, nullable: false),
                    OpensAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosesAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResolutionNote = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Motions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Motions_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Motions_UserAccounts_CreatedByUserAccountId",
                        column: x => x.CreatedByUserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RevenueEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: false),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RevenueEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RevenueEvents_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "WorkItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByUserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedToUserAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceType = table.Column<string>(type: "text", nullable: false),
                    ExternalSourceId = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    Title = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SkillLevel = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ReviewStatus = table.Column<string>(type: "text", nullable: false),
                    EstimatedHours = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ContributionPoints = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TargetDateUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SubmittedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkItems_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkItems_UserAccounts_AssignedToUserAccountId",
                        column: x => x.AssignedToUserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WorkItems_UserAccounts_RequestedByUserAccountId",
                        column: x => x.RequestedByUserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Votes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MotionId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Choice = table.Column<string>(type: "text", nullable: false),
                    Weight = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Comment = table.Column<string>(type: "text", nullable: false),
                    CastAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Votes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Votes_Motions_MotionId",
                        column: x => x.MotionId,
                        principalTable: "Motions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Votes_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RevenueShareBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RevenueEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByUserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RevenueShareBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RevenueShareBatches_RevenueEvents_RevenueEventId",
                        column: x => x.RevenueEventId,
                        principalTable: "RevenueEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RevenueShareBatches_UserAccounts_CreatedByUserAccountId",
                        column: x => x.CreatedByUserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedByUserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Note = table.Column<string>(type: "text", nullable: false),
                    AssignedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Assignments_UserAccounts_AssignedByUserAccountId",
                        column: x => x.AssignedByUserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Assignments_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Assignments_WorkItems_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GitHubIssueLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryFullName = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    IssueNumber = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    IssueUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    TitleSnapshot = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    LastSynchronizedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitHubIssueLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GitHubIssueLinks_WorkItems_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GitHubPullRequestLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryFullName = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    PullRequestNumber = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    IsMerged = table.Column<bool>(type: "boolean", nullable: false),
                    PullRequestUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    TitleSnapshot = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    ReviewDecision = table.Column<string>(type: "text", nullable: false),
                    LastSynchronizedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitHubPullRequestLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GitHubPullRequestLinks_WorkItems_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LedgerEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByUserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryType = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Points = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    NominalAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Note = table.Column<string>(type: "text", nullable: false),
                    EffectiveAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LedgerEntries_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_LedgerEntries_UserAccounts_CreatedByUserAccountId",
                        column: x => x.CreatedByUserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LedgerEntries_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LedgerEntries_WorkItems_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PointTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    IsDecaying = table.Column<bool>(type: "boolean", nullable: false),
                    Note = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PointTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PointTransactions_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PointTransactions_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PointTransactions_WorkItems_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "VolunteerClaims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Note = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolunteerClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VolunteerClaims_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VolunteerClaims_WorkItems_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Hours = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Note = table.Column<string>(type: "text", nullable: false),
                    ApprovalStatus = table.Column<string>(type: "text", nullable: false),
                    ReviewedByUserAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReviewedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkLogs_UserAccounts_ReviewedByUserAccountId",
                        column: x => x.ReviewedByUserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WorkLogs_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkLogs_WorkItems_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkReviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReviewerUserAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Note = table.Column<string>(type: "text", nullable: false),
                    ReviewedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkReviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkReviews_UserAccounts_ReviewerUserAccountId",
                        column: x => x.ReviewerUserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WorkReviews_WorkItems_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RevenueShareLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RevenueShareBatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Basis = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RevenueShareLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RevenueShareLines_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RevenueShareLines_RevenueShareBatches_RevenueShareBatchId",
                        column: x => x.RevenueShareBatchId,
                        principalTable: "RevenueShareBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RevenueShareLines_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_AssignedByUserAccountId",
                table: "Assignments",
                column: "AssignedByUserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_UserAccountId",
                table: "Assignments",
                column: "UserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_WorkItemId",
                table: "Assignments",
                column: "WorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_ActorUserAccountId",
                table: "AuditEvents",
                column: "ActorUserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_GitHubIssueLinks_RepositoryFullName_IssueNumber",
                table: "GitHubIssueLinks",
                columns: new[] { "RepositoryFullName", "IssueNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GitHubIssueLinks_WorkItemId",
                table: "GitHubIssueLinks",
                column: "WorkItemId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GitHubPullRequestLinks_RepositoryFullName_PullRequestNumber",
                table: "GitHubPullRequestLinks",
                columns: new[] { "RepositoryFullName", "PullRequestNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GitHubPullRequestLinks_WorkItemId",
                table: "GitHubPullRequestLinks",
                column: "WorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_CreatedByUserAccountId",
                table: "LedgerEntries",
                column: "CreatedByUserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_ProjectId",
                table: "LedgerEntries",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_UserAccountId",
                table: "LedgerEntries",
                column: "UserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_WorkItemId",
                table: "LedgerEntries",
                column: "WorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_MemberProfiles_UserAccountId",
                table: "MemberProfiles",
                column: "UserAccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MembershipInvitations_AcceptedByUserAccountId",
                table: "MembershipInvitations",
                column: "AcceptedByUserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_MembershipInvitations_IssuedByUserAccountId",
                table: "MembershipInvitations",
                column: "IssuedByUserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_MembershipInvitations_NormalizedGitHubLogin",
                table: "MembershipInvitations",
                column: "NormalizedGitHubLogin",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Memberships_UserAccountId",
                table: "Memberships",
                column: "UserAccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Motions_CreatedByUserAccountId",
                table: "Motions",
                column: "CreatedByUserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Motions_ProjectId",
                table: "Motions",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_PatronSupportEvents_UserAccountId",
                table: "PatronSupportEvents",
                column: "UserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PayoutProposalBatches_CreatedByUserAccountId",
                table: "PayoutProposalBatches",
                column: "CreatedByUserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PointTransactions_ProjectId",
                table: "PointTransactions",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_PointTransactions_UserAccountId",
                table: "PointTransactions",
                column: "UserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PointTransactions_WorkItemId",
                table: "PointTransactions",
                column: "WorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_GitHubRepository",
                table: "Projects",
                column: "GitHubRepository");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_OwnerUserAccountId",
                table: "Projects",
                column: "OwnerUserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_Slug",
                table: "Projects",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RevenueEvents_ProjectId",
                table: "RevenueEvents",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_RevenueShareBatches_CreatedByUserAccountId",
                table: "RevenueShareBatches",
                column: "CreatedByUserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_RevenueShareBatches_RevenueEventId",
                table: "RevenueShareBatches",
                column: "RevenueEventId");

            migrationBuilder.CreateIndex(
                name: "IX_RevenueShareLines_ProjectId",
                table: "RevenueShareLines",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_RevenueShareLines_RevenueShareBatchId",
                table: "RevenueShareLines",
                column: "RevenueShareBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_RevenueShareLines_UserAccountId",
                table: "RevenueShareLines",
                column: "UserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_AssignedByUserAccountId",
                table: "RoleAssignments",
                column: "AssignedByUserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_MembershipId_Role",
                table: "RoleAssignments",
                columns: new[] { "MembershipId", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TierSnapshots_MembershipId_Kind_IsCurrent",
                table: "TierSnapshots",
                columns: new[] { "MembershipId", "Kind", "IsCurrent" });

            migrationBuilder.CreateIndex(
                name: "IX_UserAccounts_GitHubUserId",
                table: "UserAccounts",
                column: "GitHubUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserAccounts_NormalizedGitHubLogin",
                table: "UserAccounts",
                column: "NormalizedGitHubLogin",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerClaims_UserAccountId",
                table: "VolunteerClaims",
                column: "UserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerClaims_WorkItemId_UserAccountId",
                table: "VolunteerClaims",
                columns: new[] { "WorkItemId", "UserAccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Votes_MotionId_UserAccountId",
                table: "Votes",
                columns: new[] { "MotionId", "UserAccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Votes_UserAccountId",
                table: "Votes",
                column: "UserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_AssignedToUserAccountId",
                table: "WorkItems",
                column: "AssignedToUserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_ProjectId",
                table: "WorkItems",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_RequestedByUserAccountId",
                table: "WorkItems",
                column: "RequestedByUserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkLogs_ReviewedByUserAccountId",
                table: "WorkLogs",
                column: "ReviewedByUserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkLogs_UserAccountId",
                table: "WorkLogs",
                column: "UserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkLogs_WorkItemId",
                table: "WorkLogs",
                column: "WorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkReviews_ReviewerUserAccountId",
                table: "WorkReviews",
                column: "ReviewerUserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkReviews_WorkItemId",
                table: "WorkReviews",
                column: "WorkItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Assignments");

            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "DecayRuns");

            migrationBuilder.DropTable(
                name: "GitHubIssueLinks");

            migrationBuilder.DropTable(
                name: "GitHubPullRequestLinks");

            migrationBuilder.DropTable(
                name: "LedgerEntries");

            migrationBuilder.DropTable(
                name: "MemberProfiles");

            migrationBuilder.DropTable(
                name: "MembershipInvitations");

            migrationBuilder.DropTable(
                name: "PatronSupportEvents");

            migrationBuilder.DropTable(
                name: "PayoutProposalBatches");

            migrationBuilder.DropTable(
                name: "PointTransactions");

            migrationBuilder.DropTable(
                name: "RevenueShareLines");

            migrationBuilder.DropTable(
                name: "RoleAssignments");

            migrationBuilder.DropTable(
                name: "TierSnapshots");

            migrationBuilder.DropTable(
                name: "VolunteerClaims");

            migrationBuilder.DropTable(
                name: "Votes");

            migrationBuilder.DropTable(
                name: "WorkLogs");

            migrationBuilder.DropTable(
                name: "WorkReviews");

            migrationBuilder.DropTable(
                name: "RevenueShareBatches");

            migrationBuilder.DropTable(
                name: "Memberships");

            migrationBuilder.DropTable(
                name: "Motions");

            migrationBuilder.DropTable(
                name: "WorkItems");

            migrationBuilder.DropTable(
                name: "RevenueEvents");

            migrationBuilder.DropTable(
                name: "Projects");

            migrationBuilder.DropTable(
                name: "UserAccounts");
        }
    }
}
