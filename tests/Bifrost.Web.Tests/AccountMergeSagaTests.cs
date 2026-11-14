using System.Net;
using System.Text.Json;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Heimdall;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bifrost.Web.Tests;

/// <summary>
/// The merge saga from its consumers' side: a request scope of the real host (DI, the move client's HttpClient, Data Protection, the shared
/// TestClock) driving Confirm, Advance and RequestUndo against the fake Heimdall's account model (Heimdall 4fe367a's contract). Every
/// operation takes a fresh scope, as a request or a sweeper tick would, so what survives is only what was saved.
/// </summary>
public sealed class AccountMergeSagaTests
{
    private const string Passkey = "bifrost-passkey";

    internal sealed record Party(Guid Id, string HeimdallId, IReadOnlyDictionary<string, string> Logins);

    /// <summary>A host plus the helpers every test needs: accounts with logins at the fake Heimdall, proofs, a scope per operation.</summary>
    internal sealed class Rig(bool realSessions = false, bool accountMerge = false) : IDisposable
    {
        public HeimdallHost Host { get; } = new(realSessions: realSessions, accountMerge: accountMerge);

        public FakeMoves Moves => Host.Heimdall.Moves;

        public TestClock Clock => Host.Clock;

        public void Dispose() => Host.Dispose();

        public async Task<Party> AccountAsync(string name, params string[] providers)
        {
            var heimdallId = HeimdallShapes.NewAccountId();
            var logins = providers.ToDictionary(x => x, x => Moves.AddLogin(heimdallId, x));
            using var scope = Host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
            var account = new UserAccount
            {
                DisplayName = name, BifrostIdentity = name, NormalizedBifrostIdentity = BifrostIdentityService.NormalizeIdentity(name),
                HeimdallAccountId = heimdallId, CreatedAtUtc = Clock.Now, LastSeenAtUtc = Clock.Now,
                MemberProfile = new MemberProfile { Nickname = name, Headline = "seed", UpdatedAtUtc = Clock.Now }
            };
            account.Membership = new Membership { UserAccountId = account.Id, Status = MembershipStatus.Active };
            db.UserAccounts.Add(account);
            await db.SaveChangesAsync();
            return new Party(account.Id, heimdallId, logins);
        }

        /// <summary>A fresh sign-in through the login of <paramref name="provider"/>: a weak proof of that login only.</summary>
        public MergeProof Weak(Party who, string provider) => new(Moves.Issue(who.HeimdallId, who.Logins[provider]), who.HeimdallId, provider);

        /// <summary>A fresh sign-in of <paramref name="holder"/> through a login that came from <paramref name="origin"/> (a login the merge moved to it).</summary>
        public MergeProof Moved(Party holder, Party origin, string provider) => new(Moves.Issue(holder.HeimdallId, origin.Logins[provider]), holder.HeimdallId, provider);

        /// <summary>A passkey assertion: a strong proof that speaks for the whole account.</summary>
        public MergeProof Strong(Party who) => new(Moves.Issue(who.HeimdallId, stepUpMethod: Passkey), who.HeimdallId, "passkey");

        public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
        {
            using var scope = Host.Services.CreateScope();
            return await work(scope.ServiceProvider);
        }

        public async Task<string> DigestAsync(Party survivor, Party folded) =>
            await InScopeAsync(async sp => (await sp.GetRequiredService<AccountMergeLedger>().PlanAsync(survivor.Id, folded.Id, default)).Digest(survivor.Id, folded.Id));

        public async Task<ConfirmOutcome> ConfirmAsync(Party survivor, Party folded, MergeProof survivorProof, params MergeProof[] foldedProofs)
        {
            var digest = await DigestAsync(survivor, folded);
            return await InScopeAsync(sp => sp.GetRequiredService<AccountMergeSaga>().ConfirmAsync(
                new ConfirmMergeRequest(survivor.Id, survivor.Id, folded.Id, digest, survivorProof, foldedProofs), default));
        }

        public async Task<Guid> ConfirmedAsync(Party survivor, Party folded, MergeProof survivorProof, params MergeProof[] foldedProofs) =>
            Assert.IsType<ConfirmOutcome.Confirmed>(await ConfirmAsync(survivor, folded, survivorProof, foldedProofs)).MergeId;

        public Task<AccountMerge> AdvanceAsync(Guid id) => InScopeAsync(sp => sp.GetRequiredService<AccountMergeSaga>().AdvanceAsync(id, default));

        public Task<int> AdvanceDueAsync() => InScopeAsync(sp => sp.GetRequiredService<AccountMergeSaga>().AdvanceDueAsync(Clock.Now, default));

        public Task<AccountMerge> MergeAsync(Guid id) =>
            InScopeAsync(sp => sp.GetRequiredService<BifrostDbContext>().AccountMerges.AsNoTracking().SingleAsync(x => x.Id == id));

        public Task<List<AccountMergeEntry>> JournalAsync(Guid id) =>
            InScopeAsync(sp => sp.GetRequiredService<BifrostDbContext>().AccountMergeEntries.Where(x => x.MergeId == id).ToListAsync());

        public Task<MergeStanding?> StandingAsync(Party who) =>
            InScopeAsync(sp => sp.GetRequiredService<AccountMergeLedger>().StandingAsync(who.Id, default));

        public Task<int> MergeCountAsync() => InScopeAsync(sp => sp.GetRequiredService<BifrostDbContext>().AccountMerges.CountAsync());

        public async Task<Motion> MotionAsync(Party author)
        {
            using var scope = Host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
            var motion = new Motion
            {
                CreatedByUserAccountId = author.Id, Title = "m", Status = MotionStatus.Open, Kind = MotionKind.Standard, OpensAtUtc = Clock.Now,
                ClosesAtUtc = Clock.Now.AddDays(3), CreatedAtUtc = Clock.Now, ApprovalThreshold = 0.5m
            };
            db.Motions.Add(motion);
            await db.SaveChangesAsync();
            return motion;
        }

        public async Task VoteAsync(Motion motion, Party who, VoteChoice choice)
        {
            using var scope = Host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
            db.Votes.Add(new Vote { MotionId = motion.Id, UserAccountId = who.Id, Choice = choice, Weight = 1m, CastAtUtc = Clock.Now.AddHours(1) });
            await db.SaveChangesAsync();
        }

        public Task<string[]> SeenMoveIdsAsync() =>
            Task.FromResult(Moves.Requests.Select(x => JsonDocument.Parse(x.Body).RootElement.GetProperty("moveId").GetString()!).ToArray());
    }

    // ---- the happy path and the stored move id ----

    [Fact]
    public async Task Two_weak_folded_proofs_and_a_survivor_proof_confirm_and_advance_to_committed_with_a_seven_day_grace_and_one_stored_move()
    {
        using var rig = new Rig();
        var survivor = await rig.AccountAsync("survivor", "twitch");
        var folded = await rig.AccountAsync("folded", "discord", "patreon");

        var digest = await rig.DigestAsync(survivor, folded);
        var id = await rig.ConfirmedAsync(survivor, folded, rig.Weak(survivor, "twitch"), rig.Weak(folded, "discord"), rig.Weak(folded, "patreon"));
        var confirmed = await rig.MergeAsync(id);
        Assert.Equal(AccountMergeState.Confirmed, confirmed.State);
        Assert.Equal(survivor.Id, confirmed.ConfirmedByUserAccountId);
        Assert.Equal(rig.Clock.Now, confirmed.ConfirmedUtc);
        Assert.Equal("twitch", confirmed.SurvivorProofProvider);
        Assert.Equal("discord,patreon", confirmed.FoldedProofProviders);
        Assert.Equal(digest, confirmed.PreviewDigest);
        Assert.NotNull(confirmed.SealedProofs);
        Assert.Matches("^[0-9a-f]{32}$", confirmed.HeimdallMoveId!);
        Assert.Empty(rig.Moves.Requests); // confirm calls no Heimdall
        Assert.IsType<MergeStanding.Frozen>(await rig.StandingAsync(folded));

        var done = await rig.AdvanceAsync(id);

        Assert.Equal(AccountMergeState.Committed, done.State);
        Assert.Equal(done.CommittedUtc + AccountMergeLedger.Grace, done.GraceEndsUtc);
        var stored = Assert.Single(rig.Moves.Stored);
        Assert.Equal(confirmed.HeimdallMoveId, stored.MoveId);
        var final = await rig.MergeAsync(id);
        Assert.Null(final.SealedProofs);
        Assert.Equal("weak", final.ProofFromTier);
        Assert.Equal("weak", final.ProofToTier);
        Assert.Equal(folded.Logins.Values.Order(), JsonSerializer.Deserialize<string[]>(final.MovedIdentityIds!)!.Order());
        Assert.IsType<MergeStanding.Folded>(await rig.StandingAsync(folded));
        Assert.Equal(["twitch", "discord", "patreon"], rig.Moves.Logins[survivor.HeimdallId].Select(x => x.Provider));
    }

    [Fact]
    public async Task A_vote_on_the_same_open_motion_from_both_accounts_counts_only_the_survivors_after_the_saga_lands()
    {
        using var rig = new Rig();
        var survivor = await rig.AccountAsync("survivor", "twitch");
        var folded = await rig.AccountAsync("folded", "discord");
        var motion = await rig.MotionAsync(survivor);
        await rig.VoteAsync(motion, survivor, VoteChoice.For);
        await rig.VoteAsync(motion, folded, VoteChoice.Against);

        var id = await rig.ConfirmedAsync(survivor, folded, rig.Weak(survivor, "twitch"), rig.Weak(folded, "discord"));
        await rig.AdvanceAsync(id);

        await rig.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<BifrostDbContext>();
            Assert.Equal(survivor.Id, (await db.Votes.SingleAsync(x => x.MotionId == motion.Id)).UserAccountId);
            var all = await db.Votes.IgnoreQueryFilters().Where(x => x.MotionId == motion.Id).ToListAsync();
            Assert.Equal(id, Assert.Single(all, x => x.UserAccountId == folded.Id).VoidedByMergeId);
            return 0;
        });
    }

    [Fact]
    public async Task Heimdall_sees_the_move_id_stored_on_the_row_and_it_is_not_derived_from_the_merge_id()
    {
        using var rig = new Rig();
        var survivor = await rig.AccountAsync("survivor", "twitch");
        var folded = await rig.AccountAsync("folded", "discord");
        var id = await rig.ConfirmedAsync(survivor, folded, rig.Weak(survivor, "twitch"), rig.Weak(folded, "discord"));
        Assert.NotEqual(id.ToString("N"), (await rig.MergeAsync(id)).HeimdallMoveId);
        const string overwritten = "overwritten0123456789abcdef0123456789";
        await rig.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<BifrostDbContext>();
            (await db.AccountMerges.SingleAsync(x => x.Id == id)).HeimdallMoveId = overwritten;
            await db.SaveChangesAsync();
            return 0;
        });

        await rig.AdvanceAsync(id);

        Assert.Equal(overwritten, Assert.Single(rig.Moves.Stored).MoveId);
    }

    // ---- crashes and lost answers ----

    [Fact]
    public async Task A_lost_answer_leaves_the_merge_confirmed_and_a_fresh_scope_after_the_proofs_expired_settles_it_by_replay()
    {
        using var rig = new Rig();
        var survivor = await rig.AccountAsync("survivor", "twitch");
        var folded = await rig.AccountAsync("folded", "discord");
        var id = await rig.ConfirmedAsync(survivor, folded, rig.Weak(survivor, "twitch"), rig.Weak(folded, "discord"));
        rig.Moves.MoveApplyThenFail = HttpStatusCode.InternalServerError;

        var lost = await rig.AdvanceAsync(id);

        Assert.Equal(AccountMergeState.Confirmed, lost.State);
        Assert.Equal(1, lost.Attempts);
        Assert.Equal(rig.Clock.Now + TimeSpan.FromSeconds(15), lost.NextAttemptUtc);
        Assert.Equal("HTTP 500", lost.LastOutcome);
        Assert.Single(rig.Moves.Stored);
        rig.Moves.MoveApplyThenFail = null;
        rig.Clock.Advance(TimeSpan.FromSeconds(601)); // every proof is stale and the bearers expired by now; Heimdall answers a replay first

        Assert.Equal(1, await rig.AdvanceDueAsync());

        Assert.Equal(AccountMergeState.Committed, (await rig.MergeAsync(id)).State);
        Assert.Single(rig.Moves.Stored);
        Assert.Equal(2, (await rig.SeenMoveIdsAsync()).Length);
    }

    [Fact]
    public async Task A_crash_on_the_save_that_writes_HeimdallMoved_is_resumed_with_the_same_move_id_and_one_stored_move()
    {
        using var rig = new Rig();
        var survivor = await rig.AccountAsync("survivor", "twitch");
        var folded = await rig.AccountAsync("folded", "discord");
        var id = await rig.ConfirmedAsync(survivor, folded, rig.Weak(survivor, "twitch"), rig.Weak(folded, "discord"));
        rig.Host.Fault.When = db => db.ChangeTracker.Entries<AccountMerge>().Any(x => x.Entity.State == AccountMergeState.HeimdallMoved);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.AdvanceAsync(id));
        rig.Host.Fault.When = null;
        Assert.Equal(AccountMergeState.Confirmed, (await rig.MergeAsync(id)).State);
        Assert.NotNull((await rig.MergeAsync(id)).SealedProofs);

        Assert.Equal(AccountMergeState.Committed, (await rig.AdvanceAsync(id)).State);

        var seen = await rig.SeenMoveIdsAsync();
        Assert.Equal(2, seen.Length);
        Assert.Single(seen.Distinct());
        Assert.Single(rig.Moves.Stored);
        Assert.Equal(seen[0], (await rig.MergeAsync(id)).HeimdallMoveId);
    }

    [Fact]
    public async Task A_crash_on_the_Committed_save_leaves_HeimdallMoved_and_the_next_advance_commits_once_without_calling_Heimdall_again()
    {
        using var rig = new Rig();
        var survivor = await rig.AccountAsync("survivor", "twitch");
        var folded = await rig.AccountAsync("folded", "discord");
        var motion = await rig.MotionAsync(survivor);
        await rig.VoteAsync(motion, folded, VoteChoice.For);
        var id = await rig.ConfirmedAsync(survivor, folded, rig.Weak(survivor, "twitch"), rig.Weak(folded, "discord"));
        rig.Host.Fault.When = db => db.ChangeTracker.Entries<AccountMerge>().Any(x => x.Entity.State == AccountMergeState.Committed);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.AdvanceAsync(id));
        rig.Host.Fault.When = null;
        var moved = await rig.MergeAsync(id);
        Assert.Equal(AccountMergeState.HeimdallMoved, moved.State);
        Assert.Null(moved.SealedProofs);
        Assert.Empty(await rig.JournalAsync(id));

        Assert.Equal(AccountMergeState.Committed, (await rig.AdvanceAsync(id)).State);
        Assert.Equal(AccountMergeState.Committed, (await rig.AdvanceAsync(id)).State);

        Assert.Single(rig.Moves.Requests);
        var journal = await rig.JournalAsync(id);
        Assert.Equal(journal.Count, journal.Select(x => x.Seq).Distinct().Count());
        Assert.Single(journal, x => x.Kind == AccountMergeEntryKind.Vote);
    }

    [Fact]
    public async Task The_sweepers_own_loop_resumes_a_confirmed_merge_after_a_restart_and_finalizes_it_after_the_grace()
    {
        using var rig = new Rig();
        var survivor = await rig.AccountAsync("survivor", "twitch");
        var folded = await rig.AccountAsync("folded", "discord");
        var id = await rig.ConfirmedAsync(survivor, folded, rig.Weak(survivor, "twitch"), rig.Weak(folded, "discord"));

        await RunOneSweepAsync(rig);
        Assert.Equal(AccountMergeState.Committed, (await rig.MergeAsync(id)).State);

        rig.Clock.Advance(AccountMergeLedger.Grace + TimeSpan.FromMinutes(1));
        await RunOneSweepAsync(rig);
        Assert.Equal(AccountMergeState.Final, (await rig.MergeAsync(id)).State);
    }

    /// <summary>Starts the production sweeper (its first pass runs at once, before its 30 s timer) and stops it once the pass is over.</summary>
    private static async Task RunOneSweepAsync(Rig rig)
    {
        var sweeper = new AccountMergeSweeper(rig.Host.Services.GetRequiredService<IServiceScopeFactory>(), rig.Clock, NullLogger<AccountMergeSweeper>.Instance);
        await sweeper.StartAsync(default);
        var before = rig.Clock.Now;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && sweeper.ExecuteTask is { IsCompleted: false } && await rig.InScopeAsync(async sp =>
                   await sp.GetRequiredService<BifrostDbContext>().AccountMerges.AnyAsync(x =>
                       x.State == AccountMergeState.Confirmed || x.State == AccountMergeState.HeimdallMoved || (x.State == AccountMergeState.Committed && x.GraceEndsUtc <= before))))
        {
            await Task.Delay(50);
        }

        await sweeper.StopAsync(default);
    }

    // ---- refusals ----

    [Theory]
    [InlineData("proof_stale")]
    [InlineData("identity_not_proved")]
    [InlineData("provider_conflict")]
    [InlineData("already_moved")]
    public async Task A_coded_refusal_fails_the_merge_wipes_the_proofs_and_changes_nothing_in_Bifrost(string code)
    {
        using var rig = new Rig();
        var survivor = await rig.AccountAsync("survivor", code == "provider_conflict" ? ["discord"] : ["twitch"]);
        var folded = await rig.AccountAsync("folded", "discord", "patreon");
        var motion = await rig.MotionAsync(survivor);
        await rig.VoteAsync(motion, folded, VoteChoice.For);
        MergeProof[] foldedProofs = code == "identity_not_proved"
            ? [rig.Weak(folded, "discord")] // the patreon login is not proved: no partial move
            : [rig.Weak(folded, "discord"), rig.Weak(folded, "patreon")];
        var survivorProof = rig.Weak(survivor, code == "provider_conflict" ? "discord" : "twitch");
        if (code == "already_moved")
        {
            // Heimdall already moved the folded account elsewhere; Bifrost's merge does not know.
            var elsewhere = await rig.AccountAsync("elsewhere", "twitch2");
            var move = new HeimdallMoveRequest("preexisting-move-0000", [rig.Strong(folded).AccessToken], rig.Weak(elsewhere, "twitch2").AccessToken);
            Assert.IsType<HeimdallMoveOutcome.Moved>(await rig.InScopeAsync(sp => sp.GetRequiredService<HeimdallIdentityMoveClient>().MoveAsync(move, default)));
            foldedProofs = [rig.Strong(folded)];
        }

        var id = await rig.ConfirmedAsync(survivor, folded, survivorProof, foldedProofs);
        if (code == "proof_stale")
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(400));
        }

        var failed = await rig.AdvanceAsync(id);

        Assert.Equal(AccountMergeState.Failed, failed.State);
        Assert.Equal(code, failed.FailureCode);
        Assert.Equal(rig.Clock.Now, failed.FailedUtc);
        var stored = await rig.MergeAsync(id);
        Assert.Null(stored.SealedProofs);
        Assert.Empty(await rig.JournalAsync(id));
        Assert.Null(await rig.StandingAsync(folded));
        await rig.InScopeAsync(async sp =>
        {
            var vote = await sp.GetRequiredService<BifrostDbContext>().Votes.SingleAsync();
            Assert.Equal(folded.Id, vote.UserAccountId);
            return 0;
        });
        Assert.DoesNotContain(rig.Moves.Stored, x => x.MoveId == stored.HeimdallMoveId);
    }

    // ---- confirm's consent checks ----

    [Fact]
    public async Task Confirm_refuses_a_third_party_a_changed_plan_a_proof_of_another_account_and_a_blocked_pair_and_writes_no_row()
    {
        using var rig = new Rig();
        var survivor = await rig.AccountAsync("survivor", "twitch");
        var folded = await rig.AccountAsync("folded", "discord");
        var stranger = await rig.AccountAsync("stranger", "github");
        var digest = await rig.DigestAsync(survivor, folded);
        var good = rig.Weak(survivor, "twitch");
        var foldedProof = rig.Weak(folded, "discord");

        Task<ConfirmOutcome> Confirm(Guid actor, string digestSeen, MergeProof survivorProof, params MergeProof[] proofs) =>
            rig.InScopeAsync(sp => sp.GetRequiredService<AccountMergeSaga>().ConfirmAsync(
                new ConfirmMergeRequest(actor, survivor.Id, folded.Id, digestSeen, survivorProof, proofs), default));

        Assert.IsType<ConfirmOutcome.NotParty>(await Confirm(stranger.Id, digest, good, foldedProof));
        Assert.IsType<ConfirmOutcome.ProofAccountMismatch>(await Confirm(survivor.Id, digest, rig.Weak(stranger, "github"), foldedProof));
        Assert.IsType<ConfirmOutcome.ProofAccountMismatch>(await Confirm(survivor.Id, digest, good, rig.Weak(survivor, "twitch")));
        var motion = await rig.MotionAsync(survivor);
        await rig.VoteAsync(motion, folded, VoteChoice.For); // the plan the member saw is no longer the plan
        Assert.IsType<ConfirmOutcome.PreviewStale>(await Confirm(survivor.Id, digest, good, foldedProof));
        Assert.Equal(0, await rig.MergeCountAsync());

        var blocked = Assert.IsType<ConfirmOutcome.Blocked>(await rig.InScopeAsync(sp => sp.GetRequiredService<AccountMergeSaga>().ConfirmAsync(
            new ConfirmMergeRequest(survivor.Id, survivor.Id, survivor.Id, "x", good, [good]), default)));
        Assert.Contains("same_account", blocked.Codes);
        Assert.Equal(0, await rig.MergeCountAsync());

        // a second confirm of the same fold, after the first
        var fresh = await rig.DigestAsync(survivor, folded);
        Assert.IsType<ConfirmOutcome.Confirmed>(await Confirm(survivor.Id, fresh, good, foldedProof));
        var second = Assert.IsType<ConfirmOutcome.Blocked>(await Confirm(survivor.Id, fresh, rig.Weak(survivor, "twitch"), rig.Weak(folded, "discord")));
        Assert.Contains(AccountMergeSaga.MergeInFlight, second.Codes);
        Assert.Equal(1, await rig.MergeCountAsync());
    }

    [Fact]
    public async Task A_malformed_confirm_throws_before_any_row_and_neither_the_message_nor_the_proof_text_carry_a_token()
    {
        using var rig = new Rig();
        var survivor = await rig.AccountAsync("survivor", "twitch");
        var folded = await rig.AccountAsync("folded", "discord");
        const string canary = "CANARY-bearer-9f3a";
        var proof = new MergeProof(canary, folded.HeimdallId, "discord");
        Assert.DoesNotContain(canary, proof.ToString());
        Assert.DoesNotContain(folded.HeimdallId, proof.ToString());
        var request = new ConfirmMergeRequest(survivor.Id, survivor.Id, folded.Id, "d", new MergeProof(canary, survivor.HeimdallId, "twitch"), [proof]);
        Assert.DoesNotContain(canary, request.ToString());

        var nine = Enumerable.Repeat(proof, 9).ToList();
        var tooMany = await Assert.ThrowsAsync<ArgumentException>(() =>
            rig.InScopeAsync(sp => sp.GetRequiredService<AccountMergeSaga>().ConfirmAsync(request with { FoldedProofs = nine }, default)));
        var tooLong = await Assert.ThrowsAsync<ArgumentException>(() =>
            rig.InScopeAsync(sp => sp.GetRequiredService<AccountMergeSaga>().ConfirmAsync(
                request with { FoldedProofs = [proof with { AccessToken = canary + new string('x', HeimdallIdentityMoveClient.MaxProofLength) }] }, default)));

        Assert.DoesNotContain(canary, tooMany.ToString());
        Assert.DoesNotContain(canary, tooLong.ToString());
        Assert.Equal(0, await rig.MergeCountAsync());
    }

    // ---- sealing ----

    [Fact]
    public async Task Sealed_proofs_never_hold_a_bearer_in_the_clear_are_wiped_at_each_final_answer_and_no_log_carries_a_bearer_or_an_account()
    {
        using var rig = new Rig();
        var survivor = await rig.AccountAsync("survivor", "twitch");
        var folded = await rig.AccountAsync("folded", "discord");
        var survivorProof = rig.Weak(survivor, "twitch");
        var foldedProof = rig.Weak(folded, "discord");
        var id = await rig.ConfirmedAsync(survivor, folded, survivorProof, foldedProof);
        var sealedText = (await rig.MergeAsync(id)).SealedProofs!;
        Assert.DoesNotContain(survivorProof.AccessToken, sealedText);
        Assert.DoesNotContain(foldedProof.AccessToken, sealedText);
        Assert.DoesNotContain("bearer", sealedText);

        await rig.AdvanceAsync(id);
        Assert.Null((await rig.MergeAsync(id)).SealedProofs);

        var undoProof = rig.Moved(survivor, folded, "discord"); // the moved login now lives on the survivor
        Assert.IsType<UndoOutcome.Requested>(await RequestUndoAsync(rig, id, survivor, undoProof));
        var requested = (await rig.MergeAsync(id)).SealedProofs!;
        Assert.DoesNotContain(undoProof.AccessToken, requested);
        await rig.AdvanceAsync(id);
        Assert.Null((await rig.MergeAsync(id)).SealedProofs);

        var log = string.Join('\n', rig.Host.Logs.Lines);
        foreach (var secret in new[] { survivorProof.AccessToken, foldedProof.AccessToken, undoProof.AccessToken, survivor.HeimdallId, folded.HeimdallId }
                     .Concat(folded.Logins.Values).Concat(survivor.Logins.Values))
        {
            Assert.DoesNotContain(secret, log);
        }

        Assert.Contains(id.ToString(), log);
    }

    private static Task<UndoOutcome> RequestUndoAsync(Rig rig, Guid id, Party actor, params MergeProof[] proofs) =>
        rig.InScopeAsync(sp => sp.GetRequiredService<AccountMergeSaga>().RequestUndoAsync(id, actor.Id, proofs, default));

    [Fact]
    public async Task Unreadable_sealed_proofs_change_no_state_and_retry_in_an_hour_and_the_error_log_names_only_the_merge()
    {
        using var rig = new Rig();
        var survivor = await rig.AccountAsync("survivor", "twitch");
        var folded = await rig.AccountAsync("folded", "discord");
        var id = await rig.ConfirmedAsync(survivor, folded, rig.Weak(survivor, "twitch"), rig.Weak(folded, "discord"));
        await rig.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<BifrostDbContext>();
            (await db.AccountMerges.SingleAsync(x => x.Id == id)).SealedProofs = "CfDJ8-not-a-protected-payload";
            await db.SaveChangesAsync();
            return 0;
        });

        var stuck = await rig.AdvanceAsync(id);

        Assert.Equal(AccountMergeState.Confirmed, stuck.State);
        Assert.Equal(AccountMergeSaga.ProofsUnreadable, stuck.LastOutcome);
        Assert.Equal(rig.Clock.Now + TimeSpan.FromHours(1), stuck.NextAttemptUtc);
        Assert.Empty(rig.Moves.Requests);
        Assert.Contains(rig.Host.Logs.Lines, x => x.Contains(id.ToString()) && x.Contains("unreadable"));
        Assert.Equal(0, await rig.AdvanceDueAsync()); // not due for an hour
    }

    [Fact]
    public async Task A_receipt_for_another_account_is_recorded_wipes_the_proofs_and_is_never_retried()
    {
        using var rig = new Rig();
        var survivor = await rig.AccountAsync("survivor", "twitch");
        var folded = await rig.AccountAsync("folded", "discord");
        var id = await rig.ConfirmedAsync(survivor, folded, rig.Weak(survivor, "twitch"), rig.Weak(folded, "discord"));
        await rig.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<BifrostDbContext>();
            (await db.UserAccounts.SingleAsync(x => x.Id == survivor.Id)).HeimdallAccountId = HeimdallShapes.NewAccountId(); // Bifrost's own link drifted
            await db.SaveChangesAsync();
            return 0;
        });

        var stuck = await rig.AdvanceAsync(id);
        var again = await rig.AdvanceAsync(id);

        Assert.Equal(AccountMergeState.Confirmed, stuck.State);
        Assert.Equal(AccountMergeSaga.ReceiptMismatch, stuck.LastOutcome);
        // The answer stays what Heimdall gave: a later sweep does not turn it into an hourly "proofs unreadable" retry.
        Assert.Equal(AccountMergeSaga.ReceiptMismatch, again.LastOutcome);
        Assert.Null(again.NextAttemptUtc);
        Assert.Null((await rig.MergeAsync(id)).SealedProofs);
        Assert.Single(rig.Moves.Requests);
        Assert.IsType<MergeStanding.Frozen>(await rig.StandingAsync(folded));
    }

    // ---- Heimdall down ----

    [Fact]
    public async Task While_Heimdall_is_down_the_merge_stays_confirmed_and_the_folded_account_frozen_then_commits_when_it_answers()
    {
        using var rig = new Rig();
        var survivor = await rig.AccountAsync("survivor", "twitch");
        var folded = await rig.AccountAsync("folded", "discord");
        var id = await rig.ConfirmedAsync(survivor, folded, rig.Strong(survivor), rig.Strong(folded)); // strong proofs outlive the outage below
        rig.Moves.MoveDown = new HttpRequestException("down");

        Assert.Equal(1, await rig.AdvanceDueAsync());
        Assert.Equal(0, await rig.AdvanceDueAsync()); // backed off 15 s
        rig.Clock.Advance(TimeSpan.FromSeconds(16));
        Assert.Equal(1, await rig.AdvanceDueAsync());
        rig.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(1, await rig.AdvanceDueAsync());

        var down = await rig.MergeAsync(id);
        Assert.Equal(AccountMergeState.Confirmed, down.State);
        Assert.Equal(3, down.Attempts);
        Assert.Equal(rig.Clock.Now + TimeSpan.FromMinutes(5), down.NextAttemptUtc);
        Assert.IsType<MergeStanding.Frozen>(await rig.StandingAsync(folded));
        Assert.Empty(rig.Moves.Stored);

        rig.Moves.MoveDown = null;
        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, await rig.AdvanceDueAsync());

        Assert.Equal(AccountMergeState.Committed, (await rig.MergeAsync(id)).State);
    }

    // ---- undo ----

    [Fact]
    public async Task Undo_with_survivor_proofs_of_the_moved_logins_reverses_the_move_and_ends_Undone()
    {
        using var rig = new Rig();
        var survivor = await rig.AccountAsync("survivor", "twitch");
        var folded = await rig.AccountAsync("folded", "discord", "patreon");
        var id = await rig.ConfirmedAsync(survivor, folded, rig.Weak(survivor, "twitch"), rig.Weak(folded, "discord"), rig.Weak(folded, "patreon"));
        await rig.AdvanceAsync(id);
        var forward = (await rig.MergeAsync(id)).HeimdallMoveId;

        Assert.IsType<UndoOutcome.Requested>(await RequestUndoAsync(rig, id, survivor, rig.Moved(survivor, folded, "discord"), rig.Moved(survivor, folded, "patreon")));
        var requested = await rig.MergeAsync(id);
        Assert.Equal(AccountMergeState.UndoRequested, requested.State);
        Assert.Matches("^[0-9a-f]{32}$", requested.HeimdallReverseMoveId!);
        Assert.NotEqual(forward, requested.HeimdallReverseMoveId);
        Assert.IsType<MergeStanding.Folded>(await rig.StandingAsync(folded));

        var done = await rig.AdvanceAsync(id);

        Assert.Equal(AccountMergeState.Undone, done.State);
        Assert.NotNull((await rig.MergeAsync(id)).ReversedUtc);
        Assert.Equal(2, rig.Moves.Stored.Count);
        var reversal = Assert.Single(rig.Moves.Stored, x => x.ReversesMoveId is not null);
        Assert.Equal(forward, reversal.ReversesMoveId);
        Assert.Equal(requested.HeimdallReverseMoveId, reversal.MoveId);
        Assert.Equal(["discord", "patreon"], rig.Moves.Logins[folded.HeimdallId].Select(x => x.Provider).Order());
        Assert.Null(await rig.StandingAsync(folded));
    }

    [Fact]
    public async Task A_refused_undo_returns_the_merge_to_Committed_with_the_proofs_wiped_and_the_sweep_finalizes_it_after_the_grace()
    {
        using var rig = new Rig();
        var survivor = await rig.AccountAsync("survivor", "twitch");
        var folded = await rig.AccountAsync("folded", "discord");
        var id = await rig.ConfirmedAsync(survivor, folded, rig.Weak(survivor, "twitch"), rig.Weak(folded, "discord"));
        var committed = await rig.AdvanceAsync(id);
        rig.Moves.Logins[survivor.HeimdallId].RemoveAll(x => x.Provider == "discord"); // the survivor unlinked a moved login

        Assert.IsType<UndoOutcome.Requested>(await RequestUndoAsync(rig, id, survivor, rig.Weak(survivor, "twitch")));
        var back = await rig.AdvanceAsync(id);

        Assert.Equal(AccountMergeState.Committed, back.State);
        var stored = await rig.MergeAsync(id);
        Assert.Equal("undo_not_reversible", stored.LastOutcome);
        Assert.Null(stored.SealedProofs);
        Assert.Equal(committed.GraceEndsUtc, stored.GraceEndsUtc);
        Assert.IsType<MergeStanding.Folded>(await rig.StandingAsync(folded));

        rig.Clock.Advance(AccountMergeLedger.Grace + TimeSpan.FromMinutes(1));
        await rig.InScopeAsync(async sp =>
        {
            await sp.GetRequiredService<AccountMergeSaga>().AdvanceDueAsync(rig.Clock.Now, default);
            return await sp.GetRequiredService<AccountMergeLedger>().FinalizeDueAsync(rig.Clock.Now, default);
        });
        Assert.Equal(AccountMergeState.Final, (await rig.MergeAsync(id)).State);
    }

    [Fact]
    public async Task Undo_is_requested_only_inside_the_grace_only_by_the_survivor_and_only_from_Committed()
    {
        using var rig = new Rig();
        var survivor = await rig.AccountAsync("survivor", "twitch");
        var folded = await rig.AccountAsync("folded", "discord");
        var id = await rig.ConfirmedAsync(survivor, folded, rig.Weak(survivor, "twitch"), rig.Weak(folded, "discord"));
        Assert.IsType<UndoOutcome.NotCommitted>(await RequestUndoAsync(rig, id, survivor, rig.Weak(survivor, "twitch")));
        Assert.IsType<UndoOutcome.NotCommitted>(await RequestUndoAsync(rig, Guid.NewGuid(), survivor, rig.Weak(survivor, "twitch")));
        await rig.AdvanceAsync(id);

        Assert.IsType<UndoOutcome.NotSurvivor>(await RequestUndoAsync(rig, id, folded, rig.Weak(folded, "discord")));
        Assert.IsType<UndoOutcome.ProofAccountMismatch>(await RequestUndoAsync(rig, id, survivor, rig.Weak(folded, "discord")));
        Assert.Equal(AccountMergeState.Committed, (await rig.MergeAsync(id)).State);

        var proof = rig.Weak(survivor, "twitch");
        rig.Clock.Advance(AccountMergeLedger.Grace + TimeSpan.FromSeconds(1));
        Assert.IsType<UndoOutcome.GraceOver>(await RequestUndoAsync(rig, id, survivor, proof)); // Committed, but the grace ended
        await rig.InScopeAsync(sp => sp.GetRequiredService<AccountMergeLedger>().FinalizeDueAsync(rig.Clock.Now, default));
        Assert.IsType<UndoOutcome.GraceOver>(await RequestUndoAsync(rig, id, survivor, proof)); // Final
        Assert.Equal(AccountMergeState.Final, (await rig.MergeAsync(id)).State);
    }

    [Fact]
    public async Task A_suspended_folded_account_into_a_memberless_survivor_confirms_on_the_digest_the_member_saw_and_commits()
    {
        using var rig = new Rig();
        var survivor = await rig.AccountAsync("survivor", "twitch");
        var folded = await rig.AccountAsync("folded", "discord");
        await rig.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<BifrostDbContext>();
            db.Memberships.Remove(await db.Memberships.SingleAsync(x => x.UserAccountId == survivor.Id)); // the chat tier holds no membership
            (await db.Memberships.SingleAsync(x => x.UserAccountId == folded.Id)).Status = MembershipStatus.Suspended;
            await db.SaveChangesAsync();
            return 0;
        });

        // The member is shown a plan and confirms it: the same unchanged world must show the same digest.
        Assert.Equal(await rig.DigestAsync(survivor, folded), await rig.DigestAsync(survivor, folded));
        var id = await rig.ConfirmedAsync(survivor, folded, rig.Weak(survivor, "twitch"), rig.Weak(folded, "discord"));

        Assert.Equal(AccountMergeState.Committed, (await rig.AdvanceAsync(id)).State);
        var carried = await rig.InScopeAsync(sp => sp.GetRequiredService<BifrostDbContext>().Memberships.SingleAsync(x => x.UserAccountId == survivor.Id));
        Assert.Equal(MembershipStatus.Suspended, carried.Status);
    }

    [Fact]
    public async Task Undo_is_refused_at_the_exact_instant_the_grace_ends_and_allowed_a_second_before()
    {
        using var rig = new Rig();
        var survivor = await rig.AccountAsync("survivor", "twitch");
        var folded = await rig.AccountAsync("folded", "discord");
        var id = await rig.ConfirmedAsync(survivor, folded, rig.Weak(survivor, "twitch"), rig.Weak(folded, "discord"));
        await rig.AdvanceAsync(id);
        var ends = (await rig.MergeAsync(id)).GraceEndsUtc!.Value;

        rig.Clock.Advance(ends - rig.Clock.Now);
        Assert.IsType<UndoOutcome.GraceOver>(await RequestUndoAsync(rig, id, survivor, rig.Weak(survivor, "twitch")));
        rig.Clock.Advance(TimeSpan.FromSeconds(-1));
        Assert.IsType<UndoOutcome.Requested>(await RequestUndoAsync(rig, id, survivor, rig.Weak(survivor, "twitch")));
    }

    [Fact]
    public void The_sealed_proofs_record_prints_no_bearer()
    {
        var seal = typeof(AccountMergeSaga).GetNestedType("ProofSeal", System.Reflection.BindingFlags.NonPublic)!;
        var instance = Activator.CreateInstance(seal, new List<string> { "bearer-folded-secret" }, "bearer-survivor-secret")!;

        var text = instance.ToString()!;

        Assert.DoesNotContain("secret", text);
    }

    [Fact]
    public async Task An_unknown_answer_to_the_reversal_is_replayed_under_the_same_reverse_id_even_past_the_grace()
    {
        using var rig = new Rig();
        var survivor = await rig.AccountAsync("survivor", "twitch");
        var folded = await rig.AccountAsync("folded", "discord");
        var id = await rig.ConfirmedAsync(survivor, folded, rig.Weak(survivor, "twitch"), rig.Weak(folded, "discord"));
        await rig.AdvanceAsync(id);
        Assert.IsType<UndoOutcome.Requested>(await RequestUndoAsync(rig, id, survivor, rig.Moved(survivor, folded, "discord")));
        rig.Moves.MoveApplyThenFail = HttpStatusCode.BadGateway;

        Assert.Equal(AccountMergeState.UndoRequested, (await rig.AdvanceAsync(id)).State);
        rig.Moves.MoveApplyThenFail = null;
        rig.Clock.Advance(AccountMergeLedger.Grace + TimeSpan.FromDays(1));
        await rig.AdvanceDueAsync();

        Assert.Equal(AccountMergeState.Undone, (await rig.MergeAsync(id)).State);
        var seen = (await rig.SeenMoveIdsAsync()).Skip(1).ToArray();
        Assert.Equal(2, seen.Length);
        Assert.Single(seen.Distinct());
        Assert.Equal(2, rig.Moves.Stored.Count);
    }

    // ---- the wiring ----

    [Fact]
    public void The_host_registers_exactly_one_merge_loop_in_production_and_the_saga_beside_the_ledger()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Bifrost.Web", "Program.cs"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(program, @"AddHostedService<.*Merge"));
        Assert.Contains("AddScoped<Bifrost.Web.Features.Membership.AccountMergeSaga>", program);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bifrost.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
