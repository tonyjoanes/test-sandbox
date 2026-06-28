# Azure Functions C# — Error Handling Stability Anti-Pattern Demo

This project demonstrates **four specific stability failure modes** that occur in Azure Function Apps when error handling relies solely on a global exception handler, with generic `Exception` throws throughout the call stack.

> All code is intentionally written with anti-patterns. Every anti-pattern is marked `// ❌ ANTI-PATTERN` with an explanation of what breaks and why.

---

## What This Proves

When developers rely on a single `GlobalExceptionMiddleware` as the only safety net, and every layer of the codebase throws `new Exception("something failed")`, the following problems emerge:

| # | Problem | Symptom |
|---|---|---|
| 1 | Wrong HTTP status codes | Bad requests return 500; callers can't distinguish client errors from server crashes |
| 2 | Queue trigger retry storm | Permanently-broken messages retry 5× before dead-lettering, wasting compute |
| 3 | Unobserved task exception | Fire-and-forget async crashes the process, taking all functions offline |
| 4 | Exception context destruction | Re-wrapping exceptions removes root cause, stack trace, and original message |
| **5** | **Thread pool starvation** | **`.Result` blocks threads; middleware never fires; app becomes unresponsive under load** |
| **6** | **Resource leak** | **`new HttpClient()` per request; handler catches SocketExceptions but can't reclaim sockets** |

---

## Project Structure

```
├── Program.cs                    # GlobalExceptionMiddleware — the false safety net
├── Functions/
│   ├── ProcessOrderHttp.cs       # Demo 1 & 3: HTTP trigger
│   └── ProcessQueueMessage.cs    # Demo 2: Queue trigger retry storm
├── Services/
│   ├── UserService.cs            # Throws generic Exception for 400/404/422 conditions
│   ├── OrderService.cs           # Re-wraps exceptions destroying context (Demo 4)
│   └── PaymentService.cs         # Conflates transient and permanent failures
└── Models/
    ├── OrderRequest.cs
    ├── Order.cs
    └── QueueMessage.cs
```

---

## Setup

```bash
dotnet build

# With Azure Functions Core Tools v4:
cp local.settings.json.example local.settings.json
func start
```

---

## Demo 1: Wrong HTTP Status Codes

**Files:** `Functions/ProcessOrderHttp.cs`, `Services/OrderService.cs`, `Services/UserService.cs`

The HTTP function has a single `catch (Exception ex)` block that returns `500 Internal Server Error` for every error, regardless of cause.

### Test it

```bash
# Should be 400 Bad Request (missing fields) — returns 500
curl -s -o /dev/null -w "%{http_code}" \
  -X POST http://localhost:7071/api/orders \
  -H "Content-Type: application/json" \
  -d '{}'

# Should be 404 Not Found (unknown user) — returns 500
curl -s -o /dev/null -w "%{http_code}" \
  -X POST http://localhost:7071/api/orders \
  -H "Content-Type: application/json" \
  -d '{"userId":"user-999","productId":"prod-1","quantity":1,"amount":50}'

# Should be 422 Unprocessable Entity (credit exceeded) — returns 500
curl -s -o /dev/null -w "%{http_code}" \
  -X POST http://localhost:7071/api/orders \
  -H "Content-Type: application/json" \
  -d '{"userId":"user-002","productId":"prod-1","quantity":1,"amount":99999}'
```

All three return `500`. All three should return different status codes.

### Why It Matters

- HTTP clients and load balancers retry `5xx` responses but not `4xx`. Every bad request becomes a retry storm at the network layer.
- Monitoring dashboards alert on `500` error rates — client mistakes trigger false alarms and hide real server failures.
- API consumers cannot implement correct error handling because they cannot tell what went wrong.

---

## Demo 2: Queue Trigger Retry Storm

**File:** `Functions/ProcessQueueMessage.cs`

Azure Functions retries a Storage Queue message whenever the handler throws **any** exception. `host.json` sets `maxDequeueCount: 5`.

A malformed message (invalid JSON, missing `MessageId`) will **always** fail — the bytes don't change between retries. But because validation failures throw a generic `Exception`, Azure cannot distinguish them from transient failures. Result: 5 retries before dead-lettering, all wasted.

### The Math

```
Scenario: 1,000 malformed messages arrive per hour

Current code (no distinction):
  1,000 messages × 5 retries = 5,000 invocations/hour
  80% of all compute is wasted on messages that will never succeed

With typed exceptions (NonRetriableException):
  1,000 messages × 1 attempt = 1,000 invocations/hour
  Invalid messages dead-lettered immediately
```

### The Fix (Preview)

```csharp
// A typed exception that signals "don't retry this"
public class NonRetriableException : Exception
{
    public NonRetriableException(string message, Exception? inner = null)
        : base(message, inner) { }
}

// In the queue handler:
catch (NonRetriableException ex)
{
    _logger.LogWarning(ex, "Poison message — acknowledging without retry");
    return; // Return normally → Azure marks message as processed, no retry
}
// Any other exception propagates → Azure retries (correct for transient failures)
```

---

## Demo 3: Unobserved Task Exception

**File:** `Functions/ProcessOrderHttp.cs` — the `_ = SendNotificationAsync(...)` call

`SendNotificationAsync` is called using C#'s discard pattern (`_ = ...`). This starts the async operation but does not await it and does not attach a `.ContinueWith(...)` to observe failures.

When `SendNotificationAsync` throws (30% simulated probability):
1. The exception is captured inside the `Task` object
2. The HTTP response has already been sent — the caller sees `200 OK`
3. When the GC finalises the uncollected `Task`, `TaskScheduler.UnobservedTaskException` fires
4. In environments with `ThrowUnobservedTaskExceptions = true` (common in enterprise configs), **the process crashes**
5. All in-flight requests fail; all queue messages being processed are not acknowledged; **all functions in the app go offline** until Azure restarts the worker

### How to Observe

Run the valid-user request multiple times. Watch the function logs — roughly 1 in 3 requests will log `[GLOBAL] UnobservedTaskException` several seconds after the `200 OK` response.

```bash
# Valid request — returns 200, but ~30% chance of crashing the worker later
for i in {1..10}; do
  curl -s -o /dev/null -w "Request $i: %{http_code}\n" \
    -X POST http://localhost:7071/api/orders \
    -H "Content-Type: application/json" \
    -d '{"userId":"user-001","productId":"prod-1","quantity":1,"amount":100}'
done
```

### The Fix (Two Options)

```csharp
// Option A: await it (correct, adds ~50ms latency)
await SendNotificationAsync(order.OrderId, order.UserId);

// Option B: fire-and-forget safely (non-critical work)
_ = SendNotificationAsync(order.OrderId, order.UserId)
    .ContinueWith(
        t => _logger.LogError(t.Exception, "Notification failed (non-critical)"),
        TaskContinuationOptions.OnlyOnFaulted);
```

---

## Demo 4: Exception Context Destruction via Re-Wrapping

**File:** `Services/OrderService.cs`

When `UserService.GetUserAsync("user-999")` throws `Exception("User not found: user-999")`, `OrderService` catches it and throws a brand-new `Exception("Order processing failed: could not verify user")` — without passing the original as `InnerException`.

### What the Log Shows (Current Code)

```
System.Exception: Order processing failed: could not verify user
   at AzureErrorHandlingDemo.Services.OrderService.CreateOrderAsync(OrderRequest request)
       in OrderService.cs:line 42
   at AzureErrorHandlingDemo.Functions.ProcessOrderHttp.RunAsync(...)
       in ProcessOrderHttp.cs:line 71
```

The stack trace points to `OrderService.cs:42` — not to the actual root cause in `UserService.cs`. The specific `userId` (`user-999`) is gone. An on-call engineer has to trace through the code manually to find the original error.

### What It Should Show

```
System.Exception: Order processing failed: could not verify user
   at OrderService.cs:42
 ---> System.Exception: User not found: user-999      ← root cause preserved
   at UserService.cs:44                               ← actual source preserved
```

### The Fix

```csharp
// Pass the original exception as InnerException
throw new Exception("Order processing failed: could not verify user", ex);
//                                                                    ↑↑
//                                                    original exception preserved

// Or re-throw without wrapping (preserves original stack trace entirely)
ExceptionDispatchInfo.Capture(ex).Throw();
```

---

## Demo 5: Thread Pool Starvation — GlobalExceptionMiddleware Is Never Invoked

**File:** `Functions/BlockingCallHttp.cs`

`GlobalExceptionMiddleware` only intercepts **exceptions**. Thread pool starvation doesn't throw — threads simply block. The middleware is completely invisible to this failure mode.

```csharp
// ❌ ANTI-PATTERN: .Result blocks a thread pool thread until the async work completes
var order = _orderService.CreateOrderAsync(orderRequest).Result;
```

Under concurrent load:
1. Each request blocks one thread pool thread via `.Result`
2. .NET's thread pool starts with a limited number of threads
3. When all threads are blocked, new threads are injected slowly (~1 per 500ms)
4. Requests queue up; response times climb from milliseconds to seconds to timeouts
5. `GlobalExceptionMiddleware` logs **nothing** — there is no exception to catch

### How to Observe

```bash
# Fire 20 concurrent requests and watch response times climb
for i in {1..20}; do
  curl -s -o /dev/null -w "Request $i: %{http_code} in %{time_total}s\n" \
    -X POST http://localhost:7071/api/orders/blocking \
    -H "Content-Type: application/json" \
    -d '{"userId":"user-001","productId":"p1","quantity":1,"amount":100}' &
done
wait
```

Expected: early requests complete quickly; later requests take progressively longer; no exceptions appear in the middleware logs. The handler is silent — because it was never invoked.

### Why This Happens in Real Deployments

- Legacy synchronous libraries wrapped in async adapters
- Service layer that mixes `async/await` with `.Result` or `.GetAwaiter().GetResult()`
- Calls to third-party SDKs that return `Task` but the caller blocks on them
- Azure Functions has a configurable `functionTimeout` (default 5 minutes) — when the thread pool is saturated, requests hit this timeout and produce 500s that appear to come from nowhere

### The Fix

```csharp
// Make the handler async and await the service call
[Function("BlockingCallHttp")]
public async Task<HttpResponseData> RunAsync(...)
{
    var order = await _orderService.CreateOrderAsync(orderRequest); // ✅ no blocking
}
```

---

## Demo 6: Resource Leak — The Handler Catches Exceptions But Damage Has Already Accumulated

**Files:** `Services/ExternalApiService.cs`, `Functions/ResourceLeakHttp.cs`

A new `HttpClient` is created on every request. `using` disposes the managed C# object, but the underlying TCP socket enters `TIME_WAIT` state for ~4 minutes (an OS-level protocol requirement — the OS cannot be told to skip this).

`GlobalExceptionMiddleware` (and the local catch block) catches each `SocketException` and returns 500. But it **cannot reclaim the exhausted ports**. Each caught exception represents one more leaked socket. After enough requests, the port pool is exhausted and every call fails.

```csharp
public async Task<string> FetchProductDetailsAsync(string productId)
{
    // ❌ ANTI-PATTERN: new HttpClient() per call
    // Each disposal leaves an OS socket in TIME_WAIT for ~4 minutes
    using var client = new HttpClient();
    return await client.GetStringAsync($"https://product-api/{productId}");
}
```

### How to Observe

```bash
# The failure probability increases with each request (simulating port accumulation)
for i in {1..50}; do
  curl -s -o /dev/null -w "Request $i: %{http_code}\n" \
    http://localhost:7071/api/products/prod-$(printf '%06d' $i)
done
```

Expected: early requests succeed (200); failure rate increases with request count; logs show `SocketException` per failure; restarting the function app temporarily restores operation.

### The Critical Observation

This function **does** have a `try/catch` — exceptions are caught and handled. `GlobalExceptionMiddleware` would also catch anything that escapes. Despite this, the system degrades with every request.

This is the core proof: **exception handling responds to symptoms, not root causes**. Each handled 500 response represents one more socket leaked. Handle 1,000 exceptions → 1,000 leaked sockets. The handler is cleaning up after each failure but cannot stop the next one.

### The Fix

```csharp
// Register IHttpClientFactory in Program.cs:
services.AddHttpClient<ExternalApiService>();

// Inject HttpClient via constructor — the factory manages connection pooling:
public class ExternalApiService
{
    private readonly HttpClient _client; // reused across requests, connections pooled

    public ExternalApiService(HttpClient client) { _client = client; }

    public async Task<string> FetchProductDetailsAsync(string productId)
        => await _client.GetStringAsync($"https://product-api/{productId}"); // ✅
}
```

---

## Why a Global Handler Is Not Enough — Summary

| Failure Mode | Exception Thrown? | Middleware Invoked? | Damage Recoverable by Handler? |
|---|---|---|---|
| Demo 1: Wrong status codes | Yes | Yes | No — can only return 500 |
| Demo 2: Retry storm | Yes | No (queue trigger re-throws) | No — can't classify retry |
| Demo 3: Unobserved task | No (until GC) | No | No — process may crash |
| Demo 4: Context loss | Yes | Yes | No — root cause already gone |
| **Demo 5: Thread pool starvation** | **No** | **No — bypassed entirely** | **No — threads still blocked** |
| **Demo 6: Resource leak** | Yes (eventually) | Yes | **No — sockets already leaked** |

**The pattern:** `GlobalExceptionMiddleware` only operates on exceptions that reach it. It cannot intercept blocked threads, leaked OS resources, or unobserved tasks. It cannot recover state that was already corrupted or resources already released. Real stability requires addressing root causes at the point they occur — not catching symptoms at the application boundary.

---

## Reading Order

For maximum educational value, read files in this order:

1. `Services/UserService.cs` — where generic exceptions originate
2. `Services/PaymentService.cs` — transient vs permanent conflation
3. `Services/OrderService.cs` — context destruction through re-wrapping (Demo 4)
4. `Functions/ProcessOrderHttp.cs` — wrong status codes and fire-and-forget crash (Demo 1 & 3)
5. `Functions/ProcessQueueMessage.cs` — retry storm (Demo 2)
6. `Functions/BlockingCallHttp.cs` — thread pool starvation, middleware bypassed (Demo 5)
7. `Services/ExternalApiService.cs` + `Functions/ResourceLeakHttp.cs` — resource leak (Demo 6)
8. `Program.cs` — the false safety net (GlobalExceptionMiddleware)

Every `// ❌ ANTI-PATTERN` comment explains what's wrong. Every comment block includes a preview of the correct fix.
