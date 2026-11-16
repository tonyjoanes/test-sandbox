namespace Orders.Domain.Tests;

public sealed class MoneyTests
{
    [Fact]
    public void Add_SameCurrency_SumsAmounts() =>
        Assert.Equal(new Money(15m, "GBP"), new Money(10m, "GBP") + new Money(5m, "GBP"));

    [Fact]
    public void Subtract_SameCurrency_SubtractsAmounts() =>
        Assert.Equal(new Money(5m, "GBP"), new Money(10m, "GBP") - new Money(5m, "GBP"));

    [Fact]
    public void Multiply_ByQuantity_ScalesAmount() =>
        Assert.Equal(new Money(30m, "EUR"), new Money(10m, "EUR") * 3);

    [Fact]
    public void Add_DifferentCurrencies_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => _ = new Money(1m, "GBP") + new Money(1m, "EUR"));
        Assert.Contains("GBP", ex.Message);
    }

    [Theory]
    [InlineData(1.005, 1.00)]
    [InlineData(1.015, 1.02)]
    [InlineData(2.499, 2.50)]
    public void Round_UsesBankersRounding(double input, double expected) =>
        Assert.Equal((decimal)expected, new Money((decimal)input, "GBP").Round().Amount);

    [Fact]
    public void ToString_FormatsTwoDecimalsAndCurrency() =>
        Assert.Equal("4.50 GBP", new Money(4.5m, "GBP").ToString());
}
