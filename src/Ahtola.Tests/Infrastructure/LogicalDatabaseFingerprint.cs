using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;

namespace Ahtola.Tests.Infrastructure;

internal sealed record LogicalDatabaseFingerprintResult(
    string Sha256,
    int SchemaObjects,
    int Tables,
    long Rows)
{
    public override string ToString()
        => $"{Sha256} (schema={SchemaObjects}, tables={Tables}, rows={Rows})";
}

/// <summary>
/// Test-only logical database fingerprint inspired by Turso's tools/dbhash.
/// The encoding is independent of pages, rowids, insertion history, and provider object types.
/// </summary>
internal static class LogicalDatabaseFingerprint
{
    private const byte FormatVersion = 1;

    internal static LogicalDatabaseFingerprintResult Compute(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != ConnectionState.Open)
            throw new InvalidOperationException("The connection must be open before it can be fingerprinted.");

        using var canonical = new MemoryStream();
        canonical.WriteByte(FormatVersion);

        var schemaRows = ReadRows(
            connection,
            """
            SELECT type, name, tbl_name, sql
            FROM sqlite_schema
            WHERE name NOT LIKE 'sqlite_%'
              AND name NOT LIKE '__turso_internal_%'
              AND name NOT IN ('turso_cdc', 'turso_cdc_version', 'turso_sync_last_change_id')
            ORDER BY type, name, tbl_name, sql
            """);
        WriteCollection(canonical, 0x53, schemaRows);

        var tables = ReadNames(
            connection,
            """
            SELECT name
            FROM sqlite_schema
            WHERE type = 'table'
              AND name NOT LIKE 'sqlite_%'
              AND name NOT LIKE '__turso_internal_%'
              AND name NOT IN ('turso_cdc', 'turso_cdc_version', 'turso_sync_last_change_id')
              AND sql NOT LIKE 'CREATE VIRTUAL TABLE%'
            ORDER BY name
            """);

        long rowCount = 0;
        foreach (var table in tables)
        {
            canonical.WriteByte(0x54);
            WriteBytes(canonical, Encoding.UTF8.GetBytes(table));

            var rows = ReadRows(connection, $"SELECT * FROM {QuoteIdentifier(table)}");
            rows.Sort(ByteArrayComparer.Instance);
            WriteInt64(canonical, rows.Count);
            for (var ordinal = 0; ordinal < rows.Count; ordinal++)
            {
                canonical.WriteByte(0x52);
                WriteInt64(canonical, ordinal);
                WriteBytes(canonical, rows[ordinal]);
            }

            rowCount += rows.Count;
        }

        var digest = SHA256.HashData(canonical.GetBuffer().AsSpan(0, checked((int)canonical.Length)));
        return new LogicalDatabaseFingerprintResult(
            Convert.ToHexString(digest).ToLowerInvariant(),
            schemaRows.Count,
            tables.Count,
            rowCount);
    }

    private static List<string> ReadNames(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
            values.Add(reader.GetString(0));
        return values;
    }

    private static List<byte[]> ReadRows(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<byte[]>();
        while (reader.Read())
        {
            using var encoded = new MemoryStream();
            WriteInt64(encoded, reader.FieldCount);
            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                WriteValue(encoded, reader.GetValue(ordinal));
            rows.Add(encoded.ToArray());
        }
        return rows;
    }

    private static void WriteCollection(Stream destination, byte marker, IReadOnlyList<byte[]> values)
    {
        destination.WriteByte(marker);
        WriteInt64(destination, values.Count);
        foreach (var value in values)
            WriteBytes(destination, value);
    }

    private static void WriteValue(Stream destination, object value)
    {
        switch (value)
        {
            case DBNull:
                destination.WriteByte(0);
                return;
            case byte or sbyte or short or ushort or int or uint or long:
                destination.WriteByte(1);
                WriteInt64(destination, Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
                return;
            case ulong unsigned when unsigned <= long.MaxValue:
                destination.WriteByte(1);
                WriteInt64(destination, checked((long)unsigned));
                return;
            case float or double:
                destination.WriteByte(2);
                WriteInt64(destination, BitConverter.DoubleToInt64Bits(Convert.ToDouble(
                    value,
                    System.Globalization.CultureInfo.InvariantCulture)));
                return;
            case string text:
                destination.WriteByte(3);
                WriteBytes(destination, Encoding.UTF8.GetBytes(text));
                return;
            case byte[] blob:
                destination.WriteByte(4);
                WriteBytes(destination, blob);
                return;
            case ReadOnlyMemory<byte> blob:
                destination.WriteByte(4);
                WriteBytes(destination, blob.Span);
                return;
            default:
                throw new InvalidDataException(
                    $"Unsupported logical SQLite value type '{value.GetType().FullName}'.");
        }
    }

    private static void WriteInt64(Stream destination, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        destination.Write(bytes);
    }

    private static void WriteBytes(Stream destination, ReadOnlySpan<byte> value)
    {
        WriteInt64(destination, value.Length);
        destination.Write(value);
    }

    private static string QuoteIdentifier(string value)
        => '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        internal static readonly ByteArrayComparer Instance = new();

        public int Compare(byte[]? left, byte[]? right)
        {
            if (ReferenceEquals(left, right))
                return 0;
            if (left is null)
                return -1;
            if (right is null)
                return 1;
            return left.AsSpan().SequenceCompareTo(right);
        }
    }
}
