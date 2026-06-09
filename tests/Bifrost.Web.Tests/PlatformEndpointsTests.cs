using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bifrost.Web.Features.Patronage;
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

    [Fact]
    public async Task Heimdall_paypal_support_event_records_patron_support_and_is_idempotent()
    {
        using var client = _factory.CreateClient();
        var now = DateTimeOffset.UtcNow;
        var heimdallAccountId = $"heimdall-paypal-{Guid.NewGuid():N}";
        var providerEventId = $"WH-{Guid.NewGuid():N}";
        Guid patronUserAccountId;

        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
            var patron = new UserAccount
            {
                HeimdallAccountId = heimdallAccountId,
                DisplayName = "PayPal Patron",
                GitHubLogin = $"paypal-{Guid.NewGuid():N}",
                NormalizedGitHubLogin = $"paypal-{Guid.NewGuid():N}",
                CreatedAtUtc = now,
                LastSeenAtUtc = now
            };
            patronUserAccountId = patron.Id;
            patron.Membership = new Membership
            {
                UserAccountId = patron.Id,
                Status = MembershipStatus.Active,
                CreatedAtUtc = now
            };
            dbContext.UserAccounts.Add(patron);
            await dbContext.SaveChangesAsync();
        }

        var payload = JsonSerializer.Serialize(new
        {
            heimdallAccountId,
            provider = "PayPal",
            providerEventId,
            kind = "OneTimeDonation",
            amount = 125m,
            currencyCode = "USD",
            externalSupportId = "PAYMENT.CAPTURE.COMPLETED:CAPTURE-1",
            supportedAtUtc = now,
            isCurrentRecurringSupport = false,
            providerPayerId = "PAYER-1",
            notes = "Verified PayPal checkout capture from Heimdall."
        });

        using var firstRequest = CreateSignedHeimdallRequest(payload);
        using var firstResponse = await client.SendAsync(firstRequest);
        using var secondRequest = CreateSignedHeimdallRequest(payload);
        using var secondResponse = await client.SendAsync(secondRequest);

        Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, secondResponse.StatusCode);

        using var verificationScope = _factory.Services.CreateScope();
        var verificationContext = verificationScope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var supportEvents = verificationContext.PatronSupportEvents
            .Where(x => x.Provider == ExternalPatronProvider.PayPal && x.ProviderEventId == providerEventId)
            .ToList();
        var tierSnapshot = verificationContext.TierSnapshots
            .Single(x => x.Kind == TierSnapshotKind.Patron &&
                x.IsCurrent &&
                x.Membership.UserAccountId == patronUserAccountId);

        Assert.Single(supportEvents);
        Assert.Equal("PAYMENT.CAPTURE.COMPLETED:CAPTURE-1", supportEvents.Single().ExternalSupportId);
        Assert.Equal("Patron Silver", tierSnapshot.Label);
        Assert.Equal(2m, tierSnapshot.Weight);
    }

    [Fact]
    public async Task Heimdall_paypal_adjustment_reduces_derived_patron_tier()
    {
        using var client = _factory.CreateClient();
        var now = DateTimeOffset.UtcNow;
        var heimdallAccountId = $"heimdall-refund-{Guid.NewGuid():N}";
        Guid patronUserAccountId;

        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
            var patron = new UserAccount
            {
                HeimdallAccountId = heimdallAccountId,
                DisplayName = "Refunded Patron",
                GitHubLogin = $"refund-{Guid.NewGuid():N}",
                NormalizedGitHubLogin = $"refund-{Guid.NewGuid():N}",
                CreatedAtUtc = now,
                LastSeenAtUtc = now
            };
            patronUserAccountId = patron.Id;
            patron.Membership = new Membership
            {
                UserAccountId = patron.Id,
                Status = MembershipStatus.Active,
                CreatedAtUtc = now
            };
            dbContext.UserAccounts.Add(patron);
            await dbContext.SaveChangesAsync();
        }

        var donationPayload = JsonSerializer.Serialize(new
        {
            heimdallAccountId,
            provider = "PayPal",
            providerEventId = $"WH-{Guid.NewGuid():N}",
            kind = "OneTimeDonation",
            amount = 125m,
            currencyCode = "USD",
            externalSupportId = "PAYMENT.CAPTURE.COMPLETED:CAPTURE-2",
            supportedAtUtc = now,
            isCurrentRecurringSupport = false
        });
        var refundPayload = JsonSerializer.Serialize(new
        {
            heimdallAccountId,
            provider = "PayPal",
            providerEventId = $"WH-{Guid.NewGuid():N}",
            kind = "SupportAdjustment",
            amount = -125m,
            currencyCode = "USD",
            externalSupportId = "PAYMENT.CAPTURE.REFUNDED:CAPTURE-2",
            supportedAtUtc = now.AddMinutes(1),
            isCurrentRecurringSupport = false,
            notes = "Verified PayPal refund from Heimdall."
        });

        using var donationRequest = CreateSignedHeimdallRequest(donationPayload);
        using var donationResponse = await client.SendAsync(donationRequest);
        using var refundRequest = CreateSignedHeimdallRequest(refundPayload);
        using var refundResponse = await client.SendAsync(refundRequest);

        Assert.Equal(HttpStatusCode.Accepted, donationResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, refundResponse.StatusCode);

        using var verificationScope = _factory.Services.CreateScope();
        var verificationContext = verificationScope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var currentPatronTiers = verificationContext.TierSnapshots
            .Where(x => x.Kind == TierSnapshotKind.Patron &&
                x.IsCurrent &&
                x.Membership.UserAccountId == patronUserAccountId)
            .ToList();

        Assert.Empty(currentPatronTiers);
    }

    private static string ComputeSignature(string secret, string payload)
    {
        using var hasher = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hasher.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static HttpRequestMessage CreateSignedHeimdallRequest(string payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/heimdall/patron-support/events");
        request.Headers.Add("X-Heimdall-Signature-256", ComputeSignature("test-heimdall-intake-secret", payload));
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        return request;
    }
}
