using System.Security.Claims;
using System.Text.RegularExpressions;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Shared;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Web.Features.Membership;

public sealed partial class BifrostIdentityService(
    BifrostDbContext dbContext,
    TimeProvider timeProvider)
{
    public async Task<UserAccount> RegisterNativeAsync(
        string identity,
        string? displayName,
        CancellationToken cancellationToken)
    {
        var normalizedIdentity = NormalizeIdentity(identity);
        var exists = await dbContext.UserAccounts.AnyAsync(
            x => x.NormalizedBifrostIdentity == normalizedIdentity,
            cancellationToken);
        if (exists)
        {
            throw new BifrostIdentityException("That Bifrost identity is already registered.");
        }

        var now = timeProvider.GetUtcNow();
        var publicIdentity = ToPublicIdentity(normalizedIdentity);
        var resolvedDisplayName = string.IsNullOrWhiteSpace(displayName)
            ? publicIdentity
            : displayName.Trim();
        var userAccount = new UserAccount
        {
            BifrostIdentity = publicIdentity,
            NormalizedBifrostIdentity = normalizedIdentity,
            DisplayName = resolvedDisplayName,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            MemberProfile = new MemberProfile
            {
                Nickname = resolvedDisplayName,
                Headline = "Bifrost-native identity",
                UpdatedAtUtc = now
            },
            Membership = new Bifrost.Web.Domain.Membership
            {
                Status = MembershipStatus.Authenticated,
                CreatedAtUtc = now,
                Notes = "Registered with native Bifrost identity"
            }
        };

        dbContext.UserAccounts.Add(userAccount);
        await dbContext.SaveChangesAsync(cancellationToken);
        return userAccount;
    }

    public async Task EnsureIdentityAsync(
        UserAccount userAccount,
        string preferredIdentity,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(userAccount.NormalizedBifrostIdentity))
        {
            return;
        }

        var normalizedBase = NormalizeCandidate(preferredIdentity);
        var normalizedIdentity = normalizedBase;
        for (var suffix = 2; await IdentityExistsAsync(userAccount.Id, normalizedIdentity, cancellationToken); suffix++)
        {
            normalizedIdentity = $"{normalizedBase}-{suffix}";
        }

        userAccount.BifrostIdentity = ToPublicIdentity(normalizedIdentity);
        userAccount.NormalizedBifrostIdentity = normalizedIdentity;
    }

    public ClaimsPrincipal BuildPrincipal(UserAccount userAccount, string authProvider = "bifrost")
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userAccount.Id.ToString()),
            new(ClaimTypes.Name, userAccount.DisplayName),
            new(BifrostClaimTypes.BifrostIdentity, userAccount.BifrostIdentity),
            new(BifrostClaimTypes.AuthProvider, authProvider),
            new(BifrostClaimTypes.DisplayName, userAccount.DisplayName),
            new(BifrostClaimTypes.AvatarUrl, userAccount.AvatarUrl)
        };

        if (!string.IsNullOrWhiteSpace(userAccount.HeimdallAccountId))
        {
            claims.Add(new Claim(BifrostClaimTypes.HeimdallAccountId, userAccount.HeimdallAccountId));
        }

        if (!string.IsNullOrWhiteSpace(userAccount.GitHubLogin))
        {
            claims.Add(new Claim(BifrostClaimTypes.GitHubLogin, userAccount.GitHubLogin));
        }

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    public static string NormalizeIdentity(string identity)
    {
        var normalized = identity.Trim().ToLowerInvariant();
        if (!IdentityPattern().IsMatch(normalized))
        {
            throw new BifrostIdentityException(
                "Bifrost identity must be 3-80 characters and may contain letters, numbers, dots, underscores, or hyphens.");
        }

        return normalized;
    }

    private async Task<bool> IdentityExistsAsync(
        Guid currentUserAccountId,
        string normalizedIdentity,
        CancellationToken cancellationToken) =>
        await dbContext.UserAccounts.AnyAsync(
            x => x.Id != currentUserAccountId && x.NormalizedBifrostIdentity == normalizedIdentity,
            cancellationToken);

    private static string NormalizeCandidate(string preferredIdentity)
    {
        var candidate = CandidateInvalidCharacters().Replace(
            preferredIdentity.Trim().ToLowerInvariant(),
            "-");
        candidate = CandidateSeparators().Replace(candidate, "-").Trim('.', '_', '-');
        if (candidate.Length > 80)
        {
            candidate = candidate[..80].Trim('.', '_', '-');
        }

        return candidate.Length >= 3 ? candidate : "user";
    }

    private static string ToPublicIdentity(string normalizedIdentity) => normalizedIdentity;

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,78}[a-z0-9]$")]
    private static partial Regex IdentityPattern();

    [GeneratedRegex("[^a-z0-9._-]+")]
    private static partial Regex CandidateInvalidCharacters();

    [GeneratedRegex("[._-]{2,}")]
    private static partial Regex CandidateSeparators();
}

public sealed class BifrostIdentityException(string message) : InvalidOperationException(message);
