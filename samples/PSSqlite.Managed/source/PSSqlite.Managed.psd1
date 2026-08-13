@{
    RootModule        = 'PSSqlite.Managed.psm1'
    ModuleVersion     = '0.1.0'
    GUID              = 'b3d2f8e0-6a4a-4f7c-9a3b-2a7e6a1d9c10'
    Author            = 'Devolutions'
    CompanyName       = 'Devolutions'
    Copyright         = '(c) Devolutions. All rights reserved.'
    Description       = 'Minimal PowerShell 7 module sample wiring PowerShell to the fully managed Devolutions.Ahtola.Data.Sqlite provider (namespaces still Ahtola.*; no native e_sqlite3/SQLitePCLRaw binaries).'
    PowerShellVersion = '7.0'
    CompatiblePSEditions = @('Core')

    # Loads the vendored Ahtola.Core / Ahtola.Data / Ahtola.Data.Sqlite assemblies
    # (in that order) via Assembly.LoadFrom before the root module is imported.
    ScriptsToProcess  = @('ScriptsToProcess\PreLoadTypes.ps1')

    FunctionsToExport = @('New-ManagedConnection', 'Invoke-ManagedQuery', 'Start-ManagedSample')
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()

    PrivateData = @{
        PSData = @{
            Tags       = @('Ahtola', 'Sqlite', 'Managed', 'Devolutions')
            ProjectUri = 'https://github.com/Devolutions/ahtola'
        }
    }
}
