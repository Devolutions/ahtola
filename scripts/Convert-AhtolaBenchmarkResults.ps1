#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ResultsDirectory,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$BaselinePath,
    [double]$RegressionThresholdPercent = 10
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$rows = foreach ($file in Get-ChildItem -LiteralPath $ResultsDirectory -Filter '*-report-full.json' -Recurse) {
    $report = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    $runtime = $report.HostEnvironmentInfo.RuntimeVersion
    foreach ($benchmark in $report.Benchmarks) {
        $identity = "$($benchmark.FullName)|$runtime"
        [pscustomobject]@{
            identity = $identity
            method = $benchmark.Method
            type = $benchmark.Type
            runtime = $runtime
            parameters = $benchmark.Parameters
            meanNanoseconds = $benchmark.Statistics.Mean
            allocatedBytes = $benchmark.Memory.BytesAllocatedPerOperation
            source = $file.Name
        }
    }
}

$summary = [ordered]@{
    schemaVersion = 2
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    entries = @($rows | Sort-Object identity)
}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath -Encoding utf8

if (-not [string]::IsNullOrWhiteSpace($BaselinePath) -and (Test-Path -LiteralPath $BaselinePath)) {
    $baseline = Get-Content -LiteralPath $BaselinePath -Raw | ConvertFrom-Json
    $baselineById = @{}
    foreach ($entry in $baseline.entries) {
        $baselineById[$entry.identity] = $entry
    }

    foreach ($entry in $summary.entries) {
        if ($null -eq $entry.meanNanoseconds -or -not $baselineById.ContainsKey($entry.identity)) {
            continue
        }

        $prior = $baselineById[$entry.identity].meanNanoseconds
        if ($null -eq $prior -or $prior -le 0) {
            continue
        }

        $change = (($entry.meanNanoseconds - $prior) / $prior) * 100
        if ($change -ge $RegressionThresholdPercent) {
            Write-Host "::warning title=Benchmark regression::$($entry.method) is $($change.ToString('F1'))% slower than the supplied baseline."
        }
    }
}
