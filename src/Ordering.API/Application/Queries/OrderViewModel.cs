#nullable enable
namespace eShop.Ordering.API.Application.Queries;

public record Orderitem
{
    public  string ProductName { get; init; } = string.Empty;
    public int Units { get; init; }
    public double UnitPrice { get; init; }
    public  string PictureUrl { get; init; } = string.Empty;
}

public record Order
{
    public int OrderNumber { get; init; }
    public DateTime Date { get; init; }
    public string Status { get; init; } = string.Empty;
    public  string Description { get; init; } = string.Empty;
    public  string Street { get; init; } = string.Empty;
    public  string City { get; init; } = string.Empty;
    public  string State { get; init; } = string.Empty;
    public  string Zipcode { get; init; } = string.Empty;
    public  string Country { get; init; } = string.Empty;
    public List<Orderitem> OrderItems { get; set; } = new List<Orderitem>();
    public decimal Total { get; set; }
    public string? PayPalOrderId { get; init; }
    public string? PayPalAuthorizationId { get; init; }
}

public record OrderSummary
{
    public int OrderNumber { get; init; }
    public DateTime Date { get; init; }
    public  string Status { get; init; } = string.Empty;
    public double Total { get; init; }
}

public record CardType
{
    public int Id { get; init; }
    public  string Name { get; init; } = string.Empty;
}
