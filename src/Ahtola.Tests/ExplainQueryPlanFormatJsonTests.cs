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
        Execute(
            connection,
            "CREATE TABLE users (id INTEGER PRIMARY KEY, age INTEGER); " +
            "CREATE INDEX idx_users_age ON users(age);");

        var rows = ReadAll(connection, "EXPLAIN QUERY PLAN FORMAT=JSON SELECT id FROM users WHERE age > 21;");
        rows.Should().HaveCount(1);
        var json = rows[0];
        json.Should().StartWith("{\"version\":1,");
        // The envelope echoes the statement exactly as written, FORMAT=JSON clause included
        // (turso-src/docs/eqp-json.md; core/translate/eqp.rs's `sql` field is the verbatim
        // input), so the echo names the format the caller asked for.
        json.Should().Contain("\"sql\":\"EXPLAIN QUERY PLAN FORMAT=JSON SELECT id FROM users WHERE age > 21\"");
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
        Execute(
            connection,
            "CREATE TABLE t(a INTEGER PRIMARY KEY, b INTEGER); " +
            "CREATE INDEX idx_t_b ON t(b);");

        var json = ReadAll(connection, "EXPLAIN QUERY PLAN FORMAT=JSON SELECT a FROM t WHERE b = 1;");
        json.Should().HaveCount(1);
        // A modeled node (here, an index SEARCH) carries a non-empty detail drawn from the
        // engine's plan text. A statement with no per-step plan modeled yet reports an empty
        // nodes array instead of a fabricated node (see ExecuteExplainQueryPlan's
        // isPlaceholderOnly handling) rather than a fake "detail".
        json[0].Should().MatchRegex("\"detail\":\"[^\"]+\"");
        json[0].Should().Contain("\"id\":");
        json[0].Should().Contain("\"parent\":");
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
            {
                while (statement.Step(default) == StatementStepResult.Row)
                {
                }
            }
        }
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
