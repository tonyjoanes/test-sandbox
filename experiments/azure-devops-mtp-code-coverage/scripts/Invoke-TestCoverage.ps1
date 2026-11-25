#Requires -Version 7.2
<#
.SYNOPSIS
    The one command to run locally: build, run every MTP test project with coverage, gate, and report.

.DESCRIPTION
    1. dotnet build            the solution, once
    2. dotnet test --coverage  every test project; each writes its own .cobertura.xml + .trx
    3. Test-CoverageGate.ps1   each project against its own minimum (from its testconfig.json)
    4. New-CoverageReport.ps1  merges all projects' files into one HTML report

    The Azure DevOps pipeline (pipelines/azure-pipelines.pwsh.yml) runs the same steps, so a green
    run here should be a green run there.

.EXAMPLE
    ./scripts/Invoke-TestCoverage.ps1

.EXAMPLE
    ./scripts/Invoke-TestCoverage.ps1 -Project tests/Orders.Domain.Tests   # one project only
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',

    [string] $ResultsDirectory = (Join-Path $PSScriptRoot '..' 'TestResults'),

    # Run a single test project instead of the whole solution.
    [string] $Project,

    [switch] $NoBuild,

    # Extra arguments passed straight to the test apps, e.g. '--filter', 'TestCategory!=Slow'.
    [string[]] $TestArguments = @()
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
$env:DOTNET_NOLOGO = 'true'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = 'true'
$env:TESTINGPLATFORM_TELEMETRY_OPTOUT = '1'

Push-Location $root
try {
    # Old reports would be merged into the new one, so start clean.
    if (Test-Path $ResultsDirectory) { Remove-Item $ResultsDirectory -Recurse -Force }

    if (-not $NoBuild) {
        dotnet build Orders.slnx --configuration $Configuration
        if ($LASTEXITCODE) { throw "Build failed ($LASTEXITCODE)." }
    }

    # global.json sets "test": { "runner": "Microsoft.Testing.Platform" }, so:
    #   * the target is --solution / --project, not a positional path
    #   * everything else goes to the MTP test apps: --coverage, --report-trx, --filter ...
    #   * VSTest switches (--collect, --logger, --settings for runsettings) do NOT apply
    $target = $Project ? @('--project', $Project) : @('--solution', 'Orders.slnx')
    $testArgs = @(
        $target
        '--configuration', $Configuration
        '--no-build'
        '--no-progress'
        '--results-directory', $ResultsDirectory
        '--coverage'                       # each project's testconfig.json says what/how
        '--report-trx'
        '--report-trx-filename', '{asm}.trx'
    ) + $TestArguments

    Write-Host "dotnet test $($testArgs -join ' ')" -ForegroundColor Cyan
    dotnet test @testArgs
    $testExit = $LASTEXITCODE

    # Gate and report even if a test failed: the report helps explain the failure.
    $gateDir = $Project ? $Project : 'tests'
    & (Join-Path $PSScriptRoot 'Test-CoverageGate.ps1') -ResultsDirectory $ResultsDirectory -TestsDirectory $gateDir
    $gateExit = $LASTEXITCODE

    & (Join-Path $PSScriptRoot 'New-CoverageReport.ps1') -ResultsDirectory $ResultsDirectory

    if ($testExit) { Write-Host "dotnet test failed with exit code $testExit (https://aka.ms/testingplatform/exitcodes)." -ForegroundColor Red }
    if ($gateExit) { Write-Host 'Coverage gate failed.' -ForegroundColor Red }
    exit ([int] [bool] ($testExit -or $gateExit))
}
finally {
    Pop-Location
}
