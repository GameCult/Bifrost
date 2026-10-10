using System.Net;
using Bifrost.Web.Configuration;
using Bifrost.Web.Features.ConceptVotes;
using Bifrost.Web.Features.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

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

    [Fact]
    public async Task Member_console_shows_recent_bridge_receipts()
    {
        using (var warmupClient = _factory.CreateClient())
        {
            _ = await warmupClient.GetAsync("/App");
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
            var actor = dbContext.UserAccounts.Single(x => x.NormalizedGitHubLogin == "TEST-ADMIN");
            dbContext.BridgeActions.Add(new BridgeAction
            {
                ActorKind = BridgeActorKind.Agent,
                ActorName = "nibu",
                ActorUserAccountId = actor.Id,
                TargetSurface = BridgeTargetSurface.GitHub,
                ActionKind = BridgeActionKind.GitHubDraftPullRequest,
                Status = BridgeActionStatus.Completed,
                TargetRepositoryFullName = "gamecult/bifrost",
                TargetLocator = "pulls",
                SourceKind = "bifrost_governance_topic",
                SourceId = "topic_123",
                AuthorityReference = "dispatch-approved",
                PolicyDecision = "Authorized through Bifrost policy.",
                Title = "Draft motion implementation PR",
                Summary = "Open a draft PR for the approved topic.",
                ReceiptUrl = "https://github.com/GameCult/Bifrost/pull/99",
                ExternalReceiptId = "99",
                ReceiptPayload = "{\"pr\":99}",
                RequestedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                UpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                CompletedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
            dbContext.AgentDispatchRuns.Add(new AgentDispatchRun
            {
                RequestId = "req_dispatch_123",
                TargetRepoName = "Bifrost",
                TargetRepositoryFullName = "GameCult/Bifrost",
                TargetAgentIdentity = "nibu",
                LaunchMode = "app-server",
                Status = AgentDispatchRunStatus.Completed,
                StartedByUserAccountId = actor.Id,
                WorkerProcessId = 4242,
                ThreadId = "thread_1",
                TurnId = "turn_1",
                LogPath = "E:/Projects/Bifrost/.bifrost/agent-dispatch/req_dispatch_123/codex.log",
                ResultPath = "E:/Projects/Bifrost/.bifrost/agent-dispatch/req_dispatch_123/result.json",
                Note = "Codex app turn completed for thread_1/turn_1.",
                StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-8),
                UpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
                CompletedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2)
            });
            dbContext.AgentTransportReceipts.Add(new AgentTransportReceipt
            {
                RequestId = "req_transport_123",
                Title = "Queue the GitHub bridge hardening pass",
                TargetRepoName = "Bifrost",
                TargetRepositoryFullName = "GameCult/Bifrost",
                TargetAgentIdentity = "nibu",
                ActivityKind = AgentTransportReceiptKind.Claimed,
                Status = "claimed",
                ActorUserAccountId = actor.Id,
                ActorName = "bifrost-dispatcher",
                Note = "Request claimed for Bifrost.",
                OccurredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-3)
            });
            dbContext.GovernanceActivityReceipts.Add(new GovernanceActivityReceipt
            {
                TopicId = "topic_123",
                CommentId = "comment_123",
                DispatchRequestId = "req_transport_123",
                Title = "Queue the GitHub bridge hardening pass",
                JurisdictionRepoName = "Bifrost",
                JurisdictionAgentIdentity = "nibu",
                ActivityKind = GovernanceActivityReceiptKind.TopicPromoted,
                ActorKind = "face",
                ActorName = "nibu",
                Note = "Governance topic promoted to update request req_transport_123.",
                ActorUserAccountId = actor.Id,
                OccurredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-4)
            });
            dbContext.SaveChanges();
        }

        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/App");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Agent runs", html);
        Assert.Contains("req_dispatch_123", html);
        Assert.Contains("Transport receipts", html);
        Assert.Contains("req_transport_123", html);
        Assert.Contains("Governance receipts", html);
        Assert.Contains("topic_123", html);
        Assert.Contains("Bridge receipts", html);
        Assert.Contains("Draft motion implementation PR", html);
        Assert.Contains("nibu", html);
    }

    [Fact]
    public async Task Member_console_activity_shows_a_purge_without_its_network_tag()
    {
        using (var warmupClient = _factory.CreateClient())
        {
            _ = await warmupClient.GetAsync("/App");
        }

        string tag;
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
            var admin = await dbContext.UserAccounts
                .Include(x => x.Membership)
                .SingleAsync(x => x.NormalizedGitHubLogin == "TEST-ADMIN");
            var now = DateTimeOffset.UtcNow;
            dbContext.ConceptSubjects.Add(new ConceptSubject
            {
                Collection = "aetheria-concept-mill",
                Key = "ship-a",
                Title = "ship-a",
                RegisteredAtUtc = now
            });
            await dbContext.SaveChangesAsync();

            var service = scope.ServiceProvider.GetRequiredService<ConceptAppraisalService>();
            var options = scope.ServiceProvider.GetRequiredService<IOptions<ConceptVoteOptions>>().Value;
            var client = System.Net.IPAddress.Parse("203.0.113.7");
            tag = NetworkPrefix.Tag(client, options.IPv4PrefixLength, options.IPv6PrefixLength, options.NetworkTagSecret);
            var voter = VoterIdentity.For(CurrentBifrostActor.Anonymous, "member-view-device")!;
            await service.CastAsync("aetheria-concept-mill", "ship-a", voter, ConceptStance.Up, client, default);

            var removed = await service.PurgeAsync(
                new CurrentBifrostActor(true, admin), tag, now.AddHours(-1), now.AddHours(1), default);
            Assert.Equal(1, removed);
        }

        using var memberClient = _factory.CreateClient();
        using var response = await memberClient.GetAsync("/App");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Purge", html);
        Assert.Contains("count", html);
        Assert.DoesNotContain(tag, html);
    }
}
