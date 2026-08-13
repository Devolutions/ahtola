using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class WindowFunctionSemanticsTests
{
    private static readonly string[] Setup =
    [
        "CREATE TABLE t(id INTEGER, grp TEXT, ord REAL, label TEXT, value INTEGER);",
        "INSERT INTO t VALUES "
            + "(1, 'a', NULL, 'Beta', 10), "
            + "(2, 'a', 1, 'alpha', 20), "
            + "(3, 'a', 1, 'ALPHA', 30), "
            + "(4, 'a', 2.5, NULL, 40), "
            + "(5, 'b', 1, 'gamma', NULL), "
            + "(6, 'b', 2, 'Gamma', 60);",
    ];

    [Test]
    public void DefaultFrameRankingPeersNullOrderingAndCollationMatchSqlite()
    {
        AssertMatchesSqlite(
            Setup,
            """
            SELECT id,
                   sum(value) OVER (
                       PARTITION BY grp
                       ORDER BY label COLLATE NOCASE ASC NULLS LAST),
                   row_number() OVER (
                       PARTITION BY grp
                       ORDER BY label COLLATE NOCASE ASC NULLS LAST),
                   rank() OVER (
                       PARTITION BY grp
                       ORDER BY label COLLATE NOCASE ASC NULLS LAST),
                   dense_rank() OVER (
                       PARTITION BY grp
                       ORDER BY label COLLATE NOCASE ASC NULLS LAST),
                   percent_rank() OVER (
                       PARTITION BY grp
                       ORDER BY label COLLATE NOCASE ASC NULLS LAST),
                   cume_dist() OVER (
                       PARTITION BY grp
                       ORDER BY label COLLATE NOCASE ASC NULLS LAST),
                   ntile(3) OVER (
                       PARTITION BY grp
                       ORDER BY label COLLATE NOCASE ASC NULLS LAST),
                   ntile(id) OVER (
                       PARTITION BY grp
                       ORDER BY label COLLATE NOCASE ASC NULLS LAST),
                   count(*) OVER (
                       PARTITION BY label COLLATE NOCASE)
            FROM t
            ORDER BY grp, id;
            """);
    }

    [TestCase(
        "sum(value) OVER (PARTITION BY grp ORDER BY ord NULLS FIRST "
        + "ROWS BETWEEN 1 PRECEDING AND 1 FOLLOWING EXCLUDE CURRENT ROW)")]
    [TestCase(
        "count(*) OVER (PARTITION BY grp ORDER BY ord NULLS FIRST "
        + "GROUPS BETWEEN 1 PRECEDING AND 1 FOLLOWING EXCLUDE GROUP)")]
    [TestCase(
        "group_concat(id, '|') OVER (PARTITION BY grp ORDER BY ord NULLS FIRST "
        + "RANGE BETWEEN 1.5 PRECEDING AND 0.5 FOLLOWING EXCLUDE TIES)")]
    [TestCase(
        "sum(value) OVER (PARTITION BY grp ORDER BY ord NULLS FIRST "
        + "GROUPS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING EXCLUDE NO OTHERS)")]
    [TestCase(
        "sum(value) OVER (PARTITION BY grp ORDER BY ord NULLS FIRST "
        + "ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW EXCLUDE GROUP)")]
    [TestCase(
        "sum(value) OVER (PARTITION BY grp ORDER BY ord DESC NULLS LAST "
        + "RANGE BETWEEN 0.5 PRECEDING AND 1.5 FOLLOWING)")]
    [TestCase(
        "sum(value) OVER (PARTITION BY grp ORDER BY label COLLATE NOCASE NULLS LAST "
        + "RANGE BETWEEN 1 PRECEDING AND 1 FOLLOWING)")]
    public void RowsRangeGroupsAndExclusionsMatchSqlite(string expression)
    {
        AssertMatchesSqlite(Setup, $"SELECT id, {expression} FROM t ORDER BY grp, id;");
    }

    [Test]
    public void EdgeFrameBoundaryMatrixMatchesSqlite()
    {
        string[] frames =
        [
            "ROWS BETWEEN 2 PRECEDING AND 1 PRECEDING",
            "ROWS BETWEEN 1 FOLLOWING AND 2 FOLLOWING",
            "ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING",
            "GROUPS BETWEEN 2 PRECEDING AND 1 PRECEDING",
            "GROUPS BETWEEN 1 FOLLOWING AND 2 FOLLOWING",
            "GROUPS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING",
            "RANGE BETWEEN 1.5 PRECEDING AND 0.5 PRECEDING",
            "RANGE BETWEEN 0.5 FOLLOWING AND 1.5 FOLLOWING",
            "RANGE BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING",
        ];

        foreach (var frame in frames)
        {
            AssertMatchesSqlite(
                Setup,
                $"SELECT id, group_concat(id, '|') OVER ("
                + $"PARTITION BY grp ORDER BY ord ASC NULLS LAST {frame}) "
                + "FROM t ORDER BY grp, id;");
        }
    }

    [Test]
    public void NavigationAndFrameValueFunctionsMatchSqlite()
    {
        AssertMatchesSqlite(
            Setup,
            """
            SELECT id,
                   lag(value) OVER ordered,
                   lag(value, -1, -99) OVER ordered,
                   lag(value, 1.9, -99) OVER ordered,
                   lead(value, '1.5', -99) OVER ordered,
                   lead(label, 2, 'missing') OVER ordered,
                   first_value(value) OVER framed,
                   last_value(value) OVER framed,
                   nth_value(value, 2) OVER framed
            FROM t
            WINDOW ordered AS (
                       PARTITION BY grp
                       ORDER BY ord ASC NULLS LAST, id),
                   framed AS (
                       ordered ROWS BETWEEN 1 PRECEDING AND 1 FOLLOWING EXCLUDE TIES)
            ORDER BY grp, id;
            """);
    }

    [Test]
    public void NegativeLagOffsetsMatchSqliteNavigationSemantics()
    {
        string[] setup =
        [
            "CREATE TABLE offsets(x INTEGER, offset_value INTEGER);",
            "INSERT INTO offsets VALUES (1, -1), (2, 2), (3, -2), (4, 1), (5, -3);",
        ];

        AssertMatchesSqlite(
            setup,
            """
            SELECT x,
                   lag(x, -3) OVER (ORDER BY x),
                   lead(x, -3) OVER (ORDER BY x),
                   lag(x, offset_value) OVER (ORDER BY x),
                   lead(x, offset_value) OVER (ORDER BY x)
            FROM offsets;
            """);
    }

    [Test]
    public void NamedWindowChainingFilteringAndComposedResultsMatchSqlite()
    {
        AssertMatchesSqlite(
            Setup,
            """
            SELECT id,
                   coalesce(
                       sum(value) FILTER (WHERE value >= 20) OVER running,
                       0) + row_number() OVER ordered AS composite,
                   lag(value, 1, -1) OVER ordered,
                   last_value(label) OVER framed
            FROM t
            WINDOW base AS (PARTITION BY grp),
                   ordered AS (base ORDER BY ord ASC NULLS LAST, id),
                   running AS (
                       ordered ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW),
                   framed AS (
                       ordered GROUPS BETWEEN CURRENT ROW AND 1 FOLLOWING EXCLUDE TIES)
            ORDER BY grp, id;
            """);
    }

    [Test]
    public void ParameterizedFramesAndFunctionArgumentsMatchSqliteAcrossReset()
    {
        const string query =
            """
            SELECT id,
                   sum(value) OVER (
                       PARTITION BY grp ORDER BY id
                       ROWS BETWEEN ?1 PRECEDING AND ?2 FOLLOWING),
                   nth_value(value, ?3) OVER (
                       PARTITION BY grp ORDER BY id
                       ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW),
                   ntile(?4) OVER (PARTITION BY grp ORDER BY id),
                   lag(value, ?5, -1) OVER (PARTITION BY grp ORDER BY id)
            FROM t
            ORDER BY grp, id;
            """;

        AssertMatchesSqlite(Setup, query, 1L, 1L, 2L, 3L, 1L);
        AssertMatchesSqlite(Setup, query, 0L, 2L, 1L, 2L, -1L);
        AssertMatchesSqlite(Setup, query, "1", 1.0, "2", 2.9, -1.0);
    }

    [Test]
    public void NtileUsesSqliteIntegerPrefixCoercion()
    {
        string[] setup =
        [
            "CREATE TABLE valueset(value INTEGER);",
            "INSERT INTO valueset VALUES (1), (2), (3), (4), (5);",
        ];

        foreach (var bucketCount in new[] { "'2abc'", "'1e2'", "X'33'" })
        {
            AssertMatchesSqlite(
                setup,
                $"SELECT value, ntile({bucketCount}) OVER (ORDER BY value) FROM valueset;");
        }
    }

    [Test]
    public void InvalidParameterizedFrameReportsTheSqliteError()
    {
        const string query =
            "SELECT sum(value) OVER (ORDER BY id ROWS BETWEEN ?1 PRECEDING AND CURRENT ROW) FROM t;";

        var managed = () => RunManaged(Setup, query, (object?)null);
        managed.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*frame starting offset must be a non-negative integer*");

        var sqlite = () => RunSqlite(Setup, query, (object?)null);
        sqlite.Should().Throw<MsData.SqliteException>()
            .WithMessage("*frame starting offset must be a non-negative integer*");
    }

    [Test]
    public void RuntimeWindowArgumentsAreNotEvaluatedForEmptyInput()
    {
        string[] setup = ["CREATE TABLE empty(id INTEGER, value INTEGER);"];
        string[] queries =
        [
            "SELECT sum(value) OVER (ORDER BY id ROWS -1 PRECEDING) FROM empty;",
            "SELECT nth_value(value, 0) OVER (ORDER BY id) FROM empty;",
            "SELECT ntile(0) OVER (ORDER BY id) FROM empty;",
        ];

        foreach (var query in queries)
            AssertMatchesSqlite(setup, query);
    }

    [Test]
    public void RankingAndNavigationFunctionsIgnoreRuntimeFrameOffsets()
    {
        AssertMatchesSqlite(
            Setup,
            """
            SELECT id,
                   row_number() OVER (
                       PARTITION BY grp ORDER BY id ROWS -1 PRECEDING),
                   lag(value) OVER (
                       PARTITION BY grp ORDER BY id ROWS abs(1) PRECEDING)
            FROM t
            ORDER BY grp, id;
            """);
    }

    [Test]
    public void NamedWindowDeclarationOrderMatchesSqlite()
    {
        AssertMatchesSqlite(
            Setup,
            """
            SELECT id,
                   group_concat(id) OVER duplicate_name
            FROM t
            WINDOW duplicate_name AS (ORDER BY id),
                   duplicate_name AS (ORDER BY ord)
            ORDER BY id;
            """);
        AssertMatchesSqlite(
            Setup,
            """
            SELECT id, sum(value) OVER forward_base
            FROM t
            WINDOW forward_base AS (later),
                   later AS (ORDER BY id)
            ORDER BY id;
            """);
    }

    [TestCase(
        "SELECT sum(value) OVER (ORDER BY id ROWS BETWEEN -1 PRECEDING AND CURRENT ROW) FROM t;",
        "frame starting offset must be a non-negative integer")]
    [TestCase(
        "SELECT sum(value) OVER (ORDER BY id RANGE BETWEEN 'bad' PRECEDING AND CURRENT ROW) FROM t;",
        "frame starting offset must be a non-negative number")]
    [TestCase(
        "SELECT sum(value) OVER (ORDER BY id GROUPS BETWEEN 1.5 PRECEDING AND CURRENT ROW) FROM t;",
        "frame starting offset must be a non-negative integer")]
    [TestCase(
        "SELECT sum(value) OVER (ORDER BY id ROWS BETWEEN id PRECEDING AND CURRENT ROW) FROM t;",
        "frame starting offset must be a non-negative integer")]
    [TestCase(
        "SELECT sum(value) OVER (ORDER BY id ROWS BETWEEN abs(1) PRECEDING AND CURRENT ROW) FROM t;",
        "frame starting offset must be a non-negative integer")]
    [TestCase(
        "SELECT sum(value) OVER (ORDER BY id ROWS BETWEEN CURRENT ROW AND -1 FOLLOWING) FROM t;",
        "frame ending offset must be a non-negative integer")]
    [TestCase(
        "SELECT sum(value) OVER (ORDER BY id, value RANGE BETWEEN 1 PRECEDING AND CURRENT ROW) FROM t;",
        "requires one ORDER BY expression")]
    [TestCase(
        "SELECT nth_value(value, 0) OVER (ORDER BY id) FROM t;",
        "second argument to nth_value must be a positive integer")]
    [TestCase(
        "SELECT ntile(0) OVER (ORDER BY id) FROM t;",
        "argument of ntile must be a positive integer")]
    [TestCase(
        "SELECT row_number() FILTER (WHERE value > 0) OVER (ORDER BY id) FROM t;",
        "FILTER clause may only be used with aggregate window functions")]
    [TestCase(
        "SELECT sum(DISTINCT value) OVER (ORDER BY id) FROM t;",
        "DISTINCT is not supported for window functions")]
    [TestCase(
        "SELECT sum(value) OVER child FROM t "
        + "WINDOW parent AS (PARTITION BY grp), child AS (parent PARTITION BY id);",
        "cannot override PARTITION clause of window: parent")]
    [TestCase(
        "SELECT sum(value) OVER (framed) FROM t "
        + "WINDOW framed AS (ORDER BY id ROWS UNBOUNDED PRECEDING);",
        "cannot override frame specification of window: framed")]
    [TestCase(
        "SELECT sum(value) OVER missing FROM t;",
        "no such window: missing")]
    public void WindowErrorsMatchSqlite(string query, string message)
    {
        var managed = () => RunManaged(Setup, query);
        managed.Should().Throw<EmbeddedSqlException>().WithMessage($"*{message}*");

        var sqlite = () => RunSqlite(Setup, query);
        sqlite.Should().Throw<MsData.SqliteException>().WithMessage($"*{message}*");
    }

    [Test]
    public void CollationResolutionPrecedesRuntimeFrameAndValueErrors()
    {
        const string query =
            "SELECT nth_value(value, 0) OVER ("
            + "ORDER BY label COLLATE missing "
            + "ROWS BETWEEN -1 PRECEDING AND CURRENT ROW) FROM t;";

        var managed = () => RunManaged(Setup, query);
        managed.Should().Throw<EmbeddedSqlException>().WithMessage("*no such collation sequence: missing*");

        var sqlite = () => RunSqlite(Setup, query);
        sqlite.Should().Throw<MsData.SqliteException>().WithMessage("*no such collation sequence: missing*");
    }

    [Test]
    public void FilterArgumentStepAndFinalizeCallbacksRunInRowOrder()
    {
        var events = new List<string>();
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction("record_filter", 1, values =>
        {
            events.Add($"filter:{values[0].AsInteger()}");
            return SqlValue.Integer(values[0].AsInteger() < 20 ? 1 : 0);
        });
        database.RegisterScalarFunction("record_argument", 1, values =>
        {
            if (values[0].AsInteger() == 20)
                throw new InvalidOperationException("filtered arguments must not run");
            events.Add($"argument:{values[0].AsInteger()}");
            return values[0];
        });
        database.RegisterAggregateFunction(
            "record_sum",
            1,
            SqlValue.Integer(0),
            (aggregate, values) =>
            {
                events.Add($"step:{values[0].AsInteger()}");
                return SqlValue.Integer(aggregate.AsInteger() + values[0].AsInteger());
            },
            aggregate =>
            {
                events.Add($"finalize:{aggregate.AsInteger()}");
                return aggregate;
            });

        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE input(id INTEGER, value INTEGER);");
        Execute(connection, "INSERT INTO input VALUES (1, 10), (2, 20);");

        ReadRows(
                connection,
                """
                SELECT record_sum(record_argument(value))
                       FILTER (WHERE record_filter(value))
                       OVER (ORDER BY id ROWS UNBOUNDED PRECEDING)
                FROM input
                ORDER BY id;
                """)
            .Select(row => row[0])
            .Should().Equal(SqlValue.Integer(10), SqlValue.Integer(10));
        events.Should().Equal(
            "filter:10",
            "argument:10",
            "filter:20",
            "step:10",
            "finalize:10",
            "step:10",
            "finalize:10");
    }

    [Test]
    public void WindowArgumentsAreMaterializedOncePerInputRow()
    {
        var counter = 0L;
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "next_value",
            0,
            _ => SqlValue.Integer(++counter));
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE input(id INTEGER);");
        Execute(connection, "INSERT INTO input VALUES (1), (2), (3);");

        ReadRows(
                connection,
                "SELECT first_value(next_value()) OVER (ORDER BY id) FROM input ORDER BY id;")
            .Select(row => row[0])
            .Should().Equal(SqlValue.Integer(1), SqlValue.Integer(1), SqlValue.Integer(1));
        counter.Should().Be(3);

        counter = 0;
        ReadRows(
                connection,
                "SELECT lag(next_value(), 1, -1) OVER (ORDER BY id) FROM input ORDER BY id;")
            .Select(row => row[0])
            .Should().Equal(SqlValue.Integer(-1), SqlValue.Integer(1), SqlValue.Integer(2));
        counter.Should().Be(3);

        counter = 0;
        ReadRows(
                connection,
                "SELECT sum(next_value()) OVER ("
                + "ORDER BY id ROWS UNBOUNDED PRECEDING) FROM input ORDER BY id;")
            .Select(row => row[0])
            .Should().Equal(SqlValue.Integer(1), SqlValue.Integer(3), SqlValue.Integer(6));
        counter.Should().Be(3);
    }

    [Test]
    public void CompiledAndFallbackRoutingRemainTruthful()
    {
        using var connection = OpenManaged(Setup);
        const string compiled =
            """
            SELECT grp, id, sum(value) OVER running
            FROM t
            WINDOW running AS (
                PARTITION BY grp ORDER BY id
                ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)
            ORDER BY grp, id;
            """;
        const string buffered =
            """
            SELECT id, rank() OVER (
                PARTITION BY grp ORDER BY ord
                GROUPS BETWEEN 1 PRECEDING AND CURRENT ROW EXCLUDE TIES)
            FROM t
            ORDER BY grp, id;
            """;
        const string fallback =
            """
            SELECT DISTINCT rank() OVER (PARTITION BY grp ORDER BY ord)
            FROM t
            ORDER BY 1;
            """;

        Opcodes(ReadRows(connection, "EXPLAIN " + compiled))
            .Should().Contain("SorterSort").And.Contain("AggStep").And.Contain("AggFinalize");
        ReadRows(connection, "EXPLAIN QUERY PLAN " + compiled)[0][3].AsText()
            .Should().Be("MANAGED COMPILED VDBE");

        // The peer-relative GROUPS frame with EXCLUDE is not a streaming fold, so it lowers onto the
        // buffered-window opcode family instead of the running accumulator.
        Opcodes(ReadRows(connection, "EXPLAIN " + buffered))
            .Should().Contain("OpenWindowBuffer").And.Contain("WindowBufferCompute");
        ReadRows(connection, "EXPLAIN QUERY PLAN " + buffered)[0][3].AsText()
            .Should().Be("MANAGED COMPILED VDBE");
        AssertMatchesSqlite(Setup, buffered);

        var explainFallback = () => ReadRows(connection, "EXPLAIN " + fallback);
        explainFallback.Should().Throw<EmbeddedSqlException>();
        ReadRows(connection, "EXPLAIN QUERY PLAN " + fallback)[0][3].AsText()
            .Should().Be("MANAGED EVALUATOR FALLBACK");
        AssertMatchesSqlite(Setup, fallback);
    }

    [Test]
    public void CancellationKeepsEvaluatorWindowStateReusable()
    {
        using var connection = OpenManaged(Setup);
        using var statement = connection.Prepare(
            "SELECT id, rank() OVER (PARTITION BY grp ORDER BY ord) FROM t ORDER BY id;");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var canceled = () => statement.Step(cancellation.Token);
        canceled.Should().Throw<OperationCanceledException>();

        statement.Reset();
        ReadRows(statement).Should().HaveCount(6);
    }

    [Test]
    public void NamedWindowQueryWorksThroughAttachAndDurableReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var main = EmbeddedDatabase.OpenFile("window-main.db", fileSystem))
        using (var connection = main.Connect())
        {
            Execute(connection, "ATTACH DATABASE 'window-aux.db' AS aux;");
            Execute(connection, "CREATE TABLE aux.items(id INTEGER, grp TEXT, value INTEGER);");
            Execute(connection, "INSERT INTO aux.items VALUES (1, 'a', 10), (2, 'a', 20), (3, 'b', 7);");
            ReadRows(
                    connection,
                    """
                    SELECT id,
                           sum(value) OVER running AS running
                    FROM aux.items
                    WINDOW running AS (
                        PARTITION BY grp ORDER BY id
                        ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)
                    ORDER BY id;
                    """)
                .Select(row => (row[0], row[1]))
                .Should().Equal(
                    (SqlValue.Integer(1), SqlValue.Integer(10)),
                    (SqlValue.Integer(2), SqlValue.Integer(30)),
                    (SqlValue.Integer(3), SqlValue.Integer(7)));
        }

        using var reopened = EmbeddedDatabase.OpenFile("window-aux.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadRows(
                reopenedConnection,
                """
                SELECT id,
                       sum(value) OVER running AS running
                FROM items
                WINDOW running AS (
                    PARTITION BY grp ORDER BY id
                    ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)
                ORDER BY id;
                """)
            .Select(row => (row[0], row[1]))
            .Should().Equal(
                (SqlValue.Integer(1), SqlValue.Integer(10)),
                (SqlValue.Integer(2), SqlValue.Integer(30)),
                (SqlValue.Integer(3), SqlValue.Integer(7)));
    }

    [Test]
    public void NamedWindowDependenciesCannotBypassFileSchemaValidation()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("window-schema.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER);");

        using var statement = connection.Prepare(
            """
            CREATE VIEW unsafe_window AS
            SELECT id FROM items
            WINDOW unused AS (
                ORDER BY id ROWS BETWEEN ?1 PRECEDING AND CURRENT ROW);
            """);
        statement.Bind(1, SqlValue.Integer(1));
        var create = () => statement.Step();

        create.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*cannot persist view 'unsafe_window' because it uses a bind parameter*");
    }

    private static void AssertMatchesSqlite(
        IReadOnlyList<string> setup,
        string query,
        params object?[] parameters)
    {
        var managed = RunManaged(setup, query, parameters);
        var sqlite = RunSqlite(setup, query, parameters);

        managed.Should().HaveCount(sqlite.Count);
        for (var row = 0; row < sqlite.Count; row++)
        {
            managed[row].Should().HaveCount(sqlite[row].Length);
            for (var column = 0; column < sqlite[row].Length; column++)
                CellsShouldMatch(managed[row][column], sqlite[row][column]);
        }
    }

    private static List<SqlValue[]> RunManaged(
        IReadOnlyList<string> setup,
        string query,
        params object?[] parameters)
    {
        using var connection = OpenManaged(setup);
        using var statement = connection.Prepare(query);
        for (var index = 0; index < parameters.Length; index++)
            statement.Bind(index + 1, ToSqlValue(parameters[index]));
        return ReadRows(statement);
    }

    private static EmbeddedConnection OpenManaged(IReadOnlyList<string> setup)
    {
        var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);
        return connection;
    }

    private static List<object?[]> RunSqlite(
        IReadOnlyList<string> setup,
        string query,
        params object?[] parameters)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var statement in setup)
        {
            using var setupCommand = connection.CreateCommand();
            setupCommand.CommandText = statement;
            setupCommand.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = query;
        for (var index = 0; index < parameters.Length; index++)
            command.Parameters.AddWithValue($"?{index + 1}", parameters[index] ?? DBNull.Value);

        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var values = new object?[reader.FieldCount];
            for (var column = 0; column < values.Length; column++)
                values[column] = reader.IsDBNull(column) ? null : reader.GetValue(column);
            rows.Add(values);
        }

        return rows;
    }

    private static void CellsShouldMatch(SqlValue managed, object? sqlite)
    {
        switch (sqlite)
        {
            case null:
                managed.Kind.Should().Be(SqlValueKind.Null);
                break;
            case long integer:
                managed.Should().Be(SqlValue.Integer(integer));
                break;
            case double real:
                managed.Kind.Should().Be(SqlValueKind.Real);
                managed.AsReal().Should().BeApproximately(real, 1e-9);
                break;
            case string text:
                managed.Should().Be(SqlValue.Text(text));
                break;
            case byte[] blob:
                managed.Kind.Should().Be(SqlValueKind.Blob);
                managed.AsBlob().ToArray().Should().Equal(blob);
                break;
            default:
                throw new InvalidOperationException($"Unsupported SQLite value type {sqlite.GetType()}.");
        }
    }

    private static SqlValue ToSqlValue(object? value)
        => value switch
        {
            null => SqlValue.Null,
            int integer => SqlValue.Integer(integer),
            long integer => SqlValue.Integer(integer),
            double real => SqlValue.Real(real),
            string text => SqlValue.Text(text),
            _ => throw new InvalidOperationException($"Unsupported parameter type {value.GetType()}."),
        };

    private static IEnumerable<string> Opcodes(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[1].AsText());

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static List<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        return ReadRows(statement);
    }

    private static List<SqlValue[]> ReadRows(EmbeddedStatement statement)
    {
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var values = new SqlValue[statement.ColumnCount];
            for (var ordinal = 0; ordinal < values.Length; ordinal++)
                values[ordinal] = statement.GetValue(ordinal);
            rows.Add(values);
        }

        return rows;
    }
}
