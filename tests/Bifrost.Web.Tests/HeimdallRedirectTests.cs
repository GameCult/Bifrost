using System.Net;
using Bifrost.Web.Configuration;
using Bifrost.Web.Features.Heimdall;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Tests;

/// <summary>
/// Every Heimdall client as Program registers it (<see cref="HeimdallClientServices.AddHeimdallClients"/>): a 3xx from Heimdall's origin
/// is the answer, never a second request. The app secret and a member's bearer reach only the origin they were configured for.
/// </summary>
public sealed class HeimdallRedirectTests
{
    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Theory]
    [InlineData(typeof(HeimdallAuthService), 307)]
    [InlineData(typeof(HeimdallAuthService), 302)]
    [InlineData(typeof(HeimdallMemberClient), 307)]
    [InlineData(typeof(HeimdallMemberClient), 302)]
    [InlineData(typeof(HeimdallIdentityMoveClient), 307)]
    public async Task A_redirect_from_Heimdall_is_not_followed_and_no_secret_or_bearer_reaches_the_other_host(Type client, int code)
    {
        int first = FreePort(), second = FreePort();
        using var origin = new HttpListener();
        origin.Prefixes.Add($"http://127.0.0.1:{first}/");
        origin.Start();
        using var other = new HttpListener();
        other.Prefixes.Add($"http://127.0.0.1:{second}/");
        other.Start();
        var redirected = Task.Run(async () =>
        {
            var context = await origin.GetContextAsync();
            context.Response.StatusCode = code;
            context.Response.RedirectLocation = $"http://127.0.0.1:{second}/steal";
            context.Response.Close();
        });
        var stolen = new List<string>();
        _ = Task.Run(async () =>
        {
            try
            {
                var context = await other.GetContextAsync();
                stolen.Add(context.Request.Headers["x-heimdall-app-secret"] + context.Request.Headers["Authorization"]);
                context.Response.StatusCode = 200;
                context.Response.Close();
            }
            catch (Exception error) when (error is HttpListenerException or ObjectDisposedException)
            {
            }
        });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new HeimdallOptions { BaseUrl = $"http://127.0.0.1:{first}", AppSecret = "APP-SECRET-VALUE" }));
        services.AddHeimdallClients();
        using var provider = services.BuildServiceProvider();
        using var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(client.Name);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/me/identities");
        request.Headers.Authorization = new("Bearer", "MEMBER-BEARER");

        using var response = await http.SendAsync(request);
        await redirected;
        await Task.Delay(300);

        Assert.Equal(code, (int)response.StatusCode);
        Assert.Empty(stolen);
    }
}
