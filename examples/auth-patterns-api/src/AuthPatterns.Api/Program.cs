using AuthPatterns.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;

var builder = WebApplication.CreateBuilder(args);

// Backend trust: APIM calls us with a token from its Managed Identity (authentication-managed-identity).
// We accept only tokens for our audience that were issued to APIM's identity. The network is also locked
// down to APIM (private endpoint / access restrictions), so clients cannot reach this service directly.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.Authority = builder.Configuration["BackendAuth:Authority"];
        o.Audience = builder.Configuration["BackendAuth:Audience"];
    });
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("FromApim", p => p.RequireAuthenticatedUser().RequireAssertion(ctx =>
    {
        var apimId = builder.Configuration["BackendAuth:ApimPrincipalId"];
        var caller = ctx.User.FindFirst("azp")?.Value ?? ctx.User.FindFirst("appid")?.Value;
        return string.IsNullOrEmpty(apimId) || caller == apimId;
    }));
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => new HmacVerifier(
    [builder.Configuration["Webhook:PrimarySecret"] ?? "", builder.Configuration["Webhook:SecondarySecret"] ?? ""],
    sp.GetRequiredService<TimeProvider>()));

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

// API-to-API endpoint. APIM's validate-jwt checks the partner token and its "orders.read" role.
// Defence in depth: the backend also checks the role header that APIM set.
app.MapGet("/orders", (HttpRequest req) =>
{
    var roles = req.Headers["X-Apim-Caller-Roles"].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (!roles.Contains("orders.read")) return Results.Forbid();
    return Results.Ok(new[] { new { id = 1, status = "shipped" } });
}).RequireAuthorization("FromApim");

// Webhook endpoint. APIM verifies the HMAC. If secrets are configured here, the backend re-verifies as well.
app.MapPost("/webhooks/orders", async (HttpRequest req, HmacVerifier verifier, IConfiguration cfg) =>
{
    if (req.Headers["X-Apim-Webhook-Verified"] != "true") return Results.Unauthorized();
    using var reader = new StreamReader(req.Body);
    var body = await reader.ReadToEndAsync();
    if (!string.IsNullOrEmpty(cfg["Webhook:PrimarySecret"]) &&
        !verifier.Verify(req.Headers["X-Timestamp"], req.Headers["X-Signature"], body))
        return Results.Unauthorized();
    return Results.Accepted();
}).RequireAuthorization("FromApim");

app.Run();

public partial class Program;
