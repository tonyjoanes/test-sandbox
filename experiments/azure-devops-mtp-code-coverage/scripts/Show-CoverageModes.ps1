#Requires -Version 7.2
<#
.SYNOPSIS
    Teaching aid: shows the SAME code measured three ways, so the numbers stop being a mystery.

.DESCRIPTION
    Runs every test project twice and prints a matrix of line coverage per production assembly:

      Unfiltered   each project with NO coverage filters: every assembly with a PDB that the test
                   process loaded is measured, generated code and Program.cs included.
      Configured   each project with its own testconfig.json filters (what CI actually gates on).

    and, for each of those, the MERGED column: all projects' files combined, where a line is covered
    if any project hit it.

    Nothing here is needed in CI. It exists so a team can see, with their own eyes, that:
      * one test project only measures code its own process ran
      * an assembly can appear in several projects' reports, with different numbers
      * merged coverage is a UNION, not an average: it is >= every single project's number
      * filters can move a number a long way, so a percentage means nothing without its filters

    Writes the matrix to the console and, with -MarkdownPath, to a Markdown file.

.EXAMPLE
    ./scripts/Show-CoverageModes.ps1
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $WorkDirectory = (Join-Path $PSScriptRoot '..' 'TestResults' 'modes'),
    [string] $MarkdownPath
)

$ErrorActionPreference = 'Stop'

# Line coverage per assembly for one or more Cobertura files (';'-separated), via ReportGenerator.
function Get-AssemblyCoverage([string] $Reports, [string] $OutDir) {
    dotnet reportgenerator "-reports:$Reports" "-targetdir:$OutDir" '-reporttypes:JsonSummary' '-verbosity:Error' | Out-Null
    if ($LASTEXITCODE) { throw "reportgenerator failed for $Reports" }
    $summary = Get-Content -Raw (Join-Path $OutDir 'Summary.json') | ConvertFrom-Json
    $byAssembly = [ordered]@{}
    foreach ($a in $summary.coverage.assemblies) { $byAssembly[$a.name] = [double] $a.coverage }
    $byAssembly
}

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$WorkDirectory = [IO.Path]::GetFullPath($WorkDirectory)
$env:DOTNET_NOLOGO = 'true'
$env:TESTINGPLATFORM_TELEMETRY_OPTOUT = '1'

Push-Location $root
try {
    if (Test-Path $WorkDirectory) { Remove-Item $WorkDirectory -Recurse -Force }
    New-Item -ItemType Directory -Path $WorkDirectory | Out-Null

    dotnet build Orders.slnx --configuration $Configuration -v quiet | Out-Null
    if ($LASTEXITCODE) { throw 'Build failed.' }
    dotnet tool restore | Out-Null

    # A testconfig.json with coverage on but no filters. Passed with --config-file, it REPLACES the
    # project's own testconfig.json for that run. (Passing --coverage-settings instead does not work
    # while testconfig.json has a codeCoverage section: MTP refuses to run with both.)
    $unfilteredConfig = Join-Path $WorkDirectory 'unfiltered.testconfig.json'
    '{ "codeCoverage": { "Configuration": { "Format": "cobertura" } } }' | Set-Content $unfilteredConfig

    $projects = Get-ChildItem tests -Directory | Where-Object { Test-Path (Join-Path $_ 'testconfig.json') } | Sort-Object Name
    $modes = [ordered]@{
        Unfiltered = @('--config-file', $unfilteredConfig)
        Configured = @()
    }

    # results[mode][project][assembly] = line %
    $results = @{}
    foreach ($mode in $modes.Keys) {
        $results[$mode] = [ordered]@{}
        $modeDir = Join-Path $WorkDirectory $mode
        foreach ($p in $projects) {
            Write-Host "[$mode] $($p.Name)" -ForegroundColor Cyan
            $out = "$($p.Name).cobertura.xml"
            dotnet test --project $p.FullName --configuration $Configuration --no-build --no-progress `
                --results-directory $modeDir --coverage --coverage-output-format cobertura --coverage-output $out `
                @($modes[$mode]) | Out-Null
            if ($LASTEXITCODE) { throw "Tests failed for $($p.Name) in $mode mode." }

            # Count every column with the same tool (ReportGenerator) as the MERGED column. Microsoft's
            # Cobertura writer counts a line once per method it appears in, ReportGenerator counts unique
            # source lines, so mixing the two would make columns differ by a few tenths for no real reason.
            $results[$mode][$p.Name] = Get-AssemblyCoverage -Reports (Join-Path $modeDir $out) -OutDir (Join-Path $modeDir "$($p.Name).summary")
        }

        # Merge this mode's files the same way New-CoverageReport.ps1 (and Azure DevOps) does.
        $files = (Get-ChildItem $modeDir -Filter *.cobertura.xml).FullName -join ';'
        $results[$mode]['MERGED'] = Get-AssemblyCoverage -Reports $files -OutDir (Join-Path $modeDir 'merged')
    }

    # Print one matrix per mode: rows = production assemblies, columns = test projects + merged.
    $md = [Text.StringBuilder]::new()
    foreach ($mode in $modes.Keys) {
        $columns = @($projects.Name) + 'MERGED'
        $assemblies = $results[$mode].Values | ForEach-Object { $_.Keys } | Sort-Object -Unique
        [void] $md.AppendLine("### $mode").AppendLine()
        [void] $md.AppendLine('| Assembly | ' + ($columns -join ' | ') + ' |')
        [void] $md.AppendLine('|---|' + (($columns | ForEach-Object { '---:' }) -join '|') + '|')
        foreach ($asm in $assemblies) {
            $cells = foreach ($c in $columns) {
                $v = $results[$mode][$c][$asm]
                if ($null -eq $v) { 'not measured' } else { '{0:0.0}%' -f $v }
            }
            [void] $md.AppendLine("| $asm | " + ($cells -join ' | ') + ' |')
        }
        [void] $md.AppendLine()
    }

    Write-Host ''
    Write-Host $md.ToString()
    Write-Host '"not measured" = that test project never loaded the assembly, or its filters exclude it.'
    Write-Host 'MERGED >= every column to its left: a line counts as covered if ANY project ran it.'
    if ($MarkdownPath) { Set-Content -Path $MarkdownPath -Value $md.ToString(); Write-Host "Written to $MarkdownPath" }
}
finally {
    Pop-Location
}
