using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Bifrost.Web.Features.Desk;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bifrost.Web.Tests;

public sealed class DeskSurfaceTests
{
    private static readonly string AnswerTarget = DeskReplay.Target("answer", "gamecult-chat", null);

    [Fact]
    public async Task Not_mapped_without_config()
    {
        using var factory = new DeskWebApplicationFactory(configured: false);
        await factory.EnsureAccountAsync(DeskWebApplicationFactory.Alice, true);

        var reply = await factory.SendAsync(
            "POST", AnswerTarget, DeskReplay.AnswerBody(DeskReplay.ChatFork),
            factory.Token(DeskWebApplicationFactory.Alice, "bifrost-desk", "desk.answer"));

        Assert.Equal(404, reply.Status);
        Assert.Empty(factory.Door.Calls);
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        Assert.DoesNotContain(routes, e => e.RoutePattern.RawText!.StartsWith("/api/desk", StringComparison.Ordinal));
    }

    [Fact]
    public void A_configured_desk_with_no_door_supplied_gets_the_production_unix_socket_door()
    {
        using var factory = new DeskWebApplicationFactory(registerPorts: false);

        factory.CreateClient();

        // The credential is production wiring (OpenIddict validation) and so is the door; a test supplies a fake only by replacing it.
        Assert.IsType<UnixSocketDeskDoor>(factory.Services.GetRequiredService<IDeskDoor>());
    }

    [Fact]
    public async Task Cookie_session_does_not_authenticate()
    {
        using var factory = new DeskWebApplicationFactory();
        DeskReplay.HoldVectorForks(factory.Door);
        await factory.EnsureAccountAsync(DeskWebApplicationFactory.Alice, true);

        // The test host signs every request in as an Active-looking site member through the default scheme. The desk ignores it.
        var reply = await factory.SendAsync("POST", AnswerTarget, DeskReplay.AnswerBody(DeskReplay.ChatFork));
        var withCookie = await factory.SendAsync(
            "POST", AnswerTarget, DeskReplay.AnswerBody(DeskReplay.ChatFork),
            configure: http => http.Request.Headers.Cookie = "bifrost_session=anything");

        Assert.Equal(401, reply.Status);
        Assert.Equal(401, withCookie.Status);
        Assert.Empty(factory.Door.Calls);
    }

    [Fact]
    public async Task Cors_allows_exactly_the_configured_origin_without_credentials()
    {
        using var factory = new DeskWebApplicationFactory();
        DeskReplay.HoldVectorForks(factory.Door);
        await factory.EnsureAccountAsync(DeskWebApplicationFactory.Alice, true);
        var token = factory.Token(DeskWebApplicationFactory.Alice, "bifrost-desk", "desk.answer");

        var allowed = await factory.SendAsync("POST", AnswerTarget, DeskReplay.AnswerBody(DeskReplay.ChatFork), token, DeskWebApplicationFactory.Origin);
        var refused = await factory.SendAsync("POST", AnswerTarget, DeskReplay.AnswerBody(DeskReplay.ChatFork), token, "https://evil.example");
        var lookalike = await factory.SendAsync("POST", AnswerTarget, DeskReplay.AnswerBody(DeskReplay.ChatFork), token, DeskWebApplicationFactory.Origin + ".evil.example");
        var preflight = await factory.SendAsync("OPTIONS", AnswerTarget, null, null, DeskWebApplicationFactory.Origin, http =>
        {
            http.Request.Headers["Access-Control-Request-Method"] = "POST";
            http.Request.Headers["Access-Control-Request-Headers"] = "authorization,content-type";
        });
        var elsewhere = await factory.SendAsync("GET", "/healthz", null, null, DeskWebApplicationFactory.Origin);

        Assert.Equal(DeskWebApplicationFactory.Origin, allowed.Headers.AccessControlAllowOrigin.ToString());
        Assert.False(allowed.Headers.ContainsKey("Access-Control-Allow-Credentials"));
        Assert.Contains("Origin", allowed.Headers.Vary.ToString());
        Assert.False(refused.Headers.ContainsKey("Access-Control-Allow-Origin"));
        Assert.False(lookalike.Headers.ContainsKey("Access-Control-Allow-Origin"));
        Assert.Equal(DeskWebApplicationFactory.Origin, preflight.Headers.AccessControlAllowOrigin.ToString());
        Assert.False(preflight.Headers.ContainsKey("Access-Control-Allow-Credentials"));
        Assert.Contains("POST", preflight.Headers.AccessControlAllowMethods.ToString());
        Assert.False(elsewhere.Headers.ContainsKey("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task An_unrecognised_target_is_fork_unknown_and_opens_nothing()
    {
        using var factory = new DeskWebApplicationFactory();
        await factory.EnsureAccountAsync(DeskWebApplicationFactory.Alice, true);
        var token = factory.Token(DeskWebApplicationFactory.Alice, "bifrost-desk", "desk.answer");

        foreach (var target in new[]
                 {
                     "/api/desk", "/api/desk/", "/api/desk/gamecult-chat", "/api/desk/gamecult-chat/answers/", "/api/desk/a/b/answers",
                     "/api/desk/a/../gamecult-chat/answers", "/api/desk/./gamecult-chat/answers", "/api/desk//answers/x",
                     "/API/DESK/gamecult-chat/answers", "/api/deskx/gamecult-chat/answers", "http://localhost/api/desk/gamecult-chat/answers",
                 })
        {
            var reply = await factory.SendAsync("POST", target, DeskReplay.AnswerBody(DeskReplay.ChatFork), token);
            Assert.True(reply.Status == 404, $"{target}: {reply.Status}");
        }

        Assert.Empty(factory.Door.Calls);
    }

    // Kestrel normalises Request.Path but leaves RawTarget as received. The desk parses RawTarget, so a dot
    // segment can never be folded into a configured Mind's route.
    [Fact]
    public async Task Dot_segments_never_reach_the_route()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.Run(async http =>
        {
            var raw = http.Features.Get<IHttpRequestFeature>()!.RawTarget;
            var text = $"{http.Request.Path}|{raw}";
            http.Response.ContentLength = text.Length;
            await http.Response.WriteAsync(text);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var port = new Uri(address).Port;

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /api/desk/ghost/../gamecult-chat/answers HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"));
        using var reader = new StreamReader(stream, Encoding.ASCII);
        var text = await reader.ReadToEndAsync();
        var echoed = text[(text.LastIndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..].Trim();

        var parts = echoed.Split('|');
        Assert.Equal("/api/desk/gamecult-chat/answers", parts[0]);
        Assert.Equal("/api/desk/ghost/../gamecult-chat/answers", parts[1]);
        Assert.Null(DeskRoute.Parse("POST", parts[1]));
        Assert.NotNull(DeskRoute.Parse("POST", parts[0]));
    }
}
