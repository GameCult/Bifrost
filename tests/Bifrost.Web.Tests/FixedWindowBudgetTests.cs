using Bifrost.Web.Features.Shared;

namespace Bifrost.Web.Tests;

public sealed class FixedWindowBudgetTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    [Fact]
    public void Permits_are_spent_per_key_and_the_next_is_denied_with_the_time_left()
    {
        var clock = new Clock();
        var budget = new FixedWindowBudget(2, Window, clock);

        Assert.True(budget.TryAcquire("a", out _));
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(budget.TryAcquire("a", out _));
        Assert.False(budget.TryAcquire("a", out var wait));

        Assert.Equal(TimeSpan.FromSeconds(50), wait);
        Assert.True(budget.TryAcquire("b", out _));
    }

    [Fact]
    public void A_window_opens_at_its_first_permit_and_closes_exactly_one_window_later()
    {
        var clock = new Clock();
        var budget = new FixedWindowBudget(1, Window, clock);
        Assert.True(budget.TryAcquire("a", out _));

        clock.Advance(Window - TimeSpan.FromTicks(1));
        Assert.False(budget.TryAcquire("a", out var wait));
        Assert.Equal(TimeSpan.FromTicks(1), wait);

        clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(budget.TryAcquire("a", out _));
        Assert.False(budget.TryAcquire("a", out wait));
        Assert.Equal(Window, wait);
    }

    [Fact]
    public void A_denied_attempt_does_not_extend_the_window()
    {
        var clock = new Clock();
        var budget = new FixedWindowBudget(1, Window, clock);
        budget.TryAcquire("a", out _);
        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.False(budget.TryAcquire("a", out _));
        }

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(budget.TryAcquire("a", out _));
    }

    [Fact]
    public void A_sweep_drops_only_expired_windows_and_a_live_one_keeps_counting_to_its_exact_end()
    {
        var clock = new Clock();
        var budget = new FixedWindowBudget(1, Window, clock);
        budget.TryAcquire("a", out _);
        clock.Advance(TimeSpan.FromSeconds(30));
        budget.TryAcquire("b", out _);
        clock.Advance(TimeSpan.FromSeconds(30));

        // The sweep due at 60 s drops a, which ended, and keeps b, which has 30 s left.
        budget.TryAcquire("c", out _);
        Assert.Equal(2, budget.TrackedKeys);
        Assert.False(budget.TryAcquire("b", out var wait));
        Assert.Equal(TimeSpan.FromSeconds(30), wait);

        clock.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
        Assert.False(budget.TryAcquire("b", out _));
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(budget.TryAcquire("b", out _));
    }

    [Fact]
    public void Sweeping_is_at_most_once_per_window_so_a_flood_of_keys_costs_one_pass_not_one_per_request()
    {
        var clock = new Clock();
        var budget = new FixedWindowBudget(1, Window, clock);
        budget.TryAcquire("a", out _);
        clock.Advance(TimeSpan.FromSeconds(30));
        budget.TryAcquire("b", out _);
        clock.Advance(TimeSpan.FromSeconds(31));
        budget.TryAcquire("c", out _);
        Assert.Equal(2, budget.TrackedKeys);

        // b ended at 90 s but the next sweep is not due until 121 s.
        clock.Advance(TimeSpan.FromSeconds(39));
        budget.TryAcquire("d", out _);

        Assert.Equal(3, budget.TrackedKeys);
    }

    [Fact]
    public void Expired_windows_are_dropped_so_the_table_does_not_grow_with_every_key_ever_seen()
    {
        var clock = new Clock();
        var budget = new FixedWindowBudget(1, Window, clock);
        for (var i = 0; i < 100; i++)
        {
            budget.TryAcquire("k" + i, out _);
        }

        Assert.Equal(100, budget.TrackedKeys);
        clock.Advance(Window);
        budget.TryAcquire("fresh", out _);

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
            () => new FixedWindowBudget(permits, TimeSpan.FromSeconds(seconds), new Clock()));
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
