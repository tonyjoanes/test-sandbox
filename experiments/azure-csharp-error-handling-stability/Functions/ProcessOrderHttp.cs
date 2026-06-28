using System.Net;
using System.Text.Json;
using AzureErrorHandlingDemo.Models;
using AzureErrorHandlingDemo.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AzureErrorHandlingDemo.Functions;

/// <summary>
/// DEMO: ProcessOrderHttp
///
/// This HTTP trigger demonstrates two failure modes:
///
/// DEMO 1 — Wrong HTTP Status Codes:
///   Every exception, regardless of cause, returns HTTP 500 Internal Server Error.
///   A caller sending bad data (missing fields → should be 400), referencing an
///   unknown user (→ should be 404), or exceeding a credit limit (→ should be 422)
///   all receive the exact same 500 response. This breaks:
///     - Client retry logic: HTTP clients should NOT retry 4xx errors but SHOULD
///       retry 5xx. Every bad request causes unnecessary retry storms from clients.
///     - Monitoring: 500-rate alerts fire for client mistakes, creating alert fatigue
///       and masking real server problems.
///     - API contracts: consumers cannot implement correct error handling.
///     - Load balancers: many LBs retry on 5xx — a single bad request becomes many.
///
/// DEMO 3 — Unobserved Task Exception (Fire-and-Forget):
///   SendNotificationAsync() is called using the C# discard pattern (_ = ...).
///   This starts the async work but does not await it and does not attach a
///   continuation to observe its result. If the task faults:
///     - The exception is captured by the Task but no one is observing it.
///     - When the GC finalises the Task, TaskScheduler.UnobservedTaskException fires.
///     - With ThrowUnobservedTaskExceptions = true in runtimeconfig.json (common in
///       enterprise environments), this terminates the process entirely.
///     - Even without that setting, the error is silently swallowed — the notification
///       never sent, with no log entry, no alert, no trace. Impossible to diagnose.
///   The HTTP response has already returned 200 OK. The caller sees success.
///   The process may crash moments later, taking ALL functions in the app offline.
/// </summary>
public class ProcessOrderHttp
{
    private readonly ILogger<ProcessOrderHttp> _logger;
    private readonly OrderService _orderService;
    private static readonly Random _random = new();

    public ProcessOrderHttp(ILogger<ProcessOrderHttp> logger, OrderService orderService)
    {
        _logger = logger;
        _orderService = orderService;
    }

    [Function("ProcessOrderHttp")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "orders")] HttpRequestData req)
    {
        _logger.LogInformation("Processing order HTTP request");

        try
        {
            var body = await req.ReadAsStringAsync();
            OrderRequest? orderRequest = null;

            try
            {
                orderRequest = JsonSerializer.Deserialize<OrderRequest>(body ?? string.Empty,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException)
            {
                // ❌ ANTI-PATTERN (Demo 1): Invalid JSON is a client error (400) but
                // the outer catch will turn it into a 500.
                throw new Exception("Request body is not valid JSON");
            }

            var order = await _orderService.CreateOrderAsync(orderRequest);

            // ❌ ANTI-PATTERN (Demo 3): Fire-and-forget using C# discard pattern.
            // _ = discards the Task — the compiler won't warn about the unawaited call,
            // and no continuation observes whether it succeeded or failed.
            //
            // If SendNotificationAsync throws, .NET captures the exception in the Task.
            // When the Task is garbage collected, TaskScheduler.UnobservedTaskException fires.
            // With ThrowUnobservedTaskExceptions = true → process crash → all functions offline.
            //
            // The fix (option A): await SendNotificationAsync(order.OrderId, order.UserId);
            // The fix (option B): _ = SendNotificationAsync(order.OrderId, order.UserId)
            //                         .ContinueWith(t => _logger.LogError(t.Exception, "Notification failed"),
            //                                       TaskContinuationOptions.OnlyOnFaulted);
            _ = SendNotificationAsync(order.OrderId, order.UserId);

            var successResponse = req.CreateResponse(HttpStatusCode.OK);
            await successResponse.WriteAsJsonAsync(new { message = "Order created successfully", order });
            return successResponse;
        }
        catch (Exception ex)
        {
            // ❌ ANTI-PATTERN (Demo 1): One catch block for ALL exception types.
            // There is no inspection of the exception type, message, or cause.
            // Every single failure path — validation, not-found, business rule,
            // payment error, unexpected crash — returns HTTP 500.
            //
            // What SHOULD happen:
            //   "Order request body is required"        → 400 Bad Request
            //   "Missing required field: UserId"        → 400 Bad Request
            //   "User not found: user-999"              → 404 Not Found
            //   "Credit limit exceeded: ..."            → 422 Unprocessable Entity
            //   "Payment gateway timeout"               → 503 Service Unavailable
            //   Truly unexpected NullReferenceException → 500 Internal Server Error
            //
            // What DOES happen: everything above returns 500.
            _logger.LogError(ex, "Order processing failed");

            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new
            {
                error = "Something went wrong",
                // ❌ Leaking internal exception messages to HTTP clients is also a
                // security risk — stack traces and error details can reveal system internals.
                detail = ex.Message,
            });
            return errorResponse;
        }
    }

    private static async Task SendNotificationAsync(string orderId, string userId)
    {
        await Task.Delay(50); // simulate notification service call

        // 30% chance of failure — simulates an unreliable notification service
        if (_random.NextDouble() < 0.3)
        {
            // This exception will be unobserved. If the Task is discarded (_ = ...),
            // no one will catch this — it becomes an UnobservedTaskException.
            throw new Exception($"Notification service unavailable for order {orderId} (user: {userId})");
        }

        Console.WriteLine($"[Notification] Confirmation sent for order {orderId}");
    }
}
