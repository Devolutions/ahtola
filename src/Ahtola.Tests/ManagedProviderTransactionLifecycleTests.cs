using AwesomeAssertions;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public class ManagedProviderTransactionLifecycleTests
{
    [Test]
    public void ClosingManagedConnectionRollsBackAndDetachesActiveTransaction()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"managed-transaction-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = new SqliteConnection($"Data Source={path};Local Provider=Managed");
            connection.Open();
            connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");

            var transaction = connection.BeginTransaction();
            connection.ExecuteNonQuery("INSERT INTO data VALUES (1);");

            connection.Close();

            connection.Transaction.Should().BeNull();
            transaction.Connection.Should().BeNull();

            connection.Open();
            connection.ExecuteScalar<long>("SELECT COUNT(*) FROM data;").Should().Be(0);

            using var nextTransaction = connection.BeginTransaction();
            nextTransaction.Rollback();
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var candidate = path + suffix;
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
        }
    }

    [Test]
    public void ClosingManagedConnectionClosesOpenReaderBeforeReopen()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"managed-reader-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = new SqliteConnection($"Data Source={path};Local Provider=Managed");
            connection.Open();
            connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER); INSERT INTO data VALUES (1);");

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM data;";
            using var reader = command.ExecuteReader();
            reader.Read().Should().BeTrue();

            connection.Close();

            reader.IsClosed.Should().BeTrue();
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO data VALUES (2);");
            connection.ExecuteScalar<long>("SELECT COUNT(*) FROM data;").Should().Be(2);
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var candidate = path + suffix;
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
        }
    }

    [Test]
    public void ExecuteScalarRollbackDetachesManagedAmbientTransaction()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.CommandText = "ROLLBACK;";

        command.ExecuteScalar().Should().BeNull();

        connection.Transaction.Should().BeNull();
        transaction.Connection.Should().BeNull();
        transaction.Invoking(static value => value.Rollback())
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage(Data.Sqlite.Properties.Resources.TransactionCompleted);
        connection.ExecuteScalar<long>("SELECT 1;").Should().Be(1);

        using var nextTransaction = connection.BeginTransaction();
        nextTransaction.Rollback();
    }
}
