#Requires -Version 7.2
<#
.SYNOPSIS
    Merges every test project's Cobertura file into one report: HTML for people, a summary table for logs.

.DESCRIPTION
    Each MTP test project writes its own coverage file. On its own, none of them answers
    "how well tested is Orders.Domain?", because several test projects may run that assembly.

    This script feeds all of them to ReportGenerator (a local dotnet tool, see .config/dotnet-tools.json),
    which MERGES them: a line counts as covered if ANY test project's run hit it. That is the same
    question Azure DevOps answers on its Code Coverage tab when PublishCodeCoverageResults@2 is given
    several files.

    Output, in -OutputDirectory:
      index.html          browsable report, line-by-line source highlighting
      Cobertura.xml       the merged report as a single Cobertura file
      Summary.json        machine-readable totals per assembly / class
      CoverageSummary.md  per-assembly Markdown table (attached to the Azure DevOps run with -PublishSummary)

.EXAMPLE
    ./scripts/New-CoverageReport.ps1 -ResultsDirectory ./TestResults
    # then open ./TestResults/CoverageReport/index.html
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ResultsDirectory,

    [string] $OutputDirectory = (Join-Path $ResultsDirectory 'CoverageReport'),

    # Glob, relative to -ResultsDirectory, for the reports to merge.
    [string] $Pattern = '*.cobertura.xml',

    # Attach the Markdown summary to the Azure DevOps run (Extensions tab).
    [switch] $PublishSummary
)

$ErrorActionPreference = 'Stop'
$repoRoot = Join-Path $PSScriptRoot '..'

$reports = @(Get-ChildItem -Path $ResultsDirectory -Filter $Pattern -File)
if (-not $reports) {
    Write-Error "No '$Pattern' files in $ResultsDirectory. Run dotnet test with --coverage first."
}

Write-Host "Merging $($reports.Count) coverage file(s):"
$reports | ForEach-Object { Write-Host "  $($_.Name)" }

Push-Location $repoRoot
try {
    dotnet tool restore | Out-Null
    if ($LASTEXITCODE) { throw 'dotnet tool restore failed.' }

    # ';' separates multiple reports for ReportGenerator, on every OS.
    $reportList = ($reports.FullName -join ';')
    dotnet reportgenerator `
        "-reports:$reportList" `
        "-targetdir:$OutputDirectory" `
        '-reporttypes:Html;Cobertura;JsonSummary' `
        '-verbosity:Warning'
    if ($LASTEXITCODE) { throw "reportgenerator exited with $LASTEXITCODE." }
}
finally {
    Pop-Location
}

# Per-assembly view of the merged result, straight from ReportGenerator's JSON summary.
$summary = Get-Content -Raw (Join-Path $OutputDirectory 'Summary.json') | ConvertFrom-Json
Write-Host ''
Write-Host 'Merged coverage (a line is covered if ANY test project hit it):'
# Plain string formatting rather than Format-Table: it renders the same in a terminal and a CI log.
$line = '{0,-22} {1,8} {2,9} {3,8} {4,10}'
Write-Host ($line -f 'Assembly', 'Line %', 'Branch %', 'Covered', 'Coverable')
foreach ($a in $summary.coverage.assemblies) {
    Write-Host ($line -f $a.name, $a.coverage, $a.branchcoverage, $a.coveredlines, $a.coverablelines)
}
Write-Host ("Total: {0}% line, {1}% branch" -f $summary.summary.linecoverage, $summary.summary.branchcoverage)
Write-Host "HTML report: $(Join-Path (Resolve-Path $OutputDirectory) 'index.html')"

$md = [Text.StringBuilder]::new()
[void] $md.AppendLine('## Merged code coverage (all test projects)').AppendLine()
[void] $md.AppendLine('A line counts as covered if **any** test project ran it. Per-project numbers are in the gate table.').AppendLine()
[void] $md.AppendLine('| Assembly | Line | Branch | Covered / coverable lines |')
[void] $md.AppendLine('|---|---:|---:|---:|')
foreach ($a in $summary.coverage.assemblies) {
    [void] $md.AppendLine("| $($a.name) | $($a.coverage)% | $($a.branchcoverage)% | $($a.coveredlines) / $($a.coverablelines) |")
}
[void] $md.AppendLine("| **Total** | **$($summary.summary.linecoverage)%** | **$($summary.summary.branchcoverage)%** | $($summary.summary.coveredlines) / $($summary.summary.coverablelines) |")
$mdPath = Join-Path (Resolve-Path $OutputDirectory) 'CoverageSummary.md'
Set-Content -Path $mdPath -Value $md.ToString()

if ($PublishSummary -and $env:TF_BUILD) {
    Write-Host "##vso[task.uploadsummary]$mdPath"
}
