namespace eShop.PaymentProcessor.IntegrationEvents.Events;

public record OrderStatusChangedToCancelledIntegrationEvent(
    int OrderId,
    string OrderStatus,
    string BuyerName,
    string BuyerIdentityGuid) : IntegrationEvent;
