using Bifrost.Web.Domain;

namespace Bifrost.Web.Features.Shared;

public sealed record CurrentBifrostActor(bool IsAuthenticated, UserAccount? UserAccount)
{
    public static readonly CurrentBifrostActor Anonymous = new(false, null);

    public Bifrost.Web.Domain.Membership? Membership => UserAccount?.Membership;

    public string DisplayName =>
        UserAccount?.MemberProfile?.Nickname
        ?? UserAccount?.DisplayName
        ?? UserAccount?.GitHubLogin
        ?? "Guest";

    public bool IsActiveMember => Membership?.Status == MembershipStatus.Active;

    public IReadOnlyCollection<MemberRole> Roles =>
        Membership?.RoleAssignments
            .Select(x => x.Role)
            .Distinct()
            .ToArray()
        ?? [];

    public bool IsPlatformAdmin =>
        Membership?.IsPlatformAdmin == true ||
        Roles.Contains(MemberRole.PlatformAdmin);

    public bool CanManageMembership => IsPlatformAdmin;

    public bool CanManageProjects =>
        IsPlatformAdmin ||
        Membership?.CanManageProjects == true ||
        Roles.Contains(MemberRole.Producer);

    public bool CanAssignWork =>
        CanManageProjects ||
        Roles.Contains(MemberRole.Maintainer);

    public bool CanReviewWork =>
        IsPlatformAdmin ||
        Roles.Contains(MemberRole.Producer) ||
        Roles.Contains(MemberRole.Maintainer);

    public bool CanManageLedger =>
        IsPlatformAdmin ||
        Membership?.CanManageLedger == true ||
        Roles.Contains(MemberRole.LedgerReviewer);

    public bool CanModerateMotions =>
        IsPlatformAdmin ||
        Membership?.CanModerateMotions == true ||
        Roles.Contains(MemberRole.Producer) ||
        Roles.Contains(MemberRole.Maintainer);

    public decimal EffectiveVotingWeight => Membership?.EffectiveVotingWeight ?? 0m;
}
