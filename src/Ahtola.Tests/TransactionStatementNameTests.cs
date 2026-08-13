using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

public class TransactionStatementNameTests
{
    [TestCase("BEGIN TRANSACTION [bracketed name]; COMMIT TRANSACTION [different name];")]
    [TestCase("BEGIN TRANSACTION 'quoted name'; ROLLBACK TRANSACTION 'different name';")]
    public void TransactionStatementsAcceptOptionalNames(string sql)
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        foreach (var statementSql in sql.Split(';', StringSplitOptions.RemoveEmptyEntries))
            Execute(connection, statementSql);
    }

    [Test]
    public void RollbackTransactionWithoutNameStillAcceptsToSavepoint()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "BEGIN TRANSACTION;");
        Execute(connection, "SAVEPOINT point;");
        Execute(connection, "ROLLBACK TRANSACTION TO SAVEPOINT point;");
        Execute(connection, "RELEASE SAVEPOINT point;");
        Execute(connection, "COMMIT;");
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }
}
