using System.Data;
using System.Data.Common;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using AhtolaSqliteConnection = Ahtola.Data.Sqlite.SqliteConnection;
using MicrosoftSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace Benchmarks;

/// <summary>
/// The access patterns that dominate typical .NET applications over SQLite, measured
/// through the <c>Microsoft.Data.Sqlite</c>-compatible facade a migrating consumer uses
/// (<see cref="AhtolaSqliteConnection"/>) against <c>Microsoft.Data.Sqlite</c> itself.
/// </summary>
/// <remarks>
/// Every case runs against a file-backed WAL database with <c>synchronous=NORMAL</c>, the
/// configuration most .NET applications ship. The scenarios mirror what ADO.NET code,
/// Dapper and EF Core issue in practice: pooled connection-per-operation, a fresh
/// parameterized command per query, reused commands, typed and <c>GetValue</c>-based
/// materialization, unique-key lookups, paging, <c>IN</c> lists, aggregates over joins,
/// autocommit and batched writes, UPSERT and <c>RETURNING</c>, multi-statement batches and
/// blobs. Fixtures are seeded identically for both providers and validated before timing.
/// </remarks>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class DotNetHotspotBenchmarks
{
    public enum Provider
    {
        Sqlite,
        Ahtola,
    }

    private const int ItemCount = 10_000;
    private const int OrderCount = 20_000;
    private const int CategoryCount = 100;
    private const int BatchRows = 1_000;
    private const int ReadAllRows = 1_000;
    private const int InListSize = 50;
    private const int BlobBytes = 64 * 1024;

    private string _root = null!;
    private string _path = null!;
    private string _connectionString = null!;
    private DbConnection _connection = null!;
    private DbCommand _reusedLookup = null!;
    private DbParameter _reusedLookupId = null!;
    private Guid[] _guids = null!;
    private byte[] _blob = null!;
    private int _cursor;

    [Params(Provider.Sqlite, Provider.Ahtola)]
    public Provider Engine { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _root = Path.Combine(Path.GetTempPath(), "ahtola-hotspot-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "app.db");
        _connectionString = $"Data Source={_path}";
        _guids = new Guid[ItemCount];
        var random = new Random(1234);
        Span<byte> bytes = stackalloc byte[16];
        for (var index = 0; index < _guids.Length; index++)
        {
            random.NextBytes(bytes);
            _guids[index] = new Guid(bytes);
        }

        _blob = new byte[BlobBytes];
        random.NextBytes(_blob);

        // Both engines open the same SQLite-written file, so the fixture is byte-identical and
        // its construction cost stays out of setup (bulk loading is measured by its own case).
        using (var seed = new MicrosoftSqliteConnection($"Data Source={_path};Pooling=False"))
        {
            seed.Open();
            Seed(seed);
        }

        _connection = CreateConnection();
        _connection.Open();
        Execute(_connection, "PRAGMA synchronous=NORMAL;");
        Validate(_connection);

        _reusedLookup = _connection.CreateCommand();
        _reusedLookup.CommandText = "SELECT id, guid, name, category, price, created FROM items WHERE id = @id;";
        _reusedLookupId = AddParameter(_reusedLookup, "@id", 1L);
        _reusedLookup.Prepare();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _reusedLookup.Dispose();
        _connection.Dispose();
        AhtolaSqliteConnection.ClearAllPools();
        MicrosoftSqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [IterationSetup(Target = nameof(InsertBatchInTransaction))]
    public void ResetLog() => Execute(_connection, "DELETE FROM log;");

    // ---- Connection lifecycle ------------------------------------------------------------

    [BenchmarkCategory("Connection")]
    [Benchmark(Description = "Pooled open + close")]
    public int PooledOpenClose()
    {
        using var connection = CreateConnection();
        connection.Open();
        return connection.State == ConnectionState.Open ? 1 : 0;
    }

    [BenchmarkCategory("Connection")]
    [Benchmark(Description = "Pooled open + PK lookup + close")]
    public long PooledOpenLookupClose()
    {
        using var connection = CreateConnection();
        connection.Open();
        return LookupById(connection, NextId());
    }

    // ---- Point reads ---------------------------------------------------------------------

    [BenchmarkCategory("PointRead")]
    [Benchmark(Description = "PK lookup, new command per call")]
    public long LookupByIdNewCommand() => LookupById(_connection, NextId());

    [BenchmarkCategory("PointRead")]
    [Benchmark(Description = "PK lookup, reused prepared command")]
    public long LookupByIdReusedCommand()
    {
        _reusedLookupId.Value = (long)NextId();
        using var reader = _reusedLookup.ExecuteReader();
        return reader.Read() ? reader.GetInt64(0) + reader.GetString(2).Length : 0;
    }

    [BenchmarkCategory("PointRead")]
    [Benchmark(Description = "PK lookup, Dapper-style GetValue mapping")]
    public long LookupByIdDapperStyle()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, guid, name, category, price, created, notes FROM items WHERE id = @id;";
        AddParameter(command, "@id", (long)NextId());
        using var reader = command.ExecuteReader();
        long checksum = 0;
        while (reader.Read())
        {
            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                checksum += reader.IsDBNull(ordinal) ? 0 : reader.GetValue(ordinal).GetHashCode() & 0xFF;
        }

        return checksum;
    }

    [BenchmarkCategory("PointRead")]
    [Benchmark(Description = "Unique TEXT (GUID) key lookup")]
    public long LookupByGuid()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, name, price FROM items WHERE guid = @guid;";
        AddParameter(command, "@guid", _guids[NextId() - 1].ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? reader.GetInt64(0) : 0;
    }

    [BenchmarkCategory("PointRead")]
    [Benchmark(Description = "ExecuteScalar COUNT(*) on index")]
    public long ScalarCountByCategory()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM items WHERE category = @category;";
        AddParameter(command, "@category", (long)(NextId() % CategoryCount));
        return Convert.ToInt64(command.ExecuteScalar());
    }

    [BenchmarkCategory("PointRead")]
    [Benchmark(Description = "EF-style schema probe on sqlite_master")]
    public long SchemaProbe()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"name\" = @name AND \"type\" = 'table' AND \"rootpage\" IS NOT NULL;";
        AddParameter(command, "@name", "items");
        return Convert.ToInt64(command.ExecuteScalar());
    }

    // ---- Multi-row reads -----------------------------------------------------------------

    [BenchmarkCategory("Materialize")]
    [Benchmark(Description = "1,000 rows, typed getters")]
    public long ReadRowsTyped()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, guid, name, category, price, created, notes FROM items WHERE id <= @limit;";
        AddParameter(command, "@limit", (long)ReadAllRows);
        using var reader = command.ExecuteReader();
        long checksum = 0;
        while (reader.Read())
        {
            checksum += reader.GetInt64(0)
                        + reader.GetString(1).Length
                        + reader.GetString(2).Length
                        + reader.GetInt32(3)
                        + (long)reader.GetDouble(4)
                        + reader.GetString(5).Length
                        + (reader.IsDBNull(6) ? 0 : reader.GetString(6).Length);
        }

        return checksum;
    }

    [BenchmarkCategory("Materialize")]
    [Benchmark(Description = "1,000 rows, Dapper-style GetValue mapping")]
    public long ReadRowsDapperStyle()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, guid, name, category, price, created, notes FROM items WHERE id <= @limit;";
        AddParameter(command, "@limit", (long)ReadAllRows);
        using var reader = command.ExecuteReader();
        var fieldCount = reader.FieldCount;
        long checksum = 0;
        for (var ordinal = 0; ordinal < fieldCount; ordinal++)
            checksum += reader.GetName(ordinal).Length + reader.GetFieldType(ordinal).Name.Length;
        var values = new object[fieldCount];
        while (reader.Read())
        {
            for (var ordinal = 0; ordinal < fieldCount; ordinal++)
                values[ordinal] = reader.IsDBNull(ordinal) ? DBNull.Value : reader.GetValue(ordinal);
            checksum += (long)values[0] + ((string)values[2]).Length;
        }

        return checksum;
    }

    [BenchmarkCategory("Materialize")]
    [Benchmark(Description = "1,000 rows, GetFieldValue<T> by GetOrdinal")]
    public long ReadRowsGetFieldValue()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, guid, name, category, price, created FROM items WHERE id <= @limit;";
        AddParameter(command, "@limit", (long)ReadAllRows);
        using var reader = command.ExecuteReader();
        var id = reader.GetOrdinal("id");
        var guid = reader.GetOrdinal("guid");
        var price = reader.GetOrdinal("price");
        var created = reader.GetOrdinal("created");
        long checksum = 0;
        while (reader.Read())
        {
            checksum += reader.GetFieldValue<long>(id)
                        + reader.GetFieldValue<Guid>(guid).GetHashCode()
                        + (long)reader.GetFieldValue<decimal>(price)
                        + reader.GetFieldValue<DateTime>(created).Day;
        }

        return checksum;
    }

    [BenchmarkCategory("Query")]
    [Benchmark(Description = "Indexed equality, ~100 rows")]
    public long IndexedEquality()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, name, price FROM items WHERE category = @category;";
        AddParameter(command, "@category", (long)(NextId() % CategoryCount));
        return Drain(command);
    }

    [BenchmarkCategory("Query")]
    [Benchmark(Description = "Paging ORDER BY id LIMIT 50 OFFSET n")]
    public long PagedOrderByLimitOffset()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, name, price FROM items ORDER BY id LIMIT @take OFFSET @skip;";
        AddParameter(command, "@take", 50L);
        AddParameter(command, "@skip", (long)(NextId() % 100 * 50));
        return Drain(command);
    }

    [BenchmarkCategory("Query")]
    [Benchmark(Description = "Keyset ORDER BY indexed col DESC LIMIT 20")]
    public long KeysetOrderByIndexDesc()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, name, created FROM items WHERE created < @before ORDER BY created DESC LIMIT 20;";
        AddParameter(command, "@before", CreatedAt(NextId()));
        return Drain(command);
    }

    [BenchmarkCategory("Query")]
    [Benchmark(Description = "WHERE id IN (50 parameters)")]
    public long InListParameters()
    {
        using var command = _connection.CreateCommand();
        var sql = new StringBuilder("SELECT id, name FROM items WHERE id IN (");
        var start = NextId() % (ItemCount - InListSize * 3);
        for (var index = 0; index < InListSize; index++)
        {
            if (index > 0)
                sql.Append(", ");
            sql.Append("@p").Append(index);
            AddParameter(command, "@p" + index, (long)(start + index * 3 + 1));
        }

        sql.Append(");");
        command.CommandText = sql.ToString();
        return Drain(command);
    }

    [BenchmarkCategory("Query")]
    [Benchmark(Description = "LIKE prefix search LIMIT 20")]
    public long LikePrefix()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, name FROM items WHERE name LIKE @pattern LIMIT 20;";
        AddParameter(command, "@pattern", "Item 9" + (NextId() % 10) + "%");
        return Drain(command);
    }

    [BenchmarkCategory("Query")]
    [Benchmark(Description = "JOIN + GROUP BY aggregate")]
    public long JoinGroupBy()
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT i.category, COUNT(*), SUM(o.quantity)
            FROM orders AS o JOIN items AS i ON i.id = o.item_id
            WHERE i.category < @maxCategory
            GROUP BY i.category
            ORDER BY i.category;
            """;
        AddParameter(command, "@maxCategory", 5L);
        return Drain(command);
    }

    [BenchmarkCategory("Query")]
    [Benchmark(Description = "Correlated lookup: orders of one item")]
    public long ForeignKeyLookup()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT o.id, o.quantity, i.name FROM orders AS o JOIN items AS i ON i.id = o.item_id WHERE o.item_id = @itemId;";
        AddParameter(command, "@itemId", (long)NextId());
        return Drain(command);
    }

    [BenchmarkCategory("Query")]
    [Benchmark(Description = "Two result sets via NextResult")]
    public long MultipleResultSets()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM items WHERE category = @category; SELECT id, name FROM items WHERE category = @category ORDER BY id LIMIT 10;";
        AddParameter(command, "@category", (long)(NextId() % CategoryCount));
        using var reader = command.ExecuteReader();
        long checksum = 0;
        do
        {
            while (reader.Read())
                checksum += reader.GetInt64(0);
        }
        while (reader.NextResult());

        return checksum;
    }

    // ---- Writes --------------------------------------------------------------------------

    [BenchmarkCategory("Write")]
    [Benchmark(Description = "Autocommit single-row INSERT")]
    public int InsertAutocommit()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO log(at, level, message) VALUES (@at, @level, @message);";
        AddParameter(command, "@at", "2026-01-01T00:00:00");
        AddParameter(command, "@level", 2L);
        AddParameter(command, "@message", "request completed");
        return command.ExecuteNonQuery();
    }

    [BenchmarkCategory("Write")]
    [Benchmark(Description = "1,000-row INSERT in transaction, reused command", OperationsPerInvoke = BatchRows)]
    public int InsertBatchInTransaction()
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO log(at, level, message) VALUES (@at, @level, @message);";
        var at = AddParameter(command, "@at", string.Empty);
        var level = AddParameter(command, "@level", 0L);
        var message = AddParameter(command, "@message", string.Empty);
        command.Prepare();
        var rows = 0;
        for (var row = 0; row < BatchRows; row++)
        {
            at.Value = CreatedAt(row);
            level.Value = (long)(row % 5);
            message.Value = "batch message " + row;
            rows += command.ExecuteNonQuery();
        }

        transaction.Commit();
        return rows;
    }

    [BenchmarkCategory("Write")]
    [Benchmark(Description = "Autocommit UPDATE by PK")]
    public int UpdateByPrimaryKey()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE items SET price = @price WHERE id = @id;";
        var id = NextId();
        AddParameter(command, "@price", id * 1.25);
        AddParameter(command, "@id", (long)id);
        return command.ExecuteNonQuery();
    }

    [BenchmarkCategory("Write")]
    [Benchmark(Description = "Autocommit UPSERT ON CONFLICT DO UPDATE")]
    public int Upsert()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO settings(key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        var id = NextId();
        AddParameter(command, "@key", "setting-" + (id % 500));
        AddParameter(command, "@value", "value-" + id);
        return command.ExecuteNonQuery();
    }

    [BenchmarkCategory("Write")]
    [Benchmark(Description = "EF-style unit of work: BEGIN, UPDATE, INSERT RETURNING, COMMIT")]
    public long UnitOfWorkReturning()
    {
        using var transaction = _connection.BeginTransaction();
        long id;
        using (var update = _connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE items SET notes = @notes WHERE id = @id; SELECT changes();";
            AddParameter(update, "@notes", "touched");
            AddParameter(update, "@id", (long)NextId());
            update.ExecuteScalar();
        }

        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO orders(item_id, quantity) VALUES (@itemId, @quantity) RETURNING id;";
            AddParameter(insert, "@itemId", (long)NextId());
            AddParameter(insert, "@quantity", 3L);
            id = Convert.ToInt64(insert.ExecuteScalar());
        }

        using (var delete = _connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM orders WHERE id = @id;";
            AddParameter(delete, "@id", id);
            delete.ExecuteNonQuery();
        }

        transaction.Commit();
        return id;
    }

    [BenchmarkCategory("Write")]
    [Benchmark(Description = "64 KiB BLOB write + read back")]
    public long BlobRoundTrip()
    {
        var key = "blob-" + (NextId() % 16);
        using (var write = _connection.CreateCommand())
        {
            write.CommandText = "INSERT OR REPLACE INTO blobs(key, data) VALUES (@key, @data);";
            AddParameter(write, "@key", key);
            AddParameter(write, "@data", _blob);
            write.ExecuteNonQuery();
        }

        using var read = _connection.CreateCommand();
        read.CommandText = "SELECT data FROM blobs WHERE key = @key;";
        AddParameter(read, "@key", key);
        var data = (byte[])read.ExecuteScalar()!;
        return data.Length;
    }

    // ---- Helpers -------------------------------------------------------------------------

    private DbConnection CreateConnection()
        => Engine switch
        {
            Provider.Sqlite => new MicrosoftSqliteConnection(_connectionString),
            Provider.Ahtola => new AhtolaSqliteConnection(_connectionString),
            _ => throw new InvalidOperationException(),
        };

    private int NextId()
    {
        _cursor = (_cursor * 7919 + 13) % ItemCount;
        return _cursor + 1;
    }

    private static string CreatedAt(int row)
        => new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(row).ToString("yyyy-MM-dd HH:mm:ss");

    private static long LookupById(DbConnection connection, int id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, guid, name, category, price, created FROM items WHERE id = @id;";
        AddParameter(command, "@id", (long)id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? reader.GetInt64(0) + reader.GetString(2).Length : 0;
    }

    private static long Drain(DbCommand command)
    {
        using var reader = command.ExecuteReader();
        long checksum = 0;
        while (reader.Read())
            checksum += reader.GetInt64(0) + 1;
        return checksum;
    }

    private void Seed(DbConnection connection)
    {
        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(
            connection,
            """
            CREATE TABLE items(
                id INTEGER PRIMARY KEY,
                guid TEXT NOT NULL UNIQUE,
                name TEXT NOT NULL,
                category INTEGER NOT NULL,
                price REAL NOT NULL,
                created TEXT NOT NULL,
                notes TEXT NULL);
            """);
        Execute(connection, "CREATE INDEX ix_items_category ON items(category);");
        Execute(connection, "CREATE INDEX ix_items_created ON items(created);");
        Execute(
            connection,
            "CREATE TABLE orders(id INTEGER PRIMARY KEY, item_id INTEGER NOT NULL REFERENCES items(id), quantity INTEGER NOT NULL);");
        Execute(connection, "CREATE INDEX ix_orders_item ON orders(item_id);");
        Execute(connection, "CREATE TABLE log(id INTEGER PRIMARY KEY, at TEXT NOT NULL, level INTEGER NOT NULL, message TEXT NOT NULL);");
        Execute(connection, "CREATE TABLE settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);");
        Execute(connection, "CREATE TABLE blobs(key TEXT PRIMARY KEY, data BLOB NOT NULL);");

        using var transaction = connection.BeginTransaction();
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO items(id, guid, name, category, price, created, notes) VALUES (@id, @guid, @name, @category, @price, @created, @notes);";
            var id = AddParameter(insert, "@id", 0L);
            var guid = AddParameter(insert, "@guid", string.Empty);
            var name = AddParameter(insert, "@name", string.Empty);
            var category = AddParameter(insert, "@category", 0L);
            var price = AddParameter(insert, "@price", 0.0);
            var created = AddParameter(insert, "@created", string.Empty);
            var notes = AddParameter(insert, "@notes", DBNull.Value);
            for (var row = 1; row <= ItemCount; row++)
            {
                id.Value = (long)row;
                guid.Value = _guids[row - 1].ToString();
                name.Value = "Item " + row;
                category.Value = (long)(row % CategoryCount);
                price.Value = row * 0.5;
                created.Value = CreatedAt(row);
                notes.Value = row % 3 == 0 ? DBNull.Value : "note " + row;
                insert.ExecuteNonQuery();
            }
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO orders(item_id, quantity) VALUES (@itemId, @quantity);";
            var itemId = AddParameter(insert, "@itemId", 0L);
            var quantity = AddParameter(insert, "@quantity", 0L);
            for (var row = 0; row < OrderCount; row++)
            {
                itemId.Value = (long)(row * 31 % ItemCount + 1);
                quantity.Value = (long)(row % 7 + 1);
                insert.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        Execute(connection, "ANALYZE;");
    }

    private static void Validate(DbConnection connection)
    {
        Require(connection, "SELECT COUNT(*) FROM items;", ItemCount);
        Require(connection, "SELECT COUNT(*) FROM orders;", OrderCount);
        long expectedQuantity = 0;
        for (var row = 0; row < OrderCount; row++)
        {
            if (row * 31 % ItemCount == 0)
                expectedQuantity += row % 7 + 1;
        }

        Require(connection, "SELECT SUM(quantity) FROM orders WHERE item_id = 1;", expectedQuantity);
        Require(connection, "SELECT COUNT(*) FROM items WHERE category = 7;", ItemCount / CategoryCount);
    }

    private static void Require(DbConnection connection, string sql, long expected)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var actual = Convert.ToInt64(command.ExecuteScalar());
        if (actual != expected)
            throw new InvalidOperationException($"Fixture validation failed for '{sql}': expected {expected}, got {actual}.");
    }

    private static DbParameter AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
        return parameter;
    }

    private static void Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
