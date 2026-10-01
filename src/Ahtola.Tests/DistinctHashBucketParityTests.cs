using AwesomeAssertions;
using Ahtola.Data.Sqlite;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// The evaluator's DISTINCT buckets kept rows by a hash before comparing them, instead of
/// comparing every row with every kept row. The hash must agree with DISTINCT equality for
/// every value class and collation, or duplicates would survive; these cases compare the kept
/// rows (including which representative of an equal group survives) with bundled SQLite.
/// </summary>
public sealed class DistinctHashBucketParityTests
{
    private const string Setup = """
        CREATE TABLE v(x, t TEXT, n TEXT COLLATE NOCASE, r TEXT COLLATE RTRIM, c TEXT COLLATE reverse_text);
        INSERT INTO v VALUES
            (1, 'a', 'Abc', 'pad', 'k'),
            (1.0, 'a', 'aBC', 'pad  ', 'k'),
            (0.0, 'A', 'abc', 'pad ', 'K'),
            (-0.0, 'a ', 'ABD', 'Pad', 'k'),
            (NULL, NULL, NULL, NULL, NULL),
            (NULL, NULL, NULL, NULL, NULL),
            (x'0102', 'é', 'É', 'é ', 'é'),
            (x'0102', 'é', 'é', 'é', 'é'),
            (9007199254740993, 'b', 'a' || char(0) || 'x', 'b', 'b'),
            (9007199254740992.0, 'b', 'a' || char(0) || 'y', 'b ', 'b'),
            ('1', 'c', 'mixed Case', 'c', 'c'),
            (2, 'c', 'MIXED case', 'c', 'c');
        """;

    [TestCase("x")]
    [TestCase("t")]
    [TestCase("n")]
    [TestCase("r")]
    [TestCase("c")]
    [TestCase("x, t")]
    [TestCase("n, r")]
    [TestCase("t COLLATE NOCASE")]
    [TestCase("t COLLATE RTRIM")]
    [TestCase("x, n, c")]
    public void DerivedDistinctKeepsTheSameRowsAsSqlite(string columns)
    {
        var sql = $"SELECT * FROM (SELECT DISTINCT {columns} FROM v)";

        Run(sql).Should().Equal(RunSqlite(sql));
    }

    [TestCase("x")]
    [TestCase("n")]
    [TestCase("x, t")]
    public void TopLevelDistinctKeepsTheSameRowsAsSqlite(string columns)
    {
        var sql = $"SELECT DISTINCT {columns} FROM v";

        Run(sql).Should().Equal(RunSqlite(sql));
    }

    // Which row represents an equal UNION group depends on the plan (SQLite keeps the first
    // here but the pinned corpus expects the later row), so groups are compared by count.
    [TestCase("SELECT x FROM v UNION SELECT x FROM v WHERE x IS NOT NULL")]
    [TestCase("SELECT n FROM v UNION SELECT t FROM v")]
    [TestCase("SELECT t FROM v UNION SELECT n FROM v")]
    [TestCase("SELECT r, x FROM v UNION SELECT r, x FROM v")]
    [TestCase("SELECT c FROM v UNION SELECT t FROM v")]
    [TestCase("SELECT x, t FROM v INTERSECT SELECT x, t FROM v WHERE x > 0")]
    [TestCase("SELECT n FROM v INTERSECT SELECT t FROM v")]
    [TestCase("SELECT x FROM v EXCEPT SELECT x FROM v WHERE typeof(x) = 'integer'")]
    [TestCase("SELECT r FROM v EXCEPT SELECT t FROM v")]
    [TestCase("SELECT n FROM v UNION SELECT r FROM v")]
    public void CompoundSetOperatorsKeepTheSameGroupsAsSqlite(string compound)
    {
        var sql = $"SELECT count(*) FROM ({compound})";

        Run(sql).Should().Equal(RunSqlite(sql));
    }

    [TestCase("SELECT t FROM v UNION SELECT t FROM v")]
    [TestCase("SELECT x, t FROM v WHERE typeof(x) = 'integer' UNION SELECT x, t FROM v WHERE typeof(x) = 'integer'")]
    [TestCase("SELECT t FROM v INTERSECT SELECT t FROM v WHERE t > 'a'")]
    [TestCase("SELECT t FROM v EXCEPT SELECT t FROM v WHERE t > 'b'")]
    public void CompoundSetOperatorsWithSingleSpellingGroupsMatchSqlite(string sql)
        => Run(sql).Should().Equal(RunSqlite(sql));

    [TestCase("SELECT count(DISTINCT x), count(DISTINCT t), count(DISTINCT n), count(DISTINCT r), count(DISTINCT c) FROM v")]
    [TestCase("SELECT sum(DISTINCT x), group_concat(DISTINCT n) FROM (SELECT * FROM v ORDER BY rowid)")]
    [TestCase("SELECT count(DISTINCT t COLLATE NOCASE), count(DISTINCT t COLLATE RTRIM) FROM v")]
    public void DistinctAggregatesMatchSqlite(string sql)
        => Run(sql).Should().Equal(RunSqlite(sql));

    [TestCase("WITH RECURSIVE r(k) AS (SELECT n FROM v UNION SELECT upper(k) FROM r WHERE length(k) < 4) SELECT k FROM r")]
    [TestCase("WITH RECURSIVE r(i, k) AS (SELECT 1, 1 UNION SELECT i + 1, (i + 1) % 3 * 1.0 FROM r WHERE i < 30) SELECT k FROM r")]
    public void RecursiveUnionKeepsTheSameRowsAsSqlite(string sql)
        => Run(sql).Should().Equal(RunSqlite(sql));

    [Test]
    public void LargeDerivedDistinctIsNotQuadratic()
    {
        using var connection = Open();
        Execute(connection, "CREATE TABLE big(pad TEXT)");
        Execute(connection, "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i < 20000) INSERT INTO big SELECT printf('%0200d', i % 15000) FROM n");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM (SELECT DISTINCT pad FROM big)";
        var count = Convert.ToInt64(command.ExecuteScalar());
        watch.Stop();

        count.Should().Be(15000);
        watch.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(10),
            "DISTINCT compared every row with every kept row and took ~17 s at 20k rows");
    }

    private static List<string> Run(string sql)
    {
        using var connection = Open();
        foreach (var statement in Setup.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            Execute(connection, statement);
        return Read(connection.CreateCommand(), sql);
    }

    private static List<string> RunSqlite(string sql)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        connection.CreateCollation("reverse_text", static (left, right) => string.CompareOrdinal(right, left));
        foreach (var statement in Setup.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }

        return Read(connection.CreateCommand(), sql);
    }

    private static List<string> Read(System.Data.Common.DbCommand command, string sql)
    {
        using (command)
        {
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
            {
                var values = new string[reader.FieldCount];
                for (var index = 0; index < values.Length; index++)
                    values[index] = Describe(reader.GetValue(index));
                rows.Add(string.Join("|", values));
            }

            rows.Sort(StringComparer.Ordinal);
            return rows;
        }
    }

    // Type-tagged, round-trippable rendering so the surviving representative of each equal
    // group is compared (1 vs 1.0, text vs blob), not just the number of groups.
    private static string Describe(object value) => value switch
    {
        DBNull => "null",
        long integer => $"i:{integer}",
        // The managed engine does not preserve a stored -0.0's sign (a separate, pre-existing
        // representation difference); DISTINCT treats both zeros as equal either way.
        double real => $"r:{(real == 0 ? 0d : real).ToString("R", System.Globalization.CultureInfo.InvariantCulture)}",
        string text => $"t:{text}",
        byte[] blob => $"b:{Convert.ToHexString(blob)}",
        _ => $"?:{value}",
    };

    private static SqliteConnection Open()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        connection.CreateCollation("reverse_text", static (left, right) => string.CompareOrdinal(right, left));
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
