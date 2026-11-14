using System.Net;
using System.Text;
using System.Text.Json;

namespace Bifrost.Web.Tests.Support;

/// <summary>One login of a fake account.</summary>
public sealed record MoveLogin(string Id, string Provider);

/// <summary>A move Heimdall stored: the forward move or its reversal, with the receipt it answered byte for byte.</summary>
public sealed class StoredMove(string moveId, string from, string to, string? reversesMoveId, IReadOnlyList<string> carried, string receipt)
{
    public string MoveId { get; } = moveId;

    public string From { get; } = from;

    public string To { get; } = to;

    public string? ReversesMoveId { get; } = reversesMoveId;

    public IReadOnlyList<string> Carried { get; } = carried;

    public string Receipt { get; } = receipt;

    public bool Reversed { get; set; }
}

/// <summary>
/// The fake Heimdall's account model for identity moves, and the route over it: POST /v1/apps/bifrost/identity-moves as Heimdall's
/// moveIdentitiesRoute and decideMove answer it (docs/service-contract.md at 4fe367a). It owns per-account logins, a registry of the
/// bearers it issued (token to claims), revoked sessions and stored moves. Nothing else of the fake reads these yet; the linked
/// accounts list can read <see cref="Logins"/> later so one model answers both routes.
/// </summary>
public sealed class FakeMoves(Func<DateTimeOffset> now)
{
    private const string App = "bifrost";
    private const int WeakSeconds = 300;
    private const int StrongSeconds = 600;
    private const int Lifetime = 900;

    private sealed record Claims(string Account, string Sid, string Aud, long AuthAt, string? AuthIdentityId, long? StepUpAt, string? StepUpMethod, long Exp);

    private readonly Dictionary<string, Claims> _tokens = new(StringComparer.Ordinal);
    private readonly HashSet<string> _revoked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StoredMove> _moves = new(StringComparer.Ordinal);

    public Dictionary<string, List<MoveLogin>> Logins { get; } = new(StringComparer.Ordinal);

    public IReadOnlyCollection<StoredMove> Stored => _moves.Values;

    /// <summary>Every request that reached the route: the app secret header, the Authorization header and the raw body.</summary>
    public List<(string? Secret, string? Authorization, string Body)> Requests { get; } = [];

    /// <summary>When false the app's profile lacks identityMoves, so the route answers 403 not_allowed_for_app.</summary>
    public bool Granted { get; set; } = true;

    /// <summary>When set, the route answers this status after the schema check, bypassing the fake's own rules (nothing is applied).</summary>
    public HttpStatusCode? MoveStatus { get; set; }

    /// <summary>When set, a move that passes every rule is applied and then answered with this status (200 answers a body that is not JSON): a lost answer.</summary>
    public HttpStatusCode? MoveApplyThenFail { get; set; }

    /// <summary>When set, the route throws it instead of answering, as an unreachable or timed-out Heimdall does.</summary>
    public Exception? MoveDown { get; set; }

    public string AddLogin(string account, string provider, string? id = null)
    {
        id ??= Guid.NewGuid().ToString();
        if (!Logins.TryGetValue(account, out var list))
        {
            Logins[account] = list = [];
        }

        list.Add(new MoveLogin(id, provider));
        return id;
    }

    /// <summary>
    /// Issues a live bearer of <paramref name="account"/> for the bifrost app. A weak proof is a sign-in through <paramref name="authIdentityId"/>
    /// at <paramref name="authAgo"/> before now; a step-up (<paramref name="stepUpMethod"/>) adds step_up_at at <paramref name="stepUpAgo"/> before now.
    /// </summary>
    public string Issue(string account, string? authIdentityId = null, string? stepUpMethod = null, int authAgo = 0, int? stepUpAgo = null, string aud = App)
    {
        var at = now().ToUnixTimeSeconds();
        var token = "bearer-" + Guid.NewGuid().ToString("N");
        _tokens[token] = new Claims(
            account, Guid.NewGuid().ToString(), aud, at - authAgo, authIdentityId,
            stepUpMethod is null ? null : at - (stepUpAgo ?? 0), stepUpMethod, at + Lifetime);
        return token;
    }

    public bool IsRevoked(string token) => _revoked.Contains(_tokens[token].Sid);

    public HttpResponseMessage Route(HttpRequestMessage request, string body, string? requiredSecret)
    {
        var secret = request.Headers.TryGetValues("x-heimdall-app-secret", out var values) ? values.Single() : null;
        Requests.Add((secret, request.Headers.Authorization?.ToString(), body));
        if (MoveDown is not null)
        {
            throw MoveDown;
        }

        if (Parse(body) is not { } move)
        {
            return Json(HttpStatusCode.BadRequest, """{"statusCode":400,"error":"Bad Request","message":"body does not match the schema"}""");
        }

        if (MoveStatus is { } forced)
        {
            return forced == HttpStatusCode.NotFound
                ? new HttpResponseMessage(forced)
                : Json(forced, forced == HttpStatusCode.BadRequest
                    ? """{"statusCode":400,"error":"Bad Request"}"""
                    : """{"error":"server_error","detail":"HEIMDALL FREE TEXT"}""");
        }

        if (secret != requiredSecret)
        {
            return Refuse(HttpStatusCode.Unauthorized, "app_auth_required");
        }

        if (!Granted)
        {
            return Refuse(HttpStatusCode.Forbidden, "not_allowed_for_app");
        }

        var applied = Decide(move);
        if (applied.Refusal is { } refusal || MoveApplyThenFail is not { } lost)
        {
            return applied.Refusal ?? Ok(applied.Receipt!);
        }

        return lost == HttpStatusCode.OK ? Json(HttpStatusCode.OK, "not json {") : Json(lost, """{"error":"server_error","detail":"HEIMDALL FREE TEXT"}""");
    }

    private sealed record Move(string MoveId, string[] FromProofs, string? ToProof, string[]? IdentityIds, string? ReversesMoveId);

    private static readonly System.Text.RegularExpressions.Regex MoveIdShape = new("^[A-Za-z0-9_-]{16,64}$");

    private static Move? Parse(string body)
    {
        try
        {
            var root = JsonDocument.Parse(body).RootElement;
            string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            string[]? List(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : null!).ToArray()
                : null;
            var from = List("fromProofs");
            var ids = List("identityIds");
            if (Text("moveId") is not { } id || !MoveIdShape.IsMatch(id)
                || from is not { Length: >= 1 and <= 8 } || from.Any(x => x is null)
                || ids is { Length: 0 or > 32 } || (ids is not null && ids.Any(x => x is null)))
            {
                return null;
            }

            return new Move(id, from, Text("toProof"), ids, Text("reversesMoveId"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private (HttpResponseMessage? Refusal, string? Receipt) Decide(Move move)
    {
        var at = now().ToUnixTimeSeconds();
        var reversal = move.ReversesMoveId is not null;

        // Replay: registry match of app and accounts, liveness not checked.
        var known = move.FromProofs.Select(Known).ToArray();
        if (_moves.TryGetValue(move.MoveId, out var stored))
        {
            if (known.Any(x => x is null) || known.Select(x => x!.Account).Distinct().Count() != 1)
            {
                return (Refuse(HttpStatusCode.Forbidden, "proof_invalid", proof: "from"), null);
            }

            var toKnown = move.ToProof is null ? null : Known(move.ToProof);
            if (move.ToProof is not null && toKnown is null)
            {
                return (Refuse(HttpStatusCode.Forbidden, "proof_invalid", proof: "to"), null);
            }

            return known[0]!.Account == stored.From && toKnown?.Account == (reversal ? null : stored.To) && move.ReversesMoveId == stored.ReversesMoveId
                ? (null, stored.Receipt)
                : (Refuse(HttpStatusCode.Conflict, "move_id_conflict"), null);
        }

        StoredMove? forward = null;
        if (reversal && !(_moves.TryGetValue(move.ReversesMoveId!, out forward) && forward.ReversesMoveId is null && !forward.Reversed
                          && (known[0] is not { } holder || holder.Account == forward.To)))
        {
            return (Refuse(HttpStatusCode.Conflict, "not_reversible"), null);
        }

        // proof_invalid: not a live bearer of this app, or fromProofs naming two accounts.
        var from = move.FromProofs.Select(Live).ToArray();
        if (from.Any(x => x is null) || from.Select(x => x!.Account).Distinct().Count() != 1)
        {
            return (Refuse(HttpStatusCode.Forbidden, "proof_invalid", proof: "from"), null);
        }

        var to = move.ToProof is null ? null : Live(move.ToProof);
        if (move.ToProof is not null && to is null)
        {
            return (Refuse(HttpStatusCode.Forbidden, "proof_invalid", proof: "to"), null);
        }

        var source = from[0]!.Account;
        if (to is not null && to.Account == source)
        {
            return (Refuse(HttpStatusCode.Conflict, "same_account"), null);
        }

        // proof_stale: weak 300 s by auth_at, strong 600 s by step_up_at, the host clock.
        if (from.Any(x => Tier(x!, at) is null))
        {
            return (Refuse(HttpStatusCode.Forbidden, "proof_stale", proof: "from"), null);
        }

        if (to is not null && Tier(to, at) is null)
        {
            return (Refuse(HttpStatusCode.Forbidden, "proof_stale", proof: "to"), null);
        }

        var held = Logins.GetValueOrDefault(source) ?? [];
        var carried = reversal ? forward!.Carried.ToArray() : move.IdentityIds ?? held.Select(x => x.Id).ToArray();
        var whole = from.Any(x => Tier(x!, at) == "strong" && x!.StepUpMethod == "bifrost-passkey");
        if (!whole && carried.Any(id => from.All(x => x!.AuthIdentityId != id)))
        {
            return (Refuse(HttpStatusCode.Forbidden, "identity_not_proved"), null);
        }

        var destination = reversal ? forward!.From : to!.Account;
        if (!reversal && (IsAlias(source) || IsAlias(destination)))
        {
            return (Refuse(HttpStatusCode.Conflict, "already_moved"), null);
        }

        if (carried.Any(id => held.All(x => x.Id != id)))
        {
            return (Refuse(HttpStatusCode.NotFound, "identity_not_found"), null);
        }

        var moving = held.Where(x => carried.Contains(x.Id)).ToList();
        var there = Logins.GetValueOrDefault(destination) ?? [];
        if (moving.FirstOrDefault(x => there.Any(y => y.Provider == x.Provider)) is { } clash)
        {
            return (Refuse(HttpStatusCode.Conflict, "provider_conflict", provider: clash.Provider), null);
        }

        // The move: re-home, revoke the source's sessions, store the receipt.
        held.RemoveAll(moving.Contains);
        Logins[destination] = [.. there, .. moving];
        foreach (var claims in _tokens.Values.Where(x => x.Account == source))
        {
            _revoked.Add(claims.Sid);
        }

        var strong = from.Any(x => Tier(x!, at) == "strong");
        var receipt = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["moveId"] = move.MoveId,
            ["accountId"] = destination,
            ["movedIdentityIds"] = moving.Select(x => x.Id).ToArray(),
            ["proof"] = new Dictionary<string, object?> { ["from"] = strong ? "strong" : "weak", ["to"] = to is null ? null : Tier(to, at) },
            ["reversesMoveId"] = move.ReversesMoveId
        }.Where(x => x.Key != "reversesMoveId" || x.Value is not null).ToDictionary());
        _moves[move.MoveId] = new StoredMove(move.MoveId, source, destination, move.ReversesMoveId, moving.Select(x => x.Id).ToArray(), receipt);
        if (forward is not null)
        {
            forward.Reversed = true;
        }

        return (null, receipt);
    }

    private bool IsAlias(string account) =>
        (Logins.GetValueOrDefault(account)?.Count ?? 0) == 0 && _moves.Values.Any(x => x.From == account && x.ReversesMoveId is null && !x.Reversed);

    private Claims? Known(string token) => _tokens.TryGetValue(token, out var claims) && claims.Aud == App ? claims : null;

    private Claims? Live(string token) =>
        Known(token) is { } claims && !_revoked.Contains(claims.Sid) && claims.Exp > now().ToUnixTimeSeconds() ? claims : null;

    private static string? Tier(Claims claims, long at) =>
        claims.StepUpAt is { } stepUp && at - stepUp <= StrongSeconds ? "strong" : at - claims.AuthAt <= WeakSeconds ? "weak" : null;

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Ok(string receipt)
    {
        var response = Json(HttpStatusCode.OK, receipt);
        response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true };
        return response;
    }

    private static HttpResponseMessage Refuse(HttpStatusCode status, string error, string? proof = null, string? provider = null) =>
        Json(status, JsonSerializer.Serialize(new Dictionary<string, object?> { ["error"] = error, ["proof"] = proof, ["provider"] = provider }
            .Where(x => x.Value is not null).ToDictionary()));
}
