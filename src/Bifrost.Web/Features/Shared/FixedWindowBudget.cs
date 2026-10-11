namespace Bifrost.Web.Features.Shared;

// Why a permit was refused and how long until the answer can change. TableFull is not the caller's own spending: the
// table holds only spent windows and has no room for a key it does not know.
public readonly record struct BudgetDenial(TimeSpan RetryAfter, bool TableFull);

// The one rate-limit mechanism: a fixed-window permit budget per caller-chosen partition key, timed on the injected
// clock's monotonic timestamps (never its wall time, which can step), so a clock adjustment cannot stretch or reset a
// window. Windows all have one length, so they end in the order they began: a list of entries in start order expires
// them from the front and the table never holds a key past its window.
// The table holds at most maxKeys keys. A new key arriving at the cap evicts the least recently used key that still
// has permits left (nginx limit_req_zone does the same); a key that has spent all its permits is never evicted, so a
// rotating caller can at most win back a forgotten key's partial budget and never a spent one, and nobody fresh is
// refused because the table is full. Only when every held key is spent is a new key refused, with the true wait.
// Every step is O(1): two linked lists, one by window start and one by last use among keys with permits left.
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
    private readonly LinkedList<Entry> byStart = new();
    private readonly LinkedList<Entry> byUse = new();
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

    // Takes one permit for the key and returns null, or returns why it could not.
    public BudgetDenial? Spend(string key)
    {
        lock (gate)
        {
            var now = clock.GetElapsedTime(origin);
            while (byStart.First is { Value: var oldest } && oldest.Start + window <= now)
            {
                Remove(oldest);
            }

            if (entries.TryGetValue(key, out var entry))
            {
                if (entry.Count >= permits)
                {
                    return new BudgetDenial(entry.Start + window - now, false);
                }

                entry.Count++;
                if (entry.Count >= permits)
                {
                    byUse.Remove(entry.UseNode!);
                    entry.UseNode = null;
                }
                else
                {
                    byUse.Remove(entry.UseNode!);
                    byUse.AddLast(entry.UseNode!);
                }

                return null;
            }

            if (entries.Count >= maxKeys)
            {
                if (byUse.First is not { Value: var forgotten })
                {
                    return new BudgetDenial(byStart.First!.Value.Start + window - now, true);
                }

                Remove(forgotten);
            }

            var added = new Entry(key, now);
            entries[key] = added;
            added.StartNode = byStart.AddLast(added);
            if (added.Count < permits)
            {
                added.UseNode = byUse.AddLast(added);
            }

            return null;
        }
    }

    private void Remove(Entry entry)
    {
        entries.Remove(entry.Key);
        byStart.Remove(entry.StartNode!);
        if (entry.UseNode is not null)
        {
            byUse.Remove(entry.UseNode);
        }
    }

    private sealed class Entry(string key, TimeSpan start)
    {
        public string Key { get; } = key;

        public TimeSpan Start { get; } = start;

        public int Count { get; set; } = 1;

        public LinkedListNode<Entry>? StartNode { get; set; }

        public LinkedListNode<Entry>? UseNode { get; set; }
    }
}
