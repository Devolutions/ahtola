using Ahtola.Data.Sqlite;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// A managed embedded replica runs statements on its local managed database, so client
/// functions, aggregates and collations register there (Turso bindings/dotnet
/// <c>RegisterManagedReplicaCallbacks</c>), and <c>Sync Experimental Features</c> is either
/// honored or rejected rather than silently dropped.
/// </summary>
public sealed class ManagedReplicaCallbackAndFeatureTests
{
    [Test]
    public void ManagedReplicaRegistersFunctionsAggregatesAndCollationsBeforeAndAfterOpen()
    {
        var path = CreateLocalDatabase("CREATE TABLE words (value TEXT); INSERT INTO words VALUES ('b'), ('a'), ('c');");
        try
        {
            using var replica = new SqliteConnection(ReplicaConnectionString(path));
            replica.Capabilities.SupportsUserDefinedFunctions.Should().BeTrue();
            replica.Capabilities.SupportsUserDefinedAggregates.Should().BeTrue();
            replica.Capabilities.SupportsCustomCollations.Should().BeTrue();

            // Registered before open: applied when the replica's local database opens.
            replica.CreateFunction<string, string>("shout", static value => value.ToUpperInvariant());
            replica.Open();
            replica.Capabilities.Mode.Should().Be(AhtolaConnectionMode.EmbeddedReplica);
            replica.Capabilities.SupportsUserDefinedFunctions.Should().BeTrue();

            // Registered after open: applied to the live local database.
            replica.CreateAggregate<string, string>(
                "concat_all",
                string.Empty,
                static (accumulator, value) => accumulator + value);
            replica.CreateCollation("reverse_text", static (left, right) => string.CompareOrdinal(right, left));

            Scalar(replica, "SELECT shout('quiet')").Should().Be("QUIET");
            Scalar(replica, "SELECT concat_all(value) FROM (SELECT value FROM words ORDER BY value)").Should().Be("abc");
            Scalar(replica, "SELECT group_concat(value, '') FROM (SELECT value FROM words ORDER BY value COLLATE reverse_text)")
                .Should().Be("cba");

            // Writes through a replica also run locally, so callbacks work in DML.
            using (var insert = replica.CreateCommand())
            {
                insert.CommandText = "INSERT INTO words VALUES (shout('d'))";
                insert.ExecuteNonQuery().Should().Be(1);
            }
            Scalar(replica, "SELECT count(*) FROM words WHERE value = 'D'").Should().Be(1L);

            // Unregistering removes the function from the local database too.
            replica.CreateFunction<string, string>("shout", null);
            var removed = () => Scalar(replica, "SELECT shout('x')");
            removed.Should().Throw<SqliteException>().WithMessage("*no such function*");
        }
        finally
        {
            DeleteDatabaseFiles(path);
        }
    }

    [Test]
    public async Task ManagedReplicaReRegistersCallbacksAndFeaturesAfterPublicationReopen()
    {
        var path = CreateLocalDatabase("CREATE TABLE words (value TEXT); INSERT INTO words VALUES ('b'), ('a');");
        try
        {
            using var replica = new SqliteConnection(
                ReplicaConnectionString(path) + "Sync Experimental Features=custom_types;");
            replica.CreateFunction<string, string>("shout", static value => value.ToUpperInvariant());
            replica.CreateAggregate<string, string>(
                "concat_all",
                string.Empty,
                static (accumulator, value) => accumulator + value);
            replica.Open();
            replica.CreateCollation("reverse_text", static (left, right) => string.CompareOrdinal(right, left));

            // A publication disposes the replica's local database and opens a new adapter.
            var host = replica.AhtolaConnection!.ManagedReplicaHostForTesting!;
            var before = replica.AhtolaConnection.ManagedReplicaConnection;
            await host.QuiesceAndReopenAsync(_ => Task.CompletedTask, CancellationToken.None);
            replica.AhtolaConnection.ManagedReplicaConnection.Should().NotBeSameAs(before);

            Scalar(replica, "SELECT shout('quiet')").Should().Be("QUIET");
            Scalar(replica, "SELECT concat_all(value) FROM (SELECT value FROM words ORDER BY value)").Should().Be("ab");
            Scalar(replica, "SELECT group_concat(value, '') FROM (SELECT value FROM words ORDER BY value COLLATE reverse_text)")
                .Should().Be("ba");
            Execute(replica, "CREATE DOMAIN positive AS INTEGER CHECK (value > 0)");
        }
        finally
        {
            DeleteDatabaseFiles(path);
        }
    }

    [Test]
    public void SyncExperimentalCustomTypesEnablesTheManagedTypeDomainSubset()
    {
        var path = CreateLocalDatabase("CREATE TABLE placeholder (value INTEGER);");
        try
        {
            using (var disabled = new SqliteConnection(ReplicaConnectionString(path)))
            {
                disabled.Open();
                var rejected = () => Execute(disabled, "CREATE DOMAIN positive AS INTEGER CHECK (value > 0)");
                rejected.Should().Throw<SqliteException>().WithMessage("*not enabled*");
            }

            using var enabled = new SqliteConnection(
                ReplicaConnectionString(path) + "Sync Experimental Features=strict, custom_types;");
            enabled.Open();
            Execute(enabled, "CREATE DOMAIN positive AS INTEGER CHECK (value > 0)");
            Execute(enabled, "CREATE TABLE amounts (value positive) STRICT");
            Execute(enabled, "INSERT INTO amounts VALUES (5)");
            var invalid = () => Execute(enabled, "INSERT INTO amounts VALUES (-1)");
            invalid.Should().Throw<SqliteException>().WithMessage("*violates check constraint*");
        }
        finally
        {
            DeleteDatabaseFiles(path);
        }
    }

    [Test]
    public void SyncExperimentalFeaturesRejectsUnsupportedAndUnknownNames()
    {
        var path = CreateLocalDatabase("CREATE TABLE placeholder (value INTEGER);");
        try
        {
            foreach (var feature in new[] { "views", "autovacuum", "mvcc_passive_checkpoint" })
            {
                using var unsupported = new SqliteConnection(
                    ReplicaConnectionString(path) + $"Sync Experimental Features=attach,{feature};");
                var open = () => unsupported.Open();
                open.Should().Throw<NotSupportedException>().WithMessage($"*'{feature}' is not supported*");
                unsupported.State.Should().Be(System.Data.ConnectionState.Closed);
            }

            using var unknown = new SqliteConnection(
                ReplicaConnectionString(path) + "Sync Experimental Features=generated_colums;");
            var openUnknown = () => unknown.Open();
            openUnknown.Should().Throw<ArgumentException>().WithMessage("*unknown feature 'generated_colums'*");

            using var alwaysOn = new SqliteConnection(
                ReplicaConnectionString(path)
                + "Sync Experimental Features=strict,index_method,vacuum,encryption,attach,generated_columns,without_rowid,multiprocess_wal;");
            alwaysOn.Open();
            Scalar(alwaysOn, "SELECT count(*) FROM placeholder").Should().Be(0L);
        }
        finally
        {
            DeleteDatabaseFiles(path);
        }
    }

    private static string ReplicaConnectionString(string path)
        => $"Data Source=https://example.test/cluster;Replica Path={path};Local Provider=Managed;Pooling=False;";

    private static string CreateLocalDatabase(string sql)
    {
        var path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"replica-callbacks-{Guid.NewGuid():N}.db");
        using var local = new SqliteConnection($"Data Source={path};Local Provider=Managed;Pooling=False");
        local.Open();
        Execute(local, sql);
        return path;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void DeleteDatabaseFiles(string path)
    {
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
            File.Delete(file);
    }
}
