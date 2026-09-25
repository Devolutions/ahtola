using Ahtola.Core;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite.Browser;
using AwesomeAssertions;

namespace Ahtola.Tests;

#pragma warning disable CA1416

/// <summary>
/// Exercises <see cref="AhtolaBrowserBoundedConnection"/> and
/// <see cref="AhtolaBrowserBoundedReader"/> end to end through the off-browser
/// <c>IAsyncFileSystem</c> seam (<see cref="AsyncFileSystemAdapter"/> over
/// <see cref="InMemoryFileSystem"/>) — the ADO-facing counterpart of
/// <c>AsyncBoundedRowidScanTests</c>, proving the browser wiring on top of the Core pipeline.
/// </summary>
public sealed class AhtolaBrowserBoundedScanTests
{
    private const string DatabasePath = "bounded-browser.db";

    [Test]
    public async Task ExecutesASupportedScanAndProjectsRequestedColumns()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile(DatabasePath, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, label TEXT);");
            foreach (var i in Enumerable.Range(0, 50))
                Execute(connection, $"INSERT INTO items VALUES ({i}, 'row-{i}');");
        }

        await using var boundedConnection = await AhtolaBrowserBoundedConnection.OpenAsync(
            AsyncFileSystemAdapter.Create(fileSystem),
            ownsFileSystem: false,
            DatabasePath,
            pageBudget: 32,
            CancellationToken.None);

        await using var reader = await boundedConnection.ExecuteBoundedScanAsync("SELECT label FROM items");
        reader.FieldCount.Should().Be(1);

        var labels = new List<string>();
        while (await reader.ReadAsync())
            labels.Add(reader.GetValue(0).AsText());

        labels.Should().HaveCount(50);
        labels[0].Should().Be("row-0");
        labels[49].Should().Be("row-49");
        var readAfterCompletion = () => reader.GetValue(0);
        readAfterCompletion.Should().Throw<InvalidOperationException>()
            .WithMessage("*ReadAsync*true result*");

        await reader.DisposeAsync();
        await reader.DisposeAsync();
    }

    [Test]
    public async Task IndexedRowidTableStillSupportsBaseScanAndPrimaryKeySeek()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile(DatabasePath, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, label TEXT, score INTEGER);");
            Execute(connection, "CREATE INDEX items_score ON items(score);");
            Execute(connection, "INSERT INTO items VALUES (1, 'one', 9), (2, 'two', 7), (3, 'three', 5);");
        }

        await using var boundedConnection = await AhtolaBrowserBoundedConnection.OpenAsync(
            AsyncFileSystemAdapter.Create(fileSystem),
            ownsFileSystem: false,
            DatabasePath,
            pageBudget: 16,
            CancellationToken.None);

        await using (var reader = await boundedConnection.ExecuteBoundedScanAsync(
                         "SELECT label FROM items ORDER BY id"))
        {
            var labels = new List<string>();
            while (await reader.ReadAsync())
                labels.Add(reader.GetValue(0).AsText());
            labels.Should().Equal("one", "two", "three");
        }

        await using (var reader = await boundedConnection.ExecuteBoundedScanAsync(
                         "SELECT id FROM items WHERE id >= 2 ORDER BY id DESC"))
        {
            var ids = new List<long>();
            while (await reader.ReadAsync())
                ids.Add(reader.GetValue(0).AsInteger());
            ids.Should().Equal(3L, 2L);
        }

        var unsupported = async () => await boundedConnection.ExecuteBoundedScanAsync(
            "SELECT id FROM items WHERE score = 7");
        (await unsupported.Should().ThrowAsync<AhtolaBrowserBoundedQueryException>())
            .Which.Message.Should().Contain("WHERE");
    }

    [Test]
    public async Task LeavesAnOrdinarySynchronousReaderOfTheSameDatabaseUnaffected()
    {
        // Proves zero shared code path: a bounded scan connection touching this database does
        // not disturb the ordinary synchronous EmbeddedDatabase/EmbeddedFileStore path a
        // WholeImage mirror connection ultimately opens against the very same bytes.
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile(DatabasePath, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, label TEXT);");
            foreach (var i in Enumerable.Range(0, 20))
                Execute(connection, $"INSERT INTO items VALUES ({i}, 'row-{i}');");
        }

        await using (var boundedConnection = await AhtolaBrowserBoundedConnection.OpenAsync(
            AsyncFileSystemAdapter.Create(fileSystem),
            ownsFileSystem: false,
            DatabasePath,
            pageBudget: 32,
            CancellationToken.None))
        {
            await using var reader = await boundedConnection.ExecuteBoundedScanAsync("SELECT id FROM items");
            while (await reader.ReadAsync())
            {
            }
        }

        using var reopened = EmbeddedDatabase.OpenFile(DatabasePath, fileSystem, readOnly: true);
        using var verifyConnection = reopened.Connect();
        var rows = new List<long>();
        foreach (var statement in verifyConnection.PrepareScript("SELECT id FROM items ORDER BY id;"))
        {
            using (statement)
            {
                while (statement.Step() == StatementStepResult.Row)
                    rows.Add(statement.GetValue(0).AsInteger());
            }
        }

        rows.Should().HaveCount(20);
        rows[0].Should().Be(0L);
        rows[19].Should().Be(19L);
    }

    [Test]
    public async Task RejectsAnUnsupportedShapeWithoutReadingAnyPage()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile(DatabasePath, fileSystem))
        using (var connection = database.Connect())
            Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, label TEXT);");

        await using var boundedConnection = await AhtolaBrowserBoundedConnection.OpenAsync(
            AsyncFileSystemAdapter.Create(fileSystem),
            ownsFileSystem: false,
            DatabasePath,
            pageBudget: 32,
            CancellationToken.None);

        var act = async () => await boundedConnection.ExecuteBoundedScanAsync("SELECT * FROM items WHERE label = 'a'");

        var assertion = await act.Should().ThrowAsync<AhtolaBrowserBoundedQueryException>();
        assertion.Which.Message.Should().Contain("WHERE");

        var unsupportedOrder = async () => await boundedConnection.ExecuteBoundedScanAsync(
            "SELECT id FROM items ORDER BY label");
        var orderAssertion = await unsupportedOrder.Should().ThrowAsync<AhtolaBrowserBoundedQueryException>();
        orderAssertion.Which.Message.Should().Contain("ORDER BY");

        var unsupportedOffset = async () => await boundedConnection.ExecuteBoundedScanAsync(
            "SELECT id FROM items LIMIT 2 OFFSET ?");
        var offsetAssertion = await unsupportedOffset.Should().ThrowAsync<AhtolaBrowserBoundedQueryException>();
        offsetAssertion.Which.Message.Should().Contain("OFFSET");
    }

    [Test]
    public async Task RowidEqualityFiltersBeforeLimitAndKeepsTheScanPageBounded()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile(DatabasePath, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, label TEXT);");
            Execute(connection, "INSERT INTO items VALUES (-5, 'negative');");
            foreach (var i in Enumerable.Range(0, 150))
                Execute(connection, $"INSERT INTO items VALUES ({i}, 'row-{i}');");
        }

        await using var boundedConnection = await AhtolaBrowserBoundedConnection.OpenAsync(
            AsyncFileSystemAdapter.Create(fileSystem),
            ownsFileSystem: false,
            DatabasePath,
            pageBudget: 16,
            CancellationToken.None);

        await using (var reader = await boundedConnection.ExecuteBoundedScanAsync(
                         "SELECT label FROM items AS i WHERE i.id = 120 LIMIT 1"))
        {
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetValue(0).AsText().Should().Be("row-120");
            (await reader.ReadAsync()).Should().BeFalse();
        }

        await using (var reader = await boundedConnection.ExecuteBoundedScanAsync(
                         "SELECT id FROM items WHERE -5 = id"))
        {
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetValue(0).AsInteger().Should().Be(-5);
            (await reader.ReadAsync()).Should().BeFalse();
        }

        await using (var reader = await boundedConnection.ExecuteBoundedScanAsync(
                         "SELECT id FROM items WHERE id = 120 LIMIT 0"))
            (await reader.ReadAsync()).Should().BeFalse();

        await using (var reader = await boundedConnection.ExecuteBoundedScanAsync(
                         "SELECT id FROM items WHERE id = 999"))
            (await reader.ReadAsync()).Should().BeFalse();

        await using (var reader = await boundedConnection.ExecuteBoundedScanAsync(
                         "SELECT id FROM items WHERE id = 120 LIMIT 1 OFFSET 1"))
            (await reader.ReadAsync()).Should().BeFalse();

        await using (var alias = await boundedConnection.ExecuteBoundedScanAsync(
            "SELECT rowid AS key FROM items WHERE id = 120"))
        {
            alias.GetName(0).Should().Be("key");
            (await alias.ReadAsync()).Should().BeTrue();
            alias.GetValue(0).AsInteger().Should().Be(120);
            (await alias.ReadAsync()).Should().BeFalse();
        }
    }

    [Test]
    public async Task HiddenRowidNamesSeekAndOrderWithoutAnIntegerPrimaryKeyAlias()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile(DatabasePath, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE items(label TEXT);");
            Execute(connection, "INSERT INTO items(rowid, label) VALUES (-7, 'negative'), (2, 'second'), (3, 'third'), (4, 'fourth');");
            Execute(connection, "CREATE TABLE shadowed(rowid TEXT, label TEXT);");
            Execute(connection, "INSERT INTO shadowed VALUES ('text-rowid', 'shadowed');");
        }

        await using var bounded = await AhtolaBrowserBoundedConnection.OpenAsync(
            AsyncFileSystemAdapter.Create(fileSystem),
            ownsFileSystem: false, DatabasePath, pageBudget: 8, CancellationToken.None);
        await using (var point = await bounded.ExecuteBoundedScanAsync(
            "SELECT label FROM items AS i WHERE -7 = i._rowid_"))
        {
            (await point.ReadAsync()).Should().BeTrue();
            point.GetValue(0).AsText().Should().Be("negative");
            (await point.ReadAsync()).Should().BeFalse();
        }

        await using (var range = await bounded.ExecuteBoundedScanAsync(
            "SELECT label FROM items WHERE oid BETWEEN 2 AND 4 ORDER BY oid DESC LIMIT 2 OFFSET 1"))
        {
            (await range.ReadAsync()).Should().BeTrue();
            range.GetValue(0).AsText().Should().Be("third");
            (await range.ReadAsync()).Should().BeTrue();
            range.GetValue(0).AsText().Should().Be("second");
            (await range.ReadAsync()).Should().BeFalse();
        }

        await using (var alternate = await bounded.ExecuteBoundedScanAsync(
            "SELECT rowid, _rowid_, label FROM shadowed WHERE _rowid_ = 1"))
        {
            (await alternate.ReadAsync()).Should().BeTrue();
            alternate.GetValue(0).AsText().Should().Be("text-rowid");
            alternate.GetValue(1).AsInteger().Should().Be(1);
            alternate.GetValue(2).AsText().Should().Be("shadowed");
            (await alternate.ReadAsync()).Should().BeFalse();
        }

        var shadowed = async () => await bounded.ExecuteBoundedScanAsync(
            "SELECT label FROM shadowed WHERE rowid = 1");
        await shadowed.Should().ThrowAsync<AhtolaBrowserBoundedQueryException>()
            .WithMessage("*WHERE*");

        var shadowedOrder = async () => await bounded.ExecuteBoundedScanAsync(
            "SELECT label FROM shadowed ORDER BY rowid");
        await shadowedOrder.Should().ThrowAsync<AhtolaBrowserBoundedQueryException>()
            .WithMessage("*ORDER BY*");

        await using (var projection = await bounded.ExecuteBoundedScanAsync(
            "SELECT rowid, label FROM items ORDER BY rowid DESC LIMIT 2"))
        {
            projection.FieldCount.Should().Be(2);
            (await projection.ReadAsync()).Should().BeTrue();
            projection.GetValue(0).AsInteger().Should().Be(4);
            projection.GetValue(1).AsText().Should().Be("fourth");
            (await projection.ReadAsync()).Should().BeTrue();
            projection.GetValue(0).AsInteger().Should().Be(3);
            projection.GetValue(1).AsText().Should().Be("third");
            (await projection.ReadAsync()).Should().BeFalse();
        }

        await using var star = await bounded.ExecuteBoundedScanAsync("SELECT * FROM items LIMIT 1");
        star.FieldCount.Should().Be(1);
    }

    [Test]
    public async Task RowidRangesSeekTheirLowerBoundAndApplyLimitAfterFiltering()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile(DatabasePath, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, label TEXT);");
            Execute(connection, "INSERT INTO items VALUES (-4, 'negative');");
            foreach (var id in Enumerable.Range(0, 200))
                Execute(connection, $"INSERT INTO items VALUES ({id}, 'row-{id}');");
        }

        await using var boundedConnection = await AhtolaBrowserBoundedConnection.OpenAsync(
            AsyncFileSystemAdapter.Create(fileSystem),
            ownsFileSystem: false,
            DatabasePath,
            pageBudget: 16,
            CancellationToken.None);

        await using (var reader = await boundedConnection.ExecuteBoundedScanAsync(
                         "SELECT id FROM items WHERE id >= 119 AND id < 125 ORDER BY id ASC LIMIT 3"))
        {
            var ids = new List<long>();
            while (await reader.ReadAsync())
                ids.Add(reader.GetValue(0).AsInteger());
            ids.Should().Equal(119L, 120L, 121L);
        }

        await using (var reader = await boundedConnection.ExecuteBoundedScanAsync(
                         "SELECT id FROM items AS i WHERE -4 <= i.id AND i.id <= 0 ORDER BY i.id"))
        {
            var ids = new List<long>();
            while (await reader.ReadAsync())
                ids.Add(reader.GetValue(0).AsInteger());
            ids.Should().Equal(-4L, 0L);
        }

        await using (var reader = await boundedConnection.ExecuteBoundedScanAsync(
                         "SELECT id FROM items WHERE id > 120 AND id <= 120"))
            (await reader.ReadAsync()).Should().BeFalse();

        await using (var reader = await boundedConnection.ExecuteBoundedScanAsync(
                         "SELECT id FROM items WHERE id < -4"))
            (await reader.ReadAsync()).Should().BeFalse();
    }

    [Test]
    public async Task DescendingRowidOrderPreservesRangeBoundsAndLimit()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile(DatabasePath, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY);");
            Execute(connection, "INSERT INTO items VALUES (-2);");
            foreach (var id in Enumerable.Range(0, 200))
                Execute(connection, $"INSERT INTO items VALUES ({id});");
        }

        await using var boundedConnection = await AhtolaBrowserBoundedConnection.OpenAsync(
            AsyncFileSystemAdapter.Create(fileSystem),
            ownsFileSystem: false,
            DatabasePath,
            pageBudget: 16,
            CancellationToken.None);

        await using (var reader = await boundedConnection.ExecuteBoundedScanAsync(
                         "SELECT id FROM items WHERE id >= 119 AND id < 125 ORDER BY id DESC LIMIT 3"))
        {
            var ids = new List<long>();
            while (await reader.ReadAsync())
                ids.Add(reader.GetValue(0).AsInteger());
            ids.Should().Equal(124L, 123L, 122L);
        }

        await using (var reader = await boundedConnection.ExecuteBoundedScanAsync(
                         "SELECT id FROM items WHERE id >= 119 AND id < 125 ORDER BY id DESC LIMIT 2 OFFSET 1"))
        {
            var ids = new List<long>();
            while (await reader.ReadAsync())
                ids.Add(reader.GetValue(0).AsInteger());
            ids.Should().Equal(123L, 122L);
        }

        await using (var reader = await boundedConnection.ExecuteBoundedScanAsync(
                         "SELECT id FROM items ORDER BY id DESC LIMIT 2 OFFSET 3"))
        {
            var ids = new List<long>();
            while (await reader.ReadAsync())
                ids.Add(reader.GetValue(0).AsInteger());
            ids.Should().Equal(196L, 195L);
        }

        await using (var reader = await boundedConnection.ExecuteBoundedScanAsync(
                         "SELECT id FROM items ORDER BY id DESC NULLS FIRST LIMIT 2"))
        {
            var ids = new List<long>();
            while (await reader.ReadAsync())
                ids.Add(reader.GetValue(0).AsInteger());
            ids.Should().Equal(199L, 198L);
        }

        await using (var reader = await boundedConnection.ExecuteBoundedScanAsync(
                         "SELECT id FROM items WHERE id <= 0 ORDER BY id DESC"))
        {
            var ids = new List<long>();
            while (await reader.ReadAsync())
                ids.Add(reader.GetValue(0).AsInteger());
            ids.Should().Equal(0L, -2L);
        }

        await using (var reader = await boundedConnection.ExecuteBoundedScanAsync(
                         "SELECT id FROM items WHERE id BETWEEN 5 AND 7 ORDER BY id DESC"))
        {
            var ids = new List<long>();
            while (await reader.ReadAsync())
                ids.Add(reader.GetValue(0).AsInteger());
            ids.Should().Equal(7L, 6L, 5L);
        }
    }

    [Test]
    public async Task RowidRangeHandlesBothSigned64BitExtremes()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile(DatabasePath, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY);");
            Execute(connection, "INSERT INTO items VALUES (-9223372036854775808), (9223372036854775807);");
        }

        await using var boundedConnection = await AhtolaBrowserBoundedConnection.OpenAsync(
            AsyncFileSystemAdapter.Create(fileSystem),
            ownsFileSystem: false,
            DatabasePath,
            pageBudget: 16,
            CancellationToken.None);

        await using (var reader = await boundedConnection.ExecuteBoundedScanAsync(
                         "SELECT id FROM items WHERE id = -9223372036854775808"))
        {
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetValue(0).AsInteger().Should().Be(long.MinValue);
            (await reader.ReadAsync()).Should().BeFalse();
        }

        await using (var reader = await boundedConnection.ExecuteBoundedScanAsync(
                         "SELECT id FROM items WHERE id > 9223372036854775806 ORDER BY id DESC"))
        {
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetValue(0).AsInteger().Should().Be(long.MaxValue);
            (await reader.ReadAsync()).Should().BeFalse();
        }
    }

    [Test]
    public async Task OffsetSkipsAnOverflowRowWithoutLoadingItsPayload()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile(DatabasePath, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, label TEXT);");
            Execute(connection, $"INSERT INTO items VALUES (1, '{new string('x', 6000)}');");
            Execute(connection, "INSERT INTO items VALUES (2, 'small');");
        }

        await using var boundedConnection = await AhtolaBrowserBoundedConnection.OpenAsync(
            AsyncFileSystemAdapter.Create(fileSystem),
            ownsFileSystem: false,
            DatabasePath,
            pageBudget: 1,
            CancellationToken.None);

        await using var reader = await boundedConnection.ExecuteBoundedScanAsync(
            "SELECT id FROM items LIMIT 1 OFFSET 1");
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetValue(0).AsInteger().Should().Be(2);
        (await reader.ReadAsync()).Should().BeFalse();
    }

    [TestCase("SELECT id FROM items WHERE label = 'text'")]
    [TestCase("SELECT id FROM items WHERE id != 0")]
    [TestCase("SELECT id FROM items WHERE id = 1 OR id = 2")]
    [TestCase("SELECT id FROM items WHERE id >= 0 AND label = 'text'")]
    [TestCase("SELECT id FROM items WHERE id = NULL")]
    [TestCase("SELECT id FROM items WHERE id = 1.5")]
    [TestCase("SELECT id FROM items WHERE id NOT BETWEEN 0 AND 1")]
    public async Task RejectsOtherWherePredicatesBeforeStartingAScan(string sql)
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile(DatabasePath, fileSystem))
        using (var connection = database.Connect())
            Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, label TEXT);");

        await using var boundedConnection = await AhtolaBrowserBoundedConnection.OpenAsync(
            AsyncFileSystemAdapter.Create(fileSystem),
            ownsFileSystem: false,
            DatabasePath,
            pageBudget: 16,
            CancellationToken.None);

        var act = async () => await boundedConnection.ExecuteBoundedScanAsync(sql);
        var assertion = await act.Should().ThrowAsync<AhtolaBrowserBoundedQueryException>();
        assertion.Which.Message.Should().Contain("WHERE");
    }

    [Test]
    public async Task SurfacesAnExceededPageBudgetAsABoundedQueryException()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile(DatabasePath, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, label TEXT);");
            foreach (var i in Enumerable.Range(0, 5000))
                Execute(connection, $"INSERT INTO items VALUES ({i}, 'row-{i}-padding-padding-padding');");
        }

        await using var boundedConnection = await AhtolaBrowserBoundedConnection.OpenAsync(
            AsyncFileSystemAdapter.Create(fileSystem),
            ownsFileSystem: false,
            DatabasePath,
            pageBudget: 1,
            CancellationToken.None);

        await using var reader = await boundedConnection.ExecuteBoundedScanAsync("SELECT id FROM items");
        var act = async () =>
        {
            while (await reader.ReadAsync())
            {
            }
        };

        await act.Should().ThrowAsync<AhtolaBrowserBoundedQueryException>();

        await using var seekReader = await boundedConnection.ExecuteBoundedScanAsync(
            "SELECT id FROM items WHERE id = 4999");
        var seekAct = async () => await seekReader.ReadAsync();
        await seekAct.Should().ThrowAsync<AhtolaBrowserBoundedQueryException>();

        await using var rangeReader = await boundedConnection.ExecuteBoundedScanAsync(
            "SELECT id FROM items WHERE id >= 4998 AND id < 5000");
        var rangeAct = async () => await rangeReader.ReadAsync();
        await rangeAct.Should().ThrowAsync<AhtolaBrowserBoundedQueryException>();

        await using var descendingReader = await boundedConnection.ExecuteBoundedScanAsync(
            "SELECT id FROM items WHERE id >= 4998 ORDER BY id DESC");
        var descendingAct = async () => await descendingReader.ReadAsync();
        await descendingAct.Should().ThrowAsync<AhtolaBrowserBoundedQueryException>();
    }

    [Test]
    public async Task DisposingTheConnectionReleasesAnOwnedFileSystemButNotABorrowedOne()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile(DatabasePath, fileSystem))
        using (var connection = database.Connect())
            Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY);");

        var borrowedFileSystem = AsyncFileSystemAdapter.Create(fileSystem);
        var boundedConnection = await AhtolaBrowserBoundedConnection.OpenAsync(
            borrowedFileSystem,
            ownsFileSystem: false,
            DatabasePath,
            pageBudget: 16,
            CancellationToken.None);
        await boundedConnection.DisposeAsync();
        await boundedConnection.DisposeAsync();

        // The borrowed file system must still work: disposing a connection that does not own
        // it must not have torn it down.
        (await borrowedFileSystem.FileExistsAsync(DatabasePath)).Should().BeTrue();
    }

    [Test]
    public async Task DataSourceRejectsBoundedScanForAnInMemoryDatabase()
    {
        await using var dataSource = new AhtolaBrowserDataSource(":memory:");
        var act = async () => await dataSource.OpenBoundedScanConnectionAsync();
        await act.Should().ThrowAsync<PlatformNotSupportedException>();
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
