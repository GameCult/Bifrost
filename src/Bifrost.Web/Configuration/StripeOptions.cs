namespace Bifrost.Web.Configuration;

public sealed class StripeOptions
{
    public const string SectionName = "Stripe";

    public bool EnableCheckout { get; set; }

    public string SecretKey { get; set; } = string.Empty;

    public string WebhookSecret { get; set; } = string.Empty;

    public string GeneralPatronageGitHubLogin { get; set; } = string.Empty;

    public string SuccessUrl { get; set; } = "https://velvet.gamecult.org/?patronage=success";

    public string CancelUrl { get; set; } = "https://velvet.gamecult.org/?patronage=cancelled";

    public bool IsCheckoutConfigured =>
        EnableCheckout &&
        !string.IsNullOrWhiteSpace(SecretKey) &&
        !string.IsNullOrWhiteSpace(SuccessUrl) &&
        !string.IsNullOrWhiteSpace(CancelUrl);

    public bool IsWebhookConfigured =>
        EnableCheckout &&
        !string.IsNullOrWhiteSpace(WebhookSecret);
}
