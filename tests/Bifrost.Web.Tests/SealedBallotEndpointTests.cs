using System.Net;
using System.Net.Http.Json;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Motions;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.Web.Tests;

public sealed class SealedBallotEndpointTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private const string Salt = "00112233445566778899aabbccddeeff";
    private const string Reason = "Distinctive-endpoint-reason-plugh.";

    private static int _next = 1000;

    private sealed record Seeded(Guid RevealMotion, Guid CommitMotion, Guid ClosedMotion, Guid VoterId, string Ana, string Bo, string Outsider);

    private async Task<Seeded> SeedAsync()
    {
        using (var warmup = factory.CreateClient()) { _ = await warmup.GetAsync("/App"); }
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var now = DateTimeOffset.UtcNow;
        var n = Interlocked.Add(ref _next, 10);
        UserAccount Account(long gh, string name, MembershipStatus status)
        {
            var u = new UserAccount { GitHubUserId = gh, GitHubLogin = name, NormalizedGitHubLogin = name.ToUpperInvariant(), DisplayName = name };
            u.MemberProfile = new MemberProfile { UserAccountId = u.Id, Nickname = name, Headline = "seed", UpdatedAtUtc = now };
            u.Membership = new Domain.Membership { UserAccountId = u.Id, Status = status, CreatedAtUtc = now };
            if (status == MembershipStatus.Active)
            {
                Bifrost.Web.Features.Shared.ApplicationBootstrapper.EnsureRole(u.Membership, MemberRole.StandardMember, null, now, "seed");
            }

            db.UserAccounts.Add(u);
            return u;
        }

        var ana = Account(n + 1, $"ana{n}", MembershipStatus.Active);
        Account(n + 2, $"bo{n}", MembershipStatus.Active);
        Account(n + 3, $"outsider{n}", MembershipStatus.Authenticated);
        Motion Make(DateTimeOffset commitCloses, DateTimeOffset closes)
        {
            var m = new Motion
            {
                CreatedByUserAccountId = ana.Id, Scope = MotionScope.Management, Kind = MotionKind.SealedBallot, Title = "t", Summary = "s",
                Candidates = "Ana\nBo", ApprovalThreshold = 0m, OpensAtUtc = now.AddHours(-3), CommitClosesAtUtc = commitCloses,
                ClosesAtUtc = closes, CreatedAtUtc = now.AddHours(-3)
            };
            db.Motions.Add(m);
            return m;
        }

        var reveal = Make(now.AddHours(-1), now.AddHours(1));
        var commit = Make(now.AddHours(1), now.AddHours(2));
        var closed = Make(now.AddHours(-2), now.AddHours(-1));
        db.SealedBallots.Add(new SealedBallot
        {
            MotionId = reveal.Id, UserAccountId = ana.Id, CommittedAtUtc = now.AddHours(-2),
            Commitment = SealedBallotDocument.Commit(new(SealedBallotDocument.SchemaId, reveal.Id, 1, "Bo", Reason, Salt), ana.Id)
        });
        await db.SaveChangesAsync();
        return new Seeded(reveal.Id, commit.Id, closed.Id, ana.Id, (n + 1).ToString(), (n + 2).ToString(), (n + 3).ToString());
    }

    private HttpClient As(string ghId, string login, bool anonymous = false)
    {
        var c = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (anonymous) c.DefaultRequestHeaders.Add("X-Test-Anonymous", "true");
        else { c.DefaultRequestHeaders.Add("X-Test-GitHub-Id", ghId); c.DefaultRequestHeaders.Add("X-Test-GitHub-Login", login); }
        return c;
    }

    [Fact]
    public async Task Unauthenticated_and_non_member_callers_are_refused_on_every_sealed_endpoint()
    {
        var s = await SeedAsync();
        foreach (var client in new[] { As("", "", anonymous: true), As(s.Outsider, "outsider") })
        {
            var id = s.RevealMotion;
            var responses = new[]
            {
                await client.GetAsync($"/api/sealed-motions/{id}"),
                await client.GetAsync($"/api/sealed-motions/{id}/result"),
                await client.PostAsJsonAsync($"/api/sealed-motions/{id}/commit", new { commitment = new string('a', 64) }),
                await client.PostAsJsonAsync($"/api/sealed-motions/{id}/reveal", new SealedBallotDocument(SealedBallotDocument.SchemaId, id, 1, "Bo", Reason, Salt))
            };
            foreach (var r in responses)
            {
                Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Redirect, HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized });
                Assert.DoesNotContain(Reason, await r.Content.ReadAsStringAsync());
            }
        }

        using var db = factory.Services.CreateScope().ServiceProvider.GetRequiredService<BifrostDbContext>();
        Assert.All(db.SealedBallots.Where(x => x.MotionId == s.RevealMotion), b => Assert.Null(b.RevealedReason));
    }

    [Fact]
    public async Task Member_commits_in_the_commit_window_and_reveals_in_the_reveal_window_without_any_response_leaking_it()
    {
        var s = await SeedAsync();
        var ana = As(s.Ana, "ana");
        var bo = As(s.Bo, "bo");

        var commit = await ana.PostAsJsonAsync($"/api/sealed-motions/{s.CommitMotion}/commit", new { commitment = new string('a', 64) });
        Assert.True(commit.StatusCode == HttpStatusCode.OK, await commit.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await ana.PostAsJsonAsync($"/api/sealed-motions/{s.CommitMotion}/reveal",
            new SealedBallotDocument(SealedBallotDocument.SchemaId, s.CommitMotion, 1, "Bo", Reason, Salt))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ana.PostAsJsonAsync($"/api/sealed-motions/{s.RevealMotion}/commit",
            new { commitment = new string('b', 64) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ana.PostAsJsonAsync($"/api/sealed-motions/{s.CommitMotion}/commit", new { commitment = "nope" })).StatusCode);

        var wrong = await ana.PostAsJsonAsync($"/api/sealed-motions/{s.RevealMotion}/reveal",
            new SealedBallotDocument(SealedBallotDocument.SchemaId, s.RevealMotion, 1, "Ana", Reason, Salt));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        var ok = await ana.PostAsJsonAsync($"/api/sealed-motions/{s.RevealMotion}/reveal",
            new SealedBallotDocument(SealedBallotDocument.SchemaId, s.RevealMotion, 1, "Bo", Reason, Salt));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // After the reveal is stored, no sealed endpoint answers any member with the ballot.
        foreach (var client in new[] { ana, bo })
        {
            foreach (var response in new[]
            {
                await client.GetAsync($"/api/sealed-motions/{s.RevealMotion}"),
                await client.GetAsync($"/api/sealed-motions/{s.RevealMotion}/result"),
                ok
            })
            {
                var body = await response.Content.ReadAsStringAsync();
                Assert.DoesNotContain(Reason, body);
                Assert.DoesNotContain(Salt, body);
            }
        }

        Assert.Equal(HttpStatusCode.Conflict, (await bo.GetAsync($"/api/sealed-motions/{s.RevealMotion}/result")).StatusCode);
    }

    [Fact]
    public async Task A_motion_past_its_deadline_closes_on_the_first_read_and_publishes()
    {
        var s = await SeedAsync();
        var response = await As(s.Bo, "bo").GetAsync($"/api/sealed-motions/{s.ClosedMotion}/result");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        Assert.Contains("no ballots", await response.Content.ReadAsStringAsync());
    }
}
