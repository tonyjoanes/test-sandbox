# Internal service-to-service (Managed Identity)

```mermaid
sequenceDiagram
    participant C as Service A (Container App / App Service)
    participant MI as Managed Identity endpoint
    participant E as Entra ID
    participant A as APIM (internal)
    participant B as Service B
    C->>MI: Get token for api://service-b
    MI->>E: (platform-managed, no secrets)
    E-->>C: Access token (roles=[service-b.write])
    C->>A: Call with Bearer token
    A->>A: validate-jwt (issuer, audience, role)
    A->>B: Forward
```

- Managed Identity is the default, because there are no secrets to store or rotate. Grant app roles to the calling identity.
- Use OAuth with an app registration only when Managed Identity isn't available, for example across tenants or outside Azure.
