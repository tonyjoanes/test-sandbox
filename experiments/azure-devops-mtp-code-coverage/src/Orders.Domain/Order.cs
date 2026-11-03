namespace Orders.Domain;

public sealed record OrderLine(string Sku, int Quantity, Money UnitPrice)
{
    public Money Total => UnitPrice * Quantity;
}

public enum CustomerTier
{
    Standard,
    Silver,
    Gold,
}

public enum OrderStatus
{
    Draft,
    Placed,
    Cancelled,
}

public sealed class Order
{
    private readonly List<OrderLine> _lines = [];

    public Order(Guid id, string customerId, CustomerTier tier, string currency, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        Id = id;
        CustomerId = customerId;
        Tier = tier;
        Currency = currency;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public string CustomerId { get; }

    public CustomerTier Tier { get; }

    public string Currency { get; }

    public DateTimeOffset CreatedAt { get; }

    public OrderStatus Status { get; private set; } = OrderStatus.Draft;

    public string? CouponCode { get; private set; }

    public IReadOnlyList<OrderLine> Lines => _lines;

    public Money Subtotal => _lines.Aggregate(Money.Zero(Currency), (sum, line) => sum + line.Total);

    public void AddLine(OrderLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        EnsureDraft();

        if (!string.Equals(line.UnitPrice.Currency, Currency, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Line {line.Sku} is priced in {line.UnitPrice.Currency}, order is in {Currency}.");
        }

        var existing = _lines.FindIndex(l => l.Sku == line.Sku);
        if (existing >= 0)
        {
            // Same SKU twice merges into one line, at the price already on the order.
            var merged = _lines[existing] with { Quantity = _lines[existing].Quantity + line.Quantity };
            _lines[existing] = merged;
        }
        else
        {
            _lines.Add(line);
        }
    }

    public void ApplyCoupon(string couponCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(couponCode);
        EnsureDraft();
        CouponCode = couponCode.Trim().ToUpperInvariant();
    }

    public void Place()
    {
        EnsureDraft();
        if (_lines.Count == 0)
        {
            throw new InvalidOperationException("An order needs at least one line before it can be placed.");
        }

        Status = OrderStatus.Placed;
    }

    public void Cancel()
    {
        if (Status == OrderStatus.Cancelled)
        {
            throw new InvalidOperationException("The order is already cancelled.");
        }

        Status = OrderStatus.Cancelled;
    }

    private void EnsureDraft()
    {
        if (Status != OrderStatus.Draft)
        {
            throw new InvalidOperationException($"The order is {Status}; only draft orders can change.");
        }
    }
}
