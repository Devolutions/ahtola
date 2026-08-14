#Requires -Version 7.4
<#
.SYNOPSIS
    Validates PlatyPS Markdown and generates MAML help for a staged module.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ModulePath,

    [Parameter(Mandatory)]
    [string]$MarkdownPath,

    [version]$MinimumPlatyPSVersion = '1.0.3'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ModulePath = [System.IO.Path]::GetFullPath($ModulePath)
$MarkdownPath = [System.IO.Path]::GetFullPath($MarkdownPath)

if (-not (Test-Path -LiteralPath $ModulePath -PathType Container)) {
    throw "Staged PowerShell module path not found: $ModulePath"
}

if (-not (Test-Path -LiteralPath $MarkdownPath -PathType Container)) {
    throw "PlatyPS Markdown help path not found: $MarkdownPath"
}

$platyPS = Get-Module -ListAvailable -Name Microsoft.PowerShell.PlatyPS |
    Where-Object { $_.Version -ge $MinimumPlatyPSVersion -and $_.Version.Major -eq $MinimumPlatyPSVersion.Major } |
    Sort-Object Version -Descending |
    Select-Object -First 1
if (-not $platyPS) {
    throw "Microsoft.PowerShell.PlatyPS $MinimumPlatyPSVersion or newer is required. Install with: Install-Module Microsoft.PowerShell.PlatyPS -MinimumVersion $MinimumPlatyPSVersion -MaximumVersion $($MinimumPlatyPSVersion.Major).99.99 -Scope CurrentUser"
}

Import-Module $platyPS.Path -Force -ErrorAction Stop

$manifest = Get-ChildItem -LiteralPath $ModulePath -Filter '*.psd1' -File | Select-Object -First 1
if (-not $manifest) {
    throw "No module manifest was found in $ModulePath"
}

$moduleName = [System.IO.Path]::GetFileNameWithoutExtension($manifest.Name)
$importedModules = @(Import-Module $manifest.FullName -Force -PassThru -ErrorAction Stop)
$module = $importedModules |
    Where-Object { $_.Name -eq $moduleName } |
    Select-Object -First 1
if (-not $module) {
    throw "Could not import module '$moduleName' from $($manifest.FullName)"
}
try {
    $modulePage = Join-Path $MarkdownPath "$($module.Name).md"
    if (-not (Test-Path -LiteralPath $modulePage -PathType Leaf)) {
        throw "Module Markdown help file not found: $modulePage"
    }

    $commandFiles = @(
        Get-ChildItem -LiteralPath $MarkdownPath -Filter '*.md' -File |
            Where-Object { $_.FullName -ne $modulePage } |
            Sort-Object Name
    )
    if ($commandFiles.Count -eq 0) {
        throw "No command Markdown help files were found in $MarkdownPath"
    }

    $placeholderFiles = @(
        Get-ChildItem -LiteralPath $MarkdownPath -Filter '*.md' -File |
            Where-Object {
                (Get-Content -LiteralPath $_.FullName -Raw) -match '\{\{|\bFill (in|[A-Za-z]+ Description)\b|\bInsert list of aliases\b|\bAdd example description\b'
            }
    )
    if ($placeholderFiles.Count -gt 0) {
        throw "PlatyPS Markdown still contains placeholders: $($placeholderFiles.Name -join ', ')"
    }

    $moduleHelp = Import-MarkdownModuleFile -Path $modulePage
    $commandHelp = @($commandFiles.FullName | ForEach-Object { Import-MarkdownCommandHelp -Path $_ })

    $diagnostics = @(
        $moduleHelp.Diagnostics.Messages
        $commandHelp | ForEach-Object { $_.Diagnostics.Messages }
    )
    $errors = @($diagnostics | Where-Object { $_.Severity.ToString() -eq 'Error' })
    if ($errors.Count -gt 0) {
        $messages = $errors | ForEach-Object { "$($_.Source): $($_.Message)" }
        throw "PlatyPS Markdown validation failed:`n$($messages -join [Environment]::NewLine)"
    }

    $expectedCommands = @(
        Get-Command -Module $module.Name |
            Where-Object { $_.CommandType -eq 'Cmdlet' } |
            Select-Object -ExpandProperty Name |
            Sort-Object -Unique
    )
    $documentedCommands = @($commandHelp | Select-Object -ExpandProperty Title | Sort-Object -Unique)
    $missingCommands = @($expectedCommands | Where-Object { $_ -notin $documentedCommands })
    $orphanedCommands = @($documentedCommands | Where-Object { $_ -notin $expectedCommands })
    if ($missingCommands.Count -gt 0 -or $orphanedCommands.Count -gt 0) {
        $details = @()
        if ($missingCommands.Count -gt 0) {
            $details += "Missing command help: $($missingCommands -join ', ')"
        }
        if ($orphanedCommands.Count -gt 0) {
            $details += "Orphaned command help: $($orphanedCommands -join ', ')"
        }
        throw ($details -join [Environment]::NewLine)
    }

    $incompleteCommands = @(
        $commandHelp | Where-Object {
            [string]::IsNullOrWhiteSpace($_.Synopsis) -or
            [string]::IsNullOrWhiteSpace($_.Description) -or
            $_.Examples.Count -eq 0 -or
            @($_.Parameters | Where-Object { [string]::IsNullOrWhiteSpace($_.Description) }).Count -gt 0
        }
    )
    if ($incompleteCommands.Count -gt 0) {
        throw "PlatyPS Markdown is incomplete for: $($incompleteCommands.Title -join ', ')"
    }

    $externalHelpFiles = @($commandHelp | Select-Object -ExpandProperty ExternalHelpFile -Unique)
    if ($externalHelpFiles.Count -ne 1 -or [string]::IsNullOrWhiteSpace($externalHelpFiles[0])) {
        throw "Command help must define exactly one external help file. Found: $($externalHelpFiles -join ', ')"
    }

    $outputFolder = Join-Path $ModulePath 'en-US'
    New-Item -ItemType Directory -Path $outputFolder -Force | Out-Null
    $outputFile = Join-Path $outputFolder $externalHelpFiles[0]
    Remove-Item -LiteralPath $outputFile -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $outputFolder $module.Name) -Recurse -Force -ErrorAction SilentlyContinue
    $temporaryOutput = Join-Path ([System.IO.Path]::GetTempPath()) ("ahtola-platyps-maml-" + [guid]::NewGuid().ToString('N'))
    try {
        $commandHelp | Export-MamlCommandHelp -OutputFolder $temporaryOutput -Force | Out-Null
        $generatedMaml = Get-ChildItem -LiteralPath $temporaryOutput -Recurse -Filter $externalHelpFiles[0] -File |
            Select-Object -First 1
        if (-not $generatedMaml) {
            throw "PlatyPS did not generate expected MAML help file: $($externalHelpFiles[0])"
        }

        Copy-Item -LiteralPath $generatedMaml.FullName -Destination $outputFile -Force
    }
    finally {
        Remove-Item -LiteralPath $temporaryOutput -Recurse -Force -ErrorAction SilentlyContinue
    }

    if (-not (Test-Path -LiteralPath $outputFile -PathType Leaf)) {
        throw "Could not stage generated MAML help file: $outputFile"
    }

    Write-Host "Generated MAML help: $outputFile" -ForegroundColor Green
}
finally {
    Remove-Module $module.Name -Force -ErrorAction SilentlyContinue
}
