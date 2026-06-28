using System.Text.Json;
using AzureErrorHandlingDemo.Models;
using AzureErrorHandlingDemo.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AzureErrorHandlingDemo.Functions;

/// <summary>
/// DEMO: ProcessQueueMessage
///
/// DEMO 2 — Queue Trigger Retry Storm:
///
/// Azure Functions retries a Storage Queue message whenever the function throws
/// ANY exception. The retry count is controlled by host.json's maxDequeueCount
/// (set to 5 in this project). After 5 failed attempts, the message is moved to
/// a poison queue (dead-letter equivalent for Storage Queues).
///
/// There are TWO fundamentally different failure categories:
///
///   A) NON-RETRIABLE (permanent) failures:
///      - Message is not valid JSON                 → will ALWAYS fail on retry
///      - Required fields are missing               → will ALWAYS fail on retry
///      - Business rule violation (e.g. userId "")  → will ALWAYS fail on retry
///      Retrying these 5 times wastes compute and delays valid messages.
///
///   B) RETRIABLE (transient) failures:
///      - Downstream service temporarily down       → MAY succeed if retried
///      - Payment gateway timeout                   → MAY succeed if retried
///      - Database connection blip                  → MAY succeed if retried
///      These SHOULD be retried.
///
/// Because every error in this codebase is `throw new Exception(...)`, the
/// function handler cannot distinguish A from B. It throws on ALL errors,
/// causing Azure to retry even permanently-broken messages 5 times.
///
/// SCALE OF THE PROBLEM:
///   Scenario: 1,000 malformed messages arriving per hour.
///   With current code:   1,000 messages × 5 retries = 5,000 invocations/hour
///   With typed errors:   1,000 messages × 1 attempt = 1,000 invocations/hour
///   Wasted compute: 80%. At scale, this drives up Azure Function execution costs
///   and saturates queue processing workers, delaying valid messages.
///
/// THE FIX WOULD LOOK LIKE:
///   Introduce a NonRetriableException type. In the handler:
///     catch (NonRetriableException ex) {
///         _logger.LogWarning(ex, "Poison message — acknowledging without retry");
///         return; // Return normally → Azure marks the message as processed (no retry)
///     }
///     // Any other exception still propagates → Azure retries (retriable errors)
/// </summary>
public class ProcessQueueMessage
{
    private readonly ILogger<ProcessQueueMessage> _logger;
    private readonly OrderService _orderService;

    public ProcessQueueMessage(ILogger<ProcessQueueMessage> logger, OrderService orderService)
    {
        _logger = logger;
        _orderService = orderService;
    }

    [Function("ProcessQueueMessage")]
    public async Task RunAsync(
        [QueueTrigger("%ORDER_QUEUE_NAME%", Connection = "AzureWebJobsStorage")] string messageText)
    {
        _logger.LogInformation("Processing queue message");

        // ❌ ANTI-PATTERN (Demo 2): No distinction between retriable and non-retriable.
        // ParseMessage() throws on bad JSON or missing fields — these are PERMANENT failures.
        // Any throw here causes Azure to retry the message up to maxDequeueCount (5) times.
        // The message will fail all 5 times and then land in the poison queue.
        // We burned 5× the compute for a message that was never going to succeed.
        var message = ParseMessage(messageText);

        _logger.LogInformation("Processing message {MessageId}", message.MessageId);

        // OrderService.CreateOrderAsync can throw for both permanent reasons (bad payload)
        // and transient reasons (payment gateway timeout). The function treats them identically:
        // throw → Azure retries. Only transient failures SHOULD be retried.
        var order = await _orderService.CreateOrderAsync(message.Payload!);

        _logger.LogInformation("Order {OrderId} created from queue message {MessageId}",
            order.OrderId, message.MessageId);
    }

    private static QueueMessage ParseMessage(string messageText)
    {
        QueueMessage? message;

        try
        {
            message = JsonSerializer.Deserialize<QueueMessage>(messageText,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            // ❌ ANTI-PATTERN (Demo 2): JSON parse failure is a PERMANENT error.
            // This message can never succeed — its bytes are fundamentally invalid.
            // Throwing here causes Azure to retry it 5 more times. Each retry
            // will reach this same line and fail again. All 5 retries are wasted.
            //
            // The fix: throw new NonRetriableException("Invalid JSON", ex) and
            // catch it in RunAsync to return normally (acknowledging the message).
            throw new Exception($"Queue message is not valid JSON: {messageText[..Math.Min(80, messageText.Length)]}...", ex);
        }

        if (message is null)
        {
            // ❌ ANTI-PATTERN (Demo 2): Also a permanent failure — will retry 5 times.
            throw new Exception("Queue message deserialised to null");
        }

        if (string.IsNullOrWhiteSpace(message.MessageId))
        {
            // ❌ ANTI-PATTERN (Demo 2): Missing required field — permanent failure.
            // No matter how many times Azure retries, MessageId won't appear.
            throw new Exception("Queue message missing required field: MessageId");
        }

        if (message.Payload is null)
        {
            // ❌ ANTI-PATTERN (Demo 2): Permanent failure — payload can't be null-fixed by retry.
            throw new Exception("Queue message missing required field: Payload");
        }

        return message;
    }
}
