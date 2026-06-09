using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bifrost.Web.Configuration;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Bifrost.Web.Features.Patronage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Features.Heimdall;

public sealed class HeimdallPatronSupportIntakeService(
    BifrostDbContext dbContext,
    PatronageService patronageService,
    IOptions<HeimdallOptions> heimdallOptions)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<HeimdallPatronSupportIntakeResult> ProcessAsync(
        string signature,
        string payload,
        CancellationToken cancellationToken)
    {
        if (!heimdallOptions.Value.IsPatronSupportIntakeConfigured)
        {
            return HeimdallPatronSupportIntakeResult.NotConfigured("Heimdall patron support intake is not configured.");
        }

        if (!VerifySignature(heimdallOptions.Value.PatronSupportIntakeSecret, payload, signature))
        {
            return HeimdallPatronSupportIntakeResult.Unauthorized("Invalid Heimdall patron support intake signature.");
        }

        var request = JsonSerializer.Deserialize<HeimdallPatronSupportEventRequest>(payload, JsonOptions);
        if (request is null)
        {
            return HeimdallPatronSupportIntakeResult.BadRequest("Patron support payload was empty.");
        }

        if (request.Provider is ExternalPatronProvider.Manual)
        {
            return HeimdallPatronSupportIntakeResult.BadRequest("Heimdall patron support intake requires an external provider.");
        }

        if (string.IsNullOrWhiteSpace(request.HeimdallAccountId))
        {
            return HeimdallPatronSupportIntakeResult.BadRequest("Patron support payload omitted Heimdall account id.");
        }

        if (string.IsNullOrWhiteSpace(request.ProviderEventId))
        {
            return HeimdallPatronSupportIntakeResult.BadRequest("Patron support payload omitted provider event id.");
        }

        var existingEvent = await dbContext.PatronSupportEvents
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.Provider == request.Provider &&
                    x.ProviderEventId == request.ProviderEventId.Trim(),
                cancellationToken);

        if (existingEvent is not null)
        {
            return HeimdallPatronSupportIntakeResult.Processed(
                $"Already processed {request.Provider} patron support event {request.ProviderEventId}.",
                existingEvent.Id);
        }

        var user = await dbContext.UserAccounts
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.HeimdallAccountId == request.HeimdallAccountId.Trim(), cancellationToken);

        if (user is null)
        {
            return HeimdallPatronSupportIntakeResult.BadRequest(
                $"No Bifrost account is linked to Heimdall account {request.HeimdallAccountId}.");
        }

        var supportEvent = await patronageService.RecordSupportEventAsync(
            actorUserAccountId: null,
            userAccountId: user.Id,
            request.Kind,
            request.Amount,
            request.CurrencyCode,
            request.ExternalSupportId,
            request.Provider,
            request.ProviderEventId,
            request.ProviderPayerId,
            request.ProviderSubscriptionId,
            request.SupportedAtUtc,
            request.IsCurrentRecurringSupport,
            request.Notes,
            cancellationToken);

        return HeimdallPatronSupportIntakeResult.Processed(
            $"Processed {request.Provider} patron support event {request.ProviderEventId}.",
            supportEvent.Id);
    }

    private static bool VerifySignature(string secret, string payload, string signature)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        var normalizedSignature = signature.Trim();
        if (normalizedSignature.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
        {
            normalizedSignature = normalizedSignature["sha256=".Length..];
        }

        using var hasher = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expectedBytes = hasher.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var expectedHex = Convert.ToHexString(expectedBytes).ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expectedHex),
            Encoding.ASCII.GetBytes(normalizedSignature.ToLowerInvariant()));
    }
}

public sealed record HeimdallPatronSupportEventRequest(
    string HeimdallAccountId,
    ExternalPatronProvider Provider,
    string ProviderEventId,
    PatronSupportEventKind Kind,
    decimal Amount,
    string CurrencyCode,
    string ExternalSupportId,
    DateTimeOffset SupportedAtUtc,
    bool IsCurrentRecurringSupport,
    string ProviderPayerId = "",
    string ProviderSubscriptionId = "",
    string Notes = "");

public sealed record HeimdallPatronSupportIntakeResult(
    HeimdallPatronSupportIntakeStatus Status,
    string Message,
    Guid? PatronSupportEventId)
{
    public static HeimdallPatronSupportIntakeResult Processed(string message, Guid patronSupportEventId) =>
        new(HeimdallPatronSupportIntakeStatus.Processed, message, patronSupportEventId);

    public static HeimdallPatronSupportIntakeResult BadRequest(string message) =>
        new(HeimdallPatronSupportIntakeStatus.BadRequest, message, null);

    public static HeimdallPatronSupportIntakeResult Unauthorized(string message) =>
        new(HeimdallPatronSupportIntakeStatus.Unauthorized, message, null);

    public static HeimdallPatronSupportIntakeResult NotConfigured(string message) =>
        new(HeimdallPatronSupportIntakeStatus.NotConfigured, message, null);
}

public enum HeimdallPatronSupportIntakeStatus
{
    Processed,
    BadRequest,
    Unauthorized,
    NotConfigured
}
