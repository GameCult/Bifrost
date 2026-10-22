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
/// never echo the token or any of its contents. One instance serves the process: it owns the key cache.
/// </summary>
public sealed class HeimdallTokenVerifier(
    IHttpClientFactory httpClientFactory,
    IOptions<HeimdallOptions> heimdallOptions,
    TimeProvider timeProvider)
{
    public const string TokenType = "heimdall_access";
    public const string ClientName = "heimdall-jwks";

    internal static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan KeyRefreshInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan KeyMaxAge = TimeSpan.FromHours(1);

    private static readonly string[] MemberCapabilities = ["member_access", "app_access"];

    private readonly SemaphoreSlim _fetchGate = new(1, 1);
    private volatile KeySnapshot _keys = new(new Dictionary<string, byte[]>(StringComparer.Ordinal), DateTimeOffset.MinValue);
    private DateTimeOffset _attemptedAt = DateTimeOffset.MinValue;

    public async Task<HeimdallVerifiedIdentity> VerifyAsync(
        string token,
        string? payloadAccountId,
        CancellationToken cancellationToken)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            throw Refuse("token is not a compact JWS");
        }

        var headerBytes = Decode(parts[0]);
        var payloadBytes = Decode(parts[1]);
        var signature = Decode(parts[2]);

        using var header = ParseJson(headerBytes, "token header is not JSON");
        var kid = ReadKeyId(header.RootElement);
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

        using var payload = ParseJson(payloadBytes, "token payload is not JSON");
        return CheckClaims(payload.RootElement, heimdallOptions.Value, payloadAccountId);
    }

    private sealed record KeySnapshot(Dictionary<string, byte[]> Keys, DateTimeOffset FetchedAt);

    private static byte[] Decode(string segment)
    {
        try
        {
            return WebEncoders.Base64UrlDecode(segment);
        }
        catch (FormatException)
        {
            throw Refuse("token is not base64url");
        }
    }

    private static JsonDocument ParseJson(byte[] bytes, string rule)
    {
        try
        {
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            throw Refuse(rule);
        }
    }

    private static string ReadKeyId(JsonElement header)
    {
        if (header.ValueKind != JsonValueKind.Object || StringClaim(header, "alg") != "EdDSA")
        {
            throw Refuse("token algorithm is not EdDSA");
        }

        var kid = StringClaim(header, "kid");
        if (string.IsNullOrEmpty(kid))
        {
            throw Refuse("token has no key id");
        }

        return kid;
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

        if (NumericDate(claims, "exp") is not { } expires || now - ClockSkew >= expires)
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

    /// <summary>The JWKS client waits a short, bounded time: a slow fetch must not hold sign-ins for long.</summary>
    public static void ConfigureClient(HttpClient client, HeimdallOptions options)
    {
        client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/'));
        client.Timeout = TimeSpan.FromSeconds(10);
    }

    private async Task<byte[]?> FindKeyAsync(string kid, CancellationToken cancellationToken)
    {
        if (TryFreshKey(kid, out var cached))
        {
            return cached;
        }

        await _fetchGate.WaitAsync(cancellationToken);
        try
        {
            var now = timeProvider.GetUtcNow();
            if (now - _attemptedAt >= KeyRefreshInterval)
            {
                _attemptedAt = now;
                _keys = new KeySnapshot(await FetchKeysAsync(cancellationToken), now);
            }

            return _keys.Keys.GetValueOrDefault(kid);
        }
        finally
        {
            _fetchGate.Release();
        }
    }

    private bool TryFreshKey(string kid, out byte[]? key)
    {
        var snapshot = _keys;
        key = null;
        return timeProvider.GetUtcNow() - snapshot.FetchedAt < KeyMaxAge && snapshot.Keys.TryGetValue(kid, out key);
    }

    private async Task<Dictionary<string, byte[]>> FetchKeysAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ReadKeysAsync(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw Refuse("Heimdall keys could not be fetched");
        }
    }

    private async Task<Dictionary<string, byte[]>> ReadKeysAsync(CancellationToken cancellationToken)
    {
        var httpClient = httpClientFactory.CreateClient(ClientName);
        using var response = await httpClient.GetAsync("/.well-known/jwks.json", cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var jwk in document.RootElement.GetProperty("keys").EnumerateArray())
        {
            if (jwk.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = StringClaim(jwk, "kid");
            var x = StringClaim(jwk, "x");
            if (StringClaim(jwk, "kty") != "OKP" ||
                StringClaim(jwk, "crv") != "Ed25519" ||
                string.IsNullOrEmpty(id) ||
                x is null)
            {
                continue;
            }

            var key = WebEncoders.Base64UrlDecode(x);
            if (key.Length == Ed25519.PublicKeySize)
            {
                keys[id] = key;
            }
        }

        return keys;
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
