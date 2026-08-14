#Requires -Version 7.4
#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '6.0.0' }

param(
    [string]$ModulePath = (Join-Path $PSScriptRoot '..\..\..\artifacts\powershell-modules\Devolutions.Ahtola.Sqlite')
)

BeforeAll {
    $script:ModulePath = [System.IO.Path]::GetFullPath($ModulePath)
    $script:ManifestPath = Join-Path $script:ModulePath 'Devolutions.Ahtola.Sqlite.psd1'
    $script:ModuleName = 'Devolutions.Ahtola.Sqlite'

    Get-Module $script:ModuleName -All | Remove-Module -Force -ErrorAction SilentlyContinue
    Import-Module $script:ManifestPath -Force -ErrorAction Stop
}

AfterAll {
    try {
        [Ahtola.Data.Sqlite.SqliteConnection]::ClearAllPools()
    }
    catch {
        # Best effort when type already unloaded.
    }

    Get-Module $script:ModuleName -All | Remove-Module -Force -ErrorAction SilentlyContinue
}

Describe 'Devolutions.Ahtola.Sqlite module packaging' {
    It 'stages a loadable manifest' {
        Test-Path -LiteralPath $script:ManifestPath | Should-BeTrue
    }

    It 'exports the expected AhtolaSqlite cmdlets' {
        $commands = @(Get-Command -Module $script:ModuleName | Select-Object -ExpandProperty Name)
        $expected = @(
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

        foreach ($name in $expected) {
            $commands | Should-ContainCollection $name
        }

        $commands.Count | Should-Be $expected.Count
    }

    It 'does not export YAML configuration cmdlets' {
        foreach ($name in @(
                'Find-AhtolaSqliteConfigurationFile'
                'Import-AhtolaSqliteConfiguration'
                'Initialize-AhtolaSqliteDatabase')) {
            Get-Command $name -ErrorAction SilentlyContinue | Should-BeNull
        }
    }

    It 'loads the binary assembly Devolutions.Ahtola.PowerShell' {
        $assembly = [AppDomain]::CurrentDomain.GetAssemblies() |
            Where-Object { $_.GetName().Name -eq 'Devolutions.Ahtola.PowerShell' } |
            Select-Object -First 1

        $assembly | Should-NotBeNull
    }

    It 'registers module type accelerators for SQLiteDBConfig' {
        $type = [Devolutions.Ahtola.Sqlite.SqliteDBConfig]
        $type.FullName | Should-BeString 'Ahtola.PSSqlite.SQLiteDBConfig'
    }

    It 'exposes canonical parameters and documented compatibility aliases' {
        $connectionCommands = @(
            'Invoke-AhtolaSqliteQuery'
            'Get-AhtolaSqliteSchema'
            'Get-AhtolaSqliteTable'
            'Get-AhtolaSqliteIndex'
            'Get-AhtolaSqliteDatabaseInfo'
            'Test-AhtolaSqliteConnection'
            'Test-AhtolaSqliteIntegrity'
            'Checkpoint-AhtolaSqliteDatabase'
            'Optimize-AhtolaSqliteDatabase'
            'Clear-AhtolaSqliteConnectionPool'
            'Set-AhtolaSqlitePassword'
            'Clear-AhtolaSqlitePassword'
            'Export-AhtolaSqliteTable'
            'Import-AhtolaSqliteTable'
        )
        foreach ($name in $connectionCommands) {
            $parameter = (Get-Command $name).Parameters['Connection']
            $parameter | Should-NotBeNull
            $parameter.Aliases | Should-ContainCollection 'SqliteConnection'
        }

        foreach ($name in @(
                'Get-AhtolaSqliteRow'
                'New-AhtolaSqliteRow'
                'Set-AhtolaSqliteRow'
                'Remove-AhtolaSqliteRow')) {
            $parameters = (Get-Command $name).Parameters
            $parameters['Configuration'].Aliases | Should-ContainCollection 'SqliteDBConfig'
            $parameters['Table'].Aliases | Should-ContainCollection 'TableName'
        }

        (Get-Command New-AhtolaSqliteRow).Parameters['Values'].Aliases | Should-ContainCollection 'RowData'
        (Get-Command Get-AhtolaSqliteRow).Parameters['Where'].Aliases | Should-ContainCollection 'ClauseData'
    }

    It 'offers opt-in secure Turso Cloud parameters without emitting credentials under WhatIf' {
        $command = Get-Command New-AhtolaSqliteConnection
        $parameters = $command.Parameters
        $parameters['TursoUrl'] | Should-NotBeNull
        $parameters['AuthToken'].ParameterType | Should-Be ([securestring])
        $parameters['ReplicaPath'] | Should-NotBeNull
        $parameters['UseTursoEnvironment'] | Should-NotBeNull
        $parameters['SyncInterval'] | Should-NotBeNull
        (Get-Command Invoke-AhtolaSqliteReplicaSync).Parameters['ReplicaConnection'].ParameterType |
            Should-Be ([Ahtola.PSSqlite.AhtolaCloudConnection])

        $originalUrl = $env:TURSO_REMOTE_URL
        $originalToken = $env:TURSO_AUTH_TOKEN
        $secret = 'module-test-secret'
        try {
            $env:TURSO_REMOTE_URL = 'libsql://example.turso.io'
            $env:TURSO_AUTH_TOKEN = $secret
            $output = @(
                New-AhtolaSqliteConnection -UseTursoEnvironment -ReplicaPath ./replica.db -WhatIf 6>&1 |
                    Out-String
            ) -join [Environment]::NewLine

            $output.Contains($secret) | Should-BeFalse
            $output.Contains('TURSO_AUTH_TOKEN') | Should-BeFalse

            $secureToken = ConvertTo-SecureString $secret -AsPlainText -Force
            $errorText = try {
                New-AhtolaSqliteConnection -TursoUrl 'http://127.0.0.1:1' -AuthToken $secureToken -ErrorAction Stop
                throw 'Expected the unavailable local endpoint to fail.'
            }
            catch {
                $_ | Out-String
            }
            $errorText.Contains($secret) | Should-BeFalse
        }
        finally {
            $env:TURSO_REMOTE_URL = $originalUrl
            $env:TURSO_AUTH_TOKEN = $originalToken
        }
    }
}

Describe 'Devolutions.Ahtola.Sqlite connections and queries' {
    BeforeEach {
        $script:Connection = New-AhtolaSqliteConnection -ConnectionString 'Data Source=:memory:'
    }

    AfterEach {
        if ($null -ne $script:Connection) {
            try {
                if ($script:Connection.State -eq [System.Data.ConnectionState]::Open) {
                    $script:Connection.Close()
                }

                $script:Connection.Dispose()
            }
            catch {
                # ignore dispose races
            }

            $script:Connection = $null
        }

        Close-AhtolaSqliteConnection
    }

    It 'opens an in-memory connection and runs a scalar query' {
        $table = Invoke-AhtolaSqliteQuery `
            -Connection $script:Connection `
            -CommandText 'SELECT 1 AS value;' `
            -As DataTable

        $table | Should-NotBeNull
        $table.Rows.Count | Should-Be 1
        [int]$table.Rows[0]['value'] | Should-Be 1
    }

    It 'opens a caller-owned closed pipeline connection and returns PowerShell objects by default' {
        $connection = [Ahtola.Data.Sqlite.SqliteConnection]::new('Data Source=:memory:')
        try {
            $row = @($connection | Invoke-AhtolaSqliteQuery -CommandText 'SELECT 7 AS value;')

            $connection.State | Should-Be ([System.Data.ConnectionState]::Open)
            $row.Count | Should-Be 1
            [int]$row[0].value | Should-Be 7
        }
        finally {
            $connection | Close-AhtolaSqliteConnection -Confirm:$false
        }
    }

    It 'clears a pool without taking ownership and only closes when explicitly requested' {
        Clear-AhtolaSqliteConnectionPool -Connection $script:Connection -Confirm:$false
        $script:Connection.State | Should-Be ([System.Data.ConnectionState]::Open)

        Close-AhtolaSqliteConnection -Connection $script:Connection -WhatIf
        $script:Connection.State | Should-Be ([System.Data.ConnectionState]::Open)

        Close-AhtolaSqliteConnection -Connection $script:Connection -ClearPool -Confirm:$false
        $script:Connection.State | Should-Be ([System.Data.ConnectionState]::Closed)
        $script:Connection = $null
    }

    It 'creates read-only connections that reject writes' {
        $path = Join-Path ([System.IO.Path]::GetTempPath()) ("ahtola-readonly-" + [guid]::NewGuid().ToString('N') + '.sqlite')
        $writer = New-AhtolaSqliteConnection -ConnectionString "Data Source=$path;Pooling=False"
        try {
            $null = Invoke-AhtolaSqliteQuery -Connection $writer `
                -CommandText 'CREATE TABLE readonly_items(id INTEGER); INSERT INTO readonly_items VALUES (1);' `
                -As NonQuery
        }
        finally {
            $writer | Close-AhtolaSqliteConnection -Confirm:$false
        }

        $reader = New-AhtolaSqliteConnection -ConnectionString "Data Source=$path;Pooling=False" -ReadOnly
        try {
            [int](Invoke-AhtolaSqliteQuery -Connection $reader -CommandText 'SELECT COUNT(*) FROM readonly_items;' -As Scalar) | Should-Be 1
            {
                Invoke-AhtolaSqliteQuery -Connection $reader -CommandText 'INSERT INTO readonly_items VALUES (2);' -As NonQuery
            } | Should-Throw
        }
        finally {
            $reader | Close-AhtolaSqliteConnection -Confirm:$false
            if (Test-Path -LiteralPath $path) {
                Remove-Item -LiteralPath $path -Force
            }
        }
    }

    It 'returns scalar values and preserves an explicit open connection' {
        $script:Connection.State | Should-Be ([System.Data.ConnectionState]::Open)

        $value = Invoke-AhtolaSqliteQuery `
            -SqliteConnection $script:Connection `
            -CommandText 'SELECT 42;' `
            -As Scalar

        [int]$value | Should-Be 42
        $script:Connection.State | Should-Be ([System.Data.ConnectionState]::Open)
        Test-AhtolaSqliteConnection -SqliteConnection $script:Connection | Should-BeTrue
    }

    It 'binds parameters with $name style' {
        $null = Invoke-AhtolaSqliteQuery `
            -Connection $script:Connection `
            -CommandText 'CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT); INSERT INTO t(name) VALUES (''a''), (''b'');' `
            -As DataTable

        $table = Invoke-AhtolaSqliteQuery `
            -Connection $script:Connection `
            -CommandText 'SELECT id, name FROM t WHERE name = $name ORDER BY id;' `
            -Parameters @{ '$name' = 'b' } `
            -As DataTable

        $table.Rows.Count | Should-Be 1
        [string]$table.Rows[0]['name'] | Should-BeString 'b'
    }

    It 'materializes supported query result formats' {
        $null = Invoke-AhtolaSqliteQuery -Connection $script:Connection `
            -CommandText 'CREATE TABLE formats(id INTEGER); INSERT INTO formats VALUES (1), (2);' `
            -As NonQuery

        $dataSet = Invoke-AhtolaSqliteQuery -Connection $script:Connection `
            -CommandText 'SELECT id FROM formats ORDER BY id;' `
            -As DataSet
        $dataSet.Tables.Count | Should-Be 1
        $dataSet.Tables[0].Rows.Count | Should-Be 2

        $rows = @(Invoke-AhtolaSqliteQuery -Connection $script:Connection `
            -CommandText 'SELECT id FROM formats ORDER BY id;' `
            -As OrderedDictionary)
        $rows.Count | Should-Be 2
        [int]$rows[0]['id'] | Should-Be 1

        $reader = Invoke-AhtolaSqliteQuery -Connection $script:Connection `
            -CommandText 'SELECT id FROM formats ORDER BY id;' `
            -As DetachedDataReader
        $reader.Read() | Should-BeTrue
        [int]$reader['id'] | Should-Be 1
        $reader.Dispose()
    }

    It 'commits and rolls back explicit transactions' {
        $null = Invoke-AhtolaSqliteQuery `
            -SqliteConnection $script:Connection `
            -CommandText 'CREATE TABLE tx(id INTEGER PRIMARY KEY, value TEXT);' `
            -As NonQuery

        $transaction = Start-AhtolaSqliteTransaction -Connection $script:Connection
        $null = Invoke-AhtolaSqliteQuery `
            -SqliteConnection $script:Connection `
            -Transaction $transaction `
            -CommandText 'INSERT INTO tx(id, value) VALUES ($id, $value);' `
            -Parameters @{ '$id' = 1; '$value' = 'rolled-back' } `
            -As NonQuery
        Undo-AhtolaSqliteTransaction -Transaction $transaction -Confirm:$false

        [int](Invoke-AhtolaSqliteQuery -SqliteConnection $script:Connection -CommandText 'SELECT COUNT(*) FROM tx;' -As Scalar) | Should-Be 0

        $transaction = Start-AhtolaSqliteTransaction -Connection $script:Connection
        $null = Invoke-AhtolaSqliteQuery `
            -SqliteConnection $script:Connection `
            -Transaction $transaction `
            -CommandText 'INSERT INTO tx(id, value) VALUES (1, ''committed'');' `
            -As NonQuery
        Complete-AhtolaSqliteTransaction -Transaction $transaction -Confirm:$false

        [int](Invoke-AhtolaSqliteQuery -SqliteConnection $script:Connection -CommandText 'SELECT COUNT(*) FROM tx;' -As Scalar) | Should-Be 1
    }

    It 'supports savepoints and transaction pipeline input' {
        $null = Invoke-AhtolaSqliteQuery -Connection $script:Connection `
            -CommandText 'CREATE TABLE savepoints(id INTEGER PRIMARY KEY);' `
            -As NonQuery

        $transaction = $script:Connection | Start-AhtolaSqliteTransaction
        $transaction | Save-AhtolaSqliteTransaction -Name discarded
        $null = Invoke-AhtolaSqliteQuery -Connection $script:Connection `
            -Transaction $transaction `
            -CommandText 'INSERT INTO savepoints VALUES (1);' `
            -As NonQuery
        $transaction | Undo-AhtolaSqliteTransaction -SavepointName discarded -Confirm:$false
        $transaction | Complete-AhtolaSqliteTransaction -SavepointName discarded -Confirm:$false

        $transaction | Save-AhtolaSqliteTransaction -Name retained
        $null = Invoke-AhtolaSqliteQuery -Connection $script:Connection `
            -Transaction $transaction `
            -CommandText 'INSERT INTO savepoints VALUES (2);' `
            -As NonQuery
        $transaction | Complete-AhtolaSqliteTransaction -SavepointName retained -Confirm:$false
        $transaction | Complete-AhtolaSqliteTransaction -Confirm:$false

        [int](Invoke-AhtolaSqliteQuery -Connection $script:Connection -CommandText 'SELECT COUNT(*) FROM savepoints;' -As Scalar) | Should-Be 1
    }

    It 'supports schema inspection, integrity checks, and transactional bulk copy' {
        $null = Invoke-AhtolaSqliteQuery `
            -SqliteConnection $script:Connection `
            -CommandText 'CREATE TABLE bulk(id INTEGER PRIMARY KEY, name TEXT); CREATE INDEX bulk_name ON bulk(name);' `
            -As NonQuery

        @(
            [pscustomobject]@{ id = 1; name = 'one' }
            [pscustomobject]@{ id = 2; name = 'two' }
        ) | Invoke-AhtolaSqliteBulkCopy -Connection $script:Connection -Table bulk | Should-Be 2

        $schema = @(Get-AhtolaSqliteSchema -SqliteConnection $script:Connection -Collection Columns -RestrictionValues @($null, $null, 'bulk', $null))
        $schema.Count | Should-Be 2
        @($schema | Select-Object -ExpandProperty COLUMN_NAME) | Should-ContainCollection 'name'

        $integrity = @(Test-AhtolaSqliteIntegrity -Connection $script:Connection)
        $integrity.Count | Should-BeGreaterThan 0
        @(Invoke-AhtolaSqliteMaintenance -Connection $script:Connection -Operation IntegrityCheck -As OrderedDictionary -Confirm:$false).Count | Should-BeGreaterThan 0
        @(Get-AhtolaSqliteTable -Connection $script:Connection -Table bulk).Count | Should-Be 1
        @(Get-AhtolaSqliteIndex -Connection $script:Connection -Table bulk).Count | Should-Be 1
        (Get-AhtolaSqliteDatabaseInfo -Connection $script:Connection).PageSize | Should-BeGreaterThan 0
        @(Checkpoint-AhtolaSqliteDatabase -Connection $script:Connection -Confirm:$false).Count | Should-BeGreaterThan 0
        Optimize-AhtolaSqliteDatabase -Connection $script:Connection -Confirm:$false

        {
            @(
                [pscustomobject]@{ id = 3; name = 'three' }
                [pscustomobject]@{ id = 1; name = 'duplicate' }
            ) | Invoke-AhtolaSqliteBulkCopy -Connection $script:Connection -Table bulk
        } | Should-Throw
        [int](Invoke-AhtolaSqliteQuery -SqliteConnection $script:Connection -CommandText 'SELECT COUNT(*) FROM bulk;' -As Scalar) | Should-Be 2

        [pscustomobject]@{ id = 4; name = 'what-if' } |
            Invoke-AhtolaSqliteBulkCopy -Connection $script:Connection -Table bulk -WhatIf
        [int](Invoke-AhtolaSqliteQuery -SqliteConnection $script:Connection -CommandText 'SELECT COUNT(*) FROM bulk;' -As Scalar) | Should-Be 2
    }
}

Describe 'Devolutions.Ahtola.Sqlite backup and table interchange' {
    BeforeAll {
        $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("ahtola-sqlite-transfer-" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $script:TempRoot -Force | Out-Null
        $script:SourcePath = Join-Path $script:TempRoot 'source.sqlite'
        $script:DestinationPath = Join-Path $script:TempRoot 'destination.sqlite'
        $script:JsonPath = Join-Path $script:TempRoot 'items.json'
        $script:CsvPath = Join-Path $script:TempRoot 'items.csv'
        $script:QueryPath = Join-Path $script:TempRoot 'query.json'
    }

    AfterAll {
        Close-AhtolaSqliteConnection
        if (Test-Path -LiteralPath $script:TempRoot) {
            Remove-Item -LiteralPath $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'backs up databases and round-trips JSON and CSV tables' {
        $source = New-AhtolaSqliteConnection -ConnectionString "Data Source=$script:SourcePath;Pooling=False"
        $destination = New-AhtolaSqliteConnection -ConnectionString "Data Source=$script:DestinationPath;Pooling=False"
        try {
            $null = Invoke-AhtolaSqliteQuery `
                -SqliteConnection $source `
                -CommandText 'CREATE TABLE items(id INTEGER PRIMARY KEY, name TEXT, payload BLOB); INSERT INTO items VALUES (1, ''first'', X''0102''), (2, ''second'', NULL);' `
                -As NonQuery

            Backup-AhtolaSqliteDatabase -SourceConnection $source -DestinationConnection $destination -Confirm:$false
            [int](Invoke-AhtolaSqliteQuery -SqliteConnection $destination -CommandText 'SELECT COUNT(*) FROM items;' -As Scalar) | Should-Be 2

            Export-AhtolaSqliteTable -Connection $source -Table items -Path $script:JsonPath -Confirm:$false
            Export-AhtolaSqliteTable -Connection $source -Table items -Path $script:CsvPath -Confirm:$false
            Test-Path -LiteralPath $script:JsonPath | Should-BeTrue
            Test-Path -LiteralPath $script:CsvPath | Should-BeTrue

            $jsonTarget = New-AhtolaSqliteConnection -ConnectionString 'Data Source=:memory:'
            $csvTarget = New-AhtolaSqliteConnection -ConnectionString 'Data Source=:memory:'
            try {
                $null = Invoke-AhtolaSqliteQuery -SqliteConnection $jsonTarget -CommandText 'CREATE TABLE items(id INTEGER PRIMARY KEY, name TEXT, payload BLOB);' -As NonQuery
                $null = Invoke-AhtolaSqliteQuery -SqliteConnection $csvTarget -CommandText 'CREATE TABLE items(id INTEGER PRIMARY KEY, name TEXT, payload BLOB);' -As NonQuery

                Import-AhtolaSqliteTable -Connection $jsonTarget -Table items -Path $script:JsonPath | Should-Be 2
                Import-AhtolaSqliteTable -Connection $csvTarget -Table items -Path $script:CsvPath | Should-Be 2
                [int](Invoke-AhtolaSqliteQuery -SqliteConnection $jsonTarget -CommandText 'SELECT COUNT(*) FROM items;' -As Scalar) | Should-Be 2
                [int](Invoke-AhtolaSqliteQuery -SqliteConnection $csvTarget -CommandText 'SELECT COUNT(*) FROM items;' -As Scalar) | Should-Be 2
            }
            finally {
                $jsonTarget | Close-AhtolaSqliteConnection -Confirm:$false
                $csvTarget | Close-AhtolaSqliteConnection -Confirm:$false
            }
        }
        finally {
            $source | Close-AhtolaSqliteConnection -Confirm:$false
            $destination | Close-AhtolaSqliteConnection -Confirm:$false
        }
    }

    It 'rejects an in-place backup and does not write exports under WhatIf' {
        $connection = New-AhtolaSqliteConnection -ConnectionString 'Data Source=:memory:'
        $whatIfPath = Join-Path $script:TempRoot 'what-if.json'
        try {
            $null = Invoke-AhtolaSqliteQuery -Connection $connection `
                -CommandText 'CREATE TABLE items(id INTEGER PRIMARY KEY); INSERT INTO items VALUES (1);' `
                -As NonQuery

            {
                Backup-AhtolaSqliteDatabase -SourceConnection $connection -DestinationConnection $connection -Confirm:$false
            } | Should-Throw

            Export-AhtolaSqliteTable -Connection $connection -Table items -Path $whatIfPath -WhatIf
            Test-Path -LiteralPath $whatIfPath | Should-BeFalse
        }
        finally {
            $connection | Close-AhtolaSqliteConnection -Confirm:$false
        }
    }

    It 'exports a parameterized query using the inferred format' {
        $connection = New-AhtolaSqliteConnection -ConnectionString 'Data Source=:memory:'
        try {
            $null = Invoke-AhtolaSqliteQuery -Connection $connection `
                -CommandText 'CREATE TABLE items(id INTEGER PRIMARY KEY, name TEXT); INSERT INTO items VALUES (1, ''first''), (2, ''second'');' `
                -As NonQuery

            Export-AhtolaSqliteTable -Connection $connection `
                -Query 'SELECT name FROM items WHERE id = $id;' `
                -Parameters @{ '$id' = 2 } `
                -Path $script:QueryPath `
                -Confirm:$false

            $rows = @(Get-Content -LiteralPath $script:QueryPath -Raw | ConvertFrom-Json)
            $rows.Count | Should-Be 1
            $rows[0].name | Should-BeString 'second'
        }
        finally {
            $connection | Close-AhtolaSqliteConnection -Confirm:$false
        }
    }

    It 'rejects missing imports and leaves tables unchanged under WhatIf' {
        $connection = New-AhtolaSqliteConnection -ConnectionString 'Data Source=:memory:'
        $whatIfPath = Join-Path $script:TempRoot 'what-if-import.json'
        try {
            $null = Invoke-AhtolaSqliteQuery -Connection $connection `
                -CommandText 'CREATE TABLE items(id INTEGER PRIMARY KEY, name TEXT);' `
                -As NonQuery
            '[{"id":1,"name":"what-if"}]' | Set-Content -LiteralPath $whatIfPath -Encoding utf8

            {
                Import-AhtolaSqliteTable -Connection $connection -Table items -Path (Join-Path $script:TempRoot 'missing.json')
            } | Should-Throw

            Import-AhtolaSqliteTable -Connection $connection -Table items -Path $whatIfPath -WhatIf
            [int](Invoke-AhtolaSqliteQuery -Connection $connection -CommandText 'SELECT COUNT(*) FROM items;' -As Scalar) | Should-Be 0
        }
        finally {
            $connection | Close-AhtolaSqliteConnection -Confirm:$false
        }
    }

    It 'encrypts, rekeys, and clears a managed file-backed database password' {
        $encryptedPath = Join-Path $script:TempRoot 'encrypted.sqlite'
        $password = ConvertTo-SecureString 'first-secret' -AsPlainText -Force
        $replacementPassword = ConvertTo-SecureString 'second-secret' -AsPlainText -Force
        $connection = New-AhtolaSqliteConnection -ConnectionString "Data Source=$encryptedPath;Pooling=False"
        try {
            $null = Invoke-AhtolaSqliteQuery `
                -SqliteConnection $connection `
                -CommandText 'CREATE TABLE secrets(id INTEGER PRIMARY KEY); INSERT INTO secrets VALUES (1);' `
                -As NonQuery
            Set-AhtolaSqlitePassword -SqliteConnection $connection -Password $password -Confirm:$false
        }
        finally {
            $connection | Close-AhtolaSqliteConnection -Confirm:$false
        }

        $connection = New-AhtolaSqliteConnection -ConnectionString "Data Source=$encryptedPath;Password=first-secret;Pooling=False"
        try {
            [int](Invoke-AhtolaSqliteQuery -SqliteConnection $connection -CommandText 'SELECT COUNT(*) FROM secrets;' -As Scalar) | Should-Be 1
            Set-AhtolaSqlitePassword -SqliteConnection $connection -Password $replacementPassword -Confirm:$false
        }
        finally {
            $connection | Close-AhtolaSqliteConnection -Confirm:$false
        }

        $connection = New-AhtolaSqliteConnection -ConnectionString "Data Source=$encryptedPath;Password=second-secret;Pooling=False"
        try {
            Clear-AhtolaSqlitePassword -SqliteConnection $connection -Confirm:$false
        }
        finally {
            $connection | Close-AhtolaSqliteConnection -Confirm:$false
        }

        $connection = New-AhtolaSqliteConnection -ConnectionString "Data Source=$encryptedPath;Pooling=False"
        try {
            [int](Invoke-AhtolaSqliteQuery -SqliteConnection $connection -CommandText 'SELECT COUNT(*) FROM secrets;' -As Scalar) | Should-Be 1
        }
        finally {
            $connection | Close-AhtolaSqliteConnection -Confirm:$false
        }
    }
}

Describe 'Devolutions.Ahtola.Sqlite programmatic configuration and CRUD' {
    BeforeAll {
        $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("ahtola-sqlite-pester-" + [guid]::NewGuid().ToString('N'))
        $script:DbPath = Join-Path $script:TempRoot 'db'
        New-Item -ItemType Directory -Path $script:DbPath -Force | Out-Null
        $script:Config = [Devolutions.Ahtola.Sqlite.SqliteDBConfig]::new([ordered]@{
                DatabasePath = $script:DbPath
                DatabaseFile = 'pester.sqlite'
                Version = '1.0.0'
                Schema = [ordered]@{
                    Tables = [ordered]@{
                        Items = [ordered]@{
                            Columns = [ordered]@{
                                Id = [ordered]@{
                                    Type = 'INTEGER'
                                    PrimaryKey = $true
                                    AllowNull = $false
                                }
                                Name = [ordered]@{ Type = 'TEXT' }
                                Qty = [ordered]@{ Type = 'INTEGER' }
                            }
                        }
                    }
                }
            })
        $script:Config.UpdateDatabaseSchema()
    }

    AfterAll {
        try {
            [Ahtola.Data.Sqlite.SqliteConnection]::ClearAllPools()
        }
        catch {
            # Best effort when type already unloaded.
        }

        if (Test-Path -LiteralPath $script:TempRoot) {
            Remove-Item -LiteralPath $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'creates a database from programmatic configuration' {
        $dbFile = Join-Path $script:DbPath 'pester.sqlite'
        Test-Path -LiteralPath $dbFile | Should-BeTrue

        $compare = Compare-AhtolaSqliteDatabaseVersion -Configuration $script:Config -ExpectedVersion '1.0.0'
        $compare.IsDeployed | Should-BeTrue
        $compare.CurrentVersion | Should-BeString '1.0.0'
    }

    It 'round-trips insert, select, update, and delete' {
        $config = $script:Config
        $connection = New-AhtolaSqliteConnection -DatabasePath $config.DatabasePath -DatabaseFile $config.DatabaseFile

        try {
            $inserted = @(New-AhtolaSqliteRow `
                    -SqliteDBConfig $config `
                    -TableName Items `
                    -RowData @{ Id = 1; Name = 'widget'; Qty = 3 } `
                    -SqliteConnection $connection)
            $inserted.Count | Should-Be 1

            $rows = @(Get-AhtolaSqliteRow `
                    -SqliteDBConfig $config `
                    -TableName Items `
                    -ClauseData @{ Id = 1 } `
                    -SqliteConnection $connection `
                    -As OrderedDictionary)

            $rows.Count | Should-Be 1
            [string]$rows[0]['Name'] | Should-BeString 'widget'
            [int]$rows[0]['Qty'] | Should-Be 3

            Set-AhtolaSqliteRow `
                -Configuration $config `
                -Table Items `
                -Values @{ Qty = 9 } `
                -Where @{ Id = 1 } `
                -Connection $connection | Should-Be 1

            Set-AhtolaSqliteRow `
                -Configuration $config `
                -Table Items `
                -Values @{ Id = 1; Qty = 10 } `
                -Where @{ Id = 1 } `
                -OnConflict UPSERT `
                -Connection $connection | Should-Be 1

            $updated = @($connection | Get-AhtolaSqliteRow `
                    -Configuration $config `
                    -Table Items `
                    -Where @{ Id = 1 } `
                    -As OrderedDictionary)

            [int]$updated[0]['Qty'] | Should-Be 10

            Set-AhtolaSqliteRow `
                -Configuration $config `
                -Table Items `
                -Values @{ Name = $null } `
                -Where @{ Id = 1 } `
                -Connection $connection | Should-Be 1

            $nullRoundTrip = @(Get-AhtolaSqliteRow `
                    -Configuration $config `
                    -Table Items `
                    -Where @{ Id = 1 } `
                    -Connection $connection `
                    -As OrderedDictionary)
            $nullRoundTrip[0]['Name'] | Should-BeNull

            Remove-AhtolaSqliteRow `
                -Configuration $config `
                -Table Items `
                -Where @{ Id = 1 } `
                -Connection $connection | Should-Be 1

            $afterDelete = @(Get-AhtolaSqliteRow `
                    -Configuration $config `
                    -Table Items `
                    -Connection $connection `
                    -As OrderedDictionary)

            $afterDelete.Count | Should-Be 0
        }
        finally {
            if ($null -ne $connection) {
                if ($connection.State -eq [System.Data.ConnectionState]::Open) {
                    $connection.Close()
                }

                $connection.Dispose()
            }

            Close-AhtolaSqliteConnection
        }
    }

    It 'honors WhatIf for row mutations' {
        $config = $script:Config
        $connection = New-AhtolaSqliteConnection -DatabasePath $config.DatabasePath -DatabaseFile $config.DatabaseFile
        try {
            New-AhtolaSqliteRow `
                -Configuration $config `
                -Table Items `
                -Values @{ Id = 99; Name = 'not-inserted'; Qty = 1 } `
                -Connection $connection `
                -WhatIf
            [int](Invoke-AhtolaSqliteQuery -Connection $connection -CommandText 'SELECT COUNT(*) FROM Items WHERE Id = 99;' -As Scalar) | Should-Be 0

            New-AhtolaSqliteRow `
                -Configuration $config `
                -Table Items `
                -Values @{ Id = 99; Name = 'present'; Qty = 1 } `
                -Connection $connection | Should-NotBeNull

            Set-AhtolaSqliteRow `
                -Configuration $config `
                -Table Items `
                -Values @{ Qty = 2 } `
                -Where @{ Id = 99 } `
                -Connection $connection `
                -WhatIf
            [int](Invoke-AhtolaSqliteQuery -Connection $connection -CommandText 'SELECT Qty FROM Items WHERE Id = 99;' -As Scalar) | Should-Be 1

            Remove-AhtolaSqliteRow `
                -Configuration $config `
                -Table Items `
                -Where @{ Id = 99 } `
                -Connection $connection `
                -WhatIf
            [int](Invoke-AhtolaSqliteQuery -Connection $connection -CommandText 'SELECT COUNT(*) FROM Items WHERE Id = 99;' -As Scalar) | Should-Be 1

            Remove-AhtolaSqliteRow `
                -Configuration $config `
                -Table Items `
                -Where @{ Id = 99 } `
                -Connection $connection | Should-Be 1
        }
        finally {
            $connection | Close-AhtolaSqliteConnection -Confirm:$false
        }
    }

    It 'reads version metadata' {
        $config = $script:Config
        $connection = New-AhtolaSqliteConnection -DatabasePath $config.DatabasePath -DatabaseFile $config.DatabaseFile
        try {
            $metadata = Get-AhtolaSqliteDatabaseMetadata -Connection $connection -MetadataKey version
            $metadata | Should-NotBeNull
            [string]$metadata['version'] | Should-BeString '1.0.0'
        }
        finally {
            if ($null -ne $connection) {
                if ($connection.State -eq [System.Data.ConnectionState]::Open) {
                    $connection.Close()
                }

                $connection.Dispose()
            }

            Close-AhtolaSqliteConnection
        }
    }
}
