# Our service → third-party API (secrets in Key Vault)

```mermaid
flowchart LR
    S[Our service] -- Managed Identity --> KV[(Key Vault)]
    KV -- client secret / cert / API key --> S
    S -- "OAuth 2.0 client credentials (preferred)" --> T[Third-party token endpoint]
    S -- "Bearer token, or fallback: API key / mTLS" --> API[Third-party API]
    R[Rotation job / Event Grid SecretNearExpiry] --> KV
```

- Prefer OAuth 2.0 where the third party supports it. Otherwise use API keys or mTLS certificates.
- Keep credentials in Key Vault only, and read them at runtime with Managed Identity. Never put them in pipelines, config or source.
- Rotate on a schedule, and use Key Vault near-expiry events to alert the owning team.
