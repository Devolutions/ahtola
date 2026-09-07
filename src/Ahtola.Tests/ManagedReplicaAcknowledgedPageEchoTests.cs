using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

public sealed partial class ManagedEmbeddedReplicaConnectionTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task IncrementalPageEchoDoesNotReplayAcknowledgedInsert(bool unique)
    {
        var path = NewReplicaPath($"incremental-ack-insert-{unique}");
        var createTable = unique
            ? "CREATE TABLE local_items(id INTEGER PRIMARY KEY, x TEXT);"
            : "CREATE TABLE local_items(id INTEGER, x TEXT);";
        var initialImage = CreatePageEchoImage(path + ".initial", 42, createTable);
        var remoteImage = CreatePageEchoImage(
            path + ".remote", 84, createTable,
            "INSERT INTO local_items(id,x) VALUES(1,'acknowledged');");
        var handler = new ReplicaPushHandler(
        [
            CreatePullResponse("revision-42", initialImage, protocol: 2),
            CreateLogicalPullResponse("revision-42", body: []),
            CreatePullResponse("revision-43", remoteImage, protocol: 2, applyMode: 0),
        ],
        _ => ReplicaPushHandler.SuccessfulBatchResponse(5));

        try
        {
            using var connection = AhtolaConnection.CreateReplica(
                CreateOptions(path, handler, pushOperationsThreshold: 1));
            connection.Open();
            connection.ExecuteNonQuery(
                "INSERT INTO local_items(id,x) VALUES (1,'acknowledged');");
            connection.ExecuteNonQuery(
                "INSERT INTO local_items(id,x) VALUES (2,'pending');");

            var result = await connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
            result.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);
            handler.PushCallCount.Should().Be(1);
            ManagedReplicaChangeJournal.Open(path).ReadBatch(int.MaxValue).Changes
                .Should().ContainSingle();

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT id,x FROM local_items ORDER BY id;";
                using var reader = command.ExecuteReader();
                reader.Read().Should().BeTrue();
                reader.GetInt64(0).Should().Be(1);
                reader.GetString(1).Should().Be("acknowledged");
                reader.Read().Should().BeTrue();
                reader.GetInt64(0).Should().Be(2);
                reader.GetString(1).Should().Be("pending");
                reader.Read().Should().BeFalse();
            }

            using var remoteBase = CreatePageEchoBaseConnection(path);
            remoteBase.Open();
            using var baseCommand = remoteBase.CreateCommand();
            baseCommand.CommandText = "SELECT id,x FROM local_items ORDER BY id;";
            using var baseReader = baseCommand.ExecuteReader();
            baseReader.Read().Should().BeTrue();
            baseReader.GetInt64(0).Should().Be(1);
            baseReader.GetString(1).Should().Be("acknowledged");
            baseReader.Read().Should().BeFalse(
                "the remote base must contain the acknowledged insert exactly once and no pending insert");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }

    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task IncrementalPageEchoPreservesUnacknowledgedAdditiveSchema(bool createTable)
    {
        var path = NewReplicaPath($"incremental-unack-schema-{createTable}");
        var originalTable = createTable
            ? null
            : "CREATE TABLE local_items(id INTEGER PRIMARY KEY, x TEXT);";
        var initialImage = CreatePageEchoImage(path + ".initial", 42, originalTable);
        var remoteImage = CreatePageEchoImage(path + ".remote", 84, originalTable);
        var handler = new ReplicaPushHandler(
        [
            CreatePullResponse("revision-42", initialImage, protocol: 2),
            CreateLogicalPullResponse("revision-42", body: []),
            CreatePullResponse("revision-43", remoteImage, protocol: 2, applyMode: 0),
        ],
        _ => ReplicaPushHandler.SuccessfulBatchResponse(5));

        try
        {
            using var connection = AhtolaConnection.CreateReplica(
                CreateOptions(path, handler, pushOperationsThreshold: 1));
            connection.Open();
            connection.ExecuteNonQuery("UPDATE bootstrap_marker SET value=84;");
            connection.ExecuteNonQuery(createTable
                ? "CREATE TABLE local_items(id INTEGER PRIMARY KEY, x TEXT, extra TEXT);"
                : "ALTER TABLE local_items ADD COLUMN extra TEXT;");
            connection.ExecuteNonQuery(
                "INSERT INTO local_items(id,x,extra) VALUES(1,'pending','local-only');");

            var result = await connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
            result.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);
            handler.PushCallCount.Should().Be(1);
            ReadBootstrapMarker(connection).Should().Be(84);
            var pending = ManagedReplicaChangeJournal.Open(path).ReadBatch(int.MaxValue).Changes;
            pending.Should().HaveCount(2);
            pending.Should().ContainSingle(change => change.Kind == ReplicaLocalChangeKind.Schema);
            pending.Should().ContainSingle(change => change.Kind == ReplicaLocalChangeKind.Row);

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT x,extra FROM local_items;";
                using var reader = command.ExecuteReader();
                reader.Read().Should().BeTrue();
                reader.GetString(0).Should().Be("pending");
                reader.GetString(1).Should().Be("local-only");
                reader.Read().Should().BeFalse();
            }

            using var remoteBase = CreatePageEchoBaseConnection(path);
            remoteBase.Open();
            using var baseCommand = remoteBase.CreateCommand();
            baseCommand.CommandText = "SELECT count(*) FROM pragma_table_info('local_items');";
            baseCommand.ExecuteScalar().Should().Be(createTable ? 0L : 2L,
                "the remote base must not acquire unacknowledged local schema changes");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    private static MsData.SqliteConnection CreatePageEchoBaseConnection(string path)
        => new(new MsData.SqliteConnectionStringBuilder
        {
            DataSource = new Uri(Path.GetFullPath(
                path + ManagedReplicaBootstrapper.BaseSnapshotSuffix)).AbsoluteUri + "?immutable=1",
            Mode = MsData.SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());

    private static byte[] CreatePageEchoImage(
        string path, long marker, string? tableSql, string? insertSql = null)
    {
        try
        {
            using (var image = new AhtolaConnection(
                       $"Data Source={path};Local Provider=Managed;Pooling=False"))
            {
                image.Open();
                image.ExecuteNonQuery("CREATE TABLE bootstrap_marker(value INTEGER NOT NULL);");
                image.ExecuteNonQuery($"INSERT INTO bootstrap_marker VALUES({marker});");
                if (tableSql is not null)
                    image.ExecuteNonQuery(tableSql);
                if (insertSql is not null)
                    image.ExecuteNonQuery(insertSql);
            }

            return File.ReadAllBytes(path);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }
}
