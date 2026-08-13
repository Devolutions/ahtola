using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

/// <summary>
/// Locks in SQLite-compatible behavior for the scalar builtins that the managed
/// engine previously rejected with "no such function".
/// </summary>
public sealed class ManagedScalarFunctionParityTests
{
    [TestCase("SELECT substr('abcdef', 2, 3);", "bcd")]
    [TestCase("SELECT substr('abcdef', 2);", "bcdef")]
    [TestCase("SELECT substr('abcdef', -2);", "ef")]
    [TestCase("SELECT substr('abcdef', 0, 2);", "a")]
    [TestCase("SELECT substr('abcdef', 3, -2);", "ab")]
    [TestCase("SELECT substring('abcdef', 1, 2);", "ab")]
    [TestCase("SELECT replace('abcabc', 'b', 'X');", "aXcaXc")]
    [TestCase("SELECT replace('abc', '', 'X');", "abc")]
    [TestCase("SELECT trim('  x  ');", "x")]
    [TestCase("SELECT trim('xxhixx', 'x');", "hi")]
    [TestCase("SELECT ltrim('  x  ');", "x  ")]
    [TestCase("SELECT rtrim('  x  ');", "  x")]
    [TestCase("SELECT quote('a''b');", "'a''b'")]
    [TestCase("SELECT quote(NULL);", "NULL")]
    [TestCase("SELECT char(65, 66);", "AB")]
    [TestCase("SELECT concat('a', 'b', NULL, 'c');", "abc")]
    [TestCase("SELECT concat_ws('-', 'a', NULL, 'b');", "a-b")]
    [TestCase("SELECT sqlite_version();", "3.50.4")]
    [TestCase("SELECT hex(unhex('414243'));", "414243")]
    [TestCase("SELECT quote(unhex('41FF'));", "X'41FF'")]
    public void ManagedEngineEvaluatesTextReturningBuiltinsLikeSqlite(string sql, string expected)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        ReadValue(connection, sql).Should().Be(SqlValue.Text(expected));
    }

    [TestCase("SELECT unicode('A');", 65L)]
    [TestCase("SELECT length(zeroblob(4));", 4L)]
    [TestCase("SELECT length(randomblob(4));", 4L)]
    [TestCase("SELECT sign(-3);", -1L)]
    [TestCase("SELECT sign(0);", 0L)]
    [TestCase("SELECT sign(9);", 1L)]
    [TestCase("SELECT iif(1, 7, 9);", 7L)]
    [TestCase("SELECT iif(0, 7, 9);", 9L)]
    [TestCase("SELECT likely(7);", 7L)]
    [TestCase("SELECT unlikely(7);", 7L)]
    [TestCase("SELECT likelihood(7, 0.5);", 7L)]
    public void ManagedEngineEvaluatesIntegerReturningBuiltinsLikeSqlite(string sql, long expected)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        ReadValue(connection, sql).Should().Be(SqlValue.Integer(expected));
    }

    [TestCase("SELECT boolean_to_int('yes');", 1L)]
    [TestCase("SELECT boolean_to_int('off');", 0L)]
    [TestCase("SELECT boolean_to_int(1);", 1L)]
    [TestCase("SELECT boolean_to_int(0);", 0L)]
    public void ManagedEngineEvaluatesTursoBooleanConversion(string sql, long expected)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        ReadValue(connection, sql).Should().Be(SqlValue.Integer(expected));
    }

    [TestCase("SELECT int_to_boolean(0);", "false")]
    [TestCase("SELECT int_to_boolean(2);", "true")]
    [TestCase("SELECT validate_ipaddr('127.0.0.1');", "127.0.0.1")]
    [TestCase("SELECT validate_ipaddr('2001:db8::1');", "2001:db8::1")]
    public void ManagedEngineEvaluatesTursoTypeSupportTextFunctions(string sql, string expected)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        ReadValue(connection, sql).Should().Be(SqlValue.Text(expected));
    }

    [TestCase("SELECT boolean_to_int(NULL);")]
    [TestCase("SELECT int_to_boolean(NULL);")]
    [TestCase("SELECT validate_ipaddr(NULL);")]
    public void ManagedEnginePropagatesNullThroughTursoTypeSupportFunctions(string sql)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        ReadValue(connection, sql).Should().Be(SqlValue.Null);
    }

    [TestCase("SELECT boolean_to_int('maybe');", "invalid input for type boolean")]
    [TestCase("SELECT boolean_to_int(2);", "invalid input for type boolean")]
    [TestCase("SELECT validate_ipaddr('999.1.1.1');", "invalid input for type inet")]
    public void ManagedEngineRejectsInvalidTursoTypeSupportValues(string sql, string expected)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Assert.Throws<EmbeddedSqlException>(() => ReadValue(connection, sql))!
            .Message.Should().Contain(expected);
    }

    [TestCase("SELECT ceil(1);", 1L)]
    [TestCase("SELECT ceiling(1);", 1L)]
    [TestCase("SELECT floor(1);", 1L)]
    [TestCase("SELECT trunc(1);", 1L)]
    [TestCase("SELECT ceil('9223372036854775807');", long.MaxValue)]
    public void ManagedEnginePreservesIntegerMathOperands(string sql, long expected)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        ReadValue(connection, sql).Should().Be(SqlValue.Integer(expected));
    }

    [TestCase("SELECT char('not a code point');", "")]
    [TestCase("SELECT char(65, 1.5, 'ignored', X'42', 66);", "AB")]
    public void ManagedEngineIgnoresNonIntegerCharArguments(string sql, string expected)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        ReadValue(connection, sql).Should().Be(SqlValue.Text(expected));
    }

    [TestCase("SELECT substr('a😀b', 2, 1);", "😀")]
    [TestCase("SELECT substr('a😀b', -2, 1);", "😀")]
    [TestCase("SELECT substring('a😀b', 2, 2);", "😀b")]
    public void ManagedEngineCountsSupplementaryCharactersAsSingleSubstringUnits(string sql, string expected)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        ReadValue(connection, sql).Should().Be(SqlValue.Text(expected));
    }

    [TestCase("SELECT octet_length('ąłóżźć');", 12L)]
    [TestCase("SELECT octet_length(X'010203');", 3L)]
    [TestCase("SELECT octet_length(12345);", 5L)]
    [TestCase("SELECT octet_length(123.456);", 7L)]
    public void ManagedEngineCountsUtf8BytesForOctetLength(string sql, long expected)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        ReadValue(connection, sql).Should().Be(SqlValue.Integer(expected));
    }

    [TestCase("SELECT 'a' || char(0) || 'b' GLOB 'a?b';", 0L)]
    [TestCase("SELECT 'a' || char(0) || 'b' GLOB 'a??';", 0L)]
    [TestCase("SELECT 'a' || char(0) || 'b' GLOB 'a' || char(0) || 'b';", 1L)]
    [TestCase("SELECT 'a' || char(0) || 'b' GLOB 'a*';", 1L)]
    [TestCase("SELECT 'ab' GLOB 'a?';", 1L)]
    public void ManagedEngineTruncatesGlobAtEmbeddedNul(string sql, long expected)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        ReadValue(connection, sql).Should().Be(SqlValue.Integer(expected));
    }

    [TestCase("SELECT 'a' || char(0) || 'b' LIKE 'a_b';", 0L)]
    [TestCase("SELECT 'a' || char(0) || 'b' LIKE 'a__';", 0L)]
    [TestCase("SELECT 'ab' LIKE 'a_';", 1L)]
    public void ManagedEngineTruncatesLikeAtEmbeddedNul(string sql, long expected)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        ReadValue(connection, sql).Should().Be(SqlValue.Integer(expected));
    }

    [TestCase("SELECT round(2.567, 1);", 2.6d)]
    [TestCase("SELECT round(2.5);", 3.0d)]
    [TestCase("SELECT round(-2.5);", -3.0d)]
    [TestCase("SELECT ceil(1.2);", 2.0d)]
    [TestCase("SELECT floor(1.8);", 1.0d)]
    [TestCase("SELECT trunc(1.8);", 1.0d)]
    [TestCase("SELECT sqrt(9.0);", 3.0d)]
    [TestCase("SELECT pow(2.0, 8.0);", 256.0d)]
    [TestCase("SELECT ln(1.0);", 0.0d)]
    [TestCase("SELECT log10(100.0);", 2.0d)]
    [TestCase("SELECT log(100.0);", 2.0d)]
    [TestCase("SELECT log(2.0, 8.0);", 3.0d)]
    [TestCase("SELECT exp(0.0);", 1.0d)]
    [TestCase("SELECT degrees(pi());", 180.0d)]
    [TestCase("SELECT sin(0.0);", 0.0d)]
    [TestCase("SELECT cos(0.0);", 1.0d)]
    [TestCase("SELECT atan2(0.0, 1.0);", 0.0d)]
    // mod() maps to C fmod in SQLite, so it yields a real even for integer operands.
    [TestCase("SELECT mod(7, 3);", 1.0d)]
    [TestCase("SELECT mod(-7, 3);", -1.0d)]
    public void ManagedEngineEvaluatesRealReturningMathBuiltinsLikeSqlite(string sql, double expected)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        var value = ReadValue(connection, sql);
        value.Kind.Should().Be(SqlValueKind.Real);
        value.AsReal().Should().BeApproximately(expected, 1e-9);
    }

    // SQLite reports NULL for domain errors and NULL operands rather than raising
    // or surfacing NaN/infinity to the caller.
    [TestCase("SELECT sqrt(-1);")]
    [TestCase("SELECT ln(-1);")]
    [TestCase("SELECT log(0);")]
    [TestCase("SELECT log(1, 8);")]
    [TestCase("SELECT mod(7, 0);")]
    [TestCase("SELECT substr(NULL, 1, 1);")]
    [TestCase("SELECT replace(NULL, 'a', 'b');")]
    [TestCase("SELECT trim(NULL);")]
    [TestCase("SELECT round(NULL);")]
    [TestCase("SELECT sign(NULL);")]
    [TestCase("SELECT unicode('');")]
    [TestCase("SELECT unhex('4z');")]
    [TestCase("SELECT unhex('abc');")]
    public void ManagedEngineReturnsNullForDomainErrorsAndNullOperands(string sql)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        ReadValue(connection, sql).Kind.Should().Be(SqlValueKind.Null);
    }

    [Test]
    public void ManagedEngineTracksChangesAndTotalChangesAcrossStatements()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE t(a);");

        Execute(connection, "INSERT INTO t VALUES (1), (2), (3);");
        ReadValue(connection, "SELECT changes();").Should().Be(SqlValue.Integer(3));
        ReadValue(connection, "SELECT total_changes();").Should().Be(SqlValue.Integer(3));

        Execute(connection, "DELETE FROM t WHERE a > 1;");
        ReadValue(connection, "SELECT changes();").Should().Be(SqlValue.Integer(2));
        ReadValue(connection, "SELECT total_changes();").Should().Be(SqlValue.Integer(5));

        Execute(connection, "UPDATE t SET a = 9;");
        ReadValue(connection, "SELECT changes();").Should().Be(SqlValue.Integer(1));
        ReadValue(connection, "SELECT total_changes();").Should().Be(SqlValue.Integer(6));
    }

    // Explicit-transaction statements run against a working catalog clone through a
    // separate Execute overload; the counters must still track those in-transaction
    // writes (EF Core's no-RETURNING backfill relies on changes() inside its implicit
    // transaction).
    [Test]
    public void ManagedEngineTracksChangesInsideExplicitTransactions()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE t(a);");
        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO t VALUES (1), (2);");
        ReadValue(connection, "SELECT changes();").Should().Be(SqlValue.Integer(2));
        ReadValue(connection, "SELECT total_changes();").Should().Be(SqlValue.Integer(2));
        Execute(connection, "UPDATE t SET a = a + 10 WHERE a = 1;");
        ReadValue(connection, "SELECT changes();").Should().Be(SqlValue.Integer(1));
        ReadValue(connection, "SELECT total_changes();").Should().Be(SqlValue.Integer(3));
        Execute(connection, "COMMIT;");
        ReadValue(connection, "SELECT changes();").Should().Be(SqlValue.Integer(1));
    }

    // A SELECT must not clear the counters that a preceding DML statement set.
    [Test]
    public void ManagedEngineLeavesChangeCountersUntouchedByNonMutatingStatements()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE t(a);");
        Execute(connection, "INSERT INTO t VALUES (1), (2);");

        ReadValue(connection, "SELECT a FROM t;").Should().Be(SqlValue.Integer(1));
        ReadValue(connection, "SELECT changes();").Should().Be(SqlValue.Integer(2));
    }

    [Test]
    public void ManagedEngineTreatsRandomAsNonDeterministic()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        var observed = new HashSet<long>();
        for (var attempt = 0; attempt < 32; attempt++)
        {
            observed.Add(ReadValue(connection, "SELECT random();").AsInteger());
        }

        observed.Count.Should().BeGreaterThan(1);
    }

    [Test]
    public void ManagedEngineComputesTimeDiffWithSqliteLayout()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        ReadValue(connection, "SELECT timediff('2024-01-02', '2024-01-01');")
            .Should().Be(SqlValue.Text("+0000-00-01 00:00:00.000"));
        ReadValue(connection, "SELECT timediff('2024-01-01', '2024-01-02');")
            .Should().Be(SqlValue.Text("-0000-00-01 00:00:00.000"));
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static SqlValue ReadValue(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0);
    }
}
