namespace eShop.PaymentProcessor.IntegrationEvents.Events;

public record OrderStockRejectedIntegrationEvent(int OrderId) : IntegrationEvent;
