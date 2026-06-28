using System.Net;
using AzureErrorHandlingDemo.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// ❌ ANTI-PATTERN: The ONLY error handling infrastructure in this entire application
// is the GlobalExceptionMiddleware registered below. There are no typed exceptions,
// no per-function error classification, no status-code mapping — just one middleware
// that catches everything, logs "Unhandled exception", and returns 500.
//
// This gives a false sense of security. It is useful as a last-resort logger,
// but it is NOT a substitute for proper exception handling at the right level.
// By the time GlobalExceptionMiddleware sees an exception:
//   - All semantic meaning is lost (was it a 400, 404, or genuine 500?)
//   - The middleware can't know which service threw or why
//   - For queue triggers, the middleware can't decide whether to retry

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults(builder =>
    {
        // Register the global exception handler as the sole error strategy.
        // This is the anti-pattern being demonstrated — the entire app relies on this.
        builder.UseMiddleware<GlobalExceptionMiddleware>();

        // ❌ Register the UnobservedTaskException handler for Demo 3.
        // In Azure Functions isolated worker, unobserved task exceptions from
        // fire-and-forget calls can silently crash the worker process when
        // ThrowUnobservedTaskExceptions is enabled (common in enterprise configs).
        TaskScheduler.UnobservedTaskException += (sender, args) =>
        {
            // This fires when the GC collects a faulted Task that was never observed.
            // By this point we have zero context: no request, no user, no function name.
            // We just know "something async failed somewhere".
            Console.Error.WriteLine(
                $"[GLOBAL] UnobservedTaskException — an async operation failed silently: {args.Exception?.Message}");

            // args.SetObserved() prevents the default crash behaviour in environments
            // where ThrowUnobservedTaskExceptions=true. But notice what we give up:
            // the exception is now truly gone — no retry, no alert, no recovery.
            // Either we crash (observed=false + ThrowUnobservedTaskExceptions=true)
            // or we silently swallow (observed=true). Neither is correct.
            // The RIGHT fix is: don't create unobserved tasks in the first place.
            args.SetObserved();
        };
    })
    .ConfigureServices(services =>
    {
        services.AddSingleton<UserService>();
        services.AddSingleton<PaymentService>();
        services.AddSingleton<OrderService>();
        // ❌ ANTI-PATTERN (Demo 6): Registered as singleton, but ExternalApiService
        // internally creates a new HttpClient per call — the singleton lifetime does not help.
        // The resource leak is inside the method, not at the DI registration level.
        services.AddSingleton<ExternalApiService>();
    })
    .Build();

await host.RunAsync();

/// <summary>
/// ANTI-PATTERN: GlobalExceptionMiddleware
///
/// This middleware is the entire error handling strategy for the application.
/// It catches all unhandled exceptions from all functions and returns HTTP 500.
///
/// Problems:
///   1. It cannot distinguish exception types, so it cannot map them to correct
///      HTTP status codes. Everything becomes 500.
///   2. For non-HTTP triggers (queue, timer), it cannot decide whether the error
///      is retriable — so it just re-throws, always causing Azure to retry.
///   3. By the time it catches the exception, the semantic context from the
///      original throw site is gone (especially after the re-wrapping in OrderService).
///   4. It creates a single point of failure — any bug in this middleware affects
///      ALL functions in the app simultaneously.
/// </summary>
public class GlobalExceptionMiddleware : IFunctionsWorkerMiddleware
{
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    public GlobalExceptionMiddleware(ILogger<GlobalExceptionMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            // ❌ ANTI-PATTERN: We know an exception occurred, but we've lost:
            //   - What type of error it was (validation? not-found? transient?)
            //   - Which user or request triggered it (the specific data is gone)
            //   - Whether retrying would help (for queue triggers)
            //   - What HTTP status code the caller deserves
            //
            // All we can do is log "something went wrong" and return 500 for HTTP triggers,
            // or re-throw for non-HTTP triggers (which forces a retry regardless of whether
            // the error is retriable).
            _logger.LogError(ex, "Unhandled exception in function {FunctionName}", context.FunctionDefinition.Name);

            // Attempt to return a 500 if this is an HTTP trigger.
            // For queue/timer triggers, this does nothing — the exception will still propagate.
            var httpReqData = await context.GetHttpRequestDataAsync();
            if (httpReqData is not null)
            {
                var response = httpReqData.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteStringAsync("An unexpected error occurred.");

                var invocationResult = context.GetInvocationResult();
                var httpOutputBindingFromMultipleOutputBindings = context.GetOutputBindings<HttpResponseData>()
                    .FirstOrDefault(b => b.BindingType == "http");

                if (httpOutputBindingFromMultipleOutputBindings is not null)
                    httpOutputBindingFromMultipleOutputBindings.Value = response;
                else
                    invocationResult.Value = response;
            }
        }
    }
}
