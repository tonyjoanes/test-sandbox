# Auth patterns example (C#, behind Azure API Management)

This example sits behind APIM, which is the primary enforcement point. The backend trusts APIM and adds a light defence-in-depth check. **Nothing is deployed.**

| Path | What |
|---|---|
| `apim/policies/validate-jwt.xml` | API-to-API calls: checks the Entra issuer, audience, expiry and required role (`orders.read`). Passes the caller's app ID and roles to the backend |
| `apim/policies/verify-webhook-hmac.xml` | Webhooks: HMAC-SHA256 over `{timestamp}.{body}`, a 5 minute replay window, a constant-time compare, and primary/secondary secrets from Key Vault-backed named values |
| `apim/policies/backend-trust.fragment.xml` | APIM calls the backend with its Managed Identity token |
| `apim/bicep/main.bicep` | APIM named values (Key Vault-backed secrets), policy fragment, API, operations and policies |
| `src/AuthPatterns.Api` | Minimal API: `GET /orders` and `POST /webhooks/orders` |
| `tests/AuthPatterns.Api.Tests` | xUnit tests for HMAC verification, rotation, replay, scope checks and APIM-only access |

## How the backend trusts APIM

1. **Identity:** APIM attaches a token from its Managed Identity (`authentication-managed-identity`). The backend validates the JWT (`BackendAuth:Authority` and `BackendAuth:Audience`) and only accepts tokens issued to the APIM principal (`BackendAuth:ApimPrincipalId`).
2. **Network:** the backend is reachable from APIM only, through a private endpoint or VNet integration plus access restrictions.
3. **Defence in depth:** the backend also checks the `X-Apim-Caller-Roles` and `X-Apim-Webhook-Verified` headers. APIM overwrites both, so a client can't spoof them. If `Webhook:*Secret` is configured, the backend re-verifies the HMAC too.

## Build and test

```bash
dotnet test
```

## Signing a test webhook request

```bash
SECRET='dev-secret'            # matches webhook-secret-primary (or -secondary)
BODY='{"event":"order.created","id":"123"}'
TS=$(date +%s)
SIG=$(printf '%s.%s' "$TS" "$BODY" | openssl dgst -sha256 -hmac "$SECRET" -hex | sed 's/^.* //')

curl -X POST "https://<apim-name>.azure-api.net/auth-patterns/webhooks/orders" \
  -H "Content-Type: application/json" \
  -H "X-Timestamp: $TS" \
  -H "X-Signature: sha256=$SIG" \
  --data "$BODY"
```

The same request with a different body, or a timestamp more than 5 minutes old, returns `401`.

## Calling the API-to-API endpoint

```bash
TOKEN=$(az account get-access-token --resource api://<our-api> --query accessToken -o tsv)  # or client-credentials as the partner app
curl -H "Authorization: Bearer $TOKEN" "https://<apim-name>.azure-api.net/auth-patterns/orders"
```

## Secret rotation

1. Write the new secret to `webhook-secret-secondary` in Key Vault. APIM picks it up when it refreshes its named values.
2. Have the sender switch to the new secret. Both secrets verify during the switch.
3. Promote the new secret to `webhook-secret-primary`, then clear the secondary.

See [the main doc](../../docs/integration-auth-patterns.md) and [the reference architectures](../../docs/reference-architectures/README.md).
