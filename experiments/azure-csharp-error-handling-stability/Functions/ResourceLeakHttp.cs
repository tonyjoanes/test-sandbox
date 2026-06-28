using System.Net;
using AzureErrorHandlingDemo.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AzureErrorHandlingDemo.Functions;

/// <summary>
/// DEMO 6 — Resource Leak: The Handler Catches Exceptions But Cannot Undo Accumulated Damage
///
/// This HTTP trigger calls ExternalApiService, which creates a new HttpClient on every
/// invocation. See ExternalApiService.cs for the full explanation of why this causes
/// socket exhaustion under load.
///
/// THE KEY INSIGHT FOR THIS DEMO:
///
///   Notice that this function DOES have a try/catch block — unlike the fire-and-forget
///   in Demo 3, exceptions here ARE caught and DO produce a structured error response.
///   GlobalExceptionMiddleware would also catch any exceptions that escape.
///
///   And yet, the system still degrades and eventually fails.
///
///   This demonstrates the fundamental limit of exception handling as a stability strategy:
///   Exception handlers respond to SYMPTOMS (exceptions). They cannot address ROOT CAUSES
///   (resource leaks, thread starvation, corrupted state). Each "handled" exception here
///   represents one more leaked socket. Handle 1,000 exceptions → 1,000 leaked sockets.
///
///   The exception handler is like a warning light on a car dashboard. Acknowledging
///   the warning (catching the exception, returning 500) does not fix the oil leak.
///
/// WHAT AN OPERATOR SEES IN AZURE PORTAL:
///   - Normal operation: HTTP 200, p50 latency ~30ms
///   - After 200+ requests: intermittent HTTP 500s ("Something went wrong")
///   - After 500+ requests: majority of requests fail with HTTP 500
///   - Logs: "Failed to fetch product details: Simulated socket exhaustion..."
///   - GlobalExceptionMiddleware logs: same message (if exception escapes the local catch)
///   - No indication in logs that the ROOT CAUSE is the HttpClient lifecycle
///   - Restart the function app → temporarily back to normal → problem returns under load
/// </summary>
public class ResourceLeakHttp
{
    private readonly ILogger<ResourceLeakHttp> _logger;
    private readonly ExternalApiService _externalApiService;

    public ResourceLeakHttp(ILogger<ResourceLeakHttp> logger, ExternalApiService externalApiService)
    {
        _logger = logger;
        _externalApiService = externalApiService;
    }

    [Function("ResourceLeakHttp")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "products/{productId}")] HttpRequestData req,
        string productId)
    {
        _logger.LogInformation("Fetching product details for {ProductId}", productId);

        try
        {
            var productJson = await _externalApiService.FetchProductDetailsAsync(productId);

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(productJson);
            return response;
        }
        catch (Exception ex)
        {
            // This catch block executes for EVERY request that hits socket exhaustion.
            // GlobalExceptionMiddleware would also catch this if it escaped here.
            //
            // ❌ ANTI-PATTERN: The exception is "handled" — we return 500 and log the error.
            // But this is the equivalent of logging "car warning light is on" every minute:
            // it records the symptom but does nothing about the cause (leaked sockets).
            //
            // After this line executes, the following are ALL still true:
            //   - The HttpClient that caused this failure has already been disposed
            //   - The underlying socket is in TIME_WAIT and unavailable for ~4 minutes
            //   - The NEXT request to this function will create ANOTHER new HttpClient
            //   - The NEXT request has an even higher chance of hitting socket exhaustion
            //     because there are now even more ports in TIME_WAIT
            //   - No amount of exception handling can reclaim those ports
            //
            // The global handler is catching individual failures but the system is
            // degrading with each catch. This is the core proof that exception handlers
            // alone cannot guarantee stability.
            _logger.LogError(ex, "Failed to fetch product details for {ProductId} — " +
                "this may indicate socket exhaustion from HttpClient misuse", productId);

            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new
            {
                error = "Failed to fetch product details",
                productId,
                // Note: in production, don't expose ex.Message to callers — it may
                // reveal internal system details. Shown here for demo clarity only.
                detail = ex.Message,
            });
            return errorResponse;
        }
    }
}
