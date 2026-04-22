namespace Bifrost.Web.Configuration;

public sealed class BifrostHostOptions
{
    public const string SectionName = "Host";

    public string PublicBaseUrl { get; init; } = string.Empty;

    public string ExpectedHost { get; init; } = string.Empty;

    public bool RequireStrictHostValidation { get; init; } = true;

    public bool IsConfigured =>
        Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out _) &&
        !string.IsNullOrWhiteSpace(ExpectedHost);
}
