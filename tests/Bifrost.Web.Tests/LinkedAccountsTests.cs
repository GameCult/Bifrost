using System.Net;
using System.Text.Json;
using Bifrost.Web.Features.Heimdall;
using Bifrost.Web.Features.Shared;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.Web.Tests;

/// <summary>
/// The Linked accounts page from the member's side, against a fake Heimdall. Bifrost keeps no Heimdall token: a list, a link
/// and an unlink each follow a fresh Heimdall sign-in begun by Bifrost, and the bearer lives inside that one completion request.
/// </summary>
public sealed class LinkedAccountsTests
{
    private const string Page = "/Membership/LinkedAccounts";
    private const string ErrorRedirect = "/?auth=heimdall-error";

    private static string Jar(string session, StartedAttempt attempt) =>
        $"{BifrostCookies.Session}={session}; {attempt.CookieName}={attempt.Cookie}";

    private static string? Location(HttpResponseMessage response) => response.Headers.Location?.OriginalString;

    private static string Unprotect(HeimdallHost host, string cookie) =>
        host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("Bifrost.Heimdall.Attempt.v1").Unprotect(cookie);

    /// <summary>The fresh sign-in's completion as the browser posts it: Heimdall minted a code for <paramref name="proofAccount"/>.</summary>
    private static async Task<HttpResponseMessage> Finish(
        HeimdallHost host, string session, StartedAttempt attempt, string proofAccount, string? token = null,
        string mode = "sign_in", string provider = "discord", string? sessionOverride = null, bool? stepUpRequested = null)
    {
        var code = host.CodeFor(attempt, proofAccount, token, mode: mode, provider: provider, stepUpRequested: stepUpRequested);
        return await host.PostCompleteAsync(
            new Dictionary<string, string> { ["attempt"] = attempt.Nonce, ["code"] = code }, Jar(sessionOverride ?? session, attempt));
    }

    private static string StepUpToken(HeimdallHost host, string account) =>
        host.Token(account, mutate: claims => claims["step_up_at"] = host.Clock.Now.ToUnixTimeSeconds());

    private sealed record Member(string Account, string Session);

    /// <summary>A signed-in member whose fake Heimdall account holds these providers, and whose list has been read once.</summary>
    private static async Task<Member> ListedMember(HeimdallHost host, params string[] providers)
    {
        var account = HeimdallShapes.NewAccountId();
        var session = await host.SignInSessionAsync(account);
        foreach (var provider in providers)
        {
            host.Heimdall.AddIdentity(provider, "Name of " + provider);
        }

        var start = await host.StartListAsync(session, providers[0]);
        var done = await Finish(host, session, start, account);
        Assert.Equal(Page + "?result=listed", Location(done));
        Assert.Empty(host.Heimdall.OrphanMints);
        return new Member(account, session);
    }

    // ---- who may use the page ----

    [Fact]
    public async Task An_anonymous_visitor_is_challenged_and_nothing_is_started()
    {
        using var host = new HeimdallHost(realSessions: true);

        var (get, _) = await host.GetLinkedAccountsAsync(session: null);
        var post = await host.PostLinkedAccountsAsync(session: null, "Refresh");

        Assert.Equal(HttpStatusCode.Redirect, get.StatusCode);
        Assert.Contains("/auth/sign-in", Location(get));
        Assert.NotEqual(HttpStatusCode.OK, post.Response.StatusCode);
        Assert.Empty(host.Heimdall.StartBodies);
    }

    [Fact]
    public async Task The_page_is_no_store_and_a_member_without_a_list_is_offered_one()
    {
        using var host = new HeimdallHost(realSessions: true);
        var session = await host.SignInSessionAsync(HeimdallShapes.NewAccountId());

        var (response, html) = await host.GetLinkedAccountsAsync(session);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("Show my logins through", html);
        Assert.Contains("Show my logins through Patreon", html);
        Assert.Contains("will not choose one for you", html);
        Assert.DoesNotContain("Link Discord", html);
        Assert.DoesNotContain("Unlink", html);
    }

    [Fact]
    public async Task With_a_list_the_page_offers_each_admitted_provider_to_link_and_never_twitch()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await ListedMember(host, "discord", "google");

        var (_, html) = await host.GetLinkedAccountsAsync(member.Session);

        Assert.Contains("Link Discord", html);
        Assert.Contains("Link Patreon", html);
        Assert.Contains("Link Google", html);
        Assert.DoesNotContain("Link Twitch", html);
    }

    [Fact]
    public async Task After_the_list_expired_a_proof_is_never_started_through_a_provider_the_member_unlinked()
    {
        using var host = new HeimdallHost(realSessions: true);
        var account = HeimdallShapes.NewAccountId();
        var session = await host.SignInSessionAsync(account); // signed in through Discord
        var discord = host.Heimdall.AddIdentity("discord", "D");
        host.Heimdall.AddIdentity("google", "G");
        var list = await host.StartListAsync(session, "discord");
        await Finish(host, session, list, account);
        var unlink = await host.StartUnlinkAsync(session, discord);
        var done = await Finish(host, session, unlink, account, provider: "google");
        Assert.Equal(Page + "?result=unlinked", Location(done));
        host.Clock.Advance(LinkedAccountsSnapshots.Lifetime + TimeSpan.FromSeconds(1));
        var starts = host.Heimdall.StartBodies.Count;

        var refresh = await host.StartListAsync(session, via: null);
        var link = await host.StartLinkAsync(session, "patreon", seedList: false);
        var (_, html) = await host.GetLinkedAccountsAsync(session);

        // Signing in through Discord again would mint an unrelated account holding that login: nothing is started.
        Assert.Equal(starts, host.Heimdall.StartBodies.Count);
        Assert.Equal(Page + "?result=provider_needed", Location(refresh.Response));
        Assert.Equal(Page + "?result=list_expired", Location(link.Response));
        Assert.Empty(host.Heimdall.OrphanMints);
        Assert.Contains("will not choose one for you", html);
        Assert.DoesNotContain("Link Patreon", html);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.OK)]
    public async Task An_unlink_whose_answer_is_lost_drops_the_list_and_a_link_then_starts_nothing(HttpStatusCode lost)
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await ListedMember(host, "discord", "google");
        var discord = (string)host.Heimdall.Identities[0]["id"]!;
        var start = await host.StartUnlinkAsync(member.Session, discord);
        host.Heimdall.UnlinkApplyThenFail = lost;

        var done = await Finish(host, member.Session, start, member.Account, provider: "google");
        var starts = host.Heimdall.StartBodies.Count;
        var link = await host.StartLinkAsync(member.Session, "patreon", seedList: false);
        var (_, html) = await host.GetLinkedAccountsAsync(member.Session);

        // Heimdall did unlink Discord. Linking now through the stale list would sign in through Discord and mint an orphan.
        Assert.Equal(Page + "?result=error", Location(done));
        Assert.Single(host.Heimdall.Identities);
        Assert.Equal(starts, host.Heimdall.StartBodies.Count);
        Assert.Equal(Page + "?result=list_expired", Location(link.Response));
        Assert.Empty(host.Heimdall.OrphanMints);
        Assert.Contains("will not choose one for you", html);
    }

    [Fact]
    public async Task Without_a_list_the_picker_names_Google_first_as_unable_to_create_an_account()
    {
        using var host = new HeimdallHost(realSessions: true);
        var session = await host.SignInSessionAsync(HeimdallShapes.NewAccountId());

        var (_, html) = await host.GetLinkedAccountsAsync(session);

        Assert.Contains("Google cannot do that", html);
        Assert.Contains("through Google (cannot create an account)", html);
        Assert.True(html.IndexOf("Show my logins through Google", StringComparison.Ordinal) < html.IndexOf("Show my logins through Discord", StringComparison.Ordinal));
    }

    [Fact]
    public void With_no_list_and_no_choice_there_is_no_proof_provider()
    {
        Assert.Null(HeimdallAuthService.ProofProvider(null, null));
        Assert.Null(HeimdallAuthService.ProofProvider(null, "twitch"));
        Assert.Equal("patreon", HeimdallAuthService.ProofProvider(null, "Patreon"));
        Assert.Null(HeimdallAuthService.ProofProvider([], "discord"));
        Assert.Null(HeimdallAuthService.ProofProvider(["twitch"], null));
        Assert.Equal("patreon", HeimdallAuthService.ProofProvider(["google", "patreon"], null));
    }

    [Fact]
    public async Task The_service_refuses_to_start_a_proof_with_no_list_and_no_choice()
    {
        using var host = new HeimdallHost(realSessions: true);
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<HeimdallAuthService>();
        var starts = host.Heimdall.StartBodies.Count;

        await Assert.ThrowsAsync<HeimdallAuthException>(() => service.StartListAsync(
            HeimdallShapes.NewAccountId(), null, "https://localhost", [], CancellationToken.None));
        await Assert.ThrowsAsync<HeimdallAuthException>(() => service.StartLinkAsync(
            HeimdallShapes.NewAccountId(), "patreon", "https://localhost", [], CancellationToken.None));

        Assert.Equal(starts, host.Heimdall.StartBodies.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("twitch")]
    [InlineData("nonsense")]
    public async Task Without_a_list_the_member_must_name_an_admitted_provider_or_nothing_is_started(string? via)
    {
        using var host = new HeimdallHost(realSessions: true);
        var session = await host.SignInSessionAsync(HeimdallShapes.NewAccountId());
        var starts = host.Heimdall.StartBodies.Count;

        var post = await host.StartListAsync(session, via);

        Assert.Equal(starts, host.Heimdall.StartBodies.Count);
        Assert.Equal(Page + "?result=provider_needed", Location(post.Response));
    }

    [Fact]
    public async Task With_a_list_the_member_cannot_steer_the_proof_to_a_provider_the_account_does_not_hold()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await ListedMember(host, "patreon", "google");

        var post = await host.StartListAsync(member.Session, "discord");

        Assert.Equal("/v1/oauth/patreon/start", host.Heimdall.StartPaths[^1]);
        Assert.Empty(host.Heimdall.OrphanMints);
        Assert.NotNull(post.Cookie);
    }

    [Fact]
    public async Task A_session_with_no_Heimdall_account_has_nothing_to_list_and_starts_nothing()
    {
        using var host = new HeimdallHost();

        var (response, html) = await host.GetLinkedAccountsAsync(session: null);
        var start = await host.StartListAsync(session: null);

        // No form, so no antiforgery token to make the response uncacheable: the page itself must say no-store.
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("not a Heimdall account", html);
        Assert.DoesNotContain("Link Discord", html);
        Assert.Empty(host.Heimdall.StartBodies);
        Assert.Null(start.Cookie);
    }

    [Theory]
    [InlineData("Refresh")]
    [InlineData("Link")]
    [InlineData("Unlink")]
    public async Task Every_post_needs_the_antiforgery_token_and_a_get_starts_nothing(string handler)
    {
        using var host = new HeimdallHost(realSessions: true);
        var session = await host.SignInSessionAsync(HeimdallShapes.NewAccountId());
        var form = new Dictionary<string, string> { ["provider"] = "discord", ["identityId"] = Guid.NewGuid().ToString() };
        var starts = host.Heimdall.StartBodies.Count;

        var without = await host.PostLinkedAccountsAsync(session, handler, form, antiforgery: false);
        var get = await host.PostLinkedAccountsAsync(session, handler, form, method: HttpMethod.Get);

        Assert.Equal(HttpStatusCode.BadRequest, without.Response.StatusCode);
        Assert.Null(without.Cookie);
        Assert.Null(get.Cookie);
        Assert.Equal(starts, host.Heimdall.StartBodies.Count);
    }

    // ---- the list ----

    [Fact]
    public async Task The_list_is_a_fresh_ordinary_sign_in_then_GET_identities_with_its_bearer_and_the_page_shows_it()
    {
        using var host = new HeimdallHost(traceHttp: true, realSessions: true);
        var account = HeimdallShapes.NewAccountId();
        var session = await host.SignInSessionAsync(account);
        host.Heimdall.AddIdentity("discord", "Discord Dana");
        host.Heimdall.AddIdentity("google", "Google Gale");
        var signIns = host.Heimdall.StartBodies.Count;

        var start = await host.StartListAsync(session);

        Assert.Equal(signIns + 1, host.Heimdall.StartBodies.Count);
        using (var body = JsonDocument.Parse(host.Heimdall.StartBodies[^1]))
        {
            Assert.Equal("sign_in", body.RootElement.GetProperty("mode").GetString());
            Assert.False(body.RootElement.TryGetProperty("stepUp", out _));
        }

        Assert.Equal("/v1/oauth/discord/start", host.Heimdall.StartPaths[^1]);
        Assert.Empty(host.Heimdall.ListAuthorizations);

        var token = host.Token(account);
        var done = await Finish(host, session, start, account, token);

        Assert.Equal(Page + "?result=listed", Location(done));
        Assert.Equal(["Bearer " + token], host.Heimdall.ListAuthorizations);
        Assert.Null(HeimdallHost.CookieValue(done, BifrostCookies.Session));
        var (_, html) = await host.GetLinkedAccountsAsync(session);
        Assert.Contains("Discord Dana", html);
        Assert.Contains("Google Gale", html);
        Assert.Contains("13 Nov 2026", html);
        // Only what the page shows was kept: nothing of the fields a login also carries.
        foreach (var canary in new[] { "PUID-CANARY", "username-canary", "scope-canary" })
        {
            Assert.DoesNotContain(canary, html);
        }
    }

    [Fact]
    public async Task No_bearer_reaches_a_cookie_a_log_a_location_or_the_page()
    {
        using var host = new HeimdallHost(traceHttp: true, realSessions: true);
        var account = HeimdallShapes.NewAccountId();
        var session = await host.SignInSessionAsync(account);
        host.Heimdall.AddIdentity("discord");
        host.Heimdall.AddIdentity("google");
        var listStart = await host.StartListAsync(session);
        var listToken = host.Token(account);
        var listed = await Finish(host, session, listStart, account, listToken);
        var unlinkStart = await host.StartUnlinkAsync(session, (string)host.Heimdall.Identities[0]["id"]!);
        var stepUpToken = StepUpToken(host, account);
        var unlinked = await Finish(host, session, unlinkStart, account, stepUpToken);
        Assert.Equal(Page + "?result=unlinked", Location(unlinked));
        var (_, html) = await host.GetLinkedAccountsAsync(session);

        var logs = string.Join("\n", host.Logs.Lines);
        var secrets = new[] { listToken, stepUpToken }
            .SelectMany(token => new[] { token, token.Split('.')[2], token.Split('.')[1] })
            .ToArray();
        Assert.Contains("Bearer", string.Join("\n", host.Heimdall.ListAuthorizations));
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, logs);
            Assert.DoesNotContain(secret, html);
            foreach (var response in new[] { listStart.Response, listed, unlinkStart.Response, unlinked })
            {
                Assert.DoesNotContain(secret, Location(response) ?? string.Empty);
                Assert.DoesNotContain(secret, string.Join("\n", response.Headers.SelectMany(x => x.Value)));
            }

            foreach (var cookie in new[] { listStart.Cookie, unlinkStart.Cookie })
            {
                var payload = Unprotect(host, cookie!);
                Assert.DoesNotContain(secret, payload);
                Assert.DoesNotContain("eyJ", payload);
            }
        }

        // The completion itself sets no cookie but the attempt's deletion.
        foreach (var response in new[] { listed, unlinked })
        {
            Assert.All(
                response.Headers.TryGetValues("Set-Cookie", out var set) ? set : [],
                header => Assert.StartsWith(HeimdallAttemptCookie.Prefix, header, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_list_is_per_account_and_expires_after_five_minutes()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await ListedMember(host, "discord", "google");
        var stranger = await host.SignInSessionAsync(HeimdallShapes.NewAccountId());

        Assert.Contains("Name of discord", (await host.GetLinkedAccountsAsync(member.Session)).Html);
        var (_, other) = await host.GetLinkedAccountsAsync(stranger);
        Assert.DoesNotContain("Name of discord", other);
        Assert.Contains("Show my logins through", other);

        host.Clock.Advance(LinkedAccountsSnapshots.Lifetime + TimeSpan.FromSeconds(1));

        var (_, later) = await host.GetLinkedAccountsAsync(member.Session);
        Assert.DoesNotContain("Name of discord", later);
        Assert.Contains("Show my logins through", later);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "sign_in_required")]
    [InlineData(HttpStatusCode.InternalServerError, "error")]
    public async Task A_list_Heimdall_refuses_shows_its_fixed_outcome_and_no_list(HttpStatusCode status, string outcome)
    {
        using var host = new HeimdallHost(realSessions: true);
        var account = HeimdallShapes.NewAccountId();
        var session = await host.SignInSessionAsync(account);
        host.Heimdall.AddIdentity("discord");
        host.Heimdall.ListStatus = status;
        var start = await host.StartListAsync(session);

        var done = await Finish(host, session, start, account);

        Assert.Equal($"{Page}?result={outcome}", Location(done));
        Assert.DoesNotContain("HEIMDALL FREE TEXT", string.Join("\n", host.Logs.Lines));
        Assert.Contains("Show my logins through", (await host.GetLinkedAccountsAsync(session)).Html);
    }

    [Fact]
    public async Task A_Heimdall_that_cannot_be_reached_for_the_list_changes_nothing()
    {
        using var host = new HeimdallHost(realSessions: true);
        var account = HeimdallShapes.NewAccountId();
        var session = await host.SignInSessionAsync(account);
        host.Heimdall.AddIdentity("discord");
        var start = await host.StartListAsync(session);
        host.Heimdall.MemberFailure = new HttpRequestException("connection refused");

        var done = await Finish(host, session, start, account);

        Assert.Equal(Page + "?result=error", Location(done));
        Assert.Contains("Show my logins through", (await host.GetLinkedAccountsAsync(session)).Html);
    }

    [Fact]
    public async Task A_Heimdall_that_is_down_at_the_list_start_is_a_refusal_not_a_server_error()
    {
        using var host = new HeimdallHost(realSessions: true);
        var session = await host.SignInSessionAsync(HeimdallShapes.NewAccountId());
        host.Heimdall.StartStatus = HttpStatusCode.ServiceUnavailable;

        var start = await host.StartListAsync(session);

        Assert.Equal(HttpStatusCode.Redirect, start.Response.StatusCode);
        Assert.Equal(Page + "?result=error", Location(start.Response));
        Assert.Null(start.Cookie);
    }

    // ---- unlink ----

    [Fact]
    public async Task Unlink_starts_a_step_up_sign_in_with_the_app_secret_and_the_unlink_carries_that_sign_ins_bearer()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await ListedMember(host, "discord", "google");
        var discord = (string)host.Heimdall.Identities[0]["id"]!;
        var listToken = host.Heimdall.ListAuthorizations.Single();
        var starts = host.Heimdall.StartBodies.Count;

        var start = await host.StartUnlinkAsync(member.Session, discord);

        Assert.Equal(starts + 1, host.Heimdall.StartBodies.Count);
        Assert.Equal("/v1/oauth/google/start", host.Heimdall.StartPaths[^1]);
        Assert.Equal(HeimdallHost.AppSecret, host.Heimdall.StartSecrets[^1]);
        Assert.Null(host.Heimdall.StartAuthorizations[^1]);
        using (var body = JsonDocument.Parse(host.Heimdall.StartBodies[^1]))
        {
            Assert.Equal("sign_in", body.RootElement.GetProperty("mode").GetString());
            Assert.True(body.RootElement.GetProperty("stepUp").GetBoolean());
        }

        Assert.Empty(host.Heimdall.Unlinks);
        var token = StepUpToken(host, member.Account);
        var done = await Finish(host, member.Session, start, member.Account, token, provider: "google");

        Assert.Equal(Page + "?result=unlinked", Location(done));
        var unlink = Assert.Single(host.Heimdall.Unlinks);
        Assert.Equal(discord, unlink.Id);
        Assert.Equal("Bearer " + token, unlink.Authorization);
        Assert.NotEqual(listToken, unlink.Authorization);
        var (_, html) = await host.GetLinkedAccountsAsync(member.Session);
        Assert.DoesNotContain("Name of discord", html);
        Assert.Contains("Name of google", html);
        Assert.Contains("Heimdall will not remove the last one", html);
        Assert.Contains("The login was unlinked.", (await host.GetLinkedAccountsAsync(member.Session, "?result=unlinked")).Html);
    }

    [Fact]
    public async Task The_last_login_is_refused_by_Heimdall_and_the_page_still_shows_it()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await ListedMember(host, "google");
        var only = (string)host.Heimdall.Identities[0]["id"]!;
        var start = await host.StartUnlinkAsync(member.Session, only);

        var done = await Finish(host, member.Session, start, member.Account, StepUpToken(host, member.Account), provider: "google");

        Assert.Equal(Page + "?result=last_identity", Location(done));
        Assert.Single(host.Heimdall.Unlinks);
        Assert.Single(host.Heimdall.Identities);
        var (_, html) = await host.GetLinkedAccountsAsync(member.Session);
        Assert.Contains("Name of google", html);
        Assert.Contains("will not remove it. Nothing was changed.", (await host.GetLinkedAccountsAsync(member.Session, "?result=last_identity")).Html);
    }

    [Fact]
    public async Task A_sign_in_Heimdall_does_not_count_as_fresh_proof_changes_nothing()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await ListedMember(host, "discord", "google");
        var start = await host.StartUnlinkAsync(member.Session, (string)host.Heimdall.Identities[0]["id"]!);

        // An ordinary token: no step_up_at claim, so Heimdall answers 403 step_up_required.
        var done = await Finish(host, member.Session, start, member.Account, host.Token(member.Account), provider: "google");

        Assert.Equal(Page + "?result=step_up_required", Location(done));
        Assert.Equal(2, host.Heimdall.Identities.Count);
        Assert.Contains("Name of discord", (await host.GetLinkedAccountsAsync(member.Session)).Html);
    }

    [Fact]
    public async Task A_completion_that_does_not_say_a_step_up_was_asked_unlinks_nothing()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await ListedMember(host, "discord", "google");
        var start = await host.StartUnlinkAsync(member.Session, (string)host.Heimdall.Identities[0]["id"]!);

        // The token even carries step_up_at, but the completion is not the answer to a step-up start.
        var done = await Finish(host, member.Session, start, member.Account, StepUpToken(host, member.Account), provider: "google",
            stepUpRequested: false);

        Assert.Equal(Page + "?result=step_up_required", Location(done));
        Assert.Empty(host.Heimdall.Unlinks);
        Assert.Equal(2, host.Heimdall.Identities.Count);
    }

    [Fact]
    public async Task A_proof_older_than_600_seconds_is_refused_by_Heimdall_and_changes_nothing()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await ListedMember(host, "discord", "google");
        var start = await host.StartUnlinkAsync(member.Session, (string)host.Heimdall.Identities[0]["id"]!);
        // The sign-in happened 601 seconds before the unlink reaches Heimdall (the attempt itself lives ten minutes).
        var stale = host.Token(member.Account, mutate: claims => claims["step_up_at"] = host.Clock.Now.ToUnixTimeSeconds() - 601);

        var done = await Finish(host, member.Session, start, member.Account, stale, provider: "google");

        Assert.Equal(Page + "?result=step_up_required", Location(done));
        Assert.Single(host.Heimdall.Unlinks);
        Assert.Equal(2, host.Heimdall.Identities.Count);
    }

    [Fact]
    public async Task Every_completion_answer_is_no_store()
    {
        using var host = new HeimdallHost(realSessions: true);
        var account = HeimdallShapes.NewAccountId();
        var session = await host.SignInSessionAsync(account);
        host.Heimdall.AddIdentity("discord");
        var start = await host.StartListAsync(session);

        var done = await Finish(host, session, start, account);
        var refused = await host.PostCompleteAsync(new Dictionary<string, string> { ["attempt"] = "nope" }, null);

        Assert.Contains("no-store", done.Headers.CacheControl?.ToString());
        Assert.Contains("no-store", refused.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "identity_not_found")]
    [InlineData(HttpStatusCode.Unauthorized, "sign_in_required")]
    [InlineData(HttpStatusCode.InternalServerError, "error")]
    public async Task Each_refused_unlink_has_its_own_fixed_outcome_and_leaks_no_Heimdall_text(HttpStatusCode status, string outcome)
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await ListedMember(host, "discord", "google");
        var start = await host.StartUnlinkAsync(member.Session, (string)host.Heimdall.Identities[0]["id"]!);
        host.Heimdall.UnlinkStatus = status;

        var done = await Finish(host, member.Session, start, member.Account, StepUpToken(host, member.Account), provider: "google");

        Assert.Equal($"{Page}?result={outcome}", Location(done));
        Assert.DoesNotContain("HEIMDALL FREE TEXT", string.Join("\n", host.Logs.Lines));
        var (_, html) = await host.GetLinkedAccountsAsync(member.Session);
        Assert.DoesNotContain("HEIMDALL FREE TEXT", html);
        // Only a definitive refusal (401 here) leaves the list shown; not found or an unreadable end may have changed the account, so it is dropped.
        Assert.Equal(status == HttpStatusCode.Unauthorized, html.Contains("Name of discord"));
    }

    [Fact]
    public async Task A_Heimdall_that_cannot_be_reached_for_the_unlink_changes_nothing()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await ListedMember(host, "discord", "google");
        var start = await host.StartUnlinkAsync(member.Session, (string)host.Heimdall.Identities[0]["id"]!);
        host.Heimdall.MemberFailure = new HttpRequestException("connection refused");

        var done = await Finish(host, member.Session, start, member.Account, StepUpToken(host, member.Account), provider: "google");

        Assert.Equal(Page + "?result=error", Location(done));
        Assert.DoesNotContain("Name of discord", (await host.GetLinkedAccountsAsync(member.Session)).Html);
    }

    [Fact]
    public async Task Without_a_Google_login_no_proof_can_be_had_and_the_page_says_so_without_asking_Heimdall()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await ListedMember(host, "discord", "patreon");
        var (_, html) = await host.GetLinkedAccountsAsync(member.Session);
        var starts = host.Heimdall.StartBodies.Count;

        var post = await host.StartUnlinkAsync(member.Session, (string)host.Heimdall.Identities[0]["id"]!);

        Assert.DoesNotContain(">Unlink<", html);
        Assert.Contains("fresh Google sign-in", html);
        Assert.Contains("no Google login", html);
        Assert.Equal(starts, host.Heimdall.StartBodies.Count);
        Assert.Equal(Page + "?result=proof_unavailable", Location(post.Response));
        Assert.Null(post.Cookie);
    }

    [Fact]
    public async Task A_Heimdall_with_no_Google_client_refuses_the_step_up_start_and_the_page_says_proof_is_unavailable_once()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await ListedMember(host, "discord", "google");
        host.Heimdall.StartStatus = HttpStatusCode.ServiceUnavailable;
        host.Heimdall.StartBody = """{"error":"provider_not_configured"}""";
        var starts = host.Heimdall.StartBodies.Count;

        var post = await host.StartUnlinkAsync(member.Session, (string)host.Heimdall.Identities[0]["id"]!);

        Assert.Equal(starts + 1, host.Heimdall.StartBodies.Count);
        Assert.Null(post.Cookie);
        Assert.Equal(Page + "?result=proof_unavailable", Location(post.Response));
        Assert.Contains("Fresh proof could not be started", (await host.GetLinkedAccountsAsync(member.Session, "?result=proof_unavailable")).Html);
        Assert.Equal(2, host.Heimdall.Identities.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000001")]
    public async Task An_identity_that_is_not_on_the_list_the_member_was_shown_starts_nothing(string? identityId)
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await ListedMember(host, "discord", "google");
        var starts = host.Heimdall.StartBodies.Count;

        var post = await host.PostLinkedAccountsAsync(
            member.Session, "Unlink", identityId is null ? null : new Dictionary<string, string> { ["identityId"] = identityId });

        Assert.Equal(starts, host.Heimdall.StartBodies.Count);
        Assert.Equal(Page + "?result=list_expired", Location(post.Response));
    }

    [Fact]
    public async Task An_unlink_after_the_list_expired_starts_nothing()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await ListedMember(host, "discord", "google");
        var id = (string)host.Heimdall.Identities[0]["id"]!;
        host.Clock.Advance(LinkedAccountsSnapshots.Lifetime + TimeSpan.FromSeconds(1));
        var starts = host.Heimdall.StartBodies.Count;

        var post = await host.StartUnlinkAsync(member.Session, id);

        Assert.Equal(starts, host.Heimdall.StartBodies.Count);
        Assert.Equal(Page + "?result=list_expired", Location(post.Response));
    }

    // ---- an attempt belongs to the account and browser that began it ----

    [Fact]
    public async Task An_unlink_attempt_held_by_another_accounts_session_does_nothing_and_asks_Heimdall_for_nothing()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await ListedMember(host, "discord", "google");
        var start = await host.StartUnlinkAsync(member.Session, (string)host.Heimdall.Identities[0]["id"]!);
        var otherAccount = HeimdallShapes.NewAccountId();
        var otherSession = await host.SignInSessionAsync(otherAccount);
        var redeems = host.Heimdall.RedeemCalls;

        var done = await Finish(host, member.Session, start, member.Account, StepUpToken(host, member.Account), provider: "google",
            sessionOverride: otherSession);

        Assert.Equal(Page + "?result=error", Location(done));
        Assert.Equal(redeems, host.Heimdall.RedeemCalls);
        Assert.Empty(host.Heimdall.Unlinks);
        Assert.Equal(2, host.Heimdall.Identities.Count);
    }

    [Fact]
    public async Task A_proof_by_another_account_unlinks_nothing()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await ListedMember(host, "discord", "google");
        var start = await host.StartUnlinkAsync(member.Session, (string)host.Heimdall.Identities[0]["id"]!);
        var stranger = HeimdallShapes.NewAccountId();

        var done = await Finish(host, member.Session, start, stranger, StepUpToken(host, stranger), provider: "google");

        Assert.Equal(Page + "?result=error", Location(done));
        Assert.Empty(host.Heimdall.Unlinks);
    }

    [Fact]
    public async Task A_list_proof_by_another_account_lists_nothing()
    {
        using var host = new HeimdallHost(realSessions: true);
        var account = HeimdallShapes.NewAccountId();
        var session = await host.SignInSessionAsync(account);
        host.Heimdall.AddIdentity("discord");
        var start = await host.StartListAsync(session);

        var done = await Finish(host, session, start, HeimdallShapes.NewAccountId());

        Assert.Equal(Page + "?result=error", Location(done));
        Assert.Empty(host.Heimdall.ListAuthorizations);
    }

    [Fact]
    public async Task A_consumed_unlink_attempt_cannot_be_replayed_into_a_second_unlink()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await ListedMember(host, "discord", "patreon", "google");
        var start = await host.StartUnlinkAsync(member.Session, (string)host.Heimdall.Identities[0]["id"]!);
        var fields = new Dictionary<string, string>
        {
            ["attempt"] = start.Nonce,
            ["code"] = host.CodeFor(start, member.Account, StepUpToken(host, member.Account), provider: "google")
        };
        var first = await host.PostCompleteAsync(fields, Jar(member.Session, start));
        Assert.Equal(Page + "?result=unlinked", Location(first));

        var replay = await host.PostCompleteAsync(fields, Jar(member.Session, start));

        Assert.Equal(Page + "?result=error", Location(replay));
        Assert.Single(host.Heimdall.Unlinks);
    }

    // ---- link another provider ----

    [Theory]
    [InlineData("discord")]
    [InlineData("patreon")]
    [InlineData("google")]
    public async Task Linking_each_admitted_provider_proves_the_account_then_starts_that_providers_link_with_the_bearer(string provider)
    {
        using var host = new HeimdallHost(realSessions: true);
        var account = HeimdallShapes.NewAccountId();
        var session = await host.SignInSessionAsync(account);
        var starts = host.Heimdall.StartBodies.Count;

        var start = await host.StartLinkAsync(session, provider);

        Assert.Equal(starts + 1, host.Heimdall.StartBodies.Count);
        Assert.Equal("/v1/oauth/discord/start", host.Heimdall.StartPaths[^1]);
        Assert.Null(host.Heimdall.StartAuthorizations[^1]);
        var token = host.Token(account);
        var step = await Finish(host, session, start, account, token);

        Assert.Equal(starts + 2, host.Heimdall.StartBodies.Count);
        Assert.Equal($"/v1/oauth/{provider}/start", host.Heimdall.StartPaths[^1]);
        Assert.Equal("Bearer " + token, host.Heimdall.StartAuthorizations[^1]);
        using var link = JsonDocument.Parse(host.Heimdall.StartBodies[^1]);
        Assert.Equal("link", link.RootElement.GetProperty("mode").GetString());
        Assert.Equal("bifrost", link.RootElement.GetProperty("appSlug").GetString());
        Assert.False(link.RootElement.TryGetProperty("stepUp", out _));
        Assert.Equal($"https://heimdall.test/oauth/{provider}/authorize?state=link-fixture", Location(step));
    }

    [Fact]
    public async Task A_proof_sign_in_goes_through_a_provider_the_account_still_holds()
    {
        using var host = new HeimdallHost(realSessions: true);
        // Signed in through Discord, which the account has since unlinked: signing in through it again would mint another account.
        var member = await ListedMember(host, "patreon", "google");

        var start = await host.StartLinkAsync(member.Session, "discord");

        Assert.Equal("/v1/oauth/patreon/start", host.Heimdall.StartPaths[^1]);
        Assert.NotNull(start.Cookie);
    }

    [Theory]
    [InlineData("twitch")]
    [InlineData("github")]
    [InlineData("DISCORD2")]
    [InlineData("")]
    public async Task A_provider_Heimdalls_bifrost_profile_does_not_admit_starts_nothing(string provider)
    {
        using var host = new HeimdallHost(realSessions: true);
        var session = await host.SignInSessionAsync(HeimdallShapes.NewAccountId());
        var starts = host.Heimdall.StartBodies.Count;

        var start = await host.StartLinkAsync(session, provider);

        Assert.Equal(starts, host.Heimdall.StartBodies.Count);
        Assert.Equal(Page + "?result=error", Location(start.Response));
        Assert.Null(start.Cookie);
    }

    [Fact]
    public async Task A_provider_that_attaches_in_the_callback_comes_back_with_a_link_completion_and_is_linked_once()
    {
        using var host = new HeimdallHost(realSessions: true);
        var account = HeimdallShapes.NewAccountId();
        var session = await host.SignInSessionAsync(account);
        var start = await host.StartLinkAsync(session, "patreon");
        var awaiting = host.AttemptFrom((await ContinueResponse(host, session, start, account)), host.Heimdall.StartBodies.Count - 1);
        Assert.Contains("\"Purpose\":6", Unprotect(host, awaiting.Cookie!));
        Assert.Contains("\"Target\":\"patreon\"", Unprotect(host, awaiting.Cookie!));
        host.Heimdall.AddIdentity("discord");
        var redeems = host.Heimdall.RedeemCalls;

        var done = await Finish(host, session, awaiting, account, mode: "link", provider: "patreon");

        Assert.Equal(Page + "?result=linked", Location(done));
        Assert.Equal(redeems + 1, host.Heimdall.RedeemCalls);
        Assert.Contains("now linked to your Heimdall account", (await host.GetLinkedAccountsAsync(session, "?result=linked")).Html);
        Assert.Null(HeimdallHost.CookieValue(done, BifrostCookies.Session));
        var replay = await host.PostCompleteAsync(
            new Dictionary<string, string> { ["attempt"] = awaiting.Nonce, ["code"] = "gone" }, Jar(session, awaiting));
        Assert.Equal(Page + "?result=error", Location(replay));
    }

    private static async Task<HttpResponseMessage> ContinueResponse(HeimdallHost host, string session, StartedAttempt start, string account) =>
        await Finish(host, session, start, account);

    [Theory]
    [InlineData("sign_in", "patreon")]
    [InlineData("link", "discord")]
    public async Task A_direct_link_completion_for_another_mode_or_provider_links_nothing(string mode, string provider)
    {
        using var host = new HeimdallHost(realSessions: true);
        var account = HeimdallShapes.NewAccountId();
        var session = await host.SignInSessionAsync(account);
        var start = await host.StartLinkAsync(session, "patreon");
        var before = host.Heimdall.StartBodies.Count;
        var awaiting = host.AttemptFrom(await ContinueResponse(host, session, start, account), before);

        var done = await Finish(host, session, awaiting, account, mode: mode, provider: provider);

        Assert.Equal(Page + "?result=error", Location(done));
    }

    [Fact]
    public async Task A_direct_link_completion_proven_by_another_account_links_nothing()
    {
        using var host = new HeimdallHost(realSessions: true);
        var account = HeimdallShapes.NewAccountId();
        var session = await host.SignInSessionAsync(account);
        var start = await host.StartLinkAsync(session, "discord");
        var before = host.Heimdall.StartBodies.Count;
        var awaiting = host.AttemptFrom(await ContinueResponse(host, session, start, account), before);

        var done = await Finish(host, session, awaiting, HeimdallShapes.NewAccountId(), mode: "link", provider: "discord");

        Assert.Equal(Page + "?result=error", Location(done));
    }

    [Fact]
    public async Task A_link_attempt_cookie_written_before_targets_existed_still_links_Google()
    {
        using var host = new HeimdallHost(realSessions: true);
        var account = HeimdallShapes.NewAccountId();
        var session = await host.SignInSessionAsync(account);
        var nonce = "legacy-" + Guid.NewGuid().ToString("N");
        var protector = host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("Bifrost.Heimdall.Attempt.v1");
        var legacy = protector.Protect(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["Nonce"] = nonce, ["Provider"] = "discord", ["RequiresMemberAccess"] = false, ["ReturnPath"] = "/Membership/Status",
            ["ExpiresAt"] = host.Clock.Now.AddMinutes(5), ["Purpose"] = 1, ["ExpectedAccountId"] = account
        }));
        var attempt = new StartedAttempt(
            nonce, HeimdallAttemptCookie.SlotNames[0], legacy, $"https://localhost/auth/heimdall/complete?attempt={nonce}", null!);

        var step = await Finish(host, session, attempt, account);

        Assert.Equal("/v1/oauth/google/start", host.Heimdall.StartPaths[^1]);
        Assert.Equal("https://heimdall.test/oauth/google/authorize?state=link-fixture", Location(step));
    }

    // ---- the page and its neighbours ----

    [Fact]
    public async Task Status_points_a_Heimdall_member_to_the_page_and_no_longer_starts_a_link_itself()
    {
        using var host = new HeimdallHost(realSessions: true);
        var session = await host.SignInSessionAsync(HeimdallShapes.NewAccountId());
        using var client = host.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Membership/Status");
        request.Headers.Add("Cookie", $"{BifrostCookies.Session}={session}");

        var html = await (await client.SendAsync(request)).Content.ReadAsStringAsync();

        Assert.Contains(Page, html);
        Assert.DoesNotContain("handler=LinkGoogle", html);
        Assert.DoesNotContain("LinkGoogle", html);
    }

    [Theory]
    [InlineData("linked")]
    [InlineData("identity_linked_elsewhere")]
    [InlineData("expired")]
    [InlineData("unlinked")]
    [InlineData("last_identity")]
    [InlineData("step_up_required")]
    [InlineData("identity_not_found")]
    [InlineData("sign_in_required")]
    [InlineData("proof_unavailable")]
    [InlineData("list_expired")]
    [InlineData("provider_needed")]
    [InlineData("error")]
    public void Every_outcome_code_has_words_and_nothing_else_does(string code)
    {
        Assert.False(string.IsNullOrWhiteSpace(HeimdallLinkOutcomes.Wording(code)));
        Assert.Null(HeimdallLinkOutcomes.Wording(code + " "));
        Assert.Null(HeimdallLinkOutcomes.Wording("<b>" + code));
        Assert.Contains(
            Enum.GetValues<HeimdallLinkOutcome>(), outcome => HeimdallLinkOutcomes.Code(outcome) == code);
    }

    [Fact]
    public async Task Link_refusals_say_the_login_belongs_to_another_account_without_naming_it()
    {
        using var host = new HeimdallHost(realSessions: true);
        var session = await host.SignInSessionAsync(HeimdallShapes.NewAccountId());

        var (_, html) = await host.GetLinkedAccountsAsync(session, "?result=identity_linked_elsewhere");

        Assert.Contains("That login belongs to another account. Nothing was linked.", html);
        Assert.DoesNotContain("merge", html.Replace("nothing is merged", string.Empty), StringComparison.OrdinalIgnoreCase);
    }
}
