using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

/// <summary>
/// Regression coverage for the VACUUM INTO destination-build path: the private
/// target is created in DELETE mode and written as one compact image, so these
/// pin the resulting file shape, its durability, and its failure behavior.
/// </summary>
[NonParallelizable]
public sealed class ManagedVacuumCompactImageTests
{
    private const string EncryptionKey =
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    [Test]
    public void VacuumIntoPublishesADeleteModeImageWithNoSidecars()
    {
        var fileSystem = new InMemoryFileSystem();
        const string sourcePath = "compact-source.db";
        const string destinationPath = "compact-output.db";

        using var database = EmbeddedDatabase.OpenFile(sourcePath, fileSystem);
        using var connection = database.Connect();
        CreateFragmentedCatalog(connection);
        ExecuteVacuumInto(connection, destinationPath);

        // Native VACUUM INTO leaves a self-contained rollback-journal database.
        fileSystem.FileExists(destinationPath + "-wal").Should().BeFalse();
        fileSystem.FileExists(destinationPath + "-journal").Should().BeFalse();
        fileSystem.FileExists(destinationPath + "-shm").Should().BeFalse();

        var header = ReadHeader(fileSystem, destinationPath);
        header.ReadVersion.Should().Be(SqliteFileFormatVersion.Legacy);
        header.WriteVersion.Should().Be(SqliteFileFormatVersion.Legacy);
        header.VersionValidFor.Should().Be(header.ChangeCounter);

        var length = ReadFileLength(fileSystem, destinationPath);
        (length % header.PageSize).Should().Be(0);
        header.DatabaseSizeInPages.Should().Be(checked((uint)(length / header.PageSize)));
    }

    [Test]
    public void VacuumIntoCompactsAndPreservesEveryRowAndHeaderField()
    {
        var fileSystem = new InMemoryFileSystem();
        const string sourcePath = "compact-preserve-source.db";
        const string destinationPath = "compact-preserve-output.db";

        using var database = EmbeddedDatabase.OpenFile(sourcePath, fileSystem);
        using var connection = database.Connect();
        CreateFragmentedCatalog(connection);
        Execute(connection, "PRAGMA user_version = 4321;");
        Execute(connection, "PRAGMA application_id = 1234;");

        var sourceHeader = ReadHeader(fileSystem, sourcePath);
        var sourceRows = ReadRowSignature(connection);
        var sourceSchema = ReadSchemaSignature(connection);
        var sourceLength = ReadFileLength(fileSystem, sourcePath);

        ExecuteVacuumInto(connection, destinationPath);

        var destinationHeader = ReadHeader(fileSystem, destinationPath);
        destinationHeader.UserVersion.Should().Be(sourceHeader.UserVersion);
        destinationHeader.ApplicationId.Should().Be(sourceHeader.ApplicationId);
        destinationHeader.TextEncoding.Should().Be(sourceHeader.TextEncoding);
        destinationHeader.PageSize.Should().Be(sourceHeader.PageSize);
        destinationHeader.FreelistPageCount.Should().Be(0);
        destinationHeader.FirstFreelistTrunkPage.Should().Be(0);
        destinationHeader.DatabaseSizeInPages.Should().BeLessThan(sourceHeader.DatabaseSizeInPages);
        ReadFileLength(fileSystem, destinationPath).Should().BeLessThan(sourceLength);

        using var output = EmbeddedDatabase.OpenFile(destinationPath, fileSystem, readOnly: true);
        using var outputConnection = output.Connect();
        ReadRowSignature(outputConnection).Should().Equal(sourceRows);
        ReadSchemaSignature(outputConnection).Should().Equal(sourceSchema);
    }

    [Test]
    public void VacuumIntoOutputPassesNativeIntegrityCheckAndMatchesNativeCompaction()
    {
        var root = CreateWorkDirectory();
        var sourcePath = Path.Combine(root, "native-compare-source.db");
        var managedOutputPath = Path.Combine(root, "native-compare-managed.db");
        var nativeOutputPath = Path.Combine(root, "native-compare-native.db");

        using (var database = EmbeddedDatabase.OpenFile(sourcePath, PhysicalFileSystem.Instance))
        using (var connection = database.Connect())
        {
            CreateFragmentedCatalog(connection);
            ExecuteVacuumInto(connection, managedOutputPath);
        }

        // Native vacuums the identical source image, so a matching page count is
        // evidence of equivalent compaction rather than a coincidence.
        using (var native = OpenSqlite(sourcePath))
        {
            using var command = native.CreateCommand();
            command.CommandText = "VACUUM INTO $target;";
            command.Parameters.AddWithValue("$target", nativeOutputPath);
            command.ExecuteNonQuery();
        }

        using var managedOutput = OpenSqlite(managedOutputPath);
        using var nativeOutput = OpenSqlite(nativeOutputPath);

        Scalar(managedOutput, "PRAGMA integrity_check;").Should().Be("ok");
        Scalar(managedOutput, "PRAGMA page_size;").Should().Be(Scalar(nativeOutput, "PRAGMA page_size;"));
        Scalar(managedOutput, "PRAGMA page_count;").Should().Be(Scalar(nativeOutput, "PRAGMA page_count;"));
        Scalar(managedOutput, "PRAGMA freelist_count;").Should().Be("0");
        Scalar(nativeOutput, "PRAGMA freelist_count;").Should().Be("0");
        Scalar(managedOutput, "PRAGMA user_version;").Should().Be(Scalar(nativeOutput, "PRAGMA user_version;"));
        Scalar(managedOutput, "PRAGMA application_id;").Should().Be(Scalar(nativeOutput, "PRAGMA application_id;"));
        Scalar(managedOutput, RowHashSql).Should().Be(Scalar(nativeOutput, RowHashSql));
        Scalar(managedOutput, RowIdSql).Should().Be(Scalar(nativeOutput, RowIdSql));
        Scalar(managedOutput, SchemaHashSql).Should().Be(Scalar(nativeOutput, SchemaHashSql));
    }

    [Test]
    public void VacuumIntoPreservesEncryptedPageCodecOutput()
    {
        using var encryption = AhtolaEncryptionOptions.FromHex(
            AhtolaEncryptionCipher.Aes256Gcm,
            EncryptionKey);
        var backing = new InMemoryFileSystem();
        var fileSystem = new AhtolaEncryptionFileSystem(backing, encryption);
        const string sourcePath = "encrypted-compact-source.db";
        const string destinationPath = "encrypted-compact-output.db";

        using var database = EmbeddedDatabase.OpenFile(sourcePath, fileSystem);
        using var connection = database.Connect();
        CreateFragmentedCatalog(connection);
        var sourceRows = ReadRowSignature(connection);
        ExecuteVacuumInto(connection, destinationPath);

        // The published image must be ciphertext, and only the keyed file system
        // may read it back: page-number-dependent encoding must survive renumbering.
        var raw = ReadAllBytes(backing, destinationPath);
        raw.AsSpan(0, 16).ToArray().Should().NotEqual("SQLite format 3\0"u8.ToArray());

        using var output = EmbeddedDatabase.OpenFile(destinationPath, fileSystem, readOnly: true);
        using var outputConnection = output.Connect();
        ReadRowSignature(outputConnection).Should().Equal(sourceRows);
    }

    [Test]
    public void InterruptedVacuumImageWriteLeavesNoDestinationOrTemporaryArtifacts()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string sourcePath = "compact-interrupt-source.db";
        const string destinationPath = "compact-interrupt-output.db";

        using var database = EmbeddedDatabase.OpenFile(sourcePath, fileSystem);
        using var connection = database.Connect();
        CreateFragmentedCatalog(connection);
        var sourceBefore = ReadAllBytes(fileSystem, sourcePath);
        var sourceRows = ReadRowSignature(connection);

        // Fail partway through the sequential destination image write.
        faults.FailOnOccurrence(
            FileSystemOperation.Write,
            faults.GetOperationCount(FileSystemOperation.Write) + 6);
        Assert.Throws<IOException>(() => ExecuteVacuumInto(connection, destinationPath));
        faults.ClearScheduled();

        fileSystem.FileExists(destinationPath).Should().BeFalse();
        EnumerateVacuumTemporaries(fileSystem, destinationPath).Should().BeEmpty();
        ReadAllBytes(fileSystem, sourcePath).Should().Equal(sourceBefore);
        ReadRowSignature(connection).Should().Equal(sourceRows);

        // The source is still fully usable and can vacuum successfully afterwards.
        ExecuteVacuumInto(connection, destinationPath);
        using var output = EmbeddedDatabase.OpenFile(destinationPath, fileSystem, readOnly: true);
        using var outputConnection = output.Connect();
        ReadRowSignature(outputConnection).Should().Equal(sourceRows);
    }

    [Test]
    public void InterruptedVacuumPublicationLeavesTheEmptyDestinationAndCleansTemporaries()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string sourcePath = "compact-publish-source.db";
        const string destinationPath = "compact-publish-output.db";

        using var database = EmbeddedDatabase.OpenFile(sourcePath, fileSystem);
        using var connection = database.Connect();
        CreateFragmentedCatalog(connection);
        using (var destination = fileSystem.OpenFile(destinationPath, FileOpenMode.CreateNew))
            destination.FlushToDisk();

        faults.FailNext(FileSystemOperation.AtomicReplace);
        Assert.Throws<IOException>(() => ExecuteVacuumInto(connection, destinationPath));
        faults.ClearScheduled();

        ReadFileLength(fileSystem, destinationPath).Should().Be(0);
        EnumerateVacuumTemporaries(fileSystem, destinationPath).Should().BeEmpty();

        ExecuteVacuumInto(connection, destinationPath);
        ReadFileLength(fileSystem, destinationPath).Should().BeGreaterThan(0);
    }

    [Test]
    public void VacuumIntoRejectsANonEmptyDestinationWithoutBuildingAnImage()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string sourcePath = "compact-exists-source.db";
        const string destinationPath = "compact-exists-output.db";

        using var database = EmbeddedDatabase.OpenFile(sourcePath, fileSystem);
        using var connection = database.Connect();
        CreateFragmentedCatalog(connection);
        ExecuteVacuumInto(connection, destinationPath);

        var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
        Assert.Throws<EmbeddedSqlException>(() => ExecuteVacuumInto(connection, destinationPath))!
            .Message.Should().Be("output file already exists");
        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBefore);
    }

    private const string RowHashSql =
        "SELECT group_concat(id || '|' || bucket || '|' || label || '|' || payload, char(10)) FROM docs ORDER BY id;";

    private const string RowIdSql = "SELECT group_concat(rowid, ',') FROM (SELECT rowid FROM docs ORDER BY rowid);";

    private const string SchemaHashSql =
        "SELECT group_concat(type || '|' || name || '|' || coalesce(sql, ''), char(10)) "
        + "FROM sqlite_schema ORDER BY name;";

    private static void CreateFragmentedCatalog(EmbeddedConnection connection)
    {
        Execute(
            connection,
            """
            CREATE TABLE docs(
                id INTEGER PRIMARY KEY,
                bucket INTEGER NOT NULL,
                label TEXT NOT NULL,
                payload TEXT NOT NULL);
            CREATE INDEX docs_bucket ON docs(bucket, label);
            CREATE INDEX docs_label ON docs(label);
            """);
        for (var batch = 0; batch < 6; batch++)
        {
            var rows = Enumerable
                .Range(batch * 40 + 1, 40)
                .Select(index =>
                    $"({index}, {index % 7}, 'label-{index:D4}', '{new string((char)('a' + (index % 26)), 90)}')");
            Execute(connection, $"INSERT INTO docs(id, bucket, label, payload) VALUES {string.Join(", ", rows)};");
        }

        // Leave free pages and partially filled leaves for VACUUM to reclaim.
        Execute(connection, "DELETE FROM docs WHERE id % 5 IN (1, 3);");
    }

    private static string[] ReadRowSignature(EmbeddedConnection connection)
    {
        using var statement = connection.Prepare(
            "SELECT rowid, id, bucket, label, payload FROM docs ORDER BY id;");
        var rows = new List<string>();
        while (statement.Step() == StatementStepResult.Row)
        {
            rows.Add(string.Join(
                '|',
                statement.GetValue(0).AsInteger(),
                statement.GetValue(1).AsInteger(),
                statement.GetValue(2).AsInteger(),
                statement.GetValue(3).AsText(),
                statement.GetValue(4).AsText()));
        }

        return [.. rows];
    }

    private static string[] ReadSchemaSignature(EmbeddedConnection connection)
    {
        using var statement = connection.Prepare(
            "SELECT type, name, coalesce(sql, '') FROM sqlite_schema ORDER BY name;");
        var entries = new List<string>();
        while (statement.Step() == StatementStepResult.Row)
        {
            entries.Add(string.Join(
                '|',
                statement.GetValue(0).AsText(),
                statement.GetValue(1).AsText(),
                statement.GetValue(2).AsText()));
        }

        return [.. entries];
    }

    private static string[] EnumerateVacuumTemporaries(IFileSystem fileSystem, string destinationPath)
    {
        if (fileSystem is not InMemoryFileSystem inMemory)
            return [];

        return [.. inMemory.EnumerateFilePaths()
            .Where(path => path.StartsWith(destinationPath + ".vacuum-", StringComparison.Ordinal))];
    }

    private static void ExecuteVacuumInto(EmbeddedConnection connection, string destinationPath)
    {
        using var statement = connection.Prepare("VACUUM INTO ?1;");
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

    private static SqliteDatabaseHeader ReadHeader(IFileSystem fileSystem, string path)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
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

    private static string CreateWorkDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "ahtola-vacuum-compact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static MsData.SqliteConnection OpenSqlite(string path)
    {
        var connection = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    private static string Scalar(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
