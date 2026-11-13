using System.Net;
using System.Text.Json;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Heimdall;
using Bifrost.Web.Features.Shared;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.Web.Tests;

/// <summary>
/// Attaching a Google login to a signed-in Heimdall account by explicit link. Bifrost keeps no Heimdall token: each
/// Heimdall call that needs a bearer follows a fresh Heimdall sign-in, and the bearer lives inside that one request.
/// The attempt cookie carries only a purpose, the expected account and a pending link id.
/// </summary>
public sealed class HeimdallGoogleLinkTests
{
    private const string LinkedPage = "/Membership/LinkedAccounts";
    private const string ErrorRedirect = "/?auth=heimdall-error";

    private sealed record Step(HttpResponseMessage Response, StartedAttempt Next, string Token);

    private static string Jar(string session, StartedAttempt attempt) =>
        $"{BifrostCookies.Session}={session}; {attempt.CookieName}={attempt.Cookie}";

    private static string? Location(HttpResponseMessage response) => response.Headers.Location?.OriginalString;

    /// <summary>B: the fresh sign-in of a LinkStart attempt completes; Bifrost asks Heimdall for the Google link.</summary>
    private static async Task<Step> StepB(HeimdallHost host, string session, StartedAttempt start, string account, string? token = null)
    {
        token ??= host.Token(account);
        var code = host.CodeFor(start, account, token);
        var before = host.Heimdall.StartBodies.Count;
        var response = await host.PostCompleteAsync(
            new Dictionary<string, string> { ["attempt"] = start.Nonce, ["code"] = code }, Jar(session, start));
        return new Step(response, host.AttemptFrom(response, before), token);
    }

    /// <summary>C: the browser is back from Google with a pending link id; Bifrost starts a fresh sign-in.</summary>
    private static async Task<Step> StepC(HeimdallHost host, string session, StartedAttempt awaiting, string pendingLinkId)
    {
        var before = host.Heimdall.StartBodies.Count;
        var response = await host.PostCompleteAsync(
            new Dictionary<string, string> { ["attempt"] = awaiting.Nonce, ["pendingLinkId"] = pendingLinkId }, Jar(session, awaiting));
        return new Step(response, host.AttemptFrom(response, before), string.Empty);
    }

    /// <summary>D: the fresh sign-in of a LinkConfirm attempt completes; Bifrost confirms the link.</summary>
    private static async Task<Step> StepD(HeimdallHost host, string session, StartedAttempt confirming, string account, string? token = null)
    {
        token ??= host.Token(account);
        var code = host.CodeFor(confirming, account, token);
        var response = await host.PostCompleteAsync(
            new Dictionary<string, string> { ["attempt"] = confirming.Nonce, ["code"] = code }, Jar(session, confirming));
        return new Step(response, confirming, token);
    }

    private sealed record Journey(string Account, string Session, StartedAttempt Start, Step B, string PendingLinkId, Step C);

    /// <summary>Everything up to the LinkConfirm attempt, waiting for the fresh sign-in's code.</summary>
    private static async Task<Journey> ThroughC(HeimdallHost host, string? pendingLinkId = null)
    {
        var account = HeimdallShapes.NewAccountId();
        var session = await host.SignInSessionAsync(account);
        var start = await host.StartLinkAsync(session);
        var b = await StepB(host, session, start, account);
        var pending = host.Heimdall.RegisterPendingLink(pendingLinkId);
        var c = await StepC(host, session, b.Next, pending);
        return new Journey(account, session, start, b, pending, c);
    }

    private static string Unprotect(HeimdallHost host, string cookie) =>
        host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("Bifrost.Heimdall.Attempt.v1").Unprotect(cookie);

    // ---- 1. the link start carries a fresh bearer ----

    [Fact]
    public async Task The_account_page_starts_an_ordinary_sign_in_and_the_link_start_follows_with_the_fresh_bearer()
    {
        using var host = new HeimdallHost(realSessions: true);
        var account = HeimdallShapes.NewAccountId();
        var session = await host.SignInSessionAsync(account);
        var signInStarts = host.Heimdall.StartBodies.Count;

        var start = await host.StartLinkAsync(session);

        Assert.Equal(HttpStatusCode.Redirect, start.Response.StatusCode);
        Assert.Equal(signInStarts + 1, host.Heimdall.StartBodies.Count);
        using (var body = JsonDocument.Parse(host.Heimdall.StartBodies[^1]))
        {
            Assert.Equal("sign_in", body.RootElement.GetProperty("mode").GetString());
        }

        Assert.Equal("/v1/oauth/discord/start", host.Heimdall.StartPaths[^1]);
        Assert.Null(host.Heimdall.StartAuthorizations[^1]);
        Assert.NotNull(start.Cookie);

        var b = await StepB(host, session, start, account);

        Assert.Equal(signInStarts + 2, host.Heimdall.StartBodies.Count);
        Assert.Equal("/v1/oauth/google/start", host.Heimdall.StartPaths[^1]);
        Assert.Equal("Bearer " + b.Token, host.Heimdall.StartAuthorizations[^1]);
        using var link = JsonDocument.Parse(host.Heimdall.StartBodies[^1]);
        Assert.Equal("link", link.RootElement.GetProperty("mode").GetString());
        Assert.Equal("bifrost", link.RootElement.GetProperty("appSlug").GetString());
        Assert.Equal("browser_completion", link.RootElement.GetProperty("handoff").GetProperty("kind").GetString());
        Assert.StartsWith("https://localhost/auth/heimdall/complete?attempt=", link.RootElement.GetProperty("returnTo").GetString());
        Assert.NotEqual(start.Nonce, b.Next.Nonce);
        Assert.Equal("https://heimdall.test/oauth/google/authorize?state=link-fixture", Location(b.Response));
        // The sign-in session is untouched: the link flow never replaces who is signed in.
        Assert.Null(HeimdallHost.CookieValue(b.Response, BifrostCookies.Session));
        Assert.Equal(HttpStatusCode.Redirect, b.Response.StatusCode);
    }

    [Fact]
    public async Task A_link_start_attempt_without_a_code_asks_Heimdall_for_nothing()
    {
        using var host = new HeimdallHost(realSessions: true);
        var account = HeimdallShapes.NewAccountId();
        var session = await host.SignInSessionAsync(account);
        var start = await host.StartLinkAsync(session);
        var before = host.Heimdall.StartBodies.Count;

        var response = await host.PostCompleteAsync(
            new Dictionary<string, string> { ["attempt"] = start.Nonce }, Jar(session, start));

        Assert.Equal(before, host.Heimdall.StartBodies.Count);
        Assert.Equal(LinkedPage + "?result=error", Location(response));
    }

    [Fact]
    public async Task A_link_start_attempt_cannot_sign_in_a_session()
    {
        using var host = new HeimdallHost(realSessions: true);
        var account = HeimdallShapes.NewAccountId();
        var session = await host.SignInSessionAsync(account);
        var start = await host.StartLinkAsync(session);
        var other = HeimdallShapes.NewAccountId();

        var done = await host.CompleteAsync(start, host.CodeFor(start, other));

        Assert.False(done.SignedIn);
        Assert.Null(await host.Account(other));
    }

    // ---- 2. only a signed-in user, only by POST with antiforgery ----

    [Fact]
    public async Task Without_a_session_a_link_post_starts_nothing()
    {
        using var host = new HeimdallHost(realSessions: true);
        var before = host.Heimdall.StartBodies.Count;

        var start = await host.StartLinkAsync(session: null);

        Assert.Equal(before, host.Heimdall.StartBodies.Count);
        Assert.Null(start.Cookie);
        Assert.NotEqual(HttpStatusCode.OK, start.Response.StatusCode);
    }

    [Fact]
    public async Task A_get_never_starts_a_link()
    {
        using var host = new HeimdallHost(realSessions: true);
        var session = await host.SignInSessionAsync(HeimdallShapes.NewAccountId());
        var before = host.Heimdall.StartBodies.Count;

        var start = await host.StartLinkAsync(session, method: HttpMethod.Get);

        Assert.Equal(before, host.Heimdall.StartBodies.Count);
        Assert.Null(start.Cookie);
    }

    [Fact]
    public async Task A_link_post_without_the_antiforgery_token_starts_nothing()
    {
        using var host = new HeimdallHost(realSessions: true);
        var session = await host.SignInSessionAsync(HeimdallShapes.NewAccountId());
        var before = host.Heimdall.StartBodies.Count;

        var start = await host.StartLinkAsync(session, antiforgery: false);

        Assert.Equal(before, host.Heimdall.StartBodies.Count);
        Assert.Null(start.Cookie);
        Assert.Equal(HttpStatusCode.BadRequest, start.Response.StatusCode);
    }

    // ---- 3. a browser that is not the one that began the link ----

    [Fact]
    public async Task A_foreign_browser_holding_a_pending_link_id_confirms_nothing_and_calls_Heimdall_zero_times()
    {
        using var host = new HeimdallHost(realSessions: true);
        var journey = await ThroughC(host);
        var redeems = host.Heimdall.RedeemCalls;
        var starts = host.Heimdall.StartBodies.Count;

        // No cookie at all, and a session of its own; then an unrelated sign-in attempt's cookie.
        var strangerAccount = HeimdallShapes.NewAccountId();
        var stranger = await host.SignInSessionAsync(strangerAccount);
        redeems = host.Heimdall.RedeemCalls;
        starts = host.Heimdall.StartBodies.Count;
        var noCookie = await host.PostCompleteAsync(
            new Dictionary<string, string> { ["attempt"] = journey.B.Next.Nonce, ["pendingLinkId"] = journey.PendingLinkId },
            $"{BifrostCookies.Session}={stranger}");
        var unrelated = await host.StartAsync();
        starts = host.Heimdall.StartBodies.Count;
        var wrongCookie = await host.PostCompleteAsync(
            new Dictionary<string, string> { ["attempt"] = journey.B.Next.Nonce, ["pendingLinkId"] = journey.PendingLinkId },
            $"{BifrostCookies.Session}={stranger}; {unrelated.CookieName}={unrelated.Cookie}");
        var forgedCode = host.CodeFor(journey.C.Next, strangerAccount);
        var confirmWithoutCookie = await host.PostCompleteAsync(
            new Dictionary<string, string> { ["attempt"] = journey.C.Next.Nonce, ["code"] = forgedCode },
            $"{BifrostCookies.Session}={stranger}");

        Assert.Equal(starts, host.Heimdall.StartBodies.Count);
        Assert.Equal(redeems, host.Heimdall.RedeemCalls);
        Assert.Empty(host.Heimdall.ConfirmBodies);
        foreach (var response in new[] { noCookie, wrongCookie, confirmWithoutCookie })
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.DoesNotContain("Bearer", Location(response));
        }
    }

    // ---- 4. a different Heimdall account ----

    [Fact]
    public async Task Completion_for_a_different_account_than_the_session_is_refused_with_no_link_call()
    {
        using var host = new HeimdallHost(realSessions: true);
        var account = HeimdallShapes.NewAccountId();
        var session = await host.SignInSessionAsync(account);
        var start = await host.StartLinkAsync(session);
        var starts = host.Heimdall.StartBodies.Count;

        var b = await StepB(host, session, start, HeimdallShapes.NewAccountId());

        Assert.Equal(starts, host.Heimdall.StartBodies.Count);
        Assert.Equal(LinkedPage + "?result=error", Location(b.Response));
        AssertAttemptCleared(b.Response, start);
    }

    [Fact]
    public async Task A_session_of_another_account_cannot_continue_the_link()
    {
        using var host = new HeimdallHost(realSessions: true);
        var account = HeimdallShapes.NewAccountId();
        var session = await host.SignInSessionAsync(account);
        var start = await host.StartLinkAsync(session);
        var intruder = HeimdallShapes.NewAccountId();
        var intruderSession = await host.SignInSessionAsync(intruder);
        var starts = host.Heimdall.StartBodies.Count;
        var redeems = host.Heimdall.RedeemCalls;

        var b = await StepB(host, intruderSession, start, intruder);

        Assert.Equal(starts, host.Heimdall.StartBodies.Count);
        Assert.Equal(redeems, host.Heimdall.RedeemCalls);
        Assert.Equal(LinkedPage + "?result=error", Location(b.Response));
    }

    [Fact]
    public async Task A_confirming_sign_in_for_another_account_confirms_nothing()
    {
        using var host = new HeimdallHost(realSessions: true);
        var journey = await ThroughC(host);
        var other = HeimdallShapes.NewAccountId();

        var d = await StepD(host, journey.Session, journey.C.Next, other);

        Assert.Empty(host.Heimdall.ConfirmBodies);
        Assert.Equal(LinkedPage + "?result=error", Location(d.Response));
    }

    private static void AssertAttemptCleared(HttpResponseMessage response, StartedAttempt attempt)
    {
        var header = HeimdallHost.SetCookieHeader(response, attempt.CookieName!);
        Assert.NotNull(header);
        Assert.Contains("1970", header);
    }

    // ---- 5. outcomes ----

    [Fact]
    public async Task Success_confirms_once_with_the_pending_link_id_and_a_fresh_bearer_and_shows_linked()
    {
        using var host = new HeimdallHost(realSessions: true);
        var journey = await ThroughC(host);
        Assert.Empty(host.Heimdall.ConfirmBodies);
        Assert.Equal(HttpStatusCode.Redirect, journey.C.Response.StatusCode);
        Assert.Equal("/v1/oauth/discord/start", host.Heimdall.StartPaths[^1]);
        Assert.Null(host.Heimdall.StartAuthorizations[^1]);

        var d = await StepD(host, journey.Session, journey.C.Next, journey.Account);

        Assert.Equal(LinkedPage + "?result=linked", Location(d.Response));
        Assert.Single(host.Heimdall.ConfirmBodies);
        using var body = JsonDocument.Parse(host.Heimdall.ConfirmBodies[0]);
        Assert.Equal(journey.PendingLinkId, body.RootElement.GetProperty("pendingLinkId").GetString());
        Assert.Equal("Bearer " + d.Token, host.Heimdall.ConfirmAuthorizations[0]);
        Assert.NotEqual("Bearer " + journey.B.Token, host.Heimdall.ConfirmAuthorizations[0]);
        AssertAttemptCleared(d.Response, journey.C.Next);
        Assert.Null(HeimdallHost.CookieValue(d.Response, BifrostCookies.Session));
        var account = await host.Account(journey.Account);
        Assert.Equal(MembershipStatus.Authenticated, account!.Membership!.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "identity_linked_elsewhere")]
    [InlineData(HttpStatusCode.Gone, "expired")]
    [InlineData(HttpStatusCode.Unauthorized, "error")]
    [InlineData(HttpStatusCode.InternalServerError, "error")]
    public async Task A_refused_confirm_redirects_with_its_own_fixed_outcome(HttpStatusCode status, string outcome)
    {
        using var host = new HeimdallHost(realSessions: true);
        var journey = await ThroughC(host);
        host.Heimdall.ConfirmStatus = status;

        var d = await StepD(host, journey.Session, journey.C.Next, journey.Account);

        Assert.Equal($"{LinkedPage}?result={outcome}", Location(d.Response));
        Assert.DoesNotContain("HEIMDALL FREE TEXT", string.Join("\n", host.Logs.Lines));
        AssertAttemptCleared(d.Response, journey.C.Next);
    }

    [Fact]
    public async Task The_account_page_words_each_outcome_from_a_closed_set_and_ignores_anything_else()
    {
        using var host = new HeimdallHost(realSessions: true);
        var session = await host.SignInSessionAsync(HeimdallShapes.NewAccountId());
        using var client = host.NewClient();

        async Task<string> Page(string link)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LinkedPage + "?result=" + Uri.EscapeDataString(link));
            request.Headers.Add("Cookie", $"{BifrostCookies.Session}={session}");
            return await (await client.SendAsync(request)).Content.ReadAsStringAsync();
        }

        Assert.Contains("now linked to your Heimdall account", await Page("linked"));
        Assert.Contains("belongs to another account", await Page("identity_linked_elsewhere"));
        Assert.Contains("expired before it was confirmed", await Page("expired"));
        Assert.Contains("could not be completed", await Page("error"));
        var hostile = await Page("<script>alert(1)</script>");
        Assert.DoesNotContain("alert(1)", hostile);
        Assert.DoesNotContain("now linked to your", hostile);
    }

    // ---- 6. google stays out of sign-in and member access ----

    [Theory]
    [InlineData("/auth/heimdall/google")]
    [InlineData("/auth/heimdall/google?access=member")]
    [InlineData("/auth/heimdall/Google")]
    public async Task Google_is_refused_as_a_sign_in_provider_and_asks_Heimdall_for_nothing(string url)
    {
        using var host = new HeimdallHost(realSessions: true);

        var start = await host.StartAtAsync(url);

        Assert.Equal(HttpStatusCode.Redirect, start.Response.StatusCode);
        Assert.Equal(ErrorRedirect, Location(start.Response));
        Assert.Empty(host.Heimdall.StartBodies);
    }

    // ---- 7. nothing secret leaves the request ----

    [Fact]
    public async Task No_heimdall_token_or_pending_link_id_reaches_a_log_a_cookie_or_a_location()
    {
        using var host = new HeimdallHost(traceHttp: true, realSessions: true);
        const string pendingCanary = "PENDING-CANARY-7f3a";
        var account = HeimdallShapes.NewAccountId();
        var session = await host.SignInSessionAsync(account);
        var start = await host.StartLinkAsync(session);
        var b = await StepB(host, session, start, account);
        var pending = host.Heimdall.RegisterPendingLink(pendingCanary);
        var c = await StepC(host, session, b.Next, pending);
        var d = await StepD(host, session, c.Next, account);
        Assert.Equal(LinkedPage + "?result=linked", Location(d.Response));

        var tokens = new[] { b.Token, d.Token }
            .SelectMany(token => new[] { token, token.Split('.')[2], token.Split('.')[1] })
            .ToArray();
        var logs = string.Join("\n", host.Logs.Lines);
        Assert.Contains("Bearer", string.Join("\n", host.Heimdall.StartAuthorizations.Where(x => x is not null)));
        foreach (var secret in tokens.Append(pendingCanary))
        {
            Assert.DoesNotContain(secret, logs);
            foreach (var step in new[] { start.Response, b.Response, c.Response, d.Response })
            {
                Assert.DoesNotContain(secret, Location(step) ?? string.Empty);
            }
        }

        // Every attempt cookie Bifrost set along the way, unprotected: purpose, account and pending id only.
        foreach (var cookie in new[] { start.Cookie, b.Next.Cookie, c.Next.Cookie })
        {
            var payload = Unprotect(host, cookie!);
            foreach (var token in tokens)
            {
                Assert.DoesNotContain(token, payload);
            }

            Assert.DoesNotContain("eyJ", payload);
        }

        Assert.DoesNotContain(pendingCanary, Unprotect(host, start.Cookie!));
        Assert.DoesNotContain(pendingCanary, Unprotect(host, b.Next.Cookie!));
        Assert.Contains(account, Unprotect(host, b.Next.Cookie!));
    }

    // ---- 8. old attempt cookies keep signing in ----

    [Fact]
    public async Task An_attempt_cookie_written_before_purposes_existed_still_completes_a_normal_sign_in()
    {
        using var host = new HeimdallHost(realSessions: true);
        var nonce = "legacy-nonce-" + Guid.NewGuid().ToString("N");
        var protector = host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("Bifrost.Heimdall.Attempt.v1");
        var legacy = protector.Protect(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["Nonce"] = nonce,
            ["Provider"] = "discord",
            ["RequiresMemberAccess"] = false,
            ["ReturnPath"] = "/App/Mill",
            ["ExpiresAt"] = host.Clock.Now.AddMinutes(5)
        }));
        var account = HeimdallShapes.NewAccountId();
        var attempt = new StartedAttempt(
            nonce, HeimdallAttemptCookie.SlotNames[0], legacy, $"https://localhost/auth/heimdall/complete?attempt={nonce}", null!);

        var done = await host.CompleteAsync(attempt, host.CodeFor(attempt, account));

        Assert.True(done.SignedIn);
        Assert.Equal("/App/Mill", Location(done.Response));
        Assert.NotNull(await host.Account(account));
    }

    // ---- 9. attempts are single use ----

    [Fact]
    public async Task Replaying_a_consumed_link_start_attempt_does_nothing()
    {
        using var host = new HeimdallHost(realSessions: true);
        var account = HeimdallShapes.NewAccountId();
        var session = await host.SignInSessionAsync(account);
        var start = await host.StartLinkAsync(session);
        var code = host.CodeFor(start, account);
        var fields = new Dictionary<string, string> { ["attempt"] = start.Nonce, ["code"] = code };
        var first = await host.PostCompleteAsync(fields, Jar(session, start));
        Assert.Contains("google", Location(first));
        var starts = host.Heimdall.StartBodies.Count;

        var replay = await host.PostCompleteAsync(fields, Jar(session, start));

        Assert.Equal(starts, host.Heimdall.StartBodies.Count);
        Assert.Equal(LinkedPage + "?result=error", Location(replay));
    }

    [Fact]
    public async Task Replaying_a_consumed_confirm_attempt_confirms_nothing_twice()
    {
        using var host = new HeimdallHost(realSessions: true);
        var journey = await ThroughC(host);
        var code = host.CodeFor(journey.C.Next, journey.Account);
        var fields = new Dictionary<string, string> { ["attempt"] = journey.C.Next.Nonce, ["code"] = code };
        var first = await host.PostCompleteAsync(fields, Jar(journey.Session, journey.C.Next));
        Assert.Equal(LinkedPage + "?result=linked", Location(first));

        var replay = await host.PostCompleteAsync(fields, Jar(journey.Session, journey.C.Next));

        Assert.Single(host.Heimdall.ConfirmBodies);
        Assert.Equal(LinkedPage + "?result=error", Location(replay));
    }

    [Fact]
    public async Task Replaying_a_consumed_await_attempt_links_nothing()
    {
        using var host = new HeimdallHost(realSessions: true);
        var journey = await ThroughC(host);
        var again = await StepC(host, journey.Session, journey.B.Next, journey.PendingLinkId);

        // The replay can only begin another fresh sign-in; the pending id is spent at Heimdall by the first confirm.
        var d = await StepD(host, journey.Session, again.Next, journey.Account);
        var replayed = await StepD(host, journey.Session, journey.C.Next, journey.Account);

        Assert.Equal(LinkedPage + "?result=linked", Location(d.Response));
        Assert.Equal(LinkedPage + "?result=expired", Location(replayed.Response));
    }

    // ---- 10. pins for guards the first pass left unproven ----

    [Fact]
    public async Task A_link_continues_in_its_own_slot_and_leaves_a_parallel_sign_in_attempt_alive()
    {
        using var host = new HeimdallHost(realSessions: true);
        var account = HeimdallShapes.NewAccountId();
        var session = await host.SignInSessionAsync(account);
        var parallel = await host.StartAsync();
        var parallelCookie = $"{parallel.CookieName}={parallel.Cookie}";
        var start = await host.StartLinkAsync(session, extraCookies: parallelCookie);
        // The parallel attempt holds slot 0, so the link lives in the other slot.
        Assert.Equal(HeimdallAttemptCookie.SlotNames[0], parallel.CookieName);
        Assert.Equal(HeimdallAttemptCookie.SlotNames[1], start.CookieName);

        var b = await StepB(host, session, start, account);
        var awaiting = new Step(b.Response, b.Next, b.Token);
        var pending = host.Heimdall.RegisterPendingLink();
        var c = await StepC(host, session, awaiting.Next, pending);
        var d = await StepD(host, session, c.Next, account);

        // Every continuation wrote the link's own slot and none touched the parallel attempt's slot.
        foreach (var response in new[] { b.Response, c.Response })
        {
            Assert.NotNull(HeimdallHost.SetCookieHeader(response, start.CookieName!));
            Assert.Null(HeimdallHost.SetCookieHeader(response, parallel.CookieName!));
        }

        Assert.Equal(start.CookieName, b.Next.CookieName);
        Assert.Equal(start.CookieName, c.Next.CookieName);
        Assert.Equal(LinkedPage + "?result=linked", Location(d.Response));
        Assert.Null(HeimdallHost.SetCookieHeader(d.Response, parallel.CookieName!));

        var signIn = await host.CompleteWithHeaderAsync(
            parallel.Nonce, host.CodeFor(parallel, HeimdallShapes.NewAccountId()), parallelCookie);
        Assert.True(signIn.SignedIn);
    }

    [Theory]
    [InlineData("9")]
    [InlineData("-1")]
    [InlineData("99")]
    public async Task An_attempt_cookie_with_an_undefined_purpose_is_no_attempt_and_asks_Heimdall_for_nothing(string purpose)
    {
        using var host = new HeimdallHost(realSessions: true);
        var account = HeimdallShapes.NewAccountId();
        var session = await host.SignInSessionAsync(account);
        var nonce = "crafted-" + Guid.NewGuid().ToString("N");
        var protector = host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("Bifrost.Heimdall.Attempt.v1");
        var crafted = protector.Protect(
            $$"""{"Nonce":"{{nonce}}","Provider":"discord","RequiresMemberAccess":false,"ReturnPath":"/App/Mill","ExpiresAt":"{{host.Clock.Now.AddMinutes(5):O}}","Purpose":{{purpose}},"ExpectedAccountId":"{{account}}","PendingLinkId":"p"}""");
        var attempt = new StartedAttempt(
            nonce, HeimdallAttemptCookie.SlotNames[0], crafted, $"https://localhost/auth/heimdall/complete?attempt={nonce}", null!);
        var starts = host.Heimdall.StartBodies.Count;
        var redeems = host.Heimdall.RedeemCalls;

        var response = await host.PostCompleteAsync(
            new Dictionary<string, string>
            {
                ["attempt"] = nonce, ["code"] = host.CodeFor(attempt, account), ["pendingLinkId"] = "p"
            },
            Jar(session, attempt));

        Assert.Equal(ErrorRedirect, Location(response));
        Assert.Equal(starts, host.Heimdall.StartBodies.Count);
        Assert.Equal(redeems, host.Heimdall.RedeemCalls);
        Assert.Empty(host.Heimdall.ConfirmBodies);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_await_step_without_a_pending_link_id_is_refused_before_Heimdall_hears_of_it(string pendingLinkId)
    {
        using var host = new HeimdallHost(realSessions: true);
        var account = HeimdallShapes.NewAccountId();
        var session = await host.SignInSessionAsync(account);
        var start = await host.StartLinkAsync(session);
        var b = await StepB(host, session, start, account);
        var starts = host.Heimdall.StartBodies.Count;
        var redeems = host.Heimdall.RedeemCalls;

        var c = await StepC(host, session, b.Next, pendingLinkId);

        Assert.Equal(LinkedPage + "?result=error", Location(c.Response));
        Assert.Equal(starts, host.Heimdall.StartBodies.Count);
        Assert.Equal(redeems, host.Heimdall.RedeemCalls);
        Assert.Empty(host.Heimdall.ConfirmBodies);
        AssertAttemptCleared(c.Response, b.Next);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"status":"pending"}""")]
    [InlineData("""{"status":"Linked"}""")]
    [InlineData("not json")]
    public async Task A_201_confirm_that_does_not_say_linked_is_not_shown_as_linked(string body)
    {
        using var host = new HeimdallHost(realSessions: true);
        var journey = await ThroughC(host);
        host.Heimdall.ConfirmStatus = HttpStatusCode.Created;
        host.Heimdall.ConfirmBody = body;

        var d = await StepD(host, journey.Session, journey.C.Next, journey.Account);

        Assert.Equal(LinkedPage + "?result=error", Location(d.Response));
        Assert.Single(host.Heimdall.ConfirmBodies);
        AssertAttemptCleared(d.Response, journey.C.Next);
    }
}
