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
        int noticeVersion,
        CancellationToken cancellationToken)
    {
        var normalizedIdentity = NormalizeIdentity(identity);
        if (IsReserved(normalizedIdentity))
        {
            throw new BifrostIdentityException("That Bifrost identity is reserved.");
        }

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

        // One SaveChanges: no account exists without the receipt of the notice it accepted.
        dbContext.UserAccounts.Add(userAccount);
        dbContext.ConsentReceipts.Add(new ConsentReceipt
        {
            UserAccount = userAccount,
            NoticeVersion = noticeVersion,
            AcceptedAtUtc = now
        });
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
        for (var suffix = 2;
             IsReserved(normalizedIdentity) || await IdentityExistsAsync(userAccount.Id, normalizedIdentity, cancellationToken);
             suffix++)
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

    // Matrix localparts that already belong to someone else, and system names. The identity becomes the Matrix
    // localpart at SSO, so a new account must never mint one of these. Applied to the normalized identity when an
    // account is created (native register refuses, a derived identity takes the next free suffix); lookups of
    // existing accounts go through NormalizeIdentity and are untouched. There is no exemption path: Minds and
    // other known holders do not sign in through Bifrost identities.
    private static readonly HashSet<string> ReservedIdentities = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin", "aqua", "deru", "eureka", "heimdall", "ivixey", "kiko", "libby", "meta", "nibu", "unnie", "void", "weksa",
        "root", "system", "matrix", "synapse", "support", "bot", "mod", "moderator", "postmaster", "gamecult",
        "bifrost", "idunn", "odin", "huginn", "ratatoskr"
    };

    public static bool IsReserved(string normalizedIdentity) => ReservedIdentities.Contains(normalizedIdentity);

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
