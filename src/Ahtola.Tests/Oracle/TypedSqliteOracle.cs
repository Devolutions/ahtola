using System.Data.Common;
using System.Globalization;
using AhtolaSqliteException = Ahtola.Data.Sqlite.SqliteException;
using MicrosoftSqliteException = Microsoft.Data.Sqlite.SqliteException;

namespace Ahtola.Tests.Oracle;

internal enum OracleValueKind
{
    Null,
    Integer,
    Real,
    Text,
    Blob,
}

internal readonly struct OracleValue : IEquatable<OracleValue>
{
    private readonly long _integer;
    private readonly long _realBits;
    private readonly string? _text;
    private readonly byte[]? _blob;

    private OracleValue(OracleValueKind kind, long integer = 0, long realBits = 0, string? text = null, byte[]? blob = null)
    {
        Kind = kind;
        _integer = integer;
        _realBits = realBits;
        _text = text;
        _blob = blob;
    }

    public OracleValueKind Kind { get; }

    public static OracleValue Null => new(OracleValueKind.Null);

    public static OracleValue Integer(long value) => new(OracleValueKind.Integer, integer: value);

    public static OracleValue Real(double value) => new(OracleValueKind.Real, realBits: BitConverter.DoubleToInt64Bits(value));

    public static OracleValue Text(string value) => new(OracleValueKind.Text, text: value);

    public static OracleValue Blob(byte[] value) => new(OracleValueKind.Blob, blob: [.. value]);

    public bool Equals(OracleValue other)
    {
        if (Kind != other.Kind)
            return false;

        return Kind switch
        {
            OracleValueKind.Null => true,
            OracleValueKind.Integer => _integer == other._integer,
            OracleValueKind.Real => _realBits == other._realBits,
            OracleValueKind.Text => string.Equals(_text, other._text, StringComparison.Ordinal),
            OracleValueKind.Blob => _blob!.AsSpan().SequenceEqual(other._blob),
            _ => false,
        };
    }

    public override bool Equals(object? obj) => obj is OracleValue other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        switch (Kind)
        {
            case OracleValueKind.Integer:
                hash.Add(_integer);
                break;
            case OracleValueKind.Real:
                hash.Add(_realBits);
                break;
            case OracleValueKind.Text:
                hash.Add(_text, StringComparer.Ordinal);
                break;
            case OracleValueKind.Blob:
                foreach (var octet in _blob!)
                    hash.Add(octet);
                break;
        }

        return hash.ToHashCode();
    }

    public override string ToString()
        => Kind switch
        {
            OracleValueKind.Null => "NULL",
            OracleValueKind.Integer => $"INTEGER({_integer.ToString(CultureInfo.InvariantCulture)})",
            OracleValueKind.Real => $"REAL({BitConverter.Int64BitsToDouble(_realBits).ToString("R", CultureInfo.InvariantCulture)}, bits=0x{_realBits:x16})",
            OracleValueKind.Text => $"TEXT({JsonString(_text!)})",
            OracleValueKind.Blob => $"BLOB({Convert.ToHexString(_blob!)})",
            _ => Kind.ToString(),
        };

    public static bool operator ==(OracleValue left, OracleValue right) => left.Equals(right);

    public static bool operator !=(OracleValue left, OracleValue right) => !left.Equals(right);

    private static string JsonString(string value)
        => System.Text.Json.JsonSerializer.Serialize(value);
}

internal sealed class OracleRow : IEquatable<OracleRow>
{
    public OracleRow(IEnumerable<OracleValue> values)
    {
        Values = [.. values];
    }

    public IReadOnlyList<OracleValue> Values { get; }

    public bool Equals(OracleRow? other)
        => other is not null && Values.SequenceEqual(other.Values);

    public override bool Equals(object? obj) => Equals(obj as OracleRow);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var value in Values)
            hash.Add(value);
        return hash.ToHashCode();
    }

    public override string ToString() => $"[{string.Join(", ", Values)}]";
}

internal enum OracleExecutionKind
{
    Success,
    Error,
}

internal sealed record OracleError(string Category, int? SqliteErrorCode, string Message);

internal sealed record OracleExecutionResult(
    OracleExecutionKind Kind,
    bool HasResultSet,
    IReadOnlyList<string> Columns,
    IReadOnlyList<OracleRow> Rows,
    OracleError? Error)
{
    public static OracleExecutionResult Success(bool hasResultSet, IReadOnlyList<string> columns, IReadOnlyList<OracleRow> rows)
        => new(OracleExecutionKind.Success, hasResultSet, columns, rows, null);

    public static OracleExecutionResult Failure(OracleError error)
        => new(OracleExecutionKind.Error, false, [], [], error);
}

internal static class TypedSqliteOracle
{
    public static OracleExecutionResult Execute(DbConnection connection, string sql)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var hasResultSet = reader.FieldCount != 0;
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
            var rows = new List<OracleRow>();
            while (reader.Read())
            {
                var values = new OracleValue[reader.FieldCount];
                for (var index = 0; index < values.Length; index++)
                    values[index] = Normalize(reader.GetValue(index));
                rows.Add(new OracleRow(values));
            }

            return OracleExecutionResult.Success(hasResultSet, columns, rows);
        }
        catch (Exception exception)
        {
            return OracleExecutionResult.Failure(NormalizeError(exception));
        }
    }

    public static void AssertEquivalent(
        OracleExecutionResult managed,
        OracleExecutionResult reference,
        bool ordered,
        string diagnostics)
    {
        if (managed.Kind != reference.Kind)
            Fail($"success/error mismatch: managed={Describe(managed)}, SQLite={Describe(reference)}", diagnostics);

        if (managed.Kind == OracleExecutionKind.Error)
        {
            if (!string.Equals(managed.Error!.Category, reference.Error!.Category, StringComparison.Ordinal))
                Fail($"error category mismatch: managed={Describe(managed)}, SQLite={Describe(reference)}", diagnostics);

            if (managed.Error.SqliteErrorCode is { } managedCode
                && reference.Error.SqliteErrorCode is { } referenceCode
                && managedCode != referenceCode)
            {
                Fail($"SQLite error code mismatch: managed={managedCode}, SQLite={referenceCode}", diagnostics);
            }

            return;
        }

        if (managed.HasResultSet != reference.HasResultSet)
            Fail($"result-set category mismatch: managed={managed.HasResultSet}, SQLite={reference.HasResultSet}", diagnostics);
        if (!managed.Columns.SequenceEqual(reference.Columns, StringComparer.Ordinal))
            Fail($"column mismatch: managed=[{string.Join(", ", managed.Columns)}], SQLite=[{string.Join(", ", reference.Columns)}]", diagnostics);

        var mismatch = ordered
            ? OrderedMismatch(managed.Rows, reference.Rows)
            : BagMismatch(managed.Rows, reference.Rows);
        if (mismatch is not null)
            Fail(mismatch, diagnostics);
    }

    public static void AssertEquivalent(
        DbConnection managed,
        DbConnection reference,
        string sql,
        bool ordered,
        string diagnostics)
        => AssertEquivalent(Execute(managed, sql), Execute(reference, sql), ordered, $"{diagnostics}; SQL={sql}");

    public static OracleExecutionResult TableSnapshot(DbConnection connection, string table, bool includeRowId = true)
    {
        var quoted = QuoteIdentifier(table);
        var projection = includeRowId ? "rowid AS __oracle_rowid, *" : "*";
        var ordering = includeRowId ? " ORDER BY rowid" : string.Empty;
        return Execute(connection, $"SELECT {projection} FROM {quoted}{ordering};");
    }

    public static OracleExecutionResult SchemaSnapshot(DbConnection connection)
        => Execute(
            connection,
            "SELECT type, name, tbl_name, sql FROM sqlite_schema "
            + "WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name;");

    public static void AssertIntegrity(DbConnection connection, string diagnostics)
    {
        var result = Execute(connection, "PRAGMA integrity_check;");
        if (result.Kind != OracleExecutionKind.Success
            || result.Rows.Count != 1
            || result.Rows[0].Values.Count != 1
            || result.Rows[0].Values[0] != OracleValue.Text("ok"))
        {
            Fail($"integrity_check did not return one TEXT(\"ok\") row: {Describe(result)}", diagnostics);
        }
    }

    private static OracleValue Normalize(object value)
        => value switch
        {
            null or DBNull => OracleValue.Null,
            long integer => OracleValue.Integer(integer),
            int integer => OracleValue.Integer(integer),
            short integer => OracleValue.Integer(integer),
            byte integer => OracleValue.Integer(integer),
            double real => OracleValue.Real(real),
            float real => OracleValue.Real(real),
            string text => OracleValue.Text(text),
            byte[] blob => OracleValue.Blob(blob),
            _ => throw new InvalidOperationException(
                $"Unexpected SQLite storage value type {value.GetType().FullName}; value={value}."),
        };

    private static OracleError NormalizeError(Exception exception)
    {
        var (code, message) = exception switch
        {
            AhtolaSqliteException sqlite => ((int?)sqlite.SqliteErrorCode, sqlite.Message),
            MicrosoftSqliteException sqlite => ((int?)sqlite.SqliteErrorCode, sqlite.Message),
            _ => ((int?)null, exception.Message),
        };

        return new OracleError(code is { } value ? PrimaryErrorCategory(value) : exception.GetType().Name, code, message);
    }

    private static string PrimaryErrorCategory(int code)
        => (code & 0xff) switch
        {
            1 => "SQLITE_ERROR",
            5 => "SQLITE_BUSY",
            6 => "SQLITE_LOCKED",
            8 => "SQLITE_READONLY",
            9 => "SQLITE_INTERRUPT",
            10 => "SQLITE_IOERR",
            11 => "SQLITE_CORRUPT",
            13 => "SQLITE_FULL",
            14 => "SQLITE_CANTOPEN",
            17 => "SQLITE_SCHEMA",
            18 => "SQLITE_TOOBIG",
            19 => "SQLITE_CONSTRAINT",
            20 => "SQLITE_MISMATCH",
            21 => "SQLITE_MISUSE",
            23 => "SQLITE_AUTH",
            25 => "SQLITE_RANGE",
            26 => "SQLITE_NOTADB",
            var value => $"SQLITE_{value}",
        };

    private static string? OrderedMismatch(IReadOnlyList<OracleRow> managed, IReadOnlyList<OracleRow> reference)
    {
        if (managed.Count != reference.Count)
            return $"row count mismatch: managed={managed.Count}, SQLite={reference.Count}";

        for (var index = 0; index < managed.Count; index++)
        {
            if (!managed[index].Equals(reference[index]))
                return $"ordered row mismatch at {index}: managed={managed[index]}, SQLite={reference[index]}";
        }

        return null;
    }

    private static string? BagMismatch(IReadOnlyList<OracleRow> managed, IReadOnlyList<OracleRow> reference)
    {
        var counts = new Dictionary<OracleRow, int>();
        foreach (var row in managed)
            counts[row] = counts.GetValueOrDefault(row) + 1;
        foreach (var row in reference)
            counts[row] = counts.GetValueOrDefault(row) - 1;

        var differences = counts.Where(pair => pair.Value != 0).ToArray();
        return differences.Length == 0
            ? null
            : "unordered row-bag mismatch (signed count is managed minus SQLite): "
                + string.Join(", ", differences.Select(pair => $"{pair.Key} => {pair.Value}"));
    }

    private static string Describe(OracleExecutionResult result)
        => result.Kind == OracleExecutionKind.Error
            ? $"error(category={result.Error!.Category}, code={result.Error.SqliteErrorCode}, message={result.Error.Message})"
            : $"success(resultSet={result.HasResultSet}, rows=[{string.Join(", ", result.Rows)}])";

    private static string QuoteIdentifier(string identifier)
        => '"' + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    private static void Fail(string message, string diagnostics)
        => throw new AssertionException($"{message}{Environment.NewLine}{diagnostics}");
}
