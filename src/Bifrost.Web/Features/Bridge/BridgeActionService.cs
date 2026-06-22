using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Features.Shared;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Web.Features.Bridge;

public sealed class BridgeActionService(
    BifrostDbContext dbContext,
    AuditTrailService auditTrailService,
    TimeProvider timeProvider)
{
    private const string AgentTransportRequestSourceKind = "bifrost_agent_transport_request";
    private const string GovernanceTopicSourceKind = "bifrost_governance_topic";
    private const string LegacyGovernanceTopicSourceKind = "governance_topic";

    public async Task<BridgeActionResult> RequestAsync(
        BridgeActionRequest request,
        BridgeCaller caller,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var normalizedRequest = request.Normalize(caller);
        var policy = await EvaluatePolicyAsync(normalizedRequest, caller, cancellationToken);

        var action = new BridgeAction
        {
            ActorKind = normalizedRequest.ActorKind,
            ActorUserAccountId = caller.UserAccountId,
            ActorName = normalizedRequest.ActorName,
            TargetSurface = normalizedRequest.TargetSurface,
            ActionKind = normalizedRequest.ActionKind,
            Status = policy.Allowed ? BridgeActionStatus.Authorized : BridgeActionStatus.Denied,
            WorkItemId = normalizedRequest.WorkItemId,
            MotionId = normalizedRequest.MotionId,
            TargetRepositoryFullName = NormalizeRepository(normalizedRequest.TargetRepositoryFullName),
            TargetLocator = NormalizeText(normalizedRequest.TargetLocator),
            SourceKind = NormalizeText(normalizedRequest.SourceKind),
            SourceId = NormalizeText(normalizedRequest.SourceId),
            AuthorityReference = NormalizeText(normalizedRequest.AuthorityReference),
            BifrostIdentity = NormalizeText(normalizedRequest.BifrostIdentity),
            HeimdallCapabilityReference = NormalizeText(normalizedRequest.HeimdallCapabilityReference),
            EpiphanyRunId = NormalizeText(normalizedRequest.EpiphanyRunId),
            EpiphanyLaneId = NormalizeText(normalizedRequest.EpiphanyLaneId),
            EpiphanyAgentIdentity = NormalizeText(normalizedRequest.EpiphanyAgentIdentity),
            PolicyDecision = policy.Decision,
            Title = NormalizeText(normalizedRequest.Title),
            Summary = NormalizeText(normalizedRequest.Summary),
            RequestedAtUtc = now,
            UpdatedAtUtc = now
        };

        dbContext.BridgeActions.Add(action);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            caller.UserAccountId,
            nameof(BridgeAction),
            action.Id,
            policy.Allowed ? "bridge.authorized" : "bridge.denied",
            policy.Decision,
            cancellationToken);

        return BridgeActionResult.From(action);
    }

    public Task<BridgeActionResult?> StartAsync(
        Guid actionId,
        BridgeCaller caller,
        CancellationToken cancellationToken) =>
        UpdateAsync(
            actionId,
            caller,
            "bridge.started",
            action =>
            {
                if (action.Status is not BridgeActionStatus.Authorized)
                {
                    throw new BridgeActionException("Only authorized bridge actions can be started.");
                }

                var now = timeProvider.GetUtcNow();
                action.Status = BridgeActionStatus.InProgress;
                action.StartedAtUtc = now;
                action.UpdatedAtUtc = now;
                return "Bridge action started.";
            },
            cancellationToken);

    public Task<BridgeActionResult?> CompleteAsync(
        Guid actionId,
        BridgeActionReceiptRequest request,
        BridgeCaller caller,
        CancellationToken cancellationToken) =>
        UpdateAsync(
            actionId,
            caller,
            "bridge.completed",
            action =>
            {
                if (action.Status is not (BridgeActionStatus.Authorized or BridgeActionStatus.InProgress))
                {
                    throw new BridgeActionException("Only authorized or in-progress bridge actions can be completed.");
                }

                if (string.IsNullOrWhiteSpace(request.ReceiptUrl) &&
                    string.IsNullOrWhiteSpace(request.ExternalReceiptId) &&
                    string.IsNullOrWhiteSpace(request.ReceiptPayload))
                {
                    throw new BridgeActionException("A completed bridge action must include a receipt URL, external receipt id, or receipt payload.");
                }

                var now = timeProvider.GetUtcNow();
                action.Status = BridgeActionStatus.Completed;
                action.ReceiptUrl = NormalizeText(request.ReceiptUrl);
                action.ExternalReceiptId = NormalizeText(request.ExternalReceiptId);
                action.ReceiptPayload = NormalizeText(request.ReceiptPayload);
                action.CompletedAtUtc = now;
                action.UpdatedAtUtc = now;
                return "Bridge action completed with receipt.";
            },
            cancellationToken);

    public Task<BridgeActionResult?> FailAsync(
        Guid actionId,
        BridgeActionFailureRequest request,
        BridgeCaller caller,
        CancellationToken cancellationToken) =>
        UpdateAsync(
            actionId,
            caller,
            "bridge.failed",
            action =>
            {
                var now = timeProvider.GetUtcNow();
                action.Status = BridgeActionStatus.Failed;
                action.FailureReason = NormalizeText(request.FailureReason);
                action.UpdatedAtUtc = now;
                action.CompletedAtUtc = now;
                return string.IsNullOrWhiteSpace(action.FailureReason)
                    ? "Bridge action failed."
                    : action.FailureReason;
            },
            cancellationToken);

    private async Task<BridgeActionResult?> UpdateAsync(
        Guid actionId,
        BridgeCaller caller,
        string auditAction,
        Func<BridgeAction, string> update,
        CancellationToken cancellationToken)
    {
        var action = await dbContext.BridgeActions
            .SingleOrDefaultAsync(x => x.Id == actionId, cancellationToken);

        if (action is null)
        {
            return null;
        }

        if (!caller.CanOperate(action))
        {
            throw new UnauthorizedAccessException("Caller cannot operate this bridge action.");
        }

        var detail = update(action);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            caller.UserAccountId,
            nameof(BridgeAction),
            action.Id,
            auditAction,
            detail,
            cancellationToken);

        return BridgeActionResult.From(action);
    }

    private async Task<BridgePolicyDecision> EvaluatePolicyAsync(
        BridgeActionRequest request,
        BridgeCaller caller,
        CancellationToken cancellationToken)
    {
        if (!caller.IsAllowedTransport)
        {
            return BridgePolicyDecision.Reject("Bridge actions require an active member session or a configured local bridge token.");
        }

        if (request.TargetSurface == BridgeTargetSurface.GitHub &&
            string.IsNullOrWhiteSpace(request.TargetRepositoryFullName))
        {
            return BridgePolicyDecision.Reject("GitHub bridge actions must name a target repository.");
        }

        if (request.ActorKind is BridgeActorKind.Agent or BridgeActorKind.Persona or BridgeActorKind.Service)
        {
            var hasProvenance =
                !string.IsNullOrWhiteSpace(request.AuthorityReference) ||
                (!string.IsNullOrWhiteSpace(request.SourceKind) && !string.IsNullOrWhiteSpace(request.SourceId)) ||
                request.WorkItemId is not null ||
                request.MotionId is not null;

            if (!hasProvenance)
            {
                return BridgePolicyDecision.Reject("Agent and Persona bridge actions must cite an authority reference, source object, work item, or motion.");
            }
        }

        if (RequiresExternalAccountCapability(request))
        {
            if (string.IsNullOrWhiteSpace(request.BifrostIdentity))
            {
                return BridgePolicyDecision.Reject("Persona and agent Discord/Reddit bridge actions must carry a Bifrost identity.");
            }

            var identityDecision = await EvaluateRegisteredBifrostIdentityAsync(request.BifrostIdentity, cancellationToken);
            if (identityDecision is not null)
            {
                return identityDecision;
            }

            if (string.IsNullOrWhiteSpace(request.HeimdallCapabilityReference))
            {
                return BridgePolicyDecision.Reject("Persona and agent Discord/Reddit bridge actions must carry a Heimdall-backed capability or account reference.");
            }
        }

        if (request.WorkItemId is not null)
        {
            var workItem = await dbContext.WorkItems
                .Include(x => x.Project)
                .SingleOrDefaultAsync(x => x.Id == request.WorkItemId, cancellationToken);

            if (workItem is null)
            {
                return BridgePolicyDecision.Reject("Referenced work item does not exist.");
            }

            var targetRepository = NormalizeRepository(request.TargetRepositoryFullName);
            var projectRepository = NormalizeRepository(workItem.Project.GitHubRepository);
            if (!string.IsNullOrWhiteSpace(targetRepository) &&
                !string.IsNullOrWhiteSpace(projectRepository) &&
                targetRepository != projectRepository)
            {
                return BridgePolicyDecision.Reject("GitHub target repository does not match the referenced work item's project.");
            }
        }

        if (request.MotionId is not null &&
            !await dbContext.Motions.AnyAsync(x => x.Id == request.MotionId, cancellationToken))
        {
            return BridgePolicyDecision.Reject("Referenced motion does not exist.");
        }

        var provenanceDecision = await EvaluateBifrostOwnedProvenanceAsync(request, cancellationToken);
        if (provenanceDecision is not null)
        {
            return provenanceDecision;
        }

        return BridgePolicyDecision.Permit(
            caller.IsLocalBridge
                ? "Authorized through the configured local Bifrost bridge token and Bifrost policy."
                : "Authorized for an active Bifrost member through Bifrost policy.");
    }

    private async Task<BridgePolicyDecision?> EvaluateRegisteredBifrostIdentityAsync(
        string bifrostIdentity,
        CancellationToken cancellationToken)
    {
        string normalizedIdentity;
        try
        {
            normalizedIdentity = BifrostIdentityService.NormalizeIdentity(bifrostIdentity);
        }
        catch (BifrostIdentityException exception)
        {
            return BridgePolicyDecision.Reject(exception.Message);
        }

        var exists = await dbContext.UserAccounts.AnyAsync(
            x => x.NormalizedBifrostIdentity == normalizedIdentity,
            cancellationToken);
        if (!exists)
        {
            return BridgePolicyDecision.Reject("Persona and agent Discord/Reddit bridge actions must carry a registered Bifrost identity.");
        }

        return null;
    }

    private async Task<BridgePolicyDecision?> EvaluateBifrostOwnedProvenanceAsync(
        BridgeActionRequest request,
        CancellationToken cancellationToken)
    {
        var sourceKind = NormalizeText(request.SourceKind);
        if (string.IsNullOrWhiteSpace(sourceKind))
        {
            return null;
        }

        if (string.Equals(sourceKind, AgentTransportRequestSourceKind, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(request.SourceId))
            {
                return BridgePolicyDecision.Reject("Bifrost request-backed bridge actions must cite the request id as sourceId.");
            }

            var linkedReceipt = await dbContext.AgentTransportReceipts
                .AsNoTracking()
                .Where(x => x.RequestId == request.SourceId)
                .OrderByDescending(x => x.OccurredAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if (linkedReceipt is null)
            {
                return BridgePolicyDecision.Reject($"Bifrost request-backed bridge action references unknown request {request.SourceId}.");
            }

            var repoMismatch = LinkedRepositoryMismatch(
                request.TargetRepositoryFullName,
                linkedReceipt.TargetRepositoryFullName,
                linkedReceipt.TargetRepoName);
            if (repoMismatch is not null)
            {
                return BridgePolicyDecision.Reject(repoMismatch);
            }

            return null;
        }

        if (string.Equals(sourceKind, GovernanceTopicSourceKind, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sourceKind, LegacyGovernanceTopicSourceKind, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(request.SourceId))
            {
                return BridgePolicyDecision.Reject("Bifrost governance-backed bridge actions must cite the topic id as sourceId.");
            }

            var linkedReceipt = await dbContext.GovernanceActivityReceipts
                .AsNoTracking()
                .Where(x => x.TopicId == request.SourceId)
                .OrderByDescending(x => x.OccurredAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if (linkedReceipt is null)
            {
                return BridgePolicyDecision.Reject($"Bifrost governance-backed bridge action references unknown topic {request.SourceId}.");
            }

            var repoMismatch = LinkedRepositoryMismatch(
                request.TargetRepositoryFullName,
                string.Empty,
                linkedReceipt.JurisdictionRepoName);
            if (repoMismatch is not null)
            {
                return BridgePolicyDecision.Reject(repoMismatch);
            }

            if (request.TargetSurface == BridgeTargetSurface.GitHub &&
                !await dbContext.GovernanceActivityReceipts
                    .AsNoTracking()
                    .AnyAsync(
                        x => x.TopicId == request.SourceId &&
                             (x.ActivityKind == GovernanceActivityReceiptKind.TopicApproved ||
                              x.ActivityKind == GovernanceActivityReceiptKind.TopicPromoted),
                        cancellationToken))
            {
                return BridgePolicyDecision.Reject(
                    $"GitHub bridge action references governance topic {request.SourceId}, but that topic has not been approved or promoted.");
            }
        }

        return null;
    }

    private static string? LinkedRepositoryMismatch(
        string targetRepositoryFullName,
        string linkedTargetRepositoryFullName,
        string linkedRepoName)
    {
        var normalizedTargetRepository = NormalizeRepository(targetRepositoryFullName);
        var normalizedLinkedRepository = NormalizeRepository(linkedTargetRepositoryFullName);
        if (!string.IsNullOrWhiteSpace(normalizedTargetRepository) &&
            !string.IsNullOrWhiteSpace(normalizedLinkedRepository) &&
            !string.Equals(normalizedTargetRepository, normalizedLinkedRepository, StringComparison.OrdinalIgnoreCase))
        {
            return "GitHub target repository does not match the linked Bifrost provenance.";
        }

        var targetRepoShortName = ShortRepositoryName(normalizedTargetRepository);
        var normalizedLinkedRepoName = NormalizeText(linkedRepoName);
        if (!string.IsNullOrWhiteSpace(targetRepoShortName) &&
            !string.IsNullOrWhiteSpace(normalizedLinkedRepoName) &&
            !string.Equals(targetRepoShortName, normalizedLinkedRepoName, StringComparison.OrdinalIgnoreCase))
        {
            return "GitHub target repository does not match the linked Bifrost provenance.";
        }

        return null;
    }

    private static bool RequiresExternalAccountCapability(BridgeActionRequest request) =>
        request.ActorKind is BridgeActorKind.Agent or BridgeActorKind.Persona &&
        request.TargetSurface is BridgeTargetSurface.Discord or BridgeTargetSurface.Reddit;

    private static string NormalizeRepository(string value) => NormalizeText(value).ToLowerInvariant();

    private static string ShortRepositoryName(string normalizedRepositoryFullName)
    {
        if (string.IsNullOrWhiteSpace(normalizedRepositoryFullName))
        {
            return string.Empty;
        }

        var slash = normalizedRepositoryFullName.LastIndexOf('/');
        return slash >= 0 && slash < normalizedRepositoryFullName.Length - 1
            ? normalizedRepositoryFullName[(slash + 1)..]
            : normalizedRepositoryFullName;
    }

    private static string NormalizeText(string? value) => value?.Trim() ?? string.Empty;
}

public sealed record BridgeCaller(
    bool IsActiveMember,
    bool IsLocalBridge,
    Guid? UserAccountId,
    string DisplayName)
{
    public bool IsAllowedTransport => IsActiveMember || IsLocalBridge;

    public bool CanOperate(BridgeAction action) =>
        IsLocalBridge ||
        (IsActiveMember && action.ActorUserAccountId == UserAccountId);
}

public sealed record BridgeActionRequest(
    BridgeActorKind ActorKind,
    string ActorName,
    BridgeTargetSurface TargetSurface,
    BridgeActionKind ActionKind,
    string TargetRepositoryFullName,
    string TargetLocator,
    string SourceKind,
    string SourceId,
    string AuthorityReference,
    string BifrostIdentity,
    string HeimdallCapabilityReference,
    string EpiphanyRunId,
    string EpiphanyLaneId,
    string EpiphanyAgentIdentity,
    string Title,
    string Summary,
    Guid? WorkItemId = null,
    Guid? MotionId = null)
{
    public BridgeActionRequest Normalize(BridgeCaller caller) =>
        this with
        {
            ActorKind = ActorKind == default && caller.IsActiveMember ? BridgeActorKind.Member : ActorKind,
            ActorName = string.IsNullOrWhiteSpace(ActorName) && caller.IsActiveMember
                ? caller.DisplayName
                : NormalizeText(ActorName),
            TargetRepositoryFullName = NormalizeText(TargetRepositoryFullName),
            TargetLocator = NormalizeText(TargetLocator),
            SourceKind = NormalizeText(SourceKind),
            SourceId = NormalizeText(SourceId),
            AuthorityReference = NormalizeText(AuthorityReference),
            BifrostIdentity = NormalizeText(BifrostIdentity),
            HeimdallCapabilityReference = NormalizeText(HeimdallCapabilityReference),
            EpiphanyRunId = NormalizeText(EpiphanyRunId),
            EpiphanyLaneId = NormalizeText(EpiphanyLaneId),
            EpiphanyAgentIdentity = NormalizeText(EpiphanyAgentIdentity),
            Title = NormalizeText(Title),
            Summary = NormalizeText(Summary)
        };

    private static string NormalizeText(string? value) => value?.Trim() ?? string.Empty;
}

public sealed record BridgeActionReceiptRequest(
    string ReceiptUrl,
    string ExternalReceiptId,
    string ReceiptPayload);

public sealed record BridgeActionFailureRequest(string FailureReason);

public sealed record BridgeActionResult(
    Guid Id,
    BridgeActorKind ActorKind,
    string ActorName,
    BridgeTargetSurface TargetSurface,
    BridgeActionKind ActionKind,
    BridgeActionStatus Status,
    string TargetRepositoryFullName,
    string TargetLocator,
    string SourceKind,
    string SourceId,
    string AuthorityReference,
    string BifrostIdentity,
    string HeimdallCapabilityReference,
    string EpiphanyRunId,
    string EpiphanyLaneId,
    string EpiphanyAgentIdentity,
    string PolicyDecision,
    string Title,
    string ReceiptUrl,
    string ExternalReceiptId,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public static BridgeActionResult From(BridgeAction action) => new(
        action.Id,
        action.ActorKind,
        action.ActorName,
        action.TargetSurface,
        action.ActionKind,
        action.Status,
        action.TargetRepositoryFullName,
        action.TargetLocator,
        action.SourceKind,
        action.SourceId,
        action.AuthorityReference,
        action.BifrostIdentity,
        action.HeimdallCapabilityReference,
        action.EpiphanyRunId,
        action.EpiphanyLaneId,
        action.EpiphanyAgentIdentity,
        action.PolicyDecision,
        action.Title,
        action.ReceiptUrl,
        action.ExternalReceiptId,
        action.RequestedAtUtc,
        action.UpdatedAtUtc);
}

public sealed record BridgePolicyDecision(bool Allowed, string Decision)
{
    public static BridgePolicyDecision Permit(string decision) => new(true, decision);

    public static BridgePolicyDecision Reject(string decision) => new(false, decision);
}

public sealed class BridgeActionException(string message) : InvalidOperationException(message);
