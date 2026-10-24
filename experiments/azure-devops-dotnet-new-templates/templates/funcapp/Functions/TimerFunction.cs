using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Contoso.FunctionApp.Functions;

public class TimerFunction(ILogger<TimerFunction> logger)
{
    [Function("Timer")]
    public void Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer)
    {
        logger.LogInformation("Timer fired at {Now:o}. Next run: {Next:o}", DateTimeOffset.UtcNow, timer.ScheduleStatus?.Next);
    }
}
