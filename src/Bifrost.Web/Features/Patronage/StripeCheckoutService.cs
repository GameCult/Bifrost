using System.Net.Http.Headers;
using System.Text.Json;
using Bifrost.Web.Configuration;
using Bifrost.Web.Data;
using Bifrost.Web.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Features.Patronage;

public sealed class StripeCheckoutService(
    HttpClient httpClient,
    BifrostDbContext dbContext,
    IOptions<StripeOptions> options)
{
    private static readonly IReadOnlyDictionary<string, StripePatronageProjectPolicy> ProjectPolicies =
        new Dictionary<string, StripePatronageProjectPolicy>(StringComparer.OrdinalIgnoreCase)
        {
            ["velvet"] = new(
                "velvet",
                "Velvet",
                "velvet.gamecult.org",
                "deru",
                "usd",
                100,
                500_00)
        };

    public async Task<StripeCheckoutResult> CreateProjectDonationCheckoutAsync(
        StripeDonationCheckoutRequest checkoutRequest,
        UserAccount patronAccount,
        CancellationToken cancellationToken)
    {
        var stripeOptions = options.Value;
        if (!stripeOptions.IsCheckoutConfigured)
        {
            return StripeCheckoutResult.NotConfigured("Stripe checkout is not configured.");
        }

        var validation = await ValidateAsync(checkoutRequest, cancellationToken);
        if (validation.Status != StripeCheckoutStatus.Created)
        {
            return validation;
        }

        var donation = validation.Donation!;
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/checkout/sessions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", stripeOptions.SecretKey);
        request.Content = new FormUrlEncodedContent(BuildCheckoutForm(stripeOptions, donation, patronAccount));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return StripeCheckoutResult.ProviderError(
                $"Stripe checkout session creation failed with {(int)response.StatusCode}.");
        }

        using var json = JsonDocument.Parse(body);
        var url = json.RootElement.TryGetProperty("url", out var urlElement)
            ? urlElement.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(url))
        {
            return StripeCheckoutResult.ProviderError("Stripe checkout session response did not include a URL.");
        }

        return StripeCheckoutResult.Created(new Uri(url));
    }

    private async Task<StripeCheckoutResult> ValidateAsync(
        StripeDonationCheckoutRequest request,
        CancellationToken cancellationToken)
    {
        var projectSlug = request.ProjectSlug.Trim();
        if (string.IsNullOrWhiteSpace(projectSlug))
        {
            return StripeCheckoutResult.InvalidRequest("Missing required project path segment.");
        }

        if (!ProjectPolicies.TryGetValue(projectSlug, out var policy))
        {
            return StripeCheckoutResult.UnknownProject($"Unknown patronage project '{projectSlug}'.");
        }

        if (!string.Equals(request.QueryProjectSlug?.Trim(), projectSlug, StringComparison.OrdinalIgnoreCase))
        {
            return StripeCheckoutResult.InvalidRequest("Project query parameter must match the checkout project.");
        }

        var project = await dbContext.Projects
            .AsNoTracking()
            .Where(x => x.Slug == policy.ProjectSlug)
            .OrderBy(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (project is null || project.Status != ProjectStatus.Active)
        {
            return StripeCheckoutResult.UnknownProject($"Patronage project '{policy.ProjectSlug}' is not active in Bifrost.");
        }

        if (request.AmountInMinorUnits < policy.MinimumAmountInMinorUnits ||
            request.AmountInMinorUnits > policy.MaximumAmountInMinorUnits)
        {
            return StripeCheckoutResult.InvalidRequest(
                $"Donation amount must be between {policy.MinimumAmountInMinorUnits} and {policy.MaximumAmountInMinorUnits} {policy.Currency} minor units.");
        }

        var currency = string.IsNullOrWhiteSpace(request.Currency)
            ? policy.Currency
            : request.Currency.Trim().ToLowerInvariant();
        if (!string.Equals(currency, policy.Currency, StringComparison.OrdinalIgnoreCase))
        {
            return StripeCheckoutResult.InvalidRequest($"Project '{policy.ProjectSlug}' only accepts {policy.Currency.ToUpperInvariant()} donations.");
        }

        var itemSlug = request.ItemSlug.Trim();
        if (!IsValidSlugValue(itemSlug, maxLength: 120))
        {
            return StripeCheckoutResult.InvalidRequest("Item must be a non-empty slug of 120 characters or fewer.");
        }

        var source = string.IsNullOrWhiteSpace(request.Source)
            ? policy.Source
            : request.Source.Trim();
        if (!string.Equals(source, policy.Source, StringComparison.OrdinalIgnoreCase))
        {
            return StripeCheckoutResult.InvalidRequest($"Project '{policy.ProjectSlug}' checkout source must be '{policy.Source}'.");
        }

        return StripeCheckoutResult.Validated(new StripeDonationCheckout(
            policy,
            project.Id,
            itemSlug,
            request.AmountInMinorUnits,
            currency));
    }

    private static bool IsValidSlugValue(string value, int maxLength) =>
        value.Length is > 0 and <= 120 &&
        value.Length <= maxLength &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '_' or '.');

    private static IEnumerable<KeyValuePair<string, string>> BuildCheckoutForm(
        StripeOptions options,
        StripeDonationCheckout donation,
        UserAccount patronAccount)
    {
        var policy = donation.Policy;
        var referenceId = $"{policy.ProjectSlug}:{donation.ItemSlug}:{patronAccount.Id:N}:{Guid.NewGuid():N}";
        var metadata = new Dictionary<string, string>
        {
            ["source"] = policy.Source,
            ["umbrella"] = "GameCult",
            ["ledger"] = "bifrost",
            ["purpose"] = "general_patronage",
            ["project"] = policy.ProjectSlug,
            ["project_id"] = donation.ProjectId.ToString("N"),
            ["item"] = donation.ItemSlug,
            ["model"] = policy.Model,
            ["bifrost_user_account_id"] = patronAccount.Id.ToString("N"),
            ["bifrost_github_login"] = patronAccount.GitHubLogin
        };

        yield return new("mode", "payment");
        yield return new("success_url", options.SuccessUrl);
        yield return new("cancel_url", options.CancelUrl);
        yield return new("client_reference_id", referenceId);
        yield return new("line_items[0][quantity]", "1");
        yield return new("line_items[0][price_data][currency]", donation.Currency);
        yield return new("line_items[0][price_data][unit_amount]", donation.AmountInMinorUnits.ToString());
        yield return new("line_items[0][price_data][product_data][name]", $"{policy.DisplayName} patronage - {donation.ItemSlug}");
        yield return new("line_items[0][price_data][product_data][description]", $"General GameCult patronage attributed to {policy.DisplayName}.");

        foreach (var (key, value) in metadata)
        {
            yield return new($"metadata[{key}]", value);
            yield return new($"payment_intent_data[metadata][{key}]", value);
        }
    }
}

public sealed record StripeDonationCheckoutRequest(
    string ProjectSlug,
    string QueryProjectSlug,
    string ItemSlug,
    int AmountInMinorUnits,
    string Currency,
    string Source);

public sealed record StripePatronageProjectPolicy(
    string ProjectSlug,
    string DisplayName,
    string Source,
    string Model,
    string Currency,
    int MinimumAmountInMinorUnits,
    int MaximumAmountInMinorUnits);

public sealed record StripeDonationCheckout(
    StripePatronageProjectPolicy Policy,
    Guid ProjectId,
    string ItemSlug,
    int AmountInMinorUnits,
    string Currency);

public sealed record StripeCheckoutResult(
    StripeCheckoutStatus Status,
    Uri? CheckoutUrl,
    string Message,
    StripeDonationCheckout? Donation = null)
{
    public static StripeCheckoutResult Created(Uri checkoutUrl) =>
        new(StripeCheckoutStatus.Created, checkoutUrl, "Stripe checkout session created.");

    public static StripeCheckoutResult Validated(StripeDonationCheckout donation) =>
        new(StripeCheckoutStatus.Created, null, "Stripe checkout request validated.", donation);

    public static StripeCheckoutResult NotConfigured(string message) =>
        new(StripeCheckoutStatus.NotConfigured, null, message);

    public static StripeCheckoutResult InvalidRequest(string message) =>
        new(StripeCheckoutStatus.InvalidRequest, null, message);

    public static StripeCheckoutResult UnknownProject(string message) =>
        new(StripeCheckoutStatus.UnknownProject, null, message);

    public static StripeCheckoutResult ProviderError(string message) =>
        new(StripeCheckoutStatus.ProviderError, null, message);
}

public enum StripeCheckoutStatus
{
    Created,
    NotConfigured,
    InvalidRequest,
    UnknownProject,
    ProviderError
}
