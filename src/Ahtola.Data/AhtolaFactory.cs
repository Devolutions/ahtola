using System.Data.Common;

namespace Ahtola;

public sealed class AhtolaFactory : DbProviderFactory
{
    public static readonly AhtolaFactory Instance = new();

    private AhtolaFactory()
    {
    }

    public override bool CanCreateBatch => true;

    public override bool CanCreateDataAdapter => true;

    public override bool CanCreateCommandBuilder => true;

    public override DbBatch CreateBatch() => new AhtolaBatch();

    public override DbBatchCommand CreateBatchCommand() => new AhtolaBatchCommand();

    public override DbCommand CreateCommand() => new AhtolaCommand();

    public override DbCommandBuilder CreateCommandBuilder() => new AhtolaCommandBuilder();

    public override DbConnection CreateConnection() => new AhtolaConnection();

    public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new AhtolaConnectionStringBuilder();

    public override DbDataAdapter CreateDataAdapter() => new AhtolaDataAdapter();

    public override DbParameter CreateParameter() => new AhtolaParameter();
}
