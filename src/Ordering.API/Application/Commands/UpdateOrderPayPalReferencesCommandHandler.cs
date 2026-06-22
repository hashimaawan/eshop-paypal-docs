namespace eShop.Ordering.API.Application.Commands;

public class UpdateOrderPayPalReferencesCommandHandler : IRequestHandler<UpdateOrderPayPalReferencesCommand, bool>
{
    private readonly IOrderRepository _orderRepository;

    public UpdateOrderPayPalReferencesCommandHandler(IOrderRepository orderRepository)
    {
        _orderRepository = orderRepository;
    }

    public async Task<bool> Handle(UpdateOrderPayPalReferencesCommand command, CancellationToken cancellationToken)
    {
        var orderToUpdate = await _orderRepository.GetAsync(command.OrderNumber);
        if (orderToUpdate == null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(command.PayPalAuthorizationId))
        {
            orderToUpdate.SetPayPalAuthorizationId(command.PayPalAuthorizationId);
        }

        if (!string.IsNullOrWhiteSpace(command.PayPalCaptureId))
        {
            orderToUpdate.SetPayPalCaptureId(command.PayPalCaptureId);
        }

        return await _orderRepository.UnitOfWork.SaveEntitiesAsync(cancellationToken);
    }
}
