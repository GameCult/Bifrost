using System.Security.Claims;
using System.Text.Json;
using Bifrost.Web.Configuration;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Heimdall;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Features.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Pages.Membership;

/// <summary>
/// The member's own merge page: the merges that kept this account (up to five) and the one its frozen or folded standing names, in
/// <see cref="MergeWording"/>'s closed words, and the undo. It reads merge rows and writes none: undo is the saga's
/// <see cref="AccountMergeSaga.RequestUndoAsync"/> followed by its own <see cref="AccountMergeSaga.AdvanceAsync"/>, with fresh proofs
/// of every moved login held by <see cref="MergeProofs"/>. A person who signed in through a login that moved is the survivor here, so
/// there is no separate folded-side undo. Nothing the query, the form or the store holds is shown except as a closed code's words, and
/// no account name, id, email or login of either side is rendered.
/// </summary>
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MergeStatusModel(
    ICurrentBifrostActorAccessor actors,
    BifrostDbContext dbContext,
    AccountMergeSaga saga,
    MergeProofs proofs,
    HeimdallAuthService heimdallAuthService,
    IOptions<BifrostHostOptions> hostOptions,
    TimeProvider timeProvider,
    ILogger<MergeStatusModel> logger) : PageModel
{
    private const int MaxListed = 5;
    private const string UndoParameter = "undo";
    private const string LoginsUnproved = "logins_unproved";

    public sealed record MergeRow(
        Guid Id,
        string State,
        string? Why,
        string? Outcome,
        string? Confirmed,
        string? Committed,
        string? GraceEnds,
        string? Final,
        string? DaysLeft,
        string? TierNote,
        IReadOnlyList<string> SurvivorProofs,
        IReadOnlyList<string> FoldedProofs,
        bool CanUndo,
        bool Ready,
        IReadOnlyList<string> NeedProof);

    public IReadOnlyList<MergeRow> Merges { get; private set; } = [];

    public string? ResultMessage { get; private set; }

    public string? UndoMessage { get; private set; }

    public static string ProviderName(string provider) =>
        HeimdallAuthService.LinkableProviders.Contains(provider) ? LinkedAccountsModel.ProviderName(provider) : provider == "passkey" ? "Passkey" : "another provider";

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        ResultMessage = HeimdallLinkOutcomes.Wording(Request.Query[HeimdallLinkOutcomes.ResultParameter].ToString());
        UndoMessage = MergeWording.Undo(Request.Query[UndoParameter].ToString()) is { Length: > 0 } words ? words : null;

        var actor = await actors.GetAsync(cancellationToken);
        var me = actor.UserAccount?.Id;
        var rows = me is { } id
            ? await dbContext.AccountMerges.AsNoTracking().Where(x => x.SurvivorUserAccountId == id).OrderByDescending(x => x.CreatedUtc).Take(MaxListed).ToListAsync(cancellationToken)
            : [];
        if (actor.Merge is { } standing && rows.All(x => x.Id != standing.MergeId)
            && await dbContext.AccountMerges.AsNoTracking().SingleOrDefaultAsync(x => x.Id == standing.MergeId, cancellationToken) is { } named)
        {
            rows.Add(named);
        }

        Merges = [.. rows.Select(x => Row(x, me))];
    }

    /// <summary>Starts a fresh sign-in of this account through <paramref name="via"/>, held in memory until the undo takes it.</summary>
    public async Task<IActionResult> OnPostProveForUndoAsync(string? via, CancellationToken cancellationToken)
    {
        if (User.FindFirstValue(BifrostClaimTypes.HeimdallAccountId) is not { Length: > 0 } accountId)
        {
            return Result(HeimdallLinkOutcome.Error);
        }

        try
        {
            var started = await heimdallAuthService.StartMergeProveSelfAsync(
                accountId, via, HeimdallLinkReturn, hostOptions.Value.PublicBaseUrl, Request.Cookies, cancellationToken);
            Response.Cookies.Append(started.CookieName, started.CookieValue, HeimdallAttemptCookie.Options());
            return Redirect(started.AuthorizationUrl.ToString());
        }
        catch (HeimdallAuthException error)
        {
            logger.LogWarning("Merge undo proof start refused: {Reason}", error.Message);
            return Result(HeimdallLinkOutcome.Error);
        }
    }

    /// <summary>Asks the saga to undo one merge with the proofs this member holds, then runs its step function once, as the sweeper would.</summary>
    public async Task<IActionResult> OnPostUndoAsync(string? mergeId, CancellationToken cancellationToken)
    {
        var actor = await actors.GetAsync(cancellationToken);
        if (actor.UserAccount is not { } me || !Guid.TryParse(mergeId, out var id)
            || await dbContext.AccountMerges.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken) is not { } merge
            || merge.SurvivorUserAccountId != me.Id)
        {
            return Undone("not_survivor");
        }

        if (proofs.PeekUndo(me.Id, MovedIds(merge)) is not UndoPeekResult.Ready { Proofs.Count: > 0 } ready)
        {
            return Undone(LoginsUnproved);
        }

        var outcome = await saga.RequestUndoAsync(id, me.Id, ready.Proofs, cancellationToken);
        if (outcome is UndoOutcome.Requested)
        {
            proofs.Clear(me.Id);
            try
            {
                await saga.AdvanceAsync(id, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // The saga's stored state is the truth and the sweeper resumes it.
                logger.LogWarning("Account merge {MergeId} inline advance failed: {Error}", id, error.GetType().Name);
            }
        }

        return Undone(MergeWording.UndoCode(outcome));
    }

    private const string HeimdallLinkReturn = "/Membership/MergeStatus";

    private MergeRow Row(AccountMerge merge, Guid? me)
    {
        var now = timeProvider.GetUtcNow();
        var open = merge.State == AccountMergeState.Committed && merge.GraceEndsUtc is { } end && now < end;
        var canUndo = open && me == merge.SurvivorUserAccountId;
        var ready = false;
        IReadOnlyList<string> need = [];
        if (canUndo)
        {
            switch (proofs.PeekUndo(me!.Value, MovedIds(merge)))
            {
                case UndoPeekResult.Ready { Proofs.Count: > 0 }:
                    ready = true;
                    break;
                case UndoPeekResult.LoginsUnproved { Providers.Count: > 0 } unproved:
                    need = unproved.Providers;
                    break;
                default:
                    need = HeimdallAuthService.LinkableProviders;
                    break;
            }
        }

        var inGrace = merge.State is AccountMergeState.Committed or AccountMergeState.UndoRequested && merge.GraceEndsUtc > now;
        return new MergeRow(
            merge.Id,
            MergeWording.State(merge.State),
            merge.State == AccountMergeState.Failed ? MergeWording.Failure(merge.FailureCode) : null,
            merge.State is AccountMergeState.Committed or AccountMergeState.Failed or AccountMergeState.Confirmed or AccountMergeState.HeimdallMoved
                or AccountMergeState.UndoRequested or AccountMergeState.HeimdallReversed
                ? MergeWording.Outcome(merge.LastOutcome)
                : null,
            merge.ConfirmedUtc is { } confirmed ? MergeWording.Date(confirmed) : null,
            merge.CommittedUtc is { } committed ? MergeWording.Date(committed) : null,
            merge.GraceEndsUtc is { } graceEnds ? MergeWording.Date(graceEnds) : null,
            merge.FinalizedUtc is { } final ? MergeWording.Date(final) : null,
            inGrace && merge.GraceEndsUtc is { } until ? MergeWording.DaysLeft(MergeWording.DaysUntil(until, now)) : null,
            inGrace && merge.GraceEndsUtc is { } by ? MergeWording.TierNote(merge.ProofFromTier, by) : null,
            merge.SurvivorProofProvider.Length > 0 ? [ProviderName(merge.SurvivorProofProvider)] : [],
            [.. merge.FoldedProofProviders.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(ProviderName)],
            canUndo,
            ready,
            need);
    }

    private static IReadOnlyCollection<string> MovedIds(AccountMerge merge)
    {
        try
        {
            return merge.MovedIdentityIds is { } json ? JsonSerializer.Deserialize<string[]>(json) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private IActionResult Undone(string code) =>
        RedirectToPage(null, new Dictionary<string, string?> { [UndoParameter] = code });

    private IActionResult Result(HeimdallLinkOutcome outcome) =>
        RedirectToPage(null, new Dictionary<string, string?> { [HeimdallLinkOutcomes.ResultParameter] = HeimdallLinkOutcomes.Code(outcome) });
}
