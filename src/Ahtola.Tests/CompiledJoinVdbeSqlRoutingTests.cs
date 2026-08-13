using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;

namespace Ahtola.Tests;

public class CompiledJoinVdbeSqlRoutingTests
{
    [Test]
    public void ThreeWayInnerJoinRoutesThroughMaterializingJoinCursor()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE a(id INTEGER, av TEXT);");
        Execute(connection, "CREATE TABLE b(id INTEGER, bv TEXT);");
        Execute(connection, "CREATE TABLE c(id INTEGER, cv TEXT);");
        Execute(connection, "INSERT INTO a VALUES (1, 'a1'), (2, 'a2');");
        Execute(connection, "INSERT INTO b VALUES (1, 'b1'), (2, 'b2');");
        Execute(connection, "INSERT INTO c VALUES (2, 'c2');");

        ReadRows(
                connection,
                "SELECT a.av, b.bv, c.cv FROM a JOIN b ON a.id=b.id JOIN c ON b.id=c.id;")
            .Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Text("a2"), SqlValue.Text("b2"), SqlValue.Text("c2"));

        ReadRows(
                connection,
                "EXPLAIN SELECT a.av, b.bv, c.cv FROM a JOIN b ON a.id=b.id JOIN c ON b.id=c.id;")
            .Select(row => row[1].AsText())
            .Should().Contain("OpenJoinCursor");
        ReadRows(
                connection,
                "EXPLAIN QUERY PLAN SELECT a.av, b.bv, c.cv FROM a JOIN b ON a.id=b.id JOIN c ON b.id=c.id;")
            .Should().ContainSingle()
            .Which[3].Should().Be(SqlValue.Text("MANAGED COMPILED VDBE"));
    }

    [Test]
    public void NWayLeftJoinsPreserveEachNullExtensionBoundary()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE a(id INTEGER, av TEXT);");
        Execute(connection, "CREATE TABLE b(id INTEGER, bv TEXT);");
        Execute(connection, "CREATE TABLE c(id INTEGER, cv TEXT);");
        Execute(connection, "INSERT INTO a VALUES (1, 'a1'), (2, 'a2'), (3, 'a3');");
        Execute(connection, "INSERT INTO b VALUES (1, 'b1'), (2, 'b2');");
        Execute(connection, "INSERT INTO c VALUES (2, 'c2');");

        var rows = ReadRows(
            connection,
            """
            SELECT a.av || '!', b.bv, c.cv
            FROM a
            LEFT JOIN b ON a.id=b.id
            LEFT JOIN c ON b.id=c.id;
            """);

        rows.Should().HaveCount(3);
        rows[0].Should().Equal(SqlValue.Text("a1!"), SqlValue.Text("b1"), SqlValue.Null);
        rows[1].Should().Equal(SqlValue.Text("a2!"), SqlValue.Text("b2"), SqlValue.Text("c2"));
        rows[2].Should().Equal(SqlValue.Text("a3!"), SqlValue.Null, SqlValue.Null);
        Opcodes(connection, """
            EXPLAIN SELECT a.av || '!', b.bv, c.cv
            FROM a LEFT JOIN b ON a.id=b.id LEFT JOIN c ON b.id=c.id;
            """).Should().Contain("OpenJoinCursor").And.Contain("ProjectRegisters");
    }

    [Test]
    public void RightAndFullJoinsRouteWithEvaluatorOrderAndNullExtension()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE l(id INTEGER, label TEXT);");
        Execute(connection, "CREATE TABLE r(id INTEGER, note TEXT);");
        Execute(connection, "INSERT INTO l VALUES (1, 'l1'), (2, 'l2');");
        Execute(connection, "INSERT INTO r VALUES (2, 'r2'), (3, 'r3');");

        var right = ReadRows(connection, "SELECT l.label, r.note FROM l RIGHT JOIN r ON l.id=r.id;");
        right.Should().HaveCount(2);
        right[0].Should().Equal(SqlValue.Text("l2"), SqlValue.Text("r2"));
        right[1].Should().Equal(SqlValue.Null, SqlValue.Text("r3"));

        var full = ReadRows(connection, "SELECT l.label, r.note FROM l FULL JOIN r ON l.id=r.id;");
        full.Should().HaveCount(3);
        full[0].Should().Equal(SqlValue.Text("l1"), SqlValue.Null);
        full[1].Should().Equal(SqlValue.Text("l2"), SqlValue.Text("r2"));
        full[2].Should().Equal(SqlValue.Null, SqlValue.Text("r3"));

        Opcodes(connection, "EXPLAIN SELECT l.label, r.note FROM l RIGHT JOIN r ON l.id=r.id;")
            .Should().Contain("OpenJoinCursor");
        Opcodes(connection, "EXPLAIN SELECT l.label, r.note FROM l FULL JOIN r ON l.id=r.id;")
            .Should().Contain("OpenJoinCursor");
    }

    [Test]
    public void NaturalJoinCoalescesWhileFullJoinUsingIsRejected()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE l(id INTEGER, tag TEXT);");
        Execute(connection, "CREATE TABLE r(id INTEGER, note TEXT);");
        Execute(connection, "INSERT INTO l VALUES (1, 'l1'), (2, 'l2');");
        Execute(connection, "INSERT INTO r VALUES (2, 'r2'), (3, 'r3');");

        // Turso cannot express coalesced USING output in its full-join planner, so FULL
        // OUTER JOIN ... USING errors (turso-src/core/translate/optimizer/join.rs).
        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "SELECT * FROM l FULL JOIN r USING (id);"))!
            .Message.Should().Contain("FULL OUTER JOIN requires an equality condition");

        var natural = ReadRows(connection, "SELECT id, tag, note FROM l NATURAL LEFT JOIN r;");
        natural.Should().HaveCount(2);
        natural[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("l1"), SqlValue.Null);
        natural[1].Should().Equal(SqlValue.Integer(2), SqlValue.Text("l2"), SqlValue.Text("r2"));

        // Naming the coalesced column explicitly needs the unqualified-column resolver, which the
        // compiled join builder cannot model, so that shape stays on the evaluator.
        AssertEvaluatorOwned(connection, "SELECT id, tag, note FROM l NATURAL LEFT JOIN r;");
    }

    [Test]
    public void ComputedProjectionSortDistinctAndBoundsRunInVdbePhaseOrder()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE users(id INTEGER, name TEXT);");
        Execute(connection, "CREATE TABLE orders(user_id INTEGER, amount INTEGER);");
        Execute(connection, "INSERT INTO users VALUES (1, 'Ada'), (2, 'Bo');");
        Execute(connection, "INSERT INTO orders VALUES (1, 10), (1, 20), (2, 30), (2, 30);");

        var ordered = ReadRows(
            connection,
            """
            SELECT lower(users.name) || ':' || (orders.amount + 1)
            FROM users JOIN orders ON users.id=orders.user_id
            WHERE orders.amount >= 10
            ORDER BY orders.amount DESC
            LIMIT 2 OFFSET 1;
            """);
        ordered.Select(row => row[0]).Should().Equal(SqlValue.Text("bo:31"), SqlValue.Text("ada:21"));
        var orderedOpcodes = Opcodes(
            connection,
            """
            EXPLAIN SELECT lower(users.name) || ':' || (orders.amount + 1)
            FROM users JOIN orders ON users.id=orders.user_id
            WHERE orders.amount >= 10
            ORDER BY orders.amount DESC
            LIMIT 2 OFFSET 1;
            """).ToArray();
        orderedOpcodes.Should().Contain("OpenJoinCursor")
            .And.Contain("OpenSorter")
            .And.Contain("ProjectRegisters")
            .And.Contain("OffsetGate")
            .And.Contain("LimitGate");

        var distinct = ReadRows(
            connection,
            "SELECT DISTINCT lower(users.name) FROM users JOIN orders ON users.id=orders.user_id;");
        distinct.Select(row => row[0]).Should().Equal(SqlValue.Text("ada"), SqlValue.Text("bo"));
        Opcodes(
                connection,
                "EXPLAIN SELECT DISTINCT lower(users.name) FROM users JOIN orders ON users.id=orders.user_id;")
            .Should().Contain("DistinctFilter").And.Contain("ProjectRegisters");
    }

    [Test]
    public void ScalarAndGroupedAggregatesOverJoinUseAggregateOpcodes()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE users(id INTEGER, name TEXT);");
        Execute(connection, "CREATE TABLE orders(user_id INTEGER, amount INTEGER);");
        Execute(connection, "INSERT INTO users VALUES (1, 'ada'), (2, 'bo');");
        Execute(connection, "INSERT INTO orders VALUES (1, 10), (1, 20), (2, 30);");

        ReadRows(
                connection,
                "SELECT count(*), sum(orders.amount) FROM users JOIN orders ON users.id=orders.user_id;")
            .Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(3), SqlValue.Integer(60));

        var grouped = ReadRows(
            connection,
            """
            SELECT users.name, count(*), sum(orders.amount)
            FROM users JOIN orders ON users.id=orders.user_id
            GROUP BY users.name;
            """);
        grouped.Should().HaveCount(2);
        grouped[0].Should().Equal(SqlValue.Text("ada"), SqlValue.Integer(2), SqlValue.Integer(30));
        grouped[1].Should().Equal(SqlValue.Text("bo"), SqlValue.Integer(1), SqlValue.Integer(30));

        Opcodes(
                connection,
                "EXPLAIN SELECT count(*), sum(orders.amount) FROM users JOIN orders ON users.id=orders.user_id;")
            .Should().Contain("OpenJoinCursor").And.Contain("AggStep").And.Contain("AggFinalize");
        Opcodes(
                connection,
                """
                EXPLAIN SELECT users.name, count(*) FROM users
                JOIN orders ON users.id=orders.user_id GROUP BY users.name;
                """)
            .Should().Contain("OpenJoinCursor").And.Contain("OpenSorter").And.Contain("SameGroup");
    }

    [Test]
    public void QualifiedRowidsSurviveJoinAndOuterNullExtension()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE l(value TEXT);");
        Execute(connection, "CREATE TABLE r(lid INTEGER, note TEXT);");
        Execute(connection, "INSERT INTO l VALUES ('a'), ('b');");
        Execute(connection, "INSERT INTO r VALUES (1, 'r1');");

        var rows = ReadRows(
            connection,
            "SELECT l.rowid, r.rowid, l.value, r.note FROM l LEFT JOIN r ON l.rowid=r.lid;");
        rows.Should().HaveCount(2);
        rows[0].Should().Equal(
            SqlValue.Integer(1),
            SqlValue.Integer(1),
            SqlValue.Text("a"),
            SqlValue.Text("r1"));
        rows[1].Should().Equal(SqlValue.Integer(2), SqlValue.Null, SqlValue.Text("b"), SqlValue.Null);
        Opcodes(
                connection,
                "EXPLAIN SELECT l.rowid, r.rowid FROM l LEFT JOIN r ON l.rowid=r.lid;")
            .Should().Contain("OpenJoinCursor").And.Contain("ProjectRegisters");
    }

    [Test]
    public void NWayCrossJoinRoutesAndPreservesNestedLoopOrder()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE a(x INTEGER);");
        Execute(connection, "CREATE TABLE b(y INTEGER);");
        Execute(connection, "CREATE TABLE c(z INTEGER);");
        Execute(connection, "INSERT INTO a VALUES (1), (2);");
        Execute(connection, "INSERT INTO b VALUES (10), (20);");
        Execute(connection, "INSERT INTO c VALUES (100), (200);");

        var rows = ReadRows(connection, "SELECT a.x, b.y, c.z FROM a CROSS JOIN b CROSS JOIN c;");
        rows.Select(row => (row[0].AsInteger(), row[1].AsInteger(), row[2].AsInteger()))
            .Should().Equal(
                (1, 10, 100),
                (1, 10, 200),
                (1, 20, 100),
                (1, 20, 200),
                (2, 10, 100),
                (2, 10, 200),
                (2, 20, 100),
                (2, 20, 200));
        Opcodes(connection, "EXPLAIN SELECT a.x, b.y, c.z FROM a CROSS JOIN b CROSS JOIN c;")
            .Should().Contain("OpenJoinCursor");
        ReadRows(
                connection,
                "SELECT a.x, b.y, c.z FROM a CROSS JOIN b CROSS JOIN c LIMIT -1;")
            .Should().HaveCount(8);
        Opcodes(
                connection,
                "EXPLAIN SELECT a.x, b.y, c.z FROM a CROSS JOIN b CROSS JOIN c LIMIT -1;")
            .Should().Contain("OpenJoinCursor").And.NotContain("LimitGate");
    }

    [Test]
    public void ParametersRebindAcrossResetOnCompiledJoin()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE l(id INTEGER);");
        Execute(connection, "CREATE TABLE r(id INTEGER, amount INTEGER);");
        Execute(connection, "INSERT INTO l VALUES (1), (2);");
        Execute(connection, "INSERT INTO r VALUES (1, 10), (2, 20);");
        using var statement = connection.Prepare(
            """
            SELECT l.id + ?1, r.amount
            FROM l JOIN r ON l.id=r.id
            WHERE r.amount >= ?2
            ORDER BY r.amount
            LIMIT ?3 OFFSET ?4;
            """);

        statement.Bind(1, SqlValue.Integer(100));
        statement.Bind(2, SqlValue.Integer(0));
        statement.Bind(3, SqlValue.Integer(1));
        statement.Bind(4, SqlValue.Integer(1));
        Drain(statement).Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(102), SqlValue.Integer(20));

        statement.Reset();
        statement.Bind(1, SqlValue.Integer(200));
        statement.Bind(2, SqlValue.Integer(15));
        statement.Bind(3, SqlValue.Integer(2));
        statement.Bind(4, SqlValue.Integer(0));
        Drain(statement).Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(202), SqlValue.Integer(20));
    }

    [Test]
    public void JoinCallbacksKeepEvaluatorOrderByRemainingOnFallback()
    {
        var events = new List<string>();
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction("on_ab", 2, values =>
        {
            events.Add($"ab:{values[0].AsInteger()}:{values[1].AsInteger()}");
            return SqlValue.Integer(values[0] == values[1] ? 1 : 0);
        });
        database.RegisterScalarFunction("on_bc", 2, values =>
        {
            events.Add($"bc:{values[0].AsInteger()}:{values[1].AsInteger()}");
            return SqlValue.Integer(values[0] == values[1] ? 1 : 0);
        });
        database.RegisterScalarFunction("where_mark", 1, values =>
        {
            events.Add($"where:{values[0].AsInteger()}");
            return SqlValue.Integer(1);
        });
        database.RegisterScalarFunction("project_mark", 1, values =>
        {
            events.Add($"project:{values[0].AsInteger()}");
            return values[0];
        });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE a(id INTEGER);");
        Execute(connection, "CREATE TABLE b(id INTEGER);");
        Execute(connection, "CREATE TABLE c(id INTEGER);");
        Execute(connection, "INSERT INTO a VALUES (1), (2);");
        Execute(connection, "INSERT INTO b VALUES (1), (2);");
        Execute(connection, "INSERT INTO c VALUES (2);");

        ReadRows(
                connection,
                """
                SELECT project_mark(a.id)
                FROM a JOIN b ON on_ab(a.id,b.id)
                JOIN c ON on_bc(b.id,c.id)
                WHERE where_mark(a.id);
                """)
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(2));
        events.Should().Equal(
            "ab:1:1",
            "ab:1:2",
            "ab:2:1",
            "ab:2:2",
            "bc:1:2",
            "bc:2:2",
            "where:2",
            "project:2");
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(
            connection,
            """
            EXPLAIN SELECT project_mark(a.id)
            FROM a JOIN b ON on_ab(a.id,b.id)
            JOIN c ON on_bc(b.id,c.id)
            WHERE where_mark(a.id);
            """));
        ReadRows(
                connection,
                """
                EXPLAIN QUERY PLAN SELECT project_mark(a.id)
                FROM a JOIN b ON on_ab(a.id,b.id)
                JOIN c ON on_bc(b.id,c.id)
                WHERE where_mark(a.id);
                """)
            .Should().ContainSingle().Which[3]
            .Should().Be(SqlValue.Text("MANAGED EVALUATOR FALLBACK"));
    }

    [Test]
    public void ProjectionCallbackRoutesWhenJoinAndFilterAreOrderSafe()
    {
        var calls = new List<long>();
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction("project_mark", 1, values =>
        {
            calls.Add(values[0].AsInteger());
            return values[0];
        });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE l(id INTEGER);");
        Execute(connection, "CREATE TABLE r(id INTEGER);");
        Execute(connection, "INSERT INTO l VALUES (1), (2);");
        Execute(connection, "INSERT INTO r VALUES (1), (2);");

        ReadRows(
                connection,
                "SELECT project_mark(l.id) FROM l JOIN r ON l.id=r.id WHERE r.id >= 1;")
            .Select(row => row[0]).Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));
        calls.Should().Equal(1, 2);
        Opcodes(
                connection,
                "EXPLAIN SELECT project_mark(l.id) FROM l JOIN r ON l.id=r.id WHERE r.id >= 1;")
            .Should().Contain("OpenJoinCursor").And.Contain("ProjectRegisters");
    }

    [Test]
    public void RoutedAndFallbackProjectionErrorsKeepTheSameCallbackOrder()
    {
        var routedCalls = new List<long>();
        var fallbackCalls = new List<long>();
        using var routed = OpenProjectionFailureDatabase(routedCalls);
        using var fallback = OpenProjectionFailureDatabase(fallbackCalls);

        var routedError = Assert.Throws<EmbeddedSqlException>(() => ReadRows(
            routed,
            "SELECT fail_on_two(l.id) FROM l JOIN r ON l.id=r.id;"))!;
        var fallbackError = Assert.Throws<EmbeddedSqlException>(() => ReadRows(
            fallback,
            "SELECT fail_on_two(l.id) FROM l JOIN r ON l.id + 0=r.id;"))!;

        routedError.Message.Should().Be(fallbackError.Message).And.Be("projection boom");
        routedCalls.Should().Equal(1, 2);
        fallbackCalls.Should().Equal(routedCalls);
        ReadRows(
                routed,
                "EXPLAIN QUERY PLAN SELECT fail_on_two(l.id) FROM l JOIN r ON l.id=r.id;")
            .Should().ContainSingle().Which[3]
            .Should().Be(SqlValue.Text("MANAGED COMPILED VDBE"));
        ReadRows(
                fallback,
                "EXPLAIN QUERY PLAN SELECT fail_on_two(l.id) FROM l JOIN r ON l.id + 0=r.id;")
            .Should().ContainSingle().Which[3]
            .Should().Be(SqlValue.Text("MANAGED EVALUATOR FALLBACK"));
    }

    [Test]
    public void UnsafeOrderCallbackAndCancelableExecutionStayOnEvaluator()
    {
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction("order_mark", 1, values => values[0]);
        database.RegisterCollation("callback_collation", string.CompareOrdinal);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE l(id INTEGER);");
        Execute(connection, "CREATE TABLE r(id INTEGER);");
        Execute(connection, "INSERT INTO l VALUES (1);");
        Execute(connection, "INSERT INTO r VALUES (1);");

        ReadRows(
                connection,
                "EXPLAIN QUERY PLAN SELECT l.id FROM l JOIN r ON l.id=r.id ORDER BY order_mark(r.id);")
            .Should().ContainSingle().Which[3]
            .Should().Be(SqlValue.Text("MANAGED EVALUATOR FALLBACK"));
        ReadRows(
                connection,
                """
                EXPLAIN QUERY PLAN SELECT l.id FROM l
                JOIN r ON l.id=r.id COLLATE callback_collation;
                """)
            .Should().ContainSingle().Which[3]
            .Should().Be(SqlValue.Text("MANAGED EVALUATOR FALLBACK"));
        ReadRows(
                connection,
                """
                EXPLAIN QUERY PLAN SELECT DISTINCT CAST(l.id AS TEXT) COLLATE callback_collation
                FROM l JOIN r ON l.id=r.id;
                """)
            .Should().ContainSingle().Which[3]
            .Should().Be(SqlValue.Text("MANAGED EVALUATOR FALLBACK"));

        using var cancelable = new CancellationTokenSource();
        using var plan = connection.Prepare(
            "EXPLAIN QUERY PLAN SELECT l.id + 1 FROM l JOIN r ON l.id=r.id;");
        plan.Step(cancelable.Token).Should().Be(StatementStepResult.Row);
        plan.GetValue(3).Should().Be(SqlValue.Text("MANAGED EVALUATOR FALLBACK"));
        plan.Step(cancelable.Token).Should().Be(StatementStepResult.Done);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        using var query = connection.Prepare("SELECT l.id + 1 FROM l JOIN r ON l.id=r.id;");
        Assert.Throws<OperationCanceledException>(() => query.Step(canceled.Token));
        query.Reset();
        query.Step().Should().Be(StatementStepResult.Row);
        query.GetValue(0).Should().Be(SqlValue.Integer(2));
    }

    [Test]
    public void NWayOuterUsingChainsAreRejectedLikeTurso()
    {
        string[] setup =
        [
            "CREATE TABLE a(id INTEGER, av TEXT);",
            "CREATE TABLE b(id INTEGER, bv TEXT);",
            "CREATE TABLE c(id INTEGER, cv TEXT);",
            "INSERT INTO a VALUES (1, 'a1'), (2, 'a2'), (4, 'a4');",
            "INSERT INTO b VALUES (2, 'b2'), (3, 'b3'), (4, 'b4'), (6, 'b6');",
            "INSERT INTO c VALUES (2, 'c2'), (3, 'c3'), (5, 'c5');",
        ];
        using var connection = OpenManaged(setup);

        // SQLite executes FULL OUTER JOIN ... USING chains, but Turso cannot plan them, so
        // Ahtola mirrors Turso's rejection instead of matching SQLite.
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, """
                SELECT id, av, bv, cv
                FROM a FULL JOIN b USING (id)
                FULL JOIN c USING (id)
                ORDER BY id;
                """))!
            .Message.Should().Contain("FULL OUTER JOIN chaining is not yet supported");
        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "SELECT a.*, b.* FROM a FULL JOIN b USING (id);"))!
            .Message.Should().Contain("FULL OUTER JOIN requires an equality condition");
    }

    [Test]
    public void JoinCollationAffinityAndRowidResultsMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE l(k INTEGER, label TEXT);",
            "CREATE TABLE r(k TEXT, label TEXT);",
            "INSERT INTO l VALUES (1, 'one'), (2, 'two');",
            "INSERT INTO r VALUES ('01', 'leading'), ('2', 'TWO');",
        ];

        AssertMatchesSqlite(
            setup,
            """
            SELECT l.rowid, r.rowid, l.label, r.label
            FROM l JOIN r ON l.k=r.k
            ORDER BY l.rowid, r.rowid;
            """);
        AssertMatchesSqlite(
            setup,
            "SELECT l.label, r.label FROM l JOIN r ON l.k=r.k;");
        AssertMatchesSqlite(
            setup,
            """
            SELECT l.label, r.label
            FROM l JOIN r ON l.label=r.label COLLATE NOCASE
            ORDER BY l.rowid;
            """);
    }

    [Test]
    public void SharedComparisonAffinityMatchesSqliteOutsideJoinRouting()
    {
        string[] setup =
        [
            "CREATE TABLE t(value INTEGER);",
            "INSERT INTO t VALUES (1), (2);",
        ];

        AssertMatchesSqlite(setup, "SELECT value FROM t WHERE value='01';");
        using var connection = OpenManaged(setup);
        Opcodes(connection, "EXPLAIN SELECT value FROM t WHERE value='01';")
            .Should().Contain("JumpIfNotTrue");
    }

    private static void AssertEvaluatorOwned(EmbeddedConnection connection, string sql)
    {
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + sql))!
            .Message.Should().Contain("EXPLAIN is only supported");
        ReadRows(connection, "EXPLAIN QUERY PLAN " + sql)[0][3]
            .Should().Be(SqlValue.Text("MANAGED EVALUATOR FALLBACK"));
    }

    private static void AssertMatchesSqlite(IReadOnlyList<string> setup, string sql)
    {
        using var managed = OpenManaged(setup);
        var managedRows = ReadRows(managed, sql);
        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in setup)
        {
            using var command = sqlite.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }

        using var query = sqlite.CreateCommand();
        query.CommandText = sql;
        using var reader = query.ExecuteReader();
        var sqliteRows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var index = 0; index < row.Length; index++)
                row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            sqliteRows.Add(row);
        }

        managedRows.Should().HaveCount(sqliteRows.Count);
        for (var row = 0; row < sqliteRows.Count; row++)
        {
            managedRows[row].Should().HaveCount(sqliteRows[row].Length);
            for (var column = 0; column < sqliteRows[row].Length; column++)
                CellShouldMatch(managedRows[row][column], sqliteRows[row][column], row, column);
        }
    }

    private static EmbeddedConnection OpenManaged(IReadOnlyList<string> setup)
    {
        var connection = new EmbeddedDatabase().Connect();
        foreach (var sql in setup)
            Execute(connection, sql);
        return connection;
    }

    private static EmbeddedConnection OpenProjectionFailureDatabase(List<long> calls)
    {
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction("fail_on_two", 1, values =>
        {
            var value = values[0].AsInteger();
            calls.Add(value);
            if (value == 2)
                throw new EmbeddedSqlException("projection boom");
            return values[0];
        });
        var connection = database.Connect();
        Execute(connection, "CREATE TABLE l(id INTEGER);");
        Execute(connection, "CREATE TABLE r(id INTEGER);");
        Execute(connection, "INSERT INTO l VALUES (1), (2);");
        Execute(connection, "INSERT INTO r VALUES (1), (2);");
        return connection;
    }

    private static void CellShouldMatch(SqlValue managed, object? sqlite, int row, int column)
    {
        switch (sqlite)
        {
            case null:
                managed.Should().Be(SqlValue.Null, $"at row {row}, column {column}");
                break;
            case long integer:
                managed.Should().Be(SqlValue.Integer(integer), $"at row {row}, column {column}");
                break;
            case double real:
                managed.Should().Be(SqlValue.Real(real), $"at row {row}, column {column}");
                break;
            case string text:
                managed.Should().Be(SqlValue.Text(text), $"at row {row}, column {column}");
                break;
            case byte[] blob:
                managed.Kind.Should().Be(SqlValueKind.Blob);
                managed.AsBlob().ToArray().Should().Equal(blob);
                break;
            default:
                throw new InvalidOperationException($"Unsupported SQLite value type {sqlite.GetType()}.");
        }
    }

    private static IEnumerable<string> Opcodes(EmbeddedConnection connection, string sql)
        => ReadRows(connection, sql).Select(row => row[1].AsText());

    private static List<SqlValue[]> Drain(EmbeddedStatement statement)
    {
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var row = new SqlValue[statement.GetColumnCount()];
            for (var ordinal = 0; ordinal < row.Length; ordinal++)
                row[ordinal] = statement.GetValue(ordinal);
            rows.Add(row);
        }

        return rows;
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static List<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var values = new SqlValue[statement.GetColumnCount()];
            for (var ordinal = 0; ordinal < values.Length; ordinal++)
                values[ordinal] = statement.GetValue(ordinal);
            rows.Add(values);
        }

        return rows;
    }
}
