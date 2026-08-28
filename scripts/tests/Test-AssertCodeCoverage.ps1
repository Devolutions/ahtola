#Requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$repoRoot = Split-Path -Parent $PSScriptRoot | Split-Path -Parent
$validator = Join-Path $repoRoot 'scripts/Assert-CodeCoverage.ps1'
$scratchRoot = Join-Path ([System.IO.Path]::GetTempPath()) "ahtola-coverage-$([Guid]::NewGuid().ToString('N'))"

function Fail([string]$Message) {
    throw "Test-AssertCodeCoverage failed: $Message"
}

function Write-Baseline(
    [string]$Path,
    [decimal]$LineRate = 0.80,
    [decimal]$BranchRate = 0.70
) {
    @{
        version = 1
        assemblies = @(
            @{
                name = 'Devolutions.Ahtola.Core'
                minimumLineRate = $LineRate
                minimumBranchRate = $BranchRate
            }
        )
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function Write-Coverage(
    [string]$Path,
    [decimal]$LineRate = 0.85,
    [decimal]$BranchRate = 0.75,
    [string]$PackageName = 'Devolutions.Ahtola.Core'
) {
    $line = $LineRate.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    $branch = $BranchRate.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    @"
<?xml version="1.0" encoding="utf-8"?>
<coverage line-rate="$line" branch-rate="$branch">
  <packages>
    <package name="$PackageName" line-rate="$line" branch-rate="$branch">
      <classes />
    </package>
  </packages>
</coverage>
"@ | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function Invoke-Validator(
    [string]$CoveragePath,
    [string]$BaselinePath,
    [bool]$ShouldSucceed,
    [string]$ExpectedFailure = ''
) {
    $output = @(
        & pwsh -NoLogo -NoProfile -File $validator `
            -CoveragePath $CoveragePath -BaselinePath $BaselinePath 2>&1
    )
    $exitCode = $LASTEXITCODE
    $renderedOutput = ($output -join [Environment]::NewLine) -replace '\s+', ' '
    if ($ShouldSucceed -and $exitCode -ne 0) {
        Fail "validator unexpectedly failed: $renderedOutput"
    }
    if (-not $ShouldSucceed -and $exitCode -eq 0) {
        Fail 'validator unexpectedly accepted an invalid coverage report.'
    }
    if (-not $ShouldSucceed -and $renderedOutput -notmatch [regex]::Escape($ExpectedFailure)) {
        Fail "validator failure did not contain '$ExpectedFailure': $renderedOutput"
    }
}

$passed = 0
try {
    New-Item -ItemType Directory -Path $scratchRoot -Force | Out-Null
    $coveragePath = Join-Path $scratchRoot 'coverage.cobertura.xml'
    $baselinePath = Join-Path $scratchRoot 'coverage-baseline.json'

    Write-Baseline $baselinePath
    Write-Coverage $coveragePath
    Invoke-Validator $coveragePath $baselinePath $true
    $passed++
    Write-Host 'PASS: coverage at or above both floors succeeds' -ForegroundColor Green

    Write-Coverage $coveragePath -LineRate 0.79
    Invoke-Validator $coveragePath $baselinePath $false 'line coverage'
    $passed++
    Write-Host 'PASS: line coverage regression fails' -ForegroundColor Green

    Write-Coverage $coveragePath -BranchRate 0.69
    Invoke-Validator $coveragePath $baselinePath $false 'branch coverage'
    $passed++
    Write-Host 'PASS: branch coverage regression fails' -ForegroundColor Green

    Write-Coverage $coveragePath -PackageName 'Unexpected.Assembly'
    Invoke-Validator $coveragePath $baselinePath $false 'absent from the coverage report'
    $passed++
    Write-Host 'PASS: missing required assembly fails' -ForegroundColor Green

    Set-Content -LiteralPath $coveragePath -Value '<coverage><packages /></coverage>' -Encoding utf8NoBOM
    Invoke-Validator $coveragePath $baselinePath $false 'contains no package coverage'
    $passed++
    Write-Host 'PASS: empty report fails' -ForegroundColor Green
} finally {
    Remove-Item -LiteralPath $scratchRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Test-AssertCodeCoverage passed ($passed checks)." -ForegroundColor Green
exit 0
