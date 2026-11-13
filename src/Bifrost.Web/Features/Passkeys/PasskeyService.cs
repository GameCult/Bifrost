using System.Globalization;
using System.Text;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Heimdall;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Web.Features.Passkeys;

/// <summary>One passkey as the Linked accounts page shows it: a label, its cooling-off state, never a key.</summary>
public sealed record PasskeyView(Guid Id, string Label, DateTimeOffset CreatedUtc, DateTimeOffset StrongFromUtc, DateTimeOffset? LastUsedUtc, bool IsStrong);

/// <summary>The base64url fields a browser posts after navigator.credentials.get (an assertion) or .create (an enrolment).</summary>
public sealed record PasskeyFields(
    string? Id, string? RawId, string? AuthenticatorData, string? ClientDataJson, string? Signature, string? UserHandle,
    string? AttestationObject, string? Transports);

/// <summary>
/// Passkeys for accounts that have a Heimdall account id (docs/passkeys.md). Enrolment needs a ticket earned by a fresh
/// Heimdall sign-in and starts a credential in a 48 hour cooling-off; an assertion for an unlink needs a credential that is
/// the session's own, not revoked and past its cooling-off, and records a single-use fact for that account and identity.
/// The WebAuthn parsing and verification (CBOR, COSE, signatures, origin, RP ID, challenge, user verification, sign count)
/// is Fido2NetLib's; this class decides who may begin, what is bound to what, and what is kept. Outcomes are the closed set
/// of <see cref="HeimdallLinkOutcome"/> the page words itself.
/// </summary>
public sealed class PasskeyService(
    BifrostDbContext db,
    PasskeyRelyingParty relyingParty,
    PasskeyCeremonies ceremonies,
    PasskeyRateLimit rateLimit,
    TimeProvider clock,
    ILogger<PasskeyService> logger)
{
    public const int MaxActive = 5;
    public const int MaxLabel = 40;
    public static readonly TimeSpan CoolingOff = TimeSpan.FromHours(48);

    private static readonly HashSet<string> TransportNames = new(StringComparer.Ordinal)
    {
        "usb", "nfc", "ble", "internal", "hybrid", "smart-card"
    };

    public bool IsAvailable => relyingParty.IsAvailable;

    private Task<UserAccount?> AccountAsync(string heimdallAccountId, CancellationToken ct) =>
        string.IsNullOrEmpty(heimdallAccountId)
            ? Task.FromResult<UserAccount?>(null)
            : db.UserAccounts.SingleOrDefaultAsync(x => x.HeimdallAccountId == heimdallAccountId, ct);

    private IQueryable<PasskeyCredential> Active(Guid userId) =>
        db.PasskeyCredentials.Where(x => x.UserAccountId == userId && x.RevokedUtc == null);

    public async Task<IReadOnlyList<PasskeyView>> ListAsync(string heimdallAccountId, CancellationToken ct)
    {
        if (await AccountAsync(heimdallAccountId, ct) is not { } user)
        {
            return [];
        }

        var now = clock.GetUtcNow();
        var rows = await Active(user.Id).ToListAsync(ct);
        return rows.OrderBy(x => x.CreatedUtc)
            .Select(x => new PasskeyView(x.Id, x.Label, x.CreatedUtc, x.StrongFromUtc, x.LastUsedUtc, x.StrongFromUtc <= now))
            .ToList();
    }

    /// <summary>Whether a fresh enrolment ticket is held by this browser for this account.</summary>
    public bool HasTicket(string heimdallAccountId, string? ticketId) =>
        ticketId is not null && ceremonies.Peek(CeremonyKind.EnrolTicket, heimdallAccountId, null, ticketId) is not null;

    /// <summary>Why a fresh sign-in for enrolment may not even start, or null when it may. Spends a ceremony permit.</summary>
    public async Task<HeimdallLinkOutcome?> PreflightEnrolStartAsync(string heimdallAccountId, CancellationToken ct)
    {
        if (!relyingParty.IsAvailable)
        {
            return HeimdallLinkOutcome.PasskeysUnavailable;
        }

        if (await AccountAsync(heimdallAccountId, ct) is not { } user)
        {
            return HeimdallLinkOutcome.Error;
        }

        if (await Active(user.Id).CountAsync(ct) >= MaxActive)
        {
            return HeimdallLinkOutcome.PasskeyLimit;
        }

        return rateLimit.TryAcquire(heimdallAccountId) ? null : HeimdallLinkOutcome.RateLimited;
    }

    /// <summary>The creation options for one enrolment, bound to the account; needs the ticket the fresh sign-in earned.</summary>
    public async Task<(HeimdallLinkOutcome? Refusal, string? OptionsJson)> BeginEnrolAsync(string heimdallAccountId, string? ticketId, CancellationToken ct)
    {
        if (!relyingParty.IsAvailable)
        {
            return (HeimdallLinkOutcome.PasskeysUnavailable, null);
        }

        if (!HasTicket(heimdallAccountId, ticketId) || await AccountAsync(heimdallAccountId, ct) is not { } user)
        {
            return (HeimdallLinkOutcome.EnrolNeedsSignIn, null);
        }

        if (!rateLimit.TryAcquire(heimdallAccountId))
        {
            return (HeimdallLinkOutcome.RateLimited, null);
        }

        var existing = await Active(user.Id).Select(x => x.CredentialId).ToListAsync(ct);
        if (existing.Count >= MaxActive)
        {
            return (HeimdallLinkOutcome.PasskeyLimit, null);
        }

        var options = relyingParty.Fido2!.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = user.Id.ToByteArray(), Name = Label(user.DisplayName, "Bifrost member"), DisplayName = Label(user.DisplayName, "Bifrost member") },
            ExcludeCredentials = existing.Select(x => new PublicKeyCredentialDescriptor(x)).ToList(),
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Preferred,
                UserVerification = UserVerificationRequirement.Required
            },
            AttestationPreference = AttestationConveyancePreference.None
        });
        var json = options.ToJson();
        return ceremonies.Issue(CeremonyKind.EnrolChallenge, heimdallAccountId, null, json, PasskeyCeremonies.ChallengeLifetime) is null
            ? (HeimdallLinkOutcome.Error, null)
            : (null, json);
    }

    /// <summary>
    /// Verifies a registration and stores the credential, cooling off for <see cref="CoolingOff"/>. The ticket and the
    /// challenge are both spent here, whatever the outcome: a failed attempt needs a fresh sign-in and a new challenge.
    /// </summary>
    public async Task<HeimdallLinkOutcome> EnrolAsync(
        string heimdallAccountId, string? ticketId, PasskeyFields fields, string? label, CancellationToken ct)
    {
        if (!relyingParty.IsAvailable)
        {
            return HeimdallLinkOutcome.PasskeysUnavailable;
        }

        // The caller's own preconditions first, spending nothing: someone with only the session has no ticket, so junk from them
        // can neither burn the member's ticket and challenge nor spend the verification permits.
        if (!HasTicket(heimdallAccountId, ticketId) || await AccountAsync(heimdallAccountId, ct) is not { } user)
        {
            return HeimdallLinkOutcome.EnrolNeedsSignIn;
        }

        if (await Active(user.Id).CountAsync(ct) >= MaxActive)
        {
            return HeimdallLinkOutcome.PasskeyLimit;
        }

        if (!TryDecode(fields.RawId, out var rawId) || !TryDecode(fields.AttestationObject, out var attestation)
            || !TryDecode(fields.ClientDataJson, out var clientData) || fields.Id is null)
        {
            return HeimdallLinkOutcome.PasskeyFailed;
        }

        if (!rateLimit.TryAcquire(heimdallAccountId, verification: true))
        {
            return HeimdallLinkOutcome.RateLimited;
        }

        // Now both are spent, whatever the outcome.
        var ticket = ceremonies.Take(CeremonyKind.EnrolTicket, heimdallAccountId, null, ticketId);
        var challenge = ceremonies.Take(CeremonyKind.EnrolChallenge, heimdallAccountId, null, null);
        if (ticket is null)
        {
            return HeimdallLinkOutcome.EnrolNeedsSignIn;
        }

        if (challenge?.Data is null)
        {
            return HeimdallLinkOutcome.PasskeyExpired;
        }

        try
        {
            var registered = await relyingParty.Fido2!.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = new AuthenticatorAttestationRawResponse
                {
                    Id = fields.Id,
                    RawId = rawId,
                    Type = PublicKeyCredentialType.PublicKey,
                    ClientExtensionResults = new AuthenticationExtensionsClientOutputs(),
                    Response = new AuthenticatorAttestationRawResponse.AttestationResponse
                    {
                        AttestationObject = attestation,
                        ClientDataJson = clientData,
                        Transports = Transports(fields.Transports)
                    }
                },
                OriginalOptions = CredentialCreateOptions.FromJson(challenge.Data),
                IsCredentialIdUniqueToUserCallback = async (parameters, token) =>
                    !await db.PasskeyCredentials.AnyAsync(x => x.CredentialId == parameters.CredentialId, token)
            }, ct);

            var now = clock.GetUtcNow();
            db.PasskeyCredentials.Add(new PasskeyCredential
            {
                UserAccountId = user.Id,
                CredentialId = registered.Id,
                PublicKey = registered.PublicKey,
                SignCount = registered.SignCount,
                Transports = string.Join(',', Transports(fields.Transports).Select(x => TransportName(x))),
                AaGuid = registered.AaGuid == Guid.Empty ? null : registered.AaGuid,
                Label = Label(label, "Passkey"),
                CreatedUtc = now,
                StrongFromUtc = now + CoolingOff
            });
            await db.SaveChangesAsync(ct);
            return HeimdallLinkOutcome.PasskeyAdded;
        }
        catch (Exception error) when (error is Fido2VerificationException or DbUpdateException or FormatException or System.Text.Json.JsonException)
        {
            logger.LogWarning("Passkey enrolment refused: {Reason}", error is Fido2VerificationException f ? f.Code.ToString() : error.GetType().Name);
            return HeimdallLinkOutcome.PasskeyFailed;
        }
    }

    /// <summary>Revokes one of the session's own passkeys. Needs no strong proof; the page shows the revocation.</summary>
    public async Task<HeimdallLinkOutcome> RevokeAsync(string heimdallAccountId, Guid credentialId, CancellationToken ct)
    {
        if (await AccountAsync(heimdallAccountId, ct) is not { } user)
        {
            return HeimdallLinkOutcome.Error;
        }

        var credential = await Active(user.Id).SingleOrDefaultAsync(x => x.Id == credentialId, ct);
        if (credential is null)
        {
            return HeimdallLinkOutcome.PasskeyFailed;
        }

        credential.RevokedUtc = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return HeimdallLinkOutcome.PasskeyRevoked;
    }

    private IQueryable<PasskeyCredential> Strong(Guid userId)
    {
        var now = clock.GetUtcNow();
        return Active(userId).Where(x => x.StrongFromUtc <= now);
    }

    public async Task<bool> HasStrongAsync(string heimdallAccountId, CancellationToken ct) =>
        await AccountAsync(heimdallAccountId, ct) is { } user && await Strong(user.Id).AnyAsync(ct);

    /// <summary>When the first cooling-off credential becomes strong, or null when none is cooling.</summary>
    public async Task<DateTimeOffset?> NextStrongAtAsync(string heimdallAccountId, CancellationToken ct)
    {
        if (await AccountAsync(heimdallAccountId, ct) is not { } user)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        var cooling = await Active(user.Id).Where(x => x.StrongFromUtc > now).Select(x => x.StrongFromUtc).ToListAsync(ct);
        return cooling.Count == 0 ? null : cooling.Min();
    }

    /// <summary>The request options for unlinking one identity: only the account's strong passkeys may answer, with user verification.</summary>
    public async Task<(HeimdallLinkOutcome? Refusal, string? OptionsJson)> BeginAssertionAsync(string heimdallAccountId, string identityId, CancellationToken ct)
    {
        if (!relyingParty.IsAvailable)
        {
            return (HeimdallLinkOutcome.PasskeysUnavailable, null);
        }

        if (!rateLimit.TryAcquire(heimdallAccountId))
        {
            return (HeimdallLinkOutcome.RateLimited, null);
        }

        if (await AccountAsync(heimdallAccountId, ct) is not { } user)
        {
            return (HeimdallLinkOutcome.Error, null);
        }

        var strong = await Strong(user.Id).ToListAsync(ct);
        if (strong.Count == 0)
        {
            return (HeimdallLinkOutcome.PasskeyRequired, null);
        }

        var options = relyingParty.Fido2!.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = strong.Select(x => new PublicKeyCredentialDescriptor(PublicKeyCredentialType.PublicKey, x.CredentialId, Transports(x.Transports))).ToList(),
            UserVerification = UserVerificationRequirement.Required
        });
        var json = options.ToJson();
        return ceremonies.Issue(CeremonyKind.UnlinkChallenge, heimdallAccountId, identityId, json, PasskeyCeremonies.ChallengeLifetime) is null
            ? (HeimdallLinkOutcome.Error, null)
            : (null, json);
    }

    /// <summary>
    /// Verifies an assertion made for unlinking <paramref name="identityId"/>. The challenge is spent first. On success the
    /// credential's sign count and last use are recorded and a single-use fact for this account and identity is issued; its id
    /// (not a credential, not a bearer) is what the fresh Heimdall sign-in's attempt carries.
    /// </summary>
    public async Task<(HeimdallLinkOutcome? Refusal, string? FactId)> VerifyAssertionAsync(
        string heimdallAccountId, string identityId, PasskeyFields fields, CancellationToken ct)
    {
        if (!relyingParty.IsAvailable)
        {
            return (HeimdallLinkOutcome.PasskeysUnavailable, null);
        }

        // The caller's own preconditions first, spending nothing: a decodable assertion naming one of this account's strong,
        // un-revoked credentials. Junk from someone holding only the session fails here and burns neither the member's challenge
        // nor the verification permits.
        if (await AccountAsync(heimdallAccountId, ct) is not { } user
            || !TryDecode(fields.RawId, out var rawId) || !TryDecode(fields.AuthenticatorData, out var authenticatorData)
            || !TryDecode(fields.ClientDataJson, out var clientData) || !TryDecode(fields.Signature, out var signature)
            || fields.Id is null)
        {
            return (HeimdallLinkOutcome.PasskeyFailed, null);
        }

        byte[]? userHandle = null;
        if (!string.IsNullOrEmpty(fields.UserHandle) && !TryDecode(fields.UserHandle, out userHandle))
        {
            return (HeimdallLinkOutcome.PasskeyFailed, null);
        }

        // The credential must be this session's own, not revoked and past its cooling-off. Anything else is not a strong proof.
        var credential = await Strong(user.Id).SingleOrDefaultAsync(x => x.CredentialId == rawId, ct);
        if (credential is null)
        {
            return (HeimdallLinkOutcome.PasskeyFailed, null);
        }

        // Without a live challenge for this account and identity nothing can verify, so no permit is spent either: junk naming the
        // account's own credential spends at most one permit per challenge that was asked for (the first junk then takes it).
        if (ceremonies.Peek(CeremonyKind.UnlinkChallenge, heimdallAccountId, identityId, null) is null)
        {
            return (HeimdallLinkOutcome.PasskeyExpired, null);
        }

        if (!rateLimit.TryAcquire(heimdallAccountId, verification: true))
        {
            return (HeimdallLinkOutcome.RateLimited, null);
        }

        // Spent from here on, whatever the outcome.
        var challenge = ceremonies.Take(CeremonyKind.UnlinkChallenge, heimdallAccountId, identityId, null);
        if (challenge?.Data is null)
        {
            return (HeimdallLinkOutcome.PasskeyExpired, null);
        }

        try
        {
            var result = await relyingParty.Fido2!.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = new AuthenticatorAssertionRawResponse
                {
                    Id = fields.Id,
                    RawId = rawId,
                    Type = PublicKeyCredentialType.PublicKey,
                    ClientExtensionResults = new AuthenticationExtensionsClientOutputs(),
                    Response = new AuthenticatorAssertionRawResponse.AssertionResponse
                    {
                        AuthenticatorData = authenticatorData,
                        ClientDataJson = clientData,
                        Signature = signature,
                        UserHandle = userHandle
                    }
                },
                OriginalOptions = AssertionOptions.FromJson(challenge.Data),
                StoredPublicKey = credential.PublicKey,
                StoredSignatureCounter = (uint)credential.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (parameters, _) =>
                    Task.FromResult(parameters.UserHandle.AsSpan().SequenceEqual(user.Id.ToByteArray()) && parameters.CredentialId.AsSpan().SequenceEqual(credential.CredentialId))
            }, ct);

            credential.SignCount = result.SignCount;
            credential.LastUsedUtc = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        catch (Exception error) when (error is Fido2VerificationException or FormatException or System.Text.Json.JsonException)
        {
            logger.LogWarning("Passkey assertion refused: {Reason}", error is Fido2VerificationException f ? f.Code.ToString() : error.GetType().Name);
            return (HeimdallLinkOutcome.PasskeyFailed, null);
        }

        var fact = ceremonies.Issue(CeremonyKind.UnlinkProof, heimdallAccountId, identityId, credential.Id.ToString(), PasskeyCeremonies.ProofLifetime);
        return fact is null ? (HeimdallLinkOutcome.Error, null) : (null, fact);
    }

    public void DiscardFact(string? factId) => ceremonies.Discard(factId);

    /// <summary>
    /// Whether the credential an assertion fact was made with is still this account's own, un-revoked and strong. Asked in the
    /// completion, before Heimdall is: a passkey revoked between the assertion and the completion no longer proves anything.
    /// </summary>
    public async Task<bool> StillStrongAsync(string heimdallAccountId, string? credentialId, CancellationToken ct) =>
        Guid.TryParse(credentialId, out var id) && await AccountAsync(heimdallAccountId, ct) is { } user
        && await Strong(user.Id).AnyAsync(x => x.Id == id, ct);

    /// <summary>A label from a form field: trimmed, control characters dropped, bounded; the fallback when nothing is left.</summary>
    private static string Label(string? value, string fallback)
    {
        // By rune and by text element, never by UTF-16 unit: a cut inside a surrogate pair is not valid text and Postgres refuses it.
        // Control and format characters (bidi overrides, joiners, zero-width marks) are dropped; a lone surrogate becomes U+FFFD.
        var runes = (value ?? string.Empty).EnumerateRunes()
            .Where(r => Rune.GetUnicodeCategory(r) is not (UnicodeCategory.Control or UnicodeCategory.Format))
            .Take(MaxLabel * 4);
        var cleaned = string.Concat(runes.Select(r => r.ToString())).Trim();
        var elements = new StringInfo(cleaned);
        if (elements.LengthInTextElements > MaxLabel)
        {
            cleaned = elements.SubstringByTextElements(0, MaxLabel);
        }

        return cleaned.Length == 0 ? fallback : cleaned;
    }

    private static bool TryDecode(string? value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(value) || value.Length > 16_384)
        {
            return false;
        }

        try
        {
            bytes = WebEncoders.Base64UrlDecode(value);
            return bytes.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static AuthenticatorTransport[] Transports(string? csv)
    {
        var parsed = new List<AuthenticatorTransport>();
        foreach (var name in (csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct())
        {
            try
            {
                // Only names of the closed set reach the converter; one it does not know is dropped.
                if (TransportNames.Contains(name))
                {
                    parsed.Add(System.Text.Json.JsonSerializer.Deserialize<AuthenticatorTransport>($"\"{name}\""));
                }
            }
            catch (System.Text.Json.JsonException)
            {
            }
        }

        return parsed.ToArray();
    }

    private static string TransportName(AuthenticatorTransport transport) =>
        System.Text.Json.JsonSerializer.Serialize(transport).Trim('"');
}
