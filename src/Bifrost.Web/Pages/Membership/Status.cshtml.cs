using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using System.Security.Claims;
using Bifrost.Web.Configuration;
using Bifrost.Web.Features.Heimdall;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Features.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Pages.Membership;

[Authorize]
public sealed class StatusModel(
    ICurrentBifrostActorAccessor actorAccessor,
    BifrostDbContext dbContext,
    HeimdallAuthService heimdallAuthService,
    IOptions<BifrostHostOptions> hostOptions,
    ILogger<StatusModel> logger) : PageModel
{
    /// <summary>The wording of the link outcome the page was sent back with: a closed set, null for anything else.</summary>
    public string? LinkMessage { get; private set; }

    /// <summary>Whether this session belongs to a Heimdall account a Google login can be attached to.</summary>
    public bool CanLinkGoogle => User.FindFirstValue(BifrostClaimTypes.HeimdallAccountId) is { Length: > 0 };

    /// <summary>
    /// Begins an explicit Google link: an ordinary Heimdall sign-in for the account already signed in here, kept as
    /// an attempt that is only the proof for the link. POST with antiforgery only; nothing is started without a session.
    /// </summary>
    public async Task<IActionResult> OnPostLinkGoogleAsync(CancellationToken cancellationToken)
    {
        var accountId = User.FindFirstValue(BifrostClaimTypes.HeimdallAccountId);
        if (string.IsNullOrEmpty(accountId))
        {
            return RedirectToPage(null, new { link = HeimdallLinkOutcomes.Code(HeimdallLinkOutcome.Error) });
        }

        try
        {
            var start = await heimdallAuthService.StartLinkAsync(
                accountId,
                User.FindFirstValue(BifrostClaimTypes.AuthProvider),
                hostOptions.Value.PublicBaseUrl,
                Request.Cookies,
                cancellationToken);
            Response.Cookies.Append(start.CookieName, start.CookieValue, HeimdallAttemptCookie.Options());
            return Redirect(start.AuthorizationUrl.ToString());
        }
        catch (HeimdallAuthException error)
        {
            logger.LogWarning("Heimdall link start refused: {Reason}", error.Message);
            return RedirectToPage(null, new { link = HeimdallLinkOutcomes.Code(HeimdallLinkOutcome.Error) });
        }
    }

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
        LinkMessage = HeimdallLinkOutcomes.Wording(Request.Query["link"].ToString());

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
