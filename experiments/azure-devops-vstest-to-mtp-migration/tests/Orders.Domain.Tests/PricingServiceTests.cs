using Microsoft.Extensions.Time.Testing;

namespace Orders.Domain.Tests;

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

    [Fact]
    public void Standard_BelowThreshold_PaysShipping()
    {
        var quote = Pricing(Tuesday).Price(OrderWorth(20m));

        Assert.Equal(PricingService.FlatShipping, quote.Shipping.Amount);
        Assert.Equal(24.99m, quote.Total.Amount);
        Assert.Empty(quote.AppliedRules);
    }

    [Fact]
    public void Standard_AtThreshold_ShipsFree()
    {
        var quote = Pricing(Tuesday).Price(OrderWorth(50m));

        Assert.Equal(0m, quote.Shipping.Amount);
        Assert.Equal(["shipping:free"], quote.AppliedRules);
    }

    [Theory]
    [InlineData(CustomerTier.Silver, 5.00, "tier:silver")]
    [InlineData(CustomerTier.Gold, 10.00, "tier:gold")]
    public void Tier_DiscountsSubtotal(CustomerTier tier, double expectedDiscount, string rule)
    {
        var quote = Pricing(Tuesday).Price(OrderWorth(100m, tier));

        Assert.Equal((decimal)expectedDiscount, quote.Discount.Amount);
        Assert.Contains(rule, quote.AppliedRules);
    }

    [Fact]
    public void Save10_NeverDiscountsBelowZero()
    {
        var quote = Pricing(Tuesday).Price(OrderWorth(4m, coupon: "SAVE10"));

        Assert.Equal(4m, quote.Discount.Amount);
        Assert.Equal(PricingService.FlatShipping, quote.Total.Amount);
    }

    [Fact]
    public void Weekend15_AppliesOnSaturdayOnly()
    {
        Assert.Equal(15m, Pricing(Saturday).Price(OrderWorth(100m, coupon: "WEEKEND15")).Discount.Amount);
        Assert.Equal(0m, Pricing(Tuesday).Price(OrderWorth(100m, coupon: "WEEKEND15")).Discount.Amount);
    }

    [Fact]
    public void TierDiscountThenCoupon_StackInOrder()
    {
        var quote = Pricing(Tuesday).Price(OrderWorth(100m, CustomerTier.Silver, "SAVE10"));

        Assert.Equal(85m, quote.Total.Amount);
        Assert.Equal(["tier:silver", "coupon:SAVE10", "shipping:free"], quote.AppliedRules);
    }

    [Fact]
    public void EmptyOrder_HasNoShipping()
    {
        var order = new Order(Guid.NewGuid(), "cust-1", CustomerTier.Standard, "GBP", Tuesday);
        Assert.Equal(0m, Pricing(Tuesday).Price(order).Total.Amount);
    }

    [Fact]
    public void TierDiscountRate_UnknownTier_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PricingService.TierDiscountRate((CustomerTier)99));
}
