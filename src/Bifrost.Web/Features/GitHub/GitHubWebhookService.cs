using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bifrost.Web.Configuration;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Features.GitHub;

public sealed class GitHubWebhookService(
    BifrostDbContext dbContext,
    IOptions<GitHubAppOptions> gitHubAppOptions,
    TimeProvider timeProvider,
    ILogger<GitHubWebhookService> logger)
{
    private static readonly Regex IssueReferenceRegex = new("#(?<number>\\d+)", RegexOptions.Compiled);

    public async Task<GitHubWebhookResult> ProcessAsync(
        string eventName,
        string deliveryId,
        string signature,
        string payload,
        CancellationToken cancellationToken)
    {
        var options = gitHubAppOptions.Value;
        if (!options.EnableWebhookSync)
        {
            return new GitHubWebhookResult(GitHubWebhookProcessStatus.Ignored, "Webhook sync is disabled.");
        }

        if (!options.IsWebhookConfigured)
        {
            return new GitHubWebhookResult(GitHubWebhookProcessStatus.NotConfigured, "Webhook secret is not configured.");
        }

        if (!IsValidSignature(options.WebhookSecret, payload, signature))
        {
            logger.LogWarning("Rejected GitHub webhook delivery {DeliveryId} because the signature did not match.", deliveryId);
            return new GitHubWebhookResult(GitHubWebhookProcessStatus.InvalidSignature, "Invalid signature.");
        }

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        var result = eventName switch
        {
            "issues" => await ProcessIssueAsync(root, cancellationToken),
            "pull_request" => await ProcessPullRequestAsync(root, cancellationToken),
            "pull_request_review" => await ProcessPullRequestReviewAsync(root, cancellationToken),
            _ => new GitHubWebhookResult(GitHubWebhookProcessStatus.Ignored, $"Ignoring unsupported event '{eventName}'.")
        };

        return result;
    }

    private async Task<GitHubWebhookResult> ProcessIssueAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var action = ReadString(root, "action");
        if (action is not ("opened" or "edited" or "reopened" or "closed"))
        {
            return new GitHubWebhookResult(GitHubWebhookProcessStatus.Ignored, $"Ignoring issue action '{action}'.");
        }

        var issue = root.GetProperty("issue");
        if (issue.TryGetProperty("pull_request", out _))
        {
            return new GitHubWebhookResult(GitHubWebhookProcessStatus.Ignored, "Ignoring pull-request issue shadow payload.");
        }

        var repositoryFullName = NormalizeRepositoryName(ReadString(root.GetProperty("repository"), "full_name"));
        var issueNumber = issue.GetProperty("number").GetInt32();
        var issueState = ParseGitHubEntityState(ReadString(issue, "state"), false);
        var issueUrl = ReadString(issue, "html_url");
        var title = ReadString(issue, "title");
        var body = ReadString(issue, "body");
        var senderLogin = ReadString(root.GetProperty("sender"), "login");
        var now = timeProvider.GetUtcNow();
        var owner = await ResolveFallbackOwnerAsync(senderLogin, cancellationToken);

        if (owner is null)
        {
            return new GitHubWebhookResult(GitHubWebhookProcessStatus.Ignored, "No local member exists yet to own the synced project.");
        }

        var project = await ResolveProjectAsync(repositoryFullName, owner, now, cancellationToken);
        var issueLink = await dbContext.GitHubIssueLinks
            .Include(x => x.WorkItem)
            .SingleOrDefaultAsync(
                x => x.RepositoryFullName == repositoryFullName && x.IssueNumber == issueNumber,
                cancellationToken);

        var workItem = issueLink?.WorkItem;
        if (workItem is null)
        {
            workItem = new WorkItem
            {
                ProjectId = project.Id,
                RequestedByUserAccountId = owner.Id,
                SourceType = WorkItemSourceType.GitHubIssue,
                ExternalSourceId = $"{repositoryFullName}#{issueNumber}",
                Title = title,
                Summary = body,
                Category = "GitHub issue",
                SkillLevel = WorkItemSkillLevel.Specialized,
                Status = issueState == GitHubEntityState.Closed ? WorkItemStatus.Completed : WorkItemStatus.Open,
                ReviewStatus = WorkReviewStatus.Pending,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            dbContext.WorkItems.Add(workItem);
            issueLink = new GitHubIssueLink
            {
                WorkItem = workItem,
                RepositoryFullName = repositoryFullName,
                IssueNumber = issueNumber,
                State = issueState,
                IssueUrl = issueUrl,
                TitleSnapshot = title,
                LastSynchronizedAtUtc = now
            };

            dbContext.GitHubIssueLinks.Add(issueLink);
        }
        else
        {
            workItem.ProjectId = project.Id;
            workItem.SourceType = WorkItemSourceType.GitHubIssue;
            workItem.ExternalSourceId = $"{repositoryFullName}#{issueNumber}";
            workItem.Title = title;
            workItem.Summary = body;
            workItem.UpdatedAtUtc = now;
            workItem.Status = issueState == GitHubEntityState.Closed
                ? WorkItemStatus.Completed
                : workItem.Status == WorkItemStatus.Archived
                    ? WorkItemStatus.Archived
                    : workItem.Status == WorkItemStatus.Completed
                        ? WorkItemStatus.Open
                        : workItem.Status;

            issueLink!.State = issueState;
            issueLink.IssueUrl = issueUrl;
            issueLink.TitleSnapshot = title;
            issueLink.LastSynchronizedAtUtc = now;
        }

        workItem.CompletedAtUtc = issueState == GitHubEntityState.Closed ? now : null;
        dbContext.AuditEvents.Add(CreateAuditEvent(
            owner.Id,
            nameof(WorkItem),
            workItem.Id,
            "github.issue.synced",
            $"Synced issue {repositoryFullName}#{issueNumber} into work item '{workItem.Title}'."));

        await dbContext.SaveChangesAsync(cancellationToken);
        return new GitHubWebhookResult(GitHubWebhookProcessStatus.Processed, $"Processed issue {repositoryFullName}#{issueNumber}.");
    }

    private async Task<GitHubWebhookResult> ProcessPullRequestAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var action = ReadString(root, "action");
        if (action is not ("opened" or "edited" or "reopened" or "synchronize" or "closed" or "ready_for_review"))
        {
            return new GitHubWebhookResult(GitHubWebhookProcessStatus.Ignored, $"Ignoring pull request action '{action}'.");
        }

        var repositoryFullName = NormalizeRepositoryName(ReadString(root.GetProperty("repository"), "full_name"));
        var pullRequest = root.GetProperty("pull_request");
        var pullRequestNumber = pullRequest.GetProperty("number").GetInt32();
        var pullRequestState = ParseGitHubEntityState(ReadString(pullRequest, "state"), pullRequest.GetProperty("merged").GetBoolean());
        var pullRequestUrl = ReadString(pullRequest, "html_url");
        var title = ReadString(pullRequest, "title");
        var body = ReadString(pullRequest, "body");
        var now = timeProvider.GetUtcNow();

        var pullRequestLink = await dbContext.GitHubPullRequestLinks
            .Include(x => x.WorkItem)
            .SingleOrDefaultAsync(
                x => x.RepositoryFullName == repositoryFullName && x.PullRequestNumber == pullRequestNumber,
                cancellationToken);

        var workItem = pullRequestLink?.WorkItem
            ?? await ResolveWorkItemFromReferencesAsync(repositoryFullName, body, title, cancellationToken);

        if (workItem is null)
        {
            return new GitHubWebhookResult(GitHubWebhookProcessStatus.Ignored, $"No work item mapping found for pull request {repositoryFullName}#{pullRequestNumber}.");
        }

        if (pullRequestLink is null)
        {
            pullRequestLink = new GitHubPullRequestLink
            {
                WorkItemId = workItem.Id,
                RepositoryFullName = repositoryFullName,
                PullRequestNumber = pullRequestNumber,
                LastSynchronizedAtUtc = now
            };

            dbContext.GitHubPullRequestLinks.Add(pullRequestLink);
        }

        pullRequestLink.State = pullRequestState;
        pullRequestLink.IsMerged = pullRequestState == GitHubEntityState.Merged;
        pullRequestLink.PullRequestUrl = pullRequestUrl;
        pullRequestLink.TitleSnapshot = title;
        pullRequestLink.LastSynchronizedAtUtc = now;

        workItem.UpdatedAtUtc = now;
        if (pullRequestState == GitHubEntityState.Merged)
        {
            workItem.Status = WorkItemStatus.Completed;
            workItem.ReviewStatus = WorkReviewStatus.Approved;
            workItem.CompletedAtUtc = now;
        }
        else if (pullRequestState == GitHubEntityState.Closed)
        {
            workItem.Status = WorkItemStatus.InProgress;
        }
        else
        {
            workItem.Status = WorkItemStatus.SubmittedForReview;
            workItem.ReviewStatus = WorkReviewStatus.Pending;
            workItem.SubmittedAtUtc ??= now;
        }

        dbContext.AuditEvents.Add(CreateAuditEvent(
            workItem.AssignedToUserAccountId ?? workItem.RequestedByUserAccountId,
            nameof(WorkItem),
            workItem.Id,
            "github.pull-request.synced",
            $"Synced pull request {repositoryFullName}#{pullRequestNumber} for work item '{workItem.Title}'."));

        await dbContext.SaveChangesAsync(cancellationToken);
        return new GitHubWebhookResult(GitHubWebhookProcessStatus.Processed, $"Processed pull request {repositoryFullName}#{pullRequestNumber}.");
    }

    private async Task<GitHubWebhookResult> ProcessPullRequestReviewAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var action = ReadString(root, "action");
        if (action is not ("submitted" or "edited" or "dismissed"))
        {
            return new GitHubWebhookResult(GitHubWebhookProcessStatus.Ignored, $"Ignoring pull request review action '{action}'.");
        }

        var repositoryFullName = NormalizeRepositoryName(ReadString(root.GetProperty("repository"), "full_name"));
        var pullRequest = root.GetProperty("pull_request");
        var review = root.GetProperty("review");
        var pullRequestNumber = pullRequest.GetProperty("number").GetInt32();
        var reviewState = ParseReviewDecision(ReadString(review, "state"));
        var reviewerLogin = ReadString(root.GetProperty("sender"), "login");
        var reviewBody = ReadString(review, "body");
        var now = timeProvider.GetUtcNow();

        var pullRequestLink = await dbContext.GitHubPullRequestLinks
            .Include(x => x.WorkItem)
            .SingleOrDefaultAsync(
                x => x.RepositoryFullName == repositoryFullName && x.PullRequestNumber == pullRequestNumber,
                cancellationToken);

        if (pullRequestLink?.WorkItem is null)
        {
            return new GitHubWebhookResult(GitHubWebhookProcessStatus.Ignored, $"No work item mapping found for review on {repositoryFullName}#{pullRequestNumber}.");
        }

        var reviewer = await dbContext.UserAccounts
            .SingleOrDefaultAsync(
                x => x.NormalizedGitHubLogin == reviewerLogin.Trim().ToUpperInvariant(),
                cancellationToken);

        pullRequestLink.ReviewDecision = reviewState;
        pullRequestLink.LastSynchronizedAtUtc = now;

        pullRequestLink.WorkItem.UpdatedAtUtc = now;
        pullRequestLink.WorkItem.ReviewStatus = reviewState switch
        {
            GitHubReviewDecision.Approved => WorkReviewStatus.Approved,
            GitHubReviewDecision.ChangesRequested => WorkReviewStatus.ChangesRequested,
            _ => WorkReviewStatus.Pending
        };

        pullRequestLink.WorkItem.Status = reviewState switch
        {
            GitHubReviewDecision.Approved => WorkItemStatus.Approved,
            GitHubReviewDecision.ChangesRequested => WorkItemStatus.ChangesRequested,
            _ => WorkItemStatus.SubmittedForReview
        };

        dbContext.WorkReviews.Add(new WorkReview
        {
            WorkItemId = pullRequestLink.WorkItemId,
            ReviewerUserAccountId = reviewer?.Id,
            ReviewerName = reviewer?.DisplayName ?? reviewerLogin,
            Status = pullRequestLink.WorkItem.ReviewStatus,
            Note = reviewBody,
            ReviewedAtUtc = now
        });

        dbContext.AuditEvents.Add(CreateAuditEvent(
            reviewer?.Id,
            nameof(WorkItem),
            pullRequestLink.WorkItemId,
            "github.pull-request.reviewed",
            $"Applied GitHub review state {reviewState} to work item '{pullRequestLink.WorkItem.Title}'."));

        await dbContext.SaveChangesAsync(cancellationToken);
        return new GitHubWebhookResult(GitHubWebhookProcessStatus.Processed, $"Processed review for {repositoryFullName}#{pullRequestNumber}.");
    }

    private async Task<Project> ResolveProjectAsync(
        string repositoryFullName,
        UserAccount owner,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var project = await dbContext.Projects
            .SingleOrDefaultAsync(x => x.GitHubRepository == repositoryFullName, cancellationToken);

        if (project is not null)
        {
            return project;
        }

        var repositoryName = repositoryFullName.Split('/').LastOrDefault() ?? repositoryFullName;
        var slugBase = SlugGenerator.Create(repositoryName);
        var slug = slugBase;
        var suffix = 2;
        while (await dbContext.Projects.AnyAsync(x => x.Slug == slug, cancellationToken))
        {
            slug = $"{slugBase}-{suffix++}";
        }

        project = new Project
        {
            OwnerUserAccountId = owner.Id,
            Slug = slug,
            Name = repositoryName,
            Summary = $"GitHub-synced project container for {repositoryFullName}.",
            GitHubRepository = repositoryFullName,
            Status = ProjectStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        dbContext.Projects.Add(project);
        return project;
    }

    private async Task<WorkItem?> ResolveWorkItemFromReferencesAsync(
        string repositoryFullName,
        string pullRequestBody,
        string pullRequestTitle,
        CancellationToken cancellationToken)
    {
        var referencedIssueNumbers = IssueReferenceRegex.Matches($"{pullRequestTitle} {pullRequestBody}")
            .Select(match => int.TryParse(match.Groups["number"].Value, out var issueNumber) ? issueNumber : 0)
            .Where(issueNumber => issueNumber > 0)
            .Distinct()
            .ToArray();

        if (referencedIssueNumbers.Length == 0)
        {
            return null;
        }

        return await dbContext.GitHubIssueLinks
            .Include(x => x.WorkItem)
            .Where(x => x.RepositoryFullName == repositoryFullName && referencedIssueNumbers.Contains(x.IssueNumber))
            .Select(x => x.WorkItem)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<UserAccount?> ResolveFallbackOwnerAsync(string senderLogin, CancellationToken cancellationToken)
    {
        var normalizedLogin = senderLogin.Trim().ToUpperInvariant();
        var matchedUser = await dbContext.UserAccounts
            .SingleOrDefaultAsync(x => x.NormalizedGitHubLogin == normalizedLogin, cancellationToken);

        if (matchedUser is not null)
        {
            return matchedUser;
        }

        return await dbContext.UserAccounts
            .Include(x => x.Membership)
            .OrderByDescending(x => x.Membership!.IsPlatformAdmin)
            .ThenBy(x => x.DisplayName)
            .FirstOrDefaultAsync(
                x => x.Membership != null && x.Membership.Status == MembershipStatus.Active,
                cancellationToken);
    }

    private static bool IsValidSignature(string secret, string payload, string signature)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        using var hasher = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hasher.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var expected = $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signature));
    }

    private static GitHubEntityState ParseGitHubEntityState(string state, bool merged) =>
        merged
            ? GitHubEntityState.Merged
            : string.Equals(state, "closed", StringComparison.OrdinalIgnoreCase)
                ? GitHubEntityState.Closed
                : GitHubEntityState.Open;

    private static GitHubReviewDecision ParseReviewDecision(string state) =>
        state.ToLowerInvariant() switch
        {
            "approved" => GitHubReviewDecision.Approved,
            "changes_requested" => GitHubReviewDecision.ChangesRequested,
            "commented" => GitHubReviewDecision.Commented,
            _ => GitHubReviewDecision.None
        };

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static string NormalizeRepositoryName(string repositoryFullName) =>
        repositoryFullName.Trim().Trim('/').ToLowerInvariant();

    private AuditEvent CreateAuditEvent(
        Guid? actorUserAccountId,
        string entityType,
        Guid entityId,
        string action,
        string detail) =>
        new()
        {
            ActorUserAccountId = actorUserAccountId,
            EntityType = entityType,
            EntityId = entityId.ToString(),
            Action = action,
            Detail = detail,
            OccurredAtUtc = timeProvider.GetUtcNow()
        };
}

public sealed record GitHubWebhookResult(GitHubWebhookProcessStatus Status, string Message);

public enum GitHubWebhookProcessStatus
{
    Processed,
    Ignored,
    InvalidSignature,
    NotConfigured
}
