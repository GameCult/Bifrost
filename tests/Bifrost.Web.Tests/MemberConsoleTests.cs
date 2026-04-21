using System.Net;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Bifrost.Web.Tests;

public sealed class MemberConsoleTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public MemberConsoleTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Active_member_can_open_console()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/App");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Operational picture", html);
    }

    [Fact]
    public async Task Anonymous_user_is_redirected_to_sign_in()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        client.DefaultRequestHeaders.Add("X-Test-Anonymous", "true");
        using var response = await client.GetAsync("/App");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/auth/sign-in", response.Headers.Location?.OriginalString);
    }
}
