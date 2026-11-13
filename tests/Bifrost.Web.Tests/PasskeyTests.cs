using System.Net;
using System.Text.Json;
using Bifrost.Web.Features.Heimdall;
using Bifrost.Web.Features.Passkeys;
using Bifrost.Web.Features.Shared;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.Web.Tests;

/// <summary>
/// Passkeys on the Linked accounts page from the member's side: a fake Heimdall that models the step-up route of its service contract,
/// and a software authenticator that makes real attestations and assertions. Enrolment needs a fresh Heimdall sign-in and cools off for
/// 48 hours; unlinking with a strong passkey verifies an assertion at Bifrost, then asks Heimdall for an app-asserted step-up inside the
/// completion that holds the bearer.
/// </summary>
public sealed class PasskeyTests
{
    private const string Page = "/Membership/LinkedAccounts";

    private sealed record Member(string Account, string Session, string Discord, string Patreon);

    private static string? Location(HttpResponseMessage response) => response.Headers.Location?.OriginalString;

    private static string Ticket(string ticket) => $"{BifrostCookies.PasskeyTicket}={ticket}";

    private static async Task<HttpResponseMessage> Finish(HeimdallHost host, Member member, StartedAttempt attempt, string? token = null, string? account = null)
    {
        var code = host.CodeFor(attempt, account ?? member.Account, token);
        return await host.PostCompleteAsync(
            new Dictionary<string, string> { ["attempt"] = attempt.Nonce, ["code"] = code },
            $"{BifrostCookies.Session}={member.Session}; {attempt.CookieName}={attempt.Cookie}");
    }

    /// <summary>A signed-in member whose Heimdall account holds Discord and Patreon (and Google when asked), with the list read.</summary>
    private static async Task<Member> NewMember(HeimdallHost host, bool google = false)
    {
        var account = HeimdallShapes.NewAccountId();
        var session = await host.SignInSessionAsync(account);
        var discord = host.Heimdall.AddIdentity("discord", "D");
        var patreon = host.Heimdall.AddIdentity("patreon", "P");
        if (google)
        {
            host.Heimdall.AddIdentity("google", "G");
        }

        var member = new Member(account, session, discord, patreon);
        await ReadList(host, member);
        return member;
    }

    private static async Task ReadList(HeimdallHost host, Member member)
    {
        var start = await host.StartListAsync(member.Session, "discord");
        var done = await Finish(host, member, start);
        Assert.Equal(Page + "?result=listed", Location(done));
    }

    private static async Task<string> JsonOf(StartedAttempt call) => await call.Response.Content.ReadAsStringAsync();

    /// <summary>The fresh sign-in that earns the ticket; returns the ticket cookie value.</summary>
    private static async Task<string> SignInForEnrolment(HeimdallHost host, Member member)
    {
        var before = host.Heimdall.StartBodies.Count;
        var start = await host.PostLinkedAccountsAsync(member.Session, "PasskeyEnrolStart");
        Assert.Equal(before + 1, host.Heimdall.StartBodies.Count);
        var done = await Finish(host, member, start);
        Assert.Equal(Page + "?result=enrol_ready", Location(done));
        return HeimdallHost.CookieValue(done, BifrostCookies.PasskeyTicket)!;
    }

    private static async Task<HttpResponseMessage> Enrol(HeimdallHost host, Member member, SoftwareAuthenticator authenticator, string ticket, string label = "Test key")
    {
        var options = await host.PostLinkedAccountsAsync(member.Session, "PasskeyEnrolOptions", extraCookies: Ticket(ticket));
        Assert.Equal(HttpStatusCode.OK, options.Response.StatusCode);
        var fields = authenticator.Register(await JsonOf(options), label);
        return (await host.PostLinkedAccountsAsync(member.Session, "PasskeyEnrol", fields, extraCookies: Ticket(ticket))).Response;
    }

    private static async Task AddPasskey(HeimdallHost host, Member member, SoftwareAuthenticator authenticator)
    {
        var ticket = await SignInForEnrolment(host, member);
        var done = await Enrol(host, member, authenticator, ticket);
        Assert.Equal(Page + "?result=passkey_added", Location(done));
    }

    /// <summary>Waits out the cooling-off and reads the list again (the list lives five minutes).</summary>
    private static async Task CoolOff(HeimdallHost host, Member member)
    {
        host.Clock.Advance(PasskeyService.CoolingOff);
        await ReadList(host, member);
    }

    private static async Task<(string Json, StartedAttempt Call)> Challenge(HeimdallHost host, Member member, string identityId)
    {
        var call = await host.PostLinkedAccountsAsync(member.Session, "PasskeyChallenge", new Dictionary<string, string> { ["identityId"] = identityId });
        return (await JsonOf(call), call);
    }

    private static async Task<StartedAttempt> Unlink(HeimdallHost host, Member member, SoftwareAuthenticator authenticator, string identityId)
    {
        var (json, call) = await Challenge(host, member, identityId);
        Assert.Equal(HttpStatusCode.OK, call.Response.StatusCode);
        return await PostAssertion(host, member, authenticator.Assert(json), identityId);
    }

    private static Task<StartedAttempt> PostAssertion(HeimdallHost host, Member member, Dictionary<string, string> fields, string identityId)
    {
        fields["identityId"] = identityId;
        return host.PostLinkedAccountsAsync(member.Session, "UnlinkPasskey", fields);
    }

    private static string ErrorOf(string json) => JsonDocument.Parse(json).RootElement.GetProperty("error").GetString()!;

    private static Task<List<Bifrost.Web.Domain.PasskeyCredential>> Credentials(HeimdallHost host) =>
        host.Db(db => db.PasskeyCredentials.ToListAsync());

    // ---- enrolment ----

    [Theory]
    [InlineData(SoftwareAuthenticator.Kind.Es256)]
    [InlineData(SoftwareAuthenticator.Kind.EdDsa)]
    public async Task A_passkey_is_enrolled_after_a_fresh_ordinary_sign_in_and_starts_cooling_off(SoftwareAuthenticator.Kind kind)
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await NewMember(host);
        var authenticator = new SoftwareAuthenticator(kind);

        var ticket = await SignInForEnrolment(host, member);
        var done = await Enrol(host, member, authenticator, ticket, "My <b>laptop</b>");

        Assert.Equal(Page + "?result=passkey_added", Location(done));
        var stored = Assert.Single(await Credentials(host));
        Assert.Equal(authenticator.CredentialId, stored.CredentialId);
        Assert.Equal(host.Clock.Now, stored.CreatedUtc);
        Assert.Equal(host.Clock.Now + TimeSpan.FromHours(48), stored.StrongFromUtc);
        Assert.Null(stored.RevokedUtc);
        Assert.Equal("My <b>laptop</b>", stored.Label);
        Assert.Equal((await host.Account(member.Account))!.Id, stored.UserAccountId);
        // The sign-in that earned the ticket was ordinary (no step-up) through a provider the account holds, and minted nothing.
        Assert.DoesNotContain("stepUp", host.Heimdall.StartBodies[^1]);
        Assert.Empty(host.Heimdall.OrphanMints);
        var (_, html) = await host.GetLinkedAccountsAsync(member.Session);
        Assert.Contains("cooling off until", html);
        Assert.Contains("cannot unlink yet", html);
        Assert.Contains("My &lt;b&gt;laptop&lt;/b&gt;", html);
        Assert.DoesNotContain("My <b>laptop</b>", html);
    }

    [Fact]
    public async Task A_session_alone_cannot_start_an_enrolment_because_the_ticket_needs_a_fresh_sign_in()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await NewMember(host);

        var options = await host.PostLinkedAccountsAsync(member.Session, "PasskeyEnrolOptions");
        var forged = await host.PostLinkedAccountsAsync(member.Session, "PasskeyEnrolOptions", extraCookies: Ticket("not-a-ticket-0123456789abcdefghijklmnopqrstuvw"));

        Assert.Equal(HttpStatusCode.BadRequest, options.Response.StatusCode);
        Assert.Equal("enrol_needs_sign_in", ErrorOf(await JsonOf(options)));
        Assert.Equal("enrol_needs_sign_in", ErrorOf(await JsonOf(forged)));
        Assert.Empty(await Credentials(host));
    }

    [Fact]
    public async Task A_registration_without_a_ticket_is_refused_even_with_a_valid_challenge()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await NewMember(host);
        var authenticator = new SoftwareAuthenticator();
        var ticket = await SignInForEnrolment(host, member);
        var options = await host.PostLinkedAccountsAsync(member.Session, "PasskeyEnrolOptions", extraCookies: Ticket(ticket));

        var fields = authenticator.Register(await JsonOf(options));
        var withoutTicket = await host.PostLinkedAccountsAsync(member.Session, "PasskeyEnrol", fields);

        Assert.Equal(Page + "?result=enrol_needs_sign_in", Location(withoutTicket.Response));
        Assert.Empty(await Credentials(host));
    }

    [Fact]
    public async Task A_ticket_is_single_use_and_does_not_work_for_another_session()
    {
        using var host = new HeimdallHost(realSessions: true);
        var victim = await NewMember(host);
        var other = await NewMember(host);
        var ticket = await SignInForEnrolment(host, victim);

        var otherSession = await host.PostLinkedAccountsAsync(other.Session, "PasskeyEnrolOptions", extraCookies: Ticket(ticket));
        var done = await Enrol(host, victim, new SoftwareAuthenticator(), ticket);
        var again = await host.PostLinkedAccountsAsync(victim.Session, "PasskeyEnrolOptions", extraCookies: Ticket(ticket));

        Assert.Equal("enrol_needs_sign_in", ErrorOf(await JsonOf(otherSession)));
        Assert.Equal(Page + "?result=passkey_added", Location(done));
        Assert.Equal("enrol_needs_sign_in", ErrorOf(await JsonOf(again)));
        Assert.Single(await Credentials(host));
    }

    [Fact]
    public async Task The_ticket_expires_after_five_minutes()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await NewMember(host);
        var ticket = await SignInForEnrolment(host, member);

        host.Clock.Advance(PasskeyCeremonies.TicketLifetime + TimeSpan.FromSeconds(1));
        var options = await host.PostLinkedAccountsAsync(member.Session, "PasskeyEnrolOptions", extraCookies: Ticket(ticket));

        Assert.Equal("enrol_needs_sign_in", ErrorOf(await JsonOf(options)));
    }

    [Fact]
    public async Task The_enrolment_sign_in_proven_by_another_account_earns_no_ticket()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await NewMember(host);
        var start = await host.PostLinkedAccountsAsync(member.Session, "PasskeyEnrolStart");

        var done = await Finish(host, member, start, account: HeimdallShapes.NewAccountId());

        Assert.Null(HeimdallHost.CookieValue(done, BifrostCookies.PasskeyTicket));
        Assert.Equal(Page + "?result=error", Location(done));
    }

    [Fact]
    public async Task The_ticket_cookie_is_host_only_http_only_and_short_lived()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await NewMember(host);
        var start = await host.PostLinkedAccountsAsync(member.Session, "PasskeyEnrolStart");

        var done = await Finish(host, member, start);

        var header = HeimdallHost.SetCookieHeader(done, BifrostCookies.PasskeyTicket)!.ToLowerInvariant();
        Assert.StartsWith("__host-bifrost_passkey_ticket=", header);
        Assert.Contains("secure", header);
        Assert.Contains("httponly", header);
        Assert.Contains("path=/", header);
        Assert.Contains("max-age=300", header);
        Assert.Contains("samesite=lax", header);
        Assert.DoesNotContain("domain", header);
    }

    [Fact]
    public async Task A_registration_from_another_origin_or_for_another_RP_or_without_user_verification_is_refused()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await NewMember(host);

        foreach (var tamper in new Action<SoftwareAuthenticator>[]
                 {
                     a => a.Origin = "https://bifrost.evil.example",
                     a => a.RpId = "evil.example",
                     a => a.UserVerified = false
                 })
        {
            var authenticator = new SoftwareAuthenticator();
            tamper(authenticator);
            var ticket = await SignInForEnrolment(host, member);
            var done = await Enrol(host, member, authenticator, ticket);
            Assert.Equal(Page + "?result=passkey_failed", Location(done));
        }

        Assert.Empty(await Credentials(host));
    }

    [Fact]
    public async Task A_registration_answering_an_older_challenge_is_refused()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await NewMember(host);
        var ticket = await SignInForEnrolment(host, member);
        var first = await JsonOf(await host.PostLinkedAccountsAsync(member.Session, "PasskeyEnrolOptions", extraCookies: Ticket(ticket)));
        await host.PostLinkedAccountsAsync(member.Session, "PasskeyEnrolOptions", extraCookies: Ticket(ticket));

        var fields = new SoftwareAuthenticator().Register(first);
        var done = await host.PostLinkedAccountsAsync(member.Session, "PasskeyEnrol", fields, extraCookies: Ticket(ticket));

        Assert.Equal(Page + "?result=passkey_failed", Location(done.Response));
        Assert.Empty(await Credentials(host));
    }

    [Fact]
    public async Task A_credential_id_another_account_already_holds_cannot_be_enrolled_again()
    {
        using var host = new HeimdallHost(realSessions: true);
        var first = await NewMember(host);
        var second = await NewMember(host);
        var authenticator = new SoftwareAuthenticator();
        await AddPasskey(host, first, authenticator);

        var ticket = await SignInForEnrolment(host, second);
        var done = await Enrol(host, second, authenticator, ticket);

        Assert.Equal(Page + "?result=passkey_failed", Location(done));
        Assert.Single(await Credentials(host));
    }

    [Fact]
    public async Task An_account_holds_at_most_five_passkeys_and_a_sixth_starts_no_sign_in()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await NewMember(host);
        for (var i = 0; i < PasskeyService.MaxActive; i++)
        {
            await AddPasskey(host, member, new SoftwareAuthenticator());
            await ReadList(host, member);
        }

        var starts = host.Heimdall.StartBodies.Count;
        var sixth = await host.PostLinkedAccountsAsync(member.Session, "PasskeyEnrolStart");
        var (_, html) = await host.GetLinkedAccountsAsync(member.Session);

        Assert.Equal(Page + "?result=passkey_limit", Location(sixth.Response));
        Assert.Equal(starts, host.Heimdall.StartBodies.Count);
        Assert.Contains("most passkeys an account can hold", html);
    }

    [Fact]
    public async Task Revoking_a_passkey_needs_no_strong_proof_is_the_members_own_and_frees_the_slot()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await NewMember(host);
        var stranger = await NewMember(host);
        await AddPasskey(host, member, new SoftwareAuthenticator());
        var id = (await Credentials(host)).Single().Id;

        var byStranger = await host.PostLinkedAccountsAsync(stranger.Session, "PasskeyRevoke", new Dictionary<string, string> { ["credentialId"] = id.ToString() });
        Assert.Equal(Page + "?result=passkey_failed", Location(byStranger.Response));
        Assert.Null((await Credentials(host)).Single().RevokedUtc);

        var revoke = await host.PostLinkedAccountsAsync(member.Session, "PasskeyRevoke", new Dictionary<string, string> { ["credentialId"] = id.ToString() });

        Assert.Equal(Page + "?result=passkey_revoked", Location(revoke.Response));
        Assert.Equal(host.Clock.Now, (await Credentials(host)).Single().RevokedUtc);
        var (_, html) = await host.GetLinkedAccountsAsync(member.Session);
        Assert.DoesNotContain("cooling off until", html);
    }

    [Fact]
    public async Task Ceremonies_are_rate_limited_per_account()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await NewMember(host);

        StartedAttempt? last = null;
        for (var i = 0; i < PasskeyRateLimit.Permits + 1; i++)
        {
            last = await host.PostLinkedAccountsAsync(member.Session, "PasskeyChallenge", new Dictionary<string, string> { ["identityId"] = member.Patreon });
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.Response.StatusCode);
        Assert.Equal("rate_limited", ErrorOf(await JsonOf(last)));
    }

    [Theory]
    [InlineData("PasskeyEnrolStart")]
    [InlineData("PasskeyEnrolOptions")]
    [InlineData("PasskeyEnrol")]
    [InlineData("PasskeyRevoke")]
    [InlineData("PasskeyChallenge")]
    [InlineData("UnlinkPasskey")]
    public async Task Every_passkey_post_needs_the_antiforgery_token(string handler)
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await NewMember(host);
        var starts = host.Heimdall.StartBodies.Count;

        var call = await host.PostLinkedAccountsAsync(member.Session, handler, new Dictionary<string, string> { ["identityId"] = member.Patreon }, antiforgery: false);

        Assert.Equal(HttpStatusCode.BadRequest, call.Response.StatusCode);
        Assert.Equal(starts, host.Heimdall.StartBodies.Count);
        Assert.Empty(host.Heimdall.StepUps);
    }

    [Fact]
    public async Task Passkeys_are_unavailable_unless_the_origin_is_https_and_its_host_is_the_expected_one()
    {
        foreach (var publicBase in new[] { "http://localhost", "https://other.example" })
        {
            using var host = new HeimdallHost(realSessions: true, publicBaseUrl: publicBase);
            var member = await NewMember(host);

            var options = await host.PostLinkedAccountsAsync(member.Session, "PasskeyEnrolOptions");
            var start = await host.PostLinkedAccountsAsync(member.Session, "PasskeyEnrolStart");
            var (_, html) = await host.GetLinkedAccountsAsync(member.Session);

            Assert.Equal("passkeys_unavailable", ErrorOf(await JsonOf(options)));
            Assert.Equal(Page + "?result=passkeys_unavailable", Location(start.Response));
            Assert.Contains("Passkeys are not available on this server", html);
            Assert.DoesNotContain("passkeys.js", html);
        }
    }

    [Fact]
    public async Task A_session_with_no_Heimdall_account_has_no_passkeys_and_every_ceremony_refuses()
    {
        using var host = new HeimdallHost(realSessions: true);
        var attempt = await host.StartAsync(provider: "discord");
        var native = (await host.PostLinkedAccountsAsync(null, "PasskeyEnrolOptions")).Response;

        Assert.NotEqual(HttpStatusCode.OK, native.StatusCode);
        Assert.Empty(await Credentials(host));
        Assert.NotNull(attempt);
    }

    // ---- assertion and unlink ----

    [Theory]
    [InlineData(SoftwareAuthenticator.Kind.Es256)]
    [InlineData(SoftwareAuthenticator.Kind.EdDsa)]
    public async Task A_strong_passkey_unlinks_through_an_app_asserted_step_up_inside_the_completion_that_holds_the_bearer(SoftwareAuthenticator.Kind kind)
    {
        using var host = new HeimdallHost(realSessions: true, traceHttp: true);
        var member = await NewMember(host);
        var authenticator = new SoftwareAuthenticator(kind);
        await AddPasskey(host, member, authenticator);
        await CoolOff(host, member);
        var starts = host.Heimdall.StartBodies.Count;

        var attempt = await Unlink(host, member, authenticator, member.Patreon);

        // The assertion alone starts an ordinary fresh sign-in (no step-up asked of a provider) and asks Heimdall for nothing else yet.
        Assert.Equal(starts + 1, host.Heimdall.StartBodies.Count);
        Assert.DoesNotContain("stepUp", host.Heimdall.StartBodies[^1]);
        Assert.Empty(host.Heimdall.StepUps);
        Assert.Empty(host.Heimdall.Unlinks);
        Assert.Empty(host.Heimdall.OrphanMints);

        var bearer = host.Token(member.Account);
        var done = await Finish(host, member, attempt, bearer);

        Assert.Equal(Page + "?result=unlinked", Location(done));
        var stepUp = Assert.Single(host.Heimdall.StepUps);
        Assert.Equal(HeimdallHost.AppSecret, stepUp.Secret);
        Assert.Equal("Bearer " + bearer, stepUp.Authorization);
        var body = JsonDocument.Parse(stepUp.Body).RootElement;
        Assert.Equal("bifrost-passkey", body.GetProperty("method").GetString());
        Assert.Matches("^[A-Za-z0-9_-]{43}$", body.GetProperty("nonce").GetString()!);
        var unlink = Assert.Single(host.Heimdall.Unlinks);
        Assert.Equal(member.Patreon, unlink.Id);
        Assert.NotEqual("Bearer " + bearer, unlink.Authorization);
        var minted = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(
            Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(unlink.Authorization!["Bearer ".Length..].Split('.')[1]))).RootElement;
        Assert.Equal("bifrost-passkey", minted.GetProperty("step_up_method").GetString());
        Assert.DoesNotContain(host.Heimdall.Identities, x => (string?)x["id"] == member.Patreon);

        // No bearer, minted or not, reaches a cookie, a redirect, a page or a log.
        var minted2 = unlink.Authorization["Bearer ".Length..];
        var seen = string.Join("\n", done.Headers.Select(x => x.Key + "=" + string.Join(",", x.Value))) + "\n" + attempt.Response.Headers
            + "\n" + string.Join("\n", host.Logs.Lines) + "\n" + (await host.GetLinkedAccountsAsync(member.Session)).Html;
        Assert.DoesNotContain(bearer, seen);
        Assert.DoesNotContain(minted2, seen);
        Assert.DoesNotContain("PUID-CANARY", seen);
        var held = host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("Bifrost.Heimdall.Attempt.v1").Unprotect(attempt.Cookie!);
        Assert.DoesNotContain("eyJ", held);
    }

    [Fact]
    public async Task A_stolen_session_cannot_unlink_without_the_passkey()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await NewMember(host);
        var authenticator = new SoftwareAuthenticator();
        await AddPasskey(host, member, authenticator);
        await CoolOff(host, member);
        var starts = host.Heimdall.StartBodies.Count;

        // No challenge was ever asked for, or the thief signs with its own key: nothing is verified, no sign-in starts.
        var noChallenge = await PostAssertion(host, member, new Dictionary<string, string>(), member.Patreon);
        var (json, _) = await Challenge(host, member, member.Patreon);
        var thief = new SoftwareAuthenticator();
        var fields = thief.Assert(json);
        var withOwnKey = await PostAssertion(host, member, fields, member.Patreon);
        var viaGoogleRoute = await host.StartUnlinkAsync(member.Session, member.Patreon);

        Assert.Equal(Page + "?result=passkey_failed", Location(noChallenge.Response));
        Assert.Equal(Page + "?result=passkey_failed", Location(withOwnKey.Response));
        Assert.Equal(Page + "?result=passkey_required", Location(viaGoogleRoute.Response));
        Assert.Equal(starts, host.Heimdall.StartBodies.Count);
        Assert.Empty(host.Heimdall.StepUps);
        Assert.Empty(host.Heimdall.Unlinks);
    }

    [Fact]
    public async Task An_assertion_cannot_be_replayed_and_a_double_submit_starts_one_sign_in()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await NewMember(host);
        var authenticator = new SoftwareAuthenticator();
        await AddPasskey(host, member, authenticator);
        await CoolOff(host, member);
        var (json, _) = await Challenge(host, member, member.Patreon);
        var fields = authenticator.Assert(json);
        var starts = host.Heimdall.StartBodies.Count;

        var both = await Task.WhenAll(PostAssertion(host, member, new(fields), member.Patreon), PostAssertion(host, member, new(fields), member.Patreon));
        var replay = await PostAssertion(host, member, new(fields), member.Patreon);

        Assert.Equal(starts + 1, host.Heimdall.StartBodies.Count);
        Assert.Single(both, x => Location(x.Response)!.StartsWith("https://heimdall.test"));
        Assert.Equal(Page + "?result=passkey_expired", Location(replay.Response));
    }

    [Fact]
    public async Task A_challenge_for_one_identity_does_not_unlink_another()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await NewMember(host, google: true);
        var authenticator = new SoftwareAuthenticator();
        await AddPasskey(host, member, authenticator);
        await CoolOff(host, member);
        var (json, _) = await Challenge(host, member, member.Patreon);
        var starts = host.Heimdall.StartBodies.Count;

        var other = await PostAssertion(host, member, authenticator.Assert(json), member.Discord);

        Assert.Equal(Page + "?result=passkey_expired", Location(other.Response));
        Assert.Equal(starts, host.Heimdall.StartBodies.Count);
    }

    [Fact]
    public async Task An_assertion_for_another_account_or_origin_or_without_user_verification_is_refused()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await NewMember(host);
        var authenticator = new SoftwareAuthenticator();
        await AddPasskey(host, member, authenticator);
        await CoolOff(host, member);
        var other = await NewMember(host);
        var otherKey = new SoftwareAuthenticator();
        await AddPasskey(host, other, otherKey);
        await CoolOff(host, other);
        await ReadList(host, member);
        var starts = host.Heimdall.StartBodies.Count;

        // Another account's session, with its own strong passkey, presents this member's credential for its own challenge.
        var (otherJson, _) = await Challenge(host, other, other.Patreon);
        Assert.Contains("challenge", otherJson);
        var borrowed = await PostAssertion(host, other, authenticator.Assert(otherJson), other.Patreon);
        Assert.Equal(Page + "?result=passkey_failed", Location(borrowed.Response));

        foreach (var tamper in new Action<SoftwareAuthenticator>[]
                 {
                     a => a.Origin = "https://bifrost.evil.example",
                     a => a.RpId = "evil.example",
                     a => a.UserVerified = false
                 })
        {
            var (json, _) = await Challenge(host, member, member.Patreon);
            var tampered = authenticator.Assert(json);
            var clone = new SoftwareAuthenticator();
            // Same key material is needed to sign, so tamper on the real authenticator and restore afterwards.
            var origin = (authenticator.Origin, authenticator.RpId, authenticator.UserVerified);
            tamper(authenticator);
            tampered = authenticator.Assert(json);
            (authenticator.Origin, authenticator.RpId, authenticator.UserVerified) = origin;
            var done = await PostAssertion(host, member, tampered, member.Patreon);
            Assert.Equal(Page + "?result=passkey_failed", Location(done.Response));
            Assert.NotNull(clone);
        }

        Assert.Equal(starts, host.Heimdall.StartBodies.Count);
    }

    [Fact]
    public async Task A_sign_count_that_does_not_advance_is_refused_as_a_cloned_authenticator()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await NewMember(host, google: true);
        var authenticator = new SoftwareAuthenticator();
        await AddPasskey(host, member, authenticator);
        await CoolOff(host, member);

        authenticator.SignCount = 4; // the next use is 5
        var first = await Unlink(host, member, authenticator, member.Patreon);
        Assert.StartsWith("https://heimdall.test", Location(first.Response));
        Assert.Equal(5, (await Credentials(host)).Single().SignCount);
        Assert.NotNull((await Credentials(host)).Single().LastUsedUtc);

        authenticator.SignCount = 4; // a use that counts 5 again
        var (json, _) = await Challenge(host, member, member.Discord);
        var same = await PostAssertion(host, member, authenticator.Assert(json), member.Discord);
        authenticator.SignCount = 2; // counts 3: lower
        var (json2, _) = await Challenge(host, member, member.Discord);
        var lower = await PostAssertion(host, member, authenticator.Assert(json2), member.Discord);
        authenticator.SignCount = 5; // counts 6: higher
        var (json3, _) = await Challenge(host, member, member.Discord);
        var higher = await PostAssertion(host, member, authenticator.Assert(json3), member.Discord);

        Assert.Equal(Page + "?result=passkey_failed", Location(same.Response));
        Assert.Equal(Page + "?result=passkey_failed", Location(lower.Response));
        Assert.StartsWith("https://heimdall.test", Location(higher.Response));
        Assert.Equal(6, (await Credentials(host)).Single().SignCount);
    }

    [Fact]
    public async Task A_passkey_counts_as_strong_exactly_at_forty_eight_hours()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await NewMember(host);
        var authenticator = new SoftwareAuthenticator();
        await AddPasskey(host, member, authenticator);

        host.Clock.Advance(PasskeyService.CoolingOff - TimeSpan.FromSeconds(1));
        await ReadList(host, member);
        var (_, early) = await host.GetLinkedAccountsAsync(member.Session);
        var (earlyJson, _) = await Challenge(host, member, member.Patreon);
        var earlyGoogle = await host.StartUnlinkAsync(member.Session, member.Patreon);

        Assert.Contains("cooling off until", early);
        Assert.DoesNotContain("Unlink with passkey", early);
        Assert.Contains("Unlinking needs strong proof", early);
        Assert.Contains("(your newest one counts from", early);
        Assert.Equal("passkey_required", ErrorOf(earlyJson));
        Assert.Equal(Page + "?result=proof_unavailable", Location(earlyGoogle.Response));

        host.Clock.Advance(TimeSpan.FromSeconds(1));
        await ReadList(host, member);
        var (_, exact) = await host.GetLinkedAccountsAsync(member.Session);
        var attempt = await Unlink(host, member, authenticator, member.Patreon);

        Assert.Contains("Unlink with passkey", exact);
        Assert.DoesNotContain("cooling off until", exact);
        Assert.StartsWith("https://heimdall.test", Location(attempt.Response));
    }

    [Fact]
    public async Task A_revoked_passkey_is_not_strong_and_Google_proof_returns()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await NewMember(host, google: true);
        var authenticator = new SoftwareAuthenticator();
        await AddPasskey(host, member, authenticator);
        await CoolOff(host, member);
        var (json, _) = await Challenge(host, member, member.Patreon);
        var fields = authenticator.Assert(json);
        var id = (await Credentials(host)).Single().Id.ToString();
        await host.PostLinkedAccountsAsync(member.Session, "PasskeyRevoke", new Dictionary<string, string> { ["credentialId"] = id });
        var starts = host.Heimdall.StartBodies.Count;

        var withRevoked = await PostAssertion(host, member, fields, member.Patreon);
        var (_, html) = await host.GetLinkedAccountsAsync(member.Session);

        Assert.Equal(Page + "?result=passkey_failed", Location(withRevoked.Response));
        Assert.Equal(starts, host.Heimdall.StartBodies.Count);
        Assert.DoesNotContain("Unlink with passkey", html);
        Assert.Contains(">Unlink</button>", html);

        // A challenge asked for after the revoke is refused, and one answered by a key revoked in between does not verify.
        var (after, _) = await Challenge(host, member, member.Patreon);
        Assert.Equal("passkey_required", ErrorOf(after));
    }

    [Fact]
    public async Task A_credential_revoked_between_challenge_and_assertion_does_not_verify()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await NewMember(host);
        var authenticator = new SoftwareAuthenticator();
        await AddPasskey(host, member, authenticator);
        await CoolOff(host, member);
        var (json, _) = await Challenge(host, member, member.Patreon);
        await host.PostLinkedAccountsAsync(member.Session, "PasskeyRevoke", new Dictionary<string, string> { ["credentialId"] = (await Credentials(host)).Single().Id.ToString() });
        var starts = host.Heimdall.StartBodies.Count;

        var done = await PostAssertion(host, member, authenticator.Assert(json), member.Patreon);

        Assert.Equal(Page + "?result=passkey_failed", Location(done.Response));
        Assert.Equal(starts, host.Heimdall.StartBodies.Count);
    }

    [Fact]
    public async Task The_assertion_fact_expires_and_is_spent_once_and_without_it_Heimdall_is_never_asked()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await NewMember(host);
        var authenticator = new SoftwareAuthenticator();
        await AddPasskey(host, member, authenticator);
        await CoolOff(host, member);

        // Expired: the fresh sign-in comes back after the fact's five minutes.
        var late = await Unlink(host, member, authenticator, member.Patreon);
        host.Clock.Advance(PasskeyCeremonies.ProofLifetime + TimeSpan.FromSeconds(1));
        var lateDone = await Finish(host, member, late);
        Assert.Equal(Page + "?result=passkey_expired", Location(lateDone));

        // Spent once: the attempt cookie can be presented again with another code, but the fact is gone.
        await ReadList(host, member);
        authenticator.SignCount = 2;
        var once = await Unlink(host, member, authenticator, member.Patreon);
        var first = await Finish(host, member, once);
        var second = await Finish(host, member, once);
        Assert.Equal(Page + "?result=unlinked", Location(first));
        Assert.Equal(Page + "?result=passkey_expired", Location(second));

        Assert.Single(host.Heimdall.StepUps);
        Assert.Single(host.Heimdall.Unlinks);
    }

    [Fact]
    public async Task The_fact_is_bound_to_the_identity_and_account_it_was_made_for()
    {
        var ceremonies = new PasskeyCeremonies(new TestClock());
        var fact = ceremonies.Issue(CeremonyKind.UnlinkProof, "account-a", "identity-1", null, TimeSpan.FromMinutes(5))!;

        Assert.Null(ceremonies.Take(CeremonyKind.UnlinkProof, "account-b", "identity-1", fact));
        Assert.Null(ceremonies.Take(CeremonyKind.UnlinkProof, "account-a", "identity-2", fact));
        Assert.Null(ceremonies.Take(CeremonyKind.EnrolTicket, "account-a", "identity-1", fact));
        Assert.NotNull(ceremonies.Take(CeremonyKind.UnlinkProof, "account-a", "identity-1", fact));
        Assert.Null(ceremonies.Take(CeremonyKind.UnlinkProof, "account-a", "identity-1", fact));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "step_up_unavailable")]
    [InlineData(HttpStatusCode.Conflict, "step_up_replayed")]
    [InlineData(HttpStatusCode.TooManyRequests, "step_up_rate_limited")]
    [InlineData(HttpStatusCode.Unauthorized, "sign_in_required")]
    [InlineData(HttpStatusCode.InternalServerError, "error")]
    public async Task A_Heimdall_step_up_refusal_is_shown_with_a_closed_code_and_nothing_is_unlinked_or_retried(HttpStatusCode status, string code)
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await NewMember(host);
        var authenticator = new SoftwareAuthenticator();
        await AddPasskey(host, member, authenticator);
        await CoolOff(host, member);
        var attempt = await Unlink(host, member, authenticator, member.Patreon);
        host.Heimdall.StepUpStatus = status;

        var done = await Finish(host, member, attempt);

        Assert.Equal(Page + "?result=" + code, Location(done));
        Assert.Single(host.Heimdall.StepUps);
        Assert.Empty(host.Heimdall.Unlinks);
        Assert.Contains(host.Heimdall.Identities, x => (string?)x["id"] == member.Patreon);
        Assert.DoesNotContain("HEIMDALL FREE TEXT", await (await host.GetLinkedAccountsAsync(member.Session, "?result=" + code)).Response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_Heimdall_that_is_down_for_the_step_up_changes_nothing()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await NewMember(host);
        var authenticator = new SoftwareAuthenticator();
        await AddPasskey(host, member, authenticator);
        await CoolOff(host, member);
        var attempt = await Unlink(host, member, authenticator, member.Patreon);
        host.Heimdall.StepUpDown = true;

        var done = await Finish(host, member, attempt);

        Assert.Equal(Page + "?result=error", Location(done));
        Assert.Empty(host.Heimdall.Unlinks);
    }

    [Fact]
    public async Task Every_step_up_carries_a_fresh_nonce_so_the_replay_guard_never_fires()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await NewMember(host, google: true);
        var authenticator = new SoftwareAuthenticator();
        await AddPasskey(host, member, authenticator);
        await CoolOff(host, member);
        var results = new List<string?>();
        foreach (var identity in new[] { member.Patreon, member.Discord })
        {
            var attempt = await Unlink(host, member, authenticator, identity);
            // Same Heimdall session (sid) for both: the fake keys replay on sid and nonce.
            results.Add(Location(await Finish(host, member, attempt, host.Token(member.Account, mutate: c => c["sid"] = "one-session"))));
            await ReadList(host, member);
        }

        var nonces = host.Heimdall.StepUps.Select(x => JsonDocument.Parse(x.Body).RootElement.GetProperty("nonce").GetString()).ToList();
        Assert.Equal(2, nonces.Distinct().Count());
        Assert.All(results, x => Assert.Equal(Page + "?result=unlinked", x));
    }

    [Fact]
    public async Task With_a_strong_passkey_the_Google_route_is_closed_and_without_one_it_is_as_before()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await NewMember(host, google: true);

        var before = await host.StartUnlinkAsync(member.Session, member.Patreon);
        Assert.StartsWith("https://heimdall.test", Location(before.Response));
        Assert.Contains("stepUp", host.Heimdall.StartBodies[^1]);

        await AddPasskey(host, member, new SoftwareAuthenticator());
        var cooling = await host.StartUnlinkAsync(member.Session, member.Patreon);
        Assert.StartsWith("https://heimdall.test", Location(cooling.Response));

        await CoolOff(host, member);
        var starts = host.Heimdall.StartBodies.Count;
        var after = await host.StartUnlinkAsync(member.Session, member.Patreon);

        Assert.Equal(Page + "?result=passkey_required", Location(after.Response));
        Assert.Equal(starts, host.Heimdall.StartBodies.Count);
    }

    [Fact]
    public async Task The_passkey_script_is_served_first_party_and_is_the_only_script_the_page_adds()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await NewMember(host);

        var (_, html) = await host.GetLinkedAccountsAsync(member.Session);
        using var client = host.NewClient();
        var script = await client.GetAsync("/js/passkeys.js");
        var source = await script.Content.ReadAsStringAsync();

        Assert.Contains("src=\"/js/passkeys.js", html);
        Assert.DoesNotContain("<script>", html.Replace("<script src", "<script-src"));
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.DoesNotContain("http://", source);
        Assert.DoesNotContain("https://", source);
        Assert.DoesNotContain("eval(", source);
        Assert.DoesNotContain("innerHTML", source);
        Assert.DoesNotContain("document.write", source);
    }

    // ---- a thief holding only the session cannot grief the member's flows ----

    [Fact]
    public async Task Junk_from_a_session_thief_burns_neither_the_members_challenge_nor_ticket_nor_permits()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await NewMember(host);
        var authenticator = new SoftwareAuthenticator();
        var ticket = await SignInForEnrolment(host, member);
        var options = await host.PostLinkedAccountsAsync(member.Session, "PasskeyEnrolOptions", extraCookies: Ticket(ticket));
        var registration = authenticator.Register(await JsonOf(options));

        // The thief has the session cookie and no ticket: many junk registrations, all refused, nothing spent.
        for (var i = 0; i < PasskeyRateLimit.Permits + 5; i++)
        {
            var junk = await host.PostLinkedAccountsAsync(member.Session, "PasskeyEnrol", new Dictionary<string, string> { ["rawId"] = "AAAA" });
            Assert.Equal(Page + "?result=enrol_needs_sign_in", Location(junk.Response));
        }

        var enrolled = await host.PostLinkedAccountsAsync(member.Session, "PasskeyEnrol", registration, extraCookies: Ticket(ticket));
        Assert.Equal(Page + "?result=passkey_added", Location(enrolled.Response));

        await CoolOff(host, member);
        var (json, _) = await Challenge(host, member, member.Patreon);
        var thief = new SoftwareAuthenticator();
        for (var i = 0; i < PasskeyRateLimit.Permits + 5; i++)
        {
            // Half malformed, half a complete assertion by a key the account does not hold: both fail before anything is spent.
            var fields = i % 2 == 0 ? new Dictionary<string, string> { ["rawId"] = "AAAA" } : thief.Assert(json);
            var junk = await PostAssertion(host, member, fields, member.Patreon);
            Assert.Equal(Page + "?result=passkey_failed", Location(junk.Response));
        }

        var real = await PostAssertion(host, member, authenticator.Assert(json), member.Patreon);
        Assert.StartsWith("https://heimdall.test", Location(real.Response));
    }

    [Fact]
    public async Task A_passkey_revoked_after_the_assertion_does_not_unlink_at_the_completion()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await NewMember(host);
        var authenticator = new SoftwareAuthenticator();
        await AddPasskey(host, member, authenticator);
        await CoolOff(host, member);
        var attempt = await Unlink(host, member, authenticator, member.Patreon);
        var id = (await Credentials(host)).Single().Id.ToString();
        await host.PostLinkedAccountsAsync(member.Session, "PasskeyRevoke", new Dictionary<string, string> { ["credentialId"] = id });

        var done = await Finish(host, member, attempt);

        Assert.Equal(Page + "?result=passkey_failed", Location(done));
        Assert.Empty(host.Heimdall.StepUps);
        Assert.Empty(host.Heimdall.Unlinks);
        Assert.Contains(host.Heimdall.Identities, x => (string?)x["id"] == member.Patreon);
    }

    [Fact]
    public async Task An_assertion_whose_user_handle_names_another_account_is_refused()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await NewMember(host);
        var authenticator = new SoftwareAuthenticator();
        await AddPasskey(host, member, authenticator);
        await CoolOff(host, member);
        var own = (await host.Account(member.Account))!.Id.ToByteArray();

        var (foreign, _) = await Challenge(host, member, member.Patreon);
        var refused = await PostAssertion(host, member, authenticator.Assert(foreign, Guid.NewGuid().ToByteArray()), member.Patreon);
        var (mine, _) = await Challenge(host, member, member.Patreon);
        var accepted = await PostAssertion(host, member, authenticator.Assert(mine, own), member.Patreon);

        Assert.Equal(Page + "?result=passkey_failed", Location(refused.Response));
        Assert.StartsWith("https://heimdall.test", Location(accepted.Response));
    }

    [Fact]
    public async Task The_credential_lookup_is_scoped_to_the_session_account_even_when_the_allow_list_is_empty()
    {
        using var host = new HeimdallHost(realSessions: true);
        var victim = await NewMember(host);
        var victimKey = new SoftwareAuthenticator();
        await AddPasskey(host, victim, victimKey);
        var other = await NewMember(host);
        await AddPasskey(host, other, new SoftwareAuthenticator());
        await CoolOff(host, other);
        await ReadList(host, victim);
        var (json, _) = await Challenge(host, other, other.Patreon);
        // The allow list is a second guard; take it out of the other account's challenge (the way a test can without weakening
        // the server) so only the account-scoped lookup stands between the victim's credential and the other account.
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node["allowCredentials"] = new System.Text.Json.Nodes.JsonArray();
        var stripped = node.ToJsonString();
        Assert.NotNull(host.Services.GetRequiredService<PasskeyCeremonies>()
            .Issue(CeremonyKind.UnlinkChallenge, other.Account, other.Patreon, stripped, TimeSpan.FromMinutes(2)));
        var starts = host.Heimdall.StartBodies.Count;

        var borrowed = await PostAssertion(host, other, victimKey.Assert(stripped), other.Patreon);

        Assert.Equal(Page + "?result=passkey_failed", Location(borrowed.Response));
        Assert.Equal(starts, host.Heimdall.StartBodies.Count);
    }

    // ---- configuration ----

    [Fact]
    public async Task A_misconfigured_relying_party_is_logged_once_at_boot_before_any_ceremony()
    {
        using var host = new HeimdallHost(realSessions: true, publicBaseUrl: "http://localhost");

        _ = host.Services;

        Assert.Single(host.Logs.Lines, x => x.Contains("Passkeys are unavailable"));
    }

    [Fact]
    public void An_expected_host_written_with_a_port_makes_passkeys_unavailable_and_says_why()
    {
        var logs = new CapturedLogs();
        var party = new PasskeyRelyingParty(
            Microsoft.Extensions.Options.Options.Create(new Bifrost.Web.Configuration.BifrostHostOptions { PublicBaseUrl = "https://localhost", ExpectedHost = "localhost:443" }),
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<PasskeyRelyingParty>());
        var loud = new PasskeyRelyingParty(
            Microsoft.Extensions.Options.Options.Create(new Bifrost.Web.Configuration.BifrostHostOptions { PublicBaseUrl = "https://localhost:8443", ExpectedHost = "localhost:8443" }),
            new TestLogger<PasskeyRelyingParty>(logs));
        var fine = new PasskeyRelyingParty(
            Microsoft.Extensions.Options.Options.Create(new Bifrost.Web.Configuration.BifrostHostOptions { PublicBaseUrl = "https://localhost:8443", ExpectedHost = "localhost" }),
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<PasskeyRelyingParty>());

        Assert.False(party.IsAvailable);
        Assert.False(loud.IsAvailable);
        Assert.Contains("without a port", Assert.Single(logs.Lines));
        Assert.True(fine.IsAvailable);
        Assert.Equal("https://localhost:8443", fine.Origin);
        Assert.Equal("localhost", fine.RpId);
    }

    private sealed class TestLogger<T>(CapturedLogs logs) : Microsoft.Extensions.Logging.ILogger<T>
    {
        private readonly Microsoft.Extensions.Logging.ILogger _inner = logs.CreateLogger(typeof(T).Name);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _inner.Log(logLevel, eventId, state, exception, formatter);
    }

    [Fact]
    public async Task With_a_strong_passkey_and_an_unavailable_relying_party_the_page_says_so_and_offers_no_form()
    {
        using var host = new HeimdallHost(realSessions: true, publicBaseUrl: "https://other.example");
        var member = await NewMember(host);
        var user = (await host.Account(member.Account))!;
        await host.Db(async db =>
        {
            db.PasskeyCredentials.Add(new Bifrost.Web.Domain.PasskeyCredential
            {
                UserAccountId = user.Id,
                CredentialId = [1, 2, 3, 4],
                PublicKey = [1],
                Label = "Old key",
                CreatedUtc = host.Clock.Now - TimeSpan.FromDays(3),
                StrongFromUtc = host.Clock.Now - TimeSpan.FromDays(1)
            });
            await db.SaveChangesAsync();
            return 0;
        });

        var (_, html) = await host.GetLinkedAccountsAsync(member.Session);

        Assert.DoesNotContain("UnlinkPasskey", html);
        Assert.DoesNotContain("Unlink with passkey", html);
        Assert.DoesNotContain("passkeys.js", html);
        Assert.Contains("passkeys are not available on this server right now", html);
        Assert.Contains("revoke your passkeys below", html);
        Assert.Contains("Old key", html);
        Assert.Contains("PasskeyRevoke", html);
    }

    [Fact]
    public async Task A_label_is_cut_by_text_element_never_inside_a_surrogate_pair_and_loses_format_characters()
    {
        using var host = new HeimdallHost(realSessions: true);
        var member = await NewMember(host);
        var ticket = await SignInForEnrolment(host, member);

        var done = await Enrol(host, member, new SoftwareAuthenticator(), ticket, new string('a', 39) + "\U0001F600" + "tail\u202Eevil");

        Assert.Equal(Page + "?result=passkey_added", Location(done));
        var label = Assert.Single(await Credentials(host)).Label;
        Assert.Equal(new string('a', 39) + "\U0001F600", label);
        Assert.DoesNotContain(label.EnumerateRunes(), x => x == System.Text.Rune.ReplacementChar);
        Assert.Equal(label, System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(label)));

        var second = await SignInForEnrolment(host, member);
        var again = await Enrol(host, member, new SoftwareAuthenticator(), second, "\u202Eab\u200Bc");
        Assert.Equal(Page + "?result=passkey_added", Location(again));
        Assert.Contains((await Credentials(host)), x => x.Label == "abc");
    }
}
