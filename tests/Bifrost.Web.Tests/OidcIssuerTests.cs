using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Shared;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Bifrost.Web.Tests;

public sealed class OidcIssuerTests : IClassFixture<OidcWebApplicationFactory>
{
    private static readonly Uri Origin = new("https://localhost");
    private OidcWebApplicationFactory _factory;

    public OidcIssuerTests(OidcWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Discovery_advertises_ES256_and_the_code_flow_only()
    {
        using var client = NewClient();
        var discovery = await client.GetFromJsonAsync<JsonElement>("/.well-known/openid-configuration");

        Assert.Equal(Origin.AbsoluteUri, discovery.GetProperty("issuer").GetString());
        Assert.Equal("/.well-known/jwks", new Uri(discovery.GetProperty("jwks_uri").GetString()!).AbsolutePath);
        Assert.Equal(["ES256"], Strings(discovery, "id_token_signing_alg_values_supported"));
        Assert.Equal(["code"], Strings(discovery, "response_types_supported"));
        Assert.Equal(["authorization_code"], Strings(discovery, "grant_types_supported"));
        Assert.Contains("S256", Strings(discovery, "code_challenge_methods_supported"));
        Assert.Equal(["client_secret_basic"], Strings(discovery, "token_endpoint_auth_methods_supported"));
        Assert.Equal(["openid", "profile"], Strings(discovery, "scopes_supported").Order());
    }

    [Fact]
    public async Task Jwks_holds_exactly_the_configured_P256_key()
    {
        using var client = NewClient();
        var jwks = await GetJwksAsync(client);

        var key = Assert.Single(jwks.GetProperty("keys").EnumerateArray());
        Assert.Equal("EC", key.GetProperty("kty").GetString());
        Assert.Equal("P-256", key.GetProperty("crv").GetString());
        Assert.Equal("ES256", key.GetProperty("alg").GetString());

        using var ec = ECDsa.Create();
        ec.ImportFromPem(_factory.Kit.SigningPublicKeyPem);
        var expected = ec.ExportParameters(false);
        Assert.Equal(Base64UrlEncoder.Encode(expected.Q.X), key.GetProperty("x").GetString());
        Assert.Equal(Base64UrlEncoder.Encode(expected.Q.Y), key.GetProperty("y").GetString());
        Assert.False(key.TryGetProperty("d", out _));
    }

    [Fact]
    public async Task An_active_member_completes_code_and_pkce_and_gets_the_right_claims()
    {
        var (client, identity) = await SignedInAsync(MembershipStatus.Active, "Ada Lovelace");
        var accountId = _factory.AccountId(identity);
        await SetLinksAsync(identity, "heimdall-canary-7", "github-canary-7");

        var tokens = await CompleteFlowAsync(client);

        var verified = await ValidateIdTokenAsync(tokens.IdToken, tokens.Request.Nonce);
        Assert.Equal(accountId.ToString("D"), verified.Claim("sub"));
        Assert.Equal(identity, verified.Claim("preferred_username"));
        Assert.Equal("Ada Lovelace", verified.Claim("name"));
        Assert.Equal(["ES256"], [verified.Alg]);

        // Nothing about the authenticator links, e-mail or membership leaves Bifrost.
        Assert.DoesNotContain("heimdall-canary-7", verified.PayloadJson);
        Assert.DoesNotContain("github-canary-7", verified.PayloadJson);
        Assert.DoesNotContain("email", verified.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("urn:bifrost", verified.PayloadJson);

        using var userinfo = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        userinfo.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        using var userinfoResponse = await NewClient().SendAsync(userinfo);
        Assert.Equal(HttpStatusCode.OK, userinfoResponse.StatusCode);
        var body = await userinfoResponse.Content.ReadAsStringAsync();
        var info = JsonDocument.Parse(body).RootElement;
        Assert.Equal(accountId.ToString("D"), info.GetProperty("sub").GetString());
        Assert.Equal(identity, info.GetProperty("preferred_username").GetString());
        Assert.Equal("Ada Lovelace", info.GetProperty("name").GetString());
        Assert.DoesNotContain("canary", body);
    }

    [Fact]
    public async Task Sub_and_preferred_username_are_stable_across_display_name_changes()
    {
        var (client, identity) = await SignedInAsync(MembershipStatus.Active, "First Name");

        var first = await ValidateIdTokenAsync((await CompleteFlowAsync(client)).IdToken, null);
        await _factory.SetDisplayNameAsync(identity, "Second Name");
        var second = await ValidateIdTokenAsync((await CompleteFlowAsync(client)).IdToken, null);

        Assert.Equal(first.Claim("sub"), second.Claim("sub"));
        Assert.Equal(first.Claim("preferred_username"), second.Claim("preferred_username"));
        Assert.Equal("First Name", first.Claim("name"));
        Assert.Equal("Second Name", second.Claim("name"));
    }

    [Theory]
    [InlineData(MembershipStatus.Authenticated)]
    [InlineData(MembershipStatus.PendingApproval)]
    [InlineData(MembershipStatus.Suspended)]
    public async Task A_member_who_is_not_active_is_refused_with_access_denied_and_no_code(MembershipStatus status)
    {
        var (client, _) = await SignedInAsync(status, "Not Active");
        var request = NewRequest();

        var final = await DriveAsync(client, request);

        var redirect = AssertRedirectToClient(final, request.State);
        Assert.Equal("access_denied", redirect["error"]);
        Assert.Equal(Bifrost.Web.Features.Oidc.OidcEndpoints.NotActiveDescription, redirect["error_description"]);
        Assert.False(redirect.ContainsKey("code"));
    }

    [Fact]
    public async Task An_active_account_with_no_bifrost_identity_is_refused_never_given_an_empty_username()
    {
        using var factory = new OidcNoIdentityFactory();
        using var client = factory.CreateClient(ClientOptions());
        await RegisterAsync(client, $"noid-{Guid.NewGuid():N}");
        var request = NewRequest();

        var final = await DriveAsync(client, request);

        var redirect = AssertRedirectToClient(final, request.State);
        Assert.Equal("access_denied", redirect["error"]);
        Assert.Equal(Bifrost.Web.Features.Oidc.OidcEndpoints.NoIdentityDescription, redirect["error_description"]);
        Assert.False(redirect.ContainsKey("code"));
    }

    [Theory]
    [InlineData("meta")] // an operator's or a Mind's Matrix localpart
    [InlineData("eureka")]
    [InlineData("admin")]
    [InlineData("system")] // a system name
    [InlineData("Heimdall")] // the row's casing does not matter
    public async Task An_active_member_whose_identity_is_a_reserved_localpart_gets_no_code(string reserved)
    {
        using var factory = new OidcFixedIdentityFactory(reserved);
        using var client = factory.CreateClient(ClientOptions());
        await RegisterAsync(client, $"cookie-{Guid.NewGuid():N}"); // any session: the actor is the fixed account
        var request = NewRequest();

        var final = await DriveAsync(client, request);

        var redirect = AssertRedirectToClient(final, request.State);
        Assert.Equal("access_denied", redirect["error"]);
        Assert.Equal(Bifrost.Web.Features.Oidc.OidcEndpoints.ReservedIdentityDescription, redirect["error_description"]);
        Assert.False(redirect.ContainsKey("code"));
    }

    [Theory]
    [InlineData("meta2")] // look-alikes are ordinary names
    [InlineData("admins")]
    [InlineData("kiko-fan")]
    public async Task A_name_that_only_resembles_a_reserved_localpart_is_given_a_code(string ordinary)
    {
        using var factory = new OidcFixedIdentityFactory(ordinary);
        using var client = factory.CreateClient(ClientOptions());
        await RegisterAsync(client, $"cookie-{Guid.NewGuid():N}");
        var request = NewRequest();

        var final = await DriveAsync(client, request);

        var redirect = AssertRedirectToClient(final, request.State);
        Assert.True(redirect.ContainsKey("code"));
        Assert.False(redirect.ContainsKey("error"));
    }

    private static IReadOnlyDictionary<string, string?> Settings(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => $"Bifrost:Oidc:{p.Key}", p => (string?)p.Value);

    [Fact]
    public async Task A_flood_from_one_address_is_refused_with_429_and_a_retry_after()
    {
        using var factory = new OidcSettingsFactory(Settings(("Limits:PerAddressPermits", "5")));
        using var client = factory.CreateClient(ClientOptions());

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? last = null;
        for (var i = 0; i < 8; i++)
        {
            last?.Dispose();
            last = await client.GetAsync(NewRequest().Path);
            statuses.Add(last.StatusCode);
        }

        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses.Take(5));
        Assert.All(statuses.Skip(5), status => Assert.Equal(HttpStatusCode.TooManyRequests, status));
        Assert.True(int.Parse(last!.Headers.GetValues("Retry-After").Single()) >= 1);
        using var tokenFlood = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.TooManyRequests, tokenFlood.StatusCode); // one budget over all three endpoints
        using var userinfoFlood = await client.GetAsync("/connect/userinfo");
        Assert.Equal(HttpStatusCode.TooManyRequests, userinfoFlood.StatusCode);
        last.Dispose();
    }

    [Fact]
    public async Task A_flood_naming_the_real_client_does_not_spend_anyone_elses_permits_and_a_trailing_slash_is_no_way_around()
    {
        using var factory = new OidcSettingsFactory(Settings(("Limits:PerAddressPermits", "4")));
        using var client = factory.CreateClient(ClientOptions());

        var statuses = new List<HttpStatusCode>();
        foreach (var path in new[] { NewRequest().Path, NewRequest().Path, "/connect/authorize/?client_id=x", "/connect/token/", "/connect/userinfo/" })
        {
            using var response = await client.GetAsync(path);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[4]); // the slashed forms spend the same budget
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses.Take(4));
    }

    [Theory]
    [InlineData("/connect/authorize")]
    [InlineData("/connect/authorize/")]
    [InlineData("/connect/token/")]
    [InlineData("/connect/userinfo/")]
    [InlineData("/CONNECT/Userinfo")]
    public async Task The_brake_holds_for_every_spelling_of_an_endpoint(string path)
    {
        var marker = Path.Combine(Path.GetTempPath(), $"oidc-brake-{Guid.NewGuid():N}");
        File.WriteAllText(marker, "braked");
        try
        {
            using var factory = new OidcSettingsFactory(Settings(("BrakePath", marker)));
            using var client = factory.CreateClient(ClientOptions());
            using var response = await client.GetAsync(path == "/connect/authorize" || path == "/connect/authorize/" ? path + "?client_id=x" : path);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task A_guard_that_cannot_decide_answers_503_and_never_passes_the_request_on()
    {
        var reached = false;
        var options = new Bifrost.Web.Configuration.OidcIssuerOptions();
        var middleware = new Bifrost.Web.Features.Oidc.OidcGuardMiddleware(
            _ => { reached = true; return Task.CompletedTask; }, options,
            new Bifrost.Web.Features.Oidc.OidcRateLimit(options, TimeProvider.System),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Bifrost.Web.Features.Oidc.OidcGuardMiddleware>.Instance);
        var http = new DefaultHttpContext();
        http.Request.Path = "/connect/authorize";
        http.Request.QueryString = new QueryString("?client_id=x");
        http.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(http, System.Reflection.DispatchProxy.Create<OpenIddict.Abstractions.IOpenIddictTokenManager, ThrowingProxy>());

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, http.Response.StatusCode);
    }

    /// <summary>Every call fails: the row count the guard asks of the token table cannot be answered.</summary>
    public class ThrowingProxy : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) => throw new InvalidOperationException("the token table is down");
    }

    [Fact]
    public async Task A_resumed_authorize_request_is_not_refused_by_the_row_cap_but_a_fresh_one_is()
    {
        using var factory = new OidcSettingsFactory(Settings(("Limits:MaxTokenRows", "3")));
        _factory = factory;
        var (client, _) = await SignedInAsync(MembershipStatus.Active, "Resumer");
        using var first = await client.GetAsync(NewRequest().Path); // parks one row, answers with a redirect carrying the request_uri
        var resume = first.Headers.Location!.ToString();
        Assert.Contains("request_uri", resume);

        using var anonymous = factory.CreateClient(ClientOptions());
        for (var i = 0; i < 2; i++)
        {
            using var filler = await anonymous.GetAsync(NewRequest().Path);
        }

        using var fresh = await anonymous.GetAsync(NewRequest().Path);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, fresh.StatusCode);
        using var resumed = await client.GetAsync(resume);
        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, resumed.StatusCode);
    }

    [Fact]
    public async Task A_mas_flow_fits_the_default_budgets_and_the_window_reopens()
    {
        using var factory = new OidcSettingsFactory(Settings(("Limits:PerAddressPermits", "30")));
        _factory = factory;
        var (client, identity) = await SignedInAsync(MembershipStatus.Active, "Within Budget");

        var verified = await ValidateIdTokenAsync((await CompleteFlowAsync(client)).IdToken, null);
        Assert.Equal(identity, verified.Claim("preferred_username"));

        for (var i = 0; i < 40; i++)
        {
            using var burst = await client.GetAsync("/connect/userinfo");
        }

        using var refused = await client.GetAsync("/connect/userinfo");
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        using var reopened = await client.GetAsync("/connect/userinfo");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, reopened.StatusCode);
    }

    [Fact]
    public async Task The_brake_marker_stops_the_issuer_and_releasing_it_needs_no_restart()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"bifrost-oidc-brake-{Guid.NewGuid():N}");
        using var factory = new OidcSettingsFactory(Settings(("BrakePath", marker)));
        _factory = factory;
        var (client, _) = await SignedInAsync(MembershipStatus.Active, "Braked");
        try
        {
            Assert.True((await CompleteFlowAsync(client)).IdToken.Length > 0, "released: the flow passes");

            await File.WriteAllTextAsync(marker, "held");
            foreach (var path in new[] { NewRequest().Path, "/connect/userinfo" })
            {
                using var held = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, held.StatusCode);
                Assert.True(held.Headers.Contains("Retry-After"));
            }

            using var token = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>()));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, token.StatusCode);
            using var discovery = await client.GetAsync("/.well-known/openid-configuration");
            Assert.Equal(HttpStatusCode.OK, discovery.StatusCode); // the brake stops issuing, not the published keys

            File.Delete(marker);
            Assert.True((await CompleteFlowAsync(client)).IdToken.Length > 0, "released again, same process");
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task Anonymous_authorize_requests_cannot_park_more_than_the_row_cap_and_the_prune_gives_the_room_back()
    {
        using var factory = new OidcSettingsFactory(Settings(("Limits:MaxTokenRows", "3")));
        _factory = factory;
        using var client = factory.CreateClient(ClientOptions());

        var parked = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            using var response = await client.GetAsync(NewRequest().Path);
            parked.Add(response.StatusCode);
        }

        Assert.Equal(3, await factory.TokenRowsAsync());
        Assert.All(parked.Skip(3), status => Assert.Equal(HttpStatusCode.ServiceUnavailable, status));
        Assert.All(parked.Take(3), status => Assert.NotEqual(HttpStatusCode.ServiceUnavailable, status));

        factory.Clock.Advance(TimeSpan.FromMinutes(11)); // past the ten-minute parked lifetime
        await factory.PruneAsync();
        Assert.Equal(0, await factory.TokenRowsAsync());
        using var again = await client.GetAsync(NewRequest().Path);
        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, again.StatusCode);
    }

    [Fact]
    public async Task The_prune_removes_expired_rows_and_leaves_a_live_token_alone()
    {
        using var factory = new OidcSettingsFactory(Settings());
        _factory = factory;
        var (client, _) = await SignedInAsync(MembershipStatus.Active, "Pruned");
        var tokens = await CompleteFlowAsync(client); // a parked request, a code and an access token existed along the way
        using (var anonymous = factory.CreateClient(ClientOptions()))
        {
            using var parked = await anonymous.GetAsync(NewRequest().Path); // a parked request that will expire
        }

        var before = await factory.TokenRowsAsync();
        await factory.PruneAsync();
        Assert.Equal(before, await factory.TokenRowsAsync()); // nothing has expired yet: nothing goes

        factory.Clock.Advance(TimeSpan.FromMinutes(11)); // parked requests and codes are dead, the access token (an hour) is not
        await factory.PruneAsync();
        var after = await factory.TokenRowsAsync();
        Assert.True(after < before, "expired rows went");
        Assert.Contains("urn:ietf:params:oauth:token-type:access_token", await factory.TokenTypesAsync()); // the live one, not merely some row

        using var userinfo = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        userinfo.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        using var response = await factory.CreateClient(ClientOptions()).SendAsync(userinfo);
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_prune_takes_a_revoked_authorization_with_its_tokens_and_touches_no_live_authorization()
    {
        using var factory = new OidcSettingsFactory(Settings());
        _factory = factory;
        var (revoked, _) = await SignedInAsync(MembershipStatus.Active, "Revoked");
        await CompleteFlowAsync(revoked);
        var (live, _) = await SignedInAsync(MembershipStatus.Active, "Live");
        await CompleteFlowAsync(live);
        Assert.Equal(2, await factory.AuthorizationRowsAsync());
        Assert.Equal(6, await factory.TokensOfAuthorizationsAsync()); // a spent code, an access token and an id token each
        await factory.RevokeFirstAuthorizationAsync();

        factory.Clock.Advance(TimeSpan.FromMinutes(6)); // past the grace for a young row; the access tokens (an hour) are still alive
        await factory.PruneAsync();

        Assert.Equal(1, await factory.AuthorizationRowsAsync());
        Assert.Equal(2, await factory.TokenRowsAsync()); // no orphan stays either: the revoked one's tokens went with it; the live one's access and id tokens stayed, spent codes go
    }

    [Theory]
    [InlineData("unknown-client", OidcTestKit.RedirectUri, true)]
    [InlineData(OidcTestKit.ClientId, "https://evil.example.test/callback", true)]
    [InlineData(OidcTestKit.ClientId, OidcTestKit.RedirectUri, false)]
    public async Task Unknown_client_wrong_redirect_uri_and_missing_pkce_are_refused_before_any_sign_in_redirect(
        string clientId, string redirectUri, bool pkce)
    {
        using var client = NewClient();
        var request = NewRequest(clientId, redirectUri, pkce);

        using var response = await client.GetAsync(request.Path);

        var location = response.Headers.Location?.OriginalString ?? string.Empty;
        Assert.DoesNotContain("/auth/sign-in", location);
        Assert.DoesNotContain("request_uri=", location);
        Assert.DoesNotContain("code=", location);
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_mas_sized_authorize_request_survives_sign_in_and_its_handle_is_single_use()
    {
        var request = NewRequest(extra: "&ui_locales=en&prompt=login&claims_locales=en&max_age=3600");
        Assert.True(request.Path.Length > LocalReturnPath.MaxLength, "the request must be longer than a sign-in return path");

        using var client = NewClient();
        var signIn = await DriveAsync(client, request);
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        Assert.Equal("/auth/sign-in", signIn.Headers.Location!.IsAbsoluteUri
            ? signIn.Headers.Location.AbsolutePath
            : signIn.Headers.Location.OriginalString.Split('?')[0]);
        var resume = ReturnUrl(signIn);
        Assert.True(resume.Length <= LocalReturnPath.MaxLength, "the resume path must fit the sign-in return path");
        Assert.Equal(resume, LocalReturnPath.Parse(resume).Value);

        var identity = await RegisterAsync(client, $"resume-{Guid.NewGuid():N}");
        await _factory.SetMembershipAsync(identity, MembershipStatus.Active);

        using var resumed = await client.GetAsync(resume);
        var redirect = AssertRedirectToClient(resumed, request.State);
        Assert.True(redirect.ContainsKey("code"), "the resumed request issues a code");

        var tokens = await ExchangeAsync(client, request, redirect["code"]!);
        var verified = await ValidateIdTokenAsync(tokens.IdToken, request.Nonce);
        Assert.Equal(request.Nonce, verified.Claim("nonce"));

        // The handle was spent by the first resume.
        using var replay = await client.GetAsync(resume);
        var replayRedirect = replay.Headers.Location?.OriginalString ?? string.Empty;
        Assert.DoesNotContain("code=", replayRedirect);
        Assert.NotEqual(HttpStatusCode.OK, replay.StatusCode);
    }

    [Fact]
    public async Task A_parked_authorize_request_expires()
    {
        // Own host: moving the clock must not skew the id_token checks of the other tests.
        using var own = new OidcWebApplicationFactory();
        await new OidcIssuerTests(own).ParkedRequestIsRefusedAfterItsLifetimeAsync();
    }

    private async Task ParkedRequestIsRefusedAfterItsLifetimeAsync()
    {
        // Fixed probe times, not the constant: a lifetime of a day, or of a minute, must show.
        var early = NewRequest();
        var late = NewRequest();
        using var client = NewClient();
        var earlyResume = ReturnUrl(await DriveAsync(client, early));
        var lateResume = ReturnUrl(await DriveAsync(client, late));
        var identity = await RegisterAsync(client, $"late-{Guid.NewGuid():N}");
        await _factory.SetMembershipAsync(identity, MembershipStatus.Active);

        _factory.Clock.Advance(TimeSpan.FromMinutes(5));
        using var withinLifetime = await client.GetAsync(earlyResume);
        Assert.True(AssertRedirectToClient(withinLifetime, early.State).ContainsKey("code"), "five minutes is within the lifetime");

        _factory.Clock.Advance(TimeSpan.FromMinutes(25));
        using var expired = await client.GetAsync(lateResume);
        var location = expired.Headers.Location?.OriginalString ?? string.Empty;
        Assert.DoesNotContain("code=", location);
        Assert.NotEqual(HttpStatusCode.OK, expired.StatusCode);
    }

    [Fact]
    public async Task The_token_endpoint_accepts_client_secret_basic_only()
    {
        var (client, _) = await SignedInAsync(MembershipStatus.Active, "Basic Only");
        var request = NewRequest();
        var code = await CodeAsync(client, request);

        var wrongSecret = await TokenAsync(client, request, code, secret: "wrong-secret");
        Assert.NotEqual(HttpStatusCode.OK, wrongSecret.StatusCode);

        var asPost = await TokenAsync(client, request, code, secretInBody: true);
        Assert.NotEqual(HttpStatusCode.OK, asPost.StatusCode);
    }

    [Fact]
    public async Task Identity_is_minted_once()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<Bifrost.Web.Features.Membership.BifrostIdentityService>();
        var account = new UserAccount { BifrostIdentity = "Original", NormalizedBifrostIdentity = "original" };

        await service.EnsureIdentityAsync(account, "someone-else", CancellationToken.None);

        Assert.Equal("original", account.NormalizedBifrostIdentity);
        Assert.Equal("Original", account.BifrostIdentity);
    }

    [Fact]
    public async Task The_openid_scope_alone_releases_the_subject_and_nothing_else()
    {
        var (client, identity) = await SignedInAsync(MembershipStatus.Active, "Scoped Down");
        var request = NewRequest(scope: "openid");
        var code = await CodeAsync(client, request);

        var tokens = await ExchangeAsync(client, request, code);

        var verified = await ValidateIdTokenAsync(tokens.IdToken, request.Nonce);
        Assert.Equal(_factory.AccountId(identity).ToString("D"), verified.Claim("sub"));
        Assert.Null(verified.Claim("preferred_username"));
        Assert.Null(verified.Claim("name"));

        using var userinfo = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        userinfo.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        using var response = await NewClient().SendAsync(userinfo);
        var info = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateObject().Select(x => x.Name).ToArray();
        Assert.Equal(["sub"], info);
    }

    [Fact]
    public async Task A_blank_display_name_falls_back_to_the_bifrost_identity()
    {
        var (client, identity) = await SignedInAsync(MembershipStatus.Active, "   ");

        var verified = await ValidateIdTokenAsync((await CompleteFlowAsync(client)).IdToken, null);

        Assert.Equal(identity, verified.Claim("name"));
    }

    [Fact]
    public async Task Userinfo_without_a_valid_token_is_refused()
    {
        using var client = NewClient();
        using var none = await client.GetAsync("/connect/userinfo");
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);

        using var forged = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        forged.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");
        using var forgedResponse = await client.SendAsync(forged);
        Assert.Equal(HttpStatusCode.Unauthorized, forgedResponse.StatusCode);
    }

    [Fact]
    public async Task Startup_makes_the_one_client_match_configuration_again()
    {
        // Own host: the drift below leaves the application manager's cache stale for every other test.
        using var own = new OidcWebApplicationFactory();
        await new OidcIssuerTests(own).SeederRestoresTheClientAsync();
    }

    private async Task SeederRestoresTheClientAsync()
    {
        using var warm = NewClient();
        await warm.GetAsync("/healthz");
        using var scope = _factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<OpenIddict.Abstractions.IOpenIddictApplicationManager>();
        var application = await manager.FindByClientIdAsync(OidcTestKit.ClientId);
        Assert.NotNull(application);

        // Someone widened the client in the database; the next start puts configuration back.
        var drifted = new OpenIddict.Abstractions.OpenIddictApplicationDescriptor
        {
            ClientId = OidcTestKit.ClientId,
            ClientType = OpenIddict.Abstractions.OpenIddictConstants.ClientTypes.Confidential,
            ClientSecret = "drifted-secret",
            RedirectUris = { new Uri("https://evil.example.test/callback") }
        };
        await manager.UpdateAsync(application, drifted);
        var seeders = _factory.Services.GetServices<IHostedService>().OfType<Bifrost.Web.Features.Oidc.OidcClientSeeder>().ToList();
        var seeder = Assert.Single(seeders);
        await seeder.StartAsync(CancellationToken.None);

        // Read the stored row, not through the manager: the manager's cache still holds the drifted instance.
        using var fresh = _factory.Services.CreateScope();
        var rows = fresh.ServiceProvider.GetRequiredService<Bifrost.Web.Data.BifrostDbContext>()
            .Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreApplication>()
            .AsNoTracking().ToList();
        var stored = Assert.Single(rows);
        Assert.Equal([OidcTestKit.RedirectUri], (await manager.GetRedirectUrisAsync(stored)).ToArray());
        Assert.True(await manager.ValidateClientSecretAsync(stored, _factory.Kit.ClientSecret));
        Assert.False(await manager.ValidateClientSecretAsync(stored, "drifted-secret"));
    }

    [Theory]
    [InlineData(MembershipStatus.Authenticated)]
    [InlineData(MembershipStatus.PendingApproval)]
    [InlineData(MembershipStatus.Suspended)]
    [InlineData(null)]
    public async Task A_member_no_longer_active_after_authorize_gets_no_tokens(MembershipStatus? status)
    {
        var (client, identity) = await SignedInAsync(MembershipStatus.Active, "Soon Gone");
        var request = NewRequest();
        var code = await CodeAsync(client, request);

        await LeaveActiveAsync(identity, status);

        using var response = await TokenAsync(client, request, code);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_grant", body);
        Assert.DoesNotContain("id_token", body);
        Assert.DoesNotContain("access_token", body);
    }

    [Theory]
    [InlineData(MembershipStatus.Authenticated)]
    [InlineData(MembershipStatus.PendingApproval)]
    [InlineData(MembershipStatus.Suspended)]
    [InlineData(null)]
    public async Task Userinfo_refuses_an_access_token_whose_member_is_no_longer_active(MembershipStatus? status)
    {
        var (client, identity) = await SignedInAsync(MembershipStatus.Active, "Soon Gone");
        var tokens = await CompleteFlowAsync(client);

        using (var before = await UserInfoAsync(tokens.AccessToken))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        await LeaveActiveAsync(identity, status);

        using var after = await UserInfoAsync(tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
        Assert.DoesNotContain(identity, await after.Content.ReadAsStringAsync());
    }

    private Task<HttpResponseMessage> UserInfoAsync(string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return NewClient().SendAsync(request);
    }

    /// <summary>A null status deletes the account instead.</summary>
    private Task LeaveActiveAsync(string identity, MembershipStatus? status) =>
        status is { } value ? _factory.SetMembershipAsync(identity, value) : _factory.DeleteAccountAsync(identity);

    [Fact]
    public async Task Startup_removes_every_client_but_the_configured_one_with_its_tokens_and_authorizations()
    {
        // Own host: the stale clients are rows the other tests must not see.
        using var own = new OidcCascadeFactory();
        await new OidcIssuerTests(own).StaleClientsAreRemovedAsync();
    }

    private async Task StaleClientsAreRemovedAsync()
    {
        // Two stale clients, one of them differing from the configured id only in case.
        var stale = new[]
        {
            (Id: "old-mas", Redirect: "https://old.example.test/callback"),
            (Id: OidcTestKit.ClientId.ToUpperInvariant(), Redirect: "https://shouting.example.test/callback")
        };
        var (client, _) = await SignedInAsync(MembershipStatus.Active, "Member");
        using (var scope = _factory.Services.CreateScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<OpenIddict.Abstractions.IOpenIddictApplicationManager>();
            foreach (var (id, redirect) in stale)
            {
                await manager.CreateAsync(new OpenIddict.Abstractions.OpenIddictApplicationDescriptor
                {
                    ClientId = id,
                    ClientSecret = "stale-secret",
                    ClientType = OpenIddict.Abstractions.OpenIddictConstants.ClientTypes.Confidential,
                    RedirectUris = { new Uri(redirect) },
                    Permissions =
                    {
                        OpenIddict.Abstractions.OpenIddictConstants.Permissions.Endpoints.Authorization,
                        OpenIddict.Abstractions.OpenIddictConstants.Permissions.Endpoints.Token,
                        OpenIddict.Abstractions.OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
                        OpenIddict.Abstractions.OpenIddictConstants.Permissions.ResponseTypes.Code,
                        OpenIddict.Abstractions.OpenIddictConstants.Permissions.Scopes.Profile,
                        OpenIddict.Abstractions.OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddict.Abstractions.OpenIddictConstants.Scopes.OpenId
                    }
                });
            }
        }

        // The control: before seeding every client, stale or configured, really can obtain a code and so holds
        // an authorization and a token.
        foreach (var (id, redirect) in stale)
        {
            using var issued = await DriveAsync(client, NewRequest(id, redirect));
            Assert.Contains("code=", issued.Headers.Location?.OriginalString ?? string.Empty);
        }

        _ = await CodeAsync(client, NewRequest());
        var staleCounts = Counts(stale.Select(x => x.Id));
        Assert.Equal(stale.Length, staleCounts.Count);
        Assert.All(staleCounts, x => Assert.True(x.Tokens > 0 && x.Authorizations > 0, "the control must leave grants to remove"));
        var configuredBefore = Counts([OidcTestKit.ClientId]).Single();
        Assert.True(configuredBefore.Tokens > 0 && configuredBefore.Authorizations > 0);

        var seeder = Assert.Single(_factory.Services.GetServices<IHostedService>().OfType<Bifrost.Web.Features.Oidc.OidcClientSeeder>());
        await seeder.StartAsync(CancellationToken.None);

        foreach (var (id, redirect) in stale)
        {
            using var refused = await DriveAsync(client, NewRequest(id, redirect));
            Assert.DoesNotContain("code=", refused.Headers.Location?.OriginalString ?? string.Empty);
            Assert.NotEqual(HttpStatusCode.OK, refused.StatusCode);
        }

        using var fresh = _factory.Services.CreateScope();
        var db = fresh.ServiceProvider.GetRequiredService<Bifrost.Web.Data.BifrostDbContext>();
        var rows = db.Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreApplication>().AsNoTracking().ToList();
        Assert.Equal([OidcTestKit.ClientId], rows.Select(x => x.ClientId).ToArray());

        // Nothing issued to a removed client survives, and the configured client's grants are untouched.
        var keptId = rows.Single().Id;
        Assert.Empty(db.Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreToken>().AsNoTracking().Where(x => x.Application!.Id != keptId));
        Assert.Empty(db.Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreAuthorization>().AsNoTracking().Where(x => x.Application!.Id != keptId));
        Assert.Equal(configuredBefore, Counts([OidcTestKit.ClientId]).Single());
    }

    private sealed record GrantCount(int Tokens, int Authorizations);

    private List<GrantCount> Counts(IEnumerable<string> clientIds)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Bifrost.Web.Data.BifrostDbContext>();
        var applications = db.Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreApplication>().AsNoTracking().ToList();
        return clientIds
            .Select(id => applications.Single(x => x.ClientId == id).Id)
            .Select(applicationId => new GrantCount(
                db.Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreToken>().Count(x => x.Application!.Id == applicationId),
                db.Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreAuthorization>().Count(x => x.Application!.Id == applicationId)))
            .ToList();
    }

    [Fact]
    public async Task The_issuer_is_the_configured_public_base_url_not_the_request_origin()
    {
        using var factory = new OidcForeignIssuerFactory();
        using var client = factory.CreateClient(ClientOptions());
        var discovery = await client.GetFromJsonAsync<JsonElement>("/.well-known/openid-configuration");

        Assert.Equal(OidcForeignIssuerFactory.PublicBaseUrl, discovery.GetProperty("issuer").GetString());
    }

    // ---- helpers ----

    private static WebApplicationFactoryClientOptions ClientOptions() => new()
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
        BaseAddress = Origin
    };

    private HttpClient NewClient() => _factory.CreateClient(ClientOptions());

    private static async Task<string> RegisterAsync(HttpClient client, string identity)
    {
        using var response = await client.PostAsJsonAsync("/auth/bifrost/register", new { identity, displayName = identity });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return identity.ToLowerInvariant();
    }

    private async Task<(HttpClient Client, string Identity)> SignedInAsync(MembershipStatus status, string displayName)
    {
        var client = NewClient();
        var identity = await RegisterAsync(client, $"member-{Guid.NewGuid():N}");
        await _factory.SetMembershipAsync(identity, status);
        await _factory.SetDisplayNameAsync(identity, displayName);
        return (client, identity);
    }

    private async Task SetLinksAsync(string identity, string heimdallAccountId, string gitHubLogin)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Bifrost.Web.Data.BifrostDbContext>();
        var account = db.UserAccounts.Single(x => x.NormalizedBifrostIdentity == identity);
        account.HeimdallAccountId = heimdallAccountId;
        account.GitHubLogin = gitHubLogin;
        await db.SaveChangesAsync();
    }

    private sealed record AuthRequest(string Verifier, string State, string Nonce, string Path)
    {
        public string Challenge => Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier)));
    }

    private static AuthRequest NewRequest(
        string clientId = OidcTestKit.ClientId,
        string redirectUri = OidcTestKit.RedirectUri,
        bool pkce = true,
        string extra = "",
        string scope = "openid%20profile")
    {
        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(48));
        var state = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(24));
        var nonce = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(24));
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var path = "/connect/authorize?response_type=code" +
            $"&client_id={Uri.EscapeDataString(clientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={scope}" +
            $"&state={state}&nonce={nonce}" +
            (pkce ? $"&code_challenge={challenge}&code_challenge_method=S256" : string.Empty) +
            extra;
        return new AuthRequest(verifier, state, nonce, path);
    }

    /// <summary>Starts an authorize request and follows the provider's own redirects until the first response that is not one of them.</summary>
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

    private static string ReturnUrl(HttpResponseMessage signInRedirect)
    {
        var location = signInRedirect.Headers.Location!;
        var query = location.IsAbsoluteUri ? location.Query : location.OriginalString[location.OriginalString.IndexOf('?')..];
        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query)
            .Single(x => string.Equals(x.Key, "returnUrl", StringComparison.OrdinalIgnoreCase)).Value.ToString();
    }

    private static Dictionary<string, string?> AssertRedirectToClient(HttpResponseMessage response, string state)
    {
        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found, $"expected a redirect, got {(int)response.StatusCode}");
        var location = response.Headers.Location!;
        Assert.Equal(new Uri(OidcTestKit.RedirectUri).GetLeftPart(UriPartial.Path), location.GetLeftPart(UriPartial.Path));
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(location.Query);
        Assert.Equal(state, query["state"].ToString());
        return query.ToDictionary(x => x.Key, x => (string?)x.Value.ToString());
    }

    private async Task<string> CodeAsync(HttpClient signedIn, AuthRequest request)
    {
        var final = await DriveAsync(signedIn, request);
        return AssertRedirectToClient(final, request.State)["code"]!;
    }

    private sealed record Tokens(AuthRequest Request, string IdToken, string AccessToken);

    private async Task<Tokens> CompleteFlowAsync(HttpClient signedIn)
    {
        var request = NewRequest();
        var code = await CodeAsync(signedIn, request);
        return await ExchangeAsync(signedIn, request, code);
    }

    private async Task<Tokens> ExchangeAsync(HttpClient client, AuthRequest request, string code)
    {
        using var response = await TokenAsync(client, request, code);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain("refresh_token", body.EnumerateObject().Select(x => x.Name));
        return new Tokens(request, body.GetProperty("id_token").GetString()!, body.GetProperty("access_token").GetString()!);
    }

    private async Task<HttpResponseMessage> TokenAsync(
        HttpClient client, AuthRequest request, string code, string? secret = null, bool secretInBody = false)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = OidcTestKit.RedirectUri,
            ["code_verifier"] = request.Verifier
        };
        var message = new HttpRequestMessage(HttpMethod.Post, "/connect/token");
        var effectiveSecret = secret ?? _factory.Kit.ClientSecret;
        if (secretInBody)
        {
            form["client_id"] = OidcTestKit.ClientId;
            form["client_secret"] = effectiveSecret;
        }
        else
        {
            message.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.ASCII.GetBytes($"{OidcTestKit.ClientId}:{effectiveSecret}")));
        }

        message.Content = new FormUrlEncodedContent(form);
        // The code is bound to the client; use a cookieless client so a session cannot influence the exchange.
        using var bare = NewClient();
        return await bare.SendAsync(message);
    }

    private sealed record Verified(string Alg, string PayloadJson, JsonElement Payload)
    {
        public string? Claim(string name) => Payload.TryGetProperty(name, out var value) ? value.GetString() : null;
    }

    private async Task<Verified> ValidateIdTokenAsync(string idToken, string? nonce)
    {
        using var client = NewClient();
        var jwks = new JsonWebKeySet((await GetJwksAsync(client)).GetRawText());
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuer = Origin.AbsoluteUri,
            ValidAudience = OidcTestKit.ClientId,
            IssuerSigningKeys = jwks.GetSigningKeys(),
            ValidAlgorithms = ["ES256"]
        });
        Assert.True(result.IsValid, result.Exception?.Message);
        var jwt = (JsonWebToken)result.SecurityToken;
        var payload = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(jwt.EncodedPayload));
        if (nonce is not null)
        {
            Assert.Equal(nonce, JsonDocument.Parse(payload).RootElement.GetProperty("nonce").GetString());
        }

        return new Verified(jwt.Alg, payload, JsonDocument.Parse(payload).RootElement.Clone());
    }

    private static async Task<JsonElement> GetJwksAsync(HttpClient client)
    {
        var discovery = await client.GetFromJsonAsync<JsonElement>("/.well-known/openid-configuration");
        var jwksUri = new Uri(discovery.GetProperty("jwks_uri").GetString()!);
        return await client.GetFromJsonAsync<JsonElement>(jwksUri.PathAndQuery);
    }

    private static string[] Strings(JsonElement document, string property) =>
        document.GetProperty(property).EnumerateArray().Select(x => x.GetString()!).ToArray();
}

public sealed class OidcSurfaceAbsentTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public OidcSurfaceAbsentTests(TestWebApplicationFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/.well-known/openid-configuration")]
    [InlineData("/.well-known/jwks")]
    [InlineData("/connect/authorize")]
    [InlineData("/connect/token")]
    [InlineData("/connect/userinfo")]
    public async Task Without_the_configuration_section_no_provider_surface_exists(string path)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

public sealed class OidcStartupFailureTests
{
    [Theory]
    [InlineData(OidcKeyFault.MissingFile)]
    [InlineData(OidcKeyFault.RsaKey)]
    [InlineData(OidcKeyFault.PublicKeyOnly)]
    [InlineData(OidcKeyFault.NotPem)]
    public void A_configured_provider_with_an_unusable_signing_key_stops_startup_naming_the_setting_not_the_path(OidcKeyFault fault)
    {
        using var factory = new OidcKeyFaultFactory(fault);

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var messages = Flatten(failure).ToList();
        Assert.Contains(messages, m => m.Contains("Bifrost:Oidc:SigningKeyPath", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, m => m.Contains("canary-", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, m => m.Contains("BEGIN", StringComparison.Ordinal));
    }

    private static IEnumerable<string> Flatten(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException!)
        {
            yield return current.Message;
            if (current.InnerException is null)
            {
                yield break;
            }
        }
    }
}
