using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Shared;
using Bifrost.Web.Tests.Support;
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
    private readonly OidcWebApplicationFactory _factory;

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
        var request = NewRequest();
        using var client = NewClient();
        var signIn = await DriveAsync(client, request);
        var resume = ReturnUrl(signIn);
        var identity = await RegisterAsync(client, $"late-{Guid.NewGuid():N}");
        await _factory.SetMembershipAsync(identity, MembershipStatus.Active);

        _factory.Clock.Advance(Bifrost.Web.Features.Oidc.OidcServiceCollectionExtensions.ParkedRequestLifetime + TimeSpan.FromMinutes(1));

        using var late = await client.GetAsync(resume);
        var location = late.Headers.Location?.OriginalString ?? string.Empty;
        Assert.DoesNotContain("code=", location);
        Assert.NotEqual(HttpStatusCode.OK, late.StatusCode);
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
