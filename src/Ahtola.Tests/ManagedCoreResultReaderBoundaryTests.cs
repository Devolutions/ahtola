using System.Data;
using System.Data.Common;
using AwesomeAssertions;
using Ahtola;
using Ahtola.Core;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public class ManagedCoreResultReaderBoundaryTests
{
    [Test]
    public void AhtolaManagedReaderUsesCoreResultRowsAndMetadata()
    {
        var statement = new CoreOnlyResultStatementAdapter();
        using var connection = new AhtolaConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        using var command = new AhtolaCommand { Connection = connection };
        using var reader = new AhtolaDataReader(command, null, statement, CommandBehavior.Default);

        reader.FieldCount.Should().Be(4);
        reader.GetName(1).Should().Be("text_value");
        reader.Read().Should().BeTrue();
        AssertReaderValues(reader);

        statement.LegacyResultAccessed.Should().BeFalse();
    }

    [Test]
    public void SqliteManagedReaderUsesCoreResultRowsAndMetadata()
    {
        var statement = new CoreOnlyResultStatementAdapter();
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        using var command = new SqliteCommand { Connection = connection };
        using var reader = new SqliteDataReader(
            command,
            SqliteStatementAdapter.FromManaged(statement),
            "SELECT 7 AS id, 'hello' AS text_value, X'010203' AS blob_value, NULL AS null_value",
            [],
            recordsAffected: 0,
            CommandBehavior.Default,
            static () => { });

        reader.FieldCount.Should().Be(4);
        reader.GetName(2).Should().Be("blob_value");
        reader.Read().Should().BeTrue();
        AssertReaderValues(reader);

        var schema = reader.GetSchemaTable();
        schema.Rows[0]["ColumnName"].Should().Be("id");
        schema.Rows[0]["DataTypeName"].Should().Be("INTEGER");
        schema.Rows[2]["DataType"].Should().Be(typeof(byte[]));

        statement.LegacyResultAccessed.Should().BeFalse();
    }

    private static void AssertReaderValues(IDataRecord reader)
    {
        reader.GetInt64(0).Should().Be(7);
        reader.GetString(1).Should().Be("hello");
        reader.GetDataTypeName(0).Should().Be("INTEGER");
        reader.GetFieldType(2).Should().Be(typeof(byte[]));
        reader.GetBytes(2, 0, null, 0, 0).Should().Be(3);

        var bytes = new byte[2];
        reader.GetBytes(2, 1, bytes, 0, bytes.Length).Should().Be(2);
        bytes.Should().Equal(2, 3);

        var dataReader = (DbDataReader)reader;
        using var stream = dataReader.GetStream(2);
        stream.ReadByte().Should().Be(1);
        using var textReader = dataReader.GetTextReader(1);
        textReader.ReadToEnd().Should().Be("hello");

        reader.IsDBNull(3).Should().BeTrue();
        reader.GetValue(3).Should().Be(DBNull.Value);
    }

    private sealed class CoreOnlyResultStatementAdapter : IManagedStatementAdapter
    {
        private static readonly SqlValue[] Values =
        [
            SqlValue.Integer(7),
            SqlValue.Text("hello"),
            SqlValue.Blob([1, 2, 3]),
            SqlValue.Null,
        ];

        private static readonly ManagedResultColumn[] Columns =
        [
            new("id"),
            new("text_value"),
            new("blob_value"),
            new("null_value"),
        ];

        private bool _stepped;

        public bool LegacyResultAccessed { get; private set; }

        public int ParameterCount => 0;

        public int RowsAffected => 0;

        public void Bind(int index, SqlValue value) => throw new NotSupportedException();

        public int GetParameterIndex(string name) => 0;

        public StatementStepResult Step()
        {
            if (_stepped)
                return StatementStepResult.Done;

            _stepped = true;
            return StatementStepResult.Row;
        }

        public bool HasRows() => true;

        public void Reset() => _stepped = false;

        public void ClearBindings() => throw new NotSupportedException();

        public SqlValue GetValue(int ordinal) => LegacyResult<SqlValue>();

        public string GetColumnName(int ordinal) => LegacyResult<string>();

        public int GetColumnCount() => LegacyResult<int>();

        public ManagedResultValue GetResultValue(int ordinal) => new(Values[ordinal]);

        public ManagedResultColumn GetResultColumn(int ordinal) => Columns[ordinal];

        public int GetResultColumnCount() => Columns.Length;

        public string? GetParameterName(int index) => null;

        public void Dispose()
        {
        }

        private T LegacyResult<T>()
        {
            LegacyResultAccessed = true;
            throw new AssertionException("Managed readers must use the Core result contracts.");
        }
    }
}
