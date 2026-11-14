using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Features.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Web.ViewComponents;

/// <summary>
/// The layout's merge notice for a signed-in actor: the survivor of a Committed or UndoRequested merge inside its grace (days left), a
/// frozen account (a merge is in progress) or a folded one (where to sign in). It reads the merge row once per page view and keeps no
/// state of its own; the words are <see cref="MergeWording"/>'s, and no account name or id is ever in them.
/// </summary>
public sealed class MergeNotice(ICurrentBifrostActorAccessor actors, BifrostDbContext dbContext, TimeProvider timeProvider) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var actor = await actors.GetAsync(HttpContext.RequestAborted);
        var text = await TextAsync(actor, HttpContext.RequestAborted);
        return text is null ? Content(string.Empty) : View("Default", text);
    }

    private async Task<string?> TextAsync(CurrentBifrostActor actor, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (actor.Merge is { } standing)
        {
            if (standing is MergeStanding.Frozen)
            {
                return MergeWording.FrozenNotice();
            }

            var mergedOn = await dbContext.AccountMerges.AsNoTracking()
                .Where(x => x.Id == standing.MergeId)
                .Select(x => x.CommittedUtc ?? x.CreatedUtc)
                .FirstOrDefaultAsync(cancellationToken);
            return MergeWording.FoldedNotice(mergedOn);
        }

        if (actor.UserAccount is not { } account)
        {
            return null;
        }

        var graceEnds = await dbContext.AccountMerges.AsNoTracking()
            .Where(x => x.SurvivorUserAccountId == account.Id
                && (x.State == AccountMergeState.Committed || x.State == AccountMergeState.UndoRequested)
                && x.GraceEndsUtc > now)
            .OrderBy(x => x.GraceEndsUtc)
            .Select(x => x.GraceEndsUtc)
            .FirstOrDefaultAsync(cancellationToken);
        return graceEnds is { } end ? MergeWording.SurvivorNotice(MergeWording.DaysUntil(end, now)) : null;
    }
}
