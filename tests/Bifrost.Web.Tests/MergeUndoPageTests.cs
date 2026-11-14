using System.Net;
using System.Text.Json;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Heimdall;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Bifrost.Web.Tests.AccountMergeSagaTests;

namespace Bifrost.Web.Tests;

/// <summary>
/// The merge status page's undo from the person's side: browser sessions of the real host over the saga rig and the fake Heimdall's
/// account model. The folded person signs in through a login the merge moved and is the survivor there.
/// </summary>
public sealed class MergeUndoPageTests
{
    private const string Callback = "https://localhost";

    /// <summary>The Heimdall account that holds a login now, by the fake's own model of where a move left it.</summary>
    private static string OwnerOf(Rig rig, string loginId) =>
        rig.Moves.Logins.Single(x => x.Value.Any(l => l.Id == loginId)).Key;

    private sealed class World(Rig rig, Party survivor, Party folded, Guid mergeId) : IDisposable
    {
        public Rig Rig { get; } = rig;

        public Party Survivor { get; } = survivor;

        public Party Folded { get; } = folded;

        public Guid MergeId { get; } = mergeId;

        public HeimdallHost Host => Rig.Host;

        /// <summary>Everything a response put on the wire: bodies, Location, Set-Cookie. The privacy test reads it at the end.</summary>
        public List<string> Surface { get; } = [];

        /// <summary>Every bearer the test redeemed; none may appear on the surface.</summary>
        public List<string> Bearers { get; } = [];

        public void Dispose() => Rig.Dispose();

        public async Task<string> SignInThrough(string provider) => await Host.SignInSessionAsync(OwnerOf(Rig, Folded.Logins[provider]), provider);

        public async Task<(MergePage.Antiforgery Token, HttpResponseMessage Response)> Post(string session, MergePage.Antiforgery? token, string handler, params (string, string)[] form)
        {
            token ??= await MergePage.TokenAsync(Host, session);
            var response = await MergePage.PostAsync(Host, session, token, handler, form.ToDictionary(x => x.Item1, x => x.Item2));
            Record(response, string.Empty);
            return (token, response);
        }

        public void Record(HttpResponseMessage response, string body) =>
            Surface.Add(body + "\n" + MergePage.Location(response) + "\n" + string.Join('\n', response.Headers.Select(x => x.Key + string.Concat(x.Value))));

        public async Task<string> Page(string session, string query = "")
        {
            var (response, html) = await MergePage.GetAsync(Host, session, query);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Record(response, html);
            return html;
        }

        public IReadOnlyList<string> MovedIds(AccountMerge merge) => JsonSerializer.Deserialize<string[]>(merge.MovedIdentityIds!)!;

        /// <summary>One fresh sign-in of the survivor's account through a moved login of <paramref name="provider"/>, as the page's button and Heimdall's return do.</summary>
        public async Task Prove(string session, MergePage.Antiforgery token, string provider)
        {
            var merge = await Rig.MergeAsync(MergeId);
            var before = Host.Heimdall.StartBodies.Count;
            var (_, started) = await Post(session, token, "ProveForUndo", ("via", provider));
            Assert.Equal(HttpStatusCode.Redirect, started.StatusCode);
            var attempt = Host.AttemptFrom(started, before);
            Assert.NotEmpty(attempt.Nonce);

            Host.Heimdall.Identities.Clear();
            Host.Heimdall.AddIdentity("twitch", id: Survivor.Logins["twitch"]);
            foreach (var moved in MovedIds(merge))
            {
                Host.Heimdall.AddIdentity(Rig.Moves.Logins.Values.SelectMany(x => x).Single(x => x.Id == moved).Provider, id: moved);
            }

            var bearer = Host.Token(Survivor.HeimdallId);
            Rig.Moves.Issue(Survivor.HeimdallId, Folded.Logins[provider], bearer: bearer);
            Bearers.Add(bearer);
            var done = await Host.PostCompleteAsync(
                new Dictionary<string, string> { ["attempt"] = attempt.Nonce, ["code"] = Host.CodeFor(attempt, Survivor.HeimdallId, bearer, provider: provider) },
                $"{Bifrost.Web.Features.Shared.BifrostCookies.Session}={session}; {attempt.CookieName}={attempt.Cookie}");
            Record(done, string.Empty);
            Assert.Equal(MergePage.Url + "?result=merge_proof_recorded", MergePage.Location(done));
        }
    }

    private static async Task<World> Committed(params string[] foldedLogins)
    {
        var rig = new Rig(realSessions: true);
        var survivor = await rig.AccountAsync("quill-9f3a", "twitch");
        var folded = await rig.AccountAsync("banks-2d7c", foldedLogins);
        var id = await rig.ConfirmedAsync(survivor, folded, rig.Weak(survivor, "twitch"), [.. foldedLogins.Select(x => rig.Weak(folded, x))]);
        Assert.Equal(AccountMergeState.Committed, (await rig.AdvanceAsync(id)).State);
        return new World(rig, survivor, folded, id);
    }

    private static string Undo(World w, string code) => $"{MergePage.Url}?undo={code}";

    [Fact]
    public async Task A_sign_in_through_a_login_the_merge_moved_lands_on_the_survivors_account_not_the_folded_one()
    {
        using var w = await Committed("discord");

        // Heimdall answers a sign-in through the moved login with the account that holds the login now.
        var landedOn = OwnerOf(w.Rig, w.Folded.Logins["discord"]);
        Assert.Equal(w.Survivor.HeimdallId, landedOn);
        var session = await w.Host.SignInSessionAsync(landedOn, "discord");

        // The survivor acts (the folded account would be refused on this page: its actor has no account).
        var (response, _) = await w.Host.GetLinkedAccountsAsync(session);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Undo_needs_a_fresh_proof_of_every_moved_login_and_the_missing_provider_is_named()
    {
        using var w = await Committed("discord", "patreon");
        var session = await w.SignInThrough("discord");
        var token = await MergePage.TokenAsync(w.Host, session);

        await w.Prove(session, token, "discord");
        var page = await w.Page(session);
        Assert.Contains("Patreon", page);
        var asked = w.Rig.Moves.Requests.Count;

        var (_, refused) = await w.Post(session, token, "Undo", ("mergeId", w.MergeId.ToString()));

        Assert.Equal(Undo(w, "logins_unproved"), MergePage.Location(refused));
        Assert.Equal(asked, w.Rig.Moves.Requests.Count);
        var merge = await w.Rig.MergeAsync(w.MergeId);
        Assert.Equal(AccountMergeState.Committed, merge.State);
        Assert.Null(merge.UndoRequestedUtc);
        Assert.Contains(Enc(MergeWording.Undo("logins_unproved")), await w.Page(session, "?undo=logins_unproved"));
    }

    [Fact]
    public async Task The_folded_person_proves_every_moved_login_and_the_undo_runs_once_to_Undone_and_spends_the_proofs()
    {
        using var w = await Committed("discord", "patreon");
        var session = await w.SignInThrough("patreon");
        var token = await MergePage.TokenAsync(w.Host, session);
        await w.Prove(session, token, "discord");
        await w.Prove(session, token, "patreon");
        var asked = w.Rig.Moves.Requests.Count;

        var (_, done) = await w.Post(session, token, "Undo", ("mergeId", w.MergeId.ToString()));

        Assert.Equal(Undo(w, "requested"), MergePage.Location(done));
        Assert.Equal(AccountMergeState.Undone, (await w.Rig.MergeAsync(w.MergeId)).State);
        Assert.Equal(asked + 1, w.Rig.Moves.Requests.Count);
        Assert.Equal(new MergeProofsView(null, null), w.Host.Services.GetRequiredService<MergeProofs>().Snapshot(w.Survivor.Id));

        // A replay has no proofs left and starts nothing.
        var (_, replay) = await w.Post(session, token, "Undo", ("mergeId", w.MergeId.ToString()));
        Assert.NotEqual(Undo(w, "requested"), MergePage.Location(replay));
        Assert.Equal(asked + 1, w.Rig.Moves.Requests.Count);
    }

    [Fact]
    public async Task Another_member_posting_the_survivors_undo_is_NotSurvivor_and_the_saga_is_never_asked()
    {
        using var w = await Committed("discord");
        var other = await w.Rig.AccountAsync("other-survivor-77c1", "twitch");
        var otherFolded = await w.Rig.AccountAsync("other-folded-08ab", "patreon");
        var otherMerge = await w.Rig.ConfirmedAsync(other, otherFolded, w.Rig.Weak(other, "twitch"), w.Rig.Weak(otherFolded, "patreon"));
        await w.Rig.AdvanceAsync(otherMerge);
        var session = await w.Host.SignInSessionAsync(other.HeimdallId);
        var asked = w.Rig.Moves.Requests.Count;

        var (_, response) = await w.Post(session, null, "Undo", ("mergeId", w.MergeId.ToString()));

        Assert.Equal(Undo(w, "not_survivor"), MergePage.Location(response));
        Assert.Equal(asked, w.Rig.Moves.Requests.Count);
        var merge = await w.Rig.MergeAsync(w.MergeId);
        Assert.Equal(AccountMergeState.Committed, merge.State);
        Assert.Null(merge.UndoRequestedUtc);
    }

    [Fact]
    public async Task A_post_after_the_grace_ended_is_GraceOver_with_live_proofs_and_nothing_is_requested()
    {
        using var w = await Committed("discord");
        var session = await w.SignInThrough("discord");
        var token = await MergePage.TokenAsync(w.Host, session);
        w.Rig.Clock.Advance(AccountMergeLedger.Grace - TimeSpan.FromSeconds(60));
        await w.Prove(session, token, "discord");
        w.Rig.Clock.Advance(TimeSpan.FromSeconds(61));
        var asked = w.Rig.Moves.Requests.Count;

        var (_, response) = await w.Post(session, token, "Undo", ("mergeId", w.MergeId.ToString()));

        Assert.Equal(Undo(w, "grace_over"), MergePage.Location(response));
        Assert.Equal(asked, w.Rig.Moves.Requests.Count);
        Assert.Equal(AccountMergeState.Committed, (await w.Rig.MergeAsync(w.MergeId)).State);
    }

    [Fact]
    public async Task A_reversal_Heimdall_refuses_returns_the_merge_to_Committed_and_the_page_words_the_refusal()
    {
        using var w = await Committed("discord");
        var session = await w.SignInThrough("discord");
        var token = await MergePage.TokenAsync(w.Host, session);
        await w.Prove(session, token, "discord");
        w.Rig.Moves.MoveStatus = HttpStatusCode.BadRequest;

        var (_, response) = await w.Post(session, token, "Undo", ("mergeId", w.MergeId.ToString()));

        Assert.Equal(Undo(w, "requested"), MergePage.Location(response));
        var merge = await w.Rig.MergeAsync(w.MergeId);
        Assert.Equal(AccountMergeState.Committed, merge.State);
        Assert.StartsWith("undo_", merge.LastOutcome);
        Assert.Contains(Enc(MergeWording.Outcome(merge.LastOutcome)!), await w.Page(session));
    }

    [Fact]
    public async Task No_response_cookie_or_redirect_of_the_merge_pages_carries_an_account_id_name_or_bearer()
    {
        using var w = await Committed("discord", "patreon");
        var session = await w.SignInThrough("discord");
        var token = await MergePage.TokenAsync(w.Host, session);
        await w.Page(session);
        await w.Prove(session, token, "discord");
        await w.Prove(session, token, "patreon");
        await w.Page(session);
        await w.Post(session, token, "Undo", ("mergeId", w.MergeId.ToString()));
        await w.Page(session, "?undo=requested");

        var everything = string.Join('\n', w.Surface);
        var secrets = new List<string> { "quill-9f3a", "banks-2d7c", w.Survivor.HeimdallId, w.Folded.HeimdallId };
        foreach (var id in new[] { w.Survivor.Id, w.Folded.Id })
        {
            secrets.AddRange([id.ToString(), id.ToString("N")]);
        }

        secrets.AddRange(w.Survivor.Logins.Values);
        secrets.AddRange(w.Folded.Logins.Values);
        secrets.AddRange(w.Bearers);
        Assert.Equal(2, w.Bearers.Count);
        Assert.All(secrets, x => Assert.DoesNotContain(x, everything));
        Assert.DoesNotContain("PUID-CANARY", everything);
        Assert.DoesNotContain("username-canary", everything);
    }

    private static string Enc(string text) => System.Text.Encodings.Web.HtmlEncoder.Default.Encode(text);
}
