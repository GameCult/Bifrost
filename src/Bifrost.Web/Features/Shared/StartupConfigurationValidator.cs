using Bifrost.Web.Configuration;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Features.Shared;

public sealed class StartupConfigurationValidator(
    IConfiguration configuration,
    IHostEnvironment hostEnvironment,
    IOptions<GitHubOAuthOptions> gitHubOAuthOptions,
    IOptions<GitHubAppOptions> gitHubAppOptions,
    IOptions<HeimdallOptions> heimdallOptions,
    IOptions<StripeOptions> stripeOptions,
    IOptions<BifrostHostOptions> hostOptions)
{
    public void Validate()
    {
        if (hostEnvironment.IsDevelopment() || hostEnvironment.IsEnvironment("Testing"))
        {
            return;
        }

        var failures = new List<string>();

        if (!gitHubOAuthOptions.Value.IsConfigured)
        {
            failures.Add("GitHub OAuth client configuration is required outside development.");
        }

        if (gitHubAppOptions.Value.EnableWebhookSync && !gitHubAppOptions.Value.IsConfigured)
        {
            failures.Add("GitHub App configuration is required when webhook sync is enabled.");
        }

        if (!hostOptions.Value.IsConfigured)
        {
            failures.Add("Host:PublicBaseUrl and Host:ExpectedHost must be configured.");
        }

        if (heimdallOptions.Value.EnablePatronSupportIntake &&
            !heimdallOptions.Value.IsPatronSupportIntakeConfigured)
        {
            failures.Add("Heimdall:PatronSupportIntakeSecret is required when patron support intake is enabled.");
        }

        if (stripeOptions.Value.EnableCheckout)
        {
            if (!stripeOptions.Value.IsCheckoutConfigured)
            {
                failures.Add("Stripe:SecretKey, Stripe:SuccessUrl, and Stripe:CancelUrl are required when Stripe checkout is enabled.");
            }

            // Stripe:GeneralPatronageGitHubLogin is only a legacy fallback for old sessions
            // that predate account-bound checkout metadata.
        }

        var allowedHosts = configuration["AllowedHosts"] ?? string.Empty;
        if (hostOptions.Value.RequireStrictHostValidation &&
            (string.IsNullOrWhiteSpace(allowedHosts) || allowedHosts.Contains('*')))
        {
            failures.Add("AllowedHosts must be explicitly configured for production.");
        }

        var connectionString = configuration.GetConnectionString("Bifrost");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            failures.Add("Connection string 'Bifrost' must be configured.");
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                "Bifrost startup configuration is incomplete: " + string.Join(" ", failures));
        }
    }
}
