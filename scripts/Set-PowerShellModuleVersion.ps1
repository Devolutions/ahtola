#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ManifestPath,

    [Parameter(Mandatory)]
    [string]$RequestedVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$versionMatch = [regex]::Match(
    $RequestedVersion,
    '^(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?(?:-([0-9A-Za-z][0-9A-Za-z.-]*))?$')
if (-not $versionMatch.Success) {
    throw "PowerShell module version '$RequestedVersion' is not a supported semantic version."
}
if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
    throw "PowerShell module manifest not found: $ManifestPath"
}

$moduleVersion = '{0}.{1}.{2}' -f
    $versionMatch.Groups[1].Value,
    $versionMatch.Groups[2].Value,
    $versionMatch.Groups[3].Value
$prereleaseParts = [System.Collections.Generic.List[string]]::new()
if ($versionMatch.Groups[4].Success) {
    if ($versionMatch.Groups[5].Success) {
        $prereleaseParts.Add("rev$($versionMatch.Groups[4].Value)")
    } else {
        $moduleVersion += ".$($versionMatch.Groups[4].Value)"
    }
}
if ($versionMatch.Groups[5].Success) {
    $prereleaseParts.Add($versionMatch.Groups[5].Value.Replace('.', '-'))
}
$manifestPrerelease = $prereleaseParts -join '-'
if (-not [string]::IsNullOrWhiteSpace($manifestPrerelease) -and
    $manifestPrerelease -notmatch '^[0-9A-Za-z-]+$') {
    throw "Requested version '$RequestedVersion' cannot be represented as a PowerShell prerelease label."
}

$manifest = Get-Content -LiteralPath $ManifestPath -Raw
if ($manifest -notmatch '(?m)^\s*ModuleVersion\s*=\s*''[^'']+''') {
    throw "Could not locate ModuleVersion in staged manifest $ManifestPath"
}

$updatedManifest = $manifest -replace '(?m)^(\s*ModuleVersion\s*=\s*)''[^'']+''', "`$1'$moduleVersion'"
$updatedManifest = $updatedManifest -replace "(?m)^\s*Prerelease\s*=\s*'[^']*'\s*\r?\n?", ''
$updatedManifest = $updatedManifest -replace "(?m)^\s*AhtolaPackageVersion\s*=\s*'[^']*'\s*\r?\n?", ''

$privateDataPattern = '(?m)^(\s*)PrivateData\s*=\s*@\{\s*$'
$privateDataMatch = [regex]::Match($updatedManifest, $privateDataPattern)
if (-not $privateDataMatch.Success) {
    throw "Could not locate PrivateData in staged manifest $ManifestPath"
}
$privateDataIndent = $privateDataMatch.Groups[1].Value
$privateDataReplacement = $privateDataMatch.Value +
    [Environment]::NewLine +
    "$privateDataIndent    AhtolaPackageVersion = '$RequestedVersion'"
$updatedManifest = [regex]::Replace(
    $updatedManifest,
    $privateDataPattern,
    [System.Text.RegularExpressions.MatchEvaluator] { param($match) $privateDataReplacement },
    1)

if (-not [string]::IsNullOrWhiteSpace($manifestPrerelease)) {
    $psDataPattern = '(?m)^(\s*)PSData\s*=\s*@\{\s*$'
    $psDataMatch = [regex]::Match($updatedManifest, $psDataPattern)
    if (-not $psDataMatch.Success) {
        throw "Could not locate PSData in staged manifest $ManifestPath"
    }
    $psDataIndent = $psDataMatch.Groups[1].Value
    $psDataReplacement = $psDataMatch.Value +
        [Environment]::NewLine +
        "$psDataIndent    Prerelease = '$manifestPrerelease'"
    $updatedManifest = [regex]::Replace(
        $updatedManifest,
        $psDataPattern,
        [System.Text.RegularExpressions.MatchEvaluator] { param($match) $psDataReplacement },
        1)
}

Set-Content -LiteralPath $ManifestPath -Value $updatedManifest -Encoding utf8NoBOM

# Test-ModuleManifest applies the same schema checks used by PowerShell publishing.
$null = Test-ModuleManifest -Path $ManifestPath -ErrorAction Stop
$stagedManifest = Import-PowerShellDataFile -LiteralPath $ManifestPath
$privateData = $stagedManifest['PrivateData']
if ($privateData -isnot [System.Collections.IDictionary] -or
    -not $privateData.Contains('AhtolaPackageVersion') -or
    $privateData['AhtolaPackageVersion'] -ne $RequestedVersion) {
    throw "Staged PowerShell module does not retain requested package version '$RequestedVersion'."
}

$psData = $privateData['PSData']
$actualPrerelease = ''
if ($psData -is [System.Collections.IDictionary] -and $psData.Contains('Prerelease')) {
    $actualPrerelease = [string]$psData['Prerelease']
}
if ($stagedManifest['ModuleVersion'].ToString() -ne $moduleVersion -or
    $actualPrerelease -ne $manifestPrerelease) {
    throw "Staged PowerShell manifest version does not match the publishable mapping for '$RequestedVersion'."
}

Write-Host "Staged PowerShell manifest maps package version '$RequestedVersion' to '$moduleVersion$(
    if ($manifestPrerelease) { "-$manifestPrerelease" })'." -ForegroundColor Green
