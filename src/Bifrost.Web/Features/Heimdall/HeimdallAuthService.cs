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
/// attempt's cookie and the redeemed return target names the same attempt.
/// </summary>
public sealed class HeimdallAuthService(
    HttpClient httpClient,
    HeimdallTokenVerifier tokenVerifier,
    BifrostDbContext dbContext,
    BifrostIdentityService bifrostIdentityService,
    IOptions<HeimdallOptions> heimdallOptions,
    IDataProtectionProvider dataProtectionProvider,
    TimeProvider timeProvider)
{
    public const string ErrorRedirect = "/?auth=heimdall-error";
    public const string AppSecretHeader = "x-heimdall-app-secret";

    private static readonly HashSet<string> AllowedProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "discord",
        "patreon",
        "twitch"
    };

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

    public async Task<HeimdallAuthStartResult> StartAsync(
        string provider,
        string callbackBaseUrl,
        LocalReturnPath returnPath,
        bool requireMemberAccess,
        CancellationToken cancellationToken)
    {
        if (!AllowedProviders.Contains(provider))
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
            entitlementPolicy);

        var response = await httpClient.PostAsJsonAsync(
            $"/v1/oauth/{provider}/start",
            request,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HeimdallAuthException("Heimdall rejected the auth start.");
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
            timeProvider.GetUtcNow() + HeimdallAttemptCookie.Lifetime);
        return new HeimdallAuthStartResult(
            new Uri(payload.AuthorizationUrl, UriKind.Absolute), HeimdallAttemptCookie.NameFor(nonce)!, Protect(attempt));
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
            attempt.Nonce, attempt.Provider, attempt.RequiresMemberAccess, attempt.ReturnPath.Value, attempt.ExpiresAt)));

    private HeimdallAttempt? Unprotect(string? cookieValue, string? nonce)
    {
        if (string.IsNullOrEmpty(cookieValue) || string.IsNullOrEmpty(nonce))
        {
            return null;
        }

        try
        {
            var wire = JsonSerializer.Deserialize<AttemptWire>(_protector.Unprotect(cookieValue));
            if (wire is null ||
                !string.Equals(wire.Nonce, nonce, StringComparison.Ordinal) ||
                wire.ExpiresAt <= timeProvider.GetUtcNow())
            {
                return null;
            }

            return new HeimdallAttempt(
                wire.Nonce, wire.Provider, wire.RequiresMemberAccess, LocalReturnPath.Parse(wire.ReturnPath), wire.ExpiresAt);
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
        DateTimeOffset ExpiresAt);

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
            if (grantMemberAccess)
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
        if (grantMemberAccess)
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
    DateTimeOffset ExpiresAt);

public sealed record HeimdallStartRequest(
    [property: JsonPropertyName("appSlug")] string AppSlug,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("returnTo")] string ReturnTo,
    [property: JsonPropertyName("handoff")] HeimdallHandoff Handoff,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("entitlementPolicy")] HeimdallEntitlementPolicy? EntitlementPolicy);

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
    [property: JsonPropertyName("account")] HeimdallAccount? Account);

public sealed record HeimdallAccount(
    [property: JsonPropertyName("id")] string Id);

/// <summary>
/// The per-attempt cookie: named for the attempt's nonce so two attempts in one browser both complete.
/// A name is only built from a well-formed nonce, so request input never becomes an arbitrary cookie name.
/// </summary>
public static class HeimdallAttemptCookie
{
    public const string Prefix = "bifrost_heimdall_";
    public const int NonceLength = 43;

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public static string? NameFor(string? nonce) =>
        nonce is { Length: NonceLength } && nonce.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? Prefix + nonce
            : null;

    public static CookieOptions Options() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/auth/heimdall",
        MaxAge = Lifetime
    };

    /// <summary>
    /// The options a deletion carries: only the attempt cookie's Path, which a cookie is matched on. It carries no
    /// Max-Age, which would take precedence over the expiry Delete writes and leave the emptied cookie alive.
    /// </summary>
    public static CookieOptions DeleteOptions() => new() { Path = "/auth/heimdall" };
}
