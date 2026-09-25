using Ahtola.Core;
using Ahtola.Core.Parsing;
using Ahtola.Core.Storage;
using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedTypeRegistryTests
{
    [Test]
    public void DeclarationsRequireOptInAndTypedColumnsFailClosed()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Action disabled = () => Execute(connection, "CREATE DOMAIN positive AS INTEGER NOT NULL CHECK (value > 0)");
        disabled.Should().Throw<EmbeddedSqlException>().WithMessage("*not enabled*");
        Action reserved = () => Execute(connection, "CREATE TABLE main.__turso_internal_types(name TEXT)");
        reserved.Should().Throw<EmbeddedSqlException>().WithMessage("*reserved*");
        connection.ExperimentalCustomTypesEnabled = true;

        Execute(connection, "CREATE DOMAIN positive AS INTEGER DEFAULT 7 NOT NULL CHECK (value > 0)");
        Execute(connection, "CREATE TYPE counter BASE INTEGER");
        Scalar(connection, "SELECT count(*) FROM __turso_internal_types").Should().Be(2);
        database.LiveCatalog.TypeDefinitions.Keys.Should().BeEquivalentTo(["positive", "counter"]);

        Action typed = () => Execute(connection, "CREATE TABLE entries(value positive)");
        typed.Should().Throw<EmbeddedSqlException>().WithMessage("*not yet supported*");
        Action strictTyped = () => Execute(connection, "CREATE TABLE strict_entries(value positive) STRICT");
        strictTyped.Should().Throw<EmbeddedSqlException>().WithMessage("*not yet supported*");
        Action quoted = () => Execute(connection, "CREATE TABLE quoted_entries(value \"positive\")");
        quoted.Should().Throw<EmbeddedSqlException>().WithMessage("*not yet supported*");
        Execute(connection, "CREATE TABLE ordinary(value INTEGER)");
        Action altered = () => Execute(connection, "ALTER TABLE ordinary ADD COLUMN typed \"positive\"");
        altered.Should().Throw<EmbeddedSqlException>().WithMessage("*not yet supported*");
        Action directWrite = () => Execute(connection,
            "INSERT INTO __turso_internal_types VALUES ('forged', 'CREATE TYPE forged BASE INTEGER')");
        directWrite.Should().Throw<EmbeddedSqlException>().WithMessage("*cannot be modified directly*");
        Action directDrop = () => Execute(connection, "DROP TABLE __turso_internal_types");
        directDrop.Should().Throw<EmbeddedSqlException>().WithMessage("*cannot be modified directly*");
        Scalar(connection, "SELECT count(*) FROM __turso_internal_types").Should().Be(2);
    }

    [Test]
    public void FileBackedDefinitionsReloadAndAdvanceTheSchemaCookie()
    {
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"{Guid.NewGuid():N}.db");
        try
        {
            long before;
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                connection.ExperimentalCustomTypesEnabled = true;
                before = Scalar(connection, "PRAGMA schema_version");
                Execute(connection, "CREATE DOMAIN scored AS INTEGER DEFAULT 3 NOT NULL CONSTRAINT positive CHECK (value > 0)");
                Scalar(connection, "PRAGMA schema_version").Should().Be(before + 1);
                Execute(connection, "CREATE TYPE counter BASE INTEGER");
                Scalar(connection, "PRAGMA schema_version").Should().Be(before + 2);
                Execute(connection, "CREATE DOMAIN IF NOT EXISTS scored AS INTEGER");
                Scalar(connection, "PRAGMA schema_version").Should().Be(before + 2);
            }

            using var reopened = EmbeddedDatabase.OpenFile(path);
            using var reader = reopened.Connect();
            reopened.LiveCatalog.TypeDefinitions.Keys.Should().BeEquivalentTo(["scored", "counter"]);
            var domain = reopened.LiveCatalog.TypeDefinitions["scored"]
                .Should().BeOfType<CreateDomainStatement>().Which;
            domain.NotNull.Should().BeTrue();
            domain.Default.Should().NotBeNull();
            domain.Checks.Should().ContainSingle().Which.Name.Should().Be("positive");
            Scalar(reader, "SELECT count(*) FROM __turso_internal_types").Should().Be(2);
            Scalar(reader, "PRAGMA schema_version").Should().BeGreaterThan(0);
            reader.ExperimentalCustomTypesEnabled = true;
            Action duplicate = () => Execute(reader, "CREATE DOMAIN scored AS INTEGER");
            duplicate.Should().Throw<EmbeddedSqlException>().WithMessage("*already exists*");
            Action typed = () => Execute(reader, "CREATE TABLE amounts(value scored)");
            typed.Should().Throw<EmbeddedSqlException>().WithMessage("*not yet supported*");
            Execute(reader, "CREATE TABLE ordinary(value INTEGER)");
            Execute(reader, "INSERT INTO ordinary VALUES (4)");
            Scalar(reader, "SELECT value FROM ordinary").Should().Be(4);
            Scalar(reader, "PRAGMA schema_version").Should().Be(before + 3);
        }
        finally
        {
            DeleteDatabaseFiles(path);
        }
    }

    [Test]
    public void RollbackAndSavepointDiscardDefinitions()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        connection.ExperimentalCustomTypesEnabled = true;
        Execute(connection, "BEGIN");
        Execute(connection, "CREATE DOMAIN outer_domain AS INTEGER");
        Execute(connection, "SAVEPOINT inner");
        Execute(connection, "CREATE DOMAIN inner_domain AS INTEGER");
        Scalar(connection, "SELECT count(*) FROM __turso_internal_types").Should().Be(2);
        Execute(connection, "ROLLBACK TO inner");
        Scalar(connection, "SELECT count(*) FROM __turso_internal_types").Should().Be(1);
        Execute(connection, "ROLLBACK");
        database.LiveCatalog.TypeDefinitions.Should().BeEmpty();
        Action missingBacking = () => Scalar(connection, "SELECT count(*) FROM __turso_internal_types");
        missingBacking.Should().Throw<EmbeddedSqlException>();
    }

    [Test]
    public void FileRollbackDoesNotPublishTheRegistryOrSchemaCookie()
    {
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"{Guid.NewGuid():N}.db");
        try
        {
            long before;
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                connection.ExperimentalCustomTypesEnabled = true;
                before = Scalar(connection, "PRAGMA schema_version");
                Execute(connection, "BEGIN");
                Execute(connection, "CREATE DOMAIN uncommitted AS INTEGER NOT NULL");
                database.LiveCatalog.TypeDefinitions.Should().BeEmpty();
                Execute(connection, "ROLLBACK");
                database.LiveCatalog.TypeDefinitions.Should().BeEmpty();
            }

            using var reopened = EmbeddedDatabase.OpenFile(path);
            using var reader = reopened.Connect();
            reopened.LiveCatalog.TypeDefinitions.Should().BeEmpty();
            Scalar(reader, "PRAGMA schema_version").Should().Be(before);
        }
        finally
        {
            DeleteDatabaseFiles(path);
        }
    }

    [Test]
    public void APeerRefreshSeesCommittedDefinitionsWithoutSeeingRolledBackOnes()
    {
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"{Guid.NewGuid():N}.db");
        try
        {
            using var writerDatabase = EmbeddedDatabase.OpenFile(path);
            using var readerDatabase = EmbeddedDatabase.OpenFile(path);
            using var writer = writerDatabase.Connect();
            using var reader = readerDatabase.Connect();
            writer.ExperimentalCustomTypesEnabled = true;
            reader.ExperimentalCustomTypesEnabled = true;

            Execute(writer, "BEGIN");
            Execute(writer, "CREATE DOMAIN provisional AS INTEGER");
            readerDatabase.LiveCatalog.TypeDefinitions.Should().BeEmpty();
            Execute(writer, "ROLLBACK");
            Execute(writer, "CREATE DOMAIN committed AS INTEGER");
            Scalar(reader, "SELECT count(*) FROM __turso_internal_types").Should().Be(1);
            readerDatabase.LiveCatalog.TypeDefinitions.Keys.Should().ContainSingle().Which.Should().Be("committed");
        }
        finally
        {
            DeleteDatabaseFiles(path);
        }
    }

    [Test]
    public void ForeignTypedTableFailsOpenRatherThanWritingWithOrdinaryAffinity()
    {
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"{Guid.NewGuid():N}.db");
        try
        {
            using (var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                sqlite.Open();
                using var command = sqlite.CreateCommand();
                command.CommandText = """
                    CREATE TABLE __turso_internal_types(name TEXT PRIMARY KEY, sql TEXT);
                    INSERT INTO __turso_internal_types(name, sql)
                    VALUES ('positive', 'CREATE DOMAIN positive AS INTEGER CHECK (value > 0)');
                    CREATE TABLE foreign_values(value positive);
                    """;
                command.ExecuteNonQuery();
            }

            Action open = () =>
            {
                using var database = EmbeddedDatabase.OpenFile(path);
            };
            open.Should().Throw<EmbeddedSqlException>().WithMessage("*not yet supported*");
        }
        finally
        {
            DeleteDatabaseFiles(path);
        }
    }

    [Test]
    public async Task BoundedAsyncCatalogFailsClosedWhenTypeDefinitionsExist()
    {
        const string path = "typed-registry-async.db";
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            connection.ExperimentalCustomTypesEnabled = true;
            Execute(connection, "CREATE DOMAIN positive AS INTEGER");
            Execute(connection, "CREATE TABLE ordinary(value INTEGER)");
        }

        await using var pager = await AsyncSqlitePager.OpenAsync(
            AsyncFileSystemAdapter.Create(fileSystem),
            path,
            path + "-wal",
            readOnly: true);
        await using var readTransaction = await pager.BeginReadAsync();
        var cache = new BoundedAsyncPageCache(readTransaction, pager.UsableSpace, capacity: 32);

        var load = async () => await AsyncSchemaCatalogLoader.LoadAsync(cache, SqliteTextEncoding.Utf8);
        await load.Should().ThrowAsync<EmbeddedSqlException>()
            .WithMessage("*not yet supported*");
    }

    private static long Scalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        var value = statement.GetValue(0).AsInteger();
        statement.Step().Should().Be(StatementStepResult.Done);
        return value;
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    private static void DeleteDatabaseFiles(string path)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = path + suffix;
            if (File.Exists(file))
                File.Delete(file);
        }
    }
}
