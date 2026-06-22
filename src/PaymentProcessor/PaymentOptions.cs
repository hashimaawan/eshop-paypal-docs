using System.ComponentModel.DataAnnotations;

namespace eShop.PaymentProcessor;

public class PaymentOptions
{
    // Legacy flag used to simulate payment success/failure when PayPal is disabled.
    // Acts as the kill switch: when UsePayPal is false the integration falls back to this flag
    // and never contacts PayPal (lets the rest of eShop run without sandbox credentials).
    public bool PaymentSucceeded { get; set; } = true;

    // When true (and credentials are configured) real payments are executed against PayPal via the SDK.
    public bool UsePayPal { get; set; }

    public string? PayPalClientId { get; set; }

    public string? PayPalClientSecret { get; set; }

    /// <summary>
    /// "Sandbox" (default) or "Live"/"Production". Drives the PayPal Server SDK environment.
    /// </summary>
    public string PayPalEnvironment { get; set; } = "Sandbox";

    /// <summary>
    /// Three-letter ISO-4217 currency code (for example, "USD" or "EUR").
    /// </summary>
    [StringLength(3, MinimumLength = 3)]
    public string CurrencyCode { get; set; } = "USD";

    /// <summary>
    /// Number of decimal places the configured currency uses (2 for USD/EUR, 0 for JPY, 3 for some).
    /// PayPal amounts are formatted to this precision.
    /// </summary>
    [Range(0, 4)]
    public int CurrencyDecimalPlaces { get; set; } = 2;

    /// <summary>
    /// PayPal honors an authorization for this many days before it must be re-authorized (UC3).
    /// </summary>
    [Range(1, 29)]
    public int HonorWindowDays { get; set; } = 3;

    /// <summary>
    /// A PayPal authorization remains valid (re-authorizable) for this many days; past it, funds
    /// can no longer be secured and capture fails (UC3).
    /// </summary>
    [Range(1, 60)]
    public int ValidityWindowDays { get; set; } = 29;

    /// <summary>
    /// Per-attempt timeout for a single PayPal HTTP call (applied via the standard resilience handler).
    /// </summary>
    [Range(1, 120)]
    public int PerAttemptTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Total time budget for a PayPal operation across all retries and backoff. Must stay below the
    /// deadline of whatever waits on it (the integration-event handler). Enforced at the call site.
    /// </summary>
    [Range(1, 300)]
    public int TotalTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// True when PayPal is enabled and both credentials are present.
    /// </summary>
    public bool IsPayPalConfigured =>
        UsePayPal &&
        !string.IsNullOrWhiteSpace(PayPalClientId) &&
        !string.IsNullOrWhiteSpace(PayPalClientSecret);

    /// <summary>
    /// True when the configured environment targets PayPal production (live billing).
    /// </summary>
    public bool IsProduction =>
        PayPalEnvironment.Equals("Live", StringComparison.OrdinalIgnoreCase) ||
        PayPalEnvironment.Equals("Production", StringComparison.OrdinalIgnoreCase);
}
