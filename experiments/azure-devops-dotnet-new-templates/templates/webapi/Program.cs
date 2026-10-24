//#if (IncludeSampleEndpoints)
using Contoso.WebApi.Endpoints;

//#endif
var builder = WebApplication.CreateBuilder(args);

// Consistent RFC 7807 error bodies for every failure, including unhandled exceptions.
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
//#if (EnableOpenApi)
builder.Services.AddOpenApi();
//#endif
//#if (IncludeSampleEndpoints)
builder.Services.AddSingleton<ItemStore>();
//#endif

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

//#if (EnableOpenApi)
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

//#endif
app.MapHealthChecks("/health");
//#if (IncludeSampleEndpoints)
app.MapItemEndpoints();
//#endif

app.Run();
