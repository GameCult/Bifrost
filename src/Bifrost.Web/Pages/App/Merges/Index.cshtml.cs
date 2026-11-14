using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Features.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Web.Pages.App.Merges;

/// <summary>One in-flight merge the sweeper has stopped advancing, as an operator may see it: no account, Heimdall id or login of either side.</summary>
public sealed record StuckMerge(Guid Id, AccountMergeState State, string LastOutcome, int Attempts, string MoveId, bool CanSettle);

/// <summary>
/// The operator's door for a stuck merge (ruling merge-stuck-end-only-typed-id), platform admins only. It lists in-flight merges that
/// stopped on a settleable outcome or have been retried eight times, with the stored move id of the leg in flight, and passes an
/// admin's choice and the typed move id to <see cref="AccountMergeSaga.SettleStuckAsync"/>, the one writer. It calls no Heimdall.
/// </summary>
public sealed class IndexModel(BifrostDbContext dbContext, ICurrentBifrostActorAccessor actorAccessor, AccountMergeSaga saga) : PageModel
{
    private const int RetriesBeforeListed = 8;

    public const string Url = "/App/Merges";

    private static readonly IReadOnlyDictionary<string, string> ResultWords = new Dictionary<string, string>
    {
        ["settled"] = "The merge was ended and the sweeper will not touch it again.",
        ["not_stuck"] = "Nothing changed: that merge is not stuck on a state this choice can end.",
        ["wrong_move_id"] = "Nothing changed: the move id typed is not the stored one of that leg.",
        ["lost"] = "Nothing changed: the merge moved on by itself while you were settling it."
    };

    public IReadOnlyList<StuckMerge> Merges { get; private set; } = [];

    /// <summary>The words for the closed <c>?result=</c> code of the last settle; null for no code or one this page does not know.</summary>
    public string? Notice { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? result, CancellationToken cancellationToken)
    {
        if (!(await actorAccessor.GetAsync(cancellationToken)).IsPlatformAdmin)
        {
            return Forbid();
        }

        Notice = result is not null && ResultWords.TryGetValue(result, out var words) ? words : null;
        var rows = await dbContext.AccountMerges.AsNoTracking()
            .Where(x => (x.State == AccountMergeState.Confirmed || x.State == AccountMergeState.UndoRequested)
                && (AccountMergeSaga.SettleableOutcomes.Contains(x.LastOutcome) || x.Attempts >= RetriesBeforeListed))
            .OrderBy(x => x.CreatedUtc)
            .ToListAsync(cancellationToken);
        Merges = [.. rows.Select(x => new StuckMerge(
            x.Id, x.State, x.LastOutcome, x.Attempts,
            (x.State == AccountMergeState.UndoRequested ? x.HeimdallReverseMoveId : x.HeimdallMoveId) ?? string.Empty,
            AccountMergeSaga.IsSettleable(x)))];
        return Page();
    }

    public Task<IActionResult> OnPostFailForwardAsync(Guid mergeId, string? moveId, CancellationToken cancellationToken) =>
        SettleAsync(mergeId, SettleChoice.FailForward, moveId, cancellationToken);

    public Task<IActionResult> OnPostAbandonUndoAsync(Guid mergeId, string? moveId, CancellationToken cancellationToken) =>
        SettleAsync(mergeId, SettleChoice.AbandonUndo, moveId, cancellationToken);

    private async Task<IActionResult> SettleAsync(Guid mergeId, SettleChoice choice, string? moveId, CancellationToken cancellationToken)
    {
        var actor = await actorAccessor.GetAsync(cancellationToken);
        if (!actor.CanManageMembership || actor.UserAccount is null)
        {
            return Forbid();
        }

        var outcome = await saga.SettleStuckAsync(mergeId, choice, moveId ?? string.Empty, actor.UserAccount.Id, cancellationToken);
        var code = outcome switch
        {
            SettleOutcome.Settled => "settled",
            SettleOutcome.WrongMoveId => "wrong_move_id",
            SettleOutcome.Lost => "lost",
            _ => "not_stuck"
        };
        return Redirect($"{Url}?result={code}");
    }
}
