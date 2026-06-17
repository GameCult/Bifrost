using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Shared;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Web.Features.Bridge;

public sealed class AgentDispatchRunService(
    BifrostDbContext dbContext,
    AuditTrailService auditTrailService,
    TimeProvider timeProvider)
{
    public async Task<AgentDispatchRunResult> StartAsync(
        AgentDispatchRunStartRequest request,
        Guid? startedByUserAccountId,
        CancellationToken cancellationToken)
    {
        ValidateStartRequest(request);
        await EnsureLinkedTransportRequestAsync(request, cancellationToken);

        var now = timeProvider.GetUtcNow();
        var run = new AgentDispatchRun
        {
            RequestId = NormalizeText(request.RequestId),
            TargetRepoName = NormalizeText(request.TargetRepoName),
            TargetRepositoryFullName = NormalizeText(request.TargetRepositoryFullName),
            TargetAgentIdentity = NormalizeText(request.TargetAgentIdentity),
            LaunchMode = NormalizeText(request.LaunchMode),
            Status = AgentDispatchRunStatus.Started,
            StartedByUserAccountId = startedByUserAccountId,
            WorkerProcessId = request.WorkerProcessId,
            ThreadId = NormalizeText(request.ThreadId),
            TurnId = NormalizeText(request.TurnId),
            LogPath = NormalizeText(request.LogPath),
            ResultPath = NormalizeText(request.ResultPath),
            Note = NormalizeText(request.Note),
            StartedAtUtc = now,
            UpdatedAtUtc = now
        };

        dbContext.AgentDispatchRuns.Add(run);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            startedByUserAccountId,
            nameof(AgentDispatchRun),
            run.Id,
            "agent-dispatch.started",
            $"Dispatch run started for request {run.RequestId}.",
            cancellationToken);

        return AgentDispatchRunResult.From(run);
    }

    public Task<AgentDispatchRunResult?> CompleteAsync(
        Guid runId,
        AgentDispatchRunCompletionRequest request,
        Guid? startedByUserAccountId,
        CancellationToken cancellationToken) =>
        UpdateAsync(
            runId,
            startedByUserAccountId,
            "agent-dispatch.completed",
            run =>
            {
                var now = timeProvider.GetUtcNow();
                run.Status = request.Status;
                run.Note = NormalizeText(request.Note);
                run.Error = string.Empty;
                run.ThreadId = string.IsNullOrWhiteSpace(request.ThreadId) ? run.ThreadId : NormalizeText(request.ThreadId);
                run.TurnId = string.IsNullOrWhiteSpace(request.TurnId) ? run.TurnId : NormalizeText(request.TurnId);
                run.ResultPath = string.IsNullOrWhiteSpace(request.ResultPath) ? run.ResultPath : NormalizeText(request.ResultPath);
                run.UpdatedAtUtc = now;
                run.CompletedAtUtc = now;
                return $"Dispatch run {run.RequestId} completed as {run.Status}.";
            },
            cancellationToken);

    public Task<AgentDispatchRunResult?> FailAsync(
        Guid runId,
        AgentDispatchRunFailureRequest request,
        Guid? startedByUserAccountId,
        CancellationToken cancellationToken) =>
        UpdateAsync(
            runId,
            startedByUserAccountId,
            "agent-dispatch.failed",
            run =>
            {
                var now = timeProvider.GetUtcNow();
                run.Status = AgentDispatchRunStatus.Failed;
                run.Note = NormalizeText(request.Note);
                run.Error = NormalizeText(request.Error);
                run.ThreadId = string.IsNullOrWhiteSpace(request.ThreadId) ? run.ThreadId : NormalizeText(request.ThreadId);
                run.TurnId = string.IsNullOrWhiteSpace(request.TurnId) ? run.TurnId : NormalizeText(request.TurnId);
                run.ResultPath = string.IsNullOrWhiteSpace(request.ResultPath) ? run.ResultPath : NormalizeText(request.ResultPath);
                run.UpdatedAtUtc = now;
                run.CompletedAtUtc = now;
                return string.IsNullOrWhiteSpace(run.Error)
                    ? $"Dispatch run {run.RequestId} failed."
                    : run.Error;
            },
            cancellationToken);

    private async Task<AgentDispatchRunResult?> UpdateAsync(
        Guid runId,
        Guid? startedByUserAccountId,
        string auditAction,
        Func<AgentDispatchRun, string> update,
        CancellationToken cancellationToken)
    {
        var run = await dbContext.AgentDispatchRuns
            .SingleOrDefaultAsync(x => x.Id == runId, cancellationToken);

        if (run is null)
        {
            return null;
        }

        var detail = update(run);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            startedByUserAccountId,
            nameof(AgentDispatchRun),
            run.Id,
            auditAction,
            detail,
            cancellationToken);

        return AgentDispatchRunResult.From(run);
    }

    private async Task EnsureLinkedTransportRequestAsync(
        AgentDispatchRunStartRequest request,
        CancellationToken cancellationToken)
    {
        var linkedReceipt = await dbContext.AgentTransportReceipts
            .AsNoTracking()
            .Where(x => x.RequestId == request.RequestId)
            .OrderByDescending(x => x.OccurredAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (linkedReceipt is null)
        {
            throw new AgentDispatchRunException(
                $"Dispatch runs require an existing request-lane receipt for request {request.RequestId}.");
        }

        EnsureSameIdentity(
            "dispatch run target repo",
            request.TargetRepoName,
            "request-lane target repo",
            linkedReceipt.TargetRepoName);
        EnsureSameIdentity(
            "dispatch run target repository",
            request.TargetRepositoryFullName,
            "request-lane target repository",
            linkedReceipt.TargetRepositoryFullName);
        EnsureSameIdentity(
            "dispatch run target agent",
            request.TargetAgentIdentity,
            "request-lane target agent",
            linkedReceipt.TargetAgentIdentity);
    }

    private static void ValidateStartRequest(AgentDispatchRunStartRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            throw new AgentDispatchRunException("Dispatch runs require a request id.");
        }

        if (string.IsNullOrWhiteSpace(request.TargetRepoName))
        {
            throw new AgentDispatchRunException("Dispatch runs require a target repo name.");
        }

        if (string.IsNullOrWhiteSpace(request.LaunchMode))
        {
            throw new AgentDispatchRunException("Dispatch runs require a launch mode.");
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
            throw new AgentDispatchRunException($"{currentLabel} does not match the linked {linkedLabel}.");
        }
    }

    private static string NormalizeText(string? value) => value?.Trim() ?? string.Empty;
}

public sealed record AgentDispatchRunStartRequest(
    string RequestId,
    string TargetRepoName,
    string TargetRepositoryFullName,
    string TargetAgentIdentity,
    string LaunchMode,
    int? WorkerProcessId,
    string ThreadId,
    string TurnId,
    string LogPath,
    string ResultPath,
    string Note);

public sealed record AgentDispatchRunCompletionRequest(
    AgentDispatchRunStatus Status,
    string ThreadId,
    string TurnId,
    string ResultPath,
    string Note);

public sealed record AgentDispatchRunFailureRequest(
    string ThreadId,
    string TurnId,
    string ResultPath,
    string Note,
    string Error);

public sealed record AgentDispatchRunResult(
    Guid Id,
    string RequestId,
    string TargetRepoName,
    string TargetRepositoryFullName,
    string TargetAgentIdentity,
    string LaunchMode,
    AgentDispatchRunStatus Status,
    int? WorkerProcessId,
    string ThreadId,
    string TurnId,
    string LogPath,
    string ResultPath,
    string Note,
    string Error,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc)
{
    public static AgentDispatchRunResult From(AgentDispatchRun run) => new(
        run.Id,
        run.RequestId,
        run.TargetRepoName,
        run.TargetRepositoryFullName,
        run.TargetAgentIdentity,
        run.LaunchMode,
        run.Status,
        run.WorkerProcessId,
        run.ThreadId,
        run.TurnId,
        run.LogPath,
        run.ResultPath,
        run.Note,
        run.Error,
        run.StartedAtUtc,
        run.UpdatedAtUtc,
        run.CompletedAtUtc);
}

public sealed class AgentDispatchRunException(string message) : InvalidOperationException(message);
