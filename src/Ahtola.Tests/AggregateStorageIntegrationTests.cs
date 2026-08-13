using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class AggregateStorageIntegrationTests
{
    [Test]
    public void WithoutRowidClusterOrderControlsCompiledRepresentativeRowsAfterReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "aggregate-without-rowid.db";
        const string grouped =
            "SELECT tenant, label, count(*), sum(value) FROM entries GROUP BY tenant;";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var setupConnection = database.Connect())
        {
            Execute(
                setupConnection,
                """
                CREATE TABLE entries(
                    tenant TEXT COLLATE NOCASE,
                    sequence INTEGER,
                    label TEXT,
                    value INTEGER,
                    PRIMARY KEY(tenant COLLATE NOCASE DESC, sequence ASC)
                ) WITHOUT ROWID;
                """);
            Execute(
                setupConnection,
                "CREATE INDEX entries_rich "
                    + "ON entries(label COLLATE RTRIM DESC, value ASC);");
            Execute(
                setupConnection,
                """
                INSERT INTO entries VALUES
                    ('a', 2, 'a-two', 20),
                    ('B', 1, 'b-one', 10),
                    ('A', 1, 'a-one', 30);
                """);

            ReadRows(setupConnection, "SELECT label, count(*) FROM entries;")[0]
                .Should().Equal(SqlValue.Text("b-one"), SqlValue.Integer(3));
            ReadRows(setupConnection, "SELECT label, max(value) FROM entries;")[0]
                .Should().Equal(SqlValue.Text("a-one"), SqlValue.Integer(30));
            AssertGroupedRows(ReadRows(setupConnection, grouped));
            AssertCompiled(setupConnection, grouped);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var reopenedConnection = reopened.Connect();
        ReadRows(reopenedConnection, "SELECT label, count(*) FROM entries;")[0]
            .Should().Equal(SqlValue.Text("b-one"), SqlValue.Integer(3));
        ReadRows(reopenedConnection, "SELECT label, max(value) FROM entries;")[0]
            .Should().Equal(SqlValue.Text("a-one"), SqlValue.Integer(30));
        AssertGroupedRows(ReadRows(reopenedConnection, grouped));
        ReadRows(
                reopenedConnection,
                "SELECT (SELECT count(*) FROM "
                    + "(SELECT entries.tenant UNION SELECT 'b')) "
                    + "FROM entries WHERE label = 'b-one';")[0][0]
            .Should().Be(SqlValue.Integer(1));
        AssertCompiled(reopenedConnection, grouped);
    }

    [Test]
    public void ForeignKeyCascadeReopenFeedsCompiledCollatedGroups()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "aggregate-foreign-key.db";
        const string grouped =
            "SELECT parent_code, count(*), sum(amount) FROM child GROUP BY parent_code;";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "PRAGMA foreign_keys = ON;");
            Execute(
                connection,
                """
                CREATE TABLE parent(
                    code TEXT COLLATE NOCASE PRIMARY KEY
                ) WITHOUT ROWID;
                """);
            Execute(
                connection,
                """
                CREATE TABLE child(
                    id INTEGER PRIMARY KEY,
                    parent_code TEXT COLLATE NOCASE
                        REFERENCES parent(code) ON UPDATE CASCADE,
                    amount INTEGER
                );
                """);
            Execute(connection, "INSERT INTO parent VALUES ('A'), ('B');");
            Execute(
                connection,
                "INSERT INTO child VALUES (1, 'A', 2), (2, 'a', 3), (3, 'B', 7);");
        }

        using (var reopened = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = reopened.Connect())
        {
            Execute(connection, "PRAGMA foreign_keys = ON;");
            Execute(connection, "UPDATE parent SET code = 'C' WHERE code = 'a';");
            var rows = ReadRows(connection, grouped);
            rows.Should().HaveCount(2);
            rows[0].Should().Equal(
                SqlValue.Text("B"),
                SqlValue.Integer(1),
                SqlValue.Integer(7));
            rows[1].Should().Equal(
                SqlValue.Text("C"),
                SqlValue.Integer(2),
                SqlValue.Integer(5));
            AssertCompiled(connection, grouped);
        }

        using var verified = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var verifiedConnection = verified.Connect();
        ReadRows(verifiedConnection, grouped)[0]
            .Should().Equal(
                SqlValue.Text("B"),
                SqlValue.Integer(1),
                SqlValue.Integer(7));
    }

    [Test]
    public void OverflowedSchemaAndRichIndexReopenWithCompiledAggregates()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "aggregate-schema-overflow.db";
        var payload = new string('s', 6_000);
        const string grouped =
            "SELECT group_key, count(*), sum(doubled), max(length(payload)) "
            + "FROM wide GROUP BY group_key;";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var setupConnection = database.Connect())
        {
            Execute(
                setupConnection,
                $"""
                CREATE TABLE wide(
                    id INTEGER PRIMARY KEY,
                    group_key TEXT COLLATE NOCASE,
                    amount INTEGER,
                    payload TEXT DEFAULT '{payload}',
                    doubled INTEGER GENERATED ALWAYS AS (amount * 2) VIRTUAL
                );
                """);
            Execute(
                setupConnection,
                "CREATE INDEX wide_rich "
                    + "ON wide(group_key COLLATE NOCASE DESC, doubled ASC);");
            Execute(
                setupConnection,
                """
                INSERT INTO wide(id, group_key, amount) VALUES
                    (1, 'Alpha', 1),
                    (2, 'alpha', 2),
                    (3, 'Beta', 3);
                """);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var connection = reopened.Connect();
        ReadRows(
                connection,
                "SELECT length(sql) FROM sqlite_schema WHERE type = 'table' AND name = 'wide';")[0][0]
            .AsInteger()
            .Should().BeGreaterThan(4_096);

        var rows = ReadRows(connection, grouped);
        rows.Should().HaveCount(2);
        rows[0].Should().Equal(
            SqlValue.Text("Alpha"),
            SqlValue.Integer(2),
            SqlValue.Integer(6),
            SqlValue.Integer(payload.Length));
        rows[1].Should().Equal(
            SqlValue.Text("Beta"),
            SqlValue.Integer(1),
            SqlValue.Integer(6),
            SqlValue.Integer(payload.Length));
        AssertCompiled(connection, grouped);
    }

    // Group rows are emitted in ascending key order (the managed engine always aggregates
    // through its sorter; unlike SQLite it does not skip the sorter when a DESC clustering
    // index already provides group order). The bare-column representatives stay driven by
    // cluster scan order within each group, which is what this helper pins.
    private static void AssertGroupedRows(IReadOnlyList<SqlValue[]> rows)
    {
        rows.Should().HaveCount(2);
        rows[0].Should().Equal(
            SqlValue.Text("A"),
            SqlValue.Text("a-one"),
            SqlValue.Integer(2),
            SqlValue.Integer(50));
        rows[1].Should().Equal(
            SqlValue.Text("B"),
            SqlValue.Text("b-one"),
            SqlValue.Integer(1),
            SqlValue.Integer(10));
    }

    private static void AssertCompiled(EmbeddedConnection connection, string sql)
    {
        ReadRows(connection, "EXPLAIN " + sql)
            .Select(row => row[1].AsText())
            .Should().Contain("GroupKey").And.Contain("AggFinalize");
        ReadRows(connection, "EXPLAIN QUERY PLAN " + sql)[0][3]
            .Should().Be(SqlValue.Text("MANAGED COMPILED VDBE"));
    }

    private static List<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var values = new SqlValue[statement.GetColumnCount()];
            for (var ordinal = 0; ordinal < values.Length; ordinal++)
                values[ordinal] = statement.GetValue(ordinal);
            rows.Add(values);
        }

        return rows;
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }
}
