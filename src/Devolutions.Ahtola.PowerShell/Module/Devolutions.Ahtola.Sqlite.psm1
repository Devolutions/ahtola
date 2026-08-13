# Export Devolutions.Ahtola.Sqlite model types through module-qualified type accelerators.
# CLR types live in Ahtola.PSSqlite (binary assembly Devolutions.Ahtola.PowerShell).
$ExportableTypes = [ordered]@{
    'SqliteDBConfig' = 'Ahtola.PSSqlite.SQLiteDBConfig'
    'SqliteDBSchema' = 'Ahtola.PSSqlite.SqliteDBSchema'
    'SqliteView' = 'Ahtola.PSSqlite.SqliteView'
    'SqliteCheckTableConstraint' = 'Ahtola.PSSqlite.SqliteCheckTableConstraint'
    'SqlitePrimaryKeyTableConstraint' = 'Ahtola.PSSqlite.SqlitePrimaryKeyTableConstraint'
    'SqliteForeignKeyTableConstraint' = 'Ahtola.PSSqlite.SqliteForeignKeyTableConstraint'
    'SqliteIndexConstraint' = 'Ahtola.PSSqlite.SqliteIndexConstraint'
    'SqliteTable' = 'Ahtola.PSSqlite.SqliteTable'
    'SqliteColumn' = 'Ahtola.PSSqlite.SQLiteColumn'
    'SQLiteConstraint' = 'Ahtola.PSSqlite.SQLiteConstraint'
    'DBMigrationMode' = 'Ahtola.PSSqlite.DBMigrationMode'
    'SqliteConstraintType' = 'Ahtola.PSSqlite.SqliteConstraintType'
    'SqliteOrdering' = 'Ahtola.PSSqlite.SqliteOrdering'
    'SqliteTableOption' = 'Ahtola.PSSqlite.SqliteTableOption'
    'SqliteConnection' = 'Ahtola.Data.Sqlite.SqliteConnection'
}

function Get-CurrentModule {
    [OutputType([System.Management.Automation.PSModuleInfo])]
    param()
    $MyInvocation.MyCommand.ScriptBlock.Module
}

$typeAcceleratorsClass = [psobject].Assembly.GetType('System.Management.Automation.TypeAccelerators')
$moduleName = (Get-CurrentModule).Name
$existingTypeAccelerators = $typeAcceleratorsClass::Get

foreach ($typeToExport in $ExportableTypes.Keys) {
    $fullTypeToExport = '{0}.{1}' -f $moduleName, $typeToExport
    $type = $null
    foreach ($assembly in [AppDomain]::CurrentDomain.GetAssemblies()) {
        $type = $assembly.GetType($ExportableTypes[$typeToExport], $false)
        if ($type) {
            break
        }
    }

    if (-not $type) {
        $message = "Unable to register type accelerator '$fullTypeToExport' for '$($ExportableTypes[$typeToExport])' - type not found."
        throw [System.Management.Automation.ErrorRecord]::new(
            [System.InvalidOperationException]::new($message),
            'TypeAcceleratorTypeNotFound',
            [System.Management.Automation.ErrorCategory]::InvalidOperation,
            $fullTypeToExport)
    }

    if ($fullTypeToExport -in $existingTypeAccelerators.Keys) {
        Write-Warning "Overriding type accelerator '$fullTypeToExport' with '$($type.FullName)'."
    }
    else {
        Write-Verbose "Added type accelerator '$fullTypeToExport' for '$($type.FullName)'."
    }

    $null = $typeAcceleratorsClass::Add($fullTypeToExport, $type)
}

$MyInvocation.MyCommand.ScriptBlock.Module.OnRemove = {
    foreach ($typeName in $ExportableTypes.Keys) {
        $fullTypeToExport = '{0}.{1}' -f $moduleName, $typeName
        $null = $typeAcceleratorsClass::Remove($fullTypeToExport)
    }
}.GetNewClosure()
