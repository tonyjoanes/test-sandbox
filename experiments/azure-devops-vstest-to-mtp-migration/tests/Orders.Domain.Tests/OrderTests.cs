namespace Orders.Domain.Tests;

public sealed class OrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private static Order NewOrder() => new(Guid.NewGuid(), "cust-1", CustomerTier.Standard, "GBP", Now);

    [Fact]
    public void NewOrder_IsDraftAndEmpty()
    {
        var order = NewOrder();

        Assert.Equal(OrderStatus.Draft, order.Status);
        Assert.Empty(order.Lines);
        Assert.Equal(Money.Zero("GBP"), order.Subtotal);
        Assert.Null(order.CouponCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_BlankCustomer_Throws(string customerId) =>
        Assert.ThrowsAny<ArgumentException>(() => new Order(Guid.NewGuid(), customerId, CustomerTier.Standard, "GBP", Now));

    [Fact]
    public void AddLine_SumsIntoSubtotal()
    {
        var order = NewOrder();
        order.AddLine(new OrderLine("A", 2, new Money(10m, "GBP")));
        order.AddLine(new OrderLine("B", 1, new Money(5.5m, "GBP")));

        Assert.Equal(2, order.Lines.Count);
        Assert.Equal(new Money(25.5m, "GBP"), order.Subtotal);
    }

    [Fact]
    public void AddLine_SameSku_MergesQuantityAtOriginalPrice()
    {
        var order = NewOrder();
        order.AddLine(new OrderLine("A", 2, new Money(10m, "GBP")));
        order.AddLine(new OrderLine("A", 3, new Money(99m, "GBP")));

        var line = Assert.Single(order.Lines);
        Assert.Equal(5, line.Quantity);
        Assert.Equal(new Money(50m, "GBP"), line.Total);
    }

    [Fact]
    public void AddLine_WrongCurrency_Throws() =>
        Assert.Throws<InvalidOperationException>(() => NewOrder().AddLine(new OrderLine("A", 1, new Money(1m, "USD"))));

    [Fact]
    public void ApplyCoupon_NormalisesCode()
    {
        var order = NewOrder();
        order.ApplyCoupon("  save10 ");
        Assert.Equal("SAVE10", order.CouponCode);
    }

    [Fact]
    public void Place_WithLines_MovesToPlaced()
    {
        var order = NewOrder();
        order.AddLine(new OrderLine("A", 1, new Money(1m, "GBP")));
        order.Place();
        Assert.Equal(OrderStatus.Placed, order.Status);
    }

    [Fact]
    public void Place_WithoutLines_Throws() =>
        Assert.Throws<InvalidOperationException>(() => NewOrder().Place());

    [Fact]
    public void PlacedOrder_CannotChange()
    {
        var order = NewOrder();
        order.AddLine(new OrderLine("A", 1, new Money(1m, "GBP")));
        order.Place();

        Assert.Throws<InvalidOperationException>(() => order.AddLine(new OrderLine("B", 1, new Money(1m, "GBP"))));
        Assert.Throws<InvalidOperationException>(() => order.ApplyCoupon("SAVE10"));
    }

    [Fact]
    public void Cancel_Twice_Throws()
    {
        var order = NewOrder();
        order.Cancel();
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Throws<InvalidOperationException>(order.Cancel);
    }
}
