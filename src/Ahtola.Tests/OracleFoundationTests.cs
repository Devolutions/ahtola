using System.Data.Common;
using System.Globalization;
using AwesomeAssertions;
using Ahtola.Data.Sqlite;
using Ahtola.Tests.Oracle;
using ReferenceSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace Ahtola.Tests;

public sealed class OracleFoundationTests
{
    private const ulong DefaultRootSeed = 0xa4701a20260827UL;

    [Test]
    public void GeneratedTypedSequenceMatchesSqlite()
    {
        var stream = Stream();
        var trace = ReplayTrace.Create(TestContext.CurrentContext.Test.Name, stream);

        OracleFailureArtifacts.Run(trace, () =>
        {
            using var managed = OpenManaged();
            using var reference = OpenReference();
            Differential(
                managed,
                reference,
                trace,
                """
                CREATE TABLE generated_values(
                    id INTEGER PRIMARY KEY,
                    bucket INTEGER,
                    score REAL,
                    label TEXT,
                    payload BLOB
                );
                """);

            for (var id = 1; id <= 24; id++)
            {
                var bucket = stream.Random.NextInt32(5) == 0
                    ? "NULL"
                    : stream.Random.NextInt32(-3, 4).ToString(CultureInfo.InvariantCulture);
                var score = (stream.Random.NextInt32(-4096, 4097) / 8d)
                    .ToString("R", CultureInfo.InvariantCulture);
                var labels = new[] { "alpha", "βeta", "quote's", string.Empty, "repeat" };
                var label = SqlText(labels[stream.Random.NextInt32(labels.Length)]);
                var payload = stream.Random.NextInt32(4) == 0
                    ? "NULL"
                    : $"X'{Convert.ToHexString(stream.Random.NextBytes(stream.Random.NextInt32(0, 7)))}'";
                Differential(
                    managed,
                    reference,
                    trace,
                    $"INSERT INTO generated_values VALUES ({id}, {bucket}, {score}, {label}, {payload});");

                if (id % 6 == 0)
                {
                    Differential(
                        managed,
                        reference,
                        trace,
                        "SELECT bucket, score, label, payload FROM generated_values "
                        + $"WHERE id <= {id} AND (bucket IS NULL OR bucket >= 0);",
                        ordered: false);
                }
            }

            var selectedBucket = stream.Random.NextInt32(-3, 4);
            Differential(
                managed,
                reference,
                trace,
                $"UPDATE generated_values SET score = score + 0.5 WHERE bucket = {selectedBucket};");
            Differential(
                managed,
                reference,
                trace,
                "SELECT id, bucket, score, label, payload, "
                + "typeof(bucket), typeof(score), typeof(label), typeof(payload) "
                + "FROM generated_values ORDER BY id;");

            Differential(
                managed,
                reference,
                trace,
                "SELECT missing_column FROM generated_values;");

            trace.Add(
                "SELECT rowid AS __oracle_rowid, * FROM generated_values ORDER BY rowid;",
                "ordered table snapshot");
            TypedSqliteOracle.AssertEquivalent(
                TypedSqliteOracle.TableSnapshot(managed, "generated_values"),
                TypedSqliteOracle.TableSnapshot(reference, "generated_values"),
                ordered: true,
                stream.Diagnostics);

            trace.Add(
                "SELECT type, name, tbl_name, sql FROM sqlite_schema "
                + "WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name;",
                "ordered schema snapshot");
            TypedSqliteOracle.AssertEquivalent(
                TypedSqliteOracle.SchemaSnapshot(managed),
                TypedSqliteOracle.SchemaSnapshot(reference),
                ordered: true,
                stream.Diagnostics);

            trace.Add("PRAGMA integrity_check;", "integrity");
            TypedSqliteOracle.AssertIntegrity(managed, stream.Diagnostics);
            TypedSqliteOracle.AssertIntegrity(reference, stream.Diagnostics);
        });
    }

    [Test]
    public void TernaryLogicPartitionsEveryGeneratedRow()
    {
        var stream = Stream();
        var trace = ReplayTrace.Create(TestContext.CurrentContext.Test.Name, stream);

        OracleFailureArtifacts.Run(trace, () =>
        {
            using var connection = OpenManaged();
            Execute(connection, trace, "CREATE TABLE tlp(id INTEGER PRIMARY KEY, left_value INTEGER, right_value INTEGER, label TEXT);");
            SeedRows(connection, trace, stream, "tlp", 32);

            const string predicate = "(left_value < right_value OR label = 'hit')";
            var all = Query(connection, trace, "SELECT id FROM tlp;", "row bag", ordered: false);
            var partitions = Query(
                connection,
                trace,
                $"""
                SELECT id FROM tlp WHERE {predicate}
                UNION ALL
                SELECT id FROM tlp WHERE NOT ({predicate})
                UNION ALL
                SELECT id FROM tlp WHERE ({predicate}) IS NULL;
                """,
                "TLP row bag",
                ordered: false);

            TypedSqliteOracle.AssertEquivalent(all, partitions, ordered: false, stream.Diagnostics);
        });
    }

    [Test]
    public void IndexedAndNotIndexedPlansReturnTheSameRowBag()
    {
        var stream = Stream();
        var trace = ReplayTrace.Create(TestContext.CurrentContext.Test.Name, stream);

        OracleFailureArtifacts.Run(trace, () =>
        {
            using var connection = OpenManaged();
            Execute(connection, trace, "CREATE TABLE indexed_rows(id INTEGER PRIMARY KEY, bucket INTEGER, value INTEGER, label TEXT);");
            SeedRows(connection, trace, stream, "indexed_rows", 40);
            Execute(connection, trace, "CREATE INDEX ix_indexed_rows_bucket_value ON indexed_rows(bucket, value);");

            var bucket = stream.Random.NextInt32(-3, 4);
            var indexed = Query(
                connection,
                trace,
                $"SELECT bucket, value, label FROM indexed_rows INDEXED BY ix_indexed_rows_bucket_value WHERE bucket = {bucket};",
                "indexed row bag",
                ordered: false);
            var scanned = Query(
                connection,
                trace,
                $"SELECT bucket, value, label FROM indexed_rows NOT INDEXED WHERE bucket = {bucket};",
                "NOT INDEXED row bag",
                ordered: false);

            TypedSqliteOracle.AssertEquivalent(indexed, scanned, ordered: false, stream.Diagnostics);
        });
    }

    [Test]
    public void FailedWriteLeavesThePreStatementSnapshot()
    {
        var stream = Stream();
        var trace = ReplayTrace.Create(TestContext.CurrentContext.Test.Name, stream);

        OracleFailureArtifacts.Run(trace, () =>
        {
            using var connection = OpenManaged();
            Execute(connection, trace, "CREATE TABLE atomic_rows(id INTEGER PRIMARY KEY, code TEXT UNIQUE);");
            Execute(connection, trace, "INSERT INTO atomic_rows VALUES (1, 'one'), (2, 'two');");
            var before = Query(connection, trace, "SELECT id, code FROM atomic_rows ORDER BY id;", "before failed write");

            var failed = Query(
                connection,
                trace,
                "INSERT INTO atomic_rows VALUES (3, 'three'), (4, 'one');",
                "expected constraint error");
            failed.Kind.Should().Be(OracleExecutionKind.Error, because: stream.Diagnostics);

            var after = Query(connection, trace, "SELECT id, code FROM atomic_rows ORDER BY id;", "after failed write");
            TypedSqliteOracle.AssertEquivalent(before, after, ordered: true, stream.Diagnostics);
        });
    }

    [Test]
    public void SavepointRollbackRestoresTheOriginalRows()
    {
        var stream = Stream();
        var trace = ReplayTrace.Create(TestContext.CurrentContext.Test.Name, stream);

        OracleFailureArtifacts.Run(trace, () =>
        {
            using var connection = OpenManaged();
            Execute(
                connection,
                trace,
                "CREATE TABLE savepoint_rows(id INTEGER PRIMARY KEY, value INTEGER, other_value INTEGER, label TEXT);");
            SeedRows(connection, trace, stream, "savepoint_rows", 20);
            var before = Query(
                connection,
                trace,
                "SELECT id, value, other_value, label FROM savepoint_rows ORDER BY id;",
                "before savepoint");

            Execute(connection, trace, "SAVEPOINT generated_changes;");
            Execute(connection, trace, "UPDATE savepoint_rows SET value = value * -3 WHERE id % 2 = 0;");
            Execute(connection, trace, "DELETE FROM savepoint_rows WHERE id % 5 = 0;");
            Execute(connection, trace, "INSERT INTO savepoint_rows VALUES (1001, 42, 84, 'temporary');");
            Execute(connection, trace, "ROLLBACK TO generated_changes;");
            Execute(connection, trace, "RELEASE generated_changes;");

            var after = Query(
                connection,
                trace,
                "SELECT id, value, other_value, label FROM savepoint_rows ORDER BY id;",
                "after rollback");
            TypedSqliteOracle.AssertEquivalent(before, after, ordered: true, stream.Diagnostics);
        });
    }

    [Test]
    public void UnionAllCardinalityIsTheSumOfItsGeneratedTerms()
    {
        var stream = Stream();
        var trace = ReplayTrace.Create(TestContext.CurrentContext.Test.Name, stream);

        OracleFailureArtifacts.Run(trace, () =>
        {
            using var connection = OpenManaged();
            Execute(connection, trace, "CREATE TABLE union_rows(id INTEGER PRIMARY KEY, bucket INTEGER, value INTEGER, label TEXT);");
            SeedRows(connection, trace, stream, "union_rows", 36);

            for (var iteration = 0; iteration < 8; iteration++)
            {
                var firstBucket = stream.Random.NextInt32(-3, 4);
                var threshold = stream.Random.NextInt32(-20, 21);
                var firstSql = $"SELECT id, label FROM union_rows WHERE bucket = {firstBucket}";
                var secondSql = $"SELECT id, label FROM union_rows WHERE value >= {threshold}";
                var first = Query(connection, trace, firstSql + ";", "UNION ALL first term", ordered: false);
                var second = Query(connection, trace, secondSql + ";", "UNION ALL second term", ordered: false);
                var union = Query(
                    connection,
                    trace,
                    firstSql + " UNION ALL " + secondSql + ";",
                    "UNION ALL result",
                    ordered: false);

                union.Kind.Should().Be(OracleExecutionKind.Success, because: stream.Diagnostics);
                union.Rows.Count.Should().Be(first.Rows.Count + second.Rows.Count, because: stream.Diagnostics);
            }
        });
    }

    private static StableRandomStream Stream()
        => StableTestSeed.Create(DefaultRootSeed).Derive(TestContext.CurrentContext.Test.Name);

    private static SqliteConnection OpenManaged()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        return connection;
    }

    private static ReferenceSqliteConnection OpenReference()
    {
        var connection = new ReferenceSqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private static void Differential(
        DbConnection managed,
        DbConnection reference,
        ReplayTrace trace,
        string sql,
        bool ordered = true)
    {
        trace.Add(sql, ordered ? "typed ordered differential" : "typed unordered row-bag differential", ordered);
        TypedSqliteOracle.AssertEquivalent(
            TypedSqliteOracle.Execute(managed, sql),
            TypedSqliteOracle.Execute(reference, sql),
            ordered,
            $"{trace.SeedDiagnostics}; operation={trace.Operations.Count - 1}; SQL={sql}");
    }

    private static void Execute(DbConnection connection, ReplayTrace trace, string sql)
    {
        var result = Query(connection, trace, sql, "managed execution");
        if (result.Kind != OracleExecutionKind.Success)
        {
            throw new AssertionException(
                $"Managed setup failed: category={result.Error!.Category}, code={result.Error.SqliteErrorCode}, "
                + $"message={result.Error.Message}{Environment.NewLine}{trace.SeedDiagnostics}{Environment.NewLine}SQL={sql}");
        }
    }

    private static OracleExecutionResult Query(
        DbConnection connection,
        ReplayTrace trace,
        string sql,
        string comparison,
        bool ordered = true)
    {
        trace.Add(sql, comparison, ordered);
        return TypedSqliteOracle.Execute(connection, sql);
    }

    private static void SeedRows(
        DbConnection connection,
        ReplayTrace trace,
        StableRandomStream stream,
        string table,
        int count)
    {
        for (var id = 1; id <= count; id++)
        {
            var left = stream.Random.NextInt32(5) == 0
                ? "NULL"
                : stream.Random.NextInt32(-12, 13).ToString(CultureInfo.InvariantCulture);
            var right = stream.Random.NextInt32(5) == 0
                ? "NULL"
                : stream.Random.NextInt32(-12, 13).ToString(CultureInfo.InvariantCulture);
            var label = stream.Random.NextInt32(6) == 0 ? "hit" : $"group-{stream.Random.NextInt32(4)}";
            Execute(
                connection,
                trace,
                $"INSERT INTO \"{table}\" VALUES ({id}, {left}, {right}, {SqlText(label)});");
        }
    }

    private static string SqlText(string value)
        => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
