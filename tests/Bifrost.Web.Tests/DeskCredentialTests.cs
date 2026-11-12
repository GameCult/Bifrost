using Bifrost.Web.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Desk;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Bifrost.Web.Tests;

// The production desk credential, on real tokens: the test host's own issuer mints them through the code flow, and the
// OpenIddict validation handler (the production IDeskCredential) judges them. Only the door is faked.
public sealed class DeskCredentialTests : IDisposable
{
    private const string DeskClient = "ratatoskr-test";
    private const string DeskRedirect = "https://ratatoskr.example.test/callback";
    private const string DeskSecret = "ratatoskr-test-secret";
    private static readonly Uri Origin = new("https://localhost");
    private static readonly string ListTarget = "/api/desk/gamecult-chat/forks";

    private readonly DeskWebApplicationFactory factory = new(realCredential: true);

    public void Dispose() => factory.Dispose();

    [Fact]
    public async Task A_valid_desk_read_token_lists_through_the_door_and_the_door_is_asked_once()
    {
        var (_, token) = await MemberWithTokenAsync("desk.read");
        factory.Door.ListScript = (_, r) => new DeskListOutcome.Listed(new ForkList("gamecult.fork.list.v0", r.Mind, "s1", 0, 0, [], null));

        var reply = await GetAsync(token);

        Assert.Equal(200, reply.Status);
        Assert.Equal("list", Assert.Single(factory.Door.Calls).Kind);
    }

    [Fact]
    public async Task No_token_is_401_and_the_door_is_never_asked()
    {
        await MemberWithTokenAsync("desk.read");

        var reply = await GetAsync(null);

        Assert.Equal(401, reply.Status);
        Assert.Equal("unauthenticated", Code(reply));
        Assert.Empty(factory.Door.Calls);
    }

    [Fact]
    public async Task A_tampered_token_is_401()
    {
        var (_, token) = await MemberWithTokenAsync("desk.read");
        var middle = token.Length / 2;
        var tampered = token[..middle] + (token[middle] == 'A' ? 'B' : 'A') + token[(middle + 1)..];

        Assert.Equal(401, (await GetAsync(tampered)).Status);
        Assert.Equal(401, (await GetAsync(token + "x")).Status);
        Assert.Equal(401, (await GetAsync("not-a-token")).Status);
        Assert.Empty(factory.Door.Calls);
    }

    [Fact]
    public async Task An_expired_token_is_401()
    {
        var (_, token) = await MemberWithTokenAsync("desk.read");
        Assert.Equal(200, (await GetAsync(token)).Status);

        factory.Clock.Advance(TimeSpan.FromHours(3));

        Assert.Equal(401, (await GetAsync(token)).Status);
    }

    [Fact]
    public async Task A_token_for_another_audience_is_401()
    {
        var (_, token) = await MemberWithTokenAsync($"desk.read {DeskWebApplicationFactory.OtherAudienceScope}");

        var reply = await GetAsync(token);

        Assert.Equal(401, reply.Status);
        Assert.Empty(factory.Door.Calls);
    }

    [Fact]
    public async Task A_desk_read_token_cannot_answer_and_the_door_is_never_called()
    {
        var (_, token) = await MemberWithTokenAsync("desk.read");

        var reply = await factory.SendAsync(
            "POST", DeskReplay.Target("answer", "gamecult-chat", null), DeskReplay.AnswerBody(DeskReplay.ChatFork),
            configure: Bearer(token));

        Assert.Equal(403, reply.Status);
        Assert.Equal("insufficient_scope", Code(reply));
        Assert.Empty(factory.Door.Calls);
    }

    [Fact]
    public async Task A_bearer_in_the_query_string_or_a_form_body_authenticates_nothing()
    {
        var (_, token) = await MemberWithTokenAsync("desk.answer");
        DeskReplay.HoldVectorForks(factory.Door);

        var query = await factory.SendAsync(
            "POST", DeskReplay.Target("answer", "gamecult-chat", null) + "?access_token=" + token, DeskReplay.AnswerBody(DeskReplay.ChatFork));
        var list = await factory.SendAsync("GET", DeskReplay.Target("list", "gamecult-chat", null) + "?access_token=" + token, null);

        Assert.Equal(401, query.Status);
        Assert.Equal(401, list.Status);
        Assert.Empty(factory.Door.Calls);
    }

    [Fact]
    public async Task A_desk_answer_token_reaches_the_answer_door_but_cannot_list()
    {
        var (_, token) = await MemberWithTokenAsync("desk.answer");
        DeskReplay.HoldVectorForks(factory.Door);

        var list = await GetAsync(token);
        var answer = await factory.SendAsync(
            "POST", DeskReplay.Target("answer", "gamecult-chat", null), DeskReplay.AnswerBody(DeskReplay.ChatFork), configure: Bearer(token));

        Assert.Equal(403, list.Status);
        Assert.Equal(200, answer.Status);
        Assert.Equal("answer", Assert.Single(factory.Door.Calls).Kind);
    }

    [Fact]
    public async Task A_desk_token_issues_for_a_member_with_no_training_notice_receipt_and_a_chat_token_does_not()
    {
        var (client, identity) = await SignedInAsync();
        await RemoveReceiptsAsync(identity, withdrawOnly: false);

        // Chat: sent to the consent page, no code.
        var chat = NewRequest(OidcTestKit.ClientId, OidcTestKit.RedirectUri, "openid profile");
        var chatFinal = await DriveAsync(client, chat);
        Assert.Equal(HttpStatusCode.Redirect, chatFinal.StatusCode);
        Assert.DoesNotContain("code=", chatFinal.Headers.Location!.OriginalString);

        // Desk: a code, a token, and the desk answers it.
        var request = NewRequest(DeskClient, DeskRedirect, "desk.read");
        var code = await CodeAsync(client, request);
        var token = await ExchangeAsync(client, request, code, DeskClient, DeskSecret);
        factory.Door.ListScript = (_, r) => new DeskListOutcome.Listed(new ForkList("gamecult.fork.list.v0", r.Mind, "s1", 0, 0, [], null));
        Assert.Equal(200, (await GetAsync(token)).Status);

        // A desk code survives the receipt going between authorize and the exchange; a suspended account still does not.
        var later = NewRequest(DeskClient, DeskRedirect, "desk.read");
        var laterCode = await CodeAsync(client, later);
        await RemoveReceiptsAsync(identity, withdrawOnly: true);
        Assert.False(string.IsNullOrEmpty(await ExchangeAsync(client, later, laterCode, DeskClient, DeskSecret)));
    }

    [Fact]
    public async Task A_desk_scope_mixed_with_a_chat_scope_does_not_skip_the_training_notice_receipt()
    {
        var (client, identity) = await SignedInAsync();
        await RemoveReceiptsAsync(identity, withdrawOnly: false);

        var mixed = NewRequest(DeskClient, DeskRedirect, "openid desk.read");
        var final = await DriveAsync(client, mixed);
        Assert.Equal(HttpStatusCode.Redirect, final.StatusCode);
        Assert.DoesNotContain("code=", final.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task The_chat_client_can_neither_ask_for_a_desk_scope_nor_use_its_own_token_on_the_desk()
    {
        var (client, _) = await SignedInAsync();

        // Asking for the scope: the issuer refuses, so no code and no token exist for the chat client to carry.
        var request = NewRequest(OidcTestKit.ClientId, OidcTestKit.RedirectUri, "openid profile desk.read");
        var refused = await DriveAsync(client, request);
        Assert.True(refused.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.BadRequest, $"got {(int)refused.StatusCode}");
        var back = refused.Headers.Location?.ToString() ?? await refused.Content.ReadAsStringAsync();
        Assert.Contains("not allowed to use the specified scope", back);
        Assert.DoesNotContain("code=", back);

        // The token chat does hold: validly signed by the same issuer, and no use at the desk.
        var chat = NewRequest(OidcTestKit.ClientId, OidcTestKit.RedirectUri, "openid profile");
        var chatToken = await ExchangeAsync(client, chat, await CodeAsync(client, chat), OidcTestKit.ClientId, factory.OidcSecret);
        Assert.Equal(401, (await GetAsync(chatToken)).Status);
        var post = await factory.SendAsync(
            "POST", DeskReplay.Target("answer", "gamecult-chat", null), DeskReplay.AnswerBody(DeskReplay.ChatFork), configure: Bearer(chatToken));
        Assert.Equal(401, post.Status);
        Assert.Empty(factory.Door.Calls);
    }

    [Fact]
    public async Task The_session_cookie_alone_authenticates_nothing_on_the_desk()
    {
        // A signed-in Active member's real session cookie, sent by hand to the desk: it is no credential there.
        using var bare = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false, BaseAddress = Origin });
        var identity = $"member-{Guid.NewGuid():N}";
        using var registered = await RegistrationKit.RegisterAsync(bare, identity);
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        await factory.SetMembershipAsync(identity, MembershipStatus.Active);
        var cookie = string.Join("; ", registered.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        Assert.Contains("bifrost", cookie, StringComparison.OrdinalIgnoreCase);

        var list = await factory.SendAsync("GET", ListTarget, configure: http => http.Request.Headers.Cookie = cookie);
        var answer = await factory.SendAsync(
            "POST", DeskReplay.Target("answer", "gamecult-chat", null), DeskReplay.AnswerBody(DeskReplay.ChatFork),
            configure: http => http.Request.Headers.Cookie = cookie);

        Assert.Equal(401, list.Status);
        Assert.Equal(401, answer.Status);
        Assert.Empty(factory.Door.Calls);
    }

    [Fact]
    public async Task A_bearer_token_does_not_authenticate_a_cookie_page()
    {
        var (_, token) = await MemberWithTokenAsync("desk.read desk.answer");
        using var bare = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false, BaseAddress = Origin });

        foreach (var path in new[] { "/App", "/App/Members", "/api/sealed-motions" })
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, path);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await bare.SendAsync(message);

            Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
                $"{path} answered {(int)response.StatusCode} to a bearer");
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task A_valid_token_whose_account_turns_non_active_is_refused_on_its_next_request_without_reissue()
    {
        var (identity, token) = await MemberWithTokenAsync("desk.read");
        Assert.Equal(200, (await GetAsync(token)).Status);

        await factory.SetMembershipAsync(identity, MembershipStatus.Suspended);

        var reply = await GetAsync(token);
        Assert.Equal(403, reply.Status);
        Assert.Equal("forbidden", Code(reply));
        Assert.Single(factory.Door.Calls); // only the first request got to the door

        await factory.SetMembershipAsync(identity, MembershipStatus.Active);
        Assert.Equal(200, (await GetAsync(token)).Status);
    }

    [Fact]
    public async Task A_member_who_is_not_a_decider_reads_no_notes_through_a_real_token_and_ids_pass_as_the_door_sent_them()
    {
        var (_, token) = await MemberWithTokenAsync("desk.read");
        const string secretAnswerer = "0badc0de-0000-4000-8000-00000000000d"; // not on any decider list, so no card carries it
        var entry = DeskReplay.Entry(DeskReplay.Card(DeskReplay.ChatFork));
        var answer = new DeskAnswerDocument(
            DeskSchemas.DeskAnswer, "n1", DeskReplay.ChatFork, "Counted", null, null, null, entry.Card.OptionsHash, "a", "private words",
            "2026-11-12T19:00:00Z", "2026-11-12T19:00:01Z", false, secretAnswerer, "Steward", "r-1");
        entry = entry with { Record = entry.Record with { Answers = [answer] } };
        factory.Door.ListScript = (_, r) => new DeskListOutcome.Listed(new ForkList("gamecult.fork.list.v0", r.Mind, "s1", 1, 0, [entry], null));

        var reply = await GetAsync(token);

        Assert.Equal(200, reply.Status);
        Assert.Contains(secretAnswerer, reply.Text); // Bifrost masks no id; Huginn's door withholds them from non-deciders
        Assert.DoesNotContain("private words", reply.Text);
    }

    // ---- helpers ----

    private static Action<Microsoft.AspNetCore.Http.HttpContext> Bearer(string token) =>
        http => http.Request.Headers.Authorization = "Bearer " + token;

    private Task<DeskReply> GetAsync(string? token) =>
        factory.SendAsync("GET", ListTarget, configure: token is null ? null : Bearer(token));

    private static string? Code(DeskReply reply) => (string?)JsonNode.Parse(reply.Body)!["code"];

    private HttpClient NewClient() => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
        BaseAddress = Origin,
    });

    // A registered, Active, agreed member holding the session cookie.
    private async Task<(HttpClient Client, string Identity)> SignedInAsync()
    {
        var client = NewClient();
        var identity = $"member-{Guid.NewGuid():N}";
        using var registered = await RegistrationKit.RegisterAsync(client, identity);
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        await factory.SetMembershipAsync(identity, MembershipStatus.Active);
        await SeedDeskClientAsync();
        return (client, identity);
    }

    // Leaves the account with no live training-notice receipt: none at all, or each one withdrawn.
    private async Task RemoveReceiptsAsync(string identity, bool withdrawOnly)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var id = db.UserAccounts.Single(x => x.NormalizedBifrostIdentity == identity).Id;
        var receipts = db.ConsentReceipts.Where(x => x.UserAccountId == id).ToList();
        if (withdrawOnly)
        {
            receipts.ForEach(r => r.WithdrawnAtUtc = DateTimeOffset.UtcNow);
        }
        else
        {
            db.ConsentReceipts.RemoveRange(receipts);
        }

        await db.SaveChangesAsync();
    }

    private async Task<(string Identity, string Token)> MemberWithTokenAsync(string scope)
    {
        var (client, identity) = await SignedInAsync();
        var request = NewRequest(DeskClient, DeskRedirect, scope);
        var token = await ExchangeAsync(client, request, await CodeAsync(client, request), DeskClient, DeskSecret);
        return (identity, token);
    }

    // The test-only desk client: the production seeder creates none (Ratatoskr's redirect and host are a separate decision).
    private async Task SeedDeskClientAsync()
    {
        using var scope = factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        if (await manager.FindByClientIdAsync(DeskClient) is not null)
        {
            return;
        }

        await manager.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = DeskClient,
            ClientSecret = DeskSecret,
            ClientType = ClientTypes.Confidential,
            RedirectUris = { new Uri(DeskRedirect) },
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.ResponseTypes.Code,
                Permissions.Prefixes.Scope + Scopes.OpenId,
                Permissions.Prefixes.Scope + DeskEndpoints.ReadScope,
                Permissions.Prefixes.Scope + DeskEndpoints.AnswerScope,
                Permissions.Prefixes.Scope + DeskWebApplicationFactory.OtherAudienceScope,
            },
        });
    }

    private sealed record AuthRequest(string ClientId, string RedirectUri, string Verifier, string State, string Path);

    private static AuthRequest NewRequest(string clientId, string redirectUri, string scope)
    {
        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(48));
        var state = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(24));
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var path = "/connect/authorize?response_type=code" +
            $"&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString(scope)}&state={state}&code_challenge={challenge}&code_challenge_method=S256";
        return new AuthRequest(clientId, redirectUri, verifier, state, path);
    }

    // Starts an authorize request and follows the provider's own redirects until the first response that is not one of them.
    private static async Task<HttpResponseMessage> DriveAsync(HttpClient client, AuthRequest request)
    {
        var response = await client.GetAsync(request.Path);
        for (var hops = 0; hops < 4 && IsAuthorizeRedirect(response); hops++)
        {
            var next = response.Headers.Location!;
            response.Dispose();
            response = await client.GetAsync(next.IsAbsoluteUri ? next.PathAndQuery : next.OriginalString);
        }

        return response;
    }

    private static bool IsAuthorizeRedirect(HttpResponseMessage response) =>
        response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found &&
        response.Headers.Location is { } location &&
        (location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString).StartsWith("/connect/authorize", StringComparison.Ordinal);

    private static async Task<string> CodeAsync(HttpClient client, AuthRequest request)
    {
        var final = await DriveAsync(client, request);
        Assert.True(final.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found, $"expected a redirect to the client, got {(int)final.StatusCode}");
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(final.Headers.Location!.Query);
        Assert.True(query.ContainsKey("code"), final.Headers.Location.ToString());
        return query["code"].ToString();
    }

    private async Task<string> ExchangeAsync(HttpClient client, AuthRequest request, string code, string clientId, string secret)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = request.RedirectUri,
                ["code_verifier"] = request.Verifier,
            }),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{clientId}:{secret}")));
        using var bare = NewClient();
        using var response = await bare.SendAsync(message);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("access_token").GetString()!;
    }
}
