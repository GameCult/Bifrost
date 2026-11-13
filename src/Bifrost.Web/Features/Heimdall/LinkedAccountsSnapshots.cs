using System.Collections.Concurrent;

namespace Bifrost.Web.Features.Heimdall;

/// <summary>
/// What the Linked accounts page last read from Heimdall for an account: id, provider, display name and link date of
/// each login, nothing else, for <see cref="Lifetime"/>. It lives in this process only, keyed by the Heimdall account id,
/// and is never given to the browser; it holds no bearer. The page renders from it because the list is fetched inside a
/// completion request, which redirects. After a restart it is empty and the page offers to read the list again.
/// </summary>
public sealed class LinkedAccountsSnapshots(TimeProvider timeProvider)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (IReadOnlyList<LinkedIdentitySummary> Identities, DateTimeOffset ExpiresAt)> _held = new(StringComparer.Ordinal);

    public void Put(string accountId, IReadOnlyList<LinkedIdentitySummary> identities)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var (key, value) in _held)
        {
            if (value.ExpiresAt <= now)
            {
                _held.TryRemove(key, out _);
            }
        }

        _held[accountId] = (identities, now + Lifetime);
    }

    public IReadOnlyList<LinkedIdentitySummary>? Get(string accountId) =>
        _held.TryGetValue(accountId, out var value) && value.ExpiresAt > timeProvider.GetUtcNow() ? value.Identities : null;

    public void Remove(string accountId) => _held.TryRemove(accountId, out _);
}
