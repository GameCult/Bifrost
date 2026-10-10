using Bifrost.Web.Configuration;
using Bifrost.Web.Features.ConceptVotes;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Features.Shared;

public sealed class StartupConfigurationValidator(
    IConfiguration configuration,
    IHostEnvironment hostEnvironment,
    IOptions<GitHubOAuthOptions> gitHubOAuthOptions,
    IOptions<GitHubAppOptions> gitHubAppOptions,
    IOptions<HeimdallOptions> heimdallOptions,
    IOptions<StripeOptions> stripeOptions,
    IOptions<BifrostHostOptions> hostOptions,
    IOptions<ConceptVoteOptions> conceptVoteOptions)
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

        if (string.IsNullOrWhiteSpace(configuration["DataProtection:KeysPath"]))
        {
            failures.Add("DataProtection:KeysPath must be configured for production.");
        }

        var conceptVotes = conceptVoteOptions.Value;
        if (string.IsNullOrWhiteSpace(conceptVotes.NetworkTagSecret) ||
            conceptVotes.NetworkTagSecret.Length < ConceptVoteOptions.MinNetworkTagSecretLength)
        {
            failures.Add(
                $"ConceptVotes:NetworkTagSecret must be configured with at least {ConceptVoteOptions.MinNetworkTagSecretLength} characters for production.");
        }

        if (!NetworkPrefix.IsValidLength(conceptVotes.IPv4PrefixLength, NetworkPrefix.IPv4Bits))
        {
            failures.Add($"ConceptVotes:IPv4PrefixLength must be between 0 and {NetworkPrefix.IPv4Bits}.");
        }

        if (!NetworkPrefix.IsValidLength(conceptVotes.IPv6PrefixLength, NetworkPrefix.IPv6Bits))
        {
            failures.Add($"ConceptVotes:IPv6PrefixLength must be between 0 and {NetworkPrefix.IPv6Bits}.");
        }

        if (!conceptVotes.AllowedOrigins.All(IsExactHttpsOrigin))
        {
            failures.Add("ConceptVotes:AllowedOrigins entries must each be exactly https://host with no path, wildcard or trailing slash.");
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                "Bifrost startup configuration is incomplete: " + string.Join(" ", failures));
        }
    }

    // The authority form is the canonical spelling, so a wildcard, path, query, upper-case host or default port
    // differs from it and is refused; user info is part of the authority, so it is refused on its own.
    private static bool IsExactHttpsOrigin(string? origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.UserInfo.Length == 0 &&
        string.Equals(uri.GetLeftPart(UriPartial.Authority), origin, StringComparison.Ordinal);
}
