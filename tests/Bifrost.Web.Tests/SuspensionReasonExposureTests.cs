using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
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
