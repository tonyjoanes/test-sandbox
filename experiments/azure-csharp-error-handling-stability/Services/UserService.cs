using AzureErrorHandlingDemo.Models;

namespace AzureErrorHandlingDemo.Services;

/// <summary>
/// DEMO: UserService
///
/// ANTI-PATTERN: All error conditions throw the same generic Exception type.
/// The caller cannot distinguish:
///   - "userId is null"        → should map to HTTP 400 Bad Request
///   - "user doesn't exist"    → should map to HTTP 404 Not Found
///   - "credit limit exceeded" → should map to HTTP 422 Unprocessable Entity
///   - "database is down"      → should map to HTTP 503 Service Unavailable
///
/// Without typed exceptions, the HTTP layer has no choice but to return 500 for all of them.
/// </summary>
public class UserService
{
    private static readonly Dictionary<string, User> Users = new()
    {
        ["user-001"] = new User("user-001", "alice@example.com", "Alice", creditLimit: 5000m),
        ["user-002"] = new User("user-002", "bob@example.com", "Bob", creditLimit: 1000m),
    };

    public async Task<User> GetUserAsync(string userId)
    {
        await Task.Delay(10); // simulate DB latency

        if (string.IsNullOrWhiteSpace(userId))
        {
            // ❌ ANTI-PATTERN: Validation failure — semantically a 400 Bad Request.
            // Thrown as generic Exception so the HTTP layer cannot map it correctly.
            throw new Exception("User ID is required");
        }

        if (!Users.TryGetValue(userId, out var user))
        {
            // ❌ ANTI-PATTERN: Not-found condition — semantically a 404 Not Found.
            // Identical Exception type to the validation failure above — callers
            // cannot tell these apart without fragile message-string parsing.
            throw new Exception($"User not found: {userId}");
        }

        return user;
    }

    public async Task ValidateCreditLimitAsync(string userId, decimal amount)
    {
        var user = await GetUserAsync(userId);

        if (amount <= 0)
        {
            // ❌ ANTI-PATTERN: Business rule violation — semantically a 400.
            throw new Exception("Order amount must be positive");
        }

        if (amount > user.CreditLimit)
        {
            // ❌ ANTI-PATTERN: Business rule violation — semantically a 422
            // Unprocessable Entity (valid request, but business logic rejects it).
            // Callers receive the same Exception type as "User ID is required".
            throw new Exception($"Credit limit exceeded: requested {amount:C}, limit is {user.CreditLimit:C}");
        }
    }
}

public record User(string Id, string Email, string Name, decimal CreditLimit);
