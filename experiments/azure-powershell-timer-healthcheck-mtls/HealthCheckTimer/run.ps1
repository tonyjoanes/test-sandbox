param($Timer)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '..' 'Modules' 'HealthCheck' 'HealthCheck.psd1') -Force

if ($Timer.IsPastDue) {
    Write-Warning 'Timer trigger is running late — a previous invocation may have been skipped (host restart, deployment, or an earlier run that exceeded functionTimeout).'
}

$targetUrl = $env:HEALTHCHECK_TARGET_URL
$thumbprint = $env:CLIENT_CERT_THUMBPRINT
$timeoutSeconds = [int]($env:HEALTHCHECK_TIMEOUT_SECONDS ?? 10)
$maxAttempts = [int]($env:HEALTHCHECK_MAX_ATTEMPTS ?? 2)

if (-not $targetUrl) { throw "App setting HEALTHCHECK_TARGET_URL is not set." }
if (-not $thumbprint) { throw "App setting CLIENT_CERT_THUMBPRINT is not set." }

# Structured, single-line start-of-run log — easy to query in Application
# Insights (traces | where message startswith "HealthCheck:").
Write-Information "HealthCheck: starting target=$targetUrl thumbprint=$thumbprint timeoutSeconds=$timeoutSeconds"

try {
    $certificate = Get-ClientCertificate `
        -Thumbprint $thumbprint `
        -LocalPfxPath $env:LOCAL_DEV_PFX_PATH `
        -LocalPfxPassword $env:LOCAL_DEV_PFX_PASSWORD

    $result = Invoke-HealthCheckRequest `
        -Uri $targetUrl `
        -Certificate $certificate `
        -TimeoutSeconds $timeoutSeconds `
        -MaxAttempts $maxAttempts

    Write-Information "HealthCheck: success statusCode=$($result.StatusCode) attempts=$($result.Attempts)"
}
catch {
    # Re-throw after logging so:
    #  - Application Insights captures a failed invocation (visible in
    #    Function App > Monitor, and alertable via a metric alert on
    #    "Failed function executions").
    #  - host.json's top-level "retry" policy gets a chance to retry the
    #    whole timer invocation with backoff (see host.json).
    Write-Error "HealthCheck: FAILED target=$targetUrl thumbprint=$thumbprint error=$($_.Exception.Message)"
    throw
}
