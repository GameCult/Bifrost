using Bifrost.Web.Features.Shared;

namespace Bifrost.Web.Features.Passkeys;

public enum CeremonyKind
{
    /// <summary>Permission to enrol one passkey, earned by a fresh Heimdall sign-in. Held by id in a cookie of the browser that signed in.</summary>
    EnrolTicket,
    /// <summary>The WebAuthn creation options in flight for an account.</summary>
    EnrolChallenge,
    /// <summary>The WebAuthn request options in flight for an account's unlink of one identity.</summary>
    UnlinkChallenge,
    /// <summary>The fact "this account's passkey assertion was verified for unlinking this identity". Carries no bearer and no credential.</summary>
    UnlinkProof
}

/// <summary>
/// The short-lived single-use server-side state of the passkey flows, in this process only (like the Linked accounts list):
/// a restart empties it and every flow is simply begun again. Every entry is bound to an account and a kind, and the unlink
/// kinds to the identity they are for; <c>Take</c> removes the entry whether or not the caller then succeeds, so a
/// challenge, a ticket or an assertion fact can be spent once. A challenge kind has one live entry per account: a newer one
/// replaces the older. A full table refuses new entries rather than grow.
/// </summary>
public sealed class PasskeyCeremonies(TimeProvider clock)
{
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan ProofLifetime = TimeSpan.FromMinutes(5);
    public const int MaxLive = 2048;

    public sealed record Entry(string Id, CeremonyKind Kind, string Account, string? Subject, string? Data, DateTimeOffset ExpiresAt);

    private readonly Dictionary<string, Entry> _held = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Adds an entry and returns its id, or null when the table is full.</summary>
    public string? Issue(CeremonyKind kind, string account, string? subject, string? data, TimeSpan lifetime)
    {
        lock (_gate)
        {
            Purge();
            if (kind != CeremonyKind.UnlinkProof)
            {
                foreach (var old in _held.Values.Where(x => x.Kind == kind && x.Account == account).ToList())
                {
                    _held.Remove(old.Id);
                }
            }

            if (_held.Count >= MaxLive)
            {
                return null;
            }

            var id = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');
            _held[id] = new Entry(id, kind, account, subject, data, clock.GetUtcNow() + lifetime);
            return id;
        }
    }

    /// <summary>The live entry, without spending it. <paramref name="id"/> null finds the account's one current entry of that kind.</summary>
    public Entry? Peek(CeremonyKind kind, string account, string? subject, string? id)
    {
        lock (_gate)
        {
            Purge();
            return Find(kind, account, subject, id);
        }
    }

    /// <summary>The live entry, removed: it can be taken once.</summary>
    public Entry? Take(CeremonyKind kind, string account, string? subject, string? id)
    {
        lock (_gate)
        {
            Purge();
            var entry = Find(kind, account, subject, id);
            if (entry is not null)
            {
                _held.Remove(entry.Id);
            }

            return entry;
        }
    }

    public void Discard(string? id)
    {
        if (id is not null)
        {
            lock (_gate)
            {
                _held.Remove(id);
            }
        }
    }

    private Entry? Find(CeremonyKind kind, string account, string? subject, string? id)
    {
        var entry = id is null
            ? _held.Values.FirstOrDefault(x => x.Kind == kind && x.Account == account)
            : _held.GetValueOrDefault(id);
        return entry is not null && entry.Kind == kind
            && string.Equals(entry.Account, account, StringComparison.Ordinal)
            && string.Equals(entry.Subject, subject, StringComparison.Ordinal)
            ? entry
            : null;
    }

    private void Purge()
    {
        var now = clock.GetUtcNow();
        foreach (var dead in _held.Values.Where(x => x.ExpiresAt <= now).ToList())
        {
            _held.Remove(dead.Id);
        }
    }
}

/// <summary>
/// The one budget on the passkey ceremonies, per Heimdall account, on the shared <see cref="FixedWindowBudget"/>: every
/// options request, enrolment, assertion and enrolment start spends a permit.
/// </summary>
public sealed class PasskeyRateLimit(TimeProvider clock)
{
    public const int Permits = 20;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly FixedWindowBudget _budget = new(Permits, Window, 10_000, clock);

    public bool TryAcquire(string account) => _budget.Spend(account) is null;
}
