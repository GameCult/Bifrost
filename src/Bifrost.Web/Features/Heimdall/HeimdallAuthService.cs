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
    BifrostDbContext dbContext,
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
            throw new HeimdallAuthException($"Unsupported Heimdall provider '{provider}'.");
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
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HeimdallAuthException($"Heimdall rejected {provider} auth start: {detail}");
        }

        var payload = await response.Content.ReadFromJsonAsync<HeimdallStartResponse>(cancellationToken);
        if (payload is null || string.IsNullOrWhiteSpace(payload.AuthorizationUrl))
        {
            throw new HeimdallAuthException("Heimdall auth start returned no authorization URL.");
        }

        attemptStore.Create(attemptId, returnUrl);
        attemptStore.SetRequiresMemberAccess(attemptId, requireMemberAccess);
        return new HeimdallAuthStartResult(new Uri(payload.AuthorizationUrl, UriKind.Absolute), attemptId);
    }

    public void StoreBackendCallback(HeimdallBackendCallbackPayload payload)
    {
        if (!string.Equals(payload.AppSlug, heimdallOptions.Value.AppSlug, StringComparison.OrdinalIgnoreCase))
        {
            throw new HeimdallAuthException("Heimdall callback targeted a different app.");
        }

        if (string.IsNullOrWhiteSpace(payload.AttemptId))
        {
            throw new HeimdallAuthException("Heimdall callback omitted attempt id.");
        }

        attemptStore.Complete(payload.AttemptId, payload);
    }

    public async Task<HeimdallCompletedSignIn> CompleteAttemptAsync(
        string attemptId,
        CancellationToken cancellationToken)
    {
        var attempt = attemptStore.TryTake(attemptId)
            ?? throw new HeimdallAuthPendingException("Heimdall has not delivered this auth attempt yet.");
        var payload = attempt.Payload
            ?? throw new HeimdallAuthPendingException("Heimdall has not delivered this auth attempt yet.");

        if (!payload.IsSuccessful)
        {
            throw new HeimdallAuthException(payload.ErrorDescription ?? payload.Error ?? "Heimdall auth did not succeed.");
        }

        if (attempt.RequiresMemberAccess && !payload.HasMemberAccess)
        {
            throw new HeimdallAuthException("Heimdall did not grant Bifrost member access.");
        }

        var userAccount = await UpsertUserAccountAsync(payload, attempt.RequiresMemberAccess, cancellationToken);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userAccount.GitHubUserId?.ToString() ?? userAccount.HeimdallAccountId),
            new(ClaimTypes.Name, userAccount.DisplayName),
            new(BifrostClaimTypes.HeimdallAccountId, userAccount.HeimdallAccountId),
            new(BifrostClaimTypes.AuthProvider, payload.Provider ?? "heimdall"),
            new(BifrostClaimTypes.DisplayName, userAccount.DisplayName),
            new(BifrostClaimTypes.AvatarUrl, userAccount.AvatarUrl)
        };

        if (!string.IsNullOrWhiteSpace(userAccount.GitHubLogin))
        {
            claims.Add(new Claim(BifrostClaimTypes.GitHubLogin, userAccount.GitHubLogin));
        }

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        return new HeimdallCompletedSignIn(
            principal,
            string.IsNullOrWhiteSpace(attempt.ReturnUrl) ? "/App" : attempt.ReturnUrl);
    }

    private async Task<UserAccount> UpsertUserAccountAsync(
        HeimdallBackendCallbackPayload payload,
        bool grantMemberAccess,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var accountId = payload.Account?.Id ?? throw new HeimdallAuthException("Heimdall response omitted account id.");
        var displayName = payload.Account.DisplayName ?? accountId;

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
                    Headline = $"Verified through Heimdall {payload.Provider}",
                    UpdatedAtUtc = now
                },
                Membership = new Bifrost.Web.Domain.Membership
                {
                    Status = grantMemberAccess ? MembershipStatus.Active : MembershipStatus.Authenticated,
                    CreatedAtUtc = now,
                    ApprovedAtUtc = grantMemberAccess ? now : null,
                    Notes = grantMemberAccess
                        ? "Activated by Heimdall member access claim"
                        : $"Identity verified through Heimdall {payload.Provider}"
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
                Headline = $"Verified through Heimdall {payload.Provider}",
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

public sealed record HeimdallAuthStartResult(Uri AuthorizationUrl, string AttemptId);

public sealed record HeimdallCompletedSignIn(ClaimsPrincipal Principal, string RedirectUri);

public sealed class HeimdallAuthAttemptStore
{
    private readonly ConcurrentDictionary<string, HeimdallAuthAttempt> _attempts = new(StringComparer.Ordinal);

    public void Create(string attemptId, string returnUrl)
    {
        _attempts[attemptId] = new HeimdallAuthAttempt(returnUrl, null);
    }

    public void SetRequiresMemberAccess(string attemptId, bool requiresMemberAccess)
    {
        _attempts.AddOrUpdate(
            attemptId,
            _ => new HeimdallAuthAttempt("/App", null, requiresMemberAccess),
            (_, existing) => existing with { RequiresMemberAccess = requiresMemberAccess });
    }

    public void Complete(string attemptId, HeimdallBackendCallbackPayload payload)
    {
        _attempts.AddOrUpdate(
            attemptId,
            _ => new HeimdallAuthAttempt(payload.ReturnTo ?? "/App", payload),
            (_, existing) => existing with { Payload = payload });
    }

    public HeimdallAuthAttempt? TryTake(string attemptId)
    {
        return _attempts.TryRemove(attemptId, out var attempt) ? attempt : null;
    }
}

public sealed record HeimdallAuthAttempt(
    string ReturnUrl,
    HeimdallBackendCallbackPayload? Payload,
    bool RequiresMemberAccess = false);

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
    [property: JsonPropertyName("appSlug")] string? AppSlug,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("provider")] string? Provider,
    [property: JsonPropertyName("returnTo")] string? ReturnTo,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("errorDescription")] string? ErrorDescription,
    [property: JsonPropertyName("account")] HeimdallAccount? Account,
    [property: JsonPropertyName("sharedCapabilities")] string[]? SharedCapabilities,
    [property: JsonPropertyName("entitlements")] HeimdallEntitlements? Entitlements)
{
    public bool IsSuccessful => string.Equals(Status, "success", StringComparison.OrdinalIgnoreCase);

    public bool HasMemberAccess =>
        SharedCapabilities?.Any(capability =>
            string.Equals(capability, "member_access", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(capability, "app_access", StringComparison.OrdinalIgnoreCase)) == true ||
        Entitlements?.Facts?.Any(fact =>
            string.Equals(fact, "entitlement.app_access", StringComparison.OrdinalIgnoreCase)) == true;
}

public sealed record HeimdallAccount(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("displayName")] string? DisplayName);

public sealed record HeimdallEntitlements(
    [property: JsonPropertyName("facts")] string[]? Facts);
