using Bifrost.Web.Features.Shared;
using Microsoft.AspNetCore.Authorization;

namespace Bifrost.Web.Features.Membership;

public sealed class ActiveMemberAuthorizationHandler(
    ICurrentBifrostActorAccessor actorAccessor,
    IHttpContextAccessor httpContextAccessor) : AuthorizationHandler<ActiveMemberRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ActiveMemberRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var cancellationToken = httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None;
        var actor = await actorAccessor.GetAsync(cancellationToken);

        if (actor.IsActiveMember)
        {
            context.Succeed(requirement);
        }
    }
}
