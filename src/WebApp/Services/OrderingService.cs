namespace eShop.WebApp.Services;

public class OrderingService(HttpClient httpClient)
{
    private readonly string remoteServiceBaseUrl = "/api/Orders/";

    public Task<OrderRecord[]> GetOrders()
    {
        return httpClient.GetFromJsonAsync<OrderRecord[]>(remoteServiceBaseUrl)!;
    }

    public Task CreateOrder(CreateOrderRequest request, Guid requestId)
    {
        var requestMessage = new HttpRequestMessage(HttpMethod.Post, remoteServiceBaseUrl);
        requestMessage.Headers.Add("x-requestid", requestId.ToString());
        requestMessage.Content = JsonContent.Create(request);
        return httpClient.SendAsync(requestMessage);
    }

    // UC5: cancel an order before it is paid. Triggers the existing cancellation
    // path in Ordering.API, which (via PaymentProcessor) voids the PayPal hold.
    public async Task<bool> CancelOrder(int orderNumber, Guid requestId)
    {
        var requestMessage = new HttpRequestMessage(HttpMethod.Put, $"{remoteServiceBaseUrl}cancel");
        requestMessage.Headers.Add("x-requestid", requestId.ToString());
        requestMessage.Content = JsonContent.Create(new { OrderNumber = orderNumber });
        var response = await httpClient.SendAsync(requestMessage);
        return response.IsSuccessStatusCode;
    }
}

public record OrderRecord(
    int OrderNumber,
    DateTime Date,
    string Status,
    decimal Total);
