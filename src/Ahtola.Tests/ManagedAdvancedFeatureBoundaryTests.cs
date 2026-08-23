using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class ManagedAdvancedFeatureBoundaryTests
{
    [Test]
    public void ManagedEngineEnablesMvccOnMemoryDatabasesAndVectorFunctions()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        ReadValue(connection, "PRAGMA journal_mode = mvcc;").Should().Be(SqlValue.Text("mvcc"));
        ReadValue(connection, "PRAGMA journal_mode;").Should().Be(SqlValue.Text("mvcc"));
        database.IsMvccEnabled.Should().BeTrue();

        ReadValue(connection, "SELECT vector_extract(vector32('[1.0, 2.0]'));")
            .Should().Be(SqlValue.Text("[1,2]"));
    }

    [Test]
    public void ManagedFileEngineEnablesMvccWhileKeepingWalUnderneath()
    {
        const string path = "enabled-mvcc.db";
        var fileSystem = new InMemoryFileSystem();

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            ReadValue(connection, "PRAGMA journal_mode=WAL;").Should().Be(SqlValue.Text("wal"));
            ReadValue(connection, "PRAGMA journal_mode=mvcc;").Should().Be(SqlValue.Text("mvcc"));
            ReadValue(connection, "PRAGMA journal_mode;").Should().Be(SqlValue.Text("mvcc"));
            database.IsMvccEnabled.Should().BeTrue();
            Execute(connection, "BEGIN CONCURRENT;");
            Execute(connection, "COMMIT;");
        }

        // Header version 255 + log restore MVCC on cold open.
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadValue(reopenedConnection, "PRAGMA journal_mode;").Should().Be(SqlValue.Text("mvcc"));
        reopened.IsMvccEnabled.Should().BeTrue();
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static SqlValue ReadValue(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0);
    }
}
