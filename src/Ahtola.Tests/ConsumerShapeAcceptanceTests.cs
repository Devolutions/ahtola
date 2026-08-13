using System.Data;
using System.Data.Common;
using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

// Acceptance coverage mirroring the exact SQL and ADO.NET shapes used by the two
// managed-engine consumers:
//   - pinget (Devolutions.Pinget.Core): file-backed pin store with a composite TEXT
//     PRIMARY KEY, INSERT OR REPLACE upserts, PRAGMA table_info probing, read-only
//     opens, and winget-index style JOIN/LIKE/LIMIT read queries with nested readers.
//   - synedgy.pssqlite: default connection string 'Data Source=:memory:;Cache=Shared',
//     a '_metadata (key TEXT PRIMARY KEY, value TEXT)' table, sqlite_schema lookups
//     with COLLATE NOCASE, and config-driven DDL with named PRIMARY KEY constraints.
// Scenarios run against both the managed engine and a real Microsoft.Data.Sqlite
// oracle, which must agree on every observable result.
[NonParallelizable]
public sealed class ConsumerShapeAcceptanceTests
{
    [Test]
    public void PingetPinStoreLifecyclePersistsAndReadsBack()
    {
        var managedPath = CreateDatabasePath("pinget-pin-managed");
        var sqlitePath = CreateDatabasePath("pinget-pin-sqlite");
        try
        {
            using var managed = OpenManaged(managedPath);
            using var sqlite = OpenSqlite(sqlitePath);

            const string createTable =
                """
                CREATE TABLE IF NOT EXISTS pin (
                    package_id TEXT NOT NULL,
                    version TEXT NOT NULL DEFAULT '*',
                    source_id TEXT NOT NULL DEFAULT '',
                    type INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (package_id, source_id)
                );
                """;
            ExecuteNonQuery(managed, createTable);
            ExecuteNonQuery(sqlite, createTable);

            // CREATE TABLE IF NOT EXISTS must be idempotent, as PinStore.Add relies on.
            ExecuteNonQuery(managed, createTable);
            ExecuteNonQuery(sqlite, createTable);

            InsertPin(managed, "Git.Git", "2.47.0", "winget", 2);
            InsertPin(sqlite, "Git.Git", "2.47.0", "winget", 2);
            InsertPin(managed, "Git.Git", "*", "msstore", 4);
            InsertPin(sqlite, "Git.Git", "*", "msstore", 4);

            // INSERT OR REPLACE on the composite TEXT key updates in place.
            InsertPin(managed, "Git.Git", "2.47.1", "winget", 2);
            InsertPin(sqlite, "Git.Git", "2.47.1", "winget", 2);

            ReadPins(managed, null).Should().Equal(ReadPins(sqlite, null));
            ReadPins(managed, "winget").Should().Equal(ReadPins(sqlite, "winget"));

            // PRAGMA table_info column probe, as ResolvePinTypeColumn does.
            ResolvePinTypeColumn(managed).Should().Be("type");
            ResolvePinTypeColumn(sqlite).Should().Be("type");

            DeletePin(managed, "Git.Git", "msstore").Should().Be(DeletePin(sqlite, "Git.Git", "msstore"));
            ReadPins(managed, null).Should().Equal(ReadPins(sqlite, null));
        }
        finally
        {
            DeleteDatabase(managedPath);
            DeleteDatabase(sqlitePath);
        }
    }

    [Test]
    public void PingetPinStoreSurvivesReopenAndServesReadOnlyQueries()
    {
        var path = CreateDatabasePath("pinget-pin-reopen");
        try
        {
            using (var connection = OpenManaged(path))
            {
                ExecuteNonQuery(
                    connection,
                    """
                    CREATE TABLE IF NOT EXISTS pin (
                        package_id TEXT NOT NULL,
                        version TEXT NOT NULL DEFAULT '*',
                        source_id TEXT NOT NULL DEFAULT '',
                        type INTEGER NOT NULL DEFAULT 0,
                        PRIMARY KEY (package_id, source_id)
                    );
                    """);
                InsertPin(connection, "Microsoft.VisualStudioCode", "1.99.0", "winget", 3);
                InsertPin(connection, "Git.Git", "*", "msstore", 4);
            }

            // PinStore.List opens with Mode=ReadOnly and must see committed rows.
            // Pooling=False keeps the physical pager out of the managed pool so the
            // cross-engine handoff below is not blocked by retained lock ownership.
            using (var readOnly = new SqliteConnection(
                       $"Data Source={path};Mode=ReadOnly;Pooling=False;Local Provider=Managed"))
            {
                readOnly.Open();
                ResolvePinTypeColumn(readOnly).Should().Be("type");
                ReadPins(readOnly, null).Should().Equal(
                    ("Git.Git", "*", "msstore", 4L),
                    ("Microsoft.VisualStudioCode", "1.99.0", "winget", 3L));
            }

            // The same file must round-trip through real SQLite, because pinget mixes
            // engines across upgrades.
            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            sqlite.Open();
            using (var command = sqlite.CreateCommand())
            {
                command.CommandText = "PRAGMA integrity_check;";
                command.ExecuteScalar().Should().Be("ok");
            }
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void PingetWingetIndexJoinLikeAndNestedReaderShapesMatchSqlite()
    {
        var managedPath = CreateDatabasePath("winget-index-managed");
        var sqlitePath = CreateDatabasePath("winget-index-sqlite");
        const string fixture =
            """
            CREATE TABLE ids (rowid INTEGER PRIMARY KEY, id TEXT NOT NULL);
            CREATE TABLE names (rowid INTEGER PRIMARY KEY, name TEXT NOT NULL);
            CREATE TABLE monikers (rowid INTEGER PRIMARY KEY, moniker TEXT NOT NULL);
            CREATE TABLE versions (rowid INTEGER PRIMARY KEY, version TEXT NOT NULL);
            CREATE TABLE channels (rowid INTEGER PRIMARY KEY, channel TEXT NOT NULL);
            CREATE TABLE manifest (rowid INTEGER PRIMARY KEY, id INT64 NOT NULL, name INT64 NOT NULL,
                moniker INT64, version INT64 NOT NULL, channel INT64 NOT NULL);
            INSERT INTO ids VALUES (1, 'Git.Git'), (2, 'Microsoft.VisualStudioCode');
            INSERT INTO names VALUES (1, 'Git'), (2, 'Visual Studio Code');
            INSERT INTO monikers VALUES (1, 'git'), (2, 'vscode');
            INSERT INTO versions VALUES (1, '2.47.0'), (2, '1.99.0');
            INSERT INTO channels VALUES (1, ''), (2, 'stable');
            INSERT INTO manifest VALUES (10, 1, 1, 1, 1, 1), (11, 2, 2, 2, 2, 2);
            CREATE TABLE pathparts (rowid INTEGER PRIMARY KEY, parent INT64, pathpart TEXT NOT NULL);
            INSERT INTO pathparts VALUES (100, NULL, 'manifests'), (101, 100, 'g'), (102, 101, 'Git.Git');
            """;
        const string search =
            """
            SELECT manifest.rowid, manifest.id, versions.version, channels.channel,
                   names.name, monikers.moniker
            FROM manifest
            JOIN ids ON manifest.id = ids.rowid
            JOIN names ON manifest.name = names.rowid
            LEFT JOIN monikers ON manifest.moniker = monikers.rowid
            JOIN versions ON manifest.version = versions.rowid
            JOIN channels ON manifest.channel = channels.rowid
            WHERE names.name LIKE @p0
            LIMIT 25;
            """;
        try
        {
            using var managed = OpenManaged(managedPath);
            using var sqlite = OpenSqlite(sqlitePath);
            ExecuteNonQuery(managed, fixture);
            ExecuteNonQuery(sqlite, fixture);

            RunWingetSearch(managed, search).Should().Equal(RunWingetSearch(sqlite, search));
            ResolvePathParts(managed).Should().Equal(ResolvePathParts(sqlite));
        }
        finally
        {
            DeleteDatabase(managedPath);
            DeleteDatabase(sqlitePath);
        }
    }

    [Test]
    public void PingetWingetIndexForeignReadOnlyMatchesSqliteAndTracksLiveOwner()
    {
        // pinget opens winget-owned databases read-only while stock SQLite may still own
        // them (Repository.cs / PinStore.cs use Mode=ReadOnly;Pooling=False). The foreign
        // reader must serve the winget search SQL, adopt the owner's commits on the next
        // autocommit statement, and pin its snapshot during an explicit read transaction
        // — matching a real Microsoft.Data.Sqlite read-only oracle on the same file.
        var path = CreateDatabasePath("winget-foreign");
        const string fixture =
            """
            PRAGMA journal_mode=WAL;
            CREATE TABLE ids (rowid INTEGER PRIMARY KEY, id TEXT NOT NULL);
            CREATE TABLE names (rowid INTEGER PRIMARY KEY, name TEXT NOT NULL);
            CREATE TABLE monikers (rowid INTEGER PRIMARY KEY, moniker TEXT NOT NULL);
            CREATE TABLE versions (rowid INTEGER PRIMARY KEY, version TEXT NOT NULL);
            CREATE TABLE channels (rowid INTEGER PRIMARY KEY, channel TEXT NOT NULL);
            CREATE TABLE manifest (rowid INTEGER PRIMARY KEY, id INT64 NOT NULL, name INT64 NOT NULL,
                moniker INT64, version INT64 NOT NULL, channel INT64 NOT NULL);
            INSERT INTO ids VALUES (1, 'Git.Git'), (2, 'Microsoft.VisualStudioCode');
            INSERT INTO names VALUES (1, 'Git'), (2, 'Visual Studio Code');
            INSERT INTO monikers VALUES (1, 'git'), (2, 'vscode');
            INSERT INTO versions VALUES (1, '2.47.0'), (2, '1.99.0');
            INSERT INTO channels VALUES (1, ''), (2, 'stable');
            INSERT INTO manifest VALUES (10, 1, 1, 1, 1, 1), (11, 2, 2, 2, 2, 2);
            """;
        const string search =
            """
            SELECT manifest.rowid, manifest.id, versions.version, channels.channel,
                   names.name, monikers.moniker
            FROM manifest
            JOIN ids ON manifest.id = ids.rowid
            JOIN names ON manifest.name = names.rowid
            LEFT JOIN monikers ON manifest.moniker = monikers.rowid
            JOIN versions ON manifest.version = versions.rowid
            JOIN channels ON manifest.channel = channels.rowid
            WHERE names.name LIKE @p0
            LIMIT 25;
            """;
        try
        {
            // Create + close so the main file carries a valid, checkpointed schema header;
            // the foreign reader bootstraps from the main file and then scans the WAL for
            // the live owner's subsequent commits (mirrors a winget-written index.db that
            // has been released to disk before pinget opens it read-only).
            using (var setup = OpenSqlite(path))
                ExecuteNonQuery(setup, fixture);

            using var owner = OpenSqlite(path);
            using var foreign = OpenForeignReadOnly(path);
            using var oracle = OpenSqliteReadOnly(path);
            RunWingetSearch(foreign, search).Should().Equal(RunWingetSearch(oracle, search));

            // The live owner commits a new Git.Git manifest row on a fresh version. A
            // committed owner write is adopted on the foreign reader's next autocommit
            // statement, matching the read-only oracle.
            ExecuteNonQuery(owner, "INSERT INTO versions VALUES (3, '2.47.1');");
            ExecuteNonQuery(owner, "INSERT INTO manifest VALUES (12, 1, 1, 1, 3, 1);");
            RunWingetSearch(foreign, search).Should().Equal(RunWingetSearch(oracle, search));
            RunWingetSearch(foreign, search).Should().HaveCount(2);

            // An explicit read transaction pins the snapshot: the owner's next commit is
            // not visible until the transaction commits, matching the oracle.
            using (var foreignTransaction = foreign.BeginTransaction())
            using (var oracleTransaction = oracle.BeginTransaction())
            {
                WingetSearchCount(foreign, foreignTransaction, search).Should().Be(2);
                WingetSearchCount(oracle, oracleTransaction, search).Should().Be(2);
                ExecuteNonQuery(owner, "INSERT INTO versions VALUES (4, '2.47.2');");
                ExecuteNonQuery(owner, "INSERT INTO manifest VALUES (13, 1, 1, 1, 4, 1);");
                WingetSearchCount(foreign, foreignTransaction, search).Should().Be(2);
                WingetSearchCount(oracle, oracleTransaction, search).Should().Be(2);
                foreignTransaction.Commit();
                oracleTransaction.Commit();
            }

            RunWingetSearch(foreign, search).Should().Equal(RunWingetSearch(oracle, search));
            RunWingetSearch(foreign, search).Should().HaveCount(3);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void PSSqliteDefaultConnectionStringSupportsMetadataLifecycle()
    {
        // The exact default connection string New-AhtolaSqliteConnection produces.
        const string connectionString = "Data Source=:memory:;Cache=Shared;";
        using var managed = new SqliteConnection(connectionString + "Local Provider=Managed");
        using var sqlite = new MsData.SqliteConnection(connectionString);
        managed.Open();
        sqlite.Open();

        const string createTable =
            "CREATE TABLE IF NOT EXISTS _metadata (key TEXT PRIMARY KEY, value TEXT);";
        ExecuteNonQuery(managed, createTable);
        ExecuteNonQuery(sqlite, createTable);
        ExecuteNonQuery(managed, "INSERT OR REPLACE INTO _metadata (key, value) VALUES ('version', '1.2.3');");
        ExecuteNonQuery(sqlite, "INSERT OR REPLACE INTO _metadata (key, value) VALUES ('version', '1.2.3');");
        ExecuteNonQuery(managed, "INSERT OR REPLACE INTO _metadata (key, value) VALUES ('version', '1.2.4');");
        ExecuteNonQuery(sqlite, "INSERT OR REPLACE INTO _metadata (key, value) VALUES ('version', '1.2.4');");

        SelectMetadata(managed).Should().Equal(SelectMetadata(sqlite));

        // Get-AhtolaSqliteDBMetadata probes existence through sqlite_schema with NOCASE.
        SchemaHasTable(managed, "_METADATA").Should().BeTrue();
        SchemaHasTable(sqlite, "_METADATA").Should().BeTrue();
        SchemaHasTable(managed, "missing").Should().BeFalse();

        // IN-parameter expansion, as Get-AhtolaSqliteDBMetadata builds it.
        SelectMetadataKeys(managed, ["version", "other"]).Should()
            .Equal(SelectMetadataKeys(sqlite, ["version", "other"]));
    }

    [Test]
    public void PSSqliteConfigDrivenDdlAndCrudShapesMatchSqlite()
    {
        const string connectionString = "Data Source=:memory:;Cache=Shared;";
        using var managed = new SqliteConnection(connectionString + "Local Provider=Managed");
        using var sqlite = new MsData.SqliteConnection(connectionString);
        managed.Open();
        sqlite.Open();

        // Shapes produced by SqliteTable/SqlitePrimaryKeyTableConstraint/SqliteColumn.
        const string createTable =
            """
            CREATE TABLE IF NOT EXISTS servers (
                name TEXT NOT NULL,
                os TEXT NOT NULL DEFAULT 'linux',
                CONSTRAINT servers_pk PRIMARY KEY (name, os)
            );
            CREATE TABLE IF NOT EXISTS audit_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                message TEXT NOT NULL
            );
            """;
        ExecuteNonQuery(managed, createTable);
        ExecuteNonQuery(sqlite, createTable);

        const string insertLog = "INSERT INTO audit_log (message) VALUES (@message);";
        InsertLog(managed, insertLog, "created");
        InsertLog(sqlite, insertLog, "created");
        SelectLogIds(managed).Should().Equal(SelectLogIds(sqlite));

        const string upsert =
            """
            INSERT INTO servers (name, os) VALUES (@name, @os)
            ON CONFLICT (name, os) DO UPDATE SET os = excluded.os;
            """;
        // Repeating the same key exercises DO UPDATE; a distinct key inserts alongside.
        UpsertServer(managed, upsert, "web01", "windows");
        UpsertServer(sqlite, upsert, "web01", "windows");
        UpsertServer(managed, upsert, "web01", "windows");
        UpsertServer(sqlite, upsert, "web01", "windows");
        UpsertServer(managed, upsert, "web01", "linux");
        UpsertServer(sqlite, upsert, "web01", "linux");

        SelectServers(managed).Should().Equal(SelectServers(sqlite));

        // Set-AhtolaSqliteRow UPDATE shape with parameterized WHERE clause.
        const string update = "UPDATE servers SET os = @newOs WHERE name = @name AND os = @oldOs;";
        UpdateServer(managed, update, "web01", "linux", "macos");
        UpdateServer(sqlite, update, "web01", "linux", "macos");
        SelectServers(managed).Should().Equal(SelectServers(sqlite));

        // Remove-AhtolaSqliteRow DELETE shape.
        const string delete = "DELETE FROM servers WHERE name = @name;";
        DeleteServer(managed, delete, "web01").Should().Be(DeleteServer(sqlite, delete, "web01"));
        SelectServers(managed).Should().Equal(SelectServers(sqlite));
    }

    [Test]
    public void PSSqliteInsertReturningStarMatchesSqlite()
    {
        // New-AhtolaSqliteRow appends 'RETURNING *;' to inserts.
        // The managed engine must return the same columns — names, values, and order —
        // as Microsoft.Data.Sqlite for both an AUTOINCREMENT primary key and a
        // composite PRIMARY KEY table.
        const string connectionString = "Data Source=:memory:;Cache=Shared;";
        using var managed = new SqliteConnection(connectionString + "Local Provider=Managed");
        using var sqlite = new MsData.SqliteConnection(connectionString);
        managed.Open();
        sqlite.Open();

        const string createTables =
            """
            CREATE TABLE IF NOT EXISTS servers (
                name TEXT NOT NULL,
                os TEXT NOT NULL DEFAULT 'linux',
                CONSTRAINT servers_pk PRIMARY KEY (name, os)
            );
            CREATE TABLE IF NOT EXISTS audit_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                message TEXT NOT NULL
            );
            """;
        ExecuteNonQuery(managed, createTables);
        ExecuteNonQuery(sqlite, createTables);

        const string insertLog = "INSERT INTO audit_log (message) VALUES (@m) RETURNING *;";
        InsertReturning(managed, insertLog, ("@m", "created"))
            .Should().Equal(InsertReturning(sqlite, insertLog, ("@m", "created")));
        InsertReturning(managed, insertLog, ("@m", "updated"))
            .Should().Equal(InsertReturning(sqlite, insertLog, ("@m", "updated")));

        const string insertServer = "INSERT INTO servers (name, os) VALUES (@name, @os) RETURNING *;";
        InsertReturning(managed, insertServer, ("@name", "web01"), ("@os", "windows"))
            .Should().Equal(InsertReturning(sqlite, insertServer, ("@name", "web01"), ("@os", "windows")));

        SelectLogIds(managed).Should().Equal(SelectLogIds(sqlite));
        SelectServers(managed).Should().Equal(SelectServers(sqlite));
    }

    [Test]
    public void PSSqliteDataTableLoadMatchesSqliteWithNullableJoinColumn()
    {
        // Invoke-AhtolaSqliteQuery defaults to a DataTable result.
        // The managed reader must load a multi-column result set — including a nullable
        // LEFT JOIN column — into a DataTable with the same columns, rows, and cell values
        // (incl. DBNull) as Microsoft.Data.Sqlite.
        const string connectionString = "Data Source=:memory:;Cache=Shared;";
        using var managed = new SqliteConnection(connectionString + "Local Provider=Managed");
        using var sqlite = new MsData.SqliteConnection(connectionString);
        managed.Open();
        sqlite.Open();

        const string schema =
            """
            CREATE TABLE servers (
                name TEXT NOT NULL,
                os TEXT NOT NULL DEFAULT 'linux',
                CONSTRAINT servers_pk PRIMARY KEY (name, os)
            );
            CREATE TABLE os_info (
                os TEXT NOT NULL,
                vendor TEXT,
                PRIMARY KEY (os)
            );
            INSERT INTO servers (name, os) VALUES ('web01', 'windows'), ('web02', 'linux'), ('web03', 'macos');
            INSERT INTO os_info (os, vendor) VALUES ('linux', 'Debian'), ('windows', 'Microsoft');
            """;
        ExecuteNonQuery(managed, schema);
        ExecuteNonQuery(sqlite, schema);

        const string select =
            """
            SELECT s.name AS server_name, s.os, oi.vendor AS vendor
            FROM servers s
            LEFT JOIN os_info oi ON oi.os = s.os
            ORDER BY s.name;
            """;

        LoadDataTable(managed, select).Should().Equal(LoadDataTable(sqlite, select));
    }

    [Test]
    public void PSSqliteOverwriteRecreatesAfterClearingPoolsAndDeletingSidecars()
    {
        // Initialize-AhtolaSqliteDatabase OVERWRITE: Close-AhtolaSqliteConnection ->
        // ClearAllPools() -> delete .db (+ -wal/-shm/-journal) -> recreate. The managed
        // engine must reopen a fresh file-backed database at the same path with no stale
        // ownership or orphan lock errors after the previous connection released its
        // locks.
        var path = CreateDatabasePath("pssqlite-overwrite");
        try
        {
            using (var connection = OpenManaged(path))
            {
                ExecuteNonQuery(connection, "PRAGMA journal_mode=WAL;");
                ExecuteNonQuery(connection, "CREATE TABLE _metadata (key TEXT PRIMARY KEY, value TEXT);");
                ExecuteNonQuery(connection, "INSERT INTO _metadata (key, value) VALUES ('version', '1.2.3');");
            }

            File.Exists(path).Should().BeTrue();
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
            {
                var candidate = path + suffix;
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }

            File.Exists(path).Should().BeFalse("OVERWRITE deletes the database and its sidecars");

            using (var recreated = OpenManaged(path))
            {
                ExecuteNonQuery(recreated, "CREATE TABLE _metadata (key TEXT PRIMARY KEY, value TEXT);");
                SelectMetadata(recreated).Should().BeEmpty("the recreated database is empty");
                ExecuteNonQuery(recreated, "INSERT INTO _metadata (key, value) VALUES ('version', '2.0.0');");
                SelectMetadata(recreated).Should().HaveCount(1);
            }

            // The recreated file round-trips through real SQLite, because synedgy mixes
            // engines across upgrades.
            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            sqlite.Open();
            using (var command = sqlite.CreateCommand())
            {
                command.CommandText = "PRAGMA integrity_check;";
                command.ExecuteScalar().Should().Be("ok");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void PSSqliteCreateViewWithColumnListMatchesSqlite()
    {
        // SqliteView.ps1 (09.SqliteView.ps1:181) emits 'CREATE VIEW <name> (<cols>) AS
        // <select>;', a parenthesized column list. The managed engine must parse the
        // column list and expose the renamed columns exactly as Microsoft.Data.Sqlite.
        const string connectionString = "Data Source=:memory:;Cache=Shared;";
        using var managed = new SqliteConnection(connectionString + "Local Provider=Managed");
        using var sqlite = new MsData.SqliteConnection(connectionString);
        managed.Open();
        sqlite.Open();

        const string schema =
            """
            CREATE TABLE servers (
                name TEXT NOT NULL,
                os TEXT NOT NULL DEFAULT 'linux',
                CONSTRAINT servers_pk PRIMARY KEY (name, os)
            );
            INSERT INTO servers (name, os) VALUES ('web01', 'windows'), ('web02', 'linux');
            CREATE VIEW IF NOT EXISTS v_servers (server_name, os_name) AS
                SELECT name, os FROM servers ORDER BY name;
            """;
        ExecuteNonQuery(managed, schema);
        ExecuteNonQuery(sqlite, schema);

        SelectView(managed).Should().Equal(SelectView(sqlite));
        ViewColumnNames(managed).Should().Equal(ViewColumnNames(sqlite));
    }

    private static SqliteConnection OpenManaged(string path)
    {
        // Pooling=False so disposing hands the file back to other engines cleanly; the
        // managed pool otherwise retains the SQLite lock-byte ownership by design.
        var connection = new SqliteConnection($"Data Source={path};Pooling=False;Local Provider=Managed");
        connection.Open();
        return connection;
    }

    private static MsData.SqliteConnection OpenSqlite(string path)
    {
        var connection = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    private static SqliteConnection OpenForeignReadOnly(string path)
    {
        var connection = new SqliteConnection(
            $"Data Source={path};Mode=ReadOnly;Foreign Read Only=True;Pooling=False;Local Provider=Managed");
        connection.Open();
        return connection;
    }

    private static MsData.SqliteConnection OpenSqliteReadOnly(string path)
    {
        var connection = new MsData.SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        return connection;
    }

    private static void InsertPin(DbConnection connection, string id, string version, string source, int type)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT OR REPLACE INTO pin (package_id, version, source_id, type) VALUES (@id, @ver, @src, @type)";
        AddParameter(command, "@id", id);
        AddParameter(command, "@ver", version);
        AddParameter(command, "@src", source);
        AddParameter(command, "@type", type);
        command.ExecuteNonQuery();
    }

    private static List<(string Id, string Version, string Source, long Type)> ReadPins(
        DbConnection connection,
        string? sourceId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sourceId is null
            ? "SELECT package_id, version, source_id, type FROM pin ORDER BY package_id, source_id"
            : "SELECT package_id, version, source_id, type FROM pin WHERE source_id = @src ORDER BY package_id, source_id";
        if (sourceId is not null)
            AddParameter(command, "@src", sourceId);

        var pins = new List<(string, string, string, long)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            pins.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
        return pins;
    }

    private static int DeletePin(DbConnection connection, string id, string? sourceId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sourceId is null
            ? "DELETE FROM pin WHERE package_id = @id"
            : "DELETE FROM pin WHERE package_id = @id AND source_id = @src";
        AddParameter(command, "@id", id);
        if (sourceId is not null)
            AddParameter(command, "@src", sourceId);
        return command.ExecuteNonQuery();
    }

    private static string? ResolvePinTypeColumn(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(pin)";
        var hasCurrent = false;
        var hasLegacy = false;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            if (string.Equals(name, "type", StringComparison.OrdinalIgnoreCase))
                hasCurrent = true;
            else if (string.Equals(name, "pin_type", StringComparison.OrdinalIgnoreCase))
                hasLegacy = true;
        }

        return hasCurrent ? "type" : hasLegacy ? "pin_type" : null;
    }

    private static List<(long Rowid, long Id, string Version, string Channel, string Name, string Moniker)>
        RunWingetSearch(DbConnection connection, string search)
    {
        using var command = connection.CreateCommand();
        command.CommandText = search;
        AddParameter(command, "@p0", "%it%");
        var rows = new List<(long, long, string, string, string, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? string.Empty : reader.GetString(5)));
        }

        return rows;
    }

    private static long WingetSearchCount(DbConnection connection, DbTransaction? transaction, string search)
    {
        using var command = connection.CreateCommand();
        command.CommandText = search;
        command.Transaction = transaction;
        AddParameter(command, "@p0", "%it%");
        using var reader = command.ExecuteReader();
        var count = 0L;
        while (reader.Read())
            count++;
        return count;
    }

    private static List<string> ResolvePathParts(DbConnection connection)
    {
        // PreIndexedSource walks the pathparts parent chain with a second command while
        // the outer reader stays open.
        var parts = new List<string>();
        using var outer = connection.CreateCommand();
        outer.CommandText = "SELECT rowid, pathpart FROM pathparts WHERE parent IS NULL";
        using var reader = outer.ExecuteReader();
        while (reader.Read())
        {
            var rowid = reader.GetInt64(0);
            parts.Add(reader.GetString(1));
            using var inner = connection.CreateCommand();
            inner.CommandText = "SELECT parent, pathpart FROM pathparts WHERE rowid = @id";
            AddParameter(inner, "@id", rowid + 1);
            using var innerReader = inner.ExecuteReader();
            while (innerReader.Read())
                parts.Add(innerReader.GetString(1));
        }

        return parts;
    }

    private static List<(string Key, string Value)> SelectMetadata(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value from _metadata;";
        var rows = new List<(string, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    private static List<(string Key, string Value)> SelectMetadataKeys(
        DbConnection connection,
        string[] keys)
    {
        using var command = connection.CreateCommand();
        var placeholders = new string[keys.Length];
        for (var index = 0; index < keys.Length; index++)
        {
            placeholders[index] = $"@k{index}";
            AddParameter(command, placeholders[index], keys[index]);
        }

        command.CommandText = $"SELECT key, value from _metadata WHERE key IN ({string.Join(", ", placeholders)});";
        var rows = new List<(string, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    private static bool SchemaHasTable(DbConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name from sqlite_schema WHERE name = @name COLLATE NOCASE";
        AddParameter(command, "@name", name);
        using var reader = command.ExecuteReader();
        return reader.Read();
    }

    private static void UpsertServer(DbConnection connection, string sql, string name, string os)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@name", name);
        AddParameter(command, "@os", os);
        command.ExecuteNonQuery();
    }

    private static void UpdateServer(
        DbConnection connection,
        string sql,
        string name,
        string oldOs,
        string newOs)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@name", name);
        AddParameter(command, "@oldOs", oldOs);
        AddParameter(command, "@newOs", newOs);
        command.ExecuteNonQuery();
    }

    private static int DeleteServer(DbConnection connection, string sql, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@name", name);
        return command.ExecuteNonQuery();
    }

    private static List<(string Name, string Os)> SelectServers(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, os FROM servers ORDER BY name, os;";
        var rows = new List<(string, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    private static void InsertLog(DbConnection connection, string sql, string message)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@message", message);
        command.ExecuteNonQuery();
    }

    private static List<(string Column, object Value)> InsertReturning(
        DbConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            AddParameter(command, name, value);
        using var reader = command.ExecuteReader();
        var columns = new List<(string, object)>();
        if (reader.Read())
        {
            for (var index = 0; index < reader.FieldCount; index++)
                columns.Add((reader.GetName(index), reader.GetValue(index)));
        }

        return columns;
    }

    private static List<long> SelectLogIds(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM audit_log ORDER BY id;";
        var ids = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetInt64(0));
        return ids;
    }

    private static List<string> LoadDataTable(DbConnection connection, string select)
    {
        using var command = connection.CreateCommand();
        command.CommandText = select;
        using var reader = command.ExecuteReader();
        var table = new DataTable();
        table.Load(reader);
        var snapshot = new List<string>
        {
            $"columns={table.Columns.Count};rows={table.Rows.Count}",
        };
        foreach (DataColumn column in table.Columns)
            snapshot.Add($"{column.ColumnName}:{column.DataType.Name}:{column.AllowDBNull}");
        foreach (DataRow row in table.Rows)
        {
            foreach (var item in row.ItemArray)
                snapshot.Add(item is DBNull ? "<NULL>" : (item?.ToString() ?? "<empty>"));
        }

        return snapshot;
    }

    private static void ExecuteNonQuery(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static List<(string Name, string Os)> SelectView(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT server_name, os_name FROM v_servers ORDER BY server_name;";
        var rows = new List<(string Name, string Os)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    private static List<string> ViewColumnNames(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT server_name, os_name FROM v_servers;";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        for (var i = 0; i < reader.FieldCount; i++)
            names.Add(reader.GetName(i));
        return names;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string CreateDatabasePath(string suffix)
    {
        var directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "consumer-shape-acceptance");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{suffix}-{Guid.NewGuid():N}.db");
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }
}
