using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Shared;
using Microsoft.EntityFrameworkCore;

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
        ValidateRequest(request);
        await EnsureConsistentIdentityAsync(request, cancellationToken);

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

    private async Task EnsureConsistentIdentityAsync(
        AgentTransportReceiptRequest request,
        CancellationToken cancellationToken)
    {
        var priorReceipt = await dbContext.AgentTransportReceipts
            .AsNoTracking()
            .Where(x => x.RequestId == request.RequestId)
            .OrderByDescending(x => x.OccurredAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (priorReceipt is not null)
        {
            EnsureSameIdentity(
                "request-lane target repo",
                request.TargetRepoName,
                "prior request-lane target repo",
                priorReceipt.TargetRepoName);
            EnsureSameIdentity(
                "request-lane target repository",
                request.TargetRepositoryFullName,
                "prior request-lane target repository",
                priorReceipt.TargetRepositoryFullName);
            EnsureSameIdentity(
                "request-lane target agent",
                request.TargetAgentIdentity,
                "prior request-lane target agent",
                priorReceipt.TargetAgentIdentity);
        }

        var linkedRun = await dbContext.AgentDispatchRuns
            .AsNoTracking()
            .Where(x => x.RequestId == request.RequestId)
            .OrderByDescending(x => x.StartedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (linkedRun is not null)
        {
            EnsureSameIdentity(
                "request-lane target repo",
                request.TargetRepoName,
                "dispatch-run target repo",
                linkedRun.TargetRepoName);
            EnsureSameIdentity(
                "request-lane target repository",
                request.TargetRepositoryFullName,
                "dispatch-run target repository",
                linkedRun.TargetRepositoryFullName);
            EnsureSameIdentity(
                "request-lane target agent",
                request.TargetAgentIdentity,
                "dispatch-run target agent",
                linkedRun.TargetAgentIdentity);
        }
    }

    private static void ValidateRequest(AgentTransportReceiptRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            throw new AgentTransportReceiptException("Request-lane receipts require a request id.");
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new AgentTransportReceiptException("Request-lane receipts require a title.");
        }

        if (string.IsNullOrWhiteSpace(request.TargetRepoName))
        {
            throw new AgentTransportReceiptException("Request-lane receipts require a target repo name.");
        }

        if (string.IsNullOrWhiteSpace(request.ActorName))
        {
            throw new AgentTransportReceiptException("Request-lane receipts require an actor name.");
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
            throw new AgentTransportReceiptException($"{currentLabel} does not match the linked {linkedLabel}.");
        }
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

public sealed class AgentTransportReceiptException(string message) : InvalidOperationException(message);
