using Microsoft.AspNetCore.Authorization;

namespace Bifrost.Web.Features.Membership;

public sealed class ActiveMemberRequirement : IAuthorizationRequirement
{
    public const string PolicyName = "ActiveMember";
}

public static class GitHubAuthenticationDefaults
{
    public const string AuthenticationScheme = "GitHub";
}

public static class BifrostClaimTypes
{
    public const string GitHubLogin = "urn:bifrost:github-login";
    public const string DisplayName = "urn:bifrost:display-name";
    public const string AvatarUrl = "urn:bifrost:avatar-url";
}
