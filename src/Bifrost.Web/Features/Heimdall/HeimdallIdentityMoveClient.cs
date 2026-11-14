using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bifrost.Web.Configuration;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Features.Heimdall;

/// <summary>
/// What Bifrost asks Heimdall to move (docs/service-contract.md, POST /v1/apps/{app}/identity-moves). Exactly one of
/// <see cref="ToProof"/> (a forward move, optionally of <see cref="IdentityIds"/>) and <see cref="ReversesMoveId"/> (the undo). The body
/// never names an account: the proofs, live Heimdall access tokens, say which accounts are meant.
/// </summary>
public sealed record HeimdallMoveRequest(
    string MoveId, IReadOnlyList<string> FromProofs, string? ToProof = null, IReadOnlyList<string>? IdentityIds = null, string? ReversesMoveId = null);

public enum HeimdallProofTier
{
    Strong,
    Weak
}

/// <summary>The receipt of a move: the destination account, the logins that moved and the tier of the proofs that carried it.</summary>
public sealed record HeimdallMoveReceipt(
    string MoveId, string AccountId, IReadOnlyList<string> MovedIdentityIds, HeimdallProofTier ProofFrom, HeimdallProofTier? ProofTo, string? ReversesMoveId);

/// <summary>Heimdall's fixed refusal codes for a move, plus the two local classes: a 400, and a 4xx this client does not know.</summary>
public enum HeimdallMoveRefusal
{
    AppAuthRequired,
    NotAllowedForApp,
    NotReversible,
    ProofInvalid,
    SameAccount,
    ProofStale,
    IdentityNotProved,
    AlreadyMoved,
    ProviderConflict,
    MoveIdConflict,
    IdentityNotFound,
    RequestInvalid,
    Unrecognized
}

public enum HeimdallProofSide
{
    From,
    To
}

/// <summary>
/// The closed answer to a move. Moved: the receipt. Refused: Heimdall answered a 4xx before applying anything, so nothing changed.
/// Unknown: anything else (a 5xx may follow a committed move, a timeout may hide one): the caller replays the same request.
/// </summary>
public abstract record HeimdallMoveOutcome
{
    private HeimdallMoveOutcome()
    {
    }

    public sealed record Moved(HeimdallMoveReceipt Receipt) : HeimdallMoveOutcome;

    public sealed record Refused(HeimdallMoveRefusal Code, HeimdallProofSide? Side = null, string? Provider = null) : HeimdallMoveOutcome;

    public sealed record Unknown(string Reason) : HeimdallMoveOutcome;
}

/// <summary>
/// Heimdall's move route under Bifrost's app secret (HeimdallAuthService.ConfigureClient adds the header). Each call sends one request and
/// retries nothing: replay with the same <see cref="HeimdallMoveRequest.MoveId"/> is the caller's, and Heimdall answers it with the stored
/// receipt. Logs name the outcome class and refusal code only.
/// </summary>
public sealed partial class HeimdallIdentityMoveClient(
    HttpClient httpClient, IOptions<HeimdallOptions> heimdallOptions, ILogger<HeimdallIdentityMoveClient> logger)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private const int MaxMovedIdentities = 256;

    [GeneratedRegex("^[A-Za-z0-9_-]{16,64}$")]
    private static partial Regex MoveIdShape();

    public async Task<HeimdallMoveOutcome> MoveAsync(HeimdallMoveRequest request, CancellationToken cancellationToken)
    {
        var body = Serialize(request);
        var outcome = await SendAsync(request, body, cancellationToken);
        logger.LogInformation("Heimdall identity move: {Outcome} {Code}", outcome.GetType().Name, outcome is HeimdallMoveOutcome.Refused refused ? refused.Code : null);
        return outcome;
    }

    private async Task<HeimdallMoveOutcome> SendAsync(HeimdallMoveRequest request, string body, CancellationToken cancellationToken)
    {
        try
        {
            using var message = new HttpRequestMessage(
                HttpMethod.Post, $"/v1/apps/{Uri.EscapeDataString(heimdallOptions.Value.AppSlug)}/identity-moves")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return await ReadReceiptAsync(response, request, cancellationToken) is { } receipt
                    ? new HeimdallMoveOutcome.Moved(receipt)
                    : new HeimdallMoveOutcome.Unknown("malformed or mismatched receipt");
            }

            if (status is < 400 or >= 500 || status == 429)
            {
                return new HeimdallMoveOutcome.Unknown($"HTTP {status}");
            }

            var refusal = await HeimdallRefusal.ReadAsync(response, cancellationToken);
            return status == 400 ? new HeimdallMoveOutcome.Refused(HeimdallMoveRefusal.RequestInvalid) : Refused(refusal);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException or TaskCanceledException
                                      && !cancellationToken.IsCancellationRequested)
        {
            return new HeimdallMoveOutcome.Unknown("transport");
        }
    }

    private static HeimdallMoveOutcome.Refused Refused(HeimdallRefusalBody? refusal)
    {
        var code = refusal?.Code switch
        {
            "app_auth_required" => HeimdallMoveRefusal.AppAuthRequired,
            "not_allowed_for_app" => HeimdallMoveRefusal.NotAllowedForApp,
            "not_reversible" => HeimdallMoveRefusal.NotReversible,
            "proof_invalid" => HeimdallMoveRefusal.ProofInvalid,
            "same_account" => HeimdallMoveRefusal.SameAccount,
            "proof_stale" => HeimdallMoveRefusal.ProofStale,
            "identity_not_proved" => HeimdallMoveRefusal.IdentityNotProved,
            "already_moved" => HeimdallMoveRefusal.AlreadyMoved,
            "provider_conflict" => HeimdallMoveRefusal.ProviderConflict,
            "move_id_conflict" => HeimdallMoveRefusal.MoveIdConflict,
            "identity_not_found" => HeimdallMoveRefusal.IdentityNotFound,
            _ => HeimdallMoveRefusal.Unrecognized
        };
        return code switch
        {
            HeimdallMoveRefusal.ProofInvalid or HeimdallMoveRefusal.ProofStale => new(code, Side: refusal?.Proof switch
            {
                "from" => HeimdallProofSide.From,
                "to" => HeimdallProofSide.To,
                _ => null
            }),
            HeimdallMoveRefusal.ProviderConflict => new(code, Provider: refusal?.Provider),
            _ => new(code)
        };
    }

    /// <summary>
    /// A 200 body is a receipt only when it is whole and answers this request: its moveId (and reversesMoveId) equal the request's,
    /// a forward move names a destination proof tier and a reversal does not. Anything else is not trusted as a move.
    /// </summary>
    private static async Task<HeimdallMoveReceipt?> ReadReceiptAsync(HttpResponseMessage response, HeimdallMoveRequest request, CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || Text(root, "moveId") != request.MoveId
            || Text(root, "reversesMoveId") != request.ReversesMoveId
            || Text(root, "accountId") is not { Length: > 0 } account
            || !root.TryGetProperty("movedIdentityIds", out var moved)
            || moved.ValueKind != JsonValueKind.Array
            || moved.GetArrayLength() is 0 or > MaxMovedIdentities
            || !root.TryGetProperty("proof", out var proof)
            || proof.ValueKind != JsonValueKind.Object
            || Tier(proof, "from") is not { } from)
        {
            return null;
        }

        var ids = new List<string>();
        foreach (var item in moved.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !Guid.TryParse(item.GetString(), out _))
            {
                return null;
            }

            ids.Add(item.GetString()!);
        }

        HeimdallProofTier? to = null;
        if (request.ReversesMoveId is null)
        {
            to = Tier(proof, "to");
            if (to is null)
            {
                return null;
            }
        }
        else if (!proof.TryGetProperty("to", out var none) || none.ValueKind != JsonValueKind.Null)
        {
            return null;
        }

        return new HeimdallMoveReceipt(request.MoveId, account, ids, from, to, request.ReversesMoveId);
    }

    private static HeimdallProofTier? Tier(JsonElement proof, string name) => Text(proof, name) switch
    {
        "strong" => HeimdallProofTier.Strong,
        "weak" => HeimdallProofTier.Weak,
        _ => null
    };

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The contract's closed body, or ArgumentException before any I/O when the request breaks it.</summary>
    private static string Serialize(HeimdallMoveRequest request)
    {
        if (request.MoveId is null || !MoveIdShape().IsMatch(request.MoveId))
        {
            throw new ArgumentException("moveId must match ^[A-Za-z0-9_-]{16,64}$", nameof(request));
        }

        if (request.FromProofs is not { Count: >= 1 and <= 8 } || request.FromProofs.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("fromProofs must hold 1 to 8 proofs", nameof(request));
        }

        if ((request.ToProof is null) == (request.ReversesMoveId is null))
        {
            throw new ArgumentException("exactly one of toProof and reversesMoveId is required", nameof(request));
        }

        if (request.ToProof is not null && string.IsNullOrWhiteSpace(request.ToProof))
        {
            throw new ArgumentException("toProof is empty", nameof(request));
        }

        if (request.ReversesMoveId is not null && !MoveIdShape().IsMatch(request.ReversesMoveId))
        {
            throw new ArgumentException("reversesMoveId must match the moveId shape", nameof(request));
        }

        if (request.IdentityIds is not null
            && (request.ToProof is null || request.IdentityIds.Count is < 1 or > 32 || request.IdentityIds.Any(string.IsNullOrWhiteSpace)))
        {
            throw new ArgumentException("identityIds are 1 to 32 and only with toProof", nameof(request));
        }

        var body = new Dictionary<string, object?> { ["moveId"] = request.MoveId, ["fromProofs"] = request.FromProofs };
        if (request.ToProof is not null)
        {
            body["toProof"] = request.ToProof;
        }

        if (request.IdentityIds is not null)
        {
            body["identityIds"] = request.IdentityIds;
        }

        if (request.ReversesMoveId is not null)
        {
            body["reversesMoveId"] = request.ReversesMoveId;
        }

        return JsonSerializer.Serialize(body);
    }
}
