using System.Buffers.Binary;
using System.Data.Common;
using System.Globalization;
using Ahtola.Data.Sqlite;
using Ahtola.Tests.Oracle;
using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// Storage interop harness: native SQLite authors and churns a database file
/// (scattered deletes, size-changing updates, tiny reinserts, overflow-chain
/// churn), and the managed reader must open the very same file that SQLite's
/// own integrity checks accept.
/// </summary>
/// <remarks>
/// This is the regression class behind the wild-caught managed-reader
/// rejections (stale sqlite_schema separators, stale interior separators, and
/// untracked fragmented gaps): none of those page shapes were reachable
/// through Ahtola-authored pages, because the managed writer always packs
/// pages densely with zero fragmented bytes and zero freeblocks. Only a
/// foreign author exercises the declared fragmented-byte accounting that
/// <see cref="Ahtola.Core.Storage.SqliteBtreePageValidation"/> performs.
///
/// Reserved-space databases (SEE/SQLCipher-style per-page suffixes) are not
/// authored here: the bundled e_sqlite3 build ships no reserve_bytes pragma,
/// so this stack cannot produce a foreign reserved-space fixture. Ahtola's
/// reserved-space reader plumbing is covered by the encrypted-storage suite
/// (reserved space 28) instead.
/// </remarks>
[NonParallelizable]
public sealed class NativeSqliteChurnInteropTests
{
    [TestCase(512, "delete")]
            [TestCase(4096, "delete")]
            [TestCase(512, "wal")]
            [TestCase(4096, "wal")]
            [TestCase(65536, "delete")]
            public void ManagedReaderOpensNativeChurnedDatabases(int pageSize, string journalMode)
            {
                var path = CreateDatabasePath($"churn-{pageSize}-{journalMode}");
                try
                {
                    AuthorChurnedDatabase(path, pageSize, journalMode);
                    var shape = ScanBtreePageShapes(path);
                    shape.PagesWithFreeblocks.Should().BeGreaterThan(0,
                        "scattered deletes must leave freeblocks behind, otherwise this harness no longer exercises freeblock-aware page reads");
                    TestContext.Out.WriteLine(
                        $"[native churn {pageSize}/{journalMode}] b-tree pages={shape.BtreePageCount}, "
                        + $"pages with freeblocks={shape.PagesWithFreeblocks}, pages with fragmented bytes={shape.PagesWithFragments}");

                    AssertManagedReaderMatchesNativeReference(path, pageSize, journalMode, suffix: string.Empty);
                }
                finally
                {
                    DeleteDatabase(path);
                }
            }

            /// <summary>
            /// Authors the churned fixture with native SQLite and verifies it is
            /// self-consistent before the managed engine ever touches it.
            /// </summary>
            private static void AuthorChurnedDatabase(
                string path,
                int pageSize,
                string journalMode)
            {
                using var connection = OpenNative(path, readOnly: false);
                Execute(connection, $"PRAGMA page_size={pageSize};");
                ScalarString(connection, $"PRAGMA journal_mode={journalMode};").Should().Be(journalMode);

        Execute(
            connection,
            """
            CREATE TABLE churn(
                id INTEGER PRIMARY KEY,
                bucket INTEGER NOT NULL,
                label TEXT NOT NULL,
                payload TEXT NOT NULL);
            CREATE INDEX churn_bucket ON churn(bucket, label);
            CREATE TABLE blobs(id INTEGER PRIMARY KEY, data BLOB NOT NULL);
            CREATE TABLE kv(key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;
            """);

        var random = new Random(pageSize ^ (journalMode.Length * 31));
        using (var transaction = connection.BeginTransaction())
        {
            SeedRows(connection, transaction, random);
            transaction.Commit();
        }

        using (var transaction = connection.BeginTransaction())
        {
            ApplyChurnWave(connection, transaction, random);
            transaction.Commit();
        }

        using (var transaction = connection.BeginTransaction())
        {
            ApplyCompoundWave(connection, transaction, random);
            transaction.Commit();
        }

        ScalarString(connection, "PRAGMA integrity_check;").Should().Be("ok",
                    "the native author must leave a database SQLite itself accepts");
                if (journalMode == "wal")
                    Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
            }

    private static void SeedRows(MsData.SqliteConnection connection, MsData.SqliteTransaction transaction, Random random)
    {
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO churn(id, bucket, label, payload) VALUES ($id, $bucket, $label, $payload);";
            for (var id = 1; id <= 900; id++)
            {
                insert.Parameters.Clear();
                insert.Parameters.AddWithValue("$id", (long)id);
                insert.Parameters.AddWithValue("$bucket", (long)(id % 37));
                insert.Parameters.AddWithValue("$label", "label-" + id.ToString("D4"));
                insert.Parameters.AddWithValue("$payload", BuildText(random, 1, 120));
                insert.ExecuteNonQuery();
            }
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO blobs(id, data) VALUES ($id, $data);";
            for (var id = 1; id <= 48; id++)
            {
                var data = new byte[300 + random.Next(8_700)];
                random.NextBytes(data);
                insert.Parameters.Clear();
                insert.Parameters.AddWithValue("$id", (long)id);
                insert.Parameters.AddWithValue("$data", data);
                insert.ExecuteNonQuery();
            }
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO kv(key, value) VALUES ($key, $value);";
            for (var id = 1; id <= 300; id++)
            {
                insert.Parameters.Clear();
                insert.Parameters.AddWithValue("$key", "key-" + id.ToString("D4"));
                insert.Parameters.AddWithValue("$value", BuildText(random, 3, 60));
                insert.ExecuteNonQuery();
            }
        }
    }

    private static void ApplyChurnWave(
        MsData.SqliteConnection connection,
        MsData.SqliteTransaction transaction,
        Random random)
    {
        Execute(connection, transaction, "DELETE FROM churn WHERE id % 3 = 0;");
        Execute(connection, transaction, "DELETE FROM blobs WHERE id % 4 = 0;");
        Execute(
            connection,
            transaction,
            "DELETE FROM kv WHERE CAST(SUBSTR(key, 5) AS INTEGER) % 2 = 0;");

        // Shrink some rows: the shortened cells leave freeblocks behind them.
        ShrinkPayloads(connection, transaction, random, firstId: 1, lastId: 900, every: 5, minLength: 1, maxLength: 12);
        // Grow some rows: growing cells relocate and leave more free space behind.
        ShrinkPayloads(connection, transaction, random, firstId: 3, lastId: 900, every: 7, minLength: 130, maxLength: 220);
        // Truncate some blobs: shortens or empties overflow chains.
        TruncateBlobs(connection, transaction, random, every: 5, newSizeMin: 100, newSizeMax: 600);

        // Tiny reinserts land in existing freeblocks; consuming a freeblock
        // with one to three bytes to spare is what produces fragmented bytes.
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO churn(id, bucket, label, payload) VALUES ($id, $bucket, $label, $payload);";
            for (var id = 1000; id <= 1150; id++)
            {
                insert.Parameters.Clear();
                insert.Parameters.AddWithValue("$id", (long)id);
                insert.Parameters.AddWithValue("$bucket", (long)(id % 7));
                insert.Parameters.AddWithValue("$label", "re-" + id.ToString("D4"));
                insert.Parameters.AddWithValue("$payload", BuildText(random, 1, 8));
                insert.ExecuteNonQuery();
            }
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO kv(key, value) VALUES ($key, $value);";
            for (var id = 1000; id <= 1080; id++)
            {
                insert.Parameters.Clear();
                insert.Parameters.AddWithValue("$key", "new-" + id.ToString("D4"));
                insert.Parameters.AddWithValue("$value", BuildText(random, 1, 40));
                insert.ExecuteNonQuery();
            }
        }
    }

    private static void ApplyCompoundWave(
        MsData.SqliteConnection connection,
        MsData.SqliteTransaction transaction,
        Random random)
    {
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO churn(id, bucket, label, payload) VALUES ($id, $bucket, $label, $payload);";
            for (var id = 2000; id <= 2130; id++)
            {
                insert.Parameters.Clear();
                insert.Parameters.AddWithValue("$id", (long)id);
                insert.Parameters.AddWithValue("$bucket", (long)(id % 11));
                insert.Parameters.AddWithValue("$label", "wave3-" + id.ToString("D4"));
                insert.Parameters.AddWithValue("$payload", BuildText(random, 2, 20));
                insert.ExecuteNonQuery();
            }
        }

        Execute(connection, transaction, "DELETE FROM churn WHERE id % 11 = 0;");
        Execute(
            connection,
            transaction,
            "DELETE FROM churn WHERE id BETWEEN 1100 AND 1149 AND id % 2 = 0;");

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO blobs(id, data) VALUES ($id, $data);";
            for (var id = 200; id <= 210; id++)
            {
                var data = new byte[1_500 + random.Next(5_000)];
                random.NextBytes(data);
                insert.Parameters.Clear();
                insert.Parameters.AddWithValue("$id", (long)id);
                insert.Parameters.AddWithValue("$data", data);
                insert.ExecuteNonQuery();
            }
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE kv SET value = $value WHERE key = $key;";
            for (var id = 1; id <= 300; id += 3)
            {
                update.Parameters.Clear();
                update.Parameters.AddWithValue("$key", "key-" + id.ToString("D4"));
                update.Parameters.AddWithValue("$value", BuildText(random, 50, 90));
                update.ExecuteNonQuery();
            }
        }
    }

    private static void ShrinkPayloads(
        MsData.SqliteConnection connection,
        MsData.SqliteTransaction transaction,
        Random random,
        int firstId,
        int lastId,
        int every,
        int minLength,
        int maxLength)
    {
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE churn SET payload = $payload WHERE id = $id;";
        for (var id = firstId; id <= lastId; id += every)
        {
            update.Parameters.Clear();
            update.Parameters.AddWithValue("$id", (long)id);
            update.Parameters.AddWithValue("$payload", BuildText(random, minLength, maxLength));
            update.ExecuteNonQuery();
        }
    }

    private static void TruncateBlobs(
        MsData.SqliteConnection connection,
        MsData.SqliteTransaction transaction,
        Random random,
        int every,
        int newSizeMin,
        int newSizeMax)
    {
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE blobs SET data = $data WHERE id = $id;";
        for (var id = 2; id <= 48; id += every)
        {
            var data = new byte[newSizeMin + random.Next(newSizeMax - newSizeMin + 1)];
            random.NextBytes(data);
            update.Parameters.Clear();
            update.Parameters.AddWithValue("$id", (long)id);
            update.Parameters.AddWithValue("$data", data);
            update.ExecuteNonQuery();
        }
    }

    private static void AssertManagedReaderMatchesNativeReference(
        string path,
        int pageSize,
        string journalMode,
        string suffix)
    {
        using var reference = OpenNative(path, readOnly: true);
        using var managed = OpenManaged(path);

        ScalarString(managed, "PRAGMA quick_check;").Should().Be("ok", "the managed reader must accept the native file");

        var diagnostics = $"native churn interop (page size {pageSize}, journal {journalMode}{suffix})";
        TypedSqliteOracle.AssertEquivalent(
            managed,
            reference,
            "SELECT id, bucket, label, payload FROM churn ORDER BY id;",
            ordered: true,
            diagnostics);
        TypedSqliteOracle.AssertEquivalent(
            managed,
            reference,
            "SELECT bucket, COUNT(*) FROM churn GROUP BY bucket ORDER BY bucket;",
            ordered: true,
            diagnostics);
        TypedSqliteOracle.AssertEquivalent(
            managed,
            reference,
            "SELECT id, data FROM blobs ORDER BY id;",
            ordered: true,
            diagnostics);
        TypedSqliteOracle.AssertEquivalent(
            managed,
            reference,
            "SELECT key, value FROM kv ORDER BY key;",
            ordered: true,
            diagnostics);
    }

    /// <summary>
    /// Scans the authored file's raw bytes and reports how many b-tree pages
    /// carry freeblocks or fragmented free bytes, so the harness can prove it
    /// still produces the shapes this suite exists to cover.
    /// </summary>
    private static ChurnedFileShape ScanBtreePageShapes(string path)
    {
        var bytes = File.ReadAllBytes(path);
                var encodedPageSize = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(16));
                var pageSize = encodedPageSize == 1 ? 65536 : encodedPageSize;
                var pageCount = bytes.Length / pageSize;
        var btreePageCount = 0;
        var pagesWithFreeblocks = 0;
        var pagesWithFragments = 0;
        for (var index = 0; index < pageCount; index++)
        {
            var start = index * pageSize;
            var headerOffset = index == 0 ? 100 : 0;
            if (headerOffset + 8 > pageSize)
                continue;

            var pageType = bytes[start + headerOffset];
            if (pageType is not (2 or 5 or 10 or 13))
                continue;

            btreePageCount++;
            if (BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(start + headerOffset + 1)) != 0)
                pagesWithFreeblocks++;
            if (bytes[start + headerOffset + 7] != 0)
                pagesWithFragments++;
        }

        return new ChurnedFileShape(pageSize, btreePageCount, pagesWithFreeblocks, pagesWithFragments);
    }

    private static string BuildText(Random random, int minLength, int maxLength)
        => new('x', random.Next(minLength, maxLength + 1));

    private static MsData.SqliteConnection OpenNative(string path, bool readOnly)
    {
        var connectionString = readOnly
            ? $"Data Source={path};Mode=ReadOnly;Pooling=False"
            : $"Data Source={path};Pooling=False";
        var connection = new MsData.SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static SqliteConnection OpenManaged(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Local Provider=Managed;Pooling=False");
        connection.Open();
        return connection;
    }

    private static void Execute(MsData.SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static void Execute(
        MsData.SqliteConnection connection,
        MsData.SqliteTransaction transaction,
        string commandText)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static string ScalarString(DbConnection connection, string commandText)
            => Convert.ToString(Scalar(connection, commandText), CultureInfo.InvariantCulture)!;

        private static object Scalar(DbConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return command.ExecuteScalar()!;
    }

    private static string CreateDatabasePath(string suffix)
    {
        var directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "native-churn-interop");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{suffix}-{Guid.NewGuid():N}.db");
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
        {
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }

    private sealed record ChurnedFileShape(
            int PageSize,
            int BtreePageCount,
            int PagesWithFreeblocks,
            int PagesWithFragments);
}