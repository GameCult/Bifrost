using System.Security.Claims;
using Bifrost.Web.Configuration;
using Bifrost.Web.Features.Heimdall;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Features.Passkeys;
using Bifrost.Web.Features.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
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
    PasskeyService passkeys,
    ICurrentBifrostActorAccessor actors,
    IOptions<BifrostHostOptions> hostOptions,
    IOptions<AccountMergeOptions> mergeOptions,
    ILogger<LinkedAccountsModel> logger) : PageModel
{
    private static readonly Dictionary<string, string> ProviderNames = new(StringComparer.Ordinal)
    {
        ["discord"] = "Discord",
        ["patreon"] = "Patreon",
        ["google"] = "Google"
    };

    /// <summary>
    /// A folded or frozen account manages no logins and enrols no passkey: the page's handlers all begin a Heimdall flow on the
    /// account's raw claim, so the one gate is here, on the actor accessor's standing, before any handler runs.
    /// </summary>
    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        if ((await actors.GetAsync(context.HttpContext.RequestAborted)).Merge is not null)
        {
            context.Result = Forbid();
            return;
        }

        await base.OnPageHandlerExecutionAsync(context, next);
    }

    private string? AccountId => User.FindFirstValue(BifrostClaimTypes.HeimdallAccountId) is { Length: > 0 } id ? id : null;

    /// <summary>Whether the merge page exists on this server (a flag, dark until Heimdall can move logins).</summary>
    public bool MergeEnabled => mergeOptions.Value.Enabled;

    /// <summary>The wording of the outcome the page was sent back with: a closed set, null for anything else.</summary>
    public string? ResultMessage { get; private set; }

    /// <summary>Whether this session is a Heimdall account. A Bifrost-native GitHub session has none, so there is nothing to list.</summary>
    public bool HasHeimdallAccount => AccountId is not null;

    /// <summary>The logins the last read found, or null when there is none or it has expired.</summary>
    public IReadOnlyList<LinkedIdentitySummary>? Identities { get; private set; }

    /// <summary>Whether a fresh Google sign-in can be had for this account: without a strong passkey, unlinking needs one, and only Google can give it.</summary>
    public bool HasGoogle => Identities?.Any(x => x.Provider == HeimdallAuthService.StepUpProvider) == true;

    /// <summary>The account's passkeys that are not revoked, with their cooling-off state.</summary>
    public IReadOnlyList<PasskeyView> Passkeys { get; private set; } = [];

    /// <summary>Whether a passkey past its cooling-off exists: unlinking then needs it and no longer accepts Google as the proof.</summary>
    public bool HasStrongPasskey => Passkeys.Any(x => x.IsStrong);

    /// <summary>When the first passkey still cooling off becomes strong.</summary>
    public DateTimeOffset? NextStrongAt => Passkeys.Where(x => !x.IsStrong).Select(x => (DateTimeOffset?)x.StrongFromUtc).Min();

    /// <summary>Whether this browser holds a live ticket from a fresh sign-in, so a passkey can be added now.</summary>
    public bool CanEnrol { get; private set; }

    /// <summary>Whether this server can run passkey ceremonies at all (https and the host it expects).</summary>
    public bool PasskeysAvailable => passkeys.IsAvailable;

    public bool PasskeyLimitReached => Passkeys.Count >= PasskeyService.MaxActive;

    public bool CanUnlink => HasStrongPasskey || HasGoogle;

    public IReadOnlyList<string> LinkableProviders => HeimdallAuthService.LinkableProviders;

    public static string ProviderName(string provider) => ProviderNames.GetValueOrDefault(provider, provider);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        ResultMessage = HeimdallLinkOutcomes.Wording(Request.Query[HeimdallLinkOutcomes.ResultParameter].ToString());
        Identities = AccountId is { } id ? snapshots.Get(id) : null;
        if (AccountId is { } account)
        {
            Passkeys = await passkeys.ListAsync(account, cancellationToken);
            CanEnrol = passkeys.HasTicket(account, Request.Cookies[BifrostCookies.PasskeyTicket]);
        }
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
    public async Task<IActionResult> OnPostUnlinkAsync(string? identityId, CancellationToken cancellationToken)
    {
        var shown = AccountId is { } id ? snapshots.Get(id) : null;
        if (shown is null || identityId is null || !shown.Any(x => x.Id == identityId))
        {
            return Done(HeimdallLinkOutcome.ListExpired);
        }

        // A strong passkey replaces Google as the proof: a member who has one is never asked for the weaker one.
        if (await passkeys.HasStrongAsync(AccountId!, cancellationToken))
        {
            return Done(HeimdallLinkOutcome.PasskeyRequired);
        }

        if (!shown.Any(x => x.Provider == HeimdallAuthService.StepUpProvider))
        {
            return Done(HeimdallLinkOutcome.ProofUnavailable);
        }

        return await BeginAsync(
            (account, ct) => heimdallAuthService.StartUnlinkAsync(account, identityId, hostOptions.Value.PublicBaseUrl, Request.Cookies, ct),
            HeimdallLinkOutcome.ProofUnavailable,
            cancellationToken);
    }

    /// <summary>Begins the fresh sign-in whose completion issues the enrolment ticket. Needs a current list: the proof provider is never guessed.</summary>
    public async Task<IActionResult> OnPostPasskeyEnrolStartAsync(CancellationToken cancellationToken)
    {
        if (AccountId is not { } id)
        {
            return Done(HeimdallLinkOutcome.Error);
        }

        if (snapshots.Get(id) is null)
        {
            return Done(HeimdallLinkOutcome.ListExpired);
        }

        if (await passkeys.PreflightEnrolStartAsync(id, cancellationToken) is { } refused)
        {
            return Done(refused);
        }

        return await BeginAsync(
            (account, ct) => heimdallAuthService.StartEnrolProofAsync(account, hostOptions.Value.PublicBaseUrl, Request.Cookies, ct),
            HeimdallLinkOutcome.Error,
            cancellationToken);
    }

    /// <summary>The WebAuthn creation options for the ticket this browser holds, as JSON for the page's script.</summary>
    public async Task<IActionResult> OnPostPasskeyEnrolOptionsAsync(CancellationToken cancellationToken)
    {
        if (AccountId is not { } id)
        {
            return Options(HeimdallLinkOutcome.Error, null);
        }

        var (refusal, json) = await passkeys.BeginEnrolAsync(id, Request.Cookies[BifrostCookies.PasskeyTicket], cancellationToken);
        return Options(refusal, json);
    }

    /// <summary>Verifies the registration the script posted. The ticket cookie is spent whatever the outcome.</summary>
    public async Task<IActionResult> OnPostPasskeyEnrolAsync(
        string? id, string? rawId, string? attestationObject, string? clientDataJson, string? transports, string? label, CancellationToken cancellationToken)
    {
        if (AccountId is not { } account)
        {
            return Done(HeimdallLinkOutcome.Error);
        }

        var outcome = await passkeys.EnrolAsync(
            account, Request.Cookies[BifrostCookies.PasskeyTicket],
            new PasskeyFields(id, rawId, null, clientDataJson, null, null, attestationObject, transports), label, cancellationToken);
        Response.Cookies.Delete(BifrostCookies.PasskeyTicket, HeimdallAttemptCookie.DeleteOptions());
        return Done(outcome);
    }

    public async Task<IActionResult> OnPostPasskeyRevokeAsync(Guid credentialId, CancellationToken cancellationToken) =>
        AccountId is { } id ? Done(await passkeys.RevokeAsync(id, credentialId, cancellationToken)) : Done(HeimdallLinkOutcome.Error);

    /// <summary>The WebAuthn request options for unlinking one identity, as JSON for the page's script.</summary>
    public async Task<IActionResult> OnPostPasskeyChallengeAsync(string? identityId, CancellationToken cancellationToken)
    {
        if (AccountId is not { } id)
        {
            return Options(HeimdallLinkOutcome.Error, null);
        }

        if (snapshots.Get(id) is not { } shown || identityId is null || !shown.Any(x => x.Id == identityId))
        {
            return Options(HeimdallLinkOutcome.ListExpired, null);
        }

        var (refusal, json) = await passkeys.BeginAssertionAsync(id, identityId, cancellationToken);
        return Options(refusal, json);
    }

    /// <summary>
    /// Unlinks one login on a passkey assertion. The assertion is verified here; only then does Bifrost start the fresh Heimdall sign-in
    /// whose completion asks Heimdall for the step-up and unlinks. A refused or unverifiable assertion starts no sign-in.
    /// </summary>
    public async Task<IActionResult> OnPostUnlinkPasskeyAsync(
        string? identityId, string? id, string? rawId, string? authenticatorData, string? clientDataJson, string? signature, string? userHandle,
        CancellationToken cancellationToken)
    {
        var shown = AccountId is { } account ? snapshots.Get(account) : null;
        if (shown is null || identityId is null || !shown.Any(x => x.Id == identityId))
        {
            return Done(HeimdallLinkOutcome.ListExpired);
        }

        var (refusal, factId) = await passkeys.VerifyAssertionAsync(
            AccountId!, identityId, new PasskeyFields(id, rawId, authenticatorData, clientDataJson, signature, userHandle, null, null), cancellationToken);
        if (refusal is { } failed)
        {
            return Done(failed);
        }

        try
        {
            var started = await heimdallAuthService.StartUnlinkPasskeyAsync(
                AccountId!, identityId, factId!, hostOptions.Value.PublicBaseUrl, Request.Cookies, cancellationToken);
            Response.Cookies.Append(started.CookieName, started.CookieValue, HeimdallAttemptCookie.Options());
            return Redirect(started.AuthorizationUrl.ToString());
        }
        catch (HeimdallAuthException error)
        {
            logger.LogWarning("Linked accounts start refused: {Reason}", error.Message);
            passkeys.DiscardFact(factId);
            return Done(HeimdallLinkOutcome.ProofUnavailable);
        }
    }

    /// <summary>JSON for the page's script: the options, or only a closed outcome code with the status that fits.</summary>
    private IActionResult Options(HeimdallLinkOutcome? refusal, string? json)
    {
        if (refusal is null && json is not null)
        {
            return Content(json, "application/json");
        }

        var status = refusal == HeimdallLinkOutcome.RateLimited ? StatusCodes.Status429TooManyRequests : StatusCodes.Status400BadRequest;
        return new ContentResult
        {
            StatusCode = status,
            ContentType = "application/json",
            Content = "{\"error\":\"" + HeimdallLinkOutcomes.Code(refusal ?? HeimdallLinkOutcome.Error) + "\"}"
        };
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
