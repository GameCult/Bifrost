using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Bifrost.Web.Configuration;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Features.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Features.Heimdall;

public sealed class HeimdallAuthService(
    HttpClient httpClient,
    HeimdallAuthAttemptStore attemptStore,
    HeimdallTokenVerifier tokenVerifier,
    BifrostDbContext dbContext,
    BifrostIdentityService bifrostIdentityService,
    IOptions<HeimdallOptions> heimdallOptions,
    TimeProvider timeProvider)
{
    private static readonly HashSet<string> AllowedProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "discord",
        "patreon",
        "twitch"
    };

    public async Task<HeimdallAuthStartResult> StartAsync(
        string provider,
        string callbackBaseUrl,
        string returnUrl,
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
        var attemptId = Guid.NewGuid().ToString("N");
        var callbackUrl = new Uri(new Uri(callbackBaseUrl.TrimEnd('/') + "/", UriKind.Absolute), "auth/heimdall/callback");

        var request = new HeimdallStartRequest(
            options.AppSlug,
            "sign_in",
            ToAttemptReturnUrl(callbackBaseUrl, attemptId, returnUrl),
            new HeimdallHandoff("backend_callback", attemptId, callbackUrl.ToString()),
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

        var binding = attemptStore.Create(attemptId, provider, returnUrl, requireMemberAccess);
        return new HeimdallAuthStartResult(new Uri(payload.AuthorizationUrl, UriKind.Absolute), attemptId, binding);
    }

    public async Task StoreBackendCallbackAsync(
        HeimdallBackendCallbackPayload payload,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload.AttemptId))
        {
            throw new HeimdallAuthException("Heimdall callback omitted attempt id.");
        }

        if (string.IsNullOrWhiteSpace(payload.AccessToken))
        {
            throw new HeimdallAuthException("Heimdall callback carried no access token.");
        }

        var identity = await tokenVerifier.VerifyAsync(payload.AccessToken, payload.Account?.Id, cancellationToken);
        attemptStore.Complete(payload.AttemptId, identity);
    }

    public async Task<HeimdallCompletedSignIn> CompleteAttemptAsync(
        string attemptId,
        string? binding,
        CancellationToken cancellationToken)
    {
        var attempt = attemptStore.TryTake(attemptId, binding);
        var identity = attempt.Identity;
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
            attempt.ReturnUrl);
    }

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

        throw new HeimdallAuthException($"Heimdall provider '{provider}' does not support Bifrost member access.");
    }

    private static string ToAttemptReturnUrl(string callbackBaseUrl, string attemptId, string returnUrl)
    {
        var baseUri = new Uri(callbackBaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        var target = string.IsNullOrWhiteSpace(returnUrl) ? "/App" : returnUrl;
        var waitUri = new Uri(baseUri, "auth/heimdall/wait");
        return $"{waitUri}?attemptId={Uri.EscapeDataString(attemptId)}&returnTo={Uri.EscapeDataString(target)}";
    }
}

public class HeimdallAuthException(string message) : InvalidOperationException(message);

public sealed class HeimdallAuthPendingException(string message) : HeimdallAuthException(message);

public sealed record HeimdallAuthStartResult(Uri AuthorizationUrl, string AttemptId, string Binding);

public sealed record HeimdallCompletedSignIn(ClaimsPrincipal Principal, string RedirectUri);

/// <summary>
/// Attempts Start issued. An attempt lives for <see cref="Lifetime"/>, is completed only by a verified
/// Heimdall identity, and is redeemed once by the browser that holds its binding value. The store holds at
/// most <see cref="Capacity"/> attempts; a full store drops the one that expires first.
/// </summary>
public sealed class HeimdallAuthAttemptStore(TimeProvider timeProvider)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public const int Capacity = 1024;

    private readonly object _gate = new();
    private readonly Dictionary<string, HeimdallAuthAttempt> _attempts = new(StringComparer.Ordinal);

    public string Create(string attemptId, string provider, string returnUrl, bool requiresMemberAccess)
    {
        var now = timeProvider.GetUtcNow();
        var binding = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        lock (_gate)
        {
            while (_attempts.Count >= Capacity)
            {
                _attempts.Remove(_attempts.MinBy(x => x.Value.ExpiresAt).Key);
            }

            _attempts[attemptId] = new HeimdallAuthAttempt(
                provider, returnUrl, requiresMemberAccess, binding, now + Lifetime, null);
        }

        return binding;
    }

    public void Complete(string attemptId, HeimdallVerifiedIdentity identity)
    {
        lock (_gate)
        {
            if (!_attempts.TryGetValue(attemptId, out var attempt) ||
                attempt.ExpiresAt <= timeProvider.GetUtcNow() ||
                attempt.Identity is not null)
            {
                throw new HeimdallAuthException("Heimdall callback did not match a pending attempt.");
            }

            _attempts[attemptId] = attempt with { Identity = identity };
        }
    }

    public HeimdallRedeemedAttempt TryTake(string attemptId, string? binding)
    {
        lock (_gate)
        {
            if (!_attempts.TryGetValue(attemptId, out var attempt) ||
                attempt.ExpiresAt <= timeProvider.GetUtcNow() ||
                binding is null ||
                !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(binding),
                    System.Text.Encoding.UTF8.GetBytes(attempt.Binding)))
            {
                throw new HeimdallAuthException("Heimdall auth attempt is not valid for this browser.");
            }

            if (attempt.Identity is null)
            {
                throw new HeimdallAuthPendingException("Heimdall has not delivered this auth attempt yet.");
            }

            _attempts.Remove(attemptId);
            return new HeimdallRedeemedAttempt(
                attempt.Provider, attempt.ReturnUrl, attempt.RequiresMemberAccess, attempt.Identity);
        }
    }
}

public sealed record HeimdallAuthAttempt(
    string Provider,
    string ReturnUrl,
    bool RequiresMemberAccess,
    string Binding,
    DateTimeOffset ExpiresAt,
    HeimdallVerifiedIdentity? Identity);

public sealed record HeimdallRedeemedAttempt(
    string Provider,
    string ReturnUrl,
    bool RequiresMemberAccess,
    HeimdallVerifiedIdentity Identity);

public sealed record HeimdallStartRequest(
    [property: JsonPropertyName("appSlug")] string AppSlug,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("returnTo")] string ReturnTo,
    [property: JsonPropertyName("handoff")] HeimdallHandoff Handoff,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("entitlementPolicy")] HeimdallEntitlementPolicy? EntitlementPolicy);

public sealed record HeimdallHandoff(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("attemptId")] string? AttemptId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("callbackUrl")] string? CallbackUrl = null);

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

public sealed record HeimdallBackendCallbackPayload(
    [property: JsonPropertyName("attemptId")] string AttemptId,
    [property: JsonPropertyName("accessToken")] string? AccessToken,
    [property: JsonPropertyName("account")] HeimdallAccount? Account);

public sealed record HeimdallAccount(
    [property: JsonPropertyName("id")] string Id);

public static class HeimdallBindingCookie
{
    public const string Name = "bifrost_heimdall_binding";

    public static CookieOptions Options() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/auth/heimdall",
        MaxAge = HeimdallAuthAttemptStore.Lifetime
    };
}
