#Requires -Version 7.0
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Join-Path ([IO.Path]::GetTempPath()) ('ahtola-benchmark-results-' + [Guid]::NewGuid().ToString('N'))
$current = Join-Path $root 'current'
$baselinePath = Join-Path $root 'baseline.json'
$outputPath = Join-Path $root 'results.json'

try {
    New-Item -ItemType Directory -Path $current -Force | Out-Null
    [ordered]@{
        HostEnvironmentInfo = [ordered]@{ RuntimeVersion = '.NET 10.0' }
        Benchmarks = @(
            [ordered]@{
                FullName = 'Benchmarks.Write.Insert(Rows: 100)'
                Method = 'Insert'
                Type = 'Write'
                Parameters = 'Rows=100'
                Statistics = [ordered]@{ Mean = 1200000 }
                Memory = [ordered]@{ BytesAllocatedPerOperation = 4096 }
            },
            [ordered]@{
                FullName = 'Benchmarks.Read.Select(Rows: 100)'
                Method = 'Select'
                Type = 'Read'
                Parameters = 'Rows=100'
                Statistics = [ordered]@{ Mean = 800000 }
                Memory = [ordered]@{ BytesAllocatedPerOperation = 2048 }
            }
        )
    } | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath (Join-Path $current 'sample-report-full.json') -Encoding utf8

    [ordered]@{
        schemaVersion = 2
        entries = @(
            [ordered]@{
                identity = 'Benchmarks.Write.Insert(Rows: 100)|.NET 10.0'
                method = 'Insert'
                meanNanoseconds = 1000000
            }
        )
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $baselinePath -Encoding utf8

    $messages = & (Join-Path (Split-Path -Parent $PSScriptRoot) 'Convert-AhtolaBenchmarkResults.ps1') `
        -ResultsDirectory $current `
        -OutputPath $outputPath `
        -BaselinePath $baselinePath `
        -RegressionThresholdPercent 10 *>&1

    $result = Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json
    if ($result.entries.Count -ne 2) {
        throw "Expected two normalized entries, found $($result.entries.Count)."
    }
    $insert = $result.entries | Where-Object method -eq 'Insert'
    if ($insert.meanNanoseconds -ne 1200000) {
        throw "Expected the JSON mean to remain 1200000 ns."
    }
    if (-not ($messages -match '20.0% slower')) {
        throw 'Expected the historical comparison to report a 20% regression.'
    }

    Write-Host 'Benchmark result normalization tests passed.'
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
