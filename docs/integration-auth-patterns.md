# Integration authentication patterns

**Audience:** Head of Platform, Cloud Architect  
**Status:** Proposal for agreement

## Summary

Use two patterns across the estate. Enforce both at the edge in **Azure API Management (APIM)**.

- **OAuth for service-to-service calls**: third parties calling our APIs, and us calling theirs where they support it.
- **HMAC-signed webhooks for inbound** event notifications from third parties.

> **Simple rule:** if they call our API, use OAuth. If we receive their event notifications, use HMAC.

## 1. OAuth for third-party API-to-API calls

- **Flow:** OAuth 2.0 client credentials. Each partner gets its own Entra ID app registration.
- **Issuer:** Entra ID.
- **Tokens:** short-lived, scoped and revocable, with a proper lifecycle. No long-lived shared secrets. Use certificate credentials where the partner can support them.
- **Validation:** done centrally in APIM with `validate-jwt`, checking the issuer, audience, expiry and required scope (app role).
- **Scopes:** one scope per partner and capability, so you can revoke one partner without affecting the others.

Reference: [Third party → our API via APIM](reference-architectures/01-third-party-to-our-api-oauth.md)

## 2. HMAC-signed webhooks for inbound

- **Why:** there is no token round trip, and the signature check per payload is cheap and fast.
- **Secrets:** one secret per sender, stored in Key Vault and surfaced to APIM as Key Vault-backed named values.
- **Signature:** `HMAC-SHA256(secret, "{timestamp}.{body}")`. Including the timestamp in the signed content stops replayed requests. Reject anything outside a ±5 minute window.
- **Comparison:** constant-time only.
- **Rotation:** support two active secrets (primary and secondary) during rollover. Accept either one, then retire the old secret once the sender confirms the cutover.
- **Optional:** an IP allowlist for each sender.

Reference: [Third-party webhook with HMAC](reference-architectures/02-third-party-webhook-hmac.md)

## 3. Tradeoff

| | OAuth | HMAC |
|---|---|---|
| Credential lifetime | Tokens expire on their own (minutes) | Secrets live until someone rotates them manually |
| Per-call cost | Token fetch (cached) plus JWT validation | One hash per payload |
| Revocation | Disable the app registration or remove the role | Rotate or delete the secret |
| Operational risk | Low, because expiry is built in | Rotation discipline is essential, so it needs automation and ownership |

## 4. Standard patterns (based on Jon's proposal)

| Scenario | Standard pattern |
|---|---|
| Third party consuming our APIs | OAuth 2.0 client credentials and JWT. Tokens come from Entra ID, are validated in APIM, with a scope per partner |
| Our services consuming third-party APIs | OAuth 2.0 where the third party supports it. **Fallback:** API keys or mTLS, stored in Key Vault and rotated, never kept in config |
| Third-party webhooks into our platform | HMAC signatures verified in APIM, with a timestamp and replay window, constant-time comparison, and a per-sender secret in Key Vault |
| Internal service-to-service | **Managed Identity by default**, with OAuth as the exception |

References: [Internal service-to-service with Managed Identity](reference-architectures/03-internal-managed-identity.md) · [Our service → third-party API](reference-architectures/04-our-service-to-third-party.md)

## 5. Banned patterns

- Basic auth
- Shared static API keys that are never rotated
- Long-lived, self-signed JWTs
- Secrets in pipelines, app config or source control
- Unsigned webhooks

## 6. Governance note

The GitHub Enterprise move is being driven by contractors. Platform should shape the tooling choices, such as secret scanning, OIDC federation for pipelines and repo templates, before the contractors lock in their own defaults.

## Worked example

[`examples/auth-patterns-api`](../examples/auth-patterns-api/) is a C# minimal API that sits behind APIM:
- [APIM policies](../examples/auth-patterns-api/apim/policies/): `validate-jwt` for API-to-API calls, and HMAC verification for webhooks
- [Bicep](../examples/auth-patterns-api/apim/bicep/main.bicep) for the APIM API, operations and named values
- a backend that trusts APIM, plus light defence-in-depth checks
- tests

Nothing is deployed.
