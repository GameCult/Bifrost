using System.Security.Claims;
using Bifrost.Web.Data;
using Bifrost.Web.Features.Membership;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Web.Features.Shared;

public interface ICurrentBifrostActorAccessor
{
    Task<CurrentBifrostActor> GetAsync(CancellationToken cancellationToken);
}

public sealed class CurrentBifrostActorAccessor(
    IHttpContextAccessor httpContextAccessor,
    BifrostDbContext dbContext,
    AccountMergeLedger ledger) : ICurrentBifrostActorAccessor
{
    private const string CacheKey = "Bifrost.CurrentActor";

    public async Task<CurrentBifrostActor> GetAsync(CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext?.Items.TryGetValue(CacheKey, out var cachedActor) == true &&
            cachedActor is CurrentBifrostActor actor)
        {
            return actor;
        }

        if (httpContext?.User.Identity?.IsAuthenticated != true)
        {
            return Cache(httpContext, CurrentBifrostActor.Anonymous);
        }

        var bifrostIdentity = httpContext.User.FindFirstValue(BifrostClaimTypes.BifrostIdentity);
        var normalizedBifrostIdentity = string.IsNullOrWhiteSpace(bifrostIdentity)
            ? string.Empty
            : BifrostIdentityService.NormalizeIdentity(bifrostIdentity);
        var heimdallAccountId = httpContext.User.FindFirstValue(BifrostClaimTypes.HeimdallAccountId);
        var gitHubId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var hasGitHubUserId = long.TryParse(gitHubId, out var gitHubUserId);

        if (string.IsNullOrWhiteSpace(normalizedBifrostIdentity) &&
            string.IsNullOrWhiteSpace(heimdallAccountId) &&
            !hasGitHubUserId)
        {
            return Cache(httpContext, CurrentBifrostActor.Anonymous);
        }

        // One credential names one row: identity first, then Heimdall id, then GitHub id; the first hit wins. Two rows matching
        // one principal (a merge can leave a login on either side) must never throw or pick by luck.
        var accounts = dbContext.UserAccounts
            .AsNoTracking()
            .Include(x => x.MemberProfile)
            .Include(x => x.Membership!)
                .ThenInclude(x => x.RoleAssignments)
            .Include(x => x.Membership!)
                .ThenInclude(x => x.TierSnapshots);

        Bifrost.Web.Domain.UserAccount? userAccount = null;
        if (!string.IsNullOrWhiteSpace(normalizedBifrostIdentity))
        {
            userAccount = await accounts.SingleOrDefaultAsync(x => x.NormalizedBifrostIdentity == normalizedBifrostIdentity, cancellationToken);
        }

        if (userAccount is null && !string.IsNullOrWhiteSpace(heimdallAccountId))
        {
            userAccount = await accounts.SingleOrDefaultAsync(x => x.HeimdallAccountId == heimdallAccountId, cancellationToken);
        }

        if (userAccount is null && hasGitHubUserId)
        {
            userAccount = await accounts.SingleOrDefaultAsync(x => x.GitHubUserId == gitHubUserId, cancellationToken);
        }

        // A folded or frozen account acts as nobody on every route: this is the one gate (merge ledger standing, derived per request).
        if (userAccount is not null)
        {
            var standing = await ledger.StandingAsync(userAccount.Id, cancellationToken);
            if (standing is not null)
            {
                return Cache(httpContext, new CurrentBifrostActor(true, null) { Merge = standing });
            }
        }

        return Cache(httpContext, new CurrentBifrostActor(true, userAccount));
    }

    private static CurrentBifrostActor Cache(HttpContext? httpContext, CurrentBifrostActor actor)
    {
        if (httpContext is not null)
        {
            httpContext.Items[CacheKey] = actor;
        }

        return actor;
    }
}
