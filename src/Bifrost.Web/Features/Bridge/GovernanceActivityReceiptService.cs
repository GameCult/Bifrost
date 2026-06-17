using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Shared;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Web.Features.Bridge;

public sealed class GovernanceActivityReceiptService(
    BifrostDbContext dbContext,
    AuditTrailService auditTrailService,
    TimeProvider timeProvider)
{
    public async Task<GovernanceActivityReceiptResult> RecordAsync(
        GovernanceActivityReceiptRequest request,
        Guid? actorUserAccountId,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        await EnsureLinkedDispatchRequestAsync(request, cancellationToken);

        var receipt = new GovernanceActivityReceipt
        {
            TopicId = NormalizeText(request.TopicId),
            CommentId = NormalizeText(request.CommentId),
            DispatchRequestId = NormalizeText(request.DispatchRequestId),
            Title = NormalizeText(request.Title),
            JurisdictionRepoName = NormalizeText(request.JurisdictionRepoName),
            JurisdictionAgentIdentity = NormalizeText(request.JurisdictionAgentIdentity),
            ActivityKind = request.ActivityKind,
            ActorKind = NormalizeText(request.ActorKind),
            ActorName = NormalizeText(request.ActorName),
            Note = NormalizeText(request.Note),
            ActorUserAccountId = actorUserAccountId,
            OccurredAtUtc = timeProvider.GetUtcNow()
        };

        dbContext.GovernanceActivityReceipts.Add(receipt);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            actorUserAccountId,
            nameof(GovernanceActivityReceipt),
            receipt.Id,
            $"governance.{ToAuditSuffix(receipt.ActivityKind)}",
            BuildAuditDetail(receipt),
            cancellationToken);

        return GovernanceActivityReceiptResult.From(receipt);
    }

    private async Task EnsureLinkedDispatchRequestAsync(
        GovernanceActivityReceiptRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.DispatchRequestId))
        {
            return;
        }

        var linkedReceipt = await dbContext.AgentTransportReceipts
            .AsNoTracking()
            .Where(x => x.RequestId == request.DispatchRequestId)
            .OrderByDescending(x => x.OccurredAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (linkedReceipt is null)
        {
            throw new GovernanceActivityReceiptException(
                $"Governance receipt references unknown dispatch request {request.DispatchRequestId}.");
        }

        EnsureSameIdentity(
            "governance jurisdiction repo",
            request.JurisdictionRepoName,
            "dispatch request repo",
            linkedReceipt.TargetRepoName);
        EnsureSameIdentity(
            "governance jurisdiction agent",
            request.JurisdictionAgentIdentity,
            "dispatch request agent",
            linkedReceipt.TargetAgentIdentity);
    }

    private static void ValidateRequest(GovernanceActivityReceiptRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TopicId))
        {
            throw new GovernanceActivityReceiptException("Governance receipts require a topic id.");
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new GovernanceActivityReceiptException("Governance receipts require a title.");
        }

        if (string.IsNullOrWhiteSpace(request.JurisdictionRepoName))
        {
            throw new GovernanceActivityReceiptException("Governance receipts require a jurisdiction repo name.");
        }

        if (string.IsNullOrWhiteSpace(request.ActorKind))
        {
            throw new GovernanceActivityReceiptException("Governance receipts require an actor kind.");
        }

        if (string.IsNullOrWhiteSpace(request.ActorName))
        {
            throw new GovernanceActivityReceiptException("Governance receipts require an actor name.");
        }
    }

    private static void EnsureSameIdentity(
        string currentLabel,
        string currentValue,
        string linkedLabel,
        string linkedValue)
    {
        var normalizedCurrent = NormalizeText(currentValue);
        var normalizedLinked = NormalizeText(linkedValue);
        if (string.IsNullOrWhiteSpace(normalizedCurrent) || string.IsNullOrWhiteSpace(normalizedLinked))
        {
            return;
        }

        if (!string.Equals(normalizedCurrent, normalizedLinked, StringComparison.OrdinalIgnoreCase))
        {
            throw new GovernanceActivityReceiptException($"{currentLabel} does not match the linked {linkedLabel}.");
        }
    }

    private static string ToAuditSuffix(GovernanceActivityReceiptKind activityKind) => activityKind switch
    {
        GovernanceActivityReceiptKind.TopicOpened => "topic-opened",
        GovernanceActivityReceiptKind.TopicCommented => "topic-commented",
        GovernanceActivityReceiptKind.TopicApproved => "topic-approved",
        GovernanceActivityReceiptKind.TopicPromoted => "topic-promoted",
        _ => "activity",
    };

    private static string BuildAuditDetail(GovernanceActivityReceipt receipt)
    {
        var actorName = string.IsNullOrWhiteSpace(receipt.ActorName) ? "unknown actor" : receipt.ActorName;
        var topicId = string.IsNullOrWhiteSpace(receipt.TopicId) ? "unknown topic" : receipt.TopicId;
        return $"{actorName} recorded {receipt.ActivityKind} for governance topic {topicId}.";
    }

    private static string NormalizeText(string? value) => value?.Trim() ?? string.Empty;
}

public sealed record GovernanceActivityReceiptRequest(
    string TopicId,
    string CommentId,
    string DispatchRequestId,
    string Title,
    string JurisdictionRepoName,
    string JurisdictionAgentIdentity,
    GovernanceActivityReceiptKind ActivityKind,
    string ActorKind,
    string ActorName,
    string Note);

public sealed record GovernanceActivityReceiptResult(
    Guid Id,
    string TopicId,
    string CommentId,
    string DispatchRequestId,
    string Title,
    string JurisdictionRepoName,
    string JurisdictionAgentIdentity,
    GovernanceActivityReceiptKind ActivityKind,
    string ActorKind,
    string ActorName,
    string Note,
    DateTimeOffset OccurredAtUtc)
{
    public static GovernanceActivityReceiptResult From(GovernanceActivityReceipt receipt) => new(
        receipt.Id,
        receipt.TopicId,
        receipt.CommentId,
        receipt.DispatchRequestId,
        receipt.Title,
        receipt.JurisdictionRepoName,
        receipt.JurisdictionAgentIdentity,
        receipt.ActivityKind,
        receipt.ActorKind,
        receipt.ActorName,
        receipt.Note,
        receipt.OccurredAtUtc);
}

public sealed class GovernanceActivityReceiptException(string message) : InvalidOperationException(message);
