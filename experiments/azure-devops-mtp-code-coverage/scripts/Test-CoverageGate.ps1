#Requires -Version 7.2
<#
.SYNOPSIS
    Per-test-project coverage gate: fails if any test project is under its own minimum.

.DESCRIPTION
    Microsoft.Testing.Extensions.CodeCoverage collects coverage but has no "fail under N%" option,
    so this script does that one job.

    For every tests/<Project>/testconfig.json it reads:
      codeCoverage.Configuration.CoverageFileName  -> which Cobertura file in -ResultsDirectory is this project's
      coverageGate.minimumLineRate / minimumBranchRate -> the bar that file has to clear (percent)

    The thresholds live next to the coverage filters they apply to: one file per project says what is
    measured AND how much of it must be covered. MTP ignores the `coverageGate` section; only this
    script reads it.

    On Azure DevOps (TF_BUILD set) the table is attached to the run's Extensions tab and every failure
    is logged as a pipeline error. On GitHub Actions pass -SummaryPath $env:GITHUB_STEP_SUMMARY.

.EXAMPLE
    ./scripts/Test-CoverageGate.ps1 -ResultsDirectory ./TestResults

.EXAMPLE
    # Gate a single project (e.g. one job of a matrix pipeline)
    ./scripts/Test-CoverageGate.ps1 -ResultsDirectory $env:AGENT_TEMPDIRECTORY/TestResults -TestsDirectory tests/Orders.Api.Tests
#>
[CmdletBinding()]
param(
    # Folder holding the *.cobertura.xml files from `dotnet test --coverage`.
    [Parameter(Mandatory)]
    [string] $ResultsDirectory,

    # Folder searched (recursively) for testconfig.json files. Point it at one project to gate only that one.
    [string] $TestsDirectory = (Join-Path $PSScriptRoot '..' 'tests'),

    # Optional Markdown file the results table is appended to.
    [string] $SummaryPath
)

$ErrorActionPreference = 'Stop'
$onAzureDevOps = [bool] $env:TF_BUILD

function Get-Rate([System.Xml.XmlElement] $Coverage, [string] $Attribute) {
    # Cobertura stores rates as 0..1; testconfig.json thresholds are percentages.
    [double]::Parse($Coverage.GetAttribute($Attribute), [cultureinfo]::InvariantCulture) * 100
}

function Format-Pct($Value) {
    if ($null -eq $Value) { '—' } else { '{0:0.0}%' -f $Value }
}

$configs = Get-ChildItem -Path $TestsDirectory -Filter testconfig.json -Recurse -File |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
    Sort-Object FullName

if (-not $configs) {
    Write-Error "No testconfig.json found under $(Resolve-Path $TestsDirectory)."
}

# A test project with no testconfig.json would otherwise be silently ungated: fail it instead.
$ungated = Get-ChildItem -Path $TestsDirectory -Filter *.csproj -Recurse -File |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and -not (Test-Path (Join-Path $_.DirectoryName 'testconfig.json')) } |
    ForEach-Object {
        [pscustomobject] [ordered]@{ Project = $_.BaseName; Line = $null; MinLine = $null; Branch = $null; MinBranch = $null
                                     Problem = 'test project has no testconfig.json (no coverage settings, no minimum)' }
    }

$rows = @($ungated) + @(foreach ($configFile in $configs) {
    $project = $configFile.Directory.Name
    $row = [ordered]@{ Project = $project; Line = $null; MinLine = $null; Branch = $null; MinBranch = $null; Problem = $null }

    # PowerShell 7's ConvertFrom-Json accepts the // comments testconfig.json allows.
    # (Windows PowerShell 5.1's does not: that is one reason this script requires 7.)
    $config = Get-Content -Raw $configFile.FullName | ConvertFrom-Json
    $fileName = $config.codeCoverage.Configuration.CoverageFileName
    $format = $config.codeCoverage.Configuration.Format
    $row.MinLine = $config.coverageGate.minimumLineRate
    $row.MinBranch = $config.coverageGate.minimumBranchRate

    if (-not $fileName) {
        $row.Problem = 'testconfig.json has no codeCoverage.Configuration.CoverageFileName'
    }
    elseif ($format -ne 'cobertura') {
        $row.Problem = "codeCoverage.Configuration.Format must be cobertura for the gate (is '$($format ?? 'coverage (default)')')"
    }
    elseif ($null -eq $row.MinLine -and $null -eq $row.MinBranch) {
        $row.Problem = 'testconfig.json has no coverageGate thresholds'
    }
    else {
        $report = [IO.Path]::IsPathRooted($fileName) ? $fileName : (Join-Path $ResultsDirectory $fileName)
        if (-not (Test-Path $report)) {
            $row.Problem = "no coverage report at $report (was the run started with --coverage?)"
        }
        else {
            $coverage = ([xml] (Get-Content -Raw $report)).coverage
            $row.Line = Get-Rate $coverage 'line-rate'
            $row.Branch = Get-Rate $coverage 'branch-rate'

            $problems = @()
            if ($null -ne $row.MinLine -and $row.Line -lt $row.MinLine) {
                $problems += "line coverage $(Format-Pct $row.Line) is below $(Format-Pct $row.MinLine)"
            }
            if ($null -ne $row.MinBranch -and $row.Branch -lt $row.MinBranch) {
                $problems += "branch coverage $(Format-Pct $row.Branch) is below $(Format-Pct $row.MinBranch)"
            }
            if ($problems) { $row.Problem = $problems -join '; ' }
        }
    }

    [pscustomobject] $row
})

$table = [Text.StringBuilder]::new()
[void] $table.AppendLine('## Code coverage per test project').AppendLine()
[void] $table.AppendLine('| Project | Line | Min line | Branch | Min branch | Result |')
[void] $table.AppendLine('|---|---:|---:|---:|---:|---|')
foreach ($r in $rows) {
    $result = $r.Problem ? "❌ $($r.Problem)" : '✅ pass'
    [void] $table.AppendLine("| $($r.Project) | $(Format-Pct $r.Line) | $(Format-Pct $r.MinLine) | $(Format-Pct $r.Branch) | $(Format-Pct $r.MinBranch) | $result |")
}

Write-Host $table.ToString()

if ($SummaryPath) {
    Add-Content -Path $SummaryPath -Value $table.ToString()
    if ($onAzureDevOps) {
        Write-Host "##vso[task.uploadsummary]$((Resolve-Path $SummaryPath).Path)"
    }
}

$failed = @($rows | Where-Object Problem)
foreach ($f in $failed) {
    $message = "$($f.Project): $($f.Problem)"
    if ($onAzureDevOps) { Write-Host "##vso[task.logissue type=error]$message" } else { Write-Host "FAIL $message" -ForegroundColor Red }
}

exit ($failed.Count -eq 0 ? 0 : 1)
