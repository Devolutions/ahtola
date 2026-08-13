using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Ahtola.Data.Sqlite;

public sealed class SqliteBatchCommand : DbBatchCommand
{
    private readonly SqliteParameterCollection _parameters = new();
    private string _commandText = string.Empty;
    private CommandType _commandType = CommandType.Text;
    private int _recordsAffected = -1;

    public SqliteBatchCommand()
    {
    }

    public SqliteBatchCommand(string? commandText)
    {
        CommandText = commandText;
    }

    [AllowNull]
    public override string CommandText
    {
        get => _commandText;
        set => _commandText = value ?? string.Empty;
    }

    public override CommandType CommandType
    {
        get => _commandType;
        set
        {
            if (value != CommandType.Text)
                throw new ArgumentException(Properties.Resources.InvalidCommandType(value));

            _commandType = value;
        }
    }

    protected override DbParameterCollection DbParameterCollection => _parameters;

    public new SqliteParameterCollection Parameters => _parameters;

    public override int RecordsAffected => _recordsAffected;

    public override bool CanCreateParameter => true;

    public override DbParameter CreateParameter() => new SqliteParameter();

    internal void SetRecordsAffected(int recordsAffected)
    {
        _recordsAffected = recordsAffected;
    }
}
