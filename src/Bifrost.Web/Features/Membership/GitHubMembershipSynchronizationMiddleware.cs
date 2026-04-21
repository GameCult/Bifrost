namespace Bifrost.Web.Features.Membership;

public sealed class GitHubMembershipSynchronizationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        MembershipSynchronizationService membershipSynchronizationService)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            await membershipSynchronizationService.SynchronizeAsync(
                context.User,
                context.RequestAborted);
        }

        await next(context);
    }
}
