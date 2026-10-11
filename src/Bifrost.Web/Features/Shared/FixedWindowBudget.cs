namespace Bifrost.Web.Features.Shared;

// The one rate-limit mechanism: a fixed-window permit budget per caller-chosen partition key, read against an injected
// clock. Keys are opaque and are never stored beyond their window: an entry whose window has ended is dropped by the
// next sweep, so the table is bounded by the distinct keys seen in one window. System.Threading.RateLimiting is not
// used because its limiters time themselves on Stopwatch and timers, which a test cannot move.
public sealed class FixedWindowBudget
{
    private readonly int permits;
    private readonly TimeSpan window;
    private readonly TimeProvider clock;
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private DateTimeOffset nextSweep = DateTimeOffset.MinValue;

    public FixedWindowBudget(int permits, TimeSpan window, TimeProvider clock)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(permits, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        this.permits = permits;
        this.window = window;
        this.clock = clock;
    }

    // How many keys are held now: bounded by the distinct keys seen in one window, not by every key ever seen.
    public int TrackedKeys
    {
        get
        {
            lock (gate)
            {
                return entries.Count;
            }
        }
    }

    // Takes one permit for the key. When none is left, returns false and how long until its window ends.
    public bool TryAcquire(string key, out TimeSpan retryAfter)
    {
        var now = clock.GetUtcNow();
        lock (gate)
        {
            if (now >= nextSweep)
            {
                foreach (var expired in entries.Where(x => x.Value.Start + window <= now).Select(x => x.Key).ToList())
                {
                    entries.Remove(expired);
                }

                nextSweep = now + window;
            }

            if (!entries.TryGetValue(key, out var entry) || entry.Start + window <= now)
            {
                entries[key] = new Entry(now);
                retryAfter = TimeSpan.Zero;
                return true;
            }

            if (entry.Count < permits)
            {
                entry.Count++;
                retryAfter = TimeSpan.Zero;
                return true;
            }

            retryAfter = entry.Start + window - now;
            return false;
        }
    }

    private sealed class Entry(DateTimeOffset start)
    {
        public DateTimeOffset Start { get; } = start;

        public int Count { get; set; } = 1;
    }
}
