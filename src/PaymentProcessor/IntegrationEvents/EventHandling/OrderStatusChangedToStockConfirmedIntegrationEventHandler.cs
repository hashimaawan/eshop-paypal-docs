namespace eShop.PaymentProcessor.IntegrationEvents.EventHandling;

public class OrderStatusChangedToStockConfirmedIntegrationEventHandler(
    IEventBus eventBus,
    IPaymentService paymentService,
    ILogger<OrderStatusChangedToStockConfirmedIntegrationEventHandler> logger) :
    IIntegrationEventHandler<OrderStatusChangedToStockConfirmedIntegrationEvent>
{
    public async Task Handle(OrderStatusChangedToStockConfirmedIntegrationEvent @event)
    {
        logger.LogInformation("Handling integration event: {IntegrationEventId} - ({@IntegrationEvent})", @event.Id, @event);

        // Business feature comment:
        // When OrderStatusChangedToStockConfirmed Integration Event is handled,
        // we perform the payment against the configured payment gateway (for example PayPal).
        // If the gateway reports success we publish an OrderPaymentSucceededIntegrationEvent;
        // otherwise we publish an OrderPaymentFailedIntegrationEvent.
        var paymentSucceeded = await paymentService.ProcessPaymentAsync(@event.OrderId);

        IntegrationEvent orderPaymentIntegrationEvent = paymentSucceeded
            ? new OrderPaymentSucceededIntegrationEvent(@event.OrderId)
            : new OrderPaymentFailedIntegrationEvent(@event.OrderId);

        logger.LogInformation("Publishing integration event: {IntegrationEventId} - ({@IntegrationEvent})", orderPaymentIntegrationEvent.Id, orderPaymentIntegrationEvent);

        await eventBus.PublishAsync(orderPaymentIntegrationEvent);
    }
}
