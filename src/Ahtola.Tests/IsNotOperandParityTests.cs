using ManagedSqlite = Ahtola.Data.Sqlite;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// SQLite allows a NOT chain on the right of IS / IS NOT, and that NOT negates a whole
/// comparison rather than just the next operand, so <c>1 IS NOT NOT 2 = 3</c> negates
/// <c>2 = 3</c>. NOT still binds looser than the comparison operators but tighter than AND.
/// </summary>
public class IsNotOperandParityTests
{
    private static readonly string[] Expressions =
    [
        "2 IS NOT NOT TRUE",
        "2 IS NOT NOT 0",
        "NULL IS NOT NOT NULL",
        "1 IS NOT NOT 1",
        "1 IS NOT NOT NOT 1",
        "2 IS NOT TRUE",
        "1 IS NOT NULL",
        "1 IS NOT NOT NULL",
        "2 IS NOT DISTINCT FROM 2",
        "2 IS DISTINCT FROM 2",
        "1 IS NOT DISTINCT FROM NOT 0",

        // NOT after IS negates the whole comparison, but must not swallow AND.
        "1 IS NOT NOT 2 = 3",
        "2 IS NOT NOT TRUE AND 0",
        "1 IS NOT NOT 2 BETWEEN 1 AND 3",
        "1 IS NOT NOT 2 IN (2, 3)",

        // Chained IS keeps left associativity when no NOT is present.
        "1 IS 2 = 3",
        "1 IS 1 IS 1",
    ];

    [Test]
    public void IsOperandsAcceptANotChainLikeSqlite()
    {
        var problems = new List<string>();
        foreach (var expression in Expressions)
        {
            var sql = $"SELECT {expression};";
            var managed = Describe(sql, OpenManaged);
            var sqlite = Describe(sql, OpenSqlite);
            if (managed != sqlite)
                problems.Add($"{expression}: managed {managed}, sqlite {sqlite}");
        }

        if (problems.Count > 0)
            Assert.Fail(string.Join(Environment.NewLine, problems));
    }

    private static string Describe(string sql, Func<System.Data.Common.DbConnection> open)
    {
        try
        {
            using var connection = open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            var value = command.ExecuteScalar();
            return $"{value?.GetType().Name ?? "null"}:{value ?? "null"}";
        }
        catch (Exception exception)
        {
            return $"error: {exception.Message}";
        }
    }

    private static System.Data.Common.DbConnection OpenManaged()
    {
        var connection = new ManagedSqlite.SqliteConnection(
            "Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        return connection;
    }

    private static System.Data.Common.DbConnection OpenSqlite()
    {
        var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }
}
