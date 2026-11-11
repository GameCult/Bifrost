using System.Security.Cryptography;
using System.Text;
using Bifrost.Web.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Features.Registration;

public sealed record RegistrationChallenge(string Challenge, int Difficulty, DateTimeOffset ExpiresAt);

/// <summary>
/// Self-hosted proof-of-work challenge for the anonymous registration. A challenge is a data-protected
/// (random id, expiry) payload, so the server keeps nothing per issue; only consumed ids are remembered, in a bounded
/// set whose entries are dropped at expiry. A full set refuses both issuing and consuming rather than grow.
/// </summary>
public sealed class RegistrationChallengeService(
    IDataProtectionProvider dataProtection,
    IOptions<RegistrationOptions> options,
    TimeProvider clock)
{
    public const string Purpose = "Bifrost.Registration.Challenge.v1";

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public const int MaxConsumed = 10_000;

    private const int MaxNonceLength = 20;

    private readonly IDataProtector protector = dataProtection.CreateProtector(Purpose);
    private readonly HashSet<string> consumed = new(StringComparer.Ordinal);
    // Ordered by expiry: a challenge consumed late has an earlier expiry than one consumed before it, so a plain FIFO would strand it.
    private readonly PriorityQueue<string, DateTimeOffset> expiries = new();
    private readonly object gate = new();

    private int Difficulty => options.Value.Captcha.Difficulty;

    /// <summary>Null when the consumed set is full: refuse rather than grow.</summary>
    public RegistrationChallenge? Issue()
    {
        var now = clock.GetUtcNow();
        lock (gate)
        {
            Purge(now);
            if (consumed.Count >= MaxConsumed)
            {
                return null;
            }
        }

        var expiresAt = now + Lifetime;
        var difficulty = Difficulty;
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var challenge = protector.Protect($"{id}.{expiresAt.ToUnixTimeMilliseconds()}.{difficulty}.{now.ToUnixTimeMilliseconds()}");
        return new RegistrationChallenge(challenge, difficulty, expiresAt);
    }

    /// <summary>Proof-of-work path: the nonce must meet the difficulty the challenge was issued with. Spends the challenge.</summary>
    public bool TryConsume(string? challenge, string? nonce)
    {
        if (!IsNonce(nonce) || Open(challenge) is not { } open)
        {
            return false;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(challenge + ":" + nonce));
        return LeadingZeroBits(hash) >= open.Difficulty && Spend(open);
    }

    /// <summary>
    /// Time-lock path, step one: how long until a <c>waited</c> submission of this challenge is accepted. Null when the
    /// token is not a live challenge of ours; zero when the wait is over. Spends nothing.
    /// </summary>
    public TimeSpan? WaitRemaining(string? challenge)
    {
        if (Open(challenge) is not { } open)
        {
            return null;
        }

        var remaining = open.IssuedAt + TimeSpan.FromSeconds(options.Value.Captcha.FallbackWaitSeconds) - clock.GetUtcNow();
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>Time-lock path, step two: spends the challenge if its wait is over. No proof of work is checked.</summary>
    public bool TryConsumeWaited(string? challenge) =>
        WaitRemaining(challenge) == TimeSpan.Zero && Open(challenge) is { } open && Spend(open);

    /// <summary>True when the token is ours, unexpired and not yet spent, so a form may carry it into its next attempt.</summary>
    public bool IsLive(string? challenge)
    {
        if (Open(challenge) is not { } open)
        {
            return false;
        }

        lock (gate)
        {
            return !consumed.Contains(open.Id);
        }
    }

    private sealed record OpenChallenge(string Id, int Difficulty, DateTimeOffset ExpiresAt, DateTimeOffset IssuedAt);

    /// <summary>The payload of a challenge that we protected and that has not expired; null otherwise.</summary>
    private OpenChallenge? Open(string? challenge)
    {
        if (string.IsNullOrEmpty(challenge))
        {
            return null;
        }

        OpenChallenge open;
        try
        {
            var payload = protector.Unprotect(challenge).Split('.');
            if (payload.Length != 4)
            {
                return null;
            }

            var culture = System.Globalization.CultureInfo.InvariantCulture;
            open = new OpenChallenge(
                payload[0],
                int.Parse(payload[2], culture),
                DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(payload[1], culture)),
                DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(payload[3], culture)));
        }
        catch (Exception)
        {
            return null;
        }

        return clock.GetUtcNow() >= open.ExpiresAt ? null : open;
    }

    private bool Spend(OpenChallenge open)
    {
        var now = clock.GetUtcNow();
        lock (gate)
        {
            Purge(now);
            if (consumed.Count >= MaxConsumed || !consumed.Add(open.Id))
            {
                return false;
            }

            expiries.Enqueue(open.Id, open.ExpiresAt);
            return true;
        }
    }

    private void Purge(DateTimeOffset now)
    {
        while (expiries.TryPeek(out var id, out var expiresAt) && expiresAt <= now)
        {
            expiries.Dequeue();
            consumed.Remove(id);
        }
    }

    private static bool IsNonce(string? nonce) =>
        nonce is { Length: >= 1 and <= MaxNonceLength } && nonce.All(c => c is >= '0' and <= '9');

    public static int LeadingZeroBits(ReadOnlySpan<byte> hash)
    {
        var bits = 0;
        foreach (var b in hash)
        {
            if (b == 0)
            {
                bits += 8;
                continue;
            }

            return bits + System.Numerics.BitOperations.LeadingZeroCount((uint)b) - 24;
        }

        return bits;
    }
}
