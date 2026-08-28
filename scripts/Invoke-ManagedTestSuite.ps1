<#
.SYNOPSIS
    Runs the managed Ahtola test suite and proves that it actually executed tests.

.DESCRIPTION
    `dotnet test` exits 0 both when a suite passes and when a suite silently
    discovers nothing, so a broken build graph, a stale filter, or a missing
    runtime pack can produce a green job that ran zero tests. This wrapper reads
    the TRX result file back and fails when the run did not execute at least the
    expected number of tests, or when a class that must really run on this
    platform was never discovered or was skipped away entirely.

    A platform where the engine is knowingly unimplemented can be described with
    -KnownGapFailurePattern. That allowance is scoped to a failure *message*, not
    to a platform, a class, or a hand-maintained list of test names: any failure
    that does not carry the documented message still fails the run, so a real
    regression on that platform cannot hide behind the gap.
#>
[CmdletBinding()]
param(
    [string]$Project = './src/Ahtola.Tests/Ahtola.Tests.csproj',
    [string]$Framework,
    [string]$Filter,
    [string]$Configuration = 'Debug',
    [string]$ResultsDirectory = './artifacts/test-results',
    # Floor applied to both executed and passing tests, so a leg cannot go green
    # by discovering nothing and cannot go green by failing almost everything.
    [int]$MinimumExecutedTests = 1,
    # Classes that must contribute at least one passing (non-skipped) result.
    [string[]]$RequirePassingClass = @(),
    # Classes that must be discovered and reported, even if the platform guards
    # every case away. This keeps a platform gap visible instead of silent.
    [string[]]$RequireDiscoveredClass = @(),
    # Regular expression matched against a failure message. Failures that match
    # are attributed to a documented, unimplemented platform primitive instead of
    # failing the run. Every other failure still fails the run.
    [string]$KnownGapFailurePattern,
    # Human-readable explanation printed with every known-gap failure so the gap
    # stays visible in the job log and step summary instead of becoming silent.
    [string]$KnownGapReason,
    # Aborts the run and names the offending test when a single test stops making
    # progress for this long. Without it a hung test burns the whole job timeout and
    # reports as a cancelled job with no indication of which test stalled.
    [int]$HangTimeoutMinutes = 0,
    [switch]$NoBuild,
    # Collects shipped-assembly coverage through the existing Coverlet VSTest
    # collector and normalizes its GUID-scoped report to the results root.
    [switch]$CollectCoverage,
    [string]$CoverageSettings = './coverage.runsettings',
    # Reproduces the managed lane's "must not shell out to Rust" invariant by
    # putting failing cargo/rustc shims ahead of the real toolchain on PATH.
    [switch]$DenyNativeToolchain
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

function Fail([string]$Message) {
    throw "Managed test suite validation failed: $Message"
}

function New-ToolchainDenyDirectory {
    $directory = Join-Path ([System.IO.Path]::GetTempPath()) "Ahtola-managed-deny-$([System.Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    foreach ($tool in @('cargo', 'rustc')) {
        $message = "managed .NET validation must not invoke $tool"
        if ($IsWindows) {
            Set-Content -LiteralPath (Join-Path $directory "$tool.cmd") -Encoding ascii -Value @(
                '@echo off'
                "echo $message 1>&2"
                'exit /b 97'
            )
        }
        else {
            $shim = Join-Path $directory $tool
            Set-Content -LiteralPath $shim -Encoding ascii -Value @(
                '#!/usr/bin/env sh'
                "echo '$message' >&2"
                'exit 97'
            )
            & chmod +x $shim
            if ($LASTEXITCODE -ne 0) {
                Fail "could not mark '$shim' executable."
            }
        }
    }

    return $directory
}

function Get-TrxSummary([string]$TrxPath) {
    [xml]$trx = Get-Content -LiteralPath $TrxPath -Raw

    $classByTestId = @{}
    foreach ($definition in $trx.SelectNodes("//*[local-name()='TestDefinitions']/*[local-name()='UnitTest']")) {
        $method = $definition.SelectSingleNode("*[local-name()='TestMethod']")
        if ($null -ne $method) {
            $classByTestId[$definition.GetAttribute('id')] = $method.GetAttribute('className')
        }
    }

    $passed = 0
    $failed = 0
    $skipped = 0
    $other = 0
    $failures = [System.Collections.Generic.List[psobject]]::new()
    $passedByClass = @{}
    $resultsByClass = @{}

    foreach ($result in $trx.SelectNodes("//*[local-name()='Results']/*[local-name()='UnitTestResult']")) {
        $testId = $result.GetAttribute('testId')
        $className = if ($classByTestId.ContainsKey($testId)) { $classByTestId[$testId] } else { '<unknown>' }
        $resultsByClass[$className] = 1 + $(if ($resultsByClass.ContainsKey($className)) { $resultsByClass[$className] } else { 0 })

        switch ($result.GetAttribute('outcome')) {
            'Passed' {
                $passed++
                $passedByClass[$className] = 1 + $(if ($passedByClass.ContainsKey($className)) { $passedByClass[$className] } else { 0 })
            }
            'Failed' {
                $failed++
                $messageNode = $result.SelectSingleNode("*[local-name()='Output']/*[local-name()='ErrorInfo']/*[local-name()='Message']")
                $message = if ($null -ne $messageNode) { $messageNode.InnerText } else { '' }
                $failures.Add([pscustomobject]@{
                        Name    = "$className.$($result.GetAttribute('testName'))"
                        Message = ($message -replace '\s+', ' ').Trim()
                    })
            }
            { $_ -in @('NotExecuted', 'Inconclusive', 'Warning') } { $skipped++ }
            default { $other++ }
        }
    }

    return [pscustomobject]@{
        Passed         = $passed
        Failed         = $failed
        Skipped        = $skipped
        Other          = $other
        Total          = $passed + $failed + $skipped + $other
        Failures       = $failures
        PassedByClass  = $passedByClass
        ResultsByClass = $resultsByClass
    }
}

function Test-ClassMatch([hashtable]$Counts, [string]$ClassName) {
    foreach ($key in $Counts.Keys) {
        if ($key -eq $ClassName -or $key.EndsWith(".$ClassName", [System.StringComparison]::Ordinal)) {
            return $true
        }
    }

    return $false
}

$legName = if ($Framework) { $Framework } else { 'all-frameworks' }
$resultsRoot = Join-Path $ResultsDirectory $legName
if (Test-Path -LiteralPath $resultsRoot) {
    Remove-Item -LiteralPath $resultsRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $resultsRoot -Force | Out-Null

$trxFileName = 'managed-test-suite.trx'
$arguments = @(
    'test'
    $Project
    '--configuration', $Configuration
    '--results-directory', $resultsRoot
    '--logger', "trx;LogFileName=$trxFileName"
)
if ($Framework) { $arguments += @('--framework', $Framework) }
if ($Filter) { $arguments += @('--filter', $Filter) }
if ($NoBuild) { $arguments += '--no-build' }
if ($HangTimeoutMinutes -gt 0) {
    $arguments += @('--blame-hang', '--blame-hang-timeout', "${HangTimeoutMinutes}m", '--blame-hang-dump-type', 'none')
}
if ($CollectCoverage) {
    if (-not (Test-Path -LiteralPath $CoverageSettings -PathType Leaf)) {
        Fail "coverage settings file '$CoverageSettings' does not exist."
    }
    $arguments += @('--collect', 'XPlat Code Coverage', '--settings', $CoverageSettings)
}

$denyDirectory = $null
$originalPath = $env:PATH
$originalCargo = $env:CARGO
$originalRustc = $env:RUSTC
try {
    if ($DenyNativeToolchain) {
        $denyDirectory = New-ToolchainDenyDirectory
        $env:PATH = "$denyDirectory$([System.IO.Path]::PathSeparator)$originalPath"
        $env:CARGO = Join-Path $denyDirectory $(if ($IsWindows) { 'cargo.cmd' } else { 'cargo' })
        $env:RUSTC = Join-Path $denyDirectory $(if ($IsWindows) { 'rustc.cmd' } else { 'rustc' })
    }

    Write-Host "dotnet $($arguments -join ' ')"
    & dotnet @arguments
    $testExitCode = $LASTEXITCODE
}
finally {
    $env:PATH = $originalPath
    $env:CARGO = $originalCargo
    $env:RUSTC = $originalRustc
    if ($null -ne $denyDirectory -and (Test-Path -LiteralPath $denyDirectory)) {
        Remove-Item -LiteralPath $denyDirectory -Recurse -Force
    }
}

$trxPath = Join-Path $resultsRoot $trxFileName

# `--blame-hang` writes a sequence file naming the test that stopped making progress
# and then aborts the run, so surface it before any other diagnosis: an aborted run
# also looks like a short run, and the hung test is the far more useful signal.
$sequenceFiles = @(Get-ChildItem -Path $resultsRoot -Recurse -Filter 'Sequence*.xml' -ErrorAction SilentlyContinue)
if ($sequenceFiles.Count -gt 0) {
    foreach ($sequence in $sequenceFiles) {
        Write-Host "::error::a test stopped making progress; blame sequence follows"
        Get-Content -LiteralPath $sequence.FullName | Write-Host
    }

    Fail "a test hung and the run was aborted after $HangTimeoutMinutes minute(s) without progress; see the blame sequence above for the offending test."
}

if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
    Fail "the run produced no TRX report at '$trxPath', so it cannot be proven to have executed any test (dotnet test exit code $testExitCode)."
}

if ($CollectCoverage) {
    $coverageReports = @(
        Get-ChildItem -LiteralPath $resultsRoot -Recurse -Filter 'coverage.cobertura.xml' |
            Where-Object { $_.DirectoryName -ne (Resolve-Path -LiteralPath $resultsRoot).Path }
    )
    if ($coverageReports.Count -eq 0) {
        Fail "coverage collection produced no Cobertura report under '$resultsRoot'."
    }

    $coverageHashes = @(
        $coverageReports |
            Group-Object -Property { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    )
    if ($coverageHashes.Count -ne 1) {
        Fail "coverage collection produced $($coverageHashes.Count) distinct Cobertura reports under '$resultsRoot'; one logical report was expected."
    }

    $normalizedCoveragePath = Join-Path $resultsRoot 'coverage.cobertura.xml'
    $sourceCoveragePath = $coverageReports |
        Sort-Object { $_.FullName.Length } |
        Select-Object -First 1 -ExpandProperty FullName
    Copy-Item -LiteralPath $sourceCoveragePath -Destination $normalizedCoveragePath
    Write-Host "coverage report: $normalizedCoveragePath"
}

$summary = Get-TrxSummary -TrxPath $trxPath
$executed = $summary.Passed + $summary.Failed

if ($KnownGapFailurePattern -and -not $KnownGapReason) {
    Fail 'a -KnownGapFailurePattern was supplied without a -KnownGapReason; an unexplained allowance is indistinguishable from hiding a failure.'
}

$knownGapFailures = @()
$realFailures = @($summary.Failures)
if ($KnownGapFailurePattern) {
    $knownGapFailures = @($summary.Failures | Where-Object { $_.Message -match $KnownGapFailurePattern })
    $realFailures = @($summary.Failures | Where-Object { $_.Message -notmatch $KnownGapFailurePattern })
}

$headline = "$legName on $([System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier): executed $executed (passed $($summary.Passed), failed $($summary.Failed)), skipped $($summary.Skipped), discovered $($summary.Total)"
if ($KnownGapFailurePattern) {
    $headline += ", known-gap failures $($knownGapFailures.Count)"
}
Write-Host $headline

if ($env:GITHUB_STEP_SUMMARY) {
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value "- $headline"
}

if ($KnownGapFailurePattern) {
    if ($knownGapFailures.Count -gt 0) {
        $gapNotice = "$($knownGapFailures.Count) failure(s) attributed to a documented platform gap: $KnownGapReason"
        Write-Host "::warning::$gapNotice"
        if ($env:GITHUB_STEP_SUMMARY) {
            Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value "  - :warning: $gapNotice"
        }

        $gapClasses = $knownGapFailures | ForEach-Object { ($_.Name -split '\.')[-2] } | Sort-Object -Unique
        Write-Host "known-gap classes: $($gapClasses -join ', ')"
    }
    else {
        Write-Host "::notice::no failure matched the known-gap pattern; the gap appears closed, so remove -KnownGapFailurePattern from this leg."
    }
}

if ($realFailures.Count -gt 0) {
    $detail = ($realFailures | Select-Object -First 25 | ForEach-Object { "$($_.Name): $($_.Message)" }) -join "; "
    Fail "$($realFailures.Count) test(s) failed for reasons outside any declared platform gap: $detail"
}

if ($executed -lt $MinimumExecutedTests) {
    Fail "only $executed test(s) executed but at least $MinimumExecutedTests were expected; a run this small means the suite was not really exercised."
}

if ($summary.Passed -lt $MinimumExecutedTests) {
    Fail "only $($summary.Passed) test(s) passed but at least $MinimumExecutedTests were expected; a declared platform gap must not swallow the whole suite."
}

foreach ($className in $RequirePassingClass) {
    if (-not (Test-ClassMatch -Counts $summary.PassedByClass -ClassName $className)) {
        Fail "'$className' contributed no passing result on this platform, so its coverage was skipped away or never discovered."
    }
}

foreach ($className in $RequireDiscoveredClass) {
    if (-not (Test-ClassMatch -Counts $summary.ResultsByClass -ClassName $className)) {
        Fail "'$className' was never discovered, so its platform gap is no longer being reported."
    }
}

if ($testExitCode -ne 0 -and $summary.Failed -eq 0) {
    Fail "dotnet test exited with code $testExitCode."
}

# The TRX is now the authority on success, not `dotnet test`'s exit code, because a
# leg with a declared platform gap deliberately tolerates specific failures. Exit
# explicitly so the surviving $LASTEXITCODE from `dotnet test` cannot leak out: the
# GitHub Actions pwsh wrapper appends `exit $LASTEXITCODE`, which would otherwise
# fail a validated run. `Fail` throws, which exits non-zero under `Stop`.
exit 0
