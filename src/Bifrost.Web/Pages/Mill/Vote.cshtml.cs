using Bifrost.Web.Configuration;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.ConceptVotes;
using Bifrost.Web.Features.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Pages.Mill;

// The anonymous vote page. Counts and the visitor's own stance come from ConceptAppraisalService on every GET; the
// voter key is decided by VoterIdentity.For from the actor and the device id VoterKeyResolver reads.
public sealed class VoteModel(
    ICurrentBifrostActorAccessor actorAccessor,
    ConceptAppraisalService appraisals,
    VoterKeyResolver deviceCookie,
    IOptions<ConceptVoteOptions> options) : PageModel
{
    public ConceptSubjectView Subject { get; private set; } = null!;

    public bool SignedIn { get; private set; }

    public string MillPageLink => options.Value.MillPageUrl + "#" + Uri.EscapeDataString(Subject.Key);

    public async Task<IActionResult> OnGetAsync(string collection, string key, CancellationToken cancellationToken)
    {
        var actor = await actorAccessor.GetAsync(cancellationToken);
        var voter = ResolveVoter(actor, mint: false);
        SignedIn = voter?.UserAccountId is not null;
        return await LoadAsync(collection, key, voter, cancellationToken) ? Page() : NotFound();
    }

    public Task<IActionResult> OnPostUpAsync(string collection, string key, CancellationToken cancellationToken) =>
        CastAsync(collection, key, ConceptStance.Up, cancellationToken);

    public Task<IActionResult> OnPostDownAsync(string collection, string key, CancellationToken cancellationToken) =>
        CastAsync(collection, key, ConceptStance.Down, cancellationToken);

    public async Task<IActionResult> OnPostRetractAsync(string collection, string key, CancellationToken cancellationToken)
    {
        var actor = await actorAccessor.GetAsync(cancellationToken);
        if (await appraisals.GetSubjectAsync(collection, key, null, cancellationToken) is null)
        {
            return NotFound();
        }

        var voter = ResolveVoter(actor, mint: false);
        await appraisals.RetractAsync(collection, key, voter, cancellationToken);
        return RedirectToPage(new { collection, key });
    }

    private async Task<IActionResult> CastAsync(
        string collection,
        string key,
        ConceptStance stance,
        CancellationToken cancellationToken)
    {
        var actor = await actorAccessor.GetAsync(cancellationToken);
        if (await appraisals.GetSubjectAsync(collection, key, null, cancellationToken) is null)
        {
            return NotFound();
        }

        var voter = ResolveVoter(actor, mint: true);
        var result = await appraisals.CastAsync(
            collection, key, voter, stance, HttpContext.Connection.RemoteIpAddress, cancellationToken);
        return result is CastResult.UnknownSubject or CastResult.RetiredSubject
            ? NotFound()
            : RedirectToPage(new { collection, key });
    }

    // Accounts key to the account and never touch the cookie. An anonymous visitor keys to a device cookie that
    // unprotects to a well-formed id; a cast without one mints it, and no other path does.
    private VoterIdentity? ResolveVoter(CurrentBifrostActor actor, bool mint)
    {
        var voter = VoterIdentity.For(actor, null) ?? VoterIdentity.For(actor, deviceCookie.TryRead(HttpContext));
        if (voter is null && mint)
        {
            voter = VoterIdentity.For(actor, deviceCookie.Mint(HttpContext));
        }

        return voter;
    }

    private async Task<bool> LoadAsync(string collection, string key, VoterIdentity? voter, CancellationToken cancellationToken)
    {
        var view = await appraisals.GetSubjectAsync(collection, key, voter, cancellationToken);
        if (view is null)
        {
            return false;
        }

        Subject = view;
        return true;
    }
}
