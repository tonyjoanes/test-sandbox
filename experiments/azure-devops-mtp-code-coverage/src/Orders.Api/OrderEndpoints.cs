using Microsoft.AspNetCore.Http.HttpResults;
using Orders.Domain;

namespace Orders.Api;

internal static class OrderEndpoints
{
    public static RouteGroupBuilder MapOrderEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/orders").WithTags("Orders");

        group.MapPost("/", CreateOrder).WithName("CreateOrder");
        group.MapGet("/{id:guid}", GetOrder).WithName("GetOrder");
        group.MapGet("/", ListOrders).WithName("ListOrders");
        group.MapPost("/{id:guid}/place", PlaceOrder).WithName("PlaceOrder");
        group.MapPost("/{id:guid}/cancel", CancelOrder).WithName("CancelOrder");

        return group;
    }

    internal static async Task<Created<OrderResponse>> CreateOrder(
        CreateOrderRequest request,
        IOrderRepository repository,
        PricingService pricing,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var order = new Order(Guid.CreateVersion7(), request.CustomerId, request.Tier, request.Currency, timeProvider.GetUtcNow());
        foreach (var line in request.Lines)
        {
            order.AddLine(new OrderLine(line.Sku, line.Quantity, new Money(line.UnitPrice, request.Currency)));
        }

        if (!string.IsNullOrWhiteSpace(request.CouponCode))
        {
            order.ApplyCoupon(request.CouponCode);
        }

        await repository.SaveAsync(order, cancellationToken);
        return TypedResults.Created($"/orders/{order.Id}", order.ToResponse(pricing.Price(order)));
    }

    internal static async Task<Results<Ok<OrderResponse>, NotFound>> GetOrder(
        Guid id,
        IOrderRepository repository,
        PricingService pricing,
        CancellationToken cancellationToken)
    {
        var order = await repository.FindAsync(id, cancellationToken);
        return order is null ? TypedResults.NotFound() : TypedResults.Ok(order.ToResponse(pricing.Price(order)));
    }

    internal static async Task<Results<Ok<IReadOnlyList<OrderResponse>>, ValidationProblem>> ListOrders(
        string? customerId,
        IOrderRepository repository,
        PricingService pricing,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["customerId"] = ["The customerId query parameter is required."],
            });
        }

        var orders = await repository.ListByCustomerAsync(customerId, cancellationToken);
        IReadOnlyList<OrderResponse> response = [.. orders.Select(o => o.ToResponse(pricing.Price(o)))];
        return TypedResults.Ok(response);
    }

    internal static Task<Results<Ok<OrderResponse>, NotFound, Conflict<string>>> PlaceOrder(
        Guid id,
        IOrderRepository repository,
        PricingService pricing,
        CancellationToken cancellationToken) =>
        Transition(id, repository, pricing, order => order.Place(), cancellationToken);

    internal static Task<Results<Ok<OrderResponse>, NotFound, Conflict<string>>> CancelOrder(
        Guid id,
        IOrderRepository repository,
        PricingService pricing,
        CancellationToken cancellationToken) =>
        Transition(id, repository, pricing, order => order.Cancel(), cancellationToken);

    private static async Task<Results<Ok<OrderResponse>, NotFound, Conflict<string>>> Transition(
        Guid id,
        IOrderRepository repository,
        PricingService pricing,
        Action<Order> transition,
        CancellationToken cancellationToken)
    {
        var order = await repository.FindAsync(id, cancellationToken);
        if (order is null)
        {
            return TypedResults.NotFound();
        }

        try
        {
            transition(order);
        }
        catch (InvalidOperationException ex)
        {
            return TypedResults.Conflict(ex.Message);
        }

        await repository.SaveAsync(order, cancellationToken);
        return TypedResults.Ok(order.ToResponse(pricing.Price(order)));
    }
}
