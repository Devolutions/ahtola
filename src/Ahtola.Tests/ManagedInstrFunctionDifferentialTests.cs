using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;

namespace Ahtola.Tests;

public sealed class ManagedInstrFunctionDifferentialTests
{
    [TestCase("instr('abcabc', 'bc')")]
    [TestCase("instr('abc', '')")]
    [TestCase("instr('abc', 'z')")]
    [TestCase("instr(NULL, 'a')")]
    [TestCase("instr('a', NULL)")]
    [TestCase("instr('αβγβ', 'β')")]
    [TestCase("instr(x'0102030203', x'0203')")]
    [TestCase("instr(x'010203', x'')")]
    [TestCase("instr(1234512345, 345)")]
    [TestCase("instr(x'313233', '2')")]
    public void InstrMatchesSqlite(string expression)
    {
        EvaluateManaged(expression).Should().Be(EvaluateSqlite(expression), because: expression);
    }

    [TestCase("instr('a')")]
    [TestCase("instr('a', 'b', 'c')")]
    public void InstrRejectsUnsupportedArity(string expression)
    {
        using var connection = new EmbeddedDatabase().Connect();

        Assert.Throws<EmbeddedSqlException>(() => EvaluateManaged(connection, expression))!
            .Message.Should().Be("wrong number of arguments to function instr()");
    }

    private static SqlValue EvaluateManaged(string expression)
    {
        using var connection = new EmbeddedDatabase().Connect();
        return EvaluateManaged(connection, expression);
    }

    private static SqlValue EvaluateManaged(EmbeddedConnection connection, string expression)
    {
        using var statement = connection.Prepare($"SELECT {expression};");
        statement.Step().Should().Be(StatementStepResult.Row);
        var value = statement.GetValue(0);
        statement.Step().Should().Be(StatementStepResult.Done);
        return value;
    }

    private static SqlValue EvaluateSqlite(string expression)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {expression};";
        var value = command.ExecuteScalar();
        return value switch
        {
            null or DBNull => SqlValue.Null,
            long integer => SqlValue.Integer(integer),
            double real => SqlValue.Real(real),
            string text => SqlValue.Text(text),
            byte[] blob => SqlValue.Blob(blob),
            _ => throw new InvalidOperationException($"Unexpected SQLite value type {value.GetType().Name}."),
        };
    }
}
