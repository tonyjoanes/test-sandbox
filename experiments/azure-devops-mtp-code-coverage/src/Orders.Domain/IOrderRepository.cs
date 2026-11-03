namespace Orders.Domain;

public interface IOrderRepository
{
    Task<Order?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Order>> ListByCustomerAsync(string customerId, CancellationToken cancellationToken);

    Task SaveAsync(Order order, CancellationToken cancellationToken);
}
