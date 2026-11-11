using Bifrost.Web.Domain;
using Bifrost.Web.Features.Membership;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Bifrost.Web.Features.Registration;

/// <summary>What a client sent to register. <see cref="Waited"/> claims the time-lock path instead of a proof of work.</summary>
public sealed record RegistrationAttempt(
    string? Identity,
    string? DisplayName,
    string? Challenge,
    string? Nonce,
    bool Waited,
    int? AcceptedNoticeVersion);

/// <summary>Which input a refusal is about, so the form can mark it invalid.</summary>
public enum RegistrationField { None, Identity, Consent }

/// <summary>
/// Either a signed-in <see cref="Account"/>, or a <see cref="Refusal"/> to return as is (the rate limits), or a rejection
/// with a status and message. <see cref="KeepChallenge"/> is set when the challenge was not spent and a retry needs only a correction, so the form should carry it on (the time-lock clock must not restart).
/// </summary>
public sealed record RegistrationOutcome(
    UserAccount? Account,
    IResult? Refusal,
    int Status,
    string? Message,
    RegistrationField Field,
    bool KeepChallenge)
{
    public static RegistrationOutcome Reject(int status, string message, RegistrationField field = RegistrationField.None, bool keepChallenge = false) =>
        new(null, null, status, message, field, keepChallenge);
}

/// <summary>
/// The one registration gate, run by the JSON endpoint and the form alike after each has spent the general address budget.
/// Order: consent version (when sent), one proof only, the identity rules (which spend nothing), the challenge (proof of work, or the
/// time-lock plus its tighter budget), then the account (and its receipt when consent was sent) in one save, then the cookie. A refusal at any step
/// creates nothing, and a refusal before the challenge step spends neither the challenge nor a fallback permit.
/// </summary>
public sealed class NativeRegistration(
    BifrostIdentityService identities,
    RegistrationChallengeService challenges,
    RegistrationRateLimit rateLimit)
{
    public async Task<RegistrationOutcome> RunAsync(HttpContext http, RegistrationAttempt attempt, CancellationToken cancellationToken)
    {
        // Consent is optional: none sends no receipt. A version that is sent must be the one in force; a refusal here creates nothing.
        if (attempt.AcceptedNoticeVersion is { } sent && sent != TrainingNotice.Version)
        {
            return RegistrationOutcome.Reject(
                StatusCodes.Status400BadRequest,
                $"The training notice in force is version {TrainingNotice.Version}; send that version to agree, or send none to register without agreeing.",
                RegistrationField.Consent);
        }

        var hasNonce = !string.IsNullOrEmpty(attempt.Nonce);
        if (hasNonce && attempt.Waited)
        {
            return RegistrationOutcome.Reject(StatusCodes.Status400BadRequest, "Send a nonce or waited, not both.");
        }

        try
        {
            await identities.CheckAvailableAsync(attempt.Identity ?? "", cancellationToken);
        }
        catch (BifrostIdentityException error)
        {
            return RegistrationOutcome.Reject(StatusCodes.Status400BadRequest, error.Message, RegistrationField.Identity, keepChallenge: true);
        }

        var failed = RegistrationOutcome.Reject(StatusCodes.Status400BadRequest, "Registration challenge failed.");
        if (hasNonce)
        {
            if (!challenges.TryConsume(attempt.Challenge, attempt.Nonce))
            {
                return failed;
            }
        }
        else if (attempt.Waited)
        {
            if (challenges.WaitRemaining(attempt.Challenge) is not { } remaining)
            {
                return failed;
            }

            if (remaining > TimeSpan.Zero)
            {
                var seconds = (int)Math.Ceiling(remaining.TotalSeconds);
                return RegistrationOutcome.Reject(
                    StatusCodes.Status400BadRequest,
                    $"Too early: {seconds} more second{(seconds == 1 ? "" : "s")} to wait after the page opened. Your challenge is still good; submit again then.",
                    keepChallenge: true);
            }

            if (rateLimit.RefuseFallback(http) is { } fallbackRefused)
            {
                return new(null, fallbackRefused, 0, null, RegistrationField.None, false);
            }

            if (!challenges.TryConsumeWaited(attempt.Challenge))
            {
                return failed;
            }
        }
        else
        {
            return failed;
        }

        try
        {
            var account = await identities.RegisterNativeAsync(attempt.Identity ?? "", attempt.DisplayName, attempt.AcceptedNoticeVersion, cancellationToken);
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, identities.BuildPrincipal(account));
            return new(account, null, StatusCodes.Status200OK, null, RegistrationField.None, false);
        }
        catch (BifrostIdentityException error)
        {
            return RegistrationOutcome.Reject(StatusCodes.Status400BadRequest, error.Message, RegistrationField.Identity);
        }
    }
}
