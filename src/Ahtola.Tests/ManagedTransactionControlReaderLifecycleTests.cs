using AwesomeAssertions;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public sealed class ManagedTransactionControlReaderLifecycleTests
{
    [Test]
    public void ManagedExecuteReaderDetachesTransactionAfterSqlCommit()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");

        using var transaction = connection.BeginTransaction();
        connection.ExecuteNonQuery("INSERT INTO data VALUES (1);");

        using (var command = new SqliteCommand("COMMIT;", connection, transaction))
        using (var reader = command.ExecuteReader())
            reader.FieldCount.Should().Be(0);

        connection.Transaction.Should().BeNull();
        transaction.Connection.Should().BeNull();
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM data;").Should().Be(1);

        using var nextTransaction = connection.BeginTransaction();
        nextTransaction.Rollback();
    }

    [Test]
    public void ManagedReaderCloseDetachesSqlRollbackAfterDrainingIt()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");

        using var transaction = connection.BeginTransaction();
        connection.ExecuteNonQuery("INSERT INTO data VALUES (1);");

        using (var command = new SqliteCommand("SELECT value FROM data; ROLLBACK;", connection, transaction))
        using (var reader = command.ExecuteReader())
            reader.Read().Should().BeTrue();

        connection.Transaction.Should().BeNull();
        transaction.Connection.Should().BeNull();
        transaction.Invoking(static value => value.Rollback())
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage(Data.Sqlite.Properties.Resources.TransactionCompleted);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM data;").Should().Be(0);

        using var nextTransaction = connection.BeginTransaction();
        nextTransaction.Rollback();
    }
}
