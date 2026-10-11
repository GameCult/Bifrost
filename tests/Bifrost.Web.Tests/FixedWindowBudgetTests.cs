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
        var budget = new FixedWindowBudget(1, Window, Cap, clock);
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
        var budget = new FixedWindowBudget(1, Window, Cap, clock);
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
    public void An_expired_window_is_dropped_and_a_live_one_keeps_counting_to_its_exact_end()
    {
        var clock = new Clock();
        var budget = new FixedWindowBudget(1, Window, Cap, clock);
        budget.TryAcquire("a", out _);
        clock.Advance(TimeSpan.FromSeconds(30));
        budget.TryAcquire("b", out _);
        clock.Advance(TimeSpan.FromSeconds(30));

        // At 60 s a has ended and b has 30 s left.
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
    public void A_wall_clock_step_back_does_not_extend_a_window_or_its_retry_after()
    {
        var clock = new Clock();
        var budget = new FixedWindowBudget(1, Window, Cap, clock);
        budget.TryAcquire("a", out _);
        clock.Advance(TimeSpan.FromSeconds(10));
        clock.StepWall(-TimeSpan.FromHours(1));

        Assert.False(budget.TryAcquire("a", out var wait));
        Assert.Equal(TimeSpan.FromSeconds(50), wait);
        clock.Advance(TimeSpan.FromSeconds(50));
        Assert.True(budget.TryAcquire("a", out _));
        Assert.Equal(1, budget.TrackedKeys);
    }

    [Fact]
    public void A_wall_clock_step_forward_does_not_reset_a_window()
    {
        var clock = new Clock();
        var budget = new FixedWindowBudget(1, Window, Cap, clock);
        budget.TryAcquire("a", out _);
        clock.StepWall(TimeSpan.FromHours(1));

        Assert.False(budget.TryAcquire("a", out var wait));
        Assert.Equal(Window, wait);
    }

    [Fact]
    public void A_new_key_at_the_cap_is_denied_until_the_oldest_window_ends_and_a_held_key_is_never_evicted()
    {
        var clock = new Clock();
        var budget = new FixedWindowBudget(1, Window, 2, clock);
        budget.TryAcquire("a", out _);
        clock.Advance(TimeSpan.FromSeconds(20));
        budget.TryAcquire("b", out _);
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.False(budget.TryAcquire("c", out var wait));
        Assert.Equal(TimeSpan.FromSeconds(30), wait);
        Assert.Equal(2, budget.TrackedKeys);

        // Rotating new keys cannot push a spent key out to get its budget back.
        for (var i = 0; i < 10; i++)
        {
            Assert.False(budget.TryAcquire("rot" + i, out _));
        }

        Assert.False(budget.TryAcquire("a", out wait));
        Assert.Equal(TimeSpan.FromSeconds(30), wait);

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(budget.TryAcquire("c", out _));
        Assert.Equal(2, budget.TrackedKeys);
        Assert.False(budget.TryAcquire("b", out wait));
        Assert.Equal(TimeSpan.FromSeconds(20), wait);
    }

    [Fact]
    public void A_held_key_under_its_permits_is_served_even_when_the_table_is_full()
    {
        var clock = new Clock();
        var budget = new FixedWindowBudget(2, Window, 1, clock);
        Assert.True(budget.TryAcquire("a", out _));

        Assert.True(budget.TryAcquire("a", out _));
        Assert.False(budget.TryAcquire("a", out _));
    }

    [Fact]
    public void Expired_windows_are_dropped_so_the_table_does_not_grow_with_every_key_ever_seen()
    {
        var clock = new Clock();
        var budget = new FixedWindowBudget(1, Window, Cap, clock);
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
            () => new FixedWindowBudget(permits, TimeSpan.FromSeconds(seconds), Cap, new Clock()));
    }

    [Fact]
    public void A_cap_below_one_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedWindowBudget(1, Window, 0, new Clock()));
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
