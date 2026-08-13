using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Security;
using System.Text;
using System.Text.Json;
using Ahtola.Data.Sqlite;

namespace Ahtola.PSSqlite;

public abstract class PSSqliteCmdlet : PSCmdlet
{
    protected static bool IsSingleResult(object? value)
    {
        return value is null or DataTable or DataSet or IDataReader or IDictionary or string;
    }

    protected void WriteResult(object? value)
    {
        if (value is null)
        {
            return;
        }

        if (IsSingleResult(value))
        {
            WriteObject(value);
            return;
        }

        if (value is IEnumerable enumerable)
        {
            WriteObject(enumerable, true);
            return;
        }

        WriteObject(value);
    }

    protected string ExpandString(string value)
    {
        return SessionState.InvokeCommand.ExpandString(value);
    }

    protected static void DisposeOwnedConnection(SqliteConnection connection)
    {
        if (connection.State == ConnectionState.Open)
        {
            connection.Close();
        }

        SqliteConnection.ClearPool(connection);
        connection.Dispose();
    }

    protected static void OpenIfNeeded(SqliteConnection connection)
    {
        if (connection.State != ConnectionState.Open)
        {
            connection.Open();
        }
    }
}

[Cmdlet(VerbsCommon.New, "AhtolaSqliteConnection")]
[OutputType(typeof(SqliteConnection))]
public sealed class NewPSSqliteConnectionCommand : PSSqliteCmdlet
{
    [Parameter(ParameterSetName = "byConnectionString")]
    public string ConnectionString { get; set; } = "Data Source=:memory:;Cache=Shared;";

    [Parameter(ParameterSetName = "byDatabasePath")]
    public string DatabasePath { get; set; } = Directory.GetCurrentDirectory();

    [Parameter(Mandatory = true, ParameterSetName = "byDatabasePath")]
    public string? DatabaseFile { get; set; }

    [Parameter]
    public SwitchParameter ReadOnly { get; set; }

    protected override void ProcessRecord()
    {
        var connection = ParameterSetName == "byDatabasePath"
            ? ConnectionFactory.Create(ExpandString(DatabasePath), ExpandString(DatabaseFile!))
            : ConnectionFactory.Create(ExpandString(ConnectionString));
        if (ReadOnly.IsPresent)
        {
            var builder = new SqliteConnectionStringBuilder(connection.ConnectionString)
            {
                Mode = SqliteOpenMode.ReadOnly
            };
            connection.ConnectionString = builder.ToString();
        }

        connection.Open();
        WriteObject(connection);
    }
}

[Cmdlet(VerbsLifecycle.Invoke, "AhtolaSqliteQuery")]
public sealed class InvokePSSqliteQueryCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    [Alias("SqliteConnection")]
    public SqliteConnection Connection { get; set; } = null!;

    [Parameter(Mandatory = true)]
    [Alias("Query")]
    public string CommandText { get; set; } = string.Empty;

    [Parameter]
    [ValidateSet("DataTable", "DetachedDataReader", "DataReader", "DataSet", "OrderedDictionary", "PSCustomObject", "Scalar", "NonQuery")]
    public string As { get; set; } = "PSCustomObject";

    [Parameter]
    public Type? CastAs { get; set; }

    [Parameter]
    public IDictionary? Parameters { get; set; }

    [Parameter]
    public int CommandTimeout { get; set; } = 30;

    [Parameter]
    public SqliteTransaction? Transaction { get; set; }

    protected override void ProcessRecord()
    {
        var result = QueryExecutor.Execute(
            Connection,
            CommandText,
            Parameters,
            new QueryOptions
            {
                OutputFormat = As,
                CommandTimeout = CommandTimeout,
                Transaction = Transaction
            });

        if (CastAs is not null && result is not null)
        {
            result = LanguagePrimitives.ConvertTo(result, CastAs);
        }

        WriteResult(result);
    }
}

[Cmdlet(VerbsCommon.Get, "AhtolaSqliteRow")]
public sealed class GetPSSqliteRowCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true)]
    [Alias("SqliteDBConfig")]
    public SQLiteDBConfig Configuration { get; set; } = null!;

    [Parameter(Mandatory = true)]
    [Alias("TableName")]
    public string Table { get; set; } = string.Empty;

    [Parameter]
    [Alias("ClauseData")]
    public IDictionary? Where { get; set; }

    [Parameter(ValueFromPipeline = true)]
    [ValidateNotNull]
    [Alias("SqliteConnection")]
    public SqliteConnection? Connection { get; set; }

    [Parameter]
    public SwitchParameter CaseSensitive { get; set; }

    [Parameter]
    public SqliteTransaction? Transaction { get; set; }

    [Parameter(DontShow = true)]
    [ValidateSet("DataTable", "DataReader", "DataSet", "OrderedDictionary", "PSCustomObject")]
    public string As { get; set; } = "PSCustomObject";

    protected override void ProcessRecord()
    {
        var ownsConnection = Connection is null;
        var connection = Connection ?? ConnectionFactory.Create(Configuration.ConnectionString!);
        try
        {
            var result = CrudSqlBuilder.ExecuteSelect(
                Configuration,
                Table,
                Where,
                connection,
                As,
                CaseSensitive.IsPresent,
                Transaction,
                message => WriteWarning(message + $" for table or view '{Table}'."));
            WriteResult(result);
        }
        finally
        {
            if (ownsConnection)
            {
                DisposeOwnedConnection(connection);
            }
        }
    }
}

[Cmdlet(VerbsCommon.New, "AhtolaSqliteRow", SupportsShouldProcess = true)]
public sealed class NewPSSqliteRowCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true)]
    [Alias("SqliteDBConfig")]
    public SQLiteDBConfig Configuration { get; set; } = null!;

    [Parameter(Mandatory = true)]
    [Alias("TableName")]
    public string Table { get; set; } = string.Empty;

    [Parameter(Mandatory = true)]
    [Alias("RowData")]
    public IDictionary Values { get; set; } = null!;

    [Parameter(ValueFromPipeline = true)]
    [Alias("SqliteConnection")]
    public SqliteConnection? Connection { get; set; }

    [Parameter]
    public SqliteTransaction? Transaction { get; set; }

    protected override void ProcessRecord()
    {
        if (!ShouldProcess(Table, "Insert row"))
        {
            return;
        }

        var ownsConnection = Connection is null;
        var connection = Connection ?? ConnectionFactory.Create(Configuration.ConnectionString!);
        try
        {
            var result = CrudSqlBuilder.ExecuteInsert(
                Configuration,
                Table,
                Values,
                connection,
                Transaction,
                message => WriteWarning(message + $" for table '{Table}'."));
            WriteResult(result);
        }
        finally
        {
            if (ownsConnection)
            {
                DisposeOwnedConnection(connection);
            }
        }
    }
}

[Cmdlet(VerbsCommon.Set, "AhtolaSqliteRow", SupportsShouldProcess = true)]
public sealed class SetPSSqliteRowCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true)]
    [Alias("SqliteDBConfig")]
    public SQLiteDBConfig Configuration { get; set; } = null!;

    [Parameter(Mandatory = true)]
    [Alias("TableName")]
    public string Table { get; set; } = string.Empty;

    [Parameter(Mandatory = true)]
    [Alias("RowData")]
    public IDictionary Values { get; set; } = null!;

    [Parameter]
    [Alias("ClauseData")]
    public IDictionary? Where { get; set; }

    [Parameter]
    public SwitchParameter CaseSensitive { get; set; }

    [Parameter(ValueFromPipeline = true)]
    [Alias("SqliteConnection")]
    public SqliteConnection? Connection { get; set; }

    [Parameter]
    [ValidateSet("UPDATE", "UPSERT")]
    public string OnConflict { get; set; } = "UPDATE";

    [Parameter]
    public SqliteTransaction? Transaction { get; set; }

    protected override void ProcessRecord()
    {
        if (!ShouldProcess(Table, $"{OnConflict.ToLowerInvariant()} row"))
        {
            return;
        }

        var ownsConnection = Connection is null;
        var connection = Connection ?? ConnectionFactory.Create(Configuration.ConnectionString!);
        try
        {
            WriteObject(CrudSqlBuilder.ExecuteUpdate(
                Configuration,
                Table,
                Values,
                Where,
                connection,
                CaseSensitive.IsPresent,
                OnConflict,
                Transaction,
                message => WriteWarning(message + $" for table '{Table}'.")));
        }
        finally
        {
            if (ownsConnection)
            {
                DisposeOwnedConnection(connection);
            }
        }
    }
}

[Cmdlet(VerbsCommon.Remove, "AhtolaSqliteRow", SupportsShouldProcess = true)]
public sealed class RemovePSSqliteRowCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true)]
    [Alias("SqliteDBConfig")]
    public SQLiteDBConfig Configuration { get; set; } = null!;

    [Parameter(Mandatory = true)]
    [Alias("TableName")]
    public string Table { get; set; } = string.Empty;

    [Parameter]
    [Alias("ClauseData")]
    public IDictionary? Where { get; set; }

    [Parameter]
    public SwitchParameter CaseSensitive { get; set; }

    [Parameter(ValueFromPipeline = true)]
    [Alias("SqliteConnection")]
    public SqliteConnection? Connection { get; set; }

    [Parameter]
    public SqliteTransaction? Transaction { get; set; }

    protected override void ProcessRecord()
    {
        if (!ShouldProcess(Table, "Delete row"))
        {
            return;
        }

        var ownsConnection = Connection is null;
        var connection = Connection ?? ConnectionFactory.Create(Configuration.ConnectionString!);
        try
        {
            WriteObject(CrudSqlBuilder.ExecuteDelete(
                Configuration,
                Table,
                Where,
                connection,
                CaseSensitive.IsPresent,
                Transaction,
                message => WriteWarning(message + $" for table '{Table}'.")));
        }
        finally
        {
            if (ownsConnection)
            {
                DisposeOwnedConnection(connection);
            }
        }
    }
}

[Cmdlet(VerbsCommon.Close, "AhtolaSqliteConnection", SupportsShouldProcess = true)]
public sealed class ClosePSSqliteConnectionCommand : PSSqliteCmdlet
{
    [Parameter(ValueFromPipeline = true)]
    [Alias("SqliteConnection")]
    public SqliteConnection? Connection { get; set; }

    [Parameter]
    public SwitchParameter ClearPool { get; set; }

    [Parameter]
    public SwitchParameter AllPools { get; set; }

    protected override void ProcessRecord()
    {
        if (Connection is null)
        {
            if (ShouldProcess("all Ahtola SQLite connection pools", "Clear"))
            {
                Ahtola.Data.Sqlite.SqliteConnection.ClearAllPools();
            }

            return;
        }

        if (!ShouldProcess(Connection.DataSource, "Close and dispose connection"))
        {
            return;
        }

        Connection.Close();
        if (ClearPool.IsPresent)
        {
            Ahtola.Data.Sqlite.SqliteConnection.ClearPool(Connection);
        }

        Connection.Dispose();
        if (AllPools.IsPresent)
        {
            Ahtola.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }
}

[Cmdlet(VerbsCommon.Get, "AhtolaSqliteDatabaseMetadata")]
public sealed class GetPSSqliteDBMetadataCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    [Alias("SqliteConnection")]
    public SqliteConnection Connection { get; set; } = null!;

    [Parameter]
    public string[] MetadataKey { get; set; } = new[] { "*" };

    protected override void ProcessRecord()
    {
        var connectionString = Connection.ConnectionString;
        var metadata = MetadataStore.Get(connectionString, MetadataKey);
        if (metadata is not null)
        {
            WriteObject(metadata);
        }
    }
}

[Cmdlet("Compare", "AhtolaSqliteDatabaseVersion")]
public sealed class ComparePSSqliteDBVersionCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    [Alias("DatabaseConfig", "SqliteDBConfig")]
    public SQLiteDBConfig Configuration { get; set; } = null!;

    [Parameter]
    public string? ExpectedVersion { get; set; }

    protected override void ProcessRecord()
    {
        WriteObject(DatabaseVersion.Compare(Configuration, ExpectedVersion ?? Configuration.Version));
    }
}
