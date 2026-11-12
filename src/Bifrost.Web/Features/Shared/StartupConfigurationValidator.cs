using Bifrost.Web.Configuration;
using Bifrost.Web.Features.ConceptVotes;
using Bifrost.Web.Features.Registration;
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
    IOptions<DeskOptions> deskOptions,
    IOptions<RegistrationOptions> registrationOptions,
    OidcIssuerOptions? oidcOptions = null)
{
    public void Validate()
    {
        var failures = new List<string>();

        // Building the options resolves Heimdall:AppSecretFile, and a bad file stops startup in every environment.
        _ = heimdallOptions.Value;

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

        var difficulty = registrationOptions.Value.Captcha.Difficulty;
        if (difficulty < RegistrationOptions.MinDifficulty || difficulty > RegistrationOptions.MaxDifficulty)
        {
            failures.Add($"Bifrost:Registration:Captcha:Difficulty must be between {RegistrationOptions.MinDifficulty} and {RegistrationOptions.MaxDifficulty}.");
        }

        var wait = registrationOptions.Value.Captcha.FallbackWaitSeconds;
        if (wait < RegistrationOptions.MinFallbackWaitSeconds || wait > RegistrationOptions.MaxFallbackWaitSeconds ||
            wait >= RegistrationChallengeService.Lifetime.TotalSeconds)
        {
            failures.Add($"Bifrost:Registration:Captcha:FallbackWaitSeconds must be between {RegistrationOptions.MinFallbackWaitSeconds} and {RegistrationOptions.MaxFallbackWaitSeconds} and shorter than the challenge lifetime ({RegistrationChallengeService.Lifetime.TotalSeconds:0} seconds).");
        }

        var limits = registrationOptions.Value.Limits;
        if (limits.PerAddressPermits < 1 || limits.Window <= TimeSpan.Zero || limits.MaxTrackedKeys < 1)
        {
            failures.Add("Bifrost:Registration:Limits:PerAddressPermits and MaxTrackedKeys must be at least 1 and Window must be positive.");
        }

        if (limits.FallbackPermits < 1 || limits.FallbackWindow <= TimeSpan.Zero)
        {
            failures.Add("Bifrost:Registration:Limits:FallbackPermits must be at least 1 and FallbackWindow must be positive.");
        }

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

        // The options are already resolved, so a secret from Heimdall:AppSecretFile or Heimdall:AppSecret lands in AppSecret.
        // Blank would silently switch Discord and Patreon member sign-in off.
        if (string.IsNullOrWhiteSpace(heimdallOptions.Value.AppSecret))
        {
            failures.Add("Heimdall:AppSecretFile (or Heimdall:AppSecret) must supply the Heimdall app secret outside development.");
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
    private void ValidateDesk(DeskOptions desk, List<string> failures)
    {
        // The desk's bearer is validated against Bifrost's own issuer: without Bifrost:Oidc there is no credential to serve with.
        if (oidcOptions is null && configuration.GetSection(DeskOptions.SectionName).Exists())
        {
            failures.Add("Bifrost:Oidc must be configured when the Desk section exists: the desk validates its bearer tokens against Bifrost's own issuer.");
        }

        if (!desk.IsConfigured)
        {
            if (configuration.GetSection(DeskOptions.SectionName).Exists())
            {
                failures.Add("Desk:Minds must list at least one Mind when the Desk section exists.");
            }

            return;
        }

        if (!Uri.TryCreate(desk.AllowedOrigin, UriKind.Absolute, out var origin) ||
            origin.Scheme is not ("http" or "https") ||
            !string.Equals(desk.AllowedOrigin, origin.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal))
        {
            failures.Add("Desk:AllowedOrigin must be one http(s) origin: scheme, host and optional port, no path.");
        }

        if (desk.MaxBodyBytes < 1)
        {
            failures.Add("Desk:MaxBodyBytes must be at least 1.");
        }

        if (desk.RetryAfterSeconds < 1)
        {
            failures.Add("Desk:RetryAfterSeconds must be at least 1.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var sockets = new HashSet<string>(StringComparer.Ordinal);
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
            else if (!sockets.Add(mind.Socket))
            {
                failures.Add($"Desk:Minds:{i}:Socket repeats an earlier Mind's socket.");
            }

            if (mind.Deciders.Count is < 1 or > DeskMindOptions.MaxDeciders)
            {
                failures.Add($"Desk:Minds:{i}:Deciders must hold 1 to {DeskMindOptions.MaxDeciders} account ids.");
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
