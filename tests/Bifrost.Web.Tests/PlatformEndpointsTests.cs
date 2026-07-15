using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bifrost.Web.Features.Patronage;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
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
    public async Task Bifrost_native_registration_creates_authenticated_identity_without_oauth()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/bifrost/register");
        request.Headers.Add("X-Test-Anonymous", "true");
        request.Content = JsonContent.Create(new
        {
            identity = $"native-{Guid.NewGuid():N}",
            displayName = "Native Bifrost User",
            returnUrl = "/App"
        });

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var identity = payload.GetProperty("bifrostIdentity").GetString();
        Assert.False(string.IsNullOrWhiteSpace(identity));
        Assert.Equal("Authenticated", payload.GetProperty("membershipStatus").GetString());

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var userAccount = dbContext.UserAccounts
            .Include(x => x.Membership)
            .Single(x => x.BifrostIdentity == identity);

        Assert.Null(userAccount.GitHubUserId);
        Assert.Equal(string.Empty, userAccount.HeimdallAccountId);
        Assert.Equal(MembershipStatus.Authenticated, userAccount.Membership!.Status);
    }

    [Fact]
    public async Task Bifrost_native_registration_rejects_duplicate_identity()
    {
        using var client = _factory.CreateClient();
        var identity = $"native-{Guid.NewGuid():N}";

        using var firstRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/bifrost/register");
        firstRequest.Headers.Add("X-Test-Anonymous", "true");
        firstRequest.Content = JsonContent.Create(new { identity, displayName = "First" });
        using var firstResponse = await client.SendAsync(firstRequest);

        using var secondRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/bifrost/register");
        secondRequest.Headers.Add("X-Test-Anonymous", "true");
        secondRequest.Content = JsonContent.Create(new { identity = identity.ToUpperInvariant(), displayName = "Second" });
        using var secondResponse = await client.SendAsync(secondRequest);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, secondResponse.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        Assert.Single(dbContext.UserAccounts.Where(x => x.NormalizedBifrostIdentity == identity));
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
    public async Task Heimdall_patreon_support_event_records_current_patronage()
    {
        using var client = _factory.CreateClient();
        var now = DateTimeOffset.UtcNow;
        var heimdallAccountId = $"heimdall-patreon-{Guid.NewGuid():N}";
        var providerEventId = $"patreon-member-sync-{Guid.NewGuid():N}";
        Guid patronUserAccountId;

        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
            var patron = new UserAccount
            {
                HeimdallAccountId = heimdallAccountId,
                DisplayName = "Patreon Patron",
                GitHubLogin = $"patreon-{Guid.NewGuid():N}",
                NormalizedGitHubLogin = $"patreon-{Guid.NewGuid():N}",
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
            provider = "Patreon",
            providerEventId,
            kind = "RecurringSupportSnapshot",
            amount = 125m,
            currencyCode = "USD",
            externalSupportId = "patreon:campaign:member-current-support",
            supportedAtUtc = now,
            isCurrentRecurringSupport = true,
            providerPayerId = "patreon-user-1",
            providerSubscriptionId = "patreon-member-1",
            notes = "Verified current Patreon entitlement from Heimdall."
        });

        using var request = CreateSignedHeimdallRequest(payload);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var verificationScope = _factory.Services.CreateScope();
        var verificationContext = verificationScope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var supportEvent = verificationContext.PatronSupportEvents
            .Single(x => x.Provider == ExternalPatronProvider.Patreon && x.ProviderEventId == providerEventId);
        var pointTransaction = verificationContext.PointTransactions
            .Single(x => x.UserAccountId == patronUserAccountId);
        var tierSnapshot = verificationContext.TierSnapshots
            .Single(x => x.Kind == TierSnapshotKind.Patron &&
                x.IsCurrent &&
                x.Membership.UserAccountId == patronUserAccountId);

        Assert.Equal(patronUserAccountId, supportEvent.UserAccountId);
        Assert.Equal(PatronSupportEventKind.RecurringSupportSnapshot, supportEvent.Kind);
        Assert.True(supportEvent.IsCurrentRecurringSupport);
        Assert.Equal("patreon-user-1", supportEvent.ProviderPayerId);
        Assert.Equal("patreon-member-1", supportEvent.ProviderSubscriptionId);
        Assert.Equal(PointTransactionType.PatronSupport, pointTransaction.Type);
        Assert.False(pointTransaction.IsDecaying);
        Assert.Equal("Patron Silver", tierSnapshot.Label);
        Assert.Equal(2m, tierSnapshot.Weight);
        Assert.Contains(verificationContext.AuditEvents, x => x.Action == "patron-support.recorded");
        Assert.Contains(verificationContext.AuditEvents, x => x.Action == "patron-tier.derived");
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

    [Fact]
    public async Task Velvet_patronage_checkout_redirects_to_stripe_checkout()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        await EnsureVelvetProjectAsync(client);

        using var response = await client.GetAsync("/patronage/velvet/checkout?amountCents=1900&item=velvet-room&project=velvet");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("https://checkout.stripe.com/c/pay/cs_test_velvet", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Velvet_patronage_checkout_requires_bifrost_account()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        await EnsureVelvetProjectAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/patronage/velvet/checkout?amountCents=1900&item=velvet-room&project=velvet");
        request.Headers.Add("X-Test-Anonymous", "1");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(
            "/auth/sign-in?returnUrl=%2Fpatronage%2Fvelvet%2Fcheckout%3FamountCents%3D1900%26item%3Dvelvet-room%26project%3Dvelvet",
            response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Velvet_patronage_checkout_rejects_out_of_policy_amount()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        await EnsureVelvetProjectAsync(client);

        using var response = await client.GetAsync("/patronage/velvet/checkout?amountCents=50&item=velvet-room&project=velvet");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Sign_in_chooser_lists_supported_transport_providers()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/auth/sign-in?returnUrl=%2Fpatronage%2Fvelvet%2Fcheckout%3FamountCents%3D1900%26item%3Dvelvet-room%26project%3Dvelvet");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("GitHub", html);
        Assert.Contains("/auth/heimdall/discord", html);
        Assert.Contains("/auth/heimdall/patreon", html);
        Assert.Contains("/auth/heimdall/twitch", html);
    }

    [Fact]
    public async Task Stripe_checkout_completed_webhook_records_general_patronage()
    {
        using var client = _factory.CreateClient();
        _ = await client.GetAsync("/App");
        Guid patronageAccountId;
        Guid projectId;
        using (var setupScope = _factory.Services.CreateScope())
        {
            var setupContext = setupScope.ServiceProvider.GetRequiredService<BifrostDbContext>();
            patronageAccountId = setupContext.UserAccounts.Single(x => x.NormalizedGitHubLogin == "TEST-ADMIN").Id;
            projectId = await EnsureVelvetProjectAsync(setupContext, patronageAccountId);
        }

        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = JsonSerializer.Serialize(new
        {
            id = $"evt_{Guid.NewGuid():N}",
            type = "checkout.session.completed",
            created,
            data = new
            {
                @object = new
                {
                    id = "cs_test_velvet_paid",
                    customer = "cus_velvet_1",
                    currency = "usd",
                    amount_total = 3900,
                    payment_status = "paid",
                    metadata = new Dictionary<string, string>
                    {
                        ["source"] = "velvet.gamecult.org",
                        ["ledger"] = "bifrost",
                        ["purpose"] = "general_patronage",
                        ["project"] = "velvet",
                        ["project_id"] = projectId.ToString("N"),
                        ["item"] = "after-hours-bundle",
                        ["model"] = "deru",
                        ["bifrost_user_account_id"] = patronageAccountId.ToString("N"),
                        ["bifrost_github_login"] = "test-admin"
                    }
                }
            }
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/patronage/stripe/webhook");
        request.Headers.Add("Stripe-Signature", ComputeStripeSignature("whsec_test_webhook_secret", payload));
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var patronageAccount = dbContext.UserAccounts.Single(x => x.NormalizedGitHubLogin == "TEST-ADMIN");
        var supportEvent = dbContext.PatronSupportEvents.Single(x => x.Provider == ExternalPatronProvider.Stripe);

        Assert.Equal(patronageAccount.Id, supportEvent.UserAccountId);
        Assert.Equal("cs_test_velvet_paid", supportEvent.ExternalSupportId);
        Assert.Equal(projectId, supportEvent.ProjectId);
        Assert.Equal(39m, supportEvent.Amount);
        Assert.Equal("USD", supportEvent.CurrencyCode);
        Assert.Contains("velvet", supportEvent.Notes);
        Assert.Contains("after-hours-bundle", supportEvent.Notes);
        Assert.Contains(dbContext.AuditEvents, x => x.Action == "patron-support.recorded");
    }

    [Fact]
    public async Task Eve_governance_surface_returns_motion_verse()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/eve/governance/surface");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("gamecult.eve.surface.v1", json);
        Assert.Contains("bifrost-motion-verse", json);
        Assert.Contains("motion.create", json);
        Assert.Contains("cultmesh://asgard.starfire.bifrost/commands/motion", json);
        Assert.DoesNotContain("/eve/governance/commands", json);
    }

    [Fact]
    public async Task Eve_governance_http_command_route_is_not_mapped()
    {
        using var client = _factory.CreateClient();
        _ = await client.GetAsync("/App");

        Guid motionId;
        using (var setupScope = _factory.Services.CreateScope())
        {
            var dbContext = setupScope.ServiceProvider.GetRequiredService<BifrostDbContext>();
            var actor = dbContext.UserAccounts.Single(x => x.NormalizedGitHubLogin == "TEST-ADMIN");
            var motion = new Motion
            {
                CreatedByUserAccountId = actor.Id,
                Scope = MotionScope.Management,
                Category = MotionCategory.Features,
                Title = "Govern through Eve",
                Summary = "The Motion Verse should command the canonical motion path.",
                ApprovalThreshold = 0.50m,
                OpensAtUtc = DateTimeOffset.UtcNow,
                ClosesAtUtc = DateTimeOffset.UtcNow.AddDays(7),
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            dbContext.Motions.Add(motion);
            await dbContext.SaveChangesAsync();
            motionId = motion.Id;
        }

        var payload = JsonSerializer.Serialize(new
        {
            command = "motion.vote",
            motionId,
            choice = "For",
            comment = "Cast from Eve."
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/eve/governance/commands");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var verificationScope = _factory.Services.CreateScope();
        var verificationContext = verificationScope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        Assert.Empty(verificationContext.Votes.Where(x => x.MotionId == motionId));
        Assert.DoesNotContain(verificationContext.AuditEvents, x => x.Action == "motion.voted");
    }

    [Theory]
    [InlineData("/bridge/actions/request")]
    [InlineData("/bridge/actions/action-123/start")]
    [InlineData("/bridge/actions/action-123/complete")]
    [InlineData("/dispatch/runs/start")]
    [InlineData("/dispatch/runs/run-123/complete")]
    [InlineData("/dispatch/runs/run-123/fail")]
    [InlineData("/transport/receipts")]
    [InlineData("/governance/receipts")]
    public async Task Removed_http_bridge_routes_are_not_mapped(string path)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var bridgeActions = await dbContext.BridgeActions.CountAsync();
        var dispatchRuns = await dbContext.AgentDispatchRuns.CountAsync();
        var transportReceipts = await dbContext.AgentTransportReceipts.CountAsync();
        var governanceReceipts = await dbContext.GovernanceActivityReceipts.CountAsync();

        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-Bifrost-Bridge-Token", "test-bridge-token");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(bridgeActions, await dbContext.BridgeActions.CountAsync());
        Assert.Equal(dispatchRuns, await dbContext.AgentDispatchRuns.CountAsync());
        Assert.Equal(transportReceipts, await dbContext.AgentTransportReceipts.CountAsync());
        Assert.Equal(governanceReceipts, await dbContext.GovernanceActivityReceipts.CountAsync());
    }

    private static string ComputeSignature(string secret, string payload)
    {
        using var hasher = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hasher.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static string ComputeStripeSignature(string secret, string payload)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        using var hasher = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = Convert.ToHexString(
            hasher.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{payload}"))).ToLowerInvariant();

        return $"t={timestamp},v1={signature}";
    }

    private async Task<Guid> EnsureVelvetProjectAsync(HttpClient client)
    {
        _ = await client.GetAsync("/App");

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var owner = await dbContext.UserAccounts.SingleAsync(x => x.NormalizedGitHubLogin == "TEST-ADMIN");
        return await EnsureVelvetProjectAsync(dbContext, owner.Id);
    }

    private static async Task<Guid> EnsureVelvetProjectAsync(BifrostDbContext dbContext, Guid ownerUserAccountId)
    {
        var existing = await dbContext.Projects
            .Where(x => x.Slug == "velvet")
            .OrderBy(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync();
        if (existing is not null)
        {
            if (existing.Status != ProjectStatus.Active)
            {
                existing.Status = ProjectStatus.Active;
                await dbContext.SaveChangesAsync();
            }

            return existing.Id;
        }

        var now = DateTimeOffset.UtcNow;
        var project = new Project
        {
            OwnerUserAccountId = ownerUserAccountId,
            Slug = "velvet",
            Name = "Velvet",
            Summary = "General patronage for Velvet publication.",
            Status = ProjectStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync();
        return project.Id;
    }

    private static HttpRequestMessage CreateSignedHeimdallRequest(string payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/heimdall/patron-support/events");
        request.Headers.Add("X-Heimdall-Signature-256", ComputeSignature("test-heimdall-intake-secret", payload));
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        return request;
    }

    private async Task EnsureBifrostIdentityRegisteredAsync(string identity, string heimdallAccountId = "")
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var normalizedIdentity = BifrostIdentityService.NormalizeIdentity(identity);
        if (await dbContext.UserAccounts.AnyAsync(x => x.NormalizedBifrostIdentity == normalizedIdentity))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        dbContext.UserAccounts.Add(new UserAccount
        {
            BifrostIdentity = normalizedIdentity,
            NormalizedBifrostIdentity = normalizedIdentity,
            HeimdallAccountId = heimdallAccountId,
            DisplayName = normalizedIdentity,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            Membership = new Bifrost.Web.Domain.Membership
            {
                Status = MembershipStatus.Authenticated,
                CreatedAtUtc = now,
                Notes = "Registered test Bifrost identity"
            }
        });
        await dbContext.SaveChangesAsync();
    }

    private sealed class BridgeActionHttpResult
    {
        public Guid Id { get; init; }

        public string Status { get; init; } = string.Empty;
    }

    private sealed class AgentDispatchRunHttpResult
    {
        public Guid Id { get; init; }

        public string Status { get; init; } = string.Empty;
    }

    private sealed class AgentTransportReceiptHttpResult
    {
        public Guid Id { get; init; }

        public string RequestId { get; init; } = string.Empty;

        public string ActivityKind { get; init; } = string.Empty;
    }

    private sealed class GovernanceActivityReceiptHttpResult
    {
        public Guid Id { get; init; }

        public string TopicId { get; init; } = string.Empty;

        public string ActivityKind { get; init; } = string.Empty;
    }
}
