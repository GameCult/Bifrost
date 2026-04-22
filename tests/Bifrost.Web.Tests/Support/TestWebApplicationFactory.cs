using Bifrost.Web.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bifrost.Web.Tests.Support;

public sealed class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly InMemoryDatabaseRoot _databaseRoot = new();
    private readonly string _databaseName = $"bifrost-tests-{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configurationBuilder) =>
        {
            configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bootstrap:AdminGitHubLogins:0"] = "test-admin",
                ["GitHubOAuth:ClientId"] = "configured-for-tests",
                ["GitHubOAuth:ClientSecret"] = "configured-for-tests",
                ["GitHubApp:WebhookSecret"] = "test-webhook-secret",
                ["GitHubApp:PrivateKeyPem"] = "test-private-key",
                ["GitHubApp:AppId"] = "1",
                ["Host:PublicBaseUrl"] = "https://localhost",
                ["Host:ExpectedHost"] = "localhost",
                ["Host:RequireStrictHostValidation"] = "false"
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll(typeof(DbContextOptions<BifrostDbContext>));
            services.RemoveAll<BifrostDbContext>();

            services.AddDbContext<BifrostDbContext>(options =>
            {
                options.UseInMemoryDatabase(_databaseName, _databaseRoot);
            });

            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                TestAuthHandler.SchemeName,
                _ => { });

            services.PostConfigure<CookieAuthenticationOptions>(
                CookieAuthenticationDefaults.AuthenticationScheme,
                options =>
                {
                    options.LoginPath = "/auth/sign-in";
                    options.AccessDeniedPath = "/Membership/Status";
                });
        });
    }
}
