@{
    RootModule = 'Devolutions.Ahtola.Sqlite.psm1'
    ModuleVersion = '0.1.0'
    GUID = 'b7c2f0d1-8a4e-4f6b-9c3d-2e1a0b9f8d7c'
    Author = 'Devolutions'
    CompanyName = 'Devolutions'
    Copyright = '(c) Devolutions. All rights reserved.'
    Description = 'PowerShell SQLite module backed by the pure-managed Ahtola engine (cloned from synedgy.PSSqlite). PowerShell 7+ / .NET 8+ only.'
    PowerShellVersion = '7.4'
    CompatiblePSEditions = @('Core')
    NestedModules = @('bin\Devolutions.Ahtola.PowerShell.dll')
    FunctionsToExport = @()
    CmdletsToExport = @(
        'Backup-AhtolaSqliteDatabase'
        'Checkpoint-AhtolaSqliteDatabase'
        'Clear-AhtolaSqliteConnectionPool'
        'Clear-AhtolaSqlitePassword'
        'Close-AhtolaSqliteConnection'
        'Compare-AhtolaSqliteDatabaseVersion'
        'Complete-AhtolaSqliteTransaction'
        'Export-AhtolaSqliteTable'
        'Get-AhtolaSqliteDatabaseInfo'
        'Get-AhtolaSqliteDatabaseMetadata'
        'Get-AhtolaSqliteIndex'
        'Get-AhtolaSqliteRow'
        'Get-AhtolaSqliteSchema'
        'Get-AhtolaSqliteTable'
        'Import-AhtolaSqliteTable'
        'Invoke-AhtolaSqliteBulkCopy'
        'Invoke-AhtolaSqliteMaintenance'
        'Invoke-AhtolaSqliteQuery'
        'Invoke-AhtolaSqliteReplicaSync'
        'New-AhtolaSqliteConnection'
        'New-AhtolaSqliteRow'
        'Optimize-AhtolaSqliteDatabase'
        'Remove-AhtolaSqliteRow'
        'Save-AhtolaSqliteTransaction'
        'Set-AhtolaSqlitePassword'
        'Set-AhtolaSqliteRow'
        'Start-AhtolaSqliteTransaction'
        'Test-AhtolaSqliteConnection'
        'Test-AhtolaSqliteIntegrity'
        'Undo-AhtolaSqliteTransaction'
    )
    VariablesToExport = @()
    AliasesToExport = @()
    ScriptsToProcess = @('ScriptsToProcess\PreLoadTypes.ps1')
    PrivateData = @{
        PSData = @{
            Tags = @('SQLite', 'Ahtola', 'Database', 'CRUD', 'Devolutions')
            LicenseUri = 'https://opensource.org/licenses/MIT'
            ProjectUri = 'https://github.com/Devolutions/ahtola'
            ReleaseNotes = 'Initial Ahtola-backed clone of the synedgy.PSSqlite C# port.'
        }
    }
}
