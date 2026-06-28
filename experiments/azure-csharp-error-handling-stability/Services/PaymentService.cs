namespace AzureErrorHandlingDemo.Services;

/// <summary>
/// DEMO: PaymentService
///
/// ANTI-PATTERN: Transient failures (gateway timeout — retry is correct) and
/// permanent failures (account suspended — retry is useless) both throw the
/// same generic Exception type. Callers cannot make an intelligent retry decision
/// because the error type carries no semantic meaning.
/// </summary>
public class PaymentService
{
    private static readonly Random _random = new();

    public async Task<string> ChargeAsync(string userId, decimal amount)
    {
        await Task.Delay(15); // simulate network call

        // Simulate a transient gateway timeout — this SHOULD be retried by the caller
        if (_random.NextDouble() < 0.2)
        {
            // ❌ ANTI-PATTERN: This is a TRANSIENT error. Callers should retry.
            // But we throw the same generic Exception as permanent failures below,
            // so callers cannot distinguish "try again" from "give up".
            throw new Exception("Payment gateway timeout — connection refused");
        }

        // Simulate a permanently suspended account — retrying this is wasteful and wrong
        if (userId == "user-blocked")
        {
            // ❌ ANTI-PATTERN: This is a PERMANENT error. Retrying will never succeed.
            // A proper design would throw a PaymentDeclinedException or similar typed
            // error so callers know not to retry and can surface a meaningful message.
            throw new Exception("Payment account is suspended");
        }

        return $"txn-{Guid.NewGuid():N}";
    }
}
