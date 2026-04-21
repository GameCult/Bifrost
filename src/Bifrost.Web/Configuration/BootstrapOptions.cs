namespace Bifrost.Web.Configuration;

public sealed class BootstrapOptions
{
    public const string SectionName = "Bootstrap";

    public string[] AdminGitHubLogins { get; init; } = [];
}
