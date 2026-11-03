using System.ComponentModel.DataAnnotations;
using Orders.Domain;

namespace Orders.Api;

// Request/response contracts. Validation attributes are enforced by the .NET 10 minimal API
// validation (builder.Services.AddValidation()) before an endpoint runs, returning a 400 ProblemDetails.

public sealed record CreateOrderRequest(
    [property: Required, StringLength(64, MinimumLength = 1)] string CustomerId,
    CustomerTier Tier,
    [property: Required, RegularExpression("^[A-Z]{3}$")] string Currency,
    [property: Required, MinLength(1)] IReadOnlyList<OrderLineRequest> Lines,
    string? CouponCode);

public sealed record OrderLineRequest(
    [property: Required, StringLength(32, MinimumLength = 1)] string Sku,
    [property: Range(1, 1000)] int Quantity,
    [property: Range(0.01, 100000)] decimal UnitPrice);

public sealed record OrderLineResponse(string Sku, int Quantity, decimal UnitPrice, decimal LineTotal);

public sealed record QuoteResponse(decimal Subtotal, decimal Discount, decimal Shipping, decimal Total, IReadOnlyList<string> AppliedRules);

public sealed record OrderResponse(
    Guid Id,
    string CustomerId,
    string Status,
    string Currency,
    string? CouponCode,
    DateTimeOffset CreatedAt,
    IReadOnlyList<OrderLineResponse> Lines,
    QuoteResponse Quote);

internal static class ContractMapping
{
    public static OrderResponse ToResponse(this Order order, Quote quote) => new(
        order.Id,
        order.CustomerId,
        order.Status.ToString(),
        order.Currency,
        order.CouponCode,
        order.CreatedAt,
        [.. order.Lines.Select(l => new OrderLineResponse(l.Sku, l.Quantity, l.UnitPrice.Amount, l.Total.Amount))],
        new QuoteResponse(quote.Subtotal.Amount, quote.Discount.Amount, quote.Shipping.Amount, quote.Total.Amount, quote.AppliedRules));
}
