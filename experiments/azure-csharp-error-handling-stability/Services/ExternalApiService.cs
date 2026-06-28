namespace AzureErrorHandlingDemo.Services;

/// <summary>
/// DEMO 6 — Resource Leak: GlobalExceptionMiddleware Catches the Exception,
///           But the Damage Has Already Accumulated
///
/// This service creates a new HttpClient for every call. This is one of the
/// most common mistakes in C# services and a well-documented anti-pattern.
///
/// WHY NEW HttpClient() PER CALL IS DANGEROUS:
///
///   HttpClient implements IDisposable. Wrapping it in `using` correctly disposes
///   the managed C# object — but the UNDERLYING TCP SOCKET is not immediately closed.
///
///   When a TCP connection is closed, the OS keeps the socket in TIME_WAIT state for
///   approximately 2–4 minutes (configurable, but this is the default on most systems).
///   This is a TCP protocol requirement — it ensures delayed packets don't corrupt a
///   new connection on the same port.
///
///   Each `new HttpClient()` + `Dispose()` call:
///     1. Opens a new TCP connection to the remote host
///     2. Closes it (marks it for TIME_WAIT)
///     3. Leaves the port unusable for ~2–4 minutes
///
///   A system under load making 100 calls/minute = 100 ports consumed/minute.
///   After 5–10 minutes: hundreds of ports in TIME_WAIT.
///   Eventually: SocketException "Only one usage of each socket address is permitted"
///               or "An attempt was made to access a socket in a way forbidden by its
///               access permissions" (port exhaustion).
///
/// HOW GlobalExceptionMiddleware RESPONDS:
///   When port exhaustion causes a SocketException, GlobalExceptionMiddleware catches
///   it and returns HTTP 500. From the outside, this looks like individual request
///   failures being "handled". But the middleware cannot:
///     - Release the sockets already in TIME_WAIT (the OS holds them, not .NET)
///     - Prevent the next request from failing the same way
///     - Recover the port pool — only time (2–4 minutes of no new connections) does that
///
///   The damage accumulates invisibly across requests. Each "handled" 500 response
///   represents one more leaked socket. The middleware is cleaning up the bodies
///   but not stopping the bleeding.
///
/// HOW THIS MANIFESTS IN A DEPLOYED AZURE FUNCTION:
///   - Works perfectly under low traffic.
///   - Under moderate load (50–200 req/min to external services): intermittent 500s.
///   - Under high load: sustained 500s for all requests involving external calls.
///   - Restarting the function app temporarily restores service (OS reclaims sockets
///     when the process exits), leading to teams adding auto-restarts as a "fix".
///   - Application Insights shows SocketException in dependency calls but the
///     correlation to HttpClient creation is easy to miss.
///
/// THE FIX:
///   Use IHttpClientFactory (registered in DI via services.AddHttpClient()) or make
///   HttpClient a singleton. IHttpClientFactory manages connection pooling and rotates
///   handlers to avoid DNS caching issues while reusing TCP connections.
///
///   // In Program.cs:
///   services.AddHttpClient<ExternalApiService>();
///
///   // In ExternalApiService:
///   public ExternalApiService(HttpClient client) { _client = client; } // injected, reused
/// </summary>
public class ExternalApiService
{
    private static readonly Random _random = new();

    public async Task<string> FetchProductDetailsAsync(string productId)
    {
        // ❌ ANTI-PATTERN (Demo 6): new HttpClient() per invocation.
        //
        // This creates a new TCP connection on every call. The connection is placed
        // in TIME_WAIT when disposed. Under load, this exhausts the port pool.
        //
        // GlobalExceptionMiddleware will catch SocketExceptions from port exhaustion
        // and return 500 — but it cannot reclaim the exhausted ports.
        //
        // The correct pattern: inject HttpClient via IHttpClientFactory in the constructor.
        using var client = new HttpClient();

        // We simulate the call without hitting a real URL (demo environment has no
        // network access). In a real scenario this would be:
        //   return await client.GetStringAsync($"https://product-api.internal/{productId}");
        //
        // Instead, we simulate the socket lifecycle and its failure mode:
        await SimulateExternalCallAsync(productId);

        return $"{{\"productId\":\"{productId}\",\"name\":\"Widget\",\"price\":9.99}}";
    }

    private static async Task SimulateExternalCallAsync(string productId)
    {
        await Task.Delay(25); // simulate network round-trip

        // Simulate port exhaustion under sustained load.
        // In a real deployment, this probability increases over time as ports accumulate
        // in TIME_WAIT. We model that by using a higher threshold for "late" product IDs.
        var isHighLoad = productId.Length > 6; // proxy for "many prior calls have run"
        var exhaustionProbability = isHighLoad ? 0.4 : 0.1;

        if (_random.NextDouble() < exhaustionProbability)
        {
            // This simulates the SocketException that occurs when the port pool is exhausted.
            // GlobalExceptionMiddleware will catch this and return 500.
            // But the ports from the previous 1,000 calls are still in TIME_WAIT.
            throw new InvalidOperationException(
                $"Simulated socket exhaustion fetching product {productId}: " +
                "An attempt was made to access a socket in a way forbidden by its access permissions. " +
                "(In a real deployment this would be System.Net.Sockets.SocketException)");
        }
    }
}
