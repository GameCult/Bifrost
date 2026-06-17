namespace Bifrost.Web.Configuration;

public sealed class BridgeOptions
{
    public const string SectionName = "Bridge";

    public string LocalBridgeToken { get; init; } = string.Empty;

    public bool HasLocalBridgeToken => !string.IsNullOrWhiteSpace(LocalBridgeToken);
}
