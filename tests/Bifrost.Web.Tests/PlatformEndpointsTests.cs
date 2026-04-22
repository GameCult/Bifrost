using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.Web.Tests;

public sealed class PlatformEndpointsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public PlatformEndpointsTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Health_endpoint_returns_ok()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_endpoint_returns_ok_for_test_configuration()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/readyz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GitHub_issue_webhook_creates_work_item()
    {
        using var client = _factory.CreateClient();
        _ = await client.GetAsync("/App");

        const string payload = """
        {
          "action": "opened",
          "repository": { "full_name": "GameCult/Bifrost" },
          "issue": {
            "number": 42,
            "state": "open",
            "html_url": "https://github.com/GameCult/Bifrost/issues/42",
            "title": "Sync issue into Bifrost",
            "body": "Track this from GitHub"
          },
          "sender": { "login": "test-admin" }
        }
        """;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/github/webhooks");
        request.Headers.Add("X-GitHub-Event", "issues");
        request.Headers.Add("X-GitHub-Delivery", Guid.NewGuid().ToString("N"));
        request.Headers.Add("X-Hub-Signature-256", ComputeSignature("test-webhook-secret", payload));
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var workItem = dbContext.WorkItems.Single();
        var issueLink = dbContext.GitHubIssueLinks.Single();
        var project = dbContext.Projects.Single(x => x.Id == workItem.ProjectId);

        Assert.Equal(WorkItemSourceType.GitHubIssue, workItem.SourceType);
        Assert.Equal(42, issueLink.IssueNumber);
        Assert.Equal("bifrost", project.Slug);
        Assert.Equal("GameCult/Bifrost".ToLowerInvariant(), project.GitHubRepository);
    }

    private static string ComputeSignature(string secret, string payload)
    {
        using var hasher = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hasher.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
