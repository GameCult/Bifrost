using System.Security.Claims;
using Bifrost.Web.Configuration;
using Bifrost.Web.Data;
using Bifrost.Web.Features.Heimdall;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Features.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Pages.Membership;

/// <summary>
/// Where a member merges two accounts: proves both by fresh sign-ins, chooses which one stays against a preview, ticks one box for each
/// account and confirms. The page owns only that choice and that consent. Its stage is derived from <see cref="MergeProofs"/> and the
/// query on every request, nothing about progress is kept in a cookie or a hidden field except the chosen direction and the preview's
/// digest, and both are re-checked by the saga. The other account is named (by display name only) once both proofs are held, and
/// found by no email, name or login string: it is the account Heimdall identified from the second proof. The page writes no merge,
/// vote, membership or journal row: <see cref="AccountMergeSaga.ConfirmAsync"/> inserts the one Confirmed row that is the consent.
/// </summary>
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MergeAccountsModel(
    ICurrentBifrostActorAccessor actors,
    BifrostDbContext dbContext,
    AccountMergeLedger ledger,
    AccountMergeSaga saga,
    MergeProofs proofs,
    HeimdallAuthService heimdallAuthService,
    IOptions<BifrostHostOptions> hostOptions,
    IOptions<AccountMergeOptions> mergeOptions,
    ILogger<MergeAccountsModel> logger) : PageModel
{
    private const string KeepParameter = "keep";
    private const string MergeParameter = "merge";
    private const string Self = "self";
    private const string Other = "other";

    public enum MergeStage
    {
        /// <summary>One side or both lack a proof: nothing is named.</summary>
        Prove,

        /// <summary>Both sides are proved: the member chooses which stays.</summary>
        Choose,

        /// <summary>A direction is chosen and the folded account's remaining logins are asked one sign-in each.</summary>
        ProveRemaining,

        /// <summary>Everything the saga needs is held: the confirm form.</summary>
        Confirm
    }

    public MergeStage Stage { get; private set; } = MergeStage.Prove;

    public string? ResultMessage { get; private set; }

    public string? ConfirmMessage { get; private set; }

    public bool SelfProved { get; private set; }

    public bool OtherProved { get; private set; }

    /// <summary>Display names, set only once both sides are proved.</summary>
    public string? SelfName { get; private set; }

    public string? OtherName { get; private set; }

    /// <summary>The chosen direction, "self" or "other", or null.</summary>
    public string? Keep { get; private set; }

    public string? KeptName => Keep == Self ? SelfName : Keep == Other ? OtherName : null;

    public string? FoldedName => Keep == Self ? OtherName : Keep == Other ? SelfName : null;

    public IReadOnlyList<string> PreviewLines { get; private set; } = [];

    public IReadOnlyList<string> BlockWords { get; private set; } = [];

    public IReadOnlyList<string> Statements => MergePreview.Statements;

    public IReadOnlyList<string> ProvidersToProve { get; private set; } = [];

    public string Digest { get; private set; } = string.Empty;

    public IReadOnlyList<string> LinkableProviders => HeimdallAuthService.LinkableProviders;

    public static string ProviderName(string provider) => MergeStatusModel.ProviderName(provider);

    private string? AccountId => User.FindFirstValue(BifrostClaimTypes.HeimdallAccountId) is { Length: > 0 } id ? id : null;

    /// <summary>Off, the page does not exist; a folded or frozen account is forbidden it, as it is Linked accounts.</summary>
    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        if (!mergeOptions.Value.Enabled)
        {
            context.Result = NotFound();
            return;
        }

        var actor = await actors.GetAsync(context.HttpContext.RequestAborted);
        if (actor.Merge is not null || actor.UserAccount is null || AccountId is null)
        {
            context.Result = Forbid();
            return;
        }

        await base.OnPageHandlerExecutionAsync(context, next);
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        ResultMessage = HeimdallLinkOutcomes.Wording(Request.Query[HeimdallLinkOutcomes.ResultParameter].ToString());
        ConfirmMessage = MergeWording.Confirm(Request.Query[MergeParameter].ToString()) is { Length: > 0 } words ? words : null;

        var me = (await actors.GetAsync(cancellationToken)).UserAccount!.Id;
        var held = proofs.Snapshot(me);
        SelfProved = held.Self is not null;
        OtherProved = held.Other is not null;
        if (held.Self is not { } self || held.Other is not { } other)
        {
            return;
        }

        var names = await dbContext.UserAccounts.AsNoTracking()
            .Where(x => x.Id == self.UserAccountId || x.Id == other.UserAccountId)
            .ToDictionaryAsync(x => x.Id, x => x.DisplayName, cancellationToken);
        SelfName = names.GetValueOrDefault(self.UserAccountId);
        OtherName = names.GetValueOrDefault(other.UserAccountId);
        Stage = MergeStage.Choose;
        if (SideOf(Request.Query[KeepParameter].ToString()) is not { } survivorSide)
        {
            return;
        }

        Keep = survivorSide == MergeSide.Self ? Self : Other;
        var (survivor, folded) = survivorSide == MergeSide.Self ? (self, other) : (other, self);
        var plan = await ledger.PlanAsync(survivor.UserAccountId, folded.UserAccountId, cancellationToken);
        Digest = plan.Digest(survivor.UserAccountId, folded.UserAccountId);
        PreviewLines = MergePreview.Lines(plan);
        BlockWords = [.. plan.Blocks.Select(MergePreview.Block)];
        switch (proofs.Peek(me, survivorSide))
        {
            case PeekResult.Ready when plan.Blocks.Count == 0:
                Stage = MergeStage.Confirm;
                break;
            case PeekResult.LoginsUnproved unproved:
                ProvidersToProve = unproved.Providers;
                Stage = MergeStage.ProveRemaining;
                break;
        }
    }

    /// <summary>Starts a fresh sign-in of the account signed in here.</summary>
    public Task<IActionResult> OnPostProveSelfAsync(string? via, CancellationToken cancellationToken) => BeginAsync(MergeSide.Self, via, cancellationToken);

    /// <summary>Starts a fresh sign-in of the other account. The member names the provider: Bifrost never guesses one.</summary>
    public Task<IActionResult> OnPostProveOtherAsync(string? via, CancellationToken cancellationToken) => BeginAsync(MergeSide.Other, via, cancellationToken);

    /// <summary>Starts the next sign-in the chosen folded account still owes; its side follows from the chosen direction.</summary>
    public Task<IActionResult> OnPostProveRemainingAsync(string? via, string? keep, CancellationToken cancellationToken) =>
        SideOf(keep) is { } survivorSide
            ? BeginAsync(survivorSide == MergeSide.Self ? MergeSide.Other : MergeSide.Self, via, cancellationToken)
            : Task.FromResult(Refused(null, "bad_choice"));

    /// <summary>
    /// The member's consent: both boxes, the direction and the digest of the preview they saw. A refusal writes nothing and keeps the
    /// proofs; only a Confirmed row spends them.
    /// </summary>
    public async Task<IActionResult> OnPostConfirmAsync(string? keep, string? digest, bool consentSelf, bool consentOther, CancellationToken cancellationToken)
    {
        if (SideOf(keep) is not { } survivorSide)
        {
            return Refused(null, "bad_choice");
        }

        if (!consentSelf || !consentOther)
        {
            return Refused(keep, "consent_needed");
        }

        var me = (await actors.GetAsync(cancellationToken)).UserAccount!.Id;
        var held = proofs.Snapshot(me);
        if (held.Self is not { } self || held.Other is not { } other || proofs.Peek(me, survivorSide) is not PeekResult.Ready ready)
        {
            return Refused(keep, "proofs_needed");
        }

        var (survivor, folded) = survivorSide == MergeSide.Self ? (self, other) : (other, self);
        var outcome = await saga.ConfirmAsync(
            new ConfirmMergeRequest(me, survivor.UserAccountId, folded.UserAccountId, digest ?? string.Empty, ready.Survivor, ready.Folded), cancellationToken);
        switch (outcome)
        {
            case ConfirmOutcome.Confirmed confirmed:
                proofs.Clear(me);
                try
                {
                    await saga.AdvanceAsync(confirmed.MergeId, cancellationToken);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // The stored state is the truth and the sweeper resumes it.
                    logger.LogWarning("Account merge {MergeId} inline advance failed: {Error}", confirmed.MergeId, error.GetType().Name);
                }

                return RedirectToPage("/Membership/MergeStatus");
            case ConfirmOutcome.Blocked:
                return Refused(keep, "blocked");
            case ConfirmOutcome.PreviewStale:
                return Refused(keep, "preview_stale");
            case ConfirmOutcome.NotParty:
                return Refused(keep, "not_party");
            default:
                return Refused(keep, "proof_account_mismatch");
        }
    }

    private static MergeSide? SideOf(string? keep) => keep switch
    {
        Self => MergeSide.Self,
        Other => MergeSide.Other,
        _ => null
    };

    private async Task<IActionResult> BeginAsync(MergeSide side, string? via, CancellationToken cancellationToken)
    {
        try
        {
            var start = side == MergeSide.Self
                ? (Func<string, Task<HeimdallAuthStartResult>>)(id => heimdallAuthService.StartMergeProveSelfAsync(
                    id, via, ReturnPath, hostOptions.Value.PublicBaseUrl, Request.Cookies, cancellationToken))
                : id => heimdallAuthService.StartMergeProveOtherAsync(
                    id, via, ReturnPath, hostOptions.Value.PublicBaseUrl, Request.Cookies, cancellationToken);
            var started = await start(AccountId!);
            Response.Cookies.Append(started.CookieName, started.CookieValue, HeimdallAttemptCookie.Options());
            return Redirect(started.AuthorizationUrl.ToString());
        }
        catch (HeimdallAuthException error)
        {
            logger.LogWarning("Merge proof start refused: {Reason}", error.Message);
            return RedirectToPage(null, new Dictionary<string, string?> { [HeimdallLinkOutcomes.ResultParameter] = HeimdallLinkOutcomes.Code(HeimdallLinkOutcome.Error) });
        }
    }

    private const string ReturnPath = "/Membership/MergeAccounts";

    private IActionResult Refused(string? keep, string code)
    {
        var query = new Dictionary<string, string?>();
        if (keep is Self or Other)
        {
            query[KeepParameter] = keep;
        }

        query[MergeParameter] = code;
        return RedirectToPage(null, query);
    }
}
