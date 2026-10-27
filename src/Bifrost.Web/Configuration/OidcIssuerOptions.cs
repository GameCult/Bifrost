namespace Bifrost.Web.Configuration;

/// <summary>
/// The OIDC provider surface (section <c>Bifrost:Oidc</c>). An absent section means the provider is not mapped at
/// all; a present section is complete or startup fails. Keys and the client secret are named by file path, never inlined.
/// </summary>
public sealed class OidcIssuerOptions
{
    public const string SectionName = "Bifrost:Oidc";

    /// <summary>PEM file holding the EC P-256 private key that signs tokens (ES256).</summary>
    public string SigningKeyPath { get; init; } = string.Empty;

    /// <summary>File holding the 32-byte symmetric token-encryption key as base64.</summary>
    public string EncryptionKeyPath { get; init; } = string.Empty;

    public OidcClientsOptions Clients { get; init; } = new();

    /// <summary>Null when the section is absent.</summary>
    public static OidcIssuerOptions? FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        return section.Exists() ? section.Get<OidcIssuerOptions>() ?? new OidcIssuerOptions() : null;
    }
}

public sealed class OidcClientsOptions
{
    /// <summary>Matrix Authentication Service, the one client of this provider.</summary>
    public OidcClientOptions Mas { get; init; } = new();
}

public sealed class OidcClientOptions
{
    public string ClientId { get; init; } = string.Empty;

    /// <summary>File holding the client secret.</summary>
    public string ClientSecretPath { get; init; } = string.Empty;

    public string RedirectUri { get; init; } = string.Empty;
}
