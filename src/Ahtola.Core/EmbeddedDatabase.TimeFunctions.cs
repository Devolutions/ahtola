using System.Globalization;
using System.Text;

namespace Ahtola.Core;

/// <summary>
/// The sqlean-compatible time extension, ported from <c>turso-src/core/time</c>
/// (mod.rs + internal.rs) at the pinned v0.8.0-pre.7. A Time value is a 13-byte blob:
/// version byte 1, eight big-endian bytes of seconds since 0001-01-01 UTC, and four
/// big-endian bytes of nanoseconds. Durations are integer nanoseconds.
///
/// Calendar arithmetic uses the civil-calendar days algorithm (Howard Hinnant's
/// days_from_civil / civil_from_days) because the corpus pins BCE years
/// (time_date(-1000, ...)) that a .NET DateTime cannot represent.
/// </summary>
public sealed partial class EmbeddedDatabase
{
    private const long SqleanDaysBeforeEpoch = 719_162;
    private const long UnixSecondsPerDay = 86_400;

    /// <summary>A Time value: seconds since 0001-01-01 UTC plus a nanosecond remainder.</summary>
    private readonly struct SqleanTime(long secondsSince0001, int nanoseconds)
    {
        public long Seconds { get; } = secondsSince0001;

        public int Nanoseconds { get; } = nanoseconds;

        public long ToUnixSeconds() => Seconds - SqleanDaysBeforeEpoch * UnixSecondsPerDay;

        public static SqleanTime FromUnixSeconds(long seconds, int nanoseconds)
            => new(seconds + SqleanDaysBeforeEpoch * UnixSecondsPerDay, nanoseconds);

        public SqleanTime AddNanoseconds(long nanoseconds)
        {
            var totalNanos = Nanoseconds + nanoseconds % 1_000_000_000;
            var secondCarry = nanoseconds / 1_000_000_000;
            if (totalNanos >= 1_000_000_000)
            {
                totalNanos -= 1_000_000_000;
                secondCarry++;
            }
            else if (totalNanos < 0)
            {
                totalNanos += 1_000_000_000;
                secondCarry--;
            }

            return new SqleanTime(Seconds + secondCarry, (int)totalNanos);
        }

        /// <summary>The UTC calendar date part of this time (days since 0001-01-01).</summary>
        public long Days() => FloorDiv(Seconds, UnixSecondsPerDay);

        /// <summary>Seconds past midnight of the calendar day.</summary>
        public long SecondsOfDay() => EuclideanRemainder(Seconds, UnixSecondsPerDay);

        public byte[] ToBlob()
        {
            var blob = new byte[13];
            blob[0] = 1;
            var seconds = Seconds;
            for (var index = 8; index >= 1; index--)
            {
                blob[index] = (byte)(seconds & 0xff);
                seconds >>= 8;
            }

            var nanoseconds = (uint)Nanoseconds;
            for (var index = 12; index >= 9; index--)
            {
                blob[index] = (byte)(nanoseconds & 0xff);
                nanoseconds >>= 8;
            }

            return blob;
        }

        public static SqleanTime? FromBlob(ReadOnlySpan<byte> blob)
        {
            if (blob.Length != 13 || blob[0] != 1)
                return null;

            var seconds =
                (long)blob[1] << 56
                | (long)blob[2] << 48
                | (long)blob[3] << 40
                | (long)blob[4] << 32
                | (long)blob[5] << 24
                | (long)blob[6] << 16
                | (long)blob[7] << 8
                | blob[8];
            var nanoseconds =
                (int)blob[9] << 24
                | (int)blob[10] << 16
                | (int)blob[11] << 8
                | blob[12];
            return new SqleanTime(seconds, nanoseconds);
        }
    }

    /// <summary>Days since 0001-01-01 for a civil (proleptic Gregorian) date.</summary>
    private static long SqleanDaysFromCivil(int year, int month, int day)
    {
        var y = month <= 2 ? year - 1 : year;
        var era = (y >= 0 ? y : y - 399) / 400;
        var yearOfEra = y - era * 400;
        var dayOfYear = (153 * (month + (month > 2 ? -3 : 9)) + 2) / 5 + day - 1;
        var dayOfEra = yearOfEra * 365 + yearOfEra / 4 - yearOfEra / 100 + dayOfYear;
        var epochDays = era * 146_097 + dayOfEra - 719_468;
        return epochDays + SqleanDaysBeforeEpoch;
    }

    /// <summary>The civil date for days since 0001-01-01 (proleptic Gregorian).</summary>
    private static (int Year, int Month, int Day) SqleanCivilFromDays(long daysSince0001)
    {
        var epochDays = daysSince0001 - SqleanDaysBeforeEpoch;
        var z = epochDays + 719_468;
        var era = z >= 0 ? z / 146_097 : (z - 146_096) / 146_097;
        var dayOfEra = z - era * 146_097;
        var yearOfEra = (dayOfEra - dayOfEra / 1460 + dayOfEra / 36_524 - dayOfEra / 146_096) / 365;
        var year = yearOfEra + era * 400;
        var dayOfYear = dayOfEra - (365 * yearOfEra + yearOfEra / 4 - yearOfEra / 100);
        var mp = (5 * dayOfYear + 2) / 153;
        var day = dayOfYear - (153 * mp + 2) / 5 + 1;
        var month = mp + (mp < 10 ? 3 : -9);
        return ((int)(month <= 2 ? year + 1 : year), (int)month, (int)day);
    }

    private static long FloorDiv(long value, long divisor)
        => value >= 0 ? value / divisor : (value - divisor + 1) / divisor;

    private static long EuclideanRemainder(long value, long divisor)
    {
        var remainder = value % divisor;
        return remainder < 0 ? remainder + Math.Abs(divisor) : remainder;
    }

    private static (int Year, int Month, int Day, long DaySeconds) SqleanDateParts(SqleanTime time)
    {
        var (year, month, day) = SqleanCivilFromDays(time.Days());
        return (year, month, day, time.SecondsOfDay());
    }

    private static SqlValue TimeBlobValue(SqleanTime time) => SqlValue.BlobOwned(time.ToBlob());

    private static SqleanTime? TimeArgument(SqlValue value, string parameter)
    {
        if (value.Kind != SqlValueKind.Blob)
            throw new EmbeddedSqlException($"{parameter}: should be a time blob");

        return SqleanTime.FromBlob(value.AsBlob().Span);
    }

    private static void RequireIntegerArguments(IReadOnlyList<SqlValue> arguments, string message)
    {
        foreach (var argument in arguments)
        {
            if (argument.Kind != SqlValueKind.Integer)
                throw new EmbeddedSqlException(message);
        }
    }

    private static SqlValue EvaluateTimeNow(IReadOnlyList<SqlValue> arguments)
    {
        if (arguments.Count != 0)
            throw new EmbeddedSqlException("wrong number of arguments to function time_now()");

        var (seconds, nanoseconds) = SqleanUtcNow();
        return TimeBlobValue(new SqleanTime(seconds, nanoseconds));
    }

    // time_date lives in EmbeddedDatabase.MathFunctions.cs (EvaluateTimeDate): the
    // same 13-byte blob format was already ported there. make_date/make_timestamp are
    // its aliases (sqlean's names for the same shapes).

    private static SqlValue EvaluateMakeDate(IReadOnlyList<SqlValue> arguments)
    {
        if (arguments.Count != 3)
            throw new EmbeddedSqlException("wrong number of arguments to function make_date()");

        return EvaluateTimeDate(arguments);
    }

    private static SqlValue EvaluateMakeTimestamp(IReadOnlyList<SqlValue> arguments)
    {
        if (arguments.Count != 6)
            throw new EmbeddedSqlException("wrong number of arguments to function make_timestamp()");

        return EvaluateTimeDate(arguments);
    }

    /// <summary>
    /// Builds a Time from components with chrono-style normalization (whole-month moves from
    /// 0001-01-01, then duration deltas). Shared by the trunc field reconstruction; the
    /// <c>time_date</c> SQL function itself is <see cref="EvaluateTimeDate"/> in
    /// <c>EmbeddedDatabase.MathFunctions.cs</c>, which owns the same 13-byte blob format.
    /// </summary>
    private static SqleanTime? SqleanBuildTime(
        int year,
        int month,
        long day,
        long hour,
        long minute,
        long second,
        long nanosecond)
    {
        // Normalize the (year, month) pair into a canonical (y, m) with 1 <= m <= 12.
        var totalMonths = (year - 1L) * 12 + (month - 1);
        var canonicalYear = FloorDiv(totalMonths, 12) + 1;
        var canonicalMonth = (int)EuclideanRemainder(totalMonths, 12) + 1;
        if (canonicalYear is < -999_999 or > 999_999)
            return null;

        var baseDays = SqleanDaysFromCivil((int)canonicalYear, canonicalMonth, 1);
        if (baseDays is < long.MinValue / 4 or > long.MaxValue / 4)
            return null;

        long seconds;
        try
        {
            seconds = checked(baseDays * UnixSecondsPerDay
                + (day - 1) * UnixSecondsPerDay
                + hour * 3600
                + minute * 60
                + second);
        }
        catch (OverflowException)
        {
            return null;
        }

        return new SqleanTime(seconds, 0).AddNanoseconds(nanosecond);
    }

    private static SqlValue EvaluateTimeGet(IReadOnlyList<SqlValue> arguments)
    {
        if (arguments.Count != 2)
            throw new EmbeddedSqlException("wrong number of arguments to function time_get()");
        if (TimeArgument(arguments[0], "1st parameter") is not { } time)
            return SqlValue.Null;
        if (arguments[1].Kind != SqlValueKind.Text)
            throw new EmbeddedSqlException("2nd parameter: should be a field name");

        return SqleanTimeGet(time, arguments[1].AsText());
    }

    private static SqlValue EvaluateTimeGetYear(IReadOnlyList<SqlValue> arguments)
        => EvaluateNamedTimeGet(arguments, "year");

    private static SqlValue EvaluateTimeGetMonth(IReadOnlyList<SqlValue> arguments)
        => EvaluateNamedTimeGet(arguments, "month");

    private static SqlValue EvaluateTimeGetDay(IReadOnlyList<SqlValue> arguments)
        => EvaluateNamedTimeGet(arguments, "day");

    private static SqlValue EvaluateTimeGetHour(IReadOnlyList<SqlValue> arguments)
        => EvaluateNamedTimeGet(arguments, "hour");

    private static SqlValue EvaluateTimeGetMinute(IReadOnlyList<SqlValue> arguments)
        => EvaluateNamedTimeGet(arguments, "minute");

    private static SqlValue EvaluateTimeGetSecond(IReadOnlyList<SqlValue> arguments)
    {
        // The named getter returns whole seconds (upstream's get_second()); only the
        // two-argument time_get(t, 'second') form includes the nanosecond fraction.
        if (arguments.Count != 1)
            throw new EmbeddedSqlException("wrong number of arguments to function time_get_second()");
        if (TimeArgument(arguments[0], "parameter") is not { } time)
            return SqlValue.Null;

        return SqlValue.Integer(time.SecondsOfDay() % 60);
    }

    private static SqlValue EvaluateTimeGetNano(IReadOnlyList<SqlValue> arguments)
        => EvaluateNamedTimeGet(arguments, "nano");

    private static SqlValue EvaluateTimeGetWeekday(IReadOnlyList<SqlValue> arguments)
        => EvaluateNamedTimeGet(arguments, "weekday");

    private static SqlValue EvaluateTimeGetYearday(IReadOnlyList<SqlValue> arguments)
        => EvaluateNamedTimeGet(arguments, "yearday");

    private static SqlValue EvaluateTimeGetIsoyear(IReadOnlyList<SqlValue> arguments)
        => EvaluateNamedTimeGet(arguments, "isoyear");

    private static SqlValue EvaluateTimeGetIsoweek(IReadOnlyList<SqlValue> arguments)
        => EvaluateNamedTimeGet(arguments, "isoweek");

    private static SqlValue EvaluateNamedTimeGet(IReadOnlyList<SqlValue> arguments, string field)
    {
        if (arguments.Count != 1)
            throw new EmbeddedSqlException($"wrong number of arguments to function time_get_{field}()");
        if (TimeArgument(arguments[0], "parameter") is not { } time)
            return SqlValue.Null;

        return SqleanTimeGet(time, field);
    }

    private static SqlValue SqleanTimeGet(SqleanTime time, string field)
    {
        var (year, month, day, secondsOfDay) = SqleanDateParts(time);
        var hour = secondsOfDay / 3600;
        var minute = secondsOfDay % 3600 / 60;
        var second = secondsOfDay % 60;
        return field.ToLowerInvariant() switch
        {
            "millennium" => SqlValue.Integer(FloorDiv(year, 1000)),
            "century" => SqlValue.Integer(FloorDiv(year, 100)),
            "decade" => SqlValue.Integer(FloorDiv(year, 10)),
            "year" => SqlValue.Integer(year),
            "quarter" => SqlValue.Integer((month + 2) / 3),
            "month" => SqlValue.Integer(month),
            "day" => SqlValue.Integer(day),
            "hour" => SqlValue.Integer(hour),
            "minute" => SqlValue.Integer(minute),
            "second" => SqlValue.Real(second + time.Nanoseconds / 1_000_000_000.0),
            "millisecond" or "milli" => SqlValue.Integer(time.Nanoseconds / 1_000_000 % 1_000),
            "microsecond" or "micro" => SqlValue.Integer(time.Nanoseconds / 1_000 % 1_000_000),
            "nanosecond" or "nano" => SqlValue.Integer(time.Nanoseconds % 1_000_000_000),
            "isoyear" => SqleanTimeGetIsoYear(time),
            "isoweek" => SqlValue.Integer(SqleanIsoWeek(time, out _, out _)),
            // days_from_sunday: Sunday=0 (chrono's num_days_from_sunday); 0001-01-01 is a
            // Monday, so the offset is (days + 1) % 7.
            "isodow" => SqlValue.Integer((int)EuclideanRemainder(time.Days() + 1, 7)),
            "yearday" => SqlValue.Integer(SqleanDayOfYear(year, month, day)),
            "weekday" => SqlValue.Integer((int)EuclideanRemainder(time.Days() + 1, 7)),
            "epoch" => SqlValue.Real(time.ToUnixSeconds() + time.Nanoseconds / 1_000_000_000.0),
            _ => throw new EmbeddedSqlException($"unknown field name: {field}"),
        };
    }

    private static SqlValue SqleanTimeGetIsoYear(SqleanTime time)
    {
        _ = SqleanIsoWeek(time, out var isoYear, out _);
        return SqlValue.Integer(isoYear);
    }

    private static int SqleanDayOfYear(int year, int month, int day)
        => (int)(SqleanDaysFromCivil(year, month, day) - SqleanDaysFromCivil(year, 1, 1)) + 1;

    /// <summary>
    /// The ISO-8601 week number of <paramref name="time"/>'s date. Mirrors chrono's
    /// <c>iso_week()</c>: weeks start Monday, week 1 contains the year's first Thursday,
    /// and the reported ISO year is the Thursday's year.
    /// </summary>
    private static int SqleanIsoWeek(SqleanTime time, out int isoYear, out int week)
    {
        var days = time.Days();
        var weekday = (int)EuclideanRemainder(days, 7); // 0 = Monday
        var thursday = days + (3 - weekday); // the Thursday of this week
        var (thursdayYear, _, _) = SqleanCivilFromDays(thursday);
        isoYear = thursdayYear;
        var jan1OfIsoYear = SqleanDaysFromCivil(thursdayYear, 1, 1);
        week = (int)FloorDiv(thursday - jan1OfIsoYear, 7) + 1;
        return week;
    }

    private static SqlValue EvaluateTimeUnix(IReadOnlyList<SqlValue> arguments)
    {
        if (arguments.Count is not (1 or 2))
            throw new EmbeddedSqlException("wrong number of arguments to function time_unix()");

        RequireIntegerArguments(arguments, "all parameters should be integers");

        var seconds = arguments[0].AsInteger();
        var nanoseconds = arguments.Count == 2 ? arguments[1].AsInteger() : 0;
        return TimeBlobValue(SqleanTime.FromUnixSeconds(seconds, (int)nanoseconds));
    }

    private static SqlValue EvaluateToTimestamp(IReadOnlyList<SqlValue> arguments)
    {
        if (arguments.Count != 1)
            throw new EmbeddedSqlException("wrong number of arguments to function to_timestamp()");

        return EvaluateTimeUnix(arguments);
    }

    private static SqlValue EvaluateTimeMilli(IReadOnlyList<SqlValue> arguments)
        => EvaluateTimeFromUnixScaled(arguments, "time_milli", 1_000_000);

    private static SqlValue EvaluateTimeMicro(IReadOnlyList<SqlValue> arguments)
        => EvaluateTimeFromUnixScaled(arguments, "time_micro", 1_000);

    private static SqlValue EvaluateTimeNano(IReadOnlyList<SqlValue> arguments)
        => EvaluateTimeFromUnixScaled(arguments, "time_nano", 1);

    private static SqlValue EvaluateTimeFromUnixScaled(
        IReadOnlyList<SqlValue> arguments,
        string name,
        long nanosecondsPerUnit)
    {
        if (arguments.Count != 1)
            throw new EmbeddedSqlException($"wrong number of arguments to function {name}()");
        if (arguments[0].Kind != SqlValueKind.Integer)
            throw new EmbeddedSqlException("parameter should be an integer");

        var totalNanoseconds = arguments[0].AsInteger() * nanosecondsPerUnit;
        return TimeBlobValue(SqleanTime.FromUnixSeconds(
            FloorDiv(totalNanoseconds, 1_000_000_000),
            (int)EuclideanRemainder(totalNanoseconds, 1_000_000_000)));
    }

    private static SqlValue EvaluateTimeToUnix(IReadOnlyList<SqlValue> arguments)
        => EvaluateTimeToScaled(arguments, "time_to_unix", value => value.ToUnixSeconds());

    private static SqlValue EvaluateTimeToMilli(IReadOnlyList<SqlValue> arguments)
        => EvaluateTimeToScaled(arguments, "time_to_milli", value => value.ToUnixSeconds() * 1_000 + value.Nanoseconds / 1_000_000);

    private static SqlValue EvaluateTimeToMicro(IReadOnlyList<SqlValue> arguments)
        => EvaluateTimeToScaled(arguments, "time_to_micro", value => value.ToUnixSeconds() * 1_000_000 + value.Nanoseconds / 1_000);

    private static SqlValue EvaluateTimeToNano(IReadOnlyList<SqlValue> arguments)
        => EvaluateTimeToScaled(arguments, "time_to_nano", value => checked(value.ToUnixSeconds() * 1_000_000_000 + value.Nanoseconds));

    private static SqlValue EvaluateTimeToScaled(
        IReadOnlyList<SqlValue> arguments,
        string name,
        Func<SqleanTime, long> convert)
    {
        if (arguments.Count != 1)
            throw new EmbeddedSqlException($"wrong number of arguments to function {name}()");
        if (TimeArgument(arguments[0], "parameter") is not { } time)
            return SqlValue.Null;

        return SqlValue.Integer(convert(time));
    }

    private static SqlValue EvaluateTimeAfter(IReadOnlyList<SqlValue> arguments)
    {
        if (arguments.Count != 2)
            throw new EmbeddedSqlException("wrong number of arguments to function time_after()");
        if (TimeArgument(arguments[0], "1st parameter") is not { } left)
            return SqlValue.Null;
        if (TimeArgument(arguments[1], "2nd parameter") is not { } right)
            return SqlValue.Null;

        return SqlValue.Integer(CompareSqleanTime(left, right) > 0 ? 1 : 0);
    }

    private static SqlValue EvaluateTimeBefore(IReadOnlyList<SqlValue> arguments)
    {
        if (arguments.Count != 2)
            throw new EmbeddedSqlException("wrong number of arguments to function time_before()");
        if (TimeArgument(arguments[0], "1st parameter") is not { } left)
            return SqlValue.Null;
        if (TimeArgument(arguments[1], "2nd parameter") is not { } right)
            return SqlValue.Null;

        return SqlValue.Integer(CompareSqleanTime(left, right) < 0 ? 1 : 0);
    }

    private static SqlValue EvaluateTimeCompare(IReadOnlyList<SqlValue> arguments)
    {
        if (arguments.Count != 2)
            throw new EmbeddedSqlException("wrong number of arguments to function time_compare()");
        if (TimeArgument(arguments[0], "1st parameter") is not { } left)
            return SqlValue.Null;
        if (TimeArgument(arguments[1], "2nd parameter") is not { } right)
            return SqlValue.Null;

        return SqlValue.Integer(CompareSqleanTime(left, right));
    }

    private static SqlValue EvaluateTimeEqual(IReadOnlyList<SqlValue> arguments)
    {
        if (arguments.Count != 2)
            throw new EmbeddedSqlException("wrong number of arguments to function time_equal()");
        if (TimeArgument(arguments[0], "1st parameter") is not { } left)
            return SqlValue.Null;
        if (TimeArgument(arguments[1], "2nd parameter") is not { } right)
            return SqlValue.Null;

        return SqlValue.Integer(CompareSqleanTime(left, right) == 0 ? 1 : 0);
    }

    private static int CompareSqleanTime(SqleanTime left, SqleanTime right)
    {
        var comparison = left.Seconds.CompareTo(right.Seconds);
        return comparison != 0 ? comparison : left.Nanoseconds.CompareTo(right.Nanoseconds);
    }

    private static SqlValue EvaluateDurNs(IReadOnlyList<SqlValue> arguments)
        => EvaluateDurationConstant(arguments, "dur_ns", 1);

    private static SqlValue EvaluateDurUs(IReadOnlyList<SqlValue> arguments)
        => EvaluateDurationConstant(arguments, "dur_us", 1_000);

    private static SqlValue EvaluateDurMs(IReadOnlyList<SqlValue> arguments)
        => EvaluateDurationConstant(arguments, "dur_ms", 1_000_000);

    private static SqlValue EvaluateDurS(IReadOnlyList<SqlValue> arguments)
        => EvaluateDurationConstant(arguments, "dur_s", 1_000_000_000);

    private static SqlValue EvaluateDurM(IReadOnlyList<SqlValue> arguments)
        => EvaluateDurationConstant(arguments, "dur_m", 60_000_000_000);

    private static SqlValue EvaluateDurH(IReadOnlyList<SqlValue> arguments)
        => EvaluateDurationConstant(arguments, "dur_h", 3_600_000_000_000);

    private static SqlValue EvaluateDurationConstant(IReadOnlyList<SqlValue> arguments, string name, long nanoseconds)
    {
        if (arguments.Count != 0)
            throw new EmbeddedSqlException($"wrong number of arguments to function {name}()");

        return SqlValue.Integer(nanoseconds);
    }

    private static SqlValue EvaluateTimeAdd(IReadOnlyList<SqlValue> arguments)
    {
        if (arguments.Count != 2)
            throw new EmbeddedSqlException("wrong number of arguments to function time_add()");
        if (TimeArgument(arguments[0], "1st parameter") is not { } time)
            return SqlValue.Null;
        if (arguments[1].Kind != SqlValueKind.Integer)
            throw new EmbeddedSqlException("2nd parameter: should be an integer");

        return TimeBlobValue(time.AddNanoseconds(arguments[1].AsInteger()));
    }

    private static SqlValue EvaluateTimeAddDate(IReadOnlyList<SqlValue> arguments)
    {
        if (arguments.Count is not (2 or 3 or 4))
            throw new EmbeddedSqlException("wrong number of arguments to function time_add_date()");
        if (TimeArgument(arguments[0], "1st parameter") is not { } time)
            return SqlValue.Null;

        RequireIntegerArguments(arguments.Skip(1).ToArray(), "parameters should be integers");

        var years = (int)arguments[1].AsInteger();
        var months = arguments.Count >= 3 ? (int)arguments[2].AsInteger() : 0;
        var days = arguments.Count == 4 ? arguments[3].AsInteger() : 0;

        // chrono moves whole months first (clamping the day-of-month), then days as a
        // duration.
        var (year, month, day, secondsOfDay) = SqleanDateParts(time);
        var totalMonths = (year - 1L) * 12 + (month - 1) + years * 12L + months;
        var canonicalYear = FloorDiv(totalMonths, 12) + 1;
        var canonicalMonth = (int)EuclideanRemainder(totalMonths, 12) + 1;
        var daysInMonth = SqleanDaysFromCivil(
            (int)canonicalYear,
            canonicalMonth == 12 ? 1 : canonicalMonth + 1,
            1) - SqleanDaysFromCivil((int)canonicalYear, canonicalMonth, 1);
        var clampedDay = Math.Min(day, (int)daysInMonth);
        var baseDays = SqleanDaysFromCivil((int)canonicalYear, canonicalMonth, clampedDay);
        var seconds = baseDays * UnixSecondsPerDay + secondsOfDay + days * UnixSecondsPerDay;
        return TimeBlobValue(new SqleanTime(seconds, time.Nanoseconds));
    }

    private static SqlValue EvaluateTimeSub(IReadOnlyList<SqlValue> arguments)
    {
        if (arguments.Count != 2)
            throw new EmbeddedSqlException("wrong number of arguments to function time_sub()");
        if (TimeArgument(arguments[0], "1st parameter") is not { } left)
            return SqlValue.Null;
        if (TimeArgument(arguments[1], "2nd parameter") is not { } right)
            return SqlValue.Null;

        return TimeDifference(left, right);
    }

    private static SqlValue EvaluateTimeSince(IReadOnlyList<SqlValue> arguments)
    {
        if (arguments.Count != 1)
            throw new EmbeddedSqlException("wrong number of arguments to function time_since()");
        if (TimeArgument(arguments[0], "parameter") is not { } time)
            return SqlValue.Null;

        var (nowSeconds, nowNanoseconds) = SqleanUtcNow();
        return TimeDifference(new SqleanTime(nowSeconds, nowNanoseconds), time);
    }

    private static SqlValue EvaluateTimeUntil(IReadOnlyList<SqlValue> arguments)
    {
        if (arguments.Count != 1)
            throw new EmbeddedSqlException("wrong number of arguments to function time_until()");
        if (TimeArgument(arguments[0], "parameter") is not { } time)
            return SqlValue.Null;

        var (nowSeconds, nowNanoseconds) = SqleanUtcNow();
        return TimeDifference(time, new SqleanTime(nowSeconds, nowNanoseconds));
    }

    private static (long Seconds, int Nanoseconds) SqleanUtcNow()
    {
        var utcNow = DateTime.UtcNow;
        var seconds = (long)(utcNow - new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        var nanoseconds = (int)(utcNow.Ticks % TimeSpan.TicksPerSecond * 100);
        return (seconds, nanoseconds);
    }

    private static SqlValue TimeDifference(SqleanTime left, SqleanTime right)
    {
        try
        {
            var secondDelta = checked(left.Seconds - right.Seconds);
            var nanoDelta = checked(left.Nanoseconds - right.Nanoseconds);
            if (nanoDelta < 0)
            {
                secondDelta = checked(secondDelta - 1);
                nanoDelta += 1_000_000_000;
            }
            else if (nanoDelta >= 1_000_000_000)
            {
                secondDelta = checked(secondDelta + 1);
                nanoDelta -= 1_000_000_000;
            }

            return SqlValue.Integer(checked(secondDelta * 1_000_000_000 + nanoDelta));
        }
        catch (OverflowException)
        {
            return SqlValue.Integer(CompareSqleanTime(left, right) > 0 ? long.MaxValue : long.MinValue);
        }
    }

    private static SqlValue EvaluateTimeTrunc(IReadOnlyList<SqlValue> arguments)
    {
        if (arguments.Count != 2)
            throw new EmbeddedSqlException("wrong number of arguments to function time_trunc()");
        if (TimeArgument(arguments[0], "1st parameter") is not { } time)
            return SqlValue.Null;

        return arguments[1].Kind switch
        {
            SqlValueKind.Text => SqleanTimeTruncField(time, arguments[1].AsText()),
            SqlValueKind.Integer => SqleanTimeTruncDuration(time, arguments[1].AsInteger()),
            _ => throw new EmbeddedSqlException("2nd parameter: should be a field name"),
        };
    }

    private static SqlValue SqleanTimeTruncField(SqleanTime time, string field)
    {
        var (year, month, day, secondsOfDay) = SqleanDateParts(time);
        var lower = field.ToLowerInvariant();
        int truncYear;
        var truncMonth = 1;
        var week = 0;
        long truncDay = 1;
        var hour = secondsOfDay / 3600;
        var minute = secondsOfDay % 3600 / 60;
        var second = secondsOfDay % 60;
        long truncHour = 0, truncMinute = 0, truncSecond = 0, nanosecond = 0;
        switch (lower)
        {
            case "millennium":
                truncYear = (int)FloorDiv(year, 1000) * 1000;
                break;
            case "century":
                truncYear = (int)FloorDiv(year, 100) * 100;
                break;
            case "decade":
                truncYear = (int)FloorDiv(year, 10) * 10;
                break;
            case "year":
                truncYear = year;
                break;
            case "quarter":
                truncYear = year;
                truncMonth = (month - 1) / 3 * 3 + 1;
                break;
            case "month":
                truncYear = year;
                truncMonth = month;
                break;
            case "week":
                SqleanIsoWeek(time, out var isoYear, out week);
                truncYear = isoYear;
                break;
            case "day":
                truncYear = year;
                truncMonth = month;
                truncDay = day;
                break;
            case "hour":
                truncYear = year;
                truncMonth = month;
                truncDay = day;
                truncHour = hour;
                break;
            case "minute":
                truncYear = year;
                truncMonth = month;
                truncDay = day;
                truncHour = hour;
                truncMinute = minute;
                break;
            case "second":
                truncYear = year;
                truncMonth = month;
                truncDay = day;
                truncHour = hour;
                truncMinute = minute;
                truncSecond = second;
                break;
            case "millisecond" or "milli":
                truncYear = year;
                truncMonth = month;
                truncDay = day;
                truncHour = hour;
                truncMinute = minute;
                truncSecond = second;
                nanosecond = time.Nanoseconds / 1_000_000 * 1_000_000;
                break;
            case "microsecond" or "micro":
                truncYear = year;
                truncMonth = month;
                truncDay = day;
                truncHour = hour;
                truncMinute = minute;
                truncSecond = second;
                nanosecond = time.Nanoseconds / 1_000 * 1_000;
                break;
            default:
                throw new EmbeddedSqlException($"unknown field name: {field}");
        }

        var result = SqleanBuildTime(truncYear, truncMonth, truncDay, truncHour, truncMinute, truncSecond, nanosecond);
        if (result is null)
            return SqlValue.Null;

        if (week != 0)
        {
            // Mirror upstream: Jan 1 of the ISO year plus (week - 1) * 7 days.
            var jan1 = SqleanDaysFromCivil(truncYear, 1, 1);
            var seconds = checked(jan1 * UnixSecondsPerDay + (week - 1) * 7 * UnixSecondsPerDay);
            result = new SqleanTime(seconds, 0);
        }

        return TimeBlobValue(result.Value);
    }

    private static SqlValue SqleanTimeTruncDuration(SqleanTime time, long durationNanoseconds)
    {
        if (durationNanoseconds == 0)
            return TimeBlobValue(time);

        // seconds * 1e9 can overflow long, so the trunc/round arithmetic runs in decimal.
        var totalNanoseconds = (decimal)time.Seconds * 1_000_000_000m + time.Nanoseconds;
        var remainder = totalNanoseconds - decimal.Floor(totalNanoseconds / durationNanoseconds) * durationNanoseconds;
        var truncated = totalNanoseconds - remainder;
        return TimeBlobValue(new SqleanTime(
            (long)decimal.Floor(truncated / 1_000_000_000m),
            (int)(truncated - decimal.Floor(truncated / 1_000_000_000m) * 1_000_000_000m)));
    }

    private static SqlValue EvaluateTimeRound(IReadOnlyList<SqlValue> arguments)
    {
        if (arguments.Count != 2)
            throw new EmbeddedSqlException("wrong number of arguments to function time_round()");
        if (TimeArgument(arguments[0], "1st parameter") is not { } time)
            return SqlValue.Null;
        if (arguments[1].Kind != SqlValueKind.Integer)
            throw new EmbeddedSqlException("2nd parameter: should be an integer");

        var duration = arguments[1].AsInteger();
        if (duration == 0)
            return TimeBlobValue(time);

        // chrono's duration_round rounds to the nearest multiple, ties away from zero.
        var totalNanoseconds = (decimal)time.Seconds * 1_000_000_000m + time.Nanoseconds;
        var remainder = totalNanoseconds - decimal.Floor(totalNanoseconds / duration) * duration;
        var rounded = totalNanoseconds - remainder + (remainder * 2 >= duration ? duration : 0m);
        return TimeBlobValue(new SqleanTime(
            (long)decimal.Floor(rounded / 1_000_000_000m),
            (int)(rounded - decimal.Floor(rounded / 1_000_000_000m) * 1_000_000_000m)));
    }

    private static SqlValue EvaluateTimeFmtIso(IReadOnlyList<SqlValue> arguments)
        => SqleanTimeFormat(arguments, "time_fmt_iso", (time, offset) =>
        {
            var shifted = time.AddNanoseconds(checked(offset) * 1_000_000_000);
            var (year, month, day, secondsOfDay) = SqleanDateParts(shifted);
            var date = $"{year:D4}-{month:D2}-{day:D2}";
            var timePart = $"{secondsOfDay / 3600:D2}:{secondsOfDay % 3600 / 60:D2}:{secondsOfDay % 60:D2}";

            if (offset == 0)
            {
                return time.Nanoseconds == 0
                    ? $"{date}T{timePart}Z"
                    : $"{date}T{timePart}.{SqleanNineDigits(time.Nanoseconds)}Z";
            }

            var sign = offset >= 0 ? "+" : "-";
            var absOffset = Math.Abs(offset);
            var offsetText = $"{sign}{absOffset / 3600:D2}:{absOffset % 3600 / 60:D2}";
            return time.Nanoseconds == 0
                ? $"{date}T{timePart}{offsetText}"
                : $"{date}T{timePart}.{SqleanNineDigits(time.Nanoseconds)}{offsetText}";
        });

    private static string SqleanNineDigits(int nanoseconds)
        => nanoseconds.ToString("D9", CultureInfo.InvariantCulture);

    private static SqlValue EvaluateTimeFmtDatetime(IReadOnlyList<SqlValue> arguments)
        => SqleanTimeFormat(arguments, "time_fmt_datetime", (time, offset) =>
            $"{SqleanFormatDate(time, offset)} {SqleanFormatTime(time, offset)}");

    private static SqlValue EvaluateTimeFmtDate(IReadOnlyList<SqlValue> arguments)
        => SqleanTimeFormat(arguments, "time_fmt_date", (time, offset) => SqleanFormatDate(time, offset));

    private static SqlValue EvaluateTimeFmtTime(IReadOnlyList<SqlValue> arguments)
        => SqleanTimeFormat(arguments, "time_fmt_time", (time, offset) => SqleanFormatTime(time, offset));

    private static string SqleanFormatDate(SqleanTime time, long offset)
    {
        var shifted = time.AddNanoseconds(offset * 1_000_000_000);
        var (year, month, day, _) = SqleanDateParts(shifted);
        return $"{year:D4}-{month:D2}-{day:D2}";
    }

    private static string SqleanFormatTime(SqleanTime time, long offset)
    {
        var shifted = time.AddNanoseconds(offset * 1_000_000_000);
        var secondsOfDay = shifted.SecondsOfDay();
        return $"{secondsOfDay / 3600:D2}:{secondsOfDay % 3600 / 60:D2}:{secondsOfDay % 60:D2}";
    }

    private static SqlValue SqleanTimeFormat(
        IReadOnlyList<SqlValue> arguments,
        string name,
        Func<SqleanTime, long, string> format)
    {
        if (arguments.Count is not (1 or 2))
            throw new EmbeddedSqlException($"wrong number of arguments to function {name}()");
        if (TimeArgument(arguments[0], "1st parameter") is not { } time)
            return SqlValue.Null;

        var offset = 0L;
        if (arguments.Count == 2)
        {
            if (arguments[1].Kind != SqlValueKind.Integer)
                throw new EmbeddedSqlException("2nd parameter: should be an integer");
            offset = arguments[1].AsInteger();
        }

        return SqlValue.Text(format(time, offset));
    }

    private static SqlValue EvaluateTimeParse(IReadOnlyList<SqlValue> arguments)
    {
        if (arguments.Count != 1)
            throw new EmbeddedSqlException("wrong number of arguments to function time_parse()");
        if (arguments[0].Kind != SqlValueKind.Text)
            throw new EmbeddedSqlException("parameter should be a text");

        var text = arguments[0].AsText();

        // RFC 3339 (with offset or Z).
        if (SqleanTryParseRfc3339(text, out var parsed))
            return TimeBlobValue(parsed);

        // "YYYY-MM-DD HH:MM:SS"
        if (SqleanTryParseYmdHms(text, out var naive))
            return TimeBlobValue(naive);

        // "YYYY-MM-DD"
        if (SqleanTryParseYmd(text, out var dateOnly))
            return TimeBlobValue(dateOnly);

        // "HH:MM:SS" on 0001-01-01.
        if (SqleanTryParseHms(text, out var timeOnly))
            return TimeBlobValue(timeOnly);

        throw new EmbeddedSqlException("error parsing datetime string");
    }

    private static bool SqleanTryParseYmd(string text, out SqleanTime time)
    {
        time = default;
        if (text.Length != 10 || text[4] != '-' || text[7] != '-')
            return false;

        if (!int.TryParse(text[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(text[5..7], NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            || !int.TryParse(text[8..10], NumberStyles.None, CultureInfo.InvariantCulture, out var day))
        {
            return false;
        }

        if (month is < 1 or > 12 || day is < 1 or > 31)
            return false;

        var days = SqleanDaysFromCivil(year, month, day);
        time = new SqleanTime(days * UnixSecondsPerDay, 0);
        return true;
    }

    private static bool SqleanTryParseYmdHms(string text, out SqleanTime time)
    {
        time = default;
        if (text.Length != 19)
            return false;

        if (!SqleanTryParseYmd(text[..10], out var date))
            return false;
        if (!SqleanTryParseHms(text[11..], out var timeOfDay))
            return false;

        time = new SqleanTime(date.Seconds + timeOfDay.Seconds, 0);
        return true;
    }

    private static bool SqleanTryParseHms(string text, out SqleanTime time)
    {
        time = default;
        if (text.Length != 8 || text[2] != ':' || text[5] != ':')
            return false;

        if (!int.TryParse(text[..2], NumberStyles.None, CultureInfo.InvariantCulture, out var hour)
            || !int.TryParse(text[3..5], NumberStyles.None, CultureInfo.InvariantCulture, out var minute)
            || !int.TryParse(text[6..8], NumberStyles.None, CultureInfo.InvariantCulture, out var second))
        {
            return false;
        }

        if (hour > 23 || minute > 59 || second > 59)
            return false;

        // Time-only parses onto 0001-01-01 (days = 0).
        time = new SqleanTime(hour * 3600 + minute * 60 + second, 0);
        return true;
    }

    private static bool SqleanTryParseRfc3339(string text, out SqleanTime time)
    {
        time = default;
        if (text.Length < 20)
            return false;

        if (!SqleanTryParseYmd(text[..10], out var date))
            return false;

        var rest = text[10..];
        if (rest[0] is not ('T' or 't' or ' '))
            return false;
        rest = rest[1..];

        // Time part (HH:MM:SS).
        if (rest.Length < 8 || !SqleanTryParseHms(rest[..8], out var timeOfDay))
            return false;
        rest = rest[8..];

        // Optional fraction.
        var nanoseconds = 0;
        if (rest.Length > 0 && rest[0] == '.')
        {
            var fraction = new StringBuilder();
            var index = 1;
            while (index < rest.Length && char.IsAsciiDigit(rest[index]))
            {
                fraction.Append(rest[index]);
                index++;
            }

            if (fraction.Length == 0)
                return false;
            nanoseconds = int.Parse(fraction.ToString().PadRight(9, '0')[..9], CultureInfo.InvariantCulture);
            rest = rest[index..];
        }

        // Offset: Z or ±HH:MM / ±HHMM.
        var offsetSeconds = 0L;
        if (rest.Length == 0)
            return false;

        if (rest is "Z" or "z")
        {
            // UTC.
        }
        else if (rest[0] is '+' or '-')
        {
            var sign = rest[0] == '-' ? -1 : 1;
            var body = rest[1..].Replace(":", string.Empty);
            if (body.Length is not (2 or 4))
                return false;
            var offsetHours = int.Parse(body[..2], NumberStyles.None, CultureInfo.InvariantCulture);
            var offsetMinutes = body.Length == 4
                ? int.Parse(body[2..], NumberStyles.None, CultureInfo.InvariantCulture)
                : 0;
            if (offsetHours > 23 || offsetMinutes > 59)
                return false;
            offsetSeconds = sign * (offsetHours * 3600L + offsetMinutes * 60);
        }
        else
        {
            return false;
        }

        var seconds = date.Seconds + timeOfDay.Seconds - offsetSeconds;
        time = new SqleanTime(seconds, nanoseconds);
        return true;
    }
}
