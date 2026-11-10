using System.Text;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Desk;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

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

    public DeskWebApplicationFactory(Dictionary<string, string?>? configuration = null, bool configured = true, bool registerPorts = true)
    {
        this.configuration = configured ? configuration ?? DefaultConfiguration() : null;
        this.registerPorts = registerPorts;
    }

    public FakeDeskDoor Door { get; } = new();

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
            values[$"Desk:Minds:{i}:Deciders:0"] = Alice;
            values[$"Desk:Minds:{i}:Deciders:1"] = Steward;
            i++;
        }

        return values;
    }

    public static string SocketOf(string mindId) =>
        "/run/huginn/" + (string)DeskVectors.Root["transport"]!["mindRoutes"]!["minds"]![mindId]!;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        if (configuration is not null)
        {
            foreach (var (key, value) in configuration)
            {
                builder.UseSetting(key, value);
            }
        }

        if (registerPorts)
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IDeskCredential, FakeDeskCredential>();
                services.AddSingleton<IDeskDoor>(Door);
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
