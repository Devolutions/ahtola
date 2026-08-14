#Requires -Version 7.4
<#
.SYNOPSIS
    Scaffolds missing PlatyPS Markdown help files for the staged module.
#>
[CmdletBinding()]
param(
    [string]$ModulePath = (Join-Path $PSScriptRoot '..\artifacts\powershell-modules\Devolutions.Ahtola.Sqlite'),

    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\docs\powershell-help')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ModulePath = [System.IO.Path]::GetFullPath($ModulePath)
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$platyPS = Get-Module -ListAvailable -Name Microsoft.PowerShell.PlatyPS |
    Where-Object { $_.Version -ge [version]'1.0.3' -and $_.Version.Major -eq 1 } |
    Sort-Object Version -Descending |
    Select-Object -First 1
if (-not $platyPS) {
    throw 'Microsoft.PowerShell.PlatyPS 1.0.3 or newer is required. Install it before scaffolding help.'
}

$manifest = Get-ChildItem -LiteralPath $ModulePath -Filter '*.psd1' -File | Select-Object -First 1
if (-not $manifest) {
    throw "No staged module manifest was found in $ModulePath. Run './build.ps1 pack-powershell' first."
}

Import-Module $platyPS.Path -Force -ErrorAction Stop
$moduleName = [System.IO.Path]::GetFileNameWithoutExtension($manifest.Name)
$importedModules = @(Import-Module $manifest.FullName -Force -PassThru -ErrorAction Stop)
$module = $importedModules |
    Where-Object { $_.Name -eq $moduleName } |
    Select-Object -First 1
if (-not $module) {
    throw "Could not import module '$moduleName' from $($manifest.FullName)"
}
try {
    $temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("ahtola-platyps-" + [guid]::NewGuid().ToString('N'))
    try {
        New-MarkdownCommandHelp -ModuleInfo $module -OutputFolder $temporaryRoot -WithModulePage -Force | Out-Null
        $generatedPath = Join-Path $temporaryRoot $module.Name
        $destinationPath = Join-Path $OutputRoot $module.Name
        New-Item -ItemType Directory -Path $destinationPath -Force | Out-Null

        $copied = 0
        foreach ($file in Get-ChildItem -LiteralPath $generatedPath -Filter '*.md' -File) {
            $destination = Join-Path $destinationPath $file.Name
            if (Test-Path -LiteralPath $destination) {
                continue
            }

            Copy-Item -LiteralPath $file.FullName -Destination $destination
            $copied++
        }

        Write-Host "Created $copied missing PlatyPS Markdown help file(s) in $destinationPath" -ForegroundColor Green
    }
    finally {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
finally {
    Remove-Module $module.Name -Force -ErrorAction SilentlyContinue
}
