#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('smoke', 'write-short', 'coverage', 'full', 'large', 'diagnostic')]
    [string]$Profile = 'coverage',
    [string]$Framework = 'net10.0',
    [string]$Filter = '*',
    [string]$BaselinePath,
    [string]$OutputRoot = './artifacts/benchmarks',
    [double]$RegressionThresholdPercent = 10
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$benchmarkProject = Join-Path (Join-Path $repoRoot 'src') (Join-Path 'Benchmarks' 'Ahtola.Benchmarks.csproj')
$runId = "{0:yyyyMMdd-HHmmssfff}-{1}-{2}" -f [DateTimeOffset]::UtcNow, $Profile, $PID
$output = Join-Path (Join-Path $repoRoot $OutputRoot) $runId
$bdnOutput = Join-Path $output 'bdn'
New-Item -ItemType Directory -Path $bdnOutput -Force | Out-Null

$metadata = [ordered]@{
    schemaVersion = 1
    runId = $runId
    profile = $Profile
    framework = $Framework
    filter = $Filter
    commit = (& git -C $repoRoot rev-parse HEAD)
    dirty = [bool](& git -C $repoRoot status --porcelain)
    os = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    processorCount = [Environment]::ProcessorCount
    dotnetSdk = (& dotnet --version)
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
}
$metadata | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath (Join-Path $output 'environment.json') -Encoding utf8

$arguments = @(
    'run', '--configuration', 'Release',
    '--framework', $Framework,
    '--project', $benchmarkProject,
    '--',
    '--filter', $Filter,
    '--artifacts', $bdnOutput,
    '--exporters', 'fulljson',
    '--noOverwrite'
)

switch ($Profile) {
    'smoke' {
        $env:AHTOLA_BENCHMARK_SMOKE = '1'
        $arguments += @('--job', 'Dry')
    }
    'write-short' { $arguments += @('--job', 'Short', '--anyCategories', 'Write') }
    'coverage' { $arguments += @('--job', 'Short') }
    'large' { $arguments += @('--anyCategories', 'Large') }
    'diagnostic' { $arguments += @('--job', 'Short', '--profiler', 'EP') }
}

Write-Host "Benchmark run: $runId"
Write-Host "Artifacts: $output"
$logPath = Join-Path $output 'benchmark.log'
& dotnet @arguments 2>&1 | Tee-Object -FilePath $logPath
if ($LASTEXITCODE -ne 0) {
    throw "BenchmarkDotNet failed with exit code $LASTEXITCODE."
}

$failedWorkloads = @(Select-String -LiteralPath $logPath -SimpleMatch 'No Workload Results were obtained from the run.')
$issueSections = @(Select-String -LiteralPath $logPath -SimpleMatch 'Benchmarks with issues:')
$emptyRuns = @(Select-String -LiteralPath $logPath -Pattern 'returned 0 benchmarks|Found 0 benchmark')
$reports = @(Get-ChildItem -LiteralPath $bdnOutput -Filter '*-report-full.json' -Recurse)
if ($failedWorkloads.Count -gt 0 -or $issueSections.Count -gt 0 -or $emptyRuns.Count -gt 0 -or $reports.Count -eq 0) {
    throw "BenchmarkDotNet reported invalid workloads ($($failedWorkloads.Count) failed runs, $($issueSections.Count) issue sections)."
}

$normalized = Join-Path $output 'results.json'
& (Join-Path $PSScriptRoot 'Convert-AhtolaBenchmarkResults.ps1') `
    -ResultsDirectory $bdnOutput `
    -OutputPath $normalized `
    -BaselinePath $BaselinePath `
    -RegressionThresholdPercent $RegressionThresholdPercent

$normalizedResult = Get-Content -LiteralPath $normalized -Raw | ConvertFrom-Json
if ($normalizedResult.entries.Count -eq 0) {
    throw 'BenchmarkDotNet produced no normalized result entries.'
}

Write-Host "Normalized results: $normalized"
