using System.Security.Cryptography;
using Bifrost.Web.Configuration;
using Bifrost.Web.Features.Oidc;
using Bifrost.Web.Tests.Support;
using Microsoft.IdentityModel.Tokens;

namespace Bifrost.Web.Tests;

public sealed class OidcKeyMaterialTests : IDisposable
{
    private readonly OidcTestKit _kit = new();

    public void Dispose() => _kit.Dispose();

    [Fact]
    public void A_complete_configuration_loads_the_signing_key_the_secret_and_the_encryption_key()
    {
        using var material = OidcKeyMaterial.Load(Options());

        var signing = Assert.IsType<ECDsaSecurityKey>(material.SigningKey);
        Assert.False(string.IsNullOrEmpty(signing.KeyId));
        Assert.Equal(_kit.ClientSecret, material.ClientSecret);
        Assert.Equal(32, Assert.IsType<SymmetricSecurityKey>(material.EncryptionKey).Key.Length);
    }

    [Theory]
    [InlineData("SigningKeyPath", "Bifrost:Oidc:SigningKeyPath")]
    [InlineData("EncryptionKeyPath", "Bifrost:Oidc:EncryptionKeyPath")]
    [InlineData("ClientId", "Bifrost:Oidc:Clients:Mas:ClientId")]
    [InlineData("ClientSecretPath", "Bifrost:Oidc:Clients:Mas:ClientSecretPath")]
    [InlineData("RedirectUri", "Bifrost:Oidc:Clients:Mas:RedirectUri")]
    public void A_missing_setting_stops_startup_naming_that_setting(string blank, string expectedField)
    {
        var failure = Assert.Throws<OidcConfigurationException>(() => OidcKeyMaterial.Load(Options(blank: blank)));

        Assert.Contains(expectedField, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_redirect_uri_that_is_not_absolute_stops_startup()
    {
        var failure = Assert.Throws<OidcConfigurationException>(() => OidcKeyMaterial.Load(Options(redirectUri: "/callback")));

        Assert.Contains("Bifrost:Oidc:Clients:Mas:RedirectUri", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("not base64 !!")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAA==")]
    public void An_unusable_encryption_key_stops_startup_naming_the_setting_not_the_path(string content)
    {
        if (content == "missing")
        {
            File.Delete(_kit.EncryptionKeyPath);
        }
        else
        {
            File.WriteAllText(_kit.EncryptionKeyPath, content);
        }

        var failure = Assert.Throws<OidcConfigurationException>(() => OidcKeyMaterial.Load(Options()));

        Assert.Contains("Bifrost:Oidc:EncryptionKeyPath", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("canary-", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_client_secret_file_stops_startup_naming_the_setting_not_the_path()
    {
        File.Delete(_kit.ClientSecretPath);

        var failure = Assert.Throws<OidcConfigurationException>(() => OidcKeyMaterial.Load(Options()));

        Assert.Contains("Bifrost:Oidc:Clients:Mas:ClientSecretPath", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("canary-", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_P384_key_is_not_a_P256_key()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        File.WriteAllText(_kit.SigningKeyPath, p384.ExportPkcs8PrivateKeyPem());

        var failure = Assert.Throws<OidcConfigurationException>(() => OidcKeyMaterial.Load(Options()));

        Assert.Contains("Bifrost:Oidc:SigningKeyPath", failure.Message, StringComparison.Ordinal);
    }

    private OidcIssuerOptions Options(string? blank = null, string? redirectUri = null) => new()
    {
        SigningKeyPath = blank == "SigningKeyPath" ? "" : _kit.SigningKeyPath,
        EncryptionKeyPath = blank == "EncryptionKeyPath" ? "" : _kit.EncryptionKeyPath,
        Clients = new OidcClientsOptions
        {
            Mas = new OidcClientOptions
            {
                ClientId = blank == "ClientId" ? "" : OidcTestKit.ClientId,
                ClientSecretPath = blank == "ClientSecretPath" ? "" : _kit.ClientSecretPath,
                RedirectUri = blank == "RedirectUri" ? "" : redirectUri ?? OidcTestKit.RedirectUri
            }
        }
    };
}
