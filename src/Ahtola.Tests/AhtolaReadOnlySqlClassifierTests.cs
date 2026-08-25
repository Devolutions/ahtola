using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// The classifier decides whether a browser connection in synchronous
/// read-mirror mode may execute a script without an OPFS flush, so a false
/// positive would let an unflushed mutation escape. These tests pin the shapes
/// it accepts and prove it stays conservative everywhere else.
/// </summary>
public sealed class AhtolaReadOnlySqlClassifierTests
{
    [TestCase("SELECT 1")]
    [TestCase("select 1")]
    [TestCase("  \r\n SELECT 1 \t ")]
    [TestCase("SELECT 1;")]
    [TestCase("SELECT 1;;")]
    [TestCase("SELECT * FROM probe WHERE id = $id")]
    [TestCase("SELECT * FROM probe WHERE id = @id AND name = :name")]
    [TestCase("SELECT * FROM probe WHERE id = ?1")]
    [TestCase("VALUES (1, 2, 3)")]
    [TestCase("SELECT 1; SELECT 2; VALUES (3)")]
    [TestCase("WITH cte AS (SELECT 1 AS x) SELECT x FROM cte")]
    [TestCase("WITH cte(x) AS (SELECT 1) SELECT x FROM cte")]
    [TestCase("WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < 5) SELECT n FROM seq")]
    [TestCase("WITH a AS (SELECT 1 AS x), b AS (SELECT 2 AS y) SELECT x, y FROM a, b")]
    [TestCase("WITH a AS NOT MATERIALIZED (SELECT 1 AS x) SELECT x FROM a")]
    [TestCase("WITH a AS MATERIALIZED (SELECT 1 AS x) VALUES (1)")]
    [TestCase("SELECT replace(name, 'a', 'b') FROM probe")]
    [TestCase("SELECT CASE WHEN id > 1 THEN 'x' ELSE 'y' END FROM probe")]
    [TestCase("SELECT * FROM pragma_table_info('probe')")]
    [TestCase("SELECT json_insert('{}', '$.a', 1)")]
    [TestCase("SELECT 'delete from probe' AS literal")]
    [TestCase("SELECT \"drop\" FROM probe")]
    [TestCase("SELECT [update] FROM probe")]
    [TestCase("SELECT `insert` FROM probe")]
    [TestCase("-- comment\nSELECT 1")]
    [TestCase("/* insert update delete */ SELECT 1")]
    [TestCase("SELECT 1 /* drop table probe */")]
    [TestCase("SELECT x'00ff'")]
    [TestCase("SELECT 1.5e-3, 0x1F")]
    [TestCase("SELECT (SELECT max(value) FROM probe) AS m")]
    public void ProvesReadOnlyScripts(string sql)
        => AhtolaReadOnlySqlClassifier.IsProvenReadOnlyScript(sql).Should().BeTrue();

    [TestCase("INSERT INTO probe(value) VALUES (1)")]
    [TestCase("UPDATE probe SET value = 1")]
    [TestCase("DELETE FROM probe")]
    [TestCase("REPLACE INTO probe(id, value) VALUES (1, 2)")]
    [TestCase("CREATE TABLE probe(value INTEGER)")]
    [TestCase("DROP TABLE probe")]
    [TestCase("ALTER TABLE probe RENAME TO other")]
    [TestCase("CREATE INDEX ix ON probe(value)")]
    [TestCase("PRAGMA journal_mode")]
    [TestCase("pragma table_info('probe')")]
    [TestCase("EXPLAIN SELECT 1")]
    [TestCase("EXPLAIN QUERY PLAN SELECT 1")]
    [TestCase("BEGIN")]
    [TestCase("BEGIN IMMEDIATE")]
    [TestCase("COMMIT")]
    [TestCase("END")]
    [TestCase("ROLLBACK")]
    [TestCase("SAVEPOINT s1")]
    [TestCase("RELEASE s1")]
    [TestCase("ATTACH DATABASE 'other.db' AS other")]
    [TestCase("DETACH DATABASE other")]
    [TestCase("VACUUM")]
    [TestCase("ANALYZE")]
    [TestCase("REINDEX")]
    [TestCase("WITH cte AS (SELECT 1 AS x) INSERT INTO probe(value) SELECT x FROM cte")]
    [TestCase("WITH cte AS (SELECT 1 AS x) UPDATE probe SET value = 1")]
    [TestCase("WITH cte AS (SELECT 1 AS x) DELETE FROM probe")]
    [TestCase("WITH cte AS (DELETE FROM probe RETURNING id) SELECT id FROM cte")]
    [TestCase("WITH cte AS (INSERT INTO probe(value) VALUES (1) RETURNING id) SELECT id FROM cte")]
    [TestCase("SELECT 1; INSERT INTO probe(value) VALUES (1)")]
    [TestCase("INSERT INTO probe(value) VALUES (1); SELECT 1")]
    [TestCase("SELECT 1; PRAGMA journal_mode")]
    [TestCase("SELECT 1; BEGIN")]
    [TestCase("WITH cte AS (SELECT 1)")]
    [TestCase("WITH cte SELECT 1")]
    [TestCase("WITH")]
    public void RefusesToProveMutatingOrUnprovenScripts(string sql)
        => AhtolaReadOnlySqlClassifier.IsProvenReadOnlyScript(sql).Should().BeFalse();

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase(";")]
    [TestCase(";;")]
    [TestCase("-- only a comment")]
    [TestCase("/* only a comment */")]
    public void RefusesToProveEmptyScripts(string? sql)
        => AhtolaReadOnlySqlClassifier.IsProvenReadOnlyScript(sql).Should().BeFalse();

    [TestCase("SELECT 'unterminated")]
    [TestCase("SELECT \"unterminated")]
    [TestCase("SELECT [unterminated")]
    [TestCase("SELECT `unterminated")]
    [TestCase("SELECT 1 /* unterminated")]
    [TestCase("SELECT (1")]
    [TestCase("SELECT 1)")]
    [TestCase("SELECT ((1)")]
    public void RefusesToProveMalformedScripts(string sql)
        => AhtolaReadOnlySqlClassifier.IsProvenReadOnlyScript(sql).Should().BeFalse();

    [Test]
    public void TreatsSemicolonsInsideLiteralsAsData()
    {
        AhtolaReadOnlySqlClassifier
            .IsProvenReadOnlyScript("SELECT 'a; DROP TABLE probe' AS payload")
            .Should().BeTrue();
        AhtolaReadOnlySqlClassifier
            .IsProvenReadOnlyScript("SELECT \"a; DROP TABLE probe\" FROM probe")
            .Should().BeTrue();
        AhtolaReadOnlySqlClassifier
            .IsProvenReadOnlyScript("SELECT '''; DELETE FROM probe; ''' AS payload")
            .Should().BeTrue();
    }

    [Test]
    public void TreatsSemicolonsInsideCommentsAsTrivia()
    {
        AhtolaReadOnlySqlClassifier
            .IsProvenReadOnlyScript("SELECT 1 -- ; DROP TABLE probe")
            .Should().BeTrue();
        AhtolaReadOnlySqlClassifier
            .IsProvenReadOnlyScript("SELECT 1 /* ; DROP TABLE probe */")
            .Should().BeTrue();
    }

    /// <summary>
    /// A line comment ends at CR as well as LF. Terminating only on LF would let a lone-CR
    /// newline hide the rest of a script behind a comment while the production statement
    /// splitters — SqliteCommand's script tokenizer and ManagedReadOnlySqlGuard — still split on
    /// the semicolon and execute the write.
    /// </summary>
    [TestCase("SELECT 1 --comment\r; INSERT INTO probe(id) VALUES (1)")]
    [TestCase("SELECT 1 --comment\r\n; INSERT INTO probe(id) VALUES (1)")]
    [TestCase("SELECT 1 --comment\n; INSERT INTO probe(id) VALUES (1)")]
    [TestCase("--\rINSERT INTO probe(id) VALUES (1)")]
    [TestCase("--\r\nDELETE FROM probe")]
    [TestCase("SELECT 1;--x\rDROP TABLE probe")]
    [TestCase("SELECT 1 --x\rUNION ALL SELECT 2; PRAGMA journal_mode = WAL")]
    public void LineCommentsTerminateOnCarriageReturnAndLineFeed(string sql)
        => AhtolaReadOnlySqlClassifier.IsProvenReadOnlyScript(sql).Should().BeFalse();

    [TestCase("SELECT 1 --comment\rUNION ALL SELECT 2")]
    [TestCase("SELECT 1 --comment\r\nUNION ALL SELECT 2")]
    [TestCase("--leading\rSELECT 1")]
    [TestCase("--leading\r\nSELECT 1")]
    [TestCase("SELECT 1 -- trailing only\r")]
    public void ProvesScriptsWhoseCarriageReturnEndsTheComment(string sql)
        => AhtolaReadOnlySqlClassifier.IsProvenReadOnlyScript(sql).Should().BeTrue();

    /// <summary>
    /// The classifier's comment handling must agree with production statement splitting for every
    /// newline shape. Anything the classifier proves read-only must, after the production splitter
    /// removes comments and splits on <c>;</c>, contain no mutating statement.
    /// </summary>
    [Test]
    public void CarriageReturnCommentFuzzAgreesWithProductionStatementSplitting()
    {
        var random = new Random(20260825);
        string[] newlines = ["\r", "\n", "\r\n", "\u0085", " "];
        string[] comments = ["--x", "-- ; DROP TABLE probe", "--", "/* c */", "--INSERT"];
        string[] heads = ["SELECT 1", "VALUES (1)", "SELECT a FROM t"];
        string[] tails =
        [
            "INSERT INTO probe(id) VALUES (1)",
            "DELETE FROM probe",
            "PRAGMA journal_mode = WAL",
            "SELECT 2",
            "UNION ALL SELECT 2",
        ];

        for (var iteration = 0; iteration < 4000; iteration++)
        {
            var separator = random.Next(2) == 0 ? ";" : "";
            var script = heads[random.Next(heads.Length)]
                         + " "
                         + comments[random.Next(comments.Length)]
                         + newlines[random.Next(newlines.Length)]
                         + separator
                         + tails[random.Next(tails.Length)];

            if (!AhtolaReadOnlySqlClassifier.IsProvenReadOnlyScript(script))
                continue;

            foreach (var statement in SplitLikeProduction(script))
            {
                var trimmed = statement.TrimStart();
                foreach (var keyword in new[] { "INSERT", "DELETE", "PRAGMA", "UPDATE", "DROP" })
                {
                    trimmed.Should().NotStartWith(
                        keyword,
                        $"a proven read-only script must not split into a '{keyword}' statement: {Describe(script)}");
                }
            }
        }
    }

    /// <summary>
    /// Mirrors the production splitters: strip comments (terminating a line comment at CR or LF),
    /// then split on top-level semicolons.
    /// </summary>
    private static List<string> SplitLikeProduction(string sql)
    {
        var stripped = new System.Text.StringBuilder(sql.Length);
        var index = 0;
        while (index < sql.Length)
        {
            if (sql[index] == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                index += 2;
                while (index < sql.Length && sql[index] is not '\r' and not '\n')
                    index++;
                continue;
            }
            if (sql[index] == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                var end = sql.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = end < 0 ? sql.Length : end + 2;
                continue;
            }

            stripped.Append(sql[index]);
            index++;
        }

        return [.. stripped.ToString().Split(';')];
    }

    private static string Describe(string sql)
        => sql.Replace("\r", "\\r", StringComparison.Ordinal)
              .Replace("\n", "\\n", StringComparison.Ordinal);

    [Test]
    public void RefusesSemicolonInsideParentheses()
        => AhtolaReadOnlySqlClassifier
            .IsProvenReadOnlyScript("SELECT (1; 2)")
            .Should().BeFalse();

    [Test]
    public void ProvesIdentifiersThatMerelyContainKeywords()
    {
        AhtolaReadOnlySqlClassifier
            .IsProvenReadOnlyScript("SELECT insert_count, update_count, deleted_at FROM audit_updates")
            .Should().BeTrue();
        AhtolaReadOnlySqlClassifier
            .IsProvenReadOnlyScript("SELECT * FROM creates")
            .Should().BeTrue();
    }

    /// <summary>
    /// Random text must never be proven read-only, and every generated script that
    /// contains a mutating keyword outside a literal must be refused. The generator
    /// is seeded so a failure reproduces exactly.
    /// </summary>
    [Test]
    public void FuzzedScriptsAreNeverProvenWhenTheyCanMutate()
    {
        var random = new Random(20260824);
        string[] fragments =
        [
            "SELECT", "VALUES", "WITH", "cte", "AS", "(", ")", ",", "1", "*", "FROM", "probe",
            "INSERT", "UPDATE", "DELETE", "PRAGMA", "BEGIN", "COMMIT", "ATTACH", "DROP",
            "'literal'", "\"quoted\"", ";", "--x\n", "/*c*/", "$p", "x'00'", "[bracket]",
        ];

        string[] mutating = ["INSERT", "UPDATE", "DELETE", "PRAGMA", "BEGIN", "COMMIT", "ATTACH", "DROP"];
        for (var iteration = 0; iteration < 4000; iteration++)
        {
            var length = random.Next(1, 12);
            var builder = new System.Text.StringBuilder();
            for (var index = 0; index < length; index++)
            {
                builder.Append(fragments[random.Next(fragments.Length)]);
                builder.Append(' ');
            }

            var script = builder.ToString();
            if (!AhtolaReadOnlySqlClassifier.IsProvenReadOnlyScript(script))
                continue;

            foreach (var keyword in mutating)
            {
                script.Should().NotContain(
                    keyword,
                    $"a script proven read-only must not contain '{keyword}': {script}");
            }
        }
    }
}
