using System.Net;
using Bifrost.Web.Domain;
using Bifrost.Web.Tests.Support;
using static Bifrost.Web.Tests.AccountMergeSagaTests;

namespace Bifrost.Web.Tests;

/// <summary>The merge status page's undo from the person's side: a browser session of the real host over the saga rig and the fake Heimdall's account model.</summary>
public sealed class MergeUndoPageTests
{
    /// <summary>The Heimdall account that holds a login now, by the fake's own model of where a move left it.</summary>
    private static string OwnerOf(Rig rig, string loginId) =>
        rig.Moves.Logins.Single(x => x.Value.Any(l => l.Id == loginId)).Key;

    [Fact]
    public async Task A_sign_in_through_a_login_the_merge_moved_lands_on_the_survivors_account_not_the_folded_one()
    {
        using var rig = new Rig(realSessions: true);
        var survivor = await rig.AccountAsync("survivor", "twitch");
        var folded = await rig.AccountAsync("folded", "discord");
        var id = await rig.ConfirmedAsync(survivor, folded, rig.Weak(survivor, "twitch"), rig.Weak(folded, "discord"));
        Assert.Equal(AccountMergeState.Committed, (await rig.AdvanceAsync(id)).State);

        // Heimdall answers a sign-in through the moved login with the account that holds the login now.
        var landedOn = OwnerOf(rig, folded.Logins["discord"]);
        Assert.Equal(survivor.HeimdallId, landedOn);
        var session = await rig.Host.SignInSessionAsync(landedOn, "discord");

        // The survivor acts (the folded account would be refused on this page: its actor has no account).
        var (response, _) = await rig.Host.GetLinkedAccountsAsync(session);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
