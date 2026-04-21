using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Bifrost.Web.Data;

public sealed class BifrostDbContextFactory : IDesignTimeDbContextFactory<BifrostDbContext>
{
    public BifrostDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("Bifrost")
            ?? "Host=127.0.0.1;Database=bifrost;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<BifrostDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new BifrostDbContext(options);
    }
}
