using System.Data.Common;
using System.Globalization;
using AwesomeAssertions;
using AhtolaSqliteConnection = Ahtola.Data.Sqlite.SqliteConnection;
using MicrosoftSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace Ahtola.Tests;

/// <summary>
/// Differential checks for the access paths that serve common .NET query shapes without a full
/// scan: rowid and secondary-index equality, IN lists, rowid ranges, ORDER BY satisfied by an
/// index or by rowid order with LIMIT/OFFSET, covering COUNT and existence probes, small-left
/// join probes, DML candidate pruning, and RETURNING with and without subqueries. Every query
/// runs against the same SQLite-written file through Microsoft.Data.Sqlite and through Ahtola's
/// facade, and must return identical typed rows (in order whenever the SQL orders them).
/// </summary>
[NonParallelizable]
public sealed class HotspotAccessPathParityTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "ahtola-hotspot-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
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

    [TestCase("SELECT id FROM items WHERE id IN (@a, @b, @c) ORDER BY id;", 3L, 5L, 7L)]
    [TestCase("SELECT id FROM items WHERE id IN (@a, @b, @c) ORDER BY id;", 2.0, "4", 9.5)]
    [TestCase("SELECT id FROM items WHERE id IN (@a, @b, @c) ORDER BY id;", null, -2L, 1000000L)]
    [TestCase("SELECT id FROM items WHERE category IN (@a, @b, @c) ORDER BY id;", 1L, "2", 3.0)]
    [TestCase("SELECT id FROM items WHERE name IN (@a, @b, @c) ORDER BY id;", "ALPHA", "beta", "Gamma ")]
    [TestCase("SELECT id FROM items WHERE code IN (@a, @b, @c) AND category = 1 ORDER BY id;", "c3", "c4", "c11")]
    [TestCase("SELECT id FROM items WHERE id = @a;", 2.0, null, null)]
    [TestCase("SELECT id FROM items WHERE id = @a;", "6", null, null)]
    [TestCase("SELECT id, name FROM items WHERE category = @a ORDER BY id;", "1", null, null)]
    public void InListAndEqualityProbesMatchSqlite(string sql, object? a, object? b, object? c)
        => AssertSameResults(sql, ("@a", a), ("@b", b), ("@c", c));

    [TestCase("SELECT id FROM items WHERE id <= 5 ORDER BY id;")]
    [TestCase("SELECT id FROM items WHERE id < 2.5 ORDER BY id;")]
    [TestCase("SELECT id FROM items WHERE id > -3 AND id < 4 ORDER BY id;")]
    [TestCase("SELECT id FROM items WHERE id BETWEEN -2 AND 4 ORDER BY id;")]
    [TestCase("SELECT id FROM items WHERE 5 >= id AND category = 1 ORDER BY id;")]
    [TestCase("SELECT id FROM items WHERE id >= '3' ORDER BY id;")]
    [TestCase("SELECT id FROM items WHERE id > 3 AND id > 10 AND id <= 1000000 ORDER BY id;")]
    [TestCase("SELECT id FROM items WHERE id < -100;")]
    public void RowidRangesMatchSqlite(string sql)
        => AssertSameResults(sql);

    [TestCase("SELECT id, tag FROM items ORDER BY tag DESC LIMIT 5;")]
    [TestCase("SELECT id, tag FROM items ORDER BY tag LIMIT 5;")]
    [TestCase("SELECT id, category FROM items WHERE category > 0 ORDER BY category LIMIT 3 OFFSET 2;")]
    [TestCase("SELECT id, category FROM items WHERE category < 3 ORDER BY category DESC LIMIT 4;")]
    [TestCase("SELECT id, name FROM items ORDER BY name LIMIT 4;")]
    [TestCase("SELECT id, name FROM items ORDER BY name COLLATE BINARY LIMIT 4;")]
    [TestCase("SELECT id, name FROM items ORDER BY id LIMIT 3 OFFSET 2;")]
    [TestCase("SELECT id FROM items ORDER BY rowid LIMIT 2;")]
    [TestCase("SELECT id FROM items ORDER BY id DESC LIMIT 2;")]
    [TestCase("SELECT id FROM items ORDER BY id LIMIT 0;")]
    [TestCase("SELECT id FROM items ORDER BY id LIMIT -1 OFFSET 12;")]
    public void OrderedLimitsMatchSqlite(string sql)
        => AssertSameResults(sql);

    [TestCase("SELECT id, category FROM items WHERE category > 0 AND category <= 2 ORDER BY category LIMIT 6;")]
    [TestCase("SELECT id, category FROM items WHERE category > 0 AND category <= 2 ORDER BY category, id;")]
    [TestCase("SELECT id, name FROM items WHERE name >= 'b' ORDER BY name LIMIT 3;")]
    [TestCase("SELECT id, name FROM items WHERE name < 'GAMMA' ORDER BY name DESC LIMIT 3;")]
    [TestCase("SELECT id, price FROM items WHERE price < 5 ORDER BY price DESC LIMIT 2;")]
    [TestCase("SELECT id, price FROM items WHERE price >= 2.5 AND price < 9 ORDER BY price;")]
    [TestCase("SELECT id, category FROM items WHERE category BETWEEN '1' AND 2 ORDER BY category LIMIT 4;")]
    [TestCase("SELECT id, category FROM items WHERE category > '1' ORDER BY category LIMIT 4;")]
    [TestCase("SELECT id, tag FROM items WHERE tag < 'c' ORDER BY tag DESC LIMIT 3;")]
    [TestCase("SELECT id FROM items WHERE category > NULL ORDER BY category LIMIT 3;")]
    [TestCase("SELECT id FROM items WHERE 2 >= category ORDER BY category LIMIT 5;")]
    [TestCase("SELECT COUNT(*) FROM items WHERE category >= 1 AND category < 3;")]
    public void IndexRangesMatchSqlite(string sql)
    {
        // Twice on one connection: the second run is served from the warm shared index order.
        AssertSameScript(sql, sql);
    }

    [TestCase("SELECT id, price FROM items ORDER BY id;")]
    [TestCase("SELECT id, typeof(price), price FROM items ORDER BY id;")]
    [TestCase("SELECT price FROM items WHERE price = 2 ORDER BY price;")]
    [TestCase("SELECT COUNT(*), MIN(price), MAX(price) FROM items WHERE price >= 3;")]
    [TestCase("SELECT price FROM items WHERE price BETWEEN 4 AND 9 ORDER BY price DESC;")]
    [TestCase("SELECT code, rate, typeof(rate), weight, typeof(weight) FROM rates ORDER BY code;")]
    [TestCase("SELECT rate FROM rates WHERE code = 'a';")]
    [TestCase("SELECT COUNT(*) FROM items WHERE category = 1;")]
    [TestCase("SELECT COUNT(*) FROM items WHERE category = '1';")]
    [TestCase("SELECT COUNT(*) FROM items WHERE category = NULL;")]
    [TestCase("SELECT 1 FROM items WHERE category = 2 LIMIT 1;")]
    [TestCase("SELECT COUNT(*), 7 FROM items WHERE category = 2 AND category IN (1, 2);")]
    [TestCase("SELECT COUNT(*) FROM items WHERE category = 1 AND id > 2;")]
    public void CoveringProbesMatchSqlite(string sql)
        => AssertSameResults(sql);

    [TestCase("SELECT o.id, i.name FROM orders AS o JOIN items AS i ON i.id = o.item_id WHERE o.item_id = 3 ORDER BY o.id;")]
    [TestCase("SELECT o.id, i.name FROM orders AS o LEFT JOIN items AS i ON i.id = o.item_id WHERE o.qty = 9 ORDER BY o.id;")]
    [TestCase("SELECT o.id, i.name FROM orders AS o JOIN items AS i ON i.id = o.item_id AND i.category = 1 WHERE o.qty < 3 ORDER BY o.id;")]
    [TestCase("SELECT i.category, COUNT(*), SUM(o.qty) FROM orders AS o JOIN items AS i ON i.id = o.item_id WHERE i.category < 3 GROUP BY i.category ORDER BY i.category;")]
    [TestCase("SELECT o.rowid, i.rowid, o.id FROM orders AS o JOIN items AS i ON i.id = o.item_id ORDER BY o.id;")]
    [TestCase("SELECT a.rowid, b.rowid, i.rowid, i._rowid_ FROM orders AS a JOIN orders AS b ON b.item_id = a.item_id AND b.id <> a.id JOIN items AS i ON i.id = b.item_id ORDER BY a.id, b.id;")]
    [TestCase("SELECT o.id, i.rowid FROM orders AS o LEFT JOIN items AS i ON i.id = o.item_id ORDER BY o.id;")]
    public void JoinProbesMatchSqlite(string sql)
        => AssertSameResults(sql);

    [TestCase("UPDATE items SET price = price + 1 WHERE id = 2.0;")]
    [TestCase("UPDATE items SET price = price WHERE id = 4;")]
    [TestCase("DELETE FROM orders WHERE id = '3';")]
    [TestCase("DELETE FROM orders WHERE item_id = 5 AND qty > 1;")]
    [TestCase("DELETE FROM orders WHERE id = 1 RETURNING id, qty;")]
    [TestCase("DELETE FROM orders WHERE id = 2 RETURNING id, (SELECT COUNT(*) FROM orders);")]
    [TestCase("INSERT INTO orders(item_id, qty) VALUES (1, 5) RETURNING id, qty * 2;")]
    [TestCase("INSERT INTO orders(item_id, qty) VALUES (1, 5), (2, 6) RETURNING id, (SELECT MAX(id) FROM orders);")]
    [TestCase("UPDATE orders SET qty = qty + 1 WHERE id = 4 RETURNING id, qty, (SELECT SUM(qty) FROM orders);")]
    [TestCase("UPDATE items SET price = 1 WHERE id = 2.5;")]
    [TestCase("UPDATE items SET price = 1 WHERE id = '3';")]
    [TestCase("UPDATE items SET price = 1 WHERE id = 8;")]
    [TestCase("UPDATE items SET price = 1 WHERE id = 1000000.0;")]
    [TestCase("UPDATE items SET price = 1 WHERE id = -2 AND category = 2;")]
    [TestCase("DELETE FROM items WHERE id = 9007199254740993;")]
    public void DmlMatchesSqlite(string sql)
    {
        AssertSameResults(sql);
        AssertSameResults("SELECT id, item_id, qty FROM orders ORDER BY id;", reuseFixture: true);
        AssertSameResults("SELECT id, price FROM items ORDER BY id;", reuseFixture: true);
    }

    [Test]
    public void RepeatedStatementsSeeEachOthersWrites()
    {
        // Shared per-table caches (rowid order, equality buckets, sorted index order) must follow
        // every write, including a delete of the maximum rowid followed by an insert.
        AssertSameScript(
            "SELECT id FROM items WHERE category = 1 ORDER BY id;",
            "INSERT INTO items(id, code, name, category, tag) VALUES (50, 'c50', 'zeta', 1, 'z');",
            "SELECT id FROM items WHERE category = 1 ORDER BY id;",
            "SELECT id, tag FROM items ORDER BY tag DESC LIMIT 3;",
            "UPDATE items SET category = 2, tag = 'zz' WHERE id = 50;",
            "SELECT id FROM items WHERE category = 1 ORDER BY id;",
            "SELECT id, tag FROM items ORDER BY tag DESC LIMIT 3;",
            "SELECT id FROM items WHERE id IN (49, 50, 51);",
            "DELETE FROM items WHERE id = 50;",
            "INSERT INTO items(code, name, category) VALUES ('c51', 'eta', 1) RETURNING id;",
            "SELECT id FROM items WHERE id > 7 ORDER BY id;",
            "SELECT COUNT(*) FROM items WHERE category = 1;",
            "BEGIN; INSERT INTO orders(item_id, qty) VALUES (2, 4) RETURNING id; DELETE FROM orders WHERE item_id = 2 AND qty = 4; COMMIT;",
            "SELECT o.id, i.name FROM orders AS o JOIN items AS i ON i.id = o.item_id WHERE o.item_id = 2 ORDER BY o.id;");
    }

    [Test]
    public void RepeatedIndexSeeksMatchSqliteAcrossTheWarmOrderSwitch()
    {
        // Enough repeated equality seeks on an unchanged table switch the index to an in-memory
        // order; a write restarts the count. Results must not change at either switch.
        var statements = new List<string>();
        for (var round = 0; round < 40; round++)
        {
            statements.Add($"SELECT id FROM items WHERE category = {round % 4} ORDER BY id;");
            statements.Add($"SELECT id, name FROM items WHERE name = '{(round % 2 == 0 ? "alpha" : "Gamma ")}' ORDER BY id;");
            statements.Add($"SELECT id FROM items WHERE tag = '{(char)('a' + (round % 6))}';");
            if (round == 36)
                statements.Add("UPDATE items SET category = 3, name = 'ALPHA', tag = 'a' WHERE id = 9;");
            if (round == 38)
                statements.Add("INSERT INTO items(id, code, name, category, tag) VALUES (12, 'c12', 'alpha', 2, 'b');");
        }

        AssertSameScript([.. statements]);
    }

    [Test]
    public void ChunkSpanningUpdatesAndAppendsPersistLikeSqlite()
    {
        // Commits diff the committed and new row versions chunk by chunk (1024 rows); changes in
        // several chunks, at chunk edges, and appended in the same transaction must all persist.
        string[] statements =
        [
            "CREATE TABLE wide(id INTEGER PRIMARY KEY, v INTEGER, note TEXT);",
            "CREATE INDEX ix_wide_v ON wide(v);",
            "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 3000) INSERT INTO wide SELECT x, x % 97, 'n' || x FROM n;",
            "UPDATE wide SET v = -1 WHERE id = 1024;",
            "UPDATE wide SET v = -2 WHERE id = 1025;",
            "BEGIN; UPDATE wide SET note = 'first' WHERE id = 1; UPDATE wide SET v = -3 WHERE id = 2999; INSERT INTO wide VALUES (3001, -4, 'appended'); COMMIT;",
            "INSERT INTO wide(v, note) VALUES (-5, 'tail');",
            "UPDATE wide SET v = v WHERE id = 1500;",
            "BEGIN; WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 200) INSERT INTO wide SELECT 3100 + x * 2, -x, 'batch' FROM n; WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 200) INSERT INTO wide SELECT 3600 + x, x, 'append' FROM n; COMMIT;",
            "DELETE FROM wide WHERE id > 3100 AND id % 4 = 0;",
            "BEGIN; WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 100) INSERT INTO wide SELECT 3100 + x * 4, x, 'refill' FROM n; COMMIT;",
            "SELECT id, v, note FROM wide WHERE v < 0 ORDER BY id;",
            "SELECT COUNT(*), SUM(v), MAX(id) FROM wide;",
        ];
        var (sqlitePath, ahtolaPath) = CreateFixtures();
        using (var sqlite = new MicrosoftSqliteConnection($"Data Source={sqlitePath};Pooling=False"))
        using (var ahtola = new AhtolaSqliteConnection($"Data Source={ahtolaPath}"))
        {
            sqlite.Open();
            ahtola.Open();
            foreach (var sql in statements)
                Read(ahtola, sql, []).Should().Equal(Read(sqlite, sql, []), sql);
        }

        AhtolaSqliteConnection.ClearAllPools();
        const string dump = "SELECT id, v, note FROM wide ORDER BY id;";
        var expected = Run(new MicrosoftSqliteConnection($"Data Source={sqlitePath};Pooling=False"), dump, []);
        Run(new MicrosoftSqliteConnection($"Data Source={ahtolaPath};Pooling=False"), dump, []).Should().Equal(expected);
        Run(new MicrosoftSqliteConnection($"Data Source={ahtolaPath};Pooling=False"), "PRAGMA integrity_check;", [])
            .Should().Equal("T:ok");
    }

    [Test]
    public void IntegralRealsWrittenByAhtolaReadBackAsRealInBothEngines()
    {
        // SQLite stores an integral REAL in integer form and reads it back as REAL; both engines
        // must agree on what Ahtola wrote, and SQLite must accept the file, index included.
        var (_, ahtolaPath) = CreateFixtures();
        using (var ahtola = new AhtolaSqliteConnection($"Data Source={ahtolaPath}"))
        {
            ahtola.Open();
            Read(ahtola, "UPDATE items SET price = 5 WHERE id = 1;", []);
            Read(ahtola, "UPDATE items SET price = 8.0, name = name || '!' WHERE id = 2;", []);
            Read(ahtola, "INSERT INTO items(id, code, price) VALUES (12, 'c12', -3.0), (13, 'c13', 0.0), (14, 'c14', 1e300);", []);
            Read(ahtola, "INSERT INTO rates VALUES ('d', 7, 7.5);", []);
        }

        AhtolaSqliteConnection.ClearAllPools();
        const string query = "SELECT id, typeof(price), price FROM items ORDER BY id;";
        const string rates = "SELECT code, typeof(rate), rate, typeof(weight), weight FROM rates ORDER BY code;";
        const string indexed = "SELECT price FROM items INDEXED BY ix_items_price WHERE price >= 0 ORDER BY price;";
        var fromAhtola = Run(new AhtolaSqliteConnection($"Data Source={ahtolaPath}"), query, []);
        var ratesFromAhtola = Run(new AhtolaSqliteConnection($"Data Source={ahtolaPath}"), rates, []);
        var indexedFromAhtola = Run(new AhtolaSqliteConnection($"Data Source={ahtolaPath}"), indexed, []);
        AhtolaSqliteConnection.ClearAllPools();

        Run(new MicrosoftSqliteConnection($"Data Source={ahtolaPath};Pooling=False"), query, []).Should().Equal(fromAhtola);
        Run(new MicrosoftSqliteConnection($"Data Source={ahtolaPath};Pooling=False"), rates, []).Should().Equal(ratesFromAhtola);
        Run(new MicrosoftSqliteConnection($"Data Source={ahtolaPath};Pooling=False"), indexed, []).Should().Equal(indexedFromAhtola);
        Run(new MicrosoftSqliteConnection($"Data Source={ahtolaPath};Pooling=False"), "PRAGMA integrity_check;", [])
            .Should().Equal("T:ok");
        fromAhtola.Should().Contain(["I:1|T:real|R:5", "I:2|T:real|R:8", "I:12|T:real|R:-3", "I:13|T:real|R:0"]);
    }

    private void AssertSameResults(string sql, params (string Name, object? Value)[] parameters)
        => AssertSameResults(sql, reuseFixture: false, parameters);

    private (string SqlitePath, string AhtolaPath)? _fixture;

    private void AssertSameResults(string sql, bool reuseFixture, params (string Name, object? Value)[] parameters)
    {
        var (sqlitePath, ahtolaPath) = reuseFixture && _fixture is { } existing ? existing : CreateFixtures();
        _fixture = (sqlitePath, ahtolaPath);
        var expected = Run(new MicrosoftSqliteConnection($"Data Source={sqlitePath};Pooling=False"), sql, parameters);
        var actual = Run(new AhtolaSqliteConnection($"Data Source={ahtolaPath}"), sql, parameters);
        actual.Should().Equal(expected, sql);
    }

    private void AssertSameScript(params string[] statements)
    {
        var (sqlitePath, ahtolaPath) = CreateFixtures();
        using var sqlite = new MicrosoftSqliteConnection($"Data Source={sqlitePath};Pooling=False");
        using var ahtola = new AhtolaSqliteConnection($"Data Source={ahtolaPath}");
        sqlite.Open();
        ahtola.Open();
        foreach (var sql in statements)
        {
            var expected = Read(sqlite, sql, []);
            var actual = Read(ahtola, sql, []);
            actual.Should().Equal(expected, sql);
        }
    }

    private (string SqlitePath, string AhtolaPath) CreateFixtures()
    {
        var name = Guid.NewGuid().ToString("N");
        var sqlitePath = Path.Combine(_root, name + "-sqlite.db");
        var ahtolaPath = Path.Combine(_root, name + "-ahtola.db");
        using (var connection = new MicrosoftSqliteConnection($"Data Source={sqlitePath};Pooling=False"))
        {
            connection.Open();
            Execute(connection, "PRAGMA journal_mode=WAL;");
            Execute(
                connection,
                """
                -- Integral prices exercise SQLite's on-disk form of a REAL: stored as an
                -- integer, read back as REAL.
                CREATE TABLE items(
                    id INTEGER PRIMARY KEY,
                    code TEXT UNIQUE,
                    name TEXT COLLATE NOCASE,
                    category INTEGER,
                    price REAL,
                    tag TEXT);
                CREATE INDEX ix_items_category ON items(category);
                CREATE INDEX ix_items_name ON items(name);
                CREATE INDEX ix_items_tag ON items(tag DESC);
                CREATE INDEX ix_items_price ON items(price);
                CREATE TABLE rates(code TEXT PRIMARY KEY, rate REAL, weight FLOAT) WITHOUT ROWID;
                INSERT INTO rates VALUES ('a', 1.0, 2), ('b', 2.5, 3.0), ('c', -4.0, NULL);
                CREATE TABLE orders(id INTEGER PRIMARY KEY, item_id INTEGER, qty INTEGER);
                CREATE INDEX ix_orders_item ON orders(item_id);
                INSERT INTO items VALUES (-2, 'cm2', 'Omega', 2, 1.5, NULL);
                INSERT INTO items VALUES (1, 'c1', 'alpha', 1, 2.0, 'b');
                INSERT INTO items VALUES (2, 'c2', 'Beta', 1, 2.5, 'a');
                INSERT INTO items VALUES (3, 'c3', 'gamma', 2, 3.0, NULL);
                INSERT INTO items VALUES (4, 'c4', 'ALPHA', 1, 4.0, 'c');
                INSERT INTO items VALUES (5, 'c5', 'delta', '7', 5.5, 'b');
                INSERT INTO items VALUES (6, 'c6', 'epsilon', NULL, 6.0, 'd');
                INSERT INTO items VALUES (7, 'c7', 'Gamma ', 3, 7.0, 'a');
                INSERT INTO items VALUES (9, 'c9', 'beta', 2, 9.0, 'e');
                INSERT INTO items VALUES (11, 'c11', 'theta', 1, 11.0, 'c');
                INSERT INTO items VALUES (1000000, 'cbig', 'iota', 0, 0.5, 'f');
                INSERT INTO orders VALUES (1, 3, 1);
                INSERT INTO orders VALUES (2, 3, 2);
                INSERT INTO orders VALUES (3, 5, 9);
                INSERT INTO orders VALUES (4, 42, 9);
                INSERT INTO orders VALUES (5, 1, 2);
                INSERT INTO orders VALUES (6, 5, 3);
                INSERT INTO orders VALUES (7, 2, 1);
                """);
        }

        File.Copy(sqlitePath, ahtolaPath);
        return (sqlitePath, ahtolaPath);
    }

    private static List<string> Run(DbConnection connection, string sql, (string Name, object? Value)[] parameters)
    {
        using (connection)
        {
            connection.Open();
            return Read(connection, sql, parameters);
        }
    }

    private static List<string> Read(DbConnection connection, string sql, (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            if (!sql.Contains(name, StringComparison.Ordinal))
                continue;
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        var rows = new List<string>();
        using var reader = command.ExecuteReader();
        do
        {
            while (reader.Read())
            {
                var cells = new string[reader.FieldCount];
                for (var ordinal = 0; ordinal < cells.Length; ordinal++)
                {
                    cells[ordinal] = reader.IsDBNull(ordinal)
                        ? "NULL"
                        : reader.GetValue(ordinal) switch
                        {
                            long integer => "I:" + integer.ToString(CultureInfo.InvariantCulture),
                            double real => "R:" + real.ToString("R", CultureInfo.InvariantCulture),
                            string text => "T:" + text,
                            byte[] blob => "B:" + Convert.ToHexString(blob),
                            var other => "?:" + other,
                        };
                }

                rows.Add(string.Join('|', cells));
            }
        }
        while (reader.NextResult());

        return rows;
    }

    private static void Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
