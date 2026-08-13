using System.Data;
using System.Data.Common;

namespace Ahtola;

/// <summary>
/// Generates single-table <c>INSERT</c>, <c>UPDATE</c> and <c>DELETE</c> statements for a
/// <see cref="AhtolaDataAdapter"/> from the schema of its <c>SelectCommand</c>.
/// </summary>
public sealed class AhtolaCommandBuilder : DbCommandBuilder
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AhtolaCommandBuilder"/> class.
    /// </summary>
    public AhtolaCommandBuilder()
    {
        QuotePrefix = "\"";
        QuoteSuffix = "\"";
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AhtolaCommandBuilder"/> class for the
    /// specified adapter.
    /// </summary>
    /// <param name="adapter">The adapter the generated commands are attached to.</param>
    public AhtolaCommandBuilder(AhtolaDataAdapter adapter)
        : this()
    {
        DataAdapter = adapter;
    }

    /// <summary>
    /// Gets or sets the adapter the generated commands are attached to.
    /// </summary>
    public new AhtolaDataAdapter? DataAdapter
    {
        get => (AhtolaDataAdapter?)base.DataAdapter;
        set => base.DataAdapter = value;
    }

    /// <inheritdoc />
    public override string QuoteIdentifier(string unquotedIdentifier)
    {
        ArgumentNullException.ThrowIfNull(unquotedIdentifier);
        return QuotePrefix + unquotedIdentifier.Replace(QuoteSuffix, QuoteSuffix + QuoteSuffix, StringComparison.Ordinal) + QuoteSuffix;
    }

    /// <inheritdoc />
    public override string UnquoteIdentifier(string quotedIdentifier)
    {
        ArgumentNullException.ThrowIfNull(quotedIdentifier);
        if (!quotedIdentifier.StartsWith(QuotePrefix, StringComparison.Ordinal)
            || !quotedIdentifier.EndsWith(QuoteSuffix, StringComparison.Ordinal)
            || quotedIdentifier.Length < QuotePrefix.Length + QuoteSuffix.Length)
        {
            return quotedIdentifier;
        }

        return quotedIdentifier[QuotePrefix.Length..^QuoteSuffix.Length]
            .Replace(QuoteSuffix + QuoteSuffix, QuoteSuffix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Gets the automatically generated <c>INSERT</c> statement.
    /// </summary>
    public new DbCommand GetInsertCommand() => base.GetInsertCommand();

    /// <summary>
    /// Gets the automatically generated <c>UPDATE</c> statement.
    /// </summary>
    public new DbCommand GetUpdateCommand() => base.GetUpdateCommand();

    /// <summary>
    /// Gets the automatically generated <c>DELETE</c> statement.
    /// </summary>
    public new DbCommand GetDeleteCommand() => base.GetDeleteCommand();

    /// <inheritdoc />
    protected override void ApplyParameterInfo(
        DbParameter parameter,
        DataRow row,
        StatementType statementType,
        bool whereClause)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentNullException.ThrowIfNull(row);

        // DbCommandBuilder resets Size to 0 before this call. A provider that honours Size
        // would then truncate every bound string to the empty string, so the size published
        // by the schema table has to be restored here.
        parameter.Size = row[SchemaTableColumn.ColumnSize] is int size ? size : -1;
        if (row[SchemaTableColumn.DataType] is Type dataType)
            parameter.DbType = GetDbType(dataType);
    }

    /// <inheritdoc />
    protected override string GetParameterName(int parameterOrdinal)
        => "@p" + parameterOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <inheritdoc />
    protected override string GetParameterName(string parameterName)
    {
        ArgumentNullException.ThrowIfNull(parameterName);
        return "@" + parameterName;
    }

    /// <inheritdoc />
    protected override string GetParameterPlaceholder(int parameterOrdinal)
        => GetParameterName(parameterOrdinal);

    /// <inheritdoc />
    protected override void SetRowUpdatingHandler(DbDataAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        if (adapter is not AhtolaDataAdapter ahtolaAdapter)
        {
            throw new ArgumentException(
                $"{nameof(AhtolaCommandBuilder)} requires a {nameof(AhtolaDataAdapter)}.",
                nameof(adapter));
        }

        if (ReferenceEquals(adapter, DataAdapter))
            ahtolaAdapter.RowUpdating -= OnRowUpdating;
        else
            ahtolaAdapter.RowUpdating += OnRowUpdating;
    }

    private void OnRowUpdating(object? sender, AhtolaRowUpdatingEventArgs args) => RowUpdatingHandler(args);

    private static DbType GetDbType(Type type)
    {
        if (type == typeof(long))
            return DbType.Int64;
        if (type == typeof(int))
            return DbType.Int32;
        if (type == typeof(short))
            return DbType.Int16;
        if (type == typeof(byte))
            return DbType.Byte;
        if (type == typeof(bool))
            return DbType.Boolean;
        if (type == typeof(double))
            return DbType.Double;
        if (type == typeof(float))
            return DbType.Single;
        if (type == typeof(decimal))
            return DbType.Decimal;
        if (type == typeof(byte[]))
            return DbType.Binary;
        if (type == typeof(Guid))
            return DbType.Guid;
        if (type == typeof(DateTime))
            return DbType.DateTime;
        if (type == typeof(DateTimeOffset))
            return DbType.DateTimeOffset;

        return DbType.String;
    }
}
