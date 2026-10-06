using System.Data;
using System.Diagnostics;
using AwesomeAssertions;
using Ahtola.Data.Sqlite;
using NativeSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;
using NativeSqliteException = Microsoft.Data.Sqlite.SqliteException;

namespace Ahtola.Tests;

/// <summary>
/// Regressions for the behaviours RDM and DVLS depend on when they replace native SQLite
/// (System.Data.SQLite, Microsoft.Data.Sqlite + e_sqlite3) with the managed provider. Native
/// SQLite runs in the same process as a stand-in for another process: its file locks are
/// handle-scoped on Windows exactly as they would be across processes.
/// </summary>
[NonParallelizable]
public sealed class RdmDvlsCompatibilityTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ahtola-rdm-dvls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        NativeSqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // #1, #6: BEGIN IMMEDIATE takes the write lock up front, so another process cannot write
    // while the transaction is open, in WAL and rollback-journal modes alike.
    [TestCase(true)]
    [TestCase(false)]
    public void ImmediateTransactionExcludesWritersInOtherProcesses(bool wal)
    {
        var path = CreateNativeDatabase(wal, "CREATE TABLE t(v);");
        using var managed = OpenManaged(path, "Default Timeout=1");
        using var transaction = managed.BeginTransaction(deferred: false);
        using var native = OpenNative(path, "Default Timeout=1");

        Assert.Throws<NativeSqliteException>(() => Execute(native, "INSERT INTO t VALUES (1);"))!
            .SqliteErrorCode.Should().Be(5);

        Execute(managed, "INSERT INTO t VALUES (2);", transaction);
        transaction.Commit();
        Execute(native, "INSERT INTO t VALUES (3);");
        Scalar(native, "SELECT group_concat(v) FROM t;").Should().Be("2,3");
    }

    // #1: a managed write waits for (and then sees) the other process's committed write
    // instead of overwriting it from a stale snapshot.
    [TestCase(true)]
    [TestCase(false)]
    public void WriteWaitsForAnotherProcessAndSeesItsCommit(bool wal)
    {
        var path = CreateNativeDatabase(wal, "CREATE TABLE t(v); INSERT INTO t VALUES (1);");
        using var managed = OpenManaged(path, "Default Timeout=10");
        Scalar(managed, "SELECT count(*) FROM t;").Should().Be(1L);
        using var native = OpenNative(path);
        Execute(native, "BEGIN IMMEDIATE; INSERT INTO t VALUES (5);");
        var release = Task.Run(async () =>
        {
            await Task.Delay(500);
            Execute(native, "COMMIT;");
        });

        Execute(managed, "UPDATE t SET v = 4;");
        release.Wait();

        Scalar(managed, "SELECT group_concat(v) FROM t;").Should().Be("4,4");
        using var check = OpenNative(path);
        Scalar(check, "SELECT group_concat(v) FROM t;").Should().Be("4,4");
    }

    // #1: opening the database does not need the write lock another process holds.
    [Test]
    public void OpeningWhileAnotherProcessHoldsTheWalWriteLockSucceeds()
    {
        var path = CreateNativeDatabase(wal: true, "CREATE TABLE t(v); INSERT INTO t VALUES (1);");
        using var native = OpenNative(path);
        Execute(native, "BEGIN IMMEDIATE; INSERT INTO t VALUES (2);");

        using var managed = OpenManaged(path, "Default Timeout=1");
        Scalar(managed, "SELECT count(*) FROM t;").Should().Be(1L);
        Execute(native, "COMMIT;");
        Scalar(managed, "SELECT count(*) FROM t;").Should().Be(2L);
    }

    // #2: SQLite runs DML on a connection that still has a reader open.
    [Test]
    public void WritesRunWhileTheConnectionHasAnOpenReader()
    {
        using var connection = OpenManaged(Path.Combine(_directory, "reader.db"));
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, v); INSERT INTO t(v) VALUES (1), (2), (3);");
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT id FROM t ORDER BY id;";
        using var reader = select.ExecuteReader();
        reader.Read().Should().BeTrue();
        var stopwatch = Stopwatch.StartNew();

        Execute(connection, "UPDATE t SET v = v + 10 WHERE id = 1;");
        Execute(connection, "DELETE FROM t WHERE id = 3;");
        Scalar(connection, "INSERT INTO t(v) VALUES (9) RETURNING v;").Should().Be(9L);

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        while (reader.Read())
        {
        }

        reader.Close();
        Scalar(connection, "SELECT v FROM t WHERE id = 1;").Should().Be(11L);
    }

    // #3, #4: lock contention with another process honours Default Timeout and surfaces as
    // SqliteException 5, with the storage diagnosis as the inner exception.
    [TestCase(true)]
    [TestCase(false)]
    public void CrossProcessContentionWaitsForTheBusyTimeoutAndReportsSqliteBusy(bool wal)
    {
        var path = CreateNativeDatabase(wal, "CREATE TABLE t(v); INSERT INTO t VALUES (1);");
        using var native = OpenNative(path);
        Execute(native, "BEGIN IMMEDIATE;");
        using var managed = OpenManaged(path, "Default Timeout=1");
        var stopwatch = Stopwatch.StartNew();

        var failure = Assert.Throws<SqliteException>(() => Execute(managed, "UPDATE t SET v = 2;"))!;

        stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(800));
        failure.SqliteErrorCode.Should().Be(5);
        failure.Should().BeAssignableTo<System.Data.Common.DbException>();
        failure.InnerException.Should().NotBeNull();

        Execute(native, "COMMIT;");
        Execute(managed, "UPDATE t SET v = 3;");
    }

    // #18: PRAGMA busy_timeout overrides the command timeout for lock waits.
    [Test]
    public void BusyTimeoutPragmaControlsLockWaits()
    {
        var path = CreateNativeDatabase(wal: true, "CREATE TABLE t(v);");
        using var native = OpenNative(path);
        Execute(native, "BEGIN IMMEDIATE;");
        using var managed = OpenManaged(path, "Default Timeout=30");
        Execute(managed, "PRAGMA busy_timeout = 200;");
        Scalar(managed, "PRAGMA busy_timeout;").Should().Be(200L);
        var stopwatch = Stopwatch.StartNew();

        Assert.Throws<SqliteException>(() => Execute(managed, "INSERT INTO t VALUES (1);"))!
            .SqliteErrorCode.Should().Be(5);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        Execute(native, "COMMIT;");
    }

    // #18: the WAL tuning pragmas DVLS sends are honoured and read back.
    [Test]
    public void WalTuningPragmasReadBack()
    {
        using var connection = OpenManaged(Path.Combine(_directory, "pragmas.db"));
        Scalar(connection, "PRAGMA wal_autocheckpoint;").Should().Be(1000L);
        Scalar(connection, "PRAGMA wal_autocheckpoint = 500;").Should().Be(500L);
        Scalar(connection, "PRAGMA wal_autocheckpoint;").Should().Be(500L);
        Scalar(connection, "PRAGMA wal_autocheckpoint = 0;").Should().Be(0L);
        Scalar(connection, "PRAGMA journal_size_limit;").Should().Be(-1L);
        Scalar(connection, "PRAGMA journal_size_limit = 1048576;").Should().Be(1048576L);
        Scalar(connection, "PRAGMA journal_size_limit;").Should().Be(1048576L);
    }

    // #5: a COMMIT that failed busy keeps the transaction and its write lock, and a retried
    // COMMIT succeeds once the lock is free.
    [Test]
    public void BusyCommitKeepsTheTransactionSoARetrySucceeds()
    {
        var path = CreateNativeDatabase(wal: false, "CREATE TABLE t(v);");
        using var managed = OpenManaged(path, "Default Timeout=1");
        using var transaction = managed.BeginTransaction();
        Execute(managed, "INSERT INTO t VALUES (1);", transaction);
        using var native = OpenNative(path, "Default Timeout=1");
        Execute(native, "BEGIN; SELECT count(*) FROM t;");

        Assert.Throws<SqliteException>(transaction.Commit)!.SqliteErrorCode.Should().Be(5);
        Execute(native, "COMMIT;");
        Assert.Throws<NativeSqliteException>(() => Execute(native, "INSERT INTO t VALUES (2);"));

        transaction.Commit();
        Scalar(native, "SELECT count(*) FROM t;").Should().Be(1L);
    }

    // #7: auto_vacuum changes on a database with tables are a no-op, as in SQLite.
    [Test]
    public void AutoVacuumChangeOnAPopulatedDatabaseIsAccepted()
    {
        using var connection = OpenManaged(Path.Combine(_directory, "vacuum.db"));
        Execute(connection, "CREATE TABLE t(v); PRAGMA auto_vacuum = FULL;");
        Scalar(connection, "PRAGMA auto_vacuum;").Should().Be(0L);
    }

    // #8: CURRENT_TIMESTAMP and friends work in UPSERT DO UPDATE.
    [Test]
    public void CurrentTimestampIsAllowedInUpsertDoUpdate()
    {
        using var connection = OpenManaged(Path.Combine(_directory, "upsert.db"));
        Execute(connection, "CREATE TABLE t(ID INTEGER PRIMARY KEY, d TEXT);");
        const string upsert = "INSERT INTO t(ID, d) VALUES (1, 'x') ON CONFLICT(ID) DO UPDATE SET d = CURRENT_TIMESTAMP;";
        Execute(connection, upsert);
        Execute(connection, upsert);
        ((string)Scalar(connection, "SELECT d FROM t;")!).Should().MatchRegex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$");
    }

    // #9: a column operand's collation wins, left first; a CTE/subquery column without a
    // declared collation is BINARY.
    [TestCase("WITH r AS (SELECT 'abc' AS ID) SELECT count(*) FROM r JOIN t ON r.ID = t.ID", 0L)]
    [TestCase("WITH r AS (SELECT 'abc' AS ID) SELECT count(*) FROM r JOIN t ON t.ID = r.ID", 1L)]
    [TestCase("WITH r AS (SELECT 'abc' AS ID) SELECT count(*) FROM r, t WHERE r.ID = t.ID", 0L)]
    [TestCase("SELECT count(*) FROM (SELECT 'abc' AS ID) r JOIN t ON r.ID = t.ID", 0L)]
    [TestCase("SELECT count(*) FROM p JOIN t ON p.ID = t.ID", 0L)]
    [TestCase("SELECT count(*) FROM t WHERE (SELECT 'abc') = t.ID", 1L)]
    public void ComparisonCollationFollowsSqlitePrecedence(string sql, long expected)
    {
        const string setup =
            "CREATE TABLE t(ID TEXT COLLATE NOCASE); INSERT INTO t VALUES ('ABC');"
            + "CREATE TABLE p(ID TEXT); INSERT INTO p VALUES ('abc');";
        using var native = OpenNative(Path.Combine(_directory, "native.db"));
        Execute(native, setup);
        Scalar(native, sql).Should().Be(expected, "the reference engine is the oracle");

        using var managed = OpenManaged(Path.Combine(_directory, "managed.db"));
        Execute(managed, setup);
        Scalar(managed, sql).Should().Be(expected);
    }

    // #10: VACUUM INTO only reads the source, so another connection's write does not block it.
    [Test]
    public void VacuumIntoRunsWhileAnotherConnectionWrites()
    {
        var path = Path.Combine(_directory, "source.db");
        using var writer = OpenManaged(path);
        Execute(writer, "CREATE TABLE t(v); INSERT INTO t VALUES (1);");
        using var transaction = writer.BeginTransaction();
        Execute(writer, "INSERT INTO t VALUES (2);", transaction);
        using var copier = OpenManaged(path, "Default Timeout=1");
        var destination = Path.Combine(_directory, "copy.db");

        Execute(copier, $"VACUUM INTO '{destination}';");
        transaction.Commit();

        using var copy = OpenNative(destination);
        Scalar(copy, "SELECT count(*) FROM t;").Should().Be(1L);
    }

    // #11: locking_mode=EXCLUSIVE takes its lock lazily: the pragma succeeds and the next
    // access reports SQLITE_BUSY while another connection holds the database.
    [Test]
    public void ExclusiveLockingModeReportsBusyOnTheNextAccess()
    {
        var path = CreateNativeDatabase(wal: false, "CREATE TABLE t(v);");
        using var native = OpenNative(path);
        Execute(native, "BEGIN IMMEDIATE;");
        using var managed = OpenManaged(path, "Default Timeout=1");

        Scalar(managed, "PRAGMA locking_mode = EXCLUSIVE;").Should().Be("exclusive");
        Assert.Throws<SqliteException>(() => Scalar(managed, "SELECT count(*) FROM t;"))!
            .SqliteErrorCode.Should().Be(5);

        Execute(native, "COMMIT;");
        Scalar(managed, "SELECT count(*) FROM t;").Should().Be(0L);
        Scalar(managed, "PRAGMA locking_mode;").Should().Be("exclusive");
    }

    // #12: a WAL database copied or left without its -shm opens read-only.
    [Test]
    public void ReadOnlyOpenOfAWalWithoutItsIndexFile()
    {
        var source = Path.Combine(_directory, "source.db");
        var copy = Path.Combine(_directory, "copy.db");
        using (var native = OpenNative(source))
        {
            Execute(native, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE t(v); INSERT INTO t VALUES (1);");
            File.Copy(source, copy);
            File.Copy(source + "-wal", copy + "-wal");
        }

        File.Exists(copy + "-shm").Should().BeFalse();
        using var readOnly = OpenManaged(copy, "Mode=ReadOnly");
        Scalar(readOnly, "SELECT count(*) FROM t;").Should().Be(1L);
        File.Exists(copy + "-shm").Should().BeFalse("a read-only open must not create the index file");
    }

    // #13, #14: Journal Mode=Delete creates the database directly in rollback-journal mode, so
    // no WAL is ever written and nothing is left next to the file after close.
    [Test]
    public void JournalModeKeywordCreatesARollbackJournalDatabaseWithoutSidecars()
    {
        var path = Path.Combine(_directory, "delete.db");
        using (var connection = OpenManaged(path, "Journal Mode=Delete"))
        {
            Scalar(connection, "PRAGMA journal_mode;").Should().Be("delete");
            Execute(connection, "CREATE TABLE t(v); INSERT INTO t VALUES (1);");
            File.Exists(path + "-wal").Should().BeFalse();
        }

        File.Exists(path + "-wal").Should().BeFalse();
        File.Exists(path + "-shm").Should().BeFalse();
        using var native = OpenNative(path);
        Scalar(native, "PRAGMA journal_mode;").Should().Be("delete");
    }

    // #14: switching an existing database to DELETE leaves no -shm behind on close.
    [Test]
    public void SwitchingToDeleteLeavesNoSharedMemoryFile()
    {
        var path = Path.Combine(_directory, "switch.db");
        using (var connection = OpenManaged(path))
        {
            Scalar(connection, "PRAGMA journal_mode = DELETE;").Should().Be("delete");
            Execute(connection, "CREATE TABLE t(v);");
        }

        File.Exists(path + "-wal").Should().BeFalse();
        File.Exists(path + "-shm").Should().BeFalse();
    }

    // #15: closing the last connection checkpoints and removes the WAL and its index.
    [Test]
    public void ClosingTheLastConnectionRemovesTheWalSidecars()
    {
        var path = Path.Combine(_directory, "wal.db");
        using (var first = OpenManaged(path))
        using (var second = OpenManaged(path))
        {
            Execute(first, "CREATE TABLE t(v); INSERT INTO t VALUES (1);");
            Scalar(second, "SELECT count(*) FROM t;").Should().Be(1L);
            first.Close();
            File.Exists(path + "-wal").Should().BeTrue("another connection is still open");
        }

        File.Exists(path + "-wal").Should().BeFalse();
        File.Exists(path + "-shm").Should().BeFalse();
        using var native = OpenNative(path);
        Scalar(native, "SELECT count(*) FROM t;").Should().Be(1L);
    }

    // #15: pooled connections release the sidecars when the pool is cleared.
    [Test]
    public void ClearingThePoolRemovesTheWalSidecars()
    {
        var path = Path.Combine(_directory, "pooled.db");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            Execute(connection, "CREATE TABLE t(v); INSERT INTO t VALUES (1);");
        }

        SqliteConnection.ClearAllPools();
        File.Exists(path + "-wal").Should().BeFalse();
        File.Exists(path + "-shm").Should().BeFalse();
    }

    // #16: an existing zero-length file is a new, empty database.
    [Test]
    public void AnEmptyFileOpensAsANewDatabase()
    {
        var path = Path.Combine(_directory, "empty.db");
        File.WriteAllBytes(path, []);
        using (var connection = OpenManaged(path))
            Execute(connection, "CREATE TABLE t(v); INSERT INTO t VALUES (1);");

        using var native = OpenNative(path);
        Scalar(native, "SELECT count(*) FROM t;").Should().Be(1L);
    }

    // #17: ALTER TABLE ADD COLUMN edits the stored CREATE TABLE text exactly as SQLite does.
    [TestCase("CREATE TABLE t (\n    ID guid NOT NULL, \n    DATA TEXT,\n    PRIMARY KEY (ID)\n)")]
    [TestCase("CREATE TABLE t (\n    ID guid NOT NULL,\n    DATA TEXT\n)")]
    [TestCase("CREATE TABLE t(ID INTEGER, DATA TEXT, PRIMARY KEY (ID))")]
    [TestCase("CREATE TABLE t(ID INTEGER, DATA TEXT, CHECK (ID > 0), UNIQUE (DATA))")]
    public void AddColumnRewritesTheSchemaTextLikeSqlite(string create)
    {
        const string alter = "ALTER TABLE t ADD COLUMN Username varchar";
        using var native = OpenNative(Path.Combine(_directory, "native.db"));
        Execute(native, create);
        Execute(native, alter);
        using var managed = OpenManaged(Path.Combine(_directory, "managed.db"));
        Execute(managed, create);
        Execute(managed, alter);

        Scalar(managed, "SELECT sql FROM sqlite_master WHERE name = 't';")
            .Should().Be(Scalar(native, "SELECT sql FROM sqlite_master WHERE name = 't';"));
    }

    // #19: BinaryGUID=False binds GUIDs as uppercase TEXT, like Microsoft.Data.Sqlite.
    [Test]
    public void BinaryGuidFalseBindsGuidsAsUppercaseText()
    {
        var guid = Guid.NewGuid();
        using var text = OpenManaged(Path.Combine(_directory, "text.db"), "BinaryGUID=False");
        Execute(text, "CREATE TABLE t(ID guid COLLATE NOCASE);");
        using (var insert = text.CreateCommand())
        {
            insert.CommandText = "INSERT INTO t VALUES ($id);";
            insert.Parameters.AddWithValue("$id", guid);
            insert.ExecuteNonQuery();
        }

        Scalar(text, "SELECT typeof(ID) || ':' || ID FROM t;")
            .Should().Be("text:" + guid.ToString("D").ToUpperInvariant());
        using (var lookup = text.CreateCommand())
        {
            lookup.CommandText = "SELECT ID FROM t WHERE ID = $id;";
            lookup.Parameters.AddWithValue("$id", guid);
            lookup.ExecuteScalar().Should().Be(guid);
        }

        using var binary = OpenManaged(Path.Combine(_directory, "binary.db"));
        Execute(binary, "CREATE TABLE t(ID guid);");
        using var binaryInsert = binary.CreateCommand();
        binaryInsert.CommandText = "INSERT INTO t VALUES ($id);";
        binaryInsert.Parameters.AddWithValue("$id", guid);
        binaryInsert.ExecuteNonQuery();
        Scalar(binary, "SELECT typeof(ID) FROM t;").Should().Be("blob");
    }

    // #20: DbType.Guid accepts a GUID string.
    [TestCase(true, "blob")]
    [TestCase(false, "text")]
    public void DbTypeGuidAcceptsAGuidString(bool binaryGuid, string storageClass)
    {
        var guid = Guid.NewGuid();
        using var connection = OpenManaged(Path.Combine(_directory, "dbtype.db"), $"BinaryGUID={binaryGuid}");
        Execute(connection, "CREATE TABLE t(ID guid);");
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO t VALUES ($id);";
        var parameter = insert.CreateParameter();
        parameter.ParameterName = "$id";
        parameter.DbType = DbType.Guid;
        parameter.Value = guid.ToString();
        insert.Parameters.Add(parameter);
        insert.ExecuteNonQuery();

        Scalar(connection, "SELECT typeof(ID) FROM t;").Should().Be(storageClass);
        Scalar(connection, "SELECT ID FROM t;").Should().Be(guid);
    }

    // #21: Type Mapping=SystemDataSQLite maps declared types like System.Data.SQLite.
    [Test]
    public void SystemDataSqliteTypeMappingFollowsDeclaredTypes()
    {
        using var connection = OpenManaged(Path.Combine(_directory, "types.db"), "Type Mapping=SystemDataSQLite");
        Execute(connection, """
            CREATE TABLE t(I INT, S SMALLINT, T TINYINT, B BIT, D DATETIME, M DECIMAL(10,2), R REAL, G guid, X VARCHAR(10), Y BLOB, L INTEGER);
            INSERT INTO t VALUES (5, 6, 7, 1, '2024-03-02 12:34:56', 1.5, 2.5, NULL, 'x', x'0102', 9);
            """);
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT I, S, T, B, D, M, R, X, Y, L FROM t;";
        using var reader = select.ExecuteReader();
        var expected = new[]
        {
            typeof(int), typeof(short), typeof(byte), typeof(bool), typeof(DateTime), typeof(decimal),
            typeof(double), typeof(string), typeof(byte[]), typeof(long),
        };
        for (var i = 0; i < expected.Length; i++)
            reader.GetFieldType(i).Should().Be(expected[i]);

        reader.Read().Should().BeTrue();
        reader.GetValue(0).Should().Be(5);
        reader.GetValue(1).Should().Be((short)6);
        reader.GetValue(2).Should().Be((byte)7);
        reader.GetValue(3).Should().Be(true);
        reader.GetValue(4).Should().Be(new DateTime(2024, 3, 2, 12, 34, 56));
        reader.GetValue(5).Should().Be(1.5m);
        reader.GetValue(9).Should().Be(9L);
        reader.GetSchemaTable().Rows[0][System.Data.Common.SchemaTableColumn.DataType].Should().Be(typeof(int));
        reader.Close();

        Scalar(connection, "SELECT I FROM t;").Should().Be(5);
        var table = new DataTable();
        using var adapter = connection.CreateCommand();
        adapter.CommandText = "SELECT B, D FROM t;";
        using (var adapterReader = adapter.ExecuteReader())
            table.Load(adapterReader);
        table.Columns["B"]!.DataType.Should().Be(typeof(bool));
        table.Columns["D"]!.DataType.Should().Be(typeof(DateTime));
    }

    // #21: the *ID column-name GUID heuristic is opt-in.
    [Test]
    public void GuidColumnNameHeuristicIsOptIn()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        using var plain = OpenManaged(Path.Combine(_directory, "plain.db"));
        Execute(plain, "CREATE TABLE t(ParentID BLOB);");
        Insert(plain, bytes);
        Scalar(plain, "SELECT ParentID FROM t;").Should().BeOfType<byte[]>();

        using var heuristic = OpenManaged(Path.Combine(_directory, "heuristic.db"), "Guid Column Name Heuristic=True");
        Execute(heuristic, "CREATE TABLE t(ParentID BLOB);");
        Insert(heuristic, bytes);
        Scalar(heuristic, "SELECT ParentID FROM t;").Should().Be(new Guid(bytes).ToString("D").ToUpperInvariant());

        static void Insert(SqliteConnection connection, byte[] value)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO t VALUES ($v);";
            insert.Parameters.AddWithValue("$v", value);
            insert.ExecuteNonQuery();
        }
    }

    // #22: GetValue on a guid column returns what does not parse as a GUID instead of throwing.
    [Test]
    public void GuidColumnsReturnNonGuidContentAsStored()
    {
        using var connection = OpenManaged(Path.Combine(_directory, "guid.db"));
        Execute(connection, "CREATE TABLE t(ID guid); INSERT INTO t VALUES ('not-a-guid'), (x'0102');");
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT ID FROM t ORDER BY rowid;";
        using var reader = select.ExecuteReader();
        reader.Read().Should().BeTrue();
        reader.GetValue(0).Should().Be("not-a-guid");
        reader.Read().Should().BeTrue();
        reader.GetValue(0).Should().BeEquivalentTo(new byte[] { 1, 2 });
    }

    // #23: BLOB-declared columns report object by default, so DataAdapter keeps text that
    // SQLite's dynamic typing stored in them; System.Data.SQLite type mapping reports byte[].
    [Test]
    public void BlobColumnFieldTypeDependsOnTheTypeMapping()
    {
        using var connection = OpenManaged(Path.Combine(_directory, "blob.db"));
        Execute(connection, "CREATE TABLE t(B BLOB); INSERT INTO t VALUES (x'0102');");
        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT B FROM t;";
            using var reader = select.ExecuteReader();
            reader.GetFieldType(0).Should().Be(typeof(object));
        }

        using var mapped = OpenManaged(Path.Combine(_directory, "blob.db"), "Type Mapping=SystemDataSQLite");
        using var mappedSelect = mapped.CreateCommand();
        mappedSelect.CommandText = "SELECT B FROM t;";
        using var mappedReader = mappedSelect.ExecuteReader();
        mappedReader.GetFieldType(0).Should().Be(typeof(byte[]));
    }

    // #24: Auto Enlist Transaction=True runs commands without a transaction inside the
    // connection's pending one, like System.Data.SQLite.
    [Test]
    public void AutoEnlistTransactionRunsCommandsInThePendingTransaction()
    {
        var path = Path.Combine(_directory, "enlist.db");
        using (var strict = OpenManaged(path))
        {
            Execute(strict, "CREATE TABLE t(v);");
            // A command created before the transaction has no transaction of its own.
            using var orphan = new SqliteCommand("INSERT INTO t VALUES (1);", strict);
            using var transaction = strict.BeginTransaction();
            Assert.Throws<InvalidOperationException>(() => orphan.ExecuteNonQuery());
        }

        using var enlisting = OpenManaged(path, "Auto Enlist Transaction=True");
        using (var orphan = new SqliteCommand("INSERT INTO t VALUES (1);", enlisting))
        using (var transaction = enlisting.BeginTransaction())
        {
            orphan.ExecuteNonQuery();
            transaction.Rollback();
        }

        Scalar(enlisting, "SELECT count(*) FROM t;").Should().Be(0L);
    }

    // #25: System.Data.SQLite keywords are accepted and applied; unknown keywords can be ignored.
    [Test]
    public void SystemDataSqliteKeywordsAreTranslated()
    {
        var path = Path.Combine(_directory, "keywords.db");
        using (var connection = OpenManaged(path, "Journal Mode=Delete;Synchronous=Normal;Cache Size=2000;Page Size=8192;Version=3;Legacy Format=False;FailIfMissing=False"))
        {
            Scalar(connection, "PRAGMA journal_mode;").Should().Be("delete");
            Scalar(connection, "PRAGMA synchronous;").Should().Be(1L);
            Execute(connection, "CREATE TABLE t(v);");
        }

        using (var readOnly = OpenManaged(path, "Read Only=True"))
        {
            Assert.Throws<SqliteException>(() => Execute(readOnly, "INSERT INTO t VALUES (1);"))!
                .SqliteErrorCode.Should().Be(8);
        }

        var missing = Path.Combine(_directory, "missing.db");
        Assert.Throws<SqliteException>(() => OpenManaged(missing, "FailIfMissing=True").Dispose())!
            .SqliteErrorCode.Should().Be(14);

        Assert.Throws<ArgumentException>(() => _ = new SqliteConnectionStringBuilder("Data Source=x;Max Pool Size=5"));
        var builder = new SqliteConnectionStringBuilder("Data Source=x;Max Pool Size=5;Enlist=True;Ignore Unknown Keywords=True");
        builder.IgnoredKeywords.Should().HaveCount(2);
        builder.IgnoredKeywords.ContainsKey("Max Pool Size").Should().BeTrue();
        builder.IgnoredKeywords.ContainsKey("Enlist").Should().BeTrue();
        using var lenient = OpenManaged(path, "Max Pool Size=5;Ignore Unknown Keywords=True");
        Scalar(lenient, "SELECT count(*) FROM t;").Should().Be(0L);
    }

    // #26: a wrong key reports SQLITE_NOTADB with SQLite's classic text.
    [Test]
    public void WrongKeyReportsTheClassicNotADatabaseError()
    {
        var path = Path.Combine(_directory, "encrypted.db");
        var key = new string('a', 64);
        using (var connection = OpenManaged(path, $"Encryption Cipher=aes256gcm;Encryption Key={key}"))
            Execute(connection, "CREATE TABLE t(v);");

        var failure = Assert.Throws<SqliteException>(
            () => OpenManaged(path, $"Encryption Cipher=aes256gcm;Encryption Key={new string('b', 64)}").Dispose())!;
        failure.SqliteErrorCode.Should().Be(26);
        failure.Message.Should().Contain("file is encrypted or is not a database");
    }

    // #35: the builder reports the provider a connection actually uses.
    [Test]
    public void LocalProviderDefaultsToManaged()
    {
        new SqliteConnectionStringBuilder("Data Source=x").LocalProvider.Should().Be(global::Ahtola.AhtolaLocalProvider.Managed);
    }

    private string CreateNativeDatabase(bool wal, string sql)
    {
        var path = Path.Combine(_directory, (wal ? "wal" : "rollback") + ".db");
        using var native = OpenNative(path);
        Execute(native, (wal ? "PRAGMA journal_mode=WAL;" : string.Empty) + sql);
        return path;
    }

    private static SqliteConnection OpenManaged(string path, string extra = "")
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False;{extra}");
        connection.Open();
        return connection;
    }

    private static NativeSqliteConnection OpenNative(string path, string extra = "")
    {
        var connection = new NativeSqliteConnection($"Data Source={path};Pooling=False;{extra}");
        connection.Open();
        return connection;
    }

    private static object? Scalar(System.Data.Common.DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(System.Data.Common.DbConnection connection, string sql, System.Data.Common.DbTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }
}
