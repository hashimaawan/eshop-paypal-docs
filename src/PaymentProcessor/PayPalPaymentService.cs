#nullable enable
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;

namespace eShop.PaymentProcessor;

public sealed class PayPalPaymentService(
    IOrderingApiClient orderingApiClient,
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<PaymentOptions> options,
    ILogger<PayPalPaymentService> logger) : IPaymentService
{
    // UC2 + UC3: Capture the held authorization when stock is confirmed.
    public async Task<bool> ProcessPaymentAsync(int orderId, CancellationToken cancellationToken = default)
    {
        var settings = options.CurrentValue;

        if (!settings.UsePayPal ||
            string.IsNullOrWhiteSpace(settings.PayPalClientId) ||
            string.IsNullOrWhiteSpace(settings.PayPalClientSecret))
        {
            logger.LogInformation("PayPal not configured; falling back to PaymentSucceeded flag for order {OrderId}", orderId);
            return settings.PaymentSucceeded;
        }

        var order = await orderingApiClient.GetOrderAsync(orderId, cancellationToken);
        if (order is null)
        {
            logger.LogWarning("Unable to load order {OrderId} from Ordering.API", orderId);
            return false;
        }

        if (string.IsNullOrWhiteSpace(order.PayPalAuthorizationId))
        {
            logger.LogWarning("Order {OrderId} has no PayPalAuthorizationId; falling back to PaymentSucceeded flag", orderId);
            return settings.PaymentSucceeded;
        }

        try
        {
            var client = CreatePayPalClient(settings);
            var accessToken = await GetAccessTokenAsync(client, settings, cancellationToken);
            if (string.IsNullOrEmpty(accessToken))
            {
                logger.LogWarning("Could not obtain PayPal access token for order {OrderId}", orderId);
                return false;
            }

            var authorizationId = order.PayPalAuthorizationId;

            // UC3: inspect authorization age and re-authorize if past the 3-day honor window.
            var authDetails = await GetAuthorizationAsync(client, accessToken, authorizationId, cancellationToken);
            if (authDetails is null)
            {
                logger.LogWarning("Could not retrieve PayPal authorization {AuthId} for order {OrderId}", authorizationId, orderId);
                return false;
            }

            if (string.Equals(authDetails.Status, "VOIDED", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(authDetails.Status, "CAPTURED", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Authorization {AuthId} for order {OrderId} is already {Status}", authorizationId, orderId, authDetails.Status);
                return false;
            }

            if (authDetails.CreateTime.HasValue)
            {
                var ageDays = (DateTime.UtcNow - authDetails.CreateTime.Value).TotalDays;

                if (ageDays >= 29)
                {
                    // Authorization expired — cannot capture or re-authorize.
                    logger.LogWarning("Authorization {AuthId} for order {OrderId} has expired ({AgeDays:F1} days old)", authorizationId, orderId, ageDays);
                    return false;
                }

                if (ageDays >= 3)
                {
                    // UC3: Past the 3-day honor window — re-authorize to get a fresh hold.
                    logger.LogInformation("Authorization {AuthId} is {AgeDays:F1} days old; re-authorizing for order {OrderId}", authorizationId, ageDays, orderId);
                    var reauthKey = $"reauth-{orderId}-{authorizationId}";
                    var newAuthId = await ReauthorizeAsync(client, accessToken, authorizationId, order.Total, settings.CurrencyCode, reauthKey, cancellationToken);
                    if (string.IsNullOrEmpty(newAuthId))
                    {
                        logger.LogWarning("Re-authorization failed for order {OrderId}", orderId);
                        return false;
                    }
                    authorizationId = newAuthId;
                    logger.LogInformation("Re-authorization succeeded. New authorization {AuthId} for order {OrderId}", authorizationId, orderId);

                    // UC3/§5: the refreshed authorization id must travel with the order so a
                    // later void (UC4/UC5) releases the live hold rather than the stale one.
                    await orderingApiClient.UpdatePayPalReferencesAsync(orderId, authorizationId, null, cancellationToken);
                }
            }

            // UC2: Capture the (possibly re-authorized) hold.
            var captureKey = $"capture-{orderId}-{authorizationId}";
            var captureId = await CaptureAuthorizationAsync(client, accessToken, authorizationId, captureKey, cancellationToken);
            if (string.IsNullOrEmpty(captureId))
            {
                logger.LogWarning("PayPal capture for order {OrderId} did not complete", orderId);
                return false;
            }

            logger.LogInformation("PayPal capture for order {OrderId} succeeded. CaptureId={CaptureId}", orderId, captureId);

            // §5: record the capture id as the settlement reference on the order.
            await orderingApiClient.UpdatePayPalReferencesAsync(orderId, null, captureId, cancellationToken);

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing PayPal payment for order {OrderId}", orderId);
            return false;
        }
    }

    // UC4 + UC5: Void the authorization when the order is cancelled before capture.
    public async Task VoidAuthorizationAsync(int orderId, CancellationToken cancellationToken = default)
    {
        var settings = options.CurrentValue;

        if (!settings.UsePayPal ||
            string.IsNullOrWhiteSpace(settings.PayPalClientId) ||
            string.IsNullOrWhiteSpace(settings.PayPalClientSecret))
        {
            return;
        }

        var order = await orderingApiClient.GetOrderAsync(orderId, cancellationToken);
        if (order is null || string.IsNullOrWhiteSpace(order.PayPalAuthorizationId))
        {
            logger.LogInformation("Order {OrderId} has no PayPalAuthorizationId; nothing to void", orderId);
            return;
        }

        try
        {
            var client = CreatePayPalClient(settings);
            var accessToken = await GetAccessTokenAsync(client, settings, cancellationToken);
            if (string.IsNullOrEmpty(accessToken))
            {
                logger.LogWarning("Could not obtain PayPal access token for void of order {OrderId}", orderId);
                return;
            }

            var voidKey = $"void-{orderId}-{order.PayPalAuthorizationId}";
            await VoidAuthorizationCoreAsync(client, accessToken, order.PayPalAuthorizationId, orderId, voidKey, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error voiding PayPal authorization for order {OrderId}", orderId);
        }
    }

    // ---- private helpers ----

    private HttpClient CreatePayPalClient(PaymentOptions settings)
    {
        var client = httpClientFactory.CreateClient("paypal");
        var baseUrl = settings.PayPalEnvironment?.Equals("Live", StringComparison.OrdinalIgnoreCase) == true
            ? "https://api-m.paypal.com"
            : "https://api-m.sandbox.paypal.com";
        client.BaseAddress ??= new Uri(baseUrl);
        return client;
    }

    private static async Task<string?> GetAccessTokenAsync(
        HttpClient client, PaymentOptions settings, CancellationToken ct)
    {
        var credentials = Convert.ToBase64String(
            Encoding.ASCII.GetBytes($"{settings.PayPalClientId}:{settings.PayPalClientSecret}"));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/oauth2/token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" });

        var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;

        var payload = await response.Content.ReadFromJsonAsync<PayPalTokenResponse>(cancellationToken: ct);
        return payload?.AccessToken;
    }

    // GET /v2/payments/authorizations/{id} — used to check age for UC3.
    private static async Task<PayPalAuthorizationDetails?> GetAuthorizationAsync(
        HttpClient client, string accessToken, string authorizationId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v2/payments/authorizations/{authorizationId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;

        return await response.Content.ReadFromJsonAsync<PayPalAuthorizationDetails>(cancellationToken: ct);
    }

    // POST /v2/payments/authorizations/{id}/reauthorize — UC3.
    private static async Task<string?> ReauthorizeAsync(
        HttpClient client, string accessToken, string authorizationId,
        decimal amount, string currencyCode, string requestId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v2/payments/authorizations/{authorizationId}/reauthorize");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        // §3.2 idempotency: a retry with the same key never places a second hold.
        request.Headers.Add("PayPal-Request-Id", requestId);
        request.Content = JsonContent.Create(new
        {
            amount = new
            {
                currency_code = currencyCode,
                value = amount.ToString("F2", CultureInfo.InvariantCulture)
            }
        });

        var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;

        var payload = await response.Content.ReadFromJsonAsync<PayPalReauthorizeResponse>(cancellationToken: ct);
        return payload?.Id;
    }

    // POST /v2/payments/authorizations/{id}/capture — UC2. Returns the capture id when COMPLETED.
    private static async Task<string?> CaptureAuthorizationAsync(
        HttpClient client, string accessToken, string authorizationId, string requestId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v2/payments/authorizations/{authorizationId}/capture");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        // §3.2 idempotency: a retry with the same key never captures twice.
        request.Headers.Add("PayPal-Request-Id", requestId);
        request.Content = JsonContent.Create(new { });

        var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;

        var payload = await response.Content.ReadFromJsonAsync<PayPalCaptureResponse>(cancellationToken: ct);
        return string.Equals(payload?.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase) ? payload?.Id : null;
    }

    // POST /v2/payments/authorizations/{id}/void — UC4/UC5.
    private async Task VoidAuthorizationCoreAsync(
        HttpClient client, string accessToken, string authorizationId, int orderId, string requestId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v2/payments/authorizations/{authorizationId}/void");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        // §3.2 idempotency: a retry with the same key never voids twice.
        request.Headers.Add("PayPal-Request-Id", requestId);

        var response = await client.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            // Already voided or captured — idempotent, nothing to do.
            logger.LogInformation("Authorization {AuthId} for order {OrderId} was already voided or captured", authorizationId, orderId);
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogWarning("PayPal void failed for order {OrderId}: {Status} {Body}", orderId, response.StatusCode, body);
            return;
        }

        logger.LogInformation("Voided PayPal authorization {AuthId} for order {OrderId}", authorizationId, orderId);
    }

    // ---- response DTOs ----

    private sealed class PayPalTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; init; } = string.Empty;
    }

    private sealed class PayPalAuthorizationDetails
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("create_time")]
        public DateTime? CreateTime { get; init; }

        [JsonPropertyName("expiration_time")]
        public DateTime? ExpirationTime { get; init; }
    }

    private sealed class PayPalReauthorizeResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }
    }

    private sealed class PayPalCaptureResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }
    }
}
