Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ClientCertificate {
    <#
        Resolves the client certificate used for mTLS to the internal app.

        In Azure (Linux or Windows App Service), the platform exposes any
        certificate whose thumbprint is listed in the WEBSITE_LOAD_CERTIFICATES
        app setting through the PowerShell Cert:\CurrentUser\My provider — this
        works even though a bare PowerShell 7 install on Linux has no
        certificate store provider at all. That's an Azure Functions/App
        Service shim, not a PowerShell feature, which is why it silently does
        nothing if the thumbprint isn't in WEBSITE_LOAD_CERTIFICATES or the
        plan doesn't support private certificates (see README "Known
        limitation" section — Linux Consumption is unreliable here).

        Locally (func start on a dev workstation), that provider usually
        doesn't exist, so this falls back to loading a PFX from disk for
        local testing only.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Thumbprint,

        [string]$LocalPfxPath,
        [string]$LocalPfxPassword
    )

    $certStorePath = "Cert:\CurrentUser\My\$Thumbprint"

    if (Test-Path -LiteralPath 'Cert:\CurrentUser\My' -ErrorAction SilentlyContinue) {
        $cert = Get-ChildItem -LiteralPath $certStorePath -ErrorAction SilentlyContinue
        if ($cert) {
            Write-Information "Loaded client certificate '$Thumbprint' from Cert:\CurrentUser\My (platform certificate store)."
            return $cert
        }

        throw (
            "Certificate store 'Cert:\CurrentUser\My' exists but does not contain thumbprint '$Thumbprint'. " +
            "In Azure this means WEBSITE_LOAD_CERTIFICATES either isn't set to this thumbprint (or '*'), " +
            "or the certificate isn't uploaded to this app, or the App Service plan doesn't support " +
            "private certificates (Linux Consumption has known limitations here — use Elastic Premium or " +
            "a Dedicated/App Service Environment plan). Check Configuration > Application settings > " +
            "WEBSITE_LOAD_CERTIFICATES and Configuration > General settings > Private Key Certificates."
        )
    }

    # No Cert: provider at all — we're almost certainly running locally via `func start`.
    if (-not $LocalPfxPath) {
        throw (
            "No 'Cert:\CurrentUser\My' provider is available in this PowerShell session, and no " +
            "LOCAL_DEV_PFX_PATH was supplied for local fallback. This is expected outside of Azure " +
            "App Service/Functions. Set LOCAL_DEV_PFX_PATH and LOCAL_DEV_PFX_PASSWORD in " +
            "local.settings.json for local testing (never commit the PFX or password)."
        )
    }

    $resolvedPath = Resolve-Path -LiteralPath $LocalPfxPath -ErrorAction SilentlyContinue
    if (-not $resolvedPath) {
        throw "LOCAL_DEV_PFX_PATH is set to '$LocalPfxPath' but no file exists there."
    }

    Write-Information "No platform certificate store detected — loading local dev PFX from '$resolvedPath'."
    return [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $resolvedPath.Path,
        $LocalPfxPassword,
        [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
    )
}

function Get-CertificateChainDiagnostics {
    <#
        Walks an exception's InnerException chain and, if it finds a TLS/
        certificate-related failure, returns actionable guidance instead of
        the generic message Invoke-WebRequest surfaces. This is the piece
        that turns "the runtime says error" into a specific, fixable cause.
    #>
    param(
        [Parameter(Mandatory)]
        [System.Exception]$Exception
    )

    $messages = New-Object System.Collections.Generic.List[string]
    $current = $Exception
    while ($current) {
        $messages.Add("$($current.GetType().FullName): $($current.Message)")
        $current = $current.InnerException
    }
    $chainText = $messages -join ' | '

    $guidance = switch -Regex ($chainText) {
        'unable to get local issuer certificate|PKIX path building failed|RemoteCertificateChainErrors|self[- ]signed certificate in certificate chain' {
            "Likely cause: the SERVER certificate presented by the internal endpoint was issued by a " +
            "private/internal CA that the Function App's Linux container does not trust. Multi-tenant " +
            "Linux App Service does not let you add certs to the OS trust store via WEBSITE_LOAD_CERTIFICATES " +
            "alone — you need a startup script that runs update-ca-certificates (see infra/startup.sh and " +
            "README 'Root CA trust' section), or host on an App Service Environment v3 with a custom trusted " +
            "root store."
            break
        }
        'RemoteCertificateNameMismatch|does not match the hostname' {
            "Likely cause: the server certificate's subject/SAN doesn't match the hostname you're calling. " +
            "Check HEALTHCHECK_TARGET_URL matches exactly what the certificate was issued for."
            break
        }
        'certificate could not be validated for reason: RemoteCertificateNotAvailable|The remote party closed the connection' {
            "Likely cause: mTLS handshake failed on the CLIENT side — the server rejected or never received " +
            "a client certificate. Confirm Get-ClientCertificate actually returned a certificate with a " +
            "private key (Cert.HasPrivateKey) and that the server's trusted-client-CA list includes the " +
            "issuer of your client cert."
            break
        }
        'Cannot find certificate with thumbprint|no certificate.*private key' {
            "Likely cause: the client certificate itself failed to load. See the error above from " +
            "Get-ClientCertificate — check WEBSITE_LOAD_CERTIFICATES and the App Service plan tier."
            break
        }
        default {
            "No known certificate-chain signature matched. Full exception chain: $chainText"
        }
    }

    [pscustomobject]@{
        ExceptionChain = $chainText
        Guidance       = $guidance
    }
}

function Invoke-HealthCheckRequest {
    <#
        Calls the internal health endpoint using mTLS, with bounded retries
        and exponential backoff + jitter. Kept intentionally small (2
        attempts by default) because host.json already applies its own
        retry policy to the whole timer invocation — stacking two large
        retry loops turns one failure into dozens of outbound calls.
    #>
    param(
        [Parameter(Mandatory)]
        [uri]$Uri,

        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,

        [int]$TimeoutSeconds = 10,
        [int]$MaxAttempts = 2,
        [int]$InitialBackoffSeconds = 2
    )

    if (-not $Certificate.HasPrivateKey) {
        throw "Client certificate '$($Certificate.Thumbprint)' was loaded but has no private key — cannot use it for mTLS."
    }

    $attempt = 0
    $lastError = $null

    while ($attempt -lt $MaxAttempts) {
        $attempt++
        try {
            Write-Information "Health check attempt $attempt/$MaxAttempts -> $Uri"

            $response = Invoke-WebRequest `
                -Uri $Uri `
                -Method Get `
                -Certificate $Certificate `
                -TimeoutSec $TimeoutSeconds `
                -Headers @{ 'Accept' = 'application/json' }

            if ($response.StatusCode -ne 200) {
                throw "Health endpoint returned HTTP $($response.StatusCode), expected 200."
            }

            return [pscustomobject]@{
                Success    = $true
                StatusCode = $response.StatusCode
                Attempts   = $attempt
                Body       = $response.Content
            }
        }
        catch {
            $lastError = $_
            $diagnostics = Get-CertificateChainDiagnostics -Exception $_.Exception
            Write-Warning "Attempt $attempt/$MaxAttempts failed: $($diagnostics.ExceptionChain)"
            Write-Warning "Diagnosis: $($diagnostics.Guidance)"

            if ($attempt -lt $MaxAttempts) {
                $backoff = $InitialBackoffSeconds * [math]::Pow(2, $attempt - 1)
                $jitter = Get-Random -Minimum 0.0 -Maximum ($backoff * 0.25)
                Start-Sleep -Seconds ($backoff + $jitter)
            }
        }
    }

    throw $lastError
}

Export-ModuleMember -Function Get-ClientCertificate, Get-CertificateChainDiagnostics, Invoke-HealthCheckRequest
