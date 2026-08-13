using System.Reflection;
using System.Runtime.ExceptionServices;
using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public class ManagedFileCompactionDurabilityTests
{
    [Test]
    public void ManagedCompactionReclaimsPressurePagesPreservesOverflowIndexesAndReopens()
    {
        var path = CreateDatabasePath();
        try
        {
            long expandedLength;
            using (var database = EmbeddedDatabase.OpenFile(path))
            {
                using (var connection = database.Connect())
                {
                    Execute(connection, "CREATE TABLE entries(id INTEGER PRIMARY KEY, category TEXT, payload TEXT);");
                    Execute(connection, "CREATE INDEX entries_category ON entries(category);");
                    Execute(connection, BuildInsertRows(1, 84));
                    Execute(connection, "DELETE FROM entries WHERE id > 7;");
                }

                expandedLength = new FileInfo(path).Length;
                InvokeManagedCompaction(database);
                new FileInfo(path).Length.Should().BeLessThan(expandedLength);

                using var pager = SqlitePager.Open(
                    PhysicalFileSystem.Instance,
                    path,
                    path + "-wal",
                    readOnly: true);
                var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
                header.DatabaseSizeInPages.Should().Be(pager.CommittedPageCount);
                header.FreelistPageCount.Should().Be(0);
                new FileInfo(path).Length.Should().Be((long)header.PageSize * header.DatabaseSizeInPages);
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                Scalar(connection, "SELECT COUNT(*) FROM entries;").Should().Be(7);
                var retainedCategory = "category-003-" + new string('c', 72);
                Scalar(connection, $"SELECT id FROM entries WHERE category = '{retainedCategory}';")
                    .Should()
                    .Be(3);
            }

            VerifyWithSqlite(path);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void InterruptedManagedCompactionRecoversFromWalBeforeReclaimingItsTail()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "managed-compaction-interrupted.db";

        var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        try
        {
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE entries(id INTEGER PRIMARY KEY, payload TEXT);");
                Execute(connection, BuildInsertRows(1, 36, includeCategory: false));
                Execute(connection, "DELETE FROM entries WHERE id > 4;");
            }

            var expandedLength = ReadFileLength(fileSystem, path);
            faults.FailNext(FileSystemOperation.SetLength);
            Assert.Throws<IOException>(() => InvokeManagedCompaction(database));
            ReadFileLength(fileSystem, path).Should().Be(expandedLength);
        }
        finally
        {
            database.Dispose();
        }

        using (var recovered = EmbeddedDatabase.OpenFile(path, fileSystem))
        {
            using (var connection = recovered.Connect())
                Scalar(connection, "SELECT COUNT(*) FROM entries;").Should().Be(4);

            InvokeManagedCompaction(recovered);
        }

        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        header.DatabaseSizeInPages.Should().Be(pager.CommittedPageCount);
        ReadFileLength(fileSystem, path).Should().Be((long)header.PageSize * header.DatabaseSizeInPages);
    }

    [Test]
    public void ShrinkCheckpointRejectsTrailingPagesWhenItsRecoveryWalIsMissing()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string databasePath = "shrink-corruption.db";
        const string walPath = "shrink-corruption.db-wal";
        var walHeader = SqliteWalHeader.Create(SqlitePageSize.Default, salt1: 41, salt2: 42);

        using (var pager = SqlitePager.Create(fileSystem, databasePath, walPath, walHeader))
        {
            using (var growth = pager.BeginTransaction(targetDatabaseSizeInPages: 3))
            {
                growth.WritePage(2, CreatePage(pager.PageSize, 0x41));
                growth.WritePage(3, CreatePage(pager.PageSize, 0x42));
                growth.Commit();
            }

            pager.CheckpointToMainStore();
            var compactPageOne = pager.ReadCommittedPage(1);
            var currentHeader = SqliteDatabaseHeader.Parse(compactPageOne);
            (currentHeader with
            {
                ChangeCounter = currentHeader.ChangeCounter + 1,
                DatabaseSizeInPages = 1,
                VersionValidFor = currentHeader.ChangeCounter + 1,
            }).WriteTo(compactPageOne);

            using (var shrink = pager.BeginTransaction(targetDatabaseSizeInPages: 1))
            {
                shrink.WritePage(1, compactPageOne);
                shrink.Commit();
            }

            faults.FailNext(FileSystemOperation.SetLength);
            Assert.Throws<IOException>(() => pager.CheckpointToMainStore());
        }

        fileSystem.DeleteFile(walPath);
        using (SqliteWalFile.Create(fileSystem, walPath, walHeader))
        {
        }

        Assert.Throws<InvalidDataException>(() => SqlitePager.Open(fileSystem, databasePath, walPath));
    }

    [Test]
    public void EncryptedShrinkCheckpointReclaimsPagesThroughThePagerOnly()
    {
        var fileSystem = new InMemoryFileSystem();
        using var encryption = new AhtolaEncryptionOptions(
            AhtolaEncryptionCipher.Aes256Gcm,
            Convert.FromHexString("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F"));
        var walHeader = SqliteWalHeader.Create(SqlitePageSize.Default, salt1: 51, salt2: 52);

        using (var pager = SqlitePager.Create(
                   fileSystem,
                   "encrypted-shrink.db",
                   "encrypted-shrink.db-wal",
                   walHeader,
                   encryption: encryption))
        {
            using (var growth = pager.BeginTransaction(targetDatabaseSizeInPages: 2))
            {
                growth.WritePage(2, CreatePage(pager.PageSize, 0x51, reservedSpace: 28));
                growth.Commit();
            }

            pager.CheckpointToMainStore();
            var compactPageOne = pager.ReadCommittedPage(1);
            var currentHeader = SqliteDatabaseHeader.Parse(compactPageOne);
            (currentHeader with
            {
                ChangeCounter = currentHeader.ChangeCounter + 1,
                DatabaseSizeInPages = 1,
                VersionValidFor = currentHeader.ChangeCounter + 1,
            }).WriteTo(compactPageOne);

            using (var shrink = pager.BeginTransaction(targetDatabaseSizeInPages: 1))
            {
                shrink.WritePage(1, compactPageOne);
                shrink.Commit();
            }

            pager.CheckpointToMainStore();
        }

        ReadFileLength(fileSystem, "encrypted-shrink.db").Should().Be(SqlitePageSize.Default);
        using var reopened = SqlitePager.Open(
            fileSystem,
            "encrypted-shrink.db",
            "encrypted-shrink.db-wal",
            readOnly: true,
            encryption: encryption);
        reopened.CommittedPageCount.Should().Be(1);
        SqliteDatabaseHeader.Parse(reopened.ReadCommittedPage(1)).DatabaseSizeInPages.Should().Be(1);
    }

    private static void InvokeManagedCompaction(EmbeddedDatabase database)
    {
        var store = typeof(EmbeddedDatabase)
            .GetField("_fileStore", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(database)
            ?? throw new InvalidOperationException("Managed file database has no file store.");
        var compact = store.GetType().GetMethod("Compact", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Managed file compaction primitive was not found.");

        try
        {
            compact.Invoke(store, null);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static void VerifyWithSqlite(string path)
    {
        var verificationPath = path + ".verify.db";
        File.Copy(path, verificationPath, overwrite: true);
        try
        {
            using var sqlite = new MsData.SqliteConnection($"Data Source={verificationPath}");
            sqlite.Open();
            using var integrity = sqlite.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check;";
            integrity.ExecuteScalar().Should().Be("ok");
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(verificationPath);
        }
    }

    private static string BuildInsertRows(int firstId, int count, bool includeCategory = true)
    {
        var payload = "payload-" + new string('p', 5_000);
        var rows = Enumerable.Range(firstId, count).Select(id => includeCategory
            ? $"({id}, 'category-{id:D3}-{new string('c', 72)}', '{payload}{id:D3}')"
            : $"({id}, '{payload}{id:D3}')");
        return $"INSERT INTO entries VALUES {string.Join(", ", rows)};";
    }

    private static byte[] CreatePage(int pageSize, byte fill, int reservedSpace = 0)
    {
        var page = new byte[pageSize];
        page.AsSpan(0, pageSize - reservedSpace).Fill(fill);
        return page;
    }

    private static long ReadFileLength(IFileSystem fileSystem, string path)
    {
        using var file = fileSystem.OpenFile(path, FileOpenMode.OpenExisting, readOnly: true);
        return file.Length;
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

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "managed-file-compaction-durability-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"compact-{Guid.NewGuid():N}.db");
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
