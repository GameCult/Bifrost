namespace Bifrost.Web.Configuration;

public sealed class GitHubOAuthOptions
{
    public const string SectionName = "GitHubOAuth";

    public string ClientId { get; init; } = string.Empty;

    public string ClientSecret { get; init; } = string.Empty;

    public string CallbackPath { get; init; } = "/signin-github";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret);
}
