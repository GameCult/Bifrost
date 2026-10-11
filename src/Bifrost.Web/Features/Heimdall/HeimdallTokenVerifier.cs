using System.Text.Json;
using Bifrost.Web.Configuration;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace Bifrost.Web.Features.Heimdall;

/// <summary>
/// The identity a Heimdall-signed access token asserts. Everything here comes from verified claims.
/// </summary>
public sealed record HeimdallVerifiedIdentity(string AccountId, string? DisplayName, bool HasMemberAccess);

/// <summary>
/// Verifies Heimdall's access tokens (EdDSA) against Heimdall's published JWKS. Heimdall is the issuer
/// at <see cref="HeimdallOptions.BaseUrl"/>; the audience is this app's slug. Failures name a rule and
/// never echo the token or any of its contents.
/// </summary>
public sealed class HeimdallTokenVerifier(
    HttpClient httpClient,
    IOptions<HeimdallOptions> heimdallOptions,
    TimeProvider timeProvider)
{
    public const string TokenType = "heimdall_access";

    internal static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan KeyRefreshInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan KeyMaxAge = TimeSpan.FromHours(1);

    private static readonly string[] MemberCapabilities = ["member_access", "app_access"];

    private readonly SemaphoreSlim _fetchGate = new(1, 1);
    private Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _attemptedAt = DateTimeOffset.MinValue;

    public async Task<HeimdallVerifiedIdentity> VerifyAsync(
        string token,
        string? payloadAccountId,
        CancellationToken cancellationToken)
    {
        var options = heimdallOptions.Value;
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            throw Refuse("token is not a compact JWS");
        }

        byte[] headerBytes, payloadBytes, signature;
        try
        {
            headerBytes = WebEncoders.Base64UrlDecode(parts[0]);
            payloadBytes = WebEncoders.Base64UrlDecode(parts[1]);
            signature = WebEncoders.Base64UrlDecode(parts[2]);
        }
        catch (FormatException)
        {
            throw Refuse("token is not base64url");
        }

        string kid;
        try
        {
            using var header = JsonDocument.Parse(headerBytes);
            if (header.RootElement.ValueKind != JsonValueKind.Object ||
                StringClaim(header.RootElement, "alg") != "EdDSA")
            {
                throw Refuse("token algorithm is not EdDSA");
            }

            kid = StringClaim(header.RootElement, "kid") is { Length: > 0 } found
                ? found
                : throw Refuse("token has no key id");
        }
        catch (JsonException)
        {
            throw Refuse("token header is not JSON");
        }

        var publicKey = await FindKeyAsync(kid, cancellationToken);
        if (publicKey is null)
        {
            throw Refuse("token key is not published");
        }

        var signingInput = System.Text.Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        if (signature.Length != Ed25519.SignatureSize ||
            !Ed25519.Verify(signature, 0, publicKey, 0, signingInput, 0, signingInput.Length))
        {
            throw Refuse("token signature is invalid");
        }

        try
        {
            using var payload = JsonDocument.Parse(payloadBytes);
            return CheckClaims(payload.RootElement, options, payloadAccountId);
        }
        catch (JsonException)
        {
            throw Refuse("token payload is not JSON");
        }
    }

    private HeimdallVerifiedIdentity CheckClaims(JsonElement claims, HeimdallOptions options, string? payloadAccountId)
    {
        if (claims.ValueKind != JsonValueKind.Object)
        {
            throw Refuse("token payload is not an object");
        }

        if (StringClaim(claims, "iss") != options.BaseUrl.TrimEnd('/'))
        {
            throw Refuse("token issuer is not Heimdall");
        }

        if (StringClaim(claims, "aud") != options.AppSlug)
        {
            throw Refuse("token audience is not this app");
        }

        if (StringClaim(claims, "typ") != TokenType)
        {
            throw Refuse("token type is not an access token");
        }

        if (!claims.TryGetProperty("app", out var app) ||
            app.ValueKind != JsonValueKind.Object ||
            StringClaim(app, "slug") != options.AppSlug)
        {
            throw Refuse("token app is not this app");
        }

        var now = timeProvider.GetUtcNow();
        if (NumericDate(claims, "nbf") is not { } notBefore || now < notBefore - ClockSkew)
        {
            throw Refuse("token is not yet valid");
        }

        if (NumericDate(claims, "exp") is not { } expires || now >= expires + ClockSkew)
        {
            throw Refuse("token has expired");
        }

        var subject = StringClaim(claims, "sub");
        if (string.IsNullOrEmpty(subject) ||
            subject != StringClaim(claims, "account_id") ||
            subject != payloadAccountId)
        {
            throw Refuse("token subject does not match the account");
        }

        var capabilities = new List<string>();
        if (claims.TryGetProperty("capabilities", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var capability in list.EnumerateArray())
            {
                if (capability.ValueKind == JsonValueKind.String)
                {
                    capabilities.Add(capability.GetString()!);
                }
            }
        }

        return new HeimdallVerifiedIdentity(
            subject,
            StringClaim(claims, "display_name"),
            capabilities.Any(capability => MemberCapabilities.Contains(capability, StringComparer.OrdinalIgnoreCase)));
    }

    private async Task<byte[]?> FindKeyAsync(string kid, CancellationToken cancellationToken)
    {
        await _fetchGate.WaitAsync(cancellationToken);
        try
        {
            var now = timeProvider.GetUtcNow();
            var stale = now - _fetchedAt >= KeyMaxAge;
            if ((stale || !_keys.ContainsKey(kid)) && now - _attemptedAt >= KeyRefreshInterval)
            {
                _attemptedAt = now;
                _keys = await FetchKeysAsync(cancellationToken);
                _fetchedAt = now;
            }

            return _keys.GetValueOrDefault(kid);
        }
        finally
        {
            _fetchGate.Release();
        }
    }

    private async Task<Dictionary<string, byte[]>> FetchKeysAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync("/.well-known/jwks.json", cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (document.RootElement.TryGetProperty("keys", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var jwk in list.EnumerateArray())
                {
                    if (jwk.ValueKind != JsonValueKind.Object ||
                        StringClaim(jwk, "kty") != "OKP" ||
                        StringClaim(jwk, "crv") != "Ed25519" ||
                        StringClaim(jwk, "kid") is not { Length: > 0 } id ||
                        StringClaim(jwk, "x") is not { } x)
                    {
                        continue;
                    }

                    var key = WebEncoders.Base64UrlDecode(x);
                    if (key.Length == Ed25519.PublicKeySize)
                    {
                        keys[id] = key;
                    }
                }
            }

            return keys;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or FormatException)
        {
            throw Refuse("Heimdall keys could not be fetched");
        }
    }

    private static string? StringClaim(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? NumericDate(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var seconds) ||
            seconds is < 0 or > 253402300799)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    private static HeimdallAuthException Refuse(string rule) => new($"Heimdall token refused: {rule}.");
}
