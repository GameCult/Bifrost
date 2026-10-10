using System.Net;
using System.Reflection;
using System.Text.Json;
using Bifrost.Web.Configuration;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.ConceptVotes;
using Bifrost.Web.Features.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Bifrost.Web.Tests;

public sealed class ConceptAppraisalServiceTests
{
    private const string Collection = "aetheria-concept-mill";
    private static readonly IPAddress Client = IPAddress.Parse("203.0.113.7");

    [Fact]
    public async Task Anonymous_device_voter_can_cast_and_is_counted()
    {
        await using var f = new Fixture();
        await f.AddSubject("ship-a");

        var result = await f.Cast("ship-a", f.Anonymous("device-one"), ConceptStance.Up);

        Assert.Equal(CastResult.Recorded, result);
        var tally = (await f.Service.GetTalliesAsync(Collection, default))["ship-a"];
        Assert.Equal(new ConceptTally(1, 0, new StanceCounts(0, 0)), tally);
    }

    [Fact]
    public async Task Anonymous_without_device_id_gets_no_identity_and_cast_is_refused()
    {
        await using var f = new Fixture();
        await f.AddSubject("ship-a");

        Assert.Null(VoterIdentity.For(CurrentBifrostActor.Anonymous, null));
        Assert.Null(VoterIdentity.For(CurrentBifrostActor.Anonymous, "has:colon"));
        Assert.Null(VoterIdentity.For(CurrentBifrostActor.Anonymous, string.Empty));
        Assert.Equal(CastResult.NoVoter, await f.Cast("ship-a", null, ConceptStance.Up));
        Assert.Empty(f.Db.ConceptAppraisals);
    }

    [Theory]
    [InlineData("abc\n")]
    [InlineData("abc\r\n")]
    [InlineData("abc ")]
    [InlineData("abc\t")]
    [InlineData("abc\0")]
    public async Task A_device_id_with_trailing_whitespace_or_control_char_cannot_cast_beside_its_bare_form(string forged)
    {
        await using var f = new Fixture();
        await f.AddSubject("ship-a");

        Assert.Equal(CastResult.Recorded, await f.Cast("ship-a", f.Anonymous("abc"), ConceptStance.Up));

        Assert.Null(VoterIdentity.For(CurrentBifrostActor.Anonymous, forged));
        Assert.Equal(CastResult.NoVoter, await f.Cast("ship-a", VoterIdentity.For(CurrentBifrostActor.Anonymous, forged), ConceptStance.Down));
        Assert.Single(f.Db.ConceptAppraisals);
    }

    [Fact]
    public async Task A_changed_stance_is_saved_and_read_back_through_a_fresh_context()
    {
        var dbName = $"concept-{Guid.NewGuid():N}";
        await using var first = new Fixture(dbName);
        await first.AddSubject("ship-a");
        Assert.Equal(CastResult.Recorded, await first.Cast("ship-a", first.Anonymous("device-one"), ConceptStance.Up));
        var other = IPAddress.Parse("198.51.100.9");

        // Production scopes one DbContext per request: the recast gets a context that has never tracked the row.
        await using (var second = new Fixture(dbName))
        {
            second.Clock.Advance(TimeSpan.FromMinutes(5));
            Assert.Equal(
                CastResult.Changed,
                await second.Cast("ship-a", second.Anonymous("device-one"), ConceptStance.Down, other));
        }

        await using var reader = new Fixture(dbName);
        var row = Assert.Single(reader.Db.ConceptAppraisals);
        Assert.Equal(ConceptStance.Down, row.Stance);
        Assert.Equal(NetworkPrefix.Tag(other, 32, 56, Fixture.Secret), row.NetworkTag);
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 12, 5, 0, TimeSpan.Zero), row.CastAtUtc);
        Assert.Equal(new ConceptTally(0, 1, new StanceCounts(0, 0)), (await reader.Service.GetTalliesAsync(Collection, default))["ship-a"]);
    }

    [Fact]
    public async Task Voter_class_is_stamped_at_cast()
    {
        await using var f = new Fixture();
        await f.AddSubject("ship-a");
        var account = f.Account(MembershipStatus.Authenticated);
        var member = f.Account(MembershipStatus.Active);

        await f.Cast("ship-a", f.Anonymous("device-one"), ConceptStance.Up);
        await f.Cast("ship-a", VoterIdentity.For(account, null), ConceptStance.Up);
        await f.Cast("ship-a", VoterIdentity.For(member, null), ConceptStance.Up);

        var rows = f.Db.ConceptAppraisals.ToList();
        Assert.Equal(VoterClass.Anonymous, rows.Single(x => x.VoterKey == "device:device-one").VoterClass);
        Assert.Null(rows.Single(x => x.VoterKey == "device:device-one").UserAccountId);
        var accountRow = rows.Single(x => x.UserAccountId == account.UserAccount!.Id);
        var memberRow = rows.Single(x => x.UserAccountId == member.UserAccount!.Id);
        Assert.Equal(VoterClass.Account, accountRow.VoterClass);
        Assert.Equal(VoterClass.Member, memberRow.VoterClass);

        // Membership ends; the same account votes again with the opposite stance. The row keeps its stamp.
        member.UserAccount!.Membership!.Status = MembershipStatus.Authenticated;
        var recast = await f.Cast("ship-a", VoterIdentity.For(member, null), ConceptStance.Down);

        Assert.Equal(CastResult.Changed, recast);
        Assert.Equal(VoterClass.Member, f.Db.ConceptAppraisals.Single(x => x.Id == memberRow.Id).VoterClass);
    }

    [Fact]
    public async Task Second_cast_by_the_same_key_changes_stance_without_a_new_row()
    {
        await using var f = new Fixture();
        await f.AddSubject("ship-a");
        var voter = f.Anonymous("device-one");

        Assert.Equal(CastResult.Recorded, await f.Cast("ship-a", voter, ConceptStance.Up));
        Assert.Equal(CastResult.Changed, await f.Cast("ship-a", voter, ConceptStance.Down));
        Assert.Equal(CastResult.Unchanged, await f.Cast("ship-a", voter, ConceptStance.Down));

        var row = Assert.Single(f.Db.ConceptAppraisals);
        Assert.Equal(ConceptStance.Down, row.Stance);
    }

    [Fact]
    public async Task Member_weight_does_not_weight_counts()
    {
        await using var f = new Fixture();
        await f.AddSubject("ship-a");
        var member = f.Account(MembershipStatus.Active, weight: 5m);
        Assert.Equal(5m, member.EffectiveVotingWeight);

        await f.Cast("ship-a", VoterIdentity.For(member, null), ConceptStance.Up);

        var tally = (await f.Service.GetTalliesAsync(Collection, default))["ship-a"];
        Assert.Equal(1, tally.Up);
        Assert.Equal(1, tally.Members.Up);
    }

    [Fact]
    public async Task Member_tally_is_a_filter_over_the_same_rows()
    {
        await using var f = new Fixture();
        await f.AddSubject("ship-a");
        await f.Cast("ship-a", f.Anonymous("device-one"), ConceptStance.Up);
        await f.Cast("ship-a", f.Anonymous("device-two"), ConceptStance.Down);
        await f.Cast("ship-a", VoterIdentity.For(f.Account(MembershipStatus.Authenticated), null), ConceptStance.Up);
        await f.Cast("ship-a", VoterIdentity.For(f.Account(MembershipStatus.Active), null), ConceptStance.Up);
        await f.Cast("ship-a", VoterIdentity.For(f.Account(MembershipStatus.Active), null), ConceptStance.Down);

        var tally = (await f.Service.GetTalliesAsync(Collection, default))["ship-a"];
        var rows = f.Db.ConceptAppraisals.ToList();

        Assert.Equal(rows.Count(x => x.Stance == ConceptStance.Up), tally.Up);
        Assert.Equal(rows.Count(x => x.Stance == ConceptStance.Down), tally.Down);
        Assert.Equal(rows.Count(x => x.Stance == ConceptStance.Up && x.VoterClass == VoterClass.Member), tally.Members.Up);
        Assert.Equal(rows.Count(x => x.Stance == ConceptStance.Down && x.VoterClass == VoterClass.Member), tally.Members.Down);
        Assert.Equal(new ConceptTally(3, 2, new StanceCounts(1, 1)), tally);
    }

    [Fact]
    public async Task Tally_carries_no_voter_identity()
    {
        await using var f = new Fixture();
        await f.AddSubject("ship-a");
        var account = f.Account(MembershipStatus.Authenticated);
        var member = f.Account(MembershipStatus.Active);
        await f.Cast("ship-a", f.Anonymous("device-secret-id"), ConceptStance.Up);
        await f.Cast("ship-a", VoterIdentity.For(account, null), ConceptStance.Up);
        await f.Cast("ship-a", VoterIdentity.For(member, null), ConceptStance.Down);

        Assert.Equal(
            new[] { "Down", "Members", "Up" },
            typeof(ConceptTally).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name).Order().ToArray());
        Assert.Equal(
            new[] { "Down", "Up" },
            typeof(StanceCounts).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name).Order().ToArray());

        var json = JsonSerializer.Serialize(await f.Service.GetTalliesAsync(Collection, default));
        var view = JsonSerializer.Serialize(
            await f.Service.GetSubjectAsync(Collection, "ship-a", VoterIdentity.For(account, null), default));
        foreach (var text in new[] { json, view })
        {
            Assert.DoesNotContain("device:", text);
            Assert.DoesNotContain("account:", text);
            Assert.DoesNotContain("device-secret-id", text);
            foreach (var id in new[] { account.UserAccount!.Id, member.UserAccount!.Id })
            {
                Assert.DoesNotContain(id.ToString("D"), text);
                Assert.DoesNotContain(id.ToString("N"), text);
            }

            foreach (var tag in f.Db.ConceptAppraisals.Select(x => x.NetworkTag).Distinct())
            {
                Assert.DoesNotContain(tag, text);
            }
        }
    }

    [Fact]
    public async Task Votes_on_unknown_or_retired_keys_are_refused_and_create_nothing()
    {
        await using var f = new Fixture();
        await f.AddSubject("ship-a");
        await f.AddSubject("ship-old", retired: true);
        var subjectsBefore = f.Db.ConceptSubjects.Count();
        var voter = f.Anonymous("device-one");

        Assert.Equal(CastResult.UnknownSubject, await f.Cast("ship-missing", voter, ConceptStance.Up));
        Assert.Equal(CastResult.UnknownSubject, await f.Cast("Ship_A", voter, ConceptStance.Up));
        Assert.Equal(CastResult.RetiredSubject, await f.Cast("ship-old", voter, ConceptStance.Up));

        Assert.Equal(subjectsBefore, f.Db.ConceptSubjects.Count());
        Assert.Empty(f.Db.ConceptAppraisals);
        Assert.DoesNotContain("ship-old", (await f.Service.GetTalliesAsync(Collection, default)).Keys);
        Assert.Contains("ship-a", (await f.Service.GetTalliesAsync(Collection, default)).Keys);
    }

    [Fact]
    public async Task Retract_removes_the_row_and_the_count()
    {
        await using var f = new Fixture();
        await f.AddSubject("ship-a");
        var voter = f.Anonymous("device-one");
        await f.Cast("ship-a", voter, ConceptStance.Up);
        await f.Cast("ship-a", f.Anonymous("device-two"), ConceptStance.Up);

        Assert.True(await f.Service.RetractAsync(Collection, "ship-a", voter, default));
        Assert.False(await f.Service.RetractAsync(Collection, "ship-a", voter, default));

        Assert.Equal(1, (await f.Service.GetTalliesAsync(Collection, default))["ship-a"].Up);
        Assert.Single(f.Db.ConceptAppraisals);
    }

    [Fact]
    public async Task Network_tag_is_an_hmac_never_a_raw_address()
    {
        await using var f = new Fixture();
        await f.AddSubject("ship-a");
        await f.Cast("ship-a", f.Anonymous("device-one"), ConceptStance.Up);

        var tag = f.Db.ConceptAppraisals.Single().NetworkTag;

        Assert.Matches("^[0-9a-f]{64}$", tag);
        Assert.Equal(NetworkPrefix.Tag(Client, 32, 56, Fixture.Secret), tag);
    }

    [Fact]
    public async Task Unique_violation_at_insert_becomes_a_change()
    {
        var dbName = $"race-{Guid.NewGuid():N}";
        var racer = new RacingInterceptor(dbName);
        await using var f = new Fixture(dbName, racer);
        await f.AddSubject("ship-a");
        racer.Subject = f.Db.ConceptSubjects.Single();

        var result = await f.Cast("ship-a", f.Anonymous("device-one"), ConceptStance.Down);

        Assert.Equal(CastResult.Changed, result);
        Assert.Equal(1, racer.Fired);
        var row = Assert.Single(f.Db.ConceptAppraisals);
        Assert.Equal(ConceptStance.Down, row.Stance);
    }

    [Fact]
    public void Unique_indexes_are_declared_in_model_and_migration()
    {
        var options = new DbContextOptionsBuilder<BifrostDbContext>().UseNpgsql("Host=unused").Options;
        using var db = new BifrostDbContext(options);

        Assert.Contains(
            db.Model.FindEntityType(typeof(ConceptAppraisal))!.GetIndexes(),
            i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(["SubjectId", "VoterKey"]));
        Assert.Contains(
            db.Model.FindEntityType(typeof(ConceptSubject))!.GetIndexes(),
            i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(["Collection", "Key"]));

        var script = db.Database.GenerateCreateScript();
        Assert.Matches("CREATE UNIQUE INDEX \"IX_ConceptAppraisals_SubjectId_VoterKey\"", script);
        Assert.Matches("CREATE UNIQUE INDEX \"IX_ConceptSubjects_Collection_Key\"", script);

    }

    [Fact]
    public async Task Purge_removes_only_rows_in_tag_and_window_and_audits()
    {
        await using var f = new Fixture();
        await f.AddSubject("ship-a");
        var admin = f.Admin();
        var t0 = f.Clock.GetUtcNow();
        var other = IPAddress.Parse("198.51.100.9");

        await f.Cast("ship-a", f.Anonymous("device-in"), ConceptStance.Up);
        await f.Cast("ship-a", f.Anonymous("device-other-net"), ConceptStance.Up, other);
        f.Clock.Advance(TimeSpan.FromHours(2));
        await f.Cast("ship-a", f.Anonymous("device-late"), ConceptStance.Up);
        var tag = NetworkPrefix.Tag(Client, 32, 56, Fixture.Secret);

        var removed = await f.Service.PurgeAsync(admin, tag, t0, t0.AddHours(1), default);

        Assert.Equal(1, removed);
        Assert.Equal(
            ["device:device-late", "device:device-other-net"],
            f.Db.ConceptAppraisals.Select(x => x.VoterKey).ToList().Order().ToArray());
        var audit = Assert.Single(f.Db.AuditEvents);
        Assert.Equal("ConceptAppraisal", audit.EntityType);
        Assert.Equal("Purge", audit.Action);
        Assert.DoesNotContain(tag, audit.Detail);
        Assert.DoesNotContain("tag", audit.Detail);
        Assert.Equal(admin.UserAccount!.Id, audit.ActorUserAccountId);
        Assert.Contains("from=" + t0.ToString("O"), audit.Detail);
        Assert.Contains("count=1", audit.Detail);
        Assert.DoesNotContain("device", audit.Detail);

        var refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => f.Service.PurgeAsync(f.Account(MembershipStatus.Active), tag, t0, t0.AddDays(1), default));
        Assert.DoesNotContain(tag, refused.Message);
        Assert.Equal("Only a platform administrator can purge appraisals.", refused.Message);
        Assert.Equal(2, f.Db.ConceptAppraisals.Count());
        Assert.Single(f.Db.AuditEvents);
    }

    [Fact]
    public async Task Unique_violation_with_no_surviving_row_is_not_swallowed()
    {
        var dbName = $"race-{Guid.NewGuid():N}";
        var racer = new RacingInterceptor(dbName) { InsertRival = false };
        await using var f = new Fixture(dbName, racer);
        await f.AddSubject("ship-a");
        racer.Subject = f.Db.ConceptSubjects.Single();

        await Assert.ThrowsAsync<DbUpdateException>(
            () => f.Cast("ship-a", f.Anonymous("device-one"), ConceptStance.Up));
    }

    [Fact]
    public async Task A_changed_cast_restamps_tag_and_time()
    {
        await using var f = new Fixture();
        await f.AddSubject("ship-a");
        var voter = f.Anonymous("device-one");
        await f.Cast("ship-a", voter, ConceptStance.Up, IPAddress.Parse("198.51.100.9"));
        var first = f.Db.ConceptAppraisals.Single().CastAtUtc;
        f.Clock.Advance(TimeSpan.FromMinutes(5));

        await f.Cast("ship-a", voter, ConceptStance.Down);

        var row = f.Db.ConceptAppraisals.Single();
        Assert.Equal(NetworkPrefix.Tag(Client, 32, 56, Fixture.Secret), row.NetworkTag);
        Assert.Equal(first.AddMinutes(5), row.CastAtUtc);
    }

    [Fact]
    public async Task Unknown_client_address_leaves_no_tag_and_purge_never_matches_the_empty_tag()
    {
        await using var f = new Fixture();
        await f.AddSubject("ship-a");
        await f.Service.CastAsync(Collection, "ship-a", f.Anonymous("device-one"), ConceptStance.Up, null, default);

        Assert.Equal(string.Empty, f.Db.ConceptAppraisals.Single().NetworkTag);
        var t0 = f.Clock.GetUtcNow();
        Assert.Equal(0, await f.Service.PurgeAsync(f.Admin(), string.Empty, t0.AddDays(-1), t0.AddDays(1), default));
        Assert.Single(f.Db.ConceptAppraisals);
        Assert.Empty(f.Db.AuditEvents);
    }

    [Fact]
    public async Task Purge_window_excludes_its_end_and_includes_its_start()
    {
        await using var f = new Fixture();
        await f.AddSubject("ship-a");
        var t0 = f.Clock.GetUtcNow();
        await f.Cast("ship-a", f.Anonymous("device-one"), ConceptStance.Up);
        var tag = NetworkPrefix.Tag(Client, 32, 56, Fixture.Secret);

        Assert.Equal(0, await f.Service.PurgeAsync(f.Admin(), tag, t0.AddDays(-1), t0, default));
        Assert.Equal(1, await f.Service.PurgeAsync(f.Admin(), tag, t0, t0.AddSeconds(1), default));
    }

    [Fact]
    public async Task Retract_by_no_voter_or_on_unknown_subject_is_a_no_op()
    {
        await using var f = new Fixture();
        await f.AddSubject("ship-a");
        var voter = f.Anonymous("device-one");
        await f.Cast("ship-a", voter, ConceptStance.Up);

        Assert.False(await f.Service.RetractAsync(Collection, "ship-a", null, default));
        Assert.False(await f.Service.RetractAsync(Collection, "ship-missing", voter, default));
        Assert.False(await f.Service.RetractAsync("other-collection", "ship-a", voter, default));
        Assert.Single(f.Db.ConceptAppraisals);
    }

    [Fact]
    public async Task Subject_page_view_counts_only_its_own_subject_and_shows_the_voters_own_stance()
    {
        await using var f = new Fixture();
        await f.AddSubject("ship-a");
        await f.AddSubject("ship-b");
        await f.AddSubject("ship-old", retired: true);
        var mine = f.Anonymous("device-one");
        await f.Cast("ship-a", mine, ConceptStance.Down);
        await f.Cast("ship-a", VoterIdentity.For(f.Account(MembershipStatus.Active), null), ConceptStance.Up);
        await f.Cast("ship-a", VoterIdentity.For(f.Account(MembershipStatus.Authenticated), null), ConceptStance.Up);
        await f.Cast("ship-b", f.Anonymous("device-two"), ConceptStance.Up);

        var view = await f.Service.GetSubjectAsync(Collection, "ship-a", mine, default);
        var stranger = await f.Service.GetSubjectAsync(Collection, "ship-a", f.Anonymous("device-nine"), default);
        var anon = await f.Service.GetSubjectAsync(Collection, "ship-a", null, default);

        Assert.Equal(new ConceptTally(2, 1, new StanceCounts(1, 0)), view!.Tally);
        Assert.Equal(ConceptStance.Down, view.YourStance);
        Assert.Null(stranger!.YourStance);
        Assert.Null(anon!.YourStance);
        Assert.Null(await f.Service.GetSubjectAsync(Collection, "ship-old", mine, default));
        Assert.Null(await f.Service.GetSubjectAsync(Collection, "ship-missing", mine, default));
    }

    [Fact]
    public async Task Collection_and_key_must_both_be_slugs_even_when_a_row_exists()
    {
        await using var f = new Fixture();
        await f.AddSubject("Ship_A");
        f.Db.ConceptSubjects.Add(new ConceptSubject
        {
            Collection = "Bad_Collection",
            Key = "ship-a",
            Title = "t",
            RegisteredAtUtc = f.Clock.GetUtcNow()
        });
        await f.Db.SaveChangesAsync();
        var voter = f.Anonymous("device-one");

        Assert.Equal(CastResult.UnknownSubject, await f.Cast("Ship_A", voter, ConceptStance.Up));
        Assert.Equal(
            CastResult.UnknownSubject,
            await f.Service.CastAsync("Bad_Collection", "ship-a", voter, ConceptStance.Up, Client, default));
        Assert.Empty(f.Db.ConceptAppraisals);
    }

    [Fact]
    public async Task Voter_keys_have_their_documented_shape()
    {
        await using var f = new Fixture();
        var account = f.Account(MembershipStatus.Authenticated);

        Assert.Equal("device:abc-DEF_123", VoterIdentity.For(CurrentBifrostActor.Anonymous, "abc-DEF_123")!.Key);
        Assert.Equal("account:" + account.UserAccount!.Id.ToString("N"), VoterIdentity.For(account, "ignored")!.Key);
        Assert.Null(VoterIdentity.For(CurrentBifrostActor.Anonymous, new string('a', 101)));
        Assert.NotNull(VoterIdentity.For(CurrentBifrostActor.Anonymous, new string('a', 100)));
    }

    private sealed class RacingInterceptor(string dbName) : SaveChangesInterceptor
    {
        public bool InsertRival { get; init; } = true;

        public ConceptSubject? Subject { get; set; }

        public int Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var adding = eventData.Context!.ChangeTracker.Entries<ConceptAppraisal>()
                .Any(e => e.State == EntityState.Added);
            if (Subject is null || !adding || Fired > 0)
            {
                return result;
            }

            Fired++;
            if (!InsertRival)
            {
                throw new DbUpdateException("conflict", new PostgresException("duplicate", "ERROR", "ERROR", "23505"));
            }

            var options = new DbContextOptionsBuilder<BifrostDbContext>().UseInMemoryDatabase(dbName).Options;
            await using (var other = new BifrostDbContext(options))
            {
                other.ConceptAppraisals.Add(new ConceptAppraisal
                {
                    SubjectId = Subject.Id,
                    VoterKey = "device:device-one",
                    Stance = ConceptStance.Up,
                    VoterClass = VoterClass.Anonymous,
                    CastAtUtc = DateTimeOffset.UtcNow
                });
                await other.SaveChangesAsync(cancellationToken);
            }

            throw new DbUpdateException(
                "conflict",
                new PostgresException("duplicate", "ERROR", "ERROR", "23505"));
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public const string Secret = "test-tag-secret";

        public Fixture(string? dbName = null, IInterceptor? interceptor = null)
        {
            var builder = new DbContextOptionsBuilder<BifrostDbContext>()
                .UseInMemoryDatabase(dbName ?? $"concept-{Guid.NewGuid():N}");
            if (interceptor is not null)
            {
                builder.AddInterceptors(interceptor);
            }

            Db = new BifrostDbContext(builder.Options);
            Clock = new ManualClock(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
            Service = new ConceptAppraisalService(
                Db,
                new AuditTrailService(Db, Clock),
                Clock,
                Options.Create(new ConceptVoteOptions { NetworkTagSecret = Secret }));
        }

        public BifrostDbContext Db { get; }

        public ManualClock Clock { get; }

        public ConceptAppraisalService Service { get; }

        public async Task AddSubject(string key, bool retired = false)
        {
            Db.ConceptSubjects.Add(new ConceptSubject
            {
                Collection = Collection,
                Key = key,
                Title = key,
                RegisteredAtUtc = Clock.GetUtcNow(),
                RetiredAtUtc = retired ? Clock.GetUtcNow() : null
            });
            await Db.SaveChangesAsync();
        }

        public Task<CastResult> Cast(string key, VoterIdentity? voter, ConceptStance stance, IPAddress? client = null) =>
            Service.CastAsync(Collection, key, voter, stance, client ?? Client, default);

        public VoterIdentity Anonymous(string deviceId) =>
            VoterIdentity.For(CurrentBifrostActor.Anonymous, deviceId)!;

        public CurrentBifrostActor Account(MembershipStatus status, decimal weight = 0m, bool admin = false)
        {
            var user = new UserAccount
            {
                DisplayName = "voter",
                GitHubLogin = "voter",
                NormalizedGitHubLogin = "voter",
                CreatedAtUtc = Clock.GetUtcNow(),
                LastSeenAtUtc = Clock.GetUtcNow()
            };
            user.Membership = new Membership
            {
                UserAccountId = user.Id,
                Status = status,
                IsPlatformAdmin = admin,
                CreatedAtUtc = Clock.GetUtcNow()
            };
            if (weight > 0m)
            {
                user.Membership.TierSnapshots.Add(new TierSnapshot { Weight = weight, IsCurrent = true });
            }

            Db.UserAccounts.Add(user);
            Db.SaveChanges();
            return new CurrentBifrostActor(true, user);
        }

        public CurrentBifrostActor Admin()
        {
            return Account(MembershipStatus.Active, admin: true);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
