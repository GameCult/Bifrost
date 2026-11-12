namespace Bifrost.Web.Configuration;

public sealed class HeimdallOptions
{
    public const string SectionName = "Heimdall";

    public string BaseUrl { get; init; } = "https://heimdall.gamecult.org";

    public string AppSlug { get; init; } = "bifrost";

    public string DiscordGuildId { get; init; } = string.Empty;

    public string[] DiscordAllowedRoleIds { get; init; } = [];

    public string PatreonTierTitle { get; init; } = "Inner Sanctum";

    /// <summary>Deprecated: rides in the process environment. Use <see cref="AppSecretFile"/>; the file wins when both are set.</summary>
    public string AppSecret { get; set; } = string.Empty;

    /// <summary>Path of a file holding the app secret (systemd <c>LoadCredential</c> style: <c>%d/heimdall-app-secret</c>).</summary>
    public string AppSecretFile { get; init; } = string.Empty;

    public string PatronSupportIntakeSecret { get; init; } = string.Empty;

    public bool EnablePatronSupportIntake { get; init; } = true;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) &&
        !string.IsNullOrWhiteSpace(AppSlug);

    public bool IsDiscordConfigured =>
        IsConfigured &&
        !string.IsNullOrWhiteSpace(AppSecret) &&
        !string.IsNullOrWhiteSpace(DiscordGuildId) &&
        DiscordAllowedRoleIds.Length > 0;

    public bool IsPatreonConfigured =>
        IsConfigured &&
        !string.IsNullOrWhiteSpace(AppSecret) &&
        !string.IsNullOrWhiteSpace(PatreonTierTitle);

    public bool IsPatronSupportIntakeConfigured =>
        IsConfigured &&
        EnablePatronSupportIntake &&
        !string.IsNullOrWhiteSpace(PatronSupportIntakeSecret);

    /// <summary>
    /// Resolves <see cref="AppSecret"/> once, when the options are built. A set <see cref="AppSecretFile"/> must be a
    /// readable, non-empty file, or startup fails; the message names the option and path, never a secret.
    /// </summary>
    public static void ResolveAppSecret(HeimdallOptions options, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(options.AppSecret))
        {
            logger.LogWarning("Heimdall:AppSecret is deprecated: it rides in the process environment. Use Heimdall:AppSecretFile, which wins when both are set.");
        }

        if (!string.IsNullOrWhiteSpace(options.AppSecretFile))
        {
            string secret;
            try
            {
                secret = File.ReadAllText(options.AppSecretFile).TrimEnd('\r', '\n');
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new InvalidOperationException(
                    $"Heimdall:AppSecretFile must name a readable file; '{options.AppSecretFile}' could not be read ({error.GetType().Name}).");
            }

            if (string.IsNullOrWhiteSpace(secret))
            {
                throw new InvalidOperationException($"Heimdall:AppSecretFile '{options.AppSecretFile}' is empty.");
            }

            options.AppSecret = secret;
        }
    }
}
