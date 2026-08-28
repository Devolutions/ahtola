#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$CoveragePath,
    [string]$BaselinePath = './code-coverage-baseline.json'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

function Fail([string]$Message) {
    throw "Code coverage validation failed: $Message"
}

function Get-Rate(
    [System.Xml.XmlElement]$Element,
    [string]$Attribute,
    [string]$Context
) {
    $raw = $Element.GetAttribute($Attribute)
    [decimal]$rate = 0
    if ([string]::IsNullOrWhiteSpace($raw) -or
        -not [decimal]::TryParse(
            $raw,
            [System.Globalization.NumberStyles]::Float,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$rate
        ) -or
        $rate -lt 0 -or
        $rate -gt 1) {
        Fail "$Context has invalid $Attribute '$raw'; expected a decimal from 0 through 1."
    }
    return $rate
}

function Get-MinimumRate(
    [object]$Assembly,
    [string]$Property
) {
    $propertyValue = $Assembly.PSObject.Properties[$Property]
    if ($null -eq $propertyValue) {
        Fail "baseline assembly '$($Assembly.name)' is missing '$Property'."
    }

    [decimal]$rate = 0
    $raw = [string]$propertyValue.Value
    if (-not [decimal]::TryParse(
            $raw,
            [System.Globalization.NumberStyles]::Float,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$rate
        ) -or
        $rate -lt 0 -or
        $rate -gt 1) {
        Fail "baseline assembly '$($Assembly.name)' has invalid $Property '$raw'; expected a decimal from 0 through 1."
    }
    return $rate
}

if (-not (Test-Path -LiteralPath $CoveragePath -PathType Leaf)) {
    Fail "Cobertura report '$CoveragePath' does not exist."
}
if (-not (Test-Path -LiteralPath $BaselinePath -PathType Leaf)) {
    Fail "baseline '$BaselinePath' does not exist."
}

try {
    [xml]$coverage = Get-Content -LiteralPath $CoveragePath -Raw
} catch {
    Fail "Cobertura report '$CoveragePath' is not valid XML: $($_.Exception.Message)"
}

try {
    $baseline = Get-Content -LiteralPath $BaselinePath -Raw | ConvertFrom-Json
} catch {
    Fail "baseline '$BaselinePath' is not valid JSON: $($_.Exception.Message)"
}

if ($baseline.version -ne 1) {
    Fail "baseline '$BaselinePath' has unsupported version '$($baseline.version)'."
}

$packages = @($coverage.SelectNodes("/coverage/packages/package"))
if ($packages.Count -eq 0) {
    Fail "Cobertura report '$CoveragePath' contains no package coverage."
}

$packagesByName = @{}
foreach ($package in $packages) {
    $name = $package.GetAttribute('name')
    if ([string]::IsNullOrWhiteSpace($name)) {
        Fail "Cobertura report '$CoveragePath' contains an unnamed package."
    }
    if ($packagesByName.ContainsKey($name)) {
        Fail "Cobertura report '$CoveragePath' contains duplicate package '$name'."
    }
    $packagesByName[$name] = $package
}

$assemblies = @($baseline.assemblies)
if ($assemblies.Count -eq 0) {
    Fail "baseline '$BaselinePath' contains no assemblies."
}

$failures = [System.Collections.Generic.List[string]]::new()
foreach ($assembly in $assemblies) {
    $name = [string]$assembly.name
    if ([string]::IsNullOrWhiteSpace($name)) {
        Fail "baseline '$BaselinePath' contains an assembly without a name."
    }
    if (-not $packagesByName.ContainsKey($name)) {
        $failures.Add("$name is absent from the coverage report")
        continue
    }

    $package = $packagesByName[$name]
    $lineRate = Get-Rate $package 'line-rate' "coverage package '$name'"
    $branchRate = Get-Rate $package 'branch-rate' "coverage package '$name'"
    $minimumLineRate = Get-MinimumRate $assembly 'minimumLineRate'
    $minimumBranchRate = Get-MinimumRate $assembly 'minimumBranchRate'

    Write-Host ("{0}: line {1:P2} (minimum {2:P2}), branch {3:P2} (minimum {4:P2})" -f `
            $name, $lineRate, $minimumLineRate, $branchRate, $minimumBranchRate)

    if ($lineRate -lt $minimumLineRate) {
        $failures.Add("$name line coverage $($lineRate.ToString('P2')) is below $($minimumLineRate.ToString('P2'))")
    }
    if ($branchRate -lt $minimumBranchRate) {
        $failures.Add("$name branch coverage $($branchRate.ToString('P2')) is below $($minimumBranchRate.ToString('P2'))")
    }
}

if ($failures.Count -gt 0) {
    Fail ($failures -join '; ')
}

Write-Host "Code coverage satisfies all $($assemblies.Count) assembly baselines." -ForegroundColor Green
exit 0
