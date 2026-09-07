using Ahtola.Core;
using AwesomeAssertions;

namespace Ahtola.Tests;

public class ManagedGeneratedColumnConversionTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void GeneratedColumnConversionPreservesOnlyStoredValues(bool stored)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        var sql = $"""
            CREATE TABLE t(a INTEGER, g INTEGER AS(a*2) {(stored ? "STORED" : "VIRTUAL")});
            INSERT INTO t(a) VALUES(3);
            ALTER TABLE t ALTER COLUMN g TO g INTEGER;
            """;
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
            {
                while (statement.Step() == StatementStepResult.Row)
                {
                }
            }
        }

        using var query = connection.Prepare("SELECT g FROM t;");
        query.Step().Should().Be(StatementStepResult.Row);
        query.GetValue(0).Should().Be(stored ? SqlValue.Integer(6) : SqlValue.Null);
        query.Step().Should().Be(StatementStepResult.Done);
    }
}
