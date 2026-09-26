#Requires -Version 7.0
<#
.SYNOPSIS
    Regenerates src/Ahtola.Core/Search/ManagedUnicodeData.g.cs.

.DESCRIPTION
    The managed FTS tokenizers classify, case-map and decompose characters from a pinned static
    table instead of the host's ICU, so tokenization is identical on every runtime, OS and in
    globalization-invariant browser builds. This script exports the Alphabetic property and
    canonical decompositions from Node's bundled ICU, then restricts them to the Unicode 16.0
    repertoire with a .NET 10 generator running in invariant mode.

    Requires Node.js with full ICU (Unicode >= 16) and the .NET 10 SDK.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$exporter = Join-Path $PSScriptRoot 'Export-UnicodeProperties.mjs'
$generator = Join-Path $PSScriptRoot 'GenerateManagedUnicodeData.cs'
$output = Join-Path $repoRoot 'src' 'Ahtola.Core' 'Search' 'ManagedUnicodeData.g.cs'
$export = Join-Path ([System.IO.Path]::GetTempPath()) "ahtola-unicode-$([guid]::NewGuid().ToString('N')).json"

try {
    & node $exporter $export
    if ($LASTEXITCODE -ne 0) { throw "Export-UnicodeProperties.mjs failed with exit code $LASTEXITCODE" }

    & dotnet run $generator -- $export $output
    if ($LASTEXITCODE -ne 0) { throw "GenerateManagedUnicodeData.cs failed with exit code $LASTEXITCODE" }
}
finally {
    Remove-Item -LiteralPath $export -Force -ErrorAction SilentlyContinue
}
