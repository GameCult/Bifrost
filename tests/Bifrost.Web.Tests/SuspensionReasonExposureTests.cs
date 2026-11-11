using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Membership;
using Bifrost.Web.Features.Shared;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.Web.Tests;

/// <summary>The suspension reason is for admins: no member-facing page or the OIDC token and userinfo output carries it.</summary>
public sealed class SuspensionReasonExposureTests
{
    private const string Canary = "REASON-CANARY-9d2f-do-not-leak";

    private static async Task SeedAsync(IServiceProvider services, string identity, MembershipStatus status, string? reason)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        db.UserAccounts.Add(new UserAccount
        {
            DisplayName = identity, BifrostIdentity = identity, NormalizedBifrostIdentity = identity,
            Membership = new Bifrost.Web.Domain.Membership { Status = status, ApprovedAtUtc = DateTimeOffset.UtcNow, SuspensionReason = reason }
        });
        await db.SaveChangesAsync();
    }

    private static async Task<string> GetAsync(HttpClient client, string path, string? identity)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Test-Bifrost-Identity", identity);
        using var response = await client.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, path);
        return await response.Content.ReadAsStringAsync();
    }

    private sealed class FixedActor(CurrentBifrostActor actor) : ICurrentBifrostActorAccessor
    {
        public Task<CurrentBifrostActor> GetAsync(CancellationToken cancellationToken) => Task.FromResult(actor);
    }

    // Guards the projection on its own: the view's admin gate cannot be the only thing keeping the reason off a non-admin's page model.
    [Fact]
    public async Task The_members_page_model_carries_no_reason_for_a_non_admin_viewer_and_does_for_an_admin()
    {
        using var factory = new TestWebApplicationFactory();
        await SeedAsync(factory.Services, "suspendedtwo", MembershipStatus.Suspended, Canary);
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<BifrostDbContext>();
        var viewer = await db.UserAccounts.Include(x => x.Membership!).ThenInclude(x => x.RoleAssignments).SingleAsync(x => x.BifrostIdentity == "suspendedtwo");

        async Task<IReadOnlyList<Bifrost.Web.Pages.App.Members.IndexModel.MemberDirectoryItem>> UsersSeenBy(bool admin)
        {
            viewer.Membership!.IsPlatformAdmin = admin;
            var model = new Bifrost.Web.Pages.App.Members.IndexModel(db, new FixedActor(new CurrentBifrostActor(true, viewer)),
                sp.GetRequiredService<AuditTrailService>(), sp.GetRequiredService<MembershipSuspensionService>(), TimeProvider.System);
            await model.OnGetAsync(CancellationToken.None);
            return model.Users;
        }

        Assert.All(await UsersSeenBy(admin: false), u => Assert.DoesNotContain(Canary, u.SuspensionReason));
        Assert.Contains(await UsersSeenBy(admin: true), u => u.SuspensionReason == Canary);
    }

    [Fact]
    public async Task The_member_dashboard_hides_the_bootstrap_promotion_receipt_but_shows_ordinary_activity()
    {
        using var factory = new TestWebApplicationFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
            db.AuditEvents.Add(new AuditEvent { Action = "membership.bootstrap-promoted", Detail = "PROMO-CANARY-41", EntityType = "Membership", EntityId = "x", OccurredAtUtc = DateTimeOffset.UtcNow });
            db.AuditEvents.Add(new AuditEvent { Action = "membership.approved", Detail = "ORDINARY-CANARY-41", EntityType = "Membership", EntityId = "x", OccurredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        var html = await client.GetStringAsync("/App");

        Assert.Contains("ORDINARY-CANARY-41", html);
        Assert.DoesNotContain("PROMO-CANARY-41", html);
    }

    [Fact]
    public async Task Member_facing_pages_never_carry_the_reason_and_the_admin_members_page_does()
    {
        using var factory = new TestWebApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        await SeedAsync(factory.Services, "suspendedone", MembershipStatus.Suspended, Canary);
        await SeedAsync(factory.Services, "ordinaryone", MembershipStatus.Active, null);

        foreach (var path in new[] { "/App", "/App/Members", "/Membership/Status" })
        {
            Assert.DoesNotContain(Canary, await GetAsync(client, path, "ordinaryone"));
        }

        Assert.DoesNotContain(Canary, await GetAsync(client, "/Membership/Status", "suspendedone"));

        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        Assert.Contains(Canary, await admin.GetStringAsync("/App/Members")); // the default test sign-in is a bootstrap admin
    }

    [Fact]
    public async Task The_OIDC_token_response_and_id_token_never_carry_the_reason()
    {
        using var factory = new OidcLogoutFactory();
        var (_, idToken) = await factory.GrantAsync(suspensionReason: Canary);

        Assert.DoesNotContain(Canary, factory.LastTokenResponse);
        var payload = new JsonWebToken(idToken).EncodedPayload;
        Assert.DoesNotContain(Canary, Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(payload)));
    }

    // Userinfo is broken in some environments before this change (OidcIssuerTests' userinfo cases fail identically there).
    [Fact]
    public async Task OIDC_userinfo_never_carries_the_reason()
    {
        using var factory = new OidcLogoutFactory();
        _ = await factory.GrantAsync(suspensionReason: Canary);

        using var client = factory.CreateClient(factory.HostClientOptions());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.LastAccessToken);
        using var userinfo = await client.GetAsync("/connect/userinfo");
        Assert.Equal(HttpStatusCode.OK, userinfo.StatusCode);
        Assert.DoesNotContain(Canary, await userinfo.Content.ReadAsStringAsync());
    }
}
