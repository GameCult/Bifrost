using Bifrost.Web.Configuration;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.ConceptVotes;
using Bifrost.Web.Features.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Pages.Mill;

// The anonymous vote page. Counts and the visitor's own stance come from ConceptAppraisalService on every GET; the
// voter key is decided by VoterIdentity.For from the actor and the device id VoterKeyResolver reads.
[IgnoreAntiforgeryToken]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class VoteModel(
    ICurrentBifrostActorAccessor actorAccessor,
    ConceptAppraisalService appraisals,
    VoterKeyResolver deviceCookie,
    ConceptVoteRateLimits limits,
    IOptions<ConceptVoteOptions> options,
    IOptions<BifrostHostOptions> host) : PageModel
{
    public ConceptSubjectView Subject { get; private set; } = null!;

    public bool SignedIn { get; private set; }

    public string MillPageLink => options.Value.MillPageUrl + "#" + Uri.EscapeDataString(Subject.Key);

    // The single gate before any handler runs. A request that selects no handler (a POST with no or an unknown handler,
    // any other method) is a client error, never a view over a null subject. A cast is accepted only from the site's own
    // origin (CastRequestGuard); no antiforgery cookie or token exists on this flow.
    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var method = context.HttpContext.Request.Method;
        if (context.HandlerMethod is null)
        {
            context.Result = new StatusCodeResult(
                HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsPost(method)
                    ? StatusCodes.Status400BadRequest
                    : StatusCodes.Status405MethodNotAllowed);
            return;
        }

        // The handler the request names must be the handler selected: GET ?handler=Up has no OnGetUp to run.
        var named = context.HttpContext.Request.Query["handler"].ToString();
        if (string.IsNullOrEmpty(named) != string.IsNullOrEmpty(context.HandlerMethod.Name))
        {
            context.Result = new StatusCodeResult(StatusCodes.Status400BadRequest);
            return;
        }

        if (HttpMethods.IsPost(method) && !CastRequestGuard.Allows(context.HttpContext.Request, host.Value))
        {
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
            return;
        }

        await next();
    }

    public async Task<IActionResult> OnGetAsync(string collection, string key, CancellationToken cancellationToken)
    {
        var actor = await actorAccessor.GetAsync(cancellationToken);
        var voter = ResolveVoter(actor);
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
        var voter = ResolveVoter(actor);
        if (Throttled(limits.DenyCast(HttpContext.Connection.RemoteIpAddress, voter), CastRefusal) is { } throttled)
        {
            return throttled;
        }

        if (await appraisals.GetSubjectAsync(collection, key, null, cancellationToken) is null)
        {
            return NotFound();
        }

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
        var client = HttpContext.Connection.RemoteIpAddress;
        var voter = ResolveVoter(actor);
        if (Throttled(limits.DenyCast(client, voter), CastRefusal) is { } throttled)
        {
            return throttled;
        }

        if (await appraisals.GetSubjectAsync(collection, key, null, cancellationToken) is null)
        {
            return NotFound();
        }

        if (voter is null)
        {
            if (Throttled(limits.DenyMint(client), MintRefusal) is { } mintThrottled)
            {
                return mintThrottled;
            }

            voter = VoterIdentity.For(actor, deviceCookie.Mint(HttpContext));
        }

        var result = await appraisals.CastAsync(
            collection, key, voter, stance, HttpContext.Connection.RemoteIpAddress, cancellationToken);
        return result is CastResult.UnknownSubject or CastResult.RetiredSubject
            ? NotFound()
            : RedirectToPage(new { collection, key });
    }

    // Accounts key to the account and never touch the cookie. An anonymous visitor keys to a device cookie that
    // unprotects to a well-formed id; a cast without one mints it (CastAsync, after the mint budget), and no other path does.
    private VoterIdentity? ResolveVoter(CurrentBifrostActor actor) =>
        VoterIdentity.For(actor, null) ?? VoterIdentity.For(actor, deviceCookie.TryRead(HttpContext));

    // A denied budget is a 429 that says when to retry, in words a voter can read; it writes no row, sets no cookie and
    // repeats nothing of the request. A refused new device is told it can sign in, which has a budget of its own.
    private const string CastRefusal = "You are voting too fast. Please wait a little before voting again.";

    private const string MintRefusal =
        "Too many new voters have voted from your network recently. You can sign in to vote now, or try again later.";

    private IActionResult? Throttled(TimeSpan? retryAfter, string refusal)
    {
        if (retryAfter is not { } wait)
        {
            return null;
        }

        Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new ContentResult
        {
            StatusCode = StatusCodes.Status429TooManyRequests,
            ContentType = "text/plain; charset=utf-8",
            Content = refusal
        };
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
