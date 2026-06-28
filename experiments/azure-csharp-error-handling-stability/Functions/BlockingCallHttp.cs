using System.Net;
using System.Text.Json;
using AzureErrorHandlingDemo.Models;
using AzureErrorHandlingDemo.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AzureErrorHandlingDemo.Functions;

/// <summary>
/// DEMO 5 — Thread Pool Starvation: GlobalExceptionMiddleware Is Never Invoked
///
/// This HTTP trigger uses sync-over-async: it calls .Result on an async method,
/// which blocks the calling thread pool thread until the async work completes.
///
/// WHY THE GLOBAL HANDLER CANNOT HELP:
///   GlobalExceptionMiddleware only intercepts EXCEPTIONS. Thread pool starvation
///   does not throw an exception — threads are simply blocked, not faulted.
///   The middleware is completely invisible to this failure mode. It logs nothing,
///   catches nothing, and returns nothing, because it is never invoked.
///
/// WHAT ACTUALLY HAPPENS UNDER LOAD:
///   - Each concurrent request to this endpoint blocks one thread pool thread.
///   - .NET's thread pool starts with a limited number of threads. When they are
///     all blocked, new threads are injected slowly (1 per 500ms by default).
///   - If requests arrive faster than threads are injected, the queue grows.
///   - Response times climb from milliseconds to seconds to timeouts.
///   - The function app becomes completely unresponsive — not because of an error,
///     but because every thread is waiting.
///
/// HOW THIS MANIFESTS IN A DEPLOYED AZURE FUNCTION:
///   - Application Insights shows request duration spiking under load.
///   - No exceptions appear in logs (GlobalExceptionMiddleware never fires).
///   - Azure may scale out to more instances, but each new instance develops
///     the same starvation under load — scaling doesn't fix a code defect.
///   - Eventually, the Azure Functions host's function timeout (host.json:
///     functionTimeout) kills stuck requests, producing 500s that appear
///     to come from nowhere.
///
/// COMMON REAL-WORLD TRIGGERS:
///   - Calling async service methods with .Result or .GetAwaiter().GetResult()
///   - Using Task.Run(...).Result to "offload" work
///   - Mixing async and sync code when integrating legacy libraries
///   - HttpClient calls wrapped in sync adapters
///
/// THE FIX:
///   Make the function handler async and await the service call:
///   var order = await _orderService.CreateOrderAsync(orderRequest);
/// </summary>
public class BlockingCallHttp
{
    private readonly ILogger<BlockingCallHttp> _logger;
    private readonly OrderService _orderService;

    public BlockingCallHttp(ILogger<BlockingCallHttp> logger, OrderService orderService)
    {
        _logger = logger;
        _orderService = orderService;
    }

    // Note: this function is NOT async — sync-over-async only works (badly) in sync contexts.
    // In Azure Functions isolated worker, sync function handlers are allowed, which makes
    // this anti-pattern easy to write accidentally.
    [Function("BlockingCallHttp")]
    public HttpResponseData Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "orders/blocking")] HttpRequestData req)
    {
        _logger.LogInformation("BlockingCallHttp invoked — this handler blocks the thread pool");

        string? body = null;
        OrderRequest? orderRequest = null;

        try
        {
            // ❌ ANTI-PATTERN: Reading the request body synchronously by blocking on async.
            // ReadAsStringAsync() is async; calling .Result blocks this thread until done.
            body = req.ReadAsStringAsync().Result;

            orderRequest = JsonSerializer.Deserialize<OrderRequest>(body ?? string.Empty,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read or parse request body");
            var badReqResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            badReqResponse.WriteString("Bad request");
            return badReqResponse;
        }

        // ❌ ANTI-PATTERN (Demo 5): Calling an async method synchronously via .Result.
        //
        // What happens on this line:
        //   1. CreateOrderAsync() is called — it starts executing asynchronously.
        //   2. .Result blocks the CURRENT thread (the thread pool thread serving this request)
        //      until CreateOrderAsync() completes.
        //   3. CreateOrderAsync() itself awaits Task.Delay() and other async operations.
        //      Those continuations need a thread pool thread to resume on.
        //   4. Under load, if all thread pool threads are blocked on .Result, there are
        //      no threads available for the async continuations to resume — DEADLOCK.
        //
        // In Azure Functions isolated worker (unlike classic ASP.NET), there is no
        // SynchronizationContext, so true deadlock is less likely than in legacy ASP.NET.
        // However, thread pool SATURATION still occurs:
        //   - Each blocked .Result call holds a thread for the full duration of the async work.
        //   - The thread pool has a limited number of threads (default: processor count × 4 minimum).
        //   - 20 concurrent requests = 20 blocked threads = thread pool exhausted.
        //   - Response times for all requests (including simple health checks) grow to seconds.
        //
        // GlobalExceptionMiddleware sees NOTHING here. No exception. No log entry.
        // Just silence — and a function app that stops responding under load.
        Order order;
        try
        {
            order = _orderService.CreateOrderAsync(orderRequest!).Result;
        }
        catch (AggregateException ae) when (ae.InnerException is not null)
        {
            // .Result wraps exceptions in AggregateException — another reason .Result is wrong.
            // The original exception type and stack trace are buried inside.
            // GlobalExceptionMiddleware would see a generic AggregateException if this propagated,
            // losing even more diagnostic information.
            _logger.LogError(ae.InnerException, "Order processing failed (unwrapped from AggregateException)");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            errorResponse.WriteString("Something went wrong");
            return errorResponse;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.WriteAsJsonAsync(new { message = "Order created (via blocking call)", order });
        return response;
    }
}
