using System.Net;
using System.Net.Http.Json;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Motions;
using Bifrost.Web.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.Web.Tests;

public sealed class SealedBallotCloseHttpTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private const string Salt = "00112233445566778899aabbccddeeff";
    private const string Reason = "Distinctive-close-reason-plugh.";
    private static int _next = 7000;

    private sealed record W(Guid RevealA, Guid RevealB, Guid LateMotion, Guid ClosedWithBallot, string Ana, string Bo, string Cy);

    private async Task<W> SeedAsync()
    {
        using (var warmup = factory.CreateClient()) { _ = await warmup.GetAsync("/App"); }
        var n = Interlocked.Add(ref _next, 10);
        var now = DateTimeOffset.UtcNow;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        UserAccount Make(long gh, string name)
        {
            var u = new UserAccount { GitHubUserId = gh, GitHubLogin = name, NormalizedGitHubLogin = name.ToUpperInvariant(), DisplayName = name };
            u.MemberProfile = new MemberProfile { UserAccountId = u.Id, Nickname = name, Headline = "seed", UpdatedAtUtc = now };
            u.Membership = new Domain.Membership { UserAccountId = u.Id, Status = MembershipStatus.Active, CreatedAtUtc = now };
            Bifrost.Web.Features.Shared.ApplicationBootstrapper.EnsureRole(u.Membership, MemberRole.StandardMember, null, now, "seed");
            db.UserAccounts.Add(u);
            return u;
        }

        var ana = Make(n + 1, $"ana{n}");
        Make(n + 2, $"bo{n}");
        Make(n + 3, $"cy{n}");
        Motion M(DateTimeOffset commitCloses, DateTimeOffset closes)
        {
            var m = new Motion
            {
                CreatedByUserAccountId = ana.Id, Scope = MotionScope.Management, Kind = MotionKind.SealedBallot, Title = "t", Summary = "s",
                Candidates = "Ana\nBo", OpensAtUtc = now.AddHours(-3), CommitClosesAtUtc = commitCloses, ClosesAtUtc = closes, CreatedAtUtc = now
            };
            db.Motions.Add(m);
            return m;
        }

        var a = M(now.AddHours(-1), now.AddHours(1));      // reveal phase: Ana's real ballot
        var b = M(now.AddHours(-1), now.AddHours(1));      // reveal phase: Ana's commitment from A replayed here
        var late = M(now.AddHours(-2), now.AddHours(-1));  // past deadline, Ana committed and never revealed
        var done = M(now.AddHours(-2), now.AddHours(-1));  // past deadline, Ana's reveal is in
        string Commit(Guid motion) => SealedBallotDocument.Commit(new(SealedBallotDocument.SchemaId, motion, 1, "Bo", Reason, Salt), ana.Id);
        db.SealedBallots.Add(new SealedBallot { MotionId = a.Id, UserAccountId = ana.Id, CommittedAtUtc = now.AddHours(-2), Commitment = Commit(a.Id) });
        db.SealedBallots.Add(new SealedBallot { MotionId = b.Id, UserAccountId = ana.Id, CommittedAtUtc = now.AddHours(-2), Commitment = Commit(a.Id) });
        db.SealedBallots.Add(new SealedBallot { MotionId = late.Id, UserAccountId = ana.Id, CommittedAtUtc = now.AddHours(-3), Commitment = Commit(late.Id) });
        db.SealedBallots.Add(new SealedBallot
        {
            MotionId = done.Id, UserAccountId = ana.Id, CommittedAtUtc = now.AddHours(-3), Commitment = Commit(done.Id),
            RevealedCandidate = "Bo", RevealedReason = Reason, Salt = Salt, RevealedAtUtc = now.AddHours(-1.5)
        });
        await db.SaveChangesAsync();
        return new W(a.Id, b.Id, late.Id, done.Id, (n + 1).ToString(), (n + 2).ToString(), (n + 3).ToString());
    }

    private HttpClient As(string id, string login)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-GitHub-Id", id);
        c.DefaultRequestHeaders.Add("X-Test-GitHub-Login", login);
        return c;
    }

    private static SealedBallotDocument Doc(Guid motion) => new(SealedBallotDocument.SchemaId, motion, 1, "Bo", Reason, Salt);

    private static async Task Refused(HttpResponseMessage r, HttpStatusCode expected)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == expected, $"{r.StatusCode}: {body}");
        Assert.DoesNotContain(Reason, body);
        Assert.DoesNotContain(Salt, body);
        Assert.DoesNotContain("\"Bo\"", body);
    }

    [Fact]
    public async Task A_commit_or_reveal_replayed_into_another_motion_is_refused()
    {
        var w = await SeedAsync();
        var ana = As(w.Ana, "ana");
        // A's document aimed at B: wrong motion id inside, and B's stored commitment is A's hash, which no B-bound document reproduces.
        await Refused(await ana.PostAsJsonAsync($"/api/sealed-motions/{w.RevealB}/reveal", Doc(w.RevealA)), HttpStatusCode.BadRequest);
        await Refused(await ana.PostAsJsonAsync($"/api/sealed-motions/{w.RevealB}/reveal", Doc(w.RevealB)), HttpStatusCode.BadRequest);
        // A's commit hash sent into a motion that is no longer in its commit window.
        await Refused(await ana.PostAsJsonAsync($"/api/sealed-motions/{w.RevealB}/commit", new { commitment = new string('a', 64) }), HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.OK, (await ana.PostAsJsonAsync($"/api/sealed-motions/{w.RevealA}/reveal", Doc(w.RevealA))).StatusCode);
    }

    [Fact]
    public async Task A_non_voter_cannot_reveal()
    {
        var w = await SeedAsync();
        await Refused(await As(w.Cy, "cy").PostAsJsonAsync($"/api/sealed-motions/{w.RevealA}/reveal", Doc(w.RevealA)), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_reveal_after_the_deadline_is_refused_and_the_voter_is_shown_unrevealed()
    {
        var w = await SeedAsync();
        var ana = As(w.Ana, "ana");
        await Refused(await ana.PostAsJsonAsync($"/api/sealed-motions/{w.LateMotion}/reveal", Doc(w.LateMotion)), HttpStatusCode.Conflict);
        var result = await As(w.Bo, "bo").GetAsync($"/api/sealed-motions/{w.LateMotion}/result");
        var body = await result.Content.ReadAsStringAsync();
        Assert.True(result.StatusCode == HttpStatusCode.OK, body);
        Assert.Contains("committed but did not reveal", body);
        Assert.DoesNotContain(Reason, body);
        // Still refused after the close has run, and the published result did not change.
        await Refused(await ana.PostAsJsonAsync($"/api/sealed-motions/{w.LateMotion}/reveal", Doc(w.LateMotion)), HttpStatusCode.Conflict);
        Assert.Equal(body, await (await As(w.Cy, "cy").GetAsync($"/api/sealed-motions/{w.LateMotion}/result")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Close_with_ballots_publishes_them_once_with_a_stable_digest()
    {
        var w = await SeedAsync();
        var first = await (await As(w.Bo, "bo").GetAsync($"/api/sealed-motions/{w.ClosedWithBallot}/result")).Content.ReadAsStringAsync();
        Assert.Contains(Reason, first);
        Assert.Contains(Salt, first);
        Assert.Contains("\"outcome\":\"winner\"", first);
        var second = await (await As(w.Cy, "cy").GetAsync($"/api/sealed-motions/{w.ClosedWithBallot}/result")).Content.ReadAsStringAsync();
        Assert.Equal(first, second);

        using var db = factory.Services.CreateScope().ServiceProvider.GetRequiredService<BifrostDbContext>();
        Assert.Equal(1, await db.AuditEvents.CountAsync(x => x.Action == "sealed.closed" && x.EntityId == w.ClosedWithBallot.ToString()));
        Assert.Equal(MotionStatus.Closed, (await db.Motions.SingleAsync(x => x.Id == w.ClosedWithBallot)).Status);
    }
}
