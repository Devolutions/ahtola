using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public class PrimaryKeyDescriptorPropagationFileStoreTests
{
    [Test]
    public void RowidAliasCollationSchemaSurvivesFileReopen()
    {
        const string path = "primary-key-rowid-collation-reopen.db";
        var fileSystem = new InMemoryFileSystem();

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE retained(id INTEGER COLLATE NOCASE PRIMARY KEY, value TEXT);");
            Execute(connection, "INSERT INTO retained VALUES (1, 'durable');");
            Scalar(connection, "SELECT sql FROM sqlite_master WHERE name = 'retained';")
                .AsText()
                .Should()
                .Contain("COLLATE NOCASE");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Scalar(reopenedConnection, "SELECT value FROM retained WHERE id = 1;").AsText().Should().Be("durable");
        Scalar(reopenedConnection, "SELECT sql FROM sqlite_master WHERE name = 'retained';")
            .AsText()
            .Should()
            .Contain("COLLATE NOCASE");
    }

    [Test]
    public void TablePrimaryKeyTermCollationOverridesColumnCollationAcrossFileReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "primary-key-term-override.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE keyed(k TEXT COLLATE NOCASE, PRIMARY KEY(k COLLATE BINARY ASC));");
            Execute(connection, "INSERT INTO keyed VALUES ('a'), ('A');");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Scalar(reopenedConnection, "SELECT COUNT(*) FROM keyed;").AsInteger().Should().Be(2);
        Scalar(reopenedConnection, "SELECT sql FROM sqlite_master WHERE name = 'keyed';")
            .AsText()
            .Should()
            .Contain("PRIMARY KEY(k COLLATE BINARY ASC)");
    }

    [TestCase("custom_collation")]
    public void ApplicationDefinedWithoutRowidPrimaryKeyCollationRejectsBeforeWalWriteAndPreservesCatalog(string collation)
    {
        const string path = "primary-key-unsupported-collation.db";
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE retained(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, "INSERT INTO retained VALUES (1, 'durable');");
            var writesBeforeReject = faults.GetOperationCount(FileSystemOperation.Write);
            faults.FailNext(FileSystemOperation.Write);

            var act = () => Execute(
                connection,
                $"CREATE TABLE rejected(k TEXT, PRIMARY KEY(k COLLATE {collation} ASC)) WITHOUT ROWID;");

            var exception = act.Should().Throw<EmbeddedSqlException>().Which;
            exception.Message.Should().Contain($"application-defined collation {collation.ToUpperInvariant()}");
            exception.Message.Should().NotContain("primary-key index b-tree that is not yet supported");
            faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBeforeReject);
            Scalar(connection, "SELECT value FROM retained WHERE id = 1;").AsText().Should().Be("durable");
            Assert.Throws<EmbeddedSqlException>(() => Scalar(connection, "SELECT COUNT(*) FROM rejected;"));
            faults.ClearScheduled();
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Scalar(reopenedConnection, "SELECT value FROM retained WHERE id = 1;").AsText().Should().Be("durable");
        Assert.Throws<EmbeddedSqlException>(() => Scalar(reopenedConnection, "SELECT COUNT(*) FROM rejected;"));
    }

    [TestCase("CREATE TABLE rejected(k TEXT, PRIMARY KEY(lower(k)));", "Expected RightParen")]
    public void UnsupportedPrimaryKeyExpressionRejectsBeforeWalWrite(string sql, string message)
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        using var database = EmbeddedDatabase.OpenFile("primary-key-unsupported-form.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE retained(id INTEGER PRIMARY KEY, value TEXT);");
        Execute(connection, "INSERT INTO retained VALUES (1, 'durable');");
        var writesBeforeReject = faults.GetOperationCount(FileSystemOperation.Write);
        faults.FailNext(FileSystemOperation.Write);

        var act = () => Execute(connection, sql);

        act.Should().Throw<EmbeddedSqlException>().WithMessage($"*{message}*");
        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBeforeReject);
        Scalar(connection, "SELECT value FROM retained WHERE id = 1;").AsText().Should().Be("durable");
        Assert.Throws<EmbeddedSqlException>(() => Scalar(connection, "SELECT COUNT(*) FROM rejected;"));
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private static SqlValue Scalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0);
    }
}
