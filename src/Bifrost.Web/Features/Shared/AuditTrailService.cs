using Bifrost.Web.Data;
using Bifrost.Web.Domain;

namespace Bifrost.Web.Features.Shared;

public sealed class AuditTrailService(BifrostDbContext dbContext, TimeProvider timeProvider)
{
    public Task RecordAsync(
        Guid? actorUserAccountId,
        string entityType,
        Guid entityId,
        string action,
        string detail,
        CancellationToken cancellationToken)
    {
        dbContext.AuditEvents.Add(new AuditEvent
        {
            ActorUserAccountId = actorUserAccountId,
            EntityType = entityType,
            EntityId = entityId.ToString(),
            Action = action,
            Detail = detail,
            OccurredAtUtc = timeProvider.GetUtcNow()
        });

        return dbContext.SaveChangesAsync(cancellationToken);
    }
}
