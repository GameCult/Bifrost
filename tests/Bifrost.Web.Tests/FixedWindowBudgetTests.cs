using Bifrost.Web.Features.Shared;

namespace Bifrost.Web.Tests;

public sealed class FixedWindowBudgetTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    private const int Cap = 1000;

    [Fact]
    public void Permits_are_spent_per_key_and_the_next_is_denied_with_the_time_left()
    {
        var clock = new Clock();
        var budget = new FixedWindowBudget(2, Window, Cap, clock);

        Assert.True(Try(budget, "a", out _));
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(Try(budget, "a", out _));
        Assert.False(Try(budget, "a", out var wait));

        Assert.Equal(TimeSpan.FromSeconds(50), wait);
        Assert.True(Try(budget, "b", out _));
    }

    [Fact]
    public void A_window_opens_at_its_first_permit_and_closes_exactly_one_window_later()
    {
        var clock = new Clock();
        var budget = new FixedWindowBudget(1, Window, Cap, clock);
        Assert.True(Try(budget, "a", out _));

        clock.Advance(Window - TimeSpan.FromTicks(1));
        Assert.False(Try(budget, "a", out var wait));
        Assert.Equal(TimeSpan.FromTicks(1), wait);

        clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(Try(budget, "a", out _));
        Assert.False(Try(budget, "a", out wait));
        Assert.Equal(Window, wait);
    }

    [Fact]
    public void A_denied_attempt_does_not_extend_the_window()
    {
        var clock = new Clock();
        var budget = new FixedWindowBudget(1, Window, Cap, clock);
        Try(budget, "a", out _);
        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.False(Try(budget, "a", out _));
        }

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(Try(budget, "a", out _));
    }

    [Fact]
    public void An_expired_window_is_dropped_and_a_live_one_keeps_counting_to_its_exact_end()
    {
        var clock = new Clock();
        var budget = new FixedWindowBudget(1, Window, Cap, clock);
        Try(budget, "a", out _);
        clock.Advance(TimeSpan.FromSeconds(30));
        Try(budget, "b", out _);
        clock.Advance(TimeSpan.FromSeconds(30));

        // At 60 s a has ended and b has 30 s left.
        Try(budget, "c", out _);
        Assert.Equal(2, budget.TrackedKeys);
        Assert.False(Try(budget, "b", out var wait));
        Assert.Equal(TimeSpan.FromSeconds(30), wait);

        clock.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
        Assert.False(Try(budget, "b", out _));
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(Try(budget, "b", out _));
    }

    [Fact]
    public void A_wall_clock_step_back_does_not_extend_a_window_or_its_retry_after()
    {
        var clock = new Clock();
        var budget = new FixedWindowBudget(1, Window, Cap, clock);
        Try(budget, "a", out _);
        clock.Advance(TimeSpan.FromSeconds(10));
        clock.StepWall(-TimeSpan.FromHours(1));

        Assert.False(Try(budget, "a", out var wait));
        Assert.Equal(TimeSpan.FromSeconds(50), wait);
        clock.Advance(TimeSpan.FromSeconds(50));
        Assert.True(Try(budget, "a", out _));
        Assert.Equal(1, budget.TrackedKeys);
    }

    [Fact]
    public void A_wall_clock_step_forward_does_not_reset_a_window()
    {
        var clock = new Clock();
        var budget = new FixedWindowBudget(1, Window, Cap, clock);
        Try(budget, "a", out _);
        clock.StepWall(TimeSpan.FromHours(1));

        Assert.False(Try(budget, "a", out var wait));
        Assert.Equal(Window, wait);
    }

    [Fact]
    public void A_new_key_at_the_cap_evicts_the_least_recently_used_key_with_permits_left()
    {
        var clock = new Clock();
        var budget = new FixedWindowBudget(3, Window, 2, clock);
        Assert.True(Try(budget, "a", out _));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(Try(budget, "b", out _));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(Try(budget, "a", out _));

        // b was used least recently, though a began first: c takes b's place and a keeps its count.
        Assert.True(Try(budget, "c", out _));
        Assert.Equal(2, budget.TrackedKeys);
        Assert.True(Try(budget, "a", out _));
        Assert.False(Try(budget, "a", out _));
    }

    [Fact]
    public void A_forgotten_key_regains_only_what_it_had_not_spent_and_a_spent_key_regains_nothing()
    {
        var clock = new Clock();
        var budget = new FixedWindowBudget(2, Window, 2, clock);
        Assert.True(Try(budget, "spent", out _));
        Assert.True(Try(budget, "spent", out _));
        Assert.True(Try(budget, "partial", out _));
        clock.Advance(TimeSpan.FromSeconds(10));

        // Rotating callers evict partial, never spent.
        for (var i = 0; i < 10; i++)
        {
            Assert.True(Try(budget, "rot" + i, out _));
        }

        Assert.False(Try(budget, "spent", out var wait));
        Assert.Equal(TimeSpan.FromSeconds(50), wait);
        Assert.Equal(2, budget.TrackedKeys);

        // partial came back whole (2 permits), which is all a rotating caller can win: its own forgotten budget.
        Assert.True(Try(budget, "partial", out _));
    }

    [Fact]
    public void A_key_that_spends_its_last_permit_leaves_the_evictable_set()
    {
        var clock = new Clock();
        var budget = new FixedWindowBudget(2, Window, 2, clock);
        Assert.True(Try(budget, "a", out _));
        Assert.True(Try(budget, "a", out _));
        Assert.True(Try(budget, "b", out _));

        // a is spent and b is not: the newcomer evicts b although a was used less recently.
        Assert.True(Try(budget, "c", out _));
        Assert.False(Try(budget, "a", out _));
        Assert.True(Try(budget, "b", out _));
    }

    [Fact]
    public void A_table_full_of_spent_keys_refuses_a_new_key_as_table_full_with_the_wait_for_the_oldest_window()
    {
        var clock = new Clock();
        var budget = new FixedWindowBudget(1, Window, 2, clock);
        Assert.True(Try(budget, "a", out _));
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.True(Try(budget, "b", out _));
        clock.Advance(TimeSpan.FromSeconds(10));

        var denial = budget.Spend("c");
        Assert.Equal(new BudgetDenial(TimeSpan.FromSeconds(30), true), denial);
        Assert.Equal(new BudgetDenial(TimeSpan.FromSeconds(30), false), budget.Spend("a"));
        Assert.Equal(2, budget.TrackedKeys);

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(Try(budget, "c", out _));
        Assert.Equal(2, budget.TrackedKeys);
        Assert.False(Try(budget, "b", out var wait));
        Assert.Equal(TimeSpan.FromSeconds(20), wait);
    }

    [Fact]
    public void An_evicted_key_leaves_the_expiry_list_so_its_old_window_end_cannot_remove_the_key_that_took_its_name()
    {
        var clock = new Clock();
        var budget = new FixedWindowBudget(2, Window, 2, clock);
        Try(budget, "a", out _);
        clock.Advance(TimeSpan.FromSeconds(1));
        Try(budget, "b", out _);
        clock.Advance(TimeSpan.FromSeconds(1));
        Try(budget, "c", out _);
        clock.Advance(TimeSpan.FromSeconds(1));
        Try(budget, "a", out _);

        // The first a began at 0 and ended at 60; the a that returned at 3 is still inside its window.
        clock.Advance(TimeSpan.FromSeconds(57));
        Try(budget, "c", out _);

        Assert.Equal(2, budget.TrackedKeys);
    }

    [Fact]
    public void A_held_key_under_its_permits_is_served_even_when_the_table_is_full()
    {
        var clock = new Clock();
        var budget = new FixedWindowBudget(2, Window, 1, clock);
        Assert.True(Try(budget, "a", out _));

        Assert.True(Try(budget, "a", out _));
        Assert.False(Try(budget, "a", out _));
    }

    [Fact]
    public void Eviction_work_does_not_scan_the_table()
    {
        var clock = new Clock();
        var budget = new FixedWindowBudget(2, Window, 50_000, clock);
        for (var i = 0; i < 50_000; i++)
        {
            budget.Spend("k" + i);
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 200_000; i++)
        {
            budget.Spend("n" + i);
        }

        Assert.Equal(50_000, budget.TrackedKeys);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"200000 evicting inserts took {started.Elapsed}");
    }

    [Fact]
    public void Expired_windows_are_dropped_so_the_table_does_not_grow_with_every_key_ever_seen()
    {
        var clock = new Clock();
        var budget = new FixedWindowBudget(1, Window, Cap, clock);
        for (var i = 0; i < 100; i++)
        {
            Try(budget, "k" + i, out _);
        }

        Assert.Equal(100, budget.TrackedKeys);
        clock.Advance(Window);
        Try(budget, "fresh", out _);

        Assert.Equal(1, budget.TrackedKeys);
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(-1, 60)]
    [InlineData(1, 0)]
    [InlineData(1, -5)]
    public void A_budget_that_cannot_be_enforced_is_refused(int permits, int seconds)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => new FixedWindowBudget(permits, TimeSpan.FromSeconds(seconds), Cap, new Clock()));
    }

    [Fact]
    public void A_cap_below_one_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedWindowBudget(1, Window, 0, new Clock()));
    }

    private static bool Try(FixedWindowBudget budget, string key, out TimeSpan wait)
    {
        var denial = budget.Spend(key);
        wait = denial?.RetryAfter ?? TimeSpan.Zero;
        return denial is null;
    }

    // Time advances monotonic and wall together; StepWall moves only the wall clock, as an NTP step would.
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _wall = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        private long _ticks = 5_000_000;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _wall;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by)
        {
            _wall += by;
            _ticks += by.Ticks;
        }

        public void StepWall(TimeSpan by) => _wall += by;
    }
}
