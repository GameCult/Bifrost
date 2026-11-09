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
    private static readonly Regex PersonaPattern = new("^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.Compiled);
    private static readonly Regex NoncePattern = new("^[A-Za-z0-9_-]{16,64}$", RegexOptions.Compiled);

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

        var bound = await dbContext.MindKeys.Where(x => x.PersonaId == persona).Select(x => (Guid?)x.UserAccountId).FirstOrDefaultAsync(cancellationToken);
        if (bound is { } other && other != userAccountId)
        {
            throw new SealedBallotException(SealedBallotFailure.Invalid, "That persona is already bound to a different member account.");
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
        await dbContext.SaveChangesAsync(cancellationToken);
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

        if (envelope.MotionId != routeMotionId)
        {
            throw new SealedBallotException(SealedBallotFailure.Invalid, "The envelope was signed for a different motion.");
        }

        var keys = await dbContext.MindKeys.AsNoTracking().Where(x => x.PersonaId == persona && x.RevokedAtUtc == null).ToListAsync(cancellationToken);
        var message = Encoding.UTF8.GetBytes(SigningText(envelope));
        var signature = Convert.TryFromBase64String(envelope.Signature ?? string.Empty, new byte[64], out var written) && written == 64
            ? Convert.FromBase64String(envelope.Signature!) : null;
        var signer = signature is null ? null : keys.FirstOrDefault(k =>
            TryKey(k.PublicKey, out var pub) && Ed25519.Verify(signature, 0, pub, 0, message, 0, message.Length));
        if (signer is null)
        {
            // One answer for unknown persona, revoked key, wrong key and bad signature: nothing to probe.
            throw new SealedBallotException(SealedBallotFailure.Unauthorized, "The envelope is not signed by a registered key for that persona.");
        }

        // Spend the nonce only after the signature verifies, so an unsigned caller cannot burn a persona's nonces.
        if (await dbContext.MindNonces.AnyAsync(x => x.PersonaId == persona && x.Nonce == envelope.Nonce, cancellationToken))
        {
            throw new SealedBallotException(SealedBallotFailure.WrongPhase, "That nonce was already used.");
        }

        dbContext.MindNonces.Add(new MindNonce { PersonaId = persona, Nonce = envelope.Nonce!, SeenAtUtc = timeProvider.GetUtcNow() });
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new SealedBallotException(SealedBallotFailure.WrongPhase, "That nonce was already used.");
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
        string.Join('\n', Domain, e.Persona, e.Verb, e.MotionId.ToString("D"), e.Nonce, e.Commitment ?? "", e.Round, e.Candidate ?? "", e.Reason ?? "", e.Salt ?? "");

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
    string Persona, string Verb, Guid MotionId, string Nonce,
    string? Commitment, int Round, string? Candidate, string? Reason, string? Salt, string Signature);

public sealed record MindKeyView(Guid Id, string Persona, Guid UserAccountId, string PublicKey, DateTimeOffset RegisteredAtUtc, DateTimeOffset? RevokedAtUtc);

public sealed record RegisterMindKeyRequest(string Persona, Guid UserAccountId, string PublicKey);
