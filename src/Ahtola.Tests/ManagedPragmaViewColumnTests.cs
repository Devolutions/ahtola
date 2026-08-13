using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

// Pins SQLite's table-valued PRAGMA behavior for views: pragma_table_info/xinfo
// resolve a view's columns from the view's SELECT, deriving the type per SQLite's
// expression HASTYPE rules. Every expectation in this file was verified against
// native sqlite3 3.53.3 before being recorded here.
public sealed class ManagedPragmaViewColumnTests
{
    [Test]
    public void TableInfoReportsViewColumnsDerivedFromTheViewSelect()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE T (A TEXT, B INTEGER);");
        Execute(connection, "CREATE VIEW V AS SELECT CAST(100 AS integer) AS Id, CAST('' AS text) AS Name;");

        var rows = ReadRows(connection, "PRAGMA table_info(V);");
        rows.Should().HaveCount(2);
        rows[0].Should().Equal(
            SqlValue.Integer(0),
            SqlValue.Text("Id"),
            SqlValue.Text("INT"),
            SqlValue.Integer(0),
            SqlValue.Null,
            SqlValue.Integer(0));
        rows[1].Should().Equal(
            SqlValue.Integer(1),
            SqlValue.Text("Name"),
            SqlValue.Text("TEXT"),
            SqlValue.Integer(0),
            SqlValue.Null,
            SqlValue.Integer(0));
    }

    [Test]
    public void TableXInfoTableValuedFormReportsViewColumnsWithZeroHidden()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE T (A TEXT, B INTEGER);");
        Execute(connection, "CREATE VIEW V AS SELECT CAST(100 AS integer) AS Id, CAST('' AS text) AS Name;");

        var rows = ReadRows(
            connection,
            "SELECT cid, name, type, \"notnull\", dflt_value, pk, hidden FROM pragma_table_xinfo('V');");
        rows.Should().HaveCount(2);
        rows[0].Should().Equal(
            SqlValue.Integer(0),
            SqlValue.Text("Id"),
            SqlValue.Text("INT"),
            SqlValue.Integer(0),
            SqlValue.Null,
            SqlValue.Integer(0),
            SqlValue.Integer(0));
        rows[1][6].Should().Be(SqlValue.Integer(0));
    }

    [Test]
    public void PlainColumnReferencesReportTheSourceDeclaredTypeVerbatim()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE T (A varchar(10), B numeric);");
        Execute(connection, "CREATE VIEW V AS SELECT A, B FROM T;");

        Types(connection, "V").Should().Equal("varchar(10)", "numeric");
    }

    [Test]
    public void UntypedColumnsReportNoTypeOnTablesButBlobThroughViews()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE T (A);");
        Execute(connection, "CREATE VIEW V AS SELECT A FROM T;");

        // Native parity: a table's own untyped column reports an empty type string,
        // but a view passing the same reference through falls back to the affinity
        // name "BLOB".
        Types(connection, "T", "T").Should().Equal(string.Empty);
        Types(connection, "V").Should().Equal("BLOB");
    }

    [Test]
    public void CastExpressionsReportTheAffinityNameOfTheCastType()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE T (A TEXT);");
        Execute(
            connection,
            "CREATE VIEW V AS SELECT "
            + "CAST(A AS integer) AS c_int, "
            + "CAST(A AS text) AS c_text, "
            + "CAST(A AS blob) AS c_blob, "
            + "CAST(A AS real) AS c_real, "
            + "CAST(A AS numeric) AS c_num, "
            + "CAST(A AS foo) AS c_foo, "
            + "CAST(A AS clob) AS c_clob, "
            + "CAST(A AS varchar(20)) AS c_varchar, "
            + "CAST(A AS float) AS c_float, "
            + "CAST(A AS \"double precision\") AS c_double, "
            + "CAST(A AS numeric(5,2)) AS c_numeric52 "
            + "FROM T;");

        Types(connection, "V").Should().Equal(
            "INT",
            "TEXT",
            "BLOB",
            "REAL",
            "NUM",
            "NUM",
            "TEXT",
            "TEXT",
            "REAL",
            "REAL",
            "NUM");
    }

    [Test]
    public void ComputedExpressionsReportNoType()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE T (A TEXT, B INTEGER);");
        Execute(
            connection,
            "CREATE VIEW V AS SELECT "
            + "B * 1 AS c_mul, "
            + "-B AS c_neg, "
            + "A || 'x' AS c_concat, "
            + "upper(A) AS c_upper, "
            + "coalesce(A, 'x') AS c_coalesce, "
            + "1 + 1 AS c_literal "
            + "FROM T;");

        Types(connection, "V").Should().Equal(
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty);
    }

    [Test]
    public void CollationPassthroughReportsTheInnerExpressionType()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE T (A TEXT);");
        Execute(connection, "CREATE VIEW V AS SELECT A COLLATE NOCASE AS C FROM T;");

        Types(connection, "V").Should().Equal("TEXT");
    }

    [Test]
    public void NestedViewsPropagateComputedTypes()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE T (A TEXT, U);");
        Execute(connection, "CREATE VIEW V1 AS SELECT A, U, A || 'x' AS E FROM T;");
        Execute(connection, "CREATE VIEW V2 AS SELECT A, U, E FROM V1;");

        Types(connection, "V2").Should().Equal("TEXT", "BLOB", string.Empty);
    }

    [Test]
    public void StarExpansionsReportEachSourceColumnType()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE T (A TEXT, B INTEGER);");
        Execute(connection, "CREATE TABLE S (C REAL);");
        Execute(connection, "CREATE VIEW V1 AS SELECT * FROM T;");
        Execute(connection, "CREATE VIEW V2 AS SELECT T.*, S.* FROM T, S;");

        Types(connection, "V1").Should().Equal("TEXT", "INTEGER");
        Types(connection, "V2").Should().Equal("TEXT", "INTEGER", "REAL");
    }

    [Test]
    public void CompoundSelectsReportTheFirstBranch()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE T (A TEXT);");
        Execute(connection, "CREATE VIEW V AS SELECT A FROM T UNION ALL SELECT CAST(1 AS integer);");

        Types(connection, "V").Should().Equal("TEXT");
    }

    [Test]
    public void ViewDeclaredColumnListsRenameColumnsKeepingDerivedTypes()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE T (A TEXT, B INTEGER);");
        Execute(connection, "CREATE VIEW V(X, Y) AS SELECT A, B FROM T;");

        var rows = ReadRows(connection, "PRAGMA table_info(V);");
        rows.Select(row => row[1]).ToArray().Should().Equal(SqlValue.Text("X"), SqlValue.Text("Y"));
        rows.Select(row => row[2]).ToArray().Should().Equal(SqlValue.Text("TEXT"), SqlValue.Text("INTEGER"));
    }

    [Test]
    public void ScalarSubqueriesPropagateTheSingleColumnType()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE T (A TEXT);");
        Execute(connection, "CREATE VIEW V AS SELECT (SELECT A FROM T LIMIT 1) AS S;");

        Types(connection, "V").Should().Equal("TEXT");
    }

    [Test]
    public void ViewsReportZeroNotnullDefaultAndPkFlags()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE T (A TEXT PRIMARY KEY NOT NULL DEFAULT 'x');");
        Execute(connection, "CREATE VIEW V AS SELECT A FROM T;");

        var rows = ReadRows(connection, "PRAGMA table_info(V);");
        rows.Should().ContainSingle();
        rows[0][3].Should().Be(SqlValue.Integer(0));
        rows[0][4].Should().Be(SqlValue.Null);
        rows[0][5].Should().Be(SqlValue.Integer(0));
    }

    private static string[] Types(EmbeddedConnection connection, string view, string? pragmaTarget = null)
        => ReadRows(connection, $"PRAGMA table_info({pragmaTarget ?? view});")
            .Select(row => row[2].AsText())
            .ToArray();

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
            var row = new SqlValue[statement.GetColumnCount()];
            for (var index = 0; index < row.Length; index++)
                row[index] = statement.GetValue(index);
            rows.Add(row);
        }

        return rows;
    }
}
