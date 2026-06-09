namespace Bifrost.Web.Configuration;

public sealed class HeimdallOptions
{
    public const string SectionName = "Heimdall";

    public string BaseUrl { get; init; } = "https://heimdall.gamecult.org";

    public string AppSlug { get; init; } = "bifrost";

    public string DiscordGuildId { get; init; } = string.Empty;

    public string[] DiscordAllowedRoleIds { get; init; } = [];

    public string PatreonTierTitle { get; init; } = "Inner Sanctum";

    public string PatronSupportIntakeSecret { get; init; } = string.Empty;

    public bool EnablePatronSupportIntake { get; init; } = true;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) &&
        !string.IsNullOrWhiteSpace(AppSlug);

    public bool IsDiscordConfigured =>
        IsConfigured &&
        !string.IsNullOrWhiteSpace(DiscordGuildId) &&
        DiscordAllowedRoleIds.Length > 0;

    public bool IsPatreonConfigured =>
        IsConfigured &&
        !string.IsNullOrWhiteSpace(PatreonTierTitle);

    public bool IsPatronSupportIntakeConfigured =>
        IsConfigured &&
        EnablePatronSupportIntake &&
        !string.IsNullOrWhiteSpace(PatronSupportIntakeSecret);
}
