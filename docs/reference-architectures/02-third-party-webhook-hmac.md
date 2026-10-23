# Third-party webhook → our platform (HMAC verified in APIM)

```mermaid
sequenceDiagram
    participant S as Sender
    participant A as APIM
    participant KV as Key Vault
    participant B as Backend webhook handler (private)
    KV-->>A: Named values webhook-secret-primary / -secondary (Key Vault-backed, refreshed)
    S->>A: POST /webhooks/orders  X-Timestamp, X-Signature: sha256=HMAC(secret, ts + "." + body)
    A->>A: Reject if |now - ts| > 300s (replay window)
    A->>A: Compute HMAC with primary and secondary, constant-time compare
    alt no match / stale
        A-->>S: 401
    else match
        A->>B: Forward (backend trusts APIM only)
        B-->>A: 202
        A-->>S: 202
    end
```

- Use one secret per sender. Rotate by putting the new secret in the secondary slot, cutting the sender over, promoting it to primary, then clearing the old one.
- Optional extras: an IP allowlist, and deduplicating on the event ID in the backend.
- Policy: [`verify-webhook-hmac.xml`](../../examples/auth-patterns-api/apim/policies/verify-webhook-hmac.xml)
