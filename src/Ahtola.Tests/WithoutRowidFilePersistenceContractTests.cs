using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public class WithoutRowidFilePersistenceContractTests
{
    [Test]
    public void BoundedWithoutRowidPersistenceLeavesTheDurableCatalogRecoverable()
    {
        const string path = "without-rowid-atomic-persistence.db";
        var fileSystem = new InMemoryFileSystem();

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE retained(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, "INSERT INTO retained VALUES (1, 'durable');");

            Execute(connection, "CREATE TABLE persisted(k TEXT PRIMARY KEY, value TEXT) WITHOUT ROWID;");
            Execute(connection, "INSERT INTO persisted VALUES ('key', 'persisted');");
            Scalar(connection, "SELECT value FROM retained WHERE id = 1;").AsText().Should().Be("durable");
            Scalar(connection, "SELECT value FROM persisted WHERE k = 'key';").AsText().Should().Be("persisted");
        }

        using var recovered = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var recoveredConnection = recovered.Connect();
        Scalar(recoveredConnection, "SELECT value FROM retained WHERE id = 1;").AsText().Should().Be("durable");
        Scalar(recoveredConnection, "SELECT value FROM persisted WHERE k = 'key';").AsText().Should().Be("persisted");
    }

    [Test]
    public void FailedTableLevelPrimaryKeyPublicationLeavesThePriorDurableCatalogRecoverable()
    {
        const string path = "table-primary-key-atomic-rejection.db";
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE retained(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, "INSERT INTO retained VALUES (1, 'durable');");

            faults.FailNext(FileSystemOperation.Write);
            Assert.Throws<IOException>(() => Execute(
                connection,
                "CREATE TABLE rejected(a INTEGER, b TEXT, PRIMARY KEY(a, b));"));
            Scalar(connection, "SELECT value FROM retained WHERE id = 1;").AsText().Should().Be("durable");
            Assert.Throws<EmbeddedSqlException>(() => Scalar(connection, "SELECT COUNT(*) FROM rejected;"));
        }

        using var recovered = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var recoveredConnection = recovered.Connect();
        Scalar(recoveredConnection, "SELECT value FROM retained WHERE id = 1;").AsText().Should().Be("durable");
        Assert.Throws<EmbeddedSqlException>(() => Scalar(recoveredConnection, "SELECT COUNT(*) FROM rejected;"));
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
