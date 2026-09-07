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

        var act = async () => await boundedConnection.ExecuteBoundedScanAsync("SELECT * FROM items WHERE id = 1");

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
