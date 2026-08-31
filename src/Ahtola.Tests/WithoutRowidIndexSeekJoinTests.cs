using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

/// <summary>
/// Focused coverage for the WITHOUT ROWID leg of durable join index seeks: committed,
/// autocommit, file-backed WITHOUT ROWID tables must join through the pager b-tree cursor
/// (<see cref="VdbeJoinIndexSeekMetrics.DurableCursorPlans"/>, zero
/// <see cref="VdbeJoinIndexSeekMetrics.IndexRowsMaterialized"/>) for both an equality seek on
/// the table's own primary-key b-tree and a seek through one of its secondary indexes,
/// instead of ever materializing an in-memory index view.
/// </summary>
public sealed class WithoutRowidIndexSeekJoinTests
{
    [Test]
    public void DurableReopenUsesPagerSeekForPrimaryKeyJoin()
    {
        string[] setup =
        [
            "CREATE TABLE outer_items(code TEXT);",
            "CREATE TABLE entry(code TEXT PRIMARY KEY, payload TEXT) WITHOUT ROWID;",
            "INSERT INTO outer_items VALUES ('key-00002'), ('key-00300');",
            "INSERT INTO entry VALUES "
                + string.Join(", ", Enumerable.Range(1, 500).Select(value => $"('key-{value:D5}', 'p{value}')"))
                + ";",
            "ANALYZE;",
        ];
        const string sql =
            """
            SELECT entry.payload
            FROM outer_items JOIN entry
            ON outer_items.code = entry.code
            ORDER BY outer_items.code;
            """;

        AssertMatchesSqlite(setup, sql);

        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("without-rowid-pk-join.db", fileSystem))
        using (var connection = database.Connect())
        {
            foreach (var statement in setup)
                Execute(connection, statement);
        }

        using var reopened = EmbeddedDatabase.OpenFile("without-rowid-pk-join.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadRows(reopenedConnection, "EXPLAIN QUERY PLAN " + sql).Single()[3].AsText()
            .Should().Be("SEARCH entry USING COVERING INDEX sqlite_autoindex_entry_1 (code=?)");

        reopened.ResetJoinOrderDiagnostics();
        ReadRows(reopenedConnection, sql).Select(row => row[0].AsText()).Should().Equal("p2", "p300");
        reopened.JoinIndexSeekMetrics.DurableCursorPlans.Should().BeGreaterThan(0);
        reopened.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);
    }

    [Test]
    public void DurableReopenUsesPagerSeekForSecondaryIndexJoin()
    {
        string[] setup =
        [
            "CREATE TABLE outer_items(tag TEXT);",
            "CREATE TABLE entry(code TEXT PRIMARY KEY, tag TEXT) WITHOUT ROWID;",
            "CREATE INDEX entry_tag ON entry(tag);",
            "INSERT INTO outer_items VALUES ('t-00002'), ('t-00300');",
            "INSERT INTO entry VALUES "
                + string.Join(
                    ", ",
                    Enumerable.Range(1, 500).Select(value => $"('key-{value:D5}', 't-{value:D5}')"))
                + ";",
            "ANALYZE;",
        ];
        // entry_tag's storage columns (its own column + the appended PK suffix) are tag and code,
        // which is every column the table has -- so this candidate is fully covering.
        const string sql =
            """
            SELECT entry.code
            FROM outer_items JOIN entry INDEXED BY entry_tag
            ON outer_items.tag = entry.tag
            ORDER BY outer_items.tag;
            """;

        AssertMatchesSqlite(setup, sql);

        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("without-rowid-secondary-join.db", fileSystem))
        using (var connection = database.Connect())
        {
            foreach (var statement in setup)
                Execute(connection, statement);
        }

        using var reopened = EmbeddedDatabase.OpenFile("without-rowid-secondary-join.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadRows(reopenedConnection, "EXPLAIN QUERY PLAN " + sql).Single()[3].AsText()
            .Should().Be("SEARCH entry USING COVERING INDEX entry_tag (tag=?)");

        reopened.ResetJoinOrderDiagnostics();
        ReadRows(reopenedConnection, sql).Select(row => row[0].AsText()).Should().Equal("key-00002", "key-00300");
        reopened.JoinIndexSeekMetrics.DurableCursorPlans.Should().BeGreaterThan(0);
        reopened.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);
    }

    [Test]
    public void DurableReopenUsesPagerSeekForNonCoveringSecondaryIndexJoin()
    {
        string[] setup =
        [
            "CREATE TABLE outer_items(tag TEXT);",
            "CREATE TABLE entry(code TEXT PRIMARY KEY, tag TEXT, payload TEXT, extra TEXT) WITHOUT ROWID;",
            "CREATE INDEX entry_tag ON entry(tag);",
            "INSERT INTO outer_items VALUES ('t-00002'), ('t-00300');",
            "INSERT INTO entry VALUES "
                + string.Join(
                    ", ",
                    Enumerable.Range(1, 500)
                        .Select(value => $"('key-{value:D5}', 't-{value:D5}', 'p{value}', 'x{value}')"))
                + ";",
            "ANALYZE;",
        ];
        // entry.extra is not part of entry_tag's storage columns (index columns + PK suffix), so
        // this join must stay non-covering and still fetch the table root row.
        const string sql =
            """
            SELECT entry.payload, entry.extra
            FROM outer_items JOIN entry INDEXED BY entry_tag
            ON outer_items.tag = entry.tag
            ORDER BY outer_items.tag;
            """;

        AssertMatchesSqlite(setup, sql);

        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("without-rowid-noncovering-join.db", fileSystem))
        using (var connection = database.Connect())
        {
            foreach (var statement in setup)
                Execute(connection, statement);
        }

        using var reopened = EmbeddedDatabase.OpenFile("without-rowid-noncovering-join.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadRows(reopenedConnection, "EXPLAIN QUERY PLAN " + sql).Single()[3].AsText()
            .Should().Be("SEARCH entry USING INDEX entry_tag (tag=?)");

        reopened.ResetJoinOrderDiagnostics();
        ReadRows(reopenedConnection, sql).Select(row => (row[0].AsText(), row[1].AsText()))
            .Should().Equal(("p2", "x2"), ("p300", "x300"));
        reopened.JoinIndexSeekMetrics.DurableCursorPlans.Should().BeGreaterThan(0);
        reopened.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);
    }

    [Test]
    public void DurableReopenUsesPagerSeekForCompositePrimaryKeySuffixLookup()
    {
        string[] setup =
        [
            "CREATE TABLE outer_items(part TEXT);",
            "CREATE TABLE entry(part TEXT, seq INTEGER, payload TEXT, PRIMARY KEY(part, seq)) WITHOUT ROWID;",
            "INSERT INTO outer_items VALUES ('a'), ('c');",
            "INSERT INTO entry VALUES "
                + string.Join(
                    ", ",
                    new[] { "a", "b", "c" }.SelectMany(part =>
                        Enumerable.Range(1, 100).Select(seq => $"('{part}', {seq}, 'p-{part}-{seq}')")))
                + ";",
            "ANALYZE;",
        ];
        // Only the leading PK term (part) is bound by the join predicate; seq is unconstrained,
        // so the durable seek must use a one-column prefix of the composite two-column PK.
        const string sql =
            """
            SELECT entry.seq, entry.payload
            FROM outer_items JOIN entry
            ON outer_items.part = entry.part
            ORDER BY outer_items.part, entry.seq;
            """;

        AssertMatchesSqlite(setup, sql);

        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("without-rowid-composite-pk-join.db", fileSystem))
        using (var connection = database.Connect())
        {
            foreach (var statement in setup)
                Execute(connection, statement);
        }

        using var reopened = EmbeddedDatabase.OpenFile("without-rowid-composite-pk-join.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadRows(reopenedConnection, "EXPLAIN QUERY PLAN " + sql).Single()[3].AsText()
            .Should().Be("SEARCH entry USING COVERING INDEX sqlite_autoindex_entry_1 (part=?)");

        reopened.ResetJoinOrderDiagnostics();
        var rows = ReadRows(reopenedConnection, sql);
        rows.Should().HaveCount(200);
        rows.Select(row => row[1].AsText()).Take(3).Should().Equal("p-a-1", "p-a-2", "p-a-3");
        reopened.JoinIndexSeekMetrics.DurableCursorPlans.Should().BeGreaterThan(0);
        reopened.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);
    }

    [Test]
    public void DurableReopenUsesPagerSeekWhenSecondaryIndexHasDuplicateValues()
    {
        string[] setup =
        [
            "CREATE TABLE outer_items(tag TEXT);",
            "CREATE TABLE entry(code TEXT PRIMARY KEY, tag TEXT, payload TEXT) WITHOUT ROWID;",
            "CREATE INDEX entry_tag ON entry(tag);",
            "INSERT INTO outer_items VALUES ('shared');",
            // Every row shares the same secondary-index key: the pager seek must land on every
            // matching leaf entry and disambiguate by the PK suffix carried in the index record.
            "INSERT INTO entry VALUES "
                + string.Join(
                    ", ",
                    Enumerable.Range(1, 300).Select(value => $"('key-{value:D5}', 'shared', 'p{value}')"))
                + ";",
            "ANALYZE;",
        ];
        const string sql =
            """
            SELECT entry.payload
            FROM outer_items JOIN entry INDEXED BY entry_tag
            ON outer_items.tag = entry.tag
            ORDER BY entry.payload;
            """;

        AssertMatchesSqlite(setup, sql);

        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("without-rowid-duplicate-secondary.db", fileSystem))
        using (var connection = database.Connect())
        {
            foreach (var statement in setup)
                Execute(connection, statement);
        }

        using var reopened = EmbeddedDatabase.OpenFile("without-rowid-duplicate-secondary.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadRows(reopenedConnection, "EXPLAIN QUERY PLAN " + sql).Single()[3].AsText()
            .Should().Be("SEARCH entry USING INDEX entry_tag (tag=?)");

        reopened.ResetJoinOrderDiagnostics();
        var expected = Enumerable.Range(1, 300).Select(value => $"p{value}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        ReadRows(reopenedConnection, sql).Select(row => row[0].AsText()).Should().Equal(expected);
        reopened.JoinIndexSeekMetrics.DurableCursorPlans.Should().BeGreaterThan(0);
        reopened.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);
    }

    [Test]
    public void PrimaryKeyJoinDeclinesWhenIndexedByNamesADifferentIndex()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("without-rowid-indexed-by-guard.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_items(code TEXT, tag TEXT);");
        Execute(connection, "CREATE TABLE entry(code TEXT PRIMARY KEY, tag TEXT, payload TEXT) WITHOUT ROWID;");
        Execute(connection, "CREATE INDEX entry_tag ON entry(tag);");
        Execute(connection, "INSERT INTO outer_items VALUES ('key-1', 't1'), ('key-2', 't2');");
        Execute(connection, "INSERT INTO entry VALUES ('key-1', 't1', 'p1'), ('key-2', 't2', 'p2');");
        Execute(connection, "ANALYZE;");

        // INDEXED BY names entry_tag explicitly, so the implicit primary-key candidate must be
        // suppressed even though outer_items.code = entry.code would otherwise be eligible.
        const string sql =
            """
            SELECT entry.payload
            FROM outer_items JOIN entry INDEXED BY entry_tag
            ON outer_items.code = entry.code AND outer_items.tag = entry.tag
            ORDER BY entry.payload;
            """;
        ReadRows(connection, "EXPLAIN QUERY PLAN " + sql).Single()[3].AsText()
            .Should().NotContain("sqlite_autoindex_entry_1");
        ReadRows(connection, sql).Select(row => row[0].AsText()).Should().Equal("p1", "p2");
    }

    [Test]
    public void PrimaryKeyJoinWithoutOrderByStillUsesTheCostedPagerSeek()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("without-rowid-pk-no-order.db", fileSystem))
        using (var setupConnection = database.Connect())
        {
            Execute(setupConnection, "CREATE TABLE outer_items(code TEXT);");
            Execute(setupConnection, "CREATE TABLE entry(code TEXT PRIMARY KEY, payload TEXT) WITHOUT ROWID;");
            Execute(setupConnection, "INSERT INTO outer_items VALUES ('key-2');");
            Execute(setupConnection, "INSERT INTO entry VALUES ('key-1','p1'),('key-2','p2');");
            Execute(setupConnection, "ANALYZE;");
            ReadRows(setupConnection, "SELECT idx FROM sqlite_stat1 WHERE tbl='entry';")
                .Single()[0].AsText().Should().Be("entry");
        }

        using var reopened = EmbeddedDatabase.OpenFile("without-rowid-pk-no-order.db", fileSystem);
        using var connection = reopened.Connect();
        const string sql =
            "SELECT entry.payload FROM outer_items JOIN entry ON outer_items.code=entry.code;";
        ReadRows(connection, "EXPLAIN QUERY PLAN " + sql).Single()[3].AsText()
            .Should().Contain("sqlite_autoindex_entry_1");
        reopened.ResetJoinOrderDiagnostics();
        ReadRows(connection, sql).Single()[0].AsText().Should().Be("p2");
        reopened.JoinIndexSeekMetrics.DurableCursorPlans.Should().BeGreaterThan(0);
        reopened.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);
    }

    [Test]
    public void IndexedByMayNameTheWithoutRowidPrimaryKey()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("without-rowid-pk-indexed-by.db", fileSystem))
        using (var setupConnection = database.Connect())
        {
            Execute(setupConnection, "CREATE TABLE outer_items(code TEXT);");
            Execute(setupConnection, "CREATE TABLE entry(code TEXT PRIMARY KEY, payload TEXT) WITHOUT ROWID;");
            Execute(setupConnection, "INSERT INTO outer_items VALUES ('key-2');");
            Execute(setupConnection, "INSERT INTO entry VALUES ('key-1','p1'),('key-2','p2');");
            Execute(setupConnection, "ANALYZE;");
        }

        using var reopened = EmbeddedDatabase.OpenFile("without-rowid-pk-indexed-by.db", fileSystem);
        using var connection = reopened.Connect();
        const string sql =
            """
            SELECT entry.payload
            FROM outer_items JOIN entry INDEXED BY sqlite_autoindex_entry_1
            ON outer_items.code=entry.code;
            """;
        ReadRows(connection, sql).Single()[0].AsText().Should().Be("p2");
        reopened.JoinIndexSeekMetrics.DurableCursorPlans.Should().BeGreaterThan(0);
        reopened.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);
    }

    [Test]
    public void PrimaryKeyJoinDeclinesSearchWhenBinaryCollationIsOverridden()
    {
        // Finding #3 regression: entry's own root page is a WITHOUT ROWID primary-key b-tree, but
        // CreatePrimaryKeyComparer (EmbeddedFileStore) never binds a live collation-resolver
        // override to a primary-key term -- unlike GetIndexCollation for a secondary index -- so
        // the durable b-tree's physical order always stays plain BINARY, permanently. Once an
        // application overrides BINARY, the live evaluator (EmbeddedDatabase.Compare, per the
        // Finding #1 fix) computes "equal"/"less than" using the override, while this table's own
        // root page is still ordered by the untouched built-in comparison -- a seek that assumed
        // the two agreed could silently miss matching rows or return the wrong ones. The implicit
        // primary-key candidate must therefore be withdrawn entirely while any override is active,
        // for both the planner's own automatic choice and an explicit INDEXED BY/NOT INDEXED
        // comparison, and the query must still return correct rows via whatever plan replaces it.
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("without-rowid-pk-overridden-binary.db", fileSystem);
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE outer_items(code TEXT);");
        Execute(connection, "CREATE TABLE entry(code TEXT PRIMARY KEY, payload TEXT) WITHOUT ROWID;");
        Execute(connection, "INSERT INTO outer_items VALUES ('key-00002'), ('key-00300');");
        Execute(
            connection,
            "INSERT INTO entry VALUES "
                + string.Join(", ", Enumerable.Range(1, 500).Select(value => $"('key-{value:D5}', 'p{value}')"))
                + ";");
        Execute(connection, "ANALYZE;");

        const string defaultSql =
            """
            SELECT entry.payload
            FROM outer_items JOIN entry
            ON outer_items.code = entry.code
            ORDER BY outer_items.code;
            """;
        const string notIndexedSql =
            """
            SELECT entry.payload
            FROM outer_items JOIN entry NOT INDEXED
            ON outer_items.code = entry.code
            ORDER BY outer_items.code;
            """;

        // Baseline, no override yet: the implicit primary-key candidate is offered and used, so
        // the freely-planned query genuinely searches while the NOT INDEXED one does not.
        ReadRows(connection, "EXPLAIN QUERY PLAN " + defaultSql).Single()[3].AsText()
            .Should().Be("SEARCH entry USING COVERING INDEX sqlite_autoindex_entry_1 (code=?)");
        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, defaultSql).Select(row => row[0].AsText()).Should().Equal("p2", "p300");
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().BeGreaterThan(0);

        database.RegisterCollation(
            "BINARY",
            static (left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));

        // With BINARY overridden, neither the planner's own automatic choice nor an explicit NOT
        // INDEXED scan may ever show a SEARCH plan against entry's primary-key b-tree, and the two
        // must agree on results -- proving the withdrawal, not just a coincidentally-matching
        // fallback plan shape.
        ReadRows(connection, "EXPLAIN QUERY PLAN " + defaultSql)
            .Select(row => row[3].AsText())
            .Should().NotContain(value => value.StartsWith("SEARCH ", StringComparison.Ordinal));
        ReadRows(connection, "EXPLAIN QUERY PLAN " + notIndexedSql)
            .Select(row => row[3].AsText())
            .Should().NotContain(value => value.StartsWith("SEARCH ", StringComparison.Ordinal));
        ReadRows(connection, defaultSql).Select(row => row[0].AsText()).Should().Equal("p2", "p300");
        ReadRows(connection, notIndexedSql).Select(row => row[0].AsText()).Should().Equal("p2", "p300");
    }

    private static void AssertMatchesSqlite(IReadOnlyList<string> setup, string sql)
    {
        using var managed = OpenManaged(setup);
        var managedRows = ReadRows(managed, sql);
        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var statement in setup)
        {
            using var command = sqlite.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }

        using var query = sqlite.CreateCommand();
        query.CommandText = sql;
        using var reader = query.ExecuteReader();
        var sqliteRows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var index = 0; index < row.Length; index++)
                row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            sqliteRows.Add(row);
        }

        managedRows.Should().HaveCount(sqliteRows.Count);
        for (var row = 0; row < sqliteRows.Count; row++)
        {
            for (var column = 0; column < sqliteRows[row].Length; column++)
                CellShouldMatch(managedRows[row][column], sqliteRows[row][column], row, column);
        }
    }

    private static EmbeddedConnection OpenManaged(IReadOnlyList<string> setup)
    {
        var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);
        return connection;
    }

    private static void CellShouldMatch(SqlValue managed, object? sqlite, int row, int column)
    {
        switch (sqlite)
        {
            case null:
                managed.Should().Be(SqlValue.Null, $"at row {row}, column {column}");
                break;
            case long integer:
                managed.Should().Be(SqlValue.Integer(integer), $"at row {row}, column {column}");
                break;
            case double real:
                managed.Should().Be(SqlValue.Real(real), $"at row {row}, column {column}");
                break;
            case string text:
                managed.Should().Be(SqlValue.Text(text), $"at row {row}, column {column}");
                break;
            case byte[] blob:
                managed.AsBlob().ToArray().Should().Equal(blob);
                break;
            default:
                throw new InvalidOperationException($"Unsupported SQLite value type {sqlite.GetType()}.");
        }
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
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
}
