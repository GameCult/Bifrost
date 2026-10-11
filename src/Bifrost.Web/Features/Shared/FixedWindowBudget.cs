namespace Bifrost.Web.Features.Shared;

// The one rate-limit mechanism: a fixed-window permit budget per caller-chosen partition key, timed on the injected
// clock's monotonic timestamps (never its wall time, which can step), so a clock adjustment cannot stretch or reset a
// window. Windows all have one length, so they end in the order they began: a queue of window starts expires them from
// the front and the table never holds a key past its window. The table holds at most maxKeys keys; a new key arriving
// at the cap is denied until the oldest window ends, with the true wait, so a rotating caller cannot evict a spent key
// to escape its budget and the refusal is a 429 that says when to retry, never a silent drop.
// System.Threading.RateLimiting is not used because its limiters time themselves on Stopwatch and timers, which a
// test cannot move.
public sealed class FixedWindowBudget
{
    private readonly int permits;
    private readonly TimeSpan window;
    private readonly int maxKeys;
    private readonly TimeProvider clock;
    private readonly long origin;
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Queue<string> byStart = new();
    private readonly object gate = new();

    public FixedWindowBudget(int permits, TimeSpan window, int maxKeys, TimeProvider clock)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(permits, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxKeys, 1);
        this.permits = permits;
        this.window = window;
        this.maxKeys = maxKeys;
        this.clock = clock;
        origin = clock.GetTimestamp();
    }

    // How many keys are held now: the keys whose window is still open, never more than maxKeys.
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

    // Takes one permit for the key. When none is left, or the table is full and the key is new, returns false and how
    // long until a window ends and the answer can change.
    public bool TryAcquire(string key, out TimeSpan retryAfter)
    {
        lock (gate)
        {
            var now = clock.GetElapsedTime(origin);
            while (byStart.TryPeek(out var oldest) && entries[oldest].Start + window <= now)
            {
                entries.Remove(byStart.Dequeue());
            }

            if (entries.TryGetValue(key, out var entry))
            {
                if (entry.Count >= permits)
                {
                    retryAfter = entry.Start + window - now;
                    return false;
                }

                entry.Count++;
                retryAfter = TimeSpan.Zero;
                return true;
            }

            if (entries.Count >= maxKeys)
            {
                retryAfter = entries[byStart.Peek()].Start + window - now;
                return false;
            }

            entries[key] = new Entry(now);
            byStart.Enqueue(key);
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    private sealed class Entry(TimeSpan start)
    {
        public TimeSpan Start { get; } = start;

        public int Count { get; set; } = 1;
    }
}
