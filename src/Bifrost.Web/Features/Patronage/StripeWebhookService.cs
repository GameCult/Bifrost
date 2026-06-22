using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bifrost.Web.Configuration;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Features.Patronage;

public sealed class StripeWebhookService(
    BifrostDbContext dbContext,
    PatronageService patronageService,
    IOptions<StripeOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<StripeWebhookResult> ProcessAsync(
        string signatureHeader,
        string payload,
        CancellationToken cancellationToken)
    {
        var stripeOptions = options.Value;
        if (!stripeOptions.IsWebhookConfigured)
        {
            return StripeWebhookResult.NotConfigured("Stripe webhook processing is not configured.");
        }

        if (!HasValidSignature(signatureHeader, payload, stripeOptions.WebhookSecret))
        {
            return StripeWebhookResult.Unauthorized("Invalid Stripe webhook signature.");
        }

        StripeWebhookEvent? stripeEvent;
        try
        {
            stripeEvent = JsonSerializer.Deserialize<StripeWebhookEvent>(payload, JsonOptions);
        }
        catch (JsonException)
        {
            return StripeWebhookResult.BadRequest("Stripe webhook payload is not valid JSON.");
        }

        if (stripeEvent is null || string.IsNullOrWhiteSpace(stripeEvent.Id))
        {
            return StripeWebhookResult.BadRequest("Stripe webhook payload is missing an event id.");
        }

        if (!string.Equals(stripeEvent.Type, "checkout.session.completed", StringComparison.OrdinalIgnoreCase))
        {
            return StripeWebhookResult.Ignored($"Ignored Stripe event type '{stripeEvent.Type}'.");
        }

        var session = stripeEvent.Data.Object;
        if (!string.Equals(session.PaymentStatus, "paid", StringComparison.OrdinalIgnoreCase))
        {
            return StripeWebhookResult.Ignored($"Ignored Stripe checkout session with payment status '{session.PaymentStatus}'.");
        }

        if (session.AmountTotal is null or <= 0)
        {
            return StripeWebhookResult.BadRequest("Stripe checkout session is missing a positive amount_total.");
        }

        var patronageAccount = await ResolvePatronageAccountAsync(session, stripeOptions, cancellationToken);

        if (patronageAccount?.Membership is null || patronageAccount.Membership.Status == MembershipStatus.Suspended)
        {
            return StripeWebhookResult.NotConfigured(
                "Stripe checkout session is not linked to an unsuspended Bifrost patron account.");
        }

        var amount = session.AmountTotal.Value / 100m;
        var currencyCode = string.IsNullOrWhiteSpace(session.Currency)
            ? "USD"
            : session.Currency.ToUpperInvariant();

        await patronageService.RecordSupportEventAsync(
            actorUserAccountId: null,
            patronageAccount.Id,
            PatronSupportEventKind.OneTimeDonation,
            amount,
            currencyCode,
            session.Id,
            ExternalPatronProvider.Stripe,
            stripeEvent.Id,
            session.Customer,
            string.Empty,
            DateTimeOffset.FromUnixTimeSeconds(stripeEvent.Created),
            isCurrentRecurringSupport: false,
            BuildNotes(session),
            cancellationToken);

        return StripeWebhookResult.Processed("Stripe patronage support event recorded.");
    }

    private async Task<UserAccount?> ResolvePatronageAccountAsync(
        StripeCheckoutSession session,
        StripeOptions stripeOptions,
        CancellationToken cancellationToken)
    {
        if (session.Metadata.TryGetValue("bifrost_user_account_id", out var userAccountIdText) &&
            Guid.TryParse(userAccountIdText, out var userAccountId))
        {
            return await dbContext.UserAccounts
                .Include(x => x.Membership)
                .SingleOrDefaultAsync(x => x.Id == userAccountId, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(stripeOptions.GeneralPatronageGitHubLogin))
        {
            var normalizedLogin = stripeOptions.GeneralPatronageGitHubLogin.Trim().ToUpperInvariant();
            return await dbContext.UserAccounts
                .Include(x => x.Membership)
                .SingleOrDefaultAsync(x => x.NormalizedGitHubLogin == normalizedLogin, cancellationToken);
        }

        return null;
    }

    private static string BuildNotes(StripeCheckoutSession session)
    {
        session.Metadata.TryGetValue("tier", out var tier);
        session.Metadata.TryGetValue("source", out var source);
        return $"Verified Stripe checkout session for {source ?? "unknown source"} tier {tier ?? "unknown tier"}.";
    }

    private static bool HasValidSignature(string signatureHeader, string payload, string webhookSecret)
    {
        var pieces = signatureHeader
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(piece => piece.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0], parts => parts[1])
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        if (!pieces.TryGetValue("t", out var timestamps) ||
            !pieces.TryGetValue("v1", out var signatures) ||
            timestamps.Length == 0)
        {
            return false;
        }

        var signedPayload = $"{timestamps[0]}.{payload}";
        using var hasher = new HMACSHA256(Encoding.UTF8.GetBytes(webhookSecret));
        var expected = Convert.ToHexString(hasher.ComputeHash(Encoding.UTF8.GetBytes(signedPayload))).ToLowerInvariant();
        return signatures.Any(signature => FixedTimeEquals(signature, expected));
    }

    private static bool FixedTimeEquals(string actual, string expected)
    {
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return actualBytes.Length == expectedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }
}

public sealed record StripeWebhookResult(
    StripeWebhookStatus Status,
    string Message)
{
    public static StripeWebhookResult Processed(string message) => new(StripeWebhookStatus.Processed, message);

    public static StripeWebhookResult Ignored(string message) => new(StripeWebhookStatus.Ignored, message);

    public static StripeWebhookResult BadRequest(string message) => new(StripeWebhookStatus.BadRequest, message);

    public static StripeWebhookResult Unauthorized(string message) => new(StripeWebhookStatus.Unauthorized, message);

    public static StripeWebhookResult NotConfigured(string message) => new(StripeWebhookStatus.NotConfigured, message);
}

public enum StripeWebhookStatus
{
    Processed,
    Ignored,
    BadRequest,
    Unauthorized,
    NotConfigured
}

public sealed class StripeWebhookEvent
{
    public string Id { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public long Created { get; set; }

    public StripeWebhookData Data { get; set; } = new();
}

public sealed class StripeWebhookData
{
    public StripeCheckoutSession Object { get; set; } = new();
}

public sealed class StripeCheckoutSession
{
    public string Id { get; set; } = string.Empty;

    public string Customer { get; set; } = string.Empty;

    public string Currency { get; set; } = "usd";

    [JsonPropertyName("amount_total")]
    public long? AmountTotal { get; set; }

    [JsonPropertyName("payment_status")]
    public string PaymentStatus { get; set; } = string.Empty;

    public Dictionary<string, string> Metadata { get; set; } = [];
}
