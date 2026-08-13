using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class ExtendedDmlGrammarSqlRoutingTests
{
    [Test]
    public void UpdateOrderByLimitOffsetSelectsTheSameRowsAsSqlite()
    {
        const string schema = """
            CREATE TABLE t(id INTEGER, label TEXT, value INTEGER);
            INSERT INTO t VALUES
                (1, 'b', 10),
                (2, 'A', 20),
                (3, NULL, 30),
                (4, 'a', 40),
                (5, 'c', 50);
            """;
        using var managed = Connect(schema);
        using var sqlite = ConnectSqlite(schema);

        var expectedIds = ReadSqliteIntegers(
            sqlite,
            "SELECT id FROM t WHERE value >= 20 ORDER BY label COLLATE NOCASE ASC, id DESC LIMIT 2 OFFSET 1;");
        using (var statement = managed.Prepare(
                   """
                   UPDATE t SET value = value + 100
                   WHERE value >= 20
                   RETURNING id
                   ORDER BY label COLLATE NOCASE ASC, id DESC
                   LIMIT 2 OFFSET 1;
                   """))
        {
            DrainIntegers(statement).Order().Should().Equal(expectedIds.Order());
            statement.RowsAffected.Should().Be(2);
        }

        ReadManagedRows(managed, "SELECT id FROM t WHERE value >= 120 ORDER BY id;")
            .Select(row => row[0].AsInteger())
            .Should()
            .Equal(expectedIds.Order());
    }

    [Test]
    public void DeleteOrderingUsesSqliteCollationAndDefaultNullPlacement()
    {
        const string schema = """
            CREATE TABLE t(id INTEGER, label TEXT);
            INSERT INTO t VALUES (1, 'b'), (2, 'A'), (3, NULL), (4, 'a'), (5, 'c');
            """;
        using var managed = Connect(schema);
        using var sqlite = ConnectSqlite(schema);

        var expectedDeleted = ReadSqliteIntegers(
            sqlite,
            "SELECT id FROM t ORDER BY label COLLATE NOCASE ASC, id DESC LIMIT 2 OFFSET 1;");
        ExecuteManaged(
            managed,
            """
            DELETE FROM t
            ORDER BY label COLLATE NOCASE ASC, id DESC
            LIMIT 2 OFFSET 1;
            """);

        var remaining = ReadManagedRows(managed, "SELECT id FROM t ORDER BY id;")
            .Select(row => row[0].AsInteger());
        remaining.Should().Equal(
            Enumerable.Range(1, 5).Select(static value => (long)value).Except(expectedDeleted).Order());
    }

    [Test]
    public void ParametersCommaSyntaxAndNegativeBoundsMatchSqliteSelection()
    {
        const string schema = """
            CREATE TABLE t(id INTEGER, value INTEGER);
            INSERT INTO t VALUES (1, 10), (2, 20), (3, 30), (4, 40), (5, 50);
            """;
        using var managed = Connect(schema);

        using (var update = managed.Prepare(
                   "UPDATE t SET value = value + 1 RETURNING id ORDER BY id DESC LIMIT ?1 OFFSET ?2;"))
        {
            update.Bind(1, SqlValue.Integer(2));
            update.Bind(2, SqlValue.Integer(-10));
            DrainIntegers(update).Should().Equal(4, 5);
            update.RowsAffected.Should().Be(2);
        }

        using (var delete = managed.Prepare("DELETE FROM t ORDER BY id LIMIT ?1, ?2;"))
        {
            delete.Bind(1, SqlValue.Integer(2));
            delete.Bind(2, SqlValue.Integer(-1));
            delete.Step().Should().Be(StatementStepResult.Done);
            delete.RowsAffected.Should().Be(3);
        }

        ReadManagedRows(managed, "SELECT id, value FROM t ORDER BY id;").Should().BeEquivalentTo(
        [
            new[] { SqlValue.Integer(1), SqlValue.Integer(10) },
            new[] { SqlValue.Integer(2), SqlValue.Integer(20) },
        ], options => options.WithStrictOrdering());
    }

    [Test]
    public void ManagedLimitedDmlDoesNotDependOnTheBundledSqliteBuildOption()
    {
        const string schema = """
            CREATE TABLE t(id INTEGER);
            INSERT INTO t VALUES (1), (2), (3);
            """;
        using var managed = Connect(schema);
        using var sqlite = ConnectSqlite(schema);
        using var options = sqlite.CreateCommand();
        options.CommandText = "SELECT count(*) FROM pragma_compile_options WHERE compile_options = 'ENABLE_UPDATE_DELETE_LIMIT';";
        var sqliteSupportsExtension = Convert.ToInt64(options.ExecuteScalar()) != 0;

        using var limitedDelete = sqlite.CreateCommand();
        limitedDelete.CommandText = "DELETE FROM t ORDER BY id DESC LIMIT 1;";
        if (sqliteSupportsExtension)
        {
            limitedDelete.ExecuteNonQuery().Should().Be(1);
        }
        else
        {
            Assert.Throws<SqliteException>(() => limitedDelete.ExecuteNonQuery())!
                .SqliteErrorCode.Should().Be(1);
        }

        ExecuteManaged(managed, "DELETE FROM t ORDER BY id DESC LIMIT 1;");
        ReadManagedRows(managed, "SELECT id FROM t ORDER BY id;")
            .Select(row => row[0])
            .Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));
    }

    [Test]
    public void LimitedUpdatePreservesRowidReturningAndLastInsertRowidSemantics()
    {
        using var connection = Connect(
            """
            CREATE TABLE t(value TEXT);
            INSERT INTO t VALUES ('a'), ('b'), ('c');
            """);
        connection.LastInsertRowId.Should().Be(3);

        using (var statement = connection.Prepare(
                   "UPDATE t SET rowid = rowid + 10 RETURNING rowid, value ORDER BY rowid DESC LIMIT 1;"))
        {
            statement.Step().Should().Be(StatementStepResult.Row);
            ReadCurrentRow(statement).Should().Equal(SqlValue.Integer(13), SqlValue.Text("c"));
            statement.Step().Should().Be(StatementStepResult.Done);
            statement.RowsAffected.Should().Be(1);
        }

        connection.LastInsertRowId.Should().Be(3);
        ReadManagedRows(connection, "SELECT rowid, value FROM t ORDER BY rowid;").Should().BeEquivalentTo(
        [
            new[] { SqlValue.Integer(1), SqlValue.Text("a") },
            new[] { SqlValue.Integer(2), SqlValue.Text("b") },
            new[] { SqlValue.Integer(13), SqlValue.Text("c") },
        ], options => options.WithStrictOrdering());
    }

    [Test]
    public void LimitedDmlFallsBackWithoutDisturbingOrdinaryCompiledRouting()
    {
        using var connection = Connect(
            """
            CREATE TABLE t(id INTEGER, value INTEGER);
            INSERT INTO t VALUES (1, 10), (2, 20), (3, 30);
            """);

        Opcodes(ReadManagedRows(connection, "EXPLAIN UPDATE t SET value = value + 1 WHERE id > 1;"))
            .Should().Contain("Update");
        Assert.Throws<EmbeddedSqlException>(
                () => ReadManagedRows(connection, "EXPLAIN UPDATE t SET value = value + 1 ORDER BY id LIMIT 1;"))!
            .Message.Should().Contain("EXPLAIN is only supported");
        ReadManagedRows(
                connection,
                "EXPLAIN QUERY PLAN UPDATE t SET value = value + 1 ORDER BY id LIMIT 1;")
            .Should().ContainSingle()
            .Which[3].Should().Be(SqlValue.Text("MANAGED EVALUATOR FALLBACK"));

        using var statement = connection.Prepare(
            """
            DELETE FROM t
            WHERE id IN (SELECT id FROM t WHERE value >= 20)
            RETURNING id
            ORDER BY value DESC
            LIMIT 1;
            """);
        DrainIntegers(statement).Should().Equal(3);
        statement.RowsAffected.Should().Be(1);
    }

    [Test]
    public void ReturningFailureKeepsSelectionAndMutationCallbacksOrderedAndAtomic()
    {
        var events = new List<string>();
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction("mark_predicate", 1, values =>
        {
            events.Add($"predicate:{values[0].AsInteger()}");
            return SqlValue.Integer(1);
        });
        database.RegisterScalarFunction("mark_order", 1, values =>
        {
            events.Add($"order:{values[0].AsInteger()}");
            return values[0];
        });
        database.RegisterScalarFunction("mark_assignment", 1, values =>
        {
            events.Add($"assignment:{values[0].AsInteger()}");
            return values[0];
        });
        using var connection = database.Connect();
        ExecuteManagedBatch(
            connection,
            """
            CREATE TABLE t(id INTEGER, value INTEGER);
            INSERT INTO t VALUES (1, 10), (2, 20), (3, 30);
            """);

        using (var statement = connection.Prepare(
                   """
                   UPDATE t SET value = mark_assignment(id)
                   WHERE mark_predicate(id)
                   RETURNING abs(-9223372036854775808)
                   ORDER BY mark_order(id) DESC
                   LIMIT 2;
                   """))
        {
            Assert.Throws<EmbeddedSqlException>(() => statement.Step())!
                .Message.Should().Be("integer overflow");
        }

        events.Should().Equal(
            "predicate:1", "order:1",
            "predicate:2", "order:2",
            "predicate:3", "order:3",
            "assignment:2", "assignment:3");
        ReadManagedRows(connection, "SELECT id, value FROM t ORDER BY id;").Should().BeEquivalentTo(
        [
            new[] { SqlValue.Integer(1), SqlValue.Integer(10) },
            new[] { SqlValue.Integer(2), SqlValue.Integer(20) },
            new[] { SqlValue.Integer(3), SqlValue.Integer(30) },
        ], options => options.WithStrictOrdering());
    }

    [Test]
    public void CancellationDuringLimitedSelectionLeavesTheStatementAtomic()
    {
        using var cancellation = new CancellationTokenSource();
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction("cancel_order", 1, values =>
        {
            cancellation.Cancel();
            return values[0];
        });
        using var connection = database.Connect();
        ExecuteManagedBatch(
            connection,
            """
            CREATE TABLE t(id INTEGER, value INTEGER);
            INSERT INTO t VALUES (1, 10), (2, 20);
            """);

        using (var statement = connection.Prepare(
                   "UPDATE t SET value = value + 1 ORDER BY cancel_order(id) LIMIT 1;"))
        {
            Assert.Throws<OperationCanceledException>(() => statement.Step(cancellation.Token));
        }

        ReadManagedRows(connection, "SELECT value FROM t ORDER BY id;")
            .Select(row => row[0])
            .Should().Equal(SqlValue.Integer(10), SqlValue.Integer(20));
    }

    [Test]
    public void LimitedDmlRetainsTriggerRowsAffectedAndZeroLimitSemantics()
    {
        using var connection = new EmbeddedDatabase().Connect();
        ExecuteManaged(connection, "CREATE TABLE t(id INTEGER, value INTEGER);");
        ExecuteManaged(connection, "CREATE TABLE audit(event TEXT);");
        ExecuteManaged(connection, "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30);");
        ExecuteManaged(
            connection,
            """
            CREATE TRIGGER update_audit AFTER UPDATE ON t
            BEGIN
                INSERT INTO audit VALUES ('update');
            END;
            """);
        ExecuteManaged(
            connection,
            """
            CREATE TRIGGER delete_audit AFTER DELETE ON t
            BEGIN
                INSERT INTO audit VALUES ('delete');
            END;
            """);

        using (var update = connection.Prepare("UPDATE t SET value = 99 ORDER BY id DESC LIMIT 1;"))
        {
            update.Step().Should().Be(StatementStepResult.Done);
            update.RowsAffected.Should().Be(1);
        }
        ExecuteManaged(connection, "UPDATE t SET value = 0 ORDER BY id LIMIT 0;");
        ExecuteManaged(connection, "DELETE FROM t ORDER BY id LIMIT 1;");

        ReadManagedRows(connection, "SELECT event FROM audit ORDER BY rowid;")
            .Select(row => row[0])
            .Should().Equal(SqlValue.Text("update"), SqlValue.Text("delete"));
    }

    [Test]
    public void LimitedUpdateConstraintFailureIsStatementAtomic()
    {
        using var connection = Connect(
            """
            CREATE TABLE t(id INTEGER PRIMARY KEY, value INTEGER UNIQUE);
            INSERT INTO t VALUES (1, 10), (2, 20), (3, 30);
            """);

        Assert.Throws<EmbeddedSqlException>(() =>
            ExecuteManaged(connection, "UPDATE t SET value = 10 ORDER BY id DESC LIMIT 2;"))!
            .Message.Should().Contain("UNIQUE constraint failed");

        ReadManagedRows(connection, "SELECT id, value FROM t ORDER BY id;").Should().BeEquivalentTo(
        [
            new[] { SqlValue.Integer(1), SqlValue.Integer(10) },
            new[] { SqlValue.Integer(2), SqlValue.Integer(20) },
            new[] { SqlValue.Integer(3), SqlValue.Integer(30) },
        ], options => options.WithStrictOrdering());
    }

    [Test]
    public void UnsupportedExtendedFormsAreRejectedBeforeMutation()
    {
        using var connection = Connect(
            """
            CREATE TABLE t(id INTEGER, value INTEGER);
            CREATE TABLE source(id INTEGER, value INTEGER);
            INSERT INTO t VALUES (1, 10);
            INSERT INTO source VALUES (1, 99);
            """);
        var cases = new[]
        {
            ("UPDATE t SET value = 20 ORDER BY id;", "ORDER BY without LIMIT"),
            ("DELETE FROM t ORDER BY id;", "ORDER BY without LIMIT"),

            // Introduced alongside UPDATE ... FROM: the limited-DML route buffers a
            // source-ordered subset, which has no defined meaning once the row set
            // comes from a join, so the combination is refused rather than guessed.
            (
                "UPDATE t SET value = source.value FROM source WHERE source.id = t.id LIMIT 1;",
                "LIMIT is not supported on UPDATE ... FROM"),
        };

        foreach (var (sql, message) in cases)
        {
            Assert.Throws<EmbeddedSqlException>(() =>
            {
                using var statement = connection.Prepare(sql);
                statement.Step();
            })!.Message.Should().Contain(message, sql);
            Scalar(connection, "SELECT value FROM t WHERE id = 1;").Should().Be(10);
        }
    }

    [Test]
    public void ExtendedUpdateFormsMutateTheTargetTable()
    {
        using var connection = Connect(
            """
            CREATE TABLE t(id INTEGER, value INTEGER);
            CREATE TABLE source(id INTEGER, value INTEGER);
            INSERT INTO t VALUES (1, 10);
            INSERT INTO source VALUES (1, 99);
            """);

        ExecuteManaged(connection, "UPDATE OR IGNORE t SET value = 20;");
        Scalar(connection, "SELECT value FROM t WHERE id = 1;").Should().Be(20);

        ExecuteManaged(connection, "UPDATE t AS target SET value = target.value + 1;");
        Scalar(connection, "SELECT value FROM t WHERE id = 1;").Should().Be(21);

        ExecuteManaged(connection, "UPDATE t SET value = source.value FROM source WHERE source.id = t.id;");
        Scalar(connection, "SELECT value FROM t WHERE id = 1;").Should().Be(99);

        ExecuteManaged(connection, "DELETE FROM t AS target WHERE target.id = 1;");
        Scalar(connection, "SELECT count(*) FROM t;").Should().Be(0);
    }

    /// <summary>
    /// Pins the routing boundary the extended UPDATE forms sit on.
    /// </summary>
    /// <remarks>
    /// <c>UPDATE OR</c>, <c>UPDATE ... FROM</c> and target aliases are all
    /// evaluator-owned: the conflict algorithms need the trigger-style backup and
    /// restore of whole tables, and the joined and aliased forms need qualified
    /// source rows that the compiled cursor program does not build. The Readme
    /// documents that; this asserts it, so the boundary is a declared contract
    /// rather than a silent performance cliff, and so that a later branch teaching
    /// the compiled path these shapes is told to update both.
    /// </remarks>
    [Test]
    public void ExtendedUpdateFormsReportTheEvaluatorBoundaryInQueryPlans()
    {
        using var connection = Connect(
            """
            CREATE TABLE t(id INTEGER, value INTEGER);
            CREATE TABLE source(id INTEGER, value INTEGER);
            """);

        string[] evaluatorOwned =
        [
            "UPDATE OR IGNORE t SET value = 20;",
            "UPDATE OR REPLACE t SET value = 20;",
            "UPDATE t AS target SET value = target.value + 1;",
            "UPDATE t SET value = source.value FROM source WHERE source.id = t.id;",
            "DELETE FROM t AS target WHERE target.id = 1;",
        ];

        foreach (var statement in evaluatorOwned)
        {
            ReadManagedRows(connection, "EXPLAIN QUERY PLAN " + statement)
                .Should().ContainSingle()
                .Which[3].Should().Be(
                    SqlValue.Text("MANAGED EVALUATOR FALLBACK"),
                    "'{0}' is evaluator-owned",
                    statement);
        }

        // The plain forms these were derived from must keep their compiled routing,
        // so the fallback is scoped to the extended grammar rather than regressing
        // ordinary UPDATE and DELETE.
        Opcodes(ReadManagedRows(connection, "EXPLAIN UPDATE t SET value = value + 1;"))
            .Should().Contain("Update");
        Opcodes(ReadManagedRows(connection, "EXPLAIN DELETE FROM t WHERE id = 1;"))
            .Should().Contain("Delete");
    }

    [Test]
    public void TriggerBodiesRejectLimitedDmlAtCreationTime()
    {
        using var connection = Connect(
            """
            CREATE TABLE source(value INTEGER);
            CREATE TABLE target(value INTEGER);
            INSERT INTO target VALUES (1), (2);
            """);

        Assert.Throws<EmbeddedSqlException>(() => connection.Prepare(
            """
            CREATE TRIGGER limited AFTER INSERT ON source
            BEGIN
                DELETE FROM target LIMIT 1;
            END;
            """))!.Message.Should().Contain("inside trigger bodies");

        ExecuteManaged(connection, "INSERT INTO source VALUES (1);");
        Scalar(connection, "SELECT count(*) FROM target;").Should().Be(2);
    }

    [Test]
    public void LimitedDmlParticipatesInTransactionRollback()
    {
        using var connection = Connect(
            """
            CREATE TABLE t(id INTEGER, value INTEGER);
            INSERT INTO t VALUES (1, 10), (2, 20), (3, 30);
            """);

        ExecuteManaged(connection, "BEGIN;");
        ExecuteManaged(connection, "UPDATE t SET value = 99 ORDER BY id DESC LIMIT 2;");
        Scalar(connection, "SELECT count(*) FROM t WHERE value = 99;").Should().Be(2);
        ExecuteManaged(connection, "ROLLBACK;");

        ReadManagedRows(connection, "SELECT value FROM t ORDER BY id;")
            .Select(row => row[0])
            .Should().Equal(SqlValue.Integer(10), SqlValue.Integer(20), SqlValue.Integer(30));
    }

    [Test]
    public void LimitedDmlPersistsAcrossReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "extended-dml-reopen.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            ExecuteManagedBatch(
                connection,
                """
                CREATE TABLE t(id INTEGER, value INTEGER);
                INSERT INTO t VALUES (1, 10), (2, 20), (3, 30), (4, 40);
                UPDATE t SET value = value + 100 ORDER BY id DESC LIMIT 2;
                DELETE FROM t ORDER BY value ASC LIMIT 1;
                """);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadManagedRows(reopenedConnection, "SELECT id, value FROM t ORDER BY id;").Should().BeEquivalentTo(
        [
            new[] { SqlValue.Integer(2), SqlValue.Integer(20) },
            new[] { SqlValue.Integer(3), SqlValue.Integer(130) },
            new[] { SqlValue.Integer(4), SqlValue.Integer(140) },
        ], options => options.WithStrictOrdering());
    }

    [Test]
    public void InvalidBoundsAndCollationsFailBeforeMutation()
    {
        using var connection = Connect(
            """
            CREATE TABLE t(id INTEGER, value INTEGER);
            INSERT INTO t VALUES (1, 10), (2, 20);
            """);

        using (var statement = connection.Prepare("DELETE FROM t ORDER BY id LIMIT ?1;"))
        {
            statement.Bind(1, SqlValue.Text("not-an-integer"));
            Assert.Throws<EmbeddedSqlException>(() => statement.Step())!
                .Message.Should().Be("datatype mismatch");
        }

        Assert.Throws<EmbeddedSqlException>(() =>
            ExecuteManaged(connection, "UPDATE t SET value = 0 ORDER BY value COLLATE missing LIMIT 1;"))!
            .Message.Should().Contain("no such collation sequence");
        Scalar(connection, "SELECT count(*) FROM t;").Should().Be(2);
        Scalar(connection, "SELECT sum(value) FROM t;").Should().Be(30);
    }

    private static EmbeddedConnection Connect(string sql)
    {
        var connection = new EmbeddedDatabase().Connect();
        ExecuteManagedBatch(connection, sql);
        return connection;
    }

    private static SqliteConnection ConnectSqlite(string sql)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
        return connection;
    }

    private static void ExecuteManagedBatch(EmbeddedConnection connection, string sql)
    {
        foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries))
            ExecuteManaged(connection, statement);
    }

    private static void ExecuteManaged(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static List<SqlValue[]> ReadManagedRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
            rows.Add(ReadCurrentRow(statement));
        return rows;
    }

    private static SqlValue[] ReadCurrentRow(EmbeddedStatement statement)
    {
        var row = new SqlValue[statement.GetColumnCount()];
        for (var index = 0; index < row.Length; index++)
            row[index] = statement.GetValue(index);
        return row;
    }

    private static List<long> DrainIntegers(EmbeddedStatement statement)
    {
        var values = new List<long>();
        while (statement.Step() == StatementStepResult.Row)
            values.Add(statement.GetValue(0).AsInteger());
        return values;
    }

    private static List<long> ReadSqliteIntegers(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<long>();
        while (reader.Read())
            values.Add(reader.GetInt64(0));
        return values;
    }

    private static long Scalar(EmbeddedConnection connection, string sql)
        => ReadManagedRows(connection, sql).Single().Single().AsInteger();

    private static IEnumerable<string> Opcodes(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[1].AsText());
}
