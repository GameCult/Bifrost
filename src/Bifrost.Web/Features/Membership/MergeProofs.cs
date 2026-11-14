using Bifrost.Web.Features.Heimdall;

namespace Bifrost.Web.Features.Membership;

public enum MergeSide
{
    /// <summary>The account signed in here.</summary>
    Self,

    /// <summary>The second account, proved by its own fresh sign-in.</summary>
    Other
}

public enum PutOutcome
{
    Stored,
    AmbiguousLogins,
    TooManyProofs
}

/// <summary>What a page may know of one side: no bearer, no Heimdall account id, no login names.</summary>
public sealed record MergeSideView(
    Guid UserAccountId, IReadOnlyList<string> ProvedProviders, IReadOnlyList<string> LoginProviders, TimeSpan OldestProofAge, DateTimeOffset ExpiresAt);

public sealed record MergeProofsView(MergeSideView? Self, MergeSideView? Other);

public abstract record PeekResult
{
    private PeekResult()
    {
    }

    public sealed record Ready(MergeProof Survivor, IReadOnlyList<MergeProof> Folded) : PeekResult;

    /// <summary>A side holds no live proof.</summary>
    public sealed record Expired : PeekResult;

    /// <summary>Logins whose provider has no live proof, by provider name only.</summary>
    public sealed record LoginsUnproved(IReadOnlyList<string> Providers) : PeekResult;
}

public abstract record UndoPeekResult
{
    private UndoPeekResult()
    {
    }

    public sealed record Ready(IReadOnlyList<MergeProof> Proofs) : UndoPeekResult;

    public sealed record Expired : UndoPeekResult;

    public sealed record LoginsUnproved(IReadOnlyList<string> Providers) : UndoPeekResult;
}

/// <summary>
/// The one holder of live Heimdall bearers between a merge-proof sign-in's completion and the page's call into the saga, keyed by the
/// acting Bifrost account. It lives in this process's memory only and is never persisted, logged or sent to a browser; after a restart
/// the member proves again. It decides nothing about the merge: the saga owns consent, the move and the lifecycle, and Heimdall judges
/// each proof's tier and freshness. <see cref="ProofLifetime"/> is only a margin under Heimdall's 300 s weak window, so a stale proof
/// is not sent to fail a merge. Provider is the only proof bookkeeping Bifrost has (the redeem names the provider, not the identity),
/// so a login list holding two logins of one provider is refused. Peek never consumes: the caller <see cref="Clear"/>s once the saga
/// has taken the proofs, so a refusal that wrote nothing does not cost a re-proof.
/// </summary>
public sealed class MergeProofs(TimeProvider timeProvider)
{
    public static readonly TimeSpan ProofLifetime = TimeSpan.FromSeconds(270);

    /// <summary>The saga's own bound on the proofs of one move.</summary>
    public const int MaxProofsPerSide = 8;

    private sealed record Held(MergeProof Proof, DateTimeOffset RedeemedAt);

    private sealed class Side(Guid userAccountId, string heimdallAccountId)
    {
        public Guid UserAccountId { get; } = userAccountId;

        public string HeimdallAccountId { get; } = heimdallAccountId;

        public IReadOnlyList<LinkedIdentitySummary> Logins { get; set; } = [];

        public Dictionary<string, Held> Proofs { get; } = new(StringComparer.Ordinal);
    }

    private readonly Dictionary<Guid, Dictionary<MergeSide, Side>> _held = [];
    private readonly object _gate = new();

    public PutOutcome Put(Guid actorId, MergeSide side, MergeProof proof, Guid userAccountId, IReadOnlyList<LinkedIdentitySummary> logins)
    {
        if (logins.GroupBy(x => x.Provider, StringComparer.Ordinal).Any(x => x.Count() > 1))
        {
            return PutOutcome.AmbiguousLogins;
        }

        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            Prune(now);
            var sides = _held.TryGetValue(actorId, out var existing) ? existing : _held[actorId] = [];
            if (!sides.TryGetValue(side, out var held) || held.UserAccountId != userAccountId || held.HeimdallAccountId != proof.HeimdallAccountId)
            {
                held = new Side(userAccountId, proof.HeimdallAccountId);
            }

            if (!held.Proofs.ContainsKey(proof.Provider) && held.Proofs.Count >= MaxProofsPerSide)
            {
                return PutOutcome.TooManyProofs;
            }

            held.Logins = logins;
            held.Proofs[proof.Provider] = new Held(proof, now);
            sides[side] = held;
            return PutOutcome.Stored;
        }
    }

    public MergeProofsView Snapshot(Guid actorId)
    {
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            Prune(now);
            var sides = _held.GetValueOrDefault(actorId);
            return new MergeProofsView(View(sides, MergeSide.Self, now), View(sides, MergeSide.Other, now));
        }
    }

    public PeekResult Peek(Guid actorId, MergeSide survivorSide)
    {
        lock (_gate)
        {
            Prune(timeProvider.GetUtcNow());
            var sides = _held.GetValueOrDefault(actorId);
            if (sides is null || !sides.TryGetValue(survivorSide, out var survivor)
                || !sides.TryGetValue(survivorSide == MergeSide.Self ? MergeSide.Other : MergeSide.Self, out var folded))
            {
                return new PeekResult.Expired();
            }

            var unproved = UnprovedProviders(folded, folded.Logins.Select(x => x.Provider));
            return unproved.Count > 0
                ? new PeekResult.LoginsUnproved(unproved)
                : new PeekResult.Ready(survivor.Proofs.Values.MaxBy(x => x.RedeemedAt)!.Proof, [.. folded.Proofs.Values.Select(x => x.Proof)]);
        }
    }

    /// <summary>The Self side's proofs for an undo, once every login whose id is in <paramref name="movedIdentityIds"/> is proved.</summary>
    public UndoPeekResult PeekUndo(Guid actorId, IReadOnlyCollection<string> movedIdentityIds)
    {
        lock (_gate)
        {
            Prune(timeProvider.GetUtcNow());
            if (!_held.TryGetValue(actorId, out var sides) || !sides.TryGetValue(MergeSide.Self, out var self))
            {
                return new UndoPeekResult.Expired();
            }

            var needed = self.Logins.Where(x => movedIdentityIds.Contains(x.Id)).Select(x => x.Provider).ToArray();
            var unproved = UnprovedProviders(self, needed);
            if (needed.Length < movedIdentityIds.Count)
            {
                // A moved login the list does not hold cannot be proved by it.
                return new UndoPeekResult.LoginsUnproved(unproved);
            }

            return unproved.Count > 0
                ? new UndoPeekResult.LoginsUnproved(unproved)
                : new UndoPeekResult.Ready([.. needed.Distinct(StringComparer.Ordinal).Select(x => self.Proofs[x].Proof)]);
        }
    }

    public void Clear(Guid actorId)
    {
        lock (_gate)
        {
            _held.Remove(actorId);
        }
    }

    private static List<string> UnprovedProviders(Side side, IEnumerable<string> providers) =>
        [.. providers.Distinct(StringComparer.Ordinal).Where(x => !side.Proofs.ContainsKey(x))];

    private MergeSideView? View(Dictionary<MergeSide, Side>? sides, MergeSide which, DateTimeOffset now) =>
        sides is not null && sides.TryGetValue(which, out var side)
            ? new MergeSideView(
                side.UserAccountId,
                [.. side.Proofs.Keys],
                [.. side.Logins.Select(x => x.Provider)],
                now - side.Proofs.Values.Min(x => x.RedeemedAt),
                side.Proofs.Values.Min(x => x.RedeemedAt) + ProofLifetime)
            : null;

    /// <summary>Drops every proof at or past its lifetime, then every side without one, then every actor without a side.</summary>
    private void Prune(DateTimeOffset now)
    {
        foreach (var (actor, sides) in _held.ToArray())
        {
            foreach (var (which, side) in sides.ToArray())
            {
                foreach (var (provider, held) in side.Proofs.ToArray())
                {
                    if (now - held.RedeemedAt >= ProofLifetime)
                    {
                        side.Proofs.Remove(provider);
                    }
                }

                if (side.Proofs.Count == 0)
                {
                    sides.Remove(which);
                }
            }

            if (sides.Count == 0)
            {
                _held.Remove(actor);
            }
        }
    }
}
