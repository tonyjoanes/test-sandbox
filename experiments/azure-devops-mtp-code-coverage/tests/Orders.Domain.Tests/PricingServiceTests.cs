using Microsoft.Extensions.Time.Testing;

namespace Orders.Domain.Tests;

[TestClass]
public sealed class PricingServiceTests
{
    // 2026-11-03 is a Tuesday, 2026-11-07 a Saturday.
    private static readonly DateTimeOffset Tuesday = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Saturday = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);

    private static PricingService Pricing(DateTimeOffset now) => new(new FakeTimeProvider(now));

    private static Order OrderWorth(decimal amount, CustomerTier tier = CustomerTier.Standard, string? coupon = null)
    {
        var order = new Order(Guid.NewGuid(), "cust-1", tier, "GBP", Tuesday);
        order.AddLine(new OrderLine("SKU", 1, new Money(amount, "GBP")));
        if (coupon is not null)
        {
            order.ApplyCoupon(coupon);
        }

        return order;
    }

    [TestMethod]
    public void Standard_BelowThreshold_PaysShipping()
    {
        var quote = Pricing(Tuesday).Price(OrderWorth(20m));

        Assert.AreEqual(20m, quote.Subtotal.Amount);
        Assert.AreEqual(0m, quote.Discount.Amount);
        Assert.AreEqual(PricingService.FlatShipping, quote.Shipping.Amount);
        Assert.AreEqual(24.99m, quote.Total.Amount);
        Assert.IsEmpty(quote.AppliedRules);
    }

    [TestMethod]
    public void Standard_AtThreshold_ShipsFree()
    {
        var quote = Pricing(Tuesday).Price(OrderWorth(50m));

        Assert.AreEqual(0m, quote.Shipping.Amount);
        Assert.AreEqual(50m, quote.Total.Amount);
        CollectionAssert.AreEqual(new[] { "shipping:free" }, quote.AppliedRules.ToArray());
    }

    [TestMethod]
    [DataRow(CustomerTier.Silver, 5.00, "tier:silver")]
    [DataRow(CustomerTier.Gold, 10.00, "tier:gold")]
    public void Tier_DiscountsSubtotal(CustomerTier tier, double expectedDiscount, string rule)
    {
        var quote = Pricing(Tuesday).Price(OrderWorth(100m, tier));

        Assert.AreEqual((decimal)expectedDiscount, quote.Discount.Amount);
        Assert.Contains(rule, quote.AppliedRules);
    }

    [TestMethod]
    public void Gold_DiscountCanDropOrderBelowFreeShipping()
    {
        // 54 - 10% = 48.60, under the 50 threshold.
        var quote = Pricing(Tuesday).Price(OrderWorth(54m, CustomerTier.Gold));

        Assert.AreEqual(PricingService.FlatShipping, quote.Shipping.Amount);
        Assert.AreEqual(53.59m, quote.Total.Amount);
    }

    [TestMethod]
    public void Save10_TakesFlatTenOff()
    {
        var quote = Pricing(Tuesday).Price(OrderWorth(80m, coupon: "save10"));

        Assert.AreEqual(10m, quote.Discount.Amount);
        Assert.AreEqual(70m, quote.Total.Amount);
        Assert.Contains("coupon:SAVE10", quote.AppliedRules);
    }

    [TestMethod]
    public void Save10_NeverDiscountsBelowZero()
    {
        var quote = Pricing(Tuesday).Price(OrderWorth(4m, coupon: "SAVE10"));

        Assert.AreEqual(4m, quote.Discount.Amount);
        Assert.AreEqual(PricingService.FlatShipping, quote.Total.Amount);
    }

    [TestMethod]
    public void Weekend15_AppliesOnSaturday()
    {
        var quote = Pricing(Saturday).Price(OrderWorth(100m, coupon: "WEEKEND15"));

        Assert.AreEqual(15m, quote.Discount.Amount);
        Assert.Contains("coupon:WEEKEND15", quote.AppliedRules);
    }

    [TestMethod]
    public void Weekend15_IgnoredOnWeekday()
    {
        var quote = Pricing(Tuesday).Price(OrderWorth(100m, coupon: "WEEKEND15"));

        Assert.AreEqual(0m, quote.Discount.Amount);
        Assert.DoesNotContain("coupon:WEEKEND15", quote.AppliedRules);
    }

    [TestMethod]
    public void UnknownCoupon_IsIgnored()
    {
        var quote = Pricing(Tuesday).Price(OrderWorth(100m, coupon: "BOGUS"));

        Assert.AreEqual(0m, quote.Discount.Amount);
    }

    [TestMethod]
    public void TierDiscountThenCoupon_StackInOrder()
    {
        // 100 - 5% = 95, then SAVE10 = 85.
        var quote = Pricing(Tuesday).Price(OrderWorth(100m, CustomerTier.Silver, "SAVE10"));

        Assert.AreEqual(15m, quote.Discount.Amount);
        Assert.AreEqual(85m, quote.Total.Amount);
        CollectionAssert.AreEqual(new[] { "tier:silver", "coupon:SAVE10", "shipping:free" }, quote.AppliedRules.ToArray());
    }

    [TestMethod]
    public void EmptyOrder_HasNoShipping()
    {
        var order = new Order(Guid.NewGuid(), "cust-1", CustomerTier.Standard, "GBP", Tuesday);

        var quote = Pricing(Tuesday).Price(order);

        Assert.AreEqual(0m, quote.Total.Amount);
        Assert.IsEmpty(quote.AppliedRules);
    }

    [TestMethod]
    public void Price_NullOrder_Throws() =>
        Assert.ThrowsExactly<ArgumentNullException>(() => Pricing(Tuesday).Price(null!));

    [TestMethod]
    public void TierDiscountRate_UnknownTier_Throws() =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PricingService.TierDiscountRate((CustomerTier)99));
}
