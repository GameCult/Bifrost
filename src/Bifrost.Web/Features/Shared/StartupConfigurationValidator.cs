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
    IOptions<ConceptVoteOptions> conceptVoteOptions,
    IOptions<DeskOptions> deskOptions)
{
    public void Validate()
    {
        var failures = new List<string>();

        // Checked in every environment: a bad value here would otherwise pass startup and throw on every cast.
        var conceptVotes = conceptVoteOptions.Value;
        if (!NetworkPrefix.IsValidLength(conceptVotes.IPv4PrefixLength, NetworkPrefix.IPv4Bits))
        {
            failures.Add($"ConceptVotes:IPv4PrefixLength must be between 0 and {NetworkPrefix.IPv4Bits}.");
        }

        if (!NetworkPrefix.IsValidLength(conceptVotes.IPv6PrefixLength, NetworkPrefix.IPv6Bits))
        {
            failures.Add($"ConceptVotes:IPv6PrefixLength must be between 0 and {NetworkPrefix.IPv6Bits}.");
        }

        if (conceptVotes.CastPermits < 1 || conceptVotes.CastWindow <= TimeSpan.Zero)
        {
            failures.Add("ConceptVotes:CastPermits must be at least 1 and ConceptVotes:CastWindow must be positive.");
        }

        if (conceptVotes.MintPermits < 1 || conceptVotes.MintWindow <= TimeSpan.Zero)
        {
            failures.Add("ConceptVotes:MintPermits must be at least 1 and ConceptVotes:MintWindow must be positive.");
        }

        if (conceptVotes.MaxTrackedKeys < 1)
        {
            failures.Add("ConceptVotes:MaxTrackedKeys must be at least 1.");
        }

        ValidateDesk(deskOptions.Value, failures);

        if (hostEnvironment.IsDevelopment() || hostEnvironment.IsEnvironment("Testing"))
        {
            Refuse(failures);
            return;
        }

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

        if (string.IsNullOrWhiteSpace(conceptVotes.NetworkTagSecret) ||
            conceptVotes.NetworkTagSecret.Length < ConceptVoteOptions.MinNetworkTagSecretLength)
        {
            failures.Add(
                $"ConceptVotes:NetworkTagSecret must be configured with at least {ConceptVoteOptions.MinNetworkTagSecretLength} characters for production.");
        }

        if (!conceptVotes.AllowedOrigins.All(IsExactHttpsOrigin))
        {
            failures.Add("ConceptVotes:AllowedOrigins entries must each be exactly https://host with no path, wildcard or trailing slash.");
        }

        Refuse(failures);
    }

    // Checked in every environment: a bad Mind list or decider id would otherwise pass startup and misroute or
    // mis-attribute answers. Account existence is a request-time question, not a startup one.
    private static void ValidateDesk(DeskOptions desk, List<string> failures)
    {
        if (!desk.IsConfigured)
        {
            return;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < desk.Minds.Count; i++)
        {
            var mind = desk.Minds[i];
            if (string.IsNullOrWhiteSpace(mind.Id))
            {
                failures.Add($"Desk:Minds:{i}:Id must not be blank.");
            }
            else if (!ids.Add(mind.Id))
            {
                failures.Add($"Desk:Minds:{i}:Id repeats an earlier Mind id.");
            }

            if (!Path.IsPathFullyQualified(mind.Socket))
            {
                failures.Add($"Desk:Minds:{i}:Socket must be an absolute path.");
            }

            var deciders = new HashSet<string>(StringComparer.Ordinal);
            foreach (var decider in mind.Deciders)
            {
                if (!IsLowercaseGuid(decider))
                {
                    failures.Add($"Desk:Minds:{i}:Deciders entries must be lowercase D-format account ids.");
                }
                else if (!deciders.Add(decider))
                {
                    failures.Add($"Desk:Minds:{i}:Deciders repeats an account id.");
                }
            }
        }

        var attribution = desk.Attribution;
        if (!string.IsNullOrEmpty(attribution.Operator) && !IsLowercaseGuid(attribution.Operator))
        {
            failures.Add("Desk:Attribution:Operator must be a lowercase D-format account id.");
        }

        var stewards = new HashSet<string>(StringComparer.Ordinal);
        foreach (var steward in attribution.Stewards)
        {
            if (!IsLowercaseGuid(steward))
            {
                failures.Add("Desk:Attribution:Stewards entries must be lowercase D-format account ids.");
            }
            else if (!stewards.Add(steward))
            {
                failures.Add("Desk:Attribution:Stewards repeats an account id.");
            }
            else if (string.Equals(steward, attribution.Operator, StringComparison.Ordinal))
            {
                failures.Add("Desk:Attribution:Operator must not also be a steward.");
            }
        }
    }

    private static bool IsLowercaseGuid(string? value) =>
        Guid.TryParseExact(value, "D", out var guid) && string.Equals(value, guid.ToString("D"), StringComparison.Ordinal);

    private static void Refuse(List<string> failures)
    {
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
