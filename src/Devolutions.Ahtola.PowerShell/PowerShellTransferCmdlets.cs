using System.Collections;
using System.Collections.Specialized;
using System.Data;
using System.Globalization;
using System.Management.Automation;
using System.Text;
using System.Text.Json;
using Ahtola.Data.Sqlite;

namespace Ahtola.PSSqlite;

[Cmdlet(VerbsLifecycle.Invoke, "AhtolaSqliteBulkCopy", SupportsShouldProcess = true)]
[OutputType(typeof(int))]
public sealed class InvokePSSqliteBulkCopyCommand : PSSqliteCmdlet
{
    private readonly List<OrderedDictionary> _rows = new();

    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    public object InputObject { get; set; } = null!;

    [Parameter(Mandatory = true)]
    [Alias("SqliteConnection")]
    public SqliteConnection Connection { get; set; } = null!;

    [Parameter(Mandatory = true)]
    [Alias("TableName")]
    public string Table { get; set; } = string.Empty;

    [Parameter]
    [ValidateRange(1, 100_000)]
    public int BatchSize { get; set; } = 1_000;

    [Parameter]
    public SqliteTransaction? Transaction { get; set; }

    protected override void ProcessRecord()
    {
        _rows.Add(PowerShellRow.ToDictionary(InputObject));
    }

    protected override void EndProcessing()
    {
        if (_rows.Count == 0 || !ShouldProcess(Table, $"Bulk copy {_rows.Count} row(s)"))
        {
            return;
        }

        WriteObject(BulkCopyExecutor.Execute(
            Connection,
            Table,
            _rows,
            BatchSize,
            Transaction));
    }
}

[Cmdlet(VerbsData.Export, "AhtolaSqliteTable", SupportsShouldProcess = true)]
public sealed class ExportPSSqliteTableCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    [Alias("SqliteConnection")]
    public SqliteConnection Connection { get; set; } = null!;

    [Parameter(Mandatory = true, ParameterSetName = "Table")]
    [Alias("TableName")]
    public string? Table { get; set; }

    [Parameter(Mandatory = true, ParameterSetName = "Query")]
    public string? Query { get; set; }

    [Parameter(ParameterSetName = "Query")]
    public IDictionary? Parameters { get; set; }

    [Parameter(Mandatory = true)]
    public string Path { get; set; } = string.Empty;

    [Parameter]
    [ValidateSet("Json", "Csv")]
    public string? Format { get; set; }

    protected override void ProcessRecord()
    {
        var outputPath = System.IO.Path.GetFullPath(ExpandString(Path));
        var format = TableInterchange.ResolveFormat(outputPath, Format);
        var commandText = ParameterSetName == "Query"
            ? Query!
            : $"SELECT * FROM {SqlIdentifier.QuoteQualified(Table!)};";
        var target = ParameterSetName == "Query" ? "query result" : $"table {Table}";
        if (!ShouldProcess(outputPath, $"Export {target} as {format}"))
        {
            return;
        }

        var table = (DataTable)QueryExecutor.Execute(
            Connection,
            commandText,
            Parameters,
            new QueryOptions { OutputFormat = "DataTable" })!;
        TableInterchange.Export(table, outputPath, format);
        WriteObject(outputPath);
    }
}

[Cmdlet(VerbsData.Import, "AhtolaSqliteTable", SupportsShouldProcess = true)]
[OutputType(typeof(int))]
public sealed class ImportPSSqliteTableCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    [Alias("SqliteConnection")]
    public SqliteConnection Connection { get; set; } = null!;

    [Parameter(Mandatory = true)]
    [Alias("TableName")]
    public string Table { get; set; } = string.Empty;

    [Parameter(Mandatory = true)]
    public string Path { get; set; } = string.Empty;

    [Parameter]
    [ValidateSet("Json", "Csv")]
    public string? Format { get; set; }

    [Parameter]
    [ValidateRange(1, 100_000)]
    public int BatchSize { get; set; } = 1_000;

    [Parameter]
    public SqliteTransaction? Transaction { get; set; }

    protected override void ProcessRecord()
    {
        var inputPath = System.IO.Path.GetFullPath(ExpandString(Path));
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException($"Import file not found: {inputPath}", inputPath);
        }

        var format = TableInterchange.ResolveFormat(inputPath, Format);
        if (!ShouldProcess(Table, $"Import {format} data from {inputPath}"))
        {
            return;
        }

        WriteObject(BulkCopyExecutor.Execute(
            Connection,
            Table,
            TableInterchange.Import(inputPath, format),
            BatchSize,
            Transaction));
    }
}

internal static class PowerShellRow
{
    public static OrderedDictionary ToDictionary(object input)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input), "Bulk-copy rows cannot be null.");
        }

        var result = new OrderedDictionary(StringComparer.OrdinalIgnoreCase);
        if (input is DataRow row)
        {
            foreach (DataColumn column in row.Table.Columns)
            {
                result[column.ColumnName] = row[column] is DBNull ? null : row[column];
            }

            return result;
        }

        if (input is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                Add(result, entry.Key, entry.Value);
            }

            return result;
        }

        var psObject = PSObject.AsPSObject(input);
        foreach (var property in psObject.Properties)
        {
            if (property.IsGettable)
            {
                Add(result, property.Name, property.Value);
            }
        }

        if (result.Count == 0)
        {
            throw new ArgumentException("Bulk-copy rows must have at least one readable property.", nameof(input));
        }

        return result;
    }

    private static void Add(OrderedDictionary values, object? key, object? value)
    {
        var name = Convert.ToString(key, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Bulk-copy row property names cannot be empty.");
        }

        if (values.Contains(name))
        {
            throw new ArgumentException($"Bulk-copy row contains duplicate property '{name}'.");
        }

        values.Add(name, value);
    }
}

internal static class BulkCopyExecutor
{
    public static int Execute(
        SqliteConnection connection,
        string tableName,
        IReadOnlyList<OrderedDictionary> rows,
        int batchSize,
        SqliteTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        if (rows.Count == 0)
        {
            return 0;
        }

        var columns = rows[0].Keys.Cast<string>().ToArray();
        if (columns.Length == 0)
        {
            throw new ArgumentException("Bulk-copy rows must have at least one column.", nameof(rows));
        }

        foreach (var row in rows)
        {
            ValidateRowColumns(row, columns);
        }

        if (connection.State != ConnectionState.Open)
        {
            connection.Open();
        }

        var ownsTransaction = transaction is null;
        var activeTransaction = transaction ?? connection.BeginTransaction();
        if (transaction is not null && !ReferenceEquals(transaction.Connection, connection))
        {
            throw new ArgumentException("Transaction must belong to SqliteConnection.", nameof(transaction));
        }

        var savepointName = ownsTransaction ? null : $"ps_bulk_{Guid.NewGuid():N}";
        try
        {
            if (savepointName is not null)
            {
                activeTransaction.Save(savepointName);
            }

            using var command = connection.CreateCommand();
            command.Transaction = activeTransaction;
            command.CommandText =
                $"INSERT INTO {SqlIdentifier.QuoteQualified(tableName)} ({string.Join(", ", columns.Select(SqlIdentifier.Quote))}) " +
                $"VALUES ({string.Join(", ", columns.Select((_, index) => "$value" + index))});";
            for (var index = 0; index < rows.Count; index++)
            {
                command.Parameters.Clear();
                foreach (var column in columns.Select((name, columnIndex) => (name, columnIndex)))
                {
                    command.Parameters.AddWithValue("$value" + column.columnIndex, rows[index][column.name] ?? DBNull.Value);
                }

                _ = command.ExecuteNonQuery();
                if ((index + 1) % batchSize == 0 && savepointName is not null)
                {
                    activeTransaction.Release(savepointName);
                    activeTransaction.Save(savepointName);
                }
            }

            if (ownsTransaction)
            {
                activeTransaction.Commit();
            }
            else if (savepointName is not null)
            {
                activeTransaction.Release(savepointName);
            }

            return rows.Count;
        }
        catch
        {
            if (ownsTransaction)
            {
                activeTransaction.Rollback();
            }
            else if (savepointName is not null)
            {
                activeTransaction.Rollback(savepointName);
                activeTransaction.Release(savepointName);
            }

            throw;
        }
    }

    private static void ValidateRowColumns(OrderedDictionary row, IReadOnlyCollection<string> columns)
    {
        foreach (var key in row.Keys.Cast<string>())
        {
            if (!columns.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Bulk-copy row has unexpected column '{key}'.");
            }
        }
    }
}

internal static class TableInterchange
{
    public static string ResolveFormat(string path, string? format)
    {
        if (!string.IsNullOrWhiteSpace(format))
        {
            return format;
        }

        return System.IO.Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".JSON" => "Json",
            ".CSV" => "Csv",
            _ => throw new ArgumentException(
                "Format is required when Path does not end in .json or .csv.",
                nameof(format))
        };
    }

    public static void Export(DataTable table, string path, string format)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory());
        switch (format.ToUpperInvariant())
        {
            case "JSON":
                ExportJson(table, path);
                break;
            case "CSV":
                ExportCsv(table, path);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    public static IReadOnlyList<OrderedDictionary> Import(string path, string format)
    {
        return format.ToUpperInvariant() switch
        {
            "JSON" => ImportJson(path),
            "CSV" => ImportCsv(path),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
    }

    private static void ExportJson(DataTable table, string path)
    {
        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartArray();
        foreach (DataRow row in table.Rows)
        {
            writer.WriteStartObject();
            foreach (DataColumn column in table.Columns)
            {
                writer.WritePropertyName(column.ColumnName);
                WriteJsonValue(writer, row[column]);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static IReadOnlyList<OrderedDictionary> ImportJson(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("JSON table imports must contain a top-level array.");
        }

        var rows = new List<OrderedDictionary>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("JSON table imports must contain only object rows.");
            }

            var row = new OrderedDictionary(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                row.Add(property.Name, ReadJsonValue(property.Value));
            }

            rows.Add(row);
        }

        return rows;
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, object value)
    {
        if (value is null or DBNull)
        {
            writer.WriteNullValue();
            return;
        }

        switch (value)
        {
            case byte[] bytes:
                writer.WriteStartObject();
                writer.WriteString("$binary", Convert.ToBase64String(bytes));
                writer.WriteEndObject();
                break;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                break;
            case byte number:
                writer.WriteNumberValue(number);
                break;
            case short number:
                writer.WriteNumberValue(number);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case DateTime dateTime:
                writer.WriteStringValue(dateTime);
                break;
            case DateTimeOffset dateTimeOffset:
                writer.WriteStringValue(dateTimeOffset);
                break;
            case Guid guid:
                writer.WriteStringValue(guid);
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    private static object? ReadJsonValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.Object => ReadJsonObject(value),
            _ => throw new InvalidDataException("JSON table values must be primitives or a $binary object.")
        };
    }

    private static byte[] ReadJsonObject(JsonElement value)
    {
        if (value.TryGetProperty("$binary", out var binary)
            && binary.ValueKind == JsonValueKind.String
            && value.EnumerateObject().Count() == 1)
        {
            return Convert.FromBase64String(binary.GetString()!);
        }

        throw new InvalidDataException("JSON object values must be a single $binary property.");
    }

    private static void ExportCsv(DataTable table, string path)
    {
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        WriteCsvRecord(writer, table.Columns.Cast<DataColumn>().Select(column => column.ColumnName));
        foreach (DataRow row in table.Rows)
        {
            WriteCsvRecord(writer, table.Columns.Cast<DataColumn>().Select(column => ToCsvValue(row[column])));
        }
    }

    private static IReadOnlyList<OrderedDictionary> ImportCsv(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var records = ReadCsvRecords(reader).ToArray();
        if (records.Length == 0)
        {
            return Array.Empty<OrderedDictionary>();
        }

        var headers = records[0];
        if (headers.Length == 0 || headers.Any(string.IsNullOrWhiteSpace) || headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Length)
        {
            throw new InvalidDataException("CSV table headers must be non-empty and unique.");
        }

        var rows = new List<OrderedDictionary>();
        foreach (var record in records.Skip(1))
        {
            if (record.Length != headers.Length)
            {
                throw new InvalidDataException("CSV row has a different number of values than the header.");
            }

            var row = new OrderedDictionary(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < headers.Length; index++)
            {
                row.Add(headers[index], FromCsvValue(record[index]));
            }

            rows.Add(row);
        }

        return rows;
    }

    private static string ToCsvValue(object value)
    {
        if (value is null or DBNull)
        {
            return "@null";
        }

        if (value is byte[] bytes)
        {
            return "@binary:" + Convert.ToBase64String(bytes);
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return text.StartsWith("@", StringComparison.Ordinal) ? "@" + text : text;
    }

    private static object? FromCsvValue(string value)
    {
        if (value == "@null")
        {
            return null;
        }

        if (value.StartsWith("@binary:", StringComparison.Ordinal))
        {
            return Convert.FromBase64String(value[8..]);
        }

        return value.StartsWith("@@", StringComparison.Ordinal) ? value[1..] : value;
    }

    private static void WriteCsvRecord(TextWriter writer, IEnumerable<string> values)
    {
        writer.WriteLine(string.Join(",", values.Select(EscapeCsv)));
    }

    private static string EscapeCsv(string value)
    {
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    private static IEnumerable<string[]> ReadCsvRecords(TextReader reader)
    {
        var values = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        while (true)
        {
            var next = reader.Read();
            if (next < 0)
            {
                if (quoted)
                {
                    throw new InvalidDataException("CSV input has an unterminated quoted field.");
                }

                if (field.Length > 0 || values.Count > 0)
                {
                    values.Add(field.ToString());
                    yield return values.ToArray();
                }

                yield break;
            }

            var character = (char)next;
            if (quoted)
            {
                if (character == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        _ = reader.Read();
                        field.Append('"');
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(character);
                }

                continue;
            }

            if (character == '"')
            {
                if (field.Length != 0)
                {
                    throw new InvalidDataException("CSV quotes must begin a field.");
                }

                quoted = true;
            }
            else if (character == ',')
            {
                values.Add(field.ToString());
                field.Clear();
            }
            else if (character == '\r' || character == '\n')
            {
                if (character == '\r' && reader.Peek() == '\n')
                {
                    _ = reader.Read();
                }

                values.Add(field.ToString());
                field.Clear();
                yield return values.ToArray();
                values.Clear();
            }
            else
            {
                field.Append(character);
            }
        }
    }
}
