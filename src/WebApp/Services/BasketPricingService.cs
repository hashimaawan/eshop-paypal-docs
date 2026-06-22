using eShop.WebAppComponents.Catalog;
using eShop.WebAppComponents.Services;

namespace eShop.WebApp.Services;

/// <summary>
/// Computes basket totals without relying on Blazor's AuthenticationStateProvider,
/// so it can be safely used from minimal API endpoints (e.g., PayPal create-order).
/// </summary>
public sealed class BasketPricingService
{
    private readonly BasketService _basketService;
    private readonly CatalogService _catalogService;

    public BasketPricingService(BasketService basketService, CatalogService catalogService)
    {
        _basketService = basketService;
        _catalogService = catalogService;
    }

    public async Task<decimal> GetBasketTotalAsync(CancellationToken cancellationToken = default)
    {
        var quantities = await _basketService.GetBasketAsync();
        if (quantities.Count == 0)
        {
            return 0m;
        }

        var productIds = quantities.Select(q => q.ProductId);
        var catalogItems = (await _catalogService.GetCatalogItems(productIds)).ToDictionary(i => i.Id, i => i);

        decimal total = 0m;
        foreach (var q in quantities)
        {
            if (!catalogItems.TryGetValue(q.ProductId, out var catalogItem))
            {
                continue;
            }

            total += catalogItem.Price * q.Quantity;
        }

        return total;
    }
}



