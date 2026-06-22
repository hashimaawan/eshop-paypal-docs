#nullable enable
using System.Net.Http.Json;

namespace eShop.PaymentProcessor;

public class OrderingApiClient : IOrderingApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OrderingApiClient> _logger;

    public OrderingApiClient(HttpClient httpClient, ILogger<OrderingApiClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<OrderDto?> GetOrderAsync(int orderId, CancellationToken cancellationToken = default)
    {
        try
        {
            // Ordering.API is versioned; specify api-version=1.0 explicitly.
            var requestUri = $"api/orders/{orderId}?api-version=1.0";
            return await _httpClient.GetFromJsonAsync<OrderDto>(requestUri, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving order {OrderId} from Ordering.API", orderId);
            return null;
        }
    }

    // Persists PayPal references back onto the order (UC2 capture id, UC3 refreshed authorization id).
    // Record-keeping only: a failure here must not fail the payment, so it never throws.
    public async Task UpdatePayPalReferencesAsync(
        int orderId,
        string? payPalAuthorizationId,
        string? payPalCaptureId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var requestUri = $"api/orders/{orderId}/paypal-references?api-version=1.0";
            var payload = new { payPalAuthorizationId, payPalCaptureId };
            var response = await _httpClient.PutAsJsonAsync(requestUri, payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Failed to update PayPal references for order {OrderId}: {Status}", orderId, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating PayPal references for order {OrderId}", orderId);
        }
    }
}

public sealed class OrderDto
{
    public int OrderNumber { get; set; }
    public decimal Total { get; set; }
    public string? PayPalOrderId { get; set; }
    public string? PayPalAuthorizationId { get; set; }
}


