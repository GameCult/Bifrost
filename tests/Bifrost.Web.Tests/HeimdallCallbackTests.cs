using System.Net;
using System.Text;
using System.Text.Json;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Heimdall;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.WebUtilities;

namespace Bifrost.Web.Tests;

/// <summary>
/// Sign-in through the Heimdall callback, against a local Bifrost with a test JWKS. Deliveries are shaped
/// like Heimdall's backend-handoff.ts payload and claims.ts claim set; forgeries differ from them in the
/// one property under test.
/// </summary>
public sealed class HeimdallCallbackTests
{
    private static string Flip(string value) => value[..^1] + (value[^1] == '0' ? '1' : '0');

    private static async Task<HttpResponseMessage> DeliverGenuine(
        HeimdallHost host, StartedAttempt attempt, string accountId, string[]? capabilities = null, string? displayName = "Genuine Name")
    {
        return await host.DeliverAsync(HeimdallShapes.Delivery(
            attempt.AttemptId, accountId, host.Token(accountId, capabilities, displayName)));
    }

    private static void AssertLogged(HeimdallHost host, string reason) =>
        Assert.Contains("Heimdall callback refused: " + reason, string.Join("\n", host.Logs.Lines));

    private static void AssertTokenRefused(HeimdallHost host, string rule) =>
        AssertLogged(host, $"Heimdall token refused: {rule}.");

    private static string ErrorLocation(string message) => "/?auth=heimdall-error&detail=" + Uri.EscapeDataString(message);

    private static async Task AssertRefused(HeimdallHost host, StartedAttempt attempt, string accountId, HttpResponseMessage delivery)
    {
        Assert.Equal(HttpStatusCode.BadRequest, delivery.StatusCode);
        var wait = await host.WaitAsync(attempt.AttemptId, attempt.Binding);
        Assert.False(wait.SignedIn);
        Assert.Equal(HttpStatusCode.OK, wait.Response.StatusCode);
        Assert.Null(await host.Account(accountId));
    }

    [Fact]
    public async Task Genuine_delivery_signs_in_the_account_the_token_names()
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();
        var attempt = await host.StartAsync(returnUrl: "/App/Mill");

        using var delivery = await DeliverGenuine(host, attempt, accountId);
        Assert.Equal(HttpStatusCode.Accepted, delivery.StatusCode);

        var wait = await host.WaitAsync(attempt.AttemptId, attempt.Binding);
        Assert.True(wait.SignedIn);
        Assert.Equal(HttpStatusCode.Redirect, wait.Response.StatusCode);
        Assert.Equal("/App/Mill", wait.Response.Headers.Location!.OriginalString);

        var account = await host.Account(accountId);
        Assert.NotNull(account);
        Assert.Equal("Genuine Name", account!.DisplayName);
        Assert.Equal(MembershipStatus.Authenticated, account.Membership!.Status);
        Assert.Empty(account.Membership.RoleAssignments);

        var cleared = HeimdallHost.SetCookieHeader(wait.Response, HeimdallHost.BindingCookie);
        Assert.NotNull(cleared);
        Assert.Contains("1970", cleared);
    }

    [Fact]
    public async Task The_display_name_is_the_claim_and_falls_back_to_the_account_id()
    {
        using var host = new HeimdallHost();
        var named = HeimdallShapes.NewAccountId();
        var unnamed = HeimdallShapes.NewAccountId();

        var first = await host.StartAsync();
        await DeliverGenuine(host, first, named, displayName: "Claimed Name");
        Assert.True((await host.WaitAsync(first.AttemptId, first.Binding)).SignedIn);

        var second = await host.StartAsync();
        await DeliverGenuine(host, second, unnamed, displayName: null);
        Assert.True((await host.WaitAsync(second.AttemptId, second.Binding)).SignedIn);

        Assert.Equal("Claimed Name", (await host.Account(named))!.DisplayName);
        Assert.Equal(unnamed, (await host.Account(unnamed))!.DisplayName);
    }

    [Fact]
    public async Task A_callback_without_an_access_token_signs_nobody_in()
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();
        var attempt = await host.StartAsync();
        var payload = HeimdallShapes.Delivery(attempt.AttemptId, accountId, "unused");
        payload.Remove("accessToken");

        using var delivery = await host.DeliverAsync(payload);

        await AssertRefused(host, attempt, accountId, delivery);
        AssertLogged(host, "Heimdall callback carried no access token.");
    }

    [Fact]
    public async Task A_callback_without_an_attempt_id_signs_nobody_in()
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();

        using var delivery = await host.DeliverAsync(HeimdallShapes.Delivery(" ", accountId, host.Token(accountId)));

        Assert.Equal(HttpStatusCode.BadRequest, delivery.StatusCode);
        AssertLogged(host, "Heimdall callback omitted attempt id.");
    }

    [Fact]
    public async Task An_empty_access_token_signs_nobody_in()
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();
        var attempt = await host.StartAsync();

        using var delivery = await host.DeliverAsync(HeimdallShapes.Delivery(attempt.AttemptId, accountId, "  "));

        await AssertRefused(host, attempt, accountId, delivery);
        AssertLogged(host, "Heimdall callback carried no access token.");
    }

    [Theory]
    [InlineData("other-key-same-kid", "token signature is invalid")]
    [InlineData("other-key-other-kid", "token key is not published")]
    [InlineData("tampered-payload", "token signature is invalid")]
    [InlineData("alg-none", "token algorithm is not EdDSA")]
    [InlineData("alg-hs256", "token algorithm is not EdDSA")]
    [InlineData("header-array", "token algorithm is not EdDSA")]
    [InlineData("header-not-json", "token header is not JSON")]
    [InlineData("truncated-signature", "token signature is invalid")]
    [InlineData("no-kid", "token has no key id")]
    [InlineData("unpublished-kid", "token key is not published")]
    [InlineData("four-segments", "token is not a compact JWS")]
    [InlineData("two-segments", "token is not a compact JWS")]
    [InlineData("not-base64url", "token is not base64url")]
    [InlineData("payload-not-json", "token payload is not JSON")]
    [InlineData("payload-not-object", "token payload is not an object")]
    public async Task A_forged_token_signs_nobody_in(string forgery, string rule)
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();
        var attempt = await host.StartAsync();
        var claims = HeimdallShapes.AccessClaims(accountId, host.Clock.Now, ["member_access"]);
        var claimBytes = JsonSerializer.SerializeToUtf8Bytes(claims);
        var genuine = host.Key.Sign(claims);
        var parts = genuine.Split('.');

        var token = forgery switch
        {
            "other-key-same-kid" => HeimdallKey.Generate(host.Key.Kid).Sign(claims),
            "other-key-other-kid" => HeimdallKey.Generate().Sign(claims),
            "tampered-payload" => parts[0] + "." + WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(
                HeimdallShapes.AccessClaims(accountId, host.Clock.Now, ["member_access", "x"]))) + "." + parts[2],
            "alg-none" => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("{\"alg\":\"none\",\"typ\":\"JWT\"}")) + "." + parts[1] + ".",
            "alg-hs256" => host.Key.Sign(claims, alg: "HS256"),
            "header-array" => host.Key.SignParts(Encoding.UTF8.GetBytes("[1]"), claimBytes),
            "header-not-json" => host.Key.SignParts(Encoding.UTF8.GetBytes("not json"), claimBytes),
            "truncated-signature" => parts[0] + "." + parts[1] + "." +
                WebEncoders.Base64UrlEncode(WebEncoders.Base64UrlDecode(parts[2])[..63]),
            "no-kid" => host.Key.Sign(claims, kid: null),
            "unpublished-kid" => host.Key.Sign(claims, kid: "ed25519-" + new string('0', 12)),
            "four-segments" => genuine + ".AAAA",
            "two-segments" => parts[0] + "." + parts[1],
            "not-base64url" => parts[0] + "." + parts[1] + ".!!" + parts[2][2..],
            "payload-not-json" => host.Key.SignParts(host.Key.HeaderBytes(), Encoding.UTF8.GetBytes("not json")),
            "payload-not-object" => host.Key.SignParts(host.Key.HeaderBytes(), Encoding.UTF8.GetBytes("[1]")),
            _ => throw new InvalidOperationException(forgery)
        };
        Assert.NotEqual(genuine, token);

        using var delivery = await host.DeliverAsync(HeimdallShapes.Delivery(attempt.AttemptId, accountId, token));

        await AssertRefused(host, attempt, accountId, delivery);
        AssertTokenRefused(host, rule);
    }

    [Fact]
    public async Task A_forged_token_cannot_sign_in_as_an_existing_account()
    {
        using var host = new HeimdallHost();
        var victim = HeimdallShapes.NewAccountId();
        var real = await host.StartAsync();
        await DeliverGenuine(host, real, victim, displayName: "Victim Name");
        Assert.True((await host.WaitAsync(real.AttemptId, real.Binding)).SignedIn);

        var attempt = await host.StartAsync();
        var forged = HeimdallKey.Generate(host.Key.Kid).Sign(HeimdallShapes.AccessClaims(victim, host.Clock.Now, displayName: "Mallory"));
        using var delivery = await host.DeliverAsync(HeimdallShapes.Delivery(attempt.AttemptId, victim, forged));

        Assert.Equal(HttpStatusCode.BadRequest, delivery.StatusCode);
        Assert.False((await host.WaitAsync(attempt.AttemptId, attempt.Binding)).SignedIn);
        Assert.Equal("Victim Name", (await host.Account(victim))!.DisplayName);
    }

    [Theory]
    [InlineData("aud-other-app", "token audience is not this app")]
    [InlineData("aud-case", "token audience is not this app")]
    [InlineData("aud-array", "token audience is not this app")]
    [InlineData("aud-missing", "token audience is not this app")]
    [InlineData("app-slug-other", "token app is not this app")]
    [InlineData("app-missing", "token app is not this app")]
    [InlineData("app-not-object", "token app is not this app")]
    [InlineData("typ-refresh", "token type is not an access token")]
    [InlineData("typ-missing", "token type is not an access token")]
    [InlineData("iss-other", "token issuer is not Heimdall")]
    [InlineData("iss-trailing-slash", "token issuer is not Heimdall")]
    [InlineData("expired", "token has expired")]
    [InlineData("exp-missing", "token has expired")]
    [InlineData("exp-string", "token has expired")]
    [InlineData("exp-out-of-range", "token has expired")]
    [InlineData("exp-negative", "token has expired")]
    [InlineData("not-yet-valid", "token is not yet valid")]
    [InlineData("nbf-missing", "token is not yet valid")]
    [InlineData("nbf-negative", "token is not yet valid")]
    [InlineData("sub-differs", "token subject does not match the account")]
    [InlineData("account-id-differs", "token subject does not match the account")]
    [InlineData("account-id-missing", "token subject does not match the account")]
    [InlineData("sub-empty", "token subject does not match the account")]
    public async Task A_signed_token_with_a_wrong_claim_signs_nobody_in(string wrong, string rule)
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();
        var attempt = await host.StartAsync();
        var now = host.Clock.Now.ToUnixTimeSeconds();
        var token = host.Token(accountId, ["member_access"], mutate: claims =>
        {
            switch (wrong)
            {
                case "aud-other-app": claims["aud"] = "ghostlight"; break;
                case "aud-case": claims["aud"] = "Bifrost"; break;
                case "aud-array": claims["aud"] = new[] { "bifrost" }; break;
                case "aud-missing": claims.Remove("aud"); break;
                case "app-slug-other": claims["app"] = new Dictionary<string, object?> { ["slug"] = "ghostlight" }; break;
                case "app-missing": claims.Remove("app"); break;
                case "app-not-object": claims["app"] = "bifrost"; break;
                case "typ-refresh": claims["typ"] = "heimdall_refresh"; break;
                case "typ-missing": claims.Remove("typ"); break;
                case "iss-other": claims["iss"] = "https://heimdall.example"; break;
                case "iss-trailing-slash": claims["iss"] = HeimdallShapes.Issuer + "/"; break;
                case "expired": claims["exp"] = now - 3600; break;
                case "exp-missing": claims.Remove("exp"); break;
                case "exp-string": claims["exp"] = (now + 900).ToString(); break;
                case "exp-out-of-range": claims["exp"] = long.MaxValue; break;
                case "exp-negative": claims["exp"] = -1; break;
                case "not-yet-valid": claims["nbf"] = now + 3600; claims["exp"] = now + 7200; break;
                case "nbf-missing": claims.Remove("nbf"); break;
                case "nbf-negative": claims["nbf"] = -1; break;
                case "sub-differs": claims["sub"] = Flip(accountId); break;
                case "account-id-differs": claims["account_id"] = Flip(accountId); break;
                case "account-id-missing": claims.Remove("account_id"); break;
                case "sub-empty": claims["sub"] = string.Empty; claims["account_id"] = string.Empty; break;
                default: throw new InvalidOperationException(wrong);
            }
        });

        using var delivery = await host.DeliverAsync(HeimdallShapes.Delivery(attempt.AttemptId, accountId, token));

        await AssertRefused(host, attempt, accountId, delivery);
        AssertTokenRefused(host, rule);
    }

    [Fact]
    public async Task A_token_for_a_different_account_than_the_payload_names_is_refused_by_one_character()
    {
        using var host = new HeimdallHost();
        var tokenAccount = HeimdallShapes.NewAccountId();
        var payloadAccount = Flip(tokenAccount);
        var attempt = await host.StartAsync();

        using var delivery = await host.DeliverAsync(HeimdallShapes.Delivery(
            attempt.AttemptId, payloadAccount, host.Token(tokenAccount)));

        Assert.Equal(HttpStatusCode.BadRequest, delivery.StatusCode);
        Assert.False((await host.WaitAsync(attempt.AttemptId, attempt.Binding)).SignedIn);
        Assert.Null(await host.Account(tokenAccount));
        Assert.Null(await host.Account(payloadAccount));
        AssertTokenRefused(host, "token subject does not match the account");
    }

    [Fact]
    public async Task A_payload_with_no_account_is_refused()
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();
        var attempt = await host.StartAsync();
        var payload = HeimdallShapes.Delivery(attempt.AttemptId, accountId, host.Token(accountId));
        payload["account"] = null;

        using var delivery = await host.DeliverAsync(payload);

        await AssertRefused(host, attempt, accountId, delivery);
        AssertTokenRefused(host, "token subject does not match the account");
    }

    [Theory]
    [InlineData(60, 900, true)]
    [InlineData(61, 900, false)]
    [InlineData(-1000, -59, true)]
    [InlineData(-1000, -60, false)]
    public async Task Token_validity_allows_a_minute_of_clock_skew(int notBeforeOffset, int expiresOffset, bool accepted)
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();
        var attempt = await host.StartAsync();
        var now = host.Clock.Now.ToUnixTimeSeconds();
        var token = host.Token(accountId, mutate: claims =>
        {
            claims["nbf"] = now + notBeforeOffset;
            claims["exp"] = now + expiresOffset;
        });

        using var delivery = await host.DeliverAsync(HeimdallShapes.Delivery(attempt.AttemptId, accountId, token));

        Assert.Equal(accepted ? HttpStatusCode.Accepted : HttpStatusCode.BadRequest, delivery.StatusCode);
    }

    [Fact]
    public async Task A_well_signed_token_for_an_attempt_start_never_issued_creates_nothing()
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();
        var unissued = Guid.NewGuid().ToString("N");

        using var delivery = await host.DeliverAsync(HeimdallShapes.Delivery(unissued, accountId, host.Token(accountId)));

        Assert.Equal(HttpStatusCode.BadRequest, delivery.StatusCode);
        var wait = await host.WaitAsync(unissued, "anything");
        Assert.False(wait.SignedIn);
        Assert.Equal(ErrorLocation("Heimdall auth attempt is not valid for this browser."), wait.Response.Headers.Location!.OriginalString);
        Assert.Null(await host.Account(accountId));
        AssertLogged(host, "Heimdall callback did not match a pending attempt.");
    }

    [Fact]
    public async Task A_second_delivery_does_not_replace_the_first()
    {
        using var host = new HeimdallHost();
        var first = HeimdallShapes.NewAccountId();
        var second = HeimdallShapes.NewAccountId();
        var attempt = await host.StartAsync();

        using var accepted = await DeliverGenuine(host, attempt, first);
        using var replaced = await DeliverGenuine(host, attempt, second);

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, replaced.StatusCode);
        AssertLogged(host, "Heimdall callback did not match a pending attempt.");
        Assert.True((await host.WaitAsync(attempt.AttemptId, attempt.Binding)).SignedIn);
        Assert.NotNull(await host.Account(first));
        Assert.Null(await host.Account(second));
    }

    [Fact]
    public async Task An_attempt_is_deliverable_until_ten_minutes_after_start()
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();
        var attempt = await host.StartAsync();
        host.Clock.Advance(HeimdallAuthAttemptStore.Lifetime - TimeSpan.FromSeconds(1));

        using var delivery = await DeliverGenuine(host, attempt, accountId);

        Assert.Equal(HttpStatusCode.Accepted, delivery.StatusCode);
        Assert.True((await host.WaitAsync(attempt.AttemptId, attempt.Binding)).SignedIn);
    }

    [Fact]
    public async Task An_expired_attempt_is_refused_at_callback()
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();
        var attempt = await host.StartAsync();
        host.Clock.Advance(HeimdallAuthAttemptStore.Lifetime);

        using var delivery = await DeliverGenuine(host, attempt, accountId);

        Assert.Equal(HttpStatusCode.BadRequest, delivery.StatusCode);
        AssertLogged(host, "Heimdall callback did not match a pending attempt.");
        var wait = await host.WaitAsync(attempt.AttemptId, attempt.Binding);
        Assert.False(wait.SignedIn);
        Assert.Equal(ErrorLocation("Heimdall auth attempt is not valid for this browser."), wait.Response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task A_delivered_attempt_expires_at_wait()
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();
        var attempt = await host.StartAsync();
        await DeliverGenuine(host, attempt, accountId);
        host.Clock.Advance(HeimdallAuthAttemptStore.Lifetime);

        var wait = await host.WaitAsync(attempt.AttemptId, attempt.Binding);

        Assert.False(wait.SignedIn);
        Assert.Equal(ErrorLocation("Heimdall auth attempt is not valid for this browser."), wait.Response.Headers.Location!.OriginalString);
        Assert.Null(await host.Account(accountId));
    }

    [Fact]
    public void A_full_store_drops_the_attempt_that_expires_first()
    {
        var clock = new TestClock();
        var store = new HeimdallAuthAttemptStore(clock);
        var identity = new HeimdallVerifiedIdentity("account", "Name", false);
        for (var i = 0; i < HeimdallAuthAttemptStore.Capacity; i++)
        {
            store.Create($"attempt-{i}", "discord", "/App", false);
            clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        // Exactly at capacity nothing has been dropped yet.
        store.Complete("attempt-1", identity);
        store.Create("attempt-extra", "discord", "/App", false);

        Assert.Throws<HeimdallAuthException>(() => store.Complete("attempt-0", identity));
        store.Complete("attempt-2", identity);
        store.Complete("attempt-extra", identity);
        store.Complete($"attempt-{HeimdallAuthAttemptStore.Capacity - 1}", identity);
    }

    [Fact]
    public async Task Start_sets_a_private_per_attempt_binding_cookie()
    {
        using var host = new HeimdallHost();
        var first = await host.StartAsync();
        var second = await host.StartAsync();

        Assert.Equal(HttpStatusCode.Redirect, first.Response.StatusCode);
        var header = HeimdallHost.SetCookieHeader(first.Response, HeimdallHost.BindingCookie);
        Assert.NotNull(header);
        var attributes = header!.Split(';').Select(x => x.Trim().ToLowerInvariant()).ToArray();
        Assert.Contains("httponly", attributes);
        Assert.Contains("secure", attributes);
        Assert.Contains("samesite=lax", attributes);
        Assert.Contains("path=/auth/heimdall", attributes);
        Assert.Contains("max-age=600", attributes);

        Assert.Equal(43, first.Binding!.Length);
        Assert.NotEqual(first.Binding, second.Binding);
        Assert.DoesNotContain(first.AttemptId, header);
        Assert.NotEqual(first.AttemptId, first.Binding);
    }

    [Fact]
    public async Task Wait_signs_in_only_the_browser_that_holds_the_binding_and_only_once()
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();
        var attempt = await host.StartAsync();
        var other = await host.StartAsync();
        await DeliverGenuine(host, attempt, accountId);

        var wrong = new Dictionary<string, string?>
        {
            ["no cookie"] = null,
            ["empty cookie"] = string.Empty,
            ["another attempt's cookie"] = other.Binding,
            ["same-shape random cookie"] = WebEncoders.Base64UrlEncode(new byte[32]),
            ["the attempt id as cookie"] = attempt.AttemptId,
            ["a prefix of the cookie"] = attempt.Binding![..^1],
            ["the cookie with a suffix"] = attempt.Binding + "A"
        };
        foreach (var (name, binding) in wrong)
        {
            var refused = await host.WaitAsync(attempt.AttemptId, binding);
            Assert.False(refused.SignedIn, name);
            Assert.Equal(ErrorLocation("Heimdall auth attempt is not valid for this browser."), refused.Response.Headers.Location!.OriginalString);
        }

        Assert.Null(await host.Account(accountId));

        var granted = await host.WaitAsync(attempt.AttemptId, attempt.Binding);
        Assert.True(granted.SignedIn);

        var replay = await host.WaitAsync(attempt.AttemptId, attempt.Binding);
        Assert.False(replay.SignedIn);
    }

    [Fact]
    public async Task Wait_before_delivery_is_pending_and_does_not_consume_the_attempt()
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();
        var attempt = await host.StartAsync();

        var early = await host.WaitAsync(attempt.AttemptId, attempt.Binding);
        Assert.False(early.SignedIn);
        Assert.Equal(HttpStatusCode.OK, early.Response.StatusCode);
        Assert.Equal("2", Assert.Single(early.Response.Headers.GetValues("Refresh")));

        await DeliverGenuine(host, attempt, accountId);

        Assert.True((await host.WaitAsync(attempt.AttemptId, attempt.Binding)).SignedIn);
    }

    [Fact]
    public async Task Wait_without_an_attempt_id_signs_nobody_in()
    {
        using var host = new HeimdallHost();
        using var client = host.NewClient();

        using var response = await client.GetAsync("/auth/heimdall/wait");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Null(HeimdallHost.CookieValue(response, ".AspNetCore.Cookies"));
    }

    [Theory]
    [InlineData("member_access", true)]
    [InlineData("app_access", true)]
    [InlineData("MEMBER_ACCESS", true)]
    [InlineData("billing_access", false)]
    public async Task Member_access_comes_from_the_token_capabilities_alone(string capability, bool grants)
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();
        var attempt = await host.StartAsync(member: true);
        var payload = HeimdallShapes.Delivery(
            attempt.AttemptId,
            accountId,
            host.Token(accountId, [capability]),
            looseCapabilities: grants ? [] : ["member_access", "app_access"],
            looseFacts: grants ? [] : ["entitlement.app_access"]);

        using var delivery = await host.DeliverAsync(payload);
        Assert.Equal(HttpStatusCode.Accepted, delivery.StatusCode);
        var wait = await host.WaitAsync(attempt.AttemptId, attempt.Binding);

        var account = await host.Account(accountId);
        if (grants)
        {
            Assert.True(wait.SignedIn);
            Assert.Equal(MembershipStatus.Active, account!.Membership!.Status);
            Assert.Contains(account.Membership.RoleAssignments, x => x.Role == MemberRole.StandardMember);
        }
        else
        {
            Assert.False(wait.SignedIn);
            Assert.Equal(ErrorLocation("Heimdall did not grant Bifrost member access."), wait.Response.Headers.Location!.OriginalString);
            Assert.Null(account);
        }
    }

    [Fact]
    public async Task A_token_capability_grants_no_role_to_a_sign_in_that_did_not_ask_for_member_access()
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();
        var attempt = await host.StartAsync(member: false);

        await DeliverGenuine(host, attempt, accountId, ["member_access"]);
        Assert.True((await host.WaitAsync(attempt.AttemptId, attempt.Binding)).SignedIn);

        var account = await host.Account(accountId);
        Assert.Equal(MembershipStatus.Authenticated, account!.Membership!.Status);
        Assert.Empty(account.Membership.RoleAssignments);
    }

    [Fact]
    public async Task Loose_capabilities_cannot_turn_a_plain_token_into_member_access()
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();
        var attempt = await host.StartAsync(member: false);

        using var delivery = await host.DeliverAsync(HeimdallShapes.Delivery(
            attempt.AttemptId,
            accountId,
            host.Token(accountId),
            looseCapabilities: ["member_access"],
            looseFacts: ["entitlement.app_access"]));
        Assert.Equal(HttpStatusCode.Accepted, delivery.StatusCode);
        Assert.True((await host.WaitAsync(attempt.AttemptId, attempt.Binding)).SignedIn);

        var account = await host.Account(accountId);
        Assert.Equal(MembershipStatus.Authenticated, account!.Membership!.Status);
        Assert.Empty(account.Membership.RoleAssignments);
    }

    [Fact]
    public async Task The_loose_return_to_and_name_decide_nothing()
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();
        var attempt = await host.StartAsync(returnUrl: "/App/Mill");

        await host.DeliverAsync(HeimdallShapes.Delivery(
            attempt.AttemptId, accountId, host.Token(accountId), looseName: "Loose Name", returnTo: "https://evil.example/landing"));
        var wait = await host.WaitAsync(attempt.AttemptId, attempt.Binding);

        Assert.Equal("/App/Mill", wait.Response.Headers.Location!.OriginalString);
        Assert.Equal("Genuine Name", (await host.Account(accountId))!.DisplayName);
    }

    [Fact]
    public async Task Keys_are_fetched_once_and_cached()
    {
        using var host = new HeimdallHost();
        for (var i = 0; i < 3; i++)
        {
            var accountId = HeimdallShapes.NewAccountId();
            var attempt = await host.StartAsync();
            using var delivery = await DeliverGenuine(host, attempt, accountId);
            Assert.Equal(HttpStatusCode.Accepted, delivery.StatusCode);
        }

        Assert.Equal(1, host.Heimdall.JwksFetches);
    }

    [Fact]
    public async Task An_unknown_key_id_refetches_at_most_every_thirty_seconds()
    {
        using var host = new HeimdallHost();
        var unknown = HeimdallKey.Generate();
        async Task<HttpStatusCode> DeliverUnknown()
        {
            var accountId = HeimdallShapes.NewAccountId();
            var attempt = await host.StartAsync();
            using var delivery = await host.DeliverAsync(HeimdallShapes.Delivery(
                attempt.AttemptId, accountId, host.Token(accountId, signer: unknown)));
            return delivery.StatusCode;
        }

        Assert.Equal(HttpStatusCode.BadRequest, await DeliverUnknown());
        Assert.Equal(1, host.Heimdall.JwksFetches);

        host.Clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(HttpStatusCode.BadRequest, await DeliverUnknown());
        Assert.Equal(1, host.Heimdall.JwksFetches);

        host.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.BadRequest, await DeliverUnknown());
        Assert.Equal(2, host.Heimdall.JwksFetches);
        Assert.Equal(HttpStatusCode.BadRequest, await DeliverUnknown());
        Assert.Equal(2, host.Heimdall.JwksFetches);
    }

    [Fact]
    public async Task A_rotated_in_key_is_accepted_once_the_refetch_interval_has_passed()
    {
        using var host = new HeimdallHost();
        var rotated = HeimdallKey.Generate();
        var accountId = HeimdallShapes.NewAccountId();

        var early = await host.StartAsync();
        using var refused = await host.DeliverAsync(HeimdallShapes.Delivery(
            early.AttemptId, accountId, host.Token(accountId, signer: rotated)));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        host.Heimdall.Published.Add(rotated);
        host.Clock.Advance(TimeSpan.FromSeconds(30));
        var late = await host.StartAsync();
        using var accepted = await host.DeliverAsync(HeimdallShapes.Delivery(
            late.AttemptId, accountId, host.Token(accountId, signer: rotated)));

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
    }

    [Fact]
    public async Task Cached_keys_are_refetched_after_an_hour_and_a_withdrawn_key_stops_working()
    {
        using var host = new HeimdallHost();
        var warm = await host.StartAsync();
        var warmAccount = HeimdallShapes.NewAccountId();
        using (await DeliverGenuine(host, warm, warmAccount))
        {
        }

        host.Heimdall.Published.Clear();
        host.Clock.Advance(TimeSpan.FromHours(1) - TimeSpan.FromSeconds(1));
        var inside = await host.StartAsync();
        using (var stillCached = await DeliverGenuine(host, inside, HeimdallShapes.NewAccountId()))
        {
            Assert.Equal(HttpStatusCode.Accepted, stillCached.StatusCode);
        }

        Assert.Equal(1, host.Heimdall.JwksFetches);

        host.Clock.Advance(TimeSpan.FromSeconds(1));
        var outside = await host.StartAsync();
        using var withdrawn = await DeliverGenuine(host, outside, HeimdallShapes.NewAccountId());

        Assert.Equal(HttpStatusCode.BadRequest, withdrawn.StatusCode);
        Assert.Equal(2, host.Heimdall.JwksFetches);
    }

    [Fact]
    public async Task A_failing_key_fetch_refuses_without_hammering_and_recovers()
    {
        using var host = new HeimdallHost();
        host.Heimdall.JwksStatus = HttpStatusCode.InternalServerError;
        var accountId = HeimdallShapes.NewAccountId();

        var first = await host.StartAsync();
        using (var down = await DeliverGenuine(host, first, accountId))
        {
            Assert.Equal(HttpStatusCode.BadRequest, down.StatusCode);
            AssertTokenRefused(host, "Heimdall keys could not be fetched");
        }

        var second = await host.StartAsync();
        using (var stillDown = await DeliverGenuine(host, second, accountId))
        {
            Assert.Equal(HttpStatusCode.BadRequest, stillDown.StatusCode);
        }

        Assert.Equal(1, host.Heimdall.JwksFetches);

        host.Heimdall.JwksStatus = HttpStatusCode.OK;
        host.Clock.Advance(TimeSpan.FromSeconds(30));
        var third = await host.StartAsync();
        using var recovered = await DeliverGenuine(host, third, accountId);

        Assert.Equal(HttpStatusCode.Accepted, recovered.StatusCode);
    }

    [Theory]
    [InlineData("rsa-kty")]
    [InlineData("x25519-crv")]
    [InlineData("short-x")]
    public async Task A_published_key_that_is_not_a_full_ed25519_key_verifies_nothing(string junk)
    {
        using var host = new HeimdallHost();
        var kid = "ed25519-" + new string('a', 12);
        var x = WebEncoders.Base64UrlEncode(host.Key.PublicKey);
        host.Heimdall.ExtraJwks.Add(42);
        host.Heimdall.ExtraJwks.Add(junk switch
        {
            "rsa-kty" => new Dictionary<string, object?> { ["kty"] = "RSA", ["crv"] = "Ed25519", ["kid"] = kid, ["x"] = x },
            "x25519-crv" => new Dictionary<string, object?> { ["kty"] = "OKP", ["crv"] = "X25519", ["kid"] = kid, ["x"] = x },
            _ => new Dictionary<string, object?> { ["kty"] = "OKP", ["crv"] = "Ed25519", ["kid"] = kid, ["x"] = x[..20] }
        });
        var accountId = HeimdallShapes.NewAccountId();
        var attempt = await host.StartAsync();
        var token = host.Key.Sign(HeimdallShapes.AccessClaims(accountId, host.Clock.Now), kid: kid);

        using var delivery = await host.DeliverAsync(HeimdallShapes.Delivery(attempt.AttemptId, accountId, token));

        Assert.Equal(HttpStatusCode.BadRequest, delivery.StatusCode);
        var genuine = await host.StartAsync();
        using var accepted = await DeliverGenuine(host, genuine, accountId);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
    }

    [Fact]
    public async Task Capabilities_that_are_not_a_list_of_names_grant_nothing_and_do_not_crash()
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();
        var attempt = await host.StartAsync();
        var token = host.Token(accountId, mutate: claims => claims["capabilities"] = "member_access");

        using var delivery = await host.DeliverAsync(HeimdallShapes.Delivery(attempt.AttemptId, accountId, token));

        Assert.Equal(HttpStatusCode.Accepted, delivery.StatusCode);
        Assert.True((await host.WaitAsync(attempt.AttemptId, attempt.Binding)).SignedIn);
        Assert.Empty((await host.Account(accountId))!.Membership!.RoleAssignments);
    }

    [Fact]
    public async Task Member_access_is_found_among_other_and_non_string_capabilities()
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();
        var attempt = await host.StartAsync(member: true);
        var token = host.Token(accountId, mutate: claims =>
            claims["capabilities"] = new object?[] { 7, null, "billing_access", "member_access" });

        using var delivery = await host.DeliverAsync(HeimdallShapes.Delivery(attempt.AttemptId, accountId, token));

        Assert.Equal(HttpStatusCode.Accepted, delivery.StatusCode);
        Assert.True((await host.WaitAsync(attempt.AttemptId, attempt.Binding)).SignedIn);
        Assert.Contains((await host.Account(accountId))!.Membership!.RoleAssignments, x => x.Role == MemberRole.StandardMember);
    }

    [Theory]
    [InlineData(0L, 900L)]
    [InlineData(-1L, 253402300799L)]
    public async Task Numeric_dates_at_the_ends_of_the_valid_range_are_accepted(long notBefore, long expires)
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();
        var attempt = await host.StartAsync();
        var now = host.Clock.Now.ToUnixTimeSeconds();
        var token = host.Token(accountId, mutate: claims =>
        {
            claims["nbf"] = notBefore < 0 ? now : notBefore;
            claims["exp"] = expires == 900 ? now + 900 : expires;
        });

        using var delivery = await host.DeliverAsync(HeimdallShapes.Delivery(attempt.AttemptId, accountId, token));

        Assert.Equal(HttpStatusCode.Accepted, delivery.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{}", "Heimdall rejected the auth start.")]
    [InlineData(HttpStatusCode.OK, "{}", "Heimdall auth start returned no authorization URL.")]
    [InlineData(HttpStatusCode.OK, "{\"authorizationUrl\":\" \"}", "Heimdall auth start returned no authorization URL.")]
    public async Task A_start_Heimdall_cannot_serve_issues_no_attempt_and_no_cookie(HttpStatusCode status, string body, string message)
    {
        using var host = new HeimdallHost();
        host.Heimdall.StartStatus = status;
        host.Heimdall.StartBody = body;

        var attempt = await host.StartAsync();

        Assert.Equal(ErrorLocation(message), attempt.Response.Headers.Location!.OriginalString);
        Assert.Null(attempt.Binding);
        var wait = await host.WaitAsync(attempt.AttemptId, "anything");
        Assert.False(wait.SignedIn);
    }

    [Fact]
    public async Task Refusals_echo_no_input_in_responses_or_logs()
    {
        using var host = new HeimdallHost();
        var suffix = Guid.NewGuid().ToString("N");
        var attemptCanary = "CANARYATTEMPT" + suffix;
        var accountCanary = "CANARYACCOUNT" + suffix;
        var kidCanary = "CANARYKID" + suffix;
        var claimCanary = "CANARYCLAIM" + suffix;
        var providerCanary = "CANARYPROVIDER" + suffix;
        var bindingCanary = "CANARYBINDING" + suffix;
        var tokens = new List<string>();

        var canaryClaims = HeimdallShapes.AccessClaims(accountCanary, host.Clock.Now, mutate: claims =>
        {
            claims["iss"] = claimCanary;
            claims["aud"] = claimCanary;
            claims["typ"] = claimCanary;
            claims["display_name"] = claimCanary;
        });
        var issued = await host.StartAsync();
        var wrongSubject = host.Token(HeimdallShapes.NewAccountId());
        tokens.Add(wrongSubject);
        tokens.Add(HeimdallKey.Generate(kidCanary).Sign(canaryClaims, kid: kidCanary));
        tokens.Add(host.Key.Sign(canaryClaims));
        tokens.Add($"{claimCanary}.{claimCanary}.{claimCanary}");

        var responses = new List<HttpResponseMessage>();
        responses.Add(await host.DeliverAsync(HeimdallShapes.Delivery(attemptCanary, accountCanary, tokens[1])));
        responses.Add(await host.DeliverAsync(HeimdallShapes.Delivery(attemptCanary, accountCanary, tokens[2])));
        responses.Add(await host.DeliverAsync(HeimdallShapes.Delivery(attemptCanary, accountCanary, tokens[3])));
        responses.Add(await host.DeliverAsync(HeimdallShapes.Delivery(issued.AttemptId, accountCanary, wrongSubject)));
        responses.Add((await host.WaitAsync(attemptCanary, bindingCanary)).Response);
        responses.Add((await host.WaitAsync(issued.AttemptId, bindingCanary)).Response);
        using (var client = host.NewClient())
        {
            responses.Add(await client.GetAsync($"/auth/heimdall/{providerCanary}"));
            responses.Add(await client.PostAsync(
                "/auth/heimdall/callback",
                new StringContent("{\"attemptId\":\"" + attemptCanary + "\",", Encoding.UTF8, "application/json")));
        }

        var forbidden = new List<string> { attemptCanary, accountCanary, kidCanary, claimCanary, providerCanary, bindingCanary };
        forbidden.AddRange(tokens);
        forbidden.AddRange(tokens.SelectMany(x => x.Split('.')).Where(x => x.Length > 8));

        foreach (var response in responses)
        {
            var observed = new StringBuilder()
                .Append(await response.Content.ReadAsStringAsync())
                .Append(response.ToString());
            foreach (var cookie in response.Headers.Where(x => x.Key == "Set-Cookie").SelectMany(x => x.Value))
            {
                observed.Append(cookie);
            }

            foreach (var canary in forbidden)
            {
                Assert.DoesNotContain(canary, observed.ToString());
            }

            response.Dispose();
        }

        var logs = string.Join("\n", host.Logs.Lines);
        Assert.Contains("Heimdall callback refused", logs);
        foreach (var canary in forbidden)
        {
            Assert.DoesNotContain(canary, logs);
        }
    }
}
