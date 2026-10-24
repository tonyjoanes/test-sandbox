using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Contoso.FunctionApp.Functions;

public class HelloFunction(ILogger<HelloFunction> logger)
{
    [Function("Hello")]
    public IActionResult Run([HttpTrigger(AuthorizationLevel.Function, "get", Route = "hello")] HttpRequest req)
    {
        var name = req.Query["name"].FirstOrDefault() ?? "world";
        logger.LogInformation("Saying hello to {Name}", name);
        return new OkObjectResult(new { message = $"Hello, {name}!" });
    }
}
