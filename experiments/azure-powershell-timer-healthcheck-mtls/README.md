# Azure Functions PowerShell — Timer-Triggered mTLS Health Check (Best Practices Example)

A working reference implementation of a common pattern: a PowerShell Azure
Function on a **Linux** App Service plan, triggered every 5 minutes, that
calls an internal app's health endpoint using a **client certificate**
(mutual TLS). This is the pattern that tends to fail on Linux with an opaque
runtime error and something about "certificate" / "root authority" — this
project exists to make that failure diagnosable and to show the actual fix.

---

## TL;DR — what was probably breaking

Two *different* certificate problems get lumped together as "certificate
error" on Linux App Service, and they have different causes and different
fixes:

| Problem | Cause | Fix |
|---|---|---|
| **Client cert won't load** | `WEBSITE_LOAD_CERTIFICATES` app setting missing/wrong, cert not uploaded, or **Linux Consumption plan** (private certs are unreliable there) | Upload cert, set `WEBSITE_LOAD_CERTIFICATES` to its thumbprint, use Elastic Premium/Dedicated instead of Consumption |
| **"unable to get local issuer certificate" / root authority error** | The *server* certificate on the internal app was issued by a **private/internal CA**, and multi-tenant Linux App Service does not let you add certs to the container's OS trust store just by uploading them | A **Startup Command** script that copies the CA cert into `/usr/local/share/ca-certificates` and runs `update-ca-certificates`, then restarts the app |

The second one is almost certainly what you hit — "runtime says error, no
useful detail" is exactly what you get when `Invoke-WebRequest`'s TLS
handshake fails on Linux and the underlying `.NET`/OpenSSL error doesn't get
unwrapped by the Functions host's default error surfacing. This project's
`HealthCheck` module explicitly unwraps and classifies that error (see
[`Modules/HealthCheck/HealthCheck.psm1`](Modules/HealthCheck/HealthCheck.psm1)
→ `Get-CertificateChainDiagnostics`) instead of letting you stare at a bare
"the SSL connection could not be established" message.

Multi-tenant Linux App Service **cannot** be told to trust an arbitrary
private CA through the portal — uploading a CA cert as a "public
certificate" only stages the file in the container; it does not add it to
the OS trust store. The only supported fixes on multi-tenant App Service are
a startup script (what this repo does) or moving to an App Service
Environment v3, which has first-class private-CA trust support.

---

## Project layout

```
├── host.json                  # retry policy, App Insights sampling, timeout
├── profile.ps1                # deliberately empty — see comments
├── requirements.psd1          # deliberately empty — no Az module needed
├── local.settings.json.example
├── startup.sh                  # the actual root-CA trust fix — deployed as
│                               # part of the function package, not via Kudu
├── HealthCheckTimer/
│   ├── function.json          # timer schedule: 0 */5 * * * *
│   └── run.ps1                # entry point — thin, delegates to the module
├── Modules/HealthCheck/
│   ├── HealthCheck.psd1
│   └── HealthCheck.psm1       # Get-ClientCertificate, Invoke-HealthCheckRequest,
│                               # Get-CertificateChainDiagnostics
└── infra/
    ├── main.bicep              # EP1 Linux plan, Function App, Key Vault-sourced cert
    └── deploy.sh                 # infra deploy + code publish + diagnostic settings
```

Everything above is reachable through the ARM control plane / SCM deployment
API — `func azure functionapp publish`, `az deployment group create`, `az
monitor diagnostic-settings create`. Nothing in this project depends on the
interactive Kudu site (`<app>.scm.azurewebsites.net`) being reachable. That
matters on locked-down App Services (access restrictions, private endpoint,
or a corporate egress proxy that just doesn't allow `*.scm.azurewebsites.net`)
— see "Getting logs without Kudu" below.

---

## Why this design

- **Elastic Premium (EP1), not Consumption.** Private certificates
  (`WEBSITE_LOAD_CERTIFICATES`) and custom Startup Commands are unreliable
  on Linux Consumption (Kudu isn't available on that SKU, and there are open
  reports of certs simply not appearing). If you were on Consumption, that
  alone may explain "runtime error" with no clear cause. Premium also avoids
  cold-start variance mattering on a tight 5-minute schedule and supports
  VNet integration if the internal app is only reachable privately.
- **Certificate delivered via Key Vault, not an app setting.** The client
  cert (private key included) is imported into Key Vault, then bound to the
  Function App as a Private Key Certificate (`Microsoft.Web/certificates`
  with a `keyVaultId`/`keyVaultSecretName`). Only the **thumbprint** — not
  the key material — ends up in `WEBSITE_LOAD_CERTIFICATES`. Nothing secret
  is ever in source control or plain app settings.
- **No Az module import.** This function doesn't call ARM, so
  `requirements.psd1` stays empty — importing `Az` costs real cold-start
  time for no benefit here. Only add it if a function genuinely needs an Az
  cmdlet.
- **Bounded, explicit retries at two levels, not stacked unboundedly.**
  `Invoke-HealthCheckRequest` retries the HTTP call itself (default: 2
  attempts, exponential backoff + jitter) for transient network blips.
  `host.json`'s top-level `retry` policy retries the *whole timer
  invocation* (3 attempts) if it still throws — e.g. after a startup script
  hasn't run yet post-deploy. Both are intentionally small; a timer that
  fires every 5 minutes doesn't need aggressive retrying, and a naive nested
  retry (N inner × M outer) can turn one real outage into dozens of calls
  against the internal app.
- **Errors are unwrapped, not just re-thrown.** The default behavior of
  letting `Invoke-WebRequest`'s exception bubble to the Functions host gives
  you a single generic line in the log. `Get-CertificateChainDiagnostics`
  walks the full `InnerException` chain and matches known TLS failure
  signatures to a specific, actionable cause (see the module for the exact
  patterns it checks).
- **`useMonitor: true`** on the timer binding persists trigger execution
  history, so `$Timer.IsPastDue` is meaningful — you can tell whether a
  5-minute check was actually skipped (e.g. app was restarting) versus just
  running normally.

---

## The root-CA trust fix, in detail

1. Export the internal CA's **root** and (if applicable) **intermediate/issuing**
   certificates as `.cer` (DER) files — not the server's own leaf certificate.
2. Upload each as a "Public Certificate" on the Function App (Configuration
   → General settings → Public Certificates, or via `az webapp config ssl
   upload`), and add its thumbprint to `WEBSITE_LOAD_CERTIFICATES` (comma
   separated alongside the client cert thumbprint — `main.bicep` wires the
   client cert thumbprint in for you; add the CA thumbprints via the
   `internalCaThumbprints` parameter).
3. `WEBSITE_LOAD_CERTIFICATES` now stages those CA cert files under
   `/var/ssl/certs` inside the container — but that's just a file on disk,
   **not yet trusted** by anything.
4. `startup.sh`, set as the app's **Startup Command**
   (`bash /home/site/wwwroot/startup.sh` in `main.bicep`), runs on every
   container start: it converts each staged CA cert to PEM, drops it in
   `/usr/local/share/ca-certificates/`, and runs `update-ca-certificates`.
   That's what actually makes OpenSSL (and therefore PowerShell 7's
   `Invoke-WebRequest`) trust certificates issued by that CA.
5. Because this must be reapplied on every fresh container instance, the
   script needs to live somewhere durable. Rather than pushing it separately
   via Kudu's VFS API, it's checked into the repo at the project root and
   deployed as an ordinary part of the function code package — `func azure
   functionapp publish` / zip-deploy lands it at
   `/home/site/wwwroot/startup.sh`, which is on the same persistent share
   Kudu would have written to, without needing the SCM site reachable at all.
6. **Restart the app** after the first deploy of the cert + startup command,
   and after rotating the CA. A running container that already initialized
   its trust store won't pick up changes until it restarts.

If you'd rather not manage a startup script, the supported alternative is
hosting on an **App Service Environment v3**, which exposes a real "Trusted
Root Store" feature for Linux and Windows apps — worth it if you already
need ASE for networking reasons, overkill if this is the only reason.

---

## Deploying

```bash
# 1. Import the client cert into Key Vault first (not scripted here —
#    depends on how your PKI issues certs):
az keyvault certificate import \
  --vault-name <your-kv> --name <cert-name> --file client.pfx --password <pfx-password>

# 2. Deploy infra, publish code (includes startup.sh), restart, wire logs:
export RESOURCE_GROUP=rg-healthcheck-demo
export LOCATION=uksouth
export NAME_PREFIX=hcdemo01
export KEY_VAULT_RESOURCE_ID=/subscriptions/.../resourceGroups/.../providers/Microsoft.KeyVault/vaults/<your-kv>
export CLIENT_CERT_KV_NAME=<cert-name>
export HEALTHCHECK_TARGET_URL=https://internal-app.contoso.internal/health
export INTERNAL_CA_THUMBPRINTS=<root-ca-thumbprint>,<issuing-ca-thumbprint>
export LOG_ANALYTICS_WORKSPACE_ID=/subscriptions/.../resourceGroups/.../providers/Microsoft.OperationalInsights/workspaces/<your-law>

./infra/deploy.sh
```

`LOG_ANALYTICS_WORKSPACE_ID` is optional but strongly recommended if Kudu
isn't reachable from where you sit — see the next section.

`main.bicep` grants nothing on the Key Vault itself — grant the deploying
principal (and, if you later move secret retrieval into the function,
the Function App's managed identity) `get`/`list` on certificates/secrets
before running this.

---

## Getting logs without Kudu

If the SCM/Kudu site (`<app>.scm.azurewebsites.net`) is blocked for you —
access restrictions, a private endpoint on the App Service, or a corporate
proxy that just doesn't allow that hostname — then everything that rides on
it stops working too, not just the Kudu UI: `az webapp log tail` / `log
stream`, downloading `LogFiles` over Kudu VFS, and the Kudu console all use
the same SCM endpoint. This is a separate problem from certificate trust,
and it's worth fixing independently because without it you're debugging the
cert issue blind.

The fix is to stop depending on the app's own network path for logs at all
and use the two things that are emitted at the **platform** level instead —
these are collected by Azure Monitor outside the app's sandbox, so they're
unaffected by inbound access restrictions on the app or its SCM site:

1. **`FunctionAppLogs`** — the unified log category for Linux Function Apps.
   Every `Write-Information` / `Write-Warning` / `Write-Error` / thrown
   exception in `run.ps1` ends up here (and in Application Insights, if
   configured — `main.bicep` wires `APPLICATIONINSIGHTS_CONNECTION_STRING`
   in already).
2. **`AppServiceConsoleLogs`** — raw container stdout/stderr, which is the
   only place you can see whether the **Startup Command itself** ran and
   succeeded. This is the category that replaces "open the Kudu console and
   watch it happen": if `startup.sh` errors, or `update-ca-certificates`
   fails, or the app command line is wrong, it shows up here even when
   nothing else does.

Wire both up via a Diagnostic Setting on the Function App resource (done
automatically by `deploy.sh` if `LOG_ANALYTICS_WORKSPACE_ID` is set):

```bash
az monitor diagnostic-settings create \
  --name hcdemo01-diag \
  --resource <function-app-name> \
  --resource-group <rg> \
  --resource-type "Microsoft.Web/sites" \
  --workspace <log-analytics-workspace-resource-id> \
  --logs '[{"category":"FunctionAppLogs","enabled":true},{"category":"AppServiceConsoleLogs","enabled":true}]'
```

Then query in the Log Analytics workspace (or Application Insights, for
`traces`/`exceptions` specifically) instead of tailing logs:

```kusto
AppServiceConsoleLogs
| where TimeGenerated > ago(1h)
| order by TimeGenerated desc

FunctionAppLogs
| where TimeGenerated > ago(1h)
| where Message has "HealthCheck"
| order by TimeGenerated desc
```

One thing this doesn't fix: if outbound egress from the app itself is also
restricted (e.g. VNet-integrated with forced tunneling), confirm the
Application Insights ingestion endpoint is on your allowed list too — that's
a separate outbound path from the inbound SCM access this section is about.

---

## Local development

`func start` runs on your workstation, where PowerShell 7 typically has no
`Cert:\CurrentUser\My` provider at all (that's an Azure Functions/App
Service shim, not a PowerShell feature). `Get-ClientCertificate` detects
this and falls back to loading a PFX from disk via `LOCAL_DEV_PFX_PATH` /
`LOCAL_DEV_PFX_PASSWORD` in `local.settings.json` — copy
`local.settings.json.example`, point those two settings at a **local-only**
dev certificate, and never commit either the file or the password.

```bash
cp local.settings.json.example local.settings.json
mkdir -p local-certs   # gitignored
func start
```

---

## Troubleshooting matrix

| Symptom in logs | Cause | Where to look |
|---|---|---|
| `Cannot find certificate with thumbprint …` | `WEBSITE_LOAD_CERTIFICATES` missing this thumbprint, cert not uploaded, or wrong plan tier | `Get-ClientCertificate` error message; Configuration → Application settings |
| `unable to get local issuer certificate` / `PKIX path building failed` | Internal CA not trusted by the container's OS trust store | `startup.sh` didn't run or hasn't been restarted since deploy — check `AppServiceConsoleLogs` |
| `RemoteCertificateNameMismatch` | Health check URL doesn't match the server cert's SAN | `HEALTHCHECK_TARGET_URL` app setting |
| Handshake fails, server closes connection immediately | Server rejected the client cert (wrong issuer, not in server's trusted-client list) | Confirm the client cert's issuing CA is trusted by the *server*, not just the function app |
| Timer never fires / `IsPastDue: true` | App was restarting or scaled down at the scheduled time | `useMonitor: true` in `function.json` keeps schedule history for this |
| `az webapp log tail` hangs / times out, Kudu 403s or won't load | SCM/Kudu site is network-restricted separately from the main site | See "Getting logs without Kudu" — use Diagnostic Settings → Log Analytics instead |

---

## Sources

- [Use a TLS/SSL certificate in your code — Microsoft Learn](https://learn.microsoft.com/en-us/azure/app-service/configure-ssl-certificate-in-code)
- [Root CA on App Service — Azure App Service team guide](https://azure.github.io/AppService/2021/06/22/Root-CA-on-App-Service-Guide.html)
- [Certificates in App Service Environment — Microsoft Learn](https://learn.microsoft.com/en-us/azure/app-service/environment/overview-certificates)
- [Using certs in code & trusting private CAs on App Service Linux — Patrick O'Brien](https://www.patrickob.com/2023/02/08/using-certs-in-code-trusting-private-cas-on-app-service-linux/)
- [Consumption plan Linux certificate loading issue — Azure/azure-functions-host#6286](https://github.com/Azure/azure-functions-host/issues/6286)
- [Set up access restrictions (SCM/Kudu site restrictions) — Microsoft Learn](https://learn.microsoft.com/en-us/azure/app-service/app-service-ip-restrictions)
- [Monitor Azure Functions (FunctionAppLogs / diagnostic settings) — Microsoft Learn](https://learn.microsoft.com/en-us/azure/azure-functions/monitor-functions)
