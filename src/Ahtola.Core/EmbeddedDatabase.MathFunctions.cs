using System.Globalization;
using System.Numerics;

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
        if (arguments.Count < 2)
            throw new EmbeddedSqlException("wrong number of arguments to function iif()");

        for (var index = 0; index + 1 < arguments.Count; index += 2)
        {
            if (IsTrue(arguments[index]))
                return arguments[index + 1];
        }

        return (arguments.Count & 1) != 0 ? arguments[^1] : SqlValue.Null;
    }

    private static SqlValue EvaluateTimeDate(IReadOnlyList<SqlValue> arguments)
    {
        if (arguments.Count is not (3 or 6 or 7 or 8))
            throw new EmbeddedSqlException("wrong number of arguments to function time_date()");
        if (arguments.Any(static argument => argument.Kind != SqlValueKind.Integer))
            throw new EmbeddedSqlException("all parameters should be integers");

        var year = unchecked((int)arguments[0].AsInteger());
        var month = unchecked((int)arguments[1].AsInteger());
        var yearMonths = year == 0 ? BigInteger.Zero : ((BigInteger)year - 1) * 12;
        var yearAfterYearShift = FloorDivide(yearMonths, 12) + 1;
        if (yearAfterYearShift < -262144 || yearAfterYearShift > 262142)
            return SqlValue.Null;

        var totalMonths = yearMonths + (month == 0 ? BigInteger.Zero : month - 1);
        var normalizedYear = FloorDivide(totalMonths, 12) + 1;
        var normalizedMonth = totalMonths - ((normalizedYear - 1) * 12) + 1;
        if (normalizedYear < -262144 || normalizedYear > 262142)
            return SqlValue.Null;

        var day = arguments[2].AsInteger();
        var hour = arguments.Count >= 6 ? arguments[3].AsInteger() : 0;
        var minute = arguments.Count >= 6 ? arguments[4].AsInteger() : 0;
        var second = arguments.Count >= 6 ? arguments[5].AsInteger() : 0;
        var nanosecond = arguments.Count >= 7 ? arguments[6].AsInteger() : 0;
        var offset = arguments.Count == 8 ? unchecked((int)arguments[7].AsInteger()) : 0;

        const long nanosecondsPerSecond = 1_000_000_000;
        var unixDays = DaysFromCivil((int)normalizedYear, (int)normalizedMonth, 1);
        var baseNanoseconds = unixDays * 86_400 * nanosecondsPerSecond;
        var elapsedSeconds = ((BigInteger)day - 1) * 86_400
            + (BigInteger)hour * 3_600
            + (BigInteger)minute * 60
            + second
            - offset;
        var totalNanoseconds = baseNanoseconds
            + elapsedSeconds * nanosecondsPerSecond
            + nanosecond;
        var minimumNanoseconds =
            DaysFromCivil(-262144, 1, 1) * 86_400 * nanosecondsPerSecond;
        var maximumNanoseconds =
            (DaysFromCivil(262142, 12, 31) + 1) * 86_400 * nanosecondsPerSecond - 1;
        if (totalNanoseconds < minimumNanoseconds || totalNanoseconds > maximumNanoseconds)
            return SqlValue.Null;

        var wholeSeconds = BigInteger.DivRem(
            totalNanoseconds,
            nanosecondsPerSecond,
            out var remainingNanoseconds);
        if (remainingNanoseconds < 0)
        {
            wholeSeconds--;
            remainingNanoseconds += nanosecondsPerSecond;
        }

        const long daysBeforeUnixEpoch = 719_162;
        var seconds = checked((long)(wholeSeconds + (BigInteger)daysBeforeUnixEpoch * 86_400));
        var nanos = (uint)remainingNanoseconds;
        var blob = new byte[13];
        blob[0] = 1;
        for (var index = 0; index < 8; index++)
            blob[index + 1] = (byte)(seconds >> ((7 - index) * 8));
        blob[9] = (byte)(nanos >> 24);
        blob[10] = (byte)(nanos >> 16);
        blob[11] = (byte)(nanos >> 8);
        blob[12] = (byte)nanos;
        return SqlValue.Blob(blob);
    }

    private static BigInteger FloorDivide(BigInteger value, int divisor)
    {
        var quotient = BigInteger.DivRem(value, divisor, out var remainder);
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private static BigInteger DaysFromCivil(int year, int month, int day)
    {
        var adjustedYear = (BigInteger)year - (month <= 2 ? 1 : 0);
        var era = FloorDivide(adjustedYear, 400);
        var yearOfEra = adjustedYear - era * 400;
        var adjustedMonth = month + (month > 2 ? -3 : 9);
        var dayOfYear = (153 * adjustedMonth + 2) / 5 + day - 1;
        var dayOfEra = yearOfEra * 365 + yearOfEra / 4 - yearOfEra / 100 + dayOfYear;
        return era * 146_097 + dayOfEra - 719_468;
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
    /// SQLite's fixed +YYYY-MM-DD HH:MM:SS.SSS layout. The result is a modifier that
    /// turns B into A (<c>datetime(B, timediff(A, B)) = A</c>), so the month/day split
    /// must use SQLite's month-arithmetic direction: forward month adds clamp a
    /// day-of-month overflow forward (Jan 31 + 1 month = Mar 2), while the negative
    /// direction is anchored on the later date and walked backwards, which can give a
    /// different month/day split than the positive direction (upstream issue #8242,
    /// timediff-negative-month-roundtrip.sqltest).
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

        if (left == right)
            return SqlValue.Text("+0000-00-00 00:00:00.000");

        var negative = left < right;
        // The result is a modifier applied to B: positive walks B forward to A, and a
        // negative result is applied to the later date and walked backwards, so the
        // month/day split can differ from the positive direction (upstream #8242).
        var anchor = right;
        var target = left;

        int WalkCompare(int months)
            => AddMonthsSqlite(anchor, months).CompareTo(target);

        // The walk is monotonic in the month count. Positive: find the maximum m with
        // walk(m) <= target. Negative: find the minimum m with walk(m) >= target.
        int monthDelta;
        if (negative)
        {
            var low = (target.Year - anchor.Year) * 12 + target.Month - anchor.Month - 2;
            while (WalkCompare(low) >= 0)
                low--;
            var high = 0;
            while (high - low > 1)
            {
                var middle = low + (high - low + 1) / 2;
                if (WalkCompare(middle) >= 0)
                    high = middle;
                else
                    low = middle;
            }

            monthDelta = high;
        }
        else
        {
            var high = (target.Year - anchor.Year) * 12 + target.Month - anchor.Month + 2;
            while (WalkCompare(high) <= 0)
                high++;
            var low = 0;
            while (high - low > 1)
            {
                var middle = low + (high - low) / 2;
                if (WalkCompare(middle) <= 0)
                    low = middle;
                else
                    high = middle;
            }

            monthDelta = low;
        }

        var walkedAnchor = AddMonthsSqlite(anchor, monthDelta);
        var remainder = target - walkedAnchor;

        // The remainder is a day count (negative for the backward direction) plus a
        // time-of-day delta. SQLite renders the days as the magnitude with the
        // interval sign and the clock part always as the delta toward the target.
        // SQLite renders negative sub-day intervals with the sign applied to the
        // whole interval: timediff('-02:30', '00:00') is "-0000-00-00 02:30:00"
        // (days stay 0, the time digits are the absolute remainder). Truncate
        // toward zero so days never borrow a negative time into a positive one.
        var days = (long)Math.Truncate(remainder.TotalDays);
        var time = remainder - TimeSpan.FromDays(days);

        var sign = negative ? '-' : '+';
        var monthMagnitude = Math.Abs(monthDelta) % 12;
        var yearMagnitude = Math.Abs(monthDelta) / 12;
        return SqlValue.Text(string.Create(
            CultureInfo.InvariantCulture,
            $"{sign}{yearMagnitude:D4}-{monthMagnitude:D2}-{Math.Abs(days):D2} {Math.Abs(time.Hours):D2}:{Math.Abs(time.Minutes):D2}:{Math.Abs(time.Seconds):D2}.{Math.Abs(time.Milliseconds):D3}"));
    }

    /// <summary>
    /// SQLite month arithmetic: adding N months, then normalizing an overflowed
    /// day-of-month forward (date.c's computeJD normalization), so Jan 31 + 1 month
    /// lands on Mar 2/3, and Feb 29 + 1 month lands on Mar 29.
    /// </summary>
    private static DateTime AddMonthsSqlite(DateTime value, int months)
    {
        var totalMonths = value.Month - 1 + months;
        var year = value.Year + totalMonths / 12;
        var month = totalMonths % 12;
        if (month < 0)
        {
            month += 12;
            year--;
        }

        month++;
        var dayOverflow = value.Day - DateTime.DaysInMonth(year, month);
        if (dayOverflow <= 0)
            return new DateTime(year, month, value.Day, value.Hour, value.Minute, value.Second, value.Millisecond, DateTimeKind.Utc);

        // An overflowed day normalizes forward into the following month.
        var normalized = new DateTime(year, month, DateTime.DaysInMonth(year, month), value.Hour, value.Minute, value.Second, value.Millisecond, DateTimeKind.Utc);
        return normalized.AddDays(dayOverflow);
    }
}
