using Bifrost.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Bifrost.Web.Tests;

public sealed class MigrationDiscoveryTests
{
    private static readonly string[] ExpectedMigrations =
    [
        "20260422084653_InitialAlphaFoundation",
        "20260608210000_AddHeimdallUserIdentity",
        "20260609143000_AddPatronSupportDerivation",
        "20260609160000_AddExternalPatronProviderIntake",
        "20260617010537_AddBridgeActionLedger",
        "20260617022305_AddAgentDispatchRuns",
        "20260617033635_AddAgentTransportReceipts",
        "20260617041417_AddGovernanceActivityReceipts",
        "20260622142000_AddBridgeActionIdentityProvenance",
        "20260622165000_AddBifrostNativeIdentity",
        "20260622202000_AddBridgeActionTargetSurfaceName",
        "20260705190000_AddPatronSupportProjectAttribution",
    ];

    // Database.GetMigrations() reads assembly metadata only; no connection is opened.
    private static List<string> Discovered()
    {
        var options = new DbContextOptionsBuilder<BifrostDbContext>()
            .UseNpgsql("Host=unused")
            .Options;
        using var db = new BifrostDbContext(options);
        return db.Database.GetMigrations().ToList();
    }

    [Fact]
    public void Every_migration_type_is_discovered()
    {
        var discovered = Discovered();
        var declared = typeof(BifrostDbContext).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(Migration).IsAssignableFrom(t))
            .ToList();

        Assert.NotEmpty(declared);
        Assert.Equal(declared.Count, discovered.Count);
        foreach (var type in declared)
        {
            Assert.Contains(discovered, id => id.EndsWith("_" + type.Name, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void All_twelve_migrations_are_listed_in_order()
    {
        Assert.Equal(ExpectedMigrations, Discovered());
    }
}
