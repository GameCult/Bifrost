using System.Net;
using System.Text.RegularExpressions;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.Web.Tests;

/// <summary>A refused suspend or unsuspend tells the admin why on the page the redirect lands on, and shows only a fixed message.</summary>
public sealed class MembersPageNoticeTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private HttpClient Client() => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        BaseAddress = new Uri("https://localhost"),
        HandleCookies = true
    });

    private async Task<Guid> SeedMemberAsync(MembershipStatus status, bool withMembership = true)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var id = $"notice-{Guid.NewGuid():N}"[..20];
        var account = new UserAccount
        {
            DisplayName = id, BifrostIdentity = id, NormalizedBifrostIdentity = id,
            Membership = withMembership ? new Bifrost.Web.Domain.Membership { Status = status, ApprovedAtUtc = DateTimeOffset.UtcNow } : null
        };
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SignedInAdminIdAsync(HttpClient client)
    {
        using var _ = await client.GetAsync("/App/Members"); // sign-in sync creates and promotes the bootstrap admin
        using var scope = factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<BifrostDbContext>().UserAccounts.AsNoTracking().SingleAsync(x => x.GitHubUserId == 12345)).Id;
    }

    private static async Task<string> TokenAsync(HttpClient client)
    {
        var html = await (await client.GetAsync("/App/Members")).Content.ReadAsStringAsync();
        return Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
    }

    private static async Task<string> PostThenFollowAsync(HttpClient client, string handler, Guid target, string reason, string? role = null)
    {
        var token = await TokenAsync(client);
        Assert.NotEmpty(token);
        using var post = await client.PostAsync($"/App/Members?handler={handler}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["userAccountId"] = target.ToString(),
            ["reason"] = reason,
            ["role"] = role ?? "Producer",
            ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        using var landed = await client.GetAsync(post.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, landed.StatusCode);
        return await landed.Content.ReadAsStringAsync();
    }

    private static readonly Regex Alert = new("<p[^>]*role=\"alert\"[^>]*>(.*?)</p>", RegexOptions.Singleline);

    [Fact]
    public async Task A_suspend_without_a_reason_says_a_reason_is_required_and_changes_nothing()
    {
        using var client = Client();
        _ = await SignedInAdminIdAsync(client);
        var member = await SeedMemberAsync(MembershipStatus.Active);

        var html = await PostThenFollowAsync(client, "Suspend", member, "  ");

        Assert.Contains("a reason is required", Alert.Match(html).Groups[1].Value);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(MembershipStatus.Active, (await scope.ServiceProvider.GetRequiredService<BifrostDbContext>().Memberships.AsNoTracking().SingleAsync(x => x.UserAccountId == member)).Status);
    }

    [Fact]
    public async Task A_suspend_of_the_admin_themselves_says_it_is_protected()
    {
        using var client = Client();
        var admin = await SignedInAdminIdAsync(client);

        var html = await PostThenFollowAsync(client, "Suspend", admin, "oops");

        Assert.Contains("cannot suspend yourself or another platform admin", Alert.Match(html).Groups[1].Value);
    }

    [Fact]
    public async Task A_suspend_of_a_missing_account_says_so_and_never_echoes_the_submitted_text()
    {
        using var client = Client();
        _ = await SignedInAdminIdAsync(client);
        const string canary = "<script>CANARY-notice-77</script>";

        var html = await PostThenFollowAsync(client, "Suspend", Guid.NewGuid(), canary);

        Assert.Contains("does not exist", Alert.Match(html).Groups[1].Value);
        Assert.DoesNotContain("CANARY-notice-77", html);
    }

    [Fact]
    public async Task An_unsuspend_of_an_active_account_says_it_is_already_in_that_state_and_the_notice_shows_once()
    {
        using var client = Client();
        _ = await SignedInAdminIdAsync(client);
        var member = await SeedMemberAsync(MembershipStatus.Active);

        var html = await PostThenFollowAsync(client, "Unsuspend", member, "appeal");

        Assert.Contains("already in that state", Alert.Match(html).Groups[1].Value);
        Assert.DoesNotContain("role=\"alert\"", await (await client.GetAsync("/App/Members")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_successful_suspend_shows_no_refusal()
    {
        using var client = Client();
        _ = await SignedInAdminIdAsync(client);
        var member = await SeedMemberAsync(MembershipStatus.Active);

        var html = await PostThenFollowAsync(client, "Suspend", member, "spam");

        Assert.DoesNotContain("role=\"alert\"", html);
    }

    private async Task<(MembershipStatus Status, int Roles)> StateAsync(Guid user)
    {
        using var scope = factory.Services.CreateScope();
        var m = await scope.ServiceProvider.GetRequiredService<BifrostDbContext>().Memberships.AsNoTracking().Include(x => x.RoleAssignments).SingleAsync(x => x.UserAccountId == user);
        return (m.Status, m.RoleAssignments.Count);
    }

    [Fact]
    public async Task Approving_a_suspended_account_says_only_unsuspend_lifts_it_and_leaves_it_suspended()
    {
        using var client = Client();
        _ = await SignedInAdminIdAsync(client);
        var member = await SeedMemberAsync(MembershipStatus.Suspended);

        var html = await PostThenFollowAsync(client, "Approve", member, "");

        Assert.Contains("only be lifted with Unsuspend", Alert.Match(html).Groups[1].Value);
        Assert.Equal(MembershipStatus.Suspended, (await StateAsync(member)).Status);
    }

    [Fact]
    public async Task Toggling_a_role_on_a_suspended_account_is_refused_with_a_notice_and_changes_nothing()
    {
        using var client = Client();
        _ = await SignedInAdminIdAsync(client);
        var member = await SeedMemberAsync(MembershipStatus.Suspended);

        var html = await PostThenFollowAsync(client, "ToggleRole", member, "", role: "Producer");

        Assert.Contains("roles cannot be changed until it is unsuspended", Alert.Match(html).Groups[1].Value);
        Assert.Equal(0, (await StateAsync(member)).Roles);
    }

    [Fact]
    public async Task Toggling_a_role_on_an_account_without_a_membership_says_so()
    {
        using var client = Client();
        _ = await SignedInAdminIdAsync(client);
        var member = await SeedMemberAsync(MembershipStatus.Active, withMembership: false);

        var html = await PostThenFollowAsync(client, "ToggleRole", member, "", role: "Producer");

        Assert.Contains("has no membership yet", Alert.Match(html).Groups[1].Value);
    }

    [Fact]
    public async Task An_admin_toggling_their_own_platform_admin_role_is_refused_and_keeps_the_role()
    {
        using var client = Client();
        var admin = await SignedInAdminIdAsync(client);
        var before = (await StateAsync(admin)).Roles;

        var html = await PostThenFollowAsync(client, "ToggleRole", admin, "", role: "PlatformAdmin");

        Assert.Contains("cannot remove or change your own platform admin role", Alert.Match(html).Groups[1].Value);
        Assert.Equal(before, (await StateAsync(admin)).Roles);
    }

    [Fact]
    public async Task A_role_toggle_on_an_active_account_still_works_without_a_refusal()
    {
        using var client = Client();
        _ = await SignedInAdminIdAsync(client);
        var member = await SeedMemberAsync(MembershipStatus.Active);

        var html = await PostThenFollowAsync(client, "ToggleRole", member, "", role: "Producer");

        Assert.DoesNotContain("role=\"alert\"", html);
        Assert.True((await StateAsync(member)).Roles >= 1);
    }
}
