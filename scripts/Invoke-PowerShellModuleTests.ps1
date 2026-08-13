#Requires -Version 7.4
<#
.SYNOPSIS
    Run Pester 6 tests for the Devolutions.Ahtola.Sqlite PowerShell module.

.DESCRIPTION
    Stages nothing itself — pass -ModulePath to an already-built module folder
    (typically artifacts/powershell-modules/Devolutions.Ahtola.Sqlite). Requires
    Pester 6.0 or newer.

.EXAMPLE
    ./build.ps1 test-powershell
    pwsh ./scripts/Invoke-PowerShellModuleTests.ps1
#>
[CmdletBinding()]
param(
    [string]$ModulePath = (Join-Path $PSScriptRoot '..\artifacts\powershell-modules\Devolutions.Ahtola.Sqlite'),

    [string]$TestPath = (Join-Path $PSScriptRoot '..\tests\PowerShell\Devolutions.Ahtola.Sqlite'),

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [version]$MinimumPesterVersion = '6.0.0',

    [int]$MinimumExecutedTests = 1
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ModulePath = [System.IO.Path]::GetFullPath($ModulePath)
$TestPath = [System.IO.Path]::GetFullPath($TestPath)

if (-not (Test-Path -LiteralPath $ModulePath -PathType Container)) {
    throw "Module path not found: $ModulePath. Run './build.ps1 pack-powershell' first."
}

$manifest = Join-Path $ModulePath 'Devolutions.Ahtola.Sqlite.psd1'
if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) {
    throw "Module manifest not found: $manifest"
}

if (-not (Test-Path -LiteralPath $TestPath -PathType Container)) {
    throw "Pester test path not found: $TestPath"
}

$pesterModule = Get-Module -ListAvailable -Name Pester |
    Where-Object { $_.Version -ge $MinimumPesterVersion } |
    Sort-Object Version -Descending |
    Select-Object -First 1

if (-not $pesterModule) {
    throw "Pester $MinimumPesterVersion or newer is required. Install with: Install-Module Pester -MinimumVersion 6.0.0 -Force -Scope CurrentUser -SkipPublisherCheck"
}

Import-Module $pesterModule.Path -Force

Write-Host "Using Pester $($pesterModule.Version)" -ForegroundColor DarkGray
Write-Host "Module under test: $ModulePath" -ForegroundColor DarkGray
Write-Host "Tests: $TestPath" -ForegroundColor DarkGray

$pesterConfig = New-PesterConfiguration
$pesterConfig.Run.Exit = $false
$pesterConfig.Run.PassThru = $true
$pesterConfig.Output.Verbosity = 'Detailed'
$pesterConfig.TestResult.Enabled = $true
$pesterConfig.TestResult.OutputPath = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot "..\artifacts\test-results\powershell\Devolutions.Ahtola.Sqlite.pester.xml"))
$pesterConfig.TestResult.OutputFormat = 'NUnitXml'

# Use a container so ModulePath is injected; do not also set Run.Path (would double-run).
$container = New-PesterContainer -Path $TestPath -Data @{
    ModulePath = $ModulePath
}
$pesterConfig.Run.Container = @($container)

$resultDir = Split-Path -Parent $pesterConfig.TestResult.OutputPath.Value
if (-not (Test-Path -LiteralPath $resultDir)) {
    New-Item -ItemType Directory -Path $resultDir -Force | Out-Null
}

$result = Invoke-Pester -Configuration $pesterConfig

$executed = [int]$result.TotalCount
$failed = [int]$result.FailedCount
$passed = [int]$result.PassedCount

Write-Host "Pester: executed $executed (passed $passed, failed $failed)" -ForegroundColor $(if ($failed -eq 0) { 'Green' } else { 'Red' })

if ($executed -lt $MinimumExecutedTests) {
    throw "Expected at least $MinimumExecutedTests Pester test(s) to execute, but executed $executed."
}

if ($failed -gt 0) {
    exit 1
}

exit 0
