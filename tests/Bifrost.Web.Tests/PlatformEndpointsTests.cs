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

        using var response = await client.GetAsync("/patronage/velvet/checkout?tier=velvet-room");

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

        using var request = new HttpRequestMessage(HttpMethod.Get, "/patronage/velvet/checkout?tier=velvet-room");
        request.Headers.Add("X-Test-Anonymous", "1");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(
            "/auth/sign-in?returnUrl=%2Fpatronage%2Fvelvet%2Fcheckout%3Ftier%3Dvelvet-room",
            response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Sign_in_chooser_lists_supported_transport_providers()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/auth/sign-in?returnUrl=%2Fpatronage%2Fvelvet%2Fcheckout%3Ftier%3Dvelvet-room");

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
        using (var setupScope = _factory.Services.CreateScope())
        {
            var setupContext = setupScope.ServiceProvider.GetRequiredService<BifrostDbContext>();
            patronageAccountId = setupContext.UserAccounts.Single(x => x.NormalizedGitHubLogin == "TEST-ADMIN").Id;
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
                        ["model"] = "deru",
                        ["tier"] = "after-hours-bundle",
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
        Assert.Equal(39m, supportEvent.Amount);
        Assert.Equal("USD", supportEvent.CurrencyCode);
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
        Assert.Contains("/eve/governance/commands", json);
    }

    [Fact]
    public async Task Eve_governance_vote_command_uses_canonical_motion_commit_path()
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

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var surfaceJson = await response.Content.ReadAsStringAsync();
        Assert.Contains("bifrost-motion-verse", surfaceJson);

        using var verificationScope = _factory.Services.CreateScope();
        var verificationContext = verificationScope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var vote = verificationContext.Votes.Single(x => x.MotionId == motionId);
        Assert.Equal(VoteChoice.For, vote.Choice);
        Assert.Equal("Cast from Eve.", vote.Comment);
        Assert.Contains(verificationContext.AuditEvents, x => x.Action == "motion.voted");
    }

    [Fact]
    public async Task Local_bridge_token_can_authorize_and_receipt_agent_github_action()
    {
        using var client = _factory.CreateClient();
        await PostTransportReceiptAsync(client, new
        {
            requestId = "req_bridge_123",
            title = "Queue the GitHub bridge hardening pass",
            targetRepoName = "Bifrost",
            targetRepositoryFullName = "GameCult/Bifrost",
            targetAgentIdentity = "nibu",
            activityKind = "Claimed",
            status = "claimed",
            actorName = "bifrost-dispatcher",
            note = "Request claimed for Bifrost."
        });

        var requestPayload = JsonSerializer.Serialize(new
        {
            actorKind = "Agent",
            actorName = "nibu",
            targetSurface = "GitHub",
            actionKind = "GitHubDraftPullRequest",
            targetRepositoryFullName = "GameCult/Bifrost",
            targetLocator = "pulls",
            sourceKind = "bifrost_agent_transport_request",
            sourceId = "req_bridge_123",
            authorityReference = "dispatch-approved",
            title = "Draft motion implementation PR",
            summary = "Open a draft PR for the approved topic."
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/bridge/actions/request");
        request.Headers.Add("X-Bifrost-Bridge-Token", "test-bridge-token");
        request.Content = new StringContent(requestPayload, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var action = await response.Content.ReadFromJsonAsync<BridgeActionHttpResult>();
        Assert.NotNull(action);
        Assert.Equal("Authorized", action.Status);

        using var startRequest = new HttpRequestMessage(HttpMethod.Post, $"/bridge/actions/{action.Id}/start");
        startRequest.Headers.Add("X-Bifrost-Bridge-Token", "test-bridge-token");
        using var startResponse = await client.SendAsync(startRequest);
        Assert.Equal(HttpStatusCode.Accepted, startResponse.StatusCode);

        var completionPayload = JsonSerializer.Serialize(new
        {
            receiptUrl = "https://github.com/GameCult/Bifrost/pull/99",
            externalReceiptId = "99",
            receiptPayload = "{\"pr\":99}"
        });
        using var completeRequest = new HttpRequestMessage(HttpMethod.Post, $"/bridge/actions/{action.Id}/complete");
        completeRequest.Headers.Add("X-Bifrost-Bridge-Token", "test-bridge-token");
        completeRequest.Content = new StringContent(completionPayload, Encoding.UTF8, "application/json");

        using var completeResponse = await client.SendAsync(completeRequest);

        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var savedAction = dbContext.BridgeActions.Single(x => x.Id == action.Id);

        Assert.Equal(BridgeActionStatus.Completed, savedAction.Status);
        Assert.Equal("https://github.com/GameCult/Bifrost/pull/99", savedAction.ReceiptUrl);
        Assert.Equal("99", savedAction.ExternalReceiptId);
        Assert.Contains(dbContext.AuditEvents, x => x.EntityType == nameof(BridgeAction) && x.Action == "bridge.completed");
    }

    [Fact]
    public async Task Agent_github_action_without_provenance_is_denied_and_recorded()
    {
        using var client = _factory.CreateClient();

        var requestPayload = JsonSerializer.Serialize(new
        {
            actorKind = "Agent",
            actorName = "nibu",
            targetSurface = "GitHub",
            actionKind = "GitHubDraftPullRequest",
            targetRepositoryFullName = "GameCult/Bifrost",
            targetLocator = "pulls",
            title = "Unproven draft",
            summary = "This should not pass policy."
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/bridge/actions/request");
        request.Headers.Add("X-Bifrost-Bridge-Token", "test-bridge-token");
        request.Content = new StringContent(requestPayload, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var action = await response.Content.ReadFromJsonAsync<BridgeActionHttpResult>();
        Assert.NotNull(action);
        Assert.Equal("Denied", action.Status);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var savedAction = dbContext.BridgeActions.Single(x => x.Id == action.Id);

        Assert.Equal(BridgeActionStatus.Denied, savedAction.Status);
        Assert.Contains("must cite", savedAction.PolicyDecision, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(dbContext.AuditEvents, x => x.EntityType == nameof(BridgeAction) && x.Action == "bridge.denied");
    }

    [Fact]
    public async Task Agent_github_action_with_unknown_bifrost_request_is_denied_and_recorded()
    {
        using var client = _factory.CreateClient();

        var requestPayload = JsonSerializer.Serialize(new
        {
            actorKind = "Agent",
            actorName = "nibu",
            targetSurface = "GitHub",
            actionKind = "GitHubDraftPullRequest",
            targetRepositoryFullName = "GameCult/Bifrost",
            targetLocator = "pulls",
            sourceKind = "bifrost_agent_transport_request",
            sourceId = "req_missing_123",
            authorityReference = "dispatch-approved",
            title = "Unbacked request bridge action",
            summary = "This should not pass policy."
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/bridge/actions/request");
        request.Headers.Add("X-Bifrost-Bridge-Token", "test-bridge-token");
        request.Content = new StringContent(requestPayload, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var action = await response.Content.ReadFromJsonAsync<BridgeActionHttpResult>();
        Assert.NotNull(action);
        Assert.Equal("Denied", action.Status);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var savedAction = dbContext.BridgeActions.Single(x => x.Id == action.Id);

        Assert.Equal(BridgeActionStatus.Denied, savedAction.Status);
        Assert.Contains("unknown request", savedAction.PolicyDecision, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Agent_github_action_with_mismatched_bifrost_request_repo_is_denied_and_recorded()
    {
        using var client = _factory.CreateClient();
        await PostTransportReceiptAsync(client, new
        {
            requestId = "req_bridge_mismatch_123",
            title = "Queue the VoidBot bridge hardening pass",
            targetRepoName = "VoidBot",
            targetRepositoryFullName = "GameCult/VoidBot",
            targetAgentIdentity = "nibu",
            activityKind = "Claimed",
            status = "claimed",
            actorName = "bifrost-dispatcher",
            note = "Request claimed for VoidBot."
        });

        var requestPayload = JsonSerializer.Serialize(new
        {
            actorKind = "Agent",
            actorName = "nibu",
            targetSurface = "GitHub",
            actionKind = "GitHubDraftPullRequest",
            targetRepositoryFullName = "GameCult/Bifrost",
            targetLocator = "pulls",
            sourceKind = "bifrost_agent_transport_request",
            sourceId = "req_bridge_mismatch_123",
            authorityReference = "dispatch-approved",
            title = "Repo mismatch bridge action",
            summary = "This should not pass policy."
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/bridge/actions/request");
        request.Headers.Add("X-Bifrost-Bridge-Token", "test-bridge-token");
        request.Content = new StringContent(requestPayload, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var action = await response.Content.ReadFromJsonAsync<BridgeActionHttpResult>();
        Assert.NotNull(action);
        Assert.Equal("Denied", action.Status);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var savedAction = dbContext.BridgeActions.Single(x => x.Id == action.Id);

        Assert.Equal(BridgeActionStatus.Denied, savedAction.Status);
        Assert.Contains("target repository does not match", savedAction.PolicyDecision, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Agent_github_action_with_unapproved_governance_topic_is_denied_and_recorded()
    {
        using var client = _factory.CreateClient();
        await PostGovernanceReceiptAsync(client, new
        {
            topicId = "topic_open_only_123",
            commentId = "comment_open_only_123",
            dispatchRequestId = string.Empty,
            title = "Open topic only",
            jurisdictionRepoName = "Bifrost",
            jurisdictionAgentIdentity = "nibu",
            activityKind = "TopicOpened",
            actorKind = "face",
            actorName = "nibu",
            note = "Topic opened."
        });

        var requestPayload = JsonSerializer.Serialize(new
        {
            actorKind = "Agent",
            actorName = "nibu",
            targetSurface = "GitHub",
            actionKind = "GitHubDraftPullRequest",
            targetRepositoryFullName = "GameCult/Bifrost",
            targetLocator = "pulls",
            sourceKind = "governance_topic",
            sourceId = "topic_open_only_123",
            authorityReference = "dispatch-approved",
            title = "Open-only topic bridge action",
            summary = "This should not pass policy."
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/bridge/actions/request");
        request.Headers.Add("X-Bifrost-Bridge-Token", "test-bridge-token");
        request.Content = new StringContent(requestPayload, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var action = await response.Content.ReadFromJsonAsync<BridgeActionHttpResult>();
        Assert.NotNull(action);
        Assert.Equal("Denied", action.Status);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var savedAction = dbContext.BridgeActions.Single(x => x.Id == action.Id);

        Assert.Equal(BridgeActionStatus.Denied, savedAction.Status);
        Assert.Contains("not been approved or promoted", savedAction.PolicyDecision, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Agent_github_action_with_approved_governance_topic_is_authorized()
    {
        using var client = _factory.CreateClient();
        await PostGovernanceReceiptAsync(client, new
        {
            topicId = "topic_approved_123",
            commentId = "comment_approved_123",
            dispatchRequestId = string.Empty,
            title = "Approved governance topic",
            jurisdictionRepoName = "Bifrost",
            jurisdictionAgentIdentity = "nibu",
            activityKind = "TopicApproved",
            actorKind = "face",
            actorName = "nibu",
            note = "Topic approved."
        });

        var requestPayload = JsonSerializer.Serialize(new
        {
            actorKind = "Agent",
            actorName = "nibu",
            targetSurface = "GitHub",
            actionKind = "GitHubDraftPullRequest",
            targetRepositoryFullName = "GameCult/Bifrost",
            targetLocator = "pulls",
            sourceKind = "bifrost_governance_topic",
            sourceId = "topic_approved_123",
            authorityReference = "dispatch-approved",
            title = "Approved topic bridge action",
            summary = "This should pass policy."
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/bridge/actions/request");
        request.Headers.Add("X-Bifrost-Bridge-Token", "test-bridge-token");
        request.Content = new StringContent(requestPayload, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var action = await response.Content.ReadFromJsonAsync<BridgeActionHttpResult>();
        Assert.NotNull(action);
        Assert.Equal("Authorized", action.Status);
    }

    [Fact]
    public async Task Persona_reddit_action_without_bifrost_identity_or_heimdall_reference_is_denied()
    {
        using var client = _factory.CreateClient();

        var requestPayload = JsonSerializer.Serialize(new
        {
            actorKind = "Persona",
            actorName = "Epiphany Persona",
            targetSurface = "Reddit",
            actionKind = "RedditPost",
            targetLocator = "r/GameCultOrg",
            sourceKind = "epiphany_persona_reddit",
            sourceId = "persona-speech-audit-missing-identity",
            authorityReference = "epiphany.persona_speech_audit",
            title = "Persona Reddit thread",
            summary = "This should not pass without Bifrost identity plus Heimdall reference."
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/bridge/actions/request");
        request.Headers.Add("X-Bifrost-Bridge-Token", "test-bridge-token");
        request.Content = new StringContent(requestPayload, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var action = await response.Content.ReadFromJsonAsync<BridgeActionHttpResult>();
        Assert.NotNull(action);
        Assert.Equal("Denied", action.Status);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var savedAction = dbContext.BridgeActions.Single(x => x.Id == action.Id);

        Assert.Equal(BridgeActionStatus.Denied, savedAction.Status);
        Assert.Contains("Bifrost identity", savedAction.PolicyDecision, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Persona_discord_action_requires_heimdall_reference_after_bifrost_identity()
    {
        using var client = _factory.CreateClient();
        await EnsureBifrostIdentityRegisteredAsync("epiphany.Persona", "heimdall-account-epiphany-persona");

        var requestPayload = JsonSerializer.Serialize(new
        {
            actorKind = "Persona",
            actorName = "Epiphany Persona",
            targetSurface = "Discord",
            actionKind = "DiscordPost",
            targetLocator = "channel/1501196543150264332",
            sourceKind = "epiphany_persona_speech",
            sourceId = "persona-speech-audit-missing-heimdall",
            authorityReference = "epiphany.persona_speech_audit",
            bifrostIdentity = "epiphany.Persona",
            title = "Persona Discord post",
            summary = "This should not pass without an outside-account capability reference."
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/bridge/actions/request");
        request.Headers.Add("X-Bifrost-Bridge-Token", "test-bridge-token");
        request.Content = new StringContent(requestPayload, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var action = await response.Content.ReadFromJsonAsync<BridgeActionHttpResult>();
        Assert.NotNull(action);
        Assert.Equal("Denied", action.Status);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var savedAction = dbContext.BridgeActions.Single(x => x.Id == action.Id);

        Assert.Equal(BridgeActionStatus.Denied, savedAction.Status);
        Assert.Contains("Heimdall", savedAction.PolicyDecision, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Persona_discord_action_with_unlinked_heimdall_account_is_denied()
    {
        using var client = _factory.CreateClient();
        var identity = $"native.{Guid.NewGuid():N}";
        await EnsureBifrostIdentityRegisteredAsync(identity);

        var requestPayload = JsonSerializer.Serialize(new
        {
            actorKind = "Persona",
            actorName = "Native Persona",
            targetSurface = "Discord",
            actionKind = "DiscordPost",
            targetLocator = "channel/1501196543150264332",
            sourceKind = "epiphany_persona_speech",
            sourceId = "persona-speech-audit-unlinked-heimdall",
            authorityReference = "epiphany.persona_speech_audit",
            bifrostIdentity = identity,
            heimdallCapabilityReference = "heimdall:discord:capability:native-persona",
            title = "Persona Discord post",
            summary = "This should not pass until the Bifrost identity is linked to Heimdall."
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/bridge/actions/request");
        request.Headers.Add("X-Bifrost-Bridge-Token", "test-bridge-token");
        request.Content = new StringContent(requestPayload, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var action = await response.Content.ReadFromJsonAsync<BridgeActionHttpResult>();
        Assert.NotNull(action);
        Assert.Equal("Denied", action.Status);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var savedAction = dbContext.BridgeActions.Single(x => x.Id == action.Id);

        Assert.Equal(BridgeActionStatus.Denied, savedAction.Status);
        Assert.Contains("linked to a Heimdall account", savedAction.PolicyDecision, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Persona_discord_action_with_unregistered_bifrost_identity_is_denied()
    {
        using var client = _factory.CreateClient();
        var identity = $"unregistered.{Guid.NewGuid():N}";

        var requestPayload = JsonSerializer.Serialize(new
        {
            actorKind = "Persona",
            actorName = "Epiphany Persona",
            targetSurface = "Discord",
            actionKind = "DiscordPost",
            targetLocator = "channel/1501196543150264332",
            sourceKind = "epiphany_persona_speech",
            sourceId = "persona-speech-audit-unregistered-identity",
            authorityReference = "epiphany.persona_speech_audit",
            bifrostIdentity = identity,
            heimdallCapabilityReference = "heimdall:discord:capability:epiphany-persona",
            title = "Persona Discord post",
            summary = "This should not pass with an unregistered Bifrost identity."
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/bridge/actions/request");
        request.Headers.Add("X-Bifrost-Bridge-Token", "test-bridge-token");
        request.Content = new StringContent(requestPayload, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var action = await response.Content.ReadFromJsonAsync<BridgeActionHttpResult>();
        Assert.NotNull(action);
        Assert.Equal("Denied", action.Status);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var savedAction = dbContext.BridgeActions.Single(x => x.Id == action.Id);

        Assert.Equal(BridgeActionStatus.Denied, savedAction.Status);
        Assert.Contains("registered Bifrost identity", savedAction.PolicyDecision, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Persona_reddit_action_with_bifrost_identity_and_heimdall_reference_is_authorized_and_stored()
    {
        using var client = _factory.CreateClient();
        await EnsureBifrostIdentityRegisteredAsync("epiphany.Persona", "heimdall-account-epiphany-persona");

        var requestPayload = JsonSerializer.Serialize(new
        {
            actorKind = "Persona",
            actorName = "Epiphany Persona",
            targetSurface = "Reddit",
            actionKind = "RedditPost",
            targetLocator = "r/GameCultOrg",
            sourceKind = "epiphany_persona_reddit",
            sourceId = "persona-speech-audit-authorized",
            authorityReference = "epiphany.persona_speech_audit",
            bifrostIdentity = "epiphany.Persona",
            heimdallCapabilityReference = "heimdall:reddit:capability:epiphany-persona",
            epiphanyRunId = "epiphany-run-identity-gate",
            epiphanyLaneId = "Persona",
            epiphanyAgentIdentity = "epiphany.Persona",
            title = "Persona Reddit thread",
            summary = "This should pass with Bifrost identity and Heimdall reference."
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/bridge/actions/request");
        request.Headers.Add("X-Bifrost-Bridge-Token", "test-bridge-token");
        request.Content = new StringContent(requestPayload, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var action = await response.Content.ReadFromJsonAsync<BridgeActionHttpResult>();
        Assert.NotNull(action);
        Assert.Equal("Authorized", action.Status);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var savedAction = dbContext.BridgeActions.Single(x => x.Id == action.Id);

        Assert.Equal(BridgeActionStatus.Authorized, savedAction.Status);
        Assert.Equal("epiphany.Persona", savedAction.BifrostIdentity);
        Assert.Equal("heimdall:reddit:capability:epiphany-persona", savedAction.HeimdallCapabilityReference);
        Assert.Equal("epiphany-run-identity-gate", savedAction.EpiphanyRunId);
        Assert.Equal("Persona", savedAction.EpiphanyLaneId);
        Assert.Equal("epiphany.Persona", savedAction.EpiphanyAgentIdentity);
    }

    [Fact]
    public async Task Persona_other_surface_action_without_bifrost_identity_is_denied()
    {
        using var client = _factory.CreateClient();

        var requestPayload = JsonSerializer.Serialize(new
        {
            actorKind = "Persona",
            actorName = "Epiphany Persona",
            targetSurface = "Other",
            actionKind = "Other",
            targetLocator = "future://public-surface/channel",
            sourceKind = "epiphany_persona_public_surface",
            sourceId = "persona-speech-audit-future-surface-missing-identity",
            authorityReference = "epiphany.persona_speech_audit",
            title = "Persona future public surface post",
            summary = "This future outside-world crossing must not pass without identity provenance."
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/bridge/actions/request");
        request.Headers.Add("X-Bifrost-Bridge-Token", "test-bridge-token");
        request.Content = new StringContent(requestPayload, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var action = await response.Content.ReadFromJsonAsync<BridgeActionHttpResult>();
        Assert.NotNull(action);
        Assert.Equal("Denied", action.Status);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var savedAction = dbContext.BridgeActions.Single(x => x.Id == action.Id);

        Assert.Equal(BridgeActionStatus.Denied, savedAction.Status);
        Assert.Contains("outside-world", savedAction.PolicyDecision, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Bifrost identity", savedAction.PolicyDecision, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Persona_other_surface_action_with_bifrost_identity_and_heimdall_reference_is_authorized()
    {
        using var client = _factory.CreateClient();
        await EnsureBifrostIdentityRegisteredAsync("epiphany.Persona", "heimdall-account-epiphany-persona");

        var requestPayload = JsonSerializer.Serialize(new
        {
            actorKind = "Persona",
            actorName = "Epiphany Persona",
            targetSurface = "Other",
            actionKind = "Other",
            targetLocator = "future://public-surface/channel",
            sourceKind = "epiphany_persona_public_surface",
            sourceId = "persona-speech-audit-future-surface-authorized",
            authorityReference = "epiphany.persona_speech_audit",
            bifrostIdentity = "epiphany.Persona",
            heimdallCapabilityReference = "heimdall:future-surface:capability:epiphany-persona",
            epiphanyRunId = "epiphany-run-future-surface",
            epiphanyLaneId = "Persona",
            epiphanyAgentIdentity = "epiphany.Persona",
            title = "Persona future public surface post",
            summary = "This future outside-world crossing is allowed only with Bifrost and Heimdall provenance."
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/bridge/actions/request");
        request.Headers.Add("X-Bifrost-Bridge-Token", "test-bridge-token");
        request.Content = new StringContent(requestPayload, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var action = await response.Content.ReadFromJsonAsync<BridgeActionHttpResult>();
        Assert.NotNull(action);
        Assert.Equal("Authorized", action.Status);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var savedAction = dbContext.BridgeActions.Single(x => x.Id == action.Id);

        Assert.Equal(BridgeActionStatus.Authorized, savedAction.Status);
        Assert.Equal(BridgeTargetSurface.Other, savedAction.TargetSurface);
        Assert.Equal("epiphany.Persona", savedAction.BifrostIdentity);
        Assert.Equal("heimdall:future-surface:capability:epiphany-persona", savedAction.HeimdallCapabilityReference);
        Assert.Equal("epiphany-run-future-surface", savedAction.EpiphanyRunId);
        Assert.Equal("Persona", savedAction.EpiphanyLaneId);
        Assert.Equal("epiphany.Persona", savedAction.EpiphanyAgentIdentity);
    }

    [Fact]
    public async Task Local_bridge_token_can_record_dispatch_run_lifecycle()
    {
        using var client = _factory.CreateClient();
        _ = await client.GetAsync("/App");
        await PostTransportReceiptAsync(client, new
        {
            requestId = "req_dispatch_123",
            title = "Queue the GitHub bridge hardening pass",
            targetRepoName = "Bifrost",
            targetRepositoryFullName = "GameCult/Bifrost",
            targetAgentIdentity = "nibu",
            activityKind = "Claimed",
            status = "claimed",
            actorName = "bifrost-dispatcher",
            note = "Request claimed for Bifrost."
        });

        var startPayload = JsonSerializer.Serialize(new
        {
            requestId = "req_dispatch_123",
            targetRepoName = "Bifrost",
            targetRepositoryFullName = "GameCult/Bifrost",
            targetAgentIdentity = "nibu",
            launchMode = "app-server",
            workerProcessId = 4242,
            threadId = "thread_1",
            turnId = "turn_1",
            logPath = "E:/Projects/Bifrost/.bifrost/agent-dispatch/req_dispatch_123/codex.log",
            resultPath = "E:/Projects/Bifrost/.bifrost/agent-dispatch/req_dispatch_123/result.json",
            note = "Codex app-server turn started."
        });

        using var startRequest = new HttpRequestMessage(HttpMethod.Post, "/dispatch/runs/start");
        startRequest.Headers.Add("X-Bifrost-Bridge-Token", "test-bridge-token");
        startRequest.Content = new StringContent(startPayload, Encoding.UTF8, "application/json");

        using var startResponse = await client.SendAsync(startRequest);

        Assert.Equal(HttpStatusCode.Accepted, startResponse.StatusCode);
        var started = await startResponse.Content.ReadFromJsonAsync<AgentDispatchRunHttpResult>();
        Assert.NotNull(started);
        Assert.Equal("Started", started.Status);

        var completePayload = JsonSerializer.Serialize(new
        {
            status = "Completed",
            threadId = "thread_1",
            turnId = "turn_1",
            resultPath = "E:/Projects/Bifrost/.bifrost/agent-dispatch/req_dispatch_123/result.json",
            note = "Codex app turn completed for thread_1/turn_1."
        });

        using var completeRequest = new HttpRequestMessage(HttpMethod.Post, $"/dispatch/runs/{started.Id}/complete");
        completeRequest.Headers.Add("X-Bifrost-Bridge-Token", "test-bridge-token");
        completeRequest.Content = new StringContent(completePayload, Encoding.UTF8, "application/json");

        using var completeResponse = await client.SendAsync(completeRequest);

        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var savedRun = dbContext.AgentDispatchRuns.Single(x => x.Id == started.Id);

        Assert.Equal(AgentDispatchRunStatus.Completed, savedRun.Status);
        Assert.Equal("req_dispatch_123", savedRun.RequestId);
        Assert.Equal("turn_1", savedRun.TurnId);
        Assert.Null(savedRun.StartedByUserAccountId);
        Assert.Contains(dbContext.AuditEvents, x => x.EntityType == nameof(AgentDispatchRun) && x.Action == "agent-dispatch.completed");
    }

    [Fact]
    public async Task Local_bridge_token_can_record_dispatch_run_failure()
    {
        using var client = _factory.CreateClient();
        _ = await client.GetAsync("/App");
        await PostTransportReceiptAsync(client, new
        {
            requestId = "req_dispatch_fail_123",
            title = "Queue the GitHub bridge hardening pass",
            targetRepoName = "Bifrost",
            targetRepositoryFullName = "GameCult/Bifrost",
            targetAgentIdentity = "nibu",
            activityKind = "Claimed",
            status = "claimed",
            actorName = "bifrost-dispatcher",
            note = "Request claimed for Bifrost."
        });

        var startPayload = JsonSerializer.Serialize(new
        {
            requestId = "req_dispatch_fail_123",
            targetRepoName = "Bifrost",
            targetRepositoryFullName = "GameCult/Bifrost",
            targetAgentIdentity = "nibu",
            launchMode = "app-server",
            workerProcessId = 4242,
            threadId = string.Empty,
            turnId = string.Empty,
            logPath = "E:/Projects/Bifrost/.bifrost/agent-dispatch/req_dispatch_fail_123/codex.log",
            resultPath = "E:/Projects/Bifrost/.bifrost/agent-dispatch/req_dispatch_fail_123/result.json",
            note = "Codex app-server launch started."
        });

        using var startRequest = new HttpRequestMessage(HttpMethod.Post, "/dispatch/runs/start");
        startRequest.Headers.Add("X-Bifrost-Bridge-Token", "test-bridge-token");
        startRequest.Content = new StringContent(startPayload, Encoding.UTF8, "application/json");

        using var startResponse = await client.SendAsync(startRequest);

        Assert.Equal(HttpStatusCode.Accepted, startResponse.StatusCode);
        var started = await startResponse.Content.ReadFromJsonAsync<AgentDispatchRunHttpResult>();
        Assert.NotNull(started);

        var failPayload = JsonSerializer.Serialize(new
        {
            threadId = string.Empty,
            turnId = string.Empty,
            resultPath = "E:/Projects/Bifrost/.bifrost/agent-dispatch/req_dispatch_fail_123/result.json",
            note = "Codex dispatch failed.",
            error = "Codex app-server thread/start returned no thread id."
        });

        using var failRequest = new HttpRequestMessage(HttpMethod.Post, $"/dispatch/runs/{started.Id}/fail");
        failRequest.Headers.Add("X-Bifrost-Bridge-Token", "test-bridge-token");
        failRequest.Content = new StringContent(failPayload, Encoding.UTF8, "application/json");

        using var failResponse = await client.SendAsync(failRequest);

        Assert.Equal(HttpStatusCode.OK, failResponse.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var savedRun = dbContext.AgentDispatchRuns.Single(x => x.Id == started.Id);

        Assert.Equal(AgentDispatchRunStatus.Failed, savedRun.Status);
        Assert.Equal("Codex app-server thread/start returned no thread id.", savedRun.Error);
        Assert.Null(savedRun.StartedByUserAccountId);
        Assert.Contains(dbContext.AuditEvents, x => x.EntityType == nameof(AgentDispatchRun) && x.Action == "agent-dispatch.failed");
    }

    [Fact]
    public async Task Local_bridge_token_can_record_transport_receipt()
    {
        using var client = _factory.CreateClient();
        _ = await client.GetAsync("/App");

        var payload = JsonSerializer.Serialize(new
        {
            requestId = "req_transport_123",
            title = "Queue the GitHub bridge hardening pass",
            targetRepoName = "Bifrost",
            targetRepositoryFullName = "GameCult/Bifrost",
            targetAgentIdentity = "nibu",
            activityKind = "Claimed",
            status = "claimed",
            actorName = "bifrost-dispatcher",
            note = "Request claimed for Bifrost."
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/transport/receipts");
        request.Headers.Add("X-Bifrost-Bridge-Token", "test-bridge-token");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<AgentTransportReceiptHttpResult>();
        Assert.NotNull(receipt);
        Assert.Equal("req_transport_123", receipt.RequestId);
        Assert.Equal("Claimed", receipt.ActivityKind);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var savedReceipt = dbContext.AgentTransportReceipts.Single(x => x.Id == receipt.Id);

        Assert.Equal("Claimed", savedReceipt.ActivityKind.ToString());
        Assert.Equal("bifrost-dispatcher", savedReceipt.ActorName);
        Assert.Null(savedReceipt.ActorUserAccountId);
        Assert.Contains(dbContext.AuditEvents, x => x.EntityType == nameof(AgentTransportReceipt) && x.Action == "agent-transport.claimed");
    }

    [Fact]
    public async Task Local_bridge_token_can_record_governance_receipt()
    {
        using var client = _factory.CreateClient();
        _ = await client.GetAsync("/App");
        await PostTransportReceiptAsync(client, new
        {
            requestId = "req_transport_123",
            title = "Queue the GitHub bridge hardening pass",
            targetRepoName = "Bifrost",
            targetRepositoryFullName = "GameCult/Bifrost",
            targetAgentIdentity = "nibu",
            activityKind = "Queued",
            status = "queued",
            actorName = "bifrost-dispatcher",
            note = "Request queued for Bifrost."
        });

        var payload = JsonSerializer.Serialize(new
        {
            topicId = "topic_123",
            commentId = "comment_123",
            dispatchRequestId = "req_transport_123",
            title = "Queue the GitHub bridge hardening pass",
            jurisdictionRepoName = "Bifrost",
            jurisdictionAgentIdentity = "nibu",
            activityKind = "TopicPromoted",
            actorKind = "face",
            actorName = "nibu",
            note = "Governance topic promoted to update request req_transport_123."
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/governance/receipts");
        request.Headers.Add("X-Bifrost-Bridge-Token", "test-bridge-token");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<GovernanceActivityReceiptHttpResult>();
        Assert.NotNull(receipt);
        Assert.Equal("topic_123", receipt.TopicId);
        Assert.Equal("TopicPromoted", receipt.ActivityKind);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var savedReceipt = dbContext.GovernanceActivityReceipts.Single(x => x.Id == receipt.Id);

        Assert.Equal("TopicPromoted", savedReceipt.ActivityKind.ToString());
        Assert.Equal("nibu", savedReceipt.ActorName);
        Assert.Null(savedReceipt.ActorUserAccountId);
        Assert.Contains(dbContext.AuditEvents, x => x.EntityType == nameof(GovernanceActivityReceipt) && x.Action == "governance.topic-promoted");
    }

    [Fact]
    public async Task Active_member_session_cannot_record_dispatch_run_without_local_bridge_token()
    {
        using var client = _factory.CreateClient();
        _ = await client.GetAsync("/App");

        var payload = JsonSerializer.Serialize(new
        {
            requestId = "req_dispatch_member_123",
            targetRepoName = "Bifrost",
            targetRepositoryFullName = "GameCult/Bifrost",
            targetAgentIdentity = "nibu",
            launchMode = "app-server",
            workerProcessId = 4242,
            threadId = "thread_member",
            turnId = "turn_member",
            logPath = "E:/Projects/Bifrost/.bifrost/agent-dispatch/req_dispatch_member_123/codex.log",
            resultPath = "E:/Projects/Bifrost/.bifrost/agent-dispatch/req_dispatch_member_123/result.json",
            note = "Should be rejected without the local bridge token."
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/dispatch/runs/start");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Dispatch_run_without_request_lane_receipt_is_rejected()
    {
        using var client = _factory.CreateClient();

        var payload = JsonSerializer.Serialize(new
        {
            requestId = "req_dispatch_missing_123",
            targetRepoName = "Bifrost",
            targetRepositoryFullName = "GameCult/Bifrost",
            targetAgentIdentity = "nibu",
            launchMode = "app-server",
            workerProcessId = 4242,
            threadId = "thread_missing",
            turnId = "turn_missing",
            logPath = "E:/Projects/Bifrost/.bifrost/agent-dispatch/req_dispatch_missing_123/codex.log",
            resultPath = "E:/Projects/Bifrost/.bifrost/agent-dispatch/req_dispatch_missing_123/result.json",
            note = "Should fail because the request lane has no receipt."
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/dispatch/runs/start");
        request.Headers.Add("X-Bifrost-Bridge-Token", "test-bridge-token");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("existing request-lane receipt", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Governance_promotion_receipt_requires_known_dispatch_request()
    {
        using var client = _factory.CreateClient();

        var payload = JsonSerializer.Serialize(new
        {
            topicId = "topic_unknown_dispatch_123",
            commentId = "comment_unknown_dispatch_123",
            dispatchRequestId = "req_unknown_transport_123",
            title = "Promote an unknown dispatch request",
            jurisdictionRepoName = "Bifrost",
            jurisdictionAgentIdentity = "nibu",
            activityKind = "TopicPromoted",
            actorKind = "face",
            actorName = "nibu",
            note = "Should fail because the dispatch request is unknown."
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/governance/receipts");
        request.Headers.Add("X-Bifrost-Bridge-Token", "test-bridge-token");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("unknown dispatch request", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Request_lane_receipt_cannot_change_repo_identity_for_existing_request()
    {
        using var client = _factory.CreateClient();
        await PostTransportReceiptAsync(client, new
        {
            requestId = "req_transport_conflict_123",
            title = "Queue the GitHub bridge hardening pass",
            targetRepoName = "Bifrost",
            targetRepositoryFullName = "GameCult/Bifrost",
            targetAgentIdentity = "nibu",
            activityKind = "Queued",
            status = "queued",
            actorName = "bifrost-dispatcher",
            note = "Request queued for Bifrost."
        });

        var conflictPayload = JsonSerializer.Serialize(new
        {
            requestId = "req_transport_conflict_123",
            title = "Queue the GitHub bridge hardening pass",
            targetRepoName = "VoidBot",
            targetRepositoryFullName = "GameCult/VoidBot",
            targetAgentIdentity = "nibu",
            activityKind = "Claimed",
            status = "claimed",
            actorName = "bifrost-dispatcher",
            note = "This should not be allowed to drift."
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/transport/receipts");
        request.Headers.Add("X-Bifrost-Bridge-Token", "test-bridge-token");
        request.Content = new StringContent(conflictPayload, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("target repo", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Active_member_session_cannot_record_transport_receipt_without_local_bridge_token()
    {
        using var client = _factory.CreateClient();
        _ = await client.GetAsync("/App");

        var payload = JsonSerializer.Serialize(new
        {
            requestId = "req_transport_member_123",
            title = "Member should not mint runtime receipts",
            targetRepoName = "Bifrost",
            targetRepositoryFullName = "GameCult/Bifrost",
            targetAgentIdentity = "nibu",
            activityKind = "Claimed",
            status = "claimed",
            actorName = "test-admin",
            note = "Should be rejected without the local bridge token."
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/transport/receipts");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Active_member_session_cannot_record_governance_receipt_without_local_bridge_token()
    {
        using var client = _factory.CreateClient();
        _ = await client.GetAsync("/App");

        var payload = JsonSerializer.Serialize(new
        {
            topicId = "topic_member_123",
            commentId = "comment_member_123",
            dispatchRequestId = "req_transport_member_123",
            title = "Member should not mint governance runtime receipts",
            jurisdictionRepoName = "Bifrost",
            jurisdictionAgentIdentity = "nibu",
            activityKind = "TopicPromoted",
            actorKind = "member",
            actorName = "test-admin",
            note = "Should be rejected without the local bridge token."
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/governance/receipts");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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

    private static HttpRequestMessage CreateSignedHeimdallRequest(string payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/heimdall/patron-support/events");
        request.Headers.Add("X-Heimdall-Signature-256", ComputeSignature("test-heimdall-intake-secret", payload));
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task PostTransportReceiptAsync(HttpClient client, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/transport/receipts");
        request.Headers.Add("X-Bifrost-Bridge-Token", "test-bridge-token");
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.Accepted,
            $"Expected transport receipt seed to succeed but got {(int)response.StatusCode}: {body}");
    }

    private static async Task PostGovernanceReceiptAsync(HttpClient client, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/governance/receipts");
        request.Headers.Add("X-Bifrost-Bridge-Token", "test-bridge-token");
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.Accepted,
            $"Expected governance receipt seed to succeed but got {(int)response.StatusCode}: {body}");
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
