using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Management.Automation;
using System.Net;
using System.Security;
using Ahtola.Data.Sqlite;

namespace Ahtola.PSSqlite;

[Cmdlet(VerbsDiagnostic.Test, "AhtolaSqliteConnection")]
[OutputType(typeof(bool))]
public sealed class TestPSSqliteConnectionCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    [Alias("SqliteConnection")]
    public DbConnection Connection { get; set; } = null!;

    protected override void ProcessRecord()
    {
        try
        {
            OpenIfNeeded(Connection);
            using var command = Connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            _ = command.ExecuteScalar();
            WriteObject(true);
        }
        catch when (Connection is AhtolaCloudConnection)
        {
            throw new InvalidOperationException("The Turso Cloud connection test failed.");
        }
    }
}

[Cmdlet("Clear", "AhtolaSqliteConnectionPool", SupportsShouldProcess = true)]
public sealed class ClearPSSqliteConnectionPoolCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    [Alias("SqliteConnection")]
    public SqliteConnection Connection { get; set; } = null!;

    protected override void ProcessRecord()
    {
        if (ShouldProcess(Connection.DataSource, "Clear connection pool"))
        {
            SqliteConnection.ClearPool(Connection);
        }
    }
}

[Cmdlet(VerbsLifecycle.Start, "AhtolaSqliteTransaction")]
[OutputType(typeof(DbTransaction))]
public sealed class StartPSSqliteTransactionCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    [Alias("SqliteConnection")]
    public DbConnection Connection { get; set; } = null!;

    [Parameter]
    public IsolationLevel IsolationLevel { get; set; } = IsolationLevel.Serializable;

    [Parameter]
    public SwitchParameter Deferred { get; set; }

    protected override void ProcessRecord()
    {
        OpenIfNeeded(Connection);
        if (Deferred.IsPresent && Connection is not SqliteConnection)
            throw new NotSupportedException("Deferred transactions are supported only by local Ahtola SQLite connections.");

        var transaction = Connection is SqliteConnection sqliteConnection
            ? sqliteConnection.BeginTransaction(IsolationLevel, Deferred.IsPresent)
            : Connection.BeginTransaction(IsolationLevel);
        WriteObject(transaction);
    }
}

[Cmdlet(VerbsLifecycle.Invoke, "AhtolaSqliteReplicaSync", SupportsShouldProcess = true)]
[OutputType(typeof(Ahtola.AhtolaSyncResult))]
public sealed class InvokePSSqliteReplicaSyncCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    [Alias("Connection")]
    public AhtolaCloudConnection ReplicaConnection { get; set; } = null!;

    protected override void ProcessRecord()
    {
        if (!ShouldProcess(ReplicaConnection.Endpoint, "Synchronize managed Turso Cloud replica"))
            return;

        WriteObject(ReplicaConnection.Synchronize());
    }
}

[Cmdlet(VerbsData.Save, "AhtolaSqliteTransaction")]
public sealed class SavePSSqliteTransactionCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    public DbTransaction Transaction { get; set; } = null!;

    [Parameter(Mandatory = true)]
    public string Name { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        Transaction.Save(Name);
    }
}

[Cmdlet(VerbsLifecycle.Complete, "AhtolaSqliteTransaction", SupportsShouldProcess = true)]
public sealed class CompletePSSqliteTransactionCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    public DbTransaction Transaction { get; set; } = null!;

    [Parameter]
    public string? SavepointName { get; set; }

    protected override void ProcessRecord()
    {
        var action = string.IsNullOrWhiteSpace(SavepointName) ? "Commit" : "Release savepoint";
        if (!ShouldProcess(Transaction.Connection?.DataSource ?? "SQLite transaction", action))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SavepointName))
        {
            Transaction.Commit();
        }
        else
        {
            Transaction.Release(SavepointName);
        }
    }
}

[Cmdlet(VerbsCommon.Undo, "AhtolaSqliteTransaction", SupportsShouldProcess = true)]
public sealed class UndoPSSqliteTransactionCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    public DbTransaction Transaction { get; set; } = null!;

    [Parameter]
    public string? SavepointName { get; set; }

    protected override void ProcessRecord()
    {
        var action = string.IsNullOrWhiteSpace(SavepointName) ? "Rollback" : "Rollback to savepoint";
        if (!ShouldProcess(Transaction.Connection?.DataSource ?? "SQLite transaction", action))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SavepointName))
        {
            Transaction.Rollback();
        }
        else
        {
            Transaction.Rollback(SavepointName);
        }
    }
}

[Cmdlet(VerbsData.Backup, "AhtolaSqliteDatabase", SupportsShouldProcess = true)]
public sealed class BackupPSSqliteDatabaseCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true)]
    public SqliteConnection SourceConnection { get; set; } = null!;

    [Parameter(Mandatory = true)]
    public SqliteConnection DestinationConnection { get; set; } = null!;

    protected override void ProcessRecord()
    {
        if (ReferenceEquals(SourceConnection, DestinationConnection))
        {
            throw new ArgumentException("SourceConnection and DestinationConnection must be different connections.");
        }

        if (!ShouldProcess(DestinationConnection.DataSource, $"Back up {SourceConnection.DataSource}"))
        {
            return;
        }

        OpenIfNeeded(SourceConnection);
        SourceConnection.BackupDatabase(DestinationConnection);
    }
}

[Cmdlet(VerbsCommon.Get, "AhtolaSqliteSchema")]
public sealed class GetPSSqliteSchemaCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    [Alias("SqliteConnection")]
    public SqliteConnection Connection { get; set; } = null!;

    [Parameter]
    [ValidateSet("MetaDataCollections", "ReservedWords", "Tables", "Columns", "Indexes", "IndexColumns")]
    public string Collection { get; set; } = "Tables";

    [Parameter]
    public string[]? RestrictionValues { get; set; }

    [Parameter]
    [ValidateSet("DataTable", "OrderedDictionary", "PSCustomObject")]
    public string As { get; set; } = "PSCustomObject";

    protected override void ProcessRecord()
    {
        OpenIfNeeded(Connection);
        var table = RestrictionValues is null
            ? Connection.GetSchema(Collection)
            : Connection.GetSchema(Collection, RestrictionValues);
        WriteResult(QueryExecutor.ConvertResult(table, As));
    }
}

[Cmdlet(VerbsLifecycle.Invoke, "AhtolaSqliteMaintenance", SupportsShouldProcess = true)]
public sealed class InvokePSSqliteMaintenanceCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    [Alias("SqliteConnection")]
    public SqliteConnection Connection { get; set; } = null!;

    [Parameter(Mandatory = true)]
    [ValidateSet("Vacuum", "Analyze", "IntegrityCheck", "Checkpoint")]
    public string Operation { get; set; } = string.Empty;

    [Parameter]
    [ValidateSet("Passive", "Full", "Restart", "Truncate")]
    public string CheckpointMode { get; set; } = "Truncate";

    [Parameter]
    public int CommandTimeout { get; set; } = 30;

    [Parameter]
    [ValidateSet("DataTable", "OrderedDictionary", "PSCustomObject")]
    public string As { get; set; } = "PSCustomObject";

    protected override void ProcessRecord()
    {
        var sql = Operation switch
        {
            "Vacuum" => "VACUUM;",
            "Analyze" => "ANALYZE;",
            "IntegrityCheck" => "PRAGMA integrity_check;",
            "Checkpoint" => $"PRAGMA wal_checkpoint({CheckpointMode.ToUpperInvariant()});",
            _ => throw new ArgumentOutOfRangeException(nameof(Operation))
        };

        if (!ShouldProcess(Connection.DataSource, Operation))
        {
            return;
        }

        WriteResult(QueryExecutor.Execute(
            Connection,
            sql,
            parameters: null,
            new QueryOptions
            {
                OutputFormat = Operation is "IntegrityCheck" or "Checkpoint" ? As : "NonQuery",
                CommandTimeout = CommandTimeout
            }));
    }
}

[Cmdlet(VerbsDiagnostic.Test, "AhtolaSqliteIntegrity")]
public sealed class TestAhtolaSqliteIntegrityCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    [Alias("SqliteConnection")]
    public SqliteConnection Connection { get; set; } = null!;

    [Parameter]
    public int CommandTimeout { get; set; } = 30;

    [Parameter]
    [ValidateSet("DataTable", "OrderedDictionary", "PSCustomObject")]
    public string As { get; set; } = "PSCustomObject";

    protected override void ProcessRecord()
    {
        WriteResult(QueryExecutor.Execute(
            Connection,
            "PRAGMA integrity_check;",
            parameters: null,
            new QueryOptions
            {
                OutputFormat = As,
                CommandTimeout = CommandTimeout
            }));
    }
}

[Cmdlet("Optimize", "AhtolaSqliteDatabase", SupportsShouldProcess = true)]
public sealed class OptimizeAhtolaSqliteDatabaseCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    [Alias("SqliteConnection")]
    public SqliteConnection Connection { get; set; } = null!;

    [Parameter]
    public SwitchParameter Analyze { get; set; }

    [Parameter]
    public int CommandTimeout { get; set; } = 30;

    protected override void ProcessRecord()
    {
        if (!ShouldProcess(Connection.DataSource, Analyze.IsPresent ? "VACUUM and ANALYZE" : "VACUUM"))
        {
            return;
        }

        _ = QueryExecutor.Execute(
            Connection,
            "VACUUM;",
            parameters: null,
            new QueryOptions { OutputFormat = "NonQuery", CommandTimeout = CommandTimeout });
        if (Analyze.IsPresent)
        {
            _ = QueryExecutor.Execute(
                Connection,
                "ANALYZE;",
                parameters: null,
                new QueryOptions { OutputFormat = "NonQuery", CommandTimeout = CommandTimeout });
        }
    }
}

[Cmdlet("Checkpoint", "AhtolaSqliteDatabase", SupportsShouldProcess = true)]
public sealed class CheckpointAhtolaSqliteDatabaseCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    [Alias("SqliteConnection")]
    public SqliteConnection Connection { get; set; } = null!;

    [Parameter]
    [ValidateSet("Passive", "Full", "Restart", "Truncate")]
    public string Mode { get; set; } = "Truncate";

    [Parameter]
    public int CommandTimeout { get; set; } = 30;

    protected override void ProcessRecord()
    {
        if (!ShouldProcess(Connection.DataSource, $"Checkpoint ({Mode})"))
        {
            return;
        }

        WriteResult(QueryExecutor.Execute(
            Connection,
            $"PRAGMA wal_checkpoint({Mode.ToUpperInvariant()});",
            parameters: null,
            new QueryOptions
            {
                OutputFormat = "PSCustomObject",
                CommandTimeout = CommandTimeout
            }));
    }
}

[Cmdlet(VerbsCommon.Get, "AhtolaSqliteTable")]
public sealed class GetAhtolaSqliteTableCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    [Alias("SqliteConnection")]
    public SqliteConnection Connection { get; set; } = null!;

    [Parameter]
    [Alias("TableName")]
    public string? Table { get; set; }

    protected override void ProcessRecord()
    {
        WriteResult(QueryExecutor.Execute(
            Connection,
            """
            SELECT name, sql
            FROM sqlite_schema
            WHERE type = 'table'
              AND name NOT LIKE 'sqlite_%'
              AND ($table IS NULL OR name = $table)
            ORDER BY name;
            """,
            new Dictionary<string, object?> { ["$table"] = Table },
            new QueryOptions { OutputFormat = "PSCustomObject" }));
    }
}

[Cmdlet(VerbsCommon.Get, "AhtolaSqliteIndex")]
public sealed class GetAhtolaSqliteIndexCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    [Alias("SqliteConnection")]
    public SqliteConnection Connection { get; set; } = null!;

    [Parameter]
    [Alias("TableName")]
    public string? Table { get; set; }

    protected override void ProcessRecord()
    {
        WriteResult(QueryExecutor.Execute(
            Connection,
            """
            SELECT name, tbl_name AS table_name, sql
            FROM sqlite_schema
            WHERE type = 'index'
              AND ($table IS NULL OR tbl_name = $table)
            ORDER BY tbl_name, name;
            """,
            new Dictionary<string, object?> { ["$table"] = Table },
            new QueryOptions { OutputFormat = "PSCustomObject" }));
    }
}

public sealed record AhtolaSqliteDatabaseInfo(
    string DataSource,
    string State,
    long PageCount,
    long PageSize,
    string JournalMode);

[Cmdlet(VerbsCommon.Get, "AhtolaSqliteDatabaseInfo")]
[OutputType(typeof(AhtolaSqliteDatabaseInfo))]
public sealed class GetAhtolaSqliteDatabaseInfoCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    [Alias("SqliteConnection")]
    public SqliteConnection Connection { get; set; } = null!;

    protected override void ProcessRecord()
    {
        var pageCount = Convert.ToInt64(QueryExecutor.Execute(
            Connection,
            "PRAGMA page_count;",
            parameters: null,
            new QueryOptions { OutputFormat = "Scalar" }));
        var pageSize = Convert.ToInt64(QueryExecutor.Execute(
            Connection,
            "PRAGMA page_size;",
            parameters: null,
            new QueryOptions { OutputFormat = "Scalar" }));
        var journalMode = Convert.ToString(QueryExecutor.Execute(
            Connection,
            "PRAGMA journal_mode;",
            parameters: null,
            new QueryOptions { OutputFormat = "Scalar" })) ?? string.Empty;
        WriteObject(new AhtolaSqliteDatabaseInfo(
            Connection.DataSource,
            Connection.State.ToString(),
            pageCount,
            pageSize,
            journalMode));
    }
}

[Cmdlet(VerbsCommon.Set, "AhtolaSqlitePassword", SupportsShouldProcess = true)]
public sealed class SetPSSqlitePasswordCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    [Alias("SqliteConnection")]
    public SqliteConnection Connection { get; set; } = null!;

    [Parameter(Mandatory = true)]
    public SecureString Password { get; set; } = null!;

    protected override void ProcessRecord()
    {
        if (!ShouldProcess(Connection.DataSource, "Set or rotate Ahtola managed database password"))
        {
            return;
        }

        var plaintext = string.Empty;
        try
        {
            OpenIfNeeded(Connection);
            plaintext = new NetworkCredential(string.Empty, Password).Password;
            Connection.SetPassword(plaintext);
        }
        finally
        {
            plaintext = string.Empty;
        }
    }
}

[Cmdlet("Clear", "AhtolaSqlitePassword", SupportsShouldProcess = true)]
public sealed class ClearPSSqlitePasswordCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipeline = true)]
    [Alias("SqliteConnection")]
    public SqliteConnection Connection { get; set; } = null!;

    protected override void ProcessRecord()
    {
        if (!ShouldProcess(Connection.DataSource, "Clear Ahtola managed database password"))
        {
            return;
        }

        OpenIfNeeded(Connection);
        Connection.ClearPassword();
    }
}
