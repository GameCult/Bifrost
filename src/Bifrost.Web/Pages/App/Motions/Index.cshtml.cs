using System.ComponentModel.DataAnnotations;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Motions;
using Bifrost.Web.Features.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Web.Pages.App.Motions;

public sealed class IndexModel(
    BifrostDbContext dbContext,
    ICurrentBifrostActorAccessor actorAccessor,
    AuditTrailService auditTrailService,
    TimeProvider timeProvider) : PageModel
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
        var actorUser = Actor.UserAccount;

        if (actorUser is null)
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync(cancellationToken);
            return Page();
        }

        var now = timeProvider.GetUtcNow();
        var motion = new Motion
        {
            ProjectId = Input.Scope == MotionScope.Project ? Input.ProjectId : null,
            CreatedByUserAccountId = actorUser.Id,
            Scope = Input.Scope,
            Category = Input.Category,
            Title = Input.Title.Trim(),
            Summary = Input.Summary.Trim(),
            ApprovalThreshold = MotionCategoryPolicy.GetThreshold(Input.Category),
            OpensAtUtc = now,
            ClosesAtUtc = Input.ClosesAtUtc.ToUniversalTime(),
            CreatedAtUtc = now
        };

        dbContext.Motions.Add(motion);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            actorUser.Id,
            nameof(Motion),
            motion.Id,
            "motion.created",
            $"Opened motion {motion.Title}.",
            cancellationToken);

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostVoteAsync(Guid motionId, VoteChoice choice, string comment, CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);
        var actorUser = Actor.UserAccount;

        if (actorUser is null)
        {
            return Forbid();
        }

        var motion = await dbContext.Motions
            .Include(x => x.Votes)
            .SingleAsync(x => x.Id == motionId, cancellationToken);

        if (motion.Status != MotionStatus.Open)
        {
            return RedirectToPage();
        }

        var vote = motion.Votes.SingleOrDefault(x => x.UserAccountId == actorUser.Id);
        if (vote is null)
        {
            dbContext.Votes.Add(new Vote
            {
                MotionId = motionId,
                UserAccountId = actorUser.Id,
                Choice = choice,
                Weight = Actor.EffectiveVotingWeight,
                Comment = comment?.Trim() ?? string.Empty,
                CastAtUtc = timeProvider.GetUtcNow()
            });
        }
        else
        {
            vote.Choice = choice;
            vote.Weight = Actor.EffectiveVotingWeight;
            vote.Comment = comment?.Trim() ?? string.Empty;
            vote.CastAtUtc = timeProvider.GetUtcNow();
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            actorUser.Id,
            nameof(Motion),
            motion.Id,
            "motion.voted",
            $"{Actor.DisplayName} voted {choice} on {motion.Title}.",
            cancellationToken);

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCloseAsync(Guid motionId, CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);
        var actorUser = Actor.UserAccount;
        if (actorUser is null)
        {
            return Forbid();
        }

        var motion = await dbContext.Motions
            .Include(x => x.Votes)
            .SingleAsync(x => x.Id == motionId, cancellationToken);

        if (!Actor.CanModerateMotions && motion.CreatedByUserAccountId != actorUser.Id)
        {
            return Forbid();
        }

        var votesFor = motion.Votes.Where(v => v.Choice == VoteChoice.For).Sum(v => v.Weight);
        var votesAgainst = motion.Votes.Where(v => v.Choice == VoteChoice.Against).Sum(v => v.Weight);
        var decisiveVotes = votesFor + votesAgainst;

        motion.Status = decisiveVotes > 0 && votesFor / decisiveVotes >= motion.ApprovalThreshold
            ? MotionStatus.Passed
            : decisiveVotes > 0
                ? MotionStatus.Failed
                : MotionStatus.Closed;
        motion.ResolvedAtUtc = timeProvider.GetUtcNow();
        motion.ResolutionNote = decisiveVotes > 0
            ? $"Closed with {votesFor:0.##} for and {votesAgainst:0.##} against at a {motion.ApprovalThreshold:P0} threshold."
            : "Closed without decisive votes.";

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            actorUser.Id,
            nameof(Motion),
            motion.Id,
            "motion.closed",
            $"Closed motion {motion.Title} with status {motion.Status}.",
            cancellationToken);

        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Actor = await actorAccessor.GetAsync(cancellationToken);

        ProjectOptions = await dbContext.Projects
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x => new SelectListItem(x.Name, x.Id.ToString()))
            .ToListAsync(cancellationToken);

        var motions = await dbContext.Motions
            .AsNoTracking()
            .Include(x => x.Project)
            .Include(x => x.Votes)
            .OrderBy(x => x.ClosesAtUtc)
            .ToListAsync(cancellationToken);

        Motions = motions.Select(x => new MotionListItem(
            x.Id,
            x.Title,
            x.Summary,
            x.Project?.Name ?? "Management-wide",
            x.Scope.ToString(),
            MotionCategoryPolicy.GetLabel(x.Category),
            x.ApprovalThreshold,
            ResolveStatusLabel(x, timeProvider.GetUtcNow()),
            ResolveStatusTone(x, timeProvider.GetUtcNow()),
            x.Votes.Where(v => v.Choice == VoteChoice.For).Sum(v => v.Weight),
            x.Votes.Where(v => v.Choice == VoteChoice.Against).Sum(v => v.Weight),
            x.Votes.Where(v => v.Choice == VoteChoice.Abstain).Sum(v => v.Weight),
            x.Votes.Where(v => v.UserAccountId == Actor.UserAccount?.Id).Select(v => (VoteChoice?)v.Choice).FirstOrDefault(),
            x.ClosesAtUtc,
            x.ResolutionNote)).ToList();
    }

    private static string ResolveStatusLabel(Motion motion, DateTimeOffset now)
    {
        if (motion.Status != MotionStatus.Open)
        {
            return motion.Status.ToString();
        }

        if (motion.ClosesAtUtc >= now)
        {
            return "Open";
        }

        var votesFor = motion.Votes.Where(v => v.Choice == VoteChoice.For).Sum(v => v.Weight);
        var votesAgainst = motion.Votes.Where(v => v.Choice == VoteChoice.Against).Sum(v => v.Weight);
        var decisiveVotes = votesFor + votesAgainst;
        if (decisiveVotes <= 0)
        {
            return "Expired";
        }

        return votesFor / decisiveVotes >= motion.ApprovalThreshold ? "Passed" : "Failed";
    }

    private static string ResolveStatusTone(Motion motion, DateTimeOffset now) =>
        ResolveStatusLabel(motion, now) switch
        {
            "Passed" => "success",
            "Failed" => "danger",
            "Expired" => "warning",
            _ => string.Empty
        };

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

    public sealed record MotionListItem(
        Guid Id,
        string Title,
        string Summary,
        string ProjectName,
        string Scope,
        string Category,
        decimal Threshold,
        string StatusLabel,
        string StatusTone,
        decimal VotesFor,
        decimal VotesAgainst,
        decimal VotesAbstain,
        VoteChoice? CurrentUserVote,
        DateTimeOffset ClosesAtUtc,
        string ResolutionNote);
}
