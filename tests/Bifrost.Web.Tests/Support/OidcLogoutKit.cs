using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bifrost.Web.Configuration;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Oidc;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using OpenIddict.Server;

namespace Bifrost.Web.Tests.Support;

/// <summary>A clock the test moves; its timers never fire, so the hosted reconciler only ticks when a test calls it.</summary>
public sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        base.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
}

/// <summary>A clock whose timers fire only when the test says so, to drive the reconciler's own loop.</summary>
public sealed class ControlledClock : TimeProvider
{
    private readonly List<PendingTimer> _timers = [];
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new PendingTimer(() => callback(state));
        lock (_timers)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    /// <summary>How many delays are currently waiting to be fired.</summary>
    public int Waiting
    {
        get
        {
            lock (_timers)
            {
                return _timers.Count(x => !x.Disposed);
            }
        }
    }

    /// <summary>Fires every timer that is waiting (each delay the loop is sitting in).</summary>
    public void FireTimers()
    {
        PendingTimer[] due;
        lock (_timers)
        {
            due = _timers.Where(x => !x.Disposed).ToArray();
            _timers.Clear();
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private sealed class PendingTimer(Action fire) : ITimer
    {
        public bool Disposed { get; private set; }

        public void Fire() => fire();

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Collects what a component logs, formatted, so a test can say what must and must not appear.</summary>
public sealed class ListLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToArray();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}

/// <summary>
/// A stand-in for MAS's back-channel logout route: answers a scripted status per POST (then 200), records every form
/// body, and can fail like the network does.
/// </summary>
public sealed class FakeMasHandler : HttpMessageHandler
{
    public const int NetworkFault = -1;
    public const int Timeout = -2;
    public const int Unexpected = -3;
    public const int Hang = -4;

    private readonly Queue<int> _script = new();
    private readonly List<string> _bodies = [];

    public void Script(params int[] statuses)
    {
        lock (_script)
        {
            foreach (var status in statuses)
            {
                _script.Enqueue(status);
            }
        }
    }

    public IReadOnlyList<string> Bodies
    {
        get
        {
            lock (_bodies)
            {
                return _bodies.ToArray();
            }
        }
    }

    /// <summary>The logout_tokens posted, in order; asserts each body is exactly one logout_token field.</summary>
    public IReadOnlyList<string> LogoutTokens() => Bodies.Select(body =>
    {
        var form = QueryHelpers.ParseQuery(body);
        Assert.Equal(["logout_token"], form.Keys.ToArray());
        return form["logout_token"].ToString();
    }).ToArray();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
        var body = await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_bodies)
        {
            _bodies.Add(body);
        }

        int status;
        lock (_script)
        {
            status = _script.Count > 0 ? _script.Dequeue() : 200;
        }

        return status switch
        {
            NetworkFault => throw new HttpRequestException("network down"),
            Timeout => throw new TaskCanceledException("timed out"),
            Unexpected => throw new InvalidOperationException("not a network fault"),
            Hang => await HangAsync(cancellationToken),
            _ => new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(BodyCanary) }
        };
    }

    public const string BodyCanary = "mas-body-canary";

    private static async Task<HttpResponseMessage> HangAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("unreachable");
    }
}

/// <summary>
/// A real HTTP listener on loopback standing in for a MAS that answers every POST with 302 to /login and GET /login with
/// 200: what a real HttpClient does with that answer is the registered handler's rule, which a fake handler cannot see.
/// </summary>
public sealed class RedirectingMas : IDisposable
{
    private readonly System.Net.Sockets.TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly List<string> _requests = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;

    public RedirectingMas()
    {
        _listener.Start();
        _accepting = Task.Run(AcceptAsync);
    }

    public string LogoutUri => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/logout";

    /// <summary>"METHOD path" for every request received, in order.</summary>
    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToArray();
            }
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => ServeAsync(connection));
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested)
        {
        }
    }

    private async Task ServeAsync(System.Net.Sockets.TcpClient connection)
    {
        using var owned = connection;
        try
        {
            var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(_stop.Token) ?? "";
            var length = 0;
            while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } header)
            {
                if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    length = int.Parse(header["Content-Length:".Length..].Trim());
                }
            }

            var body = new char[length];
            var read = 0;
            while (read < length)
            {
                var n = await reader.ReadAsync(body.AsMemory(read), _stop.Token);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            var parts = requestLine.Split(' ');
            lock (_requests)
            {
                _requests.Add($"{parts[0]} {parts[1]}");
            }

            var response = parts[0] == "POST"
                ? "HTTP/1.1 302 Found\r\nLocation: /login\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                : "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            _accepting.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
    }
}

/// <summary>A scope factory that fails the first scopes asked of it, as a database outage would.</summary>
public sealed class FlakyScopeFactory(IServiceScopeFactory inner, int failures) : IServiceScopeFactory
{
    public const string Secret = "scope-factory-secret-detail";

    private int _failures = failures;

    public IServiceScope CreateScope()
    {
        if (Interlocked.Decrement(ref _failures) >= 0)
        {
            throw new InvalidOperationException(Secret);
        }

        return inner.CreateScope();
    }
}

/// <summary>An OIDC host with MAS's back-channel logout URI set, a fake MAS behind it and a clock the test drives.</summary>
public sealed class OidcLogoutFactory : OidcWebApplicationFactory
{
    public const string LogoutUri = "https://mas.example.test/upstream/backchannel-logout/01TESTPROVIDER";
    public static readonly Uri Origin = new("https://localhost");

    private readonly string _logoutUri;
    private readonly bool _fakeTransport;

    public OidcLogoutFactory()
    {
        _logoutUri = LogoutUri;
        _fakeTransport = true;
    }

    /// <summary>Keeps the real registered HttpClient and its primary handler and posts to <paramref name="logoutUri"/>.</summary>
    public OidcLogoutFactory(string logoutUri)
    {
        _logoutUri = logoutUri;
        _fakeTransport = false;
    }

    public FakeMasHandler Mas { get; } = new();

    public ManualClock Time { get; } = new();

    public OidcLogoutReconciler Reconciler => Services.GetServices<IHostedService>().OfType<OidcLogoutReconciler>().Single();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Bifrost:Oidc:Clients:Mas:BackchannelLogoutUri", _logoutUri);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
            if (_fakeTransport)
            {
                services.AddHttpClient(OidcLogoutReconciler.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Mas);
            }
        });
    }

    /// <summary>A reconciler with no memory, built over this host's database, as a restarted process would have.</summary>
    public OidcLogoutReconciler NewReconciler(
        TimeProvider? time = null,
        ILogger<OidcLogoutReconciler>? logger = null,
        IServiceScopeFactory? scopes = null) => new(
        scopes ?? Services.GetRequiredService<IServiceScopeFactory>(),
        Services.GetRequiredService<OidcIssuerOptions>(),
        Services.GetRequiredService<OidcKeyMaterial>(),
        Services.GetRequiredService<IOptions<OpenIddictServerOptions>>(),
        Services.GetRequiredService<IHttpClientFactory>(),
        time ?? Time,
        logger ?? NullLogger<OidcLogoutReconciler>.Instance);

    public WebApplicationFactoryClientOptions HostClientOptions() => new()
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
        BaseAddress = Origin
    };

    /// <summary>Registers a member, sets <paramref name="status"/>, and completes one authorization-code flow for them.</summary>
    public async Task<(string Identity, string IdToken)> GrantAsync(MembershipStatus status = MembershipStatus.Active)
    {
        using var client = CreateClient(HostClientOptions());
        var identity = $"member-{Guid.NewGuid():N}";
        using (var register = await client.PostAsJsonAsync("/auth/bifrost/register", new { identity, displayName = identity }))
        {
            Assert.Equal(HttpStatusCode.OK, register.StatusCode);
        }

        await SetMembershipAsync(identity, MembershipStatus.Active);

        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(48));
        var state = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(24));
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var response = await client.GetAsync(
            "/connect/authorize?response_type=code" +
            $"&client_id={Uri.EscapeDataString(OidcTestKit.ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(OidcTestKit.RedirectUri)}" +
            $"&scope=openid%20profile&state={state}&nonce=n-{state}" +
            $"&code_challenge={challenge}&code_challenge_method=S256");
        for (var hops = 0; hops < 4 && IsAuthorizeRedirect(response); hops++)
        {
            var next = response.Headers.Location!;
            response.Dispose();
            response = await client.GetAsync(next.IsAbsoluteUri ? next.PathAndQuery : next.OriginalString);
        }

        var code = QueryHelpers.ParseQuery(response.Headers.Location!.Query)["code"].ToString();
        response.Dispose();

        using var bare = CreateClient(HostClientOptions());
        using var exchange = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = OidcTestKit.RedirectUri,
                ["code_verifier"] = verifier
            })
        };
        exchange.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{OidcTestKit.ClientId}:{Kit.ClientSecret}")));
        using var tokens = await bare.SendAsync(exchange);
        Assert.Equal(HttpStatusCode.OK, tokens.StatusCode);
        var idToken = (await tokens.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id_token").GetString()!;

        if (status != MembershipStatus.Active)
        {
            await SetMembershipAsync(identity, status);
        }

        return (identity, idToken);
    }

    private static bool IsAuthorizeRedirect(HttpResponseMessage response) =>
        response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found &&
        response.Headers.Location is { } location &&
        (location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString).StartsWith("/connect/authorize", StringComparison.Ordinal);

    /// <summary>Gives <paramref name="subject"/> one more valid ad hoc authorization of the MAS client.</summary>
    public async Task AddAuthorizationAsync(string subject)
    {
        using var scope = Services.CreateScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var application = (await applications.FindByClientIdAsync(OidcTestKit.ClientId))!;
        await authorizations.CreateAsync(new OpenIddictAuthorizationDescriptor
        {
            ApplicationId = await applications.GetIdAsync(application),
            Subject = subject,
            Status = OpenIddictConstants.Statuses.Valid,
            Type = OpenIddictConstants.AuthorizationTypes.AdHoc
        });
    }

    /// <summary>Revokes every authorization of the subject, as an operator would outside the reconciler.</summary>
    public async Task RevokeAllAsync(string subject)
    {
        using var scope = Services.CreateScope();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        await foreach (var authorization in authorizations.FindBySubjectAsync(subject))
        {
            await authorizations.TryRevokeAsync(authorization);
        }
    }

    /// <summary>How many of the subject's authorizations are still valid.</summary>
    public int ValidAuthorizations(string subject)
    {
        using var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<BifrostDbContext>()
            .Set<OpenIddictEntityFrameworkCoreAuthorization>().AsNoTracking()
            .Count(x => x.Subject == subject && x.Status == "valid");
    }

    public async Task<JsonWebKeySet> JwksAsync()
    {
        using var client = CreateClient(HostClientOptions());
        return new JsonWebKeySet(await client.GetStringAsync("/.well-known/jwks"));
    }
}
