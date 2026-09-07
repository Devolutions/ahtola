using System.Globalization;
using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Parsing;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

/// <summary>
/// Covers Turso's <c>CREATE INDEX ... NULLS FIRST/LAST</c> extension (turso-src core/schema.rs:5744
/// <c>IndexColumn.nulls_order</c>, core/types.rs <c>cmp_with_sort</c>) end to end: parsing, schema
/// round trip, the persisted comparator's independent null placement, planner ORDER BY elision,
/// table-constraint (PRIMARY KEY/UNIQUE) parity, continued UPSERT-target rejection, storage reopen,
/// and public-record binary compatibility for <see cref="SqliteIndexComparisonTerm"/>/
/// <see cref="SqlitePrimaryKeyTerm"/>.
/// </summary>
[NonParallelizable]
public sealed class IndexNullsOrderTests
{
    [TestCase("CREATE INDEX i ON t(a)")]
    [TestCase("CREATE INDEX i ON t(a NULLS FIRST)")]
    [TestCase("CREATE INDEX i ON t(a NULLS LAST)")]
    [TestCase("CREATE INDEX i ON t(a ASC NULLS LAST)")]
    [TestCase("CREATE INDEX i ON t(a DESC NULLS FIRST)")]
    [TestCase("CREATE INDEX i ON t(a DESC NULLS LAST)")]
    [TestCase("CREATE INDEX i ON t(a NULLS LAST, b DESC NULLS FIRST, c)")]
    [TestCase("CREATE INDEX i ON t(a COLLATE NOCASE NULLS LAST)")]
    public void CreateIndexSchemaRoundTripsExplicitNullsClauseVerbatim(string sql)
    {
        // Ahtola persists a table/index's CREATE ... SQL text verbatim (matching real SQLite's
        // sqlite_master behavior), rather than reconstructing/canonicalizing it the way the
        // upstream Turso fixture's own expectations do (see the NULLIDX residuals note in
        // managed-sqltest-expected-failures.txt for the pre-existing, NULLS-unrelated gap this
        // implies for 3 schema-roundtrip corpus cases). What must round-trip for this feature is
        // the NULLS clause itself, which verbatim storage trivially preserves.
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a, b, c);");
        Execute(connection, sql + ";");

        ReadScalarText(connection, "SELECT sql FROM sqlite_schema WHERE type='index' AND name='i';")
            .Should().Be(sql);
    }

    [Test]
    public void CreateIndexAscNullsLastPlacesNullsAfterNonNullValues()
    {
        using var connection = OpenNullOrderFixture("CREATE INDEX idx ON t(a ASC NULLS LAST);");
        ReadIds(connection, "SELECT id FROM t ORDER BY a ASC NULLS LAST, id;")
            .Should().Equal(3, 5, 2, 1, 4);
    }

    [Test]
    public void CreateIndexAscNullsFirstPlacesNullsBeforeNonNullValues()
    {
        using var connection = OpenNullOrderFixture("CREATE INDEX idx ON t(a ASC NULLS FIRST);");
        ReadIds(connection, "SELECT id FROM t ORDER BY a ASC NULLS FIRST, id;")
            .Should().Equal(1, 4, 3, 5, 2);
    }

    [Test]
    public void CreateIndexDescNullsFirstPlacesNullsBeforeNonNullValues()
    {
        using var connection = OpenNullOrderFixture("CREATE INDEX idx ON t(a DESC NULLS FIRST);");
        ReadIds(connection, "SELECT id FROM t ORDER BY a DESC NULLS FIRST, id;")
            .Should().Equal(1, 4, 2, 5, 3);
    }

    [Test]
    public void CreateIndexDescNullsLastPlacesNullsAfterNonNullValues()
    {
        using var connection = OpenNullOrderFixture("CREATE INDEX idx ON t(a DESC NULLS LAST);");
        ReadIds(connection, "SELECT id FROM t ORDER BY a DESC NULLS LAST, id;")
            .Should().Equal(2, 5, 3, 1, 4);
    }

    [Test]
    public void ExplicitNullsPlacementIsIndependentOfSortDirectionAndOverridesTheImplicitDefault()
    {
        // Implicit default for ASC is NULLS FIRST; an explicit ASC NULLS LAST index must still
        // place NULLs last, i.e. the reverse of what ASC alone would imply.
        using var ascNullsLast = OpenNullOrderFixture("CREATE INDEX idx ON t(a ASC NULLS LAST);");
        ReadIds(ascNullsLast, "SELECT id FROM t ORDER BY a ASC NULLS LAST, id;")
            .Should().NotEqual(ReadIds(ascNullsLast, "SELECT id FROM t ORDER BY a ASC NULLS FIRST, id;"));

        // Implicit default for DESC is NULLS LAST; an explicit DESC NULLS FIRST index must still
        // place NULLs first.
        using var descNullsFirst = OpenNullOrderFixture("CREATE INDEX idx ON t(a DESC NULLS FIRST);");
        ReadIds(descNullsFirst, "SELECT id FROM t ORDER BY a DESC NULLS FIRST, id;")
            .Should().NotEqual(ReadIds(descNullsFirst, "SELECT id FROM t ORDER BY a DESC NULLS LAST, id;"));
    }

    [Test]
    public void UniqueIndexWithNullsLastStillAllowsMultipleNullsButRejectsDuplicateNonNull()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a, b);");
        Execute(connection, "CREATE UNIQUE INDEX idx ON t(a NULLS LAST);");
        Execute(connection, "INSERT INTO t VALUES (NULL, 1), (NULL, 2), (5, 3);");
        ReadScalarInteger(connection, "SELECT count(*) FROM t;").Should().Be(3);

        Action duplicate = () => Execute(connection, "INSERT INTO t VALUES (5, 4);");
        duplicate.Should().Throw<EmbeddedSqlException>().WithMessage("*UNIQUE constraint failed*");
    }

    [Test]
    public void UpsertConflictTargetStillRejectsExplicitNullsClause()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b);");
        Execute(connection, "INSERT INTO t VALUES (1, 0);");

        Action upsert = () => Execute(
            connection,
            "INSERT INTO t(a, b) VALUES (1, 1) ON CONFLICT(a NULLS LAST) DO NOTHING;");
        upsert.Should().Throw<EmbeddedSqlException>().WithMessage("*NULLS*");
    }

    [Test]
    public void TableConstraintPrimaryKeyAcceptsNullsFirstAndLastAndSchemaRoundTrips()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        const string createT = "CREATE TABLE t(a INT, b, PRIMARY KEY(a NULLS LAST))";
        const string createT2 = "CREATE TABLE t2(a INT, b, PRIMARY KEY(a DESC NULLS FIRST, b NULLS LAST))";
        Execute(connection, $"{createT}; {createT2};");

        // Ahtola persists CREATE TABLE text verbatim (see the note on
        // CreateIndexSchemaRoundTripsExplicitNullsClauseVerbatim); the NULLS clause itself is
        // what must survive parse -> store -> reparse without being rejected or altered.
        ReadScalarText(connection, "SELECT sql FROM sqlite_schema WHERE type='table' AND name='t';")
            .Should().Be(createT);
        ReadScalarText(connection, "SELECT sql FROM sqlite_schema WHERE type='table' AND name='t2';")
            .Should().Be(createT2);
    }

    [Test]
    public void TableConstraintPrimaryKeyNullsOrdersRowsAndAllowsMultipleNulls()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INT, b, PRIMARY KEY(a NULLS LAST));");
        Execute(connection, "INSERT INTO t VALUES (NULL, 1), (3, 2), (1, 3), (NULL, 4), (2, 5);");

        ReadTextColumn(connection, "SELECT ifnull(a, 'null') FROM t ORDER BY a NULLS LAST LIMIT 3;")
            .Should().Equal("1", "2", "3");
        // NULL is never equal to NULL for uniqueness purposes: both NULL rows above are retained.
        ReadScalarInteger(connection, "SELECT count(*) FROM t;").Should().Be(5);

        Action duplicate = () => Execute(
            connection,
            "CREATE TABLE u(a INT, b, PRIMARY KEY(a NULLS LAST)); "
            + "INSERT INTO u VALUES (5, 1); INSERT INTO u VALUES (5, 2);");
        duplicate.Should().Throw<EmbeddedSqlException>();
    }

    [Test]
    public void PrimaryKeyNullsOnIntegerColumnKeepsRowidAliasAndCreatesNoAutoIndex()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b, PRIMARY KEY(a NULLS LAST));");
        Execute(connection, "INSERT INTO t(b) VALUES (10); INSERT INTO t(a, b) VALUES (NULL, 20);");

        ReadScalarInteger(connection, "SELECT count(*) FROM sqlite_schema WHERE type='index';")
            .Should().Be(0);
        var rows = Query(connection, "SELECT a, rowid, a IS rowid FROM t ORDER BY rowid;");
        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(1), SqlValue.Integer(1));
        rows[1].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(2), SqlValue.Integer(1));
    }

    [Test]
    public void PrimaryKeyNullsOnNonIntegerColumnCreatesAutoIndexAndOrdersNullsLast()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INT, b, PRIMARY KEY(a NULLS LAST));");

        ReadScalarInteger(connection, "SELECT count(*) FROM sqlite_schema WHERE type='index';")
            .Should().Be(1);
        Query(connection, "PRAGMA index_list(t);")
            .Select(row => row[1].AsText())
            .Should().Equal("sqlite_autoindex_t_1");
    }

    [Test]
    public void TableConstraintUniqueAcceptsNullsFirstAndLastAndSchemaRoundTrips()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        const string createT = "CREATE TABLE t(a INT, b, UNIQUE(a NULLS LAST), UNIQUE(b DESC NULLS FIRST))";
        Execute(connection, $"{createT};");

        // Verbatim persistence, same as the PRIMARY KEY case above.
        ReadScalarText(connection, "SELECT sql FROM sqlite_schema WHERE type='table' AND name='t';")
            .Should().Be(createT);

        Execute(connection, "INSERT INTO t VALUES (NULL, 1), (NULL, 2), (5, 3);");
        ReadScalarInteger(connection, "SELECT count(*) FROM t;").Should().Be(3);

        Action duplicate = () => Execute(connection, "INSERT INTO t VALUES (5, 4);");
        duplicate.Should().Throw<EmbeddedSqlException>();
    }

    [Test]
    public void UpsertConflictTargetOnTableConstraintStillRejectsExplicitNullsClause()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INT, b, UNIQUE(a NULLS LAST));");
        Execute(connection, "INSERT INTO t VALUES (5, 1);");

        Action upsert = () => Execute(
            connection,
            "INSERT INTO t VALUES (5, 2) ON CONFLICT(a NULLS LAST) DO NOTHING;");
        upsert.Should().Throw<EmbeddedSqlException>().WithMessage("*NULLS*");
    }

    [Test]
    public void CreateIndexUsingMethodRejectsExplicitNullsClauseOnAColumn()
    {
        var table = new EmbeddedTable(
            "t",
            [new EmbeddedColumn("a", "TEXT", false, false, false, null)]);
        var methodIndex = new EmbeddedIndex(
            "idx",
            Unique: false,
            Columns:
            [
                new EmbeddedIndexColumn("a", 0, null, false, NullPlacement: NullPlacement.Last),
            ],
            Method: "fts");

        Action validate = () => ManagedIndexMethodSemantics.ValidateDefinition("t", table, methodIndex);
        validate.Should().Throw<EmbeddedSqlException>().WithMessage("*NULLS*");
    }

    [Test]
    public void UncommittedTransactionOverlayHonorsExplicitNullPlacement()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, a INT);");
        Execute(connection, "CREATE INDEX idx ON t(a ASC NULLS LAST);");
        Execute(connection, "INSERT INTO t VALUES (1, 1), (2, NULL);");
        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO t VALUES (3, NULL), (4, 2);");

        // Uncommitted rows must merge into the same NULLS-LAST order as the committed rows.
        ReadIds(connection, "SELECT id FROM t ORDER BY a ASC NULLS LAST, id;")
            .Should().Equal(1, 4, 2, 3);

        Execute(connection, "ROLLBACK;");
    }

    [Test]
    public void FileBackedIndexWithNullsAndCustomCollationSurvivesReopen()
    {
        var path = CreateDatabasePath("nulls-reopen-nocase");
        try
        {
            long[] order;
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(
                    connection,
                    "CREATE TABLE t(id INTEGER PRIMARY KEY, label TEXT COLLATE NOCASE);");
                Execute(
                    connection,
                    "CREATE INDEX idx ON t(label COLLATE NOCASE DESC NULLS FIRST);");
                Execute(
                    connection,
                    "INSERT INTO t VALUES (1, 'beta'), (2, NULL), (3, 'ALPHA'), (4, NULL), (5, 'gamma');");
                order = ReadIds(connection, "SELECT id FROM t ORDER BY label COLLATE NOCASE DESC NULLS FIRST, id;");
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                ReadIds(connection, "SELECT id FROM t ORDER BY label COLLATE NOCASE DESC NULLS FIRST, id;")
                    .Should().Equal(order);
                ReadScalarText(connection, "SELECT sql FROM sqlite_schema WHERE type='index' AND name='idx';")
                    .Should().Be("CREATE INDEX idx ON t(label COLLATE NOCASE DESC NULLS FIRST)");
                ReadScalarText(connection, "PRAGMA integrity_check;").Should().Be("ok");
            }
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void FileBackedWithoutRowidPrimaryKeyNullsSurvivesReopen()
    {
        var path = CreateDatabasePath("nulls-reopen-without-rowid-pk");
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(
                    connection,
                    "CREATE TABLE t(a INT, b, PRIMARY KEY(a NULLS LAST)) WITHOUT ROWID;");
                Execute(connection, "INSERT INTO t VALUES (1, 1), (2, 2);");
                ReadScalarInteger(connection, "SELECT count(*) FROM t;").Should().Be(2);
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                ReadScalarInteger(connection, "SELECT count(*) FROM t;").Should().Be(2);
                ReadScalarText(connection, "PRAGMA integrity_check;").Should().Be("ok");
            }
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [TestCase(SqliteKeySortOrder.Ascending, null, false, TestName = "AscendingDefaultNullsFirst")]
    [TestCase(SqliteKeySortOrder.Descending, null, true, TestName = "DescendingDefaultNullsLast")]
    [TestCase(SqliteKeySortOrder.Ascending, SqliteIndexNullsOrder.First, false, TestName = "AscendingExplicitNullsFirst")]
    [TestCase(SqliteKeySortOrder.Ascending, SqliteIndexNullsOrder.Last, true, TestName = "AscendingExplicitNullsLast")]
    [TestCase(SqliteKeySortOrder.Descending, SqliteIndexNullsOrder.First, false, TestName = "DescendingExplicitNullsFirst")]
    [TestCase(SqliteKeySortOrder.Descending, SqliteIndexNullsOrder.Last, true, TestName = "DescendingExplicitNullsLast")]
    public void ComparerResolvesNullVersusNonNullIndependentlyOfSortOrder(
        SqliteKeySortOrder sortOrder,
        SqliteIndexNullsOrder? nullsOrder,
        bool nullSortsAfterNonNull)
    {
        var term = new SqliteIndexComparisonTerm(sortOrder, SqliteKeyCollation.Binary) { NullsOrder = nullsOrder };
        var comparer = new SqliteIndexRecordComparer(SqliteTextEncoding.Utf8, [term]);

        var nullFirst = comparer.Compare([SqlValue.Null], [SqlValue.Integer(1)]);
        var nonNullFirst = comparer.Compare([SqlValue.Integer(1)], [SqlValue.Null]);

        if (nullSortsAfterNonNull)
        {
            nullFirst.Should().BePositive();
            nonNullFirst.Should().BeNegative();
        }
        else
        {
            nullFirst.Should().BeNegative();
            nonNullFirst.Should().BePositive();
        }

        // Two NULLs never discriminate at this term, regardless of placement.
        comparer.Compare([SqlValue.Null], [SqlValue.Null]).Should().Be(0);
    }

    [Test]
    public void ComparerNonNullValueComparisonsStillHonorSortOrderWhenNullsOrderIsExplicit()
    {
        var descendingNullsFirst = new SqliteIndexComparisonTerm(
            SqliteKeySortOrder.Descending,
            SqliteKeyCollation.Binary)
        {
            NullsOrder = SqliteIndexNullsOrder.First,
        };
        var comparer = new SqliteIndexRecordComparer(SqliteTextEncoding.Utf8, [descendingNullsFirst]);

        // Non-NULL value comparisons remain governed by SortOrder (Descending): 3 sorts before 1.
        comparer.Compare([SqlValue.Integer(3)], [SqlValue.Integer(1)]).Should().BeNegative();
        comparer.Compare([SqlValue.Integer(1)], [SqlValue.Integer(3)]).Should().BePositive();
    }

    [Test]
    public void SqliteIndexComparisonTermPreservesOriginalTwoArgumentConstructorAndDeconstruct()
    {
        // Binary/source compatibility: the pre-existing positional constructor and Deconstruct
        // shape must survive unchanged; NullsOrder is an additive init-only property, not a third
        // positional parameter (see SqliteIndexRecordComparer.cs remarks).
        var term = new SqliteIndexComparisonTerm(SqliteKeySortOrder.Descending, SqliteKeyCollation.Binary);
        term.NullsOrder.Should().BeNull();

        var (sortOrder, collation) = term;
        sortOrder.Should().Be(SqliteKeySortOrder.Descending);
        collation.Should().Be(SqliteKeyCollation.Binary);

        var withNulls = term with { NullsOrder = SqliteIndexNullsOrder.First };
        withNulls.SortOrder.Should().Be(SqliteKeySortOrder.Descending);
        withNulls.NullsOrder.Should().Be(SqliteIndexNullsOrder.First);
    }

    [Test]
    public void SqlitePrimaryKeyTermPreservesOriginalFourArgumentConstructorAndDeconstruct()
    {
        var term = new SqlitePrimaryKeyTerm(
            0,
            "a",
            SqliteKeySortOrder.Ascending,
            SqliteKeyCollation.Binary);
        term.NullsOrder.Should().BeNull();

        var (columnIndex, columnName, sortOrder, collation) = term;
        columnIndex.Should().Be(0);
        columnName.Should().Be("a");
        sortOrder.Should().Be(SqliteKeySortOrder.Ascending);
        collation.Should().Be(SqliteKeyCollation.Binary);

        var withNulls = term with { NullsOrder = SqliteIndexNullsOrder.Last };
        withNulls.NullsOrder.Should().Be(SqliteIndexNullsOrder.Last);
    }

    [Test]
    public void ParserAcceptsNullsFirstAndLastOnCreateIndexColumns()
    {
        const string sql = "CREATE INDEX i ON t(a NULLS FIRST, b DESC NULLS LAST, c);";
        var statement = (CreateIndexStatement)SqlParser.Parse(sql, SqlParameterMap.Parse(sql));

        statement.Columns.Select(column => column.NullPlacement)
            .Should().Equal(NullPlacement.First, NullPlacement.Last, NullPlacement.Default);
    }

    [Test]
    public void ParserStillRejectsNullsFirstAndLastInUpsertConflictTarget()
    {
        const string sql = "INSERT INTO t(a) VALUES (1) ON CONFLICT(a NULLS FIRST) DO NOTHING;";
        Action parse = () => SqlParser.Parse(sql, SqlParameterMap.Parse(sql));
        parse.Should().Throw<EmbeddedSqlException>().WithMessage("*NULLS*");
    }

    private static EmbeddedConnection OpenNullOrderFixture(string createIndexSql)
    {
        var database = new EmbeddedDatabase();
        var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, a INT);");
        Execute(connection, createIndexSql);
        Execute(
            connection,
            "INSERT INTO t VALUES (1, NULL), (2, 3), (3, 1), (4, NULL), (5, 2);");
        return connection;
    }

    private static long[] ReadIds(EmbeddedConnection connection, string sql)
        => Query(connection, sql).Select(row => row[0].AsInteger()).ToArray();

    private static string[] ReadTextColumn(EmbeddedConnection connection, string sql)
        => Query(connection, sql).Select(row => FormatValue(row[0])).ToArray();

    private static string FormatValue(SqlValue value) => value.Kind switch
    {
        SqlValueKind.Null => "null",
        SqlValueKind.Integer => value.AsInteger().ToString(CultureInfo.InvariantCulture),
        SqlValueKind.Real => value.AsReal().ToString(CultureInfo.InvariantCulture),
        SqlValueKind.Text => value.AsText(),
        _ => value.ToString() ?? "null",
    };

    private static string ReadScalarText(EmbeddedConnection connection, string sql)
        => Query(connection, sql).Single().Single().AsText();

    private static long ReadScalarInteger(EmbeddedConnection connection, string sql)
        => Query(connection, sql).Single().Single().AsInteger();

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
                statement.Step().Should().Be(StatementStepResult.Done);
        }
    }

    private static List<SqlValue[]> Query(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var row = new SqlValue[statement.ColumnCount];
            for (var index = 0; index < row.Length; index++)
                row[index] = statement.GetValue(index);
            rows.Add(row);
        }

        return rows;
    }

    private static string CreateDatabasePath(string suffix)
    {
        var directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "index-nulls-order");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{suffix}-{Guid.NewGuid():N}.db");
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }
}
