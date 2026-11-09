using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Motions;
using Bifrost.Web.Features.Shared;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Bifrost.Web.Tests;

public sealed class MindIntakeTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private const string Salt = "00112233445566778899aabbccddeeff";
    private const string Reason = "Distinctive-mind-reason-xyzzy.";
    private static int _next = 5000;

    private sealed record Key(Ed25519PrivateKeyParameters Private)
    {
        public string Public => Convert.ToBase64String(Private.GeneratePublicKey().GetEncoded());
        public static Key New() => new(new Ed25519PrivateKeyParameters(new SecureRandom()));
    }

    private sealed class World
    {
        public required string Persona { get; init; }
        public required Guid Account { get; init; }
        public required Guid RevealMotion { get; init; }
        public required Guid CommitMotion { get; init; }
        public required Key Key { get; init; }
        public required Guid KeyId { get; init; }
        public required HttpClient Moderator { get; init; }
        public required string MemberGitHubId { get; init; }
    }

    private async Task<World> WorldAsync(bool bindRevealToPersona = true)
    {
        using (var warmup = factory.CreateClient()) { _ = await warmup.GetAsync("/App"); }
        var n = Interlocked.Add(ref _next, 10);
        var persona = $"mind{n}";
        var now = DateTimeOffset.UtcNow;
        Guid account, reveal, commit;
        using (var scope = factory.Services.CreateScope())
        {
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

            var mind = Make(n + 1, persona);
            Make(n + 2, $"member{n}");
            Motion M(DateTimeOffset commitCloses, DateTimeOffset closes)
            {
                var m = new Motion
                {
                    CreatedByUserAccountId = mind.Id, Scope = MotionScope.Management, Kind = MotionKind.SealedBallot, Title = "t", Summary = "s",
                    Candidates = "Ana\nBo", OpensAtUtc = now.AddHours(-3), CommitClosesAtUtc = commitCloses, ClosesAtUtc = closes, CreatedAtUtc = now
                };
                db.Motions.Add(m);
                return m;
            }

            var r = M(now.AddHours(-1), now.AddHours(1));
            var c = M(now.AddHours(1), now.AddHours(2));
            db.SealedBallots.Add(new SealedBallot
            {
                MotionId = r.Id, UserAccountId = mind.Id, CommittedAtUtc = now.AddHours(-2),
                Commitment = SealedBallotDocument.Commit(new(SealedBallotDocument.SchemaId, r.Id, 1, "Bo", Reason, Salt, bindRevealToPersona ? persona : ""), mind.Id)
            });
            await db.SaveChangesAsync();
            (account, reveal, commit) = (mind.Id, r.Id, c.Id);
        }

        var moderator = factory.CreateClient();
        var key = Key.New();
        var registered = await moderator.PostAsJsonAsync("/api/sealed-motions/mind-keys", new { persona, userAccountId = account, publicKey = key.Public });
        Assert.True(registered.StatusCode == HttpStatusCode.OK, await registered.Content.ReadAsStringAsync());
        var keyId = JsonDocument.Parse(await registered.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        return new World { Persona = persona, Account = account, RevealMotion = reveal, CommitMotion = commit, Key = key, KeyId = keyId, Moderator = moderator, MemberGitHubId = (n + 2).ToString() };
    }

    private static MindEnvelope Sign(Key key, string persona, string verb, Guid motion, string? nonce = null, string? commitment = null,
        string? candidate = null, string? reason = null, string? salt = null, long? issuedAt = null)
    {
        var e = new MindEnvelope(persona, verb, motion, nonce ?? Guid.NewGuid().ToString("N"), issuedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(), commitment, 1, candidate, reason, salt, "");
        var signer = new Ed25519Signer();
        signer.Init(true, key.Private);
        var bytes = System.Text.Encoding.UTF8.GetBytes(MindIntakeService.SigningText(e));
        signer.BlockUpdate(bytes, 0, bytes.Length);
        return e with { Signature = Convert.ToBase64String(signer.GenerateSignature()) };
    }

    private HttpClient Anonymous()
    {
        var c = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Add("X-Test-Anonymous", "true"); // the intake needs no session; this proves it
        return c;
    }

    private static Task<HttpResponseMessage> Post(HttpClient c, Guid route, MindEnvelope e) => c.PostAsJsonAsync($"/api/sealed-motions/{route}/mind", e);

    private static async Task Refused(HttpResponseMessage r, HttpStatusCode expected)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == expected, $"{r.StatusCode}: {body}");
        Assert.DoesNotContain(Reason, body);
        Assert.DoesNotContain(Salt, body);
        Assert.DoesNotContain("Bo", body.Replace("Bonce", ""));
    }

    [Fact]
    public async Task A_mind_commits_and_reveals_by_signature_alone_and_the_ballot_stays_sealed()
    {
        var w = await WorldAsync();
        var anon = Anonymous();
        var commitment = SealedBallotDocument.Commit(new(SealedBallotDocument.SchemaId, w.CommitMotion, 1, "Ana", Reason, Salt, w.Persona), w.Account);
        var commit = await Post(anon, w.CommitMotion, Sign(w.Key, w.Persona, "commit", w.CommitMotion, commitment: commitment));
        Assert.True(commit.StatusCode == HttpStatusCode.OK, await commit.Content.ReadAsStringAsync());

        var reveal = await Post(anon, w.RevealMotion, Sign(w.Key, w.Persona, "reveal", w.RevealMotion, candidate: "Bo", reason: Reason, salt: Salt));
        Assert.True(reveal.StatusCode == HttpStatusCode.OK, await reveal.Content.ReadAsStringAsync());
        Assert.DoesNotContain(Reason, await reveal.Content.ReadAsStringAsync());

        using var db = factory.Services.CreateScope().ServiceProvider.GetRequiredService<BifrostDbContext>();
        Assert.Equal(commitment, (await db.SealedBallots.SingleAsync(x => x.MotionId == w.CommitMotion)).Commitment);
        Assert.Contains(db.AuditEvents, x => x.Action == "mindkey.registered");
        var member = factory.CreateClient();
        member.DefaultRequestHeaders.Add("X-Test-GitHub-Id", w.MemberGitHubId);
        member.DefaultRequestHeaders.Add("X-Test-GitHub-Login", "member");
        var state = await (await member.GetAsync($"/api/sealed-motions/{w.RevealMotion}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(Reason, state);
        Assert.DoesNotContain(Salt, state);
    }

    [Fact]
    public async Task Every_refusal_is_typed_and_leaks_no_ballot()
    {
        var w = await WorldAsync();
        var anon = Anonymous();
        var other = Key.New();
        MindEnvelope Reveal(Key k, string persona = "", string? nonce = null, string? reason = null) =>
            Sign(k, persona == "" ? w.Persona : persona, "reveal", w.RevealMotion, nonce, candidate: "Bo", reason: reason ?? Reason, salt: Salt);

        await Refused(await Post(anon, w.RevealMotion, Reveal(other)), HttpStatusCode.Unauthorized);                        // key not registered
        await Refused(await Post(anon, w.RevealMotion, Reveal(w.Key, "nobody")), HttpStatusCode.Unauthorized);              // unknown persona
        await Refused(await Post(anon, w.RevealMotion, Reveal(w.Key) with { Reason = "tampered" }), HttpStatusCode.Unauthorized); // signature covers every field
        await Refused(await Post(anon, w.RevealMotion, Reveal(w.Key) with { Signature = "AAAA" }), HttpStatusCode.Unauthorized);
        await Refused(await Post(anon, w.CommitMotion, Reveal(w.Key)), HttpStatusCode.BadRequest);                           // signed for another motion than the route
        await Refused(await Post(anon, w.RevealMotion, Reveal(w.Key, nonce: "short")), HttpStatusCode.BadRequest);           // malformed nonce
        await Refused(await Post(anon, w.RevealMotion, Reveal(w.Key) with { Verb = "tally" }), HttpStatusCode.BadRequest);
        await Refused(await Post(anon, w.CommitMotion, Sign(w.Key, w.Persona, "reveal", w.CommitMotion, candidate: "Bo", reason: Reason, salt: Salt)), HttpStatusCode.Conflict); // wrong phase

        // A second persona with its own key cannot speak as the first: persona id must match the signing key.
        var rival = await WorldAsync();
        await Refused(await Post(anon, w.RevealMotion, Sign(rival.Key, w.Persona, "reveal", w.RevealMotion, candidate: "Bo", reason: Reason, salt: Salt)), HttpStatusCode.Unauthorized);

        // Replay: the same signed envelope is accepted once, then refused.
        var once = Reveal(w.Key);
        Assert.Equal(HttpStatusCode.OK, (await Post(anon, w.RevealMotion, once)).StatusCode);
        await Refused(await Post(anon, w.RevealMotion, once), HttpStatusCode.Conflict);
        // A bad signature does not spend the nonce.
        var fresh = Reveal(w.Key);
        await Refused(await Post(anon, w.RevealMotion, fresh with { Reason = "x" }), HttpStatusCode.Unauthorized);
        Assert.Equal(HttpStatusCode.OK, (await Post(anon, w.RevealMotion, fresh)).StatusCode);
    }

    [Fact]
    public async Task A_commitment_not_bound_to_the_persona_cannot_be_revealed_through_the_intake()
    {
        var w = await WorldAsync(bindRevealToPersona: false);
        await Refused(await Post(Anonymous(), w.RevealMotion, Sign(w.Key, w.Persona, "reveal", w.RevealMotion, candidate: "Bo", reason: Reason, salt: Salt)), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_revoked_key_is_refused_and_a_commitment_made_before_revocation_stays()
    {
        var w = await WorldAsync();
        var anon = Anonymous();
        var commitment = new string('c', 64);
        Assert.Equal(HttpStatusCode.OK, (await Post(anon, w.CommitMotion, Sign(w.Key, w.Persona, "commit", w.CommitMotion, commitment: commitment))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await w.Moderator.DeleteAsync($"/api/sealed-motions/mind-keys/{w.KeyId}")).StatusCode);
        await Refused(await Post(anon, w.RevealMotion, Sign(w.Key, w.Persona, "reveal", w.RevealMotion, candidate: "Bo", reason: Reason, salt: Salt)), HttpStatusCode.Unauthorized);

        using var db = factory.Services.CreateScope().ServiceProvider.GetRequiredService<BifrostDbContext>();
        Assert.Equal(commitment, (await db.SealedBallots.SingleAsync(x => x.MotionId == w.CommitMotion)).Commitment);
        Assert.Contains(db.AuditEvents, x => x.Action == "mindkey.revoked");

        // Rotation: a new key registered for the same persona works.
        var next = Key.New();
        Assert.Equal(HttpStatusCode.OK, (await w.Moderator.PostAsJsonAsync("/api/sealed-motions/mind-keys", new { persona = w.Persona, userAccountId = w.Account, publicKey = next.Public })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(anon, w.RevealMotion, Sign(next, w.Persona, "reveal", w.RevealMotion, candidate: "Bo", reason: Reason, salt: Salt))).StatusCode);
    }

    [Fact]
    public async Task Only_a_moderator_manages_keys_and_a_persona_stays_bound_to_one_account()
    {
        var w = await WorldAsync();
        var member = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        member.DefaultRequestHeaders.Add("X-Test-GitHub-Id", w.MemberGitHubId);
        member.DefaultRequestHeaders.Add("X-Test-GitHub-Login", "member");
        var body = new { persona = w.Persona, userAccountId = w.Account, publicKey = Key.New().Public };
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/api/sealed-motions/mind-keys", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync($"/api/sealed-motions/mind-keys/{w.KeyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/sealed-motions/mind-keys")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await Anonymous().PostAsJsonAsync("/api/sealed-motions/mind-keys", body)).StatusCode);

        var elsewhere = new { persona = w.Persona, userAccountId = Guid.NewGuid(), publicKey = Key.New().Public };
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Moderator.PostAsJsonAsync("/api/sealed-motions/mind-keys", elsewhere)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Moderator.PostAsJsonAsync("/api/sealed-motions/mind-keys", new { persona = "Bad Name", userAccountId = w.Account, publicKey = Key.New().Public })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Moderator.PostAsJsonAsync("/api/sealed-motions/mind-keys", new { persona = w.Persona, userAccountId = w.Account, publicKey = "AAAA" })).StatusCode);
    }

    [Fact]
    public async Task The_signing_helper_and_the_server_agree_on_the_envelope_and_the_commitment()
    {
        var w = await WorldAsync();
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "tools"))) root = Path.GetDirectoryName(root)!;
        var keyFile = Path.Combine(Path.GetTempPath(), $"sealed-{Guid.NewGuid():N}.key");
        var seed = w.Key.Private.GetEncoded();
        await File.WriteAllBytesAsync(keyFile, seed);
        File.SetUnixFileMode(keyFile, UnixFileMode.UserRead);
        try
        {
            string Run(params string[] a)
            {
                var psi = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true };
                psi.ArgumentList.Add(Path.Combine(root, "tools", "sealed-motion-sign.mjs"));
                foreach (var x in a) psi.ArgumentList.Add(x);
                using var p = Process.Start(psi)!;
                var o = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                Assert.True(p.ExitCode == 0, p.StandardError.ReadToEnd());
                return o.Trim();
            }

            Assert.Equal(w.Key.Public, Run("pubkey", "--key", keyFile));
            var committed = JsonDocument.Parse(Run("commit", "--key", keyFile, "--persona", w.Persona, "--motion", w.CommitMotion.ToString(),
                "--voter", w.Account.ToString(), "--candidate", "Ana", "--reason", Reason));
            var post = await Anonymous().PostAsync($"/api/sealed-motions/{w.CommitMotion}/mind",
                new StringContent(committed.RootElement.GetProperty("envelope").GetRawText(), System.Text.Encoding.UTF8, "application/json"));
            Assert.True(post.StatusCode == HttpStatusCode.OK, await post.Content.ReadAsStringAsync());

            var ballot = committed.RootElement.GetProperty("ballot");
            var expected = SealedBallotDocument.Commit(new(SealedBallotDocument.SchemaId, w.CommitMotion, 1, "Ana", Reason, ballot.GetProperty("salt").GetString()!, w.Persona), w.Account);
            using var db = factory.Services.CreateScope().ServiceProvider.GetRequiredService<BifrostDbContext>();
            Assert.Equal(expected, (await db.SealedBallots.SingleAsync(x => x.MotionId == w.CommitMotion)).Commitment);
        }
        finally { File.Delete(keyFile); }
    }

    [Fact]
    public async Task Every_refusal_before_a_verified_signature_costs_one_signature_check_and_reveals_nothing_about_state()
    {
        var w = await WorldAsync();
        var anon = Anonymous();
        long Checks() => Interlocked.Read(ref MindIntakeService.SignatureChecks);
        MindEnvelope Reveal(Key k, string persona) => Sign(k, persona, "reveal", w.RevealMotion, candidate: "Bo", reason: Reason, salt: Salt);

        var before = Checks();
        await Refused(await Post(anon, w.RevealMotion, Reveal(w.Key, "nobody")), HttpStatusCode.Unauthorized); // unknown persona
        Assert.True(Checks() > before, "unknown persona skipped the verification");

        before = Checks();
        await Refused(await Post(anon, w.RevealMotion, Reveal(w.Key, w.Persona) with { Signature = "not-base64!" }), HttpStatusCode.Unauthorized); // malformed signature
        Assert.True(Checks() > before, "malformed signature skipped the verification");

        // Route/motion mismatch and a stale clock are answered 400 only to a caller whose signature verified; an unsigned one gets the 401.
        var wrongRoute = Reveal(w.Key, w.Persona);
        await Refused(await Post(anon, w.CommitMotion, wrongRoute with { Signature = Convert.ToBase64String(new byte[64]) }), HttpStatusCode.Unauthorized);
        await Refused(await Post(anon, w.CommitMotion, wrongRoute), HttpStatusCode.BadRequest);

        Assert.Equal(HttpStatusCode.OK, (await w.Moderator.DeleteAsync($"/api/sealed-motions/mind-keys/{w.KeyId}")).StatusCode);
        before = Checks();
        await Refused(await Post(anon, w.RevealMotion, Reveal(w.Key, w.Persona)), HttpStatusCode.Unauthorized); // revoked
        Assert.True(Checks() > before, "revoked key skipped the verification");
    }

    [Fact]
    public async Task A_stale_envelope_is_refused_and_old_nonces_are_pruned()
    {
        var w = await WorldAsync();
        var anon = Anonymous();
        var old = DateTimeOffset.UtcNow.AddHours(-1);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
            db.MindNonces.Add(new MindNonce { PersonaId = w.Persona, Nonce = "old-nonce-old-nonce-1234", SeenAtUtc = old });
            await db.SaveChangesAsync();
        }

        await Refused(await Post(anon, w.RevealMotion, Sign(w.Key, w.Persona, "reveal", w.RevealMotion, candidate: "Bo", reason: Reason, salt: Salt,
            issuedAt: DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds())), HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.OK, (await Post(anon, w.RevealMotion, Sign(w.Key, w.Persona, "reveal", w.RevealMotion, candidate: "Bo", reason: Reason, salt: Salt))).StatusCode);

        using var check = factory.Services.CreateScope().ServiceProvider.GetRequiredService<BifrostDbContext>();
        Assert.DoesNotContain(check.MindNonces, x => x.Nonce == "old-nonce-old-nonce-1234");
        Assert.Single(check.MindNonces.Where(x => x.PersonaId == w.Persona));
    }

    [Fact]
    public async Task The_endpoint_caps_the_body_and_rate_limits_per_source()
    {
        var w = await WorldAsync();
        var big = new StringContent(new string('x', 10_000), System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Anonymous().PostAsync($"/api/sealed-motions/{w.RevealMotion}/mind", big)).StatusCode);

        using var limited = factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["MindIntake:Permits"] = "3" })));
        var client = limited.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Anonymous", "true");
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            codes.Add((await Post(client, w.RevealMotion, Sign(Key.New(), w.Persona, "reveal", w.RevealMotion))).StatusCode);
        }

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], codes);
    }

    [Fact]
    public async Task One_account_cannot_carry_two_personas_and_the_bindings_are_unique_in_the_model()
    {
        var w = await WorldAsync();
        var second = await w.Moderator.PostAsJsonAsync("/api/sealed-motions/mind-keys", new { persona = w.Persona + "b", userAccountId = w.Account, publicKey = Key.New().Public });
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);

        using var db = factory.Services.CreateScope().ServiceProvider.GetRequiredService<BifrostDbContext>();
        var type = db.Model.FindEntityType(typeof(MindPersona))!;
        Assert.Contains(type.GetIndexes(), i => i.IsUnique && i.Properties.Single().Name == nameof(MindPersona.PersonaId));
        Assert.Contains(type.GetIndexes(), i => i.IsUnique && i.Properties.Single().Name == nameof(MindPersona.UserAccountId));
        Assert.Contains(db.Model.FindEntityType(typeof(MindNonce))!.GetIndexes(), i => i.IsUnique && i.Properties.Count == 2);
    }

    private sealed class ThrowOnNonce : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData e, InterceptionResult<int> r, CancellationToken ct = default) =>
            e.Context!.ChangeTracker.Entries<MindNonce>().Any(x => x.State == EntityState.Added)
                ? throw new DbUpdateException("disk fault")
                : ValueTask.FromResult(r);
    }

    [Fact]
    public async Task A_write_failure_that_is_not_a_nonce_collision_is_not_reported_as_a_replay()
    {
        var clock = TimeProvider.System;
        await using var db = new BifrostDbContext(new DbContextOptionsBuilder<BifrostDbContext>()
            .UseInMemoryDatabase($"intake-fault-{Guid.NewGuid():N}").AddInterceptors(new ThrowOnNonce()).Options);
        var key = Key.New();
        var user = new UserAccount { DisplayName = "m", GitHubLogin = "m", BifrostIdentity = "m", NormalizedBifrostIdentity = "M", NormalizedGitHubLogin = "M" };
        user.Membership = new Domain.Membership { UserAccountId = user.Id, Status = MembershipStatus.Active };
        db.UserAccounts.Add(user);
        db.MindKeys.Add(new MindKey { PersonaId = "mindx", UserAccountId = user.Id, PublicKey = key.Public, RegisteredAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var audit = new AuditTrailService(db, clock);
        var service = new MindIntakeService(db, new SealedBallotService(db, audit, clock), audit, clock);
        var motion = Guid.NewGuid();

        await Assert.ThrowsAsync<DbUpdateException>(() => service.HandleAsync(motion, Sign(key, "mindx", "commit", motion, commitment: new string('a', 64)), default));
    }

    private sealed class FailOnNonceDelete : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData e, InterceptionResult<int> r, CancellationToken ct = default) =>
            e.Context!.ChangeTracker.Entries<MindNonce>().Any(x => x.State == EntityState.Deleted)
                ? throw new DbUpdateConcurrencyException("the other request deleted these rows first")
                : ValueTask.FromResult(r);
    }

    [Fact]
    public async Task A_prune_that_loses_a_race_never_fails_a_valid_request()
    {
        var clock = TimeProvider.System;
        await using var db = new BifrostDbContext(new DbContextOptionsBuilder<BifrostDbContext>()
            .UseInMemoryDatabase($"intake-prune-{Guid.NewGuid():N}").AddInterceptors(new FailOnNonceDelete()).Options);
        var key = Key.New();
        var user = new UserAccount { DisplayName = "m", GitHubLogin = "m", BifrostIdentity = "m", NormalizedBifrostIdentity = "M", NormalizedGitHubLogin = "M" };
        user.Membership = new Domain.Membership { UserAccountId = user.Id, Status = MembershipStatus.Active };
        var now = DateTimeOffset.UtcNow;
        var motion = new Motion
        {
            CreatedByUserAccountId = user.Id, Scope = MotionScope.Management, Kind = MotionKind.SealedBallot, Title = "t", Summary = "s", Candidates = "Ana\nBo",
            OpensAtUtc = now, CommitClosesAtUtc = now.AddHours(1), ClosesAtUtc = now.AddHours(2), CreatedAtUtc = now
        };
        db.UserAccounts.Add(user);
        db.Motions.Add(motion);
        db.MindKeys.Add(new MindKey { PersonaId = "mindx", UserAccountId = user.Id, PublicKey = key.Public, RegisteredAtUtc = now });
        db.MindNonces.Add(new MindNonce { PersonaId = "mindx", Nonce = "expired-nonce-expired-1", SeenAtUtc = now.AddHours(-2) });
        await db.SaveChangesAsync();
        var audit = new AuditTrailService(db, clock);
        var service = new MindIntakeService(db, new SealedBallotService(db, audit, clock), audit, clock);

        var receipt = await service.HandleAsync(motion.Id, Sign(key, "mindx", "commit", motion.Id, commitment: new string('a', 64)), default);

        Assert.Equal(new string('a', 64), receipt.Commitment);
        Assert.Equal(1, await db.MindNonces.CountAsync(x => x.Nonce != "expired-nonce-expired-1")); // the nonce was still spent
    }
}
