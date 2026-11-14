using System.Net;
using System.Text;
using System.Text.Json;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Membership;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Bifrost.Web.Tests.AccountMergeSagaTests;

namespace Bifrost.Web.Tests;

/// <summary>
/// The merge notice feed from the chat host's side: pulled over HTTP with the bearer, acked over HTTP, against merges the saga rig made;
/// bounds and the unique ack key from the feed itself over seeded rows. Notices are derived from the merge row: no stored status.
/// </summary>
public sealed class MergeNoticeFeedTests(MergePostgres postgres) : IClassFixture<MergePostgres>
{
    private const string Token = "chat-notices-test-token-0123456789";

    private static readonly string[] PayloadKeys =
        ["type", "merge_id", "occurred_at", "grace_ends_at", "old_subject", "new_subject", "old_preferred_username", "new_preferred_username"];

    private static async Task<JsonElement[]> PullAsync(Rig rig, string query = "")
    {
        using var client = rig.Host.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/merge-notices/chat" + query);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.EnumerateArray().Select(x => x.Clone()).ToArray();
    }

    private static async Task<HttpStatusCode> AckAsync(Rig rig, Guid merge, string type)
    {
        using var client = rig.Host.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/merge-notices/chat/ack")
        {
            Content = new StringContent($"{{\"merge_id\":\"{merge}\",\"type\":\"{type}\"}}", Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static string[] Types(JsonElement[] notices) => [.. notices.Select(x => x.GetProperty("type").GetString()!)];

    private static Task<(Guid Merge, Party Survivor, Party Folded)> CommittedAsync(Rig rig) => MergeNoticeAuthTests.Committed(rig);

    private static async Task FinalizeAsync(Rig rig)
    {
        rig.Clock.Advance(AccountMergeLedger.Grace + TimeSpan.FromMinutes(1));
        await rig.InScopeAsync(async sp => await sp.GetRequiredService<AccountMergeLedger>().FinalizeDueAsync(rig.Clock.Now, default));
    }

    // ---- what a notice says ----

    [Fact]
    public async Task A_committed_merge_yields_one_merged_notice_with_exactly_the_documented_keys_and_nothing_that_names_a_person()
    {
        using var rig = new Rig(chatNoticesToken: Token);
        var (merge, survivor, folded) = await CommittedAsync(rig);
        var committed = await rig.MergeAsync(merge);

        var notice = Assert.Single(await PullAsync(rig));

        Assert.Equal(PayloadKeys.Order(), notice.EnumerateObject().Select(x => x.Name).Order());
        Assert.Equal("merged", notice.GetProperty("type").GetString());
        Assert.Equal(merge.ToString("D"), notice.GetProperty("merge_id").GetString());
        Assert.Equal(committed.CommittedUtc, notice.GetProperty("occurred_at").GetDateTimeOffset());
        Assert.Equal(committed.GraceEndsUtc, notice.GetProperty("grace_ends_at").GetDateTimeOffset());
        Assert.Equal(folded.Id.ToString("D"), notice.GetProperty("old_subject").GetString());
        Assert.Equal(survivor.Id.ToString("D"), notice.GetProperty("new_subject").GetString());
        Assert.Equal("folded", notice.GetProperty("old_preferred_username").GetString());
        Assert.Equal("survivor", notice.GetProperty("new_preferred_username").GetString());
        Assert.DoesNotContain(folded.HeimdallId, notice.GetRawText());
        Assert.DoesNotContain(survivor.HeimdallId, notice.GetRawText());
    }

    [Fact]
    public async Task An_undone_merge_yields_merged_then_undone_and_each_is_acked_alone()
    {
        using var rig = new Rig(chatNoticesToken: Token);
        var (merge, survivor, folded) = await CommittedAsync(rig);
        rig.Clock.Advance(TimeSpan.FromHours(1));
        Assert.IsType<UndoOutcome.Requested>(await rig.InScopeAsync(sp =>
            sp.GetRequiredService<AccountMergeSaga>().RequestUndoAsync(merge, survivor.Id, [rig.Moved(survivor, folded, "discord")], default)));
        Assert.Equal(AccountMergeState.Undone, (await rig.AdvanceAsync(merge)).State);
        var stored = await rig.MergeAsync(merge);

        var notices = await PullAsync(rig);

        Assert.Equal(["merged", "undone"], Types(notices));
        Assert.Equal(stored.UndoneUtc, notices[1].GetProperty("occurred_at").GetDateTimeOffset());
        Assert.Equal(stored.GraceEndsUtc, notices[1].GetProperty("grace_ends_at").GetDateTimeOffset());
        Assert.Equal(HttpStatusCode.NoContent, await AckAsync(rig, merge, "merged"));
        Assert.Equal(["undone"], Types(await PullAsync(rig)));
    }

    [Fact]
    public async Task A_finalized_merge_yields_merged_then_final()
    {
        using var rig = new Rig(chatNoticesToken: Token);
        var (merge, _, _) = await CommittedAsync(rig);
        await FinalizeAsync(rig);
        var stored = await rig.MergeAsync(merge);
        Assert.Equal(AccountMergeState.Final, stored.State);

        var notices = await PullAsync(rig);

        Assert.Equal(["merged", "final"], Types(notices));
        Assert.Equal(stored.FinalizedUtc, notices[1].GetProperty("occurred_at").GetDateTimeOffset());
    }

    [Fact]
    public async Task A_confirmed_a_frozen_and_a_failed_merge_yield_nothing()
    {
        using var rig = new Rig(chatNoticesToken: Token);
        var survivor = await rig.AccountAsync("survivor", "twitch");
        var frozen = await rig.AccountAsync("frozen", "discord");
        var stale = await rig.AccountAsync("stale", "patreon");
        var confirmed = await rig.ConfirmedAsync(survivor, frozen, rig.Weak(survivor, "twitch"), rig.Weak(frozen, "discord"));
        Assert.Equal(AccountMergeState.Confirmed, (await rig.MergeAsync(confirmed)).State);
        Assert.IsType<MergeStanding.Frozen>(await rig.StandingAsync(frozen));
        Assert.Empty(await PullAsync(rig));

        var other = await rig.AccountAsync("other", "twitch2");
        var failing = await rig.ConfirmedAsync(other, stale, rig.Weak(other, "twitch2"), rig.Weak(stale, "patreon"));
        rig.Clock.Advance(TimeSpan.FromSeconds(400));
        Assert.Equal(AccountMergeState.Failed, (await rig.AdvanceAsync(failing)).State);

        // The first merge is still Confirmed and the second Failed: neither committed, neither is told.
        Assert.Empty(await PullAsync(rig));
    }

    // ---- ack ----

    [Fact]
    public async Task An_ack_is_idempotent_and_the_notice_is_gone_from_the_next_read()
    {
        using var rig = new Rig(chatNoticesToken: Token);
        var (merge, _, _) = await CommittedAsync(rig);
        Assert.Single(await PullAsync(rig));

        Assert.Equal(HttpStatusCode.NoContent, await AckAsync(rig, merge, "merged"));
        Assert.Equal(HttpStatusCode.NoContent, await AckAsync(rig, merge, "merged"));

        Assert.Empty(await PullAsync(rig));
        Assert.Equal(1, await rig.InScopeAsync(async sp => await sp.GetRequiredService<BifrostDbContext>().AccountMergeNoticeAcks.CountAsync()));
    }

    [Fact]
    public async Task An_ack_of_a_notice_the_feed_would_not_emit_is_refused_and_stores_nothing()
    {
        using var rig = new Rig(chatNoticesToken: Token);
        var (merge, _, _) = await CommittedAsync(rig); // merged only: not undone, not final

        Assert.Equal(HttpStatusCode.NotFound, await AckAsync(rig, merge, "undone"));
        Assert.Equal(HttpStatusCode.NotFound, await AckAsync(rig, merge, "final"));
        Assert.Equal(HttpStatusCode.NotFound, await AckAsync(rig, Guid.NewGuid(), "merged"));
        Assert.Equal(HttpStatusCode.BadRequest, await AckAsync(rig, merge, "created"));
        Assert.Equal(HttpStatusCode.BadRequest, await AckAsync(rig, merge, "Merged"));

        Assert.Equal(0, await rig.InScopeAsync(async sp => await sp.GetRequiredService<BifrostDbContext>().AccountMergeNoticeAcks.CountAsync()));
        Assert.Single(await PullAsync(rig));
    }

    [Fact]
    public async Task A_malformed_ack_body_is_400_and_stores_nothing()
    {
        using var rig = new Rig(chatNoticesToken: Token);
        await CommittedAsync(rig);
        using var client = rig.Host.NewClient();
        foreach (var body in new[] { "", "not json", "{}", "{\"merge_id\":\"nope\",\"type\":\"merged\"}", "{\"type\":\"merged\"}", "[]" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/merge-notices/chat/ack") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Equal(0, await rig.InScopeAsync(async sp => await sp.GetRequiredService<BifrostDbContext>().AccountMergeNoticeAcks.CountAsync()));
    }

    // ---- order, bounds, after: the feed over seeded rows ----

    private static readonly DateTimeOffset T0 = AccountMergeLedgerTests.T0;

    private static async Task<(AccountMergeLedgerTests.World World, UserAccount Survivor, UserAccount Folded)> SeededAsync(Func<Guid, Guid, IEnumerable<AccountMerge>> merges, string? postgres = null)
    {
        var world = postgres is null ? new AccountMergeLedgerTests.World() : AccountMergeLedgerTests.World.OnPostgres(postgres);
        await using (var migrate = world.Db())
        {
            if (postgres is not null)
            {
                await migrate.Database.MigrateAsync();
            }
        }

        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded");
        await using var db = world.Db();
        db.AccountMerges.AddRange(merges(survivor.Id, folded.Id));
        await db.SaveChangesAsync();
        return (world, survivor, folded);
    }

    private static AccountMerge Committed(Guid survivor, Guid folded, DateTimeOffset at, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(), State = AccountMergeState.Committed, SurvivorUserAccountId = survivor, FoldedUserAccountId = folded,
        CreatedUtc = at, CommittedUtc = at, GraceEndsUtc = at + AccountMergeLedger.Grace
    };

    private static MergeNoticeFeed Feed(BifrostDbContext db) => new(db, TimeProvider.System);

    [Fact]
    public async Task Notices_come_ordered_by_occurred_at_then_merge_id_then_kind_and_after_keeps_only_later_ones()
    {
        var low = new Guid("00000000-0000-0000-0000-000000000001");
        var high = new Guid("00000000-0000-0000-0000-000000000002");
        var (world, _, _) = await SeededAsync((s, f) =>
        [
            Committed(s, f, T0.AddHours(3), high),
            Committed(s, f, T0.AddHours(3), low),
            Committed(s, f, T0.AddHours(1), Guid.NewGuid())
        ]);
        await using var db = world.Db();

        var all = await Feed(db).ReadAsync(AccountMergeNoticeTarget.Chat, null, null, default);
        var later = await Feed(db).ReadAsync(AccountMergeNoticeTarget.Chat, T0.AddHours(1), null, default);

        Assert.Equal([T0.AddHours(1), T0.AddHours(3), T0.AddHours(3)], all.Select(x => x.OccurredAt));
        Assert.Equal([low, high], all.Skip(1).Select(x => x.MergeId));
        Assert.Equal([low, high], later.Select(x => x.MergeId));
    }

    [Fact]
    public async Task A_merge_that_is_undone_and_one_that_is_final_order_their_two_notices_by_their_own_times()
    {
        var (world, _, _) = await SeededAsync((s, f) =>
        {
            var undone = Committed(s, f, T0);
            undone.State = AccountMergeState.Undone;
            undone.UndoneUtc = T0.AddHours(5);
            var final = Committed(s, f, T0.AddHours(1));
            final.State = AccountMergeState.Final;
            final.FinalizedUtc = T0.AddHours(2);
            return [undone, final];
        });
        await using var db = world.Db();

        var kinds = (await Feed(db).ReadAsync(AccountMergeNoticeTarget.Chat, null, null, default)).Select(x => x.Kind);

        Assert.Equal([AccountMergeNoticeKind.Merged, AccountMergeNoticeKind.Merged, AccountMergeNoticeKind.Final, AccountMergeNoticeKind.Undone], kinds);
    }

    [Theory]
    [InlineData(null, 50)]
    [InlineData(0, 50)]
    [InlineData(-5, 50)]
    [InlineData(3, 3)]
    [InlineData(200, 200)]
    [InlineData(100000, 200)]
    public async Task The_limit_is_bounded_default_fifty_maximum_two_hundred(int? asked, int expected)
    {
        var (world, _, _) = await SeededAsync((s, f) => Enumerable.Range(0, 230).Select(i => Committed(s, f, T0.AddMinutes(i))));
        await using var db = world.Db();

        var read = await Feed(db).ReadAsync(AccountMergeNoticeTarget.Chat, null, asked, default);

        Assert.Equal(expected, read.Count);
        Assert.Equal(T0.AddMinutes(expected - 1), read[^1].OccurredAt); // the oldest first, cut at the limit
    }

    [Fact]
    public async Task The_feed_writes_no_merge_row_only_acks()
    {
        var (world, _, _) = await SeededAsync((s, f) => [Committed(s, f, T0)]);
        await using var db = world.Db();
        var before = await db.AccountMerges.AsNoTracking().SingleAsync();
        var feed = Feed(db);

        await feed.ReadAsync(AccountMergeNoticeTarget.Chat, null, null, default);
        Assert.True(await feed.AckAsync(AccountMergeNoticeTarget.Chat, before.Id, AccountMergeNoticeKind.Merged, default));
        Assert.True(await feed.AckAsync(AccountMergeNoticeTarget.Chat, before.Id, AccountMergeNoticeKind.Merged, default));
        Assert.False(await feed.AckAsync(AccountMergeNoticeTarget.Chat, before.Id, AccountMergeNoticeKind.Final, default));

        await using var fresh = world.Db();
        var after = await fresh.AccountMerges.AsNoTracking().SingleAsync();
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before), System.Text.Json.JsonSerializer.Serialize(after));
        Assert.Equal(1, await fresh.AccountMergeNoticeAcks.CountAsync());
    }

    // ---- the unique key, on a real database ----

    [PostgresFact]
    public async Task Two_concurrent_acks_of_one_notice_both_succeed_and_leave_one_row()
    {
        var name = $"bifrost_notices_{Guid.NewGuid():N}";
        var admin = Environment.GetEnvironmentVariable(PostgresGate.Variable)!;
        await using (var connection = new Npgsql.NpgsqlConnection(admin))
        {
            await connection.OpenAsync();
            await using var create = new Npgsql.NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var (world, _, _) = await SeededAsync((s, f) => [Committed(s, f, T0)], new Npgsql.NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString);
            Guid id;
            await using (var read = world.Db())
            {
                id = (await read.AccountMerges.SingleAsync()).Id;
            }

            for (var round = 0; round < 5; round++) // a fresh race each round
            {
                const AccountMergeNoticeKind kind = AccountMergeNoticeKind.Merged;
                var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
                {
                    await using var db = world.Db();
                    return await Feed(db).AckAsync(AccountMergeNoticeTarget.Chat, id, kind, default);
                }));
                Assert.All(results, Assert.True);
            }

            await using var check = world.Db();
            Assert.Equal(1, await check.AccountMergeNoticeAcks.CountAsync());
            Assert.Empty(await Feed(check).ReadAsync(AccountMergeNoticeTarget.Chat, null, null, default));
        }
        finally
        {
            Npgsql.NpgsqlConnection.ClearAllPools();
            await using var connection = new Npgsql.NpgsqlConnection(admin);
            await connection.OpenAsync();
            await using var drop = new Npgsql.NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
