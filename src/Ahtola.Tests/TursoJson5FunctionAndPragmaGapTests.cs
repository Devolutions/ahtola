using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

/// <summary>
/// Regressions for the Turso v0.7.2 SQL surface gaps closed in the managed engine: the JSON5
/// leniencies accepted by every value-producing JSON entry point, unhex() separator handling,
/// the zero-argument is_autocommit() scalar, and the column-name/MVCC-threshold/list_types
/// pragmas. Each test targets exactly one numbered gap so a regression names its own cause.
/// </summary>
public sealed class TursoJson5FunctionAndPragmaGapTests
{
    // (1) JSON5 hexadecimal numbers are parsed and canonicalized to decimal.
    [Test]
    public void Json5HexadecimalNumbersParseToDecimalIntegers()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ScalarText(connection, "SELECT json('0x1A');").Should().Be("26");
        ScalarText(connection, "SELECT json('-0xFF');").Should().Be("-255");
        ScalarText(connection, "SELECT json('[0x0, 0xff]');").Should().Be("[0,255]");
        ScalarText(connection, "SELECT json_type('0x1A');").Should().Be("integer");
    }

    // (2) JSON5 numbers may carry an explicit leading plus sign.
    [Test]
    public void Json5ExplicitPlusNumbersDropTheSign()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ScalarText(connection, "SELECT json('+42');").Should().Be("42");
        ScalarText(connection, "SELECT json('+1.5');").Should().Be("1.5");
        ScalarText(connection, "SELECT json('{\"a\": +7}');").Should().Be("{\"a\":7}");
        ScalarText(connection, "SELECT json_type('+42');").Should().Be("integer");
    }

    // (3) JSON5 numbers may begin with a decimal point.
    [Test]
    public void Json5LeadingDotNumbersGainALeadingZero()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ScalarText(connection, "SELECT json('.5');").Should().Be("0.5");
        ScalarText(connection, "SELECT json('-.25');").Should().Be("-0.25");
        ScalarText(connection, "SELECT json('[.5]');").Should().Be("[0.5]");
        ScalarText(connection, "SELECT json_type('.5');").Should().Be("real");
    }

    // (4) JSON5 numbers may end with a trailing decimal point.
    [Test]
    public void Json5TrailingDotNumbersGainATrailingZero()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ScalarText(connection, "SELECT json('42.');").Should().Be("42.0");
        ScalarText(connection, "SELECT json('-3.');").Should().Be("-3.0");
        ScalarText(connection, "SELECT json('{\"a\": 1.}');").Should().Be("{\"a\":1.0}");
        ScalarText(connection, "SELECT json_type('42.');").Should().Be("real");
    }

    // (5) JSON5 Infinity/NaN literals map to SQLite's 9e999 and JSON null respectively.
    [Test]
    public void Json5InfinityAndNanLiteralsAreAccepted()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ScalarText(connection, "SELECT json('Infinity');").Should().Be("9e999");
        ScalarText(connection, "SELECT json('-Infinity');").Should().Be("-9e999");
        ScalarText(connection, "SELECT json('NaN');").Should().Be("null");
        ScalarText(connection, "SELECT json('[Infinity, NaN]');").Should().Be("[9e999,null]");
        ScalarText(connection, "SELECT json_type('NaN');").Should().Be("null");
    }

    // (6) JSON5 single-quoted strings are re-emitted with canonical double quotes.
    [Test]
    public void Json5SingleQuotedStringsAreRequoted()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ScalarText(connection, "SELECT json('''hello world''');").Should().Be("\"hello world\"");
        ScalarText(connection, "SELECT json('[''a'', ''b'']');").Should().Be("[\"a\",\"b\"]");
        ScalarText(connection, "SELECT json('{''a'': ''b''}');").Should().Be("{\"a\":\"b\"}");
        ScalarText(connection, "SELECT json_type('''x''');").Should().Be("text");
    }

    // (7) JSON5 object keys may be bare identifiers.
    [Test]
    public void Json5UnquotedObjectKeysAreQuoted()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ScalarText(connection, "SELECT json('{a:1}');").Should().Be("{\"a\":1}");
        ScalarText(connection, "SELECT json('{ alpha : 1, _b2 : 2, $c : 3 }');")
            .Should().Be("{\"alpha\":1,\"_b2\":2,\"$c\":3}");
        ScalarText(connection, "SELECT json_extract('{a:{b:7}}', '$.a.b');").Should().Be("7");
    }

    // (8) JSON5 arrays and objects tolerate a single trailing comma.
    [Test]
    public void Json5TrailingCommasAreAccepted()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ScalarText(connection, "SELECT json('[1, 2, ]');").Should().Be("[1,2]");
        ScalarText(connection, "SELECT json('{\"a\":1,}');").Should().Be("{\"a\":1}");
        ScalarText(connection, "SELECT json_array_length('[1,2,3,]');").Should().Be("3");

        // A doubled comma is still a hole, not a trailing comma.
        var doubled = () => ScalarText(connection, "SELECT json('[1,,2]');");
        doubled.Should().Throw<EmbeddedSqlException>();
    }

    // (9) JSON5 line and block comments are skipped wherever whitespace is allowed.
    [Test]
    public void Json5CommentsAreSkipped()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ScalarText(connection, "SELECT json('[1, /* two */ 2] // trailing');").Should().Be("[1,2]");
        ScalarText(connection, "SELECT json('// leading" + "\n" + "{\"a\":1}');").Should().Be("{\"a\":1}");
        ScalarText(connection, "SELECT json('{/*k*/ a /*v*/ : /*x*/ 1}');").Should().Be("{\"a\":1}");
    }

    // (10) unhex(text, separators) ignores separator characters anywhere, not just at the ends.
    [Test]
    public void UnhexIgnoresSeparatorsAnywhereInTheInput()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ScalarText(connection, "SELECT hex(unhex('41:42:43', ':'));").Should().Be("414243");
        ScalarText(connection, "SELECT hex(unhex('-41-42-', '-'));").Should().Be("4142");
        ScalarText(connection, "SELECT hex(unhex('41 42\t43', ' \t'));").Should().Be("414243");
        ScalarText(connection, "SELECT hex(unhex('', ':'));").Should().Be(string.Empty);

        // A separator splitting a byte's two nibbles is still skipped before the pair only.
        ScalarValue(connection, "SELECT unhex('4:1', ':');").Kind.Should().Be(SqlValueKind.Null);
        // A separator that is also a hex digit keeps its digit meaning.
        ScalarText(connection, "SELECT hex(unhex('41', '4'));").Should().Be("41");
        ScalarValue(connection, "SELECT unhex('41:4', ':');").Kind.Should().Be(SqlValueKind.Null);
    }

    // (11) is_autocommit() reports 1 outside and 0 inside an explicit transaction.
    [Test]
    public void IsAutocommitReportsTransactionState()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ScalarText(connection, "SELECT is_autocommit();").Should().Be("1");

        Execute(connection, "BEGIN;");
        ScalarText(connection, "SELECT is_autocommit();").Should().Be("0");
        Execute(connection, "COMMIT;");

        ScalarText(connection, "SELECT is_autocommit();").Should().Be("1");

        var wrongArity = () => ScalarText(connection, "SELECT is_autocommit(1);");
        wrongArity.Should().Throw<EmbeddedSqlException>();
    }

    // (12) PRAGMA full_column_names round-trips as connection state, defaulting to off.
    [Test]
    public void PragmaFullColumnNamesRoundTripsWithTursoDefault()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ColumnNames(connection, "PRAGMA full_column_names;").Should().Equal("full_column_names");
        ReadRows(connection, "PRAGMA full_column_names;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(0));

        Execute(connection, "PRAGMA full_column_names = ON;");
        ReadRows(connection, "PRAGMA full_column_names;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(1));

        Execute(connection, "PRAGMA full_column_names(0);");
        ReadRows(connection, "PRAGMA full_column_names;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(0));
    }

    // (13) PRAGMA short_column_names round-trips as connection state, defaulting to on.
    [Test]
    public void PragmaShortColumnNamesRoundTripsWithTursoDefault()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ColumnNames(connection, "PRAGMA short_column_names;").Should().Equal("short_column_names");
        ReadRows(connection, "PRAGMA short_column_names;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(1));

        Execute(connection, "PRAGMA short_column_names = OFF;");
        ReadRows(connection, "PRAGMA short_column_names;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(0));

        Execute(connection, "PRAGMA short_column_names = true;");
        ReadRows(connection, "PRAGMA short_column_names;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(1));
    }

    // (14) PRAGMA mvcc_checkpoint_threshold accepts every value >= -1 and rejects the rest.
    [Test]
    public void PragmaMvccCheckpointThresholdRoundTripsAndValidates()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ColumnNames(connection, "PRAGMA mvcc_checkpoint_threshold;")
            .Should().Equal("mvcc_checkpoint_threshold");

        Execute(connection, "PRAGMA mvcc_checkpoint_threshold = -1;");
        ReadRows(connection, "PRAGMA mvcc_checkpoint_threshold;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(-1));

        Execute(connection, "PRAGMA mvcc_checkpoint_threshold = 0;");
        ReadRows(connection, "PRAGMA mvcc_checkpoint_threshold;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(0));

        Execute(connection, "PRAGMA mvcc_checkpoint_threshold = 4096;");
        ReadRows(connection, "PRAGMA mvcc_checkpoint_threshold;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(4096));

        var rejected = () => Execute(connection, "PRAGMA mvcc_checkpoint_threshold = -2;");
        rejected.Should().Throw<EmbeddedSqlException>()
            .WithMessage("mvcc_checkpoint_threshold must be -1, 0, or a positive integer");

        ReadRows(connection, "PRAGMA mvcc_checkpoint_threshold;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(4096));
    }

    // (15) PRAGMA mvcc_gc_threshold accepts -1 or any positive integer; 0 is rejected.
    [Test]
    public void PragmaMvccGcThresholdRoundTripsAndValidates()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ColumnNames(connection, "PRAGMA mvcc_gc_threshold;").Should().Equal("mvcc_gc_threshold");
        ReadRows(connection, "PRAGMA mvcc_gc_threshold;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(16 * 1024));

        Execute(connection, "PRAGMA mvcc_gc_threshold = -1;");
        ReadRows(connection, "PRAGMA mvcc_gc_threshold;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(-1));

        Execute(connection, "PRAGMA mvcc_gc_threshold = 32;");
        ReadRows(connection, "PRAGMA mvcc_gc_threshold;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(32));

        var zeroRejected = () => Execute(connection, "PRAGMA mvcc_gc_threshold = 0;");
        zeroRejected.Should().Throw<EmbeddedSqlException>()
            .WithMessage("mvcc_gc_threshold must be -1 (disabled) or a positive integer");

        var negativeRejected = () => Execute(connection, "PRAGMA mvcc_gc_threshold = -5;");
        negativeRejected.Should().Throw<EmbeddedSqlException>()
            .WithMessage("mvcc_gc_threshold must be -1 (disabled) or a positive integer");

        ReadRows(connection, "PRAGMA mvcc_gc_threshold;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(32));
    }

    // (16) PRAGMA list_types reports the five built-in storage types.
    [Test]
    public void PragmaListTypesReportsBuiltInTypes()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ColumnNames(connection, "PRAGMA list_types;")
            .Should().Equal("name", "type", "notnull", "dflt_value", "pk", "hidden");

        var rows = ReadRows(connection, "PRAGMA list_types;");
        rows.Should().HaveCount(5);
        rows.Select(row => row[0].AsText())
            .Should().Equal("INTEGER", "REAL", "TEXT", "BLOB", "ANY");
        rows.Should().AllSatisfy(row =>
            row.Skip(1).Select(value => value.Kind).Should().AllBeEquivalentTo(SqlValueKind.Null));
    }

    // (17) PRAGMA list_types cannot be assigned to.
    [Test]
    public void PragmaListTypesRejectsAssignment()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        var assign = () => Execute(connection, "PRAGMA list_types = 1;");
        assign.Should().Throw<EmbeddedSqlException>().WithMessage("list_types cannot be set*");

        var parenthesized = () => Execute(connection, "PRAGMA list_types(1);");
        parenthesized.Should().Throw<EmbeddedSqlException>().WithMessage("list_types cannot be set*");
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static SqlValue ScalarValue(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0);
    }

    private static string ScalarText(EmbeddedConnection connection, string sql)
    {
        var value = ScalarValue(connection, sql);
        return value.Kind switch
        {
            SqlValueKind.Null => "null",
            SqlValueKind.Text => value.AsText(),
            SqlValueKind.Integer => value.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
    }

    private static string[] ColumnNames(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var columns = new string[statement.GetColumnCount()];
        for (var index = 0; index < columns.Length; index++)
            columns[index] = statement.GetColumnName(index);

        return columns;
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
