var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.AddRabbitMqEventBus("EventBus")
    .AddSubscription<OrderStatusChangedToStockConfirmedIntegrationEvent, OrderStatusChangedToStockConfirmedIntegrationEventHandler>()
    .AddSubscription<OrderStockRejectedIntegrationEvent, OrderStockRejectedIntegrationEventHandler>()
    .AddSubscription<OrderStatusChangedToCancelledIntegrationEvent, OrderStatusChangedToCancelledIntegrationEventHandler>();

builder.Services.AddOptions<PaymentOptions>()
    .BindConfiguration(nameof(PaymentOptions));

// HTTP client used to query Ordering.API for order totals before invoking PayPal.
// Use service discovery so this works in containerized and cloud environments.
builder.Services.AddHttpClient<IOrderingApiClient, OrderingApiClient>(client =>
    {
        client.BaseAddress = new Uri("https+http://ordering-api");
    })
    .AddClientCredentialsToken("ServiceAuth");

// HTTP client used to talk to the external PayPal REST API
builder.Services.AddHttpClient("paypal");

builder.Services.AddScoped<IPaymentService, PayPalPaymentService>();

var app = builder.Build();

app.MapDefaultEndpoints();

await app.RunAsync();
