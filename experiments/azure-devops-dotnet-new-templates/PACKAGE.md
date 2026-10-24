# Contoso.Platform.Templates

`dotnet new` templates maintained by the Contoso Platform Team.

| Template | Short name | What you get |
|---|---|---|
| Contoso Web API | `contoso-webapi` | ASP.NET Core minimal API (.NET 10) with health checks, ProblemDetails and OpenAPI |
| Contoso Function App | `contoso-func` | Azure Functions isolated worker (.NET 10) with an HTTP trigger and optional timer trigger |

```bash
dotnet new install Contoso.Platform.Templates
dotnet new contoso-webapi -n Acme.Orders.Api
dotnet new contoso-func   -n Acme.Orders.Worker
```

Run `dotnet new contoso-webapi --help` to see each template's options.
