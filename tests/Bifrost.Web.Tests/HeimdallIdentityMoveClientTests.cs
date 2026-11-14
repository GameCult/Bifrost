using System.Net;
using System.Text;
using System.Text.Json;
using Bifrost.Web.Configuration;
using Bifrost.Web.Features.Heimdall;
using Bifrost.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moved = Bifrost.Web.Features.Heimdall.HeimdallMoveOutcome.Moved;
using Refused = Bifrost.Web.Features.Heimdall.HeimdallMoveOutcome.Refused;
using Unknown = Bifrost.Web.Features.Heimdall.HeimdallMoveOutcome.Unknown;

namespace Bifrost.Web.Tests;

/// <summary>
/// Bifrost's client for Heimdall's identity-move route, from the caller's side, against the fake Heimdall's account model, which answers
/// as Heimdall 4fe367a's service contract does. Moved and Refused are definitive; anything else is Unknown and is healed by replaying the same moveId.
/// </summary>
public sealed class HeimdallIdentityMoveClientTests
{
    private const string Passkey = "bifrost-passkey";
    private const string EmailCode = "bifrost-email-code";

    private static string NewMoveId() => "move-" + Guid.NewGuid().ToString("N");

    private static string NewAccount() => HeimdallShapes.NewAccountId();

    private static HeimdallIdentityMoveClient ClientOf(HeimdallHost host) => host.Services.GetRequiredService<HeimdallIdentityMoveClient>();

    private static Task<HeimdallMoveOutcome> Move(HeimdallHost host, HeimdallMoveRequest request) =>
        ClientOf(host).MoveAsync(request, CancellationToken.None);

    private static HeimdallMoveReceipt Receipt(HeimdallMoveOutcome outcome) => Assert.IsType<Moved>(outcome).Receipt;

    /// <summary>An account holding these providers, one login each, in order.</summary>
    private static (string Account, string[] Ids) AccountWith(FakeMoves moves, params string[] providers)
    {
        var account = NewAccount();
        return (account, providers.Select(p => moves.AddLogin(account, p)).ToArray());
    }

    /// <summary>The usual forward move of everything: a passkey proof of the source and a weak proof of the destination.</summary>
    private static HeimdallMoveRequest Forward(FakeMoves moves, string source, string destination, string? destinationLogin = null, string[]? ids = null) =>
        new(NewMoveId(), [moves.Issue(source, stepUpMethod: Passkey)], moves.Issue(destination, destinationLogin), ids);

    // ---- the forward move and its receipt ----

    [Fact]
    public async Task A_forward_move_with_one_weak_proof_per_login_answers_the_receipt_under_the_app_secret_and_names_no_account()
    {
        using var host = new HeimdallHost();
        var moves = host.Heimdall.Moves;
        var (source, ids) = AccountWith(moves, "discord", "patreon");
        var (destination, destinationIds) = AccountWith(moves, "twitch");
        var request = new HeimdallMoveRequest(
            NewMoveId(), [moves.Issue(source, ids[0]), moves.Issue(source, ids[1])], moves.Issue(destination, destinationIds[0]));

        var receipt = Receipt(await Move(host, request));

        Assert.Equal(new HeimdallMoveReceipt(request.MoveId, destination, ids, HeimdallProofTier.Weak, HeimdallProofTier.Weak, null), receipt with { MovedIdentityIds = ids });
        Assert.Equal(ids, receipt.MovedIdentityIds);
        Assert.Equal(["twitch", "discord", "patreon"], moves.Logins[destination].Select(x => x.Provider));
        Assert.Empty(moves.Logins[source]);
        var sent = Assert.Single(moves.Requests);
        Assert.Equal(HeimdallHost.AppSecret, sent.Secret);
        Assert.Null(sent.Authorization);
        Assert.DoesNotContain(source, sent.Body);
        Assert.DoesNotContain(destination, sent.Body);
        var names = JsonDocument.Parse(sent.Body).RootElement.EnumerateObject().Select(x => x.Name).Order().ToArray();
        Assert.Equal(["fromProofs", "moveId", "toProof"], names);
    }

    [Fact]
    public async Task A_proof_through_one_login_moves_only_that_login_and_only_an_asserted_passkey_speaks_for_the_account()
    {
        using var host = new HeimdallHost();
        var moves = host.Heimdall.Moves;
        var (destination, _) = AccountWith(moves, "twitch");

        // Discord proves Discord; Patreon, and so the whole account, is not proved by it.
        var (a, aIds) = AccountWith(moves, "discord", "patreon");
        Assert.Equal(new Refused(HeimdallMoveRefusal.IdentityNotProved),
            await Move(host, new(NewMoveId(), [moves.Issue(a, aIds[0])], moves.Issue(destination), aIds[1..])));
        Assert.Equal(new Refused(HeimdallMoveRefusal.IdentityNotProved),
            await Move(host, new(NewMoveId(), [moves.Issue(a, aIds[0])], moves.Issue(destination))));
        Assert.Equal(2, moves.Logins[a].Count);

        // An email code proves an inbox: strong, fresh, and still only its own login.
        Assert.Equal(new Refused(HeimdallMoveRefusal.IdentityNotProved),
            await Move(host, new(NewMoveId(), [moves.Issue(a, aIds[0], EmailCode)], moves.Issue(destination))));
        Assert.Equal(new Refused(HeimdallMoveRefusal.IdentityNotProved),
            await Move(host, new(NewMoveId(), [moves.Issue(a, aIds[0], EmailCode)], moves.Issue(destination), aIds[1..])));
        Assert.Equal(2, moves.Logins[a].Count);

        // The same proof does move the one login it came through.
        Assert.Equal([aIds[0]], Receipt(await Move(host, new(NewMoveId(), [moves.Issue(a, aIds[0], EmailCode)], moves.Issue(destination), aIds[..1]))).MovedIdentityIds);
        (destination, _) = AccountWith(moves, "twitch");

        // An asserted passkey moves everything.
        var (b, bIds) = AccountWith(moves, "discord", "patreon", "google");
        var all = Receipt(await Move(host, Forward(moves, b, destination)));
        Assert.Equal(bIds, all.MovedIdentityIds);
        Assert.Equal(HeimdallProofTier.Strong, all.ProofFrom);
    }

    [Fact]
    public async Task A_proof_by_a_stale_weak_sign_in_or_step_up_is_refused_with_its_side_at_the_window_edge()
    {
        using var host = new HeimdallHost();
        var moves = host.Heimdall.Moves;
        var (destination, _) = AccountWith(moves, "twitch");

        HeimdallMoveRequest Weak(string source, string login, int ago, int toAgo = 0) =>
            new(NewMoveId(), [moves.Issue(source, login, authAgo: ago)], moves.Issue(destination, authAgo: toAgo));

        var (a, a1) = AccountWith(moves, "discord");
        Assert.Equal(new Refused(HeimdallMoveRefusal.ProofStale, HeimdallProofSide.From), await Move(host, Weak(a, a1[0], 301)));
        Assert.Equal(new Refused(HeimdallMoveRefusal.ProofStale, HeimdallProofSide.To), await Move(host, Weak(a, a1[0], 0, 301)));
        Assert.Equal(HeimdallProofTier.Weak, Receipt(await Move(host, Weak(a, a1[0], 300))).ProofFrom);

        var (b, _) = AccountWith(moves, "patreon");
        Assert.Equal(new Refused(HeimdallMoveRefusal.ProofStale, HeimdallProofSide.From),
            await Move(host, new(NewMoveId(), [moves.Issue(b, stepUpMethod: Passkey, authAgo: 601, stepUpAgo: 601)], moves.Issue(destination))));
        Assert.Equal(HeimdallProofTier.Strong,
            Receipt(await Move(host, new(NewMoveId(), [moves.Issue(b, stepUpMethod: Passkey, authAgo: 600, stepUpAgo: 600)], moves.Issue(destination)))).ProofFrom);
    }

    // ---- replay ----

    [Fact]
    public async Task A_replayed_move_id_answers_the_first_receipt_after_the_sources_sessions_were_revoked_and_the_tokens_expired()
    {
        using var host = new HeimdallHost();
        var moves = host.Heimdall.Moves;
        var (source, ids) = AccountWith(moves, "discord", "patreon");
        var (destination, _) = AccountWith(moves, "twitch");
        var request = Forward(moves, source, destination);
        var first = Receipt(await Move(host, request));
        Assert.True(moves.IsRevoked(request.FromProofs[0]));
        host.Clock.Advance(TimeSpan.FromHours(2));

        var again = Receipt(await Move(host, request));

        Assert.Equal(first.AccountId, again.AccountId);
        Assert.Equal(ids, again.MovedIdentityIds);
        Assert.Equal(first.ProofFrom, again.ProofFrom);
        Assert.Single(moves.Stored);
        Assert.Equal(2, moves.Requests.Count);
    }

    [Fact]
    public async Task A_replay_with_different_identity_ids_answers_the_first_receipt_and_moves_nothing_more()
    {
        using var host = new HeimdallHost();
        var moves = host.Heimdall.Moves;
        var (source, ids) = AccountWith(moves, "discord", "patreon");
        var (destination, _) = AccountWith(moves, "twitch");
        var proofs = new[] { moves.Issue(source, stepUpMethod: Passkey) };
        var toProof = moves.Issue(destination);
        var moveId = NewMoveId();

        var first = Receipt(await Move(host, new(moveId, proofs, toProof, ids[..1])));
        var again = Receipt(await Move(host, new(moveId, proofs, toProof, ids)));

        Assert.Equal([ids[0]], first.MovedIdentityIds);
        Assert.Equal([ids[0]], again.MovedIdentityIds);
        Assert.Equal([ids[1]], moves.Logins[source].Select(x => x.Id));
    }

    [Fact]
    public async Task A_move_whose_answer_was_lost_heals_by_replaying_the_same_request()
    {
        using var host = new HeimdallHost();
        var moves = host.Heimdall.Moves;
        var (source, ids) = AccountWith(moves, "discord");
        var (destination, _) = AccountWith(moves, "twitch");
        var request = Forward(moves, source, destination);

        moves.MoveApplyThenFail = HttpStatusCode.InternalServerError;
        Assert.IsType<Unknown>(await Move(host, request));
        Assert.Single(moves.Stored);
        moves.MoveApplyThenFail = HttpStatusCode.OK;
        Assert.IsType<Unknown>(await Move(host, request));

        moves.MoveApplyThenFail = null;
        var healed = Receipt(await Move(host, request));
        Assert.Equal(destination, healed.AccountId);
        Assert.Equal(ids, healed.MovedIdentityIds);
        Assert.Single(moves.Stored);
    }

    // ---- reversal ----

    [Fact]
    public async Task A_reversal_moves_back_exactly_the_logins_the_forward_move_carried_once()
    {
        using var host = new HeimdallHost();
        var moves = host.Heimdall.Moves;
        var (source, ids) = AccountWith(moves, "discord", "patreon");
        var (destination, destinationIds) = AccountWith(moves, "twitch");
        var forward = Forward(moves, source, destination, ids: ids[..1]);
        Receipt(await Move(host, forward));
        Assert.Equal(["twitch", "discord"], moves.Logins[destination].Select(x => x.Provider));

        // The destination's bearer proves a login it holds; the carried one is proved by its own sign-in.
        var undo = new HeimdallMoveRequest(NewMoveId(), [moves.Issue(destination, ids[0])], ReversesMoveId: forward.MoveId);
        var receipt = Receipt(await Move(host, undo));

        Assert.Equal(source, receipt.AccountId);
        Assert.Equal([ids[0]], receipt.MovedIdentityIds);
        Assert.Equal(forward.MoveId, receipt.ReversesMoveId);
        Assert.Null(receipt.ProofTo);
        Assert.Equal([destinationIds[0]], moves.Logins[destination].Select(x => x.Id));
        Assert.Equal(ids.Order(), moves.Logins[source].Select(x => x.Id).Order());

        Assert.Equal(new Refused(HeimdallMoveRefusal.NotReversible),
            await Move(host, new(NewMoveId(), [moves.Issue(destination, destinationIds[0], Passkey)], ReversesMoveId: forward.MoveId)));
        Assert.Equal(new Refused(HeimdallMoveRefusal.NotReversible),
            await Move(host, new(NewMoveId(), [moves.Issue(source, ids[0], Passkey)], ReversesMoveId: undo.MoveId)));
    }

    // ---- refusals ----

    [Fact]
    public async Task Each_contract_refusal_code_maps_to_its_member_and_only_provider_conflict_carries_a_provider()
    {
        using var host = new HeimdallHost();
        var moves = host.Heimdall.Moves;

        // not_allowed_for_app
        var (s0, _) = AccountWith(moves, "discord");
        var (d0, _) = AccountWith(moves, "twitch");
        moves.Granted = false;
        Assert.Equal(new Refused(HeimdallMoveRefusal.NotAllowedForApp), await Move(host, Forward(moves, s0, d0)));
        moves.Granted = true;

        // proof_invalid: unknown, revoked, foreign audience (from side); a lapsed destination proof (to side)
        Assert.Equal(new Refused(HeimdallMoveRefusal.ProofInvalid, HeimdallProofSide.From),
            await Move(host, new(NewMoveId(), ["no-such-bearer"], moves.Issue(d0))));
        Assert.Equal(new Refused(HeimdallMoveRefusal.ProofInvalid, HeimdallProofSide.From),
            await Move(host, new(NewMoveId(), [moves.Issue(s0, stepUpMethod: Passkey, aud: "someone-else")], moves.Issue(d0))));
        Assert.Equal(new Refused(HeimdallMoveRefusal.ProofInvalid, HeimdallProofSide.To),
            await Move(host, new(NewMoveId(), [moves.Issue(s0, stepUpMethod: Passkey)], "no-such-bearer")));
        var (other, _) = AccountWith(moves, "google");
        Assert.Equal(new Refused(HeimdallMoveRefusal.ProofInvalid, HeimdallProofSide.From),
            await Move(host, new(NewMoveId(), [moves.Issue(s0, stepUpMethod: Passkey), moves.Issue(other, stepUpMethod: Passkey)], moves.Issue(d0))));

        // same_account
        Assert.Equal(new Refused(HeimdallMoveRefusal.SameAccount),
            await Move(host, new(NewMoveId(), [moves.Issue(s0, stepUpMethod: Passkey)], moves.Issue(s0))));

        // identity_not_found: the proof is a passkey, the login is nobody's
        Assert.Equal(new Refused(HeimdallMoveRefusal.IdentityNotFound),
            await Move(host, Forward(moves, s0, d0, ids: [Guid.NewGuid().ToString()])));

        // provider_conflict: the destination already holds a Discord login
        var (s1, _) = AccountWith(moves, "discord");
        var (d1, _) = AccountWith(moves, "discord");
        Assert.Equal(new Refused(HeimdallMoveRefusal.ProviderConflict, Provider: "discord"), await Move(host, Forward(moves, s1, d1)));

        // move_id_conflict is not a refusal: a committed move answers it when its proofs no longer verify, so the caller keeps its proofs.
        var (s2, _) = AccountWith(moves, "patreon");
        var (d2, _) = AccountWith(moves, "twitch");
        var used = Forward(moves, s2, d2);
        Receipt(await Move(host, used));
        var (s3, _) = AccountWith(moves, "patreon");
        Assert.Equal(new Unknown("move_id_conflict"),
            await Move(host, new(used.MoveId, [moves.Issue(s3, stepUpMethod: Passkey)], moves.Issue(d2))));
        Assert.Equal(new Unknown("move_id_conflict"),
            await Move(host, new(used.MoveId, ["forged-bearer"], used.ToProof)));

        // already_moved: s2 now holds nothing and an unreversed move left it
        Assert.Equal(new Refused(HeimdallMoveRefusal.AlreadyMoved), await Move(host, Forward(moves, s2, d0)));
        Assert.Equal(new Refused(HeimdallMoveRefusal.AlreadyMoved), await Move(host, Forward(moves, s3, s2)));

        // not_reversible: a move that never happened
        Assert.Equal(new Refused(HeimdallMoveRefusal.NotReversible),
            await Move(host, new(NewMoveId(), [moves.Issue(d2, stepUpMethod: Passkey)], ReversesMoveId: NewMoveId())));
    }

    [Fact]
    public async Task A_wrong_app_secret_is_refused_as_app_auth_required()
    {
        using var host = new HeimdallHost(appSecret: "not-the-secret");
        var moves = host.Heimdall.Moves;
        var (source, _) = AccountWith(moves, "discord");
        var (destination, _) = AccountWith(moves, "twitch");

        Assert.Equal(new Refused(HeimdallMoveRefusal.AppAuthRequired), await Move(host, Forward(moves, source, destination)));
        Assert.Empty(moves.Stored);
    }

    [Fact]
    public async Task A_400_carrying_Heimdalls_invalid_request_code_is_RequestInvalid_a_definitive_refusal_that_moves_nothing()
    {
        using var host = new HeimdallHost();
        var moves = host.Heimdall.Moves;
        var (source, _) = AccountWith(moves, "discord");
        var (destination, _) = AccountWith(moves, "twitch");
        moves.MoveStatus = HttpStatusCode.BadRequest;

        Assert.Equal(new Refused(HeimdallMoveRefusal.RequestInvalid), await Move(host, Forward(moves, source, destination)));
        Assert.Empty(moves.Stored);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "<html><body>404 Not Found</body></html>")]
    [InlineData(HttpStatusCode.NotFound, "")]
    [InlineData(HttpStatusCode.BadRequest, "<html><head><title>400 Bad Request</title></head><body>nginx</body></html>")]
    [InlineData(HttpStatusCode.BadRequest, "")]
    [InlineData(HttpStatusCode.BadRequest, """{"statusCode":400,"error":"Bad Request","message":"x"}""")]
    [InlineData(HttpStatusCode.BadRequest, """{"message":"no error field"}""")]
    [InlineData(HttpStatusCode.Conflict, """{"error":"invalid_request"}""")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, """{"error":"invalid_request"}""")] // Heimdall's own 413 for a body over its limit; the client's rule is exactly 400
    [InlineData(HttpStatusCode.UnsupportedMediaType, """{"error":"invalid_request"}""")]
    [InlineData(HttpStatusCode.RequestTimeout, "")]
    [InlineData(HttpStatusCode.RequestTimeout, """{"error":"request_timeout"}""")]
    [InlineData(HttpStatusCode.Gone, "")]
    [InlineData(HttpStatusCode.UnavailableForLegalReasons, "")]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":"weird_code"}""")]
    [InlineData(HttpStatusCode.Forbidden, """{"message":"no error field"}""")]
    [InlineData((HttpStatusCode)418, """{"error":"app_auth_required_sort_of"}""")]
    public async Task A_4xx_without_one_of_Heimdalls_move_codes_is_Unknown_because_it_proves_nothing_was_applied_no_more_than_a_gateway_does(
        HttpStatusCode status, string body)
    {
        Assert.IsType<Unknown>(await Answered(body, Plain, status));
    }

    [Fact]
    public async Task A_404_with_no_body_from_the_route_is_Unknown_not_identity_not_found()
    {
        using var host = new HeimdallHost();
        var moves = host.Heimdall.Moves;
        var (source, _) = AccountWith(moves, "discord");
        var (destination, _) = AccountWith(moves, "twitch");
        moves.MoveStatus = HttpStatusCode.NotFound;

        Assert.IsType<Unknown>(await Move(host, Forward(moves, source, destination)));
    }

    [Fact]
    public async Task An_exception_of_any_kind_from_the_pipeline_is_Unknown_and_only_the_callers_cancellation_propagates()
    {
        Exception[] thrown =
        [
            new InvalidOperationException("handler"), new ObjectDisposedException("connection"),
            new System.Security.Authentication.AuthenticationException("tls"), new InvalidDataException("zip"), new UriFormatException("uri"),
            new HttpRequestException("down"), new IOException("reset"), new TaskCanceledException("deadline")
        ];
        foreach (var error in thrown)
        {
            var client = new HeimdallIdentityMoveClient(
                new HttpClient(new Throwing(error)) { BaseAddress = new Uri("https://heimdall.test") },
                Options.Create(new HeimdallOptions()), NullLogger<HeimdallIdentityMoveClient>.Instance);

            Assert.IsType<Unknown>(await client.MoveAsync(Plain, CancellationToken.None));
        }
    }

    private sealed class Throwing(Exception error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw error;
    }

    // ---- unknown outcomes ----

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Found)]
    public async Task A_5xx_a_429_or_any_other_status_is_Unknown_never_a_refusal(HttpStatusCode status)
    {
        using var host = new HeimdallHost();
        var moves = host.Heimdall.Moves;
        var (source, _) = AccountWith(moves, "discord");
        var (destination, _) = AccountWith(moves, "twitch");
        moves.MoveStatus = status;

        Assert.IsType<Unknown>(await Move(host, Forward(moves, source, destination)));
    }

    [Fact]
    public async Task A_timeout_a_transport_error_and_garbage_with_a_200_are_Unknown_while_the_callers_own_cancellation_propagates()
    {
        using var host = new HeimdallHost();
        var moves = host.Heimdall.Moves;
        var (source, _) = AccountWith(moves, "discord");
        var (destination, _) = AccountWith(moves, "twitch");
        var request = Forward(moves, source, destination);

        moves.MoveDown = new TaskCanceledException("the client timed out");
        Assert.IsType<Unknown>(await Move(host, request));
        moves.MoveDown = new HttpRequestException("Heimdall is down");
        Assert.IsType<Unknown>(await Move(host, request));
        moves.MoveDown = null;
        moves.MoveApplyThenFail = HttpStatusCode.OK;
        Assert.IsType<Unknown>(await Move(host, request));
        moves.MoveApplyThenFail = null;

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClientOf(host).MoveAsync(request, cancelled.Token));
    }

    private sealed class Stub(Func<HttpContent> content, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Stub(string json, HttpStatusCode status = HttpStatusCode.OK)
            : this(() => new StringContent(json, Encoding.UTF8, "application/json"), status)
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = content() });
    }

    /// <summary>A body that serves a prefix and then stalls, resets, or streams endless filler.</summary>
    private sealed class Body(string prefix, bool reset = false, long endless = 0) : Stream
    {
        private readonly byte[] _prefix = Encoding.UTF8.GetBytes(prefix);

        public long Served { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => Served; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Served < _prefix.Length)
            {
                var n = (int)Math.Min(buffer.Length, _prefix.Length - Served);
                _prefix.AsMemory((int)Served, n).CopyTo(buffer);
                Served += n;
                return n;
            }

            if (endless > 0 && Served < _prefix.Length + endless)
            {
                var n = (int)Math.Min(buffer.Length, _prefix.Length + endless - Served);
                buffer.Span[..n].Fill((byte)'a');
                Served += n;
                return n;
            }

            if (reset)
            {
                throw new IOException("connection reset mid-body");
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private static HeimdallIdentityMoveClient Client(HttpMessageHandler handler, TimeSpan? timeout = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://heimdall.test"), Timeout = timeout ?? TimeSpan.FromSeconds(30) },
            Options.Create(new HeimdallOptions()), NullLogger<HeimdallIdentityMoveClient>.Instance);

    private static Task<HeimdallMoveOutcome> Answered(string json, HeimdallMoveRequest request, HttpStatusCode status = HttpStatusCode.OK) =>
        Client(new Stub(json, status)).MoveAsync(request, CancellationToken.None);

    private static readonly HeimdallMoveRequest Plain = new("move-0123456789abcdef", ["p"], "q");

    [Fact]
    public async Task A_200_that_is_not_a_whole_receipt_for_this_request_is_Unknown()
    {
        var id = Guid.NewGuid().ToString();
        var forward = new HeimdallMoveRequest("move-0123456789abcdef", ["p"], "q");
        var reversal = new HeimdallMoveRequest("move-0123456789abcdef", ["p"], ReversesMoveId: "move-fedcba9876543210");
        string Body(string moveId = "move-0123456789abcdef", string? reverses = null, string proof = """{"from":"weak","to":"weak"}""", string ids = "\"" + "ID" + "\"") =>
            $$"""{"moveId":"{{moveId}}","accountId":"acct","movedIdentityIds":[{{ids.Replace("ID", id)}}],"proof":{{proof}}{{(reverses is null ? "" : $",\"reversesMoveId\":\"{reverses}\"")}}}""";

        // The well formed shapes are accepted, so each refusal below is about its one flaw.
        Assert.IsType<Moved>(await Answered(Body(), forward));
        Assert.IsType<Moved>(await Answered(Body(ids: "\"not-a-guid-but-an-id\""), forward));
        Assert.IsType<Moved>(await Answered(Body("move-0123456789abcdef", "move-fedcba9876543210", """{"from":"strong","to":null}"""), reversal));

        Assert.IsType<Unknown>(await Answered(Body("move-someoneelse12345"), forward));
        Assert.IsType<Unknown>(await Answered(Body(reverses: "move-fedcba9876543210"), forward));
        Assert.IsType<Unknown>(await Answered(Body(), reversal));
        Assert.IsType<Unknown>(await Answered(Body("move-0123456789abcdef", "move-other0123456789", """{"from":"strong","to":null}"""), reversal));
        Assert.IsType<Unknown>(await Answered(Body("move-0123456789abcdef", "move-fedcba9876543210", """{"from":"strong","to":"weak"}"""), reversal));
        Assert.IsType<Unknown>(await Answered(Body(proof: """{"from":"strong","to":null}"""), forward));
        Assert.IsType<Unknown>(await Answered(Body(proof: """{"from":"maybe","to":"weak"}"""), forward));
        Assert.IsType<Unknown>(await Answered(Body(ids: ""), forward));
        Assert.IsType<Unknown>(await Answered(Body(ids: "123"), forward));
        Assert.IsType<Unknown>(await Answered("""{"moveId":"move-0123456789abcdef"}""", forward));
        Assert.IsType<Unknown>(await Answered("[]", forward));
    }

    // ---- the closed body ----

    [Fact]
    public async Task A_request_that_breaks_the_contracts_body_throws_before_any_request_reaches_Heimdall()
    {
        using var host = new HeimdallHost();
        var moves = host.Heimdall.Moves;
        var good = NewMoveId();
        var proofs = new[] { "p" };
        var broken = new HeimdallMoveRequest[]
        {
            new(good, proofs, "t", ReversesMoveId: NewMoveId()),
            new(good, proofs),
            new(good, Enumerable.Repeat("p", 9).ToArray(), "t"),
            new(good, [], "t"),
            new("short", proofs, "t"),
            new("bad move id with spaces", proofs, "t"),
            new(good, proofs, ReversesMoveId: "short"),
            new(good, proofs, IdentityIds: ["i"], ReversesMoveId: NewMoveId()),
            new(good, proofs, "t", []),
            new(good, proofs, "t", Enumerable.Repeat("i", 33).ToArray()),
            new(good, [""], "t"),
            new(good + "\n", proofs, "t"),
            new(good, proofs, ReversesMoveId: good + "\n"),
            new("move-0123456789abcdef\n", proofs, "t"),
            new(new string('a', 65), proofs, "t"),
            new(new string('a', 15), proofs, "t"),
            new(good, proofs, ReversesMoveId: new string('a', 65))
        };

        foreach (var request in broken)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => Move(host, request));
        }

        Assert.Empty(moves.Requests);
    }

    // ---- the wire: bounds, deadline, redirects, secrets ----

    [Fact]
    public async Task A_body_that_stalls_after_the_headers_is_Unknown_within_the_clients_deadline_for_a_receipt_and_a_refusal()
    {
        foreach (var (status, prefix) in new[] { (HttpStatusCode.OK, """{"moveId":"move-0123456789abcdef","""), (HttpStatusCode.Conflict, """{"error":"pro""") })
        {
            var client = Client(new Stub(() => new StreamContent(new Body(prefix)), status), TimeSpan.FromMilliseconds(500));
            var task = client.MoveAsync(Plain, CancellationToken.None);
            Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))));
            Assert.IsType<Unknown>(await task);
        }
    }

    [Fact]
    public async Task A_connection_reset_while_reading_the_body_is_Unknown_not_an_exception()
    {
        var client = Client(new Stub(() => new StreamContent(new Body("""{"moveId":"move-0123456789abcdef",""", reset: true))));

        Assert.IsType<Unknown>(await client.MoveAsync(Plain, CancellationToken.None));
    }

    [Fact]
    public async Task A_receipt_body_past_64_KiB_is_refused_unread_as_Unknown()
    {
        var body = new Body("""{"moveId":"move-0123456789abcdef","accountId":" """, endless: 64L * 1024 * 1024);
        var client = Client(new Stub(() => new StreamContent(body)));

        Assert.IsType<Unknown>(await client.MoveAsync(Plain, CancellationToken.None));
        Assert.True(body.Served < 1024 * 1024, $"read {body.Served} bytes");
    }

    [Fact]
    public async Task Refusal_qualifiers_are_kept_only_for_the_codes_that_carry_them()
    {
        Assert.Equal(new Refused(HeimdallMoveRefusal.SameAccount),
            await Answered("""{"error":"same_account","proof":"from","provider":"discord"}""", Plain, HttpStatusCode.Conflict));
        Assert.Equal(new Refused(HeimdallMoveRefusal.AlreadyMoved),
            await Answered("""{"error":"already_moved","proof":"to"}""", Plain, HttpStatusCode.Conflict));
        Assert.Equal(new Refused(HeimdallMoveRefusal.ProviderConflict, Provider: "discord"),
            await Answered("""{"error":"provider_conflict","proof":"to","provider":"discord"}""", Plain, HttpStatusCode.Conflict));
        Assert.Equal(new Refused(HeimdallMoveRefusal.ProofStale, HeimdallProofSide.To),
            await Answered("""{"error":"proof_stale","proof":"to","provider":"discord"}""", Plain, HttpStatusCode.Forbidden));
        Assert.Equal(new Refused(HeimdallMoveRefusal.ProofInvalid),
            await Answered("""{"error":"proof_invalid","proof":"sideways"}""", Plain, HttpStatusCode.Forbidden));
    }

    [Fact]
    public async Task The_largest_legal_request_is_sent_and_moves_what_it_names()
    {
        using var host = new HeimdallHost();
        var moves = host.Heimdall.Moves;
        var source = NewAccount();
        var ids = Enumerable.Range(0, 32).Select(_ => moves.AddLogin(source, "discord")).ToArray();
        var (destination, _) = AccountWith(moves, "twitch");
        var proofs = Enumerable.Range(0, 8).Select(_ => moves.Issue(source, stepUpMethod: Passkey)).ToArray();

        var longest = new string('m', 64);
        var receipt = Receipt(await Move(host, new(longest, proofs, moves.Issue(destination), ids)));
        Assert.Equal(longest, receipt.MoveId);

        Assert.Equal(ids, receipt.MovedIdentityIds);
        Assert.Equal(8, JsonDocument.Parse(moves.Requests[0].Body).RootElement.GetProperty("fromProofs").GetArrayLength());
    }

    [Fact]
    public void A_request_does_not_print_its_proofs()
    {
        var request = new HeimdallMoveRequest("move-0123456789abcdef", ["FROM-BEARER-SECRET"], "TO-BEARER-SECRET");

        Assert.DoesNotContain("BEARER-SECRET", request.ToString());
        Assert.Contains("move-0123456789abcdef", request.ToString());
    }

    private static ServiceProvider RealRegistration(string baseUrl)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new HeimdallOptions { BaseUrl = baseUrl, AppSecret = "APP-SECRET-VALUE" }));
        services.AddHeimdallIdentityMoveClient();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void The_registered_client_carries_the_app_secret_and_the_15_second_deadline()
    {
        using var provider = RealRegistration("https://heimdall.test");
        var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(HeimdallIdentityMoveClient));

        Assert.Equal(TimeSpan.FromSeconds(15), http.Timeout);
        Assert.Equal(new Uri("https://heimdall.test"), http.BaseAddress);
        Assert.Equal("APP-SECRET-VALUE", Assert.Single(http.DefaultRequestHeaders.GetValues(HeimdallAuthService.AppSecretHeader)));
    }

    [Theory]
    [InlineData(307)]
    [InlineData(302)]
    public async Task A_redirect_is_Unknown_and_nothing_reaches_the_other_origin(int code)
    {
        static int FreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        int first = FreePort(), second = FreePort();
        using var origin = new HttpListener();
        origin.Prefixes.Add($"http://127.0.0.1:{first}/");
        origin.Start();
        using var other = new HttpListener();
        other.Prefixes.Add($"http://127.0.0.1:{second}/");
        other.Start();
        var redirected = Task.Run(async () =>
        {
            var context = await origin.GetContextAsync();
            context.Response.StatusCode = code;
            context.Response.RedirectLocation = $"http://127.0.0.1:{second}/steal";
            context.Response.Close();
        });
        var stolen = new List<string>();
        _ = Task.Run(async () =>
        {
            try
            {
                var context = await other.GetContextAsync();
                stolen.Add(context.Request.Headers["x-heimdall-app-secret"] + new StreamReader(context.Request.InputStream).ReadToEnd());
                var forged = Encoding.UTF8.GetBytes($$$"""{"moveId":"move-0123456789abcdef","accountId":"attacker","movedIdentityIds":["{{{Guid.NewGuid()}}}"],"proof":{"from":"weak","to":"weak"}}""");
                context.Response.StatusCode = 200;
                await context.Response.OutputStream.WriteAsync(forged);
                context.Response.Close();
            }
            catch (HttpListenerException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        });
        using var provider = RealRegistration($"http://127.0.0.1:{first}");

        var outcome = await provider.GetRequiredService<HeimdallIdentityMoveClient>()
            .MoveAsync(new("move-0123456789abcdef", ["FROM-BEARER"], "TO-BEARER"), CancellationToken.None);
        await redirected;
        await Task.Delay(300);

        Assert.IsType<Unknown>(outcome);
        Assert.Empty(stolen);
    }

    // ---- what the logs hold ----

    [Fact]
    public async Task With_http_trace_logging_on_no_log_line_holds_a_bearer_the_secret_an_identity_id_or_an_account_id()
    {
        using var host = new HeimdallHost(traceHttp: true);
        var moves = host.Heimdall.Moves;
        var (source, ids) = AccountWith(moves, "discord", "patreon");
        var (destination, _) = AccountWith(moves, "twitch");
        var request = Forward(moves, source, destination);
        Receipt(await Move(host, request));
        Assert.IsType<Refused>(await Move(host, Forward(moves, source, destination)));

        var logs = string.Join('\n', host.Logs.Lines);
        Assert.Contains("Heimdall identity move: Moved", logs);
        Assert.Contains("Heimdall identity move: Refused", logs);
        Assert.Contains("identity-moves", logs);
        // Only the secret and bearer headers are redacted: an ordinary header's value is logged, so the shared redaction is what is in force.
        Assert.Contains("application/json", logs);
        foreach (var secret in new[] { HeimdallHost.AppSecret, source, destination, request.FromProofs[0], request.ToProof!, request.MoveId }.Concat(ids))
        {
            Assert.DoesNotContain(secret, logs);
        }
    }
}
