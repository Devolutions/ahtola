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

    [Test]
    public void ManagedReaderOpensNativeDdlChurnedSchemaTreeDatabase()
    {
        var path = CreateDatabasePath("churn-ddl");
        try
        {
            using (var connection = OpenNative(path, readOnly: false))
            {
                Execute(connection, "PRAGMA page_size=512;");
                Execute(connection, "PRAGMA journal_mode=delete;");
                Execute(
                    connection,
                    """
                    CREATE TABLE keep(id INTEGER PRIMARY KEY, value TEXT NOT NULL);
                    INSERT INTO keep VALUES (1, 'kept');
                    """);

                // CREATE/DROP cycles rewrite sqlite_schema repeatedly. The
                // schema tree spills to child leaf pages, and each dropped
                // schema row leaves a freeblock or fragment behind on one of
                // them — the exact page shape from the original wild-caught
                // report (a churned sqlite_schema *leaf*).
                var random = new Random(6054721);
                using (var transaction = connection.BeginTransaction())
                {
                    for (var cycle = 0; cycle < 60; cycle++)
                    {
                        var length = 20 + random.Next(180);
                        var padding = new string('s', length);
                        var longName = "survivor_" + new string('n', 40) + "_" + cycle;
                        Execute(
                            connection,
                            transaction,
                            $"CREATE TABLE {longName}(id INTEGER PRIMARY KEY, pad TEXT NOT NULL);");
                        Execute(
                            connection,
                            transaction,
                            $"INSERT INTO {longName} VALUES ({cycle}, '{padding}');");
                        Execute(
                            connection,
                            transaction,
                            $"CREATE TABLE cycle_{cycle}(id INTEGER PRIMARY KEY, pad TEXT NOT NULL);");
                        if (cycle % 2 == 0)
                            Execute(connection, transaction, $"DROP TABLE cycle_{cycle};");
                    }

                    // Drop some of the surviving odd-cycle tables so their
                    // schema rows become holes while later-created rows remain.
                    for (var cycle = 1; cycle < 40; cycle += 2)
                        Execute(connection, transaction, $"DROP TABLE cycle_{cycle};");

                    transaction.Commit();
                }

                ScalarString(connection, "PRAGMA integrity_check;").Should().Be("ok",
                    "the native author must leave a database SQLite itself accepts");
            }

            var shape = ScanBtreePageShapes(path);
            TestContext.Out.WriteLine(
                $"[native ddl churn 512/delete] b-tree pages={shape.BtreePageCount}, "
                + $"pages with freeblocks={shape.PagesWithFreeblocks}, pages with fragmented bytes={shape.PagesWithFragments}, "
                + $"page 1 is interior={shape.SchemaPageIsInterior}");
            // The sqlite_schema tree must be deep enough to spill (page 1
            // becomes interior) and its leaf pages must carry churn damage from
            // the dropped schema rows.
            shape.SchemaPageIsInterior.Should().BeTrue(
                "60 interleaved CREATE/DROP cycles must push sqlite_schema past one page, otherwise this test no longer exercises multi-level schema-tree reads");
            shape.PagesWithFreeblocks.Should().BeGreaterThan(0,
                "dropped schema rows must leave freeblocks behind on the schema leaves, otherwise this test no longer exercises churned schema-page reads");

            using var reference = OpenNative(path, readOnly: true);
            using var managed = OpenManaged(path);
            ScalarString(managed, "PRAGMA quick_check;").Should().Be("ok",
                "the managed reader must accept the churned sqlite_schema tree");

            var diagnostics = "native churn interop (DDL-churned schema tree)";
            TypedSqliteOracle.AssertEquivalent(
                managed,
                reference,
                "SELECT type, name FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name;",
                ordered: true,
                diagnostics);
            TypedSqliteOracle.AssertEquivalent(
                managed,
                reference,
                "SELECT * FROM keep;",
                ordered: true,
                diagnostics);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void ManagedReaderOpensNativeChurnedNocaseIndexDatabase()
    {
        var path = CreateDatabasePath("churn-nocase");
        try
        {
            using (var connection = OpenNative(path, readOnly: false))
            {
                Execute(connection, "PRAGMA page_size=4096;");
                Execute(connection, "PRAGMA journal_mode=delete;");
                Execute(
                    connection,
                    """
                    CREATE TABLE items(id INTEGER PRIMARY KEY, name TEXT NOT NULL, alt TEXT NOT NULL);
                    CREATE INDEX items_name ON items(name COLLATE NOCASE);
                    CREATE INDEX items_alt_desc ON items(alt COLLATE NOCASE DESC);
                    """);

                // Seed keys that are NOCASE-ascending but NOT BINARY-ascending,
                // so a BINARY-assuming order validation would reject the page.
                var random = new Random(0xC0FFEE);
                using (var transaction = connection.BeginTransaction())
                {
                    using (var insert = connection.CreateCommand())
                    {
                        insert.Transaction = transaction;
                        insert.CommandText =
                            "INSERT INTO items(id, name, alt) VALUES ($id, $name, $alt);";
                        var keys = new[]
                        {
                            "Apple", "banana", "Cherry", "date", "Eggplant", "fig",
                            "Grape", "honeydew", "Iceberg", "jujube", "Kale", "lettuce",
                        };
                        for (var round = 0; round < 40; round++)
                        {
                            for (var i = 0; i < keys.Length; i++)
                            {
                                insert.Parameters.Clear();
                                insert.Parameters.AddWithValue("$id", (long)(round * keys.Length + i + 1));
                                insert.Parameters.AddWithValue("$name", keys[i] + "-" + round.ToString("D3"));
                                insert.Parameters.AddWithValue("$alt", keys[(keys.Length - 1) - i] + "/" + round.ToString("D3"));
                                insert.ExecuteNonQuery();
                            }
                        }

                        transaction.Commit();
                    }
                }

                using (var transaction = connection.BeginTransaction())
                {
                    Execute(connection, transaction, "DELETE FROM items WHERE id % 5 = 0;");
                    Execute(connection, transaction, "DELETE FROM items WHERE id % 7 = 3;");
                    Execute(connection, transaction, "UPDATE items SET name = 'zebra-' || name WHERE id % 4 = 1;");
                    Execute(connection, transaction, "UPDATE items SET alt = 'alpha-' || alt WHERE id % 6 = 2;");
                    transaction.Commit();
                }

                ScalarString(connection, "PRAGMA integrity_check;").Should().Be("ok",
                    "the native author must leave a database SQLite itself accepts");
            }

            using var reference = OpenNative(path, readOnly: true);
            using var managed = OpenManaged(path);
            ScalarString(managed, "PRAGMA quick_check;").Should().Be("ok",
                "the managed reader must accept the native NOCASE-ordered index pages");

            var diagnostics = "native churn interop (NOCASE indexes)";
            TypedSqliteOracle.AssertEquivalent(
                managed,
                reference,
                "SELECT id, name, alt FROM items ORDER BY id;",
                ordered: true,
                diagnostics);
            // NOCASE equality seeks exercise the collation-aware index path.
            TypedSqliteOracle.AssertEquivalent(
                managed,
                reference,
                "SELECT id, name FROM items WHERE name = 'apple-001' ORDER BY id;",
                ordered: true,
                diagnostics);
            TypedSqliteOracle.AssertEquivalent(
                managed,
                reference,
                "SELECT id, alt FROM items WHERE alt = 'DATE-003' ORDER BY id;",
                ordered: true,
                diagnostics);
        }
        finally
        {
            DeleteDatabase(path);
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
            var schemaPageIsInterior = false;
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
                var hasFreeblocks = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(start + headerOffset + 1)) != 0;
                var hasFragments = bytes[start + headerOffset + 7] != 0;
                if (hasFreeblocks)
                    pagesWithFreeblocks++;
                if (hasFragments)
                    pagesWithFragments++;
                if (index == 0)
                    schemaPageIsInterior = pageType is 2 or 5;
            }

            return new ChurnedFileShape(
                pageSize,
                btreePageCount,
                pagesWithFreeblocks,
                pagesWithFragments,
                schemaPageIsInterior);
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
        int PagesWithFragments,
        bool SchemaPageIsInterior);
}