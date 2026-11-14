using Bifrost.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.Web.Tests;

public sealed class SnapshotDriftTests
{
    // Migrations here are hand-written with a hand-edited snapshot, so the snapshot is compared with the model the app runs on:
    // a column or index the model has and the snapshot lacks would never reach a database.
    [Fact]
    public void The_model_snapshot_has_no_drift_from_the_design_time_model()
    {
        using var db = new BifrostDbContext(new DbContextOptionsBuilder<BifrostDbContext>().UseNpgsql("Host=unused").Options);
        var snapshotModel = db.GetService<IMigrationsAssembly>().ModelSnapshot!.Model;
        var designModel = db.GetService<IDesignTimeModel>().Model;
        var differ = db.GetService<IMigrationsModelDiffer>();

        var differences = differ.GetDifferences(
            db.GetService<IModelRuntimeInitializer>().Initialize(snapshotModel).GetRelationalModel(),
            designModel.GetRelationalModel());

        Assert.Empty(differences.Select(x => x.GetType().Name + ": " + x));
    }
}
