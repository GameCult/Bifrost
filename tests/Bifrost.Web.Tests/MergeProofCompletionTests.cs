using System.Net;
using System.Text.Json;
using Bifrost.Web.Features.Heimdall;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Features.Shared;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.Web.Tests;

/// <summary>
/// The two merge-proof sign-ins from the browser's side, against a fake Heimdall: a fresh sign-in as the account signed in here (Self)
/// or as a second account (Other), each held in <see cref="MergeProofs"/> and nowhere else. No page starts them yet, so the tests start
/// them through the service and finish them as the browser does, by posting the completion code with the attempt cookie.
/// </summary>
public sealed class MergeProofCompletionTests
{
    private const string Page = "/Membership/MergeStandIn";
    private const string Callback = "https://localhost";

    private sealed record Member(string Account, string Session, Guid UserAccountId);

    private static async Task<Member> SignedIn(HeimdallHost host)
    {
        var account = HeimdallShapes.NewAccountId();
        var session = await host.SignInSessionAsync(account);
        return new Member(account, session, (await host.Account(account))!.Id);
    }

    private static async Task<StartedAttempt> Begin(HeimdallHost host, Member session, MergeSide side, string via = "discord")
    {
        var auth = host.Services.GetRequiredService<HeimdallAuthService>();
        var started = side == MergeSide.Self
            ? await auth.StartMergeProveSelfAsync(session.Account, via, Page, Callback, [], CancellationToken.None)
            : await auth.StartMergeProveOtherAsync(session.Account, via, Page, Callback, [], CancellationToken.None);
        var returnTo = JsonDocument.Parse(host.Heimdall.StartBodies[^1]).RootElement.GetProperty("returnTo").GetString()!;
        var nonce = QueryHelpers.ParseQuery(new Uri(returnTo).Query)["attempt"].ToString();
        return new StartedAttempt(nonce, started.CookieName, started.CookieValue, returnTo, new HttpResponseMessage());
    }

    private static async Task<HttpResponseMessage> Finish(
        HeimdallHost host, Member session, StartedAttempt attempt, string provedAccount, string token, string provider = "discord", string? sessionOverride = null)
    {
        var code = host.CodeFor(attempt, provedAccount, token, provider: provider);
        return await host.PostCompleteAsync(
            new Dictionary<string, string> { ["attempt"] = attempt.Nonce, ["code"] = code },
            $"{BifrostCookies.Session}={sessionOverride ?? session.Session}; {attempt.CookieName}={attempt.Cookie}");
    }

    private static string? Location(HttpResponseMessage response) => response.Headers.Location?.OriginalString;

    private static MergeProofs Proofs(HeimdallHost host) => host.Services.GetRequiredService<MergeProofs>();

    private static void SetList(HeimdallHost host, params string[] providers)
    {
        host.Heimdall.Identities.Clear();
        foreach (var provider in providers)
        {
            host.Heimdall.AddIdentity(provider, "Name of " + provider);
        }
    }

    [Theory]
    [InlineData(MergeSide.Self)]
    [InlineData(MergeSide.Other)]
    public async Task A_completion_through_another_provider_than_the_attempts_records_no_proof(MergeSide side)
    {
        using var host = new HeimdallHost(realSessions: true);
        var me = await SignedIn(host);
        var other = await SignedIn(host);
        SetList(host, "discord", "patreon");
        var proved = side == MergeSide.Self ? me : other;
        var attempt = await Begin(host, me, side, via: "discord");

        var done = await Finish(host, me, attempt, proved.Account, host.Token(proved.Account), provider: "patreon");

        Assert.NotEqual(Page + "?result=merge_proof_recorded", Location(done));
        var held = Proofs(host).Snapshot(me.UserAccountId);
        Assert.Null(held.Self);
        Assert.Null(held.Other);
    }

    [Fact]
    public async Task A_self_proof_for_the_signed_in_account_is_held_and_the_browser_goes_back_with_a_closed_code()
    {
        using var host = new HeimdallHost(realSessions: true);
        var me = await SignedIn(host);
        SetList(host, "discord", "patreon");
        var bearer = host.Token(me.Account);

        var done = await Finish(host, me, await Begin(host, me, MergeSide.Self), me.Account, bearer);

        Assert.Equal(Page + "?result=merge_proof_recorded", Location(done));
        var view = Assert.IsType<MergeSideView>(Proofs(host).Snapshot(me.UserAccountId).Self);
        Assert.Equal(me.UserAccountId, view.UserAccountId);
        Assert.Equal(["discord"], view.ProvedProviders);
        Assert.Equal(["discord", "patreon"], view.LoginProviders.Order().ToArray());
        Assert.Equal($"Bearer {bearer}", Assert.Single(host.Heimdall.ListAuthorizations));
        var surface = Location(done) + string.Join('\n', done.Headers.Select(x => x.Key + string.Concat(x.Value)));
        Assert.DoesNotContain(bearer, surface);
        Assert.DoesNotContain(me.Account, surface);
        Assert.DoesNotContain(bearer, string.Join('\n', host.Logs.Lines));
    }

    [Fact]
    public async Task A_self_proof_of_a_different_account_is_refused_and_stores_nothing()
    {
        using var host = new HeimdallHost(realSessions: true);
        var me = await SignedIn(host);
        var someoneElse = HeimdallShapes.NewAccountId();
        SetList(host, "discord");

        var done = await Finish(host, me, await Begin(host, me, MergeSide.Self), someoneElse, host.Token(someoneElse));

        Assert.Equal(Page + "?result=error", Location(done));
        Assert.Equal(new MergeProofsView(null, null), Proofs(host).Snapshot(me.UserAccountId));
        Assert.Empty(host.Heimdall.ListAuthorizations);
    }

    [Fact]
    public async Task An_other_proof_of_the_sessions_own_account_is_MergeSameAccount_and_stores_nothing()
    {
        using var host = new HeimdallHost(realSessions: true);
        var me = await SignedIn(host);
        SetList(host, "discord");

        var done = await Finish(host, me, await Begin(host, me, MergeSide.Other), me.Account, host.Token(me.Account));

        Assert.Equal(Page + "?result=merge_same_account", Location(done));
        Assert.Equal(new MergeProofsView(null, null), Proofs(host).Snapshot(me.UserAccountId));
        Assert.Empty(host.Heimdall.ListAuthorizations);
    }

    [Fact]
    public async Task An_other_proof_for_a_Heimdall_account_without_a_member_account_is_MergeNoAccount_and_the_bearer_is_dropped()
    {
        using var host = new HeimdallHost(realSessions: true);
        var me = await SignedIn(host);
        var stranger = HeimdallShapes.NewAccountId();
        SetList(host, "patreon");

        var done = await Finish(host, me, await Begin(host, me, MergeSide.Other, "patreon"), stranger, host.Token(stranger), "patreon");

        Assert.Equal(Page + "?result=merge_no_account", Location(done));
        Assert.Equal(new MergeProofsView(null, null), Proofs(host).Snapshot(me.UserAccountId));
        Assert.Empty(host.Heimdall.ListAuthorizations);
        Assert.Null(await host.Account(stranger));
    }

    [Fact]
    public async Task Both_sides_proved_give_the_actor_the_bearers_that_were_redeemed()
    {
        using var host = new HeimdallHost(realSessions: true);
        var me = await SignedIn(host);
        var them = await SignedIn(host);
        SetList(host, "discord");
        var mine = host.Token(me.Account);
        await Finish(host, me, await Begin(host, me, MergeSide.Self), me.Account, mine);
        SetList(host, "patreon");
        var theirs = host.Token(them.Account);

        var done = await Finish(host, me, await Begin(host, me, MergeSide.Other, "patreon"), them.Account, theirs, "patreon");

        Assert.Equal(Page + "?result=merge_proof_recorded", Location(done));
        var ready = Assert.IsType<PeekResult.Ready>(Proofs(host).Peek(me.UserAccountId, MergeSide.Self));
        Assert.Equal((mine, me.Account, "discord"), (ready.Survivor.AccessToken, ready.Survivor.HeimdallAccountId, ready.Survivor.Provider));
        var folded = Assert.Single(ready.Folded);
        Assert.Equal((theirs, them.Account, "patreon"), (folded.AccessToken, folded.HeimdallAccountId, folded.Provider));
        var view = Assert.IsType<MergeSideView>(Proofs(host).Snapshot(me.UserAccountId).Other);
        Assert.Equal(them.UserAccountId, view.UserAccountId);
    }

    [Fact]
    public async Task A_list_Heimdall_does_not_give_stores_nothing()
    {
        using var host = new HeimdallHost(realSessions: true);
        var me = await SignedIn(host);
        SetList(host, "discord");
        host.Heimdall.ListStatus = HttpStatusCode.InternalServerError;

        var done = await Finish(host, me, await Begin(host, me, MergeSide.Self), me.Account, host.Token(me.Account));

        Assert.Equal(Page + "?result=error", Location(done));
        Assert.Equal(new MergeProofsView(null, null), Proofs(host).Snapshot(me.UserAccountId));
    }

    [Fact]
    public async Task A_side_holding_two_logins_of_one_provider_is_MergeLoginsAmbiguous_and_stores_nothing()
    {
        using var host = new HeimdallHost(realSessions: true);
        var me = await SignedIn(host);
        SetList(host, "discord", "discord");

        var done = await Finish(host, me, await Begin(host, me, MergeSide.Self), me.Account, host.Token(me.Account));

        Assert.Equal(Page + "?result=merge_logins_ambiguous", Location(done));
        Assert.Equal(new MergeProofsView(null, null), Proofs(host).Snapshot(me.UserAccountId));
    }

    [Fact]
    public async Task A_completion_in_another_browsers_session_holds_nothing()
    {
        using var host = new HeimdallHost(realSessions: true);
        var me = await SignedIn(host);
        var them = await SignedIn(host);
        SetList(host, "discord");

        var done = await Finish(host, me, await Begin(host, me, MergeSide.Self), me.Account, host.Token(me.Account), sessionOverride: them.Session);

        Assert.Equal(new MergeProofsView(null, null), Proofs(host).Snapshot(me.UserAccountId));
        Assert.Equal(new MergeProofsView(null, null), Proofs(host).Snapshot(them.UserAccountId));
        Assert.Empty(host.Heimdall.ListAuthorizations);
        Assert.Equal(Page + "?result=error", Location(done));
    }

    [Fact]
    public async Task A_merge_proof_starts_only_through_a_provider_the_member_names()
    {
        using var host = new HeimdallHost(realSessions: true);
        var me = await SignedIn(host);
        var auth = host.Services.GetRequiredService<HeimdallAuthService>();
        var before = host.Heimdall.StartBodies.Count;

        await Assert.ThrowsAsync<HeimdallAuthException>(() => auth.StartMergeProveSelfAsync(me.Account, null, Page, Callback, [], CancellationToken.None));
        await Assert.ThrowsAsync<HeimdallAuthException>(() => auth.StartMergeProveOtherAsync(me.Account, "github", Page, Callback, [], CancellationToken.None));

        Assert.Equal(before, host.Heimdall.StartBodies.Count);
    }
}
