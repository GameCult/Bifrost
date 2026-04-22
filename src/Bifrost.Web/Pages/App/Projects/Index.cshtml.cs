using System.ComponentModel.DataAnnotations;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Web.Pages.App.Projects;

public sealed class IndexModel(
    BifrostDbContext dbContext,
    ICurrentBifrostActorAccessor actorAccessor,
    AuditTrailService auditTrailService,
    TimeProvider timeProvider) : PageModel
{
    [BindProperty]
    public CreateProjectInput Input { get; set; } = new();

    public CurrentBifrostActor Actor { get; private set; } = CurrentBifrostActor.Anonymous;

    public IReadOnlyList<ProjectListItem> Projects { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);
        if (!Actor.CanManageProjects || Actor.UserAccount is null)
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync(cancellationToken);
            return Page();
        }

        var slug = SlugGenerator.Create(string.IsNullOrWhiteSpace(Input.Slug) ? Input.Name : Input.Slug);
        var exists = await dbContext.Projects.AnyAsync(x => x.Slug == slug, cancellationToken);
        if (exists)
        {
            ModelState.AddModelError(nameof(Input.Slug), "That project slug is already in use.");
            await LoadAsync(cancellationToken);
            return Page();
        }

        var repository = NormalizeRepository(Input.GitHubRepository);
        var now = timeProvider.GetUtcNow();
        var project = new Project
        {
            OwnerUserAccountId = Actor.UserAccount.Id,
            Slug = slug,
            Name = Input.Name.Trim(),
            Summary = Input.Summary.Trim(),
            GitHubRepository = repository,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            Actor.UserAccount.Id,
            nameof(Project),
            project.Id,
            "project.created",
            $"Created project {project.Name}.",
            cancellationToken);

        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);

        Projects = await dbContext.Projects
            .AsNoTracking()
            .Include(x => x.OwnerUserAccount)
            .OrderBy(x => x.Name)
            .Select(x => new ProjectListItem(
                x.Id,
                x.Name,
                x.Slug,
                x.Summary,
                x.Status.ToString(),
                x.OwnerUserAccount.DisplayName,
                x.GitHubRepository,
                x.WorkItems.Count(workItem => workItem.Status != WorkItemStatus.Completed && workItem.Status != WorkItemStatus.Archived),
                x.UpdatedAtUtc))
            .ToListAsync(cancellationToken);
    }

    private static string NormalizeRepository(string input)
    {
        var trimmed = input.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        const string prefix = "https://github.com/";
        if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[prefix.Length..];
        }

        return trimmed.Trim('/').ToLowerInvariant();
    }

    public sealed class CreateProjectInput
    {
        [Required, StringLength(180)]
        public string Name { get; set; } = string.Empty;

        [StringLength(120)]
        public string Slug { get; set; } = string.Empty;

        [StringLength(240)]
        [Display(Name = "GitHub repository")]
        public string GitHubRepository { get; set; } = string.Empty;

        [Required, StringLength(2000)]
        public string Summary { get; set; } = string.Empty;
    }

    public sealed record ProjectListItem(
        Guid Id,
        string Name,
        string Slug,
        string Summary,
        string Status,
        string OwnerName,
        string GitHubRepository,
        int OpenWorkItems,
        DateTimeOffset UpdatedAtUtc);
}
