namespace eShop.PaymentProcessor.IntegrationEvents.EventHandling;

// UC5: When an order is cancelled by the shopper or back-office before capture,
// void the PayPal authorization so the shopper's held funds are released.
public class OrderStatusChangedToCancelledIntegrationEventHandler(
    IPaymentService paymentService,
    ILogger<OrderStatusChangedToCancelledIntegrationEventHandler> logger)
    : IIntegrationEventHandler<OrderStatusChangedToCancelledIntegrationEvent>
{
    public async Task Handle(OrderStatusChangedToCancelledIntegrationEvent @event)
    {
        logger.LogInformation("Handling integration event: {IntegrationEventId} - ({@IntegrationEvent})", @event.Id, @event);
        await paymentService.VoidAuthorizationAsync(@event.OrderId);
    }
}
