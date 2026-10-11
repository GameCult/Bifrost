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
    public string Sign(IDictionary<string, object?> claims, string alg = "EdDSA", string? kid = "")
    {
        var header = new Dictionary<string, object?> { ["alg"] = alg, ["typ"] = "JWT" };
        if (kid is not null)
        {
            header["kid"] = kid.Length == 0 ? Kid : kid;
        }

        var signingInput =
            WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header)) + "." +
            WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(claims));
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

    /// <summary>The success payload of Heimdall src/backend-handoff.ts, with the unsigned fields Bifrost used to trust.</summary>
    public static Dictionary<string, object?> Delivery(
        string attemptId,
        string accountId,
        string accessToken,
        string[]? looseCapabilities = null,
        string[]? looseFacts = null,
        string looseName = "Loose Name",
        string returnTo = "/App/Elsewhere") => new()
    {
        ["source"] = "heimdall",
        ["kind"] = "oauth_result",
        ["handoffKind"] = "backend_callback",
        ["attemptId"] = attemptId,
        ["provider"] = "discord",
        ["appSlug"] = "bifrost",
        ["returnTo"] = returnTo,
        ["connection"] = null,
        ["status"] = "success",
        ["mode"] = "sign_in",
        ["account"] = new Dictionary<string, object?> { ["id"] = accountId, ["displayName"] = looseName },
        ["entitlements"] = new Dictionary<string, object?>
        {
            ["facts"] = looseFacts ?? [],
            ["snapshots"] = Array.Empty<string>()
        },
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

/// <summary>Stands in for Heimdall's HTTP surface: the JWKS and the oauth start endpoint.</summary>
public sealed class FakeHeimdall : HttpMessageHandler
{
    public List<HeimdallKey> Published { get; } = [];

    public List<object> ExtraJwks { get; } = [];

    public int JwksFetches { get; private set; }

    public HttpStatusCode JwksStatus { get; set; } = HttpStatusCode.OK;

    public List<string> StartBodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Get && path == "/.well-known/jwks.json")
        {
            JwksFetches++;
            var keys = Published.Select(x => x.Jwk()).Concat(ExtraJwks).ToArray();
            return new HttpResponseMessage(JwksStatus)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { keys }), Encoding.UTF8, "application/json")
            };
        }

        if (request.Method == HttpMethod.Post && path.StartsWith("/v1/oauth/", StringComparison.Ordinal) && path.EndsWith("/start", StringComparison.Ordinal))
        {
            StartBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"authorizationUrl":"https://heimdall.test/oauth/discord/authorize?state=fixture"}""",
                    Encoding.UTF8,
                    "application/json")
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

public sealed record StartedAttempt(string AttemptId, string? Binding, HttpResponseMessage Response);

public sealed record WaitResult(HttpResponseMessage Response, bool SignedIn);

/// <summary>A local Bifrost with Heimdall faked in-process: a test JWKS, a test clock, captured logs.</summary>
public sealed class HeimdallHost : TestWebApplicationFactory
{
    public const string BindingCookie = "bifrost_heimdall_binding";

    public TestClock Clock { get; } = new();

    public FakeHeimdall Heimdall { get; } = new();

    public CapturedLogs Logs { get; } = new();

    public HeimdallKey Key { get; } = HeimdallKey.Generate();

    public HeimdallHost()
    {
        Heimdall.Published.Add(Key);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Heimdall:BaseUrl"] = HeimdallShapes.Issuer,
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

    public async Task<StartedAttempt> StartAsync(bool member = false, string provider = "discord", string returnUrl = "/App/Mill")
    {
        using var client = NewClient();
        var before = Heimdall.StartBodies.Count;
        var response = await client.GetAsync(
            $"/auth/heimdall/{provider}?returnUrl={Uri.EscapeDataString(returnUrl)}" + (member ? "&access=member" : string.Empty));
        var attemptId = Heimdall.StartBodies.Count > before
            ? JsonDocument.Parse(Heimdall.StartBodies[^1]).RootElement.GetProperty("handoff").GetProperty("attemptId").GetString()!
            : string.Empty;
        return new StartedAttempt(attemptId, CookieValue(response, BindingCookie), response);
    }

    public async Task<HttpResponseMessage> DeliverAsync(object payload)
    {
        using var client = NewClient();
        return await client.PostAsync(
            "/auth/heimdall/callback",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
    }

    public async Task<WaitResult> WaitAsync(string attemptId, string? binding)
    {
        using var client = NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/auth/heimdall/wait?attemptId={Uri.EscapeDataString(attemptId)}");
        if (binding is not null)
        {
            request.Headers.Add("Cookie", $"{BindingCookie}={binding}");
        }

        var response = await client.SendAsync(request);
        var session = CookieValue(response, ".AspNetCore.Cookies");
        return new WaitResult(response, !string.IsNullOrEmpty(session));
    }

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
