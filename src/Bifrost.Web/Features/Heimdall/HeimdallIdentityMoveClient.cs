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
    string MoveId, IReadOnlyList<string> FromProofs, string? ToProof = null, IReadOnlyList<string>? IdentityIds = null, string? ReversesMoveId = null)
{
    // The proofs are live bearers: the generated ToString would print them into any log or exception that holds the request.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"MoveId = {MoveId}, FromProofs = {FromProofs?.Count}, ToProof = {(ToProof is null ? "none" : "present")}, ReversesMoveId = {ReversesMoveId}");
        return true;
    }
}

public enum HeimdallProofTier
{
    Strong,
    Weak
}

/// <summary>The receipt of a move: the destination account, the logins that moved and the tier of the proofs that carried it.</summary>
public sealed record HeimdallMoveReceipt(
    string MoveId, string AccountId, IReadOnlyList<string> MovedIdentityIds, HeimdallProofTier ProofFrom, HeimdallProofTier? ProofTo, string? ReversesMoveId);

/// <summary>Heimdall's fixed refusal codes for a move, plus invalid_request, Heimdall's own schema 400.</summary>
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
    IdentityNotFound,
    RequestInvalid
}

public enum HeimdallProofSide
{
    From,
    To
}

/// <summary>
/// The closed answer to a move. Moved: the receipt. Refused: Heimdall answered a 4xx before applying anything, so nothing changed.
/// Unknown: anything else (a 5xx may follow a committed move, a timeout may hide one, and so may a 409 move_id_conflict: Heimdall
/// answers it to a replay of a committed move whose proofs no longer verify): the caller keeps its proofs and replays the same request.
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
    /// <summary>The deadline of one call, headers and body together, set on the HttpClient by <see cref="HeimdallIdentityMoveClientServices"/>.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private const int MaxMovedIdentities = 256;
    private const int MaxIdentityIdLength = 128;
    private const int MaxReceiptBytes = 64 * 1024;
    public const int MaxProofLength = 8192;

    // \z, not $: $ also matches before a final line feed, which Heimdall's schema pattern refuses with a 400.
    [GeneratedRegex("^[A-Za-z0-9_-]{16,64}\\z")]
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
        // One deadline over the whole call: HttpClient.Timeout ends at the headers, and a stalled body must not outlive it.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(httpClient.Timeout);
        var token = deadline.Token;
        try
        {
            using var message = new HttpRequestMessage(
                HttpMethod.Post, $"/v1/apps/{Uri.EscapeDataString(heimdallOptions.Value.AppSlug)}/identity-moves")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
            var status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return await ReadReceiptAsync(response, request, token) is { } receipt
                    ? new HeimdallMoveOutcome.Moved(receipt)
                    : new HeimdallMoveOutcome.Unknown("malformed or mismatched receipt");
            }

            if (status is < 400 or >= 500 || status == 429)
            {
                return new HeimdallMoveOutcome.Unknown($"HTTP {status}");
            }

            return Refused(await HeimdallRefusal.ReadAsync(response, token), status);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Whatever the pipeline throws after the request may have gone out is not a refusal; only the caller's own cancellation propagates.
            return new HeimdallMoveOutcome.Unknown("transport");
        }
    }

    /// <summary>
    /// Refused only for the coded refusals Heimdall's move route emits (4fe367a moveIdentitiesRoute, and its error handler's invalid_request 400). A 4xx without one of those codes
    /// (a gateway's HTML 404, a 408, a code a later Heimdall adds) proves nothing was applied, so it is Unknown. move_id_conflict is
    /// Unknown too: a committed move answers it when its proofs no longer verify.
    /// </summary>
    private static HeimdallMoveOutcome Refused(HeimdallRefusalBody? refusal, int status)
    {
        if (refusal?.Code == "move_id_conflict")
        {
            return new HeimdallMoveOutcome.Unknown("move_id_conflict");
        }

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
            "identity_not_found" => HeimdallMoveRefusal.IdentityNotFound,
            // Heimdall's error handler gives its own 400 this code; an nginx or other edge 400 has none, so it is not Heimdall's refusal.
            "invalid_request" when status == 400 => HeimdallMoveRefusal.RequestInvalid,
            _ => (HeimdallMoveRefusal?)null
        };
        if (code is not { } known)
        {
            return new HeimdallMoveOutcome.Unknown($"HTTP {status} without a Heimdall refusal code");
        }

        return known switch
        {
            HeimdallMoveRefusal.ProofInvalid or HeimdallMoveRefusal.ProofStale => new HeimdallMoveOutcome.Refused(known, Side: refusal?.Proof switch
            {
                "from" => HeimdallProofSide.From,
                "to" => HeimdallProofSide.To,
                _ => null
            }),
            HeimdallMoveRefusal.ProviderConflict => new HeimdallMoveOutcome.Refused(known, Provider: refusal?.Provider),
            _ => new HeimdallMoveOutcome.Refused(known)
        };
    }

    /// <summary>
    /// A 200 body is a receipt only when it is whole and answers this request: its moveId (and reversesMoveId) equal the request's,
    /// a forward move names a destination proof tier and a reversal does not. Anything else is not trusted as a move.
    /// </summary>
    private static async Task<HeimdallMoveReceipt?> ReadReceiptAsync(HttpResponseMessage response, HeimdallMoveRequest request, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (await stream.ReadAsync(chunk, cancellationToken) is > 0 and var read)
        {
            if (buffer.Length + read > MaxReceiptBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
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
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 and <= MaxIdentityIdLength })
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

    private static bool IsBadProof(string? proof) => string.IsNullOrEmpty(proof) || proof.Length > MaxProofLength;

    /// <summary>The contract's closed body, or ArgumentException before any I/O when the request breaks it.</summary>
    private static string Serialize(HeimdallMoveRequest request)
    {
        if (request.MoveId is null || !MoveIdShape().IsMatch(request.MoveId))
        {
            throw new ArgumentException("moveId must match ^[A-Za-z0-9_-]{16,64}$", nameof(request));
        }

        if (request.FromProofs is not { Count: >= 1 and <= 8 } || request.FromProofs.Any(IsBadProof))
        {
            throw new ArgumentException("fromProofs must hold 1 to 8 proofs", nameof(request));
        }

        if ((request.ToProof is null) == (request.ReversesMoveId is null))
        {
            throw new ArgumentException("exactly one of toProof and reversesMoveId is required", nameof(request));
        }

        if (request.ToProof is not null && IsBadProof(request.ToProof))
        {
            throw new ArgumentException("toProof is empty", nameof(request));
        }

        if (request.ReversesMoveId is not null && !MoveIdShape().IsMatch(request.ReversesMoveId))
        {
            throw new ArgumentException("reversesMoveId must match the moveId shape", nameof(request));
        }

        if (request.IdentityIds is not null
            && (request.ToProof is null || request.IdentityIds.Count is < 1 or > 32 || request.IdentityIds.Any(string.IsNullOrEmpty)
                || request.IdentityIds.Distinct().Count() != request.IdentityIds.Count))
        {
            throw new ArgumentException("identityIds are 1 to 32 unique ids and only with toProof", nameof(request));
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

public static class HeimdallClientServices
{
    /// <summary>
    /// Every Heimdall client Bifrost owns. The auth client carries the app secret, the member client only the member's bearer and
    /// the mover the app secret and its call deadline; all share the header redaction.
    /// </summary>
    public static void AddHeimdallClients(this IServiceCollection services)
    {
        services.AddHttpClient<HeimdallAuthService>((serviceProvider, client) =>
                HeimdallAuthService.ConfigureClient(client, serviceProvider.GetRequiredService<IOptions<HeimdallOptions>>().Value))
            .RedactLoggedHeaders(HeimdallAuthService.RedactsHeader);
        services.AddHttpClient<HeimdallMemberClient>((serviceProvider, client) =>
                HeimdallMemberClient.ConfigureClient(client, serviceProvider.GetRequiredService<IOptions<HeimdallOptions>>().Value))
            .RedactLoggedHeaders(HeimdallAuthService.RedactsHeader);
        services.AddHeimdallIdentityMoveClient();
    }

    /// <summary>
    /// The mover's HttpClient: Heimdall's base address and app secret from HeimdallAuthService.ConfigureClient (the one secret path), the
    /// call deadline, no redirects (a 3xx must never carry the secret or the proofs to another origin), and the shared header redaction.
    /// </summary>
    public static IHttpClientBuilder AddHeimdallIdentityMoveClient(this IServiceCollection services) =>
        services.AddHttpClient<HeimdallIdentityMoveClient>((serviceProvider, client) =>
            {
                HeimdallAuthService.ConfigureClient(client, serviceProvider.GetRequiredService<IOptions<HeimdallOptions>>().Value);
                client.Timeout = HeimdallIdentityMoveClient.Timeout;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
            .RedactLoggedHeaders(HeimdallAuthService.RedactsHeader);
}
