using Ahtola.Core;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Verifies <c>EXPLAIN QUERY PLAN FORMAT=JSON</c>: the parser accepts the optional
/// case-insensitive <c>FORMAT=JSON</c>/<c>FORMAT=TEXT</c> clause after
/// <c>EXPLAIN QUERY PLAN</c> (mirroring <c>parse_explain_query_plan_format</c> in
/// <c>turso-src/sqlite/parser/src/parser.rs</c>), and the JSON format emits one
/// <c>plan_json</c> TEXT row whose envelope matches the documented transport shape
/// (<c>turso-src/docs/eqp-json.md</c>): version 1, the statement SQL, result columns,
/// and one node per plan row with id/parent/detail.
/// </summary>
public class ExplainQueryPlanFormatJsonTests
{
    [Test]
    public void FormatJsonEmitsOnePlanJsonRowWithDocumentedEnvelope()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        connection.PrepareScript("CREATE TABLE users (id INTEGER PRIMARY KEY, age INTEGER);").ToList();

        var rows = ReadAll(connection, "EXPLAIN QUERY PLAN FORMAT=JSON SELECT id FROM users WHERE age > 21;");
        rows.Should().HaveCount(1);
        var json = rows[0];
        json.Should().StartWith("{\"version\":1,");
        json.Should().Contain("\"sql\":\"EXPLAIN QUERY PLAN SELECT id FROM users WHERE age > 21\"");
        json.Should().Contain("\"result_columns\":[");
        json.Should().Contain("\"nodes\":[");
        json.Should().Contain("\"detail\":\"");
        json.Should().EndWith("}");
    }

    [Test]
    public void FormatClauseIsCaseInsensitiveAndTextIsDefault()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();

        // Lowercase format keyword and value both parse.
        var json = ReadAll(connection, "explain query plan format=json SELECT 1;");
        json.Should().HaveCount(1);
        json[0].Should().Contain("\"version\":1");

        // TEXT (explicit) keeps the classic four-column rows.
        var text = ReadAll(connection, "EXPLAIN QUERY PLAN FORMAT=TEXT SELECT 1;");
        text.Should().HaveCount(1);
        text[0].Should().NotContain("\"version\":1");

        // Omitting the clause defaults to TEXT.
        var omitted = ReadAll(connection, "EXPLAIN QUERY PLAN SELECT 1;");
        omitted.Should().HaveCount(1);
        omitted[0].Should().NotContain("\"version\":1");
    }

    [Test]
    public void UnknownFormatIsRejected()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();

        var statement = () => connection.PrepareScript("EXPLAIN QUERY PLAN FORMAT=YAML SELECT 1;").ToList();
        statement.Should().Throw<EmbeddedSqlException>()
            .Which.Message.Should().Contain(
                "unknown EXPLAIN QUERY PLAN format: YAML (supported formats: TEXT, JSON)");
    }

    [Test]
    public void NodeEntriesCarryThePlanDetailText()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        connection.PrepareScript("CREATE TABLE t(a INTEGER PRIMARY KEY);").ToList();

        var json = ReadAll(connection, "EXPLAIN QUERY PLAN FORMAT=JSON SELECT * FROM t;");
        json.Should().HaveCount(1);
        // Every node entry carries a non-empty detail drawn from the engine's plan text
        // (a SCAN/SEARCH detail or the evaluator-fallback marker).
        json[0].Should().MatchRegex("\"detail\":\"[^\"]+\"");
        json[0].Should().Contain("\"id\":");
        json[0].Should().Contain("\"parent\":");
    }

    private static List<string> ReadAll(EmbeddedConnection connection, string sql)
    {
        var rows = new List<string>();
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
            {
                while (statement.Step(default) == StatementStepResult.Row)
                    rows.Add(statement.GetValue(0).Kind == SqlValueKind.Text
                        ? statement.GetValue(0).AsText()
                        : statement.GetValue(0).ToString() ?? "");
            }
        }

        return rows;
    }
}
