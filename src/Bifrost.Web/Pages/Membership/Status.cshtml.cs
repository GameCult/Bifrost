using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Web.Pages.Membership;

[Authorize]
public sealed class StatusModel(
    ICurrentBifrostActorAccessor actorAccessor,
    BifrostDbContext dbContext) : PageModel
{
    public CurrentBifrostActor Actor { get; private set; } = CurrentBifrostActor.Anonymous;

    public MembershipInvitation? Invitation { get; private set; }

    public string StatusLabel => Actor.Membership?.Status switch
    {
        MembershipStatus.Active => "Active member",
        MembershipStatus.PendingApproval => "Pending approval",
        MembershipStatus.Suspended => "Suspended",
        MembershipStatus.Authenticated => "Authenticated only",
        _ => "Unknown"
    };

    public string StatusTone => Actor.Membership?.Status switch
    {
        MembershipStatus.Active => "success",
        MembershipStatus.PendingApproval => "warning",
        MembershipStatus.Suspended => "danger",
        _ => string.Empty
    };

    public string StatusMessage => Actor.Membership?.Status switch
    {
        MembershipStatus.Active => "You can participate in projects, claim work, vote on motions, and view the ledger console.",
        MembershipStatus.PendingApproval => "Your GitHub sign-in matched an invite, but an admin still needs to approve active participation.",
        MembershipStatus.Suspended => "Your account is currently suspended from active participation pending an admin review.",
        MembershipStatus.Authenticated => "You are signed in, but there is no active invite or approval on your account yet.",
        _ => "No membership record was found."
    };

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);

        if (Actor.UserAccount is null)
        {
            return;
        }

        Invitation = await dbContext.MembershipInvitations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.NormalizedGitHubLogin == Actor.UserAccount.NormalizedGitHubLogin &&
                     x.RevokedAtUtc == null,
                cancellationToken);
    }
}
