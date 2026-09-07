using Ahtola.Core;
using AwesomeAssertions;

namespace Ahtola.Tests;

public class ManagedSequenceCommitParityTests
{
    [TestCase(false, 3, 4)]
    [TestCase(true, 1, 2)]
    public void ConcurrentSequenceAllocationsSurviveCommit(
        bool cycle, long watermark, long nextValue)
    {
        using var database = new EmbeddedDatabase();
        using var writer = database.Connect();
        Execute(writer, "PRAGMA journal_mode=mvcc;");
        Execute(writer, cycle
            ? "CREATE SEQUENCE s START WITH 1 MINVALUE 1 MAXVALUE 3 CYCLE;"
            : "CREATE SEQUENCE s;");
        Execute(writer, "BEGIN CONCURRENT;");
        for (var index = 0; index < (cycle ? 4 : 3); index++)
            ReadValue(writer, "SELECT nextval('s');").Should().Be(SqlValue.Integer(index % 3 + 1));
        Execute(writer, "COMMIT;");

        using var reader = database.Connect();
        ReadValue(reader, "SELECT max(value) FROM \"__turso_internal_seq_s\";").AsInteger()
            .Should().Be(watermark);
        ReadValue(reader, "SELECT nextval('s');").AsInteger().Should().Be(nextValue);
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
            {
                while (statement.Step() == StatementStepResult.Row)
                {
                }
            }
        }
    }

    private static SqlValue ReadValue(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0);
    }
}
