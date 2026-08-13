using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Ahtola;

public sealed class AhtolaBatchCommand : DbBatchCommand
{
    private readonly AhtolaParameterCollection _parameters = new();
    private string _commandText = "";
    private CommandType _commandType = CommandType.Text;
    private int _recordsAffected = -1;

    public AhtolaBatchCommand()
    {
    }

    public AhtolaBatchCommand(string commandText)
    {
        CommandText = commandText;
    }

    [AllowNull]
    public override string CommandText
    {
        get => _commandText;
        set => _commandText = value ?? "";
    }

    public override CommandType CommandType
    {
        get => _commandType;
        set
        {
            if (value != CommandType.Text)
                throw new NotSupportedException("AhtolaBatchCommand only supports CommandType.Text.");

            _commandType = value;
        }
    }

    protected override DbParameterCollection DbParameterCollection => _parameters;

    public new AhtolaParameterCollection Parameters => _parameters;

    public override int RecordsAffected => _recordsAffected;

    /// <summary>
    /// Gets or sets the server-side condition for this command when the batch runs over a remote
    /// connection. Local batches reject conditional commands instead of ignoring the condition.
    /// </summary>
    public AhtolaRemoteBatchCondition? RemoteCondition { get; set; }

    public override bool CanCreateParameter => true;

    public override DbParameter CreateParameter()
    {
        return new AhtolaParameter();
    }

    internal void SetRecordsAffected(int recordsAffected)
    {
        _recordsAffected = recordsAffected;
    }
}
