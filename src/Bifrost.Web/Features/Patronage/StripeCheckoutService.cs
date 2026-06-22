using System.Net.Http.Headers;
using System.Text.Json;
using Bifrost.Web.Configuration;
using Bifrost.Web.Domain;
using Microsoft.Extensions.Options;

namespace Bifrost.Web.Features.Patronage;

public sealed class StripeCheckoutService(
    HttpClient httpClient,
    IOptions<StripeOptions> options)
{
    private static readonly IReadOnlyDictionary<string, StripePatronageTier> VelvetTiers =
        new Dictionary<string, StripePatronageTier>(StringComparer.OrdinalIgnoreCase)
        {
            ["velvet-room"] = new(
                "velvet-room",
                "GameCult Patronage - Velvet Room",
                "General GameCult patronage with Deru supporter access.",
                1900,
                "usd"),
            ["mirror-test"] = new(
                "mirror-test",
                "GameCult Patronage - Mirror Test",
                "General GameCult patronage with Deru supporter access.",
                1200,
                "usd"),
            ["after-hours-bundle"] = new(
                "after-hours-bundle",
                "GameCult Patronage - After Hours Bundle",
                "General GameCult patronage with Deru supporter access.",
                3900,
                "usd")
        };

    public async Task<StripeCheckoutResult> CreateVelvetCheckoutAsync(
        string tierSlug,
        UserAccount patronAccount,
        CancellationToken cancellationToken)
    {
        var stripeOptions = options.Value;
        if (!stripeOptions.IsCheckoutConfigured)
        {
            return StripeCheckoutResult.NotConfigured("Stripe checkout is not configured.");
        }

        if (!VelvetTiers.TryGetValue(tierSlug.Trim(), out var tier))
        {
            return StripeCheckoutResult.UnknownTier($"Unknown Velvet patronage tier '{tierSlug}'.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/checkout/sessions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", stripeOptions.SecretKey);
        request.Content = new FormUrlEncodedContent(BuildCheckoutForm(stripeOptions, tier, patronAccount));

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

    private static IEnumerable<KeyValuePair<string, string>> BuildCheckoutForm(
        StripeOptions options,
        StripePatronageTier tier,
        UserAccount patronAccount)
    {
        var referenceId = $"velvet:{tier.Slug}:{patronAccount.Id:N}:{Guid.NewGuid():N}";
        var metadata = new Dictionary<string, string>
        {
            ["source"] = "velvet.gamecult.org",
            ["umbrella"] = "GameCult",
            ["ledger"] = "bifrost",
            ["purpose"] = "general_patronage",
            ["model"] = "deru",
            ["tier"] = tier.Slug,
            ["bifrost_user_account_id"] = patronAccount.Id.ToString("N"),
            ["bifrost_github_login"] = patronAccount.GitHubLogin
        };

        yield return new("mode", "payment");
        yield return new("success_url", options.SuccessUrl);
        yield return new("cancel_url", options.CancelUrl);
        yield return new("client_reference_id", referenceId);
        yield return new("line_items[0][quantity]", "1");
        yield return new("line_items[0][price_data][currency]", tier.Currency);
        yield return new("line_items[0][price_data][unit_amount]", tier.AmountInMinorUnits.ToString());
        yield return new("line_items[0][price_data][product_data][name]", tier.Name);
        yield return new("line_items[0][price_data][product_data][description]", tier.Description);

        foreach (var (key, value) in metadata)
        {
            yield return new($"metadata[{key}]", value);
            yield return new($"payment_intent_data[metadata][{key}]", value);
        }
    }
}

public sealed record StripePatronageTier(
    string Slug,
    string Name,
    string Description,
    int AmountInMinorUnits,
    string Currency);

public sealed record StripeCheckoutResult(
    StripeCheckoutStatus Status,
    Uri? CheckoutUrl,
    string Message)
{
    public static StripeCheckoutResult Created(Uri checkoutUrl) =>
        new(StripeCheckoutStatus.Created, checkoutUrl, "Stripe checkout session created.");

    public static StripeCheckoutResult NotConfigured(string message) =>
        new(StripeCheckoutStatus.NotConfigured, null, message);

    public static StripeCheckoutResult UnknownTier(string message) =>
        new(StripeCheckoutStatus.UnknownTier, null, message);

    public static StripeCheckoutResult ProviderError(string message) =>
        new(StripeCheckoutStatus.ProviderError, null, message);
}

public enum StripeCheckoutStatus
{
    Created,
    NotConfigured,
    UnknownTier,
    ProviderError
}
