using System.Data;
using System.Data.Common;

namespace Ahtola;

/// <summary>
/// Provides data for the <see cref="AhtolaDataAdapter.RowUpdating"/> event.
/// </summary>
public sealed class AhtolaRowUpdatingEventArgs(
    DataRow dataRow,
    IDbCommand? command,
    StatementType statementType,
    DataTableMapping tableMapping)
    : RowUpdatingEventArgs(dataRow, command, statementType, tableMapping)
{
    /// <summary>
    /// Gets or sets the command executed for the row being updated.
    /// </summary>
    public new DbCommand? Command
    {
        get => (DbCommand?)base.Command;
        set => base.Command = value;
    }
}

/// <summary>
/// Provides data for the <see cref="AhtolaDataAdapter.RowUpdated"/> event.
/// </summary>
public sealed class AhtolaRowUpdatedEventArgs(
    DataRow dataRow,
    IDbCommand? command,
    StatementType statementType,
    DataTableMapping tableMapping)
    : RowUpdatedEventArgs(dataRow, command, statementType, tableMapping)
{
    /// <summary>
    /// Gets the command executed for the row that was updated.
    /// </summary>
    public new DbCommand? Command => (DbCommand?)base.Command;
}
