using System.Buffers.Binary;
using Ahtola.Core;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite.Browser;
using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

#pragma warning disable CA1416

[NonParallelizable]
public sealed class AsyncBoundedWithoutRowidScanTests
{
    private const string InMemoryPath = "bounded-without-rowid.db";

    [Test]
    public async Task EmptyAndCompositePrimaryKeyTablesPreserveLogicalColumnOrder()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile(InMemoryPath, fileSystem))
        using (var connection = database.Connect())
            Execute(connection, "CREATE TABLE items(seq INTEGER, label TEXT, tenant TEXT, PRIMARY KEY(tenant, seq)) WITHOUT ROWID;");

        await using (var emptyConnection = await AhtolaBrowserBoundedConnection.OpenAsync(
            AsyncFileSystemAdapter.Create(fileSystem),
            ownsFileSystem: false, InMemoryPath, pageBudget: 2, CancellationToken.None))
        await using (var emptyReader = await emptyConnection.ExecuteBoundedScanAsync("SELECT * FROM items"))
            (await emptyReader.ReadAsync()).Should().BeFalse();

        using (var database = EmbeddedDatabase.OpenFile(InMemoryPath, fileSystem))
        using (var connection = database.Connect())
            Execute(connection, """
                INSERT INTO items VALUES (9, 'last', 'b');
                INSERT INTO items VALUES (2, 'first', 'a');
                INSERT INTO items VALUES (5, 'middle', 'a');
                """);

        await using var bounded = await AhtolaBrowserBoundedConnection.OpenAsync(
            AsyncFileSystemAdapter.Create(fileSystem),
            ownsFileSystem: false, InMemoryPath, pageBudget: 2, CancellationToken.None);
        await using var reader = await bounded.ExecuteBoundedScanAsync("SELECT * FROM items");
        var rows = new List<(long Seq, string Label, string Tenant)>();
        while (await reader.ReadAsync())
            rows.Add((reader.GetValue(0).AsInteger(), reader.GetValue(1).AsText(), reader.GetValue(2).AsText()));
        rows.Should().Equal((2L, "first", "a"), (5L, "middle", "a"), (9L, "last", "b"));

        await reader.DisposeAsync();
        await using var ordered = await bounded.ExecuteBoundedScanAsync(
            "SELECT label FROM items AS i ORDER BY i.tenant, i.seq LIMIT 2");
        var labels = new List<string>();
        while (await ordered.ReadAsync())
            labels.Add(ordered.GetValue(0).AsText());
        labels.Should().Equal("first", "middle");

        await ordered.DisposeAsync();
        await using var prefix = await bounded.ExecuteBoundedScanAsync(
            "SELECT seq FROM items ORDER BY tenant LIMIT 2");
        var sequence = new List<long>();
        while (await prefix.ReadAsync())
            sequence.Add(prefix.GetValue(0).AsInteger());
        sequence.Should().Equal(2L, 5L);

        await prefix.DisposeAsync();
        await using var reverse = await bounded.ExecuteBoundedScanAsync(
            "SELECT label FROM items ORDER BY tenant DESC, seq DESC LIMIT 2");
        var reverseLabels = new List<string>();
        while (await reverse.ReadAsync())
            reverseLabels.Add(reverse.GetValue(0).AsText());
        reverseLabels.Should().Equal("last", "middle");

        var mixedOrder = async () => await bounded.ExecuteBoundedScanAsync(
            "SELECT label FROM items ORDER BY tenant DESC, seq ASC");
        (await mixedOrder.Should().ThrowAsync<AhtolaBrowserBoundedQueryException>())
            .Which.Message.Should().Contain("ORDER BY");
    }

    [Test]
    public async Task IntegerPrimaryKeyPrefixEqualityFiltersBeforeOffsetAndLimit()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile(InMemoryPath, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE items(payload TEXT, tenant INTEGER, seq INTEGER, PRIMARY KEY(tenant, seq)) WITHOUT ROWID;");
            Execute(connection, """
                INSERT INTO items VALUES ('earlier', -1, 1);
                INSERT INTO items VALUES ('first', 2, 1);
                INSERT INTO items VALUES ('second', 2, 2);
                INSERT INTO items VALUES ('third', 2, 3);
                INSERT INTO items VALUES ('later', 3, 1);
                """);
        }

        await using var bounded = await AhtolaBrowserBoundedConnection.OpenAsync(
            AsyncFileSystemAdapter.Create(fileSystem),
            ownsFileSystem: false, InMemoryPath, pageBudget: 8, CancellationToken.None);

        await using (var reader = await bounded.ExecuteBoundedScanAsync(
            "SELECT payload FROM items AS i WHERE i.tenant = 2 ORDER BY i.tenant, i.seq LIMIT 1 OFFSET 1"))
        {
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetValue(0).AsText().Should().Be("second");
            (await reader.ReadAsync()).Should().BeFalse();
        }

        await using (var reader = await bounded.ExecuteBoundedScanAsync(
            "SELECT seq FROM items WHERE 2 = tenant ORDER BY tenant DESC, seq DESC LIMIT 2"))
        {
            var sequences = new List<long>();
            while (await reader.ReadAsync())
                sequences.Add(reader.GetValue(0).AsInteger());
            sequences.Should().Equal(3L, 2L);
        }

        await using (var missing = await bounded.ExecuteBoundedScanAsync(
                         "SELECT payload FROM items WHERE tenant = 0"))
            (await missing.ReadAsync()).Should().BeFalse();

        var wrongColumn = async () => await bounded.ExecuteBoundedScanAsync(
            "SELECT payload FROM items WHERE seq = 1");
        (await wrongColumn.Should().ThrowAsync<AhtolaBrowserBoundedQueryException>())
            .Which.Message.Should().Contain("WHERE");
    }

    [Test]
    public async Task IntegerPrefixFilterTraversesInteriorRecordsInBothDirections()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile(InMemoryPath, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE items(tenant INTEGER, seq INTEGER, payload TEXT, PRIMARY KEY(tenant, seq)) WITHOUT ROWID;");
            Execute(connection, "BEGIN;");
            for (var tenant = 1; tenant <= 2; tenant++)
            {
                for (var seq = 1; seq <= 80; seq++)
                    Execute(connection,
                        $"INSERT INTO items VALUES ({tenant}, {seq}, '{new string('x', 120)}');");
            }
            Execute(connection, "COMMIT;");
        }

        await using (var pager = await AsyncSqlitePager.OpenAsync(
            AsyncFileSystemAdapter.Create(fileSystem), InMemoryPath, InMemoryPath + "-wal", readOnly: true))
        await using (var snapshot = await pager.BeginReadAsync())
        {
            var cache = new BoundedAsyncPageCache(snapshot, pager.UsableSpace, capacity: 16);
            var page1 = await cache.ReadPageAsync(1);
            var catalog = await AsyncSchemaCatalogLoader.LoadAsync(
                cache, SqliteDatabaseHeader.Parse(page1).TextEncoding);
            catalog.TryGetTable("items", out var entry).Should().BeTrue();
            var root = await cache.ReadPageAsync(entry!.RootPage);
            SqliteBtreePageHeader.Parse(root).PageType.Should().Be(SqliteBtreePageType.IndexInterior);
        }

        await using var bounded = await AhtolaBrowserBoundedConnection.OpenAsync(
            AsyncFileSystemAdapter.Create(fileSystem),
            ownsFileSystem: false, InMemoryPath, pageBudget: 16, CancellationToken.None);

        await using (var reader = await bounded.ExecuteBoundedScanAsync(
            "SELECT seq FROM items WHERE tenant = 2 ORDER BY tenant, seq LIMIT 3 OFFSET 15"))
        {
            var sequences = new List<long>();
            while (await reader.ReadAsync())
                sequences.Add(reader.GetValue(0).AsInteger());
            sequences.Should().Equal(16L, 17L, 18L);
        }

        await using (var reader = await bounded.ExecuteBoundedScanAsync(
            "SELECT seq FROM items WHERE tenant = 2 ORDER BY tenant DESC, seq DESC LIMIT 3 OFFSET 15"))
        {
            var sequences = new List<long>();
            while (await reader.ReadAsync())
                sequences.Add(reader.GetValue(0).AsInteger());
            sequences.Should().Equal(65L, 64L, 63L);
        }
    }

    [Test]
    public async Task ScansNativeMultiLevelIndexInteriorRecordsAndOverflowInPrimaryKeyOrder()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ahtola-bounded-wr-{Guid.NewGuid():N}.db");
        try
        {
            var expected = new List<(string Code, string Payload, long Tag)>();
            using (var native = new MsData.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                native.Open();
                using (var command = native.CreateCommand())
                {
                    command.CommandText = """
                        PRAGMA page_size=512;
                        CREATE TABLE items(payload TEXT, code TEXT PRIMARY KEY, tag INTEGER) WITHOUT ROWID;
                        CREATE INDEX items_tag ON items(tag);
                        """;
                    command.ExecuteNonQuery();
                }

                using (var transaction = native.BeginTransaction())
                using (var insert = native.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT INTO items(payload, code, tag) VALUES ($payload, $code, $tag)";
                    var payload = insert.Parameters.Add("$payload", MsData.SqliteType.Text);
                    var code = insert.Parameters.Add("$code", MsData.SqliteType.Text);
                    var tag = insert.Parameters.Add("$tag", MsData.SqliteType.Integer);
                    for (var id = 239; id >= 0; id--)
                    {
                        code.Value = $"key-{id:D4}-{new string('x', 1100)}";
                        payload.Value = $"value-{id}";
                        tag.Value = id * 2;
                        insert.ExecuteNonQuery();
                    }
                    transaction.Commit();
                }

                using var select = native.CreateCommand();
                select.CommandText = "SELECT code, payload, tag FROM items ORDER BY code";
                using var result = select.ExecuteReader();
                while (result.Read())
                    expected.Add((result.GetString(0), result.GetString(1), result.GetInt64(2)));
            }

            await using (var pager = await AsyncSqlitePager.OpenAsync(
                AsyncFileSystemAdapter.Create(PhysicalFileSystem.Instance), path, path + "-wal", readOnly: true))
            await using (var transaction = await pager.BeginReadAsync())
            {
                var cache = new BoundedAsyncPageCache(transaction, pager.UsableSpace, capacity: 8);
                var page1 = await cache.ReadPageAsync(1);
                var catalog = await AsyncSchemaCatalogLoader.LoadAsync(cache, SqliteDatabaseHeader.Parse(page1).TextEncoding);
                catalog.TryGetTable("items", out var table).Should().BeTrue();
                var root = await cache.ReadPageAsync(table!.RootPage);
                var interior = SqliteIndexInteriorPageView.Parse(root, pager.UsableSpace);
                var child = await cache.ReadPageAsync(interior.Cells[0].Cell.LeftChildPage);
                SqliteBtreePageHeader.Parse(child, isFirstPage: false, pager.UsableSpace).PageType
                    .Should().Be(SqliteBtreePageType.IndexInterior);
            }

            await using (var tooSmall = await AhtolaBrowserBoundedConnection.OpenAsync(
                AsyncFileSystemAdapter.Create(PhysicalFileSystem.Instance),
                ownsFileSystem: false, path, pageBudget: 2, CancellationToken.None))
            {
                await using var reader = await tooSmall.ExecuteBoundedScanAsync("SELECT code FROM items");
                var act = async () => await reader.ReadAsync();
                await act.Should().ThrowAsync<AhtolaBrowserBoundedQueryException>()
                    .WithMessage("*budget*");
            }

            await using var bounded = await AhtolaBrowserBoundedConnection.OpenAsync(
                AsyncFileSystemAdapter.Create(PhysicalFileSystem.Instance),
                ownsFileSystem: false, path, pageBudget: 8, CancellationToken.None);
            await using (var reader = await bounded.ExecuteBoundedScanAsync("SELECT * FROM items"))
            {
                reader.FieldCount.Should().Be(3);
                reader.GetName(0).Should().Be("payload");
                reader.GetName(1).Should().Be("code");
                var actual = new List<(string Code, string Payload, long Tag)>();
                while (await reader.ReadAsync())
                    actual.Add((reader.GetValue(1).AsText(), reader.GetValue(0).AsText(), reader.GetValue(2).AsInteger()));
                actual.Should().Equal(expected);
            }

            await using (var reader = await bounded.ExecuteBoundedScanAsync(
                "SELECT tag, payload FROM items LIMIT 5 OFFSET 117"))
            {
                reader.FieldCount.Should().Be(2);
                reader.GetName(0).Should().Be("tag");
                var actual = new List<(long Tag, string Payload)>();
                while (await reader.ReadAsync())
                    actual.Add((reader.GetValue(0).AsInteger(), reader.GetValue(1).AsText()));
                actual.Should().Equal(expected.Skip(117).Take(5).Select(row => (row.Tag, row.Payload)));
            }

            await using (var reader = await bounded.ExecuteBoundedScanAsync(
                "SELECT code, payload FROM items ORDER BY code DESC LIMIT 3 OFFSET 117"))
            {
                var actual = new List<(string Code, string Payload)>();
                while (await reader.ReadAsync())
                    actual.Add((reader.GetValue(0).AsText(), reader.GetValue(1).AsText()));
                actual.Should().Equal(expected.AsEnumerable().Reverse().Skip(117).Take(3)
                    .Select(row => (row.Code, row.Payload)));
            }
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }

    [Test]
    public async Task RejectsACyclicIndexOverflowChainInsteadOfReturningAPartialRecord()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ahtola-bounded-corrupt-{Guid.NewGuid():N}.db");
        try
        {
            using (var native = new MsData.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                native.Open();
                using var command = native.CreateCommand();
                command.CommandText = $"CREATE TABLE items(code TEXT PRIMARY KEY, payload TEXT) WITHOUT ROWID; "
                    + $"INSERT INTO items VALUES ('{new string('k', 8000)}', 'content');";
                command.ExecuteNonQuery();
            }

            using (var file = PhysicalFileSystem.Instance.OpenFile(path, FileOpenMode.OpenExisting))
            {
                var headerBytes = new byte[100];
                file.Read(0, headerBytes).Should().Be(headerBytes.Length);
                var header = SqliteDatabaseHeader.Parse(headerBytes);
                var rootImage = new byte[header.PageSize];
                file.Read(header.PageSize, rootImage).Should().Be(rootImage.Length);
                var leaf = SqliteIndexLeafPageView.Parse(rootImage, header.UsableSpace);
                var firstOverflowPage = leaf.Cells[0].Cell.FirstOverflowPage;
                firstOverflowPage.Should().NotBeNull();
                var selfPointer = new byte[sizeof(uint)];
                BinaryPrimitives.WriteUInt32BigEndian(selfPointer, firstOverflowPage!.Value);
                file.Write((firstOverflowPage.Value - 1L) * header.PageSize, selfPointer);
            }

            await using var bounded = await AhtolaBrowserBoundedConnection.OpenAsync(
                AsyncFileSystemAdapter.Create(PhysicalFileSystem.Instance),
                ownsFileSystem: false, path, pageBudget: 8, CancellationToken.None);
            await using var reader = await bounded.ExecuteBoundedScanAsync("SELECT payload FROM items");
            var act = async () => await reader.ReadAsync();
            await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*cycle*");
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }

    [Test]
    public async Task OverflowBudgetFaultDoesNotPoisonNextScanAndCancellationIsObserved()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile(InMemoryPath, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE items(payload TEXT, code TEXT PRIMARY KEY) WITHOUT ROWID;");
            Execute(connection, $"INSERT INTO items VALUES ('first', '{new string('a', 8000)}');");
            Execute(connection, "INSERT INTO items VALUES ('second', 'z');");
        }

        await using (var bounded = await AhtolaBrowserBoundedConnection.OpenAsync(
            AsyncFileSystemAdapter.Create(fileSystem),
            ownsFileSystem: false, InMemoryPath, pageBudget: 1, CancellationToken.None))
        {
            await using (var reader = await bounded.ExecuteBoundedScanAsync("SELECT payload FROM items"))
            {
                var act = async () => await reader.ReadAsync();
                await act.Should().ThrowAsync<AhtolaBrowserBoundedQueryException>()
                    .WithMessage("*budget*");
            }

            await using var empty = await bounded.ExecuteBoundedScanAsync("SELECT payload FROM items LIMIT 0");
            (await empty.ReadAsync()).Should().BeFalse();
        }

        using var cancellation = new CancellationTokenSource();
        await using var recovered = await AhtolaBrowserBoundedConnection.OpenAsync(
            AsyncFileSystemAdapter.Create(fileSystem),
            ownsFileSystem: false, InMemoryPath, pageBudget: 3, CancellationToken.None);
        await using (var reader = await recovered.ExecuteBoundedScanAsync(
            "SELECT payload FROM items", cancellation.Token))
        {
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetValue(0).AsText().Should().Be("first");
            cancellation.Cancel();
            var act = async () => await reader.ReadAsync();
            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        await using var afterCancellation = await recovered.ExecuteBoundedScanAsync("SELECT payload FROM items LIMIT 1 OFFSET 1");
        (await afterCancellation.ReadAsync()).Should().BeTrue();
        afterCancellation.GetValue(0).AsText().Should().Be("second");
    }

    [Test]
    public async Task RejectsUnsupportedKeyAndQueryShapesBeforeOpeningAScan()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile(InMemoryPath, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, """
                CREATE TABLE ascending(value TEXT, code TEXT PRIMARY KEY) WITHOUT ROWID;
                CREATE INDEX ascending_value ON ascending(value);
                CREATE TABLE descending(value TEXT, code TEXT PRIMARY KEY DESC) WITHOUT ROWID;
                CREATE TABLE collated(value TEXT, code TEXT PRIMARY KEY COLLATE NOCASE) WITHOUT ROWID;
                """);
        }

        await using var bounded = await AhtolaBrowserBoundedConnection.OpenAsync(
            AsyncFileSystemAdapter.Create(fileSystem),
            ownsFileSystem: false, InMemoryPath, pageBudget: 8, CancellationToken.None);
        foreach (var (sql, message) in new[]
        {
            ("SELECT * FROM descending", "primary key"),
            ("SELECT * FROM collated", "primary key"),
            ("SELECT * FROM ascending WHERE code = 'a'", "WHERE"),
            ("SELECT * FROM ascending WHERE code = 1", "WHERE"),
            ("SELECT * FROM ascending ORDER BY value", "ORDER BY"),
            ("SELECT * FROM ascending ORDER BY value DESC", "ORDER BY"),
            ("SELECT * FROM ascending INDEXED BY ascending_value", "INDEXED BY"),
            ("SELECT rowid FROM ascending", "does not exist"),
        })
        {
            var act = async () => await bounded.ExecuteBoundedScanAsync(sql);
            await act.Should().ThrowAsync<AhtolaBrowserBoundedQueryException>()
                .WithMessage($"*{message}*");
        }

        await using var accepted = await bounded.ExecuteBoundedScanAsync("SELECT code FROM ascending LIMIT 0");
        (await accepted.ReadAsync()).Should().BeFalse();
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
            {
                while (statement.Step() == StatementStepResult.Row)
                {
                }
            }
        }
    }
}

#pragma warning restore CA1416
