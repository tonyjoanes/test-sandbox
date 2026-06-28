using AzureErrorHandlingDemo.Models;

namespace AzureErrorHandlingDemo.Services;

/// <summary>
/// DEMO: OrderService
///
/// ANTI-PATTERN 4 — Context Loss via Exception Re-Wrapping:
/// When a downstream service throws, this layer catches and re-throws a brand-new
/// Exception WITHOUT passing the original as InnerException. The result:
///
///   - The root-cause exception (e.g. "User not found: user-999" from UserService.cs:42)
///     is silently discarded.
///   - Application Insights / logs show the exception originating from THIS file,
///     not from the true source.
///   - The specific context (which userId failed, what the credit limit was) is gone.
///   - On-call engineers spend 30 minutes tracing a "could not verify user" error
///     that could have been diagnosed instantly with a proper stack trace.
///
/// Correct pattern: throw new OrderProcessingException("...", innerException: ex);
/// or re-throw with: ExceptionDispatchInfo.Capture(ex).Throw();
/// </summary>
public class OrderService
{
    private readonly UserService _userService;
    private readonly PaymentService _paymentService;

    public OrderService(UserService userService, PaymentService paymentService)
    {
        _userService = userService;
        _paymentService = paymentService;
    }

    public async Task<Order> CreateOrderAsync(OrderRequest request)
    {
        ValidateRequest(request);

        try
        {
            await _userService.GetUserAsync(request.UserId);
        }
        catch (Exception ex)
        {
            // ❌ ANTI-PATTERN (Demo 4): Re-wrapping without InnerException.
            // We've lost:
            //   - Whether this was a 400 (null userId) or 404 (user not found)
            //   - The original exception message identifying the specific userId
            //   - The stack trace pointing to UserService.cs line 42
            //
            // The fix: throw new Exception("Order processing failed: could not verify user", ex);
            //                                                                             ↑↑
            //                                                             pass ex as InnerException
            _ = ex; // suppress "ex is unused" — intentionally discarded to show the anti-pattern
            throw new Exception("Order processing failed: could not verify user");
        }

        try
        {
            await _userService.ValidateCreditLimitAsync(request.UserId, request.Amount);
        }
        catch (Exception ex)
        {
            // ❌ ANTI-PATTERN (Demo 4): The specific credit-limit failure reason is gone.
            // Was it "amount must be positive" or "credit limit exceeded: $50,000 vs $1,000"?
            // The caller — and the engineer reading the log — will never know.
            _ = ex;
            throw new Exception("Order processing failed: credit validation failed");
        }

        try
        {
            await _paymentService.ChargeAsync(request.UserId, request.Amount);
        }
        catch (Exception ex)
        {
            // ❌ ANTI-PATTERN (Demo 4): Payment errors could be transient (gateway timeout)
            // or permanent (account suspended). Re-wrapping hides which it was.
            // A caller that knows the original error type could retry transient failures
            // intelligently — but now it can't.
            _ = ex;
            throw new Exception("Order processing failed: payment could not be processed");
        }

        return new Order
        {
            OrderId = $"ord-{Guid.NewGuid():N}",
            UserId = request.UserId,
            ProductId = request.ProductId,
            Quantity = request.Quantity,
            Amount = request.Amount,
            Status = "confirmed",
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static void ValidateRequest(OrderRequest? request)
    {
        if (request is null)
        {
            // ❌ ANTI-PATTERN: Validation failure — should be 400. Generic Exception.
            throw new Exception("Order request body is required");
        }

        if (string.IsNullOrWhiteSpace(request.UserId))
            throw new Exception("Missing required field: UserId");

        if (string.IsNullOrWhiteSpace(request.ProductId))
            throw new Exception("Missing required field: ProductId");

        if (request.Quantity < 1)
            throw new Exception("Quantity must be at least 1");

        if (request.Amount <= 0)
            throw new Exception("Amount must be a positive number");
    }
}
