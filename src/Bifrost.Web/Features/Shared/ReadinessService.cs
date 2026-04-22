using Bifrost.Web.Configuration;
using Bifrost.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Features.Shared;

public sealed class ReadinessService(
    BifrostDbContext dbContext,
    IConfiguration configuration,
    IOptions<GitHubOAuthOptions> gitHubOAuthOptions,
    IOptions<GitHubAppOptions> gitHubAppOptions,
    IOptions<BifrostHostOptions> hostOptions)
{
    public async Task<ReadinessReport> GetReportAsync(CancellationToken cancellationToken)
    {
        var failures = new List<string>();

        if (!gitHubOAuthOptions.Value.IsConfigured)
        {
            failures.Add("GitHub OAuth is not configured.");
        }

        if (gitHubAppOptions.Value.EnableWebhookSync && !gitHubAppOptions.Value.IsWebhookConfigured)
        {
            failures.Add("GitHub App webhook sync is enabled but the webhook secret is missing.");
        }

        if (!hostOptions.Value.IsConfigured)
        {
            failures.Add("Host public base URL or expected host is missing.");
        }

        var connectionString = configuration.GetConnectionString("Bifrost");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            failures.Add("Connection string 'Bifrost' is missing.");
        }

        try
        {
            if (dbContext.Database.IsRelational())
            {
                var canConnect = await dbContext.Database.CanConnectAsync(cancellationToken);
                if (!canConnect)
                {
                    failures.Add("Database connectivity check failed.");
                }
            }
        }
        catch (Exception exception)
        {
            failures.Add($"Database readiness check failed: {exception.Message}");
        }

        return new ReadinessReport(failures.Count == 0, failures);
    }
}

public sealed record ReadinessReport(bool IsReady, IReadOnlyList<string> Failures);
