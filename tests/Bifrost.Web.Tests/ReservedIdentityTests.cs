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

        var ex = await Assert.ThrowsAsync<BifrostIdentityException>(() => service.RegisterNativeAsync(name, null, 1, default));

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

        await Assert.ThrowsAsync<BifrostIdentityException>(() => service.RegisterNativeAsync(name, null, 1, default));

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

        var account = await service.RegisterNativeAsync(name, null, 1, default);

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

    // The whole set, so dropping or misspelling any one name fails here.
    public static IEnumerable<object[]> EveryReservedName() =>
        new[]
        {
            "admin", "aqua", "deru", "eureka", "heimdall", "ivixey", "kiko", "libby", "meta", "nibu", "unnie", "void", "weksa",
            "root", "system", "matrix", "synapse", "support", "bot", "mod", "moderator", "postmaster", "gamecult",
            "bifrost", "idunn", "odin", "huginn", "ratatoskr"
        }.Select(name => new object[] { name });

    [Fact]
    public void The_reserved_set_is_exactly_the_28_named_names()
    {
        Assert.Equal(28, EveryReservedName().Count());
        Assert.All(EveryReservedName().Select(x => (string)x[0]), name => Assert.True(BifrostIdentityService.IsReserved(name), name));
    }

    [Theory]
    [MemberData(nameof(EveryReservedName))]
    public async Task Every_reserved_name_is_refused_by_native_register(string name)
    {
        var service = NewService(out var db);

        var ex = await Assert.ThrowsAsync<BifrostIdentityException>(() => service.RegisterNativeAsync(name, null, 1, default));

        Assert.Equal("That Bifrost identity is reserved.", ex.Message);
        Assert.Empty(db.UserAccounts);
    }

    [Theory]
    [MemberData(nameof(EveryReservedName))]
    public async Task Every_reserved_name_takes_a_suffix_as_a_derived_identity(string name)
    {
        var service = NewService(out _);
        var account = new UserAccount { DisplayName = name };

        await service.EnsureIdentityAsync(account, name, default);

        Assert.Equal($"{name}-2", account.NormalizedBifrostIdentity);
    }

    [Theory]
    [InlineData("Meta")]
    [InlineData("mETA")]
    [InlineData("RATATOSKR")]
    [InlineData("Postmaster")]
    public void Matching_is_case_insensitive_on_the_set_itself(string mixedCase)
    {
        Assert.True(BifrostIdentityService.IsReserved(mixedCase));
    }

    [Theory]
    [InlineData("MeTa")]
    [InlineData("RATATOSKR")]
    [InlineData("  Moderator  ")]
    public async Task Mixed_case_input_is_refused_by_register_and_suffixed_when_derived(string input)
    {
        var service = NewService(out var db);
        await Assert.ThrowsAsync<BifrostIdentityException>(() => service.RegisterNativeAsync(input, null, 1, default));
        Assert.Empty(db.UserAccounts);

        var account = new UserAccount { DisplayName = input };
        await service.EnsureIdentityAsync(account, input, default);
        Assert.Equal(input.Trim().ToLowerInvariant() + "-2", account.NormalizedBifrostIdentity);
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
