namespace Bifrost.Web.Configuration;

public sealed class GitHubAppOptions
{
    public const string SectionName = "GitHubApp";

    public string AppId { get; init; } = string.Empty;

    public string ClientId { get; init; } = string.Empty;

    public string ClientSecret { get; init; } = string.Empty;

    public string PrivateKeyPem { get; init; } = string.Empty;

    public string WebhookSecret { get; init; } = string.Empty;

    public bool EnableWebhookSync { get; init; } = true;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AppId) &&
        !string.IsNullOrWhiteSpace(PrivateKeyPem) &&
        !string.IsNullOrWhiteSpace(WebhookSecret);

    public bool IsWebhookConfigured =>
        !string.IsNullOrWhiteSpace(WebhookSecret);
}
