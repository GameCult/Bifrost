using System.Security.Cryptography;
using Bifrost.Web.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Bifrost.Web.Features.Oidc;

/// <summary>A configuration fault in the OIDC provider. The message names the setting, never its value.</summary>
public sealed class OidcConfigurationException(string message) : InvalidOperationException(message);

/// <summary>
/// The secrets the OIDC provider runs on, read once from the files <see cref="OidcIssuerOptions"/> names. Any
/// unreadable or wrong-shaped file stops startup; there is no fallback key.
/// </summary>
public sealed class OidcKeyMaterial : IDisposable
{
    private const string NistP256Oid = "1.2.840.10045.3.1.7";
    private const int EncryptionKeyBytes = 32;

    private readonly ECDsa _signingEcdsa;

    private OidcKeyMaterial(ECDsa signingEcdsa, SecurityKey signingKey, SecurityKey encryptionKey, string clientSecret)
    {
        _signingEcdsa = signingEcdsa;
        SigningKey = signingKey;
        EncryptionKey = encryptionKey;
        ClientSecret = clientSecret;
    }

    public SecurityKey SigningKey { get; }

    public SecurityKey EncryptionKey { get; }

    public string ClientSecret { get; }

    public static OidcKeyMaterial Load(OidcIssuerOptions options)
    {
        var mas = options.Clients.Mas;
        Require(options.SigningKeyPath, "Bifrost:Oidc:SigningKeyPath");
        Require(options.EncryptionKeyPath, "Bifrost:Oidc:EncryptionKeyPath");
        Require(mas.ClientId, "Bifrost:Oidc:Clients:Mas:ClientId");
        Require(mas.ClientSecretPath, "Bifrost:Oidc:Clients:Mas:ClientSecretPath");
        Require(mas.RedirectUri, "Bifrost:Oidc:Clients:Mas:RedirectUri");
        // A rooted path such as "/callback" parses as an absolute file URI on Linux, so the scheme is checked too.
        if (!Uri.TryCreate(mas.RedirectUri, UriKind.Absolute, out var redirect) ||
            redirect.Scheme is not ("https" or "http"))
        {
            throw new OidcConfigurationException("Bifrost:Oidc:Clients:Mas:RedirectUri must be an absolute http(s) URI.");
        }

        if (!string.IsNullOrWhiteSpace(mas.BackchannelLogoutUri) &&
            (!Uri.TryCreate(mas.BackchannelLogoutUri, UriKind.Absolute, out var logout) ||
             logout.Scheme is not ("https" or "http")))
        {
            throw new OidcConfigurationException("Bifrost:Oidc:Clients:Mas:BackchannelLogoutUri must be an absolute http(s) URI.");
        }

        var ecdsa = LoadSigningKey(options.SigningKeyPath);
        try
        {
            var signingKey = new ECDsaSecurityKey(ecdsa)
            {
                KeyId = Base64UrlEncoder.Encode(SHA256.HashData(ecdsa.ExportSubjectPublicKeyInfo()))
            };
            return new OidcKeyMaterial(
                ecdsa,
                signingKey,
                new SymmetricSecurityKey(LoadEncryptionKey(options.EncryptionKeyPath)),
                ReadFile(mas.ClientSecretPath, "Bifrost:Oidc:Clients:Mas:ClientSecretPath").Trim());
        }
        catch
        {
            ecdsa.Dispose();
            throw;
        }
    }

    public void Dispose() => _signingEcdsa.Dispose();

    private static ECDsa LoadSigningKey(string path)
    {
        const string field = "Bifrost:Oidc:SigningKeyPath";
        var pem = ReadFile(path, field);
        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportFromPem(pem);
            if (ecdsa.ExportParameters(false).Curve.Oid.Value != NistP256Oid)
            {
                throw new CryptographicException();
            }

            // ImportFromPem accepts a public key; a signing key needs the private half.
            ecdsa.ExportParameters(true);
            return ecdsa;
        }
        catch (Exception error) when (error is CryptographicException or ArgumentException)
        {
            ecdsa.Dispose();
            throw new OidcConfigurationException($"{field} must name a PEM file holding an EC P-256 private key.");
        }
    }

    private static byte[] LoadEncryptionKey(string path)
    {
        const string field = "Bifrost:Oidc:EncryptionKeyPath";
        try
        {
            var key = Convert.FromBase64String(ReadFile(path, field).Trim());
            if (key.Length == EncryptionKeyBytes)
            {
                return key;
            }
        }
        catch (FormatException)
        {
        }

        throw new OidcConfigurationException($"{field} must name a file holding {EncryptionKeyBytes} bytes as base64.");
    }

    private static string ReadFile(string path, string field)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new OidcConfigurationException($"{field} must name a readable file.");
        }
    }

    private static void Require(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new OidcConfigurationException($"{field} is required when Bifrost:Oidc is configured.");
        }
    }
}
