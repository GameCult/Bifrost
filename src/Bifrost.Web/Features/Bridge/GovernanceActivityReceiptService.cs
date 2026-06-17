using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Shared;

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
