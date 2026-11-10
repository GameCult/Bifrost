using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Membership;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Web.Tests;

// The identity becomes the Matrix localpart at SSO, so a name a Matrix account already holds (or a system name) must
// not be minted for a new account. Native register refuses it; a derived identity takes the next free suffix.
public sealed class ReservedIdentityTests
{
    private static BifrostIdentityService NewService(out BifrostDbContext db)
    {
        db = new BifrostDbContext(new DbContextOptionsBuilder<BifrostDbContext>()
            .UseInMemoryDatabase($"reserved-{Guid.NewGuid():N}")
            .Options);
        return new BifrostIdentityService(db, TimeProvider.System);
    }

    [Theory]
    [InlineData("meta")]
    [InlineData("eureka")]
    [InlineData("admin")]
    [InlineData("heimdall")]
    [InlineData("root")]
    [InlineData("postmaster")]
    public async Task Native_register_refuses_a_reserved_name(string name)
    {
        var service = NewService(out var db);

        var ex = await Assert.ThrowsAsync<BifrostIdentityException>(() => service.RegisterNativeAsync(name, null, default));

        Assert.Equal("That Bifrost identity is reserved.", ex.Message);
        Assert.Empty(db.UserAccounts);
    }

    [Theory]
    [InlineData("Meta")]
    [InlineData("ADMIN")]
    [InlineData("  EuReKa  ")]
    public async Task Native_register_refuses_a_reserved_name_in_any_case(string name)
    {
        var service = NewService(out var db);

        await Assert.ThrowsAsync<BifrostIdentityException>(() => service.RegisterNativeAsync(name, null, default));

        Assert.Empty(db.UserAccounts);
    }

    [Theory]
    [InlineData("meta2")]
    [InlineData("meta-x")]
    [InlineData("admins")]
    [InlineData("ordinary-member")]
    public async Task Native_register_accepts_a_name_that_only_resembles_a_reserved_one(string name)
    {
        var service = NewService(out var db);

        var account = await service.RegisterNativeAsync(name, null, default);

        Assert.Equal(name, account.NormalizedBifrostIdentity);
        Assert.Single(db.UserAccounts);
    }

    [Theory]
    [InlineData("Meta", "meta-2")]
    [InlineData("Admin", "admin-2")]
    [InlineData("Eureka!", "eureka-2")]
    public async Task A_derived_identity_never_lands_on_a_reserved_name(string preferred, string expected)
    {
        var service = NewService(out var db);
        var account = new UserAccount { DisplayName = preferred };

        await service.EnsureIdentityAsync(account, preferred, default);

        Assert.Equal(expected, account.NormalizedBifrostIdentity);
        Assert.Equal(expected, account.BifrostIdentity);
    }

    [Fact]
    public async Task A_derived_identity_skips_the_reserved_name_and_a_taken_suffix()
    {
        var service = NewService(out var db);
        db.UserAccounts.Add(new UserAccount { BifrostIdentity = "meta-2", NormalizedBifrostIdentity = "meta-2", DisplayName = "x" });
        await db.SaveChangesAsync();
        var account = new UserAccount { DisplayName = "Meta" };

        await service.EnsureIdentityAsync(account, "Meta", default);

        Assert.Equal("meta-3", account.NormalizedBifrostIdentity);
    }

    [Fact]
    public async Task An_ordinary_derived_identity_is_unchanged()
    {
        var service = NewService(out _);
        var account = new UserAccount { DisplayName = "Some Person" };

        await service.EnsureIdentityAsync(account, "Some Person", default);

        Assert.Equal("some-person", account.NormalizedBifrostIdentity);
    }

    [Fact]
    public async Task An_account_that_already_holds_a_reserved_identity_is_left_alone()
    {
        var service = NewService(out _);
        var account = new UserAccount { DisplayName = "x", BifrostIdentity = "meta", NormalizedBifrostIdentity = "meta" };

        await service.EnsureIdentityAsync(account, "something-else", default);

        Assert.Equal("meta", account.NormalizedBifrostIdentity);
    }

    [Fact]
    public void Lookup_normalization_still_accepts_a_reserved_name()
    {
        Assert.Equal("meta", BifrostIdentityService.NormalizeIdentity("Meta"));
        Assert.True(BifrostIdentityService.IsReserved("meta"));
        Assert.False(BifrostIdentityService.IsReserved("meta-2"));
    }
}
