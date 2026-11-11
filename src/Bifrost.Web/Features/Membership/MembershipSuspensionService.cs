using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Shared;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Web.Features.Membership;

public enum SuspensionOutcome
{
    Done,
    NotAdmin,
    NoReason,
    NoSuchAccount,
    NotApplicable,
    ProtectedAccount
}

/// <summary>
/// The one place a membership becomes Suspended or stops being. Only a platform admin may call it, every use leaves an audit
/// receipt naming the admin, the account and the reason, and no sign-in path writes or lifts a suspension (Heimdall and GitHub
/// sign-in leave a Suspended membership as it is). Unsuspending restores Active for an account that was once approved,
/// Authenticated for one that never was; a pending request is not remembered.
/// </summary>
public sealed class MembershipSuspensionService(BifrostDbContext dbContext, AuditTrailService auditTrailService, TimeProvider timeProvider)
{
    public const int MaxReasonLength = 500;

    public Task<SuspensionOutcome> SuspendAsync(CurrentBifrostActor actor, Guid userAccountId, string? reason, CancellationToken cancellationToken) =>
        ChangeAsync(actor, userAccountId, reason, suspend: true, cancellationToken);

    public Task<SuspensionOutcome> UnsuspendAsync(CurrentBifrostActor actor, Guid userAccountId, string? reason, CancellationToken cancellationToken) =>
        ChangeAsync(actor, userAccountId, reason, suspend: false, cancellationToken);

    private async Task<SuspensionOutcome> ChangeAsync(CurrentBifrostActor actor, Guid userAccountId, string? reason, bool suspend, CancellationToken cancellationToken)
    {
        if (!actor.CanManageMembership || actor.UserAccount is null)
        {
            return SuspensionOutcome.NotAdmin;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return SuspensionOutcome.NoReason;
        }

        var user = await dbContext.UserAccounts.Include(x => x.Membership).SingleOrDefaultAsync(x => x.Id == userAccountId, cancellationToken);
        if (user?.Membership is not { } membership)
        {
            return SuspensionOutcome.NoSuchAccount;
        }

        // An admin cannot suspend themselves or another platform admin here: bootstrap admins are re-promoted at sign-in by configuration.
        if (suspend && (user.Id == actor.UserAccount.Id || membership.IsPlatformAdmin))
        {
            return SuspensionOutcome.ProtectedAccount;
        }

        if (suspend == (membership.Status == MembershipStatus.Suspended))
        {
            return SuspensionOutcome.NotApplicable;
        }

        var before = membership.Status;
        membership.Status = suspend
            ? MembershipStatus.Suspended
            : membership.ApprovedAtUtc is null ? MembershipStatus.Authenticated : MembershipStatus.Active;
        var clipped = reason.Trim().Length > MaxReasonLength ? reason.Trim()[..MaxReasonLength] : reason.Trim();
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(
            actor.UserAccount.Id,
            nameof(Membership),
            membership.Id,
            suspend ? "membership.suspended" : "membership.unsuspended",
            $"{(suspend ? "Suspended" : "Unsuspended")} {user.DisplayName} ({before} -> {membership.Status}): {clipped}",
            cancellationToken);
        return SuspensionOutcome.Done;
    }
}
