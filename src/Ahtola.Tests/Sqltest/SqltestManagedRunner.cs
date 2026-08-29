using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ahtola.Core;

namespace Ahtola.Tests.Sqltest;

internal sealed record SqltestOutcome(bool Matched, string Detail);

internal sealed record SqltestIntegrityResult(IReadOnlyList<string> Rows, string? Error);

internal delegate SqltestIntegrityResult SqltestIntegrityCheck(
    EmbeddedConnection connection,
    CancellationToken cancellationToken);

/// <summary>
/// Executes a discovered <c>.sqltest</c> case against the managed engine using the same
/// execution and comparison rules as the Rust <c>sqltest</c> runner: every statement in the
/// block is executed in order, all produced rows are concatenated, and the result is
/// compared exactly, as a set, as a regex, or as an expected error.
/// </summary>
internal static class SqltestManagedRunner
{
    private static readonly TimeSpan CaseTimeout = TimeSpan.FromSeconds(30);

    // Mirrors Turso's test-helper-only process-global atomic counter.
    private static long _testNondeterministicCounter;

    public static SqltestOutcome Run(
        SqltestFile file,
        SqltestCase test,
        SqltestIntegrityCheck? integrityCheck = null)
    {
        integrityCheck ??= RunManagedIntegrityCheck;
        var failures = new List<string>();
        for (var databaseIndex = 0; databaseIndex < file.Databases.Count; databaseIndex++)
        {
            var database = file.Databases[databaseIndex];
            var outcome = RunVariant(file, test, database, integrityCheck);
            if (!outcome.Matched)
            {
                failures.Add(
                    $"database variant {databaseIndex + 1}/{file.Databases.Count} " +
                    $"({database.DisplayName}): {outcome.Detail}");
            }
        }

        return failures.Count == 0
            ? new SqltestOutcome(true, string.Empty)
            : new SqltestOutcome(false, string.Join(Environment.NewLine, failures));
    }

    private static SqltestOutcome RunVariant(
        SqltestFile file,
        SqltestCase test,
        SqltestDatabase database,
        SqltestIntegrityCheck integrityCheck)
    {
        var writableDefault = database.Kind is SqltestDatabaseKind.Default
                or SqltestDatabaseKind.DefaultNoRowidAlias
            && RequiresWritableDefaultFixture(test.Sql);
        var temporaryPath = database.Kind == SqltestDatabaseKind.TempFile || writableDefault
            ? CreateTemporaryDatabasePath()
            : null;

        try
        {
            if (writableDefault)
            {
                File.Copy(
                    SqltestDefaultDatabaseGenerator.GetDefaultPath(
                        database.Kind == SqltestDatabaseKind.DefaultNoRowidAlias),
                    temporaryPath!);
            }

            using var embedded = OpenDatabase(database, temporaryPath, writableDefault);
            embedded.RegisterScalarFunction(
                "test_nondet_counter",
                0,
                static _ => SqlValue.Integer(Interlocked.Increment(ref _testNondeterministicCounter) - 1));
            using var connection = embedded.Connect();
            using var timeout = new CancellationTokenSource(CaseTimeout);

            foreach (var setupName in test.Setups)
            {
                if (!file.Setups.TryGetValue(setupName, out var setupSql))
                    return new SqltestOutcome(false, $"undefined setup '{setupName}'");

                if (TryExecute(connection, setupSql, timeout.Token, out _) is { } setupError)
                    return new SqltestOutcome(false, $"setup '{setupName}' failed: {setupError}");
            }

            var error = TryExecute(connection, test.Sql, timeout.Token, out var rows);
            var outcome = Compare(test.Expectation, rows, error);
            // Match Turso's runner: integrity cross-checks apply only to passing,
            // writable cases that did not intentionally expect an error.
            if (!outcome.Matched
                || !test.CrossCheckIntegrity
                || database.ReadOnly
                || test.Expectation.Kind == SqltestExpectationKind.Error)
                return outcome;

            var integrity = integrityCheck(connection, timeout.Token);
            if (integrity.Error is null
                && integrity.Rows.Count == 1
                && string.Equals(integrity.Rows[0], "ok", StringComparison.Ordinal))
            {
                return outcome;
            }

            var actual = integrity.Error is { } integrityError
                ? $"error: {integrityError}"
                : $"{integrity.Rows.Count} row(s): [{string.Join(", ", integrity.Rows.Select(static row => $"'{row}'"))}]";
            return new SqltestOutcome(
                false,
                $"integrity_check failed for {file.RelativePath}::{test.Name}: " +
                $"expected exactly one row 'ok', got {actual}");
        }
        finally
        {
            DeleteTemporaryDatabase(temporaryPath);
        }
    }

    private static EmbeddedDatabase OpenDatabase(
        SqltestDatabase database,
        string? temporaryPath,
        bool writableDefault)
        => database.Kind switch
        {
            SqltestDatabaseKind.Memory => new EmbeddedDatabase(),
            SqltestDatabaseKind.TempFile => EmbeddedDatabase.OpenFile(temporaryPath!),
            SqltestDatabaseKind.Default => EmbeddedDatabase.OpenFile(
                writableDefault
                    ? temporaryPath!
                    : SqltestDefaultDatabaseGenerator.GetDefaultPath(noRowidAlias: false),
                readOnly: !writableDefault),
            SqltestDatabaseKind.DefaultNoRowidAlias => EmbeddedDatabase.OpenFile(
                writableDefault
                    ? temporaryPath!
                    : SqltestDefaultDatabaseGenerator.GetDefaultPath(noRowidAlias: true),
                readOnly: !writableDefault),
            _ => throw new NotSupportedException(
                $"The managed sqltest harness cannot construct database fixture '{database.DisplayName}'."),
        };

    private static bool RequiresWritableDefaultFixture(string sql)
        => Regex.IsMatch(
            sql,
            @"(?im)(?:^|;)\s*(?:CREATE|DROP|ALTER|INSERT|UPDATE|DELETE|REPLACE|VACUUM|REINDEX)\b",
            RegexOptions.CultureInvariant);

    private static string CreateTemporaryDatabasePath()
    {
        var directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, ".sqltest-temporary");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"Ahtola-sqltest-{Guid.NewGuid():N}.db");
    }

    private static SqltestIntegrityResult RunManagedIntegrityCheck(
        EmbeddedConnection connection,
        CancellationToken cancellationToken)
    {
        var error = TryExecute(connection, "PRAGMA integrity_check", cancellationToken, out var rows);
        return new SqltestIntegrityResult(rows, error);
    }

    private static void DeleteTemporaryDatabase(string? path)
    {
        if (path is null)
            return;

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                File.Delete(path + suffix);
            }
            catch (IOException)
            {
                // A leaked handle must not turn corpus execution into a harness failure.
            }
        }
    }

    private static string? TryExecute(
        EmbeddedConnection connection,
        string sql,
        CancellationToken cancellationToken,
        out List<string> rows)
    {
        rows = [];
        try
        {
            foreach (var statement in connection.PrepareScript(sql))
            {
                using (statement)
                {
                    while (statement.Step(cancellationToken) == StatementStepResult.Row)
                    {
                        var values = new string[statement.ColumnCount];
                        for (var column = 0; column < values.Length; column++)
                            values[column] = FormatValue(statement.GetValue(column));
                        rows.Add(string.Join('|', values));
                    }
                }
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            return $"managed execution exceeded {CaseTimeout.TotalSeconds:0} seconds";
        }
        catch (Exception exception)
        {
            return FormatError(exception);
        }
    }

    private static string FormatError(Exception exception)
    {
        var message = exception.Message;
        return exception is EmbeddedSqlException
               && (message.StartsWith("UNIQUE constraint failed:", StringComparison.Ordinal)
                   || message.StartsWith("CHECK constraint failed:", StringComparison.Ordinal)
                   || message.StartsWith("NOT NULL constraint failed:", StringComparison.Ordinal))
            ? $"{message} (19)"
            : message;
    }

    private static SqltestOutcome Compare(SqltestExpectation expectation, List<string> rows, string? error)
    {
        if (expectation.Kind == SqltestExpectationKind.Error)
        {
            if (error is null)
                return new SqltestOutcome(false, "expected an error but the statement succeeded");
            if (expectation.Pattern is null)
                return new SqltestOutcome(true, string.Empty);

            var normalizedError = NormalizeWhitespace(error);
            var normalizedPattern = NormalizeWhitespace(expectation.Pattern);
            return Regex.IsMatch(normalizedError, normalizedPattern, RegexOptions.IgnoreCase)
                ? new SqltestOutcome(true, string.Empty)
                : new SqltestOutcome(
                    false,
                    $"error '{normalizedError}' does not match pattern '{normalizedPattern}'");
        }

        if (error is not null)
            return new SqltestOutcome(false, $"expected success but got error: {error}");

        var actual = string.Join('\n', rows);
        switch (expectation.Kind)
        {
            case SqltestExpectationKind.Pattern:
                return Regex.IsMatch(actual, expectation.Pattern!)
                    ? new SqltestOutcome(true, string.Empty)
                    : new SqltestOutcome(false, $"output does not match pattern '{expectation.Pattern}'\n{actual}");

            case SqltestExpectationKind.Unordered:
                var actualSet = new HashSet<string>(rows, StringComparer.Ordinal);
                var expectedSet = new HashSet<string>(expectation.Rows, StringComparer.Ordinal);
                return actualSet.SetEquals(expectedSet)
                    ? new SqltestOutcome(true, string.Empty)
                    : new SqltestOutcome(false, Describe(expectation.Rows, rows));

            default:
                var expected = string.Join('\n', expectation.Rows);
                return string.Equals(actual, expected, StringComparison.Ordinal)
                    ? new SqltestOutcome(true, string.Empty)
                    : new SqltestOutcome(false, Describe(expectation.Rows, rows));
        }
    }

    private static string Describe(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        var headline = $"expected {expected.Count} row(s), got {actual.Count} row(s)";
        for (var index = 0; index < Math.Min(expected.Count, actual.Count); index++)
        {
            if (string.Equals(expected[index], actual[index], StringComparison.Ordinal))
                continue;

            headline += $"; row {index} expected '{expected[index]}' but was '{actual[index]}'";
            break;
        }

        return $"{headline}\n--- expected\n{string.Join('\n', expected)}\n+++ actual\n{string.Join('\n', actual)}";
    }

    private static string NormalizeWhitespace(string value)
        => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(static part => part != "\u2502"));

    /// <summary>Mirrors <c>testing/sqltest/src/backends/rust.rs::value_to_string</c>.</summary>
    private static string FormatValue(SqlValue value) => value.Kind switch
    {
        SqlValueKind.Null => string.Empty,
        SqlValueKind.Integer => value.AsInteger().ToString(CultureInfo.InvariantCulture),
        SqlValueKind.Real => FormatReal(value.AsReal()),
        SqlValueKind.Text => value.AsText(),
        SqlValueKind.Blob => Encoding.UTF8.GetString(value.AsBlob().Span),
        _ => throw new InvalidOperationException($"Unknown SQL value kind {value.Kind}."),
    };

    /// <summary>Mirrors <c>testing/sqltest/src/backends/rust.rs::format_real</c>.</summary>
    private static string FormatReal(double value)
    {
        if (double.IsNaN(value))
            return "NaN";
        if (double.IsInfinity(value))
            return double.IsPositive(value) ? "Inf" : "-Inf";

        var magnitude = Math.Abs(value);
        if (magnitude != 0.0 && (magnitude < 1e-4 || magnitude >= 1e15))
            return FormatExponential(value);

        if (value % 1 == 0)
            return $"{(long)value}.0";

        return FormatSignificantDigits(value, 15);
    }

    private static string FormatExponential(double value)
    {
        var formatted = value.ToString("E14", CultureInfo.InvariantCulture);
        var exponentIndex = formatted.IndexOf('E');
        var mantissa = formatted[..exponentIndex].TrimEnd('0');
        if (mantissa.EndsWith('.'))
            mantissa += "0";

        var exponent = int.Parse(formatted[(exponentIndex + 1)..], CultureInfo.InvariantCulture);
        return $"{mantissa}e{(exponent >= 0 ? "+" : string.Empty)}{exponent.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string FormatSignificantDigits(double value, int significantDigits)
    {
        if (value == 0.0)
            return "0.0";

        var magnitude = Math.Abs(value);
        var digitsBeforeDecimal = magnitude >= 1.0 ? (int)Math.Floor(Math.Log10(magnitude)) + 1 : 0;
        var decimalPlaces = Math.Max(0, significantDigits - digitsBeforeDecimal);
        var formatted = value.ToString("F" + decimalPlaces.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        if (!formatted.Contains('.'))
            return formatted;

        var trimmed = formatted.TrimEnd('0');
        return trimmed.EndsWith('.') ? trimmed + "0" : trimmed;
    }
}
