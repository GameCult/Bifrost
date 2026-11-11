using System.Text;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Desk;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenIddict.Abstractions;

namespace Bifrost.Web.Tests.Support;

public sealed record DeskReply(int Status, string ContentType, byte[] Body, IHeaderDictionary Headers)
{
    public string Text => Encoding.UTF8.GetString(Body);
}

// The app with the Desk section configured and the two ports faked. `Configuration` is the Desk section as
// key/value pairs; null leaves the Desk section out, `RegisterPorts` false leaves the ports unregistered.
public sealed class DeskWebApplicationFactory : TestWebApplicationFactory
{
    public const string Alice = "e0d1c2b3-a495-4687-8879-6a5b4c3d2e1f";
    public const string Steward = "9f8e7d6c-5b4a-4392-8170-0f1e2d3c4b5a";
    public const string Member = "11111111-2222-4333-8444-555555555555";
    public const string Origin = "https://chat.gamecult.org";

    private readonly Dictionary<string, string?>? configuration;
    private readonly bool registerPorts;
    private readonly bool realCredential;
    private readonly bool fakeDoor;

    // A configured desk needs Bifrost:Oidc (its credential validates against the local issuer): generated keys per factory.
    private readonly OidcTestKit oidcKit = new();

    // The chat client's secret, for a test that holds the chat client's own token.
    public string OidcSecret => oidcKit.ClientSecret;

    // fakeDoor false leaves the door to the app's own wiring (UnixSocketDeskDoor), for tests that stand a socket up.
    public DeskWebApplicationFactory(
        Dictionary<string, string?>? configuration = null, bool configured = true, bool registerPorts = true, bool realCredential = false, bool fakeDoor = true)
    {
        this.realCredential = realCredential;
        this.fakeDoor = fakeDoor;
        this.configuration = configured ? configuration ?? DefaultConfiguration() : null;
        this.registerPorts = registerPorts;
    }

    public FakeDeskDoor Door { get; } = new();

    public FakeDeskCredential Credential { get; } = new();

    // Moves time for a real-credential host, so an access token can be aged past its lifetime.
    public OidcTestClock Clock { get; } = new();

    // A test-only scope: a token issued with it is stamped for this audience instead of the desk's, by the test host's
    // own issuer, so "a valid token for another audience" is a real token.
    public const string OtherAudienceScope = "test.other_audience";
    public const string OtherAudience = "bifrost-votes";


    public static Dictionary<string, string?> DefaultConfiguration()
    {
        var values = new Dictionary<string, string?>
        {
            ["Desk:AllowedOrigin"] = Origin,
            ["Desk:RetryAfterSeconds"] = "30",
            ["Desk:Attribution:Operator"] = Alice,
            ["Desk:Attribution:Stewards:0"] = Steward,
        };
        var i = 0;
        foreach (var mind in DeskVectors.Root["transport"]!["mindRoutes"]!["minds"]!.AsObject())
        {
            values[$"Desk:Minds:{i}:Id"] = mind.Key;
            values[$"Desk:Minds:{i}:Socket"] = SocketOf(mind.Key);
            var deciders = DecidersOf(mind.Key);
            for (var d = 0; d < deciders.Length; d++)
            {
                values[$"Desk:Minds:{i}:Deciders:{d}"] = deciders[d];
            }

            i++;
        }

        return values;
    }

    // A distinct list per Mind (different members, order and length), so a door context that carries another
    // Mind's list cannot pass for the route Mind's.
    public static string[] DecidersOf(string mindId) =>
        mindId == "nibu" ? [Steward, Alice, Member] : [Alice, Steward];

    public static string SocketOf(string mindId) =>
        "/run/huginn/" + (string)DeskVectors.Root["transport"]!["mindRoutes"]!["minds"]![mindId]!;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        if (configuration is not null)
        {
            foreach (var (key, value) in oidcKit.Configuration)
            {
                builder.UseSetting(key, value);
            }

            foreach (var (key, value) in configuration)
            {
                builder.UseSetting(key, value);
            }
        }

        if (realCredential)
        {
            // The production credential stays registered; the host signs in by cookie as the real site does.
            builder.ConfigureServices(services =>
            {
                services.Configure<Microsoft.AspNetCore.Authentication.AuthenticationOptions>(options =>
                {
                    options.DefaultScheme = Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme;
                    options.DefaultAuthenticateScheme = Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme;
                });
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
                services.AddOpenIddict().AddServer(server =>
                {
                    server.RegisterScopes(OtherAudienceScope);
                    // After the issuer has prepared the access token's own principal, so the audience it carries is the one changed.
                    server.AddEventHandler<OpenIddict.Server.OpenIddictServerEvents.ProcessSignInContext>(handler =>
                        handler.UseInlineHandler(context =>
                        {
                            if (context.AccessTokenPrincipal is { } principal && principal.HasScope(OtherAudienceScope))
                            {
                                principal.SetAudiences(OtherAudience);
                            }

                            return default;
                        }).SetOrder(OpenIddict.Server.OpenIddictServerHandlers.PrepareAccessTokenPrincipal.Descriptor.Order + 1));
                });
                services.AddSingleton<IDeskDoor>(Door);
            });
        }
        else if (registerPorts)
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IDeskCredential>(Credential);
                if (fakeDoor)
                {
                    services.AddSingleton<IDeskDoor>(Door);
                }
            });
        }
    }

    // Upserts the account with an Active (or Suspended) membership.
    public async Task EnsureAccountAsync(string id, bool active)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var guid = Guid.Parse(id);
        var account = await db.UserAccounts.FindAsync(guid);
        if (account is null)
        {
            account = new UserAccount { Id = guid, GitHubLogin = "desk-" + id[..8], NormalizedGitHubLogin = "desk-" + id[..8], BifrostIdentity = "desk-" + id, NormalizedBifrostIdentity = "desk-" + id };
            db.UserAccounts.Add(account);
            db.Memberships.Add(new Domain.Membership { UserAccountId = guid, Status = active ? MembershipStatus.Active : MembershipStatus.Suspended });
        }
        else
        {
            var membership = db.Memberships.Single(m => m.UserAccountId == guid);
            membership.Status = active ? MembershipStatus.Active : MembershipStatus.Suspended;
        }

        await db.SaveChangesAsync();
    }

    // Sets the membership of a member who registered through the real gate.
    public async Task SetMembershipAsync(string identity, MembershipStatus status)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var account = db.UserAccounts.Single(x => x.NormalizedBifrostIdentity == identity);
        db.Memberships.Single(x => x.UserAccountId == account.Id).Status = status;
        await db.SaveChangesAsync();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            oidcKit.Dispose();
        }
    }

    public string Token(string? subject, string? audience, params string[] scopes) =>
        FakeDeskCredential.Encode(subject, audience, scopes);

    // What routing would see for a raw path. Kestrel refuses a NUL or a malformed escape before routing; the
    // placeholder keeps the request inside /api/desk so the handler's own judgment is what the test observes.
    private static PathString RoutedPath(string rawPath)
    {
        var schemeEnd = rawPath.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd > 0)
        {
            var pathStart = rawPath.IndexOf('/', schemeEnd + 3);
            rawPath = pathStart < 0 ? "/" : rawPath[pathStart..];
        }

        try
        {
            return PathString.FromUriComponent(rawPath);
        }
        catch (Exception)
        {
            return new PathString("/api/desk/_");
        }
    }

    // Sends one request with the RAW target set exactly as given (TestServer's HttpClient sets none). `path` is
    // what routing sees; production Kestrel would hand over the decoded path the same way.
    public async Task<DeskReply> SendAsync(
        string method, string rawTarget, byte[]? body = null, string? token = null, string? origin = null,
        Action<HttpContext>? configure = null)
    {
        var context = await Server.SendAsync(http =>
        {
            http.Request.Method = method;
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("localhost");
            var query = rawTarget.IndexOf('?');
            var rawPath = query >= 0 ? rawTarget[..query] : rawTarget;
            http.Request.Path = RoutedPath(rawPath);
            http.Request.QueryString = query >= 0 ? new QueryString(rawTarget[query..]) : QueryString.Empty;
            http.Features.GetRequiredFeature<IHttpRequestFeature>().RawTarget = rawTarget;
            if (token is not null)
            {
                http.Request.Headers[FakeDeskCredential.Header] = token;
            }

            if (origin is not null)
            {
                http.Request.Headers.Origin = origin;
            }

            if (body is not null)
            {
                http.Request.Body = new MemoryStream(body);
                http.Request.ContentLength = body.Length;
                http.Request.ContentType = "application/json";
            }

            configure?.Invoke(http);
        });

        using var buffer = new MemoryStream();
        await context.Response.Body.CopyToAsync(buffer);
        return new DeskReply(context.Response.StatusCode, context.Response.ContentType ?? string.Empty, buffer.ToArray(), context.Response.Headers);
    }
}
