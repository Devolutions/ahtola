using AwesomeAssertions;
using SQLitePCL;
using Ahtola.Data.Sqlite;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// Two-connection coverage for <c>BEGIN DEFERRED</c>, <c>BEGIN IMMEDIATE</c> and
/// <c>BEGIN EXCLUSIVE</c>. The point of these tests is the *timing* of the busy
/// error: DEFERRED must stay lazy and fail at the first write, while IMMEDIATE
/// and EXCLUSIVE must take the write lock eagerly and fail at BEGIN itself.
/// </summary>
public class ManagedTransactionModeLockingTests
{
    private const int SqliteBusy = 5;

    [Test]
    public void DeferredTransactionReportsBusyAtFirstWriteNotAtBegin()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        a.ExecuteNonQuery("BEGIN DEFERRED;");
        a.ExecuteNonQuery("INSERT INTO t VALUES (1);");

        // DEFERRED takes no lock at BEGIN, so B gets in.
        var beginError = Capture(() => b.ExecuteNonQuery("BEGIN DEFERRED;"));
        beginError.Should().BeNull();

        // The conflict only shows up when B actually tries to write.
        var writeError = Capture(() => b.ExecuteNonQuery("INSERT INTO t VALUES (2);"));
        writeError.Should().NotBeNull();
        writeError!.SqliteErrorCode.Should().Be(SqliteBusy);
        writeError.Message.Should().Contain("database is locked");
    }

    [Test]
    public void ImmediateTransactionReportsBusyAtBeginNotAtFirstWrite()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        a.ExecuteNonQuery("BEGIN IMMEDIATE;");

        // IMMEDIATE takes the write lock eagerly, so BEGIN itself is where the
        // caller learns it lost the race - before doing any work.
        var beginError = Capture(() => b.ExecuteNonQuery("BEGIN IMMEDIATE;"));
        beginError.Should().NotBeNull();
        beginError!.SqliteErrorCode.Should().Be(SqliteBusy);
        beginError.Message.Should().Contain("database is locked");

        // The failed BEGIN left B in autocommit rather than in a half-open
        // transaction, and A is unaffected.
        a.ExecuteNonQuery("INSERT INTO t VALUES (1);");
        a.ExecuteNonQuery("COMMIT;");
    }

    [Test]
    public void ImmediateTransactionIsBlockedByAnotherConnectionsDeferredWrite()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        a.ExecuteNonQuery("BEGIN;");
        a.ExecuteNonQuery("INSERT INTO t VALUES (1);");

        // A's DEFERRED transaction escalated to a write lock at its first write,
        // so B's eager acquisition has to fail.
        var error = Capture(() => b.ExecuteNonQuery("BEGIN IMMEDIATE;"));
        error.Should().NotBeNull();
        error!.SqliteErrorCode.Should().Be(SqliteBusy);
    }

    [Test]
    public void ExclusiveTransactionBlocksWritersButNotReadersUnderWal()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        a.ExecuteScalar<string>("PRAGMA journal_mode;").Should().Be("wal");
        a.ExecuteNonQuery("BEGIN EXCLUSIVE;");

        // SQLite's EXCLUSIVE does not exclude readers in WAL mode; it behaves
        // like IMMEDIATE there. Verified against Microsoft.Data.Sqlite below.
        Capture(() => b.ExecuteScalar<long>("SELECT COUNT(*) FROM t;")).Should().BeNull();

        var writerError = Capture(() => b.ExecuteNonQuery("BEGIN IMMEDIATE;"));
        writerError.Should().NotBeNull();
        writerError!.SqliteErrorCode.Should().Be(SqliteBusy);
    }

    [Test]
    public void ExclusiveTransactionBlocksReadersUnderRollbackJournal()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        a.ExecuteNonQuery("PRAGMA journal_mode=delete;");
        a.ExecuteScalar<string>("PRAGMA journal_mode;").Should().Be("delete");

        a.ExecuteNonQuery("BEGIN EXCLUSIVE;");

        // Under a rollback journal an EXCLUSIVE lock does exclude readers.
        var readError = Capture(() => b.ExecuteScalar<long>("SELECT COUNT(*) FROM t;"));
        readError.Should().NotBeNull();
        readError!.SqliteErrorCode.Should().Be(SqliteBusy);

        a.ExecuteNonQuery("ROLLBACK;");
        b.ExecuteScalar<long>("SELECT COUNT(*) FROM t;").Should().Be(0);
    }

    [Test]
    public void ImmediateTransactionDoesNotBlockAnotherConnectionsRead()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        a.ExecuteNonQuery("BEGIN IMMEDIATE;");
        a.ExecuteNonQuery("INSERT INTO t VALUES (1);");

        // B still sees the pre-transaction snapshot, and is not refused.
        b.ExecuteScalar<long>("SELECT COUNT(*) FROM t;").Should().Be(0);
    }

    [Test]
    public void AutocommitWriteIsBusyWhileAnotherConnectionHoldsWriteTransaction()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        a.ExecuteNonQuery("BEGIN IMMEDIATE;");

        var writeError = Capture(() => b.ExecuteNonQuery("INSERT INTO t VALUES (2);"));
        writeError.Should().NotBeNull();
        writeError!.SqliteErrorCode.Should().Be(SqliteBusy);

        // Autocommit reads stay allowed, as in SQLite's WAL mode.
        Capture(() => b.ExecuteScalar<long>("SELECT COUNT(*) FROM t;")).Should().BeNull();
    }

    [Test]
    public void AutocommitWriteIsBusyWhileADeferredTransactionHasAlreadyWritten()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        // Before the first write a DEFERRED transaction holds nothing, so an
        // outside autocommit write still gets through.
        a.ExecuteNonQuery("BEGIN;");
        Capture(() => b.ExecuteNonQuery("INSERT INTO t VALUES (1);")).Should().BeNull();

        // Once it escalates, it locks out other connections' autocommit writes.
        a.ExecuteNonQuery("INSERT INTO t VALUES (2);");
        var blocked = Capture(() => b.ExecuteNonQuery("INSERT INTO t VALUES (3);"));
        blocked.Should().NotBeNull();
        blocked!.SqliteErrorCode.Should().Be(SqliteBusy);
    }

    // A busy failure must not be mistaken for a rolled-back transaction. These pin
    // the interaction with the rollback hook, whose firing points are: explicit
    // ROLLBACK, a commit-hook veto, ON CONFLICT ROLLBACK, and a failed autocommit
    // mutation - but never a failed statement inside a transaction.

    [Test]
    public void BusyBeginDoesNotFireTheRollbackHook()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        a.ExecuteNonQuery("BEGIN IMMEDIATE;");

        var rollbacks = 0;
        b.SetRollbackHook(() => rollbacks++);

        Capture(() => b.ExecuteNonQuery("BEGIN IMMEDIATE;")).Should().NotBeNull();

        // The losing BEGIN never opened a transaction, so there is nothing to roll back.
        rollbacks.Should().Be(0);
    }

    [Test]
    public void BusyAutocommitWriteDoesNotFireTheRollbackHook()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        a.ExecuteNonQuery("BEGIN IMMEDIATE;");

        var rollbacks = 0;
        b.SetRollbackHook(() => rollbacks++);

        Capture(() => b.ExecuteNonQuery("INSERT INTO t VALUES (1);")).Should().NotBeNull();

        // Busy is refused before the implicit transaction is opened, so unlike a
        // failed autocommit mutation there is no implicit rollback to report.
        rollbacks.Should().Be(0);
    }

    [Test]
    public void BusyWriteInsideATransactionDoesNotFireTheRollbackHookUntilRollback()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        a.ExecuteNonQuery("BEGIN IMMEDIATE;");

        var rollbacks = 0;
        b.SetRollbackHook(() => rollbacks++);

        b.ExecuteNonQuery("BEGIN;");
        Capture(() => b.ExecuteNonQuery("INSERT INTO t VALUES (1);")).Should().NotBeNull();

        // A failed statement inside a transaction leaves it open and reports nothing.
        rollbacks.Should().Be(0);

        b.ExecuteNonQuery("ROLLBACK;");
        rollbacks.Should().Be(1);
    }

    [Test]
    public void NativeSqliteAlsoLeavesTheRollbackHookSilentOnABusyAutocommitWrite()
    {
        using var db = new NativeFileDatabase();
        var a = db.Connect();
        var b = db.Connect();

        NativeExec(a, "PRAGMA journal_mode=wal;");
        NativeExec(a, "BEGIN IMMEDIATE;");

        var rollbacks = 0;
        raw.sqlite3_rollback_hook(b.Handle!, _ => rollbacks++, null);

        NativeError(() => NativeExecFast(b, "INSERT INTO t VALUES (1);"))!
            .SqliteErrorCode.Should().Be(SqliteBusy);

        rollbacks.Should().Be(0);
        raw.sqlite3_rollback_hook(b.Handle!, (delegate_rollback?)null, null);
    }

    [Test]
    public void CreateTableAsSelectIsBusyWhileAnotherConnectionHoldsWriteTransaction()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        a.ExecuteNonQuery("BEGIN IMMEDIATE;");

        var error = Capture(() => b.ExecuteNonQuery("CREATE TABLE copy AS SELECT * FROM t;"));
        error.Should().NotBeNull();
        error!.SqliteErrorCode.Should().Be(SqliteBusy);
    }

    [Test]
    public void VacuumIsBusyWhileAnotherConnectionHoldsWriteTransaction()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        a.ExecuteNonQuery("BEGIN IMMEDIATE;");

        var error = Capture(() => b.ExecuteNonQuery("VACUUM;"));
        error.Should().NotBeNull();
        error!.SqliteErrorCode.Should().Be(SqliteBusy);
    }

    [Test]
    public void NativeSqliteAlsoRefusesAutocommitWriteAgainstAnOpenWriteTransaction()
    {
        using var db = new NativeFileDatabase();
        var a = db.Connect();
        var b = db.Connect();

        NativeExec(a, "PRAGMA journal_mode=wal;");
        NativeExec(a, "BEGIN IMMEDIATE;");

        var writeError = NativeError(() => NativeExecFast(b, "INSERT INTO t VALUES (2);"));
        writeError.Should().NotBeNull();
        writeError!.SqliteErrorCode.Should().Be(SqliteBusy);

        NativeError(() => NativeExec(b, "SELECT COUNT(*) FROM t;")).Should().BeNull();
    }

    [Test]
    public void CommitReleasesTheEagerWriteLock()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        a.ExecuteNonQuery("BEGIN IMMEDIATE;");
        Capture(() => b.ExecuteNonQuery("BEGIN IMMEDIATE;")).Should().NotBeNull();
        a.ExecuteNonQuery("COMMIT;");

        // The reservation is gone once the transaction ends, so B may take it.
        Capture(() => b.ExecuteNonQuery("BEGIN IMMEDIATE;")).Should().BeNull();
        b.ExecuteNonQuery("COMMIT;");
    }

    [Test]
    public void CommittingWriterReleasesTheLockForALaterConnection()
    {
        using var db = new ManagedFileDatabase();

        using (var a = db.Connect())
        {
            a.ExecuteNonQuery("BEGIN IMMEDIATE;");
            a.ExecuteNonQuery("INSERT INTO t VALUES (1);");
            a.ExecuteNonQuery("COMMIT;");
        }

        using var c = db.Connect();
        c.ExecuteNonQuery("BEGIN IMMEDIATE;");
        c.ExecuteNonQuery("INSERT INTO t VALUES (2);");
        c.ExecuteNonQuery("COMMIT;");
        c.ExecuteScalar<long>("SELECT COUNT(*) FROM t;").Should().Be(2);
    }

    [Test]
    public void RollbackReleasesTheEagerWriteLock()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        a.ExecuteNonQuery("BEGIN EXCLUSIVE;");
        a.ExecuteNonQuery("INSERT INTO t VALUES (1);");
        a.ExecuteNonQuery("ROLLBACK;");

        b.ExecuteNonQuery("BEGIN IMMEDIATE;");
        b.ExecuteNonQuery("INSERT INTO t VALUES (2);");
        b.ExecuteNonQuery("COMMIT;");

        b.ExecuteScalar<long>("SELECT COUNT(*) FROM t;").Should().Be(1);
    }

    [Test]
    public void ClosingAConnectionReleasesTheEagerWriteLock()
    {
        using var db = new ManagedFileDatabase();
        using var b = db.Connect();

        using (var a = db.Connect())
        {
            a.ExecuteNonQuery("BEGIN IMMEDIATE;");
            Capture(() => b.ExecuteNonQuery("BEGIN IMMEDIATE;")).Should().NotBeNull();
        }

        b.ExecuteNonQuery("BEGIN IMMEDIATE;");
        b.ExecuteNonQuery("COMMIT;");
    }

    [Test]
    public void ReenteringImmediateOnTheSameConnectionIsNotSelfBlocking()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();

        a.ExecuteNonQuery("BEGIN IMMEDIATE;");
        a.ExecuteNonQuery("SAVEPOINT s1;");
        a.ExecuteNonQuery("INSERT INTO t VALUES (1);");
        a.ExecuteNonQuery("RELEASE s1;");
        a.ExecuteNonQuery("COMMIT;");

        a.ExecuteScalar<long>("SELECT COUNT(*) FROM t;").Should().Be(1);
    }

    [Test]
    public void SerializableTransactionScopeTakesTheEagerWriteLock()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        // SqliteTransaction emits BEGIN IMMEDIATE for non-deferred Serializable,
        // which previously degraded to DEFERRED inside the engine.
        using var transaction = a.BeginTransaction();

        var error = Capture(() => b.ExecuteNonQuery("BEGIN IMMEDIATE;"));
        error.Should().NotBeNull();
        error!.SqliteErrorCode.Should().Be(SqliteBusy);

        transaction.Rollback();
        b.ExecuteNonQuery("BEGIN IMMEDIATE;");
        b.ExecuteNonQuery("COMMIT;");
    }

    [Test]
    public void BeginAcceptsTransactionKeywordWithEveryMode()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();

        foreach (var sql in new[]
                 {
                     "BEGIN TRANSACTION;",
                     "BEGIN DEFERRED TRANSACTION;",
                     "BEGIN IMMEDIATE TRANSACTION;",
                     "BEGIN EXCLUSIVE TRANSACTION;",
                 })
        {
            a.ExecuteNonQuery(sql);
            a.ExecuteNonQuery("COMMIT;");
        }
    }

    [Test]
    public void ConcurrentTransactionReportsMvccRequirementWithoutOpeningATransaction()
    {
        using var db = new ManagedFileDatabase();
        using var connection = db.Connect();

        var error = Capture(() => connection.ExecuteNonQuery("BEGIN CONCURRENT;"));

        error.Should().NotBeNull();
        error!.Message.Should().Contain("Concurrent transaction mode is only supported when MVCC is enabled");

        connection.ExecuteNonQuery("BEGIN;");
        connection.ExecuteNonQuery("ROLLBACK;");
    }

    [Test]
    public void ConcurrentTransactionSucceedsAfterMvccIsEnabled()
    {
        using var db = new ManagedFileDatabase();
        using var connection = db.Connect();

        connection.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        connection.ExecuteNonQuery("BEGIN CONCURRENT;");
        connection.ExecuteNonQuery("INSERT INTO t VALUES (1);");
        connection.ExecuteNonQuery("COMMIT;");

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT v FROM t;";
        Convert.ToInt64(command.ExecuteScalar()).Should().Be(1L);
    }

    [Test]
    public void NestedBeginInsideConcurrentErrors()
    {
        using var db = new ManagedFileDatabase();
        using var connection = db.Connect();

        connection.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        connection.ExecuteNonQuery("BEGIN CONCURRENT;");
        var error = Capture(() => connection.ExecuteNonQuery("BEGIN IMMEDIATE;"));
        error.Should().NotBeNull();
        error!.Message.Should().Contain("cannot start a transaction within a transaction");
        connection.ExecuteNonQuery("ROLLBACK;");
    }

    [Test]
    public void ConcurrentWritersCanCommitDisjointInserts()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        a.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        // Ensure peer sees durable MVCC mode (shared MvStore registry + header 255).
        ReadValue(b, "PRAGMA journal_mode;").Should().Be("mvcc");

        a.ExecuteNonQuery("BEGIN CONCURRENT;");
        b.ExecuteNonQuery("BEGIN CONCURRENT;");
        a.ExecuteNonQuery("INSERT INTO t VALUES (10);");
        b.ExecuteNonQuery("INSERT INTO t VALUES (20);");
        a.ExecuteNonQuery("COMMIT;");
        b.ExecuteNonQuery("COMMIT;");

        using var command = a.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM t WHERE v IN (10, 20);";
        Convert.ToInt64(command.ExecuteScalar()).Should().Be(2L);
    }

    [Test]
    public void ConcurrentMultiRowInsertAndTriggerInsertsAreVisibleAfterCommit()
    {
        using var db = new ManagedFileDatabase();
        using var writer = db.Connect();
        using var reader = db.Connect();

        writer.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        writer.ExecuteNonQuery("CREATE TABLE audit(v INTEGER);");
        writer.ExecuteNonQuery(
            """
                    CREATE TRIGGER t_ai AFTER INSERT ON t
                    BEGIN
                      INSERT INTO audit VALUES (new.v);
                    END;
                    """);

        writer.ExecuteNonQuery("BEGIN CONCURRENT;");
        writer.ExecuteNonQuery("INSERT INTO t VALUES (1), (2), (3);");
        writer.ExecuteNonQuery("COMMIT;");

        Convert.ToInt64(Scalar(reader, "SELECT COUNT(*) FROM t;")).Should().Be(3L);
        Convert.ToInt64(Scalar(reader, "SELECT COUNT(*) FROM audit;")).Should().Be(3L);
        Convert.ToInt64(Scalar(reader, "SELECT SUM(v) FROM t;")).Should().Be(6L);
        Convert.ToInt64(Scalar(reader, "SELECT SUM(v) FROM audit;")).Should().Be(6L);
    }

    [Test]
    public void ConcurrentNamedSavepointRollbackUndoesVersionStoreInserts()
    {
        using var db = new ManagedFileDatabase();
        using var connection = db.Connect();

        connection.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        connection.ExecuteNonQuery("BEGIN CONCURRENT;");
        connection.ExecuteNonQuery("INSERT INTO t VALUES (1);");
        connection.ExecuteNonQuery("SAVEPOINT sp1;");
        connection.ExecuteNonQuery("INSERT INTO t VALUES (2);");
        connection.ExecuteNonQuery("INSERT INTO t VALUES (3);");
        connection.ExecuteNonQuery("ROLLBACK TO sp1;");
        connection.ExecuteNonQuery("INSERT INTO t VALUES (4);");
        connection.ExecuteNonQuery("RELEASE sp1;");
        connection.ExecuteNonQuery("COMMIT;");

        Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM t;")).Should().Be(2L);
        Convert.ToInt64(Scalar(connection, "SELECT SUM(v) FROM t;")).Should().Be(5L);
    }

    [Test]
    public void ConcurrentModeRejectsAttachedDatabaseMutations()
    {
        using var db = new ManagedFileDatabase();
        using var connection = db.Connect();
        var auxPath = Path.Combine(Path.GetTempPath(), $"mvcc-attach-{Guid.NewGuid():N}.db");
        try
        {
            connection.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
            connection.ExecuteNonQuery($"ATTACH DATABASE '{auxPath}' AS aux;");
            connection.ExecuteNonQuery("CREATE TABLE aux.items(v INTEGER);");
            connection.ExecuteNonQuery("BEGIN CONCURRENT;");
            connection.ExecuteNonQuery("INSERT INTO t VALUES (1);");
            var attachedWrite = Capture(() => connection.ExecuteNonQuery("INSERT INTO aux.items VALUES (2);"));
            attachedWrite.Should().NotBeNull();
            attachedWrite!.Message.Should().Contain("only supports mutations on the main database");
            connection.ExecuteNonQuery("COMMIT;");
            Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM t;")).Should().Be(1L);
            Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM aux.items;")).Should().Be(0L);
        }
        finally
        {
            try { File.Delete(auxPath); } catch { /* best effort */ }
            try { File.Delete(auxPath + "-wal"); } catch { /* best effort */ }
            try { File.Delete(auxPath + "-shm"); } catch { /* best effort */ }
        }
    }

    [Test]
    public void ReindexIsRejectedWhileMvccIsEnabled()
    {
        using var db = new ManagedFileDatabase();
        using var connection = db.Connect();
        connection.ExecuteNonQuery("PRAGMA journal_mode=mvcc;");
        connection.ExecuteNonQuery("CREATE INDEX t_v ON t(v);");
        var error = Capture(() => connection.ExecuteNonQuery("REINDEX;"));
        error.Should().NotBeNull();
        error!.Message.Should().Contain("REINDEX is not supported in MVCC mode");
    }

    private static string ReadValue(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [Test]
    public void RepeatedTransactionModeKeywordIsRejected()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var native = new NativeFileDatabase();

        // SQLite allows at most one mode keyword.
        NativeError(() => NativeExec(native.Connect(), "BEGIN DEFERRED IMMEDIATE;")).Should().NotBeNull();
        Capture(() => a.ExecuteNonQuery("BEGIN DEFERRED IMMEDIATE;")).Should().NotBeNull();
    }

    // The differential tests below pin the managed behavior to what native
    // SQLite actually does for the same statement sequence. They use their own
    // natively created file: a managed database file is owned exclusively by the
    // managed pager for its lifetime (the Stage 0 contract), so opening one with
    // Microsoft.Data.Sqlite at the same time is refused by design.

    [Test]
    public void NativeSqliteAlsoReportsDeferredBusyAtFirstWrite()
    {
        using var db = new NativeFileDatabase();
        var a = db.Connect();
        var b = db.Connect();

        NativeExec(a, "PRAGMA journal_mode=wal;");
        NativeExec(a, "BEGIN DEFERRED;");
        NativeExec(a, "INSERT INTO t VALUES (1);");

        NativeError(() => NativeExec(b, "BEGIN DEFERRED;")).Should().BeNull();

        var writeError = NativeError(() => NativeExecFast(b, "INSERT INTO t VALUES (2);"));
        writeError.Should().NotBeNull();
        writeError!.SqliteErrorCode.Should().Be(SqliteBusy);
    }

    [Test]
    public void NativeSqliteAlsoReportsImmediateBusyAtBegin()
    {
        using var db = new NativeFileDatabase();
        var a = db.Connect();
        var b = db.Connect();

        NativeExec(a, "PRAGMA journal_mode=wal;");
        NativeExec(a, "BEGIN IMMEDIATE;");

        var beginError = NativeError(() => NativeExecFast(b, "BEGIN IMMEDIATE;"));
        beginError.Should().NotBeNull();
        beginError!.SqliteErrorCode.Should().Be(SqliteBusy);
    }

    [Test]
    public void NativeSqliteExclusiveDoesNotBlockReadersUnderWal()
    {
        using var db = new NativeFileDatabase();
        var a = db.Connect();
        var b = db.Connect();

        NativeExec(a, "PRAGMA journal_mode=wal;");
        NativeExec(a, "BEGIN EXCLUSIVE;");

        NativeError(() => NativeExec(b, "SELECT COUNT(*) FROM t;")).Should().BeNull();
        NativeError(() => NativeExecFast(b, "BEGIN IMMEDIATE;"))!.SqliteErrorCode.Should().Be(SqliteBusy);
    }

    [Test]
    public void NativeSqliteExclusiveBlocksReadersUnderRollbackJournal()
    {
        using var db = new NativeFileDatabase();
        var a = db.Connect();
        var b = db.Connect();

        NativeExec(a, "PRAGMA journal_mode=delete;");
        NativeExec(a, "BEGIN EXCLUSIVE;");

        var readError = NativeError(() => NativeExecFast(b, "SELECT COUNT(*) FROM t;"));
        readError.Should().NotBeNull();
        readError!.SqliteErrorCode.Should().Be(SqliteBusy);
    }

    /// <summary>
    /// <c>CommandTimeout</c> is a busy timeout for the managed engine, exactly as
    /// <see cref="NativeSqliteTreatsCommandTimeoutAsABusyWait"/> measures for
    /// native SQLite: Microsoft.Data.Sqlite maps it onto
    /// <c>sqlite3_busy_timeout</c>, and the managed transaction lock now waits
    /// out the same timeout instead of failing fast. <c>PRAGMA busy_timeout</c>
    /// stays unsupported (see <see cref="ManagedDocumentedBoundaryTests"/>); the
    /// command-timeout mapping is the only way in.
    /// </summary>
    [Test]
    public void CommandTimeoutTurnsABusyBeginIntoABusyWait()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        a.ExecuteNonQuery("BEGIN IMMEDIATE;");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var error = Capture(() => ExecuteWithTimeout(b, "BEGIN EXCLUSIVE;", timeoutSeconds: 2));
        stopwatch.Stop();

        error.Should().NotBeNull();
        error!.SqliteErrorCode.Should().Be(SqliteBusy);

        // The load-bearing assertion: it burned the CommandTimeout waiting for the
        // lock before reporting busy, rather than failing fast.
        stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// The other half of the busy-wait contract: a contender that waits out the
    /// holder's commit acquires the lock and proceeds, which is what makes
    /// parallel test suites sharing one database file (EFCore's Inheritance
    /// fixtures) survive on the managed engine.
    /// </summary>
    [Test]
    public void BusyWaitAcquiresTheLockWhenTheHolderReleasesInTime()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        a.ExecuteNonQuery("BEGIN IMMEDIATE;");

        var releaser = Task.Run(() =>
        {
            Thread.Sleep(400);
            a.ExecuteNonQuery("COMMIT;");
        });

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var error = Capture(() => ExecuteWithTimeout(b, "BEGIN IMMEDIATE;", timeoutSeconds: 5));
        stopwatch.Stop();

        error.Should().BeNull();
        stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(300));

        b.ExecuteNonQuery("INSERT INTO t VALUES (1);");
        b.ExecuteNonQuery("COMMIT;");
        releaser.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Cancelling a batch after <c>BEGIN EXCLUSIVE</c> has already taken the
    /// reservation leaves the transaction open and the reservation held, which is
    /// what SQLite does when a statement inside a transaction fails. The
    /// reservation must then be released by the explicit ROLLBACK.
    /// </summary>
    [Test]
    public void CancellingAfterExclusiveTookTheLockKeepsItUntilRollback()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        using (var cts = new CancellationTokenSource())
        {
            // Cancel from inside the batch, after BEGIN EXCLUSIVE has already
            // taken the reservation. The engine checks cancellation between
            // statements, so this deterministically aborts before the INSERT.
            a.CreateFunction<long>("cancel_now", () => { cts.Cancel(); return 1L; });

            using var command = a.CreateCommand();
            command.CommandText = "BEGIN EXCLUSIVE; SELECT cancel_now(); INSERT INTO t VALUES (1);";
            Assert.Catch<OperationCanceledException>(
                () => command.ExecuteNonQueryAsync(cts.Token).GetAwaiter().GetResult());
        }

        // A's transaction is still open, so the reservation is still held.
        var blocked = Capture(() => b.ExecuteNonQuery("BEGIN IMMEDIATE;"));
        blocked.Should().NotBeNull();
        blocked!.SqliteErrorCode.Should().Be(SqliteBusy);

        // Rolling back releases it and B can proceed.
        a.ExecuteNonQuery("ROLLBACK;");
        Capture(() => b.ExecuteNonQuery("BEGIN IMMEDIATE;")).Should().BeNull();
    }

    /// <summary>
    /// A connection that opened the file before a sibling committed no longer
    /// has its snapshot invalidated at BEGIN: the stale catalog is re-read and
    /// the transaction proceeds on the latest committed state.
    /// </summary>
    [Test]
    public void BeginReloadsTheCatalogSnapshotWhenASiblingCommittedFirst()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        // B's in-memory snapshot now predates A's commit.
        a.ExecuteNonQuery("INSERT INTO t VALUES (1);");

        b.ExecuteNonQuery("BEGIN IMMEDIATE;");
        b.ExecuteNonQuery("INSERT INTO t VALUES (2);");
        b.ExecuteNonQuery("COMMIT;");

        using var reader = db.Connect();
        using var command = reader.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM t;";
        command.ExecuteScalar().Should().Be(2L);
    }

    /// <summary>
    /// The autocommit half of the stale-snapshot contract: the statement is
    /// re-executed against the reloaded catalog instead of persisting on the old
    /// version and silently losing the sibling's committed row.
    /// </summary>
    [Test]
    public void AutocommitWriteRetriesOnAReloadedSnapshotInsteadOfLosingTheSiblingCommit()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        a.ExecuteNonQuery("INSERT INTO t VALUES (1);");
        b.ExecuteNonQuery("INSERT INTO t VALUES (2);");

        using var reader = db.Connect();
        using var command = reader.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM t;";
        command.ExecuteScalar().Should().Be(2L);
    }

    /// <summary>
    /// A zero busy timeout governs lock-waiting only, not snapshot visibility. Once the
    /// sibling has committed (releasing the write lock), a fresh autocommit statement
    /// reads the latest committed view and succeeds - native SQLite behaves the same.
    /// Genuine lock contention without a budget still fails fast (the BEGIN IMMEDIATE
    /// cases above).
    /// </summary>
    [Test]
    public void AutocommitWriteAfterSiblingCommitSucceedsWithoutABusyTimeout()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect(defaultTimeoutSeconds: 0);

        a.ExecuteNonQuery("INSERT INTO t VALUES (1);");
        b.ExecuteNonQuery("INSERT INTO t VALUES (2);");

        using var reader = db.Connect();
        using var command = reader.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM t;";
        command.ExecuteScalar().Should().Be(2L);
    }

    /// <summary>
    /// A DEFERRED transaction that went stale before its first write cannot
    /// commit: like SQLite's <c>SQLITE_BUSY_SNAPSHOT</c> it must roll back. The
    /// error surfaces with the public busy message, not the internal catalog
    /// wording.
    /// </summary>
    [Test]
    public void DeferredTransactionOnAStaleSnapshotReportsBusyAtCommit()
    {
        using var db = new ManagedFileDatabase();
        using var a = db.Connect();
        using var b = db.Connect();

        // B's snapshot is taken at BEGIN; A then commits past it.
        b.ExecuteNonQuery("BEGIN DEFERRED;");
        a.ExecuteNonQuery("INSERT INTO t VALUES (1);");

        b.ExecuteNonQuery("INSERT INTO t VALUES (2);");
        var error = Capture(() => b.ExecuteNonQuery("COMMIT;"));
        error.Should().NotBeNull();
        error!.SqliteErrorCode.Should().Be(SqliteBusy);
        error.Message.Should().Contain("database is locked");

        // The must-rollback contract: the transaction is still open until an
        // explicit ROLLBACK, after which the connection is usable again.
        b.ExecuteNonQuery("ROLLBACK;");
        Capture(() => b.ExecuteNonQuery("INSERT INTO t VALUES (3);")).Should().BeNull();
    }

    /// <summary>
    /// Regression for the convoy starvation the stale-retry once caused: every
    /// contender burned its whole busy budget waiting for a completely free
    /// instant on the write lock, which never comes when the lock passes
    /// holder-to-holder. With wait-out-the-persist plus reload-at-BEGIN, each
    /// worker waits only for its own turn.
    /// </summary>
    [Test]
    public void ParallelImmediateWritersAllCommitAcrossStaleSnapshotReloads()
    {
        using var db = new ManagedFileDatabase();
        const int workers = 8;
        var connections = Enumerable.Range(0, workers).Select(_ => db.Connect()).ToArray();

        var tasks = connections.Select(connection => Task.Run(() =>
        {
            ExecuteWithTimeout(connection, "BEGIN IMMEDIATE;", timeoutSeconds: 10);
            // Hold the reservation briefly so the workers actually convoy.
            Thread.Sleep(150);
            ExecuteWithTimeout(connection, "INSERT INTO t VALUES (1);", timeoutSeconds: 10);
            ExecuteWithTimeout(connection, "COMMIT;", timeoutSeconds: 10);
        })).ToArray();

        Task.WaitAll(tasks);

        using var reader = db.Connect();
        using var command = reader.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM t;";
        command.ExecuteScalar().Should().Be((long)workers);
    }

    /// <summary>
    /// The native half of <see cref="CommandTimeoutTurnsABusyBeginIntoABusyWait"/>:
    /// native SQLite retries for the CommandTimeout before reporting busy, and
    /// the managed engine now matches it.
    /// </summary>
    [Test]
    public void NativeSqliteTreatsCommandTimeoutAsABusyWait()
    {
        using var db = new NativeFileDatabase();
        var a = db.Connect();
        var b = db.Connect();

        NativeExec(a, "PRAGMA journal_mode=wal;");
        NativeExec(a, "BEGIN IMMEDIATE;");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using (var command = b.CreateCommand())
        {
            // Deliberately no 'PRAGMA busy_timeout=0' on b: Microsoft.Data.Sqlite
            // maps CommandTimeout onto sqlite3_busy_timeout.
            command.CommandText = "BEGIN IMMEDIATE;";
            command.CommandTimeout = 2;
            var error = NativeError(() => command.ExecuteNonQuery());
            error.Should().NotBeNull();
            error!.SqliteErrorCode.Should().Be(SqliteBusy);
        }

        stopwatch.Stop();
        stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(1));
    }

    private static void ExecuteWithTimeout(SqliteConnection connection, string sql, int timeoutSeconds)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = timeoutSeconds;
        command.ExecuteNonQuery();
    }

    private static SqliteException? Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (SqliteException exception)
        {
            return exception;
        }
    }

    private static MsData.SqliteException? NativeError(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (MsData.SqliteException exception)
        {
            return exception;
        }
    }

    private static void NativeExec(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Native assertions about busy errors would otherwise wait out the 30
    /// second default: Microsoft.Data.Sqlite maps CommandTimeout onto
    /// sqlite3_busy_timeout per command, which overrides the fixture's
    /// <c>PRAGMA busy_timeout=0</c>. One second is enough to prove the error.
    /// </summary>
    private static void NativeExecFast(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 1;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// A throwaway managed file database seeded with a single table, shared by
    /// the two connections each test opens against it. The 1 second default
    /// timeout keeps busy assertions quick: <c>CommandTimeout</c> is the managed
    /// busy timeout, so a contended statement would otherwise wait out the 30
    /// second default before failing.
    /// </summary>
    private sealed class ManagedFileDatabase : IDisposable
    {
        private readonly List<SqliteConnection> _connections = [];

        public ManagedFileDatabase()
        {
            Path = TempDatabasePath("managed");

            using var seed = new SqliteConnection($"Data Source={Path};Local Provider=Managed");
            seed.Open();
            seed.ExecuteNonQuery("CREATE TABLE t(v INTEGER);");
        }

        public string Path { get; }

        public SqliteConnection Connect(int defaultTimeoutSeconds = 1)
        {
            var connection = new SqliteConnection($"Data Source={Path};Local Provider=Managed;Default Timeout={defaultTimeoutSeconds}");
            connection.Open();
            _connections.Add(connection);
            return connection;
        }

        public void Dispose()
        {
            foreach (var connection in _connections)
                connection.Dispose();

            DeleteDatabaseFiles(Path);
        }
    }

    /// <summary>
    /// The same shape as <see cref="ManagedFileDatabase"/> but created and driven
    /// entirely by Microsoft.Data.Sqlite, for differential assertions.
    /// </summary>
    private sealed class NativeFileDatabase : IDisposable
    {
        private readonly List<MsData.SqliteConnection> _connections = [];

        public NativeFileDatabase()
        {
            Path = TempDatabasePath("native");
            NativeExec(Connect(), "CREATE TABLE t(v INTEGER);");
        }

        public string Path { get; }

        public MsData.SqliteConnection Connect()
        {
            var connection = new MsData.SqliteConnection($"Data Source={Path}");
            connection.Open();

            // Fail fast instead of spinning on the default busy handler.
            NativeExec(connection, "PRAGMA busy_timeout=0;");
            _connections.Add(connection);
            return connection;
        }

        public void Dispose()
        {
            foreach (var connection in _connections)
                connection.Dispose();

            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(Path);
        }
    }

    private static string TempDatabasePath(string kind) => System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        $"Ahtola-txn-mode-{kind}-{Guid.NewGuid():N}.db");

    private static void DeleteDatabaseFiles(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            var candidate = path + suffix;
            if (!File.Exists(candidate))
                continue;

            try
            {
                File.Delete(candidate);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temp file must not fail the test.
            }
        }
    }
}
