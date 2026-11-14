using System.Net;
using System.Text.RegularExpressions;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Bifrost.Web.Tests.AccountMergeSagaTests;

namespace Bifrost.Web.Tests;

/// <summary>
/// What the merge pages show each member, from browser sessions of the real host over the saga rig: the notice in the layout and the
/// page itself, for the survivor inside the grace, for a frozen or folded account, and for everyone else (nothing).
/// </summary>
public sealed class MergeStatusTests
{
    private static string Enc(string text) => System.Text.Encodings.Web.HtmlEncoder.Default.Encode(text);

    private static int Notices(string html) => Regex.Matches(html, "class=\"merge-notice\"").Count;

    private sealed record World(Rig Rig, Party Survivor, Party Folded, Party Stranger, string SurvivorSession, string FoldedSession, string StrangerSession, Guid MergeId);

    private static async Task<World> Open(string[]? foldedLogins = null)
    {
        var rig = new Rig(realSessions: true);
        var survivor = await rig.AccountAsync("quill-9f3a", "twitch");
        var folded = await rig.AccountAsync("banks-2d7c", foldedLogins ?? ["discord"]);
        var stranger = await rig.AccountAsync("stranger-51e0", "twitch");
        // Sessions exist before the merge, as a member's browser does.
        var sessions = new[] { survivor, folded, stranger };
        var cookies = new List<string>();
        foreach (var who in sessions)
        {
            cookies.Add(await rig.Host.SignInSessionAsync(who.HeimdallId));
        }

        var id = await rig.ConfirmedAsync(survivor, folded, rig.Weak(survivor, "twitch"), [.. (foldedLogins ?? ["discord"]).Select(x => rig.Weak(folded, x))]);
        return new World(rig, survivor, folded, stranger, cookies[0], cookies[1], cookies[2], id);
    }

    private static async Task<World> Committed(string[]? foldedLogins = null)
    {
        var world = await Open(foldedLogins);
        Assert.Equal(AccountMergeState.Committed, (await world.Rig.AdvanceAsync(world.MergeId)).State);
        return world;
    }

    private static async Task<string> Html(World world, string session, string url = MergePage.Url)
    {
        using var client = world.Rig.Host.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"{Bifrost.Web.Features.Shared.BifrostCookies.Session}={session}");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task Edit(World world, Action<AccountMerge> change)
    {
        using var scope = world.Rig.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var merge = await db.AccountMerges.SingleAsync(x => x.Id == world.MergeId);
        change(merge);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task The_survivor_sees_days_left_and_the_weak_note_on_the_page_and_one_notice_in_the_layout()
    {
        var w = await Committed();
        using var rig = w.Rig;
        rig.Clock.Advance(TimeSpan.FromDays(2));
        var merge = await rig.MergeAsync(w.MergeId);

        var page = await Html(w, w.SurvivorSession);

        Assert.Contains(Enc(MergeWording.DaysLeft(5)), page);
        Assert.Contains(Enc(MergeWording.TierNote("weak", merge.GraceEndsUtc!.Value)), page);
        Assert.DoesNotContain(Enc(MergeWording.TierNote("strong", merge.GraceEndsUtc!.Value)), page);
        Assert.Contains(Enc(MergeWording.State(AccountMergeState.Committed)), page);
        Assert.Equal(1, Notices(page));
        Assert.Contains(Enc(MergeWording.SurvivorNotice(5)), page);
        Assert.Equal(1, Notices(await Html(w, w.SurvivorSession, "/Membership/Status")));
    }

    [Fact]
    public async Task A_strong_proof_merge_says_so_in_its_own_words()
    {
        var w = await Committed();
        using var rig = w.Rig;
        await Edit(w, x => x.ProofFromTier = "strong");
        var merge = await rig.MergeAsync(w.MergeId);

        var page = await Html(w, w.SurvivorSession);

        Assert.NotEqual(MergeWording.TierNote("weak", merge.GraceEndsUtc!.Value), MergeWording.TierNote("strong", merge.GraceEndsUtc!.Value));
        Assert.Contains(Enc(MergeWording.TierNote("strong", merge.GraceEndsUtc!.Value)), page);
    }

    [Fact]
    public async Task No_notice_for_a_merge_that_is_Final_Undone_or_Failed_and_none_for_another_member()
    {
        var w = await Committed();
        using var rig = w.Rig;
        Assert.Equal(1, Notices(await Html(w, w.SurvivorSession)));
        Assert.Equal(0, Notices(await Html(w, w.StrangerSession)));
        Assert.DoesNotContain(Enc(MergeWording.State(AccountMergeState.Committed)), await Html(w, w.StrangerSession));

        rig.Clock.Advance(AccountMergeLedger.Grace + TimeSpan.FromHours(1));
        await rig.InScopeAsync(sp => sp.GetRequiredService<AccountMergeLedger>().FinalizeDueAsync(rig.Clock.Now, default));
        var final = await Html(w, w.SurvivorSession);
        Assert.Equal(AccountMergeState.Final, (await rig.MergeAsync(w.MergeId)).State);
        Assert.Equal(0, Notices(final));
        Assert.Contains("/Membership/MergeStatus", await Html(w, w.SurvivorSession, "/Membership/Status"));
        Assert.DoesNotContain("/Membership/MergeStatus", await Html(w, w.StrangerSession, "/Membership/Status"));
        Assert.Contains(Enc(MergeWording.State(AccountMergeState.Final)), final);

        foreach (var state in new[] { AccountMergeState.Undone, AccountMergeState.Failed })
        {
            await Edit(w, x => { x.State = state; x.FinalizedUtc = null; });
            var page = await Html(w, w.SurvivorSession);
            Assert.Equal(0, Notices(page));
            Assert.Contains(Enc(MergeWording.State(state)), page);
        }
    }

    [Theory]
    [InlineData(AccountMergeState.Undone)]
    [InlineData(AccountMergeState.Failed)]
    [InlineData(AccountMergeState.Final)]
    public async Task A_merge_that_ended_has_no_notice_even_with_its_grace_date_still_ahead(AccountMergeState state)
    {
        var w = await Committed();
        using var rig = w.Rig;
        await Edit(w, x => x.State = state);

        var page = await Html(w, w.SurvivorSession);

        Assert.Equal(0, Notices(page));
        Assert.Contains(Enc(MergeWording.State(state)), page);
    }

    [Fact]
    public async Task A_failed_merge_says_why_in_closed_words_and_never_echoes_the_stored_text()
    {
        var w = await Committed();
        using var rig = w.Rig;
        await Edit(w, x => { x.State = AccountMergeState.Failed; x.FailureCode = "proof_stale"; x.LastOutcome = "HEIMDALL FREE TEXT"; });

        var page = await Html(w, w.SurvivorSession);

        Assert.Contains(Enc(MergeWording.Failure("proof_stale")!), page);
        Assert.Contains(Enc(MergeWording.Outcome("HEIMDALL FREE TEXT")!), page);
        Assert.DoesNotContain("HEIMDALL FREE TEXT", page);
        Assert.DoesNotContain("proof_stale", page);
    }

    [Fact]
    public async Task A_frozen_account_reaches_the_page_and_is_told_the_merge_is_in_progress()
    {
        var w = await Open();
        using var rig = w.Rig;

        var page = await Html(w, w.FoldedSession);

        Assert.Equal(1, Notices(page));
        Assert.Contains(Enc(MergeWording.FrozenNotice()), page);
        Assert.Contains(Enc(MergeWording.State(AccountMergeState.Confirmed)), page);
    }

    [Fact]
    public async Task A_folded_account_reaches_the_page_is_told_when_it_was_merged_and_cannot_undo()
    {
        var w = await Committed();
        using var rig = w.Rig;
        var merge = await rig.MergeAsync(w.MergeId);

        var page = await Html(w, w.FoldedSession);

        Assert.Equal(1, Notices(page));
        Assert.Contains(Enc(MergeWording.FoldedNotice(merge.CommittedUtc!.Value)), page);
        Assert.DoesNotContain("<form", page.Split("<main")[1]);
    }
}
