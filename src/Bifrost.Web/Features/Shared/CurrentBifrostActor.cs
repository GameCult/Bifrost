using Bifrost.Web.Domain;

namespace Bifrost.Web.Features.Shared;

public sealed record CurrentBifrostActor(bool IsAuthenticated, UserAccount? UserAccount)
{
    public static readonly CurrentBifrostActor Anonymous = new(false, null);

    public Bifrost.Web.Domain.Membership? Membership => UserAccount?.Membership;

    public string DisplayName =>
        UserAccount?.DisplayName
        ?? UserAccount?.GitHubLogin
        ?? "Guest";

    public bool IsActiveMember => Membership?.Status == MembershipStatus.Active;

    public bool CanManageMembership => Membership?.IsPlatformAdmin == true;

    public bool CanManageProjects =>
        Membership?.IsPlatformAdmin == true ||
        Membership?.CanManageProjects == true;

    public bool CanManageLedger =>
        Membership?.IsPlatformAdmin == true ||
        Membership?.CanManageLedger == true;

    public bool CanModerateMotions =>
        Membership?.IsPlatformAdmin == true ||
        Membership?.CanModerateMotions == true;

    public decimal EffectiveVotingWeight => Membership?.EffectiveVotingWeight ?? 0m;
}
