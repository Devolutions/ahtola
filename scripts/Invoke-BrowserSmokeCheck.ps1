#Requires -Version 7.0
<#
.SYNOPSIS
    Runs the packed browser consumer smoke probe for one engine, downgrading
    only a specific, proven capability gap in the test engine to a visible
    notice instead of a failing check.

.DESCRIPTION
    Playwright's bundled WebKit build does not implement the Origin Private
    File System API at all (verified directly against the pinned Playwright
    version on Linux: navigator.storage.getDirectory is undefined), so
    Ahtola's capability probe correctly and safely refuses to initialize
    OPFS-backed storage. That is a limitation of the automated test engine,
    not of Ahtola, and not of real-world Safari/WebKit, which has supported
    OPFS synchronous access handles since 16.4. Failing the "Browser smoke
    (webkit)" check for that reason would hide real signal behind permanent,
    unfixable noise.

    Every other failure still fails this script: an application FAIL status
    for any other reason, a timeout, an unexpected exception, or a missing
    capability WebKit is expected to have (cross-origin isolation,
    SharedArrayBuffer, Web Locks) all propagate as a real failure. Only a
    PlatformNotSupportedException whose reported missing capabilities are a
    non-empty subset of exactly the known OPFS-related gap is downgraded.
#>
[CmdletBinding()]
param(
    [string]$PackageDirectory = './artifacts/managed-packages',

    [Parameter(Mandatory)]
    [string]$PackageVersion,

    [Parameter(Mandatory)]
    [ValidateSet('chromium', 'firefox', 'webkit')]
    [string]$BrowserEngine,

    # Capabilities Playwright's bundled WebKit is proven to lack. A failure
    # whose missing-capability set is a non-empty subset of exactly these
    # names is the known gap; anything else - including one of these normally
    # present capabilities (cross-origin isolation, SharedArrayBuffer, Web
    # Locks) also going missing - is treated as a real failure.
    [string[]]$KnownGapMissingCapabilities = @(
        'Origin Private File System',
        'Origin Private File System synchronous access handles'
    )
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

function Fail([string]$Message) {
    throw "Browser smoke check failed: $Message"
}

$consumerRunner = Join-Path $PSScriptRoot 'Invoke-BrowserPackageConsumer.ps1'
if (-not (Test-Path -LiteralPath $consumerRunner -PathType Leaf)) {
    Fail "browser consumer runner not found: $consumerRunner"
}

$capturedLines = [System.Collections.Generic.List[string]]::new()
$failureMessage = $null
try {
    & $consumerRunner `
        -PackageDirectory $PackageDirectory `
        -PackageVersion $PackageVersion `
        -BrowserEngine $BrowserEngine *>&1 |
        ForEach-Object {
            $line = $_.ToString()
            $capturedLines.Add($line)
            Write-Host $line
        }
}
catch {
    $failureMessage = $_.Exception.Message
    $capturedLines.Add($failureMessage)
}

if (-not $failureMessage) {
    Write-Host "$BrowserEngine browser smoke passed; nothing to downgrade." -ForegroundColor Green
    exit 0
}

if ($BrowserEngine -ne 'webkit') {
    Fail $failureMessage
}

$combinedOutput = $capturedLines -join "`n"
$match = [regex]::Match(
    $combinedOutput,
    'FAIL:PlatformNotSupportedException:.*?Missing in this browser: (?<missing>[^.]+)\.')
if (-not $match.Success) {
    Fail "webkit probe failed with an error that is not the known, structured OPFS capability gap. $failureMessage"
}

$missing = @(
    $match.Groups['missing'].Value -split ',\s*' |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ }
)
if ($missing.Count -eq 0) {
    Fail "webkit probe reported a capability failure with no missing capabilities parsed; treating as a real failure. $failureMessage"
}

$unexpectedlyMissing = @($missing | Where-Object { $KnownGapMissingCapabilities -notcontains $_ })
if ($unexpectedlyMissing.Count -gt 0) {
    Fail ("webkit is missing capabilities beyond the known OPFS test-engine gap " +
        "($($unexpectedlyMissing -join ', ')); this looks like a real regression, not the documented WebKit limitation.")
}

$notice = "Playwright's bundled WebKit does not implement the Origin Private File System API used for " +
    "Ahtola browser persistence (missing: $($missing -join ', ')). This is a known limitation of the " +
    "WebKit test engine (verified directly against the pinned Playwright version), not an Ahtola " +
    "regression and not a real-world Safari/WebKit capability gap. Downgrading this run to informational."
Write-Host "::notice title=Known WebKit OPFS test-engine gap::$notice"
if ($env:GITHUB_STEP_SUMMARY) {
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value "- :information_source: $notice"
}

exit 0
