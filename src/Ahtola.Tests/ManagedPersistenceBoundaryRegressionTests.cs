using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedPersistenceBoundaryRegressionTests
{
    private const string Aes256Key = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    private const string WrongAes256Key = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF";

    [Test]
    public void ManagedReadOnlyProvidersRetainTornWalWithoutWriteAccess()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("This regression exercises Windows read-only file permissions.");

        var path = CreateDatabasePath();
        var walPath = path + "-wal";
        try
        {
            using (var writer = new global::Ahtola.AhtolaConnection(
                       $"Data Source={path};Local Provider=Managed"))
            {
                writer.Open();
                writer.ExecuteNonQuery("CREATE TABLE entries(value INTEGER);");
                writer.ExecuteNonQuery("INSERT INTO entries VALUES (7);");
            }

            AppendUncommittedWalFrame(path);
            var expectedWal = File.ReadAllBytes(walPath);
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            File.SetAttributes(walPath, File.GetAttributes(walPath) | FileAttributes.ReadOnly);
            Assert.Throws<UnauthorizedAccessException>(() =>
            {
                using var writable = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            });

            using (var connection = new global::Ahtola.AhtolaConnection(
                       $"Data Source={path};Mode=ReadOnly;Local Provider=Managed"))
            {
                connection.Open();
                ExecuteAhtolaScalar(connection, "SELECT value FROM entries;").Should().Be(7);
            }

            File.ReadAllBytes(walPath).Should().Equal(expectedWal);

            using (var connection = new SqliteConnection(
                       $"Data Source={path};Mode=ReadOnly;Local Provider=Managed"))
            {
                connection.Open();
                connection.ExecuteScalar<long>("SELECT value FROM entries;").Should().Be(7);
            }

            File.ReadAllBytes(walPath).Should().Equal(expectedWal);
        }
        finally
        {
            RestoreWriteAccess(path);
            RestoreWriteAccess(walPath);
            DeleteDatabase(path);
        }
    }

    [Test]
    public void CommittedMutationPublishesCatalogWhenWalResetMaintenanceFails()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "post-commit-maintenance-regression.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE entries(value INTEGER);");

            faults.FailNext(FileSystemOperation.SetLength);
            var exception = Assert.Throws<EmbeddedPostCommitMaintenanceException>(
                () => Execute(connection, "INSERT INTO entries VALUES (7);"));

            exception!.MaintenanceFailure.Should().BeOfType<IOException>();
            exception.Message.Should().Contain("committed successfully");
            exception.Message.Should().Contain("Do not retry");
            Scalar(connection, "SELECT COUNT(*) FROM entries;").Should().Be(1);

            Assert.Throws<InvalidOperationException>(() => Execute(connection, "INSERT INTO entries VALUES (8);"))!
                .Message.Should().Contain("prior managed database mutation committed successfully");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Scalar(reopenedConnection, "SELECT value FROM entries;").Should().Be(7);
    }

    [Test]
    public void ReadOnlyPagerFailsClosedWhenWalCannotYieldCommittedSnapshot()
    {
        var fileSystem = new InMemoryFileSystem();
        const string databasePath = "read-only-torn-wal-regression.db";
        const string walPath = databasePath + "-wal";
        var header = SqliteWalHeader.Create(SqlitePageSize.Default, salt1: 1, salt2: 2);

        using (var pager = SqlitePager.Create(fileSystem, databasePath, walPath, header))
        {
        }

        long originalWalLength;
        using (var wal = SqliteWalFile.Open(fileSystem, walPath))
        {
            wal.AppendFrame(2, new byte[header.PageSize], databaseSizeInPages: 1);
            wal.Flush();
            originalWalLength = wal.Length;
        }

        var exception = Assert.Throws<InvalidDataException>(
            () => SqlitePager.Open(fileSystem, databasePath, walPath, readOnly: true));

        exception!.Message.Should().Contain("non-mutating committed snapshot");
        ReadFileLength(fileSystem, walPath).Should().Be(originalWalLength);
    }

    [Test]
    public void ManagedEncryptionOptionsAreIndependentAcrossSuccessAndFailure()
    {
        var path = CreateDatabasePath();
        var copiedKeyPath = CreateDatabasePath();
        try
        {
            using (var create = new global::Ahtola.AhtolaConnection(ManagedEncryptionConnectionString(path, Aes256Key)))
            {
                create.Open();
                create.ExecuteNonQuery("CREATE TABLE entries(value INTEGER);");
                create.ExecuteNonQuery("INSERT INTO entries VALUES (7);");
            }

            Assert.Throws<InvalidDataException>(() =>
            {
                using var failedOpen = new global::Ahtola.AhtolaConnection(
                    ManagedEncryptionConnectionString(path, WrongAes256Key));
                failedOpen.Open();
            });

            using (var reopen = new global::Ahtola.AhtolaConnection(ManagedEncryptionConnectionString(path, Aes256Key)))
            {
                reopen.Open();
                ExecuteAhtolaScalar(reopen, "SELECT value FROM entries;").Should().Be(7);
            }

            using var options = AhtolaEncryptionOptions.FromHex(AhtolaEncryptionCipher.Aes256Gcm, Aes256Key);
            using var database = EmbeddedDatabase.OpenFile(
                copiedKeyPath,
                new AhtolaEncryptionFileSystem(PhysicalFileSystem.Instance, options));
            options.Dispose();

            using var connection = database.Connect();
            Execute(connection, "CREATE TABLE copied_key(value INTEGER);");
            Execute(connection, "INSERT INTO copied_key VALUES (11);");
            Scalar(connection, "SELECT value FROM copied_key;").Should().Be(11);
        }
        finally
        {
            DeleteDatabase(path);
            DeleteDatabase(copiedKeyPath);
        }
    }

    private static void AppendUncommittedWalFrame(string path)
    {
        var page = File.ReadAllBytes(path);
        var header = SqliteDatabaseHeader.Parse(page.AsSpan(0, SqliteDatabaseHeader.Size));
        using var wal = SqliteWalFile.Open(PhysicalFileSystem.Instance, path + "-wal");
        wal.AppendFrame(1, page.AsSpan(0, header.PageSize));
        wal.Flush();
    }

    private static string ManagedEncryptionConnectionString(string path, string key)
        => $"Data Source={path};Local Provider=Managed;Encryption Cipher=Aes256Gcm;Encryption Key={key}";

    private static long ExecuteAhtolaScalar(global::Ahtola.AhtolaConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private static long Scalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static long ReadFileLength(IFileSystem fileSystem, string path)
    {
        using var file = fileSystem.OpenFile(path, FileOpenMode.OpenExisting, readOnly: true);
        return file.Length;
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "managed-persistence-boundary-regression-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():N}.db");
    }

    private static void RestoreWriteAccess(string path)
    {
        if (File.Exists(path))
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var candidate = path + suffix;
            RestoreWriteAccess(candidate);
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }
}
