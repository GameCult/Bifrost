using System.Text.Json;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Motions;
using Bifrost.Web.Features.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Bifrost.Web.Tests;

public sealed class SealedBallotTests
{
    private const string Salt = "00112233445566778899aabbccddeeff";
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Rig
    {
        public readonly Clock Clock = new(T0);
        public readonly BifrostDbContext Db = new(new DbContextOptionsBuilder<BifrostDbContext>()
            .UseInMemoryDatabase($"sealed-{Guid.NewGuid():N}").Options);
        public readonly SealedBallotService Service;
        public readonly CurrentBifrostActor Moderator;

        public Rig()
        {
            Service = Make(Db);
            Moderator = Member("Moderator", moderator: true);
        }

        private SealedBallotService Make(BifrostDbContext db) => new(db, new AuditTrailService(db, Clock), Clock);

        public CurrentBifrostActor Member(string name, bool moderator = false)
        {
            var user = new UserAccount { DisplayName = name, GitHubLogin = name, BifrostIdentity = name, NormalizedBifrostIdentity = name.ToUpperInvariant(), NormalizedGitHubLogin = name.ToUpperInvariant() };
            user.Membership = new Membership { UserAccountId = user.Id, Status = MembershipStatus.Active, CanModerateMotions = moderator };
            Db.UserAccounts.Add(user);
            Db.SaveChanges();
            return new CurrentBifrostActor(true, user);
        }

        public async Task<SealedMotionState> OpenAsync(params string[] candidates) =>
            await Service.OpenAsync(Moderator,
                new OpenSealedMotionCommand("Who writes the swarm's story", "Blind vote.", candidates, T0.AddHours(1), T0.AddHours(2)),
                default);

        public async Task Cast(CurrentBifrostActor who, Guid motionId, string candidate, string reason, string salt = Salt)
        {
            var doc = new SealedBallotDocument(SealedBallotDocument.SchemaId, motionId, 1, candidate, reason, salt);
            await Service.CommitAsync(who, motionId, SealedBallotDocument.Commit(doc, who.UserAccount!.Id), default);
        }

        public Task Reveal(CurrentBifrostActor who, Guid motionId, string candidate, string reason, string salt = Salt) =>
            Service.RevealAsync(who, motionId, new SealedBallotDocument(SealedBallotDocument.SchemaId, motionId, 1, candidate, reason, salt), default);
    }

    [Fact]
    public async Task Commit_reveal_close_publishes_every_ballot_and_each_verifies_against_its_commitment()
    {
        var rig = new Rig();
        var (a, b) = (rig.Member("Ana"), rig.Member("Bo"));
        var motion = await rig.OpenAsync("Ana", "Bo");
        await rig.Cast(a, motion.Id, "Ana", "I love my own prose.");
        await rig.Cast(b, motion.Id, "Ana", "Ana writes best.");
        rig.Clock.Now = T0.AddMinutes(90);
        await rig.Reveal(a, motion.Id, "Ana", "I love my own prose.");
        await rig.Reveal(b, motion.Id, "Ana", "Ana writes best.");

        await Assert.ThrowsAsync<SealedBallotException>(() => rig.Service.GetResultAsync(a, motion.Id, default));

        rig.Clock.Now = T0.AddHours(3);
        var result = await rig.Service.GetResultAsync(a, motion.Id, default);
        Assert.Equal("winner", result.Outcome);
        Assert.Equal(["Ana"], result.Leaders);
        Assert.Equal(2, result.Ballots.Count);
        foreach (var ballot in result.Ballots)
        {
            var doc = new SealedBallotDocument(SealedBallotDocument.SchemaId, motion.Id, 1, ballot.Candidate, ballot.Reason, ballot.Salt);
            Assert.Equal(ballot.Commitment, SealedBallotDocument.Commit(doc, ballot.VoterUserAccountId));
        }

        var again = await rig.Service.GetResultAsync(b, motion.Id, default);
        Assert.Equal(result.CommitmentDigest, again.CommitmentDigest);
        Assert.Equal(MotionStatus.Closed, (await rig.Db.Motions.SingleAsync()).Status);
    }

    [Fact]
    public async Task Nothing_readable_before_close_carries_a_ballot()
    {
        var rig = new Rig();
        var a = rig.Member("Ana");
        var motion = await rig.OpenAsync("Ana", "Bo");
        const string reason = "Distinctive-reason-xyzzy.";
        await rig.Cast(a, motion.Id, "Bo", reason);

        // Commit phase: the store holds the hash and nothing else.
        var row = await rig.Db.SealedBallots.AsNoTracking().SingleAsync();
        Assert.Null(row.RevealedCandidate);
        Assert.Null(row.RevealedReason);
        Assert.Null(row.Salt);

        rig.Clock.Now = T0.AddMinutes(90);
        await rig.Reveal(a, motion.Id, "Bo", reason);

        // Reveal phase, content now in the store: every read surface still withholds it.
        var surfaces = new List<string>
        {
            JsonSerializer.Serialize(await rig.Service.GetStateAsync(a, motion.Id, default)),
            JsonSerializer.Serialize(await rig.Service.GetStateAsync(rig.Moderator, motion.Id, default)),
            JsonSerializer.Serialize(await rig.Db.AuditEvents.AsNoTracking().ToListAsync()),
            JsonSerializer.Serialize(await new MotionGovernanceService(rig.Db, new AuditTrailService(rig.Db, rig.Clock), rig.Clock).GetStateAsync(a, default)),
            JsonSerializer.Serialize(await new DashboardSnapshotService(rig.Db, rig.Clock).GetAsync(a.UserAccount!.Id, default))
        };
        foreach (var surface in surfaces)
        {
            Assert.DoesNotContain(reason, surface);
            Assert.DoesNotContain(Salt, surface);
        }

        var ex = await Assert.ThrowsAsync<SealedBallotException>(() => rig.Service.GetResultAsync(rig.Moderator, motion.Id, default));
        Assert.Equal(SealedBallotFailure.WrongPhase, ex.Failure);
    }

    [Fact]
    public void Only_the_sealed_ballot_service_reads_sealed_content()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Bifrost.Web"))) root = Path.GetDirectoryName(root)!;
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src", "Bifrost.Web"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            .Where(f => !f.EndsWith("SealedBallotService.cs") && !f.EndsWith("BifrostModels.cs"))
            .Where(f => File.ReadAllText(f) is var s && (s.Contains("RevealedCandidate") || s.Contains("RevealedReason") || s.Contains(".Salt")))
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public async Task One_ballot_each_and_a_recommit_replaces_the_first()
    {
        var rig = new Rig();
        var a = rig.Member("Ana");
        var motion = await rig.OpenAsync("Ana", "Bo");
        await rig.Cast(a, motion.Id, "Ana", "First thought.");
        await rig.Cast(a, motion.Id, "Bo", "Second thought.");
        var row = await rig.Db.SealedBallots.SingleAsync();
        Assert.Equal(2, row.Revision);
        Assert.Equal(2, await rig.Db.AuditEvents.CountAsync(x => x.Action == "sealed.committed"));

        rig.Clock.Now = T0.AddMinutes(90);
        await Assert.ThrowsAsync<SealedBallotException>(() => rig.Reveal(a, motion.Id, "Ana", "First thought."));
        await rig.Reveal(a, motion.Id, "Bo", "Second thought.");
        await Assert.ThrowsAsync<SealedBallotException>(() => rig.Cast(a, motion.Id, "Ana", "Late change."));
    }

    [Fact]
    public async Task Unrevealed_and_absent_members_are_shown_as_abstentions_not_votes()
    {
        var rig = new Rig();
        var (a, b, c) = (rig.Member("Ana"), rig.Member("Bo"), rig.Member("Cy"));
        var motion = await rig.OpenAsync("Ana", "Bo");
        await rig.Cast(a, motion.Id, "Ana", "Mine.");
        await rig.Cast(b, motion.Id, "Bo", "Mine too.");
        rig.Clock.Now = T0.AddMinutes(90);
        await rig.Reveal(a, motion.Id, "Ana", "Mine.");
        rig.Clock.Now = T0.AddHours(3);

        var result = await rig.Service.GetResultAsync(c, motion.Id, default);
        Assert.Equal(["Ana"], result.Leaders);
        Assert.Equal(1, result.Tally.Sum(t => t.Votes));
        Assert.Contains(result.Unrevealed, x => x.Member == "Bo" && x.Commitment is not null);
        Assert.Contains(result.NonVoters, x => x.Member == "Cy");
        Assert.DoesNotContain(result.NonVoters, x => x.Member == "Bo");
    }

    [Fact]
    public async Task Ties_are_reported_and_nobody_breaks_them()
    {
        var rig = new Rig();
        var (a, b) = (rig.Member("Ana"), rig.Member("Bo"));
        var motion = await rig.OpenAsync("Ana", "Bo");
        await rig.Cast(a, motion.Id, "Ana", "Self vote.");
        await rig.Cast(b, motion.Id, "Bo", "Self vote.");
        rig.Clock.Now = T0.AddMinutes(90);
        await rig.Reveal(a, motion.Id, "Ana", "Self vote.");
        await rig.Reveal(b, motion.Id, "Bo", "Self vote.");
        rig.Clock.Now = T0.AddHours(3);
        var result = await rig.Service.GetResultAsync(a, motion.Id, default);
        Assert.Equal("tie", result.Outcome);
        Assert.Equal(2, result.Leaders.Count);
    }

    [Fact]
    public async Task Phase_gates_roles_and_the_old_vote_path_hold()
    {
        var rig = new Rig();
        var a = rig.Member("Ana");
        await Assert.ThrowsAsync<SealedBallotException>(() => rig.Service.OpenAsync(a,
            new OpenSealedMotionCommand("x", "y", ["A", "B"], T0.AddHours(1), T0.AddHours(2)), default));
        var motion = await rig.OpenAsync("Ana", "Bo");

        await Assert.ThrowsAsync<SealedBallotException>(() => rig.Reveal(a, motion.Id, "Ana", "Too early."));
        await rig.Cast(a, motion.Id, "Ana", "Fine.");
        rig.Clock.Now = T0.AddMinutes(90);
        await Assert.ThrowsAsync<SealedBallotException>(() => rig.Reveal(a, motion.Id, "Ana", "Wrong text."));
        await Assert.ThrowsAsync<SealedBallotException>(() => rig.Reveal(a, motion.Id, "Cy", "Fine."));

        var standard = new MotionGovernanceService(rig.Db, new AuditTrailService(rig.Db, rig.Clock), rig.Clock);
        await standard.CastVoteAsync(a, motion.Id, VoteChoice.For, "sneak", default);
        Assert.Empty(rig.Db.Votes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => standard.CloseMotionAsync(rig.Moderator, motion.Id, default));
    }

    [Fact]
    public async Task Racing_closers_close_once_and_the_loser_adopts_the_winners_result()
    {
        var clock = new Clock(T0);
        var name = $"sealed-race-{Guid.NewGuid():N}";
        BifrostDbContext NewDb() => new(new DbContextOptionsBuilder<BifrostDbContext>().UseInMemoryDatabase(name).Options);
        SealedBallotService NewService(BifrostDbContext db) => new(db, new AuditTrailService(db, clock), clock);

        await using var seed = NewDb();
        var user = new UserAccount { DisplayName = "Ana", GitHubLogin = "ana", BifrostIdentity = "ana", NormalizedBifrostIdentity = "ANA", NormalizedGitHubLogin = "ANA" };
        user.Membership = new Membership { UserAccountId = user.Id, Status = MembershipStatus.Active, CanModerateMotions = true };
        seed.UserAccounts.Add(user);
        await seed.SaveChangesAsync();
        var actor = new CurrentBifrostActor(true, user);
        var motion = await NewService(seed).OpenAsync(actor,
            new OpenSealedMotionCommand("t", "s", ["Ana", "Bo"], T0.AddHours(1), T0.AddHours(2)), default);
        clock.Now = T0.AddHours(3);

        await using var first = NewDb();
        await using var second = NewDb();
        _ = await second.Motions.ToListAsync(); // second now holds a stale, still-Open copy

        var winner = await NewService(first).GetResultAsync(actor, motion.Id, default);
        var loser = await NewService(second).GetResultAsync(actor, motion.Id, default); // would finalize again; the token refuses it

        Assert.Equal(winner.CommitmentDigest, loser.CommitmentDigest);
        await using var check = NewDb();
        Assert.Equal(1, await check.AuditEvents.CountAsync(x => x.Action == "sealed.closed"));

        // Genuinely concurrent first reads on fresh contexts also close exactly once.
        var other = await NewService(check).OpenAsync(actor,
            new OpenSealedMotionCommand("t2", "s", ["Ana", "Bo"], T0.AddHours(4), T0.AddHours(5)), default);
        clock.Now = T0.AddHours(6);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var db = NewDb();
            await NewService(db).GetResultAsync(actor, other.Id, default);
        }));
        await using var after = NewDb();
        Assert.Equal(1, await after.AuditEvents.CountAsync(x => x.Action == "sealed.closed" && x.EntityId == other.Id.ToString()));
    }

    [Fact]
    public async Task The_sweep_closes_a_due_motion_nobody_read()
    {
        var rig = new Rig();
        var motion = await rig.OpenAsync("Ana", "Bo");
        Assert.Equal(0, await rig.Service.FinalizeDueAsync(default));
        rig.Clock.Now = T0.AddHours(3);
        Assert.Equal(1, await rig.Service.FinalizeDueAsync(default));
        Assert.Equal(MotionStatus.Closed, (await rig.Db.Motions.SingleAsync(x => x.Id == motion.Id)).Status);
        Assert.Equal(0, await rig.Service.FinalizeDueAsync(default));
        Assert.Equal(1, await rig.Db.AuditEvents.CountAsync(x => x.Action == "sealed.closed"));
    }

    // Runs a callback once, just before the first save that closes a motion: the window between the close reading its ballots and writing.
    private sealed class BeforeCloseSave(Func<Task> action) : SaveChangesInterceptor
    {
        private bool _done;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_done && eventData.Context!.ChangeTracker.Entries<Motion>().Any(e => e.State == EntityState.Modified && e.Entity.Status == MotionStatus.Closed))
            {
                _done = true;
                await action();
            }

            return result;
        }
    }

    private static (BifrostDbContext Db, Clock Clock, SealedBallotService Service) Peer(string name, DateTimeOffset at, IInterceptor? interceptor = null)
    {
        var clock = new Clock(at);
        var options = new DbContextOptionsBuilder<BifrostDbContext>().UseInMemoryDatabase(name);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        var db = new BifrostDbContext(options.Options);
        return (db, clock, new SealedBallotService(db, new AuditTrailService(db, clock), clock));
    }

    private static async Task<(CurrentBifrostActor Actor, SealedMotionState Motion)> SeedPeerWorld(BifrostDbContext db, SealedBallotService service)
    {
        var user = new UserAccount { DisplayName = "Ana", GitHubLogin = "ana", BifrostIdentity = "ana", NormalizedBifrostIdentity = "ANA", NormalizedGitHubLogin = "ANA" };
        user.Membership = new Membership { UserAccountId = user.Id, Status = MembershipStatus.Active, CanModerateMotions = true };
        db.UserAccounts.Add(user);
        await db.SaveChangesAsync();
        var actor = new CurrentBifrostActor(true, user);
        var motion = await service.OpenAsync(actor, new OpenSealedMotionCommand("t", "s", ["Ana", "Bo"], T0.AddHours(1), T0.AddHours(2)), default);
        var doc = new SealedBallotDocument(SealedBallotDocument.SchemaId, motion.Id, 1, "Ana", "Mine.", Salt);
        await service.CommitAsync(actor, motion.Id, SealedBallotDocument.Commit(doc, user.Id), default);
        return (actor, motion);
    }

    [Fact]
    public async Task A_reveal_that_loses_the_race_to_the_close_is_refused_and_is_not_in_the_frozen_set()
    {
        var name = $"sealed-freeze-a-{Guid.NewGuid():N}";
        var closer = Peer(name, T0);
        var (actor, motion) = await SeedPeerWorld(closer.Db, closer.Service);
        closer.Clock.Now = T0.AddMinutes(90);
        var revealer = Peer(name, T0.AddMinutes(90)); // its clock still says "reveal window"
        _ = await revealer.Db.Motions.ToListAsync();   // and it holds a stale, Open copy of the motion

        closer.Clock.Now = T0.AddHours(3);
        var result = await closer.Service.GetResultAsync(actor, motion.Id, default);
        Assert.Empty(result.Ballots);

        var ex = await Assert.ThrowsAsync<SealedBallotException>(() =>
            revealer.Service.RevealAsync(actor, motion.Id, new SealedBallotDocument(SealedBallotDocument.SchemaId, motion.Id, 1, "Ana", "Mine.", Salt), default));
        Assert.Equal(SealedBallotFailure.WrongPhase, ex.Failure);

        await using var check = Peer(name, T0.AddHours(4)).Db;
        Assert.Null((await check.SealedBallots.AsNoTracking().SingleAsync()).RevealedAtUtc);
        Assert.Equal(result.CommitmentDigest, (await check.Motions.AsNoTracking().SingleAsync()).ResultDigest);
    }

    [Fact]
    public async Task A_close_that_read_before_a_reveal_landed_retries_and_the_digest_covers_that_reveal()
    {
        var name = $"sealed-freeze-b-{Guid.NewGuid():N}";
        var revealer = Peer(name, T0);
        var (actor, motion) = await SeedPeerWorld(revealer.Db, revealer.Service);
        revealer.Clock.Now = T0.AddMinutes(90);
        var doc = new SealedBallotDocument(SealedBallotDocument.SchemaId, motion.Id, 1, "Ana", "Mine.", Salt);
        // The reveal lands after the close read its ballots and before the close wrote.
        var closer = Peer(name, T0.AddHours(3), new BeforeCloseSave(() => revealer.Service.RevealAsync(actor, motion.Id, doc, default)));

        var result = await closer.Service.GetResultAsync(actor, motion.Id, default);

        Assert.Single(result.Ballots);
        Assert.Empty(result.Unrevealed);
        await using var check = Peer(name, T0.AddHours(4)).Db;
        var stored = await check.Motions.AsNoTracking().SingleAsync();
        Assert.Equal(1, stored.RevealSeq);
        var ballot = await check.SealedBallots.AsNoTracking().SingleAsync();
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"reveals:1\n{ballot.UserAccountId:D}:{ballot.Commitment}:revealed"))).ToLowerInvariant();
        Assert.Equal(expected, stored.ResultDigest); // the digest covers the reveal that raced the close
        Assert.Equal(1, await check.AuditEvents.CountAsync(x => x.Action == "sealed.closed"));
    }

    [Fact]
    public async Task Two_simultaneous_reveals_both_land_while_the_window_is_open()
    {
        var name = $"sealed-two-reveals-{Guid.NewGuid():N}";
        var first = Peer(name, T0);
        var (ana, motion) = await SeedPeerWorld(first.Db, first.Service);
        var bo = new UserAccount { DisplayName = "Bo", GitHubLogin = "bo", BifrostIdentity = "bo", NormalizedBifrostIdentity = "BO", NormalizedGitHubLogin = "BO" };
        bo.Membership = new Membership { UserAccountId = bo.Id, Status = MembershipStatus.Active };
        first.Db.UserAccounts.Add(bo);
        await first.Db.SaveChangesAsync();
        var boActor = new CurrentBifrostActor(true, bo);
        var boDoc = new SealedBallotDocument(SealedBallotDocument.SchemaId, motion.Id, 1, "Bo", "Mine too.", Salt);
        await first.Service.CommitAsync(boActor, motion.Id, SealedBallotDocument.Commit(boDoc, bo.Id), default);
        first.Clock.Now = T0.AddMinutes(90);

        var second = Peer(name, T0.AddMinutes(90));
        _ = await second.Db.Motions.ToListAsync(); // stale RevealSeq 0, window open

        await first.Service.RevealAsync(ana, motion.Id, new SealedBallotDocument(SealedBallotDocument.SchemaId, motion.Id, 1, "Ana", "Mine.", Salt), default);
        var receipt = await second.Service.RevealAsync(boActor, motion.Id, boDoc, default); // conflicts on RevealSeq, window still open: must retry, not claim a close

        Assert.True(receipt.Revealed);
        await using var check = Peer(name, T0.AddHours(4)).Db;
        Assert.Equal(2, await check.SealedBallots.CountAsync(x => x.RevealedAtUtc != null));
        Assert.Equal(2, (await check.Motions.AsNoTracking().SingleAsync()).RevealSeq);
    }

    [Fact]
    public async Task A_salt_with_a_trailing_newline_is_not_hex()
    {
        var rig = new Rig();
        var a = rig.Member("Ana");
        var motion = await rig.OpenAsync("Ana", "Bo");
        await rig.Cast(a, motion.Id, "Ana", "Mine.", Salt + "\n");
        rig.Clock.Now = T0.AddMinutes(90);
        var ex = await Assert.ThrowsAsync<SealedBallotException>(() => rig.Reveal(a, motion.Id, "Ana", "Mine.", Salt + "\n"));
        Assert.Equal(SealedBallotFailure.Invalid, ex.Failure);
        Assert.Null((await rig.Db.SealedBallots.SingleAsync()).RevealedAtUtc);
    }
}
