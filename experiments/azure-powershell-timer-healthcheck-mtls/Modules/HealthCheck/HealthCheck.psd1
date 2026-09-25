@{
    RootModule        = 'HealthCheck.psm1'
    ModuleVersion     = '1.0.0'
    GUID              = 'f3b6a6d6-9e6b-4a9b-9c2e-9d6f7a3d2b41'
    Author            = 'Platform Engineering'
    Description       = 'mTLS health-check helper functions for the timer-triggered Function App.'
    PowerShellVersion = '7.2'
    FunctionsToExport = @('Get-ClientCertificate', 'Get-CertificateChainDiagnostics', 'Invoke-HealthCheckRequest')
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()
}
