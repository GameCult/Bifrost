using Bifrost.Web.Data;
using Bifrost.Web.Features.Patronage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using System.Net;

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
                ["Bridge:LocalBridgeToken"] = "test-bridge-token",
                ["Host:PublicBaseUrl"] = "https://localhost",
                ["Host:ExpectedHost"] = "localhost",
                ["Host:RequireStrictHostValidation"] = "false",
                ["Heimdall:PatronSupportIntakeSecret"] = "test-heimdall-intake-secret",
                ["Stripe:EnableCheckout"] = "true",
                ["Stripe:SecretKey"] = "sk_test_configured",
                ["Stripe:WebhookSecret"] = "whsec_test_webhook_secret",
                ["Stripe:GeneralPatronageGitHubLogin"] = "test-admin",
                ["Stripe:SuccessUrl"] = "https://velvet.gamecult.org/?patronage=success",
                ["Stripe:CancelUrl"] = "https://velvet.gamecult.org/?patronage=cancelled"
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

            services.RemoveAll<StripeCheckoutService>();
            services.AddTransient(serviceProvider =>
            {
                var client = new HttpClient(new TestStripeCheckoutHandler())
                {
                    BaseAddress = new Uri("https://api.stripe.test/")
                };

                return new StripeCheckoutService(
                    client,
                    serviceProvider.GetRequiredService<BifrostDbContext>(),
                    serviceProvider.GetRequiredService<IOptions<Bifrost.Web.Configuration.StripeOptions>>());
            });
        });
    }

    private sealed class TestStripeCheckoutHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var form = await request.Content!.ReadAsStringAsync(cancellationToken);
            var hasExpectedMetadata =
                form.Contains("metadata%5Bpurpose%5D=general_patronage", StringComparison.Ordinal) &&
                form.Contains("metadata%5Bledger%5D=bifrost", StringComparison.Ordinal) &&
                form.Contains("metadata%5Bproject%5D=velvet", StringComparison.Ordinal) &&
                form.Contains("metadata%5Bitem%5D=velvet-room", StringComparison.Ordinal) &&
                form.Contains("metadata%5Bbifrost_user_account_id%5D=", StringComparison.Ordinal);

            if (request.RequestUri?.PathAndQuery != "/v1/checkout/sessions" || !hasExpectedMetadata)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{}")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "id": "cs_test_velvet",
                      "url": "https://checkout.stripe.com/c/pay/cs_test_velvet"
                    }
                    """)
            };
        }
    }
}
