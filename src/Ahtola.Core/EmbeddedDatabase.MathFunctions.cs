using System.Globalization;

namespace Ahtola.Core;

public sealed partial class EmbeddedDatabase
{
    // Reported by sqlite_version()/sqlite_source_id() for applications that gate on
    // a SQLite version. Kept in sync with the Rust core (core/vdbe/execute.rs).
    public const string SqliteCompatibilityVersion = "3.50.4";
    public const string TursoCompatibilityVersion = "0.7.2";
    internal const string SqliteCompatibilitySourceId =
        "0000-00-00 00:00:00 0000000000000000000000000000000000000000000000000000000000000000";

    // Backing state for changes()/total_changes(). Updated only by INSERT, UPDATE,
    // and DELETE so that intervening statements cannot clear the reported counts.
    private long _changes;
    private long _totalChanges;

    /// <summary>
    /// Coerces an argument for a math builtin. These use <c>sqlite3_value_numeric_type</c>, which
    /// converts only a value that is entirely a well-formed number, and SQLite returns NULL rather
    /// than raising when an argument has no such representation. This is deliberately stricter than
    /// the numerification used by CAST, arithmetic, <c>abs()</c> and <c>round()</c>, so
    /// <c>sqrt('4x')</c> is NULL while <c>abs('4x')</c> is 4.0.
    /// </summary>
    private static bool TryGetMathOperand(SqlValue value, out double result)
    {
        var numeric = ApplyComparisonNumericAffinity(value);
        switch (numeric.Kind)
        {
            case SqlValueKind.Integer:
                result = numeric.AsInteger();
                return true;
            case SqlValueKind.Real:
                result = numeric.AsReal();
                return true;
            default:
                result = 0;
                return false;
        }
    }

    /// <summary>
    /// Math builtins yield NULL for domain errors (for example sqrt(-1)) instead
    /// of propagating NaN or infinity.
    /// </summary>
    private static SqlValue FromMathResult(double value)
        => double.IsNaN(value) || double.IsInfinity(value)
            ? SqlValue.Null
            : SqlValue.Real(value);

    private static SqlValue EvaluateUnaryMath(
        string functionName,
        IReadOnlyList<SqlValue> arguments,
        Func<double, double> operation)
    {
        RequireArgumentCount(functionName, arguments, 1);
        if (!TryGetMathOperand(arguments[0], out var operand))
            return SqlValue.Null;

        return FromMathResult(operation(operand));
    }

    private static SqlValue EvaluateBinaryMath(
        string functionName,
        IReadOnlyList<SqlValue> arguments,
        Func<double, double, double> operation)
    {
        RequireArgumentCount(functionName, arguments, 2);
        if (!TryGetMathOperand(arguments[0], out var left) || !TryGetMathOperand(arguments[1], out var right))
            return SqlValue.Null;

        return FromMathResult(operation(left, right));
    }

    private static SqlValue EvaluateGreatestCommonDivisor(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("gcd", arguments, 2);
        if (!TryGetTursoIntegerMathOperand(arguments[0], out var left)
            || !TryGetTursoIntegerMathOperand(arguments[1], out var right))
        {
            return SqlValue.Null;
        }

        if (!TryGetGreatestCommonDivisor(left, right, out var result))
            throw new EmbeddedSqlException("integer overflow");
        return SqlValue.Integer(result);
    }

    private static SqlValue EvaluateLeastCommonMultiple(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("lcm", arguments, 2);
        if (!TryGetTursoIntegerMathOperand(arguments[0], out var left)
            || !TryGetTursoIntegerMathOperand(arguments[1], out var right))
        {
            return SqlValue.Null;
        }

        if (left == 0 || right == 0)
            return SqlValue.Integer(0);
        if (!TryGetGreatestCommonDivisor(left, right, out var greatestCommonDivisor)
            || !TryGetAbsoluteValue(right, out var rightMagnitude)
            || !TryMultiply(left / greatestCommonDivisor, rightMagnitude, out var product)
            || !TryGetAbsoluteValue(product, out var result))
        {
            throw new EmbeddedSqlException("integer overflow");
        }

        return SqlValue.Integer(result);
    }

    private static bool TryGetTursoIntegerMathOperand(SqlValue value, out long result)
    {
        switch (value.Kind)
        {
            case SqlValueKind.Integer:
                result = value.AsInteger();
                return true;
            case SqlValueKind.Real when double.IsFinite(value.AsReal()):
                result = ToSqliteInteger(value.AsReal());
                return true;
            case SqlValueKind.Text:
                return long.TryParse(
                    value.AsText(),
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out result);
            default:
                result = 0;
                return false;
        }
    }

    private static bool TryGetGreatestCommonDivisor(long left, long right, out long result)
    {
        // Turso's gcd_inner rejects the only unrepresentable positive result:
        // abs(Int64.MinValue). Reduce other MIN operands before the Euclidean loop.
        if (left == long.MinValue || right == long.MinValue)
        {
            if (left == 0 || right == 0 || left == right)
            {
                result = 0;
                return false;
            }

            if (left == long.MinValue)
            {
                if (right == -1)
                {
                    result = 1;
                    return true;
                }

                left %= right;
            }
            else
            {
                if (left == -1)
                {
                    result = 1;
                    return true;
                }

                right %= left;
            }
        }

        while (right != 0)
        {
            var remainder = left % right;
            left = right;
            right = remainder;
        }

        result = Math.Abs(left);
        return true;
    }

    private static bool TryGetAbsoluteValue(long value, out long result)
    {
        if (value == long.MinValue)
        {
            result = 0;
            return false;
        }

        result = Math.Abs(value);
        return true;
    }

    private static bool TryMultiply(long left, long right, out long result)
    {
        if (left == 0 || right == 0)
        {
            result = 0;
            return true;
        }

        var overflows = left > 0
            ? right > 0 ? left > long.MaxValue / right : right < long.MinValue / left
            : right > 0 ? left < long.MinValue / right : left < long.MaxValue / right;
        if (overflows)
        {
            result = 0;
            return false;
        }

        result = left * right;
        return true;
    }

    private static SqlValue EvaluateRound(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("round", arguments, 1, 2);
        if (arguments[0].Kind == SqlValueKind.Null)
            return SqlValue.Null;

        // round() reads its operand with sqlite3_value_double, so unlike the math builtins a
        // numeric prefix is enough and non-numeric text is 0.0 rather than NULL.
        var operand = AsReal(ApplyNumericAffinity(arguments[0]));
        var digits = 0L;
        if (arguments.Count == 2)
        {
            if (arguments[1].Kind == SqlValueKind.Null)
                return SqlValue.Null;

            digits = ToSqliteInteger(AsReal(ApplyNumericAffinity(arguments[1])));
        }

        if (digits < 0)
            digits = 0;
        if (digits > 30)
            digits = 30;

        // SQLite rounds halfway cases away from zero, unlike .NET's banker's rounding default.
        return SqlValue.Real(Math.Round(operand, (int)digits, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// trunc(), ceil(), and floor() preserve an integer argument as an integer
    /// only when the value already fits; otherwise SQLite yields a real.
    /// </summary>
    private static SqlValue EvaluateIntegralMath(
        string functionName,
        IReadOnlyList<SqlValue> arguments,
        Func<double, double> operation)
    {
        RequireArgumentCount(functionName, arguments, 1);
        var numeric = ApplyComparisonNumericAffinity(arguments[0]);
        if (numeric.Kind == SqlValueKind.Integer)
            return numeric;
        if (numeric.Kind != SqlValueKind.Real)
            return SqlValue.Null;

        return FromMathResult(operation(numeric.AsReal()));
    }

    private static SqlValue EvaluateLogarithm(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("log", arguments, 1, 2);

        // log(X) is base 10; log(B, X) is an explicit base.
        if (arguments.Count == 1)
        {
            if (!TryGetMathOperand(arguments[0], out var single))
                return SqlValue.Null;

            return single <= 0 ? SqlValue.Null : FromMathResult(Math.Log10(single));
        }

        if (!TryGetMathOperand(arguments[0], out var logBase) || !TryGetMathOperand(arguments[1], out var operand))
            return SqlValue.Null;

        if (logBase <= 0 || Math.Abs(logBase - 1.0) < double.Epsilon || operand <= 0)
            return SqlValue.Null;

        return FromMathResult(Math.Log(operand) / Math.Log(logBase));
    }

    /// <summary>
    /// mod() maps to C <c>fmod</c> in SQLite, so it always yields a real - even for integer
    /// operands - and a zero divisor yields NULL.
    /// </summary>
    private static SqlValue EvaluateModulo(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("mod", arguments, 2);
        if (!TryGetMathOperand(arguments[0], out var dividend)
            || !TryGetMathOperand(arguments[1], out var divisor))
        {
            return SqlValue.Null;
        }

        if (divisor == 0)
            return SqlValue.Null;

        return FromMathResult(dividend % divisor);
    }

    private static SqlValue EvaluateSign(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("sign", arguments, 1);
        if (!TryGetMathOperand(arguments[0], out var operand))
            return SqlValue.Null;

        if (double.IsNaN(operand))
            return SqlValue.Null;

        return SqlValue.Integer(Math.Sign(operand));
    }

    private static SqlValue EvaluatePi(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("pi", arguments, 0);
        return SqlValue.Real(Math.PI);
    }

    private static SqlValue EvaluateIif(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("iif", arguments, 3);
        return IsTrue(arguments[0]) ? arguments[1] : arguments[2];
    }

    /// <summary>
    /// likely(), unlikely(), and likelihood() are planner hints; without a cost
    /// model they behave as the identity on their first argument.
    /// </summary>
    private static SqlValue EvaluateProbabilityHint(
        string functionName,
        IReadOnlyList<SqlValue> arguments,
        int expectedArguments)
    {
        RequireArgumentCount(functionName, arguments, expectedArguments);
        return arguments[0];
    }

    private static SqlValue EvaluateSqliteVersion(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("sqlite_version", arguments, 0);
        return SqlValue.Text(SqliteCompatibilityVersion);
    }

    private static SqlValue EvaluateTursoVersion(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("turso_version", arguments, 0);
        return SqlValue.Text(TursoCompatibilityVersion);
    }

    private static SqlValue EvaluateSqliteSourceId(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("sqlite_source_id", arguments, 0);
        return SqlValue.Text(SqliteCompatibilitySourceId);
    }

    private SqlValue EvaluateChanges(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("changes", arguments, 0);
        return SqlValue.Integer(_changes);
    }

    private SqlValue EvaluateTotalChanges(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("total_changes", arguments, 0);
        return SqlValue.Integer(_totalChanges);
    }

    /// <summary>
    /// timediff(A, B) renders A minus B as a signed ISO-8601-like interval using
    /// SQLite's fixed +YYYY-MM-DD HH:MM:SS.SSS layout.
    /// </summary>
    private static SqlValue EvaluateTimeDiff(IReadOnlyList<SqlValue> arguments)
    {
        RequireArgumentCount("timediff", arguments, 2);
        if (HasNullArgument(arguments))
            return SqlValue.Null;

        if (!SqliteDateTime.TryResolveUtc(arguments[0], out var left)
            || !SqliteDateTime.TryResolveUtc(arguments[1], out var right))
        {
            return SqlValue.Null;
        }

        var negative = left < right;
        var start = negative ? left : right;
        var end = negative ? right : left;

        var years = end.Year - start.Year;
        var months = end.Month - start.Month;
        var days = end.Day - start.Day;
        var time = end.TimeOfDay - start.TimeOfDay;

        if (time < TimeSpan.Zero)
        {
            time += TimeSpan.FromDays(1);
            days--;
        }

        if (days < 0)
        {
            var previousMonth = end.AddMonths(-1);
            days += DateTime.DaysInMonth(previousMonth.Year, previousMonth.Month);
            months--;
        }

        if (months < 0)
        {
            months += 12;
            years--;
        }

        var sign = negative ? '-' : '+';
        return SqlValue.Text(string.Create(
            CultureInfo.InvariantCulture,
            $"{sign}{years:D4}-{months:D2}-{days:D2} {time.Hours:D2}:{time.Minutes:D2}:{time.Seconds:D2}.{time.Milliseconds:D3}"));
    }
}
