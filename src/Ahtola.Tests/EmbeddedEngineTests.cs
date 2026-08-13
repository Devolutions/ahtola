using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

public class EmbeddedEngineTests
{
    [Test]
    public void GenerateSeriesStepsRowsAndExposesColumnMetadata()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare("SELECT * FROM generate_series(1, 3, 1);");

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetColumnName(0).Should().Be("value");
        statement.GetValue(0).Should().Be(SqlValue.Integer(1));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(2));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(3));
        statement.Step().Should().Be(StatementStepResult.Done);

    }

    [Test]
    public void PreparedStatementsBindNamedAndNumberedParameters()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE users(id INTEGER, name TEXT);");
        using (var insert = connection.Prepare("INSERT INTO users(id, name) VALUES (?, :name);"))
        {
            insert.Bind(1, SqlValue.Integer(2));
            insert.Bind(":name", SqlValue.Text("Ada")).Should().BeTrue();
            insert.Step().Should().Be(StatementStepResult.Done);
            insert.RowsAffected.Should().Be(1);
        }

        using var select = connection.Prepare("SELECT name FROM users WHERE id = ?1;");
        select.Bind(1, SqlValue.Integer(2));
        select.Step().Should().Be(StatementStepResult.Row);
        select.GetValue(0).Should().Be(SqlValue.Text("Ada"));
        select.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void LexerAcceptsSQLiteEqualityNumericAndDollarParameterForms()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare("SELECT 1 == 1, 1e2, 0x10, .5, $a::b(c);");
        statement.Bind("$a::b(c)", SqlValue.Integer(7)).Should().BeTrue();

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(1));
        statement.GetValue(1).Should().Be(SqlValue.Real(100));
        statement.GetValue(2).Should().Be(SqlValue.Integer(16));
        statement.GetValue(3).Should().Be(SqlValue.Real(0.5));
        statement.GetValue(4).Should().Be(SqlValue.Integer(7));
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void QualifiedCteNamesAndNonAggregateHavingAreRejected()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Assert.Throws<EmbeddedSqlException>(() => connection.Prepare("WITH a.b AS (SELECT 1) SELECT 1;"));
        using var having = connection.Prepare("SELECT 1 HAVING 0;");
        Assert.Throws<EmbeddedSqlException>(() => having.Step())!
            .Message.Should().Be("HAVING clause on a non-aggregate query");
    }

    [Test]
    public void InsertValuesCanStartACompoundQuerySource()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE values_table(value INTEGER);");
        Execute(connection, "INSERT INTO values_table VALUES (1) UNION ALL SELECT 2 UNION ALL SELECT 3;");

        using var statement = connection.Prepare("SELECT value FROM values_table ORDER BY value;");
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(1));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(2));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(3));
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void DistinctOnScalarFunctionsDoesNotRequireAggregateArity()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare("SELECT coalesce(DISTINCT NULL, 7);");

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(7));
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void GroupConcatOrdersRowsByAggregateOrderByTerms()
    {
        using var connection = CreateOrderedAggregateTable();

        ReadSingle(connection, "SELECT group_concat(s ORDER BY id) FROM t;")
            .Should().Be(SqlValue.Text("b,a,c"));
        ReadSingle(connection, "SELECT group_concat(s ORDER BY id DESC) FROM t;")
            .Should().Be(SqlValue.Text("c,a,b"));
        ReadSingle(connection, "SELECT group_concat(s, '|' ORDER BY s DESC) FROM t;")
            .Should().Be(SqlValue.Text("c|b|a"));
        ReadSingle(connection, "SELECT group_concat(s ORDER BY g DESC, id ASC) FROM t;")
            .Should().Be(SqlValue.Text("c,b,a"));
    }

    [Test]
    public void AggregateOrderByHonorsNullPlacement()
    {
        using var connection = CreateOrderedAggregateTable();

        // ASC defaults to NULLS FIRST: row 2 (n NULL) leads.
        ReadSingle(connection, "SELECT group_concat(s ORDER BY n) FROM t;")
            .Should().Be(SqlValue.Text("a,b,c"));
        ReadSingle(connection, "SELECT group_concat(s ORDER BY n NULLS LAST) FROM t;")
            .Should().Be(SqlValue.Text("b,c,a"));
        ReadSingle(connection, "SELECT group_concat(s ORDER BY n DESC NULLS FIRST) FROM t;")
            .Should().Be(SqlValue.Text("a,c,b"));
    }

    [Test]
    public void AggregateOrderByAppliesPerGroup()
    {
        using var connection = CreateOrderedAggregateTable();
        using var statement = connection.Prepare(
            "SELECT g, group_concat(s ORDER BY id DESC) FROM t GROUP BY g ORDER BY g;");

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(1));
        statement.GetValue(1).Should().Be(SqlValue.Text("a,b"));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(2));
        statement.GetValue(1).Should().Be(SqlValue.Text("c"));
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void AggregateOrderByOrdinalLiteralIsAConstantNotAProjectionReference()
    {
        using var connection = CreateOrderedAggregateTable();

        // Inside an aggregate an integer term is a constant (stable input order), never
        // the projection-ordinal rewrite a select-level ORDER BY 1 would get.
        ReadSingle(connection, "SELECT group_concat(s ORDER BY 1) FROM t;")
            .Should().Be(SqlValue.Text("b,a,c"));
    }

    [Test]
    public void AggregateOrderByAppliesToEveryAggregateKind()
    {
        using var connection = CreateOrderedAggregateTable();

        ReadSingle(connection, "SELECT string_agg(s, ';' ORDER BY id DESC) FROM t;")
            .Should().Be(SqlValue.Text("c;a;b"));
        ReadSingle(connection, "SELECT json_group_array(s ORDER BY id DESC) FROM t WHERE s IS NOT NULL;")
            .Should().Be(SqlValue.Text("[\"c\",\"a\",\"b\"]"));
        ReadSingle(connection, "SELECT sum(id ORDER BY id DESC) FROM t;")
            .Should().Be(SqlValue.Integer(10));
        ReadSingle(connection, "SELECT count(s ORDER BY s) FROM t;")
            .Should().Be(SqlValue.Integer(3));
    }

    [Test]
    public void AggregateOrderByMatchesTheEfStringTranslationsShape()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE \"BasicTypesEntities\" (\"Id\" INTEGER, \"Int\" INTEGER, \"String\" TEXT);");
        Execute(connection, "INSERT INTO \"BasicTypesEntities\" VALUES (1, 1, 'foo'), (2, 1, 'bar'), (3, 1, 'baz');");

        ReadSingle(
                connection,
                "SELECT COALESCE(group_concat(\"String\", '|' ORDER BY \"Id\" DESC), '') FROM \"BasicTypesEntities\" GROUP BY \"Int\";")
            .Should().Be(SqlValue.Text("baz|bar|foo"));
    }

    [Test]
    public void CreateViewRejectsAggregateInternalOrderBy()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        Assert.Throws<EmbeddedSqlException>(
                () => Execute(connection, "CREATE VIEW v AS SELECT sum(value ORDER BY value) FROM t;"))!
            .Message.Should().Be("ORDER BY clause is not supported yet in aggregate functions");
    }

    [Test]
    public void DistinctAggregateWithOrderByDeduplicatesInAggregateOrder()
    {
        using var connection = CreateOrderedAggregateTable();

        ReadSingle(connection, "SELECT group_concat(DISTINCT s ORDER BY s DESC) FROM t;")
            .Should().Be(SqlValue.Text("c,b,a"));
    }

    [Test]
    public void WindowFunctionWithAggregateOrderByIsExplicitlyRejected()
    {
        using var connection = CreateOrderedAggregateTable();

        Assert.Throws<EmbeddedSqlException>(
                () => connection.Prepare("SELECT group_concat(s ORDER BY id) OVER () FROM t;").Step())!
            .Message.Should().Contain("ORDER BY within window functions");
    }

    private static EmbeddedConnection CreateOrderedAggregateTable()
    {
        var database = new EmbeddedDatabase();
        var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, s TEXT, g INTEGER, n INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 'b', 1, 10), (2, 'a', 1, NULL), (3, 'c', 2, 30), (4, NULL, 2, 20);");
        return connection;
    }

    private static SqlValue ReadSingle(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        var value = statement.GetValue(0);
        statement.Step().Should().Be(StatementStepResult.Done);
        return value;
    }

    [Test]
    public void StatementStreamsProjectionCallbacksOneRowAtATime()
    {
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "fail_on_two",
            1,
            values => values[0] == SqlValue.Integer(2)
                ? throw new InvalidOperationException("later row")
                : values[0]);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE values_table(value INTEGER);");
        Execute(connection, "INSERT INTO values_table VALUES (1), (2);");
        using var statement = connection.Prepare("SELECT fail_on_two(value) FROM values_table;");

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(1));
        Assert.Throws<InvalidOperationException>(() => statement.Step())!
            .Message.Should().Be("later row");
    }

    [Test]
    public void StatementStreamsWhereCallbacksOneSourceRowAtATime()
    {
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "fail_on_two",
            1,
            values => values[0] == SqlValue.Integer(2)
                ? throw new InvalidOperationException("later row")
                : SqlValue.Integer(1));
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE values_table(value INTEGER);");
        Execute(connection, "INSERT INTO values_table VALUES (1), (2);");
        using var statement = connection.Prepare(
            "SELECT value FROM values_table WHERE fail_on_two(value);");

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(1));
        Assert.Throws<InvalidOperationException>(() => statement.Step())!
            .Message.Should().Be("later row");
    }

    [Test]
    public void StreamedWhereHasRowsSkipsNonQualifyingSourceRows()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE values_table(value INTEGER);");
        Execute(connection, "INSERT INTO values_table VALUES (1), (2);");
        using var statement = connection.Prepare(
            "SELECT value FROM values_table WHERE value > 2;");

        statement.HasRows().Should().BeFalse();
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void StatementStreamsWhereLimitAndOffsetWithoutScanningPastLimit()
    {
        var observed = new List<long>();
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "observe",
            1,
            values =>
            {
                observed.Add(values[0].AsInteger());
                return values[0];
            });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE values_table(value INTEGER);");
        Execute(connection, "INSERT INTO values_table VALUES (1), (2), (3), (4), (5);");
        using var statement = connection.Prepare(
            "SELECT value FROM values_table WHERE observe(value) % 2 = 0 LIMIT 1 OFFSET 1;");

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(4));
        statement.Step().Should().Be(StatementStepResult.Done);
        observed.Should().Equal(1, 2, 3, 4);
    }

    [Test]
    public void OrderByEvaluatesScalarCallbackKeysOnceInSourceOrder()
    {
        var observed = new List<long>();
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "observe_sort_key",
            1,
            values =>
            {
                observed.Add(values[0].AsInteger());
                return values[0];
            });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE values_table(value INTEGER);");
        Execute(connection, "INSERT INTO values_table VALUES (3), (1), (2);");
        using var statement = connection.Prepare(
            "SELECT value FROM values_table ORDER BY observe_sort_key(value);");

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(1));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(2));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(3));
        statement.Step().Should().Be(StatementStepResult.Done);
        observed.Should().Equal(3, 1, 2);
    }

    [Test]
    public void GroupedOrderByEvaluatesCallbackKeysOnceInSortedGroupKeyOrder()
    {
        var observed = new List<long>();
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "observe_group_key",
            1,
            values =>
            {
                observed.Add(values[0].AsInteger());
                return values[0];
            });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE grouped_values(group_name TEXT, value INTEGER);");
        Execute(connection, "INSERT INTO grouped_values VALUES ('later', 30), ('first', 1), ('first', 2);");
        using var statement = connection.Prepare(
            "SELECT group_name, sum(value) FROM grouped_values "
            + "GROUP BY group_name ORDER BY observe_group_key(sum(value));");

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Text("first"));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Text("later"));
        statement.Step().Should().Be(StatementStepResult.Done);
        observed.Should().Equal(3, 30);
    }

    [Test]
    public void OrderByStopsAtTheFirstCallbackFailureInSourceOrder()
    {
        var observed = new List<long>();
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "fail_on_second_sort_key",
            1,
            values =>
            {
                var value = values[0].AsInteger();
                observed.Add(value);
                if (value == 1)
                    throw new InvalidOperationException("sort key failure");

                return values[0];
            });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE values_table(value INTEGER);");
        Execute(connection, "INSERT INTO values_table VALUES (3), (1), (2);");

        using var statement = connection.Prepare(
            "SELECT value FROM values_table ORDER BY fail_on_second_sort_key(value);");
        Assert.Throws<InvalidOperationException>(() => statement.Step());
        observed.Should().Equal(3, 1);
    }

    [Test]
    public void UnqualifiedColumnsFromSeparateJoinSourcesAreRejectedAsAmbiguous()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE left_values(id INTEGER, value INTEGER);");
        Execute(connection, "CREATE TABLE right_values(id INTEGER, value INTEGER);");
        Execute(connection, "INSERT INTO left_values VALUES (1, 10);");
        Execute(connection, "INSERT INTO right_values VALUES (1, 20);");

        var queries = new[]
        {
            "SELECT id FROM left_values JOIN right_values ON left_values.id = right_values.id;",
            "SELECT left_values.id FROM left_values JOIN right_values ON left_values.id = right_values.id WHERE value = 10;",
            "SELECT left_values.id FROM left_values JOIN right_values ON left_values.id = right_values.id ORDER BY value;",
            "SELECT count(*) FROM left_values JOIN right_values ON left_values.id = right_values.id GROUP BY value;",
        };
        foreach (var query in queries)
        {
            using var statement = connection.Prepare(query);
            Assert.That(
                Assert.Throws<EmbeddedSqlException>(() => statement.Step())!.Message,
                Is.EqualTo("ambiguous column name: value").Or.EqualTo("ambiguous column name: id"));
        }
    }

    [Test]
    public void UsingAndNaturalJoinColumnsRemainUnambiguous()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE left_values(id INTEGER, left_value INTEGER);");
        Execute(connection, "CREATE TABLE right_values(id INTEGER, right_value INTEGER);");
        Execute(connection, "INSERT INTO left_values VALUES (1, 10);");
        Execute(connection, "INSERT INTO right_values VALUES (1, 20);");

        using var usingStatement = connection.Prepare(
            "SELECT id FROM left_values JOIN right_values USING(id);");
        usingStatement.Step().Should().Be(StatementStepResult.Row);
        usingStatement.GetValue(0).Should().Be(SqlValue.Integer(1));

        using var naturalStatement = connection.Prepare(
            "SELECT id FROM left_values NATURAL JOIN right_values;");
        naturalStatement.Step().Should().Be(StatementStepResult.Row);
        naturalStatement.GetValue(0).Should().Be(SqlValue.Integer(1));
    }

    [Test]
    public void RegisteredBuiltinBinaryCollationOverridesBuiltinComparisonSemantics()
    {
        var database = new EmbeddedDatabase();
        database.RegisterCollation("BINARY", (_, _) => 0);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE values_table(value TEXT);");
        Execute(connection, "CREATE INDEX values_by_value ON values_table(value);");
        Execute(connection, "INSERT INTO values_table VALUES ('b'), ('a');");

        using var equality = connection.Prepare(
            "SELECT value FROM values_table WHERE value = 'a' ORDER BY value;");
        equality.Step().Should().Be(StatementStepResult.Row);
        equality.GetValue(0).Should().Be(SqlValue.Text("b"));
        equality.Step().Should().Be(StatementStepResult.Row);
        equality.GetValue(0).Should().Be(SqlValue.Text("a"));
        equality.Step().Should().Be(StatementStepResult.Done);

        using var distinct = connection.Prepare("SELECT DISTINCT value FROM values_table;");
        distinct.Step().Should().Be(StatementStepResult.Row);
        distinct.GetValue(0).Should().Be(SqlValue.Text("b"));
        distinct.Step().Should().Be(StatementStepResult.Done);

        using var grouped = connection.Prepare(
            "SELECT count(*) FROM values_table GROUP BY value;");
        grouped.Step().Should().Be(StatementStepResult.Row);
        grouped.GetValue(0).Should().Be(SqlValue.Integer(2));
        grouped.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void TransactionsRollbackChangesAndCountRows()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE values_table(value INTEGER);");
        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO values_table VALUES (1);");
        Execute(connection, "ROLLBACK;");

        using var count = connection.Prepare("SELECT COUNT(*) FROM values_table;");
        count.Step().Should().Be(StatementStepResult.Row);
        count.GetValue(0).Should().Be(SqlValue.Integer(0));
    }

    [Test]
    public void TransactionsCommitChangesWithoutMutatingTheSharedRevisionEarly()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE values_table(value INTEGER);");
        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO values_table VALUES (1);");
        Execute(connection, "COMMIT;");

        using var count = connection.Prepare("SELECT COUNT(*) FROM values_table;");
        count.Step().Should().Be(StatementStepResult.Row);
        count.GetValue(0).Should().Be(SqlValue.Integer(1));
    }

    [Test]
    public void ExpressionsApplySqlArithmeticAndNullPropagation()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare("SELECT 2 + 3 * 4, NULL = 1;");

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(14));
        statement.GetValue(1).Should().Be(SqlValue.Null);
    }

    [Test]
    public void FailedInsertIsStatementAtomic()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE values_table(value INTEGER);");

        using (var insert = connection.Prepare("INSERT INTO values_table VALUES (1), (missing_column);"))
        {
            Assert.Throws<EmbeddedSqlException>(() => insert.Step());
        }

        using var count = connection.Prepare("SELECT COUNT(*) FROM values_table;");
        count.Step().Should().Be(StatementStepResult.Row);
        count.GetValue(0).Should().Be(SqlValue.Integer(0));
    }

    [Test]
    public void GenerateSeriesRetainsBoundaryValuesWithoutOverflowing()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var maximum = connection.Prepare($"SELECT * FROM generate_series({long.MaxValue}, {long.MaxValue}, 1);");

        maximum.Step().Should().Be(StatementStepResult.Row);
        maximum.GetValue(0).Should().Be(SqlValue.Integer(long.MaxValue));
        maximum.Step().Should().Be(StatementStepResult.Done);

        using var minimum = connection.Prepare($"SELECT * FROM generate_series({long.MinValue}, {long.MinValue}, -1);");
        minimum.Step().Should().Be(StatementStepResult.Row);
        minimum.GetValue(0).Should().Be(SqlValue.Integer(long.MinValue));
        minimum.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void UnterminatedBlockCommentsReportSqlErrors()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Assert.Throws<EmbeddedSqlException>(() => connection.Prepare("/*"));
    }

    [Test]
    public void MetadataLookupDoesNotExecuteDataModificationStatements()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE values_table(value INTEGER);");
        using var insert = connection.Prepare("INSERT INTO values_table VALUES (1);");

        Assert.Throws<ArgumentOutOfRangeException>(() => insert.GetColumnName(0));

        using var count = connection.Prepare("SELECT COUNT(*) FROM values_table;");
        count.Step().Should().Be(StatementStepResult.Row);
        count.GetValue(0).Should().Be(SqlValue.Integer(0));
    }

    [Test]
    public void ReadOnlyTransactionDoesNotConflictWithConcurrentWriter()
    {
        var database = new EmbeddedDatabase();
        using var writer = database.Connect();
        using var reader = database.Connect();
        Execute(writer, "CREATE TABLE values_table(value INTEGER);");

        Execute(writer, "BEGIN;");
        Execute(reader, "BEGIN;");
        Execute(reader, "COMMIT;");
        Execute(writer, "INSERT INTO values_table VALUES (1);");
        Execute(writer, "COMMIT;");

        using var count = reader.Prepare("SELECT COUNT(*) FROM values_table;");
        count.Step().Should().Be(StatementStepResult.Row);
        count.GetValue(0).Should().Be(SqlValue.Integer(1));
    }

    [Test]
    public void NoOpDmlDoesNotConflictWithConcurrentWriter()
    {
        var database = new EmbeddedDatabase();
        using var writer = database.Connect();
        using var noOpWriter = database.Connect();
        Execute(writer, "CREATE TABLE values_table(value INTEGER);");

        Execute(writer, "BEGIN;");
        Execute(noOpWriter, "BEGIN;");
        Execute(noOpWriter, "DELETE FROM values_table WHERE value = 1;");
        Execute(noOpWriter, "COMMIT;");
        Execute(writer, "INSERT INTO values_table VALUES (1);");
        Execute(writer, "COMMIT;");

        using var count = noOpWriter.Prepare("SELECT COUNT(*) FROM values_table;");
        count.Step().Should().Be(StatementStepResult.Row);
        count.GetValue(0).Should().Be(SqlValue.Integer(1));
    }

    [Test]
    public void LimitZeroDoesNotProduceSeriesRows()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare("SELECT * FROM generate_series(1, 100, 1) LIMIT 0;");

        statement.Step().Should().Be(StatementStepResult.Done);
        statement.GetColumnName(0).Should().Be("value");
    }

    [Test]
    public void ArithmeticPreservesSqliteIntegerAndRealSemantics()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare(
            "SELECT 9223372036854775807 + 1, 5 / 2, -9223372036854775808 / -1, 1 / 0, 9007199254740993 = 9007199254740992.0;");

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Real(9223372036854775808d));
        statement.GetValue(1).Should().Be(SqlValue.Integer(2));
        statement.GetValue(2).Should().Be(SqlValue.Real(9223372036854775808d));
        statement.GetValue(3).Should().Be(SqlValue.Null);
        statement.GetValue(4).Should().Be(SqlValue.Integer(0));
    }

    [Test]
    public void CountSupportsStarEmptyAndColumnArguments()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE values_table(value INTEGER);");
        Execute(connection, "INSERT INTO values_table VALUES (1), (NULL), (3);");
        using var statement = connection.Prepare("SELECT COUNT(*), COUNT(), COUNT(value), COUNT(*) + 1 FROM values_table;");

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(3));
        statement.GetValue(1).Should().Be(SqlValue.Integer(3));
        statement.GetValue(2).Should().Be(SqlValue.Integer(2));
        statement.GetValue(3).Should().Be(SqlValue.Integer(4));
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void BuiltInAggregatesApplySqliteNullAndNumericSemantics()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE values_table(value INTEGER, label TEXT);");
        Execute(connection, "INSERT INTO values_table VALUES (1, 'a'), (NULL, 'b'), (3, 'c');");
        using var statement = connection.Prepare(
            "SELECT sum(value), total(value), avg(value), min(value), max(value), group_concat(label, '|'), CAST(count(*) AS TEXT) FROM values_table;");

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(4));
        statement.GetValue(1).Should().Be(SqlValue.Real(4));
        statement.GetValue(2).Should().Be(SqlValue.Real(2));
        statement.GetValue(3).Should().Be(SqlValue.Integer(1));
        statement.GetValue(4).Should().Be(SqlValue.Integer(3));
        statement.GetValue(5).Should().Be(SqlValue.Text("a|b|c"));
        statement.GetValue(6).Should().Be(SqlValue.Text("3"));
    }

    [Test]
    public void GroupByProducesAnAggregateRowForEachDistinctKey()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE values_table(category TEXT, value INTEGER);");
        Execute(connection, "INSERT INTO values_table VALUES ('a', 1), ('b', 2), ('a', 3);");
        using var statement = connection.Prepare(
            "SELECT category, sum(value), count(*) FROM values_table GROUP BY category;");

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Text("a"));
        statement.GetValue(1).Should().Be(SqlValue.Integer(4));
        statement.GetValue(2).Should().Be(SqlValue.Integer(2));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Text("b"));
        statement.GetValue(1).Should().Be(SqlValue.Integer(2));
        statement.GetValue(2).Should().Be(SqlValue.Integer(1));
        statement.Step().Should().Be(StatementStepResult.Done);

        using var ordered = connection.Prepare(
            "SELECT category, sum(value) AS total FROM values_table GROUP BY category ORDER BY total DESC LIMIT 1;");
        ordered.Step().Should().Be(StatementStepResult.Row);
        ordered.GetValue(0).Should().Be(SqlValue.Text("a"));
        ordered.GetValue(1).Should().Be(SqlValue.Integer(4));
        ordered.Step().Should().Be(StatementStepResult.Done);

        using var filtered = connection.Prepare(
            "SELECT category, count(*) FROM values_table GROUP BY category HAVING sum(value) > 2;");
        filtered.Step().Should().Be(StatementStepResult.Row);
        filtered.GetValue(0).Should().Be(SqlValue.Text("a"));
        filtered.GetValue(1).Should().Be(SqlValue.Integer(2));
        filtered.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void CrossAndInnerJoinsComposeRowsAndApplyOnPredicates()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE parents(parent_id INTEGER, parent_name TEXT);");
        Execute(connection, "CREATE TABLE children(parent_key INTEGER, child_name TEXT);");
        Execute(connection, "INSERT INTO parents VALUES (1, 'Ada'), (2, 'Grace');");
        Execute(connection, "INSERT INTO children VALUES (1, 'Alice'), (1, 'Bob'), (3, 'Carol');");

        using (var inner = connection.Prepare(
                   "SELECT parents.parent_name, children.child_name FROM parents " +
                   "JOIN children ON parents.parent_id = children.parent_key ORDER BY children.child_name;"))
        {
            inner.Step().Should().Be(StatementStepResult.Row);
            inner.GetValue(0).Should().Be(SqlValue.Text("Ada"));
            inner.GetValue(1).Should().Be(SqlValue.Text("Alice"));
            inner.Step().Should().Be(StatementStepResult.Row);
            inner.GetValue(1).Should().Be(SqlValue.Text("Bob"));
            inner.Step().Should().Be(StatementStepResult.Done);
        }

        using var cross = connection.Prepare("SELECT count(*) FROM parents CROSS JOIN children;");
        cross.Step().Should().Be(StatementStepResult.Row);
        cross.GetValue(0).Should().Be(SqlValue.Integer(6));

        using (var left = connection.Prepare(
                   "SELECT parents.parent_name, children.child_name FROM parents " +
                   "LEFT OUTER JOIN children ON parents.parent_id = children.parent_key ORDER BY parents.parent_name, children.child_name;"))
        {
            left.Step().Should().Be(StatementStepResult.Row);
            left.GetValue(0).Should().Be(SqlValue.Text("Ada"));
            left.Step().Should().Be(StatementStepResult.Row);
            left.GetValue(0).Should().Be(SqlValue.Text("Ada"));
            left.Step().Should().Be(StatementStepResult.Row);
            left.GetValue(0).Should().Be(SqlValue.Text("Grace"));
            left.GetValue(1).Should().Be(SqlValue.Null);
            left.Step().Should().Be(StatementStepResult.Done);
        }

        Execute(connection, "CREATE TABLE people(person_id INTEGER, parent_id INTEGER, person_name TEXT);");
        Execute(connection, "INSERT INTO people VALUES (1, NULL, 'Ada'), (2, 1, 'Bob');");
        using var selfJoin = connection.Prepare(
            "SELECT child.person_name, parent.person_name FROM people AS child " +
            "JOIN people AS parent ON child.parent_id = parent.person_id;");
        selfJoin.Step().Should().Be(StatementStepResult.Row);
        selfJoin.GetValue(0).Should().Be(SqlValue.Text("Bob"));
        selfJoin.GetValue(1).Should().Be(SqlValue.Text("Ada"));
        selfJoin.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void AlterTableCanAddNullableColumnsAndRenameTables()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE users(id INTEGER);");
        Execute(connection, "INSERT INTO users VALUES (1);");
        Execute(connection, "ALTER TABLE users ADD COLUMN name TEXT;");
        Execute(connection, "ALTER TABLE users RENAME TO people;");
        Execute(connection, "ALTER TABLE people RENAME COLUMN name TO display_name;");

        using var select = connection.Prepare("SELECT id, display_name FROM people;");
        select.Step().Should().Be(StatementStepResult.Row);
        select.GetValue(0).Should().Be(SqlValue.Integer(1));
        select.GetValue(1).Should().Be(SqlValue.Null);
        select.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void ColumnDefaultsApplyToOmittedInsertValuesAndAddedColumns()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE users(id INTEGER, name TEXT DEFAULT 'unknown');");
        Execute(connection, "INSERT INTO users(id) VALUES (1);");
        Execute(connection, "ALTER TABLE users ADD COLUMN active INTEGER NOT NULL DEFAULT 1;");

        using var select = connection.Prepare("SELECT id, name, active FROM users;");
        select.Step().Should().Be(StatementStepResult.Row);
        select.GetValue(0).Should().Be(SqlValue.Integer(1));
        select.GetValue(1).Should().Be(SqlValue.Text("unknown"));
        select.GetValue(2).Should().Be(SqlValue.Integer(1));
    }

    [Test]
    public void SqliteMasterExposesManagedTableCatalogMetadata()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE users(id INTEGER PRIMARY KEY, name TEXT DEFAULT 'unknown');");
        using var statement = connection.Prepare(
            "SELECT name, type, sql FROM sqlite_master WHERE type = 'table' AND name = 'users';");

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Text("users"));
        statement.GetValue(1).Should().Be(SqlValue.Text("table"));
        statement.GetValue(2).AsText().Should().Contain("CREATE TABLE users");
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void UpdateAndDeleteApplyWhereClausesAtomically()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE values_table(id INTEGER, value INTEGER);");
        Execute(connection, "INSERT INTO values_table VALUES (1, 10), (2, 20), (3, 30);");

        using (var update = connection.Prepare("UPDATE values_table SET value = value + 1 WHERE id <= 2;"))
        {
            update.Step().Should().Be(StatementStepResult.Done);
            update.RowsAffected.Should().Be(2);
        }

        using (var delete = connection.Prepare("DELETE FROM values_table WHERE value > 20;"))
        {
            delete.Step().Should().Be(StatementStepResult.Done);
            delete.RowsAffected.Should().Be(2);
        }

        using var select = connection.Prepare("SELECT id, value FROM values_table;");
        select.Step().Should().Be(StatementStepResult.Row);
        select.GetValue(0).Should().Be(SqlValue.Integer(1));
        select.GetValue(1).Should().Be(SqlValue.Integer(11));
        select.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void ConcatenationPropagatesNullAndReturnsText()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare("SELECT 'Hello' || ' ' || 'World', 'Hello' || NULL, x'01' || x'02';");

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Text("Hello World"));
        statement.GetValue(1).Should().Be(SqlValue.Null);
        statement.GetValue(2).Should().Be(SqlValue.Text("\u0001\u0002"));
    }

    [Test]
    public void ScalarFunctionsFollowSqliteValueSemantics()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare(
            "SELECT abs(-2), coalesce(NULL, 'Ada'), hex(x'0102'), ifnull(NULL, 2), length('Ada'), lower('ADA'), typeof(x'01'), upper('ada');");

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(2));
        statement.GetValue(1).Should().Be(SqlValue.Text("Ada"));
        statement.GetValue(2).Should().Be(SqlValue.Text("0102"));
        statement.GetValue(3).Should().Be(SqlValue.Integer(2));
        statement.GetValue(4).Should().Be(SqlValue.Integer(3));
        statement.GetValue(5).Should().Be(SqlValue.Text("ada"));
        statement.GetValue(6).Should().Be(SqlValue.Text("blob"));
        statement.GetValue(7).Should().Be(SqlValue.Text("ADA"));
    }

    [Test]
    public void OrderByIsAppliedBeforeLimit()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE values_table(value INTEGER);");
        Execute(connection, "INSERT INTO values_table VALUES (1), (3), (2);");
        using var statement = connection.Prepare("SELECT value FROM values_table ORDER BY value DESC LIMIT 2;");

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(3));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(2));
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void DistinctAndLimitOffsetsAreAppliedToResultRows()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE values_table(value INTEGER);");
        Execute(connection, "INSERT INTO values_table VALUES (1), (1), (2), (3);");
        using var offset = connection.Prepare(
            "SELECT DISTINCT value FROM values_table ORDER BY value LIMIT 1 OFFSET 1;");

        offset.Step().Should().Be(StatementStepResult.Row);
        offset.GetValue(0).Should().Be(SqlValue.Integer(2));
        offset.Step().Should().Be(StatementStepResult.Done);

        using var commaOffset = connection.Prepare(
            "SELECT DISTINCT value FROM values_table ORDER BY value LIMIT 2, 1;");
        commaOffset.Step().Should().Be(StatementStepResult.Row);
        commaOffset.GetValue(0).Should().Be(SqlValue.Integer(3));
        commaOffset.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void SqlScriptsIgnoreTrailingComments()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        var statements = connection.PrepareScript("SELECT 1; -- trailing comment");

        statements.Should().HaveCount(1);
        using var statement = statements[0];
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(1));
    }

    [Test]
    public void InsertWithDuplicateTargetColumnsUsesTheFirstValue()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE values_table(first_value INTEGER, second_value INTEGER);");
        Execute(connection, "INSERT INTO values_table(first_value, first_value) VALUES (1, 2);");
        using var statement = connection.Prepare("SELECT first_value, second_value FROM values_table;");

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(1));
        statement.GetValue(1).Should().Be(SqlValue.Null);
    }

    [Test]
    public void LimitAcceptsLosslesslyIntegralRealValues()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare("SELECT 1 LIMIT 1.0;");

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(1));
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void OrderByResolvesOutputAliasesAndOrdinals()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE values_table(value INTEGER);");
        Execute(connection, "INSERT INTO values_table VALUES (2), (1);");

        using (var alias = connection.Prepare("SELECT value AS result FROM values_table ORDER BY result;"))
        {
            alias.Step().Should().Be(StatementStepResult.Row);
            alias.GetValue(0).Should().Be(SqlValue.Integer(1));
        }

        using var ordinal = connection.Prepare("SELECT value FROM values_table ORDER BY 1;");
        ordinal.Step().Should().Be(StatementStepResult.Row);
        ordinal.GetValue(0).Should().Be(SqlValue.Integer(1));
    }

    [Test]
    public void TextArithmeticAndLengthApplySqliteConversions()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare("SELECT '2' + 3, length('😀');");

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(5));
        statement.GetValue(1).Should().Be(SqlValue.Integer(1));
    }

    [Test]
    public void CastLikeInAndBetweenFollowSqliteExpressionSemantics()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare(
            "SELECT CAST('42' AS INTEGER), CAST(7 AS TEXT), CAST('x' AS BLOB), " +
            "'Ada' LIKE 'a_a', 'a_b' LIKE 'a!_b' ESCAPE '!', " +
            "2 IN (1, 2, NULL), 3 IN (1, NULL), 2 NOT BETWEEN 3 AND 4;");

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(42));
        statement.GetValue(1).Should().Be(SqlValue.Text("7"));
        statement.GetValue(2).Should().Be(SqlValue.Blob("x"u8));
        statement.GetValue(3).Should().Be(SqlValue.Integer(1));
        statement.GetValue(4).Should().Be(SqlValue.Integer(1));
        statement.GetValue(5).Should().Be(SqlValue.Integer(1));
        statement.GetValue(6).Should().Be(SqlValue.Null);
        statement.GetValue(7).Should().Be(SqlValue.Integer(1));
    }

    [Test]
    public void LikeMatchesWildcardsAppearingInTheSubject()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare(
            "SELECT " +
            "'%Bar' LIKE '%', " +
            "'%Bar%' LIKE '%\\%' ESCAPE '\\', " +
            "'B%a%%r%' LIKE '%\\%%' ESCAPE '\\', " +
            "'a%bc' LIKE 'a\\%%c' ESCAPE '\\', " +
            "'_Bar' LIKE '\\_B%' ESCAPE '\\', " +
            "'_B_a_r' LIKE '\\_B%' ESCAPE '\\', " +
            "'%B%a%r' LIKE '\\%B%' ESCAPE '\\', " +
            "'x%' LIKE '\\%' ESCAPE '\\', " +
            "'%x' LIKE '\\%' ESCAPE '\\';");

        statement.Step().Should().Be(StatementStepResult.Row);
        for (var i = 0; i < 7; i++)
        {
            statement.GetValue(i).Should().Be(SqlValue.Integer(1), $"case {i} should match (native sqlite3 verified)");
        }

        statement.GetValue(7).Should().Be(SqlValue.Integer(0));
        statement.GetValue(8).Should().Be(SqlValue.Integer(0));
    }

    [Test]
    public void LimitAndOffsetRejectColumnReferencesLikeNativeSqlite()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(x);");
        Execute(connection, "INSERT INTO t VALUES (1), (2), (3);");
        Execute(connection, "CREATE TABLE a(id);");
        Execute(connection, "INSERT INTO a VALUES (1), (2);");
        Execute(connection, "CREATE TABLE b(nick, sid);");
        Execute(connection, "INSERT INTO b VALUES ('x', 1), ('y', 2);");

        // Native sqlite3 3.53.3 rejects every direct column reference in LIMIT/OFFSET
        // with "no such column: X" - even a correlated outer reference, a reference
        // inside a larger expression, or one on a compound SELECT's LIMIT.
        AssertLimitRejected(connection, "SELECT x FROM t LIMIT t.x;", "no such column: t.x");
        AssertLimitRejected(connection, "SELECT x FROM t LIMIT 1 + t.x;", "no such column: t.x");
        AssertLimitRejected(connection, "SELECT x FROM t LIMIT abs(t.x);", "no such column: t.x");
        AssertLimitRejected(connection, "SELECT x FROM t LIMIT 1 OFFSET t.x;", "no such column: t.x");
        AssertLimitRejected(
            connection,
            "SELECT id FROM a WHERE (SELECT nick FROM b WHERE b.sid = a.id ORDER BY nick LIMIT 1 OFFSET a.id) = 'x';",
            "no such column: a.id");
        AssertLimitRejected(
            connection,
            "SELECT x FROM t UNION ALL SELECT x FROM t LIMIT t.x;",
            "no such column: t.x");

        // Scalar subqueries inside LIMIT stay legal (their columns are scoped by the
        // nested query), as do plain constant expressions.
        using (var statement = connection.Prepare("SELECT x FROM t LIMIT (SELECT 2);"))
        {
            var rows = 0;
            while (statement.Step() == StatementStepResult.Row)
                rows++;
            rows.Should().Be(2);
        }

        using (var statement = connection.Prepare("SELECT x FROM t LIMIT 1 + 1 OFFSET 1;"))
        {
            var rows = 0;
            while (statement.Step() == StatementStepResult.Row)
                rows++;
            rows.Should().Be(2);
        }

        static void AssertLimitRejected(EmbeddedConnection connection, string sql, string message)
        {
            var exception = Assert.Throws<EmbeddedSqlException>(() =>
            {
                using var statement = connection.Prepare(sql);
                while (statement.Step() == StatementStepResult.Row)
                {
                }
            });
            exception!.Message.Should().Be(message);
        }
    }

    [Test]
    public void OrderByTiesFollowPhysicalRowidOrderLikeNativeStableSorter()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT COLLATE NOCASE);");
        // Out-of-rowid-order inserts still scan in the table B-tree's rowid order. ORDER BY
        // ties preserve that physical scan order because the sorter is stable.
        Execute(connection, "INSERT INTO t VALUES (221, 'a'), (203, 'A'), (250, 'b'), (210, 'B');");

        CollectIds(connection, "SELECT id FROM t;").Should().Equal(203, 210, 221, 250);

        // NOCASE ties ('a'='A', 'b'='B') keep scan order instead of breaking by rowid.
        CollectIds(connection, "SELECT id FROM t ORDER BY v;").Should().Equal(203, 221, 210, 250);
        CollectIds(connection, "SELECT id FROM t ORDER BY v DESC;").Should().Equal(210, 250, 203, 221);

        // In-order insertion (scan order == rowid order) keeps the native-visible tie order.
        Execute(connection, "CREATE TABLE u(id INTEGER PRIMARY KEY, v TEXT COLLATE NOCASE);");
        Execute(connection, "INSERT INTO u VALUES (1, 'a'), (2, 'A'), (3, 'b');");
        CollectIds(connection, "SELECT id FROM u ORDER BY v;").Should().Equal(1, 2, 3);

        // Compound ORDER BY ties follow compound materialization order (arm order, then
        // within-arm scan order) instead of an unstable raw List.Sort.
        Execute(connection, "CREATE TABLE c1(id INTEGER PRIMARY KEY, v TEXT COLLATE NOCASE);");
        Execute(connection, "CREATE TABLE c2(id INTEGER PRIMARY KEY, v TEXT COLLATE NOCASE);");
        Execute(connection, "INSERT INTO c1 VALUES (5, 'x'), (2, 'y');");
        Execute(connection, "INSERT INTO c2 VALUES (9, 'X'), (4, 'w');");
        CollectIds(connection, "SELECT id, v FROM c1 UNION ALL SELECT id, v FROM c2 ORDER BY v;")
            .Should().Equal(4, 5, 9, 2);

        static List<long> CollectIds(EmbeddedConnection connection, string sql)
        {
            using var statement = connection.Prepare(sql);
            var ids = new List<long>();
            while (statement.Step() == StatementStepResult.Row)
                ids.Add(statement.GetValue(0).AsInteger());
            return ids;
        }
    }

    [Test]
    public void CaseExpressionsSelectTheFirstMatchingBranch()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare(
            "SELECT CASE WHEN 2 > 3 THEN 'no' WHEN 2 = 2 THEN 'yes' ELSE 'other' END, " +
            "CASE 'Ada' WHEN 'Grace' THEN 1 WHEN 'Ada' THEN 2 END, " +
            "CASE WHEN NULL THEN 1 END;");

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Text("yes"));
        statement.GetValue(1).Should().Be(SqlValue.Integer(2));
        statement.GetValue(2).Should().Be(SqlValue.Null);
    }

    [Test]
    public void BooleanExpressionsImplementSqliteThreeValuedLogic()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare(
            "SELECT NULL IS NULL, NULL IS NOT NULL, 1 AND NULL, 0 AND NULL, 1 OR NULL, 0 OR NULL, NOT 0, NOT NULL;");

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(1));
        statement.GetValue(1).Should().Be(SqlValue.Integer(0));
        statement.GetValue(2).Should().Be(SqlValue.Null);
        statement.GetValue(3).Should().Be(SqlValue.Integer(0));
        statement.GetValue(4).Should().Be(SqlValue.Integer(1));
        statement.GetValue(5).Should().Be(SqlValue.Null);
        statement.GetValue(6).Should().Be(SqlValue.Integer(1));
        statement.GetValue(7).Should().Be(SqlValue.Null);
    }

    [Test]
    public void ColumnConstraintsAreValidatedBeforeInsertingRows()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE values_table(id INTEGER PRIMARY KEY, name TEXT NOT NULL, code TEXT UNIQUE);");
        Execute(connection, "INSERT INTO values_table VALUES (1, 'Ada', 'one');");

        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "INSERT INTO values_table VALUES (1, 'Grace', 'two');"));
        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "INSERT INTO values_table VALUES (2, NULL, 'two');"));
        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "INSERT INTO values_table VALUES (2, 'Grace', 'one');"));

        using var count = connection.Prepare("SELECT COUNT(*) FROM values_table;");
        count.Step().Should().Be(StatementStepResult.Row);
        count.GetValue(0).Should().Be(SqlValue.Integer(1));
    }

    [Test]
    public void DeclaredColumnTypesApplySqliteStorageAffinity()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE values_table(integer_value INTEGER, real_value REAL, text_value TEXT, numeric_value NUMERIC);");
        Execute(connection, "INSERT INTO values_table VALUES ('2', '2', 2, '2.0');");
        using var statement = connection.Prepare("SELECT typeof(integer_value), typeof(real_value), typeof(text_value), typeof(numeric_value) FROM values_table;");

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Text("integer"));
        statement.GetValue(1).Should().Be(SqlValue.Text("real"));
        statement.GetValue(2).Should().Be(SqlValue.Text("text"));
        statement.GetValue(3).Should().Be(SqlValue.Text("integer"));
    }

    [Test]
    public void CreateAndDropTableHonorExistenceGuards()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE values_table(value INTEGER);");
        Execute(connection, "CREATE TABLE IF NOT EXISTS values_table(value INTEGER);");
        Execute(connection, "DROP TABLE IF EXISTS values_table;");
        Execute(connection, "DROP TABLE IF EXISTS values_table;");
        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "DROP TABLE values_table;"));
    }

    [Test]
    public void TypeParametersDoNotSuppressFollowingConstraints()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE values_table(value VARCHAR(10) NOT NULL);");

        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "INSERT INTO values_table VALUES (NULL);"));
    }

    [Test]
    public void ScalarSubqueriesCorrelateToTheirOuterRows()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE scores(id INTEGER, group_id INTEGER, score INTEGER);");
        Execute(connection, "INSERT INTO scores VALUES (1, 1, 3), (2, 1, 7), (3, 2, 5);");
        using var statement = connection.Prepare(
            "SELECT outer_scores.id, " +
            "(SELECT max(inner_scores.score) FROM scores AS inner_scores " +
            "WHERE inner_scores.group_id = outer_scores.group_id), " +
            "(SELECT score FROM scores WHERE group_id = 99) " +
            "FROM scores AS outer_scores ORDER BY outer_scores.id;");

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(1));
        statement.GetValue(1).Should().Be(SqlValue.Integer(7));
        statement.GetValue(2).Should().Be(SqlValue.Null);
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(2));
        statement.GetValue(1).Should().Be(SqlValue.Integer(7));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(3));
        statement.GetValue(1).Should().Be(SqlValue.Integer(5));
        statement.Step().Should().Be(StatementStepResult.Done);
        Assert.Throws<EmbeddedSqlException>(() =>
        {
            using var invalid = connection.Prepare("SELECT (SELECT id, group_id FROM scores);");
            invalid.Step();
        });
    }

    [Test]
    public void ExistsAndNotExistsFilterCorrelatedSubqueries()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE parents(id INTEGER);");
        Execute(connection, "CREATE TABLE children(parent_id INTEGER);");
        Execute(connection, "INSERT INTO parents VALUES (1), (2), (3);");
        Execute(connection, "INSERT INTO children VALUES (1), (1), (3);");

        using (var exists = connection.Prepare(
                   "SELECT parents.id FROM parents WHERE EXISTS " +
                   "(SELECT 1 FROM children WHERE children.parent_id = parents.id) ORDER BY parents.id;"))
        {
            exists.Step().Should().Be(StatementStepResult.Row);
            exists.GetValue(0).Should().Be(SqlValue.Integer(1));
            exists.Step().Should().Be(StatementStepResult.Row);
            exists.GetValue(0).Should().Be(SqlValue.Integer(3));
            exists.Step().Should().Be(StatementStepResult.Done);
        }

        using var notExists = connection.Prepare(
            "SELECT parents.id FROM parents WHERE NOT EXISTS " +
            "(SELECT 1 FROM children WHERE children.parent_id = parents.id);");
        notExists.Step().Should().Be(StatementStepResult.Row);
        notExists.GetValue(0).Should().Be(SqlValue.Integer(2));
        notExists.Step().Should().Be(StatementStepResult.Done);

        using var inSubquery = connection.Prepare(
            "SELECT parents.id FROM parents WHERE parents.id IN (SELECT parent_id FROM children) ORDER BY parents.id;");
        inSubquery.Step().Should().Be(StatementStepResult.Row);
        inSubquery.GetValue(0).Should().Be(SqlValue.Integer(1));
        inSubquery.Step().Should().Be(StatementStepResult.Row);
        inSubquery.GetValue(0).Should().Be(SqlValue.Integer(3));
        inSubquery.Step().Should().Be(StatementStepResult.Done);
    }

    // Regression for the EF Northwind shape: correlated EXISTS plus a correlated
    // COUNT over a grouped join, with no usable persisted index. Without the
    // statement-scoped transient equality lookup this re-scanned the details table
    // for every outer row (quadratic; ~85s at this scale, well under 1s with it).
    [Test]
    public void CorrelatedJoinSubqueriesReuseTransientLookups()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY, placed TEXT);");
        Execute(connection, "CREATE TABLE products(id INTEGER PRIMARY KEY, name TEXT);");
        Execute(connection, "CREATE TABLE details(order_id INTEGER, product_id INTEGER);");
        for (var i = 1; i <= 800; i++)
        {
            Execute(connection, $"INSERT INTO orders VALUES ({i}, '2024-01-01');");
            Execute(connection, $"INSERT INTO details VALUES ({i}, {i % 40 + 1}), ({i}, {i % 40 + 2}), ({i}, {i % 40 + 3});");
        }

        for (var p = 1; p <= 43; p++)
        {
            Execute(connection, $"INSERT INTO products VALUES ({p}, 'p{p}');");
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var statement = connection.Prepare(
            "SELECT o.id, " +
            "CASE WHEN EXISTS (SELECT 1 FROM details AS d WHERE d.order_id = o.id AND d.product_id < 25) " +
            "THEN 1 ELSE 0 END, " +
            "CASE WHEN (SELECT COUNT(*) FROM (" +
            "SELECT 1 AS marker FROM details AS d2 " +
            "INNER JOIN products AS p ON d2.product_id = p.id " +
            "WHERE d2.order_id = o.id AND d2.product_id < 25 " +
            "GROUP BY p.name) AS grouped) > 1 THEN 1 ELSE 0 END " +
            "FROM orders AS o WHERE o.placed IS NOT NULL;");
        var rows = 0;
        var existsSum = 0;
        var multiGroupSum = 0;
        while (statement.Step() == StatementStepResult.Row)
        {
            rows++;
            existsSum += (int)statement.GetValue(1).AsInteger();
            multiGroupSum += (int)statement.GetValue(2).AsInteger();
        }

        stopwatch.Stop();
        rows.Should().Be(800);
        // Order i has details for products i%40+1..+3: a product < 25 exists for i%40 in 0..23,
        // and more than one such product for i%40 in 0..22.
        existsSum.Should().Be(480);
        multiGroupSum.Should().Be(460);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    // The transient lookup must not serve stale rows: an UPDATE mutating the probed
    // table mid-statement bumps the row-store revision, and the next correlated
    // evaluation rebuilds its buckets instead of reusing the pre-mutation map.
    [Test]
    public void CorrelatedSubqueryLookupRebuildsAfterSameStatementMutation()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE counters(id INTEGER PRIMARY KEY, qty INTEGER);");
        Execute(connection, "INSERT INTO counters VALUES (1, 10), (2, 20), (3, 30);");

        Execute(connection,
            "UPDATE counters SET qty = qty + (SELECT COUNT(*) FROM counters AS c2 WHERE c2.id = counters.id);");

        using var statement = connection.Prepare("SELECT qty FROM counters ORDER BY id;");
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(11));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(21));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(31));
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    // Regression for the EF Northwind Navigations/SplitInclude solo stall (#8c): a single-table
    // WHERE conjunct on the preserved side of a LEFT join over a CROSS join must be pushed into
    // the base scan, not materialized as the full LxR cartesian first (native SQLite pushes the
    // constant filter down). The ROW_NUMBER window keeps the query on the interpreted GetJoinRows
    // path (the compiled join route declines window functions) where GetJoinRowsWithPredicatePushdown
    // /GetSideSourceRows apply. Without the pushdown the 2490x830 pair materialization blows the
    // time bound; with it the details scan probes only the ~3 rows matching order_id = 1.
    [Test]
    public void LeftJoinOverCrossJoinPushesSingleTablePredicateIntoBaseScan()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE orders(id INTEGER PRIMARY KEY, customer_id INTEGER);");
        Execute(connection, "CREATE TABLE customers(id INTEGER PRIMARY KEY, name TEXT);");
        Execute(connection, "CREATE TABLE details(order_id INTEGER, product_id INTEGER);");
        Execute(connection, "INSERT INTO orders SELECT value, (value % 91) + 1 FROM generate_series(1, 830, 1);");
        Execute(connection, "INSERT INTO customers SELECT value, 'c' FROM generate_series(1, 91, 1);");
        Execute(connection, "INSERT INTO details SELECT (value % 830) + 1, value FROM generate_series(1, 2490, 1);");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var statement = connection.Prepare(
            "SELECT COUNT(*) FROM (" +
            "SELECT o2.id, ROW_NUMBER() OVER (PARTITION BY o2.id ORDER BY o2.id) AS rn " +
            "FROM details AS o1 CROSS JOIN orders AS o2 " +
            "LEFT JOIN customers AS c ON o2.customer_id = c.id " +
            "WHERE o1.order_id = 1) AS t;");
        statement.Step().Should().Be(StatementStepResult.Row);
        var count = statement.GetValue(0).AsInteger();
        stopwatch.Stop();

        // order_id = 1 matches details rows 830/1660/2490 -> 3 rows x 830 orders.
        count.Should().Be(2490);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    // Correctness guard for the LEFT-join pushdown: a conjunct pushed into the preserved (left)
    // side must not convert the outer join to an inner one - the null-supplying (right) side
    // still pads NULLs for preserved rows with no match. The window function forces the
    // interpreted path where the pushdown lives.
    [Test]
    public void LeftJoinPushdownPreservesNullPadding()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE a(id INTEGER PRIMARY KEY, x INTEGER);");
        Execute(connection, "CREATE TABLE b(id INTEGER PRIMARY KEY, aid INTEGER, y TEXT);");
        Execute(connection, "INSERT INTO a VALUES (1, 10), (2, 20), (3, 30);");
        Execute(connection, "INSERT INTO b VALUES (1, 1, 'one'), (2, 3, 'three');");

        var rows = ReadRows(connection,
            "SELECT a.id, b.y, ROW_NUMBER() OVER (ORDER BY a.id) AS rn " +
            "FROM a LEFT JOIN b ON b.aid = a.id WHERE a.x >= 20 ORDER BY a.id;");

        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Integer(2), SqlValue.Null, SqlValue.Integer(1));
        rows[1].Should().Equal(SqlValue.Integer(3), SqlValue.Text("three"), SqlValue.Integer(2));
    }

    // Regression for the EF ComplexTypeQuery hang: DISTINCT over a derived table over
    // a UNION with wide projections. Collation resolution re-derived each source's full
    // collation scope once per output column at every nesting level, multiplying cost
    // by the column count each level - 18 output columns already exceeded 60s before
    // the statement-scoped memoization; EF's 42-column query never finished.
    [Test]
    public void DistinctOverUnionDerivedTablesResolveCollisionsOncePerSource()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        const int textColumns = 8;
        var rawNames = Enumerable.Range(1, textColumns).Select(i => $"T{i}").ToArray();
        Execute(connection,
            $"CREATE TABLE u(id INTEGER PRIMARY KEY, {string.Join(", ", rawNames.Select(n => $"{n} TEXT"))});");
        Execute(connection, "INSERT INTO u(id) VALUES (1), (2);");

        string Side(string a, string b) =>
            $"SELECT {a}.id, {string.Join(", ", rawNames.Select(n => $"{a}.{n}"))}, {b}.id AS id0, " +
            $"{string.Join(", ", rawNames.Select(n => $"{b}.{n} AS {n}0"))} FROM u AS {a} CROSS JOIN u AS {b}";
        string Mid(string a) =>
            string.Join(", ", new[] { $"{a}.id" }
                .Concat(rawNames.Select(n => $"{a}.{n}"))
                .Concat(new[] { $"{a}.id0" })
                .Concat(rawNames.Select(n => $"{a}.{n}0")));
        var query =
            $"SELECT {Mid("o1")} FROM (" +
            $"SELECT DISTINCT {Mid("o0")} FROM (" +
            $"SELECT {Mid("o")} FROM ({Side("c", "c0")} UNION {Side("c1", "c2")}) AS o " +
            $"ORDER BY o.id, o.id0 LIMIT 3) AS o0) AS o1 ORDER BY o1.id, o1.id0 LIMIT 3;";

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var statement = connection.Prepare(query);
        var rows = 0;
        while (statement.Step() == StatementStepResult.Row)
        {
            rows++;
        }

        stopwatch.Stop();
        rows.Should().Be(3);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    [Test]
    public void DerivedTablesExposeProjectedColumnsToOuterQueries()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE scores(id INTEGER, score INTEGER);");
        Execute(connection, "INSERT INTO scores VALUES (1, 3), (2, 7), (3, 5);");
        using var statement = connection.Prepare(
            "SELECT derived_scores.id, derived_scores.doubled " +
            "FROM (SELECT id, score * 2 AS doubled FROM scores WHERE score > 2) AS derived_scores " +
            "WHERE derived_scores.doubled >= 10 ORDER BY derived_scores.id;");

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(2));
        statement.GetValue(1).Should().Be(SqlValue.Integer(14));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(3));
        statement.GetValue(1).Should().Be(SqlValue.Integer(10));
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void CompoundSelectsApplySetOperationsAndResultOrdering()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE left_values(value INTEGER);");
        Execute(connection, "CREATE TABLE right_values(value INTEGER);");
        Execute(connection, "INSERT INTO left_values VALUES (1), (2), (2);");
        Execute(connection, "INSERT INTO right_values VALUES (2), (3);");

        using (var union = connection.Prepare(
                   "SELECT value FROM left_values UNION SELECT value FROM right_values ORDER BY value;"))
        {
            union.Step().Should().Be(StatementStepResult.Row);
            union.GetValue(0).Should().Be(SqlValue.Integer(1));
            union.Step().Should().Be(StatementStepResult.Row);
            union.GetValue(0).Should().Be(SqlValue.Integer(2));
            union.Step().Should().Be(StatementStepResult.Row);
            union.GetValue(0).Should().Be(SqlValue.Integer(3));
            union.Step().Should().Be(StatementStepResult.Done);
        }

        using (var unionAll = connection.Prepare(
                   "SELECT value FROM left_values UNION ALL SELECT value FROM right_values ORDER BY value;"))
        {
            var values = new List<SqlValue>();
            while (unionAll.Step() == StatementStepResult.Row)
                values.Add(unionAll.GetValue(0));

            values.Should().Equal(
                SqlValue.Integer(1),
                SqlValue.Integer(2),
                SqlValue.Integer(2),
                SqlValue.Integer(2),
                SqlValue.Integer(3));
        }

        using (var intersect = connection.Prepare(
                   "SELECT value FROM left_values INTERSECT SELECT value FROM right_values;"))
        {
            intersect.Step().Should().Be(StatementStepResult.Row);
            intersect.GetValue(0).Should().Be(SqlValue.Integer(2));
            intersect.Step().Should().Be(StatementStepResult.Done);
        }

        using var except = connection.Prepare(
            "SELECT value FROM left_values EXCEPT SELECT value FROM right_values;");
        except.Step().Should().Be(StatementStepResult.Row);
        except.GetValue(0).Should().Be(SqlValue.Integer(1));
        except.Step().Should().Be(StatementStepResult.Done);
        Assert.Throws<EmbeddedSqlException>(() => connection.Prepare("SELECT 1 UNION SELECT 1, 2;").Step());
    }

    [Test]
    public void CommonTableExpressionsMaterializeEarlierCtesAndSupportRecursion()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE scores(score INTEGER);");
        Execute(connection, "INSERT INTO scores VALUES (3), (5), (7);");
        using var statement = connection.Prepare(
            "WITH high_scores(score) AS (SELECT score FROM scores WHERE score >= 5), " +
            "adjusted AS (SELECT score + 1 AS score FROM high_scores UNION ALL SELECT 10) " +
            "SELECT score FROM adjusted ORDER BY score;");

        statement.GetColumnName(0).Should().Be("score");
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(6));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(8));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(10));
        statement.Step().Should().Be(StatementStepResult.Done);

        using var nonRecursive = connection.Prepare(
            "WITH RECURSIVE numbers(value) AS (SELECT 1) SELECT value FROM numbers;");
        nonRecursive.GetColumnName(0).Should().Be("value");
        nonRecursive.Step().Should().Be(StatementStepResult.Row);
        nonRecursive.GetValue(0).Should().Be(SqlValue.Integer(1));
        nonRecursive.Step().Should().Be(StatementStepResult.Done);

        using var recursive = connection.Prepare(
            "WITH RECURSIVE counter(value) AS " +
            "(SELECT 1 UNION ALL SELECT value + 1 FROM counter WHERE value < 3) " +
            "SELECT value FROM counter ORDER BY value;");
        recursive.Step().Should().Be(StatementStepResult.Row);
        recursive.GetValue(0).Should().Be(SqlValue.Integer(1));
        recursive.Step().Should().Be(StatementStepResult.Row);
        recursive.GetValue(0).Should().Be(SqlValue.Integer(2));
        recursive.Step().Should().Be(StatementStepResult.Row);
        recursive.GetValue(0).Should().Be(SqlValue.Integer(3));
        recursive.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void DerivedTablesAndNestedCtesRetainOuterQueryScope()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE values_table(x INTEGER);");
        Execute(connection, "INSERT INTO values_table VALUES (1);");

        using (var correlated = connection.Prepare(
                   "SELECT x, (SELECT y FROM (SELECT x + 1 AS y)) FROM values_table;"))
        {
            correlated.Step().Should().Be(StatementStepResult.Row);
            correlated.GetValue(0).Should().Be(SqlValue.Integer(1));
            correlated.GetValue(1).Should().Be(SqlValue.Integer(2));
            correlated.Step().Should().Be(StatementStepResult.Done);
        }

        using var nestedCte = connection.Prepare(
            "WITH c AS (SELECT 1 AS x) SELECT (WITH c AS (SELECT 2 AS x) SELECT x FROM c);");
        nestedCte.Step().Should().Be(StatementStepResult.Row);
        nestedCte.GetValue(0).Should().Be(SqlValue.Integer(2));
        nestedCte.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void LimitZeroStillValidatesColumnsAndDistinctUsesSqlEquality()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE values_table(value INTEGER);");
        Execute(connection, "INSERT INTO values_table VALUES (1);");

        Assert.Throws<EmbeddedSqlException>(() => connection.Prepare("SELECT missing FROM values_table LIMIT 0;").Step());
        Assert.Throws<EmbeddedSqlException>(() => connection.Prepare("SELECT value FROM values_table WHERE missing LIMIT 0;").Step());
        Assert.Throws<EmbeddedSqlException>(() => connection.Prepare("SELECT value FROM values_table ORDER BY missing LIMIT 0;").Step());

        using (var numericDistinct = connection.Prepare(
                   "SELECT DISTINCT x FROM (SELECT 1 AS x UNION ALL SELECT 1.0);"))
        {
            numericDistinct.Step().Should().Be(StatementStepResult.Row);
            numericDistinct.Step().Should().Be(StatementStepResult.Done);
        }

        using var collationUnion = connection.Prepare("SELECT 'a' COLLATE NOCASE UNION SELECT 'A';");
        collationUnion.Step().Should().Be(StatementStepResult.Row);
        collationUnion.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void CompoundOrderByResolvesAliasesFromEveryTerm()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare("SELECT 2 AS a UNION SELECT 1 AS b ORDER BY b;");

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(1));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(2));
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void CreateIndexRegistersCatalogMetadataAndExposesItThroughPragmas()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT, c TEXT);");
        Execute(connection, "CREATE INDEX idx_a ON t(a);");
        Execute(connection, "CREATE UNIQUE INDEX idx_bc ON t(b, c DESC);");

        using (var master = connection.Prepare(
            "SELECT type, name, tbl_name, sql FROM sqlite_master WHERE type = 'index' ORDER BY name;"))
        {
            master.Step().Should().Be(StatementStepResult.Row);
            master.GetValue(0).Should().Be(SqlValue.Text("index"));
            master.GetValue(1).Should().Be(SqlValue.Text("idx_a"));
            master.GetValue(2).Should().Be(SqlValue.Text("t"));
            master.GetValue(3).AsText().Should().Be("CREATE INDEX idx_a ON t(a)");
            master.Step().Should().Be(StatementStepResult.Row);
            master.GetValue(1).Should().Be(SqlValue.Text("idx_bc"));
            master.GetValue(3).AsText().Should().Be("CREATE UNIQUE INDEX idx_bc ON t(b, c DESC)");
            master.Step().Should().Be(StatementStepResult.Done);
        }

        using (var list = connection.Prepare("PRAGMA index_list(t);"))
        {
            list.GetColumnName(0).Should().Be("seq");
            list.GetColumnName(2).Should().Be("unique");
            list.GetColumnName(3).Should().Be("origin");
            list.GetColumnName(4).Should().Be("partial");

            list.Step().Should().Be(StatementStepResult.Row);
            list.GetValue(0).Should().Be(SqlValue.Integer(0));
            list.GetValue(1).Should().Be(SqlValue.Text("idx_bc"));
            list.GetValue(2).Should().Be(SqlValue.Integer(1));
            list.GetValue(3).Should().Be(SqlValue.Text("c"));
            list.GetValue(4).Should().Be(SqlValue.Integer(0));
            list.Step().Should().Be(StatementStepResult.Row);
            list.GetValue(0).Should().Be(SqlValue.Integer(1));
            list.GetValue(1).Should().Be(SqlValue.Text("idx_a"));
            list.GetValue(2).Should().Be(SqlValue.Integer(0));
            list.Step().Should().Be(StatementStepResult.Done);
        }

        using var info = connection.Prepare("PRAGMA index_info(idx_bc);");
        info.GetColumnName(0).Should().Be("seqno");
        info.GetColumnName(1).Should().Be("cid");
        info.GetColumnName(2).Should().Be("name");
        info.Step().Should().Be(StatementStepResult.Row);
        info.GetValue(0).Should().Be(SqlValue.Integer(0));
        info.GetValue(1).Should().Be(SqlValue.Integer(1));
        info.GetValue(2).Should().Be(SqlValue.Text("b"));
        info.Step().Should().Be(StatementStepResult.Row);
        info.GetValue(0).Should().Be(SqlValue.Integer(1));
        info.GetValue(1).Should().Be(SqlValue.Integer(2));
        info.GetValue(2).Should().Be(SqlValue.Text("c"));
        info.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void UniqueIndexEnforcesMultiColumnConstraintWithNullDistinctSemantics()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT);");
        Execute(connection, "CREATE UNIQUE INDEX idx_ab ON t(a, b);");
        Execute(connection, "INSERT INTO t VALUES (1, 'x');");
        Execute(connection, "INSERT INTO t VALUES (1, 'y');");
        Execute(connection, "INSERT INTO t VALUES (1, NULL);");
        Execute(connection, "INSERT INTO t VALUES (1, NULL);");

        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "INSERT INTO t VALUES (1, 'x');"))!
            .Message.Should().Be("UNIQUE constraint failed: t.a, t.b");

        using var count = connection.Prepare("SELECT COUNT(*) FROM t;");
        count.Step().Should().Be(StatementStepResult.Row);
        count.GetValue(0).Should().Be(SqlValue.Integer(4));
    }

    [Test]
    public void UniqueIndexIsValidatedAgainstExistingRowsAtCreationTime()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (1);");

        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "CREATE UNIQUE INDEX idx_a ON t(a);"))!
            .Message.Should().Be("UNIQUE constraint failed: t.a");

        // The failed index must not have been registered.
        using var list = connection.Prepare("PRAGMA index_list(t);");
        list.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void UniqueIndexEnforcementAppliesToUpdates()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2);");
        Execute(connection, "CREATE UNIQUE INDEX idx_a ON t(a);");

        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "UPDATE t SET a = 1 WHERE a = 2;"))!
            .Message.Should().Be("UNIQUE constraint failed: t.a");
    }

    [Test]
    public void UniqueIndexHonorsColumnCollation()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a TEXT);");
        Execute(connection, "CREATE UNIQUE INDEX idx_a ON t(a COLLATE NOCASE);");
        Execute(connection, "INSERT INTO t VALUES ('abc');");

        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "INSERT INTO t VALUES ('ABC');"));

        using var count = connection.Prepare("SELECT COUNT(*) FROM t;");
        count.Step().Should().Be(StatementStepResult.Row);
        count.GetValue(0).Should().Be(SqlValue.Integer(1));
    }

    [Test]
    public void SqliteNoCaseIndexesFoldOnlyAsciiAndShareTheSchemaNamespace()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a TEXT);");
        Execute(connection, "CREATE UNIQUE INDEX idx_a ON t(a COLLATE NOCASE);");
        Execute(connection, "INSERT INTO t VALUES ('Ä'), ('ä');");

        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "CREATE TABLE idx_a(value INTEGER);"))!
            .Message.Should().Be("there is already an index named idx_a");
        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "ALTER TABLE t RENAME TO idx_a;"))!
            .Message.Should().Be("there is already an index named idx_a");
    }

    [Test]
    public void DropIndexRemovesMetadataAndStopsEnforcement()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE UNIQUE INDEX idx_a ON t(a);");
        Execute(connection, "INSERT INTO t VALUES (1);");
        Execute(connection, "DROP INDEX idx_a;");

        Execute(connection, "INSERT INTO t VALUES (1);");
        using (var list = connection.Prepare("PRAGMA index_list(t);"))
            list.Step().Should().Be(StatementStepResult.Done);

        Execute(connection, "DROP INDEX IF EXISTS idx_a;");
        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "DROP INDEX idx_a;"))!
            .Message.Should().Be("no such index: idx_a");
    }

    [Test]
    public void CreateIndexValidatesNamesTablesAndColumns()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE INDEX idx_a ON t(a);");

        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "CREATE INDEX idx_a ON t(a);"))!
            .Message.Should().Be("index idx_a already exists");
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_a ON t(a);");
        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "CREATE INDEX t ON t(a);"))!
            .Message.Should().Be("there is already a table named t");
        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "CREATE INDEX idx_missing ON nope(a);"))!
            .Message.Should().Be("no such table: nope");
        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "CREATE INDEX idx_missing ON t(b);"))!
            .Message.Should().Be("no such column: b");
    }

    [Test]
    public void PartialAndExpressionIndexesEnforceProjectedKeysAndExposeMetadata()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a TEXT, active INTEGER);");
        Execute(
            connection,
            "CREATE UNIQUE INDEX idx ON t(lower(a) COLLATE NOCASE DESC) WHERE active = 1;");
        Execute(connection, "INSERT INTO t VALUES ('Alpha', 0), ('alpha', 0), ('Alpha', 1);");

        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "INSERT INTO t VALUES ('ALPHA', 1);"))!
            .Message.Should().Be("UNIQUE constraint failed: index 'idx'");

        using (var list = connection.Prepare("PRAGMA index_list(t);"))
        {
            list.Step().Should().Be(StatementStepResult.Row);
            list.GetValue(4).Should().Be(SqlValue.Integer(1));
        }
        using (var info = connection.Prepare("PRAGMA index_info(idx);"))
        {
            info.Step().Should().Be(StatementStepResult.Row);
            info.GetValue(1).Should().Be(SqlValue.Integer(-2));
            info.GetValue(2).Should().Be(SqlValue.Null);
        }
        using var schema = connection.Prepare("SELECT sql FROM sqlite_schema WHERE name = 'idx';");
        schema.Step().Should().Be(StatementStepResult.Row);
        schema.GetValue(0).AsText().Should().Be(
            "CREATE UNIQUE INDEX idx ON t(lower(a) COLLATE NOCASE DESC) WHERE active = 1");
    }

    [Test]
    public void PartialAndExpressionIndexesRejectUnsafeDefinitionsBeforePublication()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b INTEGER);");
        connection.RegisterScalarFunction("managed_value", 1, values => values[0]);

        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "CREATE INDEX random_idx ON t(random());"))!
            .Message.Should().Contain("non-deterministic");
        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "CREATE INDEX udf_idx ON t(managed_value(a));"))!
            .Message.Should().Contain("non-deterministic");
        using (var parameterIndex = connection.Prepare("CREATE INDEX parameter_idx ON t(a) WHERE b = ?1;"))
        {
            parameterIndex.Bind(1, SqlValue.Integer(1));
            Assert.Throws<EmbeddedSqlException>(() => parameterIndex.Step())!
                .Message.Should().Contain("parameters are prohibited");
        }
        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "CREATE INDEX collated_idx ON t(a + b COLLATE custom);"))!
            .Message.Should().Contain("not a supported SQLite built-in collation");

        using var list = connection.Prepare("PRAGMA index_list(t);");
        list.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void IndexDefinitionsRollBackWithTheirTransaction()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1);");
        Execute(connection, "BEGIN;");
        Execute(connection, "CREATE UNIQUE INDEX idx_a ON t(a);");
        Execute(connection, "ROLLBACK;");

        // The rolled-back index is gone, so the duplicate is now allowed.
        Execute(connection, "INSERT INTO t VALUES (1);");
        using var list = connection.Prepare("PRAGMA index_list(t);");
        list.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void IndexDefinitionsCommitWithTheirTransaction()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "BEGIN;");
        Execute(connection, "CREATE UNIQUE INDEX idx_a ON t(a);");
        Execute(connection, "INSERT INTO t VALUES (1);");
        Execute(connection, "COMMIT;");

        using (var list = connection.Prepare("PRAGMA index_list(t);"))
        {
            list.Step().Should().Be(StatementStepResult.Row);
            list.GetValue(1).Should().Be(SqlValue.Text("idx_a"));
            list.Step().Should().Be(StatementStepResult.Done);
        }

        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "INSERT INTO t VALUES (1);"))!
            .Message.Should().Be("UNIQUE constraint failed: t.a");
    }

    [Test]
    public void RenamingTableAndColumnUpdatesIndexMetadata()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT);");
        Execute(connection, "CREATE INDEX idx_ab ON t(a, b);");
        Execute(connection, "ALTER TABLE t RENAME TO renamed;");
        Execute(connection, "ALTER TABLE renamed RENAME COLUMN a TO id;");

        using (var master = connection.Prepare(
            "SELECT tbl_name, sql FROM sqlite_master WHERE type = 'index' AND name = 'idx_ab';"))
        {
            master.Step().Should().Be(StatementStepResult.Row);
            master.GetValue(0).Should().Be(SqlValue.Text("renamed"));
            master.GetValue(1).AsText().Should().Be("CREATE INDEX idx_ab ON \"renamed\"(id, b)");
            master.Step().Should().Be(StatementStepResult.Done);
        }

        using var info = connection.Prepare("PRAGMA index_info(idx_ab);");
        info.Step().Should().Be(StatementStepResult.Row);
        info.GetValue(2).Should().Be(SqlValue.Text("id"));
        info.Step().Should().Be(StatementStepResult.Row);
        info.GetValue(2).Should().Be(SqlValue.Text("b"));
        info.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void SavepointRollbackToRestoresTargetAndKeepsSavepoint()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO t VALUES (1);");
        Execute(connection, "SAVEPOINT sp1;");
        Execute(connection, "INSERT INTO t VALUES (2);");
        Execute(connection, "SAVEPOINT sp2;");
        Execute(connection, "INSERT INTO t VALUES (3);");

        Execute(connection, "ROLLBACK TO SAVEPOINT sp2;");
        AssertCount(connection, "SELECT COUNT(*) FROM t;", 2);

        // sp2 is preserved by ROLLBACK TO, so it can be used again after new changes.
        Execute(connection, "INSERT INTO t VALUES (4);");
        Execute(connection, "ROLLBACK TO sp2;");
        AssertCount(connection, "SELECT COUNT(*) FROM t;", 2);

        // Rolling back to the outer savepoint discards everything after row 1.
        Execute(connection, "ROLLBACK TO sp1;");
        AssertCount(connection, "SELECT COUNT(*) FROM t;", 1);

        Execute(connection, "COMMIT;");
        AssertCount(connection, "SELECT COUNT(*) FROM t;", 1);
    }

    [Test]
    public void NestedSavepointReleaseKeepsChangesUntilOuterScopeResolves()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "BEGIN;");
        Execute(connection, "SAVEPOINT sp1;");
        Execute(connection, "INSERT INTO t VALUES (1);");
        Execute(connection, "SAVEPOINT sp2;");
        Execute(connection, "INSERT INTO t VALUES (2);");

        // Releasing the inner savepoint keeps its work but does not commit.
        Execute(connection, "RELEASE SAVEPOINT sp2;");
        AssertCount(connection, "SELECT COUNT(*) FROM t;", 2);

        // The outer savepoint is still active and can undo everything.
        Execute(connection, "ROLLBACK TO sp1;");
        AssertCount(connection, "SELECT COUNT(*) FROM t;", 0);

        Execute(connection, "COMMIT;");
        AssertCount(connection, "SELECT COUNT(*) FROM t;", 0);
    }

    [Test]
    public void DuplicateSavepointNamesTargetMostRecentSavepoint()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO t VALUES (1);");
        Execute(connection, "SAVEPOINT sp;");
        Execute(connection, "INSERT INTO t VALUES (2);");
        Execute(connection, "SAVEPOINT sp;");
        Execute(connection, "INSERT INTO t VALUES (3);");

        // ROLLBACK TO targets the most recent 'sp' (after row 2), discarding row 3.
        Execute(connection, "ROLLBACK TO sp;");
        AssertCount(connection, "SELECT COUNT(*) FROM t;", 2);

        // RELEASE removes the most recent 'sp' only, leaving the first 'sp' active.
        Execute(connection, "RELEASE sp;");
        Execute(connection, "INSERT INTO t VALUES (4);");

        // Now ROLLBACK TO targets the first 'sp' (after row 1), discarding rows 2 and 4.
        Execute(connection, "ROLLBACK TO sp;");
        AssertCount(connection, "SELECT COUNT(*) FROM t;", 1);

        Execute(connection, "COMMIT;");
        AssertCount(connection, "SELECT COUNT(*) FROM t;", 1);
    }

    [Test]
    public void SavepointOutsideTransactionOpensImplicitTransactionThatReleaseCommits()
    {
        var database = new EmbeddedDatabase();
        using var writer = database.Connect();
        Execute(writer, "CREATE TABLE t(value INTEGER);");

        // A bare SAVEPOINT starts a transaction that is invisible to other connections.
        Execute(writer, "SAVEPOINT sp;");
        Execute(writer, "INSERT INTO t VALUES (1);");
        using (var reader = database.Connect())
            AssertCount(reader, "SELECT COUNT(*) FROM t;", 0);

        // Releasing the outermost savepoint commits the implicit transaction.
        Execute(writer, "RELEASE sp;");
        using (var reader = database.Connect())
            AssertCount(reader, "SELECT COUNT(*) FROM t;", 1);
    }

    [Test]
    public void OuterRollbackDiscardsAllSavepointChanges()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO t VALUES (1);");
        Execute(connection, "SAVEPOINT sp1;");
        Execute(connection, "INSERT INTO t VALUES (2);");
        Execute(connection, "SAVEPOINT sp2;");
        Execute(connection, "INSERT INTO t VALUES (3);");

        Execute(connection, "ROLLBACK;");
        AssertCount(connection, "SELECT COUNT(*) FROM t;", 0);

        // The savepoints from the rolled-back transaction no longer exist.
        Execute(connection, "BEGIN;");
        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "ROLLBACK TO sp1;"))!
            .Message.Should().Be("no such savepoint: sp1");
        Execute(connection, "ROLLBACK;");
    }

    [Test]
    public void SavepointOperationsRejectUnknownNames()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "BEGIN;");
        Execute(connection, "SAVEPOINT sp1;");

        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "ROLLBACK TO SAVEPOINT missing;"))!
            .Message.Should().Be("no such savepoint: missing");
        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "RELEASE missing;"))!
            .Message.Should().Be("no such savepoint: missing");
        Execute(connection, "ROLLBACK;");
    }

    [Test]
    public void QualifiedStarProjectsOnlyTheNamedTableColumns()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t1(a INTEGER, b TEXT);");
        Execute(connection, "CREATE TABLE t2(a INTEGER, c TEXT);");
        Execute(connection, "INSERT INTO t1 VALUES (1, 'b1');");
        Execute(connection, "INSERT INTO t2 VALUES (1, 'c1');");

        var names = ColumnNames(connection, "SELECT t1.* FROM t1 JOIN t2 ON t1.a = t2.a;");
        names.Should().Equal("a", "b");

        var rows = ReadRows(connection, "SELECT t1.* FROM t1 JOIN t2 ON t1.a = t2.a;");
        rows.Should().HaveCount(1);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("b1"));

        var mixed = ReadRows(connection, "SELECT t2.*, t1.b FROM t1 JOIN t2 ON t1.a = t2.a;");
        mixed[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("c1"), SqlValue.Text("b1"));
    }

    [Test]
    public void QualifiedStarWithUnknownTableRaisesClearError()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t1(a INTEGER);");

        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "SELECT foo.* FROM t1;"))!
            .Message.Should().Be("no such table: foo");
    }

    [Test]
    public void JoinUsingCoalescesTheSharedColumnAndOrdersOutput()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t1(a INTEGER, b TEXT);");
        Execute(connection, "CREATE TABLE t2(a INTEGER, c TEXT);");
        Execute(connection, "INSERT INTO t1 VALUES (1, 'b1'), (2, 'b2');");
        Execute(connection, "INSERT INTO t2 VALUES (1, 'c1');");

        var names = ColumnNames(connection, "SELECT * FROM t1 JOIN t2 USING(a);");
        names.Should().Equal("a", "b", "c");

        var rows = ReadRows(connection, "SELECT * FROM t1 JOIN t2 USING(a);");
        rows.Should().HaveCount(1);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("b1"), SqlValue.Text("c1"));

        // The coalesced column keeps its position within the left table.
        Execute(connection, "CREATE TABLE t3(b TEXT, a INTEGER);");
        Execute(connection, "INSERT INTO t3 VALUES ('x', 1);");
        ColumnNames(connection, "SELECT * FROM t3 JOIN t2 USING(a);").Should().Equal("b", "a", "c");
    }

    [Test]
    public void LeftJoinUsingResolvesUnqualifiedColumnToSurvivingSide()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t1(a INTEGER, b TEXT);");
        Execute(connection, "CREATE TABLE t2(a INTEGER, c TEXT);");
        Execute(connection, "INSERT INTO t1 VALUES (1, 'b1'), (2, 'b2');");
        Execute(connection, "INSERT INTO t2 VALUES (1, 'c1');");

        var rows = ReadRows(connection, "SELECT a, b, c FROM t1 LEFT JOIN t2 USING(a) ORDER BY a;");
        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("b1"), SqlValue.Text("c1"));
        rows[1].Should().Equal(SqlValue.Integer(2), SqlValue.Text("b2"), SqlValue.Null);
    }

    [Test]
    public void JoinUsingWithMissingColumnRaisesClearError()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t1(a INTEGER);");
        Execute(connection, "CREATE TABLE t2(x INTEGER);");

        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "SELECT * FROM t1 JOIN t2 USING(a);"))!
            .Message.Should().Be("cannot join using column a - column not present in both tables");
    }

    [Test]
    public void RightOuterJoinPadsUnmatchedLeftColumnsWithNull()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t1(a INTEGER, b TEXT);");
        Execute(connection, "CREATE TABLE t2(a INTEGER, c TEXT);");
        Execute(connection, "INSERT INTO t1 VALUES (1, 'b1'), (2, 'b2');");
        Execute(connection, "INSERT INTO t2 VALUES (2, 'c2'), (1, 'c1'), (4, 'c4');");

        var rows = ReadRows(
            connection,
            "SELECT t1.a, b, t2.a, c FROM t1 RIGHT JOIN t2 ON t1.a = t2.a;");
        rows.Should().HaveCount(3);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("b1"), SqlValue.Integer(1), SqlValue.Text("c1"));
        rows[1].Should().Equal(SqlValue.Integer(2), SqlValue.Text("b2"), SqlValue.Integer(2), SqlValue.Text("c2"));
        rows[2].Should().Equal(SqlValue.Null, SqlValue.Null, SqlValue.Integer(4), SqlValue.Text("c4"));
    }

    [Test]
    public void FullOuterJoinReturnsUnmatchedRowsFromBothSides()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t1(a INTEGER, b TEXT);");
        Execute(connection, "CREATE TABLE t2(a INTEGER, c TEXT);");
        Execute(connection, "INSERT INTO t1 VALUES (1, 'b1'), (2, 'b2'), (3, 'b3');");
        Execute(connection, "INSERT INTO t2 VALUES (2, 'c2'), (1, 'c1'), (4, 'c4');");

        var rows = ReadRows(
            connection,
            "SELECT t1.a, b, t2.a, c FROM t1 FULL OUTER JOIN t2 ON t1.a = t2.a;");
        rows.Should().HaveCount(4);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("b1"), SqlValue.Integer(1), SqlValue.Text("c1"));
        rows[1].Should().Equal(SqlValue.Integer(2), SqlValue.Text("b2"), SqlValue.Integer(2), SqlValue.Text("c2"));
        rows[2].Should().Equal(SqlValue.Integer(3), SqlValue.Text("b3"), SqlValue.Null, SqlValue.Null);
        rows[3].Should().Equal(SqlValue.Null, SqlValue.Null, SqlValue.Integer(4), SqlValue.Text("c4"));
    }

    [Test]
    public void FullOuterJoinConsumesDuplicateWhereEquijoinLikeTurso()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(x);");
        Execute(connection, "CREATE TABLE u(x);");
        Execute(connection, "INSERT INTO t VALUES (1), (2), (3);");
        Execute(connection, "INSERT INTO u VALUES (2), (3), (4);");

        // Turso consumes the duplicate WHERE equality as a hash key, so it emits
        // unmatched rows despite the predicate being null-rejecting in SQLite.
        var rows = ReadRows(connection, """
            SELECT t.x, u.x FROM t FULL OUTER JOIN u ON t.x = u.x
            WHERE t.x = u.x
            ORDER BY coalesce(t.x, u.x);
            """);

        rows.Should().SatisfyRespectively(
            row => row.Should().Equal(SqlValue.Integer(1), SqlValue.Null),
            row => row.Should().Equal(SqlValue.Integer(2), SqlValue.Integer(2)),
            row => row.Should().Equal(SqlValue.Integer(3), SqlValue.Integer(3)),
            row => row.Should().Equal(SqlValue.Null, SqlValue.Integer(4)));
    }

    [Test]
    public void FullJoinUsingIsRejectedLikeTurso()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t1(a INTEGER, b TEXT);");
        Execute(connection, "CREATE TABLE t2(a INTEGER, c TEXT);");
        Execute(connection, "INSERT INTO t1 VALUES (1, 'b1'), (3, 'b3');");
        Execute(connection, "INSERT INTO t2 VALUES (1, 'c1'), (4, 'c4');");

        // Turso's full-join planner cannot express coalesced USING output, so it rejects the
        // shape; Ahtola mirrors the rejection (turso-src/core/translate/optimizer/join.rs).
        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "SELECT a, b, c FROM t1 FULL JOIN t2 USING(a);"))!
            .Message.Should().StartWith("FULL OUTER JOIN requires an equality condition");
    }

    [Test]
    public void NaturalJoinCoalescesAllSharedColumns()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t1(a INTEGER, b TEXT);");
        Execute(connection, "CREATE TABLE t2(a INTEGER, c TEXT);");
        Execute(connection, "INSERT INTO t1 VALUES (1, 'b1'), (2, 'b2');");
        Execute(connection, "INSERT INTO t2 VALUES (1, 'c1');");

        ColumnNames(connection, "SELECT * FROM t1 NATURAL JOIN t2;").Should().Equal("a", "b", "c");
        var rows = ReadRows(connection, "SELECT * FROM t1 NATURAL JOIN t2;");
        rows.Should().HaveCount(1);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("b1"), SqlValue.Text("c1"));
    }

    [Test]
    public void NaturalJoinWithOnClauseRaisesClearError()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t1(a INTEGER);");
        Execute(connection, "CREATE TABLE t2(a INTEGER);");

        Assert.Throws<EmbeddedSqlException>(
            () => connection.Prepare("SELECT * FROM t1 NATURAL JOIN t2 ON t1.a = t2.a;"))!
            .Message.Should().StartWith("a NATURAL join may not have an ON or USING clause");
    }

    [Test]
    public void NullIfMatchesSqliteSemantics()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        var rows = ReadRows(
            connection,
            "SELECT nullif(1, 1), nullif(1, 2), nullif('a', 'a'), nullif(NULL, 1), nullif(1, NULL), nullif(1, 1.0), nullif(1, '1');");
        rows[0].Should().Equal(
            SqlValue.Null,
            SqlValue.Integer(1),
            SqlValue.Null,
            SqlValue.Null,
            SqlValue.Integer(1),
            SqlValue.Null,
            SqlValue.Integer(1));

        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "SELECT nullif(1);"))!
            .Message.Should().Be("wrong number of arguments to function nullif()");
    }

    [Test]
    public void GlobIsCaseSensitiveAndSupportsWildcardsAndSets()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        var rows = ReadRows(
            connection,
            "SELECT 'abc' GLOB 'abc', 'abc' GLOB 'ABC', 'abc' GLOB 'a?c', 'abc' GLOB 'a*', "
            + "'abc' GLOB '[a-c][a-c][a-c]', 'xbc' GLOB '[^a]bc', 'abc' GLOB '[^a]bc', "
            + "'abc' GLOB 'a[bc', 123 GLOB '12*', 'abc' NOT GLOB 'x*';");
        rows[0].Should().Equal(
            SqlValue.Integer(1),
            SqlValue.Integer(0),
            SqlValue.Integer(1),
            SqlValue.Integer(1),
            SqlValue.Integer(1),
            SqlValue.Integer(1),
            SqlValue.Integer(0),
            SqlValue.Integer(0),
            SqlValue.Integer(1),
            SqlValue.Integer(1));
    }

    [Test]
    public void GlobFunctionReversesArgumentsAndPropagatesNull()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        var rows = ReadRows(connection, "SELECT glob('abc*', 'abcd'), glob('abc', 'abcd'), 'x' GLOB NULL, NULL GLOB 'x';");
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(0), SqlValue.Null, SqlValue.Null);

        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "SELECT glob('a');"))!
            .Message.Should().Be("wrong number of arguments to function glob()");
    }

    [Test]
    public void AggregateFilterRestrictsRowsPerAggregate()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE u(a INTEGER, b INTEGER);");
        Execute(connection, "INSERT INTO u VALUES (1, 10), (2, 20), (3, 30), (4, 40);");

        var rows = ReadRows(
            connection,
            "SELECT count(*) FILTER (WHERE b > 20), sum(a) FILTER (WHERE a > 2) FROM u;");
        rows[0].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(7));
    }

    [Test]
    public void AggregateDistinctCollapsesDuplicateArguments()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(x);");
        Execute(connection, "INSERT INTO t VALUES (1), (1.0), (2), ('1'), (NULL);");

        AssertCount(connection, "SELECT count(DISTINCT x) FROM t;", 3);
        ReadRows(connection, "SELECT sum(DISTINCT x) FROM t;")[0][0].Should().Be(SqlValue.Integer(4));
        ReadRows(connection, "SELECT group_concat(DISTINCT x) FROM t;")[0][0].Should().Be(SqlValue.Text("1,2,1"));
        AssertCount(connection, "SELECT count(DISTINCT x) FILTER (WHERE x > 1) FROM t;", 2);
    }

    [Test]
    public void DistinctAggregateWithMultipleArgumentsRaisesClearError()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(x, y);");
        Execute(connection, "INSERT INTO t VALUES (1, 2);");

        using var statement = connection.Prepare("SELECT group_concat(DISTINCT x, y) FROM t;");
        Assert.Throws<EmbeddedSqlException>(() => statement.Step())!
            .Message.Should().StartWith("DISTINCT aggregates must have exactly one argument.");
    }

    [Test]
    public void FilterOnNonAggregateFunctionRaisesClearError()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(x INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1);");

        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "SELECT abs(x) FILTER (WHERE x > 0) FROM t;"))!
            .Message.Should().Be("FILTER may not be used with non-aggregate abs()");
    }

    [Test]
    public void UsingJoinDistinguishesRawQualifiedColumnsFromCoalescedOutput()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t1(a, b);");
        Execute(connection, "CREATE TABLE t2(a, c);");
        Execute(connection, "INSERT INTO t1 VALUES (2, 'b2');");
        Execute(connection, "INSERT INTO t2 VALUES (2, 'c2'), (9, 'c9');");

        // Explicit t1.a / t2.a stay raw (uncoalesced); the bare column and t1.* coalesce.
        var rows = ReadRows(
            connection,
            "SELECT t1.a, t2.a, a FROM t1 RIGHT JOIN t2 USING(a) ORDER BY t2.a;");
        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(2), SqlValue.Integer(2));
        rows[1].Should().Equal(SqlValue.Null, SqlValue.Integer(9), SqlValue.Integer(9));

        var starRows = ReadRows(connection, "SELECT t1.* FROM t1 RIGHT JOIN t2 USING(a) ORDER BY a;");
        starRows[0].Should().Equal(SqlValue.Integer(2), SqlValue.Text("b2"));
        starRows[1].Should().Equal(SqlValue.Integer(9), SqlValue.Null);
    }

    [Test]
    public void NestedUsingJoinsMatchAgainstThePriorCoalescedKeyAndKeepRawQualifiedStars()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t1(a INTEGER);");
        Execute(connection, "CREATE TABLE t2(a INTEGER, c TEXT);");
        Execute(connection, "CREATE TABLE t3(a INTEGER, d TEXT);");
        Execute(connection, "INSERT INTO t2 VALUES (9, 'c9');");
        Execute(connection, "INSERT INTO t3 VALUES (9, 'd9');");

        var rows = ReadRows(connection,
            "SELECT a, t2.a, t3.a FROM t1 RIGHT JOIN t2 USING(a) JOIN t3 USING(a);");
        rows.Should().ContainSingle().Which.Should().Equal(
            SqlValue.Integer(9),
            SqlValue.Integer(9),
            SqlValue.Integer(9));

        var stars = ReadRows(connection, "SELECT t2.* FROM t1 RIGHT JOIN t2 USING(a);");
        stars.Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(9), SqlValue.Text("c9"));
    }

    [Test]
    public void NullIfAndDistinctAggregateHonorArgumentCollations()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value TEXT);");
        Execute(connection, "INSERT INTO t VALUES ('a'), ('A');");

        ReadRows(connection, "SELECT nullif('a' COLLATE NOCASE, 'A');")
            .Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Null);
        ReadRows(connection, "SELECT count(DISTINCT value COLLATE NOCASE) FROM t;")
            .Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(1));
    }

    [Test]
    public void FailedImplicitSavepointReleaseKeepsTheSavepointForRollback()
    {
        var database = new EmbeddedDatabase();
        using var writer = database.Connect();
        using var savepointConnection = database.Connect();
        Execute(writer, "CREATE TABLE t(value INTEGER);");
        Execute(savepointConnection, "SAVEPOINT s;");
        // The competing write has to land before the savepoint transaction takes its
        // write lock at its own first write.
        Execute(writer, "INSERT INTO t VALUES (2);");
        Execute(savepointConnection, "INSERT INTO t VALUES (1);");

        Assert.Throws<EmbeddedSqlException>(() => Execute(savepointConnection, "RELEASE s;"));
        Execute(savepointConnection, "ROLLBACK TO s;");
        Execute(savepointConnection, "ROLLBACK;");
        AssertCount(writer, "SELECT COUNT(*) FROM t;", 1);
    }

    [Test]
    public void ViewExpandsUnderlyingQueryWithFilter()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, active INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 1), (2, 0), (3, 1);");
        Execute(connection, "CREATE VIEW active_ids AS SELECT id FROM t WHERE active = 1;");

        var rows = ReadRows(connection, "SELECT id FROM active_ids ORDER BY id;");
        rows.Should().HaveCount(2);
        rows[0][0].Should().Be(SqlValue.Integer(1));
        rows[1][0].Should().Be(SqlValue.Integer(3));
    }

    [Test]
    public void ViewCanBeJoinedAndFilteredLikeATable()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, label TEXT);");
        Execute(connection, "INSERT INTO t VALUES (1, 'a'), (2, 'b');");
        Execute(connection, "CREATE VIEW v AS SELECT id, label FROM t;");

        var rows = ReadRows(connection, "SELECT label FROM v WHERE id = 2;");
        rows.Should().HaveCount(1);
        rows[0][0].Should().Be(SqlValue.Text("b"));
    }

    [Test]
    public void ViewExplicitColumnListRelabelsOutputColumns()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (10, 20);");
        Execute(connection, "CREATE VIEW v(x, y) AS SELECT a, b FROM t;");

        ColumnNames(connection, "SELECT * FROM v;").Should().Equal("x", "y");
        var rows = ReadRows(connection, "SELECT x, y FROM v;");
        rows.Should().HaveCount(1);
        rows[0][0].Should().Be(SqlValue.Integer(10));
        rows[0][1].Should().Be(SqlValue.Integer(20));
    }

    [Test]
    public void ViewExplicitColumnCountMismatchIsRejectedAtQueryTime()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b INTEGER);");

        // SQLite defers column-arity validation to query time, so CREATE succeeds.
        Execute(connection, "CREATE VIEW v(only_one) AS SELECT a, b FROM t;");
        var error = Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "SELECT * FROM v;"));
        error!.Message.Should().Contain("columns for v");
    }

    [Test]
    public void ViewsAppearInSqliteMasterWithOriginalSql()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE VIEW v AS SELECT a FROM t;");

        using var statement = connection.Prepare(
            "SELECT name, type, tbl_name, sql FROM sqlite_master WHERE type = 'view';");
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Text("v"));
        statement.GetValue(1).Should().Be(SqlValue.Text("view"));
        statement.GetValue(2).Should().Be(SqlValue.Text("v"));
        statement.GetValue(3).AsText().Should().Be("CREATE VIEW v AS SELECT a FROM t");
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void DropViewRemovesViewFromCatalog()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE VIEW v AS SELECT a FROM t;");
        Execute(connection, "DROP VIEW v;");

        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "SELECT * FROM v;"));
        AssertCount(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'view';", 0);
    }

    [Test]
    public void DropViewCascadesToItsInsteadOfTriggers()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE VIEW v AS SELECT a FROM t;");
        Execute(
            connection,
            "CREATE TRIGGER v_insert INSTEAD OF INSERT ON v "
                + "BEGIN INSERT INTO t VALUES (NEW.a); END;");

        Execute(connection, "DROP VIEW v;");

        AssertCount(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger';", 0);
    }

    [Test]
    public void DropViewIfExistsIsAllowedWhenMissing()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "DROP VIEW IF EXISTS missing;");
    }

    [Test]
    public void SelfReferentialViewIsRejectedAsCircularAtQueryTime()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        // SQLite defers view-body validation to query time, so CREATE succeeds and the
        // circular definition is reported when the view is queried.
        Execute(connection, "CREATE VIEW v AS SELECT * FROM v;");
        var error = Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "SELECT * FROM v;"));
        error!.Message.Should().Contain("circularly defined");
        AssertCount(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'view';", 1);
    }

    [Test]
    public void DropTableOnViewReportsCrossTypeError()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE VIEW v AS SELECT a FROM t;");

        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "DROP TABLE v;"));
    }

    [Test]
    public void DropViewOnTableReportsCrossTypeError()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");

        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "DROP VIEW t;"));
    }

    [Test]
    public void ViewNameCollidingWithTableIsRejected()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");

        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "CREATE VIEW t AS SELECT 1 AS x;"));
    }

    [Test]
    public void ViewCatalogParticipatesInTransactionRollback()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "BEGIN;");
        Execute(connection, "CREATE VIEW v AS SELECT 1 AS x;");
        Execute(connection, "ROLLBACK;");

        AssertCount(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'view';", 0);
    }

    [Test]
    public void AfterInsertTriggerRunsBodyStatement()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER);");
        Execute(connection, "CREATE TABLE log(msg TEXT);");
        Execute(connection, "CREATE TRIGGER trg AFTER INSERT ON t BEGIN INSERT INTO log VALUES ('inserted'); END;");
        Execute(connection, "INSERT INTO t VALUES (1);");

        var rows = ReadRows(connection, "SELECT msg FROM log;");
        rows.Should().HaveCount(1);
        rows[0][0].Should().Be(SqlValue.Text("inserted"));
    }

    [Test]
    public void AfterUpdateTriggerRunsBodyStatement()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER);");
        Execute(connection, "CREATE TABLE log(n INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2);");
        Execute(connection, "CREATE TRIGGER trg AFTER UPDATE ON t BEGIN INSERT INTO log VALUES (1); END;");
        Execute(connection, "UPDATE t SET id = id + 10;");

        AssertCount(connection, "SELECT COUNT(*) FROM log;", 2);
    }

    [Test]
    public void AfterDeleteTriggerRunsBodyStatement()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER);");
        Execute(connection, "CREATE TABLE log(n INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1);");
        Execute(connection, "CREATE TRIGGER trg AFTER DELETE ON t BEGIN INSERT INTO log VALUES (1); END;");
        Execute(connection, "DELETE FROM t;");

        AssertCount(connection, "SELECT COUNT(*) FROM log;", 1);
    }

    [Test]
    public void TriggerFiresOncePerAffectedRow()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER);");
        Execute(connection, "CREATE TABLE cnt(n INTEGER);");
        Execute(connection, "CREATE TRIGGER trg AFTER INSERT ON t BEGIN INSERT INTO cnt VALUES (1); END;");
        Execute(connection, "INSERT INTO t VALUES (1), (2), (3);");

        AssertCount(connection, "SELECT COUNT(*) FROM cnt;", 3);
    }

    [Test]
    public void TriggerDoesNotFireWhenNoRowsAffected()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER);");
        Execute(connection, "CREATE TABLE log(n INTEGER);");
        Execute(connection, "CREATE TRIGGER trg AFTER DELETE ON t BEGIN INSERT INTO log VALUES (1); END;");
        Execute(connection, "DELETE FROM t WHERE id = 42;");

        AssertCount(connection, "SELECT COUNT(*) FROM log;", 0);
    }

    [Test]
    public void TriggerWithMultipleBodyStatementsExecutesInOrder()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER);");
        Execute(connection, "CREATE TABLE log(step INTEGER);");
        Execute(
            connection,
            "CREATE TRIGGER trg AFTER INSERT ON t BEGIN INSERT INTO log VALUES (1); INSERT INTO log VALUES (2); END;");
        Execute(connection, "INSERT INTO t VALUES (10);");

        var rows = ReadRows(connection, "SELECT step FROM log;");
        rows.Should().HaveCount(2);
        rows[0][0].Should().Be(SqlValue.Integer(1));
        rows[1][0].Should().Be(SqlValue.Integer(2));
    }

    [Test]
    public void MultipleTriggersFireInReverseDeclarationOrder()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER);");
        Execute(connection, "CREATE TABLE log(name TEXT);");
        Execute(connection, "CREATE TRIGGER b_trg AFTER INSERT ON t BEGIN INSERT INTO log VALUES ('b'); END;");
        Execute(connection, "CREATE TRIGGER a_trg AFTER INSERT ON t BEGIN INSERT INTO log VALUES ('a'); END;");
        Execute(connection, "INSERT INTO t VALUES (1);");

        var rows = ReadRows(connection, "SELECT name FROM log;");
        rows.Should().HaveCount(2);
        rows[0][0].Should().Be(SqlValue.Text("a"));
        rows[1][0].Should().Be(SqlValue.Text("b"));
    }

    [Test]
    public void TriggersAppearInSqliteMaster()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER);");
        Execute(connection, "CREATE TRIGGER trg AFTER INSERT ON t BEGIN DELETE FROM t; END;");

        using var statement = connection.Prepare(
            "SELECT name, type, tbl_name, sql FROM sqlite_master WHERE type = 'trigger';");
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Text("trg"));
        statement.GetValue(1).Should().Be(SqlValue.Text("trigger"));
        statement.GetValue(2).Should().Be(SqlValue.Text("t"));
        statement.GetValue(3).AsText().Should().Contain("CREATE TRIGGER trg AFTER INSERT ON t");
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void DropTriggerRemovesTrigger()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER);");
        Execute(connection, "CREATE TABLE log(n INTEGER);");
        Execute(connection, "CREATE TRIGGER trg AFTER INSERT ON t BEGIN INSERT INTO log VALUES (1); END;");
        Execute(connection, "DROP TRIGGER trg;");
        Execute(connection, "INSERT INTO t VALUES (1);");

        AssertCount(connection, "SELECT COUNT(*) FROM log;", 0);
    }

    [Test]
    public void DropTableCascadesToItsTriggers()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER);");
        Execute(connection, "CREATE TRIGGER trg AFTER INSERT ON t BEGIN DELETE FROM t; END;");
        Execute(connection, "DROP TABLE t;");

        AssertCount(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger';", 0);
    }

    [Test]
    public void TriggerFailureRollsBackTriggeringStatement()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER);");
        Execute(connection, "CREATE TABLE audit(id INTEGER);");
        Execute(connection, "CREATE UNIQUE INDEX ux_audit ON audit(id);");
        Execute(connection, "INSERT INTO audit VALUES (1);");
        Execute(connection, "CREATE TRIGGER trg AFTER INSERT ON t BEGIN INSERT INTO audit VALUES (1); END;");

        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "INSERT INTO t VALUES (5);"));

        // The triggering INSERT and its failed trigger are one atomic unit: both roll back.
        AssertCount(connection, "SELECT COUNT(*) FROM t;", 0);
        AssertCount(connection, "SELECT COUNT(*) FROM audit;", 1);
    }

    [Test]
    public void RecursiveTriggerCascadeIsRejectedAndRolledBack()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER);");
        Execute(connection, "CREATE TRIGGER trg AFTER INSERT ON t BEGIN INSERT INTO t VALUES (99); END;");
        Execute(connection, "PRAGMA recursive_triggers = ON;");

        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "INSERT INTO t VALUES (1);"));
        AssertCount(connection, "SELECT COUNT(*) FROM t;", 0);
    }

    [Test]
    public void TriggerFiresWithinExplicitTransactionAndCommits()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER);");
        Execute(connection, "CREATE TABLE log(n INTEGER);");
        Execute(connection, "CREATE TRIGGER trg AFTER INSERT ON t BEGIN INSERT INTO log VALUES (1); END;");

        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO t VALUES (5);");
        Execute(connection, "COMMIT;");

        AssertCount(connection, "SELECT COUNT(*) FROM log;", 1);
    }

    [Test]
    public void CreateTriggerOnMissingTableIsRejected()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "CREATE TRIGGER trg AFTER INSERT ON missing BEGIN DELETE FROM missing; END;"));
    }

    [Test]
    public void CreateTriggerOnViewIsRejected()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "CREATE VIEW v AS SELECT a FROM t;");

        Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "CREATE TRIGGER trg AFTER INSERT ON v BEGIN DELETE FROM t; END;"));
    }

    [Test]
    public void PrepareScriptKeepsTriggerBodyIntact()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        var statements = connection.PrepareScript(
            "CREATE TABLE t(id INTEGER); CREATE TABLE log(n INTEGER); "
            + "CREATE TRIGGER trg AFTER INSERT ON t BEGIN INSERT INTO log VALUES (1); INSERT INTO log VALUES (2); END; "
            + "SELECT 1;");

        statements.Should().HaveCount(4);
        foreach (var statement in statements)
            statement.Dispose();
    }

    [Test]
    public void InsteadOfTriggerOnTableIsRejected()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER);");

        Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "CREATE TRIGGER trg INSTEAD OF INSERT ON t BEGIN DELETE FROM t; END;"));
    }

    [Test]
    public void BeforeTriggerRunsBeforeTheRowMutation()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER);");
        Execute(connection, "CREATE TABLE log(id INTEGER);");
        Execute(
            connection,
            "CREATE TRIGGER trg BEFORE INSERT ON t BEGIN INSERT INTO log VALUES (NEW.id); END;");
        Execute(connection, "INSERT INTO t VALUES (7);");

        ReadRows(connection, "SELECT id FROM log;").Should().ContainSingle()
            .Which[0].Should().Be(SqlValue.Integer(7));
    }

    [Test]
    public void TriggerWithOmittedTimingDefaultsToBefore()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER);");
        Execute(connection, "CREATE TABLE log(id INTEGER);");
        Execute(connection, "CREATE TRIGGER trg INSERT ON t BEGIN INSERT INTO log VALUES (NEW.id); END;");
        Execute(connection, "INSERT INTO t VALUES (8);");

        ReadRows(connection, "SELECT id FROM log;").Should().ContainSingle()
            .Which[0].Should().Be(SqlValue.Integer(8));
    }

    [Test]
    public void ExplicitForEachRowTriggerRunsForEveryRow()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER);");
        Execute(connection, "CREATE TABLE log(id INTEGER);");
        Execute(
            connection,
            "CREATE TRIGGER trg AFTER INSERT ON t FOR EACH ROW "
                + "BEGIN INSERT INTO log VALUES (NEW.id); END;");
        Execute(connection, "INSERT INTO t VALUES (1), (2);");

        AssertCount(connection, "SELECT COUNT(*) FROM log;", 2);
    }

    [Test]
    public void WhenClauseFiltersEachAffectedRow()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER);");
        Execute(connection, "CREATE TABLE log(id INTEGER);");
        Execute(
            connection,
            "CREATE TRIGGER trg AFTER INSERT ON t WHEN NEW.id > 1 "
                + "BEGIN INSERT INTO log VALUES (NEW.id); END;");
        Execute(connection, "INSERT INTO t VALUES (1), (2);");

        ReadRows(connection, "SELECT id FROM log;").Should().ContainSingle()
            .Which[0].Should().Be(SqlValue.Integer(2));
    }

    [Test]
    public void UpdateOfColumnsMatchesTheSetList()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, name TEXT);");
        Execute(connection, "CREATE TABLE log(value TEXT);");
        Execute(
            connection,
            "CREATE TRIGGER trg AFTER UPDATE OF name ON t "
                + "BEGIN INSERT INTO log VALUES (NEW.name); END;");
        Execute(connection, "INSERT INTO t VALUES (1, 'old');");
        Execute(connection, "UPDATE t SET id = 2;");
        Execute(connection, "UPDATE t SET name = 'new';");

        ReadRows(connection, "SELECT value FROM log;").Should().ContainSingle()
            .Which[0].Should().Be(SqlValue.Text("new"));
    }

    [Test]
    public void NewAndOldReferencesExposeAffectedRowImages()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER);");
        Execute(connection, "CREATE TABLE log(n INTEGER);");
        Execute(
            connection,
            "CREATE TRIGGER trg AFTER UPDATE ON t "
                + "BEGIN INSERT INTO log VALUES (OLD.id); INSERT INTO log VALUES (NEW.id); END;");
        Execute(connection, "INSERT INTO t VALUES (1);");
        Execute(connection, "UPDATE t SET id = 2;");

        ReadRows(connection, "SELECT n FROM log ORDER BY rowid;")
            .Select(row => row[0].AsInteger())
            .Should().Equal(1, 2);
    }

    [Test]
    public void SelectTriggerBodyStatementExecutesAndDiscardsItsRows()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER);");
        Execute(connection, "CREATE TRIGGER trg AFTER INSERT ON t BEGIN SELECT NEW.id; END;");
        Execute(connection, "INSERT INTO t VALUES (1);");

        AssertCount(connection, "SELECT COUNT(*) FROM t;", 1);
    }

    [Test]
    public void BeforeTriggerRunsForEveryInsertedRow()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER);");
        Execute(connection, "CREATE TABLE log(id INTEGER);");
        Execute(
            connection,
            "CREATE TRIGGER trg BEFORE INSERT ON t BEGIN INSERT INTO log VALUES (NEW.id); END;");
        Execute(connection, "INSERT INTO t VALUES (1), (2);");

        AssertCount(connection, "SELECT COUNT(*) FROM log;", 2);
    }

    [Test]
    public void WhenClauseFiltersDistinctTriggerRowSet()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER);");
        Execute(connection, "CREATE TABLE log(id INTEGER);");
        Execute(
            connection,
            "CREATE TRIGGER trg AFTER INSERT ON t WHEN NEW.id = 2 BEGIN INSERT INTO log VALUES (NEW.id); END;");
        Execute(connection, "INSERT INTO t VALUES (1), (2), (3);");

        AssertCount(connection, "SELECT COUNT(*) FROM log;", 1);
    }

    [Test]
    public void NewReferencesResolveForInsertedTriggerRows()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER);");
        Execute(connection, "CREATE TABLE log(n INTEGER);");
        Execute(
            connection,
            "CREATE TRIGGER trg AFTER INSERT ON t BEGIN INSERT INTO log VALUES (NEW.id); END;");
        Execute(connection, "INSERT INTO t VALUES (7);");

        ReadRows(connection, "SELECT n FROM log;").Single()[0].Should().Be(SqlValue.Integer(7));
    }

    [Test]
    public void LiteralSelectTriggerBodyStatementIsAccepted()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER);");
        Execute(connection, "CREATE TRIGGER trg AFTER INSERT ON t BEGIN SELECT 1; END;");
        Execute(connection, "INSERT INTO t VALUES (1);");

        AssertCount(connection, "SELECT COUNT(*) FROM t;", 1);
    }

    [Test]
    public void ParameterInTriggerBodyIsRejected()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER);");
        Execute(connection, "CREATE TABLE log(n INTEGER);");

        Assert.Throws<EmbeddedSqlException>(
            () => connection.Prepare("CREATE TRIGGER trg AFTER INSERT ON t BEGIN INSERT INTO log VALUES (?); END;"));
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

    private static string[] ColumnNames(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var names = new string[statement.GetColumnCount()];
        for (var ordinal = 0; ordinal < names.Length; ordinal++)
            names[ordinal] = statement.GetColumnName(ordinal);

        return names;
    }

    private static void AssertCount(EmbeddedConnection connection, string sql, long expected)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(expected));
        statement.Step().Should().Be(StatementStepResult.Done);
    }
}
