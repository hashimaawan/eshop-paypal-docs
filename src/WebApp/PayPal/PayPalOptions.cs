namespace eShop.WebApp.PayPal;

/// <summary>
/// Storefront-side PayPal configuration (UC1: create order + authorize). Bound from the "PayPal"
/// configuration section. Credentials come from user-secrets / environment, never from appsettings.
/// </summary>
public sealed class PayPalOptions
{
    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>"Sandbox" (default) or "Live"/"Production".</summary>
    public string Environment { get; set; } = "Sandbox";

    /// <summary>URL PayPal redirects the shopper back to after approval (the /paypal/return endpoint).</summary>
    public string? RedirectUri { get; set; }

    /// <summary>URL PayPal redirects the shopper to when they cancel approval (the /paypal/cancel endpoint).</summary>
    public string? CancelUrl { get; set; }

    /// <summary>Three-letter ISO-4217 currency code applied to every PayPal amount.</summary>
    public string CurrencyCode { get; set; } = "USD";

    /// <summary>Decimal places the configured currency uses (2 for USD/EUR, 0 for JPY).</summary>
    public int CurrencyDecimalPlaces { get; set; } = 2;

    /// <summary>
    /// When true, the storefront bypasses the real PayPal API and simulates the order + return flow
    /// for end-to-end (Playwright) tests. Never enabled in production.
    /// </summary>
    public bool E2ETestMode { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret) &&
        !string.IsNullOrWhiteSpace(RedirectUri) &&
        !string.IsNullOrWhiteSpace(CancelUrl);

    public bool IsProduction =>
        Environment.Equals("Live", StringComparison.OrdinalIgnoreCase) ||
        Environment.Equals("Production", StringComparison.OrdinalIgnoreCase);
}
