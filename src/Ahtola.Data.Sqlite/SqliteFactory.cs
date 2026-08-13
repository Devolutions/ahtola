using System.Data.Common;
using Ahtola;

namespace Ahtola.Data.Sqlite;

public sealed class SqliteFactory : DbProviderFactory
{
    public static readonly SqliteFactory Instance = new();

    private SqliteFactory()
    {
    }

    public override bool CanCreateBatch => true;

    public override bool CanCreateDataAdapter => true;

    public override bool CanCreateCommandBuilder => true;

    public override DbBatch CreateBatch() => new SqliteBatch();

    public override DbBatchCommand CreateBatchCommand() => new SqliteBatchCommand();

    public override DbCommand CreateCommand() => new SqliteCommand();

    public override DbCommandBuilder CreateCommandBuilder() => new AhtolaCommandBuilder();

    public override DbConnection CreateConnection() => new SqliteConnection();

    public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new SqliteConnectionStringBuilder();

    public override DbDataAdapter CreateDataAdapter() => new AhtolaDataAdapter();

    public override DbParameter CreateParameter() => new SqliteParameter();
}
