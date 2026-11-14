using System.Net;
using System.Text.RegularExpressions;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Heimdall;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Features.Shared;
using Bifrost.Web.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Bifrost.Web.Tests.AccountMergeSagaTests;

namespace Bifrost.Web.Tests;

/// <summary>
/// The merge page from the member's side: browser sessions of the real host over the saga rig and the fake Heimdall. Stages derive
/// from the proofs held; the other account is unnamed until both proofs exist; consent is two boxes and the saga's Confirmed row.
/// </summary>
public sealed class MergeAccountsPageTests
{
    private const string Url = MergePage.AccountsUrl;
    private const string OtherName = "banks-2d7c";

    private static string Enc(string text) => System.Text.Encodings.Web.HtmlEncoder.Default.Encode(text);

    private sealed class World : IDisposable
    {
        public Rig Rig { get; private init; } = null!;

        public Party Me { get; private init; } = null!;

        public Party Other { get; private init; } = null!;

        public string Session { get; private init; } = string.Empty;

        /// <summary>The name the account carries after signing in (a sign-in refreshes it from Heimdall).</summary>
        public string MyName { get; private init; } = string.Empty;

        public MergePage.Antiforgery? Token { get; private set; }

        public HeimdallHost Host => Rig.Host;

        public List<string> Surface { get; } = [];

        public List<string> Bearers { get; } = [];

        public MergeProofs Proofs => Host.Services.GetRequiredService<MergeProofs>();

        public static async Task<World> Make(bool enabled = true)
        {
            var rig = new Rig(realSessions: true, accountMerge: enabled);
            var me = await rig.AccountAsync("quill-9f3a", "discord");
            var other = await rig.AccountAsync(OtherName, "patreon", "google");
            var session = await rig.Host.SignInSessionAsync(me.HeimdallId, "discord");
            var myName = await rig.InScopeAsync(async sp => (await sp.GetRequiredService<BifrostDbContext>().UserAccounts.AsNoTracking().SingleAsync(x => x.Id == me.Id)).DisplayName);
            var world = new World { Rig = rig, Me = me, Other = other, Session = session, MyName = myName };
            if (enabled)
            {
                world.Token = await MergePage.TokenAsync(rig.Host, session, Url);
            }

            return world;
        }

        public void Dispose() => Rig.Dispose();

        public async Task<(HttpResponseMessage Response, string Html)> Get(string query = "", string? session = null)
        {
            var (response, html) = await MergePage.GetAsync(Host, session ?? Session, query, Url);
            Surface.Add(html + "\n" + MergePage.Location(response));
            return (response, html);
        }

        public async Task<string> Page(string query = "")
        {
            var (response, html) = await Get(query);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return html;
        }

        public async Task<HttpResponseMessage> Post(string handler, params (string, string)[] form)
        {
            var response = await MergePage.PostAsync(Host, Session, Token!, handler, form.ToDictionary(x => x.Item1, x => x.Item2), Url);
            Surface.Add(MergePage.Location(response) + "\n" + string.Join('\n', response.Headers.Select(x => x.Key + string.Concat(x.Value))));
            return response;
        }

        /// <summary>One fresh sign-in of <paramref name="proved"/> through its login of <paramref name="provider"/>, started by the page's own button and finished as Heimdall's return does.</summary>
        public async Task Prove(string handler, Party proved, string provider)
        {
            var before = Host.Heimdall.StartBodies.Count;
            var started = await Post(handler, ("via", provider), ("keep", handler == "ProveRemaining" ? "self" : string.Empty));
            Assert.Equal(HttpStatusCode.Redirect, started.StatusCode);
            var attempt = Host.AttemptFrom(started, before);
            Assert.NotEmpty(attempt.Nonce);

            Host.Heimdall.Identities.Clear();
            foreach (var (name, id) in proved.Logins)
            {
                Host.Heimdall.AddIdentity(name, id: id);
            }

            var bearer = Host.Token(proved.HeimdallId);
            Rig.Moves.Issue(proved.HeimdallId, proved.Logins[provider], bearer: bearer);
            Bearers.Add(bearer);
            var done = await Host.PostCompleteAsync(
                new Dictionary<string, string> { ["attempt"] = attempt.Nonce, ["code"] = Host.CodeFor(attempt, proved.HeimdallId, bearer, provider: provider) },
                $"{BifrostCookies.Session}={Session}; {attempt.CookieName}={attempt.Cookie}");
            Surface.Add(MergePage.Location(done));
            Assert.Equal(Url + "?result=merge_proof_recorded", MergePage.Location(done));
        }

        public Task ProveSelf(string provider = "discord") => Prove("ProveSelf", Me, provider);

        public Task ProveOther(string provider) => Prove("ProveOther", Other, provider);

        /// <summary>Both accounts proved in full, so either can be kept.</summary>
        public async Task ProveAll()
        {
            await ProveSelf();
            await ProveOther("patreon");
            await ProveOther("google");
        }

        public static string DigestIn(string html) => Regex.Match(html, "name=\"digest\"[^>]*value=\"([0-9a-f]{64})\"").Groups[1].Value;

        public Task<HttpResponseMessage> Confirm(string keep, string digest, bool self = true, bool other = true)
        {
            var form = new List<(string, string)> { ("keep", keep), ("digest", digest) };
            if (self)
            {
                form.Add(("consentSelf", "true"));
            }

            if (other)
            {
                form.Add(("consentOther", "true"));
            }

            return Post("Confirm", [.. form]);
        }
    }

    private static string Where(string keep, string code) => $"{Url}?keep={keep}&merge={code}";

    // ---- the flag and the gates ----

    [Fact]
    public async Task Disabled_the_page_is_404_and_Linked_accounts_renders_no_link()
    {
        using var w = await World.Make(enabled: false);

        var (response, _) = await w.Get();
        var (_, linked) = await w.Host.GetLinkedAccountsAsync(w.Session);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("Merge two accounts", linked);
        Assert.DoesNotContain("MergeAccounts", linked);
    }

    [Fact]
    public async Task Enabled_Linked_accounts_links_to_the_page()
    {
        using var w = await World.Make();

        var (_, linked) = await w.Host.GetLinkedAccountsAsync(w.Session);

        Assert.Contains("Merge two accounts", linked);
        Assert.Contains(Url, linked);
    }

    [Fact]
    public async Task A_frozen_account_is_forbidden_the_page()
    {
        using var w = await World.Make();
        await w.Rig.ConfirmedAsync(w.Me, w.Other, w.Rig.Weak(w.Me, "discord"), w.Rig.Weak(w.Other, "patreon"), w.Rig.Weak(w.Other, "google"));
        var folded = await w.Host.SignInSessionAsync(w.Other.HeimdallId, "patreon");

        var (response, _) = await w.Get(session: folded);

        AssertRefused(response);
    }

    /// <summary>Forbid answers 403 or sends the browser to the access-denied page; either way the page is not served.</summary>
    private static void AssertRefused(HttpResponseMessage response) =>
        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Found, response.StatusCode.ToString());

    // ---- stages derive from the proofs held ----

    [Fact]
    public async Task With_no_proof_the_page_asks_for_both_and_says_nothing_is_named_until_both_exist()
    {
        using var w = await World.Make();

        var page = await w.Page();

        Assert.Contains("ProveSelf", page);
        Assert.Contains("ProveOther", page);
        Assert.Contains("nothing is named until both", page);
        Assert.DoesNotContain("Keep this account", page);
    }

    [Fact]
    public async Task Nothing_about_the_other_account_appears_before_its_proof_is_stored()
    {
        using var w = await World.Make();
        await w.ProveSelf();

        var onlySelf = await w.Page();
        var onlySelfKept = await w.Page("?keep=other");

        Assert.DoesNotContain(OtherName, onlySelf + onlySelfKept);
        Assert.DoesNotContain(w.Other.HeimdallId, onlySelf + onlySelfKept);
        Assert.DoesNotContain(w.Other.Id.ToString(), onlySelf + onlySelfKept);
        Assert.DoesNotContain("Keep this account", onlySelf);
    }

    [Fact]
    public async Task The_other_proof_alone_names_neither_account()
    {
        using var w = await World.Make();
        await w.ProveOther("patreon");

        var page = await w.Page();

        Assert.DoesNotContain(OtherName, page);
        Assert.DoesNotContain("Keep this account", page);
    }

    [Fact]
    public async Task Both_proofs_name_both_accounts_by_display_name_and_offer_each_survivor()
    {
        using var w = await World.Make();
        await w.ProveSelf();
        await w.ProveOther("patreon");

        var page = await w.Page();

        Assert.Contains(w.MyName, page);
        Assert.Contains(OtherName, page);
        Assert.Contains("Keep this account", page);
        Assert.Contains("Keep the other account", page);
        Assert.Contains("keep=self", page);
        Assert.Contains("keep=other", page);
        Assert.DoesNotContain(w.Other.HeimdallId, page);
        Assert.DoesNotContain(w.Other.Id.ToString(), page);
    }

    [Fact]
    public async Task The_folded_sides_unproved_logins_are_asked_one_sign_in_each_and_the_kept_side_needs_none()
    {
        using var w = await World.Make();
        await w.ProveSelf();
        await w.ProveOther("patreon");

        var keepSelf = await w.Page("?keep=self");
        var keepOther = await w.Page("?keep=other");

        Assert.Contains("ProveRemaining", keepSelf);
        Assert.Contains("Google", keepSelf);
        Assert.DoesNotContain("name=\"digest\"", keepSelf);
        Assert.DoesNotContain("ProveRemaining", keepOther);
        Assert.Matches("name=\"digest\"", keepOther);

        await w.Prove("ProveRemaining", w.Other, "google");
        Assert.Matches("name=\"digest\"", await w.Page("?keep=self"));
    }

    [Fact]
    public async Task Proofs_older_than_their_lifetime_fall_out_and_the_page_asks_again()
    {
        using var w = await World.Make();
        await w.ProveAll();
        Assert.Matches("name=\"digest\"", await w.Page("?keep=other"));

        w.Rig.Clock.Advance(MergeProofs.ProofLifetime + TimeSpan.FromSeconds(1));
        var page = await w.Page("?keep=other");

        Assert.Contains("ProveSelf", page);
        Assert.DoesNotContain("name=\"digest\"", page);
        Assert.DoesNotContain(OtherName, page);
    }

    // ---- the preview and the digest ----

    [Fact]
    public async Task The_two_directions_preview_with_different_digests_and_a_stale_one_is_PreviewStale_and_keeps_the_proofs()
    {
        using var w = await World.Make();
        await w.ProveAll();
        var keepSelf = World.DigestIn(await w.Page("?keep=self"));
        var keepOther = World.DigestIn(await w.Page("?keep=other"));
        Assert.NotEqual(keepSelf, keepOther);
        Assert.Equal(await w.Rig.DigestAsync(w.Me, w.Other), keepSelf);
        Assert.Equal(await w.Rig.DigestAsync(w.Other, w.Me), keepOther);

        var response = await w.Confirm("self", keepOther);

        Assert.Equal(Where("self", "preview_stale"), MergePage.Location(response));
        Assert.Equal(0, await w.Rig.MergeCountAsync());
        Assert.NotNull(w.Proofs.Snapshot(w.Me.Id).Self);
        Assert.NotNull(w.Proofs.Snapshot(w.Me.Id).Other);
        Assert.Contains(Enc(MergeWording.Confirm("preview_stale")), await w.Page("?keep=self&merge=preview_stale"));
    }

    [Fact]
    public async Task A_change_after_the_member_looked_makes_the_digest_stale()
    {
        using var w = await World.Make();
        await w.ProveAll();
        var seen = World.DigestIn(await w.Page("?keep=self"));
        var motion = await w.Rig.MotionAsync(w.Me);
        await w.Rig.VoteAsync(motion, w.Other, VoteChoice.For);

        var response = await w.Confirm("self", seen);

        Assert.Equal(Where("self", "preview_stale"), MergePage.Location(response));
        Assert.Equal(0, await w.Rig.MergeCountAsync());
    }

    [Fact]
    public async Task The_preview_shows_the_plans_counts_and_the_static_statements_and_never_the_digests_inputs()
    {
        using var w = await World.Make();
        var motion = await w.Rig.MotionAsync(w.Me);
        await w.Rig.VoteAsync(motion, w.Other, VoteChoice.For);
        await w.ProveAll();

        var page = await w.Page("?keep=self");

        Assert.Contains("1 open vote ", page);
        foreach (var statement in MergePreview.Statements)
        {
            Assert.Contains(Enc(statement), page);
        }
    }

    [Fact]
    public async Task A_blocked_pair_shows_the_blocks_words_and_no_confirm_form()
    {
        using var w = await World.Make();
        var third = await w.Rig.AccountAsync("third-77aa", "discord");
        await w.Rig.ConfirmedAsync(third, w.Other, w.Rig.Weak(third, "discord"), w.Rig.Weak(w.Other, "patreon"), w.Rig.Weak(w.Other, "google"));
        await w.ProveAll();

        var page = await w.Page("?keep=other");

        Assert.Contains(Enc(MergePreview.Block("merge_in_flight")), page);
        Assert.DoesNotContain("consentSelf", page);
    }

    // ---- confirm ----

    [Fact]
    public async Task Confirm_with_only_the_self_proof_does_not_reach_the_saga()
    {
        using var w = await World.Make();
        await w.ProveSelf();
        var digest = await w.Rig.DigestAsync(w.Me, w.Other);

        var response = await w.Confirm("self", digest);

        Assert.Equal(Where("self", "proofs_needed"), MergePage.Location(response));
        Assert.Equal(0, await w.Rig.MergeCountAsync());
        Assert.Empty(w.Rig.Moves.Requests);
    }

    [Fact]
    public async Task Confirm_with_the_folded_sides_logins_not_all_proved_does_not_reach_the_saga()
    {
        using var w = await World.Make();
        await w.ProveSelf();
        await w.ProveOther("patreon");
        var digest = await w.Rig.DigestAsync(w.Me, w.Other);

        var response = await w.Confirm("self", digest);

        Assert.Equal(Where("self", "proofs_needed"), MergePage.Location(response));
        Assert.Equal(0, await w.Rig.MergeCountAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Confirm_without_both_consent_boxes_writes_nothing_and_keeps_the_proofs(bool self, bool other)
    {
        using var w = await World.Make();
        await w.ProveAll();
        var digest = World.DigestIn(await w.Page("?keep=other"));

        var response = await w.Confirm("other", digest, self, other);

        Assert.Equal(Where("other", "consent_needed"), MergePage.Location(response));
        Assert.Equal(0, await w.Rig.MergeCountAsync());
        Assert.NotNull(w.Proofs.Snapshot(w.Me.Id).Other);
    }

    [Fact]
    public async Task A_confirm_that_is_not_self_or_other_is_BadChoice()
    {
        using var w = await World.Make();
        await w.ProveAll();

        var response = await w.Confirm("both", "x");

        Assert.Equal(Url + "?merge=bad_choice", MergePage.Location(response));
        Assert.Equal(0, await w.Rig.MergeCountAsync());
    }

    [Fact]
    public async Task A_blocked_confirm_is_Blocked_and_keeps_the_proofs()
    {
        using var w = await World.Make();
        await w.ProveAll();
        var digest = World.DigestIn(await w.Page("?keep=other"));
        var third = await w.Rig.AccountAsync("third-77aa", "discord");
        await w.Rig.ConfirmedAsync(third, w.Other, w.Rig.Weak(third, "discord"), w.Rig.Weak(w.Other, "patreon"), w.Rig.Weak(w.Other, "google"));
        var merges = await w.Rig.MergeCountAsync();

        var response = await w.Confirm("other", digest);

        Assert.Equal(Where("other", "blocked"), MergePage.Location(response));
        Assert.Equal(merges, await w.Rig.MergeCountAsync());
        Assert.NotNull(w.Proofs.Snapshot(w.Me.Id).Self);
    }

    [Fact]
    public async Task A_proof_of_a_Heimdall_account_the_member_account_no_longer_has_is_ProofAccountMismatch_and_keeps_the_proofs()
    {
        using var w = await World.Make();
        await w.ProveAll();
        var digest = World.DigestIn(await w.Page("?keep=other"));
        await w.Rig.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<BifrostDbContext>();
            (await db.UserAccounts.SingleAsync(x => x.Id == w.Me.Id)).HeimdallAccountId = HeimdallShapes.NewAccountId();
            return await db.SaveChangesAsync();
        });

        var response = await w.Confirm("other", digest);

        Assert.Equal(Where("other", "proof_account_mismatch"), MergePage.Location(response));
        Assert.Equal(0, await w.Rig.MergeCountAsync());
        Assert.NotNull(w.Proofs.Snapshot(w.Me.Id).Other);
    }

    [Theory]
    [InlineData("self")]
    [InlineData("other")]
    public async Task A_confirm_in_either_direction_leaves_one_row_with_the_typed_consent_columns_sealed_proofs_and_spent_proofs(string keep)
    {
        using var w = await World.Make();
        await w.ProveAll();
        var digest = World.DigestIn(await w.Page("?keep=" + keep));

        var response = await w.Confirm(keep, digest);

        Assert.Equal(MergePage.Url, MergePage.Location(response));
        var survivor = keep == "self" ? w.Me : w.Other;
        var merge = Assert.Single(await w.Rig.InScopeAsync(sp => sp.GetRequiredService<BifrostDbContext>().AccountMerges.AsNoTracking().ToListAsync()));
        Assert.Equal(AccountMergeState.Committed, merge.State);
        Assert.Equal(survivor.Id, merge.SurvivorUserAccountId);
        Assert.Equal(w.Me.Id, merge.ConfirmedByUserAccountId);
        Assert.Equal(digest, merge.PreviewDigest);
        Assert.Null(merge.SealedProofs); // spent by the move that landed
        if (keep == "self")
        {
            Assert.Equal("discord", merge.SurvivorProofProvider);
            Assert.Equal("google,patreon", merge.FoldedProofProviders);
        }
        else
        {
            Assert.Contains(merge.SurvivorProofProvider, new[] { "patreon", "google" });
            Assert.Equal("discord", merge.FoldedProofProviders);
        }

        Assert.Equal(new MergeProofsView(null, null), w.Proofs.Snapshot(w.Me.Id));
    }

    [Fact]
    public async Task A_confirm_while_Heimdall_is_unreachable_still_lands_on_Merge_status_with_the_row_Confirmed_and_the_proofs_sealed()
    {
        using var w = await World.Make();
        await w.ProveAll();
        var digest = World.DigestIn(await w.Page("?keep=self"));
        w.Rig.Moves.MoveDown = new HttpRequestException("down");

        var response = await w.Confirm("self", digest);

        Assert.Equal(MergePage.Url, MergePage.Location(response));
        var merge = Assert.Single(await w.Rig.InScopeAsync(sp => sp.GetRequiredService<BifrostDbContext>().AccountMerges.AsNoTracking().ToListAsync()));
        Assert.Equal(AccountMergeState.Confirmed, merge.State);
        Assert.NotNull(merge.SealedProofs);
        Assert.Equal(new MergeProofsView(null, null), w.Proofs.Snapshot(w.Me.Id));
    }

    [Fact]
    public async Task The_whole_flow_by_sign_ins_ends_on_the_survivors_status_with_a_notice_and_the_folded_cookie_has_no_actor()
    {
        using var w = await World.Make();
        var foldedSession = await w.Host.SignInSessionAsync(w.Other.HeimdallId, "patreon");
        await w.ProveSelf();
        await w.ProveOther("patreon");
        await w.Prove("ProveRemaining", w.Other, "google");
        var digest = World.DigestIn(await w.Page("?keep=self"));

        var response = await w.Confirm("self", digest);

        Assert.Equal(MergePage.Url, MergePage.Location(response));
        var (_, status) = await MergePage.GetAsync(w.Host, w.Session);
        Assert.Contains(Enc(MergeWording.State(AccountMergeState.Committed)), status);
        Assert.Contains("Undo window ends", status);
        Assert.Contains("can still be undone", status);
        var (foldedResponse, _) = await w.Host.GetLinkedAccountsAsync(foldedSession);
        AssertRefused(foldedResponse);

        var everything = string.Join('\n', w.Surface) + status;
        Assert.All(w.Bearers, bearer => Assert.DoesNotContain(bearer, everything));
        Assert.DoesNotContain(w.Other.HeimdallId, everything);
    }

    [Fact]
    public async Task Proving_without_naming_a_provider_starts_nothing_and_comes_back_with_the_error_code()
    {
        using var w = await World.Make();
        var started = w.Host.Heimdall.StartBodies.Count;

        var response = await w.Post("ProveOther");

        Assert.Equal(Url + "?result=error", MergePage.Location(response));
        Assert.Equal(started, w.Host.Heimdall.StartBodies.Count);
    }
}
