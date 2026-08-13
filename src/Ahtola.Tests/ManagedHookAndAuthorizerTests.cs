using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// Behavioral coverage for the managed update, commit and rollback hooks, the authorizer, the
/// trace callback and the progress handler.
/// </summary>
/// <remarks>
/// Every expectation encoded here was first observed against real SQLite (e_sqlite3 through
/// SQLitePCLRaw) so the managed engine reproduces measured behavior rather than assumed behavior.
/// <see cref="ManagedHookSqliteDifferentialTests"/> re-runs the load-bearing scenarios against
/// real SQLite at test time so the two implementations cannot drift apart silently.
/// </remarks>
public sealed class ManagedHookAndAuthorizerTests
{
    [Test]
    public void UpdateHookReportsEachInsertedUpdatedAndDeletedRow()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");

        var changes = new List<SqliteRowChange>();
        connection.SetUpdateHook(changes.Add);

        Execute(connection, "INSERT INTO t VALUES (1, 'one'), (2, 'two')");
        Execute(connection, "UPDATE t SET b = 'ONE' WHERE a = 1");
        Execute(connection, "DELETE FROM t WHERE a = 2");

        changes.Should().Equal(
            new SqliteRowChange(SqliteChangeOperation.Insert, "main", "t", 1),
            new SqliteRowChange(SqliteChangeOperation.Insert, "main", "t", 2),
            new SqliteRowChange(SqliteChangeOperation.Update, "main", "t", 1),
            new SqliteRowChange(SqliteChangeOperation.Delete, "main", "t", 2));
    }

    [Test]
    public void UpdateHookReportsTheNewRowIdWhenARowIdAliasIsUpdated()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");
        Execute(connection, "INSERT INTO t VALUES (1, 'one')");

        var changes = new List<SqliteRowChange>();
        connection.SetUpdateHook(changes.Add);
        Execute(connection, "UPDATE t SET a = 9 WHERE a = 1");

        changes.Should().Equal(new SqliteRowChange(SqliteChangeOperation.Update, "main", "t", 9));
    }

    [Test]
    public void UpdateHookIsSilentForWithoutRowidTables()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE w(a INTEGER PRIMARY KEY, b TEXT) WITHOUT ROWID");

        var changes = new List<SqliteRowChange>();
        connection.SetUpdateHook(changes.Add);
        Execute(connection, "INSERT INTO w VALUES (1, 'one')");
        Execute(connection, "UPDATE w SET b = 'ONE' WHERE a = 1");
        Execute(connection, "DELETE FROM w WHERE a = 1");

        changes.Should().BeEmpty();
    }

    [Test]
    public void UpdateHookIsSilentForTheImplicitDeleteOfReplaceConflictResolution()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");
        Execute(connection, "INSERT INTO t VALUES (1, 'one')");

        var changes = new List<SqliteRowChange>();
        connection.SetUpdateHook(changes.Add);
        Execute(connection, "INSERT OR REPLACE INTO t VALUES (1, 'replaced')");

        changes.Should().Equal(new SqliteRowChange(SqliteChangeOperation.Insert, "main", "t", 1));
    }

    [Test]
    public void UpdateHookIsSilentForUniqueIndexReplaceConflictResolution()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT UNIQUE)");
        Execute(connection, "INSERT INTO t VALUES (1, 'one')");

        var changes = new List<SqliteRowChange>();
        connection.SetUpdateHook(changes.Add);
        Execute(connection, "INSERT OR REPLACE INTO t VALUES (2, 'one')");

        changes.Should().Equal(new SqliteRowChange(SqliteChangeOperation.Insert, "main", "t", 2));
    }

    [Test]
    public void UpdateHookIsSilentForSqliteSequenceMaintenance()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY AUTOINCREMENT, b TEXT)");

        var changes = new List<SqliteRowChange>();
        connection.SetUpdateHook(changes.Add);
        Execute(connection, "INSERT INTO t(b) VALUES ('one')");
        Execute(connection, "INSERT INTO t(b) VALUES ('two')");

        changes.Should().Equal(
            new SqliteRowChange(SqliteChangeOperation.Insert, "main", "t", 1),
            new SqliteRowChange(SqliteChangeOperation.Insert, "main", "t", 2));
    }

    [Test]
    public void UpdateHookIsSilentForCreateTableAsSelect()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");
        Execute(connection, "INSERT INTO t VALUES (1, 'one'), (2, 'two')");

        var changes = new List<SqliteRowChange>();
        connection.SetUpdateHook(changes.Add);
        Execute(connection, "CREATE TABLE copy AS SELECT * FROM t");

        changes.Should().BeEmpty();
    }

    [Test]
    public void UpdateHookReportsTriggerDrivenWrites()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");
        Execute(connection, "CREATE TABLE audit(id INTEGER PRIMARY KEY, note TEXT)");
        Execute(
            connection,
            "CREATE TRIGGER tr AFTER INSERT ON t BEGIN INSERT INTO audit(note) VALUES (new.b); END");

        var changes = new List<SqliteRowChange>();
        connection.SetUpdateHook(changes.Add);
        Execute(connection, "INSERT INTO t VALUES (1, 'one')");

        changes.Select(change => (change.Operation, change.Table))
            .Should()
            .BeEquivalentTo(
                [
                    (SqliteChangeOperation.Insert, "t"),
                    (SqliteChangeOperation.Insert, "audit"),
                ]);
    }

    [Test]
    public void UpdateHookReportsTheTempSchemaForTemporaryTables()
    {
        using var connection = Open();
        Execute(connection, "CREATE TEMP TABLE t(a INTEGER PRIMARY KEY, b TEXT)");

        var changes = new List<SqliteRowChange>();
        connection.SetUpdateHook(changes.Add);
        Execute(connection, "INSERT INTO t VALUES (1, 'one')");

        changes.Should().Equal(new SqliteRowChange(SqliteChangeOperation.Insert, "temp", "t", 1));
    }

    [Test]
    public void UpdateHookStopsFiringOnceCleared()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");

        var changes = new List<SqliteRowChange>();
        connection.SetUpdateHook(changes.Add);
        Execute(connection, "INSERT INTO t VALUES (1, 'one')");
        connection.SetUpdateHook(null);
        Execute(connection, "INSERT INTO t VALUES (2, 'two')");

        changes.Should().HaveCount(1);
    }

    [Test]
    public void EveryUpdateNotificationPrecedesTheCommitHook()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");

        var order = new List<string>();
        connection.SetUpdateHook(change => order.Add($"update:{change.RowId}"));
        connection.SetCommitHook(() =>
        {
            order.Add("commit");
            return true;
        });

        Execute(connection, "INSERT INTO t VALUES (1, 'one'), (2, 'two')");

        order.Should().Equal("update:1", "update:2", "commit");
    }

    [Test]
    public void CommitHookIsNotConsultedForReadOnlyStatements()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");
        Execute(connection, "INSERT INTO t VALUES (1, 'one')");

        var commits = 0;
        connection.SetCommitHook(() =>
        {
            commits++;
            return true;
        });

        ReadScalar(connection, "SELECT count(*) FROM t").Should().Be(1L);
        commits.Should().Be(0);
    }

    [Test]
    public void CommitHookIsNotConsultedForATransactionThatChangedNothing()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");

        var commits = 0;
        connection.SetCommitHook(() =>
        {
            commits++;
            return true;
        });

        Execute(connection, "BEGIN");
        Execute(connection, "COMMIT");

        commits.Should().Be(0);
    }

    [Test]
    public void AVetoingCommitHookRollsBackAnAutocommitStatement()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");
        Execute(connection, "INSERT INTO t VALUES (1, 'one')");

        var order = new List<string>();
        connection.SetCommitHook(() =>
        {
            order.Add("commit");
            return false;
        });
        connection.SetRollbackHook(() => order.Add("rollback"));

        var error = Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO t VALUES (2, 'two')"));
        error!.SqliteErrorCode.Should().Be(19);
        error.Message.Should().Contain("constraint failed");

        order.Should().Equal("commit", "rollback");

        connection.SetCommitHook(null);
        ReadScalar(connection, "SELECT count(*) FROM t").Should().Be(1L);
    }

    [Test]
    public void AVetoingCommitHookRollsBackAnExplicitTransaction()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");
        Execute(connection, "INSERT INTO t VALUES (1, 'one')");

        var order = new List<string>();
        connection.SetCommitHook(() =>
        {
            order.Add("commit");
            return false;
        });
        connection.SetRollbackHook(() => order.Add("rollback"));

        Execute(connection, "BEGIN");
        Execute(connection, "INSERT INTO t VALUES (2, 'two')");
        Execute(connection, "INSERT INTO t VALUES (3, 'three')");
        var error = Assert.Throws<SqliteException>(() => Execute(connection, "COMMIT"));
        error!.SqliteErrorCode.Should().Be(19);

        order.Should().Equal("commit", "rollback");

        connection.SetCommitHook(null);
        ReadScalar(connection, "SELECT count(*) FROM t").Should().Be(1L);
    }

    [Test]
    public void AVetoingCommitHookLeavesAFileBackedDatabaseUnchanged()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var seed = Open(path))
            {
                Execute(seed, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");
                Execute(seed, "INSERT INTO t VALUES (1, 'one')");
            }

            using (var connection = Open(path))
            {
                connection.SetCommitHook(() => false);
                Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO t VALUES (2, 'two')"));
            }

            using var verify = Open(path);
            ReadScalar(verify, "SELECT count(*) FROM t").Should().Be(1L);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void ARolledBackTransactionStillReportedItsRowChanges()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");

        var changes = new List<SqliteRowChange>();
        connection.SetUpdateHook(changes.Add);

        Execute(connection, "BEGIN");
        Execute(connection, "INSERT INTO t VALUES (1, 'one')");
        Execute(connection, "ROLLBACK");

        changes.Should().HaveCount(1);
        ReadScalar(connection, "SELECT count(*) FROM t").Should().Be(0L);
    }

    [Test]
    public void RollbackHookFiresForAnExplicitRollbackThatChangedNothing()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");

        var rollbacks = 0;
        connection.SetRollbackHook(() => rollbacks++);

        Execute(connection, "BEGIN");
        Execute(connection, "ROLLBACK");

        rollbacks.Should().Be(1);
    }

    [Test]
    public void RollbackHookFiresForAFailedAutocommitMutation()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");
        Execute(connection, "INSERT INTO t VALUES (1, 'one')");

        var rollbacks = 0;
        connection.SetRollbackHook(() => rollbacks++);

        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO t VALUES (1, 'duplicate')"));

        rollbacks.Should().Be(1);
    }

    [Test]
    public void RollbackHookDoesNotFireForAFailedStatementInsideATransaction()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");
        Execute(connection, "INSERT INTO t VALUES (1, 'one')");

        var rollbacks = 0;
        connection.SetRollbackHook(() => rollbacks++);

        Execute(connection, "BEGIN");
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO t VALUES (1, 'duplicate')"));
        rollbacks.Should().Be(0);

        Execute(connection, "ROLLBACK");
        rollbacks.Should().Be(1);
    }

    [Test]
    public void AnUpdateHookCannotReenterTheConnection()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");

        Exception? captured = null;
        connection.SetUpdateHook(_ =>
        {
            try
            {
                ReadScalar(connection, "SELECT count(*) FROM t");
            }
            catch (Exception exception)
            {
                captured = exception;
                throw;
            }
        });

        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO t VALUES (1, 'one')"));
        captured.Should().NotBeNull();
        captured!.Message.Should().Contain("reentrant");
    }

    [Test]
    public void ACommitHookCannotReenterTheConnection()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");

        Exception? captured = null;
        connection.SetCommitHook(() =>
        {
            try
            {
                ReadScalar(connection, "SELECT count(*) FROM t");
            }
            catch (Exception exception)
            {
                captured = exception;
                throw;
            }

            return true;
        });

        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO t VALUES (1, 'one')"));
        captured.Should().NotBeNull();
        captured!.Message.Should().Contain("reentrant");
    }

    [Test]
    public void AuthorizerReceivesTheSelectAndReadActionsOfAQuery()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");

        var seen = new List<SqliteAuthorizerContext>();
        connection.SetAuthorizer(context =>
        {
            seen.Add(context);
            return SqliteAuthorizerResult.Ok;
        });

        ReadScalar(connection, "SELECT b FROM t WHERE a = 1");

        seen.Should().Contain(context => context.Action == SqliteAuthorizerAction.Select);
        seen.Where(context => context.Action == SqliteAuthorizerAction.Read)
            .Select(context => (context.Argument0, context.Argument1, context.Database))
            .Should()
            .BeEquivalentTo([("t", "b", "main"), ("t", "a", "main")]);
    }

    [Test]
    public void AuthorizerReceivesTheInsertUpdateAndDeleteActions()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");

        var seen = new List<SqliteAuthorizerContext>();
        connection.SetAuthorizer(context =>
        {
            seen.Add(context);
            return SqliteAuthorizerResult.Ok;
        });

        Execute(connection, "INSERT INTO t VALUES (1, 'one')");
        Execute(connection, "UPDATE t SET b = 'ONE' WHERE a = 1");
        Execute(connection, "DELETE FROM t WHERE a = 1");

        seen.Should().Contain(context =>
            context.Action == SqliteAuthorizerAction.Insert && context.Argument0 == "t");
        seen.Should().Contain(context =>
            context.Action == SqliteAuthorizerAction.Update && context.Argument0 == "t" && context.Argument1 == "b");
        seen.Should().Contain(context =>
            context.Action == SqliteAuthorizerAction.Delete && context.Argument0 == "t");
    }

    [Test]
    public void AuthorizerDenialOfAReadFailsPreparationWithTheSqliteMessage()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");

        connection.SetAuthorizer(context =>
            context.Action == SqliteAuthorizerAction.Read && context.Argument1 == "b"
                ? SqliteAuthorizerResult.Deny
                : SqliteAuthorizerResult.Ok);

        var error = Assert.Throws<SqliteException>(() => ReadScalar(connection, "SELECT b FROM t"));
        error!.SqliteErrorCode.Should().Be(23);
        error.Message.Should().Contain("access to t.b is prohibited");
    }

    [Test]
    public void AuthorizerDenialOfAWriteFailsPreparationWithTheGenericMessage()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");

        connection.SetAuthorizer(context =>
            context.Action == SqliteAuthorizerAction.Insert ? SqliteAuthorizerResult.Deny : SqliteAuthorizerResult.Ok);

        var error = Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO t VALUES (1, 'one')"));
        error!.SqliteErrorCode.Should().Be(23);
        error.Message.Should().Contain("not authorized");
    }

    [Test]
    public void AuthorizerIgnoreSubstitutesNullForAColumnRead()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");
        Execute(connection, "INSERT INTO t VALUES (1, 'secret')");

        connection.SetAuthorizer(context =>
            context.Action == SqliteAuthorizerAction.Read && context.Argument1 == "b"
                ? SqliteAuthorizerResult.Ignore
                : SqliteAuthorizerResult.Ok);

        ReadScalar(connection, "SELECT b FROM t").Should().BeNull();
    }

    [Test]
    public void AuthorizerIgnoreSubstitutesNullInsideAPredicate()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");
        Execute(connection, "INSERT INTO t VALUES (1, 'secret')");

        connection.SetAuthorizer(context =>
            context.Action == SqliteAuthorizerAction.Read && context.Argument1 == "b"
                ? SqliteAuthorizerResult.Ignore
                : SqliteAuthorizerResult.Ok);

        ReadScalar(connection, "SELECT count(*) FROM t WHERE b = 'secret'").Should().Be(0L);
    }

    [Test]
    public void AuthorizerIgnoreTurnsAnInsertIntoANoOp()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");

        connection.SetAuthorizer(context =>
            context.Action == SqliteAuthorizerAction.Insert ? SqliteAuthorizerResult.Ignore : SqliteAuthorizerResult.Ok);

        Execute(connection, "INSERT INTO t VALUES (1, 'one')");

        connection.SetAuthorizer(null);
        ReadScalar(connection, "SELECT count(*) FROM t").Should().Be(0L);
    }

    [Test]
    public void AuthorizerIgnoreSkipsASingleUpdateAssignment()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT, c TEXT)");
        Execute(connection, "INSERT INTO t VALUES (1, 'b0', 'c0')");

        connection.SetAuthorizer(context =>
            context.Action == SqliteAuthorizerAction.Update && context.Argument1 == "b"
                ? SqliteAuthorizerResult.Ignore
                : SqliteAuthorizerResult.Ok);

        Execute(connection, "UPDATE t SET b = 'b1', c = 'c1' WHERE a = 1");

        connection.SetAuthorizer(null);
        ReadScalar(connection, "SELECT b FROM t").Should().Be("b0");
        ReadScalar(connection, "SELECT c FROM t").Should().Be("c1");
    }

    [Test]
    public void AuthorizerIgnoreStillDeletesRows()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");
        Execute(connection, "INSERT INTO t VALUES (1, 'one')");

        connection.SetAuthorizer(context =>
            context.Action == SqliteAuthorizerAction.Delete ? SqliteAuthorizerResult.Ignore : SqliteAuthorizerResult.Ok);

        Execute(connection, "DELETE FROM t WHERE a = 1");

        connection.SetAuthorizer(null);
        ReadScalar(connection, "SELECT count(*) FROM t").Should().Be(0L);
    }

    [Test]
    public void AuthorizerIgnoreOnSelectReturnsNoRows()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");
        Execute(connection, "INSERT INTO t VALUES (1, 'one'), (2, 'two')");

        connection.SetAuthorizer(context =>
            context.Action == SqliteAuthorizerAction.Select ? SqliteAuthorizerResult.Ignore : SqliteAuthorizerResult.Ok);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT a FROM t";
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeFalse();
    }

    [Test]
    public void AuthorizerDeniesOnlyTheOffendingStatementOfAScript()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");
        Execute(connection, "CREATE TABLE u(a INTEGER PRIMARY KEY, b TEXT)");

        connection.SetAuthorizer(context =>
            context.Action == SqliteAuthorizerAction.Insert && context.Argument0 == "u"
                ? SqliteAuthorizerResult.Deny
                : SqliteAuthorizerResult.Ok);

        Assert.Throws<SqliteException>(() => Execute(
            connection,
            "INSERT INTO t VALUES (1, 'one'); INSERT INTO u VALUES (1, 'one'); INSERT INTO t VALUES (2, 'two');"));

        connection.SetAuthorizer(null);
        ReadScalar(connection, "SELECT count(*) FROM t").Should().Be(1L);
        ReadScalar(connection, "SELECT count(*) FROM u").Should().Be(0L);
    }

    [Test]
    public void AuthorizerSeesTheBaseTableBehindAView()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");
        Execute(connection, "CREATE VIEW v AS SELECT a, b FROM t");
        Execute(connection, "INSERT INTO t VALUES (1, 'one')");

        var seen = new List<SqliteAuthorizerContext>();
        connection.SetAuthorizer(context =>
        {
            seen.Add(context);
            return SqliteAuthorizerResult.Ok;
        });

        ReadScalar(connection, "SELECT b FROM v");

        seen.Should().Contain(context =>
            context.Action == SqliteAuthorizerAction.Read
            && context.Argument0 == "t"
            && context.Argument1 == "b"
            && context.TriggerOrView == "v");
    }

    [Test]
    public void AuthorizerCannotBeBypassedByReadingThroughAView()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");
        Execute(connection, "CREATE VIEW v AS SELECT a, b FROM t");

        connection.SetAuthorizer(context =>
            context.Action == SqliteAuthorizerAction.Read && context.Argument0 == "t" && context.Argument1 == "b"
                ? SqliteAuthorizerResult.Deny
                : SqliteAuthorizerResult.Ok);

        var error = Assert.Throws<SqliteException>(() => ReadScalar(connection, "SELECT b FROM v"));
        error!.SqliteErrorCode.Should().Be(23);
    }

    [Test]
    public void AuthorizerSeesTriggerBodyActionsAndCanDenyThem()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");
        Execute(connection, "CREATE TABLE audit(id INTEGER PRIMARY KEY, note TEXT)");
        Execute(
            connection,
            "CREATE TRIGGER tr AFTER INSERT ON t BEGIN INSERT INTO audit(note) VALUES (new.b); END");

        var seen = new List<SqliteAuthorizerContext>();
        connection.SetAuthorizer(context =>
        {
            seen.Add(context);
            return context.Action == SqliteAuthorizerAction.Insert && context.Argument0 == "audit"
                ? SqliteAuthorizerResult.Deny
                : SqliteAuthorizerResult.Ok;
        });

        var error = Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO t VALUES (1, 'one')"));
        error!.SqliteErrorCode.Should().Be(23);
        seen.Should().Contain(context =>
            context.Action == SqliteAuthorizerAction.Insert
            && context.Argument0 == "audit"
            && context.TriggerOrView == "tr");
    }

    [Test]
    public void AuthorizerReceivesTransactionActions()
    {
        using var connection = Open();

        var seen = new List<SqliteAuthorizerContext>();
        connection.SetAuthorizer(context =>
        {
            seen.Add(context);
            return SqliteAuthorizerResult.Ok;
        });

        Execute(connection, "BEGIN");
        Execute(connection, "COMMIT");

        seen.Where(context => context.Action == SqliteAuthorizerAction.Transaction)
            .Select(context => context.Argument0)
            .Should()
            .Equal("BEGIN", "COMMIT");
    }

    [Test]
    public void AuthorizerStopsBeingConsultedOnceCleared()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");

        connection.SetAuthorizer(_ => SqliteAuthorizerResult.Deny);
        Assert.Throws<SqliteException>(() => ReadScalar(connection, "SELECT count(*) FROM t"));

        connection.SetAuthorizer(null);
        ReadScalar(connection, "SELECT count(*) FROM t").Should().Be(0L);
    }

    [Test]
    public void TraceReportsEveryExecutedStatement()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");

        var traced = new List<string>();
        connection.SetTraceHandler(traced.Add);

        Execute(connection, "INSERT INTO t VALUES (1, 'one')");
        ReadScalar(connection, "SELECT count(*) FROM t");

        traced.Should().Equal("INSERT INTO t VALUES (1, 'one')", "SELECT count(*) FROM t");
    }

    [Test]
    public void TraceReportsParameterizedStatementsWithoutExpandingParameters()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");

        var traced = new List<string>();
        connection.SetTraceHandler(traced.Add);

        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO t VALUES ($a, $b)";
        command.Parameters.AddWithValue("$a", 1);
        command.Parameters.AddWithValue("$b", "one");
        command.ExecuteNonQuery();

        traced.Should().Equal("INSERT INTO t VALUES ($a, $b)");
    }

    [Test]
    public void ProgressHandlerIsInvokedWhileAStatementRuns()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY)");
        InsertRange(connection, 200);

        var calls = 0;
        connection.SetProgressHandler(1, () =>
        {
            calls++;
            return false;
        });

        ReadScalar(connection, "SELECT count(*) FROM t").Should().Be(200L);
        calls.Should().BeGreaterThan(0);
    }

    [Test]
    public void ProgressHandlerCanInterruptALongRunningStatement()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY)");
        InsertRange(connection, 200);

        var calls = 0;
        connection.SetProgressHandler(1, () => ++calls >= 5);

        var error = Assert.Throws<SqliteException>(() => ReadScalar(connection, "SELECT count(*) FROM t"));
        error!.SqliteErrorCode.Should().Be(9);
        error.Message.Should().Contain("interrupted");
    }

    [Test]
    public void AnInterruptedMutationIsNotPersisted()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY)");
        Execute(connection, "CREATE TABLE u(a INTEGER PRIMARY KEY)");
        InsertRange(connection, 200);

        var calls = 0;
        connection.SetProgressHandler(1, () => ++calls >= 10);

        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO u SELECT a FROM t"));

        connection.SetProgressHandler(0, null);
        ReadScalar(connection, "SELECT count(*) FROM u").Should().Be(0L);
    }

    [Test]
    public void ProgressHandlerStopsFiringOnceCleared()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY)");
        InsertRange(connection, 50);

        var calls = 0;
        connection.SetProgressHandler(1, () =>
        {
            calls++;
            return false;
        });
        ReadScalar(connection, "SELECT count(*) FROM t");
        var afterFirst = calls;
        afterFirst.Should().BeGreaterThan(0);

        connection.SetProgressHandler(0, null);
        ReadScalar(connection, "SELECT count(*) FROM t");
        calls.Should().Be(afterFirst);
    }

    [Test]
    public void ProgressHandlerRejectsANonPositiveInterval()
    {
        using var connection = Open();
        Assert.Throws<ArgumentOutOfRangeException>(() => connection.SetProgressHandler(0, () => false));
    }

    [Test]
    public void HooksSurviveCloseAndReopen()
    {
        var path = CreateDatabasePath();
        try
        {
            var changes = new List<SqliteRowChange>();
            using var connection = new SqliteConnection($"Data Source={path};Local Provider=Managed;Pooling=False");
            connection.Open();
            Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)");
            connection.SetUpdateHook(changes.Add);
            Execute(connection, "INSERT INTO t VALUES (1, 'one')");
            connection.Close();

            connection.Open();
            Execute(connection, "INSERT INTO t VALUES (2, 'two')");

            changes.Should().HaveCount(2);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void HooksAreRejectedOnManagedSharedMemoryConnections()
    {
        using var connection = new SqliteConnection(
            $"Data Source=hooks-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Local Provider=Managed");
        connection.Open();

        Assert.Throws<NotSupportedException>(() => connection.SetUpdateHook(_ => { }));
        Assert.Throws<NotSupportedException>(() => connection.SetCommitHook(() => true));
        Assert.Throws<NotSupportedException>(() => connection.SetRollbackHook(() => { }));
        Assert.Throws<NotSupportedException>(() => connection.SetAuthorizer(_ => SqliteAuthorizerResult.Ok));
        Assert.Throws<NotSupportedException>(() => connection.SetTraceHandler(_ => { }));
        Assert.Throws<NotSupportedException>(() => connection.SetProgressHandler(1, () => false));
    }

    [Test]
    public void ClearingAHookIsAlwaysAllowed()
    {
        using var connection = new SqliteConnection(
            $"Data Source=hooks-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Local Provider=Managed");
        connection.Open();

        connection.SetUpdateHook(null);
        connection.SetCommitHook(null);
        connection.SetRollbackHook(null);
        connection.SetAuthorizer(null);
        connection.SetTraceHandler(null);
        connection.SetProgressHandler(0, null);
    }

    private static SqliteConnection Open(string? path = null)
    {
        var connection = new SqliteConnection(
            $"Data Source={path ?? ":memory:"};Local Provider=Managed;Pooling=False");
        connection.Open();
        return connection;
    }

    private static void InsertRange(SqliteConnection connection, int count)
    {
        Execute(connection, "BEGIN");
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO t(a) VALUES ($a)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$a";
            command.Parameters.Add(parameter);
            for (var index = 1; index <= count; index++)
            {
                parameter.Value = index;
                command.ExecuteNonQuery();
            }
        }

        Execute(connection, "COMMIT");
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? ReadScalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    private static string CreateDatabasePath()
        => Path.Combine(Path.GetTempPath(), $"Ahtola-hooks-{Guid.NewGuid():N}.db");

    private static void DeleteDatabase(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
        {
            try
            {
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
            catch (IOException)
            {
            }
        }
    }
}
