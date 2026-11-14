using System.Net;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Bifrost.Web.Tests.AccountMergeSagaTests;
using static Bifrost.Web.Tests.AccountMergeSettleTests;

namespace Bifrost.Web.Tests;

/// <summary>The operator's page for stuck merges, from browser sessions of the real host: admin only, the stored move id shown as the confirmation, results by closed code.</summary>
public sealed class AccountMergeSettlePageTests
{
    private const string Url = "/App/Merges";

    private sealed class World : IDisposable
    {
        public Rig Rig { get; } = new(realSessions: true);

        public Party Admin { get; private set; } = null!;

        public string AdminSession { get; private set; } = string.Empty;

        public string MemberSession { get; private set; } = string.Empty;

        public Stuck Forward { get; private set; } = null!;

        public static async Task<World> Make()
        {
            var world = new World();
            var rig = world.Rig;
            world.Admin = await rig.AccountAsync("operator", "github");
            var member = await rig.AccountAsync("member", "github");
            await rig.InScopeAsync(async sp =>
            {
                var db = sp.GetRequiredService<BifrostDbContext>();
                var membership = await db.Memberships.SingleAsync(x => x.UserAccountId == world.Admin.Id);
                membership.IsPlatformAdmin = true;
                await db.SaveChangesAsync();
                return 0;
            });
            world.AdminSession = await rig.Host.SignInSessionAsync(world.Admin.HeimdallId);
            world.MemberSession = await rig.Host.SignInSessionAsync(member.HeimdallId);
            world.Forward = await ConfirmedStuckAsync(rig, AccountMergeSaga.ReceiptMismatch);
            return world;
        }

        public void Dispose() => Rig.Dispose();

        public Task<(HttpResponseMessage Response, string Html)> Get(string session, string query = "") => MergePage.GetAsync(Rig.Host, session, query, Url);

        public async Task<HttpResponseMessage> Post(string session, string handler, string mergeId, string moveId, MergePage.Antiforgery? token = null) =>
            await MergePage.PostAsync(Rig.Host, session, token ?? await MergePage.TokenAsync(Rig.Host, session, HeimdallHost.LinkedAccountsUrl), handler,
                new Dictionary<string, string> { ["mergeId"] = mergeId, ["moveId"] = moveId }, Url);
    }

    private static bool Refused(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.Forbidden || (response.StatusCode == HttpStatusCode.Redirect && (response.Headers.Location?.OriginalString.Contains("/Membership/Status?ReturnUrl=", StringComparison.Ordinal) ?? false));

    [Fact]
    public async Task A_member_who_is_not_a_platform_admin_is_refused_on_the_listing_and_on_both_posts_and_nothing_changes()
    {
        using var w = await World.Make();
        var merge = await w.Rig.MergeAsync(w.Forward.Id);
        var requests = w.Rig.Moves.Requests.Count;

        var (get, html) = await w.Get(w.MemberSession);
        Assert.True(Refused(get), $"listing gave {(int)get.StatusCode} {MergePage.Location(get)}");
        Assert.DoesNotContain(merge.HeimdallMoveId!, html);
        Assert.True(Refused(await w.Post(w.MemberSession, "FailForward", w.Forward.Id.ToString(), merge.HeimdallMoveId!)));
        Assert.True(Refused(await w.Post(w.MemberSession, "AbandonUndo", w.Forward.Id.ToString(), merge.HeimdallMoveId!)));

        var after = await w.Rig.MergeAsync(w.Forward.Id);
        Assert.Equal(AccountMergeState.Confirmed, after.State);
        Assert.Equal(AccountMergeSaga.ReceiptMismatch, after.LastOutcome);
        Assert.Equal(requests, w.Rig.Moves.Requests.Count);
    }

    [Fact]
    public async Task The_listing_shows_stuck_merges_with_their_stored_move_id_and_no_Heimdall_account_or_provider_user_id()
    {
        using var w = await World.Make();
        var stuck = w.Forward;
        var merge = await w.Rig.MergeAsync(stuck.Id);
        // Heimdall unreachable for a long time: listed, though only the three stuck outcomes can be ended.
        var slow = await ConfirmedStuckAsync(w.Rig, AccountMergeSaga.MoveIdConflict);
        var slowMerge = await w.Rig.MergeAsync(slow.Id);
        var waiting = await ConfirmedStuckAsync(w.Rig, "transport");
        await EditMerge(w.Rig, waiting.Id, x => x.Attempts = 8);
        var healthy = await ConfirmedStuckAsync(w.Rig, "confirmed"); // one waiting for its first replay: not stuck
        await EditMerge(w.Rig, healthy.Id, x => x.Attempts = 1);
        var undo = await UndoStuckAsync(w.Rig);
        var undoMerge = await w.Rig.MergeAsync(undo.Id);

        var (response, html) = await w.Get(w.AdminSession);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(stuck.Id.ToString(), html);
        Assert.Contains(merge.HeimdallMoveId!, html);
        Assert.Contains(slow.Id.ToString(), html);
        Assert.Contains(slowMerge.HeimdallMoveId!, html);
        Assert.Contains(waiting.Id.ToString(), html);
        Assert.Contains(undo.Id.ToString(), html);
        Assert.Contains(undoMerge.HeimdallReverseMoveId!, html);
        Assert.DoesNotContain(undoMerge.HeimdallMoveId!, html); // the leg in flight is the reversal
        Assert.DoesNotContain(healthy.Id.ToString(), html);
        foreach (var party in new[] { stuck.Survivor, stuck.Folded, slow.Survivor, slow.Folded, undo.Survivor, undo.Folded })
        {
            Assert.DoesNotContain(party.HeimdallId, html);
            Assert.DoesNotContain(party.Id.ToString(), html);
            Assert.DoesNotContain(party.Logins.Values.First(), html);
        }
    }

    [Fact]
    public async Task A_platform_admin_ends_a_stuck_leg_by_typing_its_move_id_and_a_wrong_one_changes_nothing()
    {
        using var w = await World.Make();
        var merge = await w.Rig.MergeAsync(w.Forward.Id);
        var token = await MergePage.TokenAsync(w.Rig.Host, w.AdminSession, Url);
        var requests = w.Rig.Moves.Requests.Count;

        var wrong = await w.Post(w.AdminSession, "FailForward", w.Forward.Id.ToString(), "0123456789abcdef0123456789abcdef", token);
        Assert.Equal(Url + "?result=wrong_move_id", MergePage.Location(wrong));
        Assert.Equal(AccountMergeState.Confirmed, (await w.Rig.MergeAsync(w.Forward.Id)).State);

        var otherLeg = await w.Post(w.AdminSession, "AbandonUndo", w.Forward.Id.ToString(), merge.HeimdallMoveId!, token);
        Assert.Equal(Url + "?result=not_stuck", MergePage.Location(otherLeg));

        var done = await w.Post(w.AdminSession, "FailForward", w.Forward.Id.ToString(), merge.HeimdallMoveId!, token);
        Assert.Equal(Url + "?result=settled", MergePage.Location(done));
        var after = await w.Rig.MergeAsync(w.Forward.Id);
        Assert.Equal(AccountMergeState.Failed, after.State);
        await w.Rig.InScopeAsync(async sp =>
        {
            var audit = await sp.GetRequiredService<BifrostDbContext>().AuditEvents.AsNoTracking().SingleAsync(x => x.EntityId == w.Forward.Id.ToString());
            Assert.Equal(w.Admin.Id, audit.ActorUserAccountId);
            return 0;
        });
        Assert.Equal(requests, w.Rig.Moves.Requests.Count); // the page calls no Heimdall

        var (_, html) = await w.Get(w.AdminSession, "?result=settled");
        Assert.DoesNotContain(w.Forward.Id.ToString(), html); // no longer stuck, so no longer listed
    }

    [Fact]
    public async Task A_platform_admin_gives_up_a_stuck_undo_by_typing_the_reverse_move_id()
    {
        using var w = await World.Make();
        var undo = await UndoStuckAsync(w.Rig);
        var merge = await w.Rig.MergeAsync(undo.Id);

        var done = await w.Post(w.AdminSession, "AbandonUndo", undo.Id.ToString(), merge.HeimdallReverseMoveId!, await MergePage.TokenAsync(w.Rig.Host, w.AdminSession, Url));

        Assert.Equal(Url + "?result=settled", MergePage.Location(done));
        Assert.Equal(AccountMergeState.Committed, (await w.Rig.MergeAsync(undo.Id)).State);
    }
}
