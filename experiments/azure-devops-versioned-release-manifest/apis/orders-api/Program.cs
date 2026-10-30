using System.Reflection;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// The version stamped in by the build pipeline. The release pipeline reads this endpoint to
// decide whether an API is already at the manifest's version (and skips it if so), and again
// after the slot swap to prove the right build went live. The .NET SDK appends "+<commit sha>"
// to the informational version, so strip that.
var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
    .InformationalVersion.Split('+')[0] ?? "unknown";

app.MapGet("/version", () => new { api = "orders-api", version });
app.MapGet("/health", () => Results.Ok());

app.Run();
