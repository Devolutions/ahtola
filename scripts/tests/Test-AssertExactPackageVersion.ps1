#Requires -Version 7.0
<#
.SYNOPSIS
    Self-contained smoke test for build.ps1's assert-package-version task and
    Invoke-ValidatePackage's "reuse an existing CI-versioned package instead
    of silently repacking it" behavior.

.DESCRIPTION
    Exercises Assert-ExactPackageVersion (via the assert-package-version
    task) against synthetic nupkg files, proving both that a matching
    version passes and that a mismatched or missing set of packages fails
    loudly. This is the regression guard for the CI packaging bug where
    validate-package used to delete and repack artifacts/managed-packages
    under a different, throwaway version, silently orphaning the intended
    CI-versioned artifacts.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$repoRoot = Split-Path -Parent $PSScriptRoot | Split-Path -Parent
$buildScript = Join-Path $repoRoot 'build.ps1'
if (-not (Test-Path -LiteralPath $buildScript -PathType Leaf)) {
    throw "build.ps1 not found at $buildScript"
}

function Fail([string]$Message) {
    throw "Test-AssertExactPackageVersion failed: $Message"
}

function New-FakeNupkgDirectory([string]$Version, [string[]]$PackageIds) {
    $directory = Join-Path ([System.IO.Path]::GetTempPath()) "ahtola-assert-version-$([System.Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    foreach ($id in $PackageIds) {
        New-Item -ItemType File -Path (Join-Path $directory "$id.$Version.nupkg") -Force | Out-Null
    }
    return $directory
}

$passed = 0

# 1. Matching version: every nupkg carries the expected version -> succeeds.
$matchingDirectory = New-FakeNupkgDirectory -Version '0.0.0-ci.74' -PackageIds @(
    'Devolutions.Ahtola.Core',
    'Devolutions.Ahtola.Data.Sqlite'
)
try {
    & pwsh -NoLogo -NoProfile -File $buildScript assert-package-version `
        -PackageOutput $matchingDirectory -PackageVersion '0.0.0-ci.74' | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Fail "assert-package-version was expected to succeed for a matching version but exited $LASTEXITCODE."
    }
    $passed++
    Write-Host 'PASS: matching version succeeds' -ForegroundColor Green
} finally {
    Remove-Item -LiteralPath $matchingDirectory -Recurse -Force -ErrorAction SilentlyContinue
}

# 2. Mismatched version: nupkgs exist but carry a different version -> fails.
$mismatchedDirectory = New-FakeNupkgDirectory -Version '0.0.0-ci.74' -PackageIds @('Devolutions.Ahtola.Core')
try {
    & pwsh -NoLogo -NoProfile -File $buildScript assert-package-version `
        -PackageOutput $mismatchedDirectory -PackageVersion '0.0.0-managed-local' 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Fail 'assert-package-version was expected to fail for a mismatched version but exited 0.'
    }
    $passed++
    Write-Host 'PASS: mismatched version fails' -ForegroundColor Green
} finally {
    Remove-Item -LiteralPath $mismatchedDirectory -Recurse -Force -ErrorAction SilentlyContinue
}

# 3. Empty directory: no nupkgs at all -> fails, not a silent no-op pass.
$emptyDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "ahtola-assert-version-$([System.Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $emptyDirectory -Force | Out-Null
try {
    & pwsh -NoLogo -NoProfile -File $buildScript assert-package-version `
        -PackageOutput $emptyDirectory -PackageVersion '0.0.0-ci.74' 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Fail 'assert-package-version was expected to fail with no packages present but exited 0.'
    }
    $passed++
    Write-Host 'PASS: empty package directory fails' -ForegroundColor Green
} finally {
    Remove-Item -LiteralPath $emptyDirectory -Recurse -Force -ErrorAction SilentlyContinue
}

# 4. Missing -PackageVersion on the task itself -> fails with a clear message.
& pwsh -NoLogo -NoProfile -File $buildScript assert-package-version 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
    Fail "assert-package-version was expected to require -PackageVersion but exited 0."
}
$passed++
Write-Host 'PASS: missing -PackageVersion fails' -ForegroundColor Green

Write-Host "Test-AssertExactPackageVersion passed ($passed checks)." -ForegroundColor Green
exit 0
