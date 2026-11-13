using System.Security.Claims;
using Bifrost.Web.Configuration;
using Bifrost.Web.Features.Heimdall;
using Bifrost.Web.Features.Membership;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Pages.Membership;

/// <summary>
/// The member's provider logins (docs/linked-accounts.md). Bifrost keeps no Heimdall token, so every action here begins a
/// fresh Heimdall sign-in for the account already signed in and finishes inside that sign-in's completion
/// (<c>POST /auth/heimdall/complete</c>); this page only starts them and shows what the last read found. Every handler is a
/// POST with antiforgery. Nothing from the query or the form is ever shown: the outcome is a code from a closed set.
/// </summary>
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class LinkedAccountsModel(
    HeimdallAuthService heimdallAuthService,
    LinkedAccountsSnapshots snapshots,
    IOptions<BifrostHostOptions> hostOptions,
    ILogger<LinkedAccountsModel> logger) : PageModel
{
    private static readonly Dictionary<string, string> ProviderNames = new(StringComparer.Ordinal)
    {
        ["discord"] = "Discord",
        ["patreon"] = "Patreon",
        ["google"] = "Google"
    };

    private string? AccountId => User.FindFirstValue(BifrostClaimTypes.HeimdallAccountId) is { Length: > 0 } id ? id : null;

    /// <summary>The wording of the outcome the page was sent back with: a closed set, null for anything else.</summary>
    public string? ResultMessage { get; private set; }

    /// <summary>Whether this session is a Heimdall account. A Bifrost-native GitHub session has none, so there is nothing to list.</summary>
    public bool HasHeimdallAccount => AccountId is not null;

    /// <summary>The logins the last read found, or null when there is none or it has expired.</summary>
    public IReadOnlyList<LinkedIdentitySummary>? Identities { get; private set; }

    /// <summary>Whether a fresh Google sign-in can be had for this account: unlinking needs one, and only Google can give it.</summary>
    public bool CanProve => Identities?.Any(x => x.Provider == HeimdallAuthService.StepUpProvider) == true;

    public IReadOnlyList<string> LinkableProviders => HeimdallAuthService.LinkableProviders;

    public static string ProviderName(string provider) => ProviderNames.GetValueOrDefault(provider, provider);

    public void OnGet()
    {
        ResultMessage = HeimdallLinkOutcomes.Wording(Request.Query[HeimdallLinkOutcomes.ResultParameter].ToString());
        Identities = AccountId is { } id ? snapshots.Get(id) : null;
    }

    /// <summary>Reads the account's logins: an ordinary Heimdall sign-in, then the list inside its completion.</summary>
    public Task<IActionResult> OnPostRefreshAsync(string? via, CancellationToken cancellationToken)
    {
        // With no current list Bifrost cannot tell which provider the account still holds, so the member names one.
        if (AccountId is not { } id || (snapshots.Get(id) is null && (via is null || !HeimdallAuthService.LinkableProviders.Contains(via))))
        {
            return Task.FromResult(Done(HeimdallLinkOutcome.ProviderNeeded));
        }

        return BeginAsync(
            (account, ct) => heimdallAuthService.StartListAsync(account, via, hostOptions.Value.PublicBaseUrl, Request.Cookies, ct),
            HeimdallLinkOutcome.Error,
            cancellationToken);
    }

    /// <summary>Links another provider. The provider is one Heimdall's bifrost profile admits; anything else starts nothing, and so does a missing list.</summary>
    public Task<IActionResult> OnPostLinkAsync(string? provider, CancellationToken cancellationToken)
    {
        if (provider is null || !HeimdallAuthService.LinkableProviders.Contains(provider))
        {
            return Task.FromResult(Done(HeimdallLinkOutcome.Error));
        }

        if (AccountId is not { } id || snapshots.Get(id) is null)
        {
            return Task.FromResult(Done(HeimdallLinkOutcome.ListExpired));
        }

        return BeginAsync(
            (account, ct) => heimdallAuthService.StartLinkAsync(account, provider, hostOptions.Value.PublicBaseUrl, Request.Cookies, ct),
            HeimdallLinkOutcome.Error,
            cancellationToken);
    }

    /// <summary>
    /// Unlinks one login with fresh proof. The identity must be on the list this account was last shown and the account must
    /// hold a Google login to prove with; otherwise Heimdall is not asked, and the page says so.
    /// </summary>
    public Task<IActionResult> OnPostUnlinkAsync(string? identityId, CancellationToken cancellationToken)
    {
        var shown = AccountId is { } id ? snapshots.Get(id) : null;
        if (shown is null || identityId is null || !shown.Any(x => x.Id == identityId))
        {
            return Task.FromResult(Done(HeimdallLinkOutcome.ListExpired));
        }

        if (!shown.Any(x => x.Provider == HeimdallAuthService.StepUpProvider))
        {
            return Task.FromResult(Done(HeimdallLinkOutcome.ProofUnavailable));
        }

        return BeginAsync(
            (account, ct) => heimdallAuthService.StartUnlinkAsync(account, identityId, hostOptions.Value.PublicBaseUrl, Request.Cookies, ct),
            HeimdallLinkOutcome.ProofUnavailable,
            cancellationToken);
    }

    private IActionResult Done(HeimdallLinkOutcome outcome) =>
        RedirectToPage(null, new Dictionary<string, string?> { [HeimdallLinkOutcomes.ResultParameter] = HeimdallLinkOutcomes.Code(outcome) });

    private async Task<IActionResult> BeginAsync(
        Func<string, CancellationToken, Task<HeimdallAuthStartResult>> start, HeimdallLinkOutcome onRefusal, CancellationToken cancellationToken)
    {
        if (AccountId is not { } accountId)
        {
            return Done(HeimdallLinkOutcome.Error);
        }

        try
        {
            var started = await start(accountId, cancellationToken);
            Response.Cookies.Append(started.CookieName, started.CookieValue, HeimdallAttemptCookie.Options());
            return Redirect(started.AuthorizationUrl.ToString());
        }
        catch (HeimdallAuthException error)
        {
            logger.LogWarning("Linked accounts start refused: {Reason}", error.Message);
            return Done(onRefusal);
        }
    }
}
