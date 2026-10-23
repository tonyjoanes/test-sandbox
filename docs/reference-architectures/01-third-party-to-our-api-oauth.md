# Third party → our API (OAuth client credentials via APIM)

```mermaid
sequenceDiagram
    participant P as Third party
    participant E as Entra ID
    participant A as APIM
    participant B as Backend API (private)
    P->>E: client_credentials (cert or secret), scope=api://our-api/.default
    E-->>P: Access token (short-lived, roles=[orders.read])
    P->>A: GET /orders  Authorization: Bearer <token>
    A->>A: validate-jwt: issuer, audience, expiry, required role
    alt invalid
        A-->>P: 401 / 403
    else valid
        A->>B: Forward + X-Apim-Auth-Context (partner id), via private endpoint
        B-->>A: 200
        A-->>P: 200
    end
```

- Create one Entra app registration per partner, with app roles as scopes. Revoke a partner by removing its role assignment.
- APIM is the enforcement point. The backend accepts traffic from APIM only, using a private endpoint or VNet integration plus access restrictions.
- Policy: [`validate-jwt.xml`](../../examples/auth-patterns-api/apim/policies/validate-jwt.xml)
