using Bifrost.Web.Features.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bifrost.Web.Pages;

public sealed class IndexModel(ICurrentBifrostActorAccessor actorAccessor) : PageModel
{
    public CurrentBifrostActor Actor { get; private set; } = CurrentBifrostActor.Anonymous;

    [BindProperty(SupportsGet = true)]
    public string? Auth { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);
    }
}
