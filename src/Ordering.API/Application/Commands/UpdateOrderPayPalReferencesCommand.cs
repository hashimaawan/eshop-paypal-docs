#nullable enable
namespace eShop.Ordering.API.Application.Commands;

// Records PayPal identifiers produced after the order was placed:
// - PayPalAuthorizationId is refreshed when an aged authorization is re-authorized (UC3).
// - PayPalCaptureId is the settlement reference recorded on a successful capture (UC2).
public record UpdateOrderPayPalReferencesCommand(
    int OrderNumber,
    string? PayPalAuthorizationId,
    string? PayPalCaptureId) : IRequest<bool>;
