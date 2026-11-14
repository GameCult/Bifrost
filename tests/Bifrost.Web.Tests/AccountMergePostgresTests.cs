using Bifrost.Web.Configuration;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Heimdall;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Features.Motions;
using Bifrost.Web.Features.Patronage;
using Bifrost.Web.Features.Shared;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Bifrost.Web.Tests;

/// <summary>
/// The merge tests that need a real server run only when BIFROST_TEST_PG holds a Postgres connection string (no Database=; a role that
/// may create databases). Every other merge test runs on the in-memory provider, which enforces no unique index, transaction,
/// concurrency token or trigger, so the rules that rest on those are proven here, on a database each test creates, builds through the
/// real migrations and the class drops. See AGENTS.md (Live Postgres tests).
/// </summary>
public static class PostgresGate
{
    public const string Variable = "BIFROST_TEST_PG";

    public static string? Skip =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable))
            ? $"live Postgres tests: set {Variable} to a connection string without Database= (see AGENTS.md, Live Postgres tests)"
            : null;
}

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute() => Skip = PostgresGate.Skip;
}

public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute() => Skip = PostgresGate.Skip;
}

public sealed class MergePostgres : IAsyncLifetime
{
    private readonly List<string> _databases = [];

    private static string? Admin => Environment.GetEnvironmentVariable(PostgresGate.Variable);

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>A fresh database, built by the real migrations, and a world over it.</summary>
    public async Task<AccountMergeLedgerTests.World> NewWorldAsync()
    {
        var name = $"bifrost_merge_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(Admin))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        _databases.Add(name);
        var world = AccountMergeLedgerTests.World.OnPostgres(new NpgsqlConnectionStringBuilder(Admin) { Database = name }.ConnectionString);
        await using var db = world.Db();
        await db.Database.MigrateAsync();
        return world;
    }

    public async Task DisposeAsync()
    {
        if (_databases.Count == 0)
        {
            return;
        }

        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(Admin);
        await admin.OpenAsync();
        foreach (var name in _databases)
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}

public sealed class AccountMergePostgresTests(MergePostgres postgres) : IClassFixture<MergePostgres>
{
    private static PostgresException Pg(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException!)
        {
            if (current is PostgresException pg)
            {
                return pg;
            }
        }

        throw new Xunit.Sdk.XunitException($"no PostgresException under {error.GetType().Name}");
    }

    /// <summary>Runs an action once, just before the first save of a context: the rival writer that lands inside a read-then-save window.</summary>
    private sealed class BeforeFirstSave(Func<Task> rival) : SaveChangesInterceptor
    {
        private int _fired;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await rival();
            }

            return result;
        }
    }

    /// <summary>Holds each of two contexts at its save until both have arrived, so both have read before either writes.</summary>
    private sealed class Rendezvous : SaveChangesInterceptor
    {
        private readonly Barrier _barrier = new(2);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await Task.Run(() => _barrier.SignalAndWait(TimeSpan.FromSeconds(30)), cancellationToken);
            return result;
        }
    }

    private static IEnumerable<Exception> Chain(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException!)
        {
            yield return current;
        }
    }

    private static string ConnectionOf(AccountMergeLedgerTests.World world)
    {
        using var db = world.Db();
        return db.Database.GetConnectionString()!;
    }

    private static async Task ExecuteAsync(AccountMergeLedgerTests.World world, string sql)
    {
        await using var db = world.Db();
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    // ---- commit atomicity ----

    [PostgresFact]
    public async Task A_commit_that_fails_midway_leaves_nothing_applied_and_the_retry_commits_once()
    {
        var world = await postgres.NewWorldAsync();
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded");
        var motion = await world.MotionAsync();
        await world.VoteAsync(motion, survivor, VoteChoice.For);
        await world.VoteAsync(motion, folded, VoteChoice.Against);
        var merge = await world.MergeAsync(survivor, folded);
        await ExecuteAsync(world, """
            CREATE FUNCTION fail_merged() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW."Status" = 'Merged' THEN RAISE EXCEPTION 'injected fault'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER fail_merged BEFORE UPDATE ON "Memberships" FOR EACH ROW EXECUTE FUNCTION fail_merged();
            """);

        var fault = await Assert.ThrowsAsync<DbUpdateException>(() => world.CommitAsync(merge));
        Assert.Contains("injected fault", Pg(fault).MessageText);

        await using (var check = world.Db())
        {
            Assert.Equal(AccountMergeState.HeimdallMoved, (await check.AccountMerges.SingleAsync()).State);
            Assert.Empty(check.AccountMergeEntries);
            Assert.All(await check.Votes.IgnoreQueryFilters().ToListAsync(), v => Assert.Null(v.VoidedByMergeId));
            Assert.Equal(MembershipStatus.Active, (await check.Memberships.SingleAsync(x => x.UserAccountId == folded.Id)).Status);
        }

        await ExecuteAsync(world, "DROP TRIGGER fail_merged ON \"Memberships\"");
        Assert.Equal(AccountMergeState.Committed, (await world.CommitAsync(merge)).State);
        var journal = await world.JournalAsync(merge);
        Assert.Equal(1, journal.Count(x => x.Kind == AccountMergeEntryKind.Vote));
        Assert.Equal(AccountMergeState.Committed, (await world.CommitAsync(merge)).State); // a second commit is a no-op
        Assert.Equal(journal.Count, (await world.JournalAsync(merge)).Count);
    }

    [PostgresFact]
    public async Task The_journal_refuses_a_second_entry_with_the_same_merge_and_seq()
    {
        var world = await postgres.NewWorldAsync();
        var merge = await world.MergeAsync(await world.AccountAsync("survivor"), await world.AccountAsync("folded"));
        await using var db = world.Db();
        AccountMergeEntry Entry() => new() { MergeId = merge.Id, Seq = 1, Kind = AccountMergeEntryKind.Vote, Action = AccountMergeEntryAction.Voided, RecordId = Guid.NewGuid(), AtUtc = AccountMergeLedgerTests.T0 };
        db.AccountMergeEntries.Add(Entry());
        await db.SaveChangesAsync();
        db.AccountMergeEntries.Add(Entry());

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        Assert.Equal(("23505", "IX_AccountMergeEntries_MergeId_Seq"), (Pg(error).SqlState, Pg(error).ConstraintName));
    }

    [PostgresFact]
    public async Task Two_commits_of_the_same_merge_racing_apply_it_exactly_once()
    {
        var world = await postgres.NewWorldAsync();
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded");
        var motion = await world.MotionAsync();
        await world.VoteAsync(motion, survivor, VoteChoice.For);
        await world.VoteAsync(motion, folded, VoteChoice.Against);
        var merge = await world.MergeAsync(survivor, folded);
        var committers = AccountMergeLedgerTests.World.OnPostgres(ConnectionOf(world));

        async Task<Exception?> TryCommit()
        {
            try
            {
                await committers.CommitAsync(merge);
                return null;
            }
            catch (Exception error)
            {
                return error;
            }
        }

        var results = await Task.WhenAll(Task.Run(TryCommit), Task.Run(TryCommit));

        // The second commit waits on the folded account's exclusive lock, then finds the merge landed and returns its receipt.
        Assert.All(results, e => Assert.Null(e));
        var journal = await world.JournalAsync(merge);
        Assert.Equal(journal.Count, journal.Select(x => x.Seq).Distinct().Count());
        Assert.Equal(1, journal.Count(x => x.Kind == AccountMergeEntryKind.Vote));
        Assert.Single((await world.AllVotesAsync(motion)).Where(v => v.VoidedByMergeId == merge.Id));
    }

    [PostgresFact]
    public async Task A_voided_vote_leaves_every_tally_on_a_real_database()
    {
        var world = await postgres.NewWorldAsync();
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded");
        var moderator = await world.AccountAsync("mod", moderator: true);
        var motion = await world.MotionAsync();
        await world.VoteAsync(motion, survivor, VoteChoice.Against, 1m);
        await world.VoteAsync(motion, folded, VoteChoice.For, 3m);

        await world.CommitAsync(await world.MergeAsync(survivor, folded));

        var item = await world.StateOfAsync(motion, moderator);
        Assert.Equal((0m, 1m), (item.VotesFor, item.VotesAgainst));
        Assert.Equal(MotionStatus.Failed, await world.CloseAsync(motion, moderator));
    }

    // ---- the sweeper against the saga ----

    [PostgresFact]
    public async Task The_sweep_does_not_overwrite_an_UndoRequested_written_after_it_read_the_merge()
    {
        var plain = await postgres.NewWorldAsync();
        var merge = await plain.CommitAsync(await plain.MergeAsync(await plain.AccountAsync("survivor"), await plain.AccountAsync("folded")));
        var sweeper = AccountMergeLedgerTests.World.OnPostgres(
            ConnectionOf(plain), new BeforeFirstSave(() => plain.SetStateAsync(merge, AccountMergeState.UndoRequested)));

        await using (var db = sweeper.Db())
        {
            Assert.Equal(0, await sweeper.Ledger(db).FinalizeDueAsync(AccountMergeLedgerTests.T0.AddDays(8), default));
        }

        await using var read = plain.Db();
        var row = await read.AccountMerges.SingleAsync();
        Assert.Equal(AccountMergeState.UndoRequested, row.State);
        Assert.Null(row.FinalizedUtc);
    }

    [PostgresFact]
    public async Task An_UndoRequested_written_from_a_stale_Committed_read_cannot_overwrite_Final()
    {
        var world = await postgres.NewWorldAsync();
        var merge = await world.CommitAsync(await world.MergeAsync(await world.AccountAsync("survivor"), await world.AccountAsync("folded")));
        await using var saga = world.Db();
        var stale = await saga.AccountMerges.SingleAsync(x => x.Id == merge.Id); // the saga read Committed

        await using (var sweep = world.Db())
        {
            Assert.Equal(1, await world.Ledger(sweep).FinalizeDueAsync(AccountMergeLedgerTests.T0.AddDays(8), default));
        }

        stale.State = AccountMergeState.UndoRequested;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => saga.SaveChangesAsync());
        await using var read = world.Db();
        Assert.Equal(AccountMergeState.Final, (await read.AccountMerges.SingleAsync()).State);
    }

    [PostgresFact]
    public async Task A_sweep_that_loses_one_merge_to_the_saga_still_finalizes_the_others()
    {
        var plain = await postgres.NewWorldAsync();
        var survivor = await plain.AccountAsync("survivor");
        await plain.CommitAsync(await plain.MergeAsync(survivor, await plain.AccountAsync("folded-a")));
        await plain.CommitAsync(await plain.MergeAsync(survivor, await plain.AccountAsync("folded-b")));
        var sweeper = AccountMergeLedgerTests.World.OnPostgres(
            ConnectionOf(plain), new BeforeFirstSave(async () =>
            {
                // Whichever merge the sweep saves first, the saga takes one of the two before it does.
                await using var rival = plain.Db();
                var taken = await rival.AccountMerges.Where(x => x.State == AccountMergeState.Committed).OrderBy(x => x.Id).FirstAsync();
                taken.State = AccountMergeState.UndoRequested;
                await rival.SaveChangesAsync();
            }));

        await using (var db = sweeper.Db())
        {
            Assert.Equal(1, await sweeper.Ledger(db).FinalizeDueAsync(AccountMergeLedgerTests.T0.AddDays(8), default));
        }

        await using var read = plain.Db();
        Assert.Equal([AccountMergeState.Final, AccountMergeState.UndoRequested], (await read.AccountMerges.ToListAsync()).Select(x => x.State).Order());
    }

    // ---- overlapping merges, refused by the database ----

    [PostgresFact]
    public async Task A_second_live_merge_folding_the_same_account_is_refused_and_a_finished_one_frees_it()
    {
        var world = await postgres.NewWorldAsync();
        var s1 = await world.AccountAsync("acct-s1");
        var s2 = await world.AccountAsync("acct-s2");
        var folded = await world.AccountAsync("folded");

        var second = await world.MergeAsync(s2, folded, AccountMergeState.Proposed); // proposals may coexist
        var first = await world.MergeAsync(s1, folded, AccountMergeState.Confirmed);

        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => world.SetStateAsync(second, AccountMergeState.Confirmed));
        Assert.Equal(("23505", "UX_AccountMerges_FoldedUserAccountId_Live"), (Pg(refused).SqlState, Pg(refused).ConstraintName));
        var inserted = await Assert.ThrowsAsync<DbUpdateException>(() => world.MergeAsync(s2, folded, AccountMergeState.Final));
        Assert.Equal("23505", Pg(inserted).SqlState);

        await world.SetStateAsync(first, AccountMergeState.Undone);
        await world.SetStateAsync(second, AccountMergeState.Confirmed); // the slot is free again
    }

    [PostgresTheory]
    [InlineData(AccountMergeState.Confirmed, AccountMergeState.Confirmed)]
    [InlineData(AccountMergeState.Final, AccountMergeState.Confirmed)]
    [InlineData(AccountMergeState.Committed, AccountMergeState.HeimdallMoved)]
    public async Task A_chain_is_refused_in_either_arrival_order_and_the_same_survivor_twice_is_fine(AccountMergeState earlier, AccountMergeState later)
    {
        var world = await postgres.NewWorldAsync();
        foreach (var survivorFirst in new[] { true, false })
        {
            var tag = survivorFirst ? "a" : "b";
            var s = await world.AccountAsync("acct-s" + tag);
            var f = await world.AccountAsync("acct-f" + tag);
            var g = await world.AccountAsync("acct-g" + tag);
            var other = await world.AccountAsync("other" + tag);

            await world.MergeAsync(s, other, AccountMergeState.Confirmed); // one survivor may carry two folded accounts
            await world.MergeAsync(s, f, earlier);
            // f survives a live merge and folds into g, or s folds into g while it survives one: both are chains.
            var chain = await Assert.ThrowsAsync<DbUpdateException>(() => survivorFirst ? world.MergeAsync(f, g, later) : world.MergeAsync(g, s, later));
            Assert.Equal("23P01", Pg(chain).SqlState);
        }
    }

    [PostgresFact]
    public async Task Two_transactions_racing_to_chain_the_same_account_cannot_both_commit()
    {
        var world = await postgres.NewWorldAsync();
        var s = await world.AccountAsync("acct-s");
        var f = await world.AccountAsync("acct-f");
        var g = await world.AccountAsync("acct-g");
        await using var first = world.Db();
        await using var firstTransaction = await first.Database.BeginTransactionAsync();
        first.AccountMerges.Add(new AccountMerge { SurvivorUserAccountId = s.Id, FoldedUserAccountId = f.Id, State = AccountMergeState.Confirmed, CreatedUtc = AccountMergeLedgerTests.T0 });
        await first.SaveChangesAsync();

        await using var second = world.Db();
        await using var secondTransaction = await second.Database.BeginTransactionAsync();
        second.AccountMerges.Add(new AccountMerge { SurvivorUserAccountId = f.Id, FoldedUserAccountId = g.Id, State = AccountMergeState.Confirmed, CreatedUtc = AccountMergeLedgerTests.T0 });
        var racing = second.SaveChangesAsync();

        await Task.Delay(1500);
        Assert.False(racing.IsCompleted); // it waits on the first, which holds the lock on f
        await firstTransaction.CommitAsync();

        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => racing.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal("23P01", Pg(refused).SqlState);
    }

    // ---- rollback ----

    [PostgresFact]
    public async Task Reverting_the_migration_is_refused_while_any_merge_exists_even_one_that_voided_nothing_and_allowed_after()
    {
        var world = await postgres.NewWorldAsync();
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded");
        await world.CommitAsync(await world.MergeAsync(survivor, folded)); // moves and voids nothing: only the Merged membership remains

        await using (var db = world.Db())
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => db.GetService<IMigrator>().MigrateAsync("20261113120000_AddPasskeyCredentials"));
            Assert.Contains("roll-forward only", Pg(error).MessageText);
            Assert.Equal("20261114140000_AddAccountMergeNoticeAcks", (await db.Database.GetAppliedMigrationsAsync()).Last());
        }

        await ExecuteAsync(world, "DELETE FROM \"AccountMergeEntries\"; DELETE FROM \"AccountMerges\"");
        await using var down = world.Db();
        await down.GetService<IMigrator>().MigrateAsync("20261113120000_AddPasskeyCredentials");
        Assert.Equal("20261113120000_AddPasskeyCredentials", (await down.Database.GetAppliedMigrationsAsync()).Last());
        await down.GetService<IMigrator>().MigrateAsync(); // and up again, trigger and index and all
        Assert.Equal("20261114140000_AddAccountMergeNoticeAcks", (await down.Database.GetAppliedMigrationsAsync()).Last());
    }

    // ---- restage and intake journal ----

    [PostgresFact]
    public async Task Two_merges_into_one_survivor_restaging_at_once_leave_one_current_patron_snapshot_and_the_loser_commits_on_retry()
    {
        var world = await postgres.NewWorldAsync();
        var survivor = await world.AccountAsync("survivor");
        var first = await world.AccountAsync("first");
        var second = await world.AccountAsync("second");
        await using (var db = world.Db())
        {
            foreach (var who in new[] { first, second })
            {
                db.PatronSupportEvents.Add(new PatronSupportEvent
                {
                    UserAccountId = who.Id, Kind = PatronSupportEventKind.RecurringSupportSnapshot, IsCurrentRecurringSupport = true, Amount = 1000m,
                    SupportedAtUtc = AccountMergeLedgerTests.T0, RecordedAtUtc = AccountMergeLedgerTests.T0
                });
            }

            await db.SaveChangesAsync();
        }

        var one = await world.MergeAsync(survivor, first);
        var two = await world.MergeAsync(survivor, second);
        var committers = AccountMergeLedgerTests.World.OnPostgres(ConnectionOf(world), new Rendezvous());

        async Task<Exception?> TryCommit(AccountMerge merge)
        {
            try
            {
                await committers.CommitAsync(merge);
                return null;
            }
            catch (Exception error)
            {
                return error;
            }
        }

        var results = await Task.WhenAll(Task.Run(() => TryCommit(one)), Task.Run(() => TryCommit(two)));

        Assert.Equal(1, results.Count(x => x is null));
        Assert.All(results.OfType<Exception>(), e => Assert.Contains(Chain(e), x => x is PostgresException { SqlState: "23505", ConstraintName: "UX_TierSnapshots_MembershipId_CurrentPatron" }));
        var loser = results[0] is null ? two : one;
        Assert.Equal(AccountMergeState.Committed, (await world.CommitAsync(loser)).State);
        await using var read = world.Db();
        var membership = await read.Memberships.Include(x => x.TierSnapshots).SingleAsync(x => x.UserAccountId == survivor.Id);
        Assert.Single(membership.TierSnapshots, x => x.Kind == TierSnapshotKind.Patron && x.IsCurrent);
        Assert.All(await read.PatronSupportEvents.ToListAsync(), e => Assert.Equal(survivor.Id, e.UserAccountId));
    }

    [PostgresFact]
    public async Task A_patron_refresh_that_loses_the_restage_race_reads_the_winners_snapshot_and_derives_again()
    {
        var world = await postgres.NewWorldAsync();
        var patron = await world.AccountAsync("patron");
        await using (var db = world.Db())
        {
            db.PatronSupportEvents.Add(new PatronSupportEvent
            {
                UserAccountId = patron.Id, Kind = PatronSupportEventKind.RecurringSupportSnapshot, IsCurrentRecurringSupport = true, Amount = 1000m,
                SupportedAtUtc = AccountMergeLedgerTests.T0, RecordedAtUtc = AccountMergeLedgerTests.T0
            });
            await db.SaveChangesAsync();
        }

        var clock = world.Clock;
        var rival = AccountMergeLedgerTests.World.OnPostgres(ConnectionOf(world));
        var loser = AccountMergeLedgerTests.World.OnPostgres(ConnectionOf(world), new BeforeFirstSave(async () =>
        {
            await using var rivalDb = rival.Db();
            await new PatronageService(rivalDb, new AuditTrailService(rivalDb, clock), clock).RefreshPatronTierSnapshotAsync(null, patron.Id, default);
        }));

        await using (var db = loser.Db())
        {
            var summary = await new PatronageService(db, new AuditTrailService(db, clock), clock).RefreshPatronTierSnapshotAsync(null, patron.Id, default);
            Assert.True(summary.VotingWeight > 0m);
        }

        await using var read = world.Db();
        var snapshots = await read.TierSnapshots.Where(x => x.Kind == TierSnapshotKind.Patron).ToListAsync();
        Assert.Single(snapshots, x => x.IsCurrent);
        Assert.Equal(2, snapshots.Count); // the winner's, demoted by the retry, and the retry's own
    }

    [PostgresFact]
    public async Task The_migration_keeps_only_the_newest_of_duplicate_current_patron_snapshots_before_it_builds_the_index()
    {
        var world = await postgres.NewWorldAsync();
        var patron = await world.AccountAsync("patron");
        await using var db = world.Db();
        await db.GetService<IMigrator>().MigrateAsync("20261113120000_AddPasskeyCredentials"); // the index does not exist yet
        var membership = await db.Memberships.SingleAsync(x => x.UserAccountId == patron.Id);
        TierSnapshot Snapshot(string label, int minutes) => new()
        {
            MembershipId = membership.Id, Kind = TierSnapshotKind.Patron, Label = label, Weight = 1m, IsCurrent = true,
            EffectiveFromUtc = AccountMergeLedgerTests.T0, CapturedAtUtc = AccountMergeLedgerTests.T0.AddMinutes(minutes)
        };
        db.TierSnapshots.AddRange(Snapshot("older", 0), Snapshot("newer", 5));
        await db.SaveChangesAsync();

        await db.GetService<IMigrator>().MigrateAsync();

        await using var read = world.Db();
        Assert.Equal("newer", (await read.TierSnapshots.SingleAsync(x => x.IsCurrent)).Label);
        Assert.Equal(2, await read.TierSnapshots.CountAsync());
    }

    [PostgresFact]
    public async Task A_journalled_patron_intake_takes_the_next_seq_after_the_commits_entries_and_is_idempotent()
    {
        var world = await postgres.NewWorldAsync();
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded");
        var motion = await world.MotionAsync();
        await world.VoteAsync(motion, folded, VoteChoice.For);
        var merge = await world.CommitAsync(await world.MergeAsync(survivor, folded));
        var committed = (await world.JournalAsync(merge)).Count;
        Assert.True(committed > 0);

        var events = new[] { $"WH-{Guid.NewGuid():N}", $"WH-{Guid.NewGuid():N}" };
        foreach (var id in events.Concat(events)) // each twice: the provider retries
        {
            await using var db = world.Db();
            await new PatronageService(db, new AuditTrailService(db, world.Clock), world.Clock).RecordSupportEventAsync(
                null, folded.Id, PatronSupportEventKind.OneTimeDonation, 1m, "USD", "ext", ExternalPatronProvider.PayPal, id,
                string.Empty, string.Empty, AccountMergeLedgerTests.T0, false, string.Empty, default);
        }

        var journal = await world.JournalAsync(merge);
        Assert.Equal(committed + 2, journal.Count);
        Assert.Equal(journal.Count, journal.Select(x => x.Seq).Distinct().Count());
    }

    [PostgresTheory]
    [InlineData(AccountMergeState.Proposed)]
    [InlineData(AccountMergeState.Confirmed)]
    [InlineData(AccountMergeState.HeimdallMoved)]
    [InlineData(AccountMergeState.Final)]
    [InlineData(AccountMergeState.UndoRequested)]
    [InlineData(AccountMergeState.HeimdallReversed)]
    [InlineData(AccountMergeState.Undone)]
    [InlineData(AccountMergeState.Failed)]
    public async Task Reverting_the_migration_is_refused_for_a_merge_in_any_state(AccountMergeState state)
    {
        var world = await postgres.NewWorldAsync();
        await world.MergeAsync(await world.AccountAsync("survivor"), await world.AccountAsync("folded"), state);

        await using var db = world.Db();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => db.GetService<IMigrator>().MigrateAsync("20261113120000_AddPasskeyCredentials"));
        Assert.Contains("roll-forward only", Pg(error).MessageText);
    }

    // ---- the write gate ----

    [PostgresFact]
    public async Task A_vote_that_read_no_merge_saves_before_a_merge_can_freeze_the_account_and_the_commit_then_moves_it()
    {
        var world = await postgres.NewWorldAsync();
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded");
        var motion = await world.MotionAsync();
        var actor = await world.ActorAsync(folded.Id);
        Task<AccountMerge>? freeze = null;
        var blocked = false;
        var voters = AccountMergeLedgerTests.World.OnPostgres(ConnectionOf(world), new BeforeFirstSave(async () =>
        {
            // The vote has read its standing (none) and is about to save: the freeze lands here and must wait for the save.
            freeze = Task.Run(() => world.MergeAsync(survivor, folded, AccountMergeState.Confirmed));
            await Task.Delay(1500);
            blocked = !freeze.IsCompleted;
        }));

        await using (var db = voters.Db())
        {
            await voters.Motions(db).CastVoteAsync(actor, motion.Id, VoteChoice.For, null, default);
        }

        var merge = await freeze!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(blocked);
        await world.SetStateAsync(merge, AccountMergeState.HeimdallMoved);
        await world.CommitAsync(merge);

        var votes = await world.AllVotesAsync(motion);
        Assert.Equal(survivor.Id, Assert.Single(votes).UserAccountId); // the commit saw the vote and moved it: no second voice stays on the folded account
        Assert.Single(await world.JournalAsync(merge), x => x.Kind == AccountMergeEntryKind.Vote);
    }

    [PostgresFact]
    public async Task A_patron_event_that_read_a_Frozen_account_saves_before_the_commit_reduces_and_the_commit_moves_it()
    {
        var world = await postgres.NewWorldAsync();
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded");
        var merge = await world.MergeAsync(survivor, folded); // HeimdallMoved: the folded account is Frozen
        Task<AccountMerge>? commit = null;
        var blocked = false;
        var recorders = AccountMergeLedgerTests.World.OnPostgres(ConnectionOf(world), new BeforeFirstSave(async () =>
        {
            commit = Task.Run(() => world.CommitAsync(merge)); // takes the folded account's exclusive lock before it reduces
            await Task.Delay(1500);
            blocked = !commit.IsCompleted;
        }));

        await using (var db = recorders.Db())
        {
            await new PatronageService(db, new AuditTrailService(db, recorders.Clock), recorders.Clock).RecordSupportEventAsync(
                null, folded.Id, PatronSupportEventKind.OneTimeDonation, 1m, "USD", "ext", ExternalPatronProvider.PayPal, "WH-frozen",
                string.Empty, string.Empty, AccountMergeLedgerTests.T0, false, string.Empty, default);
        }

        await commit!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(blocked);
        await using var read = world.Db();
        Assert.Equal(survivor.Id, (await read.PatronSupportEvents.SingleAsync()).UserAccountId); // the reduction found the event and moved it
        Assert.Single(await world.JournalAsync(merge), x => x.Kind == AccountMergeEntryKind.PatronSupportEvent);
    }

    [PostgresFact]
    public async Task A_sealed_reveal_that_retries_on_a_conflict_keeps_its_lock_so_no_merge_freezes_the_account_between_attempts()
    {
        const string salt = "00112233445566778899aabbccddeeff";
        var world = await postgres.NewWorldAsync();
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded");
        var other = await world.AccountAsync("other");
        var moderator = await world.AccountAsync("moderator", moderator: true);
        SealedBallotService Sealed(AccountMergeLedgerTests.World w, BifrostDbContext db) => new(db, new AuditTrailService(db, w.Clock), w.Clock);
        SealedBallotDocument Doc(Guid motionId, string reason) => new(SealedBallotDocument.SchemaId, motionId, 1, "Ana", reason, salt);

        await using var setup = world.Db();
        var motionId = (await Sealed(world, setup).OpenAsync(await world.ActorAsync(moderator.Id),
            new OpenSealedMotionCommand("Who", "Blind.", ["Ana", "Bo"], AccountMergeLedgerTests.T0.AddHours(1), AccountMergeLedgerTests.T0.AddHours(2)), default)).Id;
        foreach (var (account, reason) in new[] { (folded, "mine"), (other, "theirs") })
        {
            await using var db = world.Db();
            await Sealed(world, db).CommitAsync(await world.ActorAsync(account.Id), motionId, SealedBallotDocument.Commit(Doc(motionId, reason), account.Id), default);
        }

        Task<AccountMerge>? freeze = null;
        var blocked = false;
        var revealers = AccountMergeLedgerTests.World.OnPostgres(ConnectionOf(world), new BeforeFirstSave(async () =>
        {
            // The first attempt has read its standing (none). A freeze starts and must wait; another member's reveal then lands first,
            // so this attempt's save conflicts on RevealSeq and the reveal retries while the freeze still waits.
            freeze = Task.Run(() => world.MergeAsync(survivor, folded, AccountMergeState.Confirmed));
            await using var rival = world.Db();
            world.Clock.Now = AccountMergeLedgerTests.T0.AddMinutes(90);
            await Sealed(world, rival).RevealAsync(await world.ActorAsync(other.Id), motionId, Doc(motionId, "theirs"), default);
            await Task.Delay(1500);
            blocked = !freeze.IsCompleted;
        }));
        revealers.Clock.Now = AccountMergeLedgerTests.T0.AddMinutes(90);
        var actor = await world.ActorAsync(folded.Id);

        await using (var db = revealers.Db())
        {
            Assert.True((await Sealed(revealers, db).RevealAsync(actor, motionId, Doc(motionId, "mine"), default)).Revealed);
        }

        await freeze!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(blocked);
    }

    // ---- the saga: races the State token and the live-merge index carry ----

    /// <summary>Holds the first save of each of two contexts until both have arrived: both have read, neither has written.</summary>
    private sealed class FirstSaveRendezvous : SaveChangesInterceptor
    {
        private readonly Barrier _barrier = new(2);
        private readonly HashSet<Guid> _seen = [];

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            bool first;
            lock (_seen)
            {
                first = _seen.Add(eventData.Context!.ContextId.InstanceId);
            }

            if (first)
            {
                await Task.Run(() => _barrier.SignalAndWait(TimeSpan.FromSeconds(30)), cancellationToken);
            }

            return result;
        }
    }

    /// <summary>The fake Heimdall's account model behind the real move client, one key ring for every saga built over it.</summary>
    private sealed class Heimdall
    {
        private readonly EphemeralDataProtectionProvider _keys = new();
        private readonly HeimdallIdentityMoveClient _mover;

        public Heimdall(AccountMergeLedgerTests.World clockOwner)
        {
            Fake = new FakeHeimdall { RequiredAppSecret = HeimdallHost.AppSecret, Now = () => clockOwner.Clock.Now };
            var options = new HeimdallOptions { BaseUrl = HeimdallShapes.Issuer, AppSecret = HeimdallHost.AppSecret };
            var client = new HttpClient(Fake);
            HeimdallAuthService.ConfigureClient(client, options);
            _mover = new HeimdallIdentityMoveClient(client, Options.Create(options), NullLogger<HeimdallIdentityMoveClient>.Instance);
        }

        public FakeHeimdall Fake { get; }

        public AccountMergeSaga Saga(AccountMergeLedgerTests.World world, BifrostDbContext db) =>
            new(db, world.Ledger(db), _mover, _keys, world.Clock, NullLogger<AccountMergeSaga>.Instance);

        /// <summary>A world account with one login at the fake Heimdall and a weak proof through it.</summary>
        public MergeProof Proof(UserAccount account, string provider) =>
            new(Fake.Moves.Issue(account.HeimdallAccountId, Fake.Moves.AddLogin(account.HeimdallAccountId, provider)), account.HeimdallAccountId, provider);
    }

    [PostgresFact]
    public async Task Two_concurrent_advances_of_one_confirmed_merge_make_one_move_commit_once_and_journal_once()
    {
        var world = await postgres.NewWorldAsync();
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded");
        var motion = await world.MotionAsync();
        await world.VoteAsync(motion, folded, VoteChoice.For);
        var heimdall = new Heimdall(world);
        var survivorProof = heimdall.Proof(survivor, "twitch");
        var foldedProof = heimdall.Proof(folded, "discord");
        Guid mergeId;
        await using (var db = world.Db())
        {
            var saga = heimdall.Saga(world, db);
            var digest = (await world.Ledger(db).PlanAsync(survivor.Id, folded.Id, default)).Digest(survivor.Id, folded.Id);
            mergeId = Assert.IsType<ConfirmOutcome.Confirmed>(await saga.ConfirmAsync(
                new ConfirmMergeRequest(survivor.Id, survivor.Id, folded.Id, digest, survivorProof, [foldedProof]), default)).MergeId;
        }

        var racers = AccountMergeLedgerTests.World.OnPostgres(ConnectionOf(world), new FirstSaveRendezvous());
        async Task<AccountMerge> Advance()
        {
            await using var db = racers.Db();
            return await heimdall.Saga(racers, db).AdvanceAsync(mergeId, default);
        }

        var both = await Task.WhenAll(Task.Run(Advance), Task.Run(Advance));

        Assert.All(both, x => Assert.Equal(AccountMergeState.Committed, x.State));
        Assert.Single(heimdall.Fake.Moves.Stored);
        var journal = await world.JournalAsync(new AccountMerge { Id = mergeId });
        Assert.Equal(journal.Count, journal.Select(x => x.Seq).Distinct().Count());
        Assert.Single(journal, x => x.Kind == AccountMergeEntryKind.Vote);
        Assert.Equal(survivor.Id, Assert.Single(await world.AllVotesAsync(motion)).UserAccountId);
    }

    [PostgresFact]
    public async Task Two_confirms_folding_the_same_account_leave_one_row_and_the_other_is_blocked_by_the_database()
    {
        var world = await postgres.NewWorldAsync();
        var first = await world.AccountAsync("first");
        var second = await world.AccountAsync("second");
        var folded = await world.AccountAsync("folded");
        var heimdall = new Heimdall(world);
        var proofs = new Dictionary<Guid, (MergeProof Survivor, string Digest)>();
        foreach (var survivor in new[] { first, second })
        {
            await using var db = world.Db();
            proofs[survivor.Id] = (heimdall.Proof(survivor, "twitch"), (await world.Ledger(db).PlanAsync(survivor.Id, folded.Id, default)).Digest(survivor.Id, folded.Id));
        }

        var foldedProof = heimdall.Proof(folded, "discord");
        var racers = AccountMergeLedgerTests.World.OnPostgres(ConnectionOf(world), new FirstSaveRendezvous());
        async Task<ConfirmOutcome> Confirm(UserAccount survivor)
        {
            await using var db = racers.Db();
            return await heimdall.Saga(racers, db).ConfirmAsync(
                new ConfirmMergeRequest(survivor.Id, survivor.Id, folded.Id, proofs[survivor.Id].Digest, proofs[survivor.Id].Survivor, [foldedProof]), default);
        }

        var results = await Task.WhenAll(Task.Run(() => Confirm(first)), Task.Run(() => Confirm(second)));

        Assert.Single(results.OfType<ConfirmOutcome.Confirmed>());
        var blocked = Assert.Single(results.OfType<ConfirmOutcome.Blocked>());
        Assert.Contains(AccountMergeSaga.MergeInFlight, blocked.Codes);
        await using var check = world.Db();
        Assert.Equal(1, await check.AccountMerges.CountAsync());
    }

    [PostgresFact]
    public async Task Two_merges_cannot_share_a_Heimdall_move_id_or_a_reverse_move_id()
    {
        var world = await postgres.NewWorldAsync();
        var survivor = await world.AccountAsync("survivor");
        var one = await world.MergeAsync(survivor, await world.AccountAsync("one"), AccountMergeState.Confirmed);
        var two = await world.MergeAsync(survivor, await world.AccountAsync("two"), AccountMergeState.Confirmed);

        foreach (var column in new[] { "HeimdallMoveId", "HeimdallReverseMoveId" })
        {
            await ExecuteAsync(world, $"UPDATE \"AccountMerges\" SET \"{column}\" = 'shared-0123456789abcdef' WHERE \"Id\" = '{one.Id}'");
            var error = await Assert.ThrowsAnyAsync<Exception>(() =>
                ExecuteAsync(world, $"UPDATE \"AccountMerges\" SET \"{column}\" = 'shared-0123456789abcdef' WHERE \"Id\" = '{two.Id}'"));
            Assert.Equal("23505", Pg(error).SqlState);
        }
    }

    [PostgresFact]
    public async Task Confirm_holds_the_actors_write_lock_until_its_row_is_saved()
    {
        var world = await postgres.NewWorldAsync();
        var survivor = await world.AccountAsync("survivor");
        var folded = await world.AccountAsync("folded");
        var heimdall = new Heimdall(world);
        var proof = heimdall.Proof(survivor, "twitch");
        var foldedProof = heimdall.Proof(folded, "discord");
        string digest;
        await using (var db = world.Db())
        {
            digest = (await world.Ledger(db).PlanAsync(survivor.Id, folded.Id, default)).Digest(survivor.Id, folded.Id);
        }

        Task? exclusive = null;
        var blocked = false;
        var confirmers = AccountMergeLedgerTests.World.OnPostgres(ConnectionOf(world), new BeforeFirstSave(async () =>
        {
            // A writer that needs the actor's exclusive lock (a commit freezing it) arrives while the confirm is about to save.
            exclusive = Task.Run(async () =>
            {
                await using var connection = new NpgsqlConnection(ConnectionOf(world));
                await connection.OpenAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", connection, transaction);
                command.Parameters.AddWithValue("key", survivor.Id.ToString());
                await command.ExecuteNonQueryAsync();
            });
            await Task.Delay(1500);
            blocked = !exclusive.IsCompleted;
        }));

        await using (var db = confirmers.Db())
        {
            Assert.IsType<ConfirmOutcome.Confirmed>(await heimdall.Saga(confirmers, db).ConfirmAsync(
                new ConfirmMergeRequest(survivor.Id, survivor.Id, folded.Id, digest, proof, [foldedProof]), default));
        }

        await exclusive!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(blocked);
    }

    [PostgresFact]
    public async Task The_saga_migration_adds_and_drops_its_columns_and_keys_and_its_Down_is_refused_while_any_merge_exists()
    {
        var world = await postgres.NewWorldAsync();
        await world.MergeAsync(await world.AccountAsync("survivor"), await world.AccountAsync("folded"), AccountMergeState.Confirmed);
        await using (var db = world.Db())
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => db.GetService<IMigrator>().MigrateAsync("20261114120000_AddAccountMerges"));
            Assert.Contains("AddAccountMergeSaga cannot be reverted", Pg(error).MessageText);
            Assert.Equal("20261114140000_AddAccountMergeNoticeAcks", (await db.Database.GetAppliedMigrationsAsync()).Last());
        }

        await ExecuteAsync(world, "DELETE FROM \"AccountMerges\"");
        await using var down = world.Db();
        await down.GetService<IMigrator>().MigrateAsync("20261114120000_AddAccountMerges");
        Assert.Equal(0, await ScalarAsync(world, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'AccountMerges' AND column_name IN ('HeimdallMoveId','SealedProofs','Attempts')"));
        Assert.Equal(0, await ScalarAsync(world, "SELECT count(*) FROM pg_indexes WHERE tablename = 'AccountMerges' AND indexname LIKE 'IX_AccountMerges_Heimdall%'"));
        await down.GetService<IMigrator>().MigrateAsync();
        Assert.Equal(3, await ScalarAsync(world, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'AccountMerges' AND column_name IN ('HeimdallMoveId','SealedProofs','Attempts')"));
        Assert.Equal(2, await ScalarAsync(world, "SELECT count(*) FROM pg_indexes WHERE tablename = 'AccountMerges' AND indexname LIKE 'IX_AccountMerges_Heimdall%'"));
    }

    private static async Task<long> ScalarAsync(AccountMergeLedgerTests.World world, string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionOf(world));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
