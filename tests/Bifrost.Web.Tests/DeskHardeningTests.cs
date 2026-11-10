using System.Text;
using System.Text.Json.Nodes;
using Bifrost.Web.Configuration;
using Bifrost.Web.Features.Desk;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Tests;

// The rules the desk's edge keeps that a happy path never shows: whose decider list rides each call, the body cap
// to the byte, what the Retry-After comes from, the audience's case, no-store, and what a failing port becomes.
public sealed class DeskHardeningTests
{
    private const string Nibu = "nibu";
    private const string Chat = "gamecult-chat";
    private const string NibuFork = "nibu:question:glow-order";

    private static async Task<DeskWebApplicationFactory> ArrangeAsync(Dictionary<string, string?>? configuration = null)
    {
        var factory = new DeskWebApplicationFactory(configuration);
        DeskReplay.HoldVectorForks(factory.Door);
        await factory.EnsureAccountAsync(DeskWebApplicationFactory.Alice, true);
        return factory;
    }

    private static string Token(DeskWebApplicationFactory factory, string audience = "bifrost-desk") =>
        factory.Token(DeskWebApplicationFactory.Alice, audience, "desk.read", "desk.answer");

    private static Task<DeskReply> SendRouteAsync(DeskWebApplicationFactory factory, string route, string mind, string token)
    {
        var fork = mind == Nibu ? NibuFork : DeskReplay.ChatFork;
        return route switch
        {
            "answer" => factory.SendAsync("POST", DeskReplay.Target("answer", mind, null), DeskReplay.AnswerBody(fork), token),
            "read" => factory.SendAsync("GET", DeskReplay.Target("read", mind, fork), null, token),
            _ => factory.SendAsync("GET", DeskReplay.Target("list", mind, null), null, token),
        };
    }

    private static readonly string[] Routes = ["answer", "read", "list"];

    private static string Code(DeskReply reply) => (string)JsonNode.Parse(reply.Body)!["code"]!;

    // (a) Each Mind's list is its own: a context built from another Mind's list changes the call.
    [Fact]
    public async Task Each_call_carries_the_route_minds_own_decider_list_and_envelope()
    {
        using var factory = await ArrangeAsync();
        Assert.NotEqual(DeskWebApplicationFactory.DecidersOf(Chat), DeskWebApplicationFactory.DecidersOf(Nibu));
        var token = Token(factory);

        foreach (var mind in new[] { Chat, Nibu })
        {
            foreach (var route in Routes)
            {
                factory.Door.Calls.Clear();
                var reply = await SendRouteAsync(factory, route, mind, token);
                Assert.True(reply.Status == 200, $"{mind} {route}: {reply.Status}");
                var call = Assert.Single(factory.Door.Calls);
                Assert.True(DeskWebApplicationFactory.DecidersOf(mind).SequenceEqual(call.Context.Deciders), $"{mind} {route}: deciders");
                Assert.Equal(mind, call.Context.Mind);
                Assert.Equal(DeskWebApplicationFactory.SocketOf(mind), call.Socket);
                Assert.Equal(DeskWebApplicationFactory.Alice, call.Context.Subject);
                Assert.Equal("Operator", call.Context.Authority);
            }
        }
    }

    // (b) The cap to the byte, at the HTTP edge, however the length is announced.
    [Theory]
    [InlineData(0)]
    [InlineData(4096)]
    [InlineData(4097)]
    [InlineData(8192)]
    public async Task A_body_of_exactly_the_cap_reaches_the_shape_judge_and_one_more_byte_does_not(int cap)
    {
        var bare = DeskReplay.AnswerBody(DeskReplay.ChatFork);
        cap = Math.Max(cap, bare.Length);
        var config = DeskWebApplicationFactory.DefaultConfiguration();
        config["Desk:MaxBodyBytes"] = cap.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var factory = await ArrangeAsync(config);
        var token = Token(factory);
        var target = DeskReplay.Target("answer", Chat, null);
        byte[] Padded(int length) => [.. bare, .. Enumerable.Repeat((byte)' ', length - bare.Length)];

        var modes = new (string Name, Action<HttpContext> Configure)[]
        {
            ("content-length", _ => { }),
            ("no content-length", http => http.Request.ContentLength = null),
            ("chunked", http =>
            {
                http.Request.ContentLength = null;
                http.Request.Headers.TransferEncoding = "chunked";
            }),
        };
        foreach (var (name, configure) in modes)
        {
            factory.Door.Calls.Clear();
            var exact = await factory.SendAsync("POST", target, Padded(cap), token, configure: configure);
            Assert.True(exact.Status == 200, $"{name} exact: {exact.Status} {exact.Text}");
            Assert.Equal(cap, Assert.Single(factory.Door.Calls).Body!.Length);

            factory.Door.Calls.Clear();
            var over = await factory.SendAsync("POST", target, Padded(cap + 1), token, configure: configure);
            Assert.True(over.Status == 400 && Code(over) == "body_invalid", $"{name} over: {over.Status} {over.Text}");
            Assert.Empty(factory.Door.Calls);
        }
    }

    // (c) The wait an `unavailable` names is the configured one, in the document and in the header.
    [Fact]
    public async Task Retry_after_comes_from_configuration_in_the_document_and_the_header()
    {
        var config = DeskWebApplicationFactory.DefaultConfiguration();
        config["Desk:RetryAfterSeconds"] = "7";
        using var factory = await ArrangeAsync(config);
        factory.Door.Down = true;
        var token = Token(factory);

        foreach (var route in Routes)
        {
            var reply = await SendRouteAsync(factory, route, Chat, token);
            Assert.True(reply.Status == 503, route);
            Assert.Equal(7, (int)JsonNode.Parse(reply.Body)!["retryAfterSeconds"]!);
            Assert.Equal("7", reply.Headers.RetryAfter.ToString());
        }
    }

    // (d) The audience is compared ordinally.
    [Theory]
    [InlineData("BIFROST-DESK")]
    [InlineData("Bifrost-Desk")]
    [InlineData("bifrost-desk ")]
    [InlineData("bifrost-votes")]
    public async Task The_audience_is_case_sensitive(string audience)
    {
        using var factory = await ArrangeAsync();

        foreach (var route in Routes)
        {
            var reply = await SendRouteAsync(factory, route, Chat, Token(factory, audience));
            Assert.True(reply.Status == 401 && Code(reply) == "unauthenticated", $"{route}: {reply.Status}");
        }

        Assert.Empty(factory.Door.Calls);
    }

    // (e) No desk response is cacheable: answers, refusals, unavailable and an unrecognised target alike.
    [Fact]
    public async Task Every_desk_response_is_no_store()
    {
        using var factory = await ArrangeAsync();
        var token = Token(factory);
        var replies = new List<(string, DeskReply)>();
        foreach (var route in Routes)
        {
            replies.Add((route + " 200", await SendRouteAsync(factory, route, Chat, token)));
            replies.Add((route + " 401", await SendRouteAsync(factory, route, Chat, "")));
            replies.Add((route + " 404", await SendRouteAsync(factory, route, "ghost", token)));
        }

        factory.Door.Down = true;
        replies.Add(("503", await SendRouteAsync(factory, "list", Chat, token)));
        replies.Add(("unrecognised", await factory.SendAsync("GET", "/api/desk/x", null, token)));

        foreach (var (label, reply) in replies)
        {
            Assert.True(reply.Headers.CacheControl.ToString() == "no-store", $"{label}: '{reply.Headers.CacheControl}'");
        }

        Assert.Equal(200, replies[0].Item2.Status);
        Assert.Equal(503, replies[^2].Item2.Status);
    }

    // (f) A cancelled request is not `unavailable`: the exception goes on to the host.
    [Fact]
    public async Task A_cancelled_request_is_not_called_unavailable()
    {
        using var factory = await ArrangeAsync();
        using var cts = new CancellationTokenSource();
        factory.Door.AnswerScript = (_, _, _) =>
        {
            cts.Cancel();
            throw new IOException("the client went away");
        };

        var reply = await factory.SendAsync(
            "POST", DeskReplay.Target("answer", Chat, null), DeskReplay.AnswerBody(DeskReplay.ChatFork), Token(factory),
            configure: http => http.RequestAborted = cts.Token);

        // The host's exception handling answers; the desk does not claim the door was merely unavailable.
        Assert.NotEqual(503, reply.Status);
        Assert.DoesNotContain("unavailable", reply.Text);
    }

    // (f) A stale_fork needs the current card; a card read that finds nothing, for any code, is `unavailable`.
    [Theory]
    [InlineData("fork_unknown")]
    [InlineData("request_invalid")]
    [InlineData("made_up")]
    public async Task A_stale_fork_whose_card_read_finds_nothing_is_unavailable_never_a_stale_fork_without_a_card(string code)
    {
        using var factory = await ArrangeAsync();
        var stale = new AnswerResult(
            DeskSchemas.AnswerResult,
            new DeskAnswerDocument(
                DeskSchemas.DeskAnswer, "n1", DeskReplay.ChatFork, "Refused", null, null, "stale_fork", "oldhash", "no", null,
                "2026-11-12T19:02:00Z", "2026-11-12T19:02:05Z", false, DeskWebApplicationFactory.Alice, "Operator",
                "00000001-0000-4000-8000-000000000001"),
            null,
            null);
        factory.Door.AnswerScript = (_, _, _) => new DeskAnswerOutcome.Answered(stale);
        factory.Door.ReadScript = (_, _) => new DeskReadOutcome.NotFound(code);

        var reply = await SendRouteAsync(factory, "answer", Chat, Token(factory));

        Assert.Equal(503, reply.Status);
        Assert.Equal("unavailable", Code(reply));
        Assert.Null(JsonNode.Parse(reply.Body)!["card"]);
        Assert.Contains(factory.Door.Calls, c => c.Kind == "read");
    }

    // (h) A port that fails is `unavailable`, never a 500 and never a pass.
    [Fact]
    public async Task A_throwing_credential_port_is_unavailable_and_opens_nothing()
    {
        using var factory = await ArrangeAsync();
        factory.Credential.Throws = true;

        foreach (var route in Routes)
        {
            var reply = await SendRouteAsync(factory, route, Chat, Token(factory));
            Assert.True(reply.Status == 503 && Code(reply) == "unavailable", $"{route}: {reply.Status}");
        }

        Assert.Empty(factory.Door.Calls);
    }

    [Fact]
    public async Task A_throwing_account_lookup_is_unavailable_and_opens_nothing()
    {
        using var factory = await ArrangeAsync();
        // The request's database context is gone by the time the desk looks the account up (the database failed).
        factory.Credential.OnAuthenticate = http => http.RequestServices.GetRequiredService<Bifrost.Web.Data.BifrostDbContext>().Dispose();

        foreach (var route in Routes)
        {
            var reply = await SendRouteAsync(factory, route, Chat, Token(factory));
            Assert.True(reply.Status == 503 && Code(reply) == "unavailable", $"{route}: {reply.Status}");
        }

        Assert.Empty(factory.Door.Calls);
    }

    // The runtime guard on the decider list (the startup validator is the first layer; this is the second).
    private static string Id(int n) => new Guid(n, 0, 0, new byte[8]).ToString("D");

    private static void SetDeciders(DeskWebApplicationFactory factory, string mind, IEnumerable<string> ids)
    {
        var list = factory.Services.GetRequiredService<IOptions<DeskOptions>>().Value.Minds.Single(m => m.Id == mind).Deciders;
        list.Clear();
        list.AddRange(ids);
    }

    [Fact]
    public async Task The_door_is_called_with_64_deciders()
    {
        using var factory = await ArrangeAsync();
        var ids = Enumerable.Range(1, 64).Select(Id).ToList();
        SetDeciders(factory, Chat, ids);

        foreach (var route in Routes)
        {
            factory.Door.Calls.Clear();
            var reply = await SendRouteAsync(factory, route, Chat, Token(factory));
            Assert.True(reply.Status == 200, route);
            Assert.Equal(ids, Assert.Single(factory.Door.Calls).Context.Deciders);
        }
    }

    [Theory]
    [InlineData("65")]
    [InlineData("0")]
    [InlineData("duplicate")]
    public async Task A_decider_list_out_of_bounds_is_unavailable_without_a_door_call(string fault)
    {
        using var factory = await ArrangeAsync();
        var ids = fault switch
        {
            "65" => Enumerable.Range(1, 65).Select(Id).ToList(),
            "0" => [],
            _ => [Id(1), Id(2), Id(1)],
        };
        SetDeciders(factory, Chat, ids);

        foreach (var route in Routes)
        {
            var reply = await SendRouteAsync(factory, route, Chat, Token(factory));
            Assert.True(reply.Status == 503 && Code(reply) == "unavailable", $"{route}: {reply.Status}");
        }

        Assert.Empty(factory.Door.Calls);

        // Only that Mind's list is at fault: the other Mind still answers.
        var other = await SendRouteAsync(factory, "list", Nibu, Token(factory));
        Assert.Equal(200, other.Status);
    }
}
