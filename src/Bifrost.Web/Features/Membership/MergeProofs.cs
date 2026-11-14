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

/// <summary>Holder of live bearers between a merge-proof sign-in's completion and the page's call into the saga. See the cut spec.</summary>
public sealed class MergeProofs(TimeProvider timeProvider)
{
    public static readonly TimeSpan ProofLifetime = TimeSpan.FromSeconds(270);

    public const int MaxProofsPerSide = 8;

    public PutOutcome Put(Guid actorId, MergeSide side, MergeProof proof, Guid userAccountId, IReadOnlyList<LinkedIdentitySummary> logins) =>
        throw new NotImplementedException();

    public MergeProofsView Snapshot(Guid actorId) => throw new NotImplementedException();

    public PeekResult Peek(Guid actorId, MergeSide survivorSide) => throw new NotImplementedException();

    public UndoPeekResult PeekUndo(Guid actorId, IReadOnlyCollection<string> movedIdentityIds) => throw new NotImplementedException();

    public void Clear(Guid actorId) => throw new NotImplementedException();
}
