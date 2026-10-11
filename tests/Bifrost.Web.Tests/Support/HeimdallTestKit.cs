using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bifrost.Web.Data;
using Bifrost.Web.Features.Heimdall;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace Bifrost.Web.Tests.Support;

/// <summary>An Ed25519 key shaped like Heimdall's signing key: a "ed25519-" kid over a thumbprint prefix.</summary>
public sealed class HeimdallKey
{
    private readonly byte[] _seed;

    private HeimdallKey(byte[] seed, byte[] publicKey, string kid)
    {
        _seed = seed;
        PublicKey = publicKey;
        Kid = kid;
    }

    public string Kid { get; }

    public byte[] PublicKey { get; }

    public static HeimdallKey Generate(string? kid = null)
    {
        var seed = RandomNumberGenerator.GetBytes(Ed25519.SecretKeySize);
        var publicKey = new byte[Ed25519.PublicKeySize];
        Ed25519.GeneratePublicKey(seed, 0, publicKey, 0);
        kid ??= "ed25519-" + Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant()[..12];
        return new HeimdallKey(seed, publicKey, kid);
    }

    public object Jwk() => new Dictionary<string, object?>
    {
        ["kty"] = "OKP",
        ["crv"] = "Ed25519",
        ["x"] = WebEncoders.Base64UrlEncode(PublicKey),
        ["alg"] = "EdDSA",
        ["kid"] = Kid,
        ["use"] = "sig"
    };

    /// <summary>Signs like Heimdall's signJwt: header {alg, kid, typ}, compact JWS.</summary>
    public string Sign(IDictionary<string, object?> claims, string alg = "EdDSA", string? kid = "") =>
        SignParts(HeaderBytes(alg, kid), JsonSerializer.SerializeToUtf8Bytes(claims));

    public byte[] HeaderBytes(string alg = "EdDSA", string? kid = "")
    {
        var header = new Dictionary<string, object?> { ["alg"] = alg, ["typ"] = "JWT" };
        if (kid is not null)
        {
            header["kid"] = kid.Length == 0 ? Kid : kid;
        }

        return JsonSerializer.SerializeToUtf8Bytes(header);
    }

    /// <summary>Signs exactly these header and payload bytes, whatever they hold.</summary>
    public string SignParts(byte[] headerBytes, byte[] payloadBytes)
    {
        var signingInput = WebEncoders.Base64UrlEncode(headerBytes) + "." + WebEncoders.Base64UrlEncode(payloadBytes);
        var message = Encoding.ASCII.GetBytes(signingInput);
        var signature = new byte[Ed25519.SignatureSize];
        Ed25519.Sign(_seed, 0, message, 0, message.Length, signature, 0);
        return signingInput + "." + WebEncoders.Base64UrlEncode(signature);
    }
}

public static class HeimdallShapes
{
    public const string Issuer = "https://heimdall.test";

    public static string NewAccountId() => Guid.NewGuid().ToString();

    /// <summary>The claim set of Heimdall src/claims.ts issueAccessClaim.</summary>
    public static Dictionary<string, object?> AccessClaims(
        string accountId,
        DateTimeOffset now,
        string[]? capabilities = null,
        string? displayName = "Genuine Name",
        Action<Dictionary<string, object?>>? mutate = null)
    {
        var issuedAt = now.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object?>
        {
            ["iss"] = Issuer,
            ["aud"] = "bifrost",
            ["sub"] = accountId,
            ["sid"] = Guid.NewGuid().ToString(),
            ["jti"] = Guid.NewGuid().ToString(),
            ["iat"] = issuedAt,
            ["nbf"] = issuedAt,
            ["exp"] = issuedAt + 900,
            ["typ"] = "heimdall_access",
            ["account_id"] = accountId,
            ["access_revision"] = 1,
            ["app"] = new Dictionary<string, object?> { ["slug"] = "bifrost", ["profile_version"] = 1 },
            ["facts"] = Array.Empty<string>(),
            ["capabilities"] = capabilities ?? [],
            ["identities"] = new[]
            {
                new Dictionary<string, object?> { ["provider"] = "discord", ["providerUserId"] = "1234567890" }
            }
        };
        if (displayName is not null)
        {
            claims["display_name"] = displayName;
        }

        mutate?.Invoke(claims);
        return claims;
    }

    /// <summary>
    /// The body Heimdall's redeem route answers with: the completionPayload of src/app.ts, whose unsigned
    /// fields (loose capabilities, facts, name) Bifrost must not decide anything from.
    /// </summary>
    public static Dictionary<string, object?> Completion(
        string returnTo,
        string accountId,
        string accessToken,
        string[]? looseCapabilities = null,
        string[]? looseFacts = null,
        string looseName = "Loose Name") => new()
    {
        ["status"] = "success",
        ["provider"] = "discord",
        ["mode"] = "sign_in",
        ["appSlug"] = "bifrost",
        ["account"] = new Dictionary<string, object?> { ["id"] = accountId, ["displayName"] = looseName },
        ["entitlements"] = new Dictionary<string, object?>
        {
            ["facts"] = looseFacts ?? [],
            ["snapshots"] = Array.Empty<string>()
        },
        ["returnTo"] = returnTo,
        ["connection"] = null,
        ["session"] = new Dictionary<string, object?> { ["accountId"] = accountId, ["appSlug"] = "bifrost" },
        ["accessToken"] = accessToken,
        ["refreshToken"] = "refresh-token-not-read",
        ["refresh"] = new Dictionary<string, object?> { ["expiresAt"] = "2026-11-11T00:00:00.000Z" },
        ["verification"] = new Dictionary<string, object?> { ["issuer"] = Issuer },
        ["sharedCapabilities"] = looseCapabilities ?? [],
        ["hybridCapabilities"] = Array.Empty<string>()
    };
}

public sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    public DateTimeOffset Now => _now;

    public void Advance(TimeSpan by) => _now += by;

    public override DateTimeOffset GetUtcNow() => _now;
}

/// <summary>
/// Stands in for Heimdall's HTTP surface: the JWKS, the oauth start endpoint (which, like app.ts, answers
/// a policy start only when the app caller's secret is present) and the completion-code redeem route.
/// </summary>
public sealed class FakeHeimdall : HttpMessageHandler
{
    private readonly Dictionary<string, Dictionary<string, object?>> _codes = new(StringComparer.Ordinal);

    public List<HeimdallKey> Published { get; } = [];

    public List<object> ExtraJwks { get; } = [];

    public int JwksFetches { get; private set; }

    public HttpStatusCode JwksStatus { get; set; } = HttpStatusCode.OK;

    private TaskCompletionSource? _jwksHold;

    /// <summary>Completes when a JWKS request arrives after <see cref="HoldJwks"/>.</summary>
    public TaskCompletionSource JwksArrived { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Holds the next JWKS responses until the returned source completes.</summary>
    public TaskCompletionSource HoldJwks()
    {
        JwksArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return _jwksHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Exception? JwksFailure { get; set; }

    public List<string> StartBodies { get; } = [];

    public List<string?> StartSecrets { get; } = [];

    /// <summary>The secret a policy start must carry in x-heimdall-app-secret; null means Heimdall asks for none.</summary>
    public string? RequiredAppSecret { get; set; }

    public HttpStatusCode StartStatus { get; set; } = HttpStatusCode.OK;

    public string StartBody { get; set; } =
        """{"authorizationUrl":"https://heimdall.test/oauth/discord/authorize?state=fixture"}""";

    public int RedeemCalls { get; private set; }

    public HttpStatusCode? RedeemStatus { get; set; }

    public string? RedeemBody { get; set; }

    /// <summary>Registers a completion code Heimdall will redeem once, answering with this payload.</summary>
    public string RegisterCode(Dictionary<string, object?> completion)
    {
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _codes[code] = completion;
        return code;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Get && path == "/.well-known/jwks.json")
        {
            JwksFetches++;
            JwksArrived.TrySetResult();
            if (_jwksHold is not null)
            {
                await _jwksHold.Task.WaitAsync(cancellationToken);
            }

            if (JwksFailure is not null)
            {
                throw JwksFailure;
            }

            var keys = Published.Select(x => x.Jwk()).Concat(ExtraJwks).ToArray();
            return new HttpResponseMessage(JwksStatus)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { keys }), Encoding.UTF8, "application/json")
            };
        }

        if (request.Method == HttpMethod.Post && path.StartsWith("/v1/oauth/", StringComparison.Ordinal) && path.EndsWith("/start", StringComparison.Ordinal))
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var secret = request.Headers.TryGetValues("x-heimdall-app-secret", out var values) ? values.Single() : null;
            StartBodies.Add(body);
            StartSecrets.Add(secret);
            var wantsPolicy = JsonDocument.Parse(body).RootElement.TryGetProperty("entitlementPolicy", out _);
            if (wantsPolicy && RequiredAppSecret is not null && secret != RequiredAppSecret)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("""{"error":"app_auth_required"}""", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(StartStatus)
            {
                Content = new StringContent(StartBody, Encoding.UTF8, "application/json")
            };
        }

        if (request.Method == HttpMethod.Post && path == "/v1/apps/bifrost/auth-completions/redeem")
        {
            RedeemCalls++;
            if (RedeemStatus is { } status)
            {
                return new HttpResponseMessage(status)
                {
                    Content = new StringContent(RedeemBody ?? "{}", Encoding.UTF8, "application/json")
                };
            }

            var code = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))
                .RootElement.GetProperty("completionCode").GetString()!;
            if (!_codes.Remove(code, out var completion))
            {
                return new HttpResponseMessage(HttpStatusCode.Gone)
                {
                    Content = new StringContent("""{"error":"invalid_or_expired_completion_code"}""", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(RedeemBody ?? JsonSerializer.Serialize(completion), Encoding.UTF8, "application/json")
            };
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

public sealed class CapturedLogs : ILoggerProvider
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return _lines.ToArray();
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Capture(this);

    public void Dispose()
    {
    }

    private sealed class Capture(CapturedLogs owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner._lines)
            {
                owner._lines.Add(formatter(state, exception) + (exception is null ? string.Empty : "\n" + exception));
            }
        }
    }
}

/// <summary>An attempt Start issued: its nonce, the cookie it set, and the returnTo it sent Heimdall.</summary>
public sealed record StartedAttempt(string Nonce, string? Cookie, string ReturnTo, HttpResponseMessage Response);

public sealed record CompletionResult(HttpResponseMessage Response, bool SignedIn);

/// <summary>A local Bifrost with Heimdall faked in-process: a test JWKS, a test clock, captured logs.</summary>
public sealed class HeimdallHost : TestWebApplicationFactory
{
    public const string AppSecret = "test-heimdall-app-secret";

    private readonly string _appSecret;
    private readonly string _baseUrl;

    public TestClock Clock { get; } = new();

    public FakeHeimdall Heimdall { get; } = new();

    public CapturedLogs Logs { get; } = new();

    public HeimdallKey Key { get; } = HeimdallKey.Generate();

    public HeimdallHost(string appSecret = AppSecret, string baseUrl = HeimdallShapes.Issuer)
    {
        _appSecret = appSecret;
        _baseUrl = baseUrl;
        Heimdall.Published.Add(Key);
        Heimdall.RequiredAppSecret = AppSecret;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // The GitHub OAuth scheme is registered from configuration read while the app builds, which only host settings reach.
        builder.UseSetting("GitHubOAuth:ClientId", "configured-for-tests");
        builder.UseSetting("GitHubOAuth:ClientSecret", "configured-for-tests");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Heimdall:BaseUrl"] = _baseUrl,
                ["Heimdall:AppSecret"] = _appSecret,
                ["Heimdall:DiscordGuildId"] = "guild-1",
                ["Heimdall:DiscordAllowedRoleIds:0"] = "role-1"
            });
        });
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddHttpClient(HeimdallTokenVerifier.ClientName).ConfigurePrimaryHttpMessageHandler(() => Heimdall);
            services.AddHttpClient<HeimdallAuthService>().ConfigurePrimaryHttpMessageHandler(() => Heimdall);
        });
    }

    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = false,
        BaseAddress = new Uri("https://localhost")
    });

    public string Token(string accountId, string[]? capabilities = null, string? displayName = "Genuine Name",
        Action<Dictionary<string, object?>>? mutate = null, HeimdallKey? signer = null) =>
        (signer ?? Key).Sign(HeimdallShapes.AccessClaims(accountId, Clock.Now, capabilities, displayName, mutate));

    public Task<StartedAttempt> StartAsync(bool member = false, string provider = "discord", string returnUrl = "/App/Mill") =>
        StartAtAsync(
            $"/auth/heimdall/{provider}?returnUrl={Uri.EscapeDataString(returnUrl)}" + (member ? "&access=member" : string.Empty));

    public async Task<StartedAttempt> StartAtAsync(string url)
    {
        using var client = NewClient();
        var before = Heimdall.StartBodies.Count;
        var response = await client.GetAsync(url);
        if (Heimdall.StartBodies.Count == before)
        {
            return new StartedAttempt(string.Empty, null, string.Empty, response);
        }

        var returnTo = JsonDocument.Parse(Heimdall.StartBodies[^1]).RootElement.GetProperty("returnTo").GetString()!;
        var nonce = QueryHelpers.ParseQuery(new Uri(returnTo).Query)["attempt"].ToString();
        return new StartedAttempt(nonce, CookieValue(response, HeimdallAttemptCookie.Prefix + nonce), returnTo, response);
    }

    /// <summary>Registers a completion code as Heimdall would mint it after authenticating this attempt.</summary>
    public string CodeFor(StartedAttempt attempt, string accountId, string? accessToken = null, string[]? looseCapabilities = null,
        string[]? looseFacts = null, string looseName = "Loose Name", string? returnTo = null)
    {
        var completion = HeimdallShapes.Completion(
            returnTo ?? attempt.ReturnTo, accountId, accessToken ?? Token(accountId), looseCapabilities, looseFacts, looseName);
        return Heimdall.RegisterCode(completion);
    }

    /// <summary>The browser's POST from the complete page: the attempt and code in a form, the attempt cookie by name.</summary>
    public async Task<CompletionResult> CompleteAsync(string? nonce, string? code, string? cookie, string? cookieName = null)
    {
        using var client = NewClient();
        var fields = new Dictionary<string, string>();
        if (nonce is not null)
        {
            fields["attempt"] = nonce;
        }

        if (code is not null)
        {
            fields["code"] = code;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/heimdall/complete") { Content = new FormUrlEncodedContent(fields) };
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", $"{cookieName ?? HeimdallAttemptCookie.Prefix + nonce}={cookie}");
        }

        var response = await client.SendAsync(request);
        var session = CookieValue(response, ".AspNetCore.Cookies");
        return new CompletionResult(response, !string.IsNullOrEmpty(session));
    }

    public Task<CompletionResult> CompleteAsync(StartedAttempt attempt, string code) =>
        CompleteAsync(attempt.Nonce, code, attempt.Cookie);

    public static string? CookieValue(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var headers))
        {
            return null;
        }

        var prefix = name + "=";
        var header = headers.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.Ordinal));
        return header?[prefix.Length..].Split(';')[0];
    }

    public static string? SetCookieHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var headers)
            ? headers.FirstOrDefault(x => x.StartsWith(name + "=", StringComparison.Ordinal))
            : null;

    public async Task<T> Db<T>(Func<BifrostDbContext, Task<T>> query)
    {
        using var scope = Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<BifrostDbContext>());
    }

    public Task<Bifrost.Web.Domain.UserAccount?> Account(string heimdallAccountId) =>
        Db(db => db.UserAccounts
            .Include(x => x.Membership!).ThenInclude(x => x.RoleAssignments)
            .SingleOrDefaultAsync(x => x.HeimdallAccountId == heimdallAccountId));

    public Task<int> AccountCount() => Db(db => db.UserAccounts.CountAsync());
}
