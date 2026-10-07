using System.Data.Common;
using System.Text;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite.Codecs;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using AhtolaSqliteConnection = Ahtola.Data.Sqlite.SqliteConnection;
using MicrosoftSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace Benchmarks;

/// <summary>
/// The workloads Remote Desktop Manager and Devolutions Server put on SQLite, measured through
/// the <c>Microsoft.Data.Sqlite</c>-compatible facade against <c>Microsoft.Data.Sqlite</c> +
/// e_sqlite3: the schema upgrade ladder that runs at first open after an update (DDL-heavy),
/// bulk insert of entries, large-TEXT (connection XML) reads and writes, <c>LIKE</c> scans over
/// the <c>DATA</c> column, open cost, and the legacy page codecs (RC4 / AES-128-CBC) per-page
/// overhead. The <c>Codec</c> parameter applies to Ahtola only; native rows run plain.
/// </summary>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class RdmDvlsWorkloadBenchmarks
{
    public enum Provider
    {
        Sqlite,
        Ahtola,
    }

    public enum PageCodecKind
    {
        None,
        SystemDataSQLiteRc4,
        WxSQLite3Aes128,
    }

    private const int EntryCount = 2_000;
    private const int XmlBytes = 20 * 1024;
    private const string Password = "benchmark-password";

    private string _root = null!;
    private string _path = null!;
    private DbConnection _connection = null!;
    private string _xml = null!;
    private int _cursor;

    [Params(Provider.Sqlite, Provider.Ahtola)]
    public Provider Engine { get; set; }

    [Params(PageCodecKind.None, PageCodecKind.SystemDataSQLiteRc4, PageCodecKind.WxSQLite3Aes128)]
    public PageCodecKind Codec { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _root = Path.Combine(Path.GetTempPath(), "ahtola-rdm-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "workspace.db");
        _xml = BuildConnectionXml(0);
        _connection = Open(_path);
        Execute(_connection, """
            CREATE TABLE Connections (
                ID guid NOT NULL,
                DATA TEXT,
                Name varchar(255),
                GroupName varchar(255),
                CreationDate datetime NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (ID));
            CREATE INDEX IX_Connections_Name ON Connections (Name);
            """);
        InsertEntries(_connection, EntryCount);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _connection.Dispose();
        AhtolaSqliteConnection.ClearAllPools();
        MicrosoftSqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [BenchmarkCategory("RDM schema ladder")]
    [Benchmark(Description = "Upgrade ladder (fresh file, step per transaction)")]
    public int SchemaUpgradeLadder()
    {
        var path = Path.Combine(_root, "ladder-" + Interlocked.Increment(ref _cursor) + ".db");
        using var connection = Open(path);
        var version = 0;
        foreach (var step in LadderSteps)
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, step, transaction);
            Execute(connection, "UPDATE DatabaseInfo SET DatabaseVersion = " + ++version + ";", transaction);
            transaction.Commit();
        }

        return version;
    }

    [BenchmarkCategory("Bulk insert")]
    [Benchmark(Description = "Insert 500 entries with 20 KB XML in one transaction")]
    public int BulkInsert()
    {
        using var transaction = _connection.BeginTransaction();
        var inserted = InsertEntries(_connection, 500, transaction);
        transaction.Rollback();
        return inserted;
    }

    [BenchmarkCategory("Large TEXT")]
    [Benchmark(Description = "Read every DATA column (20 KB XML)")]
    public long ReadAllXml()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT DATA FROM Connections;";
        using var reader = command.ExecuteReader();
        long total = 0;
        while (reader.Read())
            total += reader.GetString(0).Length;
        return total;
    }

    [BenchmarkCategory("Large TEXT")]
    [Benchmark(Description = "Update one DATA column (20 KB XML)")]
    public int UpdateXml()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE Connections SET DATA = $data WHERE rowid = $rowid;";
        AddParameter(command, "$data", BuildConnectionXml(Interlocked.Increment(ref _cursor)));
        AddParameter(command, "$rowid", (long)(_cursor % EntryCount) + 1);
        return command.ExecuteNonQuery();
    }

    [BenchmarkCategory("LIKE scan")]
    [Benchmark(Description = "LIKE '%host-1234%' over DATA")]
    public long LikeScanOverData()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM Connections WHERE DATA LIKE '%host-1234.example%';";
        return (long)command.ExecuteScalar()!;
    }

    [BenchmarkCategory("Open")]
    [Benchmark(Description = "Open (unpooled) + first query")]
    public long OpenAndQuery()
    {
        using var connection = Open(_path, pooling: false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM Connections;";
        return (long)command.ExecuteScalar()!;
    }

    private DbConnection Open(string path, bool pooling = true)
    {
        DbConnection connection;
        if (Engine == Provider.Sqlite)
        {
            connection = new MicrosoftSqliteConnection($"Data Source={path};Pooling={pooling}");
        }
        else
        {
            var ahtola = new AhtolaSqliteConnection($"Data Source={path};Pooling={pooling}");
            ahtola.PageCodec = CreateCodec();
            connection = ahtola;
        }

        connection.Open();
        Execute(connection, "PRAGMA synchronous=NORMAL;");
        return connection;
    }

    private IPageCodec? CreateCodec()
        => Codec switch
        {
            PageCodecKind.SystemDataSQLiteRc4 => LegacyPageCodecs.Create(LegacyPageCodecFormat.SystemDataSQLiteRc4, Password),
            PageCodecKind.WxSQLite3Aes128 => LegacyPageCodecs.Create(LegacyPageCodecFormat.WxSQLite3Aes128, Password),
            _ => null,
        };

    private int InsertEntries(DbConnection connection, int count, DbTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO Connections (ID, DATA, Name, GroupName) VALUES ($id, $data, $name, $group);";
        var id = AddParameter(command, "$id", Guid.Empty);
        var data = AddParameter(command, "$data", _xml);
        var name = AddParameter(command, "$name", string.Empty);
        var group = AddParameter(command, "$group", string.Empty);
        for (var index = 0; index < count; index++)
        {
            id.Value = Guid.NewGuid();
            data.Value = BuildConnectionXml(index);
            name.Value = "Connection " + index;
            group.Value = "Group " + (index % 50);
            command.ExecuteNonQuery();
        }

        return count;
    }

    private static string BuildConnectionXml(int seed)
    {
        var builder = new StringBuilder(XmlBytes + 256);
        builder.Append("<?xml version=\"1.0\"?><Connection><Host>host-").Append(seed).Append(".example</Host><Settings>");
        while (builder.Length < XmlBytes)
            builder.Append("<Setting Name=\"Option").Append(builder.Length).Append("\" Value=\"value\" />");
        return builder.Append("</Settings></Connection>").ToString();
    }

    private static DbParameter AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
        return parameter;
    }

    private static void Execute(DbConnection connection, string sql, DbTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    // A synthetic ladder with the shape of RDM's and DVLS's SQLite upgrade steps: tables with
    // GUID keys, many ALTER TABLE ADD COLUMN steps, indexes, triggers and a table rebuild.
    private static readonly string[] LadderSteps = BuildLadder();

    private static string[] BuildLadder()
    {
        var steps = new List<string>
        {
            """
            CREATE TABLE IF NOT EXISTS Connections (ID guid NOT NULL, DATA TEXT,
                CreationDate Datetime NOT NULL DEFAULT (datetime('now', 'localtime')), PRIMARY KEY (ID));
            CREATE TABLE IF NOT EXISTS DatabaseInfo (DatabaseVersion int, PRIMARY KEY (DatabaseVersion));
            INSERT OR IGNORE INTO DatabaseInfo (DatabaseVersion) VALUES (0);
            """,
        };
        for (var table = 0; table < 12; table++)
        {
            steps.Add($"CREATE TABLE IF NOT EXISTS T{table} (ID guid NOT NULL, ConnectionID guid REFERENCES Connections(ID), Name varchar, PRIMARY KEY (ID));");
            for (var column = 0; column < 6; column++)
                steps.Add($"ALTER TABLE T{table} ADD Column{column} {(column % 2 == 0 ? "int NOT NULL DEFAULT 0" : "varchar NULL")};");
            steps.Add($"CREATE INDEX IF NOT EXISTS IX_T{table}_Name ON T{table} (Name);");
            steps.Add($"""
                CREATE TRIGGER IF NOT EXISTS TR_T{table}_Delete AFTER DELETE ON T{table} FOR EACH ROW BEGIN
                    UPDATE Connections SET DATA = DATA WHERE ID = OLD.ConnectionID;
                END;
                """);
        }

        steps.Add("""
            CREATE TABLE T0_new (ID guid NOT NULL, ConnectionID guid, Name varchar, Column0 int NOT NULL DEFAULT 0, PRIMARY KEY (ID));
            INSERT INTO T0_new (ID, ConnectionID, Name, Column0) SELECT ID, ConnectionID, Name, Column0 FROM T0;
            DROP TABLE T0;
            ALTER TABLE T0_new RENAME TO T0;
            CREATE INDEX IF NOT EXISTS IX_T0_Name ON T0 (Name);
            """);
        return [.. steps];
    }
}
