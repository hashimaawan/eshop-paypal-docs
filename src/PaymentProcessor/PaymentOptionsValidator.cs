using System.ComponentModel.DataAnnotations;

namespace eShop.PaymentProcessor;

/// <summary>
/// Validates <see cref="PaymentOptions"/> at startup. When PayPal is enabled the pod must fail to
/// start — naming the offending key — rather than failing on the first order. When PayPal is disabled
/// the service runs in simulation mode and credentials are not required.
/// </summary>
public sealed class PaymentOptionsValidator : IValidateOptions<PaymentOptions>
{
    public ValidateOptionsResult Validate(string? name, PaymentOptions options)
    {
        var failures = new List<string>();

        // Data-annotation checks (currency length, ranges) always apply.
        var annotationResults = new List<ValidationResult>();
        if (!Validator.TryValidateObject(options, new ValidationContext(options), annotationResults, validateAllProperties: true))
        {
            failures.AddRange(annotationResults.Select(r => r.ErrorMessage ?? "Invalid PaymentOptions value."));
        }

        if (options.PerAttemptTimeoutSeconds > options.TotalTimeoutSeconds)
        {
            failures.Add(
                $"{nameof(PaymentOptions.PerAttemptTimeoutSeconds)} ({options.PerAttemptTimeoutSeconds}s) must not exceed " +
                $"{nameof(PaymentOptions.TotalTimeoutSeconds)} ({options.TotalTimeoutSeconds}s).");
        }

        // Credential/environment shape is only mandatory when PayPal is enabled.
        if (options.UsePayPal)
        {
            if (string.IsNullOrWhiteSpace(options.PayPalClientId))
            {
                failures.Add($"{nameof(PaymentOptions.PayPalClientId)} is required when {nameof(PaymentOptions.UsePayPal)} is true. Set it via user-secrets or environment configuration.");
            }

            if (string.IsNullOrWhiteSpace(options.PayPalClientSecret))
            {
                failures.Add($"{nameof(PaymentOptions.PayPalClientSecret)} is required when {nameof(PaymentOptions.UsePayPal)} is true. Set it via user-secrets or environment configuration.");
            }

            var env = options.PayPalEnvironment;
            var validEnv =
                env.Equals("Sandbox", StringComparison.OrdinalIgnoreCase) ||
                env.Equals("Live", StringComparison.OrdinalIgnoreCase) ||
                env.Equals("Production", StringComparison.OrdinalIgnoreCase);
            if (!validEnv)
            {
                failures.Add($"{nameof(PaymentOptions.PayPalEnvironment)} must be 'Sandbox', 'Live', or 'Production' but was '{env}'.");
            }
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
