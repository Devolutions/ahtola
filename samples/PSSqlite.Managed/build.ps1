#Requires -Version 7.0

<#
.SYNOPSIS
    Builds PSSqlite.Managed and vendors the managed Ahtola assemblies used by the module.

.DESCRIPTION
    Runs `dotnet build` against PSSqlite.Managed.csproj. The csproj restores
    Devolutions.Ahtola.Data.Sqlite from the local NuGet feed configured in nuget.config
    (ahtola/artifacts/nupkg) and copies the three managed assemblies
    (Devolutions.Ahtola.Core.dll, Devolutions.Ahtola.Data.dll,
    Devolutions.Ahtola.Data.Sqlite.dll) into source/lib/net8.0 via an MSBuild target
    that runs after Build.
#>

$ErrorActionPreference = 'Stop'

$sampleRoot = $PSScriptRoot
$csproj = Join-Path -Path $sampleRoot -ChildPath 'PSSqlite.Managed.csproj'
$vendorDir = Join-Path -Path $sampleRoot -ChildPath 'source\lib\net8.0'

Write-Host "Building $csproj ..."
dotnet build $csproj -c Debug
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

Write-Host ''
if (Test-Path -LiteralPath $vendorDir) {
    Write-Host "Vendored managed Ahtola assemblies at: $vendorDir"
    Get-ChildItem -LiteralPath $vendorDir -Filter '*.dll' | ForEach-Object {
        Write-Host "  $($_.Name)"
    }
}
else {
    throw "Expected vendored assemblies were not found at '$vendorDir'."
}

Write-Host ''
Write-Host 'Next steps:'
Write-Host "  Import-Module $(Join-Path -Path $sampleRoot -ChildPath 'source\PSSqlite.Managed.psd1')"
Write-Host '  Start-ManagedSample'
