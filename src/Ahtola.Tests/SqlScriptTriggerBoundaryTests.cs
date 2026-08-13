using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

public sealed class SqlScriptTriggerBoundaryTests
{
    [Test]
    public void PrepareScriptKeepsSupportedAfterTriggerBodiesIntact()
    {
        using var connection = new EmbeddedDatabase().Connect();
        var statements = connection.PrepareScript("""
            CREATE TABLE [source; data](value TEXT);
            CREATE TABLE [audit; data](value TEXT);
            CREATE TRIGGER [insert; trigger] AFTER INSERT ON [source; data]
            BEGIN
                /* A semicolon and END here are not trigger delimiters: ; END */
                INSERT INTO [audit; data] VALUES ('insert; BEGIN END');
            END;
            CREATE TRIGGER update_trigger AFTER UPDATE ON [source; data]
            BEGIN
                INSERT INTO [audit; data] VALUES ('update');
            END;
            CREATE TRIGGER delete_trigger AFTER DELETE ON [source; data]
            BEGIN
                INSERT INTO [audit; data] VALUES ('delete');
            END;
            INSERT INTO [source; data] VALUES ('initial');
            UPDATE [source; data] SET value = 'updated';
            DELETE FROM [source; data];
            """);

        foreach (var statement in statements)
        {
            using (statement)
                statement.Step().Should().Be(StatementStepResult.Done);
        }

        using var count = connection.Prepare("SELECT COUNT(*) FROM [audit; data];");
        count.Step().Should().Be(StatementStepResult.Row);
        count.GetValue(0).Should().Be(SqlValue.Integer(3));
    }

    [Test]
    public void PrepareScriptRejectsTrailingSyntaxAfterTriggerEnd()
    {
        using var connection = new EmbeddedDatabase().Connect();

        var exception = Assert.Throws<EmbeddedSqlException>(() => connection.PrepareScript("""
            CREATE TABLE source(value INTEGER);
            CREATE TABLE audit(value INTEGER);
            CREATE TRIGGER source_audit AFTER INSERT ON source
            BEGIN
                INSERT INTO audit VALUES (1);
            END trailing;
            """));

        exception!.Message.Should().Contain("Expected End");
    }

    [Test]
    public void PrepareScriptRecognizesFullRowTriggerHeaders()
    {
        using var connection = new EmbeddedDatabase().Connect();
        var statements = connection.PrepareScript("""
            CREATE TABLE source(id INTEGER, value TEXT);
            CREATE TABLE audit(value TEXT);
            CREATE TRIGGER source_before UPDATE OF value ON source FOR EACH ROW
            WHEN NEW.value <> OLD.value
            BEGIN
                INSERT INTO audit VALUES (OLD.value || ':' || NEW.value);
            END;
            INSERT INTO source VALUES (1, 'old');
            UPDATE source SET value = 'new';
            """);

        foreach (var statement in statements)
        {
            using (statement)
                statement.Step().Should().Be(StatementStepResult.Done);
        }

        using var value = connection.Prepare("SELECT value FROM audit;");
        value.Step().Should().Be(StatementStepResult.Row);
        value.GetValue(0).Should().Be(SqlValue.Text("old:new"));
        value.Step().Should().Be(StatementStepResult.Done);
    }
}
