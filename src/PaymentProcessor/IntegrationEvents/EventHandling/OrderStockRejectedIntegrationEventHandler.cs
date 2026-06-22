namespace eShop.PaymentProcessor.IntegrationEvents.EventHandling;

// UC4: When stock is rejected the order is cancelled before any capture occurs.
// Void the PayPal authorization so the shopper's reserved funds are released.
public class OrderStockRejectedIntegrationEventHandler(
    IPaymentService paymentService,
    ILogger<OrderStockRejectedIntegrationEventHandler> logger)
    : IIntegrationEventHandler<OrderStockRejectedIntegrationEvent>
{
    public async Task Handle(OrderStockRejectedIntegrationEvent @event)
    {
        logger.LogInformation("Handling integration event: {IntegrationEventId} - ({@IntegrationEvent})", @event.Id, @event);
        await paymentService.VoidAuthorizationAsync(@event.OrderId);
    }
}
