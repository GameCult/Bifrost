using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Heimdall;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Bifrost.Web.Tests.AccountMergeSagaTests;

namespace Bifrost.Web.Tests;

/// <summary>
/// The operator's settle of a stuck merge (ruling merge-stuck-end-only-typed-id): two ends, FailForward and AbandonUndo, only from a
/// stuck leg and only with the leg's stored move id typed back, one audit row, never an adopt. The saga rig of AccountMergeSagaTests.
/// </summary>
public sealed class AccountMergeSettleTests
{
    private static readonly Guid Operator = Guid.NewGuid();

    internal sealed record Stuck(Guid Id, Party Survivor, Party Folded);

    /// <summary>A Confirmed merge that is stuck on <paramref name="outcome"/>: the two the saga records itself are reached through the real step, the third is the stored Unknown reason.</summary>
    internal static async Task<Stuck> ConfirmedStuckAsync(Rig rig, string outcome)
    {
        var survivor = await rig.AccountAsync("survivor", "twitch");
        var folded = await rig.AccountAsync("folded", "discord");
        var id = await rig.ConfirmedAsync(survivor, folded, rig.Weak(survivor, "twitch"), rig.Weak(folded, "discord"));
        switch (outcome)
        {
            case AccountMergeSaga.ReceiptMismatch:
                await Edit(rig, x => x.UserAccounts.Single(a => a.Id == survivor.Id).HeimdallAccountId = HeimdallShapes.NewAccountId());
                await rig.AdvanceAsync(id);
                break;
            case AccountMergeSaga.ProofsUnreadable:
                await EditMerge(rig, id, x => x.SealedProofs = "CfDJ8-not-a-protected-payload");
                await rig.AdvanceAsync(id);
                break;
            default:
                await EditMerge(rig, id, x => { x.LastOutcome = outcome; x.Attempts = 3; x.NextAttemptUtc = rig.Clock.Now.AddMinutes(5); });
                break;
        }

        Assert.Equal(outcome, (await rig.MergeAsync(id)).LastOutcome);
        return new Stuck(id, survivor, folded);
    }

    /// <summary>A merge in UndoRequested whose reversal got a receipt for another account.</summary>
    internal static async Task<Stuck> UndoStuckAsync(Rig rig)
    {
        var survivor = await rig.AccountAsync("survivor", "twitch");
        var folded = await rig.AccountAsync("folded", "discord");
        var id = await rig.ConfirmedAsync(survivor, folded, rig.Weak(survivor, "twitch"), rig.Weak(folded, "discord"));
        await rig.AdvanceAsync(id);
        var undo = await rig.InScopeAsync(sp => sp.GetRequiredService<AccountMergeSaga>().RequestUndoAsync(id, survivor.Id, [rig.Moved(survivor, folded, "discord")], default));
        Assert.IsType<UndoOutcome.Requested>(undo);
        await Edit(rig, x => x.UserAccounts.Single(a => a.Id == folded.Id).HeimdallAccountId = HeimdallShapes.NewAccountId());
        await rig.AdvanceAsync(id);
        var stuck = await rig.MergeAsync(id);
        Assert.Equal(AccountMergeState.UndoRequested, stuck.State);
        Assert.Equal(AccountMergeSaga.ReceiptMismatch, stuck.LastOutcome);
        return new Stuck(id, survivor, folded);
    }

    private static Task Edit(Rig rig, Action<BifrostDbContext> change) => rig.InScopeAsync(async sp =>
    {
        var db = sp.GetRequiredService<BifrostDbContext>();
        change(db);
        await db.SaveChangesAsync();
        return 0;
    });

    internal static Task EditMerge(Rig rig, Guid id, Action<AccountMerge> change) => Edit(rig, db => change(db.AccountMerges.Single(x => x.Id == id)));

    private static Task<SettleOutcome> SettleAsync(Rig rig, Guid id, SettleChoice choice, string? typed) =>
        rig.InScopeAsync(sp => sp.GetRequiredService<AccountMergeSaga>().SettleStuckAsync(id, choice, typed!, Operator, default));

    private static Task<List<AuditEvent>> AuditAsync(Rig rig, Guid id) =>
        rig.InScopeAsync(sp => sp.GetRequiredService<BifrostDbContext>().AuditEvents.AsNoTracking().Where(x => x.EntityId == id.ToString()).ToListAsync());

    // ---- FailForward ----

    [Theory]
    [InlineData("receipt_mismatch")]
    [InlineData("proofs_unreadable")]
    [InlineData("move_id_conflict")]
    public async Task FailForward_ends_a_stuck_Confirmed_merge_as_Failed_with_the_proofs_wiped_the_folded_account_free_and_one_audit_row(string outcome)
    {
        using var rig = new Rig();
        var stuck = await ConfirmedStuckAsync(rig, outcome);
        Assert.IsType<MergeStanding.Frozen>(await rig.StandingAsync(stuck.Folded));
        var moveId = (await rig.MergeAsync(stuck.Id)).HeimdallMoveId!;

        Assert.IsType<SettleOutcome.Settled>(await SettleAsync(rig, stuck.Id, SettleChoice.FailForward, moveId));

        var merge = await rig.MergeAsync(stuck.Id);
        Assert.Equal(AccountMergeState.Failed, merge.State);
        Assert.Equal(AccountMergeSaga.OperatorFailed, merge.FailureCode);
        Assert.Equal(rig.Clock.Now, merge.FailedUtc);
        Assert.Null(merge.SealedProofs);
        Assert.Null(merge.NextAttemptUtc);
        Assert.Null(await rig.StandingAsync(stuck.Folded));
        Assert.Null(await rig.StandingAsync(stuck.Survivor));
        Assert.Empty(await rig.JournalAsync(stuck.Id));
        Assert.Equal(0, await rig.AdvanceDueAsync()); // the sweeper does not pick a settled merge up again
        var audit = Assert.Single(await AuditAsync(rig, stuck.Id));
        Assert.Equal("merge.settled", audit.Action);
        Assert.Equal(Operator, audit.ActorUserAccountId);
        Assert.Contains("FailForward", audit.Detail);
        // The row names the choice and the merge: nothing of either side.
        Assert.DoesNotContain(stuck.Survivor.HeimdallId, audit.Detail);
        Assert.DoesNotContain(stuck.Folded.HeimdallId, audit.Detail);
        Assert.DoesNotContain(moveId, audit.Detail);
        Assert.DoesNotContain(stuck.Survivor.Id.ToString(), audit.Detail);
        Assert.DoesNotContain(stuck.Folded.Id.ToString(), audit.Detail);
    }

    [Fact]
    public async Task A_settled_merge_can_be_confirmed_again_for_the_same_pair()
    {
        using var rig = new Rig();
        var stuck = await ConfirmedStuckAsync(rig, AccountMergeSaga.ReceiptMismatch);
        await EditHeimdallBack(rig, stuck);
        Assert.IsType<SettleOutcome.Settled>(await SettleAsync(rig, stuck.Id, SettleChoice.FailForward, (await rig.MergeAsync(stuck.Id)).HeimdallMoveId));

        Assert.IsType<ConfirmOutcome.Confirmed>(await rig.ConfirmAsync(stuck.Survivor, stuck.Folded, rig.Weak(stuck.Survivor, "twitch"), rig.Weak(stuck.Folded, "discord")));
    }

    private static Task EditHeimdallBack(Rig rig, Stuck stuck) =>
        Edit(rig, db => db.UserAccounts.Single(x => x.Id == stuck.Survivor.Id).HeimdallAccountId = stuck.Survivor.HeimdallId);

    // ---- AbandonUndo ----

    [Fact]
    public async Task AbandonUndo_returns_a_stuck_UndoRequested_merge_to_Committed_with_the_grace_the_proofs_wiped_and_one_audit_row()
    {
        using var rig = new Rig();
        var stuck = await UndoStuckAsync(rig);
        var before = await rig.MergeAsync(stuck.Id);

        Assert.IsType<SettleOutcome.Settled>(await SettleAsync(rig, stuck.Id, SettleChoice.AbandonUndo, before.HeimdallReverseMoveId));

        var merge = await rig.MergeAsync(stuck.Id);
        Assert.Equal(AccountMergeState.Committed, merge.State);
        Assert.Equal(AccountMergeSaga.UndoOperatorAbandoned, merge.LastOutcome);
        Assert.Null(merge.SealedProofs);
        Assert.Null(merge.NextAttemptUtc);
        Assert.Equal(before.CommittedUtc, merge.CommittedUtc);
        Assert.Equal(before.GraceEndsUtc, merge.GraceEndsUtc);
        Assert.IsType<MergeStanding.Folded>(await rig.StandingAsync(stuck.Folded));
        Assert.Equal(0, await rig.AdvanceDueAsync());
        var audit = Assert.Single(await AuditAsync(rig, stuck.Id));
        Assert.Equal("merge.settled", audit.Action);
        Assert.Contains("AbandonUndo", audit.Detail);
        Assert.DoesNotContain(before.HeimdallReverseMoveId!, audit.Detail);

        rig.Clock.Advance(AccountMergeLedger.Grace + TimeSpan.FromMinutes(1));
        await rig.InScopeAsync(sp => sp.GetRequiredService<AccountMergeLedger>().FinalizeDueAsync(rig.Clock.Now, default));
        Assert.Equal(AccountMergeState.Final, (await rig.MergeAsync(stuck.Id)).State);
    }

    // ---- refusals write nothing ----

    [Fact]
    public async Task A_merge_in_any_other_state_or_with_another_LastOutcome_is_NotStuck_and_nothing_is_written()
    {
        using var rig = new Rig();
        var survivor = await rig.AccountAsync("survivor", "twitch");
        var folded = await rig.AccountAsync("folded", "discord");
        var id = await rig.ConfirmedAsync(survivor, folded, rig.Weak(survivor, "twitch"), rig.Weak(folded, "discord"));
        var moveId = (await rig.MergeAsync(id)).HeimdallMoveId!;

        // Confirmed and healthy ("confirmed"), then waiting on Heimdall ("transport"): not stuck.
        Assert.IsType<SettleOutcome.NotStuck>(await SettleAsync(rig, id, SettleChoice.FailForward, moveId));
        await EditMerge(rig, id, x => { x.LastOutcome = "transport"; x.Attempts = 9; });
        Assert.IsType<SettleOutcome.NotStuck>(await SettleAsync(rig, id, SettleChoice.FailForward, moveId));
        var snapshot = await rig.MergeAsync(id);
        Assert.Equal(AccountMergeState.Confirmed, snapshot.State);
        Assert.NotNull(snapshot.SealedProofs);

        // A stuck outcome does not make the other leg's choice valid.
        await EditMerge(rig, id, x => x.LastOutcome = AccountMergeSaga.ReceiptMismatch);
        Assert.IsType<SettleOutcome.NotStuck>(await SettleAsync(rig, id, SettleChoice.AbandonUndo, moveId));

        // Heimdall answered Moved: HeimdallMoved and Committed are never settleable, whatever LastOutcome says.
        foreach (var state in new[] { AccountMergeState.HeimdallMoved, AccountMergeState.Committed, AccountMergeState.HeimdallReversed, AccountMergeState.Undone, AccountMergeState.Final, AccountMergeState.Failed })
        {
            await EditMerge(rig, id, x => { x.State = state; x.LastOutcome = AccountMergeSaga.ReceiptMismatch; });
            Assert.IsType<SettleOutcome.NotStuck>(await SettleAsync(rig, id, SettleChoice.FailForward, moveId));
            Assert.IsType<SettleOutcome.NotStuck>(await SettleAsync(rig, id, SettleChoice.AbandonUndo, moveId));
            Assert.Equal(state, (await rig.MergeAsync(id)).State);
        }

        Assert.IsType<SettleOutcome.NotStuck>(await SettleAsync(rig, Guid.NewGuid(), SettleChoice.FailForward, moveId));
        Assert.Empty(await AuditAsync(rig, id));
    }

    [Fact]
    public async Task A_move_id_that_is_not_the_stored_one_of_the_leg_is_WrongMoveId_and_nothing_is_written()
    {
        using var rig = new Rig();
        var stuck = await ConfirmedStuckAsync(rig, AccountMergeSaga.ReceiptMismatch);
        var merge = await rig.MergeAsync(stuck.Id);

        foreach (var typed in new[] { "", " ", merge.HeimdallMoveId!.ToUpperInvariant(), merge.HeimdallMoveId + " ", merge.HeimdallMoveId![..31], stuck.Id.ToString("N"), Guid.NewGuid().ToString("N") })
        {
            Assert.IsType<SettleOutcome.WrongMoveId>(await SettleAsync(rig, stuck.Id, SettleChoice.FailForward, typed));
        }

        Assert.IsType<SettleOutcome.WrongMoveId>(await SettleAsync(rig, stuck.Id, SettleChoice.FailForward, null));
        var after = await rig.MergeAsync(stuck.Id);
        Assert.Equal(AccountMergeState.Confirmed, after.State);
        Assert.Equal(AccountMergeSaga.ReceiptMismatch, after.LastOutcome);
        Assert.Empty(await AuditAsync(rig, stuck.Id));
    }

    [Fact]
    public async Task The_undo_leg_is_confirmed_by_the_reverse_move_id_not_the_forward_one()
    {
        using var rig = new Rig();
        var stuck = await UndoStuckAsync(rig);
        var merge = await rig.MergeAsync(stuck.Id);

        Assert.IsType<SettleOutcome.WrongMoveId>(await SettleAsync(rig, stuck.Id, SettleChoice.AbandonUndo, merge.HeimdallMoveId));

        Assert.Equal(AccountMergeState.UndoRequested, (await rig.MergeAsync(stuck.Id)).State);
        Assert.Empty(await AuditAsync(rig, stuck.Id));
    }

    // ---- the race ----

    [Fact]
    public async Task A_settle_that_races_AdvanceAsync_and_loses_on_the_State_token_changes_nothing()
    {
        using var rig = new Rig();
        // Heimdall answered move_id_conflict, so the sweeper keeps replaying: the day its proofs verify, the replay lands.
        var stuck = await ConfirmedStuckAsync(rig, AccountMergeSaga.MoveIdConflict);
        await EditMerge(rig, stuck.Id, x => x.NextAttemptUtc = null);
        var moveId = (await rig.MergeAsync(stuck.Id)).HeimdallMoveId!;
        var raced = false;
        rig.Host.Fault.When = db =>
        {
            if (raced || !db.ChangeTracker.Entries<AccountMerge>().Any(x => x.Entity.State == AccountMergeState.Failed))
            {
                return false;
            }

            raced = true; // the settle has staged Failed and is about to save: the step function moves and commits first
            Task.Run(() => rig.AdvanceAsync(stuck.Id)).GetAwaiter().GetResult();
            return false;
        };

        var outcome = await SettleAsync(rig, stuck.Id, SettleChoice.FailForward, moveId);
        rig.Host.Fault.When = null;

        Assert.True(raced);
        Assert.IsType<SettleOutcome.Lost>(outcome);
        Assert.Equal(AccountMergeState.Committed, (await rig.MergeAsync(stuck.Id)).State);
        Assert.Empty((await rig.MergeAsync(stuck.Id)).FailureCode);
        // The audit row rides in the same SaveChanges as the transition, so a real database drops both; the in-memory provider
        // applies entries one by one and cannot show that half.
        Assert.IsType<MergeStanding.Folded>(await rig.StandingAsync(stuck.Folded));
    }

    // ---- proof bearers are swept by the existing tick ----

    [Fact]
    public async Task The_sweeper_tick_drops_a_bearer_past_its_lifetime_with_no_page_access()
    {
        using var rig = new Rig();
        var proofs = rig.Host.Services.GetRequiredService<MergeProofs>();
        var actor = Guid.NewGuid();
        var proof = new MergeProof("BEARER-CANARY", HeimdallShapes.NewAccountId(), "discord");
        Assert.Equal(PutOutcome.Stored, proofs.Put(actor, MergeSide.Self, proof, Guid.NewGuid(), []));
        rig.Clock.Advance(MergeProofs.ProofLifetime + AccountMergeSweeper.Tick);
        Assert.Equal(1, HeldActors(proofs)); // nobody has touched it since

        var sweeper = new AccountMergeSweeper(rig.Host.Services.GetRequiredService<IServiceScopeFactory>(), rig.Clock, NullLogger<AccountMergeSweeper>.Instance);
        await sweeper.StartAsync(default);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && HeldActors(proofs) > 0)
        {
            await Task.Delay(50);
        }

        await sweeper.StopAsync(default);
        Assert.Equal(0, HeldActors(proofs));
    }

    private static int HeldActors(MergeProofs proofs) =>
        ((System.Collections.IDictionary)typeof(MergeProofs).GetField("_held", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(proofs)!).Count;
}
