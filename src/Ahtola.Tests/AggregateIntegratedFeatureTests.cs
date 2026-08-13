using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class AggregateIntegratedFeatureTests
{
    [Test]
    public void PartialIndexAutoIncrementStrictAndVacuumPreserveCompiledAggregates()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "aggregate-integrated-features.db";
        const string query =
            "SELECT lower(group_name), count(*), sum((flags << 1) | amount), sum(amount = '010') "
            + "FROM metrics GROUP BY lower(group_name) ORDER BY count(*) DESC;";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(
                connection,
                """
                CREATE TABLE metrics(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    group_name TEXT COLLATE NOCASE,
                    flags INTEGER,
                    amount INTEGER
                ) STRICT;
                """);
            Execute(
                connection,
                "CREATE INDEX metrics_partial "
                    + "ON metrics(((flags << 1) | id) DESC) WHERE (flags & 1) = 1;");
            Execute(
                connection,
                "CREATE INDEX metrics_amount_partial "
                    + "ON metrics(amount DESC) WHERE amount > 0;");
            Execute(
                connection,
                "INSERT INTO metrics(group_name, flags, amount) VALUES "
                    + "('Alpha', 1, 10), ('alpha', 2, 5), ('Beta', 3, 7);");

            AssertRows(ReadRows(connection, query));
            AssertCompiled(connection, query);
            ReadRows(connection, "SELECT seq FROM sqlite_sequence WHERE name = 'metrics';")[0][0]
                .Should().Be(SqlValue.Integer(3));
            ReadRows(
                    connection,
                    "SELECT group_concat(group_name || '!') "
                        + "FROM metrics WHERE amount > 0;")[0][0]
                .Should().Be(SqlValue.Text("Alpha!,Beta!,alpha!"));
            ReadRows(
                    connection,
                    "EXPLAIN QUERY PLAN SELECT group_concat(group_name || '!') "
                        + "FROM metrics WHERE amount > 0;")[0][3]
                .AsText()
                .Should().MatchRegex("USING (COVERING )?INDEX metrics_amount_partial");
            ReadRows(
                    connection,
                    "EXPLAIN SELECT group_concat(group_name || '!') "
                        + "FROM metrics WHERE amount > 0;")
                .Select(row => row[1].AsText())
                .Should().Contain("AggFinalize");
            ReadRows(
                    connection,
                    "SELECT sum(amount = '010') OVER "
                        + "(ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING) "
                        + "FROM metrics WHERE amount > 0;")
                .Select(row => row[0].AsInteger())
                .Should().Equal(1, 1, 1);

            Execute(connection, "CREATE TABLE unique_terms(name TEXT, x INTEGER) STRICT;");
            Execute(
                connection,
                "CREATE UNIQUE INDEX unique_terms_partial "
                    + "ON unique_terms(name) WHERE x = '01';");
            Execute(connection, "INSERT INTO unique_terms VALUES ('dup', 1);");
            Assert.Throws<EmbeddedSqlException>(
                () => Execute(connection, "INSERT INTO unique_terms VALUES ('dup', 1);"));
            Execute(connection, "CREATE TABLE any_values(f ANY) STRICT;");
            Execute(connection, "INSERT INTO any_values VALUES ('0006');");
            ReadRows(connection, "SELECT sum(f = '0006') FROM any_values;")[0][0]
                .Should().Be(SqlValue.Integer(1));
            ReadRows(connection, "EXPLAIN SELECT sum(f = '0006') FROM any_values;")
                .Select(row => row[1].AsText())
                .Should().Contain("AggFinalize");
            Execute(connection, "VACUUM;");
            AssertRows(ReadRows(connection, query));
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var reopenedConnection = reopened.Connect();
        AssertRows(ReadRows(reopenedConnection, query));
        AssertCompiled(reopenedConnection, query);
    }

    [Test]
    public void TempAggregatePlanMatchesItsActualExecutionRoute()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(
            connection,
            "CREATE TEMP TABLE temp_metrics(group_name TEXT COLLATE NOCASE, amount INTEGER) STRICT;");
        Execute(
            connection,
            "INSERT INTO temp_metrics VALUES ('A', 2), ('a', 3), ('B', 7);");
        const string query =
            "SELECT group_name, count(*), sum(amount) FROM temp_metrics GROUP BY group_name;";

        var rows = ReadRows(connection, query);
        rows.Should().HaveCount(2);
        rows[0].Should().Equal(
            SqlValue.Text("A"),
            SqlValue.Integer(2),
            SqlValue.Integer(5));
        rows[1].Should().Equal(
            SqlValue.Text("B"),
            SqlValue.Integer(1),
            SqlValue.Integer(7));

        ReadRows(connection, "EXPLAIN QUERY PLAN " + query)[0][3]
            .Should().Be(SqlValue.Text("MANAGED COMPILED VDBE"));
        ReadRows(connection, "EXPLAIN " + query)
            .Select(row => row[1].AsText())
            .Should().Contain("AggFinalize");
        ReadRows(
                connection,
                "EXPLAIN SELECT group_name, count(*) "
                    + "FROM temp.temp_metrics GROUP BY group_name;")
            .Select(row => row[1].AsText())
            .Should().Contain("AggFinalize");
        ReadRows(
                connection,
                "EXPLAIN QUERY PLAN SELECT group_name, count(*) "
                    + "FROM temp.temp_metrics GROUP BY group_name;")[0][3]
            .Should().Be(SqlValue.Text("MANAGED COMPILED VDBE"));
    }

    [Test]
    public void CompiledJoinAggregatePreservesEvaluatorCallbackOrder()
    {
        var calls = new List<long>();
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction("observe_amount", 1, arguments =>
        {
            calls.Add(arguments[0].AsInteger());
            return arguments[0];
        });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE users(id INTEGER PRIMARY KEY);");
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY, user_id INTEGER, amount INTEGER);");
        Execute(connection, "INSERT INTO users VALUES (1), (2);");
        Execute(connection, "INSERT INTO orders VALUES (10, 1, 10), (20, 1, 20), (30, 2, 5);");

        const string compiled =
            "SELECT sum(observe_amount(orders.amount)) "
            + "FROM users JOIN orders ON users.id = orders.user_id;";
        ReadRows(connection, compiled)[0][0].Should().Be(SqlValue.Integer(35));
        calls.Should().Equal(10, 20, 5);
        ReadRows(connection, "EXPLAIN " + compiled)
            .Select(row => row[1].AsText())
            .Should().Contain("OpenJoinCursor").And.Contain("AggFinalize");

        calls.Clear();
        const string fallback =
            "SELECT sum(observe_amount(amount)) FROM "
            + "(SELECT orders.amount AS amount "
            + "FROM users JOIN orders ON users.id = orders.user_id);";
        ReadRows(connection, fallback)[0][0].Should().Be(SqlValue.Integer(35));
        calls.Should().Equal(10, 20, 5);
    }

    [Test]
    public void CancellableIndexAggregatesAndRunningWindowsUseIndexOrder()
    {
        var scalarCalls = new List<long>();
        var aggregateCalls = new List<long>();
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction("observe_value", 1, arguments =>
        {
            scalarCalls.Add(arguments[0].AsInteger());
            return arguments[0];
        });
        database.RegisterAggregateFunction(
            "observed_sum",
            1,
            SqlValue.Integer(0),
            (state, arguments) =>
            {
                aggregateCalls.Add(arguments[0].AsInteger());
                return SqlValue.Integer(state.AsInteger() + arguments[0].AsInteger());
            },
            state => state);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE values_table(id INTEGER PRIMARY KEY, amount INTEGER);");
        Execute(
            connection,
            "CREATE INDEX values_amount_partial "
                + "ON values_table(amount DESC) WHERE amount > 0;");
        Execute(connection, "INSERT INTO values_table VALUES (1, 10), (2, 5), (3, 7);");

        using (var cancellation = new CancellationTokenSource())
        using (var statement = connection.Prepare(
                   "SELECT group_concat(observe_value(amount)) "
                       + "FROM values_table WHERE amount > 0;"))
        {
            statement.Step(cancellation.Token).Should().Be(StatementStepResult.Row);
            statement.GetValue(0).Should().Be(SqlValue.Text("10,7,5"));
            statement.Step(cancellation.Token).Should().Be(StatementStepResult.Done);
        }
        scalarCalls.Should().Equal(10, 7, 5);

        scalarCalls.Clear();
        ReadRows(
                connection,
                "SELECT group_concat(observe_value(amount)) "
                    + "FROM values_table WHERE amount > 0 LIMIT 1;")[0][0]
            .Should().Be(SqlValue.Text("10,7,5"));
        scalarCalls.Should().Equal(10, 7, 5);

        scalarCalls.Clear();
        const string compound =
            "SELECT group_concat(observe_value(amount)) "
            + "FROM values_table WHERE amount > 0 "
            + "UNION ALL SELECT 'tail';";
        var compoundRows = ReadRows(connection, compound);
        compoundRows.Should().HaveCount(2);
        compoundRows[0][0].Should().Be(SqlValue.Text("10,7,5"));
        compoundRows[1][0].Should().Be(SqlValue.Text("tail"));
        scalarCalls.Should().Equal(10, 7, 5);
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + compound));
        ReadRows(connection, "EXPLAIN QUERY PLAN " + compound)[0][3]
            .Should().Be(SqlValue.Text("MANAGED EVALUATOR FALLBACK"));

        const string window =
            "SELECT observed_sum(amount) OVER "
            + "(ORDER BY amount DESC ROWS UNBOUNDED PRECEDING) "
            + "FROM values_table WHERE amount > 0 ORDER BY amount DESC;";
        ReadRows(connection, window)
            .Select(row => row[0].AsInteger())
            .Should().Equal(10, 17, 22);
        aggregateCalls.Should().Equal(10, 10, 7, 10, 7, 5);
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + window));
        // Covering and non-covering plans both count as using the partial index.
        ReadRows(connection, "EXPLAIN QUERY PLAN " + window)[0][3]
            .AsText()
            .Should().MatchRegex("USING (COVERING )?INDEX values_amount_partial");

        aggregateCalls.Clear();
        const string namedWindow =
            "SELECT observed_sum(amount) OVER running "
            + "FROM values_table WHERE amount > 0 "
            + "WINDOW running AS (ORDER BY amount DESC ROWS UNBOUNDED PRECEDING) "
            + "ORDER BY amount DESC;";
        ReadRows(connection, namedWindow)
            .Select(row => row[0].AsInteger())
            .Should().Equal(10, 17, 22);
        aggregateCalls.Should().Equal(10, 10, 7, 10, 7, 5);
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + namedWindow));
    }

    [Test]
    public void LimitedAggregateCompoundFallsBackWithoutRelocationFailure()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2);");
        const string query =
            "SELECT * FROM (SELECT count(*) AS n FROM t LIMIT 1) "
            + "UNION ALL SELECT count(*) FROM t;";

        ReadRows(connection, query)
            .Select(row => row[0].AsInteger())
            .Should().Equal(2, 2);
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + query));
        ReadRows(connection, "EXPLAIN QUERY PLAN " + query)[0][3]
            .Should().Be(SqlValue.Text("MANAGED EVALUATOR FALLBACK"));

        const string limitedSibling =
            "SELECT count(*) FROM t "
            + "UNION ALL SELECT * FROM (SELECT value FROM t LIMIT 1);";
        ReadRows(connection, limitedSibling)
            .Select(row => row[0].AsInteger())
            .Should().Equal(2, 1);
        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN " + limitedSibling));
    }

    [Test]
    public void FallbackLimitCallbacksRunOnceForJoinAndCompoundAggregates()
    {
        static (EmbeddedConnection Connection, Func<int> Calls) Open()
        {
            var calls = 0;
            var database = new EmbeddedDatabase();
            database.RegisterScalarFunction("limit_cb", 0, _ =>
            {
                calls++;
                return SqlValue.Integer(calls == 1 ? 1 : 0);
            });
            var connection = database.Connect();
            Execute(connection, "CREATE TABLE left_rows(id INTEGER);");
            Execute(connection, "CREATE TABLE right_rows(id INTEGER);");
            Execute(connection, "INSERT INTO left_rows VALUES (1), (2);");
            Execute(connection, "INSERT INTO right_rows VALUES (1), (2);");
            return (connection, () => calls);
        }

        var joined = Open();
        using (joined.Connection)
        {
            ReadRows(
                    joined.Connection,
                    "SELECT count(*) FROM left_rows JOIN right_rows "
                        + "ON left_rows.id = right_rows.id LIMIT limit_cb();")
                .Should().ContainSingle();
            joined.Calls().Should().Be(1);
        }

        var compound = Open();
        using (compound.Connection)
        {
            ReadRows(
                    compound.Connection,
                    "SELECT * FROM "
                        + "(SELECT count(*) FROM left_rows LIMIT limit_cb()) "
                        + "UNION ALL SELECT count(*) FROM right_rows;")
                .Select(row => row[0].AsInteger())
                .Should().Equal(2, 2);
            compound.Calls().Should().Be(1);
        }

        var orderedWindow = Open();
        using (orderedWindow.Connection)
        {
            ReadRows(
                    orderedWindow.Connection,
                    "SELECT left_rows.id FROM left_rows JOIN right_rows "
                        + "ON left_rows.id = right_rows.id "
                        + "ORDER BY row_number() OVER (ORDER BY left_rows.id) "
                        + "LIMIT limit_cb();")
                .Select(row => row[0].AsInteger())
                .Should().Equal(1);
            orderedWindow.Calls().Should().Be(1);
        }
    }

    [Test]
    public void AggregateInAndRowComparisonsShortCircuitWithLhsAffinity()
    {
        var calls = 0;
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction("boom", 0, _ =>
        {
            calls++;
            throw new InvalidOperationException("boom must not run");
        });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(x TEXT);");
        Execute(connection, "INSERT INTO t VALUES ('1');");

        ReadRows(connection, "SELECT count(*) IN (x) FROM t;")[0][0]
            .Should().Be(SqlValue.Integer(0));
        ReadRows(connection, "SELECT count(*) IN (1, boom()) FROM t;")[0][0]
            .Should().Be(SqlValue.Integer(1));
        ReadRows(connection, "SELECT (count(*), boom()) = (0, 0) FROM t;")[0][0]
            .Should().Be(SqlValue.Integer(0));
        calls.Should().Be(0);
    }

    [Test]
    public void AggregateAndWindowCaseAndBetweenApplyAffinity()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE strict_values(i INTEGER, tx TEXT, r REAL) STRICT;");
        Execute(connection, "INSERT INTO strict_values VALUES (1, '1', 1);");
        const string frame =
            "ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING";

        ReadRows(
                connection,
                $"SELECT sum(i) OVER ({frame}) BETWEEN tx AND r FROM strict_values;")[0][0]
            .Should().Be(SqlValue.Integer(1));
        ReadRows(
                connection,
                "SELECT CASE sum(i) WHEN tx THEN 1 ELSE 0 END FROM strict_values;")[0][0]
            .Should().Be(SqlValue.Integer(1));
        ReadRows(
                connection,
                "SELECT CASE (sum(i), count(*)) "
                    + "WHEN (1, 1) THEN 9 ELSE 0 END FROM strict_values;")[0][0]
            .Should().Be(SqlValue.Integer(9));
        ReadRows(
                connection,
                $"SELECT CASE sum(i) OVER ({frame}) "
                    + "WHEN tx THEN 1 ELSE 0 END FROM strict_values;")[0][0]
            .Should().Be(SqlValue.Integer(1));
    }

    [Test]
    public void NonUniqueIndexExpressionsValidateBeforeAutoIncrementCommit()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE source_values(x INTEGER);");
        Execute(connection, "INSERT INTO source_values VALUES (-9223372036854775808);");
        Execute(
            connection,
            "CREATE TABLE target_values("
                + "id INTEGER PRIMARY KEY AUTOINCREMENT, x INTEGER);");
        Execute(connection, "CREATE INDEX target_abs ON target_values(abs(x));");

        Assert.Throws<EmbeddedSqlException>(
                () => Execute(
                    connection,
                    "INSERT INTO target_values(x) SELECT min(x) FROM source_values;"))!
            .Message.Should().Contain("integer overflow");
        ReadRows(connection, "SELECT count(*) FROM target_values;")[0][0]
            .Should().Be(SqlValue.Integer(0));
        ReadRows(
                connection,
                "SELECT count(*) FROM sqlite_sequence WHERE name = 'target_values';")[0][0]
            .Should().Be(SqlValue.Integer(0));
    }

    [Test]
    public void AggregateCallbacksCannotVacuumDuringExecution()
    {
        EmbeddedConnection? callbackConnection = null;
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile(
            "aggregate-vacuum-callback.db",
            fileSystem);
        database.RegisterAggregateFunction(
            "vacuum_sum",
            1,
            SqlValue.Integer(0),
            (state, arguments) =>
            {
                Execute(callbackConnection!, "VACUUM;");
                return SqlValue.Integer(state.AsInteger() + arguments[0].AsInteger());
            },
            state => state);
        using var connection = database.Connect();
        callbackConnection = connection;
        Execute(connection, "CREATE TABLE source_values(x INTEGER);");
        Execute(connection, "INSERT INTO source_values VALUES (1), (2), (3);");

        Assert.Throws<EmbeddedSqlException>(
                () => ReadRows(connection, "SELECT vacuum_sum(x) FROM source_values;"))!
            .Message.Should().Contain("SQL statements in progress");
        Assert.Throws<EmbeddedSqlException>(
                () => Execute(
                    connection,
                    "CREATE TABLE copied AS SELECT vacuum_sum(x) FROM source_values;"))!
            .Message.Should().Contain("SQL statements in progress");
    }

    [Test]
    public void CorrelatedPredicateDoesNotActivateInnerPartialIndex()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE outer_values(x INTEGER);");
        Execute(connection, "CREATE TABLE data(x INTEGER);");
        Execute(connection, "INSERT INTO outer_values VALUES (1), (0);");
        Execute(connection, "INSERT INTO data VALUES (1), (2), (3);");
        Execute(connection, "CREATE INDEX data_one ON data(x) WHERE x = 1;");

        ReadRows(
                connection,
                "SELECT (SELECT count(*) FROM data WHERE outer_values.x = 1) "
                    + "FROM outer_values;")
            .Select(row => row[0].AsInteger())
            .Should().Equal(3, 0);
    }

    [Test]
    public void IndexedBoundedDistinctCustomCollationEvaluatesLimitOnce()
    {
        var limitCalls = 0;
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction("next_limit", 0, _ =>
        {
            limitCalls++;
            return SqlValue.Integer(limitCalls);
        });
        database.RegisterCollation(
            "observed",
            (left, right) => string.CompareOrdinal(left, right));
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value TEXT);");
        Execute(connection, "CREATE INDEX t_partial ON t(value) WHERE value > '';");
        Execute(connection, "INSERT INTO t VALUES ('a'), ('b'), ('c');");

        ReadRows(
                connection,
                "SELECT DISTINCT value COLLATE observed FROM t "
                    + "WHERE value > '' GROUP BY value LIMIT next_limit();")
            .Should().ContainSingle();
        limitCalls.Should().Be(1);
    }

    [Test]
    public void PostRefreshEvaluatorFallbackReusesResolvedLimit()
    {
        var calls = 0;
        var database = new EmbeddedDatabase();
        database.RegisterCollation(
            "observed",
            (left, right) => string.CompareOrdinal(left, right));
        using var callbackConnection = database.Connect();
        database.RegisterScalarFunction("mutate_limit", 0, _ =>
        {
            calls++;
            Execute(callbackConnection, "DROP TABLE t;");
            Execute(
                callbackConnection,
                "CREATE TABLE t(x TEXT COLLATE observed);");
            Execute(callbackConnection, "INSERT INTO t VALUES ('a'), ('b');");
            return SqlValue.Integer(1);
        });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(x TEXT);");
        Execute(connection, "CREATE INDEX t_partial ON t(x) WHERE x > '';");
        Execute(connection, "INSERT INTO t VALUES ('old');");

        ReadRows(
                connection,
                "SELECT count(*) FROM t GROUP BY x "
                    + "HAVING x > '' LIMIT mutate_limit();")
            .Should().ContainSingle();
        calls.Should().Be(1);
    }

    [Test]
    public void AliasedCorrelatedPredicateDoesNotUseHiddenTableNameForPartialIndex()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE data(x INTEGER);");
        Execute(connection, "INSERT INTO data VALUES (1), (2), (3);");
        Execute(connection, "CREATE INDEX data_one ON data(x) WHERE x = 1;");

        ReadRows(
                connection,
                "SELECT x, (SELECT count(*) FROM data AS d WHERE data.x = 1) "
                    + "FROM data;")
            .Select(row => (row[0].AsInteger(), row[1].AsInteger()))
            .Should().Equal((1, 3), (2, 0), (3, 0));
    }

    [Test]
    public void NonIndexedLimitCallbackRefreshesAggregateSource()
    {
        var database = new EmbeddedDatabase();
        using var callbackConnection = database.Connect();
        var calls = 0;
        database.RegisterScalarFunction("replace_source", 0, _ =>
        {
            calls++;
            Execute(callbackConnection, "DROP TABLE source_values;");
            Execute(callbackConnection, "CREATE TABLE source_values(x INTEGER);");
            Execute(callbackConnection, "INSERT INTO source_values VALUES (9);");
            return SqlValue.Integer(1);
        });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE source_values(x INTEGER);");
        Execute(connection, "INSERT INTO source_values VALUES (1), (2);");

        ReadRows(
                connection,
                "SELECT sum(x) FROM source_values LIMIT replace_source();")[0][0]
            .Should().Be(SqlValue.Integer(9));
        calls.Should().Be(1);
    }

    [Test]
    public void LimitRefreshesCollationsAndMaterializesReplacementViewsOnce()
    {
        var collationDatabase = new EmbeddedDatabase();
        using var collationCallback = collationDatabase.Connect();
        var collationCalls = 0;
        collationDatabase.RegisterScalarFunction("replace_collation", 0, _ =>
        {
            collationCalls++;
            Execute(collationCallback, "DROP TABLE t;");
            Execute(collationCallback, "CREATE TABLE t(x TEXT COLLATE NOCASE);");
            Execute(collationCallback, "INSERT INTO t VALUES ('a'), ('A');");
            return SqlValue.Integer(1);
        });
        using (var connection = collationDatabase.Connect())
        {
            Execute(connection, "CREATE TABLE t(x TEXT COLLATE BINARY);");
            Execute(connection, "INSERT INTO t VALUES ('a'), ('A');");
            ReadRows(
                    connection,
                    "SELECT count(*) FROM t GROUP BY x LIMIT replace_collation();")[0][0]
                .Should().Be(SqlValue.Integer(2));
            collationCalls.Should().Be(1);
        }

        var viewDatabase = new EmbeddedDatabase();
        using var viewCallback = viewDatabase.Connect();
        var viewCalls = 0;
        viewDatabase.RegisterScalarFunction("replace_with_view", 0, _ =>
        {
            viewCalls++;
            Execute(viewCallback, "DROP TABLE t;");
            Execute(viewCallback, "CREATE TABLE backing(x INTEGER);");
            Execute(viewCallback, "INSERT INTO backing VALUES (9);");
            Execute(viewCallback, "CREATE VIEW t AS SELECT x FROM backing;");
            return SqlValue.Integer(1);
        });
        using var viewConnection = viewDatabase.Connect();
        Execute(viewConnection, "CREATE TABLE t(x INTEGER);");
        Execute(viewConnection, "INSERT INTO t VALUES (1), (2);");
        ReadRows(
                viewConnection,
                "SELECT sum(x) FROM t LIMIT replace_with_view();")[0][0]
            .Should().Be(SqlValue.Integer(9));
        viewCalls.Should().Be(1);
    }

    [Test]
    public void AggregateRowInMaterializesRhsAndBetweenShortCircuitsElements()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(v TEXT);");
        Execute(connection, "INSERT INTO t VALUES ('a');");

        ReadRows(
                connection,
                "SELECT (min(v), 1) IN "
                    + "(('A' COLLATE NOCASE, 1), ('z', 2)) FROM t;")[0][0]
            .Should().Be(SqlValue.Integer(0));
        Assert.Throws<EmbeddedSqlException>(
                () => ReadRows(
                    connection,
                    "SELECT (count(*), 1) IN "
                        + "((1, 1), (1, abs(-9223372036854775808))) FROM t;"))!
            .Message.Should().Contain("integer overflow");
        ReadRows(
                connection,
                "SELECT (count(*), 5) BETWEEN "
                    + "(10, abs(-9223372036854775808)) "
                    + "AND (20, abs(-9223372036854775808)) FROM t;")[0][0]
            .Should().Be(SqlValue.Integer(0));
    }

    [Test]
    public void LimitCallbacksRefreshAggregateSchemaAndCompoundsDoNotPrebindCallbackTerms()
    {
        var database = new EmbeddedDatabase();
        using var callbackConnection = database.Connect();
        var limitCalls = 0;
        database.RegisterScalarFunction("mutate_limit", 0, _ =>
        {
            limitCalls++;
            Execute(callbackConnection, "DROP TABLE source_values;");
            Execute(callbackConnection, "CREATE TABLE source_values(y INTEGER);");
            Execute(callbackConnection, "INSERT INTO source_values VALUES (9);");
            return SqlValue.Integer(1);
        });
        database.RegisterScalarFunction("drop_target", 1, arguments =>
        {
            Execute(callbackConnection, "DROP TABLE target_values;");
            return arguments[0];
        });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE source_values(x INTEGER);");
        Execute(connection, "CREATE INDEX source_positive ON source_values(x) WHERE x > 0;");
        Execute(connection, "INSERT INTO source_values VALUES (1), (2);");

        Assert.Throws<EmbeddedSqlException>(
                () => ReadRows(
                    connection,
                    "SELECT sum(x) FROM source_values "
                        + "WHERE x > 0 LIMIT mutate_limit();"))!
            .Message.Should().Contain("no such column: x");
        limitCalls.Should().Be(1);

        Execute(connection, "CREATE TABLE first_values(x INTEGER);");
        Execute(connection, "CREATE TABLE target_values(x INTEGER);");
        Execute(connection, "INSERT INTO first_values VALUES (1);");
        Execute(connection, "INSERT INTO target_values VALUES (1), (2);");
        const string compound =
            "SELECT drop_target(count(*)) FROM first_values "
            + "UNION ALL SELECT sum(x) FROM target_values;";
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, compound))!
            .Message.Should().Contain("no such table: target_values");
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + compound));

        Execute(connection, "CREATE TABLE target_values(x INTEGER);");
        Execute(connection, "INSERT INTO target_values VALUES (1), (2);");
        const string whereCompound =
            "SELECT count(*) FROM first_values WHERE drop_target(x) > 0 "
            + "UNION ALL SELECT sum(x) FROM target_values;";
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, whereCompound))!
            .Message.Should().Contain("no such table: target_values");
        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN " + whereCompound));
    }

    private static void AssertRows(IReadOnlyList<SqlValue[]> rows)
    {
        rows.Should().HaveCount(2);
        rows[0].Should().Equal(
            SqlValue.Text("alpha"),
            SqlValue.Integer(2),
            SqlValue.Integer(15),
            SqlValue.Integer(1));
        rows[1].Should().Equal(
            SqlValue.Text("beta"),
            SqlValue.Integer(1),
            SqlValue.Integer(7),
            SqlValue.Integer(0));
    }

    private static void AssertCompiled(EmbeddedConnection connection, string sql)
    {
        ReadRows(connection, "EXPLAIN " + sql)
            .Select(row => row[1].AsText())
            .Should().Contain("GroupKey").And.Contain("AggFinalize");
        ReadRows(connection, "EXPLAIN QUERY PLAN " + sql)[0][3]
            .Should().Be(SqlValue.Text("MANAGED COMPILED VDBE"));
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

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }
}
