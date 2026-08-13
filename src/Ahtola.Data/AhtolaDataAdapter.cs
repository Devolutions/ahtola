using System.Data;
using System.Data.Common;

namespace Ahtola;

/// <summary>
/// Fills a <see cref="DataSet"/> from a Ahtola database and writes changes back to it.
/// </summary>
/// <remarks>
/// The adapter deliberately types its commands as <see cref="DbCommand"/> so that both
/// ADO.NET surfaces in this package - <see cref="AhtolaConnection"/> and the
/// <c>Ahtola.Data.Sqlite</c> facade - can use one adapter implementation.
/// </remarks>
public sealed class AhtolaDataAdapter : DbDataAdapter
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AhtolaDataAdapter"/> class.
    /// </summary>
    public AhtolaDataAdapter()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AhtolaDataAdapter"/> class using the
    /// specified select command.
    /// </summary>
    /// <param name="selectCommand">The command used to fill the dataset.</param>
    public AhtolaDataAdapter(DbCommand selectCommand)
    {
        SelectCommand = selectCommand;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AhtolaDataAdapter"/> class using the
    /// specified select statement and connection.
    /// </summary>
    /// <param name="selectCommandText">The SQL statement used to fill the dataset.</param>
    /// <param name="connection">The connection the statement runs on.</param>
    public AhtolaDataAdapter(string selectCommandText, DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var command = connection.CreateCommand();
        command.CommandText = selectCommandText;
        SelectCommand = command;
    }

    /// <summary>
    /// Occurs before a command is executed against the data source for a changed row.
    /// </summary>
    public event EventHandler<AhtolaRowUpdatingEventArgs>? RowUpdating;

    /// <summary>
    /// Occurs after a command is executed against the data source for a changed row.
    /// </summary>
    public event EventHandler<AhtolaRowUpdatedEventArgs>? RowUpdated;

    /// <inheritdoc />
    protected override RowUpdatingEventArgs CreateRowUpdatingEvent(
        DataRow dataRow,
        IDbCommand? command,
        StatementType statementType,
        DataTableMapping tableMapping)
        => new AhtolaRowUpdatingEventArgs(dataRow, command, statementType, tableMapping);

    /// <inheritdoc />
    protected override RowUpdatedEventArgs CreateRowUpdatedEvent(
        DataRow dataRow,
        IDbCommand? command,
        StatementType statementType,
        DataTableMapping tableMapping)
        => new AhtolaRowUpdatedEventArgs(dataRow, command, statementType, tableMapping);

    /// <inheritdoc />
    protected override void OnRowUpdating(RowUpdatingEventArgs value)
        => RowUpdating?.Invoke(this, (AhtolaRowUpdatingEventArgs)value);

    /// <inheritdoc />
    protected override void OnRowUpdated(RowUpdatedEventArgs value)
        => RowUpdated?.Invoke(this, (AhtolaRowUpdatedEventArgs)value);
}
