using Bifrost.Web.Configuration;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.ConceptVotes;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Features.Motions;
using Bifrost.Web.Features.Patronage;
using Bifrost.Web.Features.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Tests;

/// <summary>
/// The merge ledger from its consumers' side: the motion state and close a member sees, the concept tallies a visitor sees, the actor
/// a sign-in resolves to, the patron weight, and the undo. Each operation opens its own context, as a request scope would.
/// </summary>
public sealed class AccountMergeLedgerTests
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    public sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>The merge fixtures. In memory by default; <see cref="OnPostgres"/> points the same fixtures at a real database, where unique indexes, transactions, tokens and triggers exist.</summary>
    public sealed class World
    {
        private readonly string _name = $"merge-{Guid.NewGuid():N}";
        private readonly SaveChangesInterceptor[] _interceptors;
        private readonly string? _postgres;

        public World(params SaveChangesInterceptor[] interceptors) => _interceptors = interceptors;

        private World(string postgres, SaveChangesInterceptor[] interceptors) : this(interceptors) => _postgres = postgres;

        public static World OnPostgres(string connectionString, params SaveChangesInterceptor[] interceptors) => new(connectionString, interceptors);

        public Clock Clock { get; } = new(T0);

        public BifrostDbContext Db()
        {
            var options = new DbContextOptionsBuilder<BifrostDbContext>().AddInterceptors(_interceptors);
            if (_postgres is null)
            {
                options.UseInMemoryDatabase(_name).ConfigureWarnings(x => x.Ignore(InMemoryEventId.TransactionIgnoredWarning));
            }
            else
            {
                options.UseNpgsql(_postgres);
            }

            return new BifrostDbContext(options.Options);
        }

        public AccountMergeLedger Ledger(BifrostDbContext db) => new(db, new PatronageService(db, new AuditTrailService(db, Clock), Clock), Clock);

        public MotionGovernanceService Motions(BifrostDbContext db) => new(db, new AuditTrailService(db, Clock), Clock);

        public ConceptAppraisalService Concepts(BifrostDbContext db) =>
            new(db, new AuditTrailService(db, Clock), Clock, Options.Create(new ConceptVoteOptions()));

        public async Task<UserAccount> AccountAsync(string name, MembershipStatus status = MembershipStatus.Active, bool heimdall = true, bool moderator = false, params MemberRole[] roles)
        {
            await using var db = Db();
            var account = new UserAccount
            {
                DisplayName = name, BifrostIdentity = name, NormalizedBifrostIdentity = BifrostIdentityService.NormalizeIdentity(name),
                HeimdallAccountId = heimdall ? $"hd-{name}-{Guid.NewGuid():N}" : string.Empty,
                CreatedAtUtc = T0, LastSeenAtUtc = T0
            };
            account.Membership = new Membership { UserAccountId = account.Id, Status = status, CanModerateMotions = moderator };
            foreach (var role in roles)
            {
                account.Membership.RoleAssignments.Add(new RoleAssignment { Role = role });
            }

            db.UserAccounts.Add(account);
            await db.SaveChangesAsync();
            return account;
        }

        public async Task<Motion> MotionAsync(string title = "m", MotionStatus status = MotionStatus.Open, MotionKind kind = MotionKind.Standard)
        {
            await using var db = Db();
            var author = await AccountAsync("author-" + Guid.NewGuid().ToString("N")[..6]);
            var motion = new Motion
            {
                CreatedByUserAccountId = author.Id, Title = title, Status = status, Kind = kind, OpensAtUtc = T0,
                ClosesAtUtc = T0.AddDays(3), CreatedAtUtc = T0, ApprovalThreshold = 0.5m
            };
            db.Motions.Add(motion);
            await db.SaveChangesAsync();
            return motion;
        }

        public async Task VoteAsync(Motion motion, UserAccount who, VoteChoice choice, decimal weight = 1m, DateTimeOffset? at = null)
        {
            await using var db = Db();
            db.Votes.Add(new Vote { MotionId = motion.Id, UserAccountId = who.Id, Choice = choice, Weight = weight, CastAtUtc = at ?? T0.AddHours(1) });
            await db.SaveChangesAsync();
        }

        public async Task<AccountMerge> MergeAsync(UserAccount survivor, UserAccount folded, AccountMergeState state = AccountMergeState.HeimdallMoved)
        {
            await using var db = Db();
            var merge = new AccountMerge { SurvivorUserAccountId = survivor.Id, FoldedUserAccountId = folded.Id, State = state, CreatedUtc = T0 };
            db.AccountMerges.Add(merge);
            await db.SaveChangesAsync();
            return merge;
        }

        public async Task<AccountMerge> CommitAsync(AccountMerge merge)
        {
            await using var db = Db();
            return await Ledger(db).CommitAsync(merge.Id, default);
        }

        public async Task SetStateAsync(AccountMerge merge, AccountMergeState state)
        {
            await using var db = Db();
            (await db.AccountMerges.SingleAsync(x => x.Id == merge.Id)).State = state;
            await db.SaveChangesAsync();
        }

        public async Task<MotionListItem> StateOfAsync(Motion motion, UserAccount viewer)
        {
            await using var db = Db();
            var actorAccount = await db.UserAccounts.Include(x => x.Membership!).ThenInclude(x => x.TierSnapshots).Include(x => x.Membership!).ThenInclude(x => x.RoleAssignments)
                .SingleAsync(x => x.Id == viewer.Id);
            var state = await Motions(db).GetStateAsync(new CurrentBifrostActor(true, actorAccount), default);
            return state.Motions.Single(x => x.Id == motion.Id);
        }

        public async Task<CurrentBifrostActor> ActorAsync(Guid accountId)
        {
            await using var db = Db();
            var account = await db.UserAccounts.Include(x => x.Membership!).ThenInclude(x => x.TierSnapshots).Include(x => x.Membership!).ThenInclude(x => x.RoleAssignments)
                .SingleAsync(x => x.Id == accountId);
            return new CurrentBifrostActor(true, account);
        }

        public async Task<MotionStatus> CloseAsync(Motion motion, UserAccount moderator)
        {
            await using var db = Db();
            await Motions(db).CloseMotionAsync(await ActorAsync(moderator.Id), motion.Id, default);
            await using var check = Db();
            return (await check.Motions.SingleAsync(x => x.Id == motion.Id)).Status;
        }

        public async Task<List<Vote>> AllVotesAsync(Motion motion)
        {
            await using var db = Db();
            return await db.Votes.IgnoreQueryFilters().AsNoTracking().Where(x => x.MotionId == motion.Id).ToListAsync();
        }

        public async Task<List<AccountMergeEntry>> JournalAsync(AccountMerge merge)
        {
            await using var db = Db();
            return await db.AccountMergeEntries.AsNoTracking().Where(x => x.MergeId == merge.Id).OrderBy(x => x.Seq).ToListAsync();
        }

        public async Task<Membership> MembershipOfAsync(UserAccount account)
        {
            await using var db = Db();
            return await db.Memberships.AsNoTracking().Include(x => x.TierSnapshots).Include(x => x.RoleAssignments).SingleAsync(x => x.UserAccountId == account.Id);
        }
    }

    // ---- votes ----

    [Fact]
    public async Task Both_voted_on_an_open_motion_only_the_survivors_weight_counts_in_state_and_close()
    {
        var world = new World();
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded");
        var moderator = await world.AccountAsync("mod", moderator: true);
        var motion = await world.MotionAsync();
        await world.VoteAsync(motion, survivor, VoteChoice.Against, 1m);
        await world.VoteAsync(motion, folded, VoteChoice.For, 3m);
        var before = await world.StateOfAsync(motion, moderator);
        Assert.Equal((3m, 1m), (before.VotesFor, before.VotesAgainst));

        await world.CommitAsync(await world.MergeAsync(survivor, folded));

        var after = await world.StateOfAsync(motion, moderator);
        Assert.Equal((0m, 1m), (after.VotesFor, after.VotesAgainst));
        Assert.Equal(MotionStatus.Failed, await world.CloseAsync(motion, moderator));
        Assert.Equal(2, (await world.AllVotesAsync(motion)).Count); // nothing is deleted
    }

    [Fact]
    public async Task A_vote_only_the_folded_account_cast_moves_and_a_later_survivor_cast_replaces_it_never_doubling()
    {
        var world = new World();
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded");
        var moderator = await world.AccountAsync("mod", moderator: true);
        var motion = await world.MotionAsync();
        await world.VoteAsync(motion, folded, VoteChoice.For, 1m);

        await world.CommitAsync(await world.MergeAsync(survivor, folded));

        Assert.Equal(1m, (await world.StateOfAsync(motion, moderator)).VotesFor);
        await using (var db = world.Db())
        {
            await world.Motions(db).CastVoteAsync(await world.ActorAsync(survivor.Id), motion.Id, VoteChoice.Against, "changed my mind", default);
        }

        var item = await world.StateOfAsync(motion, moderator);
        Assert.Equal((0m, 1m), (item.VotesFor, item.VotesAgainst));
        var row = Assert.Single(await world.AllVotesAsync(motion));
        Assert.Equal(survivor.Id, row.UserAccountId);
        Assert.Null(row.VoidedByMergeId);
    }

    [Fact]
    public async Task A_vote_only_the_folded_account_cast_is_voided_when_the_survivor_is_not_active()
    {
        var world = new World();
        var survivor = await world.AccountAsync("survivor", MembershipStatus.PendingApproval);
        var folded = await world.AccountAsync("folded");
        var moderator = await world.AccountAsync("mod", moderator: true);
        var motion = await world.MotionAsync();
        await world.VoteAsync(motion, folded, VoteChoice.For, 1m);

        var merge = await world.CommitAsync(await world.MergeAsync(survivor, folded));

        Assert.Equal(0m, (await world.StateOfAsync(motion, moderator)).VotesFor);
        var row = Assert.Single(await world.AllVotesAsync(motion));
        Assert.Equal(merge.Id, row.VoidedByMergeId);
        Assert.Equal(folded.Id, row.UserAccountId);
    }

    [Fact]
    public async Task Closed_motions_keep_every_vote_and_tally()
    {
        var world = new World();
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded");
        var moderator = await world.AccountAsync("mod", moderator: true);
        var motion = await world.MotionAsync(status: MotionStatus.Passed);
        await world.VoteAsync(motion, survivor, VoteChoice.For, 1m);
        await world.VoteAsync(motion, folded, VoteChoice.For, 2m);

        await world.CommitAsync(await world.MergeAsync(survivor, folded));

        Assert.Equal(3m, (await world.StateOfAsync(motion, moderator)).VotesFor);
        Assert.All(await world.AllVotesAsync(motion), v => Assert.Null(v.VoidedByMergeId));
    }

    // ---- concept appraisals ----

    [Fact]
    public async Task Appraisals_reduce_to_one_voice_per_person_and_the_tallies_count_the_person_once()
    {
        var world = new World();
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded");
        Guid both, onlyFolded;
        await using (var db = world.Db())
        {
            var s1 = new ConceptSubject { Collection = "c", Key = "both", Title = "Both", RegisteredAtUtc = T0 };
            var s2 = new ConceptSubject { Collection = "c", Key = "only-folded", Title = "Only folded", RegisteredAtUtc = T0 };
            db.ConceptSubjects.AddRange(s1, s2);
            ConceptAppraisal Row(ConceptSubject s, UserAccount who, ConceptStance stance) => new()
            {
                SubjectId = s.Id, UserAccountId = who.Id, VoterKey = "account:" + who.Id.ToString("N"), Stance = stance,
                VoterClass = VoterClass.Member, CastAtUtc = T0
            };
            db.ConceptAppraisals.AddRange(Row(s1, survivor, ConceptStance.Up), Row(s1, folded, ConceptStance.Down), Row(s2, folded, ConceptStance.Down));
            await db.SaveChangesAsync();
            (both, onlyFolded) = (s1.Id, s2.Id);
        }

        await world.CommitAsync(await world.MergeAsync(survivor, folded));

        await using var read = world.Db();
        var tallies = await world.Concepts(read).GetTalliesAsync("c", default);
        Assert.Equal((1, 0), (tallies["both"].Up, tallies["both"].Down));
        Assert.Equal((0, 1), (tallies["only-folded"].Up, tallies["only-folded"].Down));
        var moved = await read.ConceptAppraisals.SingleAsync(x => x.SubjectId == onlyFolded);
        Assert.Equal(survivor.Id, moved.UserAccountId);
        Assert.Equal("account:" + survivor.Id.ToString("N"), moved.VoterKey);
        Assert.Equal(1, await read.ConceptAppraisals.IgnoreQueryFilters().CountAsync(x => x.SubjectId == both && x.VoidedByMergeId != null));
    }

    // ---- membership, standing, patronage ----

    [Fact]
    public async Task The_folded_membership_becomes_Merged_with_weight_zero_and_no_role_or_admin_flag_is_carried()
    {
        var world = new World();
        var survivor = await world.AccountAsync("survivor", roles: MemberRole.StandardMember);
        var folded = await world.AccountAsync("folded", roles: [MemberRole.PlatformAdmin, MemberRole.Producer]);
        await using (var db = world.Db())
        {
            (await db.Memberships.SingleAsync(x => x.UserAccountId == folded.Id)).IsPlatformAdmin = true;
            await db.SaveChangesAsync();
        }

        await world.CommitAsync(await world.MergeAsync(survivor, folded));

        var foldedMembership = await world.MembershipOfAsync(folded);
        Assert.Equal(MembershipStatus.Merged, foldedMembership.Status);
        Assert.Equal(0m, foldedMembership.EffectiveVotingWeight);
        var survivorMembership = await world.MembershipOfAsync(survivor);
        Assert.Equal([MemberRole.StandardMember], survivorMembership.RoleAssignments.Select(x => x.Role));
        Assert.False(survivorMembership.IsPlatformAdmin);
        Assert.Equal(MembershipStatus.Active, survivorMembership.Status);
    }

    [Fact]
    public async Task A_suspended_folded_account_suspends_the_survivor_and_undo_restores_it_only_if_unchanged()
    {
        var world = new World();
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded", MembershipStatus.Suspended);
        var untouched = await world.MergeAsync(survivor, folded);
        await world.CommitAsync(untouched);

        var suspended = await world.MembershipOfAsync(survivor);
        Assert.Equal(MembershipStatus.Suspended, suspended.Status);
        Assert.Equal(AccountMergeLedger.SuspensionReason(untouched.Id), suspended.SuspensionReason);

        await world.SetStateAsync(untouched, AccountMergeState.HeimdallReversed);
        await using (var db = world.Db())
        {
            await world.Ledger(db).UndoLocalAsync(untouched.Id, default);
        }

        Assert.Equal(MembershipStatus.Active, (await world.MembershipOfAsync(survivor)).Status);
        Assert.Equal(MembershipStatus.Suspended, (await world.MembershipOfAsync(folded)).Status);

        // The same again, but a steward re-suspends the survivor for their own reason before the undo: that standing is theirs now.
        var folded2 = await world.AccountAsync("folded2", MembershipStatus.Suspended);
        var second = await world.MergeAsync(survivor, folded2);
        await world.CommitAsync(second);
        await using (var db = world.Db())
        {
            (await db.Memberships.SingleAsync(x => x.UserAccountId == survivor.Id)).SuspensionReason = "steward decision";
            await db.SaveChangesAsync();
        }

        await world.SetStateAsync(second, AccountMergeState.HeimdallReversed);
        await using (var db = world.Db())
        {
            await world.Ledger(db).UndoLocalAsync(second.Id, default);
        }

        Assert.Equal(MembershipStatus.Suspended, (await world.MembershipOfAsync(survivor)).Status);
        Assert.Contains(await world.JournalAsync(second), e => e.Kind == AccountMergeEntryKind.Standing && e.Action == AccountMergeEntryAction.KeptAfterUndo);
    }

    [Fact]
    public async Task Patron_events_follow_the_person_and_the_survivors_tier_derives_once_from_all_events_not_tier_plus_tier()
    {
        var world = new World();
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded");
        await using (var db = world.Db())
        {
            PatronSupportEvent Event(UserAccount who, decimal amount) => new()
            {
                UserAccountId = who.Id, Kind = PatronSupportEventKind.RecurringSupportSnapshot, IsCurrentRecurringSupport = true, Amount = amount,
                ProviderEventId = Guid.NewGuid().ToString("N"), SupportedAtUtc = T0, RecordedAtUtc = T0
            };
            db.PatronSupportEvents.AddRange(Event(survivor, 90m), Event(folded, 1000m));
            var memberships = await db.Memberships.ToListAsync();
            foreach (var (who, weight) in new[] { (survivor, 1m), (folded, 3m) })
            {
                db.TierSnapshots.Add(new TierSnapshot { MembershipId = memberships.Single(m => m.UserAccountId == who.Id).Id, Kind = TierSnapshotKind.Patron, Label = "t", Weight = weight, IsCurrent = true, EffectiveFromUtc = T0, CapturedAtUtc = T0 });
            }

            await db.SaveChangesAsync();
        }

        await world.CommitAsync(await world.MergeAsync(survivor, folded));

        var survivorMembership = await world.MembershipOfAsync(survivor);
        Assert.Equal(3m, survivorMembership.EffectiveVotingWeight); // 1090 points: Gold, not Bronze 1 + Gold 3
        Assert.Single(survivorMembership.TierSnapshots, x => x.Kind == TierSnapshotKind.Patron && x.IsCurrent);
        Assert.DoesNotContain(( await world.MembershipOfAsync(folded)).TierSnapshots, x => x.IsCurrent);
        await using var db2 = world.Db();
        Assert.All(await db2.PatronSupportEvents.ToListAsync(), e => Assert.Equal(survivor.Id, e.UserAccountId));
    }

    // ---- who the actor is ----

    [Fact]
    public async Task A_principal_whose_identity_names_one_row_and_whose_heimdall_id_names_another_resolves_the_first_and_does_not_throw()
    {
        var world = new World();
        var byIdentity = await world.AccountAsync("by-identity");
        var byHeimdall = await world.AccountAsync("by-heimdall");
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
        [
            new System.Security.Claims.Claim(BifrostClaimTypes.BifrostIdentity, byIdentity.BifrostIdentity),
            new System.Security.Claims.Claim(BifrostClaimTypes.HeimdallAccountId, byHeimdall.HeimdallAccountId)
        ], "test"));
        await using var db = world.Db();
        var accessor = new CurrentBifrostActorAccessor(new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = http }, db, world.Ledger(db));

        var actor = await accessor.GetAsync(default);

        Assert.Equal(byIdentity.Id, actor.UserAccount!.Id);
    }

    [Theory]
    [InlineData(AccountMergeState.Confirmed, true)]
    [InlineData(AccountMergeState.HeimdallMoved, true)]
    [InlineData(AccountMergeState.Committed, false)]
    [InlineData(AccountMergeState.Final, false)]
    [InlineData(AccountMergeState.UndoRequested, false)]
    [InlineData(AccountMergeState.HeimdallReversed, false)]
    public async Task A_folded_account_is_frozen_then_folded_and_acts_as_nobody_while_the_survivor_acts_normally(AccountMergeState state, bool frozen)
    {
        var world = new World();
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded", roles: MemberRole.PlatformAdmin);
        var merge = await world.MergeAsync(survivor, folded, state);

        foreach (var (account, isFolded) in new[] { (folded, true), (survivor, false) })
        {
            var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            http.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(BifrostClaimTypes.HeimdallAccountId, account.HeimdallAccountId)], "test"));
            await using var db = world.Db();
            var actor = await new CurrentBifrostActorAccessor(new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = http }, db, world.Ledger(db)).GetAsync(default);

            if (!isFolded)
            {
                Assert.Equal(survivor.Id, actor.UserAccount!.Id);
                Assert.Null(actor.Merge);
                continue;
            }

            Assert.True(actor.IsAuthenticated);
            Assert.Null(actor.UserAccount);
            Assert.False(actor.IsPlatformAdmin);
            Assert.False(actor.IsActiveMember);
            Assert.Equal(merge.Id, actor.Merge!.MergeId);
            Assert.Equal(frozen, actor.Merge is MergeStanding.Frozen);
            Assert.Equal(!frozen, actor.Merge is MergeStanding.Folded f && f.SurvivorId == survivor.Id);
        }
    }

    [Theory]
    [InlineData(AccountMergeState.Proposed)]
    [InlineData(AccountMergeState.Failed)]
    [InlineData(AccountMergeState.Undone)]
    public async Task A_merge_that_never_reached_or_has_left_the_move_leaves_the_folded_account_standing_alone(AccountMergeState state)
    {
        var world = new World();
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded");
        await world.MergeAsync(survivor, folded, state);

        await using var db = world.Db();
        Assert.Null(await world.Ledger(db).StandingAsync(folded.Id, default));
    }

    // ---- plan ----

    [Fact]
    public async Task Plan_writes_nothing_and_says_what_a_commit_would_do()
    {
        var world = new World();
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded");
        var motion = await world.MotionAsync();
        await world.VoteAsync(motion, folded, VoteChoice.For);

        await using (var db = world.Db())
        {
            var plan = await world.Ledger(db).PlanAsync(survivor.Id, folded.Id, default);
            Assert.Empty(plan.Blocks);
            Assert.Contains(plan.Entries, e => e.Kind == AccountMergeEntryKind.Vote && e.Action == AccountMergeEntryAction.Moved);
            Assert.Contains(plan.Entries, e => e.Kind == AccountMergeEntryKind.Membership && e.Action == AccountMergeEntryAction.Closed);
        }

        await using var check = world.Db();
        Assert.Empty(check.AccountMerges);
        Assert.Empty(check.AccountMergeEntries);
        Assert.Equal(MembershipStatus.Active, (await world.MembershipOfAsync(folded)).Status);
        Assert.Equal(folded.Id, Assert.Single(await world.AllVotesAsync(motion)).UserAccountId);
    }

    public static IEnumerable<object[]> BlockCases() =>
        new[] { "same_account", "mind_account", "native_account", "merge_in_flight", "survivor_is_folded", "already_folded", "folded_is_survivor", "sealed_ballot_pending" }
            .Select(x => new object[] { x });

    [Theory]
    [MemberData(nameof(BlockCases))]
    public async Task Plan_reports_each_block_by_code(string code)
    {
        var world = new World();
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded", heimdall: code != "native_account");
        var other = await world.AccountAsync("other");
        var (planSurvivor, planFolded) = (survivor.Id, folded.Id);
        switch (code)
        {
            case "same_account":
                planFolded = survivor.Id;
                break;
            case "mind_account":
                await using (var db = world.Db())
                {
                    db.MindPersonas.Add(new MindPersona { PersonaId = "p", UserAccountId = folded.Id });
                    await db.SaveChangesAsync();
                }

                break;
            case "merge_in_flight":
                await world.MergeAsync(other, folded, AccountMergeState.Confirmed);
                break;
            case "survivor_is_folded":
                await world.MergeAsync(other, survivor, AccountMergeState.Final);
                break;
            case "already_folded":
                await world.MergeAsync(other, folded, AccountMergeState.Committed);
                break;
            case "folded_is_survivor":
                await world.MergeAsync(folded, other, AccountMergeState.Final);
                break;
            case "sealed_ballot_pending":
                var motion = await world.MotionAsync(kind: MotionKind.SealedBallot);
                await using (var db = world.Db())
                {
                    db.SealedBallots.Add(new SealedBallot { MotionId = motion.Id, UserAccountId = folded.Id, Commitment = "c", CommittedAtUtc = T0 });
                    await db.SaveChangesAsync();
                }

                break;
        }

        await using var read = world.Db();
        var plan = await world.Ledger(read).PlanAsync(planSurvivor, planFolded, default);

        Assert.Contains(code, plan.Blocks);
    }

    // ---- commit, undo, finalize ----

    [Fact]
    public async Task Commit_twice_is_a_no_op_the_second_time_and_the_journal_is_unchanged()
    {
        var world = new World();
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded");
        var motion = await world.MotionAsync();
        await world.VoteAsync(motion, folded, VoteChoice.For);
        var merge = await world.MergeAsync(survivor, folded);

        var first = await world.CommitAsync(merge);
        var journal = await world.JournalAsync(merge);
        world.Clock.Now = T0.AddDays(1);
        var second = await world.CommitAsync(merge);

        Assert.Equal(AccountMergeState.Committed, second.State);
        Assert.Equal(first.CommittedUtc, second.CommittedUtc);
        Assert.Equal(first.GraceEndsUtc, T0 + TimeSpan.FromDays(7));
        Assert.Equal(journal.Count, (await world.JournalAsync(merge)).Count);
        Assert.NotEmpty(journal);
    }

    private sealed class FailOnce : SaveChangesInterceptor
    {
        public bool Armed { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                Armed = false;
                throw new DbUpdateException("simulated store failure");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task A_commit_whose_save_fails_applies_nothing_and_a_retry_applies_exactly_once()
    {
        var failure = new FailOnce();
        var world = new World(failure);
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded");
        var motion = await world.MotionAsync();
        await world.VoteAsync(motion, folded, VoteChoice.For);
        var merge = await world.MergeAsync(survivor, folded);

        failure.Armed = true;
        await using (var db = world.Db())
        {
            var ledger = world.Ledger(db);
            await Assert.ThrowsAsync<DbUpdateException>(() => ledger.CommitAsync(merge.Id, default));
            await using var check = world.Db();
            Assert.Equal(AccountMergeState.HeimdallMoved, (await check.AccountMerges.SingleAsync()).State);
            Assert.Empty(check.AccountMergeEntries);
            Assert.Equal(MembershipStatus.Active, (await world.MembershipOfAsync(folded)).Status);

            await ledger.CommitAsync(merge.Id, default); // the same context, retried
        }

        var journal = await world.JournalAsync(merge);
        Assert.Equal(journal.Count, journal.Select(x => x.Seq).Distinct().Count());
        Assert.Equal(1, journal.Count(x => x.Kind == AccountMergeEntryKind.Vote));
        Assert.Equal(AccountMergeState.Committed, (await world.CommitAsync(merge)).State);
    }

    [Theory]
    [InlineData(AccountMergeState.Proposed)]
    [InlineData(AccountMergeState.Confirmed)]
    [InlineData(AccountMergeState.Failed)]
    public async Task Commit_from_any_state_but_HeimdallMoved_throws_and_undo_from_Committed_throws(AccountMergeState state)
    {
        var world = new World();
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded");
        var merge = await world.MergeAsync(survivor, folded, state);

        await using var db = world.Db();
        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Ledger(db).CommitAsync(merge.Id, default));
        await world.SetStateAsync(merge, AccountMergeState.Committed);
        await using var db2 = world.Db();
        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Ledger(db2).UndoLocalAsync(merge.Id, default));
    }

    [Fact]
    public async Task Undo_restores_open_motion_votes_keeps_voided_ones_on_since_closed_motions_and_a_survivor_recast_and_restores_membership_and_patron_events()
    {
        var world = new World();
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded");
        var moderator = await world.AccountAsync("mod", moderator: true);
        var bothOpen = await world.MotionAsync("both-open");
        var bothClosing = await world.MotionAsync("both-closing");
        var movedKept = await world.MotionAsync("moved-recast");
        var movedBack = await world.MotionAsync("moved-back");
        foreach (var motion in new[] { bothOpen, bothClosing })
        {
            await world.VoteAsync(motion, survivor, VoteChoice.For);
            await world.VoteAsync(motion, folded, VoteChoice.Against);
        }

        await world.VoteAsync(movedKept, folded, VoteChoice.For, at: T0.AddHours(2));
        await world.VoteAsync(movedBack, folded, VoteChoice.For, at: T0.AddHours(3));
        await using (var db = world.Db())
        {
            db.PatronSupportEvents.Add(new PatronSupportEvent
            {
                UserAccountId = folded.Id, Kind = PatronSupportEventKind.RecurringSupportSnapshot, IsCurrentRecurringSupport = true, Amount = 100m,
                ProviderEventId = "e1", SupportedAtUtc = T0, RecordedAtUtc = T0
            });
            await db.SaveChangesAsync();
        }

        var merge = await world.CommitAsync(await world.MergeAsync(survivor, folded));
        Assert.Equal(2m, (await world.MembershipOfAsync(survivor)).EffectiveVotingWeight);

        // After the merge: one motion closes, and the survivor re-casts on a moved vote.
        await world.CloseAsync(bothClosing, moderator);
        world.Clock.Now = T0.AddDays(1);
        await using (var db = world.Db())
        {
            await world.Motions(db).CastVoteAsync(await world.ActorAsync(survivor.Id), movedKept.Id, VoteChoice.Against, null, default);
        }

        await world.SetStateAsync(merge, AccountMergeState.HeimdallReversed);
        await using (var db = world.Db())
        {
            Assert.Equal(AccountMergeState.Undone, (await world.Ledger(db).UndoLocalAsync(merge.Id, default)).State);
        }

        var open = (await world.AllVotesAsync(bothOpen)).Single(v => v.UserAccountId == folded.Id);
        Assert.Null(open.VoidedByMergeId);
        var closed = (await world.AllVotesAsync(bothClosing)).Single(v => v.UserAccountId == folded.Id);
        Assert.Equal(merge.Id, closed.VoidedByMergeId);
        var recast = Assert.Single(await world.AllVotesAsync(movedKept));
        Assert.Equal((survivor.Id, VoteChoice.Against), (recast.UserAccountId, recast.Choice));
        Assert.Equal(folded.Id, Assert.Single(await world.AllVotesAsync(movedBack)).UserAccountId);

        Assert.Equal(MembershipStatus.Active, (await world.MembershipOfAsync(folded)).Status);
        Assert.Equal(2m, (await world.MembershipOfAsync(folded)).EffectiveVotingWeight);
        Assert.Equal(1m, (await world.MembershipOfAsync(survivor)).EffectiveVotingWeight);
        await using var read = world.Db();
        Assert.Equal(folded.Id, (await read.PatronSupportEvents.SingleAsync()).UserAccountId);
        var journal = await world.JournalAsync(merge);
        Assert.Contains(journal, e => e.Action == AccountMergeEntryAction.KeptAfterUndo && e.Kind == AccountMergeEntryKind.Vote);
        Assert.Contains(journal, e => e.Action == AccountMergeEntryAction.Restored && e.Kind == AccountMergeEntryKind.Membership);
        Assert.Equal(journal.Count, journal.Select(x => x.Seq).Distinct().Count());
        Assert.Equal(AccountMergeState.Undone, (await world.Ledger(read).UndoLocalAsync(merge.Id, default)).State); // a second undo is a no-op
    }

    [Fact]
    public async Task Finalize_moves_Committed_merges_past_their_grace_to_Final_and_leaves_the_rest()
    {
        var world = new World();
        var survivor = await world.AccountAsync("survivor");
        var due = await world.AccountAsync("due");
        var young = await world.AccountAsync("young");
        var dueMerge = await world.CommitAsync(await world.MergeAsync(survivor, due));
        world.Clock.Now = T0.AddDays(5);
        var youngMerge = await world.CommitAsync(await world.MergeAsync(survivor, young));

        world.Clock.Now = T0.AddDays(7);
        await using var db = world.Db();
        Assert.Equal(1, await world.Ledger(db).FinalizeDueAsync(world.Clock.Now, default));

        await using var read = world.Db();
        var final = await read.AccountMerges.SingleAsync(x => x.Id == dueMerge.Id);
        Assert.Equal((AccountMergeState.Final, T0.AddDays(7)), (final.State, final.FinalizedUtc));
        Assert.Equal(AccountMergeState.Committed, (await read.AccountMerges.SingleAsync(x => x.Id == youngMerge.Id)).State);
    }

    // ---- verdict cut-merge-ledger.s1: the rules each fix decides ----

    [Theory]
    [InlineData(AccountMergeState.Confirmed)]
    [InlineData(AccountMergeState.HeimdallMoved)]
    [InlineData(AccountMergeState.UndoRequested)]
    [InlineData(AccountMergeState.HeimdallReversed)]
    public async Task Plan_blocks_while_either_account_is_in_a_merge_in_flight(AccountMergeState state)
    {
        foreach (var survivorSide in new[] { true, false })
        {
            var world = new World();
            var survivor = await world.AccountAsync("survivor");
            var folded = await world.AccountAsync("folded");
            var other = await world.AccountAsync("other");
            await (survivorSide ? world.MergeAsync(survivor, other, state) : world.MergeAsync(other, folded, state));

            await using var read = world.Db();
            Assert.Contains("merge_in_flight", (await world.Ledger(read).PlanAsync(survivor.Id, folded.Id, default)).Blocks);
        }
    }

    [Theory]
    [InlineData(AccountMergeState.Committed)]
    [InlineData(AccountMergeState.Final)]
    public async Task A_landed_merges_survivor_cannot_itself_fold_so_an_alias_is_never_a_chain(AccountMergeState state)
    {
        var world = new World();
        var a = await world.AccountAsync("acct-a");
        var b = await world.AccountAsync("acct-b");
        var c = await world.AccountAsync("acct-c");
        await world.MergeAsync(b, a, state);

        await using var read = world.Db();
        Assert.Contains("folded_is_survivor", (await world.Ledger(read).PlanAsync(c.Id, b.Id, default)).Blocks);
        Assert.Empty((await world.Ledger(read).PlanAsync(b.Id, c.Id, default)).Blocks); // the other direction is an ordinary merge
        Assert.Equal(b.Id, ((MergeStanding.Folded)(await world.Ledger(read).StandingAsync(a.Id, default))!).SurvivorId);
    }

    private static async Task DropMembershipAsync(World world, UserAccount account)
    {
        await using var db = world.Db();
        db.Memberships.Remove(await db.Memberships.SingleAsync(x => x.UserAccountId == account.Id));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_suspension_is_not_laundered_into_a_survivor_without_a_membership_and_undo_removes_the_row_it_made()
    {
        var world = new World();
        var survivor = await world.AccountAsync("survivor");
        await DropMembershipAsync(world, survivor);
        var folded = await world.AccountAsync("folded", MembershipStatus.Suspended);
        var merge = await world.MergeAsync(survivor, folded);

        await using (var db = world.Db())
        {
            var plan = await world.Ledger(db).PlanAsync(survivor.Id, folded.Id, default);
            Assert.Contains(plan.Entries, e => e is { Kind: AccountMergeEntryKind.Standing, Action: AccountMergeEntryAction.Created });
            Assert.Empty(db.Memberships.Where(x => x.UserAccountId == survivor.Id)); // a plan writes nothing
        }

        await world.CommitAsync(merge);

        var carried = await world.MembershipOfAsync(survivor);
        Assert.Equal(MembershipStatus.Suspended, carried.Status);
        Assert.Equal(AccountMergeLedger.SuspensionReason(merge.Id), carried.SuspensionReason);
        Assert.False((await world.ActorAsync(survivor.Id)).IsActiveMember);

        await world.SetStateAsync(merge, AccountMergeState.HeimdallReversed);
        await using (var db = world.Db())
        {
            await world.Ledger(db).UndoLocalAsync(merge.Id, default);
        }

        await using var read = world.Db();
        Assert.Empty(read.Memberships.Where(x => x.UserAccountId == survivor.Id));
        Assert.Contains(await world.JournalAsync(merge), e => e is { Kind: AccountMergeEntryKind.Standing, Action: AccountMergeEntryAction.Restored });
    }

    [Fact]
    public async Task Undo_keeps_the_membership_a_merge_made_for_a_suspension_once_an_admin_has_taken_it_over()
    {
        var world = new World();
        var survivor = await world.AccountAsync("survivor");
        await DropMembershipAsync(world, survivor);
        var folded = await world.AccountAsync("folded", MembershipStatus.Suspended);
        var merge = await world.CommitAsync(await world.MergeAsync(survivor, folded));
        await using (var db = world.Db())
        {
            (await db.Memberships.SingleAsync(x => x.UserAccountId == survivor.Id)).SuspensionReason = "steward decision";
            await db.SaveChangesAsync();
        }

        await world.SetStateAsync(merge, AccountMergeState.HeimdallReversed);
        await using (var db = world.Db())
        {
            await world.Ledger(db).UndoLocalAsync(merge.Id, default);
        }

        Assert.Equal(MembershipStatus.Suspended, (await world.MembershipOfAsync(survivor)).Status);
        Assert.Contains(await world.JournalAsync(merge), e => e is { Kind: AccountMergeEntryKind.Standing, Action: AccountMergeEntryAction.KeptAfterUndo });
    }

    [Fact]
    public async Task A_moved_vote_carries_the_survivors_weight_not_the_folded_accounts_and_undo_restores_it()
    {
        var world = new World();
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded");
        var moderator = await world.AccountAsync("mod", moderator: true);
        var motion = await world.MotionAsync();
        await world.VoteAsync(motion, folded, VoteChoice.For, 5m);

        var merge = await world.CommitAsync(await world.MergeAsync(survivor, folded));

        var moved = Assert.Single(await world.AllVotesAsync(motion));
        Assert.Equal((survivor.Id, 1m), (moved.UserAccountId, moved.Weight));
        Assert.Equal(1m, (await world.StateOfAsync(motion, moderator)).VotesFor);

        await world.SetStateAsync(merge, AccountMergeState.HeimdallReversed);
        await using (var db = world.Db())
        {
            await world.Ledger(db).UndoLocalAsync(merge.Id, default);
        }

        var back = Assert.Single(await world.AllVotesAsync(motion));
        Assert.Equal((folded.Id, 5m), (back.UserAccountId, back.Weight));
    }

    [Fact]
    public async Task A_moved_vote_takes_the_survivors_weight_after_the_patron_tiers_were_derived_again()
    {
        var world = new World();
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded");
        var motion = await world.MotionAsync();
        await world.VoteAsync(motion, folded, VoteChoice.For, 1m);
        await using (var db = world.Db())
        {
            db.PatronSupportEvents.Add(new PatronSupportEvent
            {
                UserAccountId = folded.Id, Kind = PatronSupportEventKind.RecurringSupportSnapshot, IsCurrentRecurringSupport = true, Amount = 100m,
                ProviderEventId = "e1", SupportedAtUtc = T0, RecordedAtUtc = T0
            });
            await db.SaveChangesAsync();
        }

        await world.CommitAsync(await world.MergeAsync(survivor, folded));

        var weight = (await world.MembershipOfAsync(survivor)).EffectiveVotingWeight;
        Assert.Equal(2m, weight);
        Assert.Equal(weight, Assert.Single(await world.AllVotesAsync(motion)).Weight);
    }

    private static async Task<(Guid Both, Guid OnlyFolded)> SeedAppraisalsAsync(World world, UserAccount survivor, UserAccount folded, VoterClass foldedClass)
    {
        await using var db = world.Db();
        var both = new ConceptSubject { Collection = "c", Key = "both", Title = "Both", RegisteredAtUtc = T0 };
        var only = new ConceptSubject { Collection = "c", Key = "only-folded", Title = "Only folded", RegisteredAtUtc = T0 };
        db.ConceptSubjects.AddRange(both, only);
        ConceptAppraisal Row(ConceptSubject subject, UserAccount who, ConceptStance stance, VoterClass voterClass) => new()
        {
            SubjectId = subject.Id, UserAccountId = who.Id, VoterKey = "account:" + who.Id.ToString("N"), Stance = stance, VoterClass = voterClass, CastAtUtc = T0
        };
        db.ConceptAppraisals.AddRange(Row(both, survivor, ConceptStance.Up, VoterClass.Member), Row(both, folded, ConceptStance.Down, foldedClass), Row(only, folded, ConceptStance.Down, foldedClass));
        await db.SaveChangesAsync();
        return (both.Id, only.Id);
    }

    [Fact]
    public async Task A_lone_folded_appraisal_is_voided_when_the_survivor_is_not_active()
    {
        var world = new World();
        var survivor = await world.AccountAsync("survivor", MembershipStatus.PendingApproval);
        var folded = await world.AccountAsync("folded");
        var (_, onlyFolded) = await SeedAppraisalsAsync(world, survivor, folded, VoterClass.Member);

        var merge = await world.CommitAsync(await world.MergeAsync(survivor, folded));

        await using var read = world.Db();
        var row = await read.ConceptAppraisals.IgnoreQueryFilters().SingleAsync(x => x.SubjectId == onlyFolded);
        Assert.Equal((merge.Id, folded.Id), (row.VoidedByMergeId, row.UserAccountId));
        Assert.Equal(0, (await world.Concepts(read).GetTalliesAsync("c", default))["only-folded"].Down);
    }

    [Fact]
    public async Task A_moved_appraisal_takes_the_member_class_and_undo_gives_back_the_class_and_key_unless_the_survivor_recast_it()
    {
        var world = new World();
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded");
        var (_, onlyFolded) = await SeedAppraisalsAsync(world, survivor, folded, VoterClass.Account);
        var survivorKey = "account:" + survivor.Id.ToString("N");
        var merge = await world.CommitAsync(await world.MergeAsync(survivor, folded));

        await using (var read = world.Db())
        {
            var moved = await read.ConceptAppraisals.SingleAsync(x => x.SubjectId == onlyFolded);
            Assert.Equal((survivor.Id, survivorKey, VoterClass.Member), (moved.UserAccountId, moved.VoterKey, moved.VoterClass));
        }

        await world.SetStateAsync(merge, AccountMergeState.HeimdallReversed);
        await using (var db = world.Db())
        {
            await world.Ledger(db).UndoLocalAsync(merge.Id, default);
        }

        await using (var read = world.Db())
        {
            var back = await read.ConceptAppraisals.SingleAsync(x => x.SubjectId == onlyFolded);
            Assert.Equal((folded.Id, "account:" + folded.Id.ToString("N"), VoterClass.Account), (back.UserAccountId, back.VoterKey, back.VoterClass));
        }

        // Again, but the survivor re-casts the moved appraisal before the undo: it is theirs now and stays.
        var folded2 = await world.AccountAsync("folded2");
        await using (var db = world.Db())
        {
            db.ConceptAppraisals.Add(new ConceptAppraisal
            {
                SubjectId = onlyFolded, UserAccountId = folded2.Id, VoterKey = "account:" + folded2.Id.ToString("N"), Stance = ConceptStance.Down,
                VoterClass = VoterClass.Account, CastAtUtc = T0
            });
            await db.SaveChangesAsync();
        }

        var second = await world.CommitAsync(await world.MergeAsync(survivor, folded2));
        world.Clock.Now = T0.AddDays(1);
        await using (var db = world.Db())
        {
            var voter = new VoterIdentity(survivorKey, VoterClass.Member, survivor.Id);
            Assert.Equal(CastResult.Changed, await world.Concepts(db).CastAsync("c", "only-folded", voter, ConceptStance.Up, null, default));
        }

        await world.SetStateAsync(second, AccountMergeState.HeimdallReversed);
        await using (var db = world.Db())
        {
            await world.Ledger(db).UndoLocalAsync(second.Id, default);
        }

        await using var after = world.Db();
        var kept = await after.ConceptAppraisals.SingleAsync(x => x.SubjectId == onlyFolded && x.UserAccountId == survivor.Id);
        Assert.Equal((survivorKey, ConceptStance.Up), (kept.VoterKey, kept.Stance));
        Assert.Contains(await world.JournalAsync(second), e => e is { Kind: AccountMergeEntryKind.ConceptAppraisal, Action: AccountMergeEntryAction.KeptAfterUndo });
    }

    [Fact]
    public async Task Closed_motions_keep_their_votes_with_their_owners_and_journal_nothing_for_them()
    {
        var world = new World();
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded");
        var closed = await world.MotionAsync(status: MotionStatus.Passed);
        await world.VoteAsync(closed, survivor, VoteChoice.For, 1m);
        await world.VoteAsync(closed, folded, VoteChoice.For, 2m);
        var lone = await world.MotionAsync(status: MotionStatus.Failed);
        await world.VoteAsync(lone, folded, VoteChoice.Against, 2m);

        var merge = await world.CommitAsync(await world.MergeAsync(survivor, folded));

        Assert.Equal(new[] { folded.Id, survivor.Id }.Order(), (await world.AllVotesAsync(closed)).Select(x => x.UserAccountId).Order());
        var loneVote = Assert.Single(await world.AllVotesAsync(lone));
        Assert.Equal((folded.Id, 2m), (loneVote.UserAccountId, loneVote.Weight)); // not re-pointed, not re-weighed
        Assert.DoesNotContain(await world.JournalAsync(merge), e => e.Kind == AccountMergeEntryKind.Vote);
    }

    [Theory]
    [InlineData(AccountMergeState.Committed)]
    [InlineData(AccountMergeState.Final)]
    public async Task Commit_on_a_landed_merge_returns_the_receipt_and_writes_nothing(AccountMergeState state)
    {
        var world = new World();
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded");
        var merge = await world.MergeAsync(survivor, folded, state);

        var receipt = await world.CommitAsync(merge);

        Assert.Equal((merge.Id, state), (receipt.Id, receipt.State));
        Assert.Empty(await world.JournalAsync(merge));
        Assert.Equal(MembershipStatus.Active, (await world.MembershipOfAsync(folded)).Status);
    }

    [Fact]
    public async Task Undo_is_refused_for_a_merge_that_was_ever_finalized()
    {
        var world = new World();
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded");
        var merge = await world.CommitAsync(await world.MergeAsync(survivor, folded));
        await using (var db = world.Db())
        {
            var row = await db.AccountMerges.SingleAsync(x => x.Id == merge.Id);
            row.State = AccountMergeState.HeimdallReversed;
            row.FinalizedUtc = T0.AddDays(8);
            await db.SaveChangesAsync();
        }

        await using var undo = world.Db();
        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Ledger(undo).UndoLocalAsync(merge.Id, default));
        Assert.Equal(MembershipStatus.Merged, (await world.MembershipOfAsync(folded)).Status);
    }

    [Fact]
    public async Task A_vote_and_an_appraisal_from_an_actor_resolved_before_the_account_froze_write_nothing()
    {
        var world = new World();
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded");
        var motion = await world.MotionAsync();
        var staleActor = await world.ActorAsync(folded.Id); // a request resolved this before the merge was confirmed
        await using (var db = world.Db())
        {
            db.ConceptSubjects.Add(new ConceptSubject { Collection = "c", Key = "k", Title = "K", RegisteredAtUtc = T0 });
            await db.SaveChangesAsync();
        }

        await world.MergeAsync(survivor, folded, AccountMergeState.Confirmed);

        await using (var db = world.Db())
        {
            await world.Motions(db).CastVoteAsync(staleActor, motion.Id, VoteChoice.For, null, default);
            Assert.Equal(CastResult.NoVoter, await world.Concepts(db).CastAsync("c", "k", VoterIdentity.For(staleActor, null), ConceptStance.Up, null, default));
        }

        Assert.Empty(await world.AllVotesAsync(motion));
        await using var read = world.Db();
        Assert.Empty(read.ConceptAppraisals.IgnoreQueryFilters());

        // The survivor, not frozen, still writes.
        await using var control = world.Db();
        await world.Motions(control).CastVoteAsync(await world.ActorAsync(survivor.Id), motion.Id, VoteChoice.For, null, default);
        Assert.Single(await world.AllVotesAsync(motion));
    }

    [Fact]
    public async Task A_merged_membership_is_never_rewritten_by_the_bootstrap_promotion_or_by_suspension()
    {
        var world = new World();
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded");
        var admin = await world.AccountAsync("admin", roles: MemberRole.PlatformAdmin);
        await world.CommitAsync(await world.MergeAsync(survivor, folded));

        await using (var db = world.Db())
        {
            var account = await db.UserAccounts.Include(x => x.Membership!).ThenInclude(x => x.RoleAssignments).SingleAsync(x => x.Id == folded.Id);
            Assert.False(ApplicationBootstrapper.TryPromoteBootstrapAdmin(account, T0, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
            Assert.Equal((MembershipStatus.Merged, false), (account.Membership!.Status, account.Membership.IsPlatformAdmin));
        }

        await using (var db = world.Db())
        {
            var suspension = new MembershipSuspensionService(db, new AuditTrailService(db, world.Clock));
            var actor = await world.ActorAsync(admin.Id);
            Assert.Equal(SuspensionOutcome.NotApplicable, await suspension.SuspendAsync(actor, folded.Id, "reason", default));
            Assert.Equal(SuspensionOutcome.NotApplicable, await suspension.UnsuspendAsync(actor, folded.Id, "reason", default));
        }

        Assert.Equal(MembershipStatus.Merged, (await world.MembershipOfAsync(folded)).Status);
    }

    [Fact]
    public async Task The_digest_names_the_survivor_so_two_memberless_survivors_with_identical_records_digest_differently()
    {
        // A memberless survivor leaves no entry that mentions it: the plan for (A, folded) and for (C, folded) is the same list of
        // entries, so only the pair the digest is made for can tell them apart (heimdall-body:finding:cut-merge-saga.s2.digest-pair-binding-unpinned).
        var world = new World();
        var folded = await world.AccountAsync("folded");
        var a = await MemberlessAsync(world, "survivor-a");
        var c = await MemberlessAsync(world, "survivor-c");

        await using var db = world.Db();
        var forA = await world.Ledger(db).PlanAsync(a.Id, folded.Id, default);
        var forC = await world.Ledger(db).PlanAsync(c.Id, folded.Id, default);

        Assert.Empty(forA.Blocks);
        Assert.Empty(forC.Blocks);
        Assert.Equal(forA.Entries.Select(x => (x.Kind, x.Action, x.PriorText)), forC.Entries.Select(x => (x.Kind, x.Action, x.PriorText)));
        Assert.NotEqual(forA.Digest(a.Id, folded.Id), forC.Digest(c.Id, folded.Id));
        Assert.NotEqual(forA.Digest(a.Id, folded.Id), forA.Digest(c.Id, folded.Id)); // a digest made for one pair does not confirm the other
        Assert.Equal(forA.Digest(a.Id, folded.Id), (await world.Ledger(db).PlanAsync(a.Id, folded.Id, default)).Digest(a.Id, folded.Id));
    }

    private static async Task<UserAccount> MemberlessAsync(World world, string name)
    {
        await using var db = world.Db();
        var account = new UserAccount
        {
            DisplayName = name, BifrostIdentity = name, NormalizedBifrostIdentity = name, HeimdallAccountId = $"hd-{name}-{Guid.NewGuid():N}",
            CreatedAtUtc = T0, LastSeenAtUtc = T0
        };
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }
}
