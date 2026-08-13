using System.Reflection;
using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public sealed class ManagedBackupCoreSnapshotAdapterBoundaryTests
{
    [Test]
    public void ManagedBackupUsesCoreSnapshotAdaptersWithoutRawHandles()
    {
        typeof(IManagedConnectionAdapter).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Should()
            .NotContain("Turso.Raw");

        using var source = OpenManagedConnection();
        using var destination = OpenManagedConnection();
        source.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('source');");

        GetPrivateField(source, "_database").Should().BeNull();
        GetPrivateField(destination, "_database").Should().BeNull();

        source.BackupDatabase(destination);

        GetPrivateField(source, "_database").Should().BeNull();
        GetPrivateField(destination, "_database").Should().BeNull();
        destination.ExecuteScalar<string>("SELECT value FROM source_data;").Should().Be("source");
    }

    [Test]
    public void CoreSnapshotCopyReleasesSourceAndDestinationAdaptersAfterSuccess()
    {
        using var sourceDatabase = ManagedDatabaseAdapter.Open(":memory:");
        using var destinationDatabase = ManagedDatabaseAdapter.Open(":memory:");
        using var source = sourceDatabase.Connect();
        using var destination = destinationDatabase.Connect();
        Execute(source, "CREATE TABLE source_data(value TEXT);");
        Execute(source, "INSERT INTO source_data VALUES ('before');");

        source.CopySnapshotTo(destination);

        Execute(source, "BEGIN;");
        Execute(source, "ROLLBACK;");
        Execute(destination, "INSERT INTO source_data VALUES ('after');");
        Scalar(destination, "SELECT COUNT(*) FROM source_data;").Should().Be(2);
        Scalar(source, "SELECT COUNT(*) FROM source_data;").Should().Be(1);
    }

    [Test]
    public void CoreSnapshotCopyRollsBackDestinationAndReleasesSourceAfterFailure()
    {
        using var sourceDatabase = ManagedDatabaseAdapter.Open(":memory:");
        using var destinationDatabase = ManagedDatabaseAdapter.Open(":memory:");
        using var source = sourceDatabase.Connect();
        using var destination = destinationDatabase.Connect();
        Execute(destination, "PRAGMA foreign_keys = ON;");
        Execute(source, "CREATE TABLE copied_data(value TEXT);");
        Execute(source, "INSERT INTO copied_data VALUES ('source');");
        Execute(source, "CREATE TABLE inaccessible_rowid(rowid TEXT, _rowid_ TEXT, oid TEXT);");
        Execute(source, "INSERT INTO inaccessible_rowid VALUES ('a', 'b', 'c');");

        var exception = Assert.Throws<ManagedSnapshotException>(() => source.CopySnapshotTo(destination));

        exception!.Failure.Should().Be(ManagedSnapshotFailure.RowidNotAccessible);
        exception.ObjectName.Should().Be("inaccessible_rowid");
        Scalar(destination, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';").Should().Be(0);
        Scalar(destination, "PRAGMA foreign_keys;").Should().Be(1);
        Execute(source, "BEGIN;");
        Execute(source, "ROLLBACK;");
        Execute(destination, "CREATE TABLE destination_still_usable(value TEXT);");
        Scalar(destination, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';").Should().Be(1);
    }

    [Test]
    public void CoreSnapshotCopyRestoresDestinationForeignKeysForChildFirstSchemas()
    {
        using var sourceDatabase = ManagedDatabaseAdapter.Open(":memory:");
        using var destinationDatabase = ManagedDatabaseAdapter.Open(":memory:");
        using var source = sourceDatabase.Connect();
        using var destination = destinationDatabase.Connect();
        Execute(source, "CREATE TABLE child(parent_id INTEGER REFERENCES parent(id));");
        Execute(source, "CREATE TABLE parent(id INTEGER PRIMARY KEY);");
        Execute(source, "INSERT INTO parent VALUES (1);");
        Execute(source, "INSERT INTO child VALUES (1);");
        Execute(destination, "PRAGMA foreign_keys = ON;");

        source.CopySnapshotTo(destination);

        Scalar(destination, "PRAGMA foreign_keys;").Should().Be(1);
        Scalar(destination, "SELECT COUNT(*) FROM child;").Should().Be(1);
        Action invalidChild = () => Execute(destination, "INSERT INTO child VALUES (2);");
        invalidChild.Should().Throw<EmbeddedSqlException>().WithMessage("FOREIGN KEY constraint failed");
    }

    [Test]
    public void CoreSnapshotPreservesCompositeActionsAndDeferral()
    {
        using var sourceDatabase = ManagedDatabaseAdapter.Open(":memory:");
        using var destinationDatabase = ManagedDatabaseAdapter.Open(":memory:");
        using var source = sourceDatabase.Connect();
        using var destination = destinationDatabase.Connect();
        Execute(source, "CREATE TABLE parent(a INTEGER, b INTEGER, PRIMARY KEY(a, b));");
        Execute(
            source,
            "CREATE TABLE child(id INTEGER PRIMARY KEY, a INTEGER, b INTEGER, "
                + "FOREIGN KEY(a, b) REFERENCES parent "
                + "ON UPDATE CASCADE ON DELETE SET NULL DEFERRABLE INITIALLY DEFERRED);");
        Execute(source, "INSERT INTO parent VALUES (1, 2);");
        Execute(source, "INSERT INTO child VALUES (10, 1, 2);");
        Execute(destination, "PRAGMA foreign_keys = ON;");

        source.CopySnapshotTo(destination);

        Scalar(destination, "PRAGMA foreign_keys;").Should().Be(1);
        Execute(destination, "UPDATE parent SET a = 3, b = 4;");
        Scalar(destination, "SELECT a FROM child WHERE id = 10;").Should().Be(3);
        Execute(destination, "BEGIN;");
        Execute(destination, "INSERT INTO child VALUES (11, 9, 9);");
        Assert.Throws<EmbeddedSqlException>(() => Execute(destination, "COMMIT;"))!
            .Message.Should().Be("FOREIGN KEY constraint failed");
        Execute(destination, "INSERT INTO parent VALUES (9, 9);");
        Execute(destination, "COMMIT;");
        Execute(destination, "DELETE FROM parent WHERE a = 3 AND b = 4;");
        Scalar(destination, "SELECT a IS NULL FROM child WHERE id = 10;").Should().Be(1);
    }

    [Test]
    public void CoreSnapshotCopyUsesAndPreservesAnExistingSourceTransaction()
    {
        using var sourceDatabase = ManagedDatabaseAdapter.Open(":memory:");
        using var destinationDatabase = ManagedDatabaseAdapter.Open(":memory:");
        using var source = sourceDatabase.Connect();
        using var destination = destinationDatabase.Connect();
        Execute(source, "CREATE TABLE source_data(value TEXT);");
        Execute(source, "BEGIN;");
        Execute(source, "INSERT INTO source_data VALUES ('uncommitted');");

        source.CopySnapshotTo(destination);

        Scalar(source, "SELECT COUNT(*) FROM source_data;").Should().Be(1);
        Scalar(destination, "SELECT COUNT(*) FROM source_data;").Should().Be(1);
        Execute(source, "ROLLBACK;");
        Scalar(source, "SELECT COUNT(*) FROM source_data;").Should().Be(0);
        Scalar(destination, "SELECT COUNT(*) FROM source_data;").Should().Be(1);
    }

    [Test]
    public void CoreSnapshotWriteFailureKeepsPriorDestinationDurable()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        using (var sourceDatabase = ManagedDatabaseAdapter.OpenFile("backup-source.db", fileSystem))
        using (var destinationDatabase = ManagedDatabaseAdapter.OpenFile("backup-destination.db", fileSystem))
        using (var source = sourceDatabase.Connect())
        using (var destination = destinationDatabase.Connect())
        {
            Execute(source, "CREATE TABLE source_data(value TEXT);");
            Execute(source, "INSERT INTO source_data VALUES ('source');");
            Execute(destination, "CREATE TABLE preserved(value TEXT);");
            Execute(destination, "INSERT INTO preserved VALUES ('destination');");

            faults.FailNext(FileSystemOperation.Write);
            Action copy = () => source.CopySnapshotTo(destination);

            copy.Should().Throw<IOException>();
        }

        using var reopenedDatabase = ManagedDatabaseAdapter.OpenFile("backup-destination.db", fileSystem);
        using var reopened = reopenedDatabase.Connect();
        Scalar(reopened, "SELECT COUNT(*) FROM preserved;").Should().Be(1);
        Scalar(reopened, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'source_data';").Should().Be(0);
    }

    [Test]
    public void CoreSnapshotPublishesFromCommitMetadataWithoutPostCommitRead()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        using var sourceDatabase = ManagedDatabaseAdapter.OpenFile("source.db", fileSystem);
        using var destinationDatabase = ManagedDatabaseAdapter.OpenFile("destination.db", fileSystem);
        using var source = sourceDatabase.Connect();
        using var destination = destinationDatabase.Connect();
        PrepareReplacementPair(source, destination);

        faults.FailNextAfter(FileSystemOperation.SetLength, FileSystemOperation.Read);

        source.CopySnapshotTo(destination);

        Scalar(destination, "SELECT COUNT(*) FROM source_data;").Should().Be(1);
        Scalar(destination, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'preserved';").Should().Be(0);
        faults.ClearScheduled();
    }

    [Test]
    public void CoreSnapshotKeepsCommittedClassificationWhenCheckpointMaintenanceAndReadFail()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        using (var sourceDatabase = ManagedDatabaseAdapter.OpenFile("source.db", fileSystem))
        using (var destinationDatabase = ManagedDatabaseAdapter.OpenFile("destination.db", fileSystem))
        using (var source = sourceDatabase.Connect())
        using (var destination = destinationDatabase.Connect())
        {
            PrepareReplacementPair(source, destination);

            faults.FailNextAfter(FileSystemOperation.SetLength, FileSystemOperation.Read);
            faults.FailNext(FileSystemOperation.SetLength);

            Assert.Throws<EmbeddedPostCommitMaintenanceException>(
                () => source.CopySnapshotTo(destination));
            Scalar(destination, "SELECT COUNT(*) FROM source_data;").Should().Be(1);
            faults.ClearScheduled();
        }

        using var reopenedDatabase = ManagedDatabaseAdapter.OpenFile("destination.db", fileSystem);
        using var reopened = reopenedDatabase.Connect();
        Scalar(reopened, "SELECT COUNT(*) FROM source_data;").Should().Be(1);
        Scalar(reopened, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'preserved';").Should().Be(0);
    }

    [Test]
    public void CoreSnapshotRejectsSamePhysicalFileAcrossFileSystemInstances()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "managed-backup-core-tests");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"same-file-{Guid.NewGuid():N}.db");
        try
        {
            using (var sourceDatabase = ManagedDatabaseAdapter.OpenFile(path, new PhysicalFileSystem()))
            using (var source = sourceDatabase.Connect())
            {
                Execute(source, "CREATE TABLE preserved(value TEXT);");
                Execute(source, "INSERT INTO preserved VALUES ('same file');");

                using var destinationDatabase = ManagedDatabaseAdapter.OpenFile(path, new PhysicalFileSystem());
                using var destination = destinationDatabase.Connect();
                Action copy = () => source.CopySnapshotTo(destination);

                copy.Should().Throw<EmbeddedSqlException>()
                    .WithMessage("source and destination must be distinct");
            }

            using var reopenedDatabase = ManagedDatabaseAdapter.OpenFile(path, new PhysicalFileSystem());
            using var reopened = reopenedDatabase.Connect();
            Scalar(reopened, "SELECT COUNT(*) FROM preserved;").Should().Be(1);
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var candidate = path + suffix;
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
        }
    }

    [Test]
    public void CoreSnapshotRejectsUnknownFileSystemIdentityWithoutChangingDestination()
    {
        var fileSystem = new UnknownIdentityFileSystem(new InMemoryFileSystem());
        using var sourceDatabase = ManagedDatabaseAdapter.OpenFile("source.db", fileSystem);
        using var destinationDatabase = ManagedDatabaseAdapter.OpenFile("destination.db", fileSystem);
        using var source = sourceDatabase.Connect();
        using var destination = destinationDatabase.Connect();
        PrepareReplacementPair(source, destination);

        var exception = Assert.Throws<ManagedSnapshotException>(
            () => source.CopySnapshotTo(destination));

        exception!.Failure.Should().Be(ManagedSnapshotFailure.PhysicalFileIdentityUnavailable);
        Scalar(destination, "SELECT COUNT(*) FROM preserved;").Should().Be(1);
        Scalar(destination, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'source_data';").Should().Be(0);
    }

    private static SqliteConnection OpenManagedConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        return connection;
    }

    private static void Execute(IManagedConnectionAdapter connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    private static void PrepareReplacementPair(
        IManagedConnectionAdapter source,
        IManagedConnectionAdapter destination)
    {
        Execute(source, "CREATE TABLE source_data(value TEXT);");
        Execute(source, "INSERT INTO source_data VALUES ('source');");
        Execute(destination, "CREATE TABLE preserved(value TEXT);");
        Execute(destination, "INSERT INTO preserved VALUES ('destination');");
    }

    private static long Scalar(IManagedConnectionAdapter connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static object? GetPrivateField(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Expected {instance.GetType().Name}.{fieldName}.");
        return field.GetValue(instance);
    }

    private sealed class UnknownIdentityFileSystem(IFileSystem inner) : IFileSystem
    {
        public bool FileExists(string path) => inner.FileExists(path);

        public IFile OpenFile(string path, FileOpenMode mode, bool readOnly = false)
            => inner.OpenFile(path, mode, readOnly);

        public void DeleteFile(string path) => inner.DeleteFile(path);
    }
}
