namespace Orders.Domain.Tests;

[TestClass]
public sealed class MoneyTests
{
    [TestMethod]
    public void Add_SameCurrency_SumsAmounts() =>
        Assert.AreEqual(new Money(15m, "GBP"), new Money(10m, "GBP") + new Money(5m, "GBP"));

    [TestMethod]
    public void Subtract_SameCurrency_SubtractsAmounts() =>
        Assert.AreEqual(new Money(5m, "GBP"), new Money(10m, "GBP") - new Money(5m, "GBP"));

    [TestMethod]
    public void Multiply_ByQuantity_ScalesAmount() =>
        Assert.AreEqual(new Money(30m, "EUR"), new Money(10m, "EUR") * 3);

    [TestMethod]
    public void Add_DifferentCurrencies_Throws()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => _ = new Money(1m, "GBP") + new Money(1m, "EUR"));
        Assert.Contains("GBP", ex.Message);
    }

    [TestMethod]
    [DataRow(1.005, 1.00)]
    [DataRow(1.015, 1.02)]
    [DataRow(2.499, 2.50)]
    public void Round_UsesBankersRounding(double input, double expected) =>
        Assert.AreEqual((decimal)expected, new Money((decimal)input, "GBP").Round().Amount);

    [TestMethod]
    public void ToString_FormatsTwoDecimalsAndCurrency() =>
        Assert.AreEqual("4.50 GBP", new Money(4.5m, "GBP").ToString());
}
