using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Heimdall;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Features.Shared;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Tests;

/// <summary>
/// Sign-in through Heimdall's browser completion, against a local Bifrost with a test JWKS and a fake
/// Heimdall shaped like src/app.ts (start, redeem) and src/claims.ts. Forgeries differ from the genuine
/// shape in the one property under test.
/// </summary>
public sealed class HeimdallCompletionTests
{
    private const string ErrorRedirect = "/?auth=heimdall-error";

    private static string Flip(string value) => value[..^1] + (value[^1] == '0' ? '1' : '0');

    /// <summary>Changes one character in the middle of a protected value, which its authentication tag must notice.</summary>
    private static string Corrupt(string value) => value[..20] + (value[20] == 'A' ? 'B' : 'A') + value[21..];

    private static async Task<CompletionResult> Genuine(
        HeimdallHost host, StartedAttempt attempt, string accountId, string[]? capabilities = null, string? displayName = "Genuine Name") =>
        await host.CompleteAsync(attempt, host.CodeFor(attempt, accountId, host.Token(accountId, capabilities, displayName)));

    private static string CodeWith(
        HeimdallHost host, StartedAttempt attempt, string accountId, Action<Dictionary<string, object?>> edit, string? token = null)
    {
        var completion = HeimdallShapes.Completion(attempt.ReturnTo, accountId, token ?? host.Token(accountId));
        edit(completion);
        return host.Heimdall.RegisterCode(completion);
    }

    private static void AssertLogged(HeimdallHost host, string reason) =>
        Assert.Contains("Heimdall completion refused: " + reason, string.Join("\n", host.Logs.Lines));

    private static void AssertTokenRefused(HeimdallHost host, string rule) =>
        AssertLogged(host, $"Heimdall token refused: {rule}.");

    private static void AssertRefusedRedirect(CompletionResult result)
    {
        Assert.Equal(HttpStatusCode.Redirect, result.Response.StatusCode);
        Assert.Equal(ErrorRedirect, result.Response.Headers.Location!.OriginalString);
        Assert.False(result.SignedIn);
    }

    private static async Task AssertRefused(HeimdallHost host, string accountId, CompletionResult result)
    {
        AssertRefusedRedirect(result);
        Assert.Null(await host.Account(accountId));
    }

    [Fact]
    public async Task Genuine_completion_signs_in_once_in_the_starting_browser()
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();
        var attempt = await host.StartAsync(returnUrl: "/App/Mill");
        var code = host.CodeFor(attempt, accountId);

        var done = await host.CompleteAsync(attempt, code);

        Assert.True(done.SignedIn);
        Assert.Equal(HttpStatusCode.Redirect, done.Response.StatusCode);
        Assert.Equal("/App/Mill", done.Response.Headers.Location!.OriginalString);
        var account = await host.Account(accountId);
        Assert.NotNull(account);
        Assert.Equal("Genuine Name", account!.DisplayName);
        Assert.Equal(MembershipStatus.Authenticated, account.Membership!.Status);
        Assert.Empty(account.Membership.RoleAssignments);
        var cleared = HeimdallHost.SetCookieHeader(done.Response, attempt.CookieName!);
        Assert.NotNull(cleared);
        Assert.Contains("1970", cleared);

        var again = await host.CompleteAsync(attempt, code);

        AssertRefusedRedirect(again);
        Assert.Equal(2, host.Heimdall.RedeemCalls);
        AssertLogged(host, "Heimdall did not redeem the completion code.");
    }

    [Fact]
    public async Task The_display_name_is_the_claim_and_falls_back_to_the_account_id()
    {
        using var host = new HeimdallHost();
        var named = HeimdallShapes.NewAccountId();
        var unnamed = HeimdallShapes.NewAccountId();

        Assert.True((await Genuine(host, await host.StartAsync(), named, displayName: "Claimed Name")).SignedIn);
        Assert.True((await Genuine(host, await host.StartAsync(), unnamed, displayName: null)).SignedIn);

        Assert.Equal("Claimed Name", (await host.Account(named))!.DisplayName);
        Assert.Equal(unnamed, (await host.Account(unnamed))!.DisplayName);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_completion_without_an_access_token_signs_nobody_in(string token)
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();
        var attempt = await host.StartAsync();
        var code = CodeWith(host, attempt, accountId, completion =>
        {
            if (token == "absent")
            {
                completion.Remove("accessToken");
            }
            else
            {
                completion["accessToken"] = token;
            }
        });

        var result = await host.CompleteAsync(attempt, code);

        await AssertRefused(host, accountId, result);
        AssertLogged(host, "Heimdall completion carried no access token.");
    }

    [Theory]
    [InlineData(HttpStatusCode.Gone, "{}", "Heimdall did not redeem the completion code.")]
    [InlineData(HttpStatusCode.InternalServerError, "{}", "Heimdall did not redeem the completion code.")]
    [InlineData(HttpStatusCode.OK, "valid", "Heimdall did not redeem the completion code.")]
    [InlineData(HttpStatusCode.Created, "not json", "Heimdall redeem could not be read.")]
    [InlineData(HttpStatusCode.Created, "[1]", "Heimdall redeem could not be read.")]
    [InlineData(HttpStatusCode.Created, "null", "Heimdall redeem returned no body.")]
    public async Task A_redeem_that_is_not_a_201_with_a_readable_body_signs_nobody_in(HttpStatusCode status, string body, string reason)
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();
        var attempt = await host.StartAsync();
        host.Heimdall.RedeemStatus = status;
        host.Heimdall.RedeemBody = body == "valid"
            ? JsonSerializer.Serialize(HeimdallShapes.Completion(attempt.ReturnTo, accountId, host.Token(accountId)))
            : body;

        var result = await host.CompleteAsync(attempt, "a-code");

        await AssertRefused(host, accountId, result);
        AssertLogged(host, reason);
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

        var result = await host.CompleteAsync(attempt, host.CodeFor(attempt, accountId, token));

        await AssertRefused(host, accountId, result);
        AssertTokenRefused(host, rule);
    }

    [Fact]
    public async Task A_forged_token_cannot_sign_in_as_an_existing_account()
    {
        using var host = new HeimdallHost();
        var victim = HeimdallShapes.NewAccountId();
        Assert.True((await Genuine(host, await host.StartAsync(), victim, displayName: "Victim Name")).SignedIn);

        var attempt = await host.StartAsync();
        var forged = HeimdallKey.Generate(host.Key.Kid).Sign(HeimdallShapes.AccessClaims(victim, host.Clock.Now, displayName: "Mallory"));
        var result = await host.CompleteAsync(attempt, host.CodeFor(attempt, victim, forged));

        AssertRefusedRedirect(result);
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

        var result = await host.CompleteAsync(attempt, host.CodeFor(attempt, accountId, token));

        await AssertRefused(host, accountId, result);
        AssertTokenRefused(host, rule);
    }

    [Fact]
    public async Task A_token_for_a_different_account_than_the_redeemed_one_is_refused_by_one_character()
    {
        using var host = new HeimdallHost();
        var tokenAccount = HeimdallShapes.NewAccountId();
        var redeemedAccount = Flip(tokenAccount);
        var attempt = await host.StartAsync();

        var result = await host.CompleteAsync(attempt, host.CodeFor(attempt, redeemedAccount, host.Token(tokenAccount)));

        AssertRefusedRedirect(result);
        Assert.Null(await host.Account(tokenAccount));
        Assert.Null(await host.Account(redeemedAccount));
        AssertTokenRefused(host, "token subject does not match the account");
    }

    [Fact]
    public async Task A_redeem_response_with_no_account_is_refused()
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();
        var attempt = await host.StartAsync();
        var code = CodeWith(host, attempt, accountId, completion => completion["account"] = null);

        var result = await host.CompleteAsync(attempt, code);

        await AssertRefused(host, accountId, result);
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

        var result = await host.CompleteAsync(attempt, host.CodeFor(attempt, accountId, token));

        Assert.Equal(accepted, result.SignedIn);
    }

    [Fact]
    public async Task A_consent_link_completed_in_another_browser_signs_nobody_in()
    {
        using var host = new HeimdallHost();
        var victim = HeimdallShapes.NewAccountId();
        var attacker = await host.StartAsync();
        var victimsCode = host.CodeFor(attacker, victim);

        var swap = await host.CompleteAsync(attacker.Nonce, victimsCode, null);

        AssertRefusedRedirect(swap);
        Assert.Null(await host.Account(victim));
        Assert.Equal(0, host.Heimdall.RedeemCalls);
        AssertLogged(host, "Heimdall completion has no attempt for this browser.");

        var guess = await host.CompleteAsync(attacker.Nonce, "0123456789abcdef0123456789abcdef", attacker.Cookie);

        AssertRefusedRedirect(guess);
        Assert.Equal(1, host.Heimdall.RedeemCalls);
        Assert.Null(await host.Account(victim));
    }

    [Fact]
    public async Task An_attackers_code_in_a_victims_attempt_signs_nobody_in()
    {
        using var host = new HeimdallHost();
        var attackerAccount = HeimdallShapes.NewAccountId();
        var victimAttempt = await host.StartAsync();
        var attackerAttempt = await host.StartAsync();
        var attackersCode = host.CodeFor(attackerAttempt, attackerAccount);

        var forced = await host.CompleteAsync(victimAttempt.Nonce, attackersCode, victimAttempt.Cookie);

        AssertRefusedRedirect(forced);
        Assert.Null(await host.Account(attackerAccount));
        Assert.Equal(1, host.Heimdall.RedeemCalls);
        AssertLogged(host, "Heimdall completion belongs to another attempt.");

        var crossed = await host.CompleteAsync(attackerAttempt.Nonce, attackersCode, victimAttempt.Cookie);

        AssertRefusedRedirect(crossed);
        Assert.Equal(1, host.Heimdall.RedeemCalls);
    }

    [Theory]
    [InlineData("nonce-flipped")]
    [InlineData("trailing-slash")]
    [InlineData("other-host")]
    [InlineData("other-scheme")]
    [InlineData("other-path")]
    [InlineData("extra-query")]
    [InlineData("empty")]
    public async Task A_redeemed_return_to_that_differs_by_one_character_is_refused(string difference)
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();
        var attempt = await host.StartAsync();
        var returnTo = difference switch
        {
            "nonce-flipped" => Flip(attempt.ReturnTo),
            "trailing-slash" => attempt.ReturnTo + "/",
            "other-host" => attempt.ReturnTo.Replace("https://localhost", "https://localhost.evil.example"),
            "other-scheme" => attempt.ReturnTo.Replace("https://", "http://"),
            "other-path" => attempt.ReturnTo.Replace("/complete", "/completed"),
            "extra-query" => attempt.ReturnTo + "&x=1",
            _ => string.Empty
        };
        Assert.NotEqual(attempt.ReturnTo, returnTo);

        var result = await host.CompleteAsync(attempt, host.CodeFor(attempt, accountId, returnTo: returnTo));

        await AssertRefused(host, accountId, result);
        Assert.Equal(1, host.Heimdall.RedeemCalls);
        AssertLogged(host, "Heimdall completion belongs to another attempt.");
    }

    [Fact]
    public async Task An_attempt_cookie_that_is_tampered_renamed_or_expired_is_no_attempt()
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();
        var attempt = await host.StartAsync();
        var other = await host.StartAsync();
        var onTime = await host.StartAsync();
        var atExpiry = await host.StartAsync();
        var afterExpiry = await host.StartAsync();
        var code = host.CodeFor(attempt, accountId);
        var wrong = new Dictionary<string, string?>
        {
            ["empty cookie"] = string.Empty,
            ["another attempt's cookie under this name"] = other.Cookie,
            ["a corrupted byte"] = Corrupt(attempt.Cookie!),
            ["a truncated value"] = attempt.Cookie![..^8],
            ["a value with a suffix"] = attempt.Cookie + "A",
            ["the nonce as the value"] = attempt.Nonce,
            ["a same-shape random value"] = WebEncoders.Base64UrlEncode(new byte[attempt.Cookie.Length * 3 / 4])
        };

        foreach (var (name, cookie) in wrong)
        {
            var refused = await host.CompleteAsync(attempt.Nonce, code, cookie);
            AssertRefusedRedirect(refused);
            Assert.True(refused.Response.Headers.Contains("Set-Cookie"), name);
        }

        Assert.Equal(0, host.Heimdall.RedeemCalls);
        Assert.Null(await host.Account(accountId));

        host.Clock.Advance(HeimdallAttemptCookie.Lifetime - TimeSpan.FromSeconds(1));
        var accepted = await host.CompleteAsync(onTime, host.CodeFor(onTime, accountId));
        Assert.True(accepted.SignedIn);
        Assert.Equal(1, host.Heimdall.RedeemCalls);

        host.Clock.Advance(TimeSpan.FromSeconds(1));
        var expiring = await host.CompleteAsync(atExpiry, host.CodeFor(atExpiry, accountId));
        AssertRefusedRedirect(expiring);

        host.Clock.Advance(TimeSpan.FromSeconds(1));
        var expired = await host.CompleteAsync(afterExpiry, host.CodeFor(afterExpiry, accountId));
        AssertRefusedRedirect(expired);
        Assert.Equal(1, host.Heimdall.RedeemCalls);
    }

    [Fact]
    public async Task Anonymous_starts_cannot_displace_a_pending_sign_in()
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();
        var genuine = await host.StartAsync();

        using (var client = host.NewClient())
        {
            for (var i = 0; i < 5000; i++)
            {
                using var response = await client.GetAsync("/auth/heimdall/discord");
                Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            }
        }

        Assert.True((await Genuine(host, genuine, accountId)).SignedIn);
    }

    [Fact]
    public async Task Two_attempts_in_one_browser_both_complete()
    {
        using var host = new HeimdallHost();
        var first = HeimdallShapes.NewAccountId();
        var second = HeimdallShapes.NewAccountId();
        var attemptOne = await host.StartAsync(returnUrl: "/App/One");
        var attemptTwo = await host.StartAsync(returnUrl: "/App/Two");

        var doneTwo = await Genuine(host, attemptTwo, second);
        var doneOne = await Genuine(host, attemptOne, first);

        Assert.True(doneTwo.SignedIn);
        Assert.Equal("/App/Two", doneTwo.Response.Headers.Location!.OriginalString);
        Assert.True(doneOne.SignedIn);
        Assert.Equal("/App/One", doneOne.Response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Start_sets_a_private_per_attempt_cookie_that_reveals_nothing()
    {
        using var host = new HeimdallHost();
        var first = await host.StartAsync(returnUrl: "/App/Mill");
        var second = await host.StartAsync(returnUrl: "/App/Mill");

        Assert.Equal(HttpStatusCode.Redirect, first.Response.StatusCode);
        Assert.Equal("https://heimdall.test/oauth/discord/authorize?state=fixture", first.Response.Headers.Location!.OriginalString);
        Assert.Equal(43, first.Nonce.Length);
        Assert.NotEqual(first.Nonce, second.Nonce);
        var header = HeimdallHost.SetCookieHeader(first.Response, first.CookieName!);
        Assert.NotNull(header);
        var attributes = header!.Split(';').Select(x => x.Trim().ToLowerInvariant()).ToArray();
        Assert.Contains("httponly", attributes);
        Assert.Contains("secure", attributes);
        Assert.Contains("samesite=lax", attributes);
        Assert.Contains("path=/auth/heimdall", attributes);
        Assert.Contains("max-age=600", attributes);
        Assert.NotEqual(first.Cookie, second.Cookie);
        Assert.DoesNotContain(first.Nonce, first.Cookie!);
        Assert.DoesNotContain("Mill", first.Cookie!);
        Assert.DoesNotContain("Mill", Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(first.Cookie!)));

        using var body = JsonDocument.Parse(host.Heimdall.StartBodies[0]);
        var handoff = body.RootElement.GetProperty("handoff");
        Assert.Equal("browser_completion", handoff.GetProperty("kind").GetString());
        Assert.Single(handoff.EnumerateObject());
        Assert.Equal($"https://localhost/auth/heimdall/complete?attempt={first.Nonce}", body.RootElement.GetProperty("returnTo").GetString());
        Assert.Equal("bifrost", body.RootElement.GetProperty("appSlug").GetString());
        Assert.Equal("sign_in", body.RootElement.GetProperty("mode").GetString());
        Assert.False(body.RootElement.TryGetProperty("entitlementPolicy", out _));
    }

    [Fact]
    public async Task The_complete_page_holds_no_code_and_leaks_no_referrer()
    {
        using var host = new HeimdallHost();
        using var client = host.NewClient();

        using var response = await client.GetAsync("/auth/heimdall/complete?attempt=CANARYATTEMPT&code=CANARYCODE");
        var page = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Contains("location.hash", page);
        Assert.Contains("heimdall_completion_code", page);
        Assert.Contains("history.replaceState", page);
        Assert.Contains("form.submit()", page);
        Assert.Contains("method=\"post\" action=\"/auth/heimdall/complete\"", page);
        Assert.Contains("<noscript>", page);
        Assert.Contains("/?auth=heimdall-error", page);
        Assert.DoesNotContain("CANARY", page);
        Assert.Null(HeimdallHost.CookieValue(response, ".AspNetCore.Cookies"));
    }

    [Theory]
    [InlineData("https://evil.example", "/App")]
    [InlineData("//evil.example", "/App")]
    [InlineData("/\\evil.example", "/App")]
    [InlineData("javascript:alert(1)", "/App")]
    [InlineData("http:/evil", "/App")]
    [InlineData("evil", "/App")]
    [InlineData("", "/App")]
    [InlineData("/App/\u0001Mill", "/App")]
    [InlineData("/App/Mill\r\nX: y", "/App")]
    [InlineData("/App/Mill?x=1", "/App/Mill?x=1")]
    [InlineData("/", "/")]
    [InlineData("/a/b", "/a/b")]
    public async Task Return_targets_are_local_paths_only(string returnUrl, string expected)
    {
        using var host = new HeimdallHost();

        var direct = await host.StartAsync(returnUrl: returnUrl);
        var signedIn = await Genuine(host, direct, HeimdallShapes.NewAccountId());
        Assert.True(signedIn.SignedIn);
        Assert.Equal(expected, signedIn.Response.Headers.Location!.OriginalString);

        using var client = host.NewClient();
        using var chooserRedirect = await client.GetAsync(
            $"/auth/sign-in?provider=discord&returnUrl={Uri.EscapeDataString(returnUrl)}");
        Assert.Equal(
            "/auth/heimdall/discord?returnUrl=" + Uri.EscapeDataString(expected),
            chooserRedirect.Headers.Location!.OriginalString);
        var viaSignIn = await host.StartAtAsync(chooserRedirect.Headers.Location.OriginalString);
        var signedInViaChooser = await Genuine(host, viaSignIn, HeimdallShapes.NewAccountId());
        Assert.Equal(expected, signedInViaChooser.Response.Headers.Location!.OriginalString);

        using var chooser = await client.GetAsync($"/auth/sign-in?returnUrl={Uri.EscapeDataString(returnUrl)}");
        var page = await chooser.Content.ReadAsStringAsync();
        Assert.Contains("returnUrl=" + Uri.EscapeDataString(expected), page);
        Assert.DoesNotContain("evil", page);

        using var github = await client.GetAsync(
            $"/auth/sign-in?provider=github&returnUrl={Uri.EscapeDataString(returnUrl)}");
        var state = QueryHelpers.ParseQuery(github.Headers.Location!.Query)["state"].ToString();
        var gitHub = host.Services.GetRequiredService<IOptionsMonitor<OAuthOptions>>().Get(GitHubAuthenticationDefaults.AuthenticationScheme);
        Assert.Equal(expected, gitHub.StateDataFormat.Unprotect(state)!.RedirectUri);
    }

    [Fact]
    public async Task Member_starts_authenticate_as_the_app()
    {
        using var host = new HeimdallHost();
        var plain = await host.StartAsync();
        var member = await host.StartAsync(member: true);
        var patreon = await host.StartAsync(member: true, provider: "patreon");

        Assert.Equal(HeimdallHost.AppSecret, host.Heimdall.StartSecrets[0]);
        Assert.Equal(HeimdallHost.AppSecret, host.Heimdall.StartSecrets[1]);
        Assert.Equal(HeimdallHost.AppSecret, host.Heimdall.StartSecrets[2]);
        Assert.NotNull(plain.Cookie);
        Assert.NotNull(member.Cookie);
        Assert.NotNull(patreon.Cookie);
        using var memberBody = JsonDocument.Parse(host.Heimdall.StartBodies[1]);
        Assert.Equal("discord_role_access", memberBody.RootElement.GetProperty("entitlementPolicy").GetProperty("kind").GetString());

        host.Heimdall.RequiredAppSecret = "a-different-secret";
        var refused = await host.StartAsync(member: true);

        Assert.Equal(ErrorRedirect, refused.Response.Headers.Location!.OriginalString);
        Assert.Null(refused.Cookie);
    }

    [Theory]
    [InlineData("discord")]
    [InlineData("patreon")]
    public async Task A_member_start_without_the_app_secret_is_refused_before_Heimdall_is_called(string provider)
    {
        using var host = new HeimdallHost(appSecret: string.Empty);

        var member = await host.StartAsync(member: true, provider: provider);

        Assert.Equal(ErrorRedirect, member.Response.Headers.Location!.OriginalString);
        Assert.Empty(host.Heimdall.StartBodies);
        Assert.False(member.Response.Headers.Contains("Set-Cookie"));

        var plain = await host.StartAsync();
        Assert.NotNull(plain.Cookie);
        Assert.Null(host.Heimdall.StartSecrets[0]);
    }

    [Theory]
    [InlineData("https://localhost", "https://localhost/auth/heimdall/complete?attempt=")]
    [InlineData("https://localhost/", "https://localhost/auth/heimdall/complete?attempt=")]
    [InlineData("https://localhost/sub", "https://localhost/sub/auth/heimdall/complete?attempt=")]
    [InlineData("https://localhost/sub/", "https://localhost/sub/auth/heimdall/complete?attempt=")]
    public async Task The_return_target_Heimdall_gets_is_under_the_public_base_url(string publicBaseUrl, string prefix)
    {
        using var host = new HeimdallHost(publicBaseUrl: publicBaseUrl);
        var attempt = await host.StartAsync();

        Assert.Equal(prefix + attempt.Nonce, attempt.ReturnTo);
        Assert.True((await Genuine(host, attempt, HeimdallShapes.NewAccountId())).SignedIn);
    }

    [Fact]
    public async Task A_member_start_with_no_allowed_discord_roles_is_refused_before_Heimdall_is_called()
    {
        using var host = new HeimdallHost(discordRoles: false);

        var member = await host.StartAsync(member: true);

        Assert.Equal(ErrorRedirect, member.Response.Headers.Location!.OriginalString);
        Assert.Empty(host.Heimdall.StartBodies);
        Assert.NotNull((await host.StartAsync()).Cookie);
    }

    [Fact]
    public async Task Every_completion_refusal_looks_the_same()
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();
        var attempt = await host.StartAsync();
        var memberAttempt = await host.StartAsync(member: true);
        var other = await host.StartAsync();
        var forged = HeimdallKey.Generate(host.Key.Kid).Sign(HeimdallShapes.AccessClaims(accountId, host.Clock.Now));
        var results = new List<(string Name, CompletionResult Result, string Reason)>
        {
            ("no cookie", await host.CompleteAsync(attempt.Nonce, host.CodeFor(attempt, accountId), null),
                "Heimdall completion has no attempt for this browser."),
            ("bad cookie", await host.CompleteAsync(attempt.Nonce, host.CodeFor(attempt, accountId), Corrupt(attempt.Cookie!)),
                "Heimdall completion has no attempt for this browser."),
            ("no attempt field", await host.CompleteAsync(null, "code", attempt.Cookie),
                "Heimdall completion has no attempt for this browser."),
            ("malformed attempt", await host.CompleteAsync("short", "code", attempt.Cookie, "bifrost_heimdall_short"),
                "Heimdall completion has no attempt for this browser."),
            ("no code", await host.CompleteAsync(attempt.Nonce, null, attempt.Cookie), "Heimdall completion carried no code."),
            ("blank code", await host.CompleteAsync(attempt.Nonce, "  ", attempt.Cookie), "Heimdall completion carried no code."),
            ("unknown code", await host.CompleteAsync(attempt.Nonce, "unknown", attempt.Cookie),
                "Heimdall did not redeem the completion code."),
            ("returnTo mismatch", await host.CompleteAsync(other.Nonce, host.CodeFor(attempt, accountId), other.Cookie),
                "Heimdall completion belongs to another attempt."),
            ("forged token", await host.CompleteAsync(attempt, host.CodeFor(attempt, accountId, forged)),
                "Heimdall token refused: token signature is invalid."),
            ("no member access", await host.CompleteAsync(memberAttempt, host.CodeFor(memberAttempt, accountId)),
                "Heimdall did not grant Bifrost member access.")
        };

        var illFormed = await host.CompleteAsync(new string('A', 42) + "!", "code", "x");
        results.Add(("ill-formed nonce", illFormed, "Heimdall completion has no attempt for this browser."));

        using var client = host.NewClient();
        using var json = await client.PostAsync(
            "/auth/heimdall/complete",
            new StringContent("{\"attempt\":\"x\"}", Encoding.UTF8, "application/json"));
        results.Add(("json body", new CompletionResult(json, false), "Heimdall completion has no attempt for this browser."));
        using var malformed = await client.PostAsync(
            "/auth/heimdall/complete",
            new StringContent("not a multipart body", Encoding.UTF8, "multipart/form-data"));
        results.Add(("malformed form", new CompletionResult(malformed, false), "Heimdall completion has no attempt for this browser."));

        foreach (var (name, result, reason) in results)
        {
            AssertRefusedRedirect(result);
            Assert.Equal(string.Empty, await result.Response.Content.ReadAsStringAsync());
            Assert.Null(HeimdallHost.CookieValue(result.Response, ".AspNetCore.Cookies"));
            AssertLogged(host, reason);
            Assert.True(result.Response.Headers.Location!.OriginalString == ErrorRedirect, name);
        }

        Assert.Null(await host.Account(accountId));
        Assert.False(illFormed.Response.Headers.Contains("Set-Cookie"));

        foreach (var url in new[] { "/auth/heimdall/bogus", "/auth/heimdall/twitch?access=member" })
        {
            using var start = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
            Assert.Equal(ErrorRedirect, start.Headers.Location!.OriginalString);
            Assert.Equal(string.Empty, await start.Content.ReadAsStringAsync());
        }

        Assert.Contains("Heimdall start refused: Unsupported Heimdall provider.", string.Join("\n", host.Logs.Lines));
        Assert.Contains("Heimdall start refused: Heimdall provider does not support Bifrost member access.", string.Join("\n", host.Logs.Lines));
    }

    [Fact]
    public async Task A_base_url_with_a_trailing_slash_matches_Heimdalls_issuer()
    {
        using var host = new HeimdallHost(baseUrl: HeimdallShapes.Issuer + "/");
        var accountId = HeimdallShapes.NewAccountId();

        var result = await Genuine(host, await host.StartAsync(), accountId);

        Assert.True(result.SignedIn);
        Assert.NotNull(await host.Account(accountId));
    }

    [Fact]
    public async Task A_slow_key_fetch_does_not_hold_up_cached_keys()
    {
        using var host = new HeimdallHost();
        Assert.True((await Genuine(host, await host.StartAsync(), HeimdallShapes.NewAccountId())).SignedIn);
        host.Clock.Advance(TimeSpan.FromSeconds(31));
        var unknown = await host.StartAsync();
        var unknownAccount = HeimdallShapes.NewAccountId();
        var unknownCode = host.CodeFor(unknown, unknownAccount, host.Token(unknownAccount, signer: HeimdallKey.Generate()));
        var release = host.Heimdall.HoldJwks();

        var slow = host.CompleteAsync(unknown, unknownCode);
        await host.Heimdall.JwksArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var cachedAccount = HeimdallShapes.NewAccountId();
        var cached = await Genuine(host, await host.StartAsync(), cachedAccount).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(cached.SignedIn);
        Assert.False(slow.IsCompleted);
        release.SetResult();
        AssertRefusedRedirect(await slow);
        Assert.Equal(2, host.Heimdall.JwksFetches);
    }

    [Fact]
    public void The_key_fetch_client_gives_up_after_ten_seconds()
    {
        using var host = new HeimdallHost();

        var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient(HeimdallTokenVerifier.ClientName);

        Assert.Equal(TimeSpan.FromSeconds(10), client.Timeout);
    }

    [Fact]
    public async Task A_key_fetch_that_times_out_refuses_the_sign_in()
    {
        using var host = new HeimdallHost();
        host.Heimdall.JwksFailure = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
        var accountId = HeimdallShapes.NewAccountId();

        var result = await Genuine(host, await host.StartAsync(), accountId);

        await AssertRefused(host, accountId, result);
        AssertTokenRefused(host, "Heimdall keys could not be fetched");
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
        var code = host.CodeFor(
            attempt,
            accountId,
            host.Token(accountId, [capability]),
            looseCapabilities: grants ? [] : ["member_access", "app_access"],
            looseFacts: grants ? [] : ["entitlement.app_access"]);

        var result = await host.CompleteAsync(attempt, code);

        var account = await host.Account(accountId);
        if (grants)
        {
            Assert.True(result.SignedIn);
            Assert.Equal(MembershipStatus.Active, account!.Membership!.Status);
            Assert.Contains(account.Membership.RoleAssignments, x => x.Role == MemberRole.StandardMember);
        }
        else
        {
            AssertRefusedRedirect(result);
            Assert.Null(account);
            AssertLogged(host, "Heimdall did not grant Bifrost member access.");
        }
    }

    [Fact]
    public async Task A_token_capability_grants_no_role_to_a_sign_in_that_did_not_ask_for_member_access()
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();

        Assert.True((await Genuine(host, await host.StartAsync(member: false), accountId, ["member_access"])).SignedIn);

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
        var code = host.CodeFor(
            attempt, accountId, looseCapabilities: ["member_access"], looseFacts: ["entitlement.app_access"]);

        Assert.True((await host.CompleteAsync(attempt, code)).SignedIn);

        var account = await host.Account(accountId);
        Assert.Equal(MembershipStatus.Authenticated, account!.Membership!.Status);
        Assert.Empty(account.Membership.RoleAssignments);
    }

    [Fact]
    public async Task The_loose_name_decides_nothing()
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();
        var attempt = await host.StartAsync(returnUrl: "/App/Mill");

        var result = await host.CompleteAsync(attempt, host.CodeFor(attempt, accountId, looseName: "Loose Name"));

        Assert.Equal("/App/Mill", result.Response.Headers.Location!.OriginalString);
        Assert.Equal("Genuine Name", (await host.Account(accountId))!.DisplayName);
    }

    [Fact]
    public async Task Keys_are_fetched_once_and_cached()
    {
        using var host = new HeimdallHost();
        for (var i = 0; i < 3; i++)
        {
            Assert.True((await Genuine(host, await host.StartAsync(), HeimdallShapes.NewAccountId())).SignedIn);
        }

        Assert.Equal(1, host.Heimdall.JwksFetches);
    }

    [Fact]
    public async Task An_unknown_key_id_refetches_at_most_every_thirty_seconds()
    {
        using var host = new HeimdallHost();
        var unknown = HeimdallKey.Generate();
        async Task<bool> CompleteUnknown()
        {
            var accountId = HeimdallShapes.NewAccountId();
            var attempt = await host.StartAsync();
            return (await host.CompleteAsync(attempt, host.CodeFor(attempt, accountId, host.Token(accountId, signer: unknown)))).SignedIn;
        }

        Assert.False(await CompleteUnknown());
        Assert.Equal(1, host.Heimdall.JwksFetches);

        host.Clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(await CompleteUnknown());
        Assert.Equal(1, host.Heimdall.JwksFetches);

        host.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(await CompleteUnknown());
        Assert.Equal(2, host.Heimdall.JwksFetches);
        Assert.False(await CompleteUnknown());
        Assert.Equal(2, host.Heimdall.JwksFetches);
    }

    [Fact]
    public async Task A_rotated_in_key_is_accepted_once_the_refetch_interval_has_passed()
    {
        using var host = new HeimdallHost();
        var rotated = HeimdallKey.Generate();
        var accountId = HeimdallShapes.NewAccountId();

        var early = await host.StartAsync();
        var refused = await host.CompleteAsync(early, host.CodeFor(early, accountId, host.Token(accountId, signer: rotated)));
        Assert.False(refused.SignedIn);

        host.Heimdall.Published.Add(rotated);
        host.Clock.Advance(TimeSpan.FromSeconds(30));
        var late = await host.StartAsync();
        var accepted = await host.CompleteAsync(late, host.CodeFor(late, accountId, host.Token(accountId, signer: rotated)));

        Assert.True(accepted.SignedIn);
    }

    [Fact]
    public async Task Cached_keys_are_refetched_after_an_hour_and_a_withdrawn_key_stops_working()
    {
        using var host = new HeimdallHost();
        Assert.True((await Genuine(host, await host.StartAsync(), HeimdallShapes.NewAccountId())).SignedIn);

        host.Heimdall.Published.Clear();
        host.Clock.Advance(TimeSpan.FromHours(1) - TimeSpan.FromSeconds(1));
        Assert.True((await Genuine(host, await host.StartAsync(), HeimdallShapes.NewAccountId())).SignedIn);
        Assert.Equal(1, host.Heimdall.JwksFetches);

        host.Clock.Advance(TimeSpan.FromSeconds(1));
        var withdrawn = await Genuine(host, await host.StartAsync(), HeimdallShapes.NewAccountId());

        Assert.False(withdrawn.SignedIn);
        Assert.Equal(2, host.Heimdall.JwksFetches);
    }

    [Fact]
    public async Task A_failing_key_fetch_refuses_without_hammering_and_recovers()
    {
        using var host = new HeimdallHost();
        host.Heimdall.JwksStatus = HttpStatusCode.InternalServerError;
        var accountId = HeimdallShapes.NewAccountId();

        var down = await Genuine(host, await host.StartAsync(), accountId);
        AssertRefusedRedirect(down);
        AssertTokenRefused(host, "Heimdall keys could not be fetched");
        Assert.False((await Genuine(host, await host.StartAsync(), accountId)).SignedIn);
        Assert.Equal(1, host.Heimdall.JwksFetches);

        host.Heimdall.JwksStatus = HttpStatusCode.OK;
        host.Clock.Advance(TimeSpan.FromSeconds(30));

        Assert.True((await Genuine(host, await host.StartAsync(), accountId)).SignedIn);
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

        var refused = await host.CompleteAsync(attempt, host.CodeFor(attempt, accountId, token));

        Assert.False(refused.SignedIn);
        Assert.True((await Genuine(host, await host.StartAsync(), accountId)).SignedIn);
    }

    [Fact]
    public async Task Capabilities_that_are_not_a_list_of_names_grant_nothing_and_do_not_crash()
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();
        var attempt = await host.StartAsync();
        var token = host.Token(accountId, mutate: claims => claims["capabilities"] = "member_access");

        var result = await host.CompleteAsync(attempt, host.CodeFor(attempt, accountId, token));

        Assert.True(result.SignedIn);
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

        var result = await host.CompleteAsync(attempt, host.CodeFor(attempt, accountId, token));

        Assert.True(result.SignedIn);
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

        var result = await host.CompleteAsync(attempt, host.CodeFor(attempt, accountId, token));

        Assert.True(result.SignedIn);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{}", "Heimdall rejected the auth start.")]
    [InlineData(HttpStatusCode.OK, "{}", "Heimdall auth start returned no authorization URL.")]
    [InlineData(HttpStatusCode.OK, "{\"authorizationUrl\":\" \"}", "Heimdall auth start returned no authorization URL.")]
    public async Task A_start_Heimdall_cannot_serve_issues_no_cookie_and_a_fixed_refusal(HttpStatusCode status, string body, string reason)
    {
        using var host = new HeimdallHost();
        host.Heimdall.StartStatus = status;
        host.Heimdall.StartBody = body;

        var attempt = await host.StartAsync();

        Assert.Equal(ErrorRedirect, attempt.Response.Headers.Location!.OriginalString);
        Assert.Null(attempt.Cookie);
        Assert.False(attempt.Response.Headers.Contains("Set-Cookie"));
        Assert.Contains("Heimdall start refused: " + reason, string.Join("\n", host.Logs.Lines));
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
        var cookieCanary = "CANARYCOOKIE" + suffix;
        var codeCanary = "CANARYCODE" + suffix;
        var returnToCanary = "https://CANARYRETURN" + suffix + ".example/";
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
        foreach (var token in tokens.Skip(1))
        {
            responses.Add((await host.CompleteAsync(issued, host.CodeFor(issued, accountCanary, token))).Response);
        }

        responses.Add((await host.CompleteAsync(issued, host.CodeFor(issued, accountCanary, wrongSubject))).Response);
        responses.Add((await host.CompleteAsync(issued, host.CodeFor(issued, accountCanary, returnTo: returnToCanary))).Response);
        responses.Add((await host.CompleteAsync(issued.Nonce, codeCanary, cookieCanary)).Response);
        responses.Add((await host.CompleteAsync(attemptCanary, codeCanary, cookieCanary, "bifrost_heimdall_" + attemptCanary)).Response);
        responses.Add((await host.CompleteAsync(issued.Nonce, codeCanary, issued.Cookie)).Response);
        using (var client = host.NewClient())
        {
            responses.Add(await client.GetAsync($"/auth/heimdall/{providerCanary}"));
            responses.Add(await client.PostAsync(
                "/auth/heimdall/complete",
                new StringContent("{\"attempt\":\"" + attemptCanary + "\",", Encoding.UTF8, "application/json")));
        }

        var forbidden = new List<string>
        {
            attemptCanary, accountCanary, kidCanary, claimCanary, providerCanary, cookieCanary, codeCanary, "CANARYRETURN" + suffix
        };
        forbidden.AddRange(tokens);
        forbidden.AddRange(tokens.SelectMany(x => x.Split('.')).Where(x => x.Length > 8));
        forbidden.Add(issued.Cookie!);

        foreach (var response in responses)
        {
            var observed = new StringBuilder()
                .Append(await response.Content.ReadAsStringAsync())
                .Append(response);
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
        Assert.Contains("Heimdall completion refused", logs);
        foreach (var canary in forbidden)
        {
            Assert.DoesNotContain(canary, logs);
        }
    }

    [Fact]
    public async Task The_app_secret_reaches_Heimdall_but_is_never_logged_even_at_trace()
    {
        const string canary = "CANARY-app-secret-7c1e";
        using var host = new HeimdallHost(appSecret: canary);
        host.Heimdall.RequiredAppSecret = canary;
        using var traced = host.WithWebHostBuilder(builder => builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddFilter((string? _, LogLevel _) => true);
            logging.AddFilter("System.Net.Http", LogLevel.Trace);
            logging.AddFilter("Microsoft", LogLevel.Trace);
        }));
        using var client = traced.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost")
        });

        using var start = await client.GetAsync("/auth/heimdall/discord?access=member&returnUrl=/App");

        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        Assert.Equal(canary, Assert.Single(host.Heimdall.StartSecrets));
        var logs = string.Join("\n", host.Logs.Lines);
        Assert.Contains(HeimdallAuthService.AppSecretHeader, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(canary, logs);
    }

    private static void AssertAttemptCookieExpired(HttpResponseMessage response, string name)
    {
        var header = HeimdallHost.SetCookieHeader(response, name);
        Assert.NotNull(header);
        Assert.StartsWith(name + "=;", header);
        var attributes = header!.Split(';').Select(x => x.Trim().ToLowerInvariant()).ToArray();
        Assert.Contains("path=/auth/heimdall", attributes);
        Assert.Contains(attributes, x => x.StartsWith("expires=", StringComparison.Ordinal) && x.Contains("1970"));
        Assert.DoesNotContain(attributes, x => x.StartsWith("max-age", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_attempt_cookie_is_expired_at_its_path_on_success_and_on_refusal()
    {
        using var host = new HeimdallHost();
        var accountId = HeimdallShapes.NewAccountId();

        var signedAttempt = await host.StartAsync();
        var signedIn = await Genuine(host, signedAttempt, accountId);
        Assert.True(signedIn.SignedIn);
        AssertAttemptCookieExpired(signedIn.Response, signedAttempt.Nonce);

        var unknownCode = await host.StartAsync();
        var gone = await host.CompleteAsync(unknownCode, "unknown-code");
        AssertRefusedRedirect(gone);
        AssertAttemptCookieExpired(gone.Response, unknownCode.Nonce);

        var tampered = await host.StartAsync();
        var refused = await host.CompleteAsync(
            tampered.Nonce, host.CodeFor(tampered, accountId), Corrupt(tampered.Cookie!));
        AssertRefusedRedirect(refused);
        AssertAttemptCookieExpired(refused.Response, tampered.Nonce);
    }

    [Fact]
    public async Task Return_paths_are_bounded_so_the_attempt_cookie_stays_small()
    {
        using var host = new HeimdallHost();
        var atBound = "/" + new string('a', LocalReturnPath.MaxLength - 1);
        var overBound = atBound + "a";
        var worstCase = "/" + new string('<', LocalReturnPath.MaxLength - 1);

        Assert.Equal(atBound, LocalReturnPath.Parse(atBound).Value);
        Assert.Equal(LocalReturnPath.Default, LocalReturnPath.Parse(overBound));

        var kept = await host.StartAsync(returnUrl: atBound);
        var keptSignIn = await Genuine(host, kept, HeimdallShapes.NewAccountId());
        Assert.True(keptSignIn.SignedIn);
        Assert.Equal(atBound, keptSignIn.Response.Headers.Location!.OriginalString);

        var over = await host.StartAsync(returnUrl: overBound);
        var overSignIn = await Genuine(host, over, HeimdallShapes.NewAccountId());
        Assert.True(overSignIn.SignedIn);
        Assert.Equal("/App", overSignIn.Response.Headers.Location!.OriginalString);

        var heaviest = await host.StartAsync(returnUrl: worstCase);
        var header = HeimdallHost.SetCookieHeader(heaviest.Response, heaviest.CookieName!)!;
        Assert.InRange(header.Split(';')[0].Length, 1, 3072);
    }

    /// <summary>The browser's cookie jar for /auth/heimdall: Set-Cookie headers applied, a deletion removing the cookie.</summary>
    private static void ApplySetCookies(Dictionary<string, string> jar, HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var headers))
        {
            return;
        }

        foreach (var header in headers)
        {
            var parts = header.Split(';').Select(x => x.Trim()).ToArray();
            var pair = parts[0].Split('=', 2);
            if (parts.Any(x => x.StartsWith("expires=", StringComparison.OrdinalIgnoreCase) && x.Contains("1970")))
            {
                jar.Remove(pair[0]);
            }
            else
            {
                jar[pair[0]] = pair[1];
            }
        }
    }

    private static string CookieHeader(Dictionary<string, string> jar) =>
        string.Join("; ", jar.Select(x => x.Key + "=" + x.Value));

    /// <summary>The deletions a response carries; each must match the cookie by Path and carry no Max-Age.</summary>
    private static int Deletions(HttpResponseMessage response)
    {
        var deletions = response.Headers.TryGetValues("Set-Cookie", out var headers)
            ? headers.Where(x => x.Contains("1970")).ToArray()
            : [];
        foreach (var header in deletions)
        {
            var attributes = header.Split(';').Skip(1).Select(x => x.Trim().ToLowerInvariant()).ToArray();
            Assert.Contains("path=/auth/heimdall", attributes);
            Assert.DoesNotContain(attributes, x => x.StartsWith("max-age", StringComparison.Ordinal));
        }

        return deletions.Length;
    }

    [Fact]
    public async Task Parallel_starts_cannot_push_the_Cookie_header_past_the_bound_and_the_last_start_completes()
    {
        using var host = new HeimdallHost();
        var worstCase = "/" + new string('<', LocalReturnPath.MaxLength - 1);
        var jar = new Dictionary<string, string>();
        var largest = 0;

        // Rounds of parallel starts: every start of a round carries the same Cookie header, as a browser's
        // simultaneous requests do before any Set-Cookie lands.
        for (var round = 0; round < 3; round++)
        {
            var header = jar.Count == 0 ? null : CookieHeader(jar);
            var parallel = new List<StartedAttempt>();
            for (var i = 0; i < 6; i++)
            {
                host.Clock.Advance(TimeSpan.FromSeconds(1));
                parallel.Add(await host.StartAsync(returnUrl: worstCase, cookieHeader: header));
            }

            foreach (var attempt in parallel)
            {
                Assert.NotNull(attempt.Cookie);
                Assert.Equal(0, Deletions(attempt.Response));
                ApplySetCookies(jar, attempt.Response);
                largest = Math.Max(largest, CookieHeader(jar).Length);
            }

            // The browser holds the last-started attempt and it completes; an overwritten one fails cleanly.
            var last = parallel[^1];
            var overwritten = parallel[0];
            var lost = await host.CompleteWithHeaderAsync(
                overwritten.Nonce, host.CodeFor(overwritten, HeimdallShapes.NewAccountId()), CookieHeader(jar));
            Assert.False(lost.SignedIn);
            Assert.Equal(HttpStatusCode.Redirect, lost.Response.StatusCode);
            Assert.Equal(ErrorRedirect, lost.Response.Headers.Location!.OriginalString);
            Assert.DoesNotContain(overwritten.Nonce, string.Join("\n", lost.Response.Headers.SelectMany(x => x.Value)));
            var done = await host.CompleteWithHeaderAsync(
                last.Nonce, host.CodeFor(last, HeimdallShapes.NewAccountId()), CookieHeader(jar));
            Assert.True(done.SignedIn);
            ApplySetCookies(jar, done.Response);
        }

        Assert.True(largest <= 6144, $"Cookie header reached {largest} bytes");
        Assert.True(largest > 4096, "the probe never held two worst-case attempts");
    }

    [Fact]
    public async Task A_third_sequential_attempt_takes_the_oldest_slot_and_the_others_still_complete()
    {
        using var host = new HeimdallHost();
        var jar = new Dictionary<string, string>();
        var started = new List<StartedAttempt>();
        for (var i = 0; i < 3; i++)
        {
            host.Clock.Advance(TimeSpan.FromSeconds(1));
            var attempt = await host.StartAsync(
                returnUrl: "/App", cookieHeader: (jar.Count == 0 ? "" : CookieHeader(jar) + "; ") + ".AspNetCore.Cookies=session; x=1");
            ApplySetCookies(jar, attempt.Response);
            started.Add(attempt);
        }

        Assert.Equal(HeimdallAttemptCookie.SlotCount, jar.Keys.Count(x => x.StartsWith(HeimdallAttemptCookie.Prefix, StringComparison.Ordinal)));
        Assert.Equal(started[0].CookieName, started[2].CookieName);

        var oldest = await host.CompleteWithHeaderAsync(
            started[0].Nonce, host.CodeFor(started[0], HeimdallShapes.NewAccountId()), CookieHeader(jar));
        Assert.False(oldest.SignedIn);
        Assert.Equal(ErrorRedirect, oldest.Response.Headers.Location!.OriginalString);
        foreach (var attempt in started.Skip(1))
        {
            var done = await host.CompleteWithHeaderAsync(
                attempt.Nonce, host.CodeFor(attempt, HeimdallShapes.NewAccountId()), CookieHeader(jar));
            Assert.True(done.SignedIn);
        }
    }

    [Fact]
    public async Task The_complete_page_form_has_the_fields_and_action_the_server_reads()
    {
        using var host = new HeimdallHost();
        using var client = host.NewClient();
        var page = await client.GetStringAsync("/auth/heimdall/complete");

        var form = Regex.Match(page, @"<form\b([^>]*)>(.*?)</form>", RegexOptions.Singleline);
        Assert.True(form.Success);
        Assert.Matches(@"\bmethod=""post""", form.Groups[1].Value);
        var formId = Regex.Match(form.Groups[1].Value, @"\bid=""([^""]*)""").Groups[1].Value;
        Assert.NotEmpty(formId);
        Assert.Contains($"getElementById(\"{formId}\")", page);
        var action = Regex.Match(form.Groups[1].Value, @"\baction=""([^""]*)""").Groups[1].Value;
        Assert.Equal("/auth/heimdall/complete", action);
        var inputs = Regex.Matches(form.Groups[2].Value, @"<input\b([^>]*)>");
        Assert.All(inputs, x => Assert.Contains("type=\"hidden\"", x.Groups[1].Value));
        var names = inputs.Select(x => Regex.Match(x.Groups[1].Value, @"\bname=""([^""]*)""").Groups[1].Value).ToArray();
        Assert.Equal(new[] { "attempt", "code" }, names);

        // The page's own field names and action, filled as its script fills them, complete a sign-in.
        var attempt = await host.StartAsync();
        var code = host.CodeFor(attempt, HeimdallShapes.NewAccountId());
        var fields = new Dictionary<string, string> { [names[0]] = attempt.Nonce, [names[1]] = code };
        using var post = new HttpRequestMessage(HttpMethod.Post, action) { Content = new FormUrlEncodedContent(fields) };
        post.Headers.Add("Cookie", $"{attempt.CookieName}={attempt.Cookie}");
        using var response = await client.SendAsync(post);
        Assert.False(string.IsNullOrEmpty(HeimdallHost.CookieValue(response, ".AspNetCore.Cookies")));
    }

    [Fact]
    public async Task A_blank_app_slug_is_refused_before_Heimdall_is_called()
    {
        using var host = new HeimdallHost(appSlug: " ");

        var refused = await host.StartAsync();

        Assert.Equal(ErrorRedirect, refused.Response.Headers.Location!.OriginalString);
        Assert.Empty(host.Heimdall.StartBodies);
        Assert.False(refused.Response.Headers.Contains("Set-Cookie"));
        Assert.Contains("Heimdall start refused: Heimdall is not configured.", string.Join("\n", host.Logs.Lines));
    }
}
