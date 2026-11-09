using System.Text;
using System.Text.RegularExpressions;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Shared;
using Microsoft.EntityFrameworkCore;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace Bifrost.Web.Features.Motions;

/// <summary>
/// Signed intake for Minds. A Mind holds an Ed25519 private key; Bifrost holds only registered public keys. The signature,
/// not a session, is the credential: a verified envelope is handed to <see cref="SealedBallotService"/> as the Mind's member
/// account, so Minds and members share one set of rules. Registration and revocation are moderator acts and are audited.
/// </summary>
public sealed class MindIntakeService(
    BifrostDbContext dbContext,
    SealedBallotService sealedBallots,
    AuditTrailService auditTrailService,
    TimeProvider timeProvider)
{
    public const string Domain = "gamecult.sealed_intake.v0";
    public static readonly TimeSpan Skew = TimeSpan.FromMinutes(10);

    // Diagnostics: every Ed25519 verification the intake performs, real or dummy. A refusal that skips the verify is a timing tell.
    public static long SignatureChecks;

    // A valid signature by a throwaway key over a different message: verifying it does the full work and fails at the end.
    private static readonly (byte[] Public, byte[] Signature, byte[] Message) Dummy = MakeDummy();

    private static (byte[], byte[], byte[]) MakeDummy()
    {
        var secret = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(secret);
        var pub = new byte[32];
        Ed25519.GeneratePublicKey(secret, 0, pub, 0);
        var message = Encoding.UTF8.GetBytes("dummy");
        var sig = new byte[64];
        Ed25519.Sign(secret, 0, pub, 0, null, message, 0, message.Length, sig, 0);
        return (pub, sig, Encoding.UTF8.GetBytes("not the dummy message"));
    }

    private static bool Verify(byte[] signature, byte[] publicKey, byte[] message)
    {
        Interlocked.Increment(ref SignatureChecks);
        return Ed25519.Verify(signature, 0, publicKey, 0, message, 0, message.Length);
    }
    private static readonly Regex PersonaPattern = new(@"^[a-z0-9][a-z0-9-]{0,31}\z", RegexOptions.Compiled);
    private static readonly Regex NoncePattern = new(@"^[A-Za-z0-9_-]{16,64}\z", RegexOptions.Compiled);

    public async Task<MindKeyView> RegisterKeyAsync(
        CurrentBifrostActor actor, string persona, Guid userAccountId, string publicKey, CancellationToken cancellationToken)
    {
        var moderator = RequireModerator(actor);
        persona = (persona ?? string.Empty).Trim();
        if (!PersonaPattern.IsMatch(persona))
        {
            throw new SealedBallotException(SealedBallotFailure.Invalid, "A persona id is 1-32 lowercase letters, digits or hyphens.");
        }

        if (!TryKey(publicKey, out _))
        {
            throw new SealedBallotException(SealedBallotFailure.Invalid, "A public key is the base64 of 32 bytes (Ed25519).");
        }

        var account = await dbContext.UserAccounts.Include(x => x.Membership).SingleOrDefaultAsync(x => x.Id == userAccountId, cancellationToken);
        if (account?.Membership?.Status != MembershipStatus.Active)
        {
            throw new SealedBallotException(SealedBallotFailure.Invalid, "The persona's member account must exist and be Active.");
        }

        // One persona <-> one account, both ways (a shared account would let one persona overwrite the other's commit).
        // The unique indexes on MindPersonas are the arbiter; these reads only give a plain error before them.
        var binding = await dbContext.MindPersonas.SingleOrDefaultAsync(x => x.PersonaId == persona || x.UserAccountId == userAccountId, cancellationToken);
        if (binding is not null && (binding.PersonaId != persona || binding.UserAccountId != userAccountId))
        {
            throw new SealedBallotException(SealedBallotFailure.Invalid, "That persona or member account is already bound to a different account or persona.");
        }

        if (await dbContext.MindKeys.AnyAsync(x => x.PersonaId == persona && x.PublicKey == publicKey, cancellationToken))
        {
            throw new SealedBallotException(SealedBallotFailure.WrongPhase, "That key is already registered for the persona.");
        }

        var key = new MindKey
        {
            PersonaId = persona, UserAccountId = userAccountId, PublicKey = publicKey,
            RegisteredByUserAccountId = moderator.Id, RegisteredAtUtc = timeProvider.GetUtcNow()
        };
        dbContext.MindKeys.Add(key);
        if (binding is null)
        {
            dbContext.MindPersonas.Add(new MindPersona { PersonaId = persona, UserAccountId = userAccountId });
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (binding is null)
        {
            // Lost a registration race on the unique persona/account indexes.
            throw new SealedBallotException(SealedBallotFailure.Invalid, "That persona or member account is already bound to a different account or persona.");
        }

        await auditTrailService.RecordAsync(moderator.Id, nameof(MindKey), key.Id, "mindkey.registered",
            $"Registered a key for persona {persona} (member account {userAccountId}).", cancellationToken);
        return View(key);
    }

    public async Task RevokeKeyAsync(CurrentBifrostActor actor, Guid keyId, CancellationToken cancellationToken)
    {
        var moderator = RequireModerator(actor);
        var key = await dbContext.MindKeys.SingleOrDefaultAsync(x => x.Id == keyId, cancellationToken)
            ?? throw new SealedBallotException(SealedBallotFailure.NotFound, "No such key.");
        if (key.RevokedAtUtc is not null)
        {
            return;
        }

        key.RevokedAtUtc = timeProvider.GetUtcNow();
        key.RevokedByUserAccountId = moderator.Id;
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrailService.RecordAsync(moderator.Id, nameof(MindKey), key.Id, "mindkey.revoked",
            $"Revoked a key for persona {key.PersonaId}.", cancellationToken);
    }

    public async Task<IReadOnlyList<MindKeyView>> ListKeysAsync(CurrentBifrostActor actor, CancellationToken cancellationToken)
    {
        RequireModerator(actor);
        return (await dbContext.MindKeys.AsNoTracking().OrderBy(x => x.PersonaId).ThenBy(x => x.RegisteredAtUtc).ToListAsync(cancellationToken))
            .Select(View).ToList();
    }

    /// <summary>Verifies the envelope, spends its nonce, then runs the verb as the persona's member account.</summary>
    public async Task<SealedBallotReceipt> HandleAsync(Guid routeMotionId, MindEnvelope envelope, CancellationToken cancellationToken)
    {
        var persona = envelope.Persona ?? string.Empty;
        if (!PersonaPattern.IsMatch(persona) || envelope.Verb is not ("commit" or "reveal") || !NoncePattern.IsMatch(envelope.Nonce ?? string.Empty) ||
            new[] { envelope.Commitment, envelope.Candidate, envelope.Reason, envelope.Salt }.Any(f => (f ?? string.Empty).AsSpan().IndexOfAny('\n', '\r') >= 0))
        {
            throw new SealedBallotException(SealedBallotFailure.Invalid, "The envelope is malformed.");
        }

        // Signature first. Every path below that can tell a caller anything about state comes after a verified signature, and
        // every refusal before it costs one Ed25519 verification (a dummy one when there is no key or no usable signature).
        var keys = await dbContext.MindKeys.AsNoTracking().Where(x => x.PersonaId == persona && x.RevokedAtUtc == null).ToListAsync(cancellationToken);
        var message = Encoding.UTF8.GetBytes(SigningText(envelope));
        var raw = new byte[64];
        var wellFormed = Convert.TryFromBase64String(envelope.Signature ?? string.Empty, raw, out var written) && written == 64;
        MindKey? signer = null;
        if (!wellFormed || keys.Count == 0)
        {
            _ = Verify(Dummy.Signature, Dummy.Public, Dummy.Message);
        }
        else
        {
            signer = keys.FirstOrDefault(k => TryKey(k.PublicKey, out var pub) && Verify(raw, pub, message));
        }

        if (signer is null)
        {
            // One answer for unknown persona, revoked key, wrong key and bad signature: nothing to probe.
            throw new SealedBallotException(SealedBallotFailure.Unauthorized, "The envelope is not signed by a registered key for that persona.");
        }

        if (envelope.MotionId != routeMotionId)
        {
            throw new SealedBallotException(SealedBallotFailure.Invalid, "The envelope was signed for a different motion.");
        }

        var now = timeProvider.GetUtcNow();
        if (Math.Abs((now - DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(envelope.IssuedAt, 0, 253402300799))).TotalSeconds) > Skew.TotalSeconds)
        {
            throw new SealedBallotException(SealedBallotFailure.Invalid, "The envelope is outside the accepted clock window.");
        }

        // Spend the nonce only after the signature verifies, so an unsigned caller cannot burn a persona's nonces. Nonces older
        // than twice the skew can no longer pass the window above, so they are pruned here.
        if (await dbContext.MindNonces.AnyAsync(x => x.PersonaId == persona && x.Nonce == envelope.Nonce, cancellationToken))
        {
            throw new SealedBallotException(SealedBallotFailure.WrongPhase, "That nonce was already used.");
        }

        // Pruning is housekeeping and runs apart from the write that spends the nonce: two requests can load and delete the same
        // expired rows, and the loser's concurrency exception must never fail a valid request.
        try
        {
            var cutoff = now - 2 * Skew;
            dbContext.MindNonces.RemoveRange(await dbContext.MindNonces.Where(x => x.SeenAtUtc < cutoff).ToListAsync(cancellationToken));
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
        }

        dbContext.MindNonces.Add(new MindNonce { PersonaId = persona, Nonce = envelope.Nonce!, SeenAtUtc = now });
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A replay only if the nonce is now there (the unique index lost a race); any other write failure is a fault, not a replay.
            dbContext.ChangeTracker.Clear();
            if (await dbContext.MindNonces.AnyAsync(x => x.PersonaId == persona && x.Nonce == envelope.Nonce, cancellationToken))
            {
                throw new SealedBallotException(SealedBallotFailure.WrongPhase, "That nonce was already used.");
            }

            throw;
        }

        var account = await dbContext.UserAccounts.AsNoTracking()
            .Include(x => x.MemberProfile)
            .Include(x => x.Membership!).ThenInclude(x => x.RoleAssignments)
            .Include(x => x.Membership!).ThenInclude(x => x.TierSnapshots)
            .SingleAsync(x => x.Id == signer.UserAccountId, cancellationToken);
        var actor = new CurrentBifrostActor(true, account);

        return envelope.Verb == "commit"
            ? await sealedBallots.CommitAsync(actor, routeMotionId, envelope.Commitment ?? string.Empty, cancellationToken)
            : await sealedBallots.RevealAsync(actor, routeMotionId,
                new SealedBallotDocument(SealedBallotDocument.SchemaId, routeMotionId, envelope.Round, envelope.Candidate ?? string.Empty,
                    envelope.Reason ?? string.Empty, envelope.Salt ?? string.Empty, persona),
                cancellationToken, persona);
    }

    /// <summary>The exact text a Mind signs: every field, fixed order, newline separated, unused fields empty.</summary>
    public static string SigningText(MindEnvelope e) =>
        string.Join('\n', Domain, e.Persona, e.Verb, e.MotionId.ToString("D"), e.Nonce, e.IssuedAt, e.Commitment ?? "", e.Round, e.Candidate ?? "", e.Reason ?? "", e.Salt ?? "");

    private static bool TryKey(string base64, out byte[] key)
    {
        key = new byte[32];
        return Convert.TryFromBase64String(base64 ?? string.Empty, key, out var n) && n == 32;
    }

    private static UserAccount RequireModerator(CurrentBifrostActor actor) =>
        actor is { IsActiveMember: true, CanModerateMotions: true, UserAccount: { } user }
            ? user
            : throw new SealedBallotException(SealedBallotFailure.Forbidden, "Only a moderator manages Mind keys.");

    private static MindKeyView View(MindKey k) => new(k.Id, k.PersonaId, k.UserAccountId, k.PublicKey, k.RegisteredAtUtc, k.RevokedAtUtc);
}

/// <summary>A signed Mind verb. <c>Signature</c> is base64 Ed25519 over <see cref="MindIntakeService.SigningText"/>.</summary>
public sealed record MindEnvelope(
    string Persona, string Verb, Guid MotionId, string Nonce, long IssuedAt,
    string? Commitment, int Round, string? Candidate, string? Reason, string? Salt, string Signature);

public sealed record MindKeyView(Guid Id, string Persona, Guid UserAccountId, string PublicKey, DateTimeOffset RegisteredAtUtc, DateTimeOffset? RevokedAtUtc);

public sealed record RegisterMindKeyRequest(string Persona, Guid UserAccountId, string PublicKey);

/// <summary>Limits on the anonymous signed-intake endpoint (section "MindIntake").</summary>
public sealed class MindIntakeOptions
{
    public const string SectionName = "MindIntake";

    public int Permits { get; init; } = 30;

    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(1);

    public int MaxTrackedKeys { get; init; } = 10_000;

    public int MaxBodyBytes { get; init; } = 4096;
}

/// <summary>Per-source permit budget for the intake, on the shared <see cref="FixedWindowBudget"/>. Spent before the body is read.</summary>
public sealed class MindIntakeRateLimit(Microsoft.Extensions.Options.IOptions<MindIntakeOptions> options, TimeProvider clock)
{
    private readonly FixedWindowBudget budget = new(options.Value.Permits, options.Value.Window, options.Value.MaxTrackedKeys, clock);

    public BudgetDenial? Spend(string source) => budget.Spend(source);
}
