using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

public sealed class WithoutRowidIndexTablePersistenceSliceTests
{
    private const int LargeRowCount = 512;

    [Test]
    public void BinaryPrimaryKeyPersistsAcrossMultipleIndexLeavesAndRealSqliteReadsIt()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE entry(note TEXT, code TEXT PRIMARY KEY, amount INTEGER) WITHOUT ROWID;");
                Execute(connection, BuildLargeInsert(1, LargeRowCount));
                Execute(connection, "UPDATE entry SET note = 'updated' WHERE code = 'key-00100';");
                Execute(connection, "DELETE FROM entry WHERE code = 'key-00300';");
                Execute(connection, "INSERT INTO entry VALUES ('replacement', 'key-00513', 513);");
            }

            using (var pager = SqlitePager.Open(
                       PhysicalFileSystem.Instance,
                       path,
                       path + "-wal",
                       readOnly: true))
            {
                var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
                var rootPage = FindRootPage(pager, header, "entry");
                SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(rootPage)).PageType
                    .Should()
                    .Be(SqliteBtreePageType.IndexInterior);
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                Scalar(connection, "SELECT COUNT(*) FROM entry;").AsInteger().Should().Be(LargeRowCount);
                Scalar(connection, "SELECT note FROM entry WHERE code = 'key-00100';").AsText().Should().Be("updated");
                Scalar(connection, "SELECT COUNT(*) FROM entry WHERE code = 'key-00300';").AsInteger().Should().Be(0);
                Scalar(connection, "SELECT note FROM entry WHERE code = 'key-00513';").AsText().Should().Be("replacement");
            }

            var verificationPath = path + ".verify.db";
            File.Copy(path, verificationPath, overwrite: true);
            try
            {
                using var sqlite = new MsData.SqliteConnection($"Data Source={verificationPath}");
                sqlite.Open();

                using var integrity = sqlite.CreateCommand();
                integrity.CommandText = "PRAGMA integrity_check;";
                integrity.ExecuteScalar().Should().Be("ok");

                using var query = sqlite.CreateCommand();
                query.CommandText = "SELECT COUNT(*) FROM entry;";
                Convert.ToInt64(query.ExecuteScalar()).Should().Be(LargeRowCount);
            }
            finally
            {
                MsData.SqliteConnection.ClearAllPools();
                DeleteDatabase(verificationPath);
            }
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void DuplicateWithoutRowidMutationRejectsBeforeWriting()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        using var database = EmbeddedDatabase.OpenFile("without-rowid-bounded.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE entry(code TEXT PRIMARY KEY, value TEXT) WITHOUT ROWID;");
        Execute(connection, "INSERT INTO entry VALUES ('saved', 'first');");

        var writesBeforeDuplicate = faults.GetOperationCount(FileSystemOperation.Write);
        faults.FailNext(FileSystemOperation.Write);
        var duplicate = () => Execute(connection, "INSERT INTO entry VALUES ('saved', 'duplicate');");
        duplicate.Should().Throw<EmbeddedSqlException>().WithMessage("*UNIQUE constraint failed*");
        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBeforeDuplicate);
        faults.ClearScheduled();

        Scalar(connection, "SELECT value FROM entry WHERE code = 'saved';").AsText().Should().Be("first");
    }

    [TestCase(
        "CREATE TABLE rejected(k TEXT COLLATE custom_collation PRIMARY KEY, value TEXT) WITHOUT ROWID;",
        "application-defined collation CUSTOM_COLLATION")]
    public void UnsupportedWithoutRowidKeyShapesRejectBeforeWriting(string sql, string message)
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        using var database = EmbeddedDatabase.OpenFile("without-rowid-key-reject.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE retained(id INTEGER PRIMARY KEY, value TEXT);");
        Execute(connection, "INSERT INTO retained VALUES (1, 'durable');");

        var writesBeforeReject = faults.GetOperationCount(FileSystemOperation.Write);
        faults.FailNext(FileSystemOperation.Write);
        var rejected = () => Execute(connection, sql);
        rejected.Should().Throw<EmbeddedSqlException>().WithMessage($"*{message}*");
        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBeforeReject);
        faults.ClearScheduled();

        Scalar(connection, "SELECT value FROM retained WHERE id = 1;").AsText().Should().Be("durable");
    }

    [Test]
    public void ApplicationDefinedSecondaryIndexCollationRejectsBeforeWriting()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        using var database = EmbeddedDatabase.OpenFile("without-rowid-index-collation-reject.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE entry(code TEXT PRIMARY KEY, value TEXT) WITHOUT ROWID;");
        Execute(connection, "INSERT INTO entry VALUES ('saved', 'durable');");

        var writesBeforeReject = faults.GetOperationCount(FileSystemOperation.Write);
        faults.FailNext(FileSystemOperation.Write);
        var rejected = () => Execute(
            connection,
            "CREATE INDEX entry_value ON entry(value COLLATE custom_collation);");

        // "custom_collation" is a valid application-defined collation name, but nothing has
        // registered a callback for it on this connection, so CREATE INDEX must fail closed with
        // the SQLite-style "no such collation sequence" message instead of the old unconditional
        // "application-defined collations are rejected" wording (see CustomCollationIndexTests).
        rejected.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*no such collation sequence*custom_collation*");
        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBeforeReject);
        faults.ClearScheduled();
        Scalar(connection, "SELECT value FROM entry WHERE code = 'saved';").AsText().Should().Be("durable");
        Scalar(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'entry_value';")
            .AsInteger().Should().Be(0);
    }

    [Test]
    public void WithoutRowidSecondaryIndexPersistsAndReopens()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("without-rowid-secondary-index.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE entry(value TEXT, code TEXT PRIMARY KEY) WITHOUT ROWID;");
        Execute(connection, "INSERT INTO entry VALUES ('saved', 'key');");
        Execute(connection, "CREATE INDEX entry_value ON entry(value);");
        Scalar(connection, "SELECT value FROM entry WHERE code = 'key';").AsText().Should().Be("saved");

        connection.Dispose();
        database.Dispose();
        using var reopened = EmbeddedDatabase.OpenFile("without-rowid-secondary-index.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        Scalar(reopenedConnection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'entry_value';")
            .AsInteger().Should().Be(1);
        Scalar(reopenedConnection, "SELECT value FROM entry WHERE code = 'key';").AsText().Should().Be("saved");
    }

    [Test]
    public void CompositeCollatedIndexesAndGeneratedColumnsRoundTripThroughSqlite()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, """
                    CREATE TABLE entry(
                        tenant TEXT,
                        sequence INTEGER,
                        tag TEXT COLLATE RTRIM,
                        payload TEXT,
                        doubled INTEGER GENERATED ALWAYS AS (sequence * 2) VIRTUAL,
                        shifted INTEGER GENERATED ALWAYS AS (doubled + 1) VIRTUAL,
                        PRIMARY KEY(tenant COLLATE NOCASE, sequence DESC),
                        UNIQUE(tag)
                    ) WITHOUT ROWID;
                    """);
                Execute(connection, "CREATE INDEX entry_shifted ON entry(shifted DESC);");
                Execute(connection, "CREATE INDEX entry_payload_tenant ON entry(payload, tenant COLLATE BINARY);");
                Execute(connection, """
                    INSERT INTO entry(tenant, sequence, tag, payload) VALUES
                        ('beta', 1, NULL, 'b1'),
                        ('Alpha', 2, 'tag ', 'a2'),
                        ('alpha', 1, 'other', 'a1'),
                        ('charlie', 3, NULL, 'c3');
                    """);
                var duplicate = () => Execute(
                    connection,
                    "INSERT INTO entry(tenant, sequence, tag, payload) VALUES ('delta', 1, 'tag', 'duplicate');");
                duplicate.Should().Throw<EmbeddedSqlException>().WithMessage("*UNIQUE constraint failed*");
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                ReadRows(connection, "SELECT tenant, sequence, doubled, shifted FROM entry;")
                    .Should()
                    .Equal(
                        "Alpha|2|4|5",
                        "alpha|1|2|3",
                        "beta|1|2|3",
                        "charlie|3|6|7");
                Scalar(connection, """
                    SELECT COUNT(*) FROM sqlite_schema
                    WHERE type = 'index'
                      AND name IN ('sqlite_autoindex_entry_2', 'entry_shifted', 'entry_payload_tenant');
                    """).AsInteger().Should().Be(3);
            }

            var verificationPath = path + ".verify.db";
            File.Copy(path, verificationPath, overwrite: true);
            try
            {
                using var sqlite = new MsData.SqliteConnection($"Data Source={verificationPath}");
                sqlite.Open();
                using (var integrity = sqlite.CreateCommand())
                {
                    integrity.CommandText = "PRAGMA integrity_check;";
                    integrity.ExecuteScalar().Should().Be("ok");
                }

                using (var query = sqlite.CreateCommand())
                {
                    query.CommandText = """
                        SELECT group_concat(tenant || sequence, ',')
                        FROM (
                            SELECT tenant, sequence
                            FROM entry
                            ORDER BY tenant COLLATE NOCASE, sequence DESC
                        );
                        """;
                    query.ExecuteScalar().Should().Be("Alpha2,alpha1,beta1,charlie3");
                }

                using var xinfo = sqlite.CreateCommand();
                xinfo.CommandText = """
                    SELECT group_concat(name || ':' || coll || ':' || key, ',')
                    FROM pragma_index_xinfo('entry_payload_tenant');
                    """;
                xinfo.ExecuteScalar().Should().Be(
                    "payload:BINARY:1,tenant:BINARY:1,tenant:NOCASE:0,sequence:BINARY:0");
            }
            finally
            {
                MsData.SqliteConnection.ClearAllPools();
                DeleteDatabase(verificationPath);
            }
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void ManagedEngineOpensMutatesAndReturnsOrdinarySqliteWithoutRowidFile()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var sqlite = new MsData.SqliteConnection($"Data Source={path}"))
            {
                sqlite.Open();
                using var command = sqlite.CreateCommand();
                command.CommandText = """
                    PRAGMA journal_mode=DELETE;
                    CREATE TABLE entry(
                        tenant TEXT,
                        sequence INTEGER,
                        value TEXT,
                        computed INTEGER GENERATED ALWAYS AS (sequence + 1) VIRTUAL,
                        PRIMARY KEY(tenant COLLATE NOCASE, sequence DESC),
                        UNIQUE(value)
                    ) WITHOUT ROWID;
                    CREATE INDEX entry_value_tenant ON entry(value DESC, tenant COLLATE BINARY);
                    CREATE INDEX entry_computed ON entry(computed DESC);
                    INSERT INTO entry(tenant, sequence, value) VALUES
                        ('alpha', 1, 'one'),
                        ('Alpha', 2, 'two'),
                        ('beta', 3, NULL),
                        ('charlie', 4, NULL);
                    """;
                command.ExecuteNonQuery();
            }
            MsData.SqliteConnection.ClearAllPools();

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                ReadRows(connection, "SELECT tenant, sequence, value, computed FROM entry;")
                    .Should()
                    .Equal(
                        "Alpha|2|two|3",
                        "alpha|1|one|2",
                        "beta|3|NULL|4",
                        "charlie|4|NULL|5");
                Execute(connection, "UPDATE entry SET sequence = 9 WHERE value = 'one';");
                Execute(connection, "INSERT INTO entry(tenant, sequence, value) VALUES ('delta', 5, 'five');");
                Execute(connection, "DELETE FROM entry WHERE value = 'two';");
            }

            using (var sqlite = new MsData.SqliteConnection($"Data Source={path}"))
            {
                sqlite.Open();
                using (var integrity = sqlite.CreateCommand())
                {
                    integrity.CommandText = "PRAGMA integrity_check;";
                    integrity.ExecuteScalar().Should().Be("ok");
                }

                using var query = sqlite.CreateCommand();
                query.CommandText = """
                    SELECT group_concat(tenant || ':' || sequence || ':' || computed, ',')
                    FROM (
                        SELECT tenant, sequence, computed
                        FROM entry
                        ORDER BY tenant COLLATE NOCASE, sequence DESC
                    );
                    """;
                query.ExecuteScalar().Should().Be("alpha:9:10,beta:3:4,charlie:4:5,delta:5:6");
            }
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void TableConstraintOrderPreservesSqliteAutoindexOrdinalsInBothDirections()
    {
        var managedPath = CreateDatabasePath();
        var sqlitePath = CreateDatabasePath();
        const string schema = """
            CREATE TABLE entry(
                id TEXT,
                first_unique TEXT,
                second_unique TEXT,
                final_unique TEXT,
                UNIQUE(first_unique),
                UNIQUE(second_unique),
                PRIMARY KEY(id),
                UNIQUE(final_unique)
            ) WITHOUT ROWID;
            """;
        const string ordinarySchema = """
            CREATE TABLE ordinary(
                id TEXT,
                unique_before TEXT,
                UNIQUE(unique_before),
                PRIMARY KEY(id)
            );
            """;
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(managedPath))
            using (var connection = database.Connect())
            {
                Execute(connection, schema);
                Execute(connection, ordinarySchema);
                Execute(connection, "INSERT INTO entry VALUES ('id', 'first', 'second', 'final');");
                Execute(connection, "INSERT INTO ordinary VALUES ('id', 'unique');");
            }

            AssertSqliteAutoindexesAndIntegrity(managedPath);

            using (var sqlite = new MsData.SqliteConnection($"Data Source={sqlitePath}"))
            {
                sqlite.Open();
                using var command = sqlite.CreateCommand();
                command.CommandText = schema
                    + ordinarySchema
                    + " INSERT INTO entry VALUES ('id', 'first', 'second', 'final');"
                    + " INSERT INTO ordinary VALUES ('id', 'unique');";
                command.ExecuteNonQuery();
            }
            MsData.SqliteConnection.ClearAllPools();

            using (var database = EmbeddedDatabase.OpenFile(sqlitePath))
            using (var connection = database.Connect())
            {
                Scalar(connection, """
                    SELECT group_concat(name, ',') FROM (
                        SELECT name FROM sqlite_schema
                        WHERE type = 'index' AND tbl_name = 'entry'
                        ORDER BY name
                    );
                    """).AsText().Should().Be(
                        "sqlite_autoindex_entry_1,sqlite_autoindex_entry_2,sqlite_autoindex_entry_4");
                Execute(connection, "INSERT INTO entry VALUES ('id-2', 'first-2', 'second-2', 'final-2');");
                Execute(connection, "INSERT INTO ordinary VALUES ('id-2', 'unique-2');");
            }

            AssertSqliteAutoindexesAndIntegrity(sqlitePath);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(managedPath);
            DeleteDatabase(sqlitePath);
        }
    }

    [Test]
    public void RenamePreservesPrimaryKeyReservedAutoindexOrdinalAcrossReopen()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, """
                    CREATE TABLE entry(
                        id TEXT PRIMARY KEY,
                        value TEXT UNIQUE
                    ) WITHOUT ROWID;
                    """);
                Execute(connection, "INSERT INTO entry VALUES ('id', 'value');");
                Execute(connection, "ALTER TABLE entry RENAME TO renamed;");
                Scalar(connection, """
                    SELECT COUNT(*) FROM sqlite_schema
                    WHERE type = 'index'
                      AND name = 'sqlite_autoindex_renamed_2'
                      AND tbl_name = 'renamed';
                    """).AsInteger().Should().Be(1);
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                Scalar(connection, "SELECT value FROM renamed WHERE id = 'id';")
                    .AsText().Should().Be("value");
                Scalar(connection, """
                    SELECT COUNT(*) FROM sqlite_schema
                    WHERE type = 'index' AND name = 'sqlite_autoindex_renamed_2';
                    """).AsInteger().Should().Be(1);
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path}");
            sqlite.Open();
            using var integrity = sqlite.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check;";
            integrity.ExecuteScalar().Should().Be("ok");
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void WithoutRowidRootLeafRoundTripsOverflowRecords()
    {
        var fileSystem = new InMemoryFileSystem();
        var payload = new string('x', 10_000);
        using (var database = EmbeddedDatabase.OpenFile("without-rowid-overflow.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE entry(value TEXT, code TEXT PRIMARY KEY) WITHOUT ROWID;");
            Execute(connection, $"INSERT INTO entry VALUES ('{payload}', 'key');");
        }

        using (var pager = SqlitePager.Open(
                   fileSystem,
                   "without-rowid-overflow.db",
                   "without-rowid-overflow.db-wal",
                   readOnly: true))
        {
            var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            var root = SqliteIndexLeafPageView.Parse(
                pager.ReadCommittedPage(FindRootPage(pager, header, "entry")),
                header.UsableSpace,
                header.TextEncoding,
                overflowReader: new SqliteOverflowChainReader(pager, header));
            root.Cells.Should().ContainSingle();
            root.Cells[0].Cell.FirstOverflowPage.Should().NotBeNull();
            root.GetRecord(0).Should().Equal(SqliteRecordCodec.Encode([SqlValue.Text("key"), SqlValue.Text(payload)]));
        }

        using var reopened = EmbeddedDatabase.OpenFile("without-rowid-overflow.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        Scalar(reopenedConnection, "SELECT value FROM entry WHERE code = 'key';").AsText().Should().Be(payload);
    }

    [Test]
    public void InterruptedWithoutRowidWalMutationRecoversOnlyThePriorCommit()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        using (var database = EmbeddedDatabase.OpenFile("without-rowid-wal-recovery.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, """
                CREATE TABLE entry(
                    value TEXT,
                    tenant TEXT,
                    sequence INTEGER,
                    PRIMARY KEY(tenant COLLATE NOCASE, sequence DESC),
                    UNIQUE(value)
                ) WITHOUT ROWID;
                """);
            Execute(connection, "CREATE INDEX entry_value_tenant ON entry(value DESC, tenant COLLATE BINARY);");
            Execute(connection, "INSERT INTO entry VALUES ('committed', 'one', 1);");

            faults.FailOnOccurrence(
                FileSystemOperation.Write,
                faults.GetOperationCount(FileSystemOperation.Write) + 2);
            Assert.Throws<IOException>(() => Execute(
                connection,
                "INSERT INTO entry VALUES ('uncommitted', 'two', 2);"));
        }

        faults.ClearScheduled();
        using var recovered = EmbeddedDatabase.OpenFile("without-rowid-wal-recovery.db", fileSystem);
        using var recoveredConnection = recovered.Connect();
        Scalar(recoveredConnection, "SELECT value FROM entry WHERE tenant = 'one' AND sequence = 1;")
            .AsText().Should().Be("committed");
        Scalar(recoveredConnection, "SELECT COUNT(*) FROM entry WHERE tenant = 'two';").AsInteger().Should().Be(0);
        Scalar(recoveredConnection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'entry_value_tenant';")
            .AsInteger().Should().Be(1);
    }

    [Test]
    public void EncryptedAndReadOnlyReopenRetainTheWithoutRowidIndexTable()
    {
        var fileSystem = new InMemoryFileSystem();
        using var encryption = AhtolaEncryptionOptions.FromHex(
            AhtolaEncryptionCipher.Aes256Gcm,
            "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
        var encryptedFileSystem = new AhtolaEncryptionFileSystem(fileSystem, encryption);

        using (var database = EmbeddedDatabase.OpenFile("without-rowid-encrypted.db", encryptedFileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, """
                CREATE TABLE entry(
                    value TEXT,
                    tenant TEXT,
                    sequence INTEGER,
                    computed INTEGER GENERATED ALWAYS AS (sequence + 1) VIRTUAL,
                    PRIMARY KEY(tenant COLLATE NOCASE, sequence DESC),
                    UNIQUE(value)
                ) WITHOUT ROWID;
                """);
            Execute(connection, "CREATE INDEX entry_computed ON entry(computed DESC);");
            Execute(connection, """
                INSERT INTO entry(value, tenant, sequence) VALUES
                    ('persisted', 'key', 1),
                    ('second', 'KEY', 2);
                """);
        }

        using (var readOnly = EmbeddedDatabase.OpenFile(
                   "without-rowid-encrypted.db",
                   encryptedFileSystem,
                   readOnly: true))
        using (var connection = readOnly.Connect())
        {
            Scalar(connection, "SELECT value FROM entry WHERE tenant = 'key' AND sequence = 1;")
                .AsText().Should().Be("persisted");
            Scalar(connection, "SELECT computed FROM entry WHERE sequence = 2;")
                .AsInteger().Should().Be(3);
            var write = () => Execute(
                connection,
                "INSERT INTO entry(value, tenant, sequence) VALUES ('blocked', 'other', 3);");
            write.Should().Throw<EmbeddedSqlException>()
                .WithMessage("attempt to write a readonly database");
        }

        using var reopened = EmbeddedDatabase.OpenFile("without-rowid-encrypted.db", encryptedFileSystem);
        using var reopenedConnection = reopened.Connect();
        Scalar(reopenedConnection, "SELECT value FROM entry WHERE tenant = 'key' AND sequence = 1;")
            .AsText().Should().Be("persisted");
        Scalar(reopenedConnection, "SELECT COUNT(*) FROM entry WHERE tenant = 'other';").AsInteger().Should().Be(0);
    }

    [Test]
    public void CorruptWithoutRowidRootIsRejectedOnReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        SqliteDatabaseHeader header;
        uint rootPage;
        using (var database = EmbeddedDatabase.OpenFile("without-rowid-corrupt.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE entry(value TEXT, code TEXT PRIMARY KEY) WITHOUT ROWID;");
            Execute(connection, "INSERT INTO entry VALUES ('saved', 'key');");
        }

        using (var store = SqlitePageStore.Open(fileSystem, "without-rowid-corrupt.db"))
        {
            header = store.Header;
            using var pager = SqlitePager.Open(
                fileSystem,
                "without-rowid-corrupt.db",
                "without-rowid-corrupt.db-wal",
                readOnly: true);
            rootPage = FindRootPage(pager, header, "entry");
            var page = store.ReadPage(rootPage);
            page[0] = (byte)SqliteBtreePageType.TableLeaf;
            store.WritePage(rootPage, page);
            store.Flush();
        }

        var reopen = () => EmbeddedDatabase.OpenFile("without-rowid-corrupt.db", fileSystem);
        reopen.Should().Throw<EmbeddedSqlException>().WithMessage("*WITHOUT ROWID table*");
    }

    private static uint FindRootPage(SqlitePager pager, SqliteDatabaseHeader header, string name)
    {
        var schema = SqliteTableLeafPageView.Parse(
            pager.ReadCommittedPage(1),
            header.UsableSpace,
            isFirstPage: true);
        return checked((uint)schema.Cells
            .Select(cell => SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding))
            .Single(values => values[0].AsText() == "table" && values[1].AsText() == name)[3]
            .AsInteger());
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private static SqlValue Scalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0);
    }

    private static IReadOnlyList<string> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<string>();
        while (statement.Step() == StatementStepResult.Row)
        {
            rows.Add(string.Join(
                "|",
                Enumerable.Range(0, statement.GetColumnCount())
                    .Select(column => FormatValue(statement.GetValue(column)))));
        }

        return rows;
    }

    private static void AssertSqliteAutoindexesAndIntegrity(string path)
    {
        using var sqlite = new MsData.SqliteConnection($"Data Source={path}");
        sqlite.Open();
        using (var indexes = sqlite.CreateCommand())
        {
            indexes.CommandText = """
                SELECT group_concat(name, ',') FROM (
                    SELECT name FROM sqlite_schema
                    WHERE type = 'index' AND tbl_name = 'entry'
                    ORDER BY name
                );
                """;
            indexes.ExecuteScalar().Should().Be(
                "sqlite_autoindex_entry_1,sqlite_autoindex_entry_2,sqlite_autoindex_entry_4");
        }
        using (var indexes = sqlite.CreateCommand())
        {
            indexes.CommandText = """
                SELECT group_concat(name, ',') FROM (
                    SELECT name FROM sqlite_schema
                    WHERE type = 'index' AND tbl_name = 'ordinary'
                    ORDER BY name
                );
                """;
            indexes.ExecuteScalar().Should().Be(
                "sqlite_autoindex_ordinary_1,sqlite_autoindex_ordinary_2");
        }

        using var integrity = sqlite.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check;";
        integrity.ExecuteScalar().Should().Be("ok");
    }

    private static string FormatValue(SqlValue value)
        => value.Kind switch
        {
            SqlValueKind.Null => "NULL",
            SqlValueKind.Integer => value.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture),
            SqlValueKind.Real => value.AsReal().ToString(System.Globalization.CultureInfo.InvariantCulture),
            SqlValueKind.Text => value.AsText(),
            SqlValueKind.Blob => Convert.ToHexString(value.AsBlob().Span),
            _ => throw new InvalidOperationException($"Unknown SQL value kind {value.Kind}."),
        };

    private static string BuildLargeInsert(int firstIndex, int count)
        => $"INSERT INTO entry VALUES {string.Join(", ", Enumerable.Range(firstIndex, count)
            .Select(index => $"('note-{index:D5}-{new string('x', 128)}', 'key-{index:D5}', {index})"))};";

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "without-rowid-index-table-persistence-slice-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():N}.db");
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }
}
