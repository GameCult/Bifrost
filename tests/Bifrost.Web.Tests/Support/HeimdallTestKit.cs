using Bifrost.Web.Features.Shared;
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
        string looseName = "Loose Name",
        string mode = "sign_in",
        string provider = "discord") => new()
    {
        ["status"] = "success",
        ["provider"] = provider,
        ["mode"] = mode,
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

    /// <summary>The path of every start, so a google link start can be told from a sign-in start.</summary>
    public List<string> StartPaths { get; } = [];

    /// <summary>The Authorization header of every start (null when absent).</summary>
    public List<string?> StartAuthorizations { get; } = [];

    public List<string> ConfirmBodies { get; } = [];

    public List<string?> ConfirmAuthorizations { get; } = [];

    /// <summary>When set, every links/confirm answers this status with Heimdall's error code for it.</summary>
    public HttpStatusCode? ConfirmStatus { get; set; }

    /// <summary>When set with <see cref="ConfirmStatus"/>, the body of that answer verbatim (empty string included).</summary>
    public string? ConfirmBody { get; set; }

    private readonly HashSet<string> _pendingLinks = new(StringComparer.Ordinal);

    /// <summary>The logins the fake account holds, as GET /v1/me/identities answers them (with the fields Bifrost must never keep).</summary>
    public List<Dictionary<string, object?>> Identities { get; } = [];

    /// <summary>Adds a login to the fake account and returns its id.</summary>
    public string AddIdentity(string provider, string? displayName = "A Name", string? id = null)
    {
        id ??= Guid.NewGuid().ToString();
        Identities.Add(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["provider"] = provider,
            ["providerUserId"] = "PUID-CANARY-" + id[..8],
            ["username"] = "username-canary-" + id[..8],
            ["displayName"] = displayName,
            ["linkedAt"] = "2026-11-13T12:00:00.000Z",
            ["scopes"] = new[] { "scope-canary" }
        });
        return id;
    }

    public List<string?> ListAuthorizations { get; } = [];

    /// <summary>When set, GET /v1/me/identities answers this status with an error body.</summary>
    public HttpStatusCode? ListStatus { get; set; }

    /// <summary>The Authorization header and identity id of every unlink.</summary>
    public List<(string? Authorization, string Id)> Unlinks { get; } = [];

    /// <summary>When set, every unlink answers this status with Heimdall's error code for it (the fake's own rules are bypassed).</summary>
    public HttpStatusCode? UnlinkStatus { get; set; }

    /// <summary>Throws instead of answering a member route, as an unreachable Heimdall does.</summary>
    public Exception? MemberFailure { get; set; }

    private HttpResponseMessage MemberRoute(HttpRequestMessage request, string path)
    {
        static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (MemberFailure is not null)
        {
            throw MemberFailure;
        }

        var authorization = request.Headers.Authorization?.ToString();
        object Listing() => new { accountId = "a", identities = Identities };
        if (request.Method == HttpMethod.Get && path == "/v1/me/identities")
        {
            ListAuthorizations.Add(authorization);
            if (authorization is null)
            {
                return Json(HttpStatusCode.Unauthorized, """{"error":"sign_in_required"}""");
            }

            return ListStatus switch
            {
                HttpStatusCode.Unauthorized => Json(HttpStatusCode.Unauthorized, """{"error":"sign_in_required"}"""),
                { } listed => Json(listed, """{"error":"server_error","detail":"HEIMDALL FREE TEXT"}"""),
                _ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(Listing()))
            };
        }

        var segments = path.Split('/');
        if (request.Method == HttpMethod.Post && segments is ["", "v1", "me", "identities", var id, "unlink"])
        {
            Unlinks.Add((authorization, id));
            if (authorization is null)
            {
                return Json(HttpStatusCode.Unauthorized, """{"error":"sign_in_required"}""");
            }

            if (UnlinkStatus is { } forced)
            {
                return forced switch
                {
                    HttpStatusCode.Unauthorized => Json(forced, """{"error":"sign_in_required"}"""),
                    HttpStatusCode.Forbidden => Json(forced, """{"error":"step_up_required"}"""),
                    HttpStatusCode.NotFound => Json(forced, """{"error":"identity_not_found"}"""),
                    HttpStatusCode.Conflict => Json(forced, """{"error":"last_identity"}"""),
                    _ => Json(forced, """{"error":"server_error","detail":"HEIMDALL FREE TEXT"}""")
                };
            }

            // Heimdall's rule: the bearer must carry a step_up_at claim. (The 600 second window is Heimdall's own and not modelled.)
            var payload = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(authorization["Bearer ".Length..].Split('.')[1]));
            if (!payload.Contains("\"step_up_at\"", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.Forbidden, """{"error":"step_up_required"}""");
            }

            var held = Identities.FirstOrDefault(x => (string?)x["id"] == id);
            if (held is null)
            {
                return Json(HttpStatusCode.NotFound, """{"error":"identity_not_found"}""");
            }

            if (Identities.Count == 1)
            {
                return Json(HttpStatusCode.Conflict, """{"error":"last_identity"}""");
            }

            Identities.Remove(held);
            return Json(
                HttpStatusCode.OK,
                JsonSerializer.Serialize(new { unlinked = new { id, provider = held["provider"], providerUserId = held["providerUserId"] }, identities = Identities }));
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    /// <summary>Registers a pending link Heimdall will confirm once for any bearer.</summary>
    public string RegisterPendingLink(string? id = null)
    {
        id ??= "pending-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        _pendingLinks.Add(id);
        return id;
    }

    /// <summary>The secret a policy start must carry in x-heimdall-app-secret; null means Heimdall asks for none.</summary>
    public string? RequiredAppSecret { get; set; }

    public HttpStatusCode StartStatus { get; set; } = HttpStatusCode.OK;

    /// <summary>A raw Content-Type header for the start answer (for example a charset .NET cannot decode); null means application/json.</summary>
    public string? StartContentType { get; set; }

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
            var authorization = request.Headers.Authorization?.ToString();
            StartBodies.Add(body);
            StartSecrets.Add(secret);
            StartPaths.Add(path);
            StartAuthorizations.Add(authorization);
            var root = JsonDocument.Parse(body).RootElement;
            if (root.TryGetProperty("mode", out var mode) && mode.GetString() == "link")
            {
                if (authorization is null)
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        Content = new StringContent("""{"error":"link_session_required"}""", Encoding.UTF8, "application/json")
                    };
                }

                var linkProvider = path.Split('/')[3];
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(
                        $$"""{"authorizationUrl":"https://heimdall.test/oauth/{{linkProvider}}/authorize?state=link-fixture"}""", Encoding.UTF8, "application/json")
                };
            }

            var wantsPolicy = JsonDocument.Parse(body).RootElement.TryGetProperty("entitlementPolicy", out _);
            if (wantsPolicy && RequiredAppSecret is not null && secret != RequiredAppSecret)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("""{"error":"app_auth_required"}""", Encoding.UTF8, "application/json")
                };
            }

            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(StartBody));
            content.Headers.TryAddWithoutValidation("Content-Type", StartContentType ?? "application/json");
            return new HttpResponseMessage(StartStatus) { Content = content };
        }

        if (path.StartsWith("/v1/me/identities", StringComparison.Ordinal))
        {
            return MemberRoute(request, path);
        }

        if (request.Method == HttpMethod.Post && path == "/v1/apps/bifrost/links/confirm")
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var authorization = request.Headers.Authorization?.ToString();
            ConfirmBodies.Add(body);
            ConfirmAuthorizations.Add(authorization);
            static HttpResponseMessage Json(HttpStatusCode status, string json) =>
                new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            if (authorization is null)
            {
                return Json(HttpStatusCode.Unauthorized, """{"error":"invalid_link_session"}""");
            }

            if (ConfirmStatus is { } forced)
            {
                if (ConfirmBody is not null)
                {
                    return Json(forced, ConfirmBody);
                }

                return forced switch
                {
                    HttpStatusCode.Conflict => Json(forced, """{"error":"identity_linked_elsewhere"}"""),
                    HttpStatusCode.Gone => Json(forced, """{"error":"invalid_or_expired_pending_link"}"""),
                    _ => Json(forced, """{"error":"invalid_link_session","detail":"HEIMDALL FREE TEXT"}""")
                };
            }

            var id = JsonDocument.Parse(body).RootElement.GetProperty("pendingLinkId").GetString()!;
            return _pendingLinks.Remove(id)
                ? Json(HttpStatusCode.Created, """{"status":"linked","provider":"google","accountId":"a","returnTo":"https://localhost/auth/heimdall/complete"}""")
                : Json(HttpStatusCode.Gone, """{"error":"invalid_or_expired_pending_link"}""");
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
public sealed record StartedAttempt(string Nonce, string? CookieName, string? Cookie, string ReturnTo, HttpResponseMessage Response);

public sealed record CompletionResult(HttpResponseMessage Response, bool SignedIn);

/// <summary>A local Bifrost with Heimdall faked in-process: a test JWKS, a test clock, captured logs.</summary>
public sealed class HeimdallHost : TestWebApplicationFactory
{
    public const string AppSecret = "test-heimdall-app-secret";

    private readonly string _appSecret;
    private readonly string _appSlug;
    private readonly string _baseUrl;
    private readonly string _publicBaseUrl;
    private readonly bool _discordRoles;
    private readonly bool _traceHttp;
    private readonly bool _realSessions;

    public TestClock Clock { get; } = new();

    public FakeHeimdall Heimdall { get; } = new();

    public CapturedLogs Logs { get; } = new();

    public HeimdallKey Key { get; } = HeimdallKey.Generate();

    public HeimdallHost(
        string appSecret = AppSecret,
        string baseUrl = HeimdallShapes.Issuer,
        string publicBaseUrl = "https://localhost",
        bool discordRoles = true,
        string appSlug = "bifrost",
        bool traceHttp = false,
        bool realSessions = false)
    {
        _traceHttp = traceHttp;
        _realSessions = realSessions;
        _appSecret = appSecret;
        _appSlug = appSlug;
        _baseUrl = baseUrl;
        _publicBaseUrl = publicBaseUrl;
        _discordRoles = discordRoles;
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
                ["Heimdall:AppSlug"] = _appSlug,
                ["Heimdall:DiscordGuildId"] = "guild-1",
                ["Host:PublicBaseUrl"] = _publicBaseUrl
            });
            if (_discordRoles)
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Heimdall:DiscordAllowedRoleIds:0"] = "role-1" });
            }
        });
        builder.ConfigureLogging(logging =>
        {
            logging.AddProvider(Logs);
            if (_traceHttp)
            {
                // The framework's HttpClient logging writes request headers at Trace; it must never write a bearer there.
                logging.AddFilter("System.Net.Http", LogLevel.Trace);
            }
        });
        builder.ConfigureServices(services =>
        {
            if (_realSessions)
            {
                // The base factory authenticates every request as a test admin; a link needs the session cookie to be the identity.
                services.Configure<Microsoft.AspNetCore.Authentication.AuthenticationOptions>(options =>
                {
                    options.DefaultAuthenticateScheme = Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme;
                    options.DefaultScheme = Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme;
                });
            }

            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddHttpClient(HeimdallTokenVerifier.ClientName).ConfigurePrimaryHttpMessageHandler(() => Heimdall);
            services.AddHttpClient<HeimdallAuthService>().ConfigurePrimaryHttpMessageHandler(() => Heimdall);
            services.AddHttpClient<HeimdallMemberClient>().ConfigurePrimaryHttpMessageHandler(() => Heimdall);
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

    public Task<StartedAttempt> StartAsync(
        bool member = false, string provider = "discord", string returnUrl = "/App/Mill", string? cookieHeader = null) =>
        StartAtAsync(
            $"/auth/heimdall/{provider}?returnUrl={Uri.EscapeDataString(returnUrl)}" + (member ? "&access=member" : string.Empty),
            cookieHeader);

    public async Task<StartedAttempt> StartAtAsync(string url, string? cookieHeader = null)
    {
        using var client = NewClient();
        var before = Heimdall.StartBodies.Count;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (cookieHeader is not null)
        {
            request.Headers.Add("Cookie", cookieHeader);
        }

        return AttemptFrom(await client.SendAsync(request), before);
    }

    /// <summary>The attempt a response started at Heimdall, if the start count moved since <paramref name="before"/>.</summary>
    public StartedAttempt AttemptFrom(HttpResponseMessage response, int before)
    {
        if (Heimdall.StartBodies.Count == before)
        {
            return new StartedAttempt(string.Empty, null, null, string.Empty, response);
        }

        var returnTo = JsonDocument.Parse(Heimdall.StartBodies[^1]).RootElement.GetProperty("returnTo").GetString()!;
        var nonce = QueryHelpers.ParseQuery(new Uri(returnTo).Query)["attempt"].ToString();
        var slot = HeimdallAttemptCookie.SlotNames.FirstOrDefault(x => CookieValue(response, x) is { Length: > 0 });
        return new StartedAttempt(nonce, slot, slot is null ? null : CookieValue(response, slot), returnTo, response);
    }

    /// <summary>Registers a completion code as Heimdall would mint it after authenticating this attempt.</summary>
    public string CodeFor(StartedAttempt attempt, string accountId, string? accessToken = null, string[]? looseCapabilities = null,
        string[]? looseFacts = null, string looseName = "Loose Name", string? returnTo = null, string mode = "sign_in", string provider = "discord")
    {
        var completion = HeimdallShapes.Completion(
            returnTo ?? attempt.ReturnTo, accountId, accessToken ?? Token(accountId), looseCapabilities, looseFacts, looseName, mode, provider);
        return Heimdall.RegisterCode(completion);
    }

    /// <summary>The browser's POST from the complete page: the attempt and code in a form, the attempt cookie by name.</summary>
    public Task<CompletionResult> CompleteAsync(string? nonce, string? code, string? cookie, string? cookieName = null) =>
        CompleteWithHeaderAsync(
            nonce, code, cookie is null ? null : $"{cookieName ?? HeimdallAttemptCookie.SlotNames[0]}={cookie}");

    /// <summary>The same POST carrying a browser's whole Cookie header (a jar), not one attempt cookie.</summary>
    public async Task<CompletionResult> CompleteWithHeaderAsync(string? nonce, string? code, string? cookieHeader)
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
        if (cookieHeader is not null)
        {
            request.Headers.Add("Cookie", cookieHeader);
        }

        var response = await client.SendAsync(request);
        var session = CookieValue(response, BifrostCookies.Session);
        return new CompletionResult(response, !string.IsNullOrEmpty(session));
    }

    /// <summary>A browser session: a normal Heimdall sign-in, answered with the session cookie it earned.</summary>
    public async Task<string> SignInSessionAsync(string accountId, string provider = "discord")
    {
        var attempt = await StartAsync(provider: provider);
        var done = await CompleteAsync(attempt, CodeFor(attempt, accountId));
        return CookieValue(done.Response, BifrostCookies.Session) ?? throw new InvalidOperationException("sign-in set no session");
    }

    /// <summary>The raw POST of the complete page, whatever fields and cookies the caller names.</summary>
    public async Task<HttpResponseMessage> PostCompleteAsync(IDictionary<string, string> fields, string? cookieHeader)
    {
        using var client = NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/heimdall/complete")
        {
            Content = new FormUrlEncodedContent(fields)
        };
        if (cookieHeader is not null)
        {
            request.Headers.Add("Cookie", cookieHeader);
        }

        return await client.SendAsync(request);
    }

    public const string LinkedAccountsUrl = "/Membership/LinkedAccounts";

    /// <summary>The Linked accounts page as a browser reads it, with its session when there is one.</summary>
    public async Task<(HttpResponseMessage Response, string Html)> GetLinkedAccountsAsync(string? session, string query = "")
    {
        using var client = NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, LinkedAccountsUrl + query);
        if (session is not null)
        {
            request.Headers.Add("Cookie", $"{BifrostCookies.Session}={session}");
        }

        var response = await client.SendAsync(request);
        return (response, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// One of the page's buttons as a browser posts it: the page is fetched for its antiforgery token and cookie (when
    /// <paramref name="antiforgery"/>), then the form is posted with the session. Without a session nothing is fetched.
    /// </summary>
    public async Task<StartedAttempt> PostLinkedAccountsAsync(
        string? session, string handler, IDictionary<string, string>? form = null, bool antiforgery = true, HttpMethod? method = null,
        string? extraCookies = null)
    {
        using var client = NewClient();
        var cookies = new List<string>();
        var fields = new Dictionary<string, string>(form ?? new Dictionary<string, string>());
        if (session is not null)
        {
            cookies.Add($"{BifrostCookies.Session}={session}");
        }

        if (antiforgery && session is not null)
        {
            using var get = new HttpRequestMessage(HttpMethod.Get, LinkedAccountsUrl);
            get.Headers.Add("Cookie", string.Join("; ", cookies));
            using var page = await client.SendAsync(get);
            var html = await page.Content.ReadAsStringAsync();
            var token = System.Text.RegularExpressions.Regex.Match(
                html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
            fields["__RequestVerificationToken"] = token;
            cookies.Add($"{BifrostCookies.Antiforgery}={CookieValue(page, BifrostCookies.Antiforgery)}");
        }

        if (extraCookies is not null)
        {
            cookies.Add(extraCookies);
        }

        var before = Heimdall.StartBodies.Count;
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, $"{LinkedAccountsUrl}?handler={handler}");
        if (request.Method == HttpMethod.Post)
        {
            request.Content = new FormUrlEncodedContent(fields);
        }

        if (cookies.Count > 0)
        {
            request.Headers.Add("Cookie", string.Join("; ", cookies));
        }

        return AttemptFrom(await client.SendAsync(request), before);
    }

    public Task<StartedAttempt> StartLinkAsync(
        string? session, string provider = "google", bool antiforgery = true, HttpMethod? method = null, string? extraCookies = null) =>
        PostLinkedAccountsAsync(session, "Link", new Dictionary<string, string> { ["provider"] = provider }, antiforgery, method, extraCookies);

    public Task<StartedAttempt> StartListAsync(string? session) => PostLinkedAccountsAsync(session, "Refresh");

    public Task<StartedAttempt> StartUnlinkAsync(string? session, string identityId) =>
        PostLinkedAccountsAsync(session, "Unlink", new Dictionary<string, string> { ["identityId"] = identityId });

    public Task<CompletionResult> CompleteAsync(StartedAttempt attempt, string code) =>
        CompleteAsync(attempt.Nonce, code, attempt.Cookie, attempt.CookieName);

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
