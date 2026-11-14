using System.Net;
using System.Text;
using Bifrost.Web.Tests.Support;

namespace Bifrost.Web.Tests;

/// <summary>
/// The fake mover is the spec the client is tested against, so it must refuse what Heimdall 4fe367a refuses, with the same status and code, in the
/// same order (app.ts moveIdentitiesRoute and its schema, identity-move.ts decideMove). Each case sends a raw body to the fake.
/// </summary>
public sealed class FakeHeimdallMoverTests
{
    private static string Id() => "move-" + Guid.NewGuid().ToString("N");

    private static string Account() => HeimdallShapes.NewAccountId();

    private static async Task<(HttpStatusCode Status, string Error)> Post(HeimdallHost host, string json)
    {
        using var client = new HttpClient(host.Heimdall, disposeHandler: false) { BaseAddress = new Uri("https://heimdall.test") };
        client.DefaultRequestHeaders.Add("x-heimdall-app-secret", HeimdallHost.AppSecret);
        using var response = await client.PostAsync("/v1/apps/bifrost/identity-moves", new StringContent(json, Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();
        var error = System.Text.Json.JsonDocument.Parse(body).RootElement.TryGetProperty("error", out var e) ? e.GetString()! : "";
        return (response.StatusCode, error);
    }

    private static string Move(string moveId, string[] from, string? to = null, string[]? ids = null, string? reverses = null) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["moveId"] = moveId,
            ["fromProofs"] = from,
            ["toProof"] = to,
            ["identityIds"] = ids,
            ["reversesMoveId"] = reverses
        }.Where(x => x.Value is not null).ToDictionary());

    [Fact]
    public async Task A_body_the_routes_schema_rejects_is_a_400_and_moves_nothing()
    {
        using var host = new HeimdallHost();
        var moves = host.Heimdall.Moves;
        var (s, d) = (Account(), Account());
        var login = moves.AddLogin(s, "discord");
        moves.AddLogin(d, "twitch");
        var forward = Id();
        Assert.Equal(HttpStatusCode.OK, (await Post(host, Move(forward, [moves.Issue(s, login, "bifrost-passkey")], moves.Issue(d)))).Status);
        var (s2, d2) = (Account(), Account());
        var login2 = moves.AddLogin(s2, "patreon");
        moves.AddLogin(d2, "twitch");
        var strong = () => moves.Issue(s2, login2, "bifrost-passkey");

        var bad = new[]
        {
            Move(Id(), [moves.Issue(d)], moves.Issue(s), reverses: forward),
            Move(Id(), [strong()]),
            Move(Id(), [strong()], reverses: "x"),
            Move(Id(), [strong()], moves.Issue(d2), [login2, login2]),
            Move(Id(), [""], "x"),
            Move(Id(), [strong()], "x", [""]),
            Move(Id(), [strong()], "x", []),
            Move(Id(), [moves.Issue(d)], ids: [login], reverses: forward),
            Move(Id(), [], "x"),
            Move(Id(), Enumerable.Repeat("p", 9).ToArray(), "x"),
            Move(Id(), [new string('p', 8193)], "x"),
            Move("short", [strong()], "x"),
            Move(Id() + "\n", [strong()], "x"),
            Move(new string('a', 65), [strong()], "x"),
            Move(Id(), [strong()], reverses: Id() + "\n"),
            """{"moveId":"move-0123456789abcdef","fromProofs":"p","toProof":"x"}""",
            "[]"
        };
        foreach (var json in bad)
        {
            Assert.Equal((HttpStatusCode.BadRequest, "invalid_request"), await Post(host, json));
        }

        Assert.Single(moves.Stored);
        Assert.Single(moves.Logins[s2]);
    }

    [Fact]
    public async Task A_source_holding_no_login_is_404_identity_not_found_not_an_empty_receipt()
    {
        using var host = new HeimdallHost();
        var moves = host.Heimdall.Moves;
        var (s, d) = (Account(), Account());
        moves.AddLogin(d, "twitch");

        var answer = await Post(host, Move(Id(), [moves.Issue(s, Guid.NewGuid().ToString(), "bifrost-passkey")], moves.Issue(d)));

        Assert.Equal((HttpStatusCode.NotFound, "identity_not_found"), answer);
    }

    [Fact]
    public async Task An_unheld_login_is_404_before_coverage_is_judged()
    {
        using var host = new HeimdallHost();
        var moves = host.Heimdall.Moves;
        var (s, d) = (Account(), Account());
        var login = moves.AddLogin(s, "discord");
        moves.AddLogin(d, "twitch");

        var answer = await Post(host, Move(Id(), [moves.Issue(s, login)], moves.Issue(d), [login, Guid.NewGuid().ToString()]));

        Assert.Equal((HttpStatusCode.NotFound, "identity_not_found"), answer);
    }

    [Fact]
    public async Task A_proof_that_covers_no_login_at_all_is_403_identity_not_proved_before_the_store_looks()
    {
        using var host = new HeimdallHost();
        var moves = host.Heimdall.Moves;
        var (s, d) = (Account(), Account());
        moves.AddLogin(d, "twitch");

        // No auth_identity_id and no passkey: nothing is covered, even though the source holds no login either.
        Assert.Equal((HttpStatusCode.Forbidden, "identity_not_proved"), await Post(host, Move(Id(), [moves.Issue(s)], moves.Issue(d))));
    }

    [Fact]
    public async Task A_future_dated_proof_proves_nothing()
    {
        using var host = new HeimdallHost();
        var moves = host.Heimdall.Moves;
        var (s, d) = (Account(), Account());
        var login = moves.AddLogin(s, "discord");
        moves.AddLogin(d, "twitch");

        Assert.Equal((HttpStatusCode.Forbidden, "proof_stale"), await Post(host, Move(Id(), [moves.Issue(s, login, authAgo: -60)], moves.Issue(d))));
    }

    [Fact]
    public async Task A_replay_whose_proofs_are_not_signed_for_the_stored_accounts_is_409_move_id_conflict_not_proof_invalid()
    {
        using var host = new HeimdallHost();
        var moves = host.Heimdall.Moves;
        var (s, d) = (Account(), Account());
        var login = moves.AddLogin(s, "discord");
        moves.AddLogin(d, "twitch");
        var id = Id();
        var to = moves.Issue(d);
        Assert.Equal(HttpStatusCode.OK, (await Post(host, Move(id, [moves.Issue(s, login)], to))).Status);

        Assert.Equal((HttpStatusCode.Conflict, "move_id_conflict"), await Post(host, Move(id, ["forged-bearer"], to)));
        Assert.Equal((HttpStatusCode.Conflict, "move_id_conflict"), await Post(host, Move(id, [moves.Issue(d)], to)));
        Assert.Equal((HttpStatusCode.Conflict, "move_id_conflict"), await Post(host, Move(id, [moves.Issue(s)], reverses: Id())));
        Assert.Equal(HttpStatusCode.OK, (await Post(host, Move(id, [moves.Issue(s)], to))).Status);
    }

    [Fact]
    public async Task A_reversal_by_the_wrong_account_or_of_a_reversed_move_is_409_not_reversible_in_Heimdalls_order()
    {
        using var host = new HeimdallHost();
        var moves = host.Heimdall.Moves;
        var (s, d, other) = (Account(), Account(), Account());
        var login = moves.AddLogin(s, "discord");
        moves.AddLogin(d, "twitch");
        moves.AddLogin(other, "google");
        var forward = Id();
        await Post(host, Move(forward, [moves.Issue(s, login, "bifrost-passkey")], moves.Issue(d)));

        // The wrong account proves the reversal: not_reversible only after its proof was judged live.
        Assert.Equal((HttpStatusCode.Forbidden, "proof_invalid"), await Post(host, Move(Id(), ["unknown"], reverses: forward)));
        Assert.Equal((HttpStatusCode.Conflict, "not_reversible"), await Post(host, Move(Id(), [moves.Issue(other, stepUpMethod: "bifrost-passkey")], reverses: forward)));
        Assert.Equal((HttpStatusCode.OK, ""), await Post(host, Move(Id(), [moves.Issue(d, login, "bifrost-passkey")], reverses: forward)));
        Assert.Equal((HttpStatusCode.Conflict, "not_reversible"), await Post(host, Move(Id(), [moves.Issue(d, stepUpMethod: "bifrost-passkey")], reverses: forward)));
    }
}
