#Requires -Version 7.0

<#
.SYNOPSIS
    Demo functions wiring PowerShell to the fully managed Devolutions.Ahtola.Data.Sqlite provider (Ahtola.Data.Sqlite types).

.DESCRIPTION
    This module never touches the native e_sqlite3/SQLitePCLRaw binaries. Every
    connection string opts into "Local Provider=Managed", which routes all
    reads/writes through Ahtola's managed storage engine.
#>

$script:ManagedConnectionString = 'Data Source=:memory:;Cache=Shared;Local Provider=Managed'

function New-ManagedConnection {
    <#
    .SYNOPSIS
        Creates and opens a Ahtola.Data.Sqlite.SqliteConnection backed entirely
        by the managed provider.

    .PARAMETER ConnectionString
        Optional override. Defaults to an in-memory, shared-cache, managed-only
        connection string.

    .OUTPUTS
        Ahtola.Data.Sqlite.SqliteConnection
    #>
    [CmdletBinding()]
    [OutputType([Ahtola.Data.Sqlite.SqliteConnection])]
    param(
        [Parameter()]
        [string]$ConnectionString = $script:ManagedConnectionString
    )

    $connection = [Ahtola.Data.Sqlite.SqliteConnection]::new($ConnectionString)
    $connection.Open()
    return $connection
}

function Invoke-ManagedQuery {
    <#
    .SYNOPSIS
        Runs a SQL command against an open managed connection and returns rows.

    .PARAMETER Connection
        An open Ahtola.Data.Sqlite.SqliteConnection.

    .PARAMETER CommandText
        The SQL text to execute.

    .PARAMETER Parameters
        Optional hashtable of named parameters (e.g. @{ '@id' = 1 }).

    .OUTPUTS
        PSCustomObject rows, one per result row, when the command returns a
        result set; otherwise the affected row count.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [Ahtola.Data.Sqlite.SqliteConnection]$Connection,

        [Parameter(Mandatory)]
        [string]$CommandText,

        [Parameter()]
        [hashtable]$Parameters
    )

    $command = $Connection.CreateCommand()
    try {
        $command.CommandText = $CommandText

        if ($Parameters) {
            foreach ($key in $Parameters.Keys) {
                [void]$command.Parameters.AddWithValue($key, $Parameters[$key])
            }
        }

        $reader = $command.ExecuteReader()
        try {
            if ($reader.FieldCount -eq 0) {
                return
            }

            $columnNames = 0..($reader.FieldCount - 1) | ForEach-Object { $reader.GetName($_) }

            while ($reader.Read()) {
                $row = [ordered]@{}
                foreach ($columnName in $columnNames) {
                    $row[$columnName] = $reader[$columnName]
                }

                [PSCustomObject]$row
            }
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $command.Dispose()
    }
}

function Start-ManagedSample {
    <#
    .SYNOPSIS
        End-to-end demo: opens a managed connection, creates a metadata table,
        inserts a row, reads it back, prints it, then clears connection pools.
    #>
    [CmdletBinding()]
    param()

    $connection = New-ManagedConnection
    try {
        Write-Host "Opened managed connection: $script:ManagedConnectionString"

        $createTable = @'
CREATE TABLE IF NOT EXISTS sample_metadata (
    id INTEGER PRIMARY KEY,
    key TEXT NOT NULL,
    value TEXT NOT NULL
);
'@
        Invoke-ManagedQuery -Connection $connection -CommandText $createTable | Out-Null

        $insertCommand = $connection.CreateCommand()
        try {
            $insertCommand.CommandText = 'INSERT INTO sample_metadata (key, value) VALUES (@key, @value);'
            [void]$insertCommand.Parameters.AddWithValue('@key', 'provider')
            [void]$insertCommand.Parameters.AddWithValue('@value', 'Ahtola.Data.Sqlite (Managed)')
            [void]$insertCommand.ExecuteNonQuery()
        }
        finally {
            $insertCommand.Dispose()
        }

        $rows = Invoke-ManagedQuery -Connection $connection -CommandText 'SELECT id, key, value FROM sample_metadata;'
        $rows | Format-Table -AutoSize | Out-String | Write-Host

        return $rows
    }
    finally {
        $connection.Close()
        $connection.Dispose()

        # Managed pooling still applies to :memory:;Cache=Shared connections;
        # clear pools so no pooled state lingers after the demo runs.
        [Ahtola.Data.Sqlite.SqliteConnection]::ClearAllPools()
    }
}

Export-ModuleMember -Function New-ManagedConnection, Invoke-ManagedQuery, Start-ManagedSample
