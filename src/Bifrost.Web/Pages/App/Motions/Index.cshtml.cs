using System.ComponentModel.DataAnnotations;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Motions;
using Bifrost.Web.Features.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Bifrost.Web.Pages.App.Motions;

public sealed class IndexModel(
    ICurrentBifrostActorAccessor actorAccessor,
    MotionGovernanceService motionGovernanceService) : PageModel
{
    [BindProperty]
    public CreateMotionInput Input { get; set; } = new()
    {
        ClosesAtUtc = DateTimeOffset.UtcNow.AddDays(7)
    };

    public CurrentBifrostActor Actor { get; private set; } = CurrentBifrostActor.Anonymous;

    public IReadOnlyList<SelectListItem> ProjectOptions { get; private set; } = [];

    public IReadOnlyList<MotionListItem> Motions { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);

        if (!ModelState.IsValid)
        {
            await LoadAsync(cancellationToken);
            return Page();
        }

        await motionGovernanceService.CreateMotionAsync(
            Actor,
            new CreateMotionCommand(
                Input.Scope,
                Input.ProjectId,
                Input.Category,
                Input.Title,
                Input.Summary,
                Input.ClosesAtUtc),
            cancellationToken);

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostVoteAsync(Guid motionId, VoteChoice choice, string comment, CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);
        await motionGovernanceService.CastVoteAsync(Actor, motionId, choice, comment, cancellationToken);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCloseAsync(Guid motionId, CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);
        await motionGovernanceService.CloseMotionAsync(Actor, motionId, cancellationToken);
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);
        var state = await motionGovernanceService.GetStateAsync(Actor, cancellationToken);
        ProjectOptions = state.Projects
            .Select(x => new SelectListItem(x.Name, x.Id.ToString()))
            .ToList();
        Motions = state.Motions;
    }

    public sealed class CreateMotionInput
    {
        [Display(Name = "Scope")]
        public MotionScope Scope { get; set; } = MotionScope.Project;

        [Display(Name = "Project")]
        public Guid? ProjectId { get; set; }

        [Display(Name = "Category")]
        public MotionCategory Category { get; set; } = MotionCategory.Features;

        [Required, StringLength(180)]
        public string Title { get; set; } = string.Empty;

        [Required, StringLength(3000)]
        public string Summary { get; set; } = string.Empty;

        [Display(Name = "Close at (UTC)")]
        public DateTimeOffset ClosesAtUtc { get; set; }
    }

}
