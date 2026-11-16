using System.Collections.Concurrent;
using Orders.Domain;

namespace Orders.Api;

/// <summary>Demo storage. A real API would put a database behind <see cref="IOrderRepository"/>.</summary>
internal sealed class InMemoryOrderRepository : IOrderRepository
{
    private readonly ConcurrentDictionary<Guid, Order> _orders = new();

    public Task<Order?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(_orders.GetValueOrDefault(id));

    public Task<IReadOnlyList<Order>> ListByCustomerAsync(string customerId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Order>>(
            [.. _orders.Values.Where(o => o.CustomerId == customerId).OrderBy(o => o.CreatedAt)]);

    public Task SaveAsync(Order order, CancellationToken cancellationToken)
    {
        _orders[order.Id] = order;
        return Task.CompletedTask;
    }
}
