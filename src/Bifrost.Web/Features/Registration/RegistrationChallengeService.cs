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
        var challenge = protector.Protect($"{id}.{expiresAt.ToUnixTimeMilliseconds()}.{difficulty}");
        return new RegistrationChallenge(challenge, difficulty, expiresAt);
    }

    public bool TryConsume(string? challenge, string? nonce)
    {
        if (string.IsNullOrEmpty(challenge) || !IsNonce(nonce))
        {
            return false;
        }

        string id;
        int difficulty;
        DateTimeOffset expiresAt;
        try
        {
            var payload = protector.Unprotect(challenge).Split('.');
            if (payload.Length != 3)
            {
                return false;
            }

            id = payload[0];
            difficulty = int.Parse(payload[2], System.Globalization.CultureInfo.InvariantCulture);
            expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(payload[1], System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception)
        {
            return false;
        }

        var now = clock.GetUtcNow();
        if (now >= expiresAt)
        {
            return false;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(challenge + ":" + nonce));
        if (LeadingZeroBits(hash) < difficulty)
        {
            return false;
        }

        lock (gate)
        {
            Purge(now);
            if (consumed.Count >= MaxConsumed || !consumed.Add(id))
            {
                return false;
            }

            expiries.Enqueue(id, expiresAt);
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
