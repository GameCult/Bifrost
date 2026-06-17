using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Shared;

namespace Bifrost.Web.Features.Bridge;

public sealed class AgentTransportReceiptService(
    BifrostDbContext dbContext,
    AuditTrailService auditTrailService,
    TimeProvider timeProvider)
{
    public async Task<AgentTransportReceiptResult> RecordAsync(
        AgentTransportReceiptRequest request,
        Guid? actorUserAccountId,
        CancellationToken cancellationToken)
    {
        var receipt = new AgentTransportReceipt
        {
            RequestId = NormalizeText(request.RequestId),
            Title = NormalizeText(request.Title),
            TargetRepoName = NormalizeText(request.TargetRepoName),
            TargetRepositoryFullName = NormalizeText(request.TargetRepositoryFullName),
            TargetAgentIdentity = NormalizeText(request.TargetAgentIdentity),
            ActivityKind = request.ActivityKind,
            Status = NormalizeText(request.Status),
            ActorUserAccountId = actorUserAccountId,
            ActorName = NormalizeText(request.ActorName),
            Note = NormalizeText(request.Note),
            OccurredAtUtc = timeProvider.GetUtcNow()
        };

        dbContext.AgentTransportReceipts.Add(receipt);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            actorUserAccountId,
            nameof(AgentTransportReceipt),
            receipt.Id,
            $"agent-transport.{receipt.ActivityKind.ToString().ToLowerInvariant()}",
            BuildAuditDetail(receipt),
            cancellationToken);

        return AgentTransportReceiptResult.From(receipt);
    }

    private static string BuildAuditDetail(AgentTransportReceipt receipt)
    {
        var actorName = string.IsNullOrWhiteSpace(receipt.ActorName) ? "unknown actor" : receipt.ActorName;
        var repoName = string.IsNullOrWhiteSpace(receipt.TargetRepoName) ? "unknown repo" : receipt.TargetRepoName;
        return $"{actorName} recorded {receipt.ActivityKind} for request {receipt.RequestId} in {repoName} as {receipt.Status}.";
    }

    private static string NormalizeText(string? value) => value?.Trim() ?? string.Empty;
}

public sealed record AgentTransportReceiptRequest(
    string RequestId,
    string Title,
    string TargetRepoName,
    string TargetRepositoryFullName,
    string TargetAgentIdentity,
    AgentTransportReceiptKind ActivityKind,
    string Status,
    string ActorName,
    string Note);

public sealed record AgentTransportReceiptResult(
    Guid Id,
    string RequestId,
    string Title,
    string TargetRepoName,
    string TargetRepositoryFullName,
    string TargetAgentIdentity,
    AgentTransportReceiptKind ActivityKind,
    string Status,
    string ActorName,
    string Note,
    DateTimeOffset OccurredAtUtc)
{
    public static AgentTransportReceiptResult From(AgentTransportReceipt receipt) => new(
        receipt.Id,
        receipt.RequestId,
        receipt.Title,
        receipt.TargetRepoName,
        receipt.TargetRepositoryFullName,
        receipt.TargetAgentIdentity,
        receipt.ActivityKind,
        receipt.Status,
        receipt.ActorName,
        receipt.Note,
        receipt.OccurredAtUtc);
}
