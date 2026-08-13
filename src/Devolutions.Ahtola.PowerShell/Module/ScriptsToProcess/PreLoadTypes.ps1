# Pure-managed Ahtola stack — no native e_sqlite3 / SQLitePCLRaw preload.
$moduleRoot = Split-Path -Path $PSScriptRoot -Parent
$binPath = Join-Path -Path $moduleRoot -ChildPath 'bin'

if (-not (Test-Path -LiteralPath $binPath)) {
    Write-Error "Devolutions.Ahtola.Sqlite bin folder not found: $binPath"
    return
}

# Load dependency assemblies before the binary module binds types.
$preferredOrder = @(
    'YamlDotNet.dll'
    'Ahtola.Core.dll'
    'Ahtola.Data.dll'
    'Ahtola.Data.Sqlite.dll'
    'Devolutions.Ahtola.PowerShell.dll'
)

$loadedNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($assembly in [AppDomain]::CurrentDomain.GetAssemblies()) {
    try {
        $name = [System.IO.Path]::GetFileName($assembly.Location)
        if ($name) {
            [void]$loadedNames.Add($name)
        }
    }
    catch {
        # Dynamic assemblies have no Location.
    }
}

function Import-AhtolaAssembly {
    param([Parameter(Mandatory)][string]$Path)

    $fileName = [System.IO.Path]::GetFileName($Path)
    if ($loadedNames.Contains($fileName)) {
        Write-Verbose "Assembly already loaded: $fileName"
        return
    }

    Write-Verbose "Loading assembly: $Path"
    $null = [System.Reflection.Assembly]::LoadFrom($Path)
    [void]$loadedNames.Add($fileName)
}

foreach ($name in $preferredOrder) {
    $path = Join-Path -Path $binPath -ChildPath $name
    if (Test-Path -LiteralPath $path) {
        Import-AhtolaAssembly -Path $path
    }
}

Get-ChildItem -LiteralPath $binPath -Filter '*.dll' |
    Where-Object { -not $loadedNames.Contains($_.Name) } |
    ForEach-Object { Import-AhtolaAssembly -Path $_.FullName }
