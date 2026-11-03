namespace Orders.Domain;

/// <summary>An amount in a single currency. Arithmetic across currencies is a bug, so it throws.</summary>
public readonly record struct Money(decimal Amount, string Currency)
{
    public static Money Zero(string currency) => new(0m, currency);

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return left with { Amount = left.Amount + right.Amount };
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return left with { Amount = left.Amount - right.Amount };
    }

    public static Money operator *(Money money, int quantity) => money with { Amount = money.Amount * quantity };

    /// <summary>Rounds to 2 decimal places using banker's rounding, the .NET default for money.</summary>
    public Money Round() => this with { Amount = Math.Round(Amount, 2, MidpointRounding.ToEven) };

    public override string ToString() => $"{Amount:0.00} {Currency}";

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (!string.Equals(left.Currency, right.Currency, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Cannot combine {left.Currency} with {right.Currency}.");
        }
    }
}
