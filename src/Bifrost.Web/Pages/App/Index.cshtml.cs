using Bifrost.Web.Features.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bifrost.Web.Pages.App;

public sealed class IndexModel(
    ICurrentBifrostActorAccessor actorAccessor,
    DashboardSnapshotService snapshotService) : PageModel
{
    public CurrentBifrostActor Actor { get; private set; } = CurrentBifrostActor.Anonymous;

    public DashboardSnapshot Snapshot { get; private set; } =
        new(0, 0, 0, 0, 0, 0, 0, 0, 0m, [], [], [], [], [], [], [], [], [], []);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);
        Snapshot = await snapshotService.GetAsync(Actor.UserAccount?.Id, cancellationToken);
    }

    public async Task<PartialViewResult> OnGetSummaryAsync(CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);
        Snapshot = await snapshotService.GetAsync(Actor.UserAccount?.Id, cancellationToken);
        return Partial("_SummaryCards", Snapshot);
    }
}
