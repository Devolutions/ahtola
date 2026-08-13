using System;
using System.Collections.Generic;
using System.Globalization;
using AwesomeAssertions;
using NUnit.Framework;
using Ahtola.Core;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// Differential coverage for the printf conversions whose formatting rules are specific to
/// SQLite rather than inherited from C. Every expectation here was confirmed against real
/// SQLite before being asserted.
/// </summary>
[TestFixture]
public sealed class PrintfFormattingParityTests
{
    /// <summary>
    /// SQLite's <c>!</c> flag (alternate form 2) removes trailing fractional zeroes but always
    /// leaves at least one fractional digit, so it is not the same as a shortest round-trip
    /// representation: <c>%!f</c> of 1/3 is still the default precision "0.333333".
    /// </summary>
    [Test]
    public void AlternateForm2StripsTrailingFractionZeros()
    {
        AssertMatchesSqlite(
            "printf('%!f', 3.14)",
            "printf('%!f', 1.0)",
            "printf('%!f', 0.0)",
            "printf('%!f', -3.14)",
            "printf('%!+f', 3.14)",
            "printf('%!.4f', 3.14)",
            "printf('%!f', 1.0/3.0)",
            "printf('%!e', 23000000.0)",
            "printf('%!e', 1.0)",
            "printf('%!e', 0.0)",
            "printf('%!.2e', 23000000.0)",
            "printf('%!g', 23000000.0)",
            "printf('%!E', 23000000.0)");
    }

    /// <summary>
    /// The <c>!</c> flag still participates in width and justification.
    /// </summary>
    [Test]
    public void AlternateForm2HonoursWidthAndJustification()
    {
        AssertMatchesSqlite(
            "'/' || printf('%!10f', 3.14) || '/'",
            "'/' || printf('%!-10f', 3.14) || '/'");
    }

    /// <summary>
    /// A "-" and "0" flag pair is resolved differently per conversion class: an integer stays
    /// zero padded while a real is left justified, and "0" on a string is ignored.
    /// </summary>
    [Test]
    public void LeftJustifyAndZeroPadFollowTheConversionClass()
    {
        AssertMatchesSqlite(
            "printf('%-05d', 42)",
            "printf('%-5d', 42)",
            "printf('%05d', 42)",
            "printf('%-08x', 255)",
            "printf('%-08o', 8)",
            "printf('%-08u', 42)",
            "'|' || printf('%-010.2f', 3.14) || '|'",
            "'|' || printf('%-10.2f', 3.14) || '|'",
            "'|' || printf('%010.2f', 3.14) || '|'",
            "'|' || printf('%-012.2e', 3.14) || '|'",
            "'|' || printf('%-012.2g', 3.14) || '|'",
            "'|' || printf('%-05s', 'ab') || '|'",
            "'|' || printf('%05s', 'ab') || '|'");
    }

    /// <summary>
    /// An alternate-form integer prefix sits outside the zero padded field, so the rendered
    /// width can exceed the requested width: <c>%#04x</c> of 255 is "0x00ff".
    /// </summary>
    [Test]
    public void AlternateFormIntegerPrefixSitsOutsideZeroPadding()
    {
        AssertMatchesSqlite(
            "printf('%#04x', 255)",
            "printf('%#06x', 255)",
            "printf('%#08x', 255)",
            "printf('%#04X', 255)",
            "printf('%#x', 255)",
            "printf('%#x', 0)",
            "printf('%#o', 0)");
    }

    /// <summary>
    /// An infinity normally renders as "Inf", but zero padding a three character word would be
    /// meaningless, so SQLite substitutes the largest value its formatter can describe instead.
    /// </summary>
    [Test]
    public void ZeroPaddedInfinitySubstitutesNineTimesTenToThe999()
    {
        AssertMatchesSqlite(
            "printf('%f', 1e308*10)",
            "printf('%e', 1e308*10)",
            "printf('%20e', 1e308*10)",
            "printf('%020e', 1e308*10)",
            "printf('%020E', 1e308*10)",
            "printf('%020g', 1e308*10)",
            "printf('%020e', -1e308*10)",
            "length(printf('%020f', 1e308*10))",
            "substr(printf('%020f', 1e308*10), 1, 30)");
    }

    /// <summary>
    /// SQLite decodes a real to at most sixteen significant decimal digits and zero fills the
    /// remainder rather than emitting the exact binary expansion of the double.
    /// </summary>
    [Test]
    public void RealsRenderAtMostSixteenSignificantDigits()
    {
        AssertMatchesSqlite(
            "printf('%.20f', 1.0/3.0)",
            "printf('%.20e', 1.0/3.0)",
            "printf('%.20g', 1.0/3.0)",
            "printf('%.20f', 2.0/3.0)",
            "printf('%.18e', 1.0/7.0)",
            "printf('%.15f', 1234567890.123456789)",
            "printf('%.25f', 0.1)",
            "printf('%.16f', 0.1)",
            "printf('%.17f', 0.1)",
            "printf('%.17g', 0.1)");
    }

    /// <summary>
    /// Guards the shared width, sign and rounding paths that the rules above run through, so a
    /// later change to one conversion cannot silently move the others.
    /// </summary>
    [Test]
    public void FlagWidthAndPrecisionCombinationsMatchSqlite()
    {
        AssertMatchesSqlite(
            "printf('%d|%5d|%-5d|%05d|%+d|% d', 42, 42, 42, 42, 42, 42)",
            "printf('%d|%5d|%-5d|%05d', -42, -42, -42, -42)",
            "printf('%-+5d|%0+6d|%020d', 42, 42, -42)",
            "printf('%x|%X|%o|%u', 255, 255, 8, 42)",
            "printf('%08.3f|%-8.3f|%+08.3f', 3.14159, 3.14159, 3.14159)",
            "printf('%e|%E|%g|%G', 1234.5678, 1234.5678, 1234.5678, 1234.5678)",
            "printf('%g|%g|%g|%g', 0.0001, 0.00001, 100000.0, 1000000.0)",
            "printf('%.0f|%.0e|%.0g', 2.5, 2.5, 2.5)",
            "printf('%.0f|%.0f|%.0f', 0.5, 1.5, 2.5)",
            "printf('%#.0f|%#g|%#.3g', 2.0, 1.0, 1.0)",
            "printf('%f|%f', 0.0, -1.5)",
            "printf('%5.1f|%-5.1f|%05.1f', -1.25, -1.25, -1.25)",
            "printf('%s|%5s|%-5s|%.2s', 'ab', 'ab', 'ab', 'abcd')",
            "printf('%,d|%,d', 1234567, -1234567)",
            "printf('%.3e', 0.0)",
            "printf('%.10f', 1e-10)",
            "printf('%g', 1e300)",
            "printf('%d', 9223372036854775807)",
            "printf('%c|%3c|%-3c', 'x', 'x', 'x')",
            "printf('%.2f', 2.675)",
            "printf('%.1f|%.1f|%.1f', 0.05, 0.15, 0.25)",
            "printf('%+.2f|%+.2f', 0.0, -0.0)");
    }

    private static void AssertMatchesSqlite(params string[] expressions)
    {
        using var managed = new EmbeddedDatabase().Connect();
        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();

        var failures = new List<string>();
        foreach (var expression in expressions)
        {
            var expected = RunSqlite(sqlite, expression);
            var actual = RunManaged(managed, expression);
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                failures.Add($"{expression}: sqlite={expected} managed={actual}");
        }

        failures.Should().BeEmpty();
    }

    private static string RunManaged(EmbeddedConnection connection, string expression)
    {
        using var statement = connection.Prepare("SELECT " + expression + ";");
        return statement.Step() == StatementStepResult.Row
            ? Describe(statement.GetValue(0))
            : "<no row>";
    }

    private static string Describe(SqlValue value)
    {
        return value.Kind switch
        {
            SqlValueKind.Null => "<null>",
            SqlValueKind.Integer => value.AsInteger().ToString(CultureInfo.InvariantCulture),
            SqlValueKind.Real => value.AsReal().ToString("R", CultureInfo.InvariantCulture),
            _ => value.AsText(),
        };
    }

    private static string RunSqlite(MsData.SqliteConnection connection, string expression)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + expression + ";";
        var result = command.ExecuteScalar();
        return result switch
        {
            null or DBNull => "<null>",
            long integer => integer.ToString(CultureInfo.InvariantCulture),
            double real => real.ToString("R", CultureInfo.InvariantCulture),
            _ => Convert.ToString(result, CultureInfo.InvariantCulture) ?? "<null>",
        };
    }
}
