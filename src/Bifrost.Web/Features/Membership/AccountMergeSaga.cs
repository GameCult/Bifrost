using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Heimdall;
using Bifrost.Web.Features.Shared;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Bifrost.Web.Features.Membership;

/// <summary>
/// A live Heimdall bearer a member redeemed from a fresh sign-in, with the Heimdall account and provider it came through. The token is a
/// secret: <see cref="PrintMembers"/> never prints it (nor the account id), so no log or exception that holds a proof can leak it.
/// </summary>
public sealed record MergeProof(string AccessToken, string HeimdallAccountId, string Provider)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Provider = {Provider}");
        return true;
    }
}

/// <summary>What the member confirmed: the pair, the plan digest they saw, and a proof for every login of each side that must speak.</summary>
public sealed record ConfirmMergeRequest(
    Guid ActorUserAccountId,
    Guid SurvivorUserAccountId,
    Guid FoldedUserAccountId,
    string PreviewDigest,
    MergeProof SurvivorProof,
    IReadOnlyList<MergeProof> FoldedProofs);

/// <summary>The closed answer to a confirm. Only Confirmed wrote a row.</summary>
public abstract record ConfirmOutcome
{
    private ConfirmOutcome()
    {
    }

    public sealed record Confirmed(Guid MergeId) : ConfirmOutcome;

    /// <summary>The plan's block codes (never account names), or merge_in_flight when the database refused an overlapping merge.</summary>
    public sealed record Blocked(IReadOnlyList<string> Codes) : ConfirmOutcome;

    public sealed record PreviewStale : ConfirmOutcome;

    public sealed record NotParty : ConfirmOutcome;

    public sealed record ProofAccountMismatch : ConfirmOutcome;
}

/// <summary>The closed answer to an undo request. Only Requested wrote.</summary>
public abstract record UndoOutcome
{
    private UndoOutcome()
    {
    }

    public sealed record Requested : UndoOutcome;

    public sealed record NotCommitted : UndoOutcome;

    public sealed record GraceOver : UndoOutcome;

    public sealed record NotSurvivor : UndoOutcome;

    public sealed record ProofAccountMismatch : UndoOutcome;
}

/// <summary>The two ways an operator may end a stuck leg: the forward move is failed, or the undo is given up and the merge stands.</summary>
public enum SettleChoice
{
    FailForward,
    AbandonUndo
}

/// <summary>The closed answer to an operator's settle. Only Settled wrote.</summary>
public abstract record SettleOutcome
{
    private SettleOutcome()
    {
    }

    public sealed record Settled : SettleOutcome;

    /// <summary>The merge is missing, in another state than the choice ends, or not stuck on one of the settleable outcomes.</summary>
    public sealed record NotStuck : SettleOutcome;

    public sealed record WrongMoveId : SettleOutcome;

    /// <summary>The State token went to another writer first; the other transition stands and nothing was changed.</summary>
    public sealed record Lost : SettleOutcome;
}

/// <summary>
/// The one owner of an account merge's lifecycle outside the ledger's Committed, Final and Undone, and the only caller of
/// <see cref="HeimdallIdentityMoveClient"/>. It inserts Confirmed (the consent record, with the Heimdall bearers sealed and the move
/// id minted), runs the one idempotent step function <see cref="AdvanceAsync"/> (move, commit, reverse, undo), and writes
/// HeimdallMoved, Failed, UndoRequested and HeimdallReversed. It never writes votes, memberships or journal rows (the ledger's), never
/// judges a proof's tier or freshness (Heimdall's), and reads no standing of its own: the plan's blocks and the database's live-merge
/// index answer overlap, and the writers that act for an account go through <see cref="AccountMergeLedger.WriteAsAsync{T}"/>.
/// Every transition is one save guarded by State, the merge's concurrency token; the loser of a race reloads and continues from the
/// stored state. Heimdall's closed outcome is consumed as is: Unknown (a lost answer, an outage) is replayed with the same stored
/// move id and never fails a merge. Logs name the merge id, state, outcome class and refusal code only.
/// </summary>
public sealed partial class AccountMergeSaga(
    BifrostDbContext dbContext,
    AccountMergeLedger ledger,
    HeimdallIdentityMoveClient mover,
    IDataProtectionProvider dataProtection,
    AuditTrailService audit,
    TimeProvider timeProvider,
    ILogger<AccountMergeSaga> logger)
{
    public const string MergeInFlight = "merge_in_flight";
    public const string ReceiptMismatch = "receipt_mismatch";
    public const string ProofsUnreadable = "proofs_unreadable";
    public const string MoveIdConflict = "move_id_conflict";
    public const string OperatorFailed = "operator_failed";
    public const string UndoOperatorAbandoned = "undo_operator_abandoned";

    private const string LiveMergeIndex = "UX_AccountMerges_FoldedUserAccountId_Live";
    private const int MaxProofs = 8;

    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];
    private static readonly TimeSpan UnreadableRetry = TimeSpan.FromHours(1);

    private static readonly AccountMergeState[] DueStates =
        [AccountMergeState.Confirmed, AccountMergeState.HeimdallMoved, AccountMergeState.UndoRequested, AccountMergeState.HeimdallReversed];

    private readonly IDataProtector _seal = dataProtection.CreateProtector("Bifrost.AccountMerge.Proofs.v1");

    private sealed record ProofSeal(List<string> From, string? To)
    {
        // The seal holds live bearers: the generated ToString would print them into any log or exception that holds it.
        private bool PrintMembers(StringBuilder builder)
        {
            builder.Append($"From = {From?.Count}, To = {(To is null ? "none" : "present")}");
            return true;
        }
    }

    // ---- confirm ----

    /// <summary>
    /// The member's consent, as one insert. The actor must be one of the pair; the plan must have no blocks and be the one the member
    /// saw (digest); every proof must be of its side's Heimdall account. Calls no Heimdall: the caller runs <see cref="AdvanceAsync"/>.
    /// </summary>
    public async Task<ConfirmOutcome> ConfirmAsync(ConfirmMergeRequest request, CancellationToken cancellationToken)
    {
        RequireProofs(request.SurvivorProof, request.FoldedProofs);
        if (request.ActorUserAccountId != request.SurvivorUserAccountId && request.ActorUserAccountId != request.FoldedUserAccountId)
        {
            return new ConfirmOutcome.NotParty();
        }

        try
        {
            return await AccountMergeLedger.WriteAsAsync(dbContext, request.ActorUserAccountId, _ => InsertConfirmedAsync(request, cancellationToken), cancellationToken);
        }
        catch (DbUpdateException error) when (IsLiveMergeViolation(error))
        {
            dbContext.ChangeTracker.Clear();
            return new ConfirmOutcome.Blocked([MergeInFlight]);
        }
    }

    private async Task<ConfirmOutcome> InsertConfirmedAsync(ConfirmMergeRequest request, CancellationToken cancellationToken)
    {
        var plan = await ledger.PlanAsync(request.SurvivorUserAccountId, request.FoldedUserAccountId, cancellationToken);
        if (plan.Blocks.Count > 0)
        {
            return new ConfirmOutcome.Blocked(plan.Blocks);
        }

        if (!string.Equals(plan.Digest(request.SurvivorUserAccountId, request.FoldedUserAccountId), request.PreviewDigest, StringComparison.Ordinal))
        {
            return new ConfirmOutcome.PreviewStale();
        }

        var survivorHeimdall = await HeimdallAccountAsync(request.SurvivorUserAccountId, cancellationToken);
        var foldedHeimdall = await HeimdallAccountAsync(request.FoldedUserAccountId, cancellationToken);
        if (request.SurvivorProof.HeimdallAccountId != survivorHeimdall || request.FoldedProofs.Any(x => x.HeimdallAccountId != foldedHeimdall))
        {
            return new ConfirmOutcome.ProofAccountMismatch();
        }

        var now = timeProvider.GetUtcNow();
        var merge = new AccountMerge
        {
            State = AccountMergeState.Confirmed,
            SurvivorUserAccountId = request.SurvivorUserAccountId,
            FoldedUserAccountId = request.FoldedUserAccountId,
            CreatedUtc = now,
            ConfirmedByUserAccountId = request.ActorUserAccountId,
            ConfirmedUtc = now,
            PreviewDigest = request.PreviewDigest,
            SurvivorProofProvider = request.SurvivorProof.Provider,
            FoldedProofProviders = string.Join(',', request.FoldedProofs.Select(x => x.Provider).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
            // Heimdall's idempotency key: random, minted once here, read back from the row on every call, never derived from the merge id.
            HeimdallMoveId = Guid.NewGuid().ToString("N"),
            SealedProofs = Seal(request.FoldedProofs.Select(x => x.AccessToken), request.SurvivorProof.AccessToken),
            LastOutcome = "confirmed"
        };
        dbContext.AccountMerges.Add(merge);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Account merge {MergeId} {State}", merge.Id, merge.State);
        return new ConfirmOutcome.Confirmed(merge.Id);
    }

    // ---- undo request ----

    /// <summary>
    /// The survivor's request to give the moved logins back, inside the grace and with fresh survivor proofs. One save: UndoRequested, a
    /// freshly minted reverse move id, the sealed proofs. Losing the race to the Final sweep is the State token's, answered GraceOver.
    /// </summary>
    public async Task<UndoOutcome> RequestUndoAsync(
        Guid mergeId, Guid actorUserAccountId, IReadOnlyList<MergeProof> survivorProofs, CancellationToken cancellationToken)
    {
        RequireProofs(null, survivorProofs);
        try
        {
            return await AccountMergeLedger.WriteAsAsync(dbContext, actorUserAccountId, _ => WriteUndoRequestedAsync(mergeId, actorUserAccountId, survivorProofs, cancellationToken), cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            return new UndoOutcome.GraceOver();
        }
    }

    private async Task<UndoOutcome> WriteUndoRequestedAsync(
        Guid mergeId, Guid actorUserAccountId, IReadOnlyList<MergeProof> survivorProofs, CancellationToken cancellationToken)
    {
        var merge = await LoadAsync(mergeId, cancellationToken);
        if (merge is null)
        {
            return new UndoOutcome.NotCommitted();
        }

        var now = timeProvider.GetUtcNow();
        if (actorUserAccountId != merge.SurvivorUserAccountId)
        {
            return new UndoOutcome.NotSurvivor();
        }

        if (merge.State == AccountMergeState.Final || merge.FinalizedUtc is not null)
        {
            return new UndoOutcome.GraceOver();
        }

        if (merge.State != AccountMergeState.Committed)
        {
            return new UndoOutcome.NotCommitted();
        }

        if (merge.GraceEndsUtc is not { } graceEnds || now >= graceEnds)
        {
            return new UndoOutcome.GraceOver();
        }

        var survivorHeimdall = await HeimdallAccountAsync(merge.SurvivorUserAccountId, cancellationToken);
        if (survivorProofs.Any(x => x.HeimdallAccountId != survivorHeimdall))
        {
            return new UndoOutcome.ProofAccountMismatch();
        }

        merge.State = AccountMergeState.UndoRequested;
        merge.UndoRequestedUtc = now;
        merge.HeimdallReverseMoveId = Guid.NewGuid().ToString("N");
        merge.SealedProofs = Seal(survivorProofs.Select(x => x.AccessToken), null);
        merge.Attempts = 0;
        merge.NextAttemptUtc = null;
        merge.LastOutcome = "undo_requested";
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Account merge {MergeId} {State}", merge.Id, merge.State);
        return new UndoOutcome.Requested();
    }

    // ---- the step function ----

    /// <summary>
    /// The one step function: confirm's inline advance, the sweeper's resume after a crash and an undo request all run it. From the
    /// stored state it makes the Heimdall move (or reversal) under the stored id, records the answer, and calls the ledger's commit (or
    /// local undo). Idempotent: every state has one next step and every step is safe to repeat. Other states are a no-op.
    /// </summary>
    public async Task<AccountMerge> AdvanceAsync(Guid mergeId, CancellationToken cancellationToken)
    {
        for (var lost = 0; ; lost++)
        {
            try
            {
                return await AdvanceOnceAsync(mergeId, cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (lost < 2)
            {
                // Another advancer saved first: forget what this context staged and continue from the stored state.
                dbContext.ChangeTracker.Clear();
            }
        }
    }

    private async Task<AccountMerge> AdvanceOnceAsync(Guid mergeId, CancellationToken cancellationToken)
    {
        var merge = await LoadAsync(mergeId, cancellationToken) ?? throw new InvalidOperationException($"Merge {mergeId} does not exist.");
        if (merge.State is AccountMergeState.Confirmed or AccountMergeState.UndoRequested)
        {
            merge = await MoveAsync(merge, cancellationToken);
        }

        return merge.State switch
        {
            AccountMergeState.HeimdallMoved => await ledger.CommitAsync(merge.Id, cancellationToken),
            AccountMergeState.HeimdallReversed => await ledger.UndoLocalAsync(merge.Id, cancellationToken),
            _ => merge
        };
    }

    private async Task<AccountMerge> MoveAsync(AccountMerge merge, CancellationToken cancellationToken)
    {
        if (merge.LastOutcome == ReceiptMismatch)
        {
            return merge; // Heimdall answered, wrongly, and its proofs are wiped: replay can only repeat the same answer.
        }

        var forward = merge.State == AccountMergeState.Confirmed;
        var now = timeProvider.GetUtcNow();
        if (Open(merge.SealedProofs) is not { } proofs)
        {
            // The key ring is lost or rotated out. Heimdall may already have moved, so nothing changes but the retry time.
            merge.LastOutcome = ProofsUnreadable;
            merge.NextAttemptUtc = now + UnreadableRetry;
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogError("Account merge {MergeId} {State}: sealed proofs unreadable", merge.Id, merge.State);
            return merge;
        }

        var request = forward
            ? new HeimdallMoveRequest(merge.HeimdallMoveId!, proofs.From, proofs.To)
            : new HeimdallMoveRequest(merge.HeimdallReverseMoveId!, proofs.From, ReversesMoveId: merge.HeimdallMoveId);
        var outcome = await mover.MoveAsync(request, cancellationToken);
        var code = outcome is HeimdallMoveOutcome.Refused refused ? RefusalCode(refused.Code) : null;
        switch (outcome)
        {
            case HeimdallMoveOutcome.Moved moved:
                // The leg's destination is the survivor going forward and the folded account coming back.
                var destination = await HeimdallAccountAsync(forward ? merge.SurvivorUserAccountId : merge.FoldedUserAccountId, cancellationToken);
                merge.SealedProofs = null;
                if (moved.Receipt.AccountId != destination)
                {
                    merge.LastOutcome = ReceiptMismatch;
                    break;
                }

                merge.Attempts = 0;
                merge.NextAttemptUtc = null;
                merge.LastOutcome = "moved";
                if (forward)
                {
                    merge.State = AccountMergeState.HeimdallMoved;
                    merge.ProofFromTier = Tier(moved.Receipt.ProofFrom);
                    merge.ProofToTier = moved.Receipt.ProofTo is { } to ? Tier(to) : null;
                    merge.MovedIdentityIds = JsonSerializer.Serialize(moved.Receipt.MovedIdentityIds);
                }
                else
                {
                    merge.State = AccountMergeState.HeimdallReversed;
                    merge.ReversedUtc = now;
                }

                break;
            case HeimdallMoveOutcome.Refused:
                // Heimdall applied nothing before a 4xx. A forward refusal ends the merge; a refused undo leaves it as it was.
                merge.SealedProofs = null;
                merge.NextAttemptUtc = null;
                if (forward)
                {
                    merge.State = AccountMergeState.Failed;
                    merge.FailedUtc = now;
                    merge.FailureCode = code!;
                    merge.LastOutcome = "refused";
                }
                else
                {
                    merge.State = AccountMergeState.Committed;
                    merge.LastOutcome = "undo_" + code;
                }

                break;
            case HeimdallMoveOutcome.Unknown unknown:
                merge.Attempts++;
                merge.NextAttemptUtc = now + Backoff[Math.Min(merge.Attempts, Backoff.Length) - 1];
                merge.LastOutcome = unknown.Reason.Length > 64 ? unknown.Reason[..64] : unknown.Reason;
                break;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Account merge {MergeId} {State}: {Outcome} {Code}", merge.Id, merge.State, outcome.GetType().Name, code);
        return merge;
    }

    /// <summary>Advances every in-flight merge that is due, oldest first. One that throws is logged and skipped; returns how many were advanced.</summary>
    public async Task<int> AdvanceDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var due = await dbContext.AccountMerges.AsNoTracking()
            .Where(x => DueStates.Contains(x.State) && (x.NextAttemptUtc == null || x.NextAttemptUtc <= now))
            .OrderBy(x => x.CreatedUtc)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var advanced = 0;
        foreach (var id in due)
        {
            try
            {
                await AdvanceAsync(id, cancellationToken);
                advanced++;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                dbContext.ChangeTracker.Clear();
                logger.LogWarning("Account merge {MergeId} advance failed: {Error}", id, error.GetType().Name);
            }
        }

        return advanced;
    }

    // ---- operator settle ----

    public Task<SettleOutcome> SettleStuckAsync(
        Guid mergeId, SettleChoice choice, string typedMoveId, Guid operatorUserAccountId, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    // ---- helpers ----

    /// <summary>The merge as stored now: a row this context already tracks is reloaded, never trusted.</summary>
    private async Task<AccountMerge?> LoadAsync(Guid mergeId, CancellationToken cancellationToken)
    {
        var tracked = dbContext.AccountMerges.Local.FirstOrDefault(x => x.Id == mergeId);
        if (tracked is not null)
        {
            await dbContext.Entry(tracked).ReloadAsync(cancellationToken);
            return dbContext.Entry(tracked).State == EntityState.Detached ? null : tracked;
        }

        return await dbContext.AccountMerges.SingleOrDefaultAsync(x => x.Id == mergeId, cancellationToken);
    }

    private Task<string> HeimdallAccountAsync(Guid userAccountId, CancellationToken cancellationToken) =>
        dbContext.UserAccounts.AsNoTracking().Where(x => x.Id == userAccountId).Select(x => x.HeimdallAccountId).SingleAsync(cancellationToken);

    /// <summary>Shapes Heimdall would 400 are refused before a row exists, so a confirm can never store a merge that cannot call. Messages name no value.</summary>
    private static void RequireProofs(MergeProof? one, IReadOnlyList<MergeProof> many)
    {
        if (many is not { Count: >= 1 and <= MaxProofs })
        {
            throw new ArgumentException("1 to 8 proofs are required", nameof(many));
        }

        foreach (var proof in one is null ? many : [one, .. many])
        {
            if (string.IsNullOrEmpty(proof.AccessToken) || proof.AccessToken.Length > HeimdallIdentityMoveClient.MaxProofLength
                || string.IsNullOrEmpty(proof.HeimdallAccountId) || string.IsNullOrEmpty(proof.Provider) || proof.Provider.Length > 32)
            {
                throw new ArgumentException("a proof is malformed", nameof(many));
            }
        }

        if (many.Select(x => x.Provider).Distinct(StringComparer.Ordinal).Sum(x => x.Length + 1) > 121)
        {
            throw new ArgumentException("the proofs' providers do not fit the record", nameof(many));
        }
    }

    private string Seal(IEnumerable<string> from, string? to) => _seal.Protect(JsonSerializer.Serialize(new ProofSeal([.. from], to)));

    private ProofSeal? Open(string? sealedProofs)
    {
        if (sealedProofs is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ProofSeal>(_seal.Unprotect(sealedProofs)) is { From.Count: > 0 } proofs ? proofs : null;
        }
        catch (Exception error) when (error is CryptographicException or JsonException)
        {
            return null;
        }
    }

    private static string Tier(HeimdallProofTier tier) => tier == HeimdallProofTier.Strong ? "strong" : "weak";

    private static string RefusalCode(HeimdallMoveRefusal code) =>
        code == HeimdallMoveRefusal.RequestInvalid ? "invalid_request" : WordBoundary().Replace(code.ToString(), "_$1").ToLowerInvariant();

    [GeneratedRegex("(?<!^)([A-Z])")]
    private static partial Regex WordBoundary();

    /// <summary>The database's refusal of an overlapping merge: the live-merge unique index or the chain trigger's exclusion violation.</summary>
    private static bool IsLiveMergeViolation(DbUpdateException error)
    {
        for (var current = (Exception?)error; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: "23P01" } or PostgresException { SqlState: "23505", ConstraintName: LiveMergeIndex })
            {
                return true;
            }
        }

        return false;
    }
}
