using AwesomeAssertions;
using ManagedSqlite = Ahtola.Data.Sqlite;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// SQLite converts text to a number under three different rules, and conflating them changes
/// stored values and result types. Numerification via <c>sqlite3_value_double</c> (CAST,
/// arithmetic, truth tests, <c>abs()</c>, <c>round()</c>) consumes a leading numeric prefix and
/// ignores the rest, so <c>CAST('12abc' AS INTEGER)</c> is 12. The math builtins instead use
/// <c>sqlite3_value_numeric_type</c>, converting only a value that is entirely a well-formed
/// number and returning NULL otherwise, so <c>sqrt('4x')</c> is NULL. Comparison and column
/// affinity use that same stricter conversion but leave the value as text, so <c>'12abc'</c>
/// stays text in a NUMERIC column and never compares equal to 12.
/// </summary>
public class NumericAffinityParityTests
{
    private static readonly string[] NumerifyExpressions =
    [
        "CAST('12abc' AS INTEGER)",
        "CAST('12abc' AS REAL)",
        "CAST('  12abc' AS INTEGER)",
        "CAST('-7xyz' AS INTEGER)",
        "CAST('+7xyz' AS INTEGER)",
        "CAST('3.5kg' AS REAL)",
        "CAST('1e3xyz' AS REAL)",
        "CAST('1e' AS REAL)",
        "CAST('.5x' AS REAL)",
        "CAST('abc' AS INTEGER)",
        "CAST('' AS INTEGER)",
        "CAST('0x10' AS INTEGER)",
        "CAST('12abc' AS NUMERIC)",
        "'12abc' + 0",
        "'12abc' * 2",
        "'12abc' - 2",
        "'1e3xyz' + 0",
        "'abc' + 1",
        "-'12abc'",
        "'12abc' % 5",
        "'12abc' / 2",
        "abs('12abc')",
        "round('12.7abc')",
        "'12abc' IS TRUE",
        "'abc' IS TRUE",
        "'0abc' IS TRUE",
        "'12abc' IS FALSE",
        "'12abc' | 0",
        "'12abc' << 1",
    ];

    private static readonly string[] MathBuiltinExpressions =
    [
        // sqlite3_value_numeric_type: only a value that is entirely a number converts, and
        // anything else - a numeric prefix, a blob, empty or blank text - yields NULL.
        "sqrt('4')", "sqrt('4.0')", "sqrt('  4  ')", "sqrt('4x')", "sqrt(x'34')", "sqrt('')", "sqrt(' ')",
        "ln('1')", "ln('abc')", "exp('0')", "exp('abc')", "pow('2','3')", "pow('abc',2)",
        "log10('100')", "log10('abc')", "atan2('1','1')", "acos('abc')",
        "ceil('1.2')", "ceil('4x')", "floor('1.8')", "floor('abc')", "floor('4.9x')",
        "sign('4')", "sign('-4abc')", "sign('abc')", "trunc('3.9x')", "trunc(x'34')",
    ];

    private static readonly string[] ModuloExpressions =
    [
        // mod() maps to C fmod, so it is always real even for integer operands.
        "mod(7,4)", "mod('7','4')", "mod(7.5,4)", "mod('7.5','4')", "mod(-7,4)",
        "mod(7,0)", "mod('7abc','4')", "mod(x'37','4')", "mod(-9223372036854775808,-1)",
    ];

    private static readonly string[] PrefixNumerifyingFunctionExpressions =
    [
        // abs() and round() read their operand with sqlite3_value_double, so a numeric prefix is
        // enough, a blob is read as its bytes, and non-numeric text is 0.0 rather than NULL.
        "abs(-12)", "abs('12')", "abs('12.5')", "abs('12abc')", "abs('-12.5x')",
        "abs('abc')", "abs('')", "abs(x'3132')",
        "round('12.7')", "round('12.7abc')", "round('abc')", "round(x'3132')", "round('12.789xy','2abc')",
    ];

    private static readonly string[] AggregateExpressions =
    [
        "sum('12abc')", "avg('12abc')", "total('12abc')", "sum('abc')", "sum(x'3132')",
        "max('12abc', 1)", "min('12abc', 1)",
    ];
    private static readonly string[] ComparisonExpressions =
    [
        "'12abc' = 12",
        "'12abc' < 12",
        "'12abc' > 12",
        "'12' = 12",
        "' 12 ' = 12",
        "'abc' = 0",
        "'' = 0",
    ];

    [Test]
    public void NumerificationConsumesALeadingNumericPrefix()
    {
        AssertParity(NumerifyExpressions);
    }

    [Test]
    public void ComparisonAffinityRequiresAWellFormedNumber()
    {
        AssertParity(ComparisonExpressions);
    }

    [Test]
    public void MathBuiltinsYieldNullWithoutACompleteNumber()
    {
        AssertParity(MathBuiltinExpressions);
    }

    [Test]
    public void ModuloAlwaysYieldsAReal()
    {
        AssertParity(ModuloExpressions);
    }

    [Test]
    public void AbsAndRoundNumerifyWithThePrefixRule()
    {
        AssertParity(PrefixNumerifyingFunctionExpressions);
    }

    [Test]
    public void NumericAggregatesAccumulateIntegersOnlyWhileEveryValueIsOne()
    {
        AssertParity(AggregateExpressions);
    }

    [Test]
    public void ColumnAffinityDoesNotStoreANumericPrefix()
    {
        AssertParity(
            """
            CREATE TABLE t(n NUMERIC, i INTEGER, r REAL);
            INSERT INTO t VALUES('12abc', '12abc', '12abc');
            INSERT INTO t VALUES('12', '12', '12');
            SELECT typeof(n), n, typeof(i), i, typeof(r), r FROM t;
            SELECT n = 12, i = 12, r = 12.0 FROM t;
            """);
    }

    [Test]
    public void NumerifiedTextIsComparedAsANumberOnlyAfterConversion()
    {
        // The prefix rule must not leak back into comparison: adding zero numerifies, but the
        // bare column reference keeps text affinity.
        AssertParity(
            """
            CREATE TABLE t(v TEXT);
            INSERT INTO t VALUES('12abc'), ('12'), ('abc');
            SELECT v, v = 12, v + 0 = 12, CAST(v AS INTEGER) = 12 FROM t ORDER BY rowid;
            """);
    }

    /// <summary>
    /// Compares every expression rather than stopping at the first divergence, so one report
    /// names every value and error that differs from SQLite.
    /// </summary>
    private static void AssertParity(IEnumerable<string> expressions)
    {
        var problems = new List<string>();
        foreach (var expression in expressions)
        {
            var sql = $"SELECT {expression};";
            var managed = Describe(() => Read(OpenManaged(), sql));
            var sqlite = Describe(() => Read(OpenSqlite(), sql));
            if (managed != sqlite)
                problems.Add($"{expression}: managed {managed}, sqlite {sqlite}");
        }

        if (problems.Count > 0)
            Assert.Fail(string.Join(Environment.NewLine, problems));
    }

    private static string Describe(Func<List<object?[]>> read)
    {
        try
        {
            return string.Join(
                " | ",
                read().Select(static row => string.Join(
                    ",",
                    row.Select(static value => $"{value?.GetType().Name ?? "null"}:{value ?? "null"}"))));
        }
        catch (Exception exception)
        {
            return $"error: {exception.Message}";
        }
    }

    private static void AssertParity(string sql)
    {
        var managedRows = Read(OpenManaged(), sql);
        var sqliteRows = Read(OpenSqlite(), sql);
        managedRows.Should().BeEquivalentTo(
            sqliteRows,
            options => options.WithStrictOrdering(),
            "managed and SQLite must agree for {0}",
            sql);
    }

    private static System.Data.Common.DbConnection OpenManaged()
    {
        var connection = new ManagedSqlite.SqliteConnection(
            "Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        return connection;
    }

    private static System.Data.Common.DbConnection OpenSqlite()
    {
        var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private static List<object?[]> Read(System.Data.Common.DbConnection connection, string sql)
    {
        using var owned = connection;
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
}
