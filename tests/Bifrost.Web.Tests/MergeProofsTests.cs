using Bifrost.Web.Features.Heimdall;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Tests.Support;

namespace Bifrost.Web.Tests;

/// <summary>The in-memory holder of merge proofs: per actor, expiring, never consuming, bearer-free in every view.</summary>
public sealed class MergeProofsTests
{
    private const string Secret = "BEARER-CANARY";

    private readonly TestClock _clock = new();
    private readonly MergeProofs _proofs;
    private readonly Guid _actor = Guid.NewGuid();
    private readonly Guid _self = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly string _selfAccount = HeimdallShapes.NewAccountId();
    private readonly string _otherAccount = HeimdallShapes.NewAccountId();

    public MergeProofsTests() => _proofs = new MergeProofs(_clock);

    private static LinkedIdentitySummary Login(string provider) => new(Guid.NewGuid().ToString(), provider, "Name of " + provider, null);

    private MergeProof Proof(string account, string provider, string? token = null) => new(token ?? Secret + provider, account, provider);

    private PutOutcome PutSelf(string provider, params LinkedIdentitySummary[] logins) =>
        _proofs.Put(_actor, MergeSide.Self, Proof(_selfAccount, provider), _self, logins);

    private PutOutcome PutOther(string provider, params LinkedIdentitySummary[] logins) =>
        _proofs.Put(_actor, MergeSide.Other, Proof(_otherAccount, provider), _other, logins);

    [Fact]
    public void Peek_gives_one_actor_their_own_proofs_and_never_consumes()
    {
        var selfLogin = Login("discord");
        var otherLogin = Login("patreon");
        Assert.Equal(PutOutcome.Stored, PutSelf("discord", selfLogin));
        Assert.Equal(PutOutcome.Stored, PutOther("patreon", otherLogin));

        var first = Assert.IsType<PeekResult.Ready>(_proofs.Peek(_actor, MergeSide.Self));
        var second = Assert.IsType<PeekResult.Ready>(_proofs.Peek(_actor, MergeSide.Self));

        Assert.Equal("discord", first.Survivor.Provider);
        Assert.Equal("patreon", Assert.Single(first.Folded).Provider);
        Assert.Equal(first.Survivor, second.Survivor);
        Assert.Equal(first.Folded, second.Folded);
        Assert.IsType<PeekResult.Expired>(_proofs.Peek(Guid.NewGuid(), MergeSide.Self));
        Assert.Null(_proofs.Snapshot(Guid.NewGuid()).Self);
        Assert.Null(_proofs.Snapshot(Guid.NewGuid()).Other);
    }

    [Fact]
    public void Peek_runs_both_ways_the_survivor_is_chosen_by_the_caller()
    {
        PutSelf("discord", Login("discord"));
        PutOther("patreon", Login("patreon"));

        var ready = Assert.IsType<PeekResult.Ready>(_proofs.Peek(_actor, MergeSide.Other));

        Assert.Equal(_otherAccount, ready.Survivor.HeimdallAccountId);
        Assert.Equal(_selfAccount, Assert.Single(ready.Folded).HeimdallAccountId);
    }

    [Fact]
    public void Clear_removes_both_sides_and_only_that_actors()
    {
        PutSelf("discord", Login("discord"));
        PutOther("patreon", Login("patreon"));
        var stranger = Guid.NewGuid();
        _proofs.Put(stranger, MergeSide.Self, Proof(_selfAccount, "discord"), _self, [Login("discord")]);

        _proofs.Clear(_actor);

        Assert.IsType<PeekResult.Expired>(_proofs.Peek(_actor, MergeSide.Self));
        Assert.Equal(new MergeProofsView(null, null), _proofs.Snapshot(_actor));
        Assert.NotNull(_proofs.Snapshot(stranger).Self);
    }

    [Theory]
    [InlineData(269, true)]
    [InlineData(270, false)]
    [InlineData(271, false)]
    public void A_proof_lives_270_seconds_from_its_redeem(int seconds, bool alive)
    {
        PutSelf("discord", Login("discord"));
        PutOther("patreon", Login("patreon"));

        _clock.Advance(TimeSpan.FromSeconds(seconds));

        Assert.Equal(alive, _proofs.Peek(_actor, MergeSide.Self) is PeekResult.Ready);
        Assert.Equal(alive, _proofs.Snapshot(_actor).Self is not null);
    }

    [Fact]
    public void One_lapsed_proof_costs_only_that_provider_and_a_newer_proof_replaces_the_older()
    {
        var discord = Login("discord");
        var patreon = Login("patreon");
        PutSelf("twitch", Login("twitch"));
        PutOther("discord", discord, patreon);
        _clock.Advance(TimeSpan.FromSeconds(200));
        PutOther("patreon", discord, patreon);
        _clock.Advance(TimeSpan.FromSeconds(100)); // the discord proof is 300 s old, the patreon one 100 s

        var self = PutSelf("twitch", Login("twitch")); // keeps the self side alive
        var unproved = Assert.IsType<PeekResult.LoginsUnproved>(_proofs.Peek(_actor, MergeSide.Self));

        Assert.Equal(PutOutcome.Stored, self);
        Assert.Equal(["discord"], unproved.Providers);

        PutOther("discord", discord, patreon);
        var ready = Assert.IsType<PeekResult.Ready>(_proofs.Peek(_actor, MergeSide.Self));
        Assert.Equal(["discord", "patreon"], ready.Folded.Select(x => x.Provider).Order().ToArray());
        var replacedAt = Assert.IsType<MergeSideView>(_proofs.Snapshot(_actor).Other);
        Assert.Equal(["discord", "patreon"], replacedAt.ProvedProviders.Order().ToArray());
    }

    [Fact]
    public void A_login_list_with_two_logins_of_one_provider_is_refused_and_stores_nothing()
    {
        var outcome = PutOther("discord", Login("discord"), Login("discord"));

        Assert.Equal(PutOutcome.AmbiguousLogins, outcome);
        Assert.Null(_proofs.Snapshot(_actor).Other);
    }

    [Fact]
    public void A_folded_side_with_an_unproved_provider_names_providers_and_nothing_else()
    {
        PutSelf("discord", Login("discord"));
        PutOther("patreon", Login("patreon"), Login("twitch"), Login("google"));

        var unproved = Assert.IsType<PeekResult.LoginsUnproved>(_proofs.Peek(_actor, MergeSide.Self));

        Assert.Equal(["google", "twitch"], unproved.Providers.Order().ToArray());
        Assert.DoesNotContain(Secret, unproved.ToString());
    }

    [Fact]
    public void A_proof_for_another_account_on_a_side_replaces_the_side_and_the_old_proofs_are_not_mixed_in()
    {
        PutOther("patreon", Login("patreon"), Login("twitch"));
        var someoneElse = Guid.NewGuid();
        var elsewhere = HeimdallShapes.NewAccountId();

        _proofs.Put(_actor, MergeSide.Other, Proof(elsewhere, "twitch"), someoneElse, [Login("twitch")]);

        var view = Assert.IsType<MergeSideView>(_proofs.Snapshot(_actor).Other);
        Assert.Equal(someoneElse, view.UserAccountId);
        Assert.Equal(["twitch"], view.ProvedProviders);
    }

    [Fact]
    public void A_side_holds_at_most_eight_proofs()
    {
        var providers = Enumerable.Range(0, MergeProofs.MaxProofsPerSide + 1).Select(x => "p" + x).ToArray();
        var logins = providers.Select(Login).ToArray();

        var outcomes = providers.Select(p => PutOther(p, logins)).ToArray();

        Assert.All(outcomes.Take(MergeProofs.MaxProofsPerSide), x => Assert.Equal(PutOutcome.Stored, x));
        Assert.Equal(PutOutcome.TooManyProofs, outcomes[^1]);
        Assert.Equal(MergeProofs.MaxProofsPerSide, _proofs.Snapshot(_actor).Other!.ProvedProviders.Count);
    }

    [Fact]
    public void Undo_needs_every_moved_login_proved_and_returns_only_the_selfs_proofs()
    {
        var discord = Login("discord");
        var patreon = Login("patreon");
        var twitch = Login("twitch");
        PutSelf("discord", discord, patreon, twitch);
        PutOther("twitch", Login("twitch"));

        var missing = Assert.IsType<UndoPeekResult.LoginsUnproved>(_proofs.PeekUndo(_actor, [discord.Id, patreon.Id]));
        PutSelf("patreon", discord, patreon, twitch);
        var ready = Assert.IsType<UndoPeekResult.Ready>(_proofs.PeekUndo(_actor, [discord.Id, patreon.Id]));

        Assert.Equal(["patreon"], missing.Providers);
        Assert.Equal(["discord", "patreon"], ready.Proofs.Select(x => x.Provider).Order().ToArray());
        Assert.All(ready.Proofs, x => Assert.Equal(_selfAccount, x.HeimdallAccountId));
        Assert.IsType<UndoPeekResult.LoginsUnproved>(_proofs.PeekUndo(_actor, [discord.Id, Guid.NewGuid().ToString()]));
        Assert.IsType<UndoPeekResult.Expired>(_proofs.PeekUndo(Guid.NewGuid(), [discord.Id]));
    }

    [Fact]
    public void No_view_or_printed_result_holds_a_bearer()
    {
        PutSelf("discord", Login("discord"));
        PutOther("patreon", Login("patreon"));
        var ready = _proofs.Peek(_actor, MergeSide.Self);

        var printed = string.Join('\n', _proofs.Snapshot(_actor), ready, ready is PeekResult.Ready r ? r.Survivor : null, _proofs.PeekUndo(_actor, ["x"]));

        Assert.DoesNotContain(Secret, printed);
        Assert.DoesNotContain(_selfAccount, printed);
        Assert.DoesNotContain(_otherAccount, printed);
    }

    /// <summary>Actors with anything in memory: pruning drops a side with no proof and an actor with no side.</summary>
    private int HeldProofs() =>
        ((System.Collections.IDictionary)typeof(MergeProofs).GetField("_held", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(_proofs)!)
        .Count;

    [Fact]
    public void An_expired_bearer_is_dropped_by_any_later_access_even_one_for_another_actor()
    {
        PutSelf("discord", Login("discord"));
        Assert.Equal(1, HeldProofs());
        _clock.Advance(MergeProofs.ProofLifetime + TimeSpan.FromSeconds(1));

        _proofs.Clear(Guid.NewGuid());

        Assert.Equal(0, HeldProofs());
    }

    [Fact]
    public void PruneExpired_drops_an_expired_bearer_with_no_other_access_and_keeps_a_live_one()
    {
        PutSelf("discord", Login("discord"));
        _clock.Advance(MergeProofs.ProofLifetime);
        _proofs.PruneExpired();
        Assert.Equal(0, HeldProofs());

        PutSelf("discord", Login("discord"));
        _clock.Advance(MergeProofs.ProofLifetime - TimeSpan.FromSeconds(1));
        _proofs.PruneExpired();
        Assert.Equal(1, HeldProofs());
    }

    [Fact]
    public void Two_sides_holding_a_login_of_one_provider_are_a_conflict_named_by_provider_only_in_both_directions()
    {
        PutSelf("discord", Login("discord"), Login("twitch"));
        PutOther("patreon", Login("patreon"), Login("discord"), Login("twitch"));

        foreach (var survivor in new[] { MergeSide.Self, MergeSide.Other })
        {
            var conflict = Assert.IsType<PeekResult.ProviderConflict>(_proofs.Peek(_actor, survivor));
            Assert.Equal(["discord", "twitch"], conflict.Providers);
        }
    }
}
