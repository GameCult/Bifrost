using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bifrost.Web.Configuration;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Features.Shared;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Features.Heimdall;

/// <summary>
/// Heimdall sign-in. Start issues an attempt that lives only in a Data Protection-protected cookie in the
/// browser that began it. The browser that Heimdall authenticated presents the completion code from its
/// URL fragment; Bifrost redeems it at Heimdall itself and signs in only when that browser holds the
/// attempt's cookie and the redeemed return target names the same attempt. The Linked accounts page's steps (list,
/// link, unlink) are the same machine with other attempt purposes: each is a fresh Heimdall sign-in whose bearer is
/// used inside that one request and dropped.
/// </summary>
public sealed class HeimdallAuthService(
    HttpClient httpClient,
    HeimdallTokenVerifier tokenVerifier,
    BifrostDbContext dbContext,
    BifrostIdentityService bifrostIdentityService,
    IOptions<HeimdallOptions> heimdallOptions,
    IDataProtectionProvider dataProtectionProvider,
    TimeProvider timeProvider,
    HeimdallMemberClient memberClient,
    LinkedAccountsSnapshots snapshots)
{
    public const string ErrorRedirect = "/?auth=heimdall-error";
    public const string AppSecretHeader = "x-heimdall-app-secret";

    private static readonly HashSet<string> AllowedProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "discord",
        "patreon",
        "twitch"
    };

    /// <summary>The headers HttpClient logging must never write: the app secret and any bearer.</summary>
    public static bool RedactsHeader(string header) =>
        string.Equals(header, AppSecretHeader, StringComparison.OrdinalIgnoreCase)
        || string.Equals(header, "Authorization", StringComparison.OrdinalIgnoreCase);

    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("Bifrost.Heimdall.Attempt.v1");

    /// <summary>Points the client at Heimdall and, when the app has a secret, identifies Bifrost as the app.</summary>
    public static void ConfigureClient(HttpClient client, HeimdallOptions options)
    {
        client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/'));
        if (!string.IsNullOrWhiteSpace(options.AppSecret))
        {
            client.DefaultRequestHeaders.Add(AppSecretHeader, options.AppSecret);
        }
    }

    /// <summary>
    /// Names the HTTP status and, when the body carries one, Heimdall's error code (a short snake_case token only,
    /// see <see cref="HeimdallRefusal"/>), so nothing else from the body reaches a log.
    /// </summary>
    private static async Task<string> RefusedStartMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var detail = $"HTTP {(int)response.StatusCode}";
        if (await HeimdallRefusal.CodeAsync(response, cancellationToken) is { } code)
        {
            detail += ", " + code;
        }

        return $"Heimdall rejected the auth start ({detail}).";
    }

    public async Task<HeimdallAuthStartResult> StartAsync(
        string provider,
        string callbackBaseUrl,
        LocalReturnPath returnPath,
        bool requireMemberAccess,
        IEnumerable<KeyValuePair<string, string>> requestCookies,
        CancellationToken cancellationToken,
        HeimdallAttemptPurpose purpose = HeimdallAttemptPurpose.SignIn,
        string? expectedAccountId = null,
        string? pendingLinkId = null,
        string? replaceSlot = null,
        bool stepUp = false,
        string? target = null)
    {
        // The public sign-in routes offer AllowedProviders; the proofs of the Linked accounts page may also use Google
        // (a step-up proof only Google can give a bifrost sign-in, an ordinary one for an account that holds Google).
        if (!(purpose == HeimdallAttemptPurpose.SignIn ? AllowedProviders : ProofProviders).Contains(provider))
        {
            throw new HeimdallAuthException("Unsupported Heimdall provider.");
        }

        var options = heimdallOptions.Value;
        if (!options.IsConfigured)
        {
            throw new HeimdallAuthException("Heimdall is not configured.");
        }

        var entitlementPolicy = requireMemberAccess
            ? BuildMemberAccessPolicy(provider, options)
            : null;
        var nonce = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

        var request = new HeimdallStartRequest(
            options.AppSlug,
            "sign_in",
            ToAttemptReturnUrl(callbackBaseUrl, nonce),
            new HeimdallHandoff("browser_completion"),
            entitlementPolicy,
            stepUp ? true : null);

        // Headers only: a refusal body is read through RefusedStartMessageAsync's own 4 KiB bound, not buffered whole.
        using var startMessage = new HttpRequestMessage(HttpMethod.Post, $"/v1/oauth/{provider}/start")
        {
            Content = JsonContent.Create(request)
        };
        using var response = await SendStartAsync(startMessage, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HeimdallAuthException(await RefusedStartMessageAsync(response, cancellationToken));
        }

        var payload = await response.Content.ReadFromJsonAsync<HeimdallStartResponse>(cancellationToken);
        if (payload is null || string.IsNullOrWhiteSpace(payload.AuthorizationUrl))
        {
            throw new HeimdallAuthException("Heimdall auth start returned no authorization URL.");
        }

        var attempt = new HeimdallAttempt(
            nonce,
            provider,
            requireMemberAccess,
            returnPath,
            timeProvider.GetUtcNow() + HeimdallAttemptCookie.Lifetime,
            purpose,
            expectedAccountId,
            pendingLinkId,
            target);
        return new HeimdallAuthStartResult(
            new Uri(payload.AuthorizationUrl, UriKind.Absolute), replaceSlot ?? ChooseSlot(requestCookies), Protect(attempt));
    }

    /// <summary>A start that cannot reach Heimdall is a refusal like any other, never a 500.</summary>
    private async Task<HttpResponseMessage> SendStartAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new HeimdallAuthException("Heimdall could not be reached for the auth start.");
        }
    }

    /// <summary>
    /// The provider an ordinary proof sign-in uses: the one the person signed in with when the account still holds it,
    /// else the first held one of Discord, Patreon, Google. With no list to go by, the session's provider, else Discord.
    /// Signing in through a provider the account no longer holds would only mint an unrelated Heimdall account.
    /// </summary>
    public static string ProofProvider(string? sessionProvider, IReadOnlyCollection<string>? held)
    {
        var session = sessionProvider is not null && ProofProviders.Contains(sessionProvider) ? sessionProvider.ToLowerInvariant() : null;
        if (held is { Count: > 0 })
        {
            foreach (var candidate in new[] { session, "discord", "patreon", "google" })
            {
                if (candidate is not null && held.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        return session ?? "discord";
    }

    /// <summary>
    /// The page's proof sign-ins. Each is a Heimdall sign-in for the account already signed in here, kept as an attempt
    /// that says what it is the proof for (list, link, unlink) and which person it must prove. The session is never replaced.
    /// </summary>
    private Task<HeimdallAuthStartResult> StartProofAsync(
        HeimdallAttemptPurpose purpose,
        string provider,
        string sessionAccountId,
        string callbackBaseUrl,
        IEnumerable<KeyValuePair<string, string>> requestCookies,
        bool stepUp,
        string? target,
        CancellationToken cancellationToken) =>
        StartAsync(
            provider,
            callbackBaseUrl,
            LocalReturnPath.Parse(LinkReturnPath),
            requireMemberAccess: false,
            requestCookies,
            cancellationToken,
            purpose,
            sessionAccountId,
            stepUp: stepUp,
            target: target);

    /// <summary>Reads the account's logins: an ordinary sign-in, then GET /v1/me/identities inside its completion.</summary>
    public Task<HeimdallAuthStartResult> StartListAsync(
        string sessionAccountId, string? sessionProvider, string callbackBaseUrl,
        IEnumerable<KeyValuePair<string, string>> requestCookies, CancellationToken cancellationToken) =>
        StartProofAsync(
            HeimdallAttemptPurpose.ListProof, ProofProvider(sessionProvider, snapshots.Get(sessionAccountId)?.Select(x => x.Provider).ToArray()),
            sessionAccountId, callbackBaseUrl, requestCookies, false, null, cancellationToken);

    /// <summary>Links another provider: an ordinary sign-in as proof, then the link start for <paramref name="targetProvider"/> with its bearer.</summary>
    public Task<HeimdallAuthStartResult> StartLinkAsync(
        string sessionAccountId, string? sessionProvider, string targetProvider, string callbackBaseUrl,
        IEnumerable<KeyValuePair<string, string>> requestCookies, CancellationToken cancellationToken)
    {
        if (!LinkableProviders.Contains(targetProvider))
        {
            throw new HeimdallAuthException("Unsupported provider to link.");
        }

        return StartProofAsync(
            HeimdallAttemptPurpose.LinkStart, ProofProvider(sessionProvider, snapshots.Get(sessionAccountId)?.Select(x => x.Provider).ToArray()),
            sessionAccountId, callbackBaseUrl, requestCookies, false, targetProvider.ToLowerInvariant(), cancellationToken);
    }

    /// <summary>
    /// Unlinks one login: a step-up sign-in through Google, then POST /v1/me/identities/{id}/unlink with its bearer. The
    /// identity must be on the account's current snapshot and the account must hold Google; otherwise no proof can be had
    /// and Heimdall is not asked.
    /// </summary>
    public Task<HeimdallAuthStartResult> StartUnlinkAsync(
        string sessionAccountId, string identityId, string callbackBaseUrl,
        IEnumerable<KeyValuePair<string, string>> requestCookies, CancellationToken cancellationToken) =>
        StartProofAsync(
            HeimdallAttemptPurpose.UnlinkProof, StepUpProvider, sessionAccountId, callbackBaseUrl, requestCookies, true, identityId, cancellationToken);

    /// <summary>
    /// The providers Heimdall's bifrost profile admits (src/app-profiles.ts, <c>identityProviders</c>): a link or a proof can use them.
    /// Twitch is offered for sign-in here but the profile refuses it, so it is not linkable.
    /// </summary>
    public static readonly IReadOnlyList<string> LinkableProviders = ["discord", "patreon", "google"];

    private static readonly HashSet<string> ProofProviders = new(LinkableProviders, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The one provider whose step-up a bifrost sign-in can use (docs/service-contract.md, step-up: Discord and Patreon cannot
    /// re-authenticate, Twitch and YouTube are not in the bifrost profile). Heimdall answers 503 until a Google client is configured.
    /// </summary>
    public const string StepUpProvider = "google";

    public const string LinkReturnPath = "/Membership/LinkedAccounts";

    /// <summary>
    /// The slot the new attempt's cookie takes: the first slot that holds no live attempt, else the one whose
    /// attempt expires first. Parallel starts carry the same request cookies, so they choose the same slot and
    /// overwrite each other; the Cookie header holds at most <see cref="HeimdallAttemptCookie.SlotNames"/> attempt
    /// cookies however many starts a browser makes.
    /// </summary>
    private string ChooseSlot(IEnumerable<KeyValuePair<string, string>> requestCookies)
    {
        var cookies = requestCookies.ToLookup(x => x.Key, x => x.Value, StringComparer.Ordinal);
        string? oldest = null;
        var oldestExpiry = DateTimeOffset.MaxValue;
        foreach (var name in HeimdallAttemptCookie.SlotNames)
        {
            var live = Read(cookies[name].FirstOrDefault());
            if (live is null)
            {
                return name;
            }

            if (live.ExpiresAt < oldestExpiry)
            {
                oldest = name;
                oldestExpiry = live.ExpiresAt;
            }
        }

        return oldest!;
    }

    /// <summary>
    /// The slot cookie that holds the attempt for this nonce: its name and value, or null when the browser holds
    /// none (never started here, expired, or overwritten by a later start).
    /// </summary>
    public (string Name, string Value)? FindAttemptCookie(IEnumerable<KeyValuePair<string, string>> requestCookies, string? nonce)
    {
        foreach (var (name, value) in requestCookies)
        {
            if (HeimdallAttemptCookie.SlotNames.Contains(name) && Unprotect(value, nonce) is not null)
            {
                return (name, value);
            }
        }

        return null;
    }

    /// <summary>What the attempt this browser holds for the nonce is for, or null when it holds none.</summary>
    public HeimdallAttemptPurpose? PurposeOf(string? cookieValue, string? nonce) => Unprotect(cookieValue, nonce)?.Purpose;

    /// <summary>Redeems the code of an attempt and verifies the token it carries. The token is returned to the caller's stack frame and nowhere else.</summary>
    private async Task<(HeimdallVerifiedIdentity Identity, string AccessToken, HeimdallRedeemResponse Redeemed)> RedeemVerifiedAsync(
        string callbackBaseUrl, HeimdallAttempt attempt, string? code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new HeimdallAuthException("Heimdall completion carried no code.");
        }

        var redeemed = await RedeemAsync(code, cancellationToken);
        if (!string.Equals(redeemed.ReturnTo, ToAttemptReturnUrl(callbackBaseUrl, attempt.Nonce), StringComparison.Ordinal))
        {
            throw new HeimdallAuthException("Heimdall completion belongs to another attempt.");
        }

        if (string.IsNullOrWhiteSpace(redeemed.AccessToken))
        {
            throw new HeimdallAuthException("Heimdall completion carried no access token.");
        }

        var identity = await tokenVerifier.VerifyAsync(redeemed.AccessToken, redeemed.Account?.Id, cancellationToken);
        return (identity, redeemed.AccessToken, redeemed);
    }

    public async Task<HeimdallCompletedSignIn> CompleteAsync(
        string callbackBaseUrl,
        string? nonce,
        string? cookieValue,
        string? code,
        CancellationToken cancellationToken)
    {
        var attempt = Unprotect(cookieValue, nonce)
            ?? throw new HeimdallAuthException("Heimdall completion has no attempt for this browser.");
        if (attempt.Purpose != HeimdallAttemptPurpose.SignIn)
        {
            throw new HeimdallAuthException("Heimdall completion is not a sign-in attempt.");
        }

        var (identity, _, _) = await RedeemVerifiedAsync(callbackBaseUrl, attempt, code, cancellationToken);
        if (attempt.RequiresMemberAccess && !identity.HasMemberAccess)
        {
            throw new HeimdallAuthException("Heimdall did not grant Bifrost member access.");
        }

        var userAccount = await UpsertUserAccountAsync(
            identity,
            attempt.Provider,
            attempt.RequiresMemberAccess,
            cancellationToken);
        return new HeimdallCompletedSignIn(
            bifrostIdentityService.BuildPrincipal(userAccount, attempt.Provider),
            attempt.ReturnPath);
    }

    /// <summary>
    /// One step of a Linked accounts action, chosen by the purpose of the attempt this browser holds. The browser's session
    /// must be the account the action was begun for, checked before Heimdall is asked anything and again against the identity
    /// the fresh sign-in proves. Each step consumes the held attempt: the next step is a new attempt in the same slot, or none.
    /// The bearer a step redeems is used inside the step and dropped.
    /// </summary>
    public async Task<HeimdallLinkStep> ContinueLinkAsync(
        string callbackBaseUrl,
        string? nonce,
        string cookieName,
        string? cookieValue,
        string? code,
        string? pendingLinkId,
        string? sessionAccountId,
        CancellationToken cancellationToken)
    {
        var attempt = Unprotect(cookieValue, nonce)
            ?? throw new HeimdallAuthException("Heimdall link has no attempt for this browser.");
        if (string.IsNullOrEmpty(attempt.ExpectedAccountId)
            || !string.Equals(sessionAccountId, attempt.ExpectedAccountId, StringComparison.Ordinal))
        {
            throw new HeimdallAuthException("Heimdall link belongs to another session.");
        }

        switch (attempt.Purpose)
        {
            case HeimdallAttemptPurpose.LinkStart:
            {
                var (_, bearer) = await RedeemOwnerAsync(callbackBaseUrl, attempt, code, cancellationToken);
                var authorizationUrl = await StartLinkProviderAsync(callbackBaseUrl, attempt, bearer, cancellationToken);
                return new HeimdallLinkStep(authorizationUrl.Url, cookieName, authorizationUrl.CookieValue, null);
            }

            case HeimdallAttemptPurpose.LinkAwaitGoogle:
            {
                if (string.IsNullOrWhiteSpace(pendingLinkId))
                {
                    throw new HeimdallAuthException("Heimdall link carried no pending link.");
                }

                var next = await StartAsync(
                    attempt.Provider,
                    callbackBaseUrl,
                    attempt.ReturnPath,
                    requireMemberAccess: false,
                    [],
                    cancellationToken,
                    HeimdallAttemptPurpose.LinkConfirm,
                    attempt.ExpectedAccountId,
                    pendingLinkId,
                    replaceSlot: cookieName);
                return new HeimdallLinkStep(next.AuthorizationUrl, next.CookieName, next.CookieValue, null);
            }

            case HeimdallAttemptPurpose.LinkConfirm:
            {
                var (identity, bearer) = await RedeemOwnerAsync(callbackBaseUrl, attempt, code, cancellationToken);
                var outcome = await ConfirmLinkAsync(attempt.PendingLinkId!, bearer, cancellationToken);
                if (outcome == HeimdallLinkOutcome.Linked)
                {
                    // Membership follows the verified identity exactly as at sign-in; the session principal is not replaced.
                    await UpsertUserAccountAsync(identity, attempt.Provider, grantMemberAccess: false, cancellationToken);
                    snapshots.Remove(identity.AccountId);
                }

                return new HeimdallLinkStep(null, cookieName, null, outcome);
            }

            case HeimdallAttemptPurpose.LinkAwaitDirect:
            {
                // A provider other than Google attaches in Heimdall's callback; the browser returns with a link completion.
                var (identity, _, redeemed) = await RedeemOwnerWithBodyAsync(callbackBaseUrl, attempt, code, cancellationToken);
                if (redeemed.Mode != "link" || !string.Equals(redeemed.Provider, attempt.Target, StringComparison.Ordinal))
                {
                    throw new HeimdallAuthException("Heimdall completion is not this link.");
                }

                await UpsertUserAccountAsync(identity, attempt.Target!, grantMemberAccess: false, cancellationToken);
                snapshots.Remove(identity.AccountId);
                return new HeimdallLinkStep(null, cookieName, null, HeimdallLinkOutcome.Linked);
            }

            case HeimdallAttemptPurpose.ListProof:
            {
                var (identity, bearer) = await RedeemOwnerAsync(callbackBaseUrl, attempt, code, cancellationToken);
                var listed = await memberClient.ListAsync(bearer, cancellationToken);
                if (listed.Call == HeimdallMemberCall.Ok)
                {
                    snapshots.Put(identity.AccountId, listed.Identities!);
                }

                return new HeimdallLinkStep(null, cookieName, null, listed.Call == HeimdallMemberCall.Ok ? HeimdallLinkOutcome.Listed : OutcomeOf(listed.Call));
            }

            case HeimdallAttemptPurpose.UnlinkProof:
            {
                var (identity, bearer) = await RedeemOwnerAsync(callbackBaseUrl, attempt, code, cancellationToken);
                var unlinked = await memberClient.UnlinkAsync(bearer, attempt.Target ?? string.Empty, cancellationToken);
                switch (unlinked.Call)
                {
                    case HeimdallMemberCall.Ok:
                        snapshots.Put(identity.AccountId, unlinked.Identities!);
                        break;
                    case HeimdallMemberCall.IdentityNotFound:
                        snapshots.Remove(identity.AccountId);
                        break;
                }

                return new HeimdallLinkStep(null, cookieName, null, unlinked.Call == HeimdallMemberCall.Ok ? HeimdallLinkOutcome.Unlinked : OutcomeOf(unlinked.Call));
            }

            default:
                throw new HeimdallAuthException("Heimdall completion is not a link attempt.");
        }
    }

    private static HeimdallLinkOutcome OutcomeOf(HeimdallMemberCall call) => call switch
    {
        HeimdallMemberCall.SignInRequired => HeimdallLinkOutcome.SignInRequired,
        HeimdallMemberCall.StepUpRequired => HeimdallLinkOutcome.StepUpRequired,
        HeimdallMemberCall.IdentityNotFound => HeimdallLinkOutcome.IdentityNotFound,
        HeimdallMemberCall.LastIdentity => HeimdallLinkOutcome.LastIdentity,
        _ => HeimdallLinkOutcome.Error
    };

    /// <summary>Redeems a link attempt's code and requires it to prove the account the link was begun for.</summary>
    private async Task<(HeimdallVerifiedIdentity Identity, string AccessToken)> RedeemOwnerAsync(
        string callbackBaseUrl, HeimdallAttempt attempt, string? code, CancellationToken cancellationToken)
    {
        var (identity, bearer, _) = await RedeemOwnerWithBodyAsync(callbackBaseUrl, attempt, code, cancellationToken);
        return (identity, bearer);
    }

    private async Task<(HeimdallVerifiedIdentity Identity, string AccessToken, HeimdallRedeemResponse Redeemed)> RedeemOwnerWithBodyAsync(
        string callbackBaseUrl, HeimdallAttempt attempt, string? code, CancellationToken cancellationToken)
    {
        var proven = await RedeemVerifiedAsync(callbackBaseUrl, attempt, code, cancellationToken);
        if (!string.Equals(proven.Identity.AccountId, attempt.ExpectedAccountId, StringComparison.Ordinal))
        {
            throw new HeimdallAuthException("Heimdall link was proven by another account.");
        }

        return proven;
    }

    /// <summary>
    /// Starts the link to the attempt's target provider with the member's bearer. Google attaches in two phases (the
    /// callback writes a pending link, a second fresh sign-in confirms it); the other admitted providers attach in the callback.
    /// A cookie written before the target existed names no provider and is a Google link.
    /// </summary>
    private async Task<(Uri Url, string CookieValue)> StartLinkProviderAsync(
        string callbackBaseUrl, HeimdallAttempt attempt, string bearer, CancellationToken cancellationToken)
    {
        var target = attempt.Target ?? "google";
        if (!LinkableProviders.Contains(target))
        {
            throw new HeimdallAuthException("Unsupported provider to link.");
        }

        var options = heimdallOptions.Value;
        var nonce = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/v1/oauth/{target}/start")
        {
            Content = JsonContent.Create(new HeimdallStartRequest(
                options.AppSlug, "link", ToAttemptReturnUrl(callbackBaseUrl, nonce), new HeimdallHandoff("browser_completion"), null, null))
        };
        message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        using var response = await SendStartAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HeimdallAuthException(await RefusedStartMessageAsync(response, cancellationToken));
        }

        var payload = await response.Content.ReadFromJsonAsync<HeimdallStartResponse>(cancellationToken);
        if (payload is null || string.IsNullOrWhiteSpace(payload.AuthorizationUrl))
        {
            throw new HeimdallAuthException("Heimdall link start returned no authorization URL.");
        }

        var awaiting = new HeimdallAttempt(
            nonce,
            attempt.Provider,
            false,
            attempt.ReturnPath,
            timeProvider.GetUtcNow() + HeimdallAttemptCookie.Lifetime,
            target == "google" ? HeimdallAttemptPurpose.LinkAwaitGoogle : HeimdallAttemptPurpose.LinkAwaitDirect,
            attempt.ExpectedAccountId,
            null,
            target);
        return (new Uri(payload.AuthorizationUrl, UriKind.Absolute), Protect(awaiting));
    }

    private async Task<HeimdallLinkOutcome> ConfirmLinkAsync(string pendingLinkId, string bearer, CancellationToken cancellationToken)
    {
        try
        {
            using var message = new HttpRequestMessage(
                HttpMethod.Post, $"/v1/apps/{Uri.EscapeDataString(heimdallOptions.Value.AppSlug)}/links/confirm")
            {
                Content = JsonContent.Create(new HeimdallConfirmLinkRequest(pendingLinkId))
            };
            message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
            using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            switch (response.StatusCode)
            {
                case HttpStatusCode.Created:
                    var linked = await response.Content.ReadFromJsonAsync<HeimdallConfirmLinkResponse>(cancellationToken);
                    return linked?.Status == "linked" ? HeimdallLinkOutcome.Linked : HeimdallLinkOutcome.Error;
                case HttpStatusCode.Conflict:
                    return HeimdallLinkOutcome.IdentityLinkedElsewhere;
                case HttpStatusCode.Gone:
                    return HeimdallLinkOutcome.Expired;
                default:
                    return HeimdallLinkOutcome.Error;
            }
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException && !cancellationToken.IsCancellationRequested)
        {
            return HeimdallLinkOutcome.Error;
        }
    }

    private async Task<HeimdallRedeemResponse> RedeemAsync(string code, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"/v1/apps/{Uri.EscapeDataString(heimdallOptions.Value.AppSlug)}/auth-completions/redeem",
                new HeimdallRedeemRequest(code),
                cancellationToken);
            if (response.StatusCode != HttpStatusCode.Created)
            {
                throw new HeimdallAuthException("Heimdall did not redeem the completion code.");
            }

            return await response.Content.ReadFromJsonAsync<HeimdallRedeemResponse>(cancellationToken)
                ?? throw new HeimdallAuthException("Heimdall redeem returned no body.");
        }
        catch (Exception error) when (error is not HeimdallAuthException && !cancellationToken.IsCancellationRequested)
        {
            throw new HeimdallAuthException("Heimdall redeem could not be read.");
        }
    }

    private string Protect(HeimdallAttempt attempt) =>
        _protector.Protect(JsonSerializer.Serialize(new AttemptWire(
            attempt.Nonce, attempt.Provider, attempt.RequiresMemberAccess, attempt.ReturnPath.Value, attempt.ExpiresAt,
            attempt.Purpose, attempt.ExpectedAccountId, attempt.PendingLinkId, attempt.Target)));

    /// <summary>The live attempt a cookie value protects when it belongs to this nonce, else null.</summary>
    private HeimdallAttempt? Unprotect(string? cookieValue, string? nonce) =>
        Read(cookieValue) is { } attempt && string.Equals(attempt.Nonce, nonce, StringComparison.Ordinal) ? attempt : null;

    /// <summary>The live attempt a cookie value protects, else null.</summary>
    private HeimdallAttempt? Read(string? cookieValue)
    {
        if (string.IsNullOrEmpty(cookieValue))
        {
            return null;
        }

        try
        {
            var wire = JsonSerializer.Deserialize<AttemptWire>(_protector.Unprotect(cookieValue));
            if (wire is null || wire.ExpiresAt <= timeProvider.GetUtcNow() || !Enum.IsDefined(wire.Purpose))
            {
                return null;
            }

            return new HeimdallAttempt(
                wire.Nonce, wire.Provider, wire.RequiresMemberAccess, LocalReturnPath.Parse(wire.ReturnPath), wire.ExpiresAt,
                wire.Purpose, wire.ExpectedAccountId, wire.PendingLinkId, wire.Target);
        }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }

    private sealed record AttemptWire(
        string Nonce,
        string Provider,
        bool RequiresMemberAccess,
        string ReturnPath,
        DateTimeOffset ExpiresAt,
        HeimdallAttemptPurpose Purpose = HeimdallAttemptPurpose.SignIn,
        string? ExpectedAccountId = null,
        string? PendingLinkId = null,
        string? Target = null);

    private async Task<UserAccount> UpsertUserAccountAsync(
        HeimdallVerifiedIdentity identity,
        string provider,
        bool grantMemberAccess,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var accountId = identity.AccountId;
        var displayName = identity.DisplayName ?? accountId;

        var userAccount = await dbContext.UserAccounts
            .Include(x => x.MemberProfile)
            .Include(x => x.Membership!)
                .ThenInclude(x => x.RoleAssignments)
            .Include(x => x.Membership!)
                .ThenInclude(x => x.TierSnapshots)
            .SingleOrDefaultAsync(x => x.HeimdallAccountId == accountId, cancellationToken);

        if (userAccount is null)
        {
            userAccount = new UserAccount
            {
                HeimdallAccountId = accountId,
                DisplayName = displayName,
                CreatedAtUtc = now,
                LastSeenAtUtc = now,
                MemberProfile = new MemberProfile
                {
                    Nickname = displayName,
                    Headline = $"Verified through Heimdall {provider}",
                    UpdatedAtUtc = now
                },
                Membership = new Bifrost.Web.Domain.Membership
                {
                    Status = grantMemberAccess ? MembershipStatus.Active : MembershipStatus.Authenticated,
                    CreatedAtUtc = now,
                    ApprovedAtUtc = grantMemberAccess ? now : null,
                    Notes = grantMemberAccess
                        ? "Activated by Heimdall member access claim"
                        : $"Identity verified through Heimdall {provider}"
                }
            };
            dbContext.UserAccounts.Add(userAccount);
        }
        else
        {
            userAccount.DisplayName = displayName;
            userAccount.LastSeenAtUtc = now;
            userAccount.MemberProfile ??= new MemberProfile
            {
                UserAccountId = userAccount.Id,
                Nickname = displayName,
                Headline = $"Verified through Heimdall {provider}",
                UpdatedAtUtc = now
            };
            userAccount.Membership ??= new Bifrost.Web.Domain.Membership
            {
                UserAccountId = userAccount.Id,
                CreatedAtUtc = now
            };
            // A suspension is an admin's decision: the claim neither lifts it nor, absent, imposes it.
            if (grantMemberAccess && userAccount.Membership.Status != MembershipStatus.Suspended)
            {
                userAccount.Membership.Status = MembershipStatus.Active;
                userAccount.Membership.ApprovedAtUtc ??= now;
            }
        }

        userAccount.MemberProfile!.Nickname = displayName;
        userAccount.MemberProfile.UpdatedAtUtc = now;
        await bifrostIdentityService.EnsureIdentityAsync(
            userAccount,
            displayName,
            cancellationToken);
        if (grantMemberAccess && userAccount.Membership!.Status == MembershipStatus.Active)
        {
            ApplicationBootstrapper.EnsureRole(
                userAccount.Membership!,
                MemberRole.StandardMember,
                null,
                now,
                "Heimdall member access");
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return userAccount;
    }

    private static HeimdallEntitlementPolicy BuildDiscordPolicy(HeimdallOptions options)
    {
        if (!options.IsDiscordConfigured)
        {
            throw new HeimdallAuthException("Heimdall Discord access is not configured.");
        }

        return new HeimdallEntitlementPolicy(
            "discord_role_access",
            options.DiscordGuildId,
            options.DiscordAllowedRoleIds,
            null);
    }

    private static HeimdallEntitlementPolicy BuildPatreonPolicy(HeimdallOptions options)
    {
        if (!options.IsPatreonConfigured)
        {
            throw new HeimdallAuthException("Heimdall Patreon access is not configured.");
        }

        return new HeimdallEntitlementPolicy(
            "patreon_membership_access",
            null,
            null,
            options.PatreonTierTitle);
    }

    private static HeimdallEntitlementPolicy BuildMemberAccessPolicy(string provider, HeimdallOptions options)
    {
        if (provider.Equals("discord", StringComparison.OrdinalIgnoreCase))
        {
            return BuildDiscordPolicy(options);
        }

        if (provider.Equals("patreon", StringComparison.OrdinalIgnoreCase))
        {
            return BuildPatreonPolicy(options);
        }

        throw new HeimdallAuthException("Heimdall provider does not support Bifrost member access.");
    }

    private static string ToAttemptReturnUrl(string callbackBaseUrl, string nonce)
    {
        var baseUri = new Uri(callbackBaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        return $"{new Uri(baseUri, "auth/heimdall/complete")}?attempt={Uri.EscapeDataString(nonce)}";
    }
}

public class HeimdallAuthException(string message) : InvalidOperationException(message);

public sealed record HeimdallAuthStartResult(Uri AuthorizationUrl, string CookieName, string CookieValue);

public sealed record HeimdallCompletedSignIn(ClaimsPrincipal Principal, LocalReturnPath ReturnPath);

/// <summary>An attempt Start issued. It exists only inside its protected cookie.</summary>
public sealed record HeimdallAttempt(
    string Nonce,
    string Provider,
    bool RequiresMemberAccess,
    LocalReturnPath ReturnPath,
    DateTimeOffset ExpiresAt,
    HeimdallAttemptPurpose Purpose = HeimdallAttemptPurpose.SignIn,
    string? ExpectedAccountId = null,
    string? PendingLinkId = null,
    string? Target = null);

/// <summary>
/// What a held attempt is for. SignIn is the default, so a cookie written before purposes existed reads as a sign-in.
/// The other purposes belong to the Linked accounts page: the fresh sign-ins that prove an account for a list, a link or
/// an unlink, and the provider round trips of a link. They never sign anyone in. Values are persisted in cookies: append only.
/// </summary>
public enum HeimdallAttemptPurpose
{
    SignIn = 0,
    LinkStart = 1,
    LinkAwaitGoogle = 2,
    LinkConfirm = 3,
    ListProof = 4,
    UnlinkProof = 5,
    LinkAwaitDirect = 6
}

/// <summary>How a Linked accounts action ended. <see cref="HeimdallLinkOutcomes.Code"/> is its closed wire form.</summary>
public enum HeimdallLinkOutcome
{
    Linked,
    IdentityLinkedElsewhere,
    Expired,
    Error,
    Listed,
    Unlinked,
    LastIdentity,
    StepUpRequired,
    IdentityNotFound,
    SignInRequired,
    ProofUnavailable,
    ListExpired
}

/// <summary>
/// Where a Linked accounts step sends the browser: to <see cref="HeimdallLinkStep.Next"/> with a new attempt cookie in the held
/// slot, or, when the action is finished, back to the page with the outcome's code in <c>?result=</c>.
/// </summary>
public static class HeimdallLinkOutcomes
{
    public const string ResultParameter = "result";

    public static string Redirect(HeimdallLinkOutcome outcome) =>
        HeimdallAuthService.LinkReturnPath + "?" + ResultParameter + "=" + Code(outcome);

    public static string Code(HeimdallLinkOutcome outcome) => outcome switch
    {
        HeimdallLinkOutcome.Linked => "linked",
        HeimdallLinkOutcome.IdentityLinkedElsewhere => "identity_linked_elsewhere",
        HeimdallLinkOutcome.Expired => "expired",
        HeimdallLinkOutcome.Listed => "listed",
        HeimdallLinkOutcome.Unlinked => "unlinked",
        HeimdallLinkOutcome.LastIdentity => "last_identity",
        HeimdallLinkOutcome.StepUpRequired => "step_up_required",
        HeimdallLinkOutcome.IdentityNotFound => "identity_not_found",
        HeimdallLinkOutcome.SignInRequired => "sign_in_required",
        HeimdallLinkOutcome.ProofUnavailable => "proof_unavailable",
        HeimdallLinkOutcome.ListExpired => "list_expired",
        _ => "error"
    };

    /// <summary>The words for an outcome code: a closed set, never Heimdall's text and never the query's. Anything else gets none.</summary>
    public static string? Wording(string? code) => code switch
    {
        "linked" => "The login is now linked to your Heimdall account.",
        "identity_linked_elsewhere" => "That login belongs to another account. Nothing was linked.",
        "expired" => "The link expired before it was confirmed. Nothing was linked; start the link again.",
        "listed" => null,
        "unlinked" => "The login was unlinked.",
        "last_identity" => "That is the last login on your account, and Heimdall will not remove it. Nothing was changed.",
        "step_up_required" => "Heimdall did not accept the sign-in as fresh proof. Nothing was changed; try again.",
        "identity_not_found" => "That login is no longer on your account. Nothing was changed; the list below is read again when you ask.",
        "sign_in_required" => "Heimdall no longer accepts that sign-in. Nothing was changed; try again.",
        "proof_unavailable" => "Fresh proof could not be started, so nothing was changed. Unlinking needs a fresh Google sign-in, and it is not available right now.",
        "list_expired" => "The list was out of date. Nothing was changed; read your linked logins again.",
        "error" => "That could not be completed. Nothing was changed.",
        _ => null
    };
}

public sealed record HeimdallLinkStep(Uri? Next, string CookieName, string? CookieValue, HeimdallLinkOutcome? Outcome);

public sealed record HeimdallConfirmLinkRequest(
    [property: JsonPropertyName("pendingLinkId")] string PendingLinkId);

public sealed record HeimdallConfirmLinkResponse(
    [property: JsonPropertyName("status")] string? Status);

public sealed record HeimdallStartRequest(
    [property: JsonPropertyName("appSlug")] string AppSlug,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("returnTo")] string ReturnTo,
    [property: JsonPropertyName("handoff")] HeimdallHandoff Handoff,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("entitlementPolicy")] HeimdallEntitlementPolicy? EntitlementPolicy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("stepUp")] bool? StepUp = null);

public sealed record HeimdallHandoff(
    [property: JsonPropertyName("kind")] string Kind);

public sealed record HeimdallEntitlementPolicy(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("guildId")] string? GuildId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("allowedRoleIds")] string[]? AllowedRoleIds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("requiredTierTitle")] string? RequiredTierTitle);

public sealed record HeimdallStartResponse(
    [property: JsonPropertyName("authorizationUrl")] string AuthorizationUrl);

public sealed record HeimdallRedeemRequest(
    [property: JsonPropertyName("completionCode")] string CompletionCode);

public sealed record HeimdallRedeemResponse(
    [property: JsonPropertyName("returnTo")] string? ReturnTo,
    [property: JsonPropertyName("accessToken")] string? AccessToken,
    [property: JsonPropertyName("account")] HeimdallAccount? Account,
    [property: JsonPropertyName("mode")] string? Mode = null,
    [property: JsonPropertyName("provider")] string? Provider = null);

public sealed record HeimdallAccount(
    [property: JsonPropertyName("id")] string Id);

/// <summary>
/// The attempt cookies: a ring of <see cref="SlotNames"/> fixed names, so the Cookie header a browser sends to
/// every path is bounded by construction (the __Host- prefix forces Path=/; a worst-case attempt is about 2.4 KB, nginx's line limit is 8 KB).
/// A start takes a free slot or overwrites the one expiring first; an overwritten attempt finds no cookie and
/// is refused. The attempt's nonce lives inside the protected value, never in the cookie name.
/// </summary>
public static class HeimdallAttemptCookie
{
    public const string Prefix = BifrostCookies.HeimdallAttemptPrefix;
    public const int SlotCount = 2;

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public static readonly IReadOnlyList<string> SlotNames =
        Enumerable.Range(0, SlotCount).Select(x => Prefix + x).ToArray();

    public static CookieOptions Options() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = BifrostCookies.Path,
        MaxAge = Lifetime
    };

    /// <summary>
    /// The options a deletion carries: the attempt cookie's Path, which a cookie is matched on, and Secure, without
    /// which a browser ignores a Set-Cookie for a __Host- name. It carries no Max-Age, which would take precedence
    /// over the expiry Delete writes and leave the emptied cookie alive.
    /// </summary>
    public static CookieOptions DeleteOptions() => new() { Path = BifrostCookies.Path, Secure = true };
}
