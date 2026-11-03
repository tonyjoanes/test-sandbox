namespace Orders.Domain;

public sealed record Quote(Money Subtotal, Money Discount, Money Shipping, Money Total, IReadOnlyList<string> AppliedRules);

/// <summary>
/// Turns an order into a quote. The rules, in order:
/// 1. Tier discount: Silver 5%, Gold 10% of the subtotal.
/// 2. Coupon: SAVE10 takes a flat 10 off (never below zero); WEEKEND15 takes 15% off on Saturday/Sunday only.
/// 3. Shipping: free from 50 after discounts, otherwise a flat 4.99.
/// </summary>
public sealed class PricingService(TimeProvider timeProvider)
{
    public const decimal FreeShippingThreshold = 50m;
    public const decimal FlatShipping = 4.99m;

    public Quote Price(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);

        var currency = order.Currency;
        var subtotal = order.Subtotal;
        var discount = Money.Zero(currency);
        var rules = new List<string>();

        var tierRate = TierDiscountRate(order.Tier);
        if (tierRate > 0)
        {
            discount += new Money(subtotal.Amount * tierRate, currency);
            rules.Add($"tier:{order.Tier.ToString().ToLowerInvariant()}");
        }

        if (order.CouponCode is { } coupon)
        {
            var afterTier = subtotal - discount;
            var couponDiscount = CouponDiscount(coupon, afterTier);
            if (couponDiscount.Amount > 0)
            {
                discount += couponDiscount;
                rules.Add($"coupon:{coupon}");
            }
        }

        var discounted = subtotal - discount;
        var shipping = order.Lines.Count == 0 || discounted.Amount >= FreeShippingThreshold
            ? Money.Zero(currency)
            : new Money(FlatShipping, currency);

        if (order.Lines.Count > 0 && shipping.Amount == 0)
        {
            rules.Add("shipping:free");
        }

        return new Quote(subtotal.Round(), discount.Round(), shipping, (discounted + shipping).Round(), rules);
    }

    public static decimal TierDiscountRate(CustomerTier tier) => tier switch
    {
        CustomerTier.Standard => 0m,
        CustomerTier.Silver => 0.05m,
        CustomerTier.Gold => 0.10m,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown customer tier."),
    };

    private Money CouponDiscount(string coupon, Money amount) => coupon switch
    {
        "SAVE10" => new Money(Math.Min(10m, amount.Amount), amount.Currency),
        "WEEKEND15" when IsWeekend() => new Money(amount.Amount * 0.15m, amount.Currency),
        _ => Money.Zero(amount.Currency),
    };

    private bool IsWeekend()
    {
        var day = timeProvider.GetUtcNow().DayOfWeek;
        return day is DayOfWeek.Saturday or DayOfWeek.Sunday;
    }
}
