using AwesomeAssertions;
using ManagedSqlite = Ahtola.Data.Sqlite;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;

namespace Ahtola.Tests;

public sealed class ExpressionOperatorParityTests
{
    [TestCase("typeof(+'10')")]
    [TestCase("hex(+x'3130')")]
    [TestCase("1 | 2 & 4")]
    [TestCase("1 + 2 << 1")]
    [TestCase("8 >> 1 + 1")]
    [TestCase("0 = 1 < 2")]
    [TestCase("0 IS NOT DISTINCT FROM 1 < 2")]
    [TestCase("0 BETWEEN 0 AND 1 = 0")]
    [TestCase("~'10x'")]
    [TestCase("'10x' & 3")]
    [TestCase("1.9 | 2.9")]
    [TestCase("1 << 64")]
    [TestCase("-1 >> 64")]
    [TestCase("8 << -1")]
    [TestCase("8 >> -1")]
    [TestCase("1 << -9223372036854775808")]
    [TestCase("NULL << 1")]
    public void UnaryAndBitwiseScalarSemanticsMatchSqlite(string expression)
        => AssertQueryMatchesSqlite($"SELECT {expression};");

    [Test]
    public void UnaryAndBitwiseParametersMatchSqlite()
    {
        AssertQueryMatchesSqlite(
            "SELECT typeof(+?1), +?1, ~?2, ?3 << ?4, ?3 >> -?4, ?5 & 7 | 8;",
            SqlValue.Text("10"),
            SqlValue.Text("10x"),
            SqlValue.Integer(8),
            SqlValue.Integer(1),
            SqlValue.Real(3.9));
    }

    // TRUE and FALSE are not reserved words: SQLite parses them as identifiers and only rewrites
    // them into the integer literals 1/0 when no column of that name resolves. `IS TRUE`/`IS FALSE`
    // are truth tests rather than equality tests, so `2 IS TRUE` is 1 while `2 IS 1` is 0.
    [TestCase("TRUE, FALSE")]
    [TestCase("typeof(true), typeof(false)")]
    [TestCase("8 IS TRUE, 1 IS TRUE, 0 IS TRUE, -1 IS TRUE")]
    [TestCase("0.0 IS TRUE, 0.5 IS TRUE, -0.5 IS TRUE")]
    [TestCase("'hello' IS TRUE, '' IS TRUE, '0' IS TRUE, '1' IS TRUE, '42' IS TRUE")]
    [TestCase("8 IS FALSE, 1 IS FALSE, 0 IS FALSE, -1 IS FALSE")]
    [TestCase("'hello' IS FALSE, '' IS FALSE, '0' IS FALSE, '1' IS FALSE")]
    [TestCase("8 IS NOT TRUE, 0 IS NOT TRUE, 8 IS NOT FALSE, 0 IS NOT FALSE")]
    [TestCase("NULL IS TRUE, NULL IS FALSE, NULL IS NOT TRUE, NULL IS NOT FALSE")]
    [TestCase("2 IS TRUE, 2 IS 1, 2 = TRUE, 2 IS (TRUE)")]
    [TestCase("2 IS TRUE COLLATE BINARY, 2 IS DISTINCT FROM TRUE, 2 IS NOT DISTINCT FROM TRUE")]
    [TestCase("true + 1, true = 1, true == true, +true, -true, ~true")]
    [TestCase("true AND false, true OR false, NOT true")]
    public void BooleanKeywordLiteralsMatchSqlite(string projection)
        => AssertQueryMatchesSqlite($"SELECT {projection};");

    [Test]
    public void BooleanKeywordsBoundAgainstParametersMatchSqlite()
    {
        AssertQueryMatchesSqlite(
            "SELECT ?1 IS TRUE, ?1 IS FALSE, ?1 = TRUE, ?2 IS TRUE, ?2 IS NOT FALSE, ?3 IS TRUE, ?3 IS NOT TRUE;",
            setup: null,
            [SqlValue.Text("42"), SqlValue.Integer(0), SqlValue.Null]);
    }

    // The shadowing column values are chosen so a keyword-only implementation disagrees: "true" is
    // 0 while the probed value is truthy, and "false" is 7 so it equals the probed value.
    [Test]
    public void BooleanKeywordsResolveToColumnsOfThatNameWhenInScope()
    {
        const string setup = """
            CREATE TABLE shadowed("true" INTEGER, "false" INTEGER, value INTEGER);
            INSERT INTO shadowed VALUES (0, 7, 7);
            """;
        AssertQueryMatchesSqlite(
            "SELECT true, false, shadowed.true, value IS TRUE, value IS FALSE FROM shadowed;",
            setup);
    }

    [Test]
    public void BooleanKeywordsFallBackToLiteralsForNamesThatAreNotInScope()
    {
        const string setup = """
            CREATE TABLE partly_shadowed("true" INTEGER, value INTEGER);
            INSERT INTO partly_shadowed VALUES (42, 0);
            """;
        AssertQueryMatchesSqlite(
            "SELECT true, false, value IS TRUE, value IS FALSE FROM partly_shadowed;",
            setup);
    }

    [Test]
    public void QuotedBooleanKeywordsAreNeverLiterals()
    {
        using var connection = new EmbeddedDatabase().Connect();
        var error = Assert.Throws<EmbeddedSqlException>(
            () => ReadManaged(connection, "SELECT \"true\", \"false\";"));
        error!.Message.Should().Contain("no such column");
    }

    [Test]
    public void BooleanKeywordDefaultsAndCheckConstraintsMatchSqlite()
    {
        const string setup = """
            CREATE TABLE flagged(value INTEGER CHECK(value IS TRUE), yes DEFAULT TRUE, no DEFAULT FALSE);
            INSERT INTO flagged(value) VALUES (5);
            """;
        AssertQueryMatchesSqlite("SELECT value, yes, no FROM flagged;", setup);
    }

    [Test]
    public void BooleanKeywordCheckConstraintRejectsFalsyValues()
    {
        using var connection = new EmbeddedDatabase().Connect();
        ExecuteManaged(connection, "CREATE TABLE flagged(value INTEGER CHECK(value IS TRUE));");
        var error = Assert.Throws<EmbeddedSqlException>(
            () => ExecuteManaged(connection, "INSERT INTO flagged VALUES (0);"));
        error!.Message.Should().Contain("CHECK constraint failed");
    }

    // A DEFAULT of TRUE/FALSE is a constant, so ADD COLUMN may backfill pre-existing rows with it.
    [Test]
    public void BooleanKeywordDefaultsAreConstantForAlterTableAddColumn()
    {
        const string setup = """
            CREATE TABLE existing(x INTEGER);
            INSERT INTO existing VALUES (1);
            ALTER TABLE existing ADD COLUMN yes DEFAULT TRUE;
            ALTER TABLE existing ADD COLUMN no DEFAULT FALSE;
            """;
        AssertQueryMatchesSqlite("SELECT x, yes, no FROM existing;", setup);
    }

    [Test]
    public void BooleanKeywordsAreAllowedInPartialIndexPredicates()
    {
        const string setup = """
            CREATE TABLE flags(x INTEGER, flag INTEGER);
            CREATE INDEX flags_enabled ON flags(x) WHERE flag IS TRUE;
            INSERT INTO flags VALUES (1, 1), (2, 0), (3, 2), (4, NULL);
            """;
        AssertQueryMatchesSqlite("SELECT x FROM flags WHERE flag IS TRUE ORDER BY x;", setup);
    }

    [TestCase("(1, 2) = (1, 2)")]
    [TestCase("(1, NULL) = (1, NULL)")]
    [TestCase("(1, NULL) = (2, NULL)")]
    [TestCase("(1, NULL) < (2, 0)")]
    [TestCase("(1, NULL) < (1, 0)")]
    [TestCase("(1, NULL) IS (1, NULL)")]
    [TestCase("(1, NULL) IS NOT (1, NULL)")]
    [TestCase("(1, NULL) IS DISTINCT FROM (1, NULL)")]
    [TestCase("(1, NULL) IS NOT DISTINCT FROM (1, NULL)")]
    [TestCase("('a' COLLATE NOCASE, 1) = ('A', 1)")]
    [TestCase("(1, 2) IN ((1, 2), (3, 4))")]
    [TestCase("(1, NULL) IN ((1, NULL), (3, 4))")]
    [TestCase("(1, 2) IN ((1, NULL), (3, 4))")]
    [TestCase("(1, 2) NOT IN ()")]
    [TestCase("('a', 1) IN (('A' COLLATE NOCASE, 1), ('z', 2))")]
    [TestCase("('a', 1) IN (('z', 2), ('A' COLLATE NOCASE, 1))")]
    [TestCase("(SELECT 1, 2) = (1, 2)")]
    [TestCase("(1, 2) IN (SELECT 1, 2 UNION ALL SELECT 3, 4)")]
    [TestCase("(1, 2) BETWEEN (1, 1) AND (1, 3)")]
    public void RowValueNullCollationAndSubquerySemanticsMatchSqlite(string expression)
        => AssertQueryMatchesSqlite($"SELECT {expression};");

    [Test]
    public void RowValueParametersAndCorrelatedSubqueriesMatchSqlite()
    {
        const string setup = """
            CREATE TABLE pairs(id INTEGER, left_value, right_value);
            INSERT INTO pairs VALUES (1, 1, 2), (2, 1, NULL), (3, 3, 4);
            """;
        AssertQueryMatchesSqlite(
            """
            SELECT id,
                   (left_value, right_value) < (?1, ?2),
                   (left_value, right_value) IN (
                       SELECT candidate.left_value, candidate.right_value
                       FROM pairs AS candidate
                       WHERE candidate.id <= pairs.id)
            FROM pairs
            ORDER BY id;
            """,
            setup,
            [SqlValue.Integer(2), SqlValue.Integer(0)]);
    }

    [Test]
    public void RowComparisonAppliesDeclaredAffinityAndCollationPerElement()
    {
        const string setup = """
            CREATE TABLE typed(id INTEGER, number INTEGER, label TEXT COLLATE NOCASE, plain TEXT);
            INSERT INTO typed VALUES (1, 2, 'alpha', 'x');
            """;
        AssertQueryMatchesSqlite(
            """
            SELECT (number, label) = (?1, ?2),
                   (?1, ?2) IS NOT DISTINCT FROM (number, label),
                   (label, number) IN ((?2, ?1)),
                   (plain COLLATE RTRIM, number) = (?3, ?1)
            FROM typed;
            """,
            setup,
            [SqlValue.Text("2"), SqlValue.Text("ALPHA"), SqlValue.Text("x ")]);
    }

    [Test]
    public void BlobAffinityRemainsDistinctFromAnExpressionWithoutAffinity()
    {
        const string setup = """
            CREATE TABLE affinity_values(untyped, text_value TEXT, numeric_value INTEGER);
            INSERT INTO affinity_values VALUES (1, '1', 1);
            """;
        AssertQueryMatchesSqlite(
            """
            SELECT untyped = text_value,
                   (untyped, text_value) = (text_value, untyped),
                   untyped = numeric_value,
                   text_value = 1,
                   untyped IN ('1'),
                   text_value IN (1),
                   numeric_value IN ('1')
            FROM affinity_values;
            """,
            setup);
    }

    [Test]
    public void RowComparisonAndInPreserveSqliteCallbackOrder()
    {
        var managedEvents = new List<string>();
        var managedDatabase = new EmbeddedDatabase();
        managedDatabase.RegisterScalarFunction(
            "mark",
            2,
            arguments =>
            {
                managedEvents.Add(arguments[0].AsText());
                return arguments[1];
            });
        using var managed = managedDatabase.Connect();

        ReadManaged(
            managed,
            "SELECT (mark('L1', 1), mark('L2', 2)) < (mark('R1', 3), mark('R2', 4));");
        managedEvents.Should().Equal("L1", "R1");

        managedEvents.Clear();
        ReadManaged(
            managed,
            """
            SELECT (mark('L1', 1), mark('L2', 2))
                   IN ((mark('A1', 1), mark('A2', 2)), (mark('B1', 3), mark('B2', 4)));
            """);
        managedEvents.Should().Equal("A1", "A2", "B1", "B2", "L1", "L2");

        managedEvents.Clear();
        ReadManaged(
            managed,
            """
            SELECT (mark('L1', 1), mark('L2', 2))
                   = (SELECT mark('R1', 1), mark('R2', 2));
            """);
        managedEvents.Should().Equal("R1", "R2", "L1", "L2");

        managedEvents.Clear();
        ReadManaged(
            managed,
            "SELECT mark('L', NULL) IN (mark('A', 1), mark('B', 2));");
        managedEvents.Should().Equal("L", "A", "B");

        managedEvents.Clear();
        ReadManaged(
            managed,
            """
            SELECT (mark('v1', 5), mark('v2', 5))
                   BETWEEN (mark('lo1', 10), mark('lo2', 10))
                       AND (mark('hi1', 20), mark('hi2', 20));
            """);
        managedEvents.Should().Equal("v1", "v2", "lo1", "hi1");
    }

    [Test]
    public void RowValueUpdateSubqueryAndUpsertAssignmentsMatchSqlite()
    {
        const string setup = """
            CREATE TABLE pairs(id INTEGER PRIMARY KEY, left_value INTEGER, right_value INTEGER);
            INSERT INTO pairs VALUES (1, 10, 20), (2, 30, 40);
            """;

        AssertQueryMatchesSqlite(
            """
            UPDATE pairs
            SET (left_value, right_value) = (right_value + ?1, left_value + ?2)
            WHERE (id, left_value) IN ((1, 10), (2, 30))
            RETURNING id, left_value, right_value;
            """,
            setup,
            [SqlValue.Integer(1), SqlValue.Integer(2)]);

        AssertQueryMatchesSqlite(
            """
            UPDATE pairs
            SET (left_value, right_value) = (SELECT right_value, left_value)
            WHERE id = 1
            RETURNING id, left_value, right_value;
            """,
            setup);

        AssertQueryMatchesSqlite(
            """
            INSERT INTO pairs VALUES (1, 50, 60)
            ON CONFLICT(id) DO UPDATE
            SET (left_value, right_value) = (excluded.right_value, excluded.left_value)
            RETURNING id, left_value, right_value;
            """,
            setup);
    }

    [Test]
    public void RowAssignmentEvaluatesEachRightHandTupleOnceInOrder()
    {
        var events = new List<string>();
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "mark",
            2,
            arguments =>
            {
                events.Add(arguments[0].AsText());
                return arguments[1];
            });
        using var connection = database.Connect();
        ExecuteManaged(connection, "CREATE TABLE target(left_value, right_value);");
        ExecuteManaged(connection, "INSERT INTO target VALUES (1, 2);");

        ReadManaged(
            connection,
            """
            UPDATE target
            SET (left_value, right_value) = (mark('left', 10), mark('right', 20))
            RETURNING left_value, right_value;
            """)
            .Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(10), SqlValue.Integer(20));
        events.Should().Equal("left", "right");
    }

    [Test]
    public void RowValueArityAndScalarMisuseDiagnosticsMatchSqliteShape()
    {
        using var connection = new EmbeddedDatabase().Connect();
        ExecuteManaged(connection, "CREATE TABLE target(left_value, right_value);");
        ExecuteManaged(connection, "INSERT INTO target VALUES (1, 2);");

        Assert.Throws<EmbeddedSqlException>(
                () => ReadManaged(connection, "SELECT (1, 2) = 1;"))
            !.Message.Should().Be("row value misused");
        Assert.Throws<EmbeddedSqlException>(
                () => ReadManaged(connection, "SELECT (1, 2) IN (1, 2);"))
            !.Message.Should().Be("IN(...) element has 1 term - expected 2");
        Assert.Throws<EmbeddedSqlException>(
                () => ReadManaged(connection, "UPDATE target SET (left_value, right_value) = (1);"))
            !.Message.Should().Be("2 columns assigned 1 values");
        Assert.Throws<EmbeddedSqlException>(
                () => ReadManaged(connection, "SELECT (1, 2);"))
            !.Message.Should().Be("row value misused");
    }

    [Test]
    public void OperatorsWorkInDefaultCheckWindowCompoundAndDmlContexts()
    {
        const string schema = """
            CREATE TABLE context_values(
                value INTEGER DEFAULT (~1),
                shifted INTEGER DEFAULT (8 >> 1),
                positive INTEGER DEFAULT +3,
                CHECK ((value, shifted) IS NOT DISTINCT FROM (value, shifted)),
                CHECK ((shifted & 3) = 0));
            INSERT INTO context_values DEFAULT VALUES;
            INSERT INTO context_values(value, shifted) VALUES (5, 4);
            """;
        AssertQueryMatchesSqlite(
            """
            SELECT value,
                   shifted,
                   positive,
                   +(sum(shifted) OVER (
                       ORDER BY rowid ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)) | 1,
                   (sum(shifted) OVER (
                       ORDER BY rowid ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW), shifted)
                       >= (4, 4)
            FROM context_values
            ORDER BY rowid;
            """,
            schema);

        AssertQueryMatchesSqlite(
            "SELECT ~1 AS value UNION ALL SELECT 8 >> 1 UNION ALL SELECT (1, NULL) IS DISTINCT FROM (1, NULL);");

        AssertQueryMatchesSqlite(
            """
            UPDATE context_values
            SET value = value << 1
            WHERE (value, shifted) IS NOT DISTINCT FROM (5, 4)
            RETURNING +value, ~value, value | shifted;
            """,
            schema);
    }

    [Test]
    public void CompositeForeignKeyCascadePreservesOperatorSchemaSemantics()
    {
        const string setup = """
            PRAGMA foreign_keys = ON;
            CREATE TABLE parent(
                code TEXT COLLATE NOCASE,
                version INTEGER DEFAULT (8 >> 1),
                parity INTEGER GENERATED ALWAYS AS (version & 1) VIRTUAL,
                CHECK (version > 0
                    AND (version, parity) IS NOT DISTINCT FROM (version, version & 1)),
                PRIMARY KEY(code COLLATE NOCASE, version)
            ) WITHOUT ROWID;
            CREATE INDEX parent_lookup
                ON parent(code COLLATE NOCASE DESC, version ASC);
            CREATE TABLE child(
                id INTEGER PRIMARY KEY,
                parent_code TEXT COLLATE NOCASE,
                parent_version INTEGER DEFAULT (8 >> 1),
                inverted INTEGER GENERATED ALWAYS AS (~parent_version) VIRTUAL,
                CHECK (parent_version > 0 AND (parent_version & 3) = 0),
                FOREIGN KEY(parent_code, parent_version)
                    REFERENCES parent(code, version)
                    ON UPDATE CASCADE
                    ON DELETE CASCADE
            );
            INSERT INTO parent(code) VALUES ('Alpha');
            INSERT INTO child(id, parent_code) VALUES (1, 'alpha');
            UPDATE parent
            SET (code, version) = ('Beta', version << 1)
            WHERE (code, version) IS NOT DISTINCT FROM ('alpha', 4);
            """;

        AssertQueryMatchesSqlite(
            """
            SELECT parent.code,
                   parent.version,
                   parent.parity,
                   child.parent_code,
                   child.parent_version,
                   child.inverted
            FROM parent
            JOIN child
              ON (parent.code, parent.version)
                 IS NOT DISTINCT FROM (child.parent_code, child.parent_version);
            """,
            setup);
    }

    [Test]
    public void LimitedDmlRecomputesOperatorGeneratedColumnsAfterTupleAssignments()
    {
        using var connection = new EmbeddedDatabase().Connect();
        ExecuteManagedBatch(
            connection,
            """
            CREATE TABLE limited_values(
                id INTEGER PRIMARY KEY,
                value INTEGER DEFAULT (8 >> 1),
                mask INTEGER GENERATED ALWAYS AS (value & 7) VIRTUAL,
                label TEXT COLLATE NOCASE,
                CHECK (value >= 0
                    AND (value, mask) IS NOT DISTINCT FROM (value, value & 7))
            );
            CREATE INDEX limited_label
                ON limited_values(label COLLATE NOCASE DESC, mask ASC);
            INSERT INTO limited_values(id, label) VALUES (1, 'alpha');
            INSERT INTO limited_values(id, value, label) VALUES (2, 3, 'Bravo');
            INSERT INTO limited_values(id, value, label) VALUES (3, 5, 'charlie');
            INSERT INTO limited_values(id, value, label) VALUES (4, 7, 'Delta');
            """);

        ReadManaged(
                connection,
                """
                UPDATE limited_values
                SET (value, label) = (value << 1, +label)
                WHERE (value & 1) = 1
                RETURNING id, value, mask, typeof(label)
                ORDER BY label COLLATE NOCASE DESC, id DESC
                LIMIT 2;
                """)
            .OrderBy(row => row[0].AsInteger())
            .Should().BeEquivalentTo(
            [
                new[]
                {
                    SqlValue.Integer(3),
                    SqlValue.Integer(10),
                    SqlValue.Integer(2),
                    SqlValue.Text("text"),
                },
                new[]
                {
                    SqlValue.Integer(4),
                    SqlValue.Integer(14),
                    SqlValue.Integer(6),
                    SqlValue.Text("text"),
                },
            ], options => options.WithStrictOrdering());

        Assert.Throws<EmbeddedSqlException>(
                () => ExecuteManaged(connection, "UPDATE limited_values SET value = -1 WHERE id = 1;"))
            !.Message.Should().Contain("CHECK constraint failed");

        ReadManaged(
            connection,
            """
            DELETE FROM limited_values
            WHERE (value, mask) IN ((10, 2), (14, 6))
            RETURNING id
            ORDER BY id DESC
            LIMIT 1;
            """)
            .Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(4));

        ReadManaged(connection, "SELECT id, value, mask FROM limited_values ORDER BY id;")
            .Should().BeEquivalentTo(
            [
                new[] { SqlValue.Integer(1), SqlValue.Integer(4), SqlValue.Integer(4) },
                new[] { SqlValue.Integer(2), SqlValue.Integer(3), SqlValue.Integer(3) },
                new[] { SqlValue.Integer(3), SqlValue.Integer(10), SqlValue.Integer(2) },
            ], options => options.WithStrictOrdering());
    }

    [Test]
    public void RegexpAndMatchUseOnlyRegisteredScalarSemanticsWithSqliteArgumentOrder()
    {
        var managedEvents = new List<string>();
        using var managed = new ManagedSqlite.SqliteConnection(
            "Data Source=:memory:;Local Provider=Managed");
        managed.CreateFunction<string, long, long>(
            "mark",
            (name, value) =>
            {
                managedEvents.Add(name);
                return value;
            });
        managed.CreateFunction<long, long, long>(
            "regexp",
            (pattern, value) =>
            {
                managedEvents.Add($"regexp:{pattern}:{value}");
                return pattern == 2 && value == 1 ? 1 : 0;
            });
        managed.CreateFunction<string, string, long>(
            "match",
            (pattern, value) => value.Contains(pattern, StringComparison.Ordinal) ? 1 : 0);
        managed.Open();

        ExecuteManagedProviderScalar(managed, "SELECT mark('left', 1) REGEXP mark('right', 2);")
            .Should().Be(1L);
        managedEvents.Should().Equal("right", "left", "regexp:2:1");
        ExecuteManagedProviderScalar(managed, "SELECT 'alphabet' MATCH 'pha';").Should().Be(1L);
        ExecuteManagedProviderScalar(managed, "SELECT 'alphabet' NOT MATCH 'zzz';").Should().Be(1L);

        using var unregistered = new EmbeddedDatabase().Connect();
        Assert.Throws<EmbeddedSqlException>(() => ReadManaged(unregistered, "SELECT 'x' MATCH 'x';"))
            !.Message.Should().Be("no such function: MATCH");
        Assert.Throws<EmbeddedSqlException>(
                () => unregistered.Prepare("CREATE VIRTUAL TABLE docs USING fts5(body);"))
            !.Message.Should().Contain("CREATE VIRTUAL TABLE modules are not supported");
    }

    [Test]
    public void RegisteredRegexpAndMatchWorkInCheckAndDefaultExpressions()
    {
        const string sql = """
            CREATE TABLE guarded(
                value TEXT CHECK (value REGEXP 'a'),
                matched INTEGER DEFAULT ('alphabet' MATCH 'pha'));
            INSERT INTO guarded(value) VALUES ('alpha');
            SELECT value, matched FROM guarded;
            """;

        using var managed = new ManagedSqlite.SqliteConnection(
            "Data Source=:memory:;Local Provider=Managed");
        managed.CreateFunction<string, string, long>(
            "regexp",
            (pattern, value) => value.StartsWith(pattern, StringComparison.Ordinal) ? 1 : 0);
        managed.CreateFunction<string, string, long>(
            "match",
            (pattern, value) => value.Contains(pattern, StringComparison.Ordinal) ? 1 : 0);
        managed.Open();
        var managedRows = ReadProvider(managed, sql);

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.CreateFunction<string, string, long>(
            "regexp",
            (pattern, value) => value.StartsWith(pattern, StringComparison.Ordinal) ? 1 : 0);
        sqlite.CreateFunction<string, string, long>(
            "match",
            (pattern, value) => value.Contains(pattern, StringComparison.Ordinal) ? 1 : 0);
        sqlite.Open();
        var sqliteRows = ReadProvider(sqlite, sql);

        managedRows.Should().BeEquivalentTo(sqliteRows, options => options.WithStrictOrdering());

        using var unregistered = new EmbeddedDatabase().Connect();
        Assert.Throws<EmbeddedSqlException>(
                () => ExecuteManaged(unregistered, "CREATE TABLE rejected(value CHECK (value REGEXP 'a'));"))
            !.Message.Should().Be("no such function: REGEXP");
    }

    [Test]
    public void MatchCancellationIsObservedAfterTheCallbackAndStatementRemainsReusable()
    {
        using var cancellation = new CancellationTokenSource();
        var cancel = true;
        var calls = 0;
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "match",
            2,
            _ =>
            {
                calls++;
                if (cancel)
                    cancellation.Cancel();
                return SqlValue.Integer(1);
            });
        using var connection = database.Connect();
        using var statement = connection.Prepare("SELECT 'value' MATCH 'pattern';");

        Assert.Throws<OperationCanceledException>(() => statement.Step(cancellation.Token));
        calls.Should().Be(1);

        cancel = false;
        statement.Reset();
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(1));
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void MatchCancellationKeepsDmlAtomic()
    {
        using var cancellation = new CancellationTokenSource();
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "match",
            2,
            _ =>
            {
                cancellation.Cancel();
                return SqlValue.Integer(1);
            });
        using var connection = database.Connect();
        ExecuteManaged(connection, "CREATE TABLE values_table(value INTEGER);");
        ExecuteManaged(connection, "INSERT INTO values_table VALUES (1), (2);");

        using (var statement = connection.Prepare(
                   "UPDATE values_table SET value = value + 10 WHERE value MATCH 'pattern';"))
        {
            Assert.Throws<OperationCanceledException>(() => statement.Step(cancellation.Token));
        }

        ReadManaged(connection, "SELECT value FROM values_table ORDER BY value;")
            .Select(row => row[0])
            .Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));
    }

    [Test]
    public void SafeUnaryAndBitwiseExpressionsUseRealVdbeWhileRowsAndCallbacksFallback()
    {
        var calls = 0;
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "regexp",
            2,
            _ =>
            {
                calls++;
                return SqlValue.Integer(1);
            });
        using var connection = database.Connect();

        var opcodes = ReadManaged(
                connection,
                "EXPLAIN SELECT +?1, ~?2, ?3 & 7, ?3 << ?4, -?5;",
                SqlValue.Text("10"),
                SqlValue.Text("10x"),
                SqlValue.Integer(8),
                SqlValue.Integer(1),
                SqlValue.Integer(2))
            .Select(row => row[1].AsText())
            .ToArray();
        opcodes.Count(opcode => opcode == "Arithmetic").Should().Be(5);
        opcodes.Should().Contain("NumericAffinity");

        ReadPlanDetail(connection, "EXPLAIN QUERY PLAN SELECT (1, 2) = (1, 2);")
            .Should().Be("MANAGED EVALUATOR FALLBACK");
        ReadPlanDetail(connection, "EXPLAIN QUERY PLAN SELECT 'x' REGEXP 'x';")
            .Should().Be("MANAGED EVALUATOR FALLBACK");
        calls.Should().Be(0);

        ExecuteManaged(connection, "CREATE TABLE routed(value INTEGER);");
        var dmlOpcodes = ReadManaged(
                connection,
                "EXPLAIN INSERT INTO routed VALUES (3) RETURNING +value, ~value, value << 1;")
            .Select(row => row[1].AsText())
            .ToArray();
        dmlOpcodes.Count(opcode => opcode == "Arithmetic").Should().Be(3);
    }

    private static void AssertQueryMatchesSqlite(string sql, params SqlValue[] parameters)
        => AssertQueryMatchesSqlite(sql, setup: null, parameters);

    private static void AssertQueryMatchesSqlite(string sql, string setup)
        => AssertQueryMatchesSqlite(sql, setup, []);

    private static void AssertQueryMatchesSqlite(
        string sql,
        string? setup,
        IReadOnlyList<SqlValue> parameters)
    {
        using var managed = new EmbeddedDatabase().Connect();
        if (setup is not null)
            ExecuteManagedBatch(managed, setup);
        var managedRows = ReadManaged(managed, sql, parameters);

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        if (setup is not null)
        {
            using var setupCommand = sqlite.CreateCommand();
            setupCommand.CommandText = setup;
            setupCommand.ExecuteNonQuery();
        }

        using var command = sqlite.CreateCommand();
        command.CommandText = sql;
        for (var index = 0; index < parameters.Count; index++)
            command.Parameters.AddWithValue($"?{index + 1}", ToSqliteValue(parameters[index]));
        using var reader = command.ExecuteReader();
        var sqliteRows = new List<object?[]>();
        while (reader.Read())
        {
            var values = new object?[reader.FieldCount];
            for (var index = 0; index < values.Length; index++)
                values[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            sqliteRows.Add(values);
        }

        managedRows.Should().HaveCount(sqliteRows.Count, because: sql);
        for (var row = 0; row < sqliteRows.Count; row++)
        {
            managedRows[row].Should().HaveCount(sqliteRows[row].Length, because: sql);
            for (var column = 0; column < sqliteRows[row].Length; column++)
                AssertCellMatches(managedRows[row][column], sqliteRows[row][column], sql);
        }
    }

    private static string ReadPlanDetail(EmbeddedConnection connection, string sql)
        => ReadManaged(connection, sql).Single()[3].AsText();

    private static List<SqlValue[]> ReadManaged(
        EmbeddedConnection connection,
        string sql,
        params SqlValue[] parameters)
        => ReadManaged(connection, sql, (IReadOnlyList<SqlValue>)parameters);

    private static List<SqlValue[]> ReadManaged(
        EmbeddedConnection connection,
        string sql,
        IReadOnlyList<SqlValue> parameters)
    {
        using var statement = connection.Prepare(sql);
        for (var index = 0; index < parameters.Count; index++)
            statement.Bind(index + 1, parameters[index]);

        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var values = new SqlValue[statement.GetColumnCount()];
            for (var index = 0; index < values.Length; index++)
                values[index] = statement.GetValue(index);
            rows.Add(values);
        }

        return rows;
    }

    private static void ExecuteManagedBatch(EmbeddedConnection connection, string sql)
    {
        foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries))
            ExecuteManaged(connection, statement);
    }

    private static object? ExecuteManagedProviderScalar(
        ManagedSqlite.SqliteConnection connection,
        string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static List<object?[]> ReadProvider(System.Data.Common.DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        do
        {
            while (reader.Read())
            {
                var values = new object?[reader.FieldCount];
                for (var index = 0; index < values.Length; index++)
                    values[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
                rows.Add(values);
            }
        }
        while (reader.NextResult());

        return rows;
    }

    private static void ExecuteManaged(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static object ToSqliteValue(SqlValue value) => value.Kind switch
    {
        SqlValueKind.Null => DBNull.Value,
        SqlValueKind.Integer => value.AsInteger(),
        SqlValueKind.Real => value.AsReal(),
        SqlValueKind.Text => value.AsText(),
        SqlValueKind.Blob => value.AsBlob().ToArray(),
        _ => throw new InvalidOperationException($"Unsupported SQLite parameter type {value.Kind}."),
    };

    private static void AssertCellMatches(SqlValue managed, object? sqlite, string sql)
    {
        switch (sqlite)
        {
            case null:
                managed.Should().Be(SqlValue.Null, because: sql);
                break;
            case long integer:
                managed.Should().Be(SqlValue.Integer(integer), because: sql);
                break;
            case double real:
                managed.Should().Be(SqlValue.Real(real), because: sql);
                break;
            case string text:
                managed.Should().Be(SqlValue.Text(text), because: sql);
                break;
            case byte[] blob:
                managed.Kind.Should().Be(SqlValueKind.Blob, because: sql);
                managed.AsBlob().ToArray().Should().Equal(blob, because: sql);
                break;
            default:
                Assert.Fail($"Unexpected SQLite value type {sqlite.GetType().Name} for {sql}.");
                break;
        }
    }
}
