using System.Security.Claims;
using Bifrost.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Web.Features.Shared;

public interface ICurrentBifrostActorAccessor
{
    Task<CurrentBifrostActor> GetAsync(CancellationToken cancellationToken);
}

public sealed class CurrentBifrostActorAccessor(
    IHttpContextAccessor httpContextAccessor,
    BifrostDbContext dbContext) : ICurrentBifrostActorAccessor
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

        var gitHubId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!long.TryParse(gitHubId, out var gitHubUserId))
        {
            return Cache(httpContext, CurrentBifrostActor.Anonymous);
        }

        var userAccount = await dbContext.UserAccounts
            .AsNoTracking()
            .Include(x => x.MemberProfile)
            .Include(x => x.Membership)
            .SingleOrDefaultAsync(x => x.GitHubUserId == gitHubUserId, cancellationToken);

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
