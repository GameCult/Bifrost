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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
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

/// <summary>
/// A stand-in for MAS's back-channel logout route: answers a scripted status per POST (then 200), records every form
/// body, and can fail like the network does.
/// </summary>
public sealed class FakeMasHandler : HttpMessageHandler
{
    public const int NetworkFault = -1;
    public const int Timeout = -2;

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
            _ => new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("mas-body-canary") }
        };
    }
}

/// <summary>An OIDC host with MAS's back-channel logout URI set, a fake MAS behind it and a clock the test drives.</summary>
public sealed class OidcLogoutFactory : OidcWebApplicationFactory
{
    public const string LogoutUri = "https://mas.example.test/upstream/backchannel-logout/01TESTPROVIDER";
    public static readonly Uri Origin = new("https://localhost");

    public FakeMasHandler Mas { get; } = new();

    public ManualClock Time { get; } = new();

    public OidcLogoutReconciler Reconciler => Services.GetServices<IHostedService>().OfType<OidcLogoutReconciler>().Single();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Bifrost:Oidc:Clients:Mas:BackchannelLogoutUri", LogoutUri);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
            services.AddHttpClient(OidcLogoutReconciler.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Mas);
        });
    }

    /// <summary>A reconciler with no memory, built over this host's database, as a restarted process would have.</summary>
    public OidcLogoutReconciler NewReconciler() => new(
        Services.GetRequiredService<IServiceScopeFactory>(),
        Services.GetRequiredService<OidcIssuerOptions>(),
        Services.GetRequiredService<OidcKeyMaterial>(),
        Services.GetRequiredService<IOptions<OpenIddictServerOptions>>(),
        Services.GetRequiredService<IHttpClientFactory>(),
        Time,
        NullLogger<OidcLogoutReconciler>.Instance);

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
