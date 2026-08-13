using System.Text;
using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedVacuumStorageTests
{
    private const string MainEncryptionKey =
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    private const string AttachedEncryptionKey =
        "202122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F";

    [Test]
    public void VacuumMainReclaimsCompleteCatalogAndPreservesHeaderAndRowids()
    {
        var path = CreateDatabasePath("catalog");
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                CreateRichCatalog(connection);
                DeletePressureRows(connection);
                ReadValue(connection, "PRAGMA journal_mode=DELETE;").Should().Be(SqlValue.Text("delete"));
            }

            using (var sqlite = OpenSqlite(path))
            {
                ExecuteSqlite(sqlite, "PRAGMA user_version=321; PRAGMA application_id=654;");
            }

            var expandedLength = new FileInfo(path).Length;
            string[] schemaBefore;
            long[] rowidsBefore;
            SqliteDatabaseHeader headerBefore;
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                schemaBefore = ReadSchemaSql(connection);
                rowidsBefore = ReadIntegers(connection, "SELECT rowid FROM loose ORDER BY rowid;");
                headerBefore = ReadHeader(path);

                Execute(connection, "PRAGMA foreign_keys=ON;");
                Execute(connection, "VACUUM main;");

                var headerAfter = ReadHeader(path);
                headerAfter.SchemaCookie.Should().Be(unchecked(headerBefore.SchemaCookie + 1));
                headerAfter.DefaultPageCacheSize.Should().Be(headerBefore.DefaultPageCacheSize);
                headerAfter.TextEncoding.Should().Be(headerBefore.TextEncoding);
                headerAfter.UserVersion.Should().Be(321);
                headerAfter.ApplicationId.Should().Be(654);
                headerAfter.FreelistPageCount.Should().Be(0);
                new FileInfo(path).Length.Should().BeLessThan(expandedLength);
                new FileInfo(path).Length.Should()
                    .Be((long)headerAfter.PageSize * headerAfter.DatabaseSizeInPages);

                ReadSchemaSql(connection).Should().Equal(schemaBefore);
                ReadIntegers(connection, "SELECT rowid FROM loose ORDER BY rowid;")
                    .Should().Equal(rowidsBefore);
                ReadValue(connection, "SELECT COUNT(*) FROM active_entries;")
                    .Should().Be(SqlValue.Integer(5));
                ReadValue(connection, "SELECT doubled FROM entries WHERE id=5;")
                    .Should().Be(SqlValue.Integer(10));
                ReadValue(connection, "SELECT value FROM keyed WHERE tenant='tenant-1' AND sequence=5;")
                    .Should().Be(SqlValue.Text("keyed-05"));
                ReadValue(
                        connection,
                        "SELECT id FROM entries "
                        + "ORDER BY category COLLATE NOCASE DESC, payload COLLATE RTRIM ASC LIMIT 1;")
                    .Should().Be(SqlValue.Integer(5));

                Execute(connection, "UPDATE entries SET category='changed' WHERE id=1;");
                ReadValue(connection, "SELECT COUNT(*) FROM audit WHERE entry_id=1 AND category='updated';")
                    .Should().Be(SqlValue.Integer(1));
                Assert.Throws<EmbeddedSqlException>(() =>
                    Execute(
                        connection,
                        "INSERT INTO entries(id, parent_code, category, payload) "
                        + "VALUES (99, 'missing', 'invalid', 'payload');"))!
                    .Message.Should().Contain("FOREIGN KEY constraint failed");
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                ReadValue(connection, "PRAGMA user_version;").Should().Be(SqlValue.Integer(321));
                ReadValue(connection, "PRAGMA application_id;").Should().Be(SqlValue.Integer(654));
                ReadValue(connection, "SELECT COUNT(*) FROM entries;").Should().Be(SqlValue.Integer(5));
                ReadIntegers(connection, "SELECT rowid FROM loose ORDER BY rowid;")
                    .Should().Equal(rowidsBefore);
            }

            VerifyWithSqlite(path, expectedEntryCount: 5);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void VacuumIntoUsesPendingPageSizeAndLeavesWalSourceUnchanged()
    {
        var sourcePath = CreateDatabasePath("into-source");
        var outputPath = CreateDatabasePath("into-output", createDirectoryOnly: true);
        var emptyOutputPath = CreateDatabasePath("into-empty", createDirectoryOnly: true);
        var existingOutputPath = CreateDatabasePath("into-existing", createDirectoryOnly: true);
        try
        {
            using var database = EmbeddedDatabase.OpenFile(sourcePath);
            using var connection = database.Connect();
            CreateRichCatalog(connection);
            DeletePressureRows(connection);
            ReadValue(connection, "PRAGMA journal_mode;").Should().Be(SqlValue.Text("wal"));
            Execute(connection, "PRAGMA page_size=1024;");

            var sourceHeaderBefore = ReadHeader(sourcePath);
            var sourceLengthBefore = new FileInfo(sourcePath).Length;
            var sourceWalLengthBefore = new FileInfo(sourcePath + "-wal").Length;
            ExecuteVacuumInto(connection, "VACUUM main INTO ?1;", outputPath);

            ReadHeader(sourcePath).Should().Be(sourceHeaderBefore);
            new FileInfo(sourcePath).Length.Should().Be(sourceLengthBefore);
            new FileInfo(sourcePath + "-wal").Length.Should().Be(sourceWalLengthBefore);
            ReadValue(connection, "PRAGMA page_size;").Should().Be(SqlValue.Integer(4096));

            var outputHeader = ReadHeader(outputPath);
            outputHeader.PageSize.Should().Be(1024);
            outputHeader.SchemaCookie.Should().Be(unchecked(sourceHeaderBefore.SchemaCookie + 1));
            outputHeader.FreelistPageCount.Should().Be(0);
            outputHeader.WriteVersion.Should().Be(SqliteFileFormatVersion.Legacy);
            outputHeader.ReadVersion.Should().Be(SqliteFileFormatVersion.Legacy);
            new FileInfo(outputPath).Length.Should()
                .Be((long)outputHeader.PageSize * outputHeader.DatabaseSizeInPages);
            new FileInfo(outputPath).Length.Should().BeLessThan(new FileInfo(sourcePath).Length);

            using (var output = EmbeddedDatabase.OpenFile(outputPath, readOnly: true))
            using (var outputConnection = output.Connect())
            {
                ReadValue(outputConnection, "SELECT COUNT(*) FROM entries;")
                    .Should().Be(SqlValue.Integer(5));
                ReadIntegers(outputConnection, "SELECT rowid FROM loose ORDER BY rowid;")
                    .Should().Equal(10_000, 10_001, 10_002, 10_003, 10_004);
            }

            using (File.Create(emptyOutputPath))
            {
            }
            ExecuteVacuumInto(connection, "VACUUM INTO ?1;", emptyOutputPath);
            ReadHeader(emptyOutputPath).FreelistPageCount.Should().Be(0);

            File.WriteAllBytes(existingOutputPath, [0x41]);
            var existingBytes = File.ReadAllBytes(existingOutputPath);
            Assert.Throws<EmbeddedSqlException>(() =>
                ExecuteVacuumInto(connection, "VACUUM INTO ?1;", existingOutputPath))!
                .Message.Should().Be("output file already exists");
            File.ReadAllBytes(existingOutputPath).Should().Equal(existingBytes);

            using var nonText = connection.Prepare("VACUUM INTO ?1;");
            nonText.Bind(1, SqlValue.Integer(42));
            Assert.Throws<EmbeddedSqlException>(() => nonText.Step())!
                .Message.Should().Be("non-text filename");

            VerifyWithSqlite(outputPath, expectedEntryCount: 5);
            VerifyWithSqlite(emptyOutputPath, expectedEntryCount: 5);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(sourcePath);
            DeleteDatabase(outputPath);
            DeleteDatabase(emptyOutputPath);
            DeleteDatabase(existingOutputPath);
        }
    }

    [Test]
    public void VacuumIntoPreservesTheSourcePendingPageSizeForLaterVacuum()
    {
        var sourcePath = CreateDatabasePath("into-pending-source");
        var outputPath = CreateDatabasePath("into-pending-output", createDirectoryOnly: true);
        try
        {
            using var database = EmbeddedDatabase.OpenFile(sourcePath);
            using var connection = database.Connect();
            Execute(connection, "CREATE TABLE entries(value TEXT); INSERT INTO entries VALUES ('source');");
            ReadValue(connection, "PRAGMA journal_mode=delete;").Should().Be(SqlValue.Text("delete"));
            Execute(connection, "PRAGMA page_size=1024;");

            ExecuteVacuumInto(connection, "VACUUM INTO ?1;", outputPath);
            ReadValue(connection, "PRAGMA page_size;").Should().Be(SqlValue.Integer(4096));

            Execute(connection, "VACUUM;");
            ReadValue(connection, "PRAGMA page_size;").Should().Be(SqlValue.Integer(1024));
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(sourcePath);
            DeleteDatabase(outputPath);
        }
    }

    [Test]
    public void VacuumAttachedSchemaAndIntoPreserveAttachmentEncryptionKey()
    {
        var inner = new InMemoryFileSystem();
        using var mainEncryption = AhtolaEncryptionOptions.FromHex(
            AhtolaEncryptionCipher.Aes256Gcm,
            MainEncryptionKey);
        using var mainFileSystem = new AhtolaEncryptionFileSystem(inner, mainEncryption);
        using (var database = EmbeddedDatabase.OpenFile("main.db", mainFileSystem))
        using (var connection = database.Connect())
        {
            Execute(
                connection,
                $"ATTACH 'attached.db' AS aux KEY '{AttachedEncryptionKey}';"
                + "CREATE TABLE aux.data(id INTEGER PRIMARY KEY, value TEXT);"
                + BuildQualifiedInsert("aux.data", 32)
                + "DELETE FROM aux.data WHERE id > 3;");

            Execute(connection, "VACUUM aux;");
            Execute(connection, "VACUUM aux INTO 'attached-copy.db';");
            ReadValue(connection, "SELECT COUNT(*) FROM aux.data;").Should().Be(SqlValue.Integer(3));
            ReadValue(connection, "SELECT COUNT(*) FROM main.sqlite_schema;").Should().Be(SqlValue.Integer(0));
        }

        using var attachedEncryption = AhtolaEncryptionOptions.FromHex(
            AhtolaEncryptionCipher.Aes256Gcm,
            AttachedEncryptionKey);
        using var attachedFileSystem = new AhtolaEncryptionFileSystem(inner, attachedEncryption);
        using (var attached = EmbeddedDatabase.OpenFile("attached.db", attachedFileSystem, readOnly: true))
        using (var connection = attached.Connect())
            ReadValue(connection, "SELECT COUNT(*) FROM data;").Should().Be(SqlValue.Integer(3));
        using (var copy = EmbeddedDatabase.OpenFile("attached-copy.db", attachedFileSystem, readOnly: true))
        using (var connection = copy.Connect())
            ReadValue(connection, "SELECT value FROM data WHERE id=3;")
                .Should().Be(SqlValue.Text("value-03"));

        Assert.Throws<InvalidDataException>(() =>
        {
            using var wrong = EmbeddedDatabase.OpenFile("attached-copy.db", mainFileSystem, readOnly: true);
        });
    }

    [Test]
    public void VacuumRejectsTransactionsReadersBlobsQueryOnlyAndReadOnlyBeforeWriting()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "vacuum-gates.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var primary = database.Connect())
        using (var sibling = database.Connect())
        using (var primaryAdapter = ManagedConnectionAdapter.Wrap(primary))
        {
            Execute(primary, "CREATE TABLE data(id INTEGER PRIMARY KEY, value BLOB);");
            Execute(primary, "INSERT INTO data VALUES (1, X'01020304');");

            Execute(primary, "BEGIN;");
            AssertVacuumRejectsWithoutWrite(
                faults,
                sibling,
                "VACUUM;",
                "cannot VACUUM while a transaction is active");
            Execute(primary, "ROLLBACK;");

            using (var reader = primary.Prepare("SELECT value FROM data;"))
            {
                reader.Step().Should().Be(StatementStepResult.Row);
                AssertVacuumRejectsWithoutWrite(
                    faults,
                    sibling,
                    "VACUUM;",
                    "cannot VACUUM - SQL statements in progress");
            }

            using (primaryAdapter.OpenBlob("main", "data", "value", 1))
            {
                AssertVacuumRejectsWithoutWrite(
                    faults,
                    sibling,
                    "VACUUM;",
                    "cannot VACUUM while a blob handle is active");
            }

            Execute(sibling, "PRAGMA query_only=ON;");
            AssertVacuumRejectsWithoutWrite(
                faults,
                sibling,
                "VACUUM;",
                "attempt to write a readonly database");
            Execute(sibling, "PRAGMA query_only=OFF;");

            using var pager = SqlitePager.Open(fileSystem, path, path + "-wal");
            using (pager.BeginReadTransaction())
            {
                var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
                Assert.Throws<SqlitePagerBusyException>(() => Execute(sibling, "VACUUM;"));
                faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBefore);
            }

            Execute(sibling, "VACUUM;");
        }

        using var readOnly = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var readOnlyConnection = readOnly.Connect();
        AssertVacuumRejectsWithoutWrite(
            faults,
            readOnlyConnection,
            "VACUUM;",
            "attempt to write a readonly database");
    }

    [Test]
    public void VacuumPublishesSiblingPagerGenerationAndRefreshesSiblingAutocommitWrites()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "vacuum-sibling-generation.db";
        using var primaryDatabase = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var primary = primaryDatabase.Connect();
        Execute(primary, "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT);");
        Execute(primary, BuildQualifiedInsert("data", 12));
        Execute(primary, "DELETE FROM data WHERE id > 3;");

        using var siblingDatabase = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var sibling = siblingDatabase.Connect();
        using var siblingPager = SqlitePager.Open(fileSystem, path, path + "-wal");
        var schemaCookieBefore =
            SqliteDatabaseHeader.Parse(siblingPager.ReadCommittedPage(1)).SchemaCookie;

        Execute(primary, "VACUUM;");

        SqliteDatabaseHeader.Parse(siblingPager.ReadCommittedPage(1)).SchemaCookie
            .Should().Be(unchecked(schemaCookieBefore + 1));
        // Native parity: the sibling's fresh autocommit write refreshes the catalog at
        // statement start, so it observes the post-VACUUM committed view and succeeds
        // without a manual reset. (Verified against Microsoft.Data.Sqlite/e_sqlite3.)
        Execute(sibling, "INSERT INTO data VALUES (4, 'stale');");
        ReadValue(sibling, "SELECT value FROM data WHERE id=4;")
            .Should().Be(SqlValue.Text("stale"));

        // An explicit pool reset still leaves the connection fully usable.
        sibling.ResetForPooling();
        ReadValue(sibling, "SELECT value FROM data WHERE id=4;")
            .Should().Be(SqlValue.Text("stale"));
    }

    [Test]
    public async Task OpposingVacuumIntoOperationsRejectExistingOutputsWithoutDeadlock()
    {
        var fileSystem = new InMemoryFileSystem();
        using var leftDatabase = EmbeddedDatabase.OpenFile("vacuum-left.db", fileSystem);
        using var left = leftDatabase.Connect();
        Execute(left, "CREATE TABLE data(value TEXT); INSERT INTO data VALUES ('left');");
        using var rightDatabase = EmbeddedDatabase.OpenFile("vacuum-right.db", fileSystem);
        using var right = rightDatabase.Connect();
        Execute(right, "CREATE TABLE data(value TEXT); INSERT INTO data VALUES ('right');");

        var leftVacuum = Task.Run(() =>
            Assert.Throws<EmbeddedSqlException>(() =>
                ExecuteVacuumInto(left, "VACUUM INTO ?1;", "vacuum-right.db")));
        var rightVacuum = Task.Run(() =>
            Assert.Throws<EmbeddedSqlException>(() =>
                ExecuteVacuumInto(right, "VACUUM INTO ?1;", "vacuum-left.db")));
        var errors = await Task.WhenAll(leftVacuum, rightVacuum)
            .WaitAsync(TimeSpan.FromSeconds(5));

        errors.Should().OnlyContain(error => error!.Message == "output file already exists");
        ReadValue(left, "SELECT value FROM data;").Should().Be(SqlValue.Text("left"));
        ReadValue(right, "SELECT value FROM data;").Should().Be(SqlValue.Text("right"));
    }

    [TestCase(FileSystemOperation.Write)]
    [TestCase(FileSystemOperation.AtomicReplace)]
    public void VacuumIntoFailureLeavesSourceAndEmptyDestinationUnchanged(FileSystemOperation operation)
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string sourcePath = "vacuum-into-failure.db";
        const string destinationPath = "vacuum-into-failure-copy.db";
        using var database = EmbeddedDatabase.OpenFile(sourcePath, fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT);");
        Execute(connection, BuildQualifiedInsert("data", 24));
        Execute(connection, "DELETE FROM data WHERE id > 3;");
        using (var destination = fileSystem.OpenFile(destinationPath, FileOpenMode.CreateNew))
            destination.FlushToDisk();

        var sourceBefore = ReadAllBytes(fileSystem, sourcePath);
        faults.FailNext(operation);
        Assert.Throws<IOException>(() =>
            ExecuteVacuumInto(connection, "VACUUM INTO ?1;", destinationPath));
        faults.ClearScheduled();

        ReadAllBytes(fileSystem, sourcePath).Should().Equal(sourceBefore);
        ReadFileLength(fileSystem, destinationPath).Should().Be(0);
        ReadValue(connection, "SELECT COUNT(*) FROM data;").Should().Be(SqlValue.Integer(3));

        ExecuteVacuumInto(connection, "VACUUM INTO ?1;", destinationPath);
        using var output = EmbeddedDatabase.OpenFile(destinationPath, fileSystem, readOnly: true);
        using var outputConnection = output.Connect();
        ReadValue(outputConnection, "SELECT value FROM data WHERE id=3;")
            .Should().Be(SqlValue.Text("value-03"));
    }

    private static void CreateRichCatalog(EmbeddedConnection connection)
    {
        Execute(
            connection,
            """
            CREATE TABLE parents(
                id INTEGER PRIMARY KEY,
                code TEXT NOT NULL UNIQUE,
                label TEXT NOT NULL
            );
            CREATE TABLE entries(
                id INTEGER PRIMARY KEY,
                parent_code TEXT NOT NULL REFERENCES parents(code) ON UPDATE CASCADE ON DELETE RESTRICT,
                category TEXT COLLATE NOCASE UNIQUE,
                payload TEXT NOT NULL,
                doubled INTEGER GENERATED ALWAYS AS (id * 2) VIRTUAL,
                CONSTRAINT positive_id CHECK (id > 0)
            );
            CREATE INDEX entries_order
                ON entries(category COLLATE NOCASE DESC, payload COLLATE RTRIM ASC);
            CREATE TABLE keyed(
                tenant TEXT,
                sequence INTEGER,
                value TEXT,
                PRIMARY KEY(tenant COLLATE NOCASE, sequence DESC)
            ) WITHOUT ROWID;
            CREATE TABLE loose(value TEXT);
            CREATE TABLE audit(entry_id INTEGER, category TEXT);
            CREATE VIEW active_entries AS
                SELECT id, doubled FROM entries WHERE id <= 5;
            CREATE TRIGGER entries_audit AFTER UPDATE ON entries
            BEGIN
                INSERT INTO audit VALUES (1, 'updated');
            END;
            INSERT INTO parents VALUES
                (1, 'parent-0', 'zero'),
                (2, 'parent-1', 'one'),
                (3, 'parent-2', 'two'),
                (4, 'parent-3', 'three');
            """);

        var payload = new string('p', 1_800);
        var entries = Enumerable.Range(1, 48).Select(index =>
            $"({index}, 'parent-{index % 4}', 'category-{index:D2}', '{payload}-{index:D2}')");
        Execute(
            connection,
            "INSERT INTO entries(id, parent_code, category, payload) VALUES "
            + string.Join(", ", entries)
            + ";");
        var keyed = Enumerable.Range(1, 48).Select(index =>
            $"('tenant-{index % 4}', {index}, 'keyed-{index:D2}')");
        Execute(
            connection,
            "INSERT INTO keyed(tenant, sequence, value) VALUES "
            + string.Join(", ", keyed)
            + ";");
        var loose = Enumerable.Range(0, 48).Select(index =>
            $"({10_000 + index}, 'loose-{index:D2}-{new string('l', 600)}')");
        Execute(
            connection,
            "INSERT INTO loose(rowid, value) VALUES "
            + string.Join(", ", loose)
            + ";");
    }

    private static void DeletePressureRows(EmbeddedConnection connection)
    {
        Execute(
            connection,
            """
            DELETE FROM entries WHERE id > 5;
            DELETE FROM keyed WHERE sequence > 5;
            DELETE FROM loose WHERE rowid > 10004;
            """);
    }

    private static string BuildQualifiedInsert(string tableName, int count)
    {
        var rows = Enumerable.Range(1, count)
            .Select(index => $"({index}, 'value-{index:D2}')");
        return $"INSERT INTO {tableName}(id, value) VALUES {string.Join(", ", rows)};";
    }

    private static void AssertVacuumRejectsWithoutWrite(
        DeterministicFaultInjector faults,
        EmbeddedConnection connection,
        string sql,
        string expectedMessage)
    {
        var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, sql))!
            .Message.Should().Be(expectedMessage);
        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBefore);
    }

    private static void ExecuteVacuumInto(
        EmbeddedConnection connection,
        string sql,
        string destinationPath)
    {
        using var statement = connection.Prepare(sql);
        statement.Bind(1, SqlValue.Text(destinationPath));
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
                statement.Step().Should().Be(StatementStepResult.Done);
        }
    }

    private static SqlValue ReadValue(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        var value = statement.GetValue(0);
        statement.Step().Should().Be(StatementStepResult.Done);
        return value;
    }

    private static long[] ReadIntegers(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var values = new List<long>();
        while (statement.Step() == StatementStepResult.Row)
            values.Add(statement.GetValue(0).AsInteger());
        return values.ToArray();
    }

    private static string[] ReadSchemaSql(EmbeddedConnection connection)
    {
        using var statement = connection.Prepare(
            "SELECT name, sql FROM sqlite_schema WHERE sql IS NOT NULL ORDER BY name;");
        var entries = new List<string>();
        while (statement.Step() == StatementStepResult.Row)
            entries.Add(statement.GetValue(0).AsText() + "\0" + statement.GetValue(1).AsText());
        return entries.ToArray();
    }

    private static SqliteDatabaseHeader ReadHeader(string path)
    {
        using var pager = SqlitePager.Open(
            PhysicalFileSystem.Instance,
            path,
            path + "-wal",
            readOnly: true);
        return SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
    }

    private static byte[] ReadAllBytes(IFileSystem fileSystem, string path)
    {
        using var file = fileSystem.OpenFile(path, FileOpenMode.OpenExisting, readOnly: true);
        var contents = new byte[checked((int)file.Length)];
        file.Read(0, contents).Should().Be(contents.Length);
        return contents;
    }

    private static long ReadFileLength(IFileSystem fileSystem, string path)
    {
        using var file = fileSystem.OpenFile(path, FileOpenMode.OpenExisting, readOnly: true);
        return file.Length;
    }

    private static MsData.SqliteConnection OpenSqlite(string path)
    {
        var connection = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    private static void ExecuteSqlite(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void VerifyWithSqlite(string path, long expectedEntryCount)
    {
        using var sqlite = OpenSqlite(path);
        using var command = sqlite.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        command.ExecuteScalar().Should().Be("ok");
        command.CommandText = "SELECT COUNT(*) FROM entries;";
        command.ExecuteScalar().Should().Be(expectedEntryCount);
        command.CommandText = "SELECT rowid FROM loose ORDER BY rowid;";
        using var reader = command.ExecuteReader();
        var rowids = new List<long>();
        while (reader.Read())
            rowids.Add(reader.GetInt64(0));
        rowids.Should().Equal(10_000, 10_001, 10_002, 10_003, 10_004);
    }

    private static string CreateDatabasePath(string name, bool createDirectoryOnly = false)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "managed-vacuum-storage-tests");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{name}-{Guid.NewGuid():N}.db");
        if (!createDirectoryOnly)
            DeleteDatabase(path);
        return path;
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }

        var directory = Path.GetDirectoryName(path);
        var fileName = Path.GetFileName(path);
        if (directory is not null && Directory.Exists(directory))
        {
            foreach (var temporary in Directory.GetFiles(directory, fileName + ".vacuum-*.tmp*"))
                File.Delete(temporary);
        }
    }
}
