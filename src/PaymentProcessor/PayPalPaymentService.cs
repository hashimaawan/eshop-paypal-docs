using System.Globalization;
using PaypalServerSdk.Standard;
using PaypalServerSdk.Standard.Controllers;
using PaypalServerSdk.Standard.Exceptions;
using PaypalServerSdk.Standard.Http.Response;
using PaypalServerSdk.Standard.Models;

namespace eShop.PaymentProcessor;

/// <summary>
/// Captures, re-authorizes and voids PayPal authorizations through the PayPal Server SDK.
/// Replaces the previous hand-rolled HTTP implementation: every PayPal interaction now flows through
/// the SDK's <see cref="PaymentsController"/>. PayPal failures are always translated into a local
/// outcome (bool / no-op) and never surface SDK exception types to the integration-event handlers.
/// </summary>
public sealed class PayPalPaymentService(
    PaypalServerSdkClient payPalClient,
    IOrderingApiClient orderingApiClient,
    IOptionsMonitor<PaymentOptions> options,
    ILogger<PayPalPaymentService> logger) : IPaymentService
{
    private const string PreferRepresentation = "return=representation";

    // UC2 + UC3: capture the held authorization when stock is confirmed, re-authorizing first if the
    // 3-day honor window has elapsed. Returns true only when funds are actually captured.
    public async Task<bool> ProcessPaymentAsync(int orderId, CancellationToken cancellationToken = default)
    {
        var settings = options.CurrentValue;

        if (!settings.IsPayPalConfigured)
        {
            logger.LogInformation(
                "PayPal disabled or unconfigured; falling back to simulated PaymentSucceeded={PaymentSucceeded} for order {OrderId}",
                settings.PaymentSucceeded, orderId);
            return settings.PaymentSucceeded;
        }

        var order = await orderingApiClient.GetOrderAsync(orderId, cancellationToken);
        if (order is null)
        {
            logger.LogWarning("Unable to load order {OrderId} from Ordering.API; treating payment as failed", orderId);
            return false;
        }

        if (string.IsNullOrWhiteSpace(order.PayPalAuthorizationId))
        {
            logger.LogWarning(
                "Order {OrderId} has no PayPalAuthorizationId; falling back to simulated PaymentSucceeded flag", orderId);
            return settings.PaymentSucceeded;
        }

        // C5: bound the whole operation (including the resilience handler's retries+backoff) by a total
        // budget sourced from configuration, linked to the caller's token.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(settings.TotalTimeoutSeconds));
        var ct = cts.Token;

        var payments = payPalClient.PaymentsController;
        var authorizationId = order.PayPalAuthorizationId!;

        try
        {
            // UC3: inspect the held authorization and re-authorize if it is past the honor window.
            var authorization = await GetAuthorizationOrNullAsync(payments, authorizationId, orderId, ct);
            if (authorization is null)
            {
                return false;
            }

            if (authorization.Status is AuthorizationStatus.Voided or AuthorizationStatus.Captured or AuthorizationStatus.Denied)
            {
                logger.LogWarning(
                    "Authorization {AuthId} for order {OrderId} is already {Status}; cannot capture",
                    authorizationId, orderId, authorization.Status);
                return false;
            }

            if (TryGetAuthorizationAgeDays(authorization, out var ageDays))
            {
                if (ageDays >= settings.ValidityWindowDays)
                {
                    logger.LogWarning(
                        "Authorization {AuthId} for order {OrderId} has expired ({AgeDays:F1} days old); cannot capture or re-authorize",
                        authorizationId, orderId, ageDays);
                    return false;
                }

                if (ageDays >= settings.HonorWindowDays)
                {
                    logger.LogInformation(
                        "Authorization {AuthId} is {AgeDays:F1} days old (past the {HonorWindow}-day honor window); re-authorizing for order {OrderId}",
                        authorizationId, ageDays, settings.HonorWindowDays, orderId);

                    var newAuthorizationId = await ReauthorizeAsync(payments, authorizationId, order.Total, settings, orderId, ct);
                    if (string.IsNullOrEmpty(newAuthorizationId))
                    {
                        return false;
                    }

                    authorizationId = newAuthorizationId;
                    logger.LogInformation(
                        "Re-authorization succeeded. New authorization {AuthId} for order {OrderId}", authorizationId, orderId);

                    // §5: the refreshed authorization id must travel with the order so a later void
                    // releases the live hold rather than the stale one. Record-keeping uses the caller's
                    // token (not the time-boxed one) so the self-imposed budget can't drop the update.
                    await orderingApiClient.UpdatePayPalReferencesAsync(orderId, authorizationId, null, cancellationToken);
                }
            }

            // UC2: capture the (possibly re-authorized) hold.
            var captureId = await CaptureAsync(payments, authorizationId, order.Total, settings, orderId, ct);
            if (string.IsNullOrEmpty(captureId))
            {
                return false;
            }

            logger.LogInformation(
                "PayPal capture for order {OrderId} succeeded. AuthorizationId={AuthId} CaptureId={CaptureId} Amount={Amount} {Currency}",
                orderId, authorizationId, captureId, FormatAmount(order.Total, settings), settings.CurrencyCode);

            await orderingApiClient.UpdatePayPalReferencesAsync(orderId, null, captureId, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own total-budget timeout (or a transport timeout) — treat as a failed payment so it
            // routes to eShop's existing payment-failed -> cancelled path rather than escaping the handler.
            logger.LogWarning(
                "PayPal capture for order {OrderId} timed out within the {TotalTimeout}s budget; treating as failed",
                orderId, settings.TotalTimeoutSeconds);
            return false;
        }
        catch (OperationCanceledException)
        {
            // Caller-initiated cancellation (host shutdown): propagate so the work is not silently dropped.
            throw;
        }
    }

    // UC4 + UC5: void the authorization when the order is cancelled before capture. A void failure is
    // isolated — it never blocks eShop's cancellation; the discrepancy is logged and the un-captured
    // hold lapses on its own.
    public async Task VoidAuthorizationAsync(int orderId, CancellationToken cancellationToken = default)
    {
        var settings = options.CurrentValue;

        if (!settings.IsPayPalConfigured)
        {
            return;
        }

        var order = await orderingApiClient.GetOrderAsync(orderId, cancellationToken);
        if (order is null || string.IsNullOrWhiteSpace(order.PayPalAuthorizationId))
        {
            logger.LogInformation("Order {OrderId} has no PayPalAuthorizationId; nothing to void", orderId);
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(settings.TotalTimeoutSeconds));
        var ct = cts.Token;

        var authorizationId = order.PayPalAuthorizationId!;

        try
        {
            var input = new VoidPaymentInput
            {
                AuthorizationId = authorizationId,
                // §3.2 idempotency: a retry with the same key never voids twice.
                PaypalRequestId = $"void-{orderId}-{authorizationId}",
            };

            await payPalClient.PaymentsController.VoidPaymentAsync(input, ct);
            logger.LogInformation("Voided PayPal authorization {AuthId} for order {OrderId}", authorizationId, orderId);
        }
        catch (ApiException ex) when (ex.ResponseCode is 422 or 409 or 404)
        {
            // Already voided/captured, or no longer present — idempotent outcome, nothing to do.
            logger.LogInformation(
                "Void of authorization {AuthId} for order {OrderId} was a no-op ({Status}: {Reason})",
                authorizationId, orderId, ex.ResponseCode, DescribeError(ex));
        }
        catch (ApiException ex)
        {
            // Any other PayPal error must not block cancellation — log for reconciliation and move on.
            logger.LogWarning(
                "PayPal void failed for order {OrderId} (Status={Status}: {Reason}); order is still cancelled, hold will lapse",
                orderId, ex.ResponseCode, DescribeError(ex));
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("PayPal void for order {OrderId} was cancelled/timed out; order is still cancelled, hold will lapse", orderId);
        }
        catch (Exception ex)
        {
            // A void failure must never block eShop's cancellation.
            logger.LogWarning(ex, "Unexpected error voiding PayPal authorization for order {OrderId}; order is still cancelled, hold will lapse", orderId);
        }
    }

    // ---- SDK-wrapping helpers (all SDK exception types are caught here, never leaked) ----

    private async Task<PaymentAuthorization?> GetAuthorizationOrNullAsync(
        PaymentsController payments, string authorizationId, int orderId, CancellationToken ct)
    {
        try
        {
            var input = new GetAuthorizedPaymentInput { AuthorizationId = authorizationId };
            ApiResponse<PaymentAuthorization> response = await payments.GetAuthorizedPaymentAsync(input, ct);

            if (response?.Data is null)
            {
                logger.LogWarning("PayPal returned an empty authorization body for {AuthId} (order {OrderId})", authorizationId, orderId);
                return null;
            }

            return response.Data;
        }
        catch (ApiException ex)
        {
            logger.LogWarning(
                "Could not retrieve PayPal authorization {AuthId} for order {OrderId} (Status={Status}: {Reason})",
                authorizationId, orderId, ex.ResponseCode, DescribeError(ex));
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Any non-API failure (e.g. a malformed/unparseable body) is handled at the boundary
            // rather than crashing the integration-event handler.
            logger.LogWarning(ex, "Unexpected error retrieving PayPal authorization {AuthId} for order {OrderId}", authorizationId, orderId);
            return null;
        }
    }

    private async Task<string?> ReauthorizeAsync(
        PaymentsController payments, string authorizationId, decimal amount, PaymentOptions settings, int orderId, CancellationToken ct)
    {
        try
        {
            var input = new ReauthorizePaymentInput
            {
                AuthorizationId = authorizationId,
                Prefer = PreferRepresentation,
                // §3.2 idempotency: a retry with the same key never places a second hold.
                PaypalRequestId = $"reauth-{orderId}-{authorizationId}",
                Body = new ReauthorizeRequest
                {
                    Amount = new Money
                    {
                        CurrencyCode = settings.CurrencyCode,
                        MValue = FormatAmount(amount, settings),
                    },
                },
            };

            ApiResponse<PaymentAuthorization> response = await payments.ReauthorizePaymentAsync(input, ct);
            var data = response?.Data;

            if (data is null || string.IsNullOrWhiteSpace(data.Id) || data.Status == AuthorizationStatus.Denied)
            {
                logger.LogWarning(
                    "Re-authorization for order {OrderId} did not yield a usable authorization (Status={Status})",
                    orderId, data?.Status);
                return null;
            }

            return data.Id;
        }
        catch (ApiException ex)
        {
            logger.LogWarning(
                "PayPal re-authorization failed for order {OrderId} (Status={Status}: {Reason})",
                orderId, ex.ResponseCode, DescribeError(ex));
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Unexpected error re-authorizing PayPal payment for order {OrderId}", orderId);
            return null;
        }
    }

    private async Task<string?> CaptureAsync(
        PaymentsController payments, string authorizationId, decimal expectedAmount, PaymentOptions settings, int orderId, CancellationToken ct)
    {
        try
        {
            var input = new CaptureAuthorizedPaymentInput
            {
                AuthorizationId = authorizationId,
                Prefer = PreferRepresentation,
                // §3.2 idempotency: a retry with the same key never captures twice.
                PaypalRequestId = $"capture-{orderId}-{authorizationId}",
                // eShop captures the full authorized amount exactly once; no partial/repeat captures.
                Body = new CaptureRequest { FinalCapture = true },
            };

            ApiResponse<CapturedPayment> response = await payments.CaptureAuthorizedPaymentAsync(input, ct);
            var data = response?.Data;

            if (data is null)
            {
                logger.LogWarning("PayPal capture for order {OrderId} returned an empty body", orderId);
                return null;
            }

            if (data.Status != CaptureStatus.Completed)
            {
                logger.LogWarning(
                    "PayPal capture for order {OrderId} did not complete (Status={Status})", orderId, data.Status);
                return null;
            }

            // H3: validate the captured currency/amount the vendor reports against what we expect.
            ValidateCapturedAmount(data, expectedAmount, settings, orderId);

            return data.Id;
        }
        catch (ApiException ex)
        {
            // A decline or any business/technical failure is a normal "payment failed" outcome here,
            // routed to eShop's existing payment-failed -> cancelled path by the caller.
            logger.LogWarning(
                "PayPal capture failed for order {OrderId} (Status={Status}: {Reason})",
                orderId, ex.ResponseCode, DescribeError(ex));
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A malformed/unparseable capture body or any other unexpected failure becomes a failed
            // payment outcome (never an unhandled exception escaping into the saga).
            logger.LogWarning(ex, "Unexpected error capturing PayPal payment for order {OrderId}", orderId);
            return null;
        }
    }

    // ---- utility ----

    private void ValidateCapturedAmount(CapturedPayment capture, decimal expectedAmount, PaymentOptions settings, int orderId)
    {
        var amount = capture.Amount;
        if (amount is null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(amount.CurrencyCode) &&
            !amount.CurrencyCode.Equals(settings.CurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Captured currency {Captured} for order {OrderId} differs from configured {Expected}",
                amount.CurrencyCode, orderId, settings.CurrencyCode);
        }

        if (decimal.TryParse(amount.MValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var captured))
        {
            var tolerance = (decimal)Math.Pow(10, -settings.CurrencyDecimalPlaces);
            if (Math.Abs(captured - expectedAmount) > tolerance)
            {
                logger.LogWarning(
                    "Captured amount {Captured} for order {OrderId} differs from expected {Expected}",
                    captured, orderId, expectedAmount);
            }
        }
    }

    private static bool TryGetAuthorizationAgeDays(PaymentAuthorization authorization, out double ageDays)
    {
        ageDays = 0;
        if (string.IsNullOrWhiteSpace(authorization.CreateTime))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(
                authorization.CreateTime, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var createTime))
        {
            return false;
        }

        ageDays = (DateTimeOffset.UtcNow - createTime).TotalDays;
        return true;
    }

    // H3: format money exactly to the currency's decimal places, rounding away from zero, invariant culture.
    private static string FormatAmount(decimal amount, PaymentOptions settings)
    {
        var rounded = Math.Round(amount, settings.CurrencyDecimalPlaces, MidpointRounding.AwayFromZero);
        return rounded.ToString("F" + settings.CurrencyDecimalPlaces, CultureInfo.InvariantCulture);
    }

    // Builds a non-secret, structured description of a PayPal error for logging (no tokens/PII).
    private static string DescribeError(ApiException ex)
    {
        if (ex is ErrorException error)
        {
            var issue = error.Details is { Count: > 0 } ? error.Details[0].Issue : null;
            return $"{error.Name}/{issue} (debug_id={error.DebugId})";
        }

        return ex.Message;
    }
}
