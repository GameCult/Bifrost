using System.Text.Json;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Oidc;
using Bifrost.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;

namespace Bifrost.Web.Tests;

public sealed class OidcLogoutTests
{
    private static readonly TimeSpan[] ExpectedBackoff =
        new[] { 30, 60, 120, 240, 480, 900, 900 }.Select(x => TimeSpan.FromSeconds(x)).ToArray();

    [Fact]
    public async Task Probe_authorizations_carry_the_subject_and_a_valid_status_after_a_code_flow()
    {
        using var factory = new OidcLogoutFactory();
        var (identity, _) = await factory.GrantAsync();

        Assert.True(factory.ValidAuthorizations(factory.AccountId(identity).ToString("D")) > 0);
        using var scope = factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var found = new List<object>();
        await foreach (var authorization in manager.FindBySubjectAsync(factory.AccountId(identity).ToString("D")))
        {
            found.Add(authorization);
        }

        Assert.NotEmpty(found);
        Assert.True(await manager.TryRevokeAsync(found[0]));
    }

    [Theory]
    [InlineData(MembershipStatus.Suspended)]
    [InlineData(MembershipStatus.PendingApproval)]
    [InlineData(MembershipStatus.Authenticated)]
    public async Task A_member_who_is_not_active_gets_one_signed_logout_and_loses_the_authorizations(MembershipStatus status)
    {
        using var factory = new OidcLogoutFactory();
        var (identity, idToken) = await factory.GrantAsync(status);
        var subject = factory.AccountId(identity).ToString("D");
        var reconciler = factory.Reconciler;

        await reconciler.ReconcileOnceAsync(CancellationToken.None);

        var token = Assert.Single(factory.Mas.LogoutTokens());
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = OidcLogoutFactory.Origin.AbsoluteUri,
            ValidAudience = OidcTestKit.ClientId,
            IssuerSigningKeys = (await factory.JwksAsync()).GetSigningKeys(),
            ValidAlgorithms = ["ES256"],
            ValidTypes = ["logout+jwt"],
            ValidateLifetime = false
        });
        Assert.True(result.IsValid, result.Exception?.Message);
        var jwt = (JsonWebToken)result.SecurityToken;
        Assert.Equal("logout+jwt", jwt.Typ);
        Assert.Equal(new JsonWebToken(idToken).Issuer, jwt.Issuer);
        Assert.Equal(subject, jwt.Subject);
        Assert.Equal(TimeSpan.FromMinutes(2), jwt.ValidTo - jwt.IssuedAt);
        Assert.Equal(factory.Time.GetUtcNow().ToUnixTimeSeconds(), new DateTimeOffset(jwt.IssuedAt).ToUnixTimeSeconds());
        Assert.False(string.IsNullOrEmpty(jwt.Id));
        var payload = JsonDocument.Parse(Base64UrlEncoder.Decode(jwt.EncodedPayload)).RootElement;
        var events = payload.GetProperty("events");
        Assert.Equal(["http://schemas.openid.net/event/backchannel-logout"], events.EnumerateObject().Select(x => x.Name).ToArray());
        Assert.Equal(JsonValueKind.Object, events.EnumerateObject().Single().Value.ValueKind);
        Assert.False(payload.TryGetProperty("nonce", out _));
        Assert.False(payload.TryGetProperty("sid", out _));
        Assert.Equal(0, factory.ValidAuthorizations(subject));

        await reconciler.ReconcileOnceAsync(CancellationToken.None);
        Assert.Single(factory.Mas.Bodies);
    }

    [Fact]
    public async Task Two_logouts_for_one_subject_carry_different_jti()
    {
        using var factory = new OidcLogoutFactory();
        var (identity, _) = await factory.GrantAsync(MembershipStatus.Suspended);
        factory.Mas.Script(500, 200);

        await factory.Reconciler.ReconcileOnceAsync(CancellationToken.None);
        factory.Time.Advance(TimeSpan.FromSeconds(30));
        await factory.Reconciler.ReconcileOnceAsync(CancellationToken.None);

        var ids = factory.Mas.LogoutTokens().Select(x => new JsonWebToken(x).Id).ToArray();
        Assert.Equal(2, ids.Length);
        Assert.NotEqual(ids[0], ids[1]);
        Assert.Equal(0, factory.ValidAuthorizations(factory.AccountId(identity).ToString("D")));
    }

    [Fact]
    public async Task An_active_member_is_never_sent_and_keeps_the_authorizations()
    {
        using var factory = new OidcLogoutFactory();
        var (suspended, _) = await factory.GrantAsync(MembershipStatus.Suspended);
        var (active, _) = await factory.GrantAsync(MembershipStatus.Active);
        var activeSubject = factory.AccountId(active).ToString("D");
        var before = factory.ValidAuthorizations(activeSubject);
        Assert.True(before > 0);

        await factory.Reconciler.ReconcileOnceAsync(CancellationToken.None);

        var token = Assert.Single(factory.Mas.LogoutTokens());
        Assert.Equal(factory.AccountId(suspended).ToString("D"), new JsonWebToken(token).Subject);
        Assert.Equal(before, factory.ValidAuthorizations(activeSubject));
    }

    [Fact]
    public async Task One_subject_that_cannot_be_delivered_does_not_block_another()
    {
        using var factory = new OidcLogoutFactory();
        var (first, _) = await factory.GrantAsync(MembershipStatus.Suspended);
        var (second, _) = await factory.GrantAsync(MembershipStatus.Suspended);
        factory.Mas.Script(500);

        await factory.Reconciler.ReconcileOnceAsync(CancellationToken.None);

        Assert.Equal(2, factory.Mas.Bodies.Count);
        var delivered = new[] { first, second }.Count(x => factory.ValidAuthorizations(factory.AccountId(x).ToString("D")) == 0);
        Assert.Equal(1, delivered);
    }

    [Fact]
    public async Task Delivery_retries_with_exponential_capped_backoff_and_revokes_only_after_a_2xx()
    {
        using var factory = new OidcLogoutFactory();
        var (identity, _) = await factory.GrantAsync(MembershipStatus.Suspended);
        var subject = factory.AccountId(identity).ToString("D");
        factory.Mas.Script(500, 500, 500, 500, 500, 500, 500, 200);
        var reconciler = factory.Reconciler;

        var posts = 0;
        foreach (var delay in ExpectedBackoff)
        {
            await reconciler.ReconcileOnceAsync(CancellationToken.None);
            Assert.Equal(++posts, factory.Mas.Bodies.Count);
            Assert.True(factory.ValidAuthorizations(subject) > 0, "a failed delivery must not revoke");

            factory.Time.Advance(delay - TimeSpan.FromSeconds(1));
            await reconciler.ReconcileOnceAsync(CancellationToken.None);
            Assert.Equal(posts, factory.Mas.Bodies.Count);

            factory.Time.Advance(TimeSpan.FromSeconds(1));
        }

        await reconciler.ReconcileOnceAsync(CancellationToken.None);
        Assert.Equal(8, factory.Mas.Bodies.Count);
        Assert.Equal(0, factory.ValidAuthorizations(subject));
    }

    [Theory]
    [InlineData(FakeMasHandler.NetworkFault)]
    [InlineData(FakeMasHandler.Timeout)]
    [InlineData(300)]
    [InlineData(302)]
    [InlineData(401)]
    public async Task A_network_fault_timeout_or_unexpected_status_keeps_the_authorizations_and_retries(int fault)
    {
        using var factory = new OidcLogoutFactory();
        var (identity, _) = await factory.GrantAsync(MembershipStatus.Suspended);
        var subject = factory.AccountId(identity).ToString("D");
        factory.Mas.Script(fault);

        await factory.Reconciler.ReconcileOnceAsync(CancellationToken.None);
        Assert.True(factory.ValidAuthorizations(subject) > 0);
        factory.Time.Advance(TimeSpan.FromSeconds(30));
        await factory.Reconciler.ReconcileOnceAsync(CancellationToken.None);

        Assert.Equal(2, factory.Mas.Bodies.Count);
        Assert.Equal(0, factory.ValidAuthorizations(subject));
    }

    [Fact]
    public async Task A_restarted_reconciler_resumes_the_pending_subject_from_the_table()
    {
        using var factory = new OidcLogoutFactory();
        var (identity, _) = await factory.GrantAsync(MembershipStatus.Suspended);
        var subject = factory.AccountId(identity).ToString("D");
        factory.Mas.Script(500);

        await factory.Reconciler.ReconcileOnceAsync(CancellationToken.None);
        Assert.Single(factory.Mas.Bodies);
        await factory.NewReconciler().ReconcileOnceAsync(CancellationToken.None);

        Assert.Equal(2, factory.Mas.Bodies.Count);
        Assert.Equal(0, factory.ValidAuthorizations(subject));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    public async Task A_400_or_404_is_a_fault_not_success_and_backs_off_at_the_cap(int status)
    {
        using var factory = new OidcLogoutFactory();
        var (identity, _) = await factory.GrantAsync(MembershipStatus.Suspended);
        var subject = factory.AccountId(identity).ToString("D");
        factory.Mas.Script(status, status);
        var reconciler = factory.Reconciler;

        await reconciler.ReconcileOnceAsync(CancellationToken.None);
        factory.Time.Advance(OidcLogoutReconciler.MaxBackoff - TimeSpan.FromSeconds(1));
        await reconciler.ReconcileOnceAsync(CancellationToken.None);
        Assert.Single(factory.Mas.Bodies);
        Assert.True(factory.ValidAuthorizations(subject) > 0);

        factory.Time.Advance(TimeSpan.FromSeconds(1));
        await reconciler.ReconcileOnceAsync(CancellationToken.None);
        Assert.Equal(2, factory.Mas.Bodies.Count);
        factory.Time.Advance(OidcLogoutReconciler.MaxBackoff - TimeSpan.FromSeconds(1));
        await reconciler.ReconcileOnceAsync(CancellationToken.None);
        Assert.Equal(2, factory.Mas.Bodies.Count);
        Assert.True(factory.ValidAuthorizations(subject) > 0);
    }

    [Fact]
    public async Task A_subject_that_is_not_an_account_id_is_treated_as_not_active()
    {
        using var factory = new OidcLogoutFactory();
        var (active, _) = await factory.GrantAsync(MembershipStatus.Active);
        await factory.AddAuthorizationAsync("not-a-guid");

        await factory.Reconciler.ReconcileOnceAsync(CancellationToken.None);

        var token = Assert.Single(factory.Mas.LogoutTokens());
        Assert.Equal("not-a-guid", new JsonWebToken(token).Subject);
        Assert.Equal(0, factory.ValidAuthorizations("not-a-guid"));
        Assert.True(factory.ValidAuthorizations(factory.AccountId(active).ToString("D")) > 0);
    }

    [Fact]
    public async Task A_deleted_account_is_treated_as_not_active()
    {
        using var factory = new OidcLogoutFactory();
        var (identity, _) = await factory.GrantAsync(MembershipStatus.Active);
        var subject = factory.AccountId(identity).ToString("D");
        await factory.DeleteAccountAsync(identity);

        await factory.Reconciler.ReconcileOnceAsync(CancellationToken.None);

        Assert.Equal(subject, new JsonWebToken(Assert.Single(factory.Mas.LogoutTokens())).Subject);
        Assert.Equal(0, factory.ValidAuthorizations(subject));
    }

    [Theory]
    [InlineData(201)]
    [InlineData(204)]
    [InlineData(299)]
    public async Task Any_2xx_is_delivery(int status)
    {
        using var factory = new OidcLogoutFactory();
        var (identity, _) = await factory.GrantAsync(MembershipStatus.Suspended);
        factory.Mas.Script(status);

        await factory.Reconciler.ReconcileOnceAsync(CancellationToken.None);

        Assert.Equal(0, factory.ValidAuthorizations(factory.AccountId(identity).ToString("D")));
    }

    [Fact]
    public async Task A_failure_is_logged_by_status_and_delay_only_and_a_success_logs_no_failure()
    {
        using var factory = new OidcLogoutFactory();
        await factory.GrantAsync(MembershipStatus.Suspended);
        await factory.GrantAsync(MembershipStatus.Suspended);
        var log = new ListLogger<OidcLogoutReconciler>();
        factory.Mas.Script(503, FakeMasHandler.NetworkFault);

        await factory.NewReconciler(logger: log).ReconcileOnceAsync(CancellationToken.None);

        // The first subject failed with 503, the second with no response; both were delivered nothing.
        var warnings = log.Entries.Where(x => x.Level == LogLevel.Warning).Select(x => x.Message).ToArray();
        Assert.Equal(2, warnings.Length);
        Assert.Contains(warnings, x => x.Contains("503", StringComparison.Ordinal) && x.Contains("00:00:30", StringComparison.Ordinal));
        Assert.Contains(warnings, x => x.Contains("no response", StringComparison.Ordinal));
        Assert.All(log.Entries, x =>
        {
            Assert.DoesNotContain(FakeMasHandler.BodyCanary, x.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("mas.example.test", x.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("logout_token", x.Message, StringComparison.Ordinal);
        });

        var clean = new ListLogger<OidcLogoutReconciler>();
        await factory.NewReconciler(logger: clean).ReconcileOnceAsync(CancellationToken.None);
        Assert.Empty(clean.Entries.Where(x => x.Level >= LogLevel.Warning));
    }

    [Fact]
    public async Task A_subject_who_recovers_or_whose_grants_vanish_carries_no_stale_backoff()
    {
        using var factory = new OidcLogoutFactory();
        var (identity, _) = await factory.GrantAsync(MembershipStatus.Suspended);
        var subject = factory.AccountId(identity).ToString("D");
        factory.Mas.Script(500, 500);
        var reconciler = factory.Reconciler;

        // Recovered: Active again clears the backoff, so the next demotion is delivered at once.
        await reconciler.ReconcileOnceAsync(CancellationToken.None);
        Assert.Single(factory.Mas.Bodies);
        await factory.SetMembershipAsync(identity, MembershipStatus.Active);
        await reconciler.ReconcileOnceAsync(CancellationToken.None);
        await factory.SetMembershipAsync(identity, MembershipStatus.Suspended);
        await reconciler.ReconcileOnceAsync(CancellationToken.None);
        Assert.Equal(2, factory.Mas.Bodies.Count);

        // Vanished: grants revoked elsewhere drop the subject from the list, which drops its backoff.
        await factory.RevokeAllAsync(subject);
        await reconciler.ReconcileOnceAsync(CancellationToken.None);
        await factory.AddAuthorizationAsync(subject);
        await reconciler.ReconcileOnceAsync(CancellationToken.None);
        Assert.Equal(3, factory.Mas.Bodies.Count);
        Assert.Equal(0, factory.ValidAuthorizations(subject));
    }

    [Fact]
    public async Task An_unexpected_exception_is_not_a_network_fault_and_a_cancelled_post_is_not_a_failed_delivery()
    {
        using var factory = new OidcLogoutFactory();
        var (identity, _) = await factory.GrantAsync(MembershipStatus.Suspended);
        var subject = factory.AccountId(identity).ToString("D");
        var reconciler = factory.Reconciler;

        factory.Mas.Script(FakeMasHandler.Unexpected);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reconciler.ReconcileOnceAsync(CancellationToken.None));

        // Stopping while the post is in flight surfaces as cancellation and records no backoff: the next pass posts at once.
        factory.Mas.Script(FakeMasHandler.Hang);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reconciler.ReconcileOnceAsync(stop.Token));
        Assert.Equal(2, factory.Mas.Bodies.Count);
        await reconciler.ReconcileOnceAsync(CancellationToken.None);
        Assert.Equal(3, factory.Mas.Bodies.Count);
        Assert.Equal(0, factory.ValidAuthorizations(subject));
    }

    [Fact]
    public async Task The_hosted_loop_ticks_again_after_each_interval_and_survives_a_failed_tick()
    {
        using var factory = new OidcLogoutFactory();
        var clock = new ControlledClock();
        var log = new ListLogger<OidcLogoutReconciler>();
        var reconciler = factory.NewReconciler(clock, log);
        var (first, _) = await factory.GrantAsync(MembershipStatus.Suspended);
        await reconciler.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => factory.Mas.Bodies.Count == 1);
            Assert.Equal(0, factory.ValidAuthorizations(factory.AccountId(first).ToString("D")));

            // A failing tick is logged and retried only after the interval, never in a tight loop.
            var (second, _) = await factory.GrantAsync(MembershipStatus.Suspended);
            factory.Mas.Script(FakeMasHandler.Unexpected);
            await WaitForAsync(() => clock.Waiting > 0);
            clock.FireTimers();
            await WaitForAsync(() => factory.Mas.Bodies.Count == 2);
            await Task.Delay(300);
            Assert.Equal(2, factory.Mas.Bodies.Count);

            await WaitForAsync(() => clock.Waiting > 0);
            clock.FireTimers();
            await WaitForAsync(() => factory.Mas.Bodies.Count == 3);
            var failure = Assert.Single(log.Entries, x => x.Level == LogLevel.Error);
            Assert.Contains(nameof(InvalidOperationException), failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("not a network fault", failure.Message, StringComparison.Ordinal);
            await WaitForAsync(() => factory.ValidAuthorizations(factory.AccountId(second).ToString("D")) == 0);
        }
        finally
        {
            await reconciler.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the reconciler");
            await Task.Delay(20);
        }
    }

    [Fact]
    public void Without_a_back_channel_logout_uri_the_reconciler_is_not_hosted_and_nothing_is_revoked()
    {
        using var factory = new OidcWebApplicationFactory();
        using var client = factory.CreateClient();

        Assert.Empty(factory.Services.GetServices<IHostedService>().OfType<OidcLogoutReconciler>());
    }

    [Fact]
    public void The_reconciler_is_hosted_when_the_uri_is_set()
    {
        using var factory = new OidcLogoutFactory();
        using var client = factory.CreateClient();

        Assert.NotNull(factory.Reconciler);
    }
}
