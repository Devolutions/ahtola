using Ahtola.Core;
using Ahtola.Core.Indexing;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Shared harness for the managed index-method suites: in-memory and file-backed databases, a
/// small document corpus, and value-shaped query helpers.
/// </summary>
internal static class ManagedIndexMethodTestHarness
{
    public const string CreateDocuments =
        "CREATE TABLE docs(id INTEGER PRIMARY KEY, title TEXT, body TEXT);";

    public const string CreateFtsIndex =
        "CREATE INDEX docs_fts ON docs USING fts (title, body);";

    public static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    public static IReadOnlyList<SqlValue[]> Query(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var row = new SqlValue[statement.ColumnCount];
            for (var index = 0; index < row.Length; index++)
                row[index] = statement.GetValue(index);
            rows.Add(row);
        }

        return rows;
    }

    public static IReadOnlyList<long> QueryIntegers(EmbeddedConnection connection, string sql)
        => Query(connection, sql).Select(static row => row[0].AsInteger()).ToArray();

    public static IReadOnlyList<string> QueryTexts(EmbeddedConnection connection, string sql)
        => Query(connection, sql).Select(static row => row[0].AsText()).ToArray();

    public static IReadOnlyList<double> QueryReals(EmbeddedConnection connection, string sql)
        => Query(connection, sql)
            .Select(static row => row[0].Kind == SqlValueKind.Integer ? row[0].AsInteger() : row[0].AsReal())
            .ToArray();

    public static EmbeddedSqlException ShouldThrow(EmbeddedConnection connection, string sql)
    {
        var act = () => Execute(connection, sql);
        return act.Should().Throw<EmbeddedSqlException>().Which;
    }

    public static void SeedCorpus(EmbeddedConnection connection)
    {
        Execute(connection, CreateDocuments);
        Execute(connection, CreateFtsIndex);
        Execute(
            connection,
            """
            INSERT INTO docs(id, title, body) VALUES
              (1, 'The quick brown fox', 'A quick brown fox jumps over the lazy dog'),
              (2, 'Lazy afternoon',      'The dog sleeps all afternoon, lazy and warm'),
              (3, 'Foxes and hounds',    'Foxes outwit hounds; the quick fox wins again'),
              (4, 'Gardening notes',     'Tomatoes, beans and a slow snail');
            """);
    }

    public static string CreateDatabasePath(string suite)
    {
        var directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, suite);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():N}.db");
    }

    public static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }
}

/// <summary>
/// A minimal in-memory <see cref="IManagedIndexSource"/> for unit-level method tests: a fixed row
/// set with a revision counter and no mutation journal, so every refresh takes the full-rebuild
/// path the cost model prices.
/// </summary>
internal sealed class ArrayManagedIndexSource : IManagedIndexSource
{
    private readonly List<long> _rowIds = [];
    private readonly List<SqlValue[]> _rows = [];
    private long _revision;

    public ArrayManagedIndexSource(params (long RowId, SqlValue[] Values)[] rows)
    {
        foreach (var (rowId, values) in rows)
        {
            _rowIds.Add(rowId);
            _rows.Add(values);
        }
    }

    public static ArrayManagedIndexSource FromText(params (long RowId, string Text)[] rows)
        => new(rows.Select(static row => (row.RowId, new[] { SqlValue.Text(row.Text) })).ToArray());

    public int RowCount => _rows.Count;

    public long Revision => _revision;

    /// <summary>Number of times a method asked for a rebuild baseline reset.</summary>
    public int RebuildNotifications { get; private set; }

    public ManagedIndexSourceDelta? TryGetDelta(long sinceRevision) => null;

    public void NotifyRebuilt(long revision) => RebuildNotifications++;

    public long GetRowId(int position) => _rowIds[position];

    public SqlValue[] GetRow(int position) => _rows[position];

    public bool TryGetPosition(long rowId, out int position)
    {
        position = _rowIds.IndexOf(rowId);
        return position >= 0;
    }

    /// <summary>Replaces or appends one row and bumps the revision, as the engine's row store does.</summary>
    public void Upsert(long rowId, params SqlValue[] values)
    {
        var position = _rowIds.IndexOf(rowId);
        if (position >= 0)
        {
            _rows[position] = values;
        }
        else
        {
            _rowIds.Add(rowId);
            _rows.Add(values);
        }

        _revision++;
    }

    public void Remove(long rowId)
    {
        var position = _rowIds.IndexOf(rowId);
        if (position < 0)
            return;

        _rowIds.RemoveAt(position);
        _rows.RemoveAt(position);
        _revision++;
    }
}
