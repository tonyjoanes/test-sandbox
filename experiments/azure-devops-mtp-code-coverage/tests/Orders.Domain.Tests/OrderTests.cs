namespace Orders.Domain.Tests;

[TestClass]
public sealed class OrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private static Order NewOrder(CustomerTier tier = CustomerTier.Standard) =>
        new(Guid.NewGuid(), "cust-1", tier, "GBP", Now);

    [TestMethod]
    public void NewOrder_IsDraftAndEmpty()
    {
        var order = NewOrder();

        Assert.AreEqual(OrderStatus.Draft, order.Status);
        Assert.IsEmpty(order.Lines);
        Assert.AreEqual(Money.Zero("GBP"), order.Subtotal);
        Assert.IsNull(order.CouponCode);
        Assert.AreEqual(Now, order.CreatedAt);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void Constructor_BlankCustomer_Throws(string customerId) =>
        Assert.ThrowsExactly<ArgumentException>(() => new Order(Guid.NewGuid(), customerId, CustomerTier.Standard, "GBP", Now));

    [TestMethod]
    public void Constructor_BlankCurrency_Throws() =>
        Assert.ThrowsExactly<ArgumentException>(() => new Order(Guid.NewGuid(), "cust-1", CustomerTier.Standard, " ", Now));

    [TestMethod]
    public void AddLine_SumsIntoSubtotal()
    {
        var order = NewOrder();
        order.AddLine(new OrderLine("A", 2, new Money(10m, "GBP")));
        order.AddLine(new OrderLine("B", 1, new Money(5.5m, "GBP")));

        Assert.HasCount(2, order.Lines);
        Assert.AreEqual(new Money(25.5m, "GBP"), order.Subtotal);
    }

    [TestMethod]
    public void AddLine_SameSku_MergesQuantityAtOriginalPrice()
    {
        var order = NewOrder();
        order.AddLine(new OrderLine("A", 2, new Money(10m, "GBP")));
        order.AddLine(new OrderLine("A", 3, new Money(99m, "GBP")));

        var line = Assert.ContainsSingle(order.Lines);
        Assert.AreEqual(5, line.Quantity);
        Assert.AreEqual(new Money(50m, "GBP"), line.Total);
    }

    [TestMethod]
    public void AddLine_WrongCurrency_Throws() =>
        Assert.ThrowsExactly<InvalidOperationException>(() => NewOrder().AddLine(new OrderLine("A", 1, new Money(1m, "USD"))));

    [TestMethod]
    public void AddLine_Null_Throws() =>
        Assert.ThrowsExactly<ArgumentNullException>(() => NewOrder().AddLine(null!));

    [TestMethod]
    public void ApplyCoupon_NormalisesCode()
    {
        var order = NewOrder();
        order.ApplyCoupon("  save10 ");
        Assert.AreEqual("SAVE10", order.CouponCode);
    }

    [TestMethod]
    public void ApplyCoupon_Blank_Throws() =>
        Assert.ThrowsExactly<ArgumentException>(() => NewOrder().ApplyCoupon(""));

    [TestMethod]
    public void Place_WithLines_MovesToPlaced()
    {
        var order = NewOrder();
        order.AddLine(new OrderLine("A", 1, new Money(1m, "GBP")));

        order.Place();

        Assert.AreEqual(OrderStatus.Placed, order.Status);
    }

    [TestMethod]
    public void Place_WithoutLines_Throws() =>
        Assert.ThrowsExactly<InvalidOperationException>(() => NewOrder().Place());

    [TestMethod]
    public void PlacedOrder_CannotChange()
    {
        var order = NewOrder();
        order.AddLine(new OrderLine("A", 1, new Money(1m, "GBP")));
        order.Place();

        Assert.ThrowsExactly<InvalidOperationException>(() => order.AddLine(new OrderLine("B", 1, new Money(1m, "GBP"))));
        Assert.ThrowsExactly<InvalidOperationException>(() => order.ApplyCoupon("SAVE10"));
        Assert.ThrowsExactly<InvalidOperationException>(order.Place);
    }

    [TestMethod]
    public void Cancel_FromDraftOrPlaced_Works()
    {
        var draft = NewOrder();
        draft.Cancel();
        Assert.AreEqual(OrderStatus.Cancelled, draft.Status);

        var placed = NewOrder();
        placed.AddLine(new OrderLine("A", 1, new Money(1m, "GBP")));
        placed.Place();
        placed.Cancel();
        Assert.AreEqual(OrderStatus.Cancelled, placed.Status);
    }

    [TestMethod]
    public void Cancel_Twice_Throws()
    {
        var order = NewOrder();
        order.Cancel();
        Assert.ThrowsExactly<InvalidOperationException>(order.Cancel);
    }
}
