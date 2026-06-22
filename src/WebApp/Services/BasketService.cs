using eShop.Basket.API.Grpc;
using GrpcBasketItem = eShop.Basket.API.Grpc.BasketItem;
using GrpcBasketClient = eShop.Basket.API.Grpc.Basket.BasketClient;
using Grpc.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

namespace eShop.WebApp.Services;

public class BasketService(GrpcBasketClient basketClient, IHttpContextAccessor httpContextAccessor)
{
    public async Task<IReadOnlyCollection<BasketQuantity>> GetBasketAsync()
    {
        var metadata = await CreateAuthMetadataAsync();
        var result = await basketClient.GetBasketAsync(new GetBasketRequest(), metadata);
        return MapToBasket(result);
    }

    public async Task DeleteBasketAsync()
    {
        var metadata = await CreateAuthMetadataAsync();
        await basketClient.DeleteBasketAsync(new DeleteBasketRequest(), metadata);
    }

    public async Task UpdateBasketAsync(IReadOnlyCollection<BasketQuantity> basket)
    {
        var updatePayload = new UpdateBasketRequest();

        foreach (var item in basket)
        {
            var updateItem = new GrpcBasketItem
            {
                ProductId = item.ProductId,
                Quantity = item.Quantity,
            };
            updatePayload.Items.Add(updateItem);
        }

        var metadata = await CreateAuthMetadataAsync();
        await basketClient.UpdateBasketAsync(updatePayload, metadata);
    }

    private async Task<Metadata> CreateAuthMetadataAsync()
    {
        var metadata = new Metadata();

        if (httpContextAccessor.HttpContext is { } context)
        {
            var accessToken = await context.GetTokenAsync("access_token");
            if (!string.IsNullOrEmpty(accessToken))
            {
                metadata.Add("Authorization", $"Bearer {accessToken}");
            }
        }

        return metadata;
    }

    private static List<BasketQuantity> MapToBasket(CustomerBasketResponse response)
    {
        var result = new List<BasketQuantity>();
        foreach (var item in response.Items)
        {
            result.Add(new BasketQuantity(item.ProductId, item.Quantity));
        }

        return result;
    }
}

public record BasketQuantity(int ProductId, int Quantity);
