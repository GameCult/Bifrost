using System.Net;
using Bifrost.Web.Data;
using Bifrost.Web.Data.Migrations;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Registration;
using Bifrost.Web.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.Web.Tests;

/// <summary>Throws the store's refusal when a receipt is inserted and the flag is up, as the filtered unique index does to the loser of a race.</summary>
public sealed class UniqueViolationInterceptor : SaveChangesInterceptor
{
    public volatile bool Armed;

    /// <summary>The SqlState to wrap in the thrown exception.</summary>
    public volatile string SqlState = Npgsql.PostgresErrorCodes.UniqueViolation;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (Armed && eventData.Context!.ChangeTracker.Entries<ConsentReceipt>().Any(x => x.State == EntityState.Added))
        {
            throw new DbUpdateException(
                "An error occurred while saving the entity changes.",
                new Npgsql.PostgresException("store refused the insert", "ERROR", "ERROR", SqlState));
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

public sealed class UniqueViolationFactory : TestWebApplicationFactory
{
    public UniqueViolationInterceptor Interceptor { get; } = new();

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            // Wrap the options the base factory registered so the interceptor rides on the same in-memory store.
            var original = services.Single(x => x.ServiceType == typeof(DbContextOptions<BifrostDbContext>));
            services.Remove(original);
            services.Add(ServiceDescriptor.Describe(
                typeof(DbContextOptions<BifrostDbContext>),
                sp => new DbContextOptionsBuilder<BifrostDbContext>((DbContextOptions<BifrostDbContext>)original.ImplementationFactory!(sp)).AddInterceptors(Interceptor).Options,
                original.Lifetime));
        });
    }
}

public sealed class ConsentRaceTests(UniqueViolationFactory factory) : IClassFixture<UniqueViolationFactory>
{
    private async Task<HttpResponseMessage> AcceptWithStoreFailureAsync(string sqlState, string identity)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/bifrost/consent/accept")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["version"] = TrainingNotice.Version.ToString() })
        };
        request.Headers.Add("X-Test-Bifrost-Identity", identity);
        request.Headers.Add("Origin", "https://localhost");

        factory.Interceptor.SqlState = sqlState;
        factory.Interceptor.Armed = true;
        try
        {
            return await client.SendAsync(request);
        }
        finally
        {
            factory.Interceptor.Armed = false;
        }
    }

    private Guid NewAccount(string identity)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BifrostDbContext>();
        var account = new UserAccount { BifrostIdentity = identity, NormalizedBifrostIdentity = identity, DisplayName = identity };
        db.UserAccounts.Add(account);
        db.SaveChanges();
        return account.Id;
    }

    private bool HasReceipt(Guid accountId)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<BifrostDbContext>().ConsentReceipts.Any(x => x.UserAccountId == accountId);
    }

    [Fact]
    public async Task The_loser_of_an_accept_race_gets_303_not_500_and_no_second_receipt()
    {
        var identity = $"race-{Guid.NewGuid():N}";
        var accountId = NewAccount(identity);

        using var response = await AcceptWithStoreFailureAsync(Npgsql.PostgresErrorCodes.UniqueViolation, identity);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/auth/bifrost/consent", response.Headers.Location!.OriginalString);
        Assert.False(HasReceipt(accountId));
    }

    [Theory]
    [InlineData(Npgsql.PostgresErrorCodes.ForeignKeyViolation)]
    [InlineData(Npgsql.PostgresErrorCodes.DeadlockDetected)]
    [InlineData(Npgsql.PostgresErrorCodes.NotNullViolation)]
    public async Task Any_other_store_fault_on_accept_is_a_500_not_a_silent_redirect(string sqlState)
    {
        var identity = $"fault-{Guid.NewGuid():N}";
        var accountId = NewAccount(identity);

        using var response = await AcceptWithStoreFailureAsync(sqlState, identity);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(HasReceipt(accountId));
    }
}

// The in-memory provider ignores index filters, so the model and the migration are checked directly.
public sealed class ConsentReceiptIndexTests
{
    private const string Filter = "WithdrawnAtUtc\" IS NULL";

    private static IIndex ReceiptIndex(IModel model) =>
        model.FindEntityType(typeof(ConsentReceipt))!.GetIndexes()
            .Single(x => x.Properties.Select(p => p.Name).SequenceEqual(["UserAccountId", "NoticeVersion"]));

    [Fact]
    public void The_design_time_model_and_the_snapshot_have_a_filtered_unique_index_on_live_receipts()
    {
        using var db = new BifrostDbContext(new DbContextOptionsBuilder<BifrostDbContext>().UseNpgsql("Host=unused").Options);

        foreach (var model in new[] { db.GetService<IDesignTimeModel>().Model, db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrationsAssembly>().ModelSnapshot!.Model })
        {
            var index = ReceiptIndex(model);
            Assert.True(index.IsUnique);
            Assert.Contains(Filter, index.GetFilter());
        }
    }

    [Fact]
    public void The_latest_consent_migration_creates_the_same_filter()
    {
        var up = new ConsentReceiptsOneLivePerVersion().UpOperations.OfType<CreateIndexOperation>()
            .Single(x => x.Table == "ConsentReceipts");

        Assert.True(up.IsUnique);
        Assert.Contains(Filter, up.Filter);
        Assert.Equal(["UserAccountId", "NoticeVersion"], up.Columns);
    }
}
