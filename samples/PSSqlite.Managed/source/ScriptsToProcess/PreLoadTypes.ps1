#Requires -Version 7.0

<#
.SYNOPSIS
    Loads the managed Ahtola assemblies required by the PSSqlite.Managed module.

.DESCRIPTION
    Loads Devolutions.Ahtola.Core.dll, Devolutions.Ahtola.Data.dll, and
    Devolutions.Ahtola.Data.Sqlite.dll directly via
    Assembly.LoadFrom from the module's vendored lib/net8.0 folder. Load order
    matters because Devolutions.Ahtola.Data.Sqlite depends on
    Devolutions.Ahtola.Data, which in turn depends on Devolutions.Ahtola.Core.

    Assembly file names and NuGet package IDs use the Devolutions.Ahtola.* prefix;
    public namespaces remain Ahtola.*.

    There is no native e_sqlite3/SQLitePCLRaw binary involved: this module only
    ever talks to the fully managed Devolutions.Ahtola.Data.Sqlite provider
    (Local Provider=Managed), so there is no native library path/RID resolution
    to worry about, and no net48 fallback branch.
#>

$libDir = Join-Path -Path $PSScriptRoot -ChildPath '..\lib\net8.0'
$libDir = [System.IO.Path]::GetFullPath($libDir)

# Order matters: Devolutions.Ahtola.Core -> Devolutions.Ahtola.Data -> Devolutions.Ahtola.Data.Sqlite.
$assemblyNames = @('Devolutions.Ahtola.Core.dll', 'Devolutions.Ahtola.Data.dll', 'Devolutions.Ahtola.Data.Sqlite.dll')

foreach ($assemblyName in $assemblyNames) {
    $assemblyPath = Join-Path -Path $libDir -ChildPath $assemblyName
    if (-not (Test-Path -LiteralPath $assemblyPath)) {
        throw "PSSqlite.Managed: required assembly '$assemblyName' was not found at '$assemblyPath'. " +
            "Run build.ps1 from the sample root to vendor the managed Ahtola assemblies before importing this module."
    }

    [System.Reflection.Assembly]::LoadFrom($assemblyPath) | Out-Null
}
