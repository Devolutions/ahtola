using AwesomeAssertions;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Ahtola.Core;

namespace Ahtola.Tests;

public sealed class ManagedEmbeddedReplicaConnectionTests
{
    private const int BootstrapStagedDatabaseBoundary = (int)ManagedReplicaDurableBoundary.BootstrapStagedDatabase;
    private const int BootstrapDatabasePublishedBoundary = (int)ManagedReplicaDurableBoundary.BootstrapDatabasePublished;
    private const int IncrementalApplyStagedDatabaseBoundary = (int)ManagedReplicaDurableBoundary.IncrementalApplyStagedDatabase;
    private const int IncrementalApplyDatabasePublishedBoundary = (int)ManagedReplicaDurableBoundary.IncrementalApplyDatabasePublished;
    private const int IncrementalApplyMetadataPublishedBoundary = (int)ManagedReplicaDurableBoundary.IncrementalApplyMetadataPublished;
    private const int LogicalApplyCommittedBoundary = (int)ManagedReplicaDurableBoundary.LogicalApplyCommitted;
    private const int LogicalApplyCheckpointedBoundary = (int)ManagedReplicaDurableBoundary.LogicalApplyCheckpointed;
    private const int LogicalApplyMetadataPublishedBoundary = (int)ManagedReplicaDurableBoundary.LogicalApplyMetadataPublished;

    [Test]
    public void ReplicaOptionsNormalizeLibsqlUrlsToHttps()
    {
        var options = new AhtolaReplicaOptions(
            "replica.db",
            new Uri("libsql://example.test/cluster"),
            authToken: null);

        options.RemoteUri.Should().Be(new Uri("https://example.test/cluster"));
    }

    [Test]
    public void WithoutLongPollSharesTheReentrancyScopeWithItsSourceOptions()
    {
        var options = new AhtolaReplicaOptions("replica.db", new Uri("https://example.test"), authToken: null);
        var withoutLongPoll = options.WithoutLongPoll();

        using (options.EnterApplicationHttpScope())
        {
            // A reentrant call observed through the derived (WithoutLongPoll) options instance
            // must be detected: it is the SAME logical connection's HTTP call stack (e.g. the
            // one-shot fresh-bootstrap catch-up pull), not an independent one.
            Action reentrant = () => withoutLongPoll.ThrowIfApplicationHttpReentrant(closing: false);
            reentrant.Should().Throw<InvalidOperationException>()
                .WithMessage("*cannot be reentered*");
        }

        // Once the scope exits (entered via the original instance), the derived instance must
        // also observe it as inactive again.
        Action afterExit = () => withoutLongPoll.ThrowIfApplicationHttpReentrant(closing: false);
        afterExit.Should().NotThrow();
    }

    [Test]
    public void EnteringTheScopeViaWithoutLongPollIsObservedByTheSourceOptions()
    {
        var options = new AhtolaReplicaOptions("replica.db", new Uri("https://example.test"), authToken: null);
        var withoutLongPoll = options.WithoutLongPoll();

        using (withoutLongPoll.EnterApplicationHttpScope())
        {
            Action reentrant = () => options.ThrowIfApplicationHttpReentrant(closing: false);
            reentrant.Should().Throw<InvalidOperationException>();
        }

        Action afterExit = () => options.ThrowIfApplicationHttpReentrant(closing: false);
        afterExit.Should().NotThrow();
    }

    [Test]
    public void WithoutLongPollDoesNotShareTheScopeWithAnUnrelatedCloneForConnection()
    {
        // CloneForConnection() represents a genuinely independent connection (a fresh
        // AhtolaConnection built from a shared template AhtolaReplicaOptions), so it correctly
        // keeps its own separate reentrancy scope; only WithoutLongPoll's derivation (the same
        // connection's one-shot catch-up pull) needs to share state with its source.
        var options = new AhtolaReplicaOptions("replica.db", new Uri("https://example.test"), authToken: null);
        var forAnotherConnection = options.CloneForConnection();

        using (options.EnterApplicationHttpScope())
        {
            Action act = () => forAnotherConnection.ThrowIfApplicationHttpReentrant(closing: false);
            act.Should().NotThrow();
        }
    }

    [Test]
    public void ManagedReplicaJournalCapturesCommittedAutocommitMutation()
    {
        var path = NewReplicaPath("managed-replica-journal-autocommit");
        try
        {
            CreateJournalDatabase(path);
            using var connection = AhtolaConnection.CreateReplica(
                new AhtolaReplicaOptions(path, new Uri("https://example.test"), authToken: null));
            connection.Open();

            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");

            var batch = connection.ReadManagedReplicaLocalChanges(10);
            batch.FirstSequence.Should().Be(1);
            batch.Watermark.Should().Be(2);
            batch.Changes.Should().ContainSingle();
            var change = batch.Changes[0];
            change.Sequence.Should().Be(1);
            change.Kind.Should().Be(ReplicaLocalChangeKind.Row);
            change.Operation.Should().Be(SqliteChangeOperation.Insert);
            change.Database.Should().Be("main");
            change.Table.Should().Be("journal_events");
            change.RowId.Should().Be(1);
            File.Exists(path + ManagedReplicaChangeJournal.Suffix).Should().BeTrue();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void ManagedReplicaBatchSupportsAutocommitWritesAndReportsCapabilities()
    {
        var path = NewReplicaPath("managed-replica-batch-autocommit");
        try
        {
            CreateJournalDatabase(path);
            using var connection = AhtolaConnection.CreateReplica(
                new AhtolaReplicaOptions(path, new Uri("https://example.test"), authToken: null));
            connection.Open();

            connection.CanCreateBatch.Should().BeTrue();
            connection.Capabilities.CanCreateBatch.Should().BeTrue();
            using var batch = connection.CreateBatch();
            batch.Should().BeOfType<AhtolaBatch>();
            var managedBatch = (AhtolaBatch)batch;
            managedBatch.BatchCommands.Add(new AhtolaBatchCommand("INSERT INTO journal_events VALUES (10);"));
            managedBatch.BatchCommands.Add(new AhtolaBatchCommand("INSERT INTO journal_events VALUES (20);"));

            managedBatch.ExecuteNonQuery().Should().Be(2);
            managedBatch.BatchCommands.Select(command => command.RecordsAffected).Should().Equal(1, 1);
            connection.ReadManagedReplicaLocalChanges(10).Changes
                .Select(change => change.RowId)
                .Should()
                .Equal(1, 2);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task ManagedReplicaBatchPreservesTransactionAndCancellationSemantics()
    {
        var path = NewReplicaPath("managed-replica-batch-transaction");
        try
        {
            CreateJournalDatabase(path);
            using var connection = AhtolaConnection.CreateReplica(
                new AhtolaReplicaOptions(path, new Uri("https://example.test"), authToken: null));
            connection.Open();

            using (var transaction = connection.BeginTransaction())
            {
                using var batch = (AhtolaBatch)connection.CreateBatch();
                batch.BatchCommands.Add(new AhtolaBatchCommand("INSERT INTO journal_events VALUES (10);"));
                batch.BatchCommands.Add(new AhtolaBatchCommand("INSERT INTO journal_events VALUES (20);"));
                (await batch.ExecuteNonQueryAsync(CancellationToken.None)).Should().Be(2);
                connection.ReadManagedReplicaLocalChanges(10).Changes.Should().BeEmpty();
                transaction.Commit();
            }

            using (var transaction = connection.BeginTransaction())
            {
                using var batch = (AhtolaBatch)connection.CreateBatch();
                batch.BatchCommands.Add(new AhtolaBatchCommand("INSERT INTO journal_events VALUES (30);"));
                batch.ExecuteNonQuery().Should().Be(1);
                transaction.Rollback();
            }

            using (var cancelledBatch = (AhtolaBatch)connection.CreateBatch())
            {
                cancelledBatch.BatchCommands.Add(new AhtolaBatchCommand("INSERT INTO journal_events VALUES (40);"));
                Assert.ThrowsAsync<OperationCanceledException>(
                    async () => await cancelledBatch.ExecuteNonQueryAsync(new CancellationToken(canceled: true)));
            }

            connection.ReadManagedReplicaLocalChanges(10).Changes
                .Select(change => change.RowId)
                .Should()
                .Equal(1, 2);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void ManagedReplicaBatchPreservesMixedResultOrderingAndRejectsRemoteConditions()
    {
        var path = NewReplicaPath("managed-replica-batch-results");
        try
        {
            CreateJournalDatabase(path);
            using var connection = AhtolaConnection.CreateReplica(
                new AhtolaReplicaOptions(path, new Uri("https://example.test"), authToken: null));
            connection.Open();

            using (var batch = (AhtolaBatch)connection.CreateBatch())
            {
                batch.BatchCommands.Add(new AhtolaBatchCommand("INSERT INTO journal_events VALUES (10);"));
                batch.BatchCommands.Add(new AhtolaBatchCommand("SELECT value FROM journal_events ORDER BY value;"));
                batch.BatchCommands.Add(new AhtolaBatchCommand("INSERT INTO journal_events VALUES (20);"));
                batch.BatchCommands.Add(new AhtolaBatchCommand("SELECT value FROM journal_events ORDER BY value;"));

                using var reader = batch.ExecuteReader();
                reader.FieldCount.Should().Be(0);
                reader.NextResult().Should().BeTrue();
                reader.Read().Should().BeTrue();
                reader.GetInt64(0).Should().Be(10);
                reader.Read().Should().BeFalse();
                reader.NextResult().Should().BeTrue();
                reader.FieldCount.Should().Be(0);
                reader.NextResult().Should().BeTrue();
                reader.Read().Should().BeTrue();
                reader.GetInt64(0).Should().Be(10);
                reader.Read().Should().BeTrue();
                reader.GetInt64(0).Should().Be(20);
                reader.NextResult().Should().BeFalse();
                reader.RecordsAffected.Should().Be(2);
            }

            using var conditionalBatch = (AhtolaBatch)connection.CreateBatch();
            conditionalBatch.BatchCommands.Add(new AhtolaBatchCommand("SELECT 1")
            {
                RemoteCondition = AhtolaRemoteBatchCondition.IsAutocommit,
            });
            Assert.Throws<NotSupportedException>(() => conditionalBatch.ExecuteNonQuery())!
                .Message.Should().Be("RemoteCondition requires a remote Ahtola connection.");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void ManagedReplicaJournalCapturesOnlyCommittedTransactionMutations()
    {
        var path = NewReplicaPath("managed-replica-journal-commit");
        try
        {
            CreateJournalDatabase(path);
            using var connection = AhtolaConnection.CreateReplica(
                new AhtolaReplicaOptions(path, new Uri("https://example.test"), authToken: null));
            connection.Open();

            using (var transaction = connection.BeginTransaction())
            {
                connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
                connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (20);");
                connection.ReadManagedReplicaLocalChanges(10).Changes.Should().BeEmpty();
                transaction.Commit();
            }

            var batch = connection.ReadManagedReplicaLocalChanges(10);
            batch.Changes.Select(change => change.Sequence).Should().Equal(1, 2);
            batch.Changes.Select(change => change.RowId).Should().Equal(1, 2);
            batch.Watermark.Should().Be(3);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void ManagedReplicaJournalLeavesRolledBackTransactionOutOfBatch()
    {
        var path = NewReplicaPath("managed-replica-journal-rollback");
        try
        {
            CreateJournalDatabase(path);
            using var connection = AhtolaConnection.CreateReplica(
                new AhtolaReplicaOptions(path, new Uri("https://example.test"), authToken: null));
            connection.Open();

            using (var transaction = connection.BeginTransaction())
            {
                connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
                transaction.Rollback();
            }

            connection.ReadManagedReplicaLocalChanges(10).Changes.Should().BeEmpty();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void ManagedReplicaJournalPreservesCommitOrderAcrossTransactions()
    {
        var path = NewReplicaPath("managed-replica-journal-order");
        try
        {
            CreateJournalDatabase(path);
            using var connection = AhtolaConnection.CreateReplica(
                new AhtolaReplicaOptions(path, new Uri("https://example.test"), authToken: null));
            connection.Open();

            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
            using (var transaction = connection.BeginTransaction())
            {
                connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (20);");
                connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (30);");
                transaction.Commit();
            }
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (40);");

            var batch = connection.ReadManagedReplicaLocalChanges(10);
            batch.Changes.Select(change => change.Sequence).Should().Equal(1, 2, 3, 4);
            batch.Changes.Select(change => change.RowId).Should().Equal(1, 2, 3, 4);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void ManagedReplicaJournalSurvivesReopen()
    {
        var path = NewReplicaPath("managed-replica-journal-reopen");
        try
        {
            CreateJournalDatabase(path);
            var options = new AhtolaReplicaOptions(path, new Uri("https://example.test"), authToken: null);
            using (var connection = AhtolaConnection.CreateReplica(options))
            {
                connection.Open();
                using var writeBatch = (AhtolaBatch)connection.CreateBatch();
                writeBatch.BatchCommands.Add(new AhtolaBatchCommand("INSERT INTO journal_events VALUES (10);"));
                writeBatch.ExecuteNonQuery().Should().Be(1);
            }

            using var reopened = AhtolaConnection.CreateReplica(options);
            reopened.Open();
            var batch = reopened.ReadManagedReplicaLocalChanges(10);
            batch.Changes.Should().ContainSingle();
            batch.Changes[0].Sequence.Should().Be(1);
            batch.Changes[0].Operation.Should().Be(SqliteChangeOperation.Insert);
            batch.Watermark.Should().Be(2);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void CreateReplicaUsesManagedLocalSqlAndPersistsCommittedTransactions()
    {
        var path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"managed-embedded-replica-{Guid.NewGuid():N}.db");
        var options = new AhtolaReplicaOptions(
            path,
            new Uri("https://example.com"),
            authToken: null);

        try
        {
            CreateInitializedDatabase(path);

            using (var connection = AhtolaConnection.CreateReplica(options))
            {
                connection.Open();
                connection.State.Should().Be(System.Data.ConnectionState.Open);
                connection.Capabilities.Mode.Should().Be(AhtolaConnectionMode.EmbeddedReplica);
                connection.Capabilities.SupportsSync.Should().BeTrue();
                Assert.Throws<NotSupportedException>(() => connection.Sync())!.Message.Should()
                    .Be("Managed embedded replica synchronization requires bootstrap metadata.");

                connection.ExecuteNonQuery("CREATE TABLE events(value INTEGER NOT NULL);");
                using (var transaction = connection.BeginTransaction())
                {
                    using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT INTO events VALUES (41);";
                    insert.ExecuteNonQuery();
                    transaction.Commit();
                }

                using (var transaction = connection.BeginTransaction())
                {
                    using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT INTO events VALUES (99);";
                    insert.ExecuteNonQuery();
                    transaction.Rollback();
                }

                using (var timeout = connection.CreateCommand())
                {
                    timeout.CommandText = "PRAGMA busy_timeout;";
                    timeout.CommandTimeout = 1;
                    timeout.ExecuteScalar().Should().Be(1000L);
                }
            }

            using var reopened = AhtolaConnection.CreateReplica(options);
            reopened.Open();
            using var reader = reopened.CreateCommand();
            reader.CommandText = "SELECT value FROM events;";
            using var rows = reader.ExecuteReader();
            rows.Read().Should().BeTrue();
            rows.GetInt64(0).Should().Be(41);
            rows.Read().Should().BeFalse();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void CreateReplicaBootstrapsRawPagesAndSendsThePullUpdatesRequest()
    {
        var path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"managed-embedded-replica-bootstrap-{Guid.NewGuid():N}.db");
        var sourcePath = path + ".source";
        byte[] databaseImage;
        try
        {
            CreateInitializedDatabase(sourcePath);
            databaseImage = File.ReadAllBytes(sourcePath);
        }

        finally
        {
            DeleteReplicaFiles(sourcePath);
        }

        var response = CreatePullResponse("revision-42", databaseImage);
        var handler = new PullUpdatesHandler(response, request =>
        {
            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri!.AbsolutePath.Should().Be("/cluster/pull-updates");
            request.Headers.Authorization.Should().Be(new AuthenticationHeaderValue("Bearer", "token-42"));
            request.Headers.TryGetValues("Accept-Encoding", out var acceptEncoding).Should().BeTrue();
            acceptEncoding.Should().ContainSingle().Which.Should().Be("application/protobuf");
            request.Content!.Headers.ContentType!.MediaType.Should().Be("application/protobuf");

            var fields = ReadVarintFields(request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
            fields.Should().ContainSingle();
            fields[4].Should().Be(3000);
        });
        var options = new AhtolaReplicaOptions(
            path,
            new Uri("https://example.test/cluster"),
            authToken: "token-42",
            bootstrapIfEmpty: true)
        {
            LongPollTimeout = TimeSpan.FromSeconds(3),
            HttpPolicy = new AhtolaSyncHttpPolicy(handler),
        };

        try
        {
            using var connection = AhtolaConnection.CreateReplica(options);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM bootstrap_marker;";
            command.ExecuteScalar().Should().Be(42L);
            handler.CallCount.Should().Be(1);
            File.Exists(path).Should().BeTrue();
            File.ReadAllText(path + ".ahtola-replica-meta").Should().Contain("server_revision_base64=cmV2aXNpb24tNDI=");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    // protocol=2 (MvccLogical) describes the incremental pulls that follow, not the bootstrap, which the server
    // still ships as a raw page stream. A fresh MVCC bootstrap must also catch up with one immediate,
    // non-long-poll logical pull before the connection opens (see CatchUpAfterFreshBootstrapAsync).
    [Test]
    public void CreateReplicaBootstrapsRawPagesFromALogicalProtocolRemote()
    {
        var path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"managed-embedded-replica-logical-bootstrap-{Guid.NewGuid():N}.db");
        var sourcePath = path + ".source";
        byte[] databaseImage;
        try
        {
            CreateInitializedDatabase(sourcePath);
            databaseImage = File.ReadAllBytes(sourcePath);
        }
        finally
        {
            DeleteReplicaFiles(sourcePath);
        }

        var handler = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", databaseImage, protocol: 2),
            CreateLogicalPullResponse("revision-42", body: []), // fresh-bootstrap catch-up: nothing new
        ]);
        var options = new AhtolaReplicaOptions(
            path,
            new Uri("https://example.test"),
            authToken: null,
            bootstrapIfEmpty: true)
        {
            HttpPolicy = new AhtolaSyncHttpPolicy(handler),
        };

        try
        {
            using var connection = AhtolaConnection.CreateReplica(options);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM bootstrap_marker;";
            command.ExecuteScalar().Should().Be(42L);
            handler.CallCount.Should().Be(2, "a fresh MVCC bootstrap must be followed by exactly one catch-up pull");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void FailedFreshBootstrapCatchUpRollsBackTheWholeBootstrapForACleanRetry()
    {
        var path = NewReplicaPath("managed-replica-catchup-failure-rollback");
        var sourcePath = path + ".source";
        byte[] databaseImage;
        try
        {
            CreateInitializedDatabase(sourcePath);
            databaseImage = File.ReadAllBytes(sourcePath);
        }
        finally
        {
            DeleteReplicaFiles(sourcePath);
        }

        // Only the bootstrap page response is queued: the catch-up pull's HTTP call has nothing
        // left to dequeue and fails, simulating a network/server failure during catch-up.
        var failingHandler = new PullUpdatesHandler([CreatePullResponse("revision-42", databaseImage, protocol: 2)]);
        var options = new AhtolaReplicaOptions(path, new Uri("https://example.test"), authToken: null, bootstrapIfEmpty: true)
        {
            HttpPolicy = new AhtolaSyncHttpPolicy(failingHandler),
        };

        try
        {
            Assert.Throws<InvalidOperationException>(() => AhtolaConnection.CreateReplica(options).Open());

            // The bootstrap alone is not a complete, safe-to-serve replica (it is missing the
            // logical catch-up that brings it current); the whole (database, metadata) pair must
            // be rolled back rather than left as a durably "bootstrapped but never caught up"
            // replica that a later Open() would never retry.
            File.Exists(path).Should().BeFalse();
            File.Exists(path + ".ahtola-replica-meta").Should().BeFalse();

            // A subsequent Open() (here, with a working handler) must retry a clean bootstrap +
            // catch-up rather than being blocked by leftover partial state.
            var workingHandler = new PullUpdatesHandler(
            [
                CreatePullResponse("revision-42", databaseImage, protocol: 2),
                CreateLogicalPullResponse("revision-42", body: []),
            ]);
            var retryOptions = new AhtolaReplicaOptions(path, new Uri("https://example.test"), authToken: null, bootstrapIfEmpty: true)
            {
                HttpPolicy = new AhtolaSyncHttpPolicy(workingHandler),
            };
            using var connection = AhtolaConnection.CreateReplica(retryOptions);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM bootstrap_marker;";
            command.ExecuteScalar().Should().Be(42L);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task ConcurrentFirstOpensForTheSameMissingPathDoNotRaceEachOthersBootstrap()
    {
        var path = NewReplicaPath("managed-replica-concurrent-first-open");
        var sourcePath = path + ".source";
        byte[] databaseImage;
        try
        {
            CreateInitializedDatabase(sourcePath);
            databaseImage = File.ReadAllBytes(sourcePath);
        }
        finally
        {
            DeleteReplicaFiles(sourcePath);
        }

        // Bootstrap + catch-up publication is exclusive per path, so only ONE of the concurrent
        // Open() calls actually performs it; the queue only ever needs to satisfy one attempt.
        var handler = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", databaseImage, protocol: 2),
            CreateLogicalPullResponse("revision-42", body: []),
        ]);
        var options = new AhtolaReplicaOptions(path, new Uri("https://example.test"), authToken: null, bootstrapIfEmpty: true)
        {
            HttpPolicy = new AhtolaSyncHttpPolicy(handler),
        };

        try
        {
            var openTasks = Enumerable.Range(0, 4)
                .Select(_ => Task.Run(() => AhtolaConnection.CreateReplica(options).Open()))
                .ToArray();
            await Task.WhenAll(openTasks);

            handler.CallCount.Should().Be(2, "concurrent first opens must serialize on one bootstrap+catch-up, not race duplicate downloads");
            using var verify = new AhtolaConnection($"Data Source={path};Local Provider=Managed");
            verify.Open();
            verify.ExecuteNonQuery("SELECT value FROM bootstrap_marker;").Should().Be(0);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task LogicalPullPreservesAPendingUnpushedLocalRowChangeOnADisjointTable()
    {
        var path = NewReplicaPath("managed-replica-logical-precollect-disjoint");
        var sourcePath = path + ".source";
        byte[] databaseImage;
        try
        {
            CreateInitializedDatabase(sourcePath);
            databaseImage = File.ReadAllBytes(sourcePath);
        }
        finally
        {
            DeleteReplicaFiles(sourcePath);
        }

        var (logicalBody, rangeMessage) = BuildSimpleLogicalPullBody(
            tableName: "remote_items",
            rowId: 2,
            columnValue: "remote",
            schemaSql: "CREATE TABLE remote_items(id INTEGER PRIMARY KEY, x TEXT)",
            salt: 900UL);

        var handler = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", databaseImage, protocol: 2),
            CreateLogicalPullResponse("revision-42", body: []),
            CreateLogicalPullResponse("revision-43", logicalBody, rangeMessages: [rangeMessage]),
        ]);
        var options = CreateOptions(path, handler);
        try
        {
            IReadOnlyList<ReplicaLocalChange> pendingChanges;
            ManagedReplicaBootstrapper.ManagedReplicaMetadata metadata;
            using (var connection = AhtolaConnection.CreateReplica(options))
            {
                connection.Open();

                // A genuine local write, through the real connection: captured by the update
                // hook into the change journal exactly like a real "header-255" application
                // write. The connection is closed before directly driving CheckForUpdatesAsync
                // below, since that call (like a real sync) requires exclusive file access.
                connection.ExecuteNonQuery("CREATE TABLE local_items(id INTEGER PRIMARY KEY, x TEXT);");
                connection.ExecuteNonQuery("INSERT INTO local_items(id, x) VALUES (1, 'local');");

                pendingChanges = ManagedReplicaChangeJournal.Open(path).ReadBatch(int.MaxValue).Changes;
                pendingChanges.Should().NotBeEmpty("the local writes above must have been captured as pending, unpushed changes");
                metadata = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            }

            var result = await ManagedReplicaBootstrapper.CheckForUpdatesAsync(
                options, metadata, new AhtolaSyncOptions(), pendingChanges, CancellationToken.None);

            result.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);

            using var reopened = AhtolaConnection.CreateReplica(options);
            reopened.Open();
            using var remoteCommand = reopened.CreateCommand();
            remoteCommand.CommandText = "SELECT x FROM remote_items WHERE id = 2;";
            remoteCommand.ExecuteScalar().Should().Be("remote");

            using var localCommand = reopened.CreateCommand();
            localCommand.CommandText = "SELECT x FROM local_items WHERE id = 1;";
            localCommand.ExecuteScalar().Should().Be("local", "the pending local write must survive the concurrent remote pull");

            // The reconciled local change is still unpushed (the pull never acknowledges it).
            ManagedReplicaChangeJournal.Open(path).ReadBatch(int.MaxValue).Changes.Should().NotBeEmpty();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task LogicalPullOverwritesAPendingLocalRowThatRemoteAlsoChangedWithTheLocalValue()
    {
        // A "remote conflict": both the pending local change and the incoming remote transaction
        // touch the SAME row. The local pending write must win, since it is reapplied AFTER the
        // remote apply (matching Turso's ordering-based "last write wins" semantics).
        var path = NewReplicaPath("managed-replica-logical-precollect-conflict");
        var sourcePath = path + ".source";
        byte[] databaseImage;
        try
        {
            CreateInitializedDatabase(sourcePath);
            databaseImage = File.ReadAllBytes(sourcePath);
        }
        finally
        {
            DeleteReplicaFiles(sourcePath);
        }

        var (logicalBody, rangeMessage) = BuildSimpleLogicalPullBody(
            tableName: "shared",
            rowId: 1,
            columnValue: "remote-value",
            schemaSql: "CREATE TABLE shared(id INTEGER PRIMARY KEY, x TEXT)",
            salt: 901UL);

        var handler = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", databaseImage, protocol: 2),
            CreateLogicalPullResponse("revision-42", body: []),
            CreateLogicalPullResponse("revision-43", logicalBody, rangeMessages: [rangeMessage]),
        ]);
        var options = CreateOptions(path, handler);
        try
        {
            IReadOnlyList<ReplicaLocalChange> pendingChanges;
            ManagedReplicaBootstrapper.ManagedReplicaMetadata metadata;
            using (var connection = AhtolaConnection.CreateReplica(options))
            {
                connection.Open();

                connection.ExecuteNonQuery("CREATE TABLE shared(id INTEGER PRIMARY KEY, x TEXT);");
                connection.ExecuteNonQuery("INSERT INTO shared(id, x) VALUES (1, 'local-value');");

                pendingChanges = ManagedReplicaChangeJournal.Open(path).ReadBatch(int.MaxValue).Changes;
                metadata = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            }

            await ManagedReplicaBootstrapper.CheckForUpdatesAsync(
                options, metadata, new AhtolaSyncOptions(), pendingChanges, CancellationToken.None);

            using var reopened = AhtolaConnection.CreateReplica(options);
            reopened.Open();
            using var command = reopened.CreateCommand();
            command.CommandText = "SELECT x FROM shared WHERE id = 1;";
            command.ExecuteScalar().Should().Be("local-value");
            RowCountViaConnection(reopened, "shared").Should().Be(1);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task SyncPushesOneBatchThenReconcilesRemainingPendingChangesAcrossThePull()
    {
        // More local changes than PushOperationsThreshold: the push only sends the first batch,
        // leaving the rest pending in the change journal, which the pull must still reconcile
        // rather than silently lose.
        var path = NewReplicaPath("managed-replica-logical-batch-reconcile");
        var sourcePath = path + ".source";
        byte[] databaseImage;
        try
        {
            // local_items is baked into the bootstrap image itself (not created via the open
            // connection), so only the two row inserts below become pending journal entries.
            using (var source = new AhtolaConnection($"Data Source={sourcePath};Local Provider=Managed"))
            {
                source.Open();
                source.ExecuteNonQuery("CREATE TABLE bootstrap_marker(value INTEGER NOT NULL);");
                source.ExecuteNonQuery("INSERT INTO bootstrap_marker VALUES (42);");
                source.ExecuteNonQuery("CREATE TABLE local_items(id INTEGER PRIMARY KEY, x TEXT);");
            }

            databaseImage = File.ReadAllBytes(sourcePath);
        }
        finally
        {
            DeleteReplicaFiles(sourcePath);
        }

        var (logicalBody, rangeMessage) = BuildSimpleLogicalPullBody(
            tableName: "remote_items",
            rowId: 2,
            columnValue: "remote",
            schemaSql: "CREATE TABLE remote_items(id INTEGER PRIMARY KEY, x TEXT)",
            salt: 902UL);

        var handler = new ReplicaPushHandler(
        [
            CreatePullResponse("revision-42", databaseImage, protocol: 2),
            CreateLogicalPullResponse("revision-42", body: []),
            CreateLogicalPullResponse("revision-43", logicalBody, rangeMessages: [rangeMessage]),
        ],
        _ => ReplicaPushHandler.SuccessfulBatchResponse(5));
        var options = CreateOptions(path, handler, pushOperationsThreshold: 1);

        try
        {
            using var connection = AhtolaConnection.CreateReplica(options);
            connection.Open();

            connection.ExecuteNonQuery("INSERT INTO local_items(id, x) VALUES (1, 'first');"); // pushed
            connection.ExecuteNonQuery("INSERT INTO local_items(id, x) VALUES (2, 'second');"); // left pending

            var result = await connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);

            result.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);
            handler.PushCallCount.Should().Be(1, "only one push batch (capped at threshold 1) must run per sync");

            using var remoteCommand = connection.CreateCommand();
            remoteCommand.CommandText = "SELECT x FROM remote_items WHERE id = 2;";
            remoteCommand.ExecuteScalar().Should().Be("remote");

            using var localCommand = connection.CreateCommand();
            localCommand.CommandText = "SELECT x FROM local_items ORDER BY id;";
            using var reader = localCommand.ExecuteReader();
            var values = new List<string>();
            while (reader.Read())
                values.Add(reader.GetString(0));
            values.Should().Equal("first", "second");

            // The pushed change (id=1) is acknowledged; the unpushed one (id=2) is still pending.
            var remainingPending = ManagedReplicaChangeJournal.Open(path).ReadBatch(int.MaxValue).Changes;
            remainingPending.Should().ContainSingle();
            remainingPending[0].RowId.Should().Be(2);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task PendingLocalSchemaChangeRejectsALogicalPullUntilItIsPushedThenMakesProgress()
    {
        // More local changes than PushOperationsThreshold (1), the last of which is a SCHEMA
        // (DDL) change rather than a row change: the first sync's push batch only drains the row
        // change, leaving the schema change pending in the journal when the pull runs. A remote
        // logical response with actual transactions to apply must reject outright rather than
        // silently proceed (a remote Refresh/Drop is generated against a schema state that does
        // not yet reflect the unpushed local DDL). Once a SECOND sync finally pushes the
        // remaining schema change, the SAME logical response (still describing the same old
        // revision, since the first attempt's rejection happened before any metadata was
        // touched) must apply normally: eventual progress, not a permanent stall.
        var path = NewReplicaPath("managed-replica-logical-pending-schema-reject");
        var sourcePath = path + ".source";
        byte[] databaseImage;
        try
        {
            using (var source = new AhtolaConnection($"Data Source={sourcePath};Local Provider=Managed"))
            {
                source.Open();
                source.ExecuteNonQuery("CREATE TABLE bootstrap_marker(value INTEGER NOT NULL);");
                source.ExecuteNonQuery("INSERT INTO bootstrap_marker VALUES (42);");
                source.ExecuteNonQuery("CREATE TABLE local_items(id INTEGER PRIMARY KEY, x TEXT);");
            }

            databaseImage = File.ReadAllBytes(sourcePath);
        }
        finally
        {
            DeleteReplicaFiles(sourcePath);
        }

        var (logicalBody, rangeMessage) = BuildSimpleLogicalPullBody(
            tableName: "remote_items",
            rowId: 2,
            columnValue: "remote",
            schemaSql: "CREATE TABLE remote_items(id INTEGER PRIMARY KEY, x TEXT)",
            salt: 903UL);

        // The SAME logical response is queued twice: the first sync's pull must be rejected
        // before it consumes/publishes anything, so the old revision is still current when the
        // second sync's pull runs and must succeed against an identical response.
        var handler = new ReplicaPushHandler(
        [
            CreatePullResponse("revision-42", databaseImage, protocol: 2),
            CreateLogicalPullResponse("revision-42", body: []),
            CreateLogicalPullResponse("revision-43", logicalBody, rangeMessages: [rangeMessage]),
            CreateLogicalPullResponse("revision-43", logicalBody, rangeMessages: [rangeMessage]),
        ],
        _ => ReplicaPushHandler.SuccessfulBatchResponse(5));
        var options = CreateOptions(path, handler, pushOperationsThreshold: 1);

        try
        {
            using var connection = AhtolaConnection.CreateReplica(options);
            connection.Open();

            connection.ExecuteNonQuery("INSERT INTO local_items(id, x) VALUES (1, 'first');"); // pushed first
            connection.ExecuteNonQuery("ALTER TABLE local_items ADD COLUMN extra TEXT;"); // left pending

            var beforeMetadata = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;

            Func<Task> firstSync = () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
            await firstSync.Should().ThrowAsync<NotSupportedException>()
                .WithMessage("*schema change*pending push*");

            handler.PushCallCount.Should().Be(1, "the row change was pushed; the schema change was left pending");
            handler.PullCallCount.Should().Be(3, "bootstrap catch-up + the rejected explicit sync's pull");

            // The rejected logical apply never reaches WriteMetadataAsync, so the revision
            // recorded from the successful push-side of this same sync (RecordLocalPushAsync,
            // which legitimately refreshes the fingerprint against the file as already mutated by
            // the two local writes above, independent of the pull's own outcome) still carries the
            // OLD, pre-pull revision rather than the rejected response's "revision-43".
            var afterFailedSyncMetadata = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            afterFailedSyncMetadata.Revision.Should().Be(beforeMetadata.Revision);

            // The still-pending schema change remains in the journal untouched by the rejection.
            var pendingAfterFailure = ManagedReplicaChangeJournal.Open(path).ReadBatch(int.MaxValue).Changes;
            pendingAfterFailure.Should().ContainSingle(c => c.Kind == ReplicaLocalChangeKind.Schema);

            // Second sync: the schema change is now the only (and first) journal entry, so the
            // threshold-1 push finally drains it. With no residual pending schema entries, the
            // pull proceeds and applies the remote transaction normally.
            var secondResult = await connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
            secondResult.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);
            handler.PushCallCount.Should().Be(2, "the second sync finally pushes the pending schema change");

            using var remoteCommand = connection.CreateCommand();
            remoteCommand.CommandText = "SELECT x FROM remote_items WHERE id = 2;";
            remoteCommand.ExecuteScalar().Should().Be("remote");

            using var localCommand = connection.CreateCommand();
            localCommand.CommandText = "SELECT extra FROM local_items WHERE id = 1;";
            localCommand.ExecuteScalar().Should().Be(DBNull.Value);

            var finalMetadata = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            finalMetadata.Revision.Should().Be("revision-43");

            var pendingAfterProgress = ManagedReplicaChangeJournal.Open(path).ReadBatch(int.MaxValue).Changes;
            pendingAfterProgress.Should().BeEmpty();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void LogicalDivergenceGuardToleratesAnEvolvingWalUnlikeThePageProtocol()
    {
        var path = NewReplicaPath("managed-replica-logical-divergence-tolerance");
        try
        {
            CreateInitializedDatabase(path);
            // Simulate legitimate, in-flight local WAL content (well beyond an empty 32-byte
            // header), which the page-protocol guard would reject outright.
            File.WriteAllBytes(path + "-wal", new byte[128]);

            var logicalMetadata = new ManagedReplicaBootstrapper.ManagedReplicaMetadata(
                "revision-1", "not-a-real-fingerprint", "client-x", RemotePullProtocol.MvccLogical,
                new Dictionary<ulong, string>());
            Action logicalAct = () => ManagedReplicaBootstrapper.EnsureNoLocalDivergence(path, logicalMetadata);
            logicalAct.Should().NotThrow("the MVCC logical protocol reconciles local writes across a pull instead of rejecting them");

            var pageMetadata = logicalMetadata with { Protocol = RemotePullProtocol.Pages };
            Action pageAct = () => ManagedReplicaBootstrapper.EnsureNoLocalDivergence(path, pageMetadata);
            pageAct.Should().Throw<NotSupportedException>("the page protocol has no mechanism to reconcile local writes across a pull");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    private static long RowCountViaConnection(AhtolaConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{table}\";";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    [Test]
    public async Task ZeroTransactionRevisionAdvancePublishesMetadataSoTheNextSyncIsUpToDate()
    {
        // A logical response can advance the revision while decoding to zero transactions (e.g.
        // every wire transaction was excluded as this client's own echo, or the body was
        // genuinely empty). Metadata must still publish the new revision, or the next sync would
        // resend the identical range forever instead of ever reaching UpToDate.
        var path = NewReplicaPath("managed-replica-logical-zero-tx-revision");
        var sourcePath = path + ".source";
        byte[] databaseImage;
        try
        {
            CreateInitializedDatabase(sourcePath);
            databaseImage = File.ReadAllBytes(sourcePath);
        }
        finally
        {
            DeleteReplicaFiles(sourcePath);
        }

        var handler = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", databaseImage, protocol: 2),
            CreateLogicalPullResponse("revision-42", body: []), // fresh-bootstrap catch-up
            CreateLogicalPullResponse("revision-43", body: []), // new revision, nothing decoded
            CreateLogicalPullResponse("revision-43", body: []), // second sync: same revision
        ]);
        var options = CreateOptions(path, handler);

        try
        {
            using var connection = AhtolaConnection.CreateReplica(options);
            connection.Open();

            var firstResult = await connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
            firstResult.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);
            firstResult.Statistics.Revision.Should().Be("revision-43");
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.Revision.Should().Be(
                "revision-43", "metadata must publish the new revision even though nothing was decoded to replay");

            var secondResult = await connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
            secondResult.Outcome.Should().Be(
                AhtolaSyncOutcome.UpToDate, "metadata must have advanced so the identical revision now round-trips as up to date");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void SyncAgainstALogicalProtocolRemoteAppliesLogicalChanges()
    {
        var path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"managed-embedded-replica-logical-sync-{Guid.NewGuid():N}.db");
        var sourcePath = path + ".source";
        byte[] databaseImage;
        try
        {
            CreateInitializedDatabase(sourcePath);
            databaseImage = File.ReadAllBytes(sourcePath);
        }
        finally
        {
            DeleteReplicaFiles(sourcePath);
        }

        var salt = 777UL;
        var logHeader = Lml3TestBuilder.BuildHeader(salt);
        var crc = Lml3TestBuilder.HeaderSeedCrc(salt);
        var schemaRecord = Lml3TestBuilder.SchemaRecord("table", "widgets", 5, "CREATE TABLE widgets(id INTEGER PRIMARY KEY, name TEXT)");
        var schemaOp = Lml3TestBuilder.BuildRecoveryOp(0, 0, -1, Lml3TestBuilder.UpsertTablePayload(1, schemaRecord));
        var rowRecord = Core.Storage.SqliteRecordCodec.Encode([Core.SqlValue.Null, Core.SqlValue.Text("alice")]);
        var rowOp = Lml3TestBuilder.BuildRecoveryOp(0, 0, -2, Lml3TestBuilder.UpsertTablePayload(9, rowRecord));
        var recoveryPayload = schemaOp.Concat(rowOp).ToArray();
        var portableTxn = Lml3TestBuilder.BuildPortableLogicalTxn(1, 1, ["widgets"], [(-2, 0)]);
        var extRecord = Lml3TestBuilder.BuildExtensionRecord(Lml3TestBuilder.PortableChangesExtensionType, Lml3TestBuilder.Delimited(portableTxn));
        var frame = Lml3TestBuilder.BuildFrame(ref crc, recoveryPayload, opCount: 2, extensionBlock: extRecord);
        var logicalBody = logHeader.Concat(frame).ToArray();
        var rangeMessage = BuildLogicalLogRangeMessage(1, 0, (ulong)logicalBody.Length, startsWithHeader: true);

        // Responses in order: (1) protocol-2 page bootstrap, (2) fresh-bootstrap catch-up (nothing
        // new yet), (3) the explicit Sync() call's logical pull with real changes.
        var handler = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", databaseImage, protocol: 2),
            CreateLogicalPullResponse("revision-42", body: []),
            CreateLogicalPullResponse("revision-43", logicalBody, rangeMessages: [rangeMessage]),
        ]);
        var options = new AhtolaReplicaOptions(
            path,
            new Uri("https://example.test"),
            authToken: null,
            bootstrapIfEmpty: true)
        {
            HttpPolicy = new AhtolaSyncHttpPolicy(handler),
        };

        try
        {
            using var connection = AhtolaConnection.CreateReplica(options);
            connection.Open();

            var result = connection.Sync(new AhtolaSyncOptions());

            result.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);
            result.Statistics.Revision.Should().Be("revision-43");
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM widgets WHERE id = 9;";
            command.ExecuteScalar().Should().Be("alice");
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.Revision.Should().Be("revision-43");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task LogicalSyncRequestBytesIncludeClientRevisionAndLogicalStreamKind()
    {
        var path = NewReplicaPath("managed-replica-logical-request-fields");
        var image = CreateDatabaseImage(path + ".source");
        var capturedRequests = new List<Dictionary<int, (ulong? Number, string? Text)>>();
        var handler = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", image, protocol: 2),
            CreateLogicalPullResponse("revision-42", body: []),
            CreateLogicalPullResponse("revision-42", body: []),
        ],
        request =>
        {
            var bytes = request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            if (bytes.Length > 0)
                capturedRequests.Add(ReadFields(bytes));
        });
        var options = new AhtolaReplicaOptions(path, new Uri("https://example.test"), authToken: null)
        {
            HttpPolicy = new AhtolaSyncHttpPolicy(handler),
        };

        try
        {
            using var connection = AhtolaConnection.CreateReplica(options);
            connection.Open(); // bootstrap (no revision, empty request) + fresh-bootstrap catch-up
            await connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None).ConfigureAwait(false);

            // The fresh-bootstrap catch-up and the explicit Sync() both know the remote is
            // MvccLogical: both must carry client_revision (tag 3) and request the logical
            // stream_kind (tag 8 = 1), proving the CreatePullRequest fix applies to every
            // logical-capable pull, not just ones with a configured long-poll timeout.
            capturedRequests.Should().HaveCount(2);
            foreach (var fields in capturedRequests)
            {
                fields[3].Text.Should().Be("revision-42");
                fields[8].Number.Should().Be(1);
            }
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task MalformedLogicalResponseRollsBackAndRetainsThePreviousRevisionThenRetrySucceeds()
    {
        var path = NewReplicaPath("managed-replica-logical-malformed-rollback");
        var image = CreateDatabaseImage(path + ".source");

        var salt = 999UL;
        var goodHeader = Lml3TestBuilder.BuildHeader(salt);
        var goodCrc = Lml3TestBuilder.HeaderSeedCrc(salt);
        var record = Core.Storage.SqliteRecordCodec.Encode([Core.SqlValue.Text("hello")]);
        var op = Lml3TestBuilder.BuildRecoveryOp(0, 0, -1, Lml3TestBuilder.UpsertTablePayload(1, record));
        var schemaRecord = Lml3TestBuilder.SchemaRecord("table", "widgets", 5, "CREATE TABLE widgets(id INTEGER PRIMARY KEY, name TEXT)");
        var schemaOp = Lml3TestBuilder.BuildRecoveryOp(0, 0, -1, Lml3TestBuilder.UpsertTablePayload(1, schemaRecord));
        var rowRecord = Core.Storage.SqliteRecordCodec.Encode([Core.SqlValue.Null, Core.SqlValue.Text("bob")]);
        var rowOp = Lml3TestBuilder.BuildRecoveryOp(0, 0, -2, Lml3TestBuilder.UpsertTablePayload(3, rowRecord));
        var recoveryPayload = schemaOp.Concat(rowOp).ToArray();
        var portableTxn = Lml3TestBuilder.BuildPortableLogicalTxn(1, 1, ["widgets"], [(-2, 0)]);
        var extRecord = Lml3TestBuilder.BuildExtensionRecord(Lml3TestBuilder.PortableChangesExtensionType, Lml3TestBuilder.Delimited(portableTxn));
        var goodFrame = Lml3TestBuilder.BuildFrame(ref goodCrc, recoveryPayload, opCount: 2, extensionBlock: extRecord);
        var goodBody = goodHeader.Concat(goodFrame).ToArray();
        var goodRange = BuildLogicalLogRangeMessage(1, 0, (ulong)goodBody.Length, startsWithHeader: true);

        // A corrupted body: flip a byte inside the frame's CRC so the decoder rejects it.
        var corruptBody = (byte[])goodBody.Clone();
        corruptBody[^5] ^= 0xFF;

        var handler = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", image, protocol: 2),
            CreateLogicalPullResponse("revision-42", body: []), // fresh-bootstrap catch-up
            CreateLogicalPullResponse("revision-43", corruptBody, rangeMessages: [goodRange]), // malformed
            CreateLogicalPullResponse("revision-43", goodBody, rangeMessages: [goodRange]), // retry, valid
        ]);
        var options = new AhtolaReplicaOptions(path, new Uri("https://example.test"), authToken: null)
        {
            HttpPolicy = new AhtolaSyncHttpPolicy(handler),
        };

        try
        {
            using var connection = AhtolaConnection.CreateReplica(options);
            connection.Open();

            Func<Task> firstSync = () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
            await firstSync.Should().ThrowAsync<InvalidDataException>();

            // Failure must not advance the durable revision or table map, and must not leave a
            // partially-applied schema/row change behind.
            var afterFailure = ManagedReplicaBootstrapper.LoadMetadata(path)!.Value;
            afterFailure.Revision.Should().Be("revision-42");
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'widgets';";
                command.ExecuteScalar().Should().Be(0L, "the failed apply must not have left a partially-created table");
            }

            var retryResult = await connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
            retryResult.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);
            retryResult.Statistics.Revision.Should().Be("revision-43");
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT name FROM widgets WHERE id = 3;";
                command.ExecuteScalar().Should().Be("bob");
            }

            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.Revision.Should().Be("revision-43");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task ProtocolTwoPagesReplaceBaseWithPendingLocalChangesIsRejected()
    {
        // A protocol-2 (logical) replica whose response for THIS pull surprisingly turns out to
        // be Pages+ReplaceBase (rather than a logical stream) has no way to reconcile still-
        // pending local writes the way the logical path's precollect/reapply does; it must
        // reject rather than silently clobber/mix local data with the incoming page replacement.
        var path = NewReplicaPath("managed-replica-replace-base-pending-local");
        var initialImage = CreateDatabaseImageWithMarker(path + ".initial", 42);
        var replacedImage = CreateDatabaseImageWithMarker(path + ".replaced", 84);

        var handler = new ReplicaPushHandler(
        [
            CreatePullResponse("revision-42", initialImage, protocol: 2),
            CreateLogicalPullResponse("revision-42", body: []), // fresh-bootstrap catch-up
            CreateReplaceBasePullResponse("revision-43", replacedImage),
        ],
        _ => ReplicaPushHandler.SuccessfulBatchResponse(5));
        var options = CreateOptions(path, handler, pushOperationsThreshold: 1);

        try
        {
            using var connection = AhtolaConnection.CreateReplica(options);
            connection.Open();
            ReadBootstrapMarker(connection).Should().Be(42);

            // Two local writes with a push threshold of 1: only the first is pushed, leaving the
            // second genuinely pending when the sync's pull runs.
            connection.ExecuteNonQuery("CREATE TABLE local_items(id INTEGER PRIMARY KEY, x TEXT);");
            connection.ExecuteNonQuery("INSERT INTO local_items(id, x) VALUES (1, 'first');");
            connection.ExecuteNonQuery("INSERT INTO local_items(id, x) VALUES (2, 'second');");

            Assert.ThrowsAsync<NotSupportedException>(
                () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));

            // The rejected page apply must not have touched local data at all.
            ReadBootstrapMarker(connection).Should().Be(42);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT x FROM local_items ORDER BY id;";
            using var reader = command.ExecuteReader();
            var values = new List<string>();
            while (reader.Read())
                values.Add(reader.GetString(0));
            values.Should().Equal("first", "second");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task ProtocolTwoPagesReplaceBaseAppliesAFullAtomicReplacementAndRetainsLogicalProtocol()
    {
        var path = NewReplicaPath("managed-replica-logical-replace-base");
        var initialImage = CreateDatabaseImageWithMarker(path + ".initial", 42);
        var replacedImage = CreateDatabaseImageWithMarker(path + ".replaced", 84);

        var capturedThirdRequest = new List<Dictionary<int, (ulong? Number, string? Text)>>();
        var handler = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", initialImage, protocol: 2),
            CreateLogicalPullResponse("revision-42", body: []), // fresh-bootstrap catch-up
            CreateReplaceBasePullResponse("revision-43", replacedImage),
            CreateLogicalPullResponse("revision-43", body: []), // proves the next pull still requests logical
        ],
        request =>
        {
            var bytes = request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            if (bytes.Length > 0)
                capturedThirdRequest.Add(ReadFields(bytes));
        });
        var options = new AhtolaReplicaOptions(path, new Uri("https://example.test"), authToken: null)
        {
            HttpPolicy = new AhtolaSyncHttpPolicy(handler),
        };

        try
        {
            using var connection = AhtolaConnection.CreateReplica(options);
            connection.Open();
            ReadBootstrapMarker(connection).Should().Be(42);

            var result = await connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
            result.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);
            ReadBootstrapMarker(connection).Should().Be(84);

            // A second sync must still request the logical protocol: Pages+ReplaceBase for one
            // response does not downgrade the persisted remote capability.
            await connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
            capturedThirdRequest.Should().Contain(fields => fields.ContainsKey(8) && fields[8].Number == 1);
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.Protocol.Should().Be(RemotePullProtocol.MvccLogical);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task LogicalSyncCancellationLeavesThePreviousRevisionInPlace()
    {
        var path = NewReplicaPath("managed-replica-logical-cancel");
        var image = CreateDatabaseImage(path + ".source");
        var handler = new BlockingLogicalSyncHandler(
            CreatePullResponse("revision-42", image, protocol: 2),
            CreateLogicalPullResponse("revision-42", body: [])); // fresh-bootstrap catch-up, non-blocking
        using var cancellation = new CancellationTokenSource();

        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();

            var sync = connection.SyncAsync(new AhtolaSyncOptions(), cancellation.Token);
            await handler.SyncStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.CatchAsync<OperationCanceledException>(() => sync);

            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.Revision.Should().Be("revision-42");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [TestCase(LogicalApplyCommittedBoundary, false)]
    [TestCase(LogicalApplyCheckpointedBoundary, false)]
    [TestCase(LogicalApplyMetadataPublishedBoundary, true)]
    public async Task LogicalApplyCancellationRecoversAMatchedDatabaseAndMetadataPair(
        int boundaryValue,
        bool expectedRemoteChanges)
    {
        var boundary = (ManagedReplicaDurableBoundary)boundaryValue;
        var path = NewReplicaPath($"managed-replica-logical-apply-cancel-{boundary}");
        var sourcePath = path + ".source";
        byte[] databaseImage;
        try
        {
            CreateInitializedDatabase(sourcePath);
            databaseImage = File.ReadAllBytes(sourcePath);
        }
        finally
        {
            DeleteReplicaFiles(sourcePath);
        }

        var (logicalBody, rangeMessage) = BuildSimpleLogicalPullBody(
            tableName: "widgets",
            rowId: 9,
            columnValue: "alice",
            schemaSql: "CREATE TABLE widgets(id INTEGER PRIMARY KEY, name TEXT)");

        var handler = new PullUpdatesHandler(
        [
            CreatePullResponse("revision-42", databaseImage, protocol: 2),
            CreateLogicalPullResponse("revision-42", body: []), // fresh-bootstrap catch-up: nothing new
            CreateLogicalPullResponse("revision-43", logicalBody, rangeMessages: [rangeMessage]),
        ]);
        var options = CreateOptions(path, handler);
        using var cancellation = new CancellationTokenSource();
        try
        {
            using (var connection = AhtolaConnection.CreateReplica(options))
            {
                connection.Open();
                using (ManagedReplicaFaultInjection.Push(point =>
                       {
                           if (point == boundary)
                               cancellation.Cancel();
                       }))
                {
                    Assert.CatchAsync<OperationCanceledException>(
                        () => connection.SyncAsync(new AhtolaSyncOptions(), cancellation.Token));
                }
            }

            var metadata = ManagedReplicaBootstrapper.LoadMetadata(path);
            metadata.Should().NotBeNull();
            metadata!.Value.Revision.Should().Be(expectedRemoteChanges ? "revision-43" : "revision-42");

            // The database file and metadata must be a MATCHED pair either way: the metadata's
            // recorded fingerprint must describe the file that is actually on disk.
            var actualFingerprint = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            metadata.Value.DatabaseSha256.Should().Be(actualFingerprint);

            using var reopened = AhtolaConnection.CreateReplica(options);
            reopened.Open();
            using var existsCommand = reopened.CreateCommand();
            existsCommand.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'widgets';";
            var tableExists = Convert.ToInt64(existsCommand.ExecuteScalar()) > 0;
            tableExists.Should().Be(expectedRemoteChanges);
            if (tableExists)
            {
                using var rowCommand = reopened.CreateCommand();
                rowCommand.CommandText = "SELECT COUNT(*) FROM widgets WHERE id = 9;";
                rowCommand.ExecuteScalar().Should().Be(1L);
            }
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    /// <summary>
    /// Builds a minimal logical pull body containing one schema Create and one row UpsertRow,
    /// plus its matching lml3 range message, for tests that only need "some real remote change
    /// happened" without caring about its exact shape.
    /// </summary>
    private static (byte[] Body, byte[] RangeMessage) BuildSimpleLogicalPullBody(
        string tableName,
        long rowId,
        string columnValue,
        string schemaSql,
        ulong salt = 12345UL)
    {
        var logHeader = Lml3TestBuilder.BuildHeader(salt);
        var crc = Lml3TestBuilder.HeaderSeedCrc(salt);
        var schemaRecord = Lml3TestBuilder.SchemaRecord("table", tableName, 5, schemaSql);
        var schemaOp = Lml3TestBuilder.BuildRecoveryOp(0, 0, -1, Lml3TestBuilder.UpsertTablePayload(1, schemaRecord));
        var rowRecord = Core.Storage.SqliteRecordCodec.Encode([Core.SqlValue.Null, Core.SqlValue.Text(columnValue)]);
        var rowOp = Lml3TestBuilder.BuildRecoveryOp(0, 0, -2, Lml3TestBuilder.UpsertTablePayload(rowId, rowRecord));
        var recoveryPayload = schemaOp.Concat(rowOp).ToArray();
        var portableTxn = Lml3TestBuilder.BuildPortableLogicalTxn(1, 1, [tableName], [(-2, 0)]);
        var extRecord = Lml3TestBuilder.BuildExtensionRecord(Lml3TestBuilder.PortableChangesExtensionType, Lml3TestBuilder.Delimited(portableTxn));
        var frame = Lml3TestBuilder.BuildFrame(ref crc, recoveryPayload, opCount: 2, extensionBlock: extRecord);
        var logicalBody = logHeader.Concat(frame).ToArray();
        var rangeMessage = BuildLogicalLogRangeMessage(1, 0, (ulong)logicalBody.Length, startsWithHeader: true);
        return (logicalBody, rangeMessage);
    }

    /// <summary>
    /// Like <see cref="BlockingPullUpdatesHandler"/> but accounts for the extra, non-blocking
    /// fresh-bootstrap logical catch-up call: only the third call (the explicit <c>Sync()</c>)
    /// blocks until released.
    /// </summary>
    private sealed class BlockingLogicalSyncHandler(byte[] bootstrapResponse, byte[] catchUpResponse) : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public TaskCompletionSource<bool> SyncStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _callCount);
            byte[] payload;
            if (call == 1)
            {
                payload = bootstrapResponse;
            }
            else if (call == 2)
            {
                payload = catchUpResponse;
            }
            else
            {
                SyncStarted.TrySetResult(true);
                await _release.Task.WaitAsync(cancellationToken);
                payload = catchUpResponse;
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/protobuf");
            return response;
        }
    }

    private static byte[] CreateReplaceBasePullResponse(string revision, byte[] databaseImage, ulong protocol = 2)
        // CreatePullResponse already hard-codes apply_mode (tag 6) to 1 (ReplaceBase), matching
        // every other page-stream fixture in this file; this wrapper just names that intent for
        // the ReplaceBase-specific test below.
        => CreatePullResponse(revision, databaseImage, protocol: protocol);

    [Test]
    public async Task ManagedReplicaAcceptsRawLegacyAndV1PageStreams()
    {
        var path = NewReplicaPath("managed-replica-raw-legacy-v1");
        var initialImage = CreateDatabaseImageWithMarker(path + ".initial", 42);
        var updatedImage = CreateDatabaseImageWithMarker(path + ".updated", 84);
        var handler = new PullUpdatesHandler([
            CreatePullResponse("legacy-revision", initialImage, protocol: null),
            CreatePullResponse("v1-revision", updatedImage, protocol: 1),
        ]);

        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            ReadBootstrapMarker(connection).Should().Be(42);

            var result = await connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);

            result.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);
            ReadBootstrapMarker(connection).Should().Be(84);
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.Revision.Should().Be("v1-revision");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }


    [Test]
    public async Task ManagedReplicaJournalDoesNotCaptureRemotePageApply()
    {
        var path = NewReplicaPath("managed-replica-journal-remote-apply");
        var image = CreateDatabaseImage(path + ".source");
        var handler = new PullUpdatesHandler([
            CreatePullResponse("revision-42", image),
            CreatePullResponse("revision-43", image),
        ]);
        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();

            _ = await connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);

            connection.ReadManagedReplicaLocalChanges(10).Changes.Should().BeEmpty();
            File.Exists(path + ManagedReplicaChangeJournal.Suffix).Should().BeFalse();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task SyncAsyncPushesJournalInGuardedHranaTransactionAndDurablyAcknowledgesIt()
    {
        var path = NewReplicaPath("managed-replica-push");
        var image = CreateJournalDatabaseImage(path + ".source");
        JsonDocument? push = null;
        var handler = new ReplicaPushHandler(
            [
                CreatePullResponse("revision-42", image),
                CreatePullResponse("revision-42", [], declaredPages: 1),
            ],
            request =>
            {
                request.Method.Should().Be(HttpMethod.Post);
                request.RequestUri!.AbsolutePath.Should().Be("/cluster/v2/pipeline");
                request.Headers.Authorization.Should().Be(new AuthenticationHeaderValue("Bearer", "token-42"));
                push = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return ReplicaPushHandler.SuccessfulBatchResponse(5);
            });
        try
        {
            var options = CreateOptions(path, handler);
            using (var connection = AhtolaConnection.CreateReplica(options))
            {
                connection.Open();
                using var batch = (AhtolaBatch)connection.CreateBatch();
                batch.BatchCommands.Add(new AhtolaBatchCommand("INSERT INTO journal_events VALUES (10);"));
                batch.ExecuteNonQuery().Should().Be(1);

                var result = await connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
                result.Statistics.CdcOperations.Should().Be(1);
                result.Statistics.LastPush.Should().NotBeNull();
                connection.ReadManagedReplicaLocalChanges(10).Changes.Should().BeEmpty();
            }

            using (var reopened = AhtolaConnection.CreateReplica(options))
            {
                reopened.Open();
                reopened.ReadManagedReplicaLocalChanges(10).Changes.Should().BeEmpty();
                reopened.ExecuteNonQuery("INSERT INTO journal_events VALUES (20);");
                reopened.ReadManagedReplicaLocalChanges(10).FirstSequence.Should().Be(2);
            }

            push.Should().NotBeNull();
            var steps = push!.RootElement.GetProperty("requests")[0].GetProperty("batch").GetProperty("steps");
            steps.GetArrayLength().Should().Be(5);
            steps[0].GetProperty("stmt").GetProperty("sql").GetString().Should().Be("BEGIN IMMEDIATE");
            steps[1].GetProperty("stmt").GetProperty("sql").GetString().Should()
                .Be("CREATE TABLE IF NOT EXISTS turso_sync_last_change_id (client_id TEXT PRIMARY KEY, pull_gen INTEGER, change_id INTEGER)");
            steps[2].GetProperty("stmt").GetProperty("sql").GetString().Should().Be("INSERT INTO journal_events VALUES (10);");
            steps[3].GetProperty("stmt").GetProperty("sql").GetString().Should()
                .StartWith("INSERT INTO turso_sync_last_change_id");
            steps[4].GetProperty("stmt").GetProperty("sql").GetString().Should().Be("COMMIT");
            foreach (var index in new[] { 1, 2, 3 })
            {
                steps[index].GetProperty("condition").GetProperty("type").GetString().Should().Be("not");
                steps[index].GetProperty("condition").GetProperty("cond").GetProperty("type").GetString()
                    .Should().Be("is_autocommit");
            }
            steps[3].GetProperty("stmt").GetProperty("args")[0].GetProperty("type").GetString().Should().Be("text");
            steps[3].GetProperty("stmt").GetProperty("args")[0].GetProperty("value").GetString().Should()
                .MatchRegex("^[0-9a-f]{32}$");
            steps[3].GetProperty("stmt").GetProperty("args")[1].GetProperty("type").GetString().Should().Be("integer");
            steps[3].GetProperty("stmt").GetProperty("args")[1].GetProperty("value").GetString().Should().Be("0");
            steps[3].GetProperty("stmt").GetProperty("args")[2].GetProperty("type").GetString().Should().Be("integer");
            steps[3].GetProperty("stmt").GetProperty("args")[2].GetProperty("value").GetString().Should().Be("1");
        }
        finally
        {
            push?.Dispose();
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task SyncAsyncRetainsJournalWhenPushHttpRequestFails()
    {
        var path = NewReplicaPath("managed-replica-push-http-failure");
        var image = CreateJournalDatabaseImage(path + ".source");
        var handler = new ReplicaPushHandler(
            [CreatePullResponse("revision-42", image)],
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("unavailable"),
            });
        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");

            Assert.ThrowsAsync<AhtolaException>(() => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            connection.ReadManagedReplicaLocalChanges(10).Changes.Should().ContainSingle();
            handler.PullCallCount.Should().Be(1);
            handler.PushCallCount.Should().Be(1);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task SyncAsyncSurfacesSameRowWriteConflictWithDurableMetadataAndRetainsJournal()
    {
        var path = NewReplicaPath("managed-replica-push-conflict");
        var image = CreateJournalDatabaseImage(path + ".source");
        var handler = new ReplicaPushHandler(
            [CreatePullResponse("revision-42", image)],
            _ => ReplicaPushHandler.BatchErrorResponse(5, 2, "conflicting local change", "SQLITE_CONSTRAINT"));
        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");

            var exception = Assert.ThrowsAsync<AhtolaReplicaConflictException>(
                () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            exception!.RemoteErrorCode.Should().Be("SQLITE_CONSTRAINT");
            exception.ConflictKind.Should().Be(AhtolaReplicaConflictKind.RowWrite);
            exception.LocalChangeSequence.Should().Be(1);
            connection.ReadManagedReplicaLocalChanges(10).Changes.Should().ContainSingle();
            handler.PullCallCount.Should().Be(1);
            handler.PushCallCount.Should().Be(1);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [TestCase(PullResponseFailure.Zstd)]
    [TestCase(PullResponseFailure.InvalidPage)]
    [TestCase(PullResponseFailure.Non4KiBPage)]
    [TestCase(PullResponseFailure.LogicalStream)]
    public void CreateReplicaRejectsInvalidBootstrapStreamsWithoutInstallingFiles(PullResponseFailure failure)
    {
        var path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"managed-embedded-replica-invalid-bootstrap-{Guid.NewGuid():N}.db");
        var response = failure switch
        {
            PullResponseFailure.Zstd => CreatePullResponse("revision-zstd", new byte[4096], zstd: true),
            PullResponseFailure.InvalidPage => CreatePullResponse("revision-page", new byte[1]),
            PullResponseFailure.Non4KiBPage => CreatePullResponse("revision-non-4k", new byte[4095]),
            PullResponseFailure.LogicalStream => CreatePullResponse("revision-logical", new byte[4096], streamKind: 1),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
        var handler = new PullUpdatesHandler(response);
        var options = new AhtolaReplicaOptions(path, new Uri("https://example.test"), authToken: null)
        {
            HttpPolicy = new AhtolaSyncHttpPolicy(handler),
        };

        try
        {
            using var connection = AhtolaConnection.CreateReplica(options);
            Assert.Throws<InvalidDataException>(() => connection.Open());
            handler.CallCount.Should().Be(1);
            File.Exists(path).Should().BeFalse();
            File.Exists(path + ".ahtola-replica-meta").Should().BeFalse();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [TestCase(UnsupportedReplicaMode.RemoteEncryption)]
    [TestCase(UnsupportedReplicaMode.PartialPrefix)]
    [TestCase(UnsupportedReplicaMode.PartialQuery)]
    [TestCase(UnsupportedReplicaMode.ChunkedBootstrap)]
    public void CreateReplicaRejectsUnsupportedModesBeforeOpeningOrMutatingLocalState(
        UnsupportedReplicaMode mode)
    {
        var path = NewReplicaPath($"managed-replica-unsupported-{mode}");
        var handler = new PullUpdatesHandler(CreatePullResponse("unused", new byte[4096]));
        var options = CreateUnsupportedOptions(path, handler, mode);

        try
        {
            using var connection = AhtolaConnection.CreateReplica(options);
            Assert.Throws<NotSupportedException>(() => connection.Open());

            handler.CallCount.Should().Be(0);
            File.Exists(path).Should().BeFalse();
            File.Exists(path + ".ahtola-replica-meta").Should().BeFalse();
            File.Exists(path + ManagedReplicaChangeJournal.Suffix).Should().BeFalse();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task SyncRejectsUnsafeLocalDivergenceBeforeSendingOrReplacingState()
    {
        var path = NewReplicaPath("managed-replica-unsafe-local-divergence");
        var image = CreateDatabaseImage(path + ".source");
        var handler = new PullUpdatesHandler([
            CreatePullResponse("revision-42", image),
            CreatePullResponse("revision-43", image),
        ]);
        var options = CreateOptions(path, handler);

        try
        {
            using (var initial = AhtolaConnection.CreateReplica(options))
                initial.Open();

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                stream.Position = 60; // SQLite's user-version header field is safe to alter externally.
                stream.WriteByte(1);
                stream.Flush(flushToDisk: true);
            }

            var databaseBeforeSync = File.ReadAllBytes(path);
            var metadataBeforeSync = File.ReadAllBytes(path + ".ahtola-replica-meta");
            using (var reopened = AhtolaConnection.CreateReplica(options))
            {
                reopened.Open();
                Assert.ThrowsAsync<NotSupportedException>(
                    () => reopened.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            }

            handler.CallCount.Should().Be(1, "the divergence guard must run before issuing an incremental pull");
            File.ReadAllBytes(path).Should().Equal(databaseBeforeSync);
            File.ReadAllBytes(path + ".ahtola-replica-meta").Should().Equal(metadataBeforeSync);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [TestCase(PullResponseFramingFailure.TruncatedLengthPrefix)]
    [TestCase(PullResponseFramingFailure.TruncatedPayload)]
    [TestCase(PullResponseFramingFailure.OversizedMessage)]
    public void CreateReplicaRejectsMalformedLengthFramingWithoutInstallingFiles(
        PullResponseFramingFailure failure)
    {
        var path = NewReplicaPath($"managed-replica-malformed-framing-{failure}");
        var response = failure switch
        {
            PullResponseFramingFailure.TruncatedLengthPrefix => new byte[] { 0x80 },
            PullResponseFramingFailure.TruncatedPayload => new byte[] { 0x04, 0x01, 0x02 },
            PullResponseFramingFailure.OversizedMessage => CreateOversizedMessagePrefix(),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
        var handler = new PullUpdatesHandler(response);

        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            Assert.Throws<InvalidDataException>(() => connection.Open());
            handler.CallCount.Should().Be(1);
            File.Exists(path).Should().BeFalse();
            File.Exists(path + ".ahtola-replica-meta").Should().BeFalse();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [TestCase(InvalidPageSet.Duplicate)]
    [TestCase(InvalidPageSet.OutOfRange)]
    public void CreateReplicaRejectsDuplicateAndOutOfRangePages(InvalidPageSet invalidPageSet)
    {
        var path = NewReplicaPath($"managed-replica-invalid-pages-{invalidPageSet}");
        var image = CreateDatabaseImage(path + ".source");
        var pageCount = checked((ulong)(image.Length / 4096));
        var response = AppendPage(
            CreatePullResponse("revision-42", image),
            invalidPageSet == InvalidPageSet.Duplicate ? 0UL : pageCount,
            image.AsSpan(0, 4096));
        var handler = new PullUpdatesHandler(response);

        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            Assert.Throws<InvalidDataException>(() => connection.Open());
            File.Exists(path).Should().BeFalse();
            File.Exists(path + ".ahtola-replica-meta").Should().BeFalse();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task SyncAsyncRejectsAChangedRevisionWithNoPagesAndPreservesTheStoredRevision()
    {
        var path = NewReplicaPath("managed-replica-revision-without-pages");
        var image = CreateDatabaseImage(path + ".source");
        var handler = new PullUpdatesHandler([
            CreatePullResponse("revision-42", image),
            CreatePullResponse("revision-43", [], declaredPages: checked((ulong)(image.Length / 4096))),
        ]);

        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            Assert.ThrowsAsync<InvalidDataException>(
                () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
            ManagedReplicaBootstrapper.LoadMetadata(path)!.Value.Revision.Should().Be("revision-42");
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task SyncAsyncCancelsALongPollRequest()
    {
        var path = NewReplicaPath("managed-replica-long-poll-cancel");
        var image = CreateDatabaseImage(path + ".source");
        var handler = new BlockingPullUpdatesHandler(
            CreatePullResponse("revision-42", image),
            CreatePullResponse("revision-42", [], declaredPages: checked((ulong)(image.Length / 4096))));
        using var cancellation = new CancellationTokenSource();

        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            var sync = connection.SyncAsync(new AhtolaSyncOptions(), cancellation.Token);
            await handler.SyncStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.CatchAsync<OperationCanceledException>(() => sync);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task SyncAsyncAppliesTheConfiguredRequestTimeout()
    {
        var path = NewReplicaPath("managed-replica-request-timeout");
        var image = CreateDatabaseImage(path + ".source");
        var handler = new BlockingPullUpdatesHandler(
            CreatePullResponse("revision-42", image),
            CreatePullResponse("revision-42", [], declaredPages: checked((ulong)(image.Length / 4096))));
        var options = new AhtolaReplicaOptions(path, new Uri("https://example.test/cluster"), authToken: "token-42")
        {
            LongPollTimeout = TimeSpan.FromSeconds(3),
            HttpPolicy = new AhtolaSyncHttpPolicy(handler, requestTimeout: TimeSpan.FromMilliseconds(50)),
        };

        try
        {
            using var connection = AhtolaConnection.CreateReplica(options);
            connection.Open();
            var sync = connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
            await handler.SyncStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.CatchAsync<OperationCanceledException>(() => sync);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ReplicaHttpHandlerOwnershipMatchesTheConfiguredPolicy(bool disposeHandler)
    {
        var path = NewReplicaPath($"managed-replica-handler-ownership-{disposeHandler}");
        var image = CreateDatabaseImage(path + ".source");
        var handler = new TrackingPullUpdatesHandler(CreatePullResponse("revision-42", image));
        var options = new AhtolaReplicaOptions(path, new Uri("https://example.test"), authToken: null)
        {
            HttpPolicy = new AhtolaSyncHttpPolicy(handler, disposeMessageHandler: disposeHandler),
        };

        try
        {
            var connection = AhtolaConnection.CreateReplica(options);
            connection.Open();
            connection.Close();
            handler.IsDisposed.Should().BeFalse();
            connection.Open();
            connection.Dispose();
            handler.IsDisposed.Should().Be(disposeHandler);
        }
        finally
        {
            handler.Dispose();
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task SyncAsyncSurfacesSchemaConflictWithDurableMetadataAndRetainsJournal()
    {
        var path = NewReplicaPath("managed-replica-schema-conflict");
        var image = CreateJournalDatabaseImage(path + ".source");
        var handler = new ReplicaPushHandler(
            [CreatePullResponse("revision-42", image)],
            _ => ReplicaPushHandler.BatchErrorResponse(5, 2, "schema conflict detected", "SQLITE_SCHEMA"));
        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            connection.ExecuteNonQuery("CREATE TABLE local_conflict(value INTEGER NOT NULL);");

            var exception = Assert.ThrowsAsync<AhtolaReplicaConflictException>(
                () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));

            exception!.RemoteErrorCode.Should().Be("SQLITE_SCHEMA");
            exception.ConflictKind.Should().Be(AhtolaReplicaConflictKind.SchemaChange);
            exception.LocalChangeSequence.Should().Be(1);
            connection.ReadManagedReplicaLocalChanges(10).Changes.Should().ContainSingle()
                .Which.Kind.Should().Be(ReplicaLocalChangeKind.Schema);
            handler.PullCallCount.Should().Be(1);
            handler.PushCallCount.Should().Be(1);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task ConflictRetryAfterReopenReusesExactPayloadAndAcknowledgesOnlyPushedEntries()
    {
        var path = NewReplicaPath("managed-replica-conflict-retry-after-reopen");
        var image = CreateJournalDatabaseImage(path + ".source");
        var payloads = new List<string>();
        var pushAttempt = 0;
        var handler = new ReplicaPushHandler(
            [
                CreatePullResponse("revision-42", image),
                CreatePullResponse("revision-42", [], declaredPages: 1),
                CreatePullResponse("revision-42", [], declaredPages: 1),
            ],
            request =>
            {
                payloads.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                pushAttempt++;
                return pushAttempt switch
                {
                    1 => ReplicaPushHandler.BatchErrorResponse(
                        stepCount: 6,
                        errorStep: 2,
                        message: "same row write conflict",
                        code: "SQLITE_CONSTRAINT"),
                    2 => ReplicaPushHandler.SuccessfulBatchResponse(stepCount: 6),
                    3 => ReplicaPushHandler.SuccessfulBatchResponse(stepCount: 5),
                    _ => throw new InvalidOperationException("Unexpected replica push attempt."),
                };
            });
        var options = CreateOptions(path, handler, pushOperationsThreshold: 2);
        try
        {
            using (var connection = AhtolaConnection.CreateReplica(options))
            {
                connection.Open();
                connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");
                connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (20);");

                Assert.ThrowsAsync<AhtolaReplicaConflictException>(
                    () => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None));
                connection.ReadManagedReplicaLocalChanges(10).Changes
                    .Select(change => change.Sequence).Should().Equal(1, 2);
            }

            using (var reopened = AhtolaConnection.CreateReplica(options))
            {
                reopened.Open();
                reopened.ReadManagedReplicaLocalChanges(10).Changes
                    .Select(change => change.Sequence).Should().Equal(1, 2);
                reopened.ExecuteNonQuery("INSERT INTO journal_events VALUES (30);");

                var recovery = await reopened.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
                recovery.Statistics.CdcOperations.Should().Be(2);
                payloads.Should().HaveCount(2);
                payloads[1].Should().Be(payloads[0]);
                reopened.ReadManagedReplicaLocalChanges(10).Changes
                    .Select(change => change.Sequence).Should().Equal(3);

                var final = await reopened.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
                final.Statistics.CdcOperations.Should().Be(1);
                reopened.ReadManagedReplicaLocalChanges(10).Changes.Should().BeEmpty();
            }

            handler.PushCallCount.Should().Be(3);
            handler.PullCallCount.Should().Be(3);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [TestCase(BootstrapStagedDatabaseBoundary)]
    [TestCase(BootstrapDatabasePublishedBoundary)]
    public async Task BootstrapCancellationAtDurableBoundaryLeavesNoUnpairedReplicaFiles(
        int boundaryValue)
    {
        var boundary = (ManagedReplicaDurableBoundary)boundaryValue;
        var path = NewReplicaPath($"managed-replica-bootstrap-cancel-{boundary}");
        var image = CreateDatabaseImage(path + ".source");
        var handler = new PullUpdatesHandler([
            CreatePullResponse("revision-42", image),
            CreatePullResponse("revision-42", image),
        ]);
        var options = CreateOptions(path, handler);
        using var cancellation = new CancellationTokenSource();
        try
        {
            using (ManagedReplicaFaultInjection.Push(point =>
                   {
                       if (point == boundary)
                           cancellation.Cancel();
                   }))
            {
                Assert.CatchAsync<OperationCanceledException>(
                    () => ManagedReplicaBootstrapper.BootstrapAsync(options, cancellation.Token));
            }

            File.Exists(path).Should().BeFalse();
            ManagedReplicaBootstrapper.LoadMetadata(path).Should().BeNull();

            using var reopened = AhtolaConnection.CreateReplica(options);
            reopened.Open();
            reopened.ExecuteNonQuery("SELECT value FROM bootstrap_marker;").Should().Be(0);
            handler.CallCount.Should().Be(2);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [TestCase(IncrementalApplyStagedDatabaseBoundary, false)]
    [TestCase(IncrementalApplyDatabasePublishedBoundary, false)]
    [TestCase(IncrementalApplyMetadataPublishedBoundary, true)]
    public async Task IncrementalApplyCancellationRecoversAMatchedDatabaseAndMetadataPair(
        int boundaryValue,
        bool expectedRemoteImage)
    {
        var boundary = (ManagedReplicaDurableBoundary)boundaryValue;
        var path = NewReplicaPath($"managed-replica-incremental-cancel-{boundary}");
        var initialImage = CreateDatabaseImage(path + ".initial");
        var updatedImage = CreateDatabaseImageWithMarker(path + ".updated", 84);
        var handler = new PullUpdatesHandler([
            CreatePullResponse("revision-42", initialImage),
            CreatePullResponse("revision-43", updatedImage),
        ]);
        var options = CreateOptions(path, handler);
        using var cancellation = new CancellationTokenSource();
        try
        {
            using (var connection = AhtolaConnection.CreateReplica(options))
            {
                connection.Open();
                using (ManagedReplicaFaultInjection.Push(point =>
                       {
                           if (point == boundary)
                               cancellation.Cancel();
                       }))
                {
                    Assert.CatchAsync<OperationCanceledException>(
                        () => connection.SyncAsync(new AhtolaSyncOptions(), cancellation.Token));
                }

                using var verifyCommand = connection.CreateCommand();
                verifyCommand.CommandText = "SELECT value FROM bootstrap_marker;";
                verifyCommand.ExecuteScalar().Should().Be(expectedRemoteImage ? 84L : 42L);
            }

            File.ReadAllBytes(path).Should().Equal(expectedRemoteImage ? updatedImage : initialImage);
            var metadata = ManagedReplicaBootstrapper.LoadMetadata(path);
            metadata.Should().NotBeNull();
            metadata!.Value.Revision.Should().Be(expectedRemoteImage ? "revision-43" : "revision-42");
            ManagedReplicaBootstrapper.EnsureNoLocalDivergence(path, metadata.Value);

            using var reopened = AhtolaConnection.CreateReplica(options);
            reopened.Open();
            reopened.ExecuteNonQuery("SELECT value FROM bootstrap_marker;").Should().Be(0);
            using var command = reopened.CreateCommand();
            command.CommandText = "SELECT value FROM bootstrap_marker;";
            command.ExecuteScalar().Should().Be(expectedRemoteImage ? 84L : 42L);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void JournalPublicationFaultsRecoverWithoutDuplicateAcknowledgedChanges()
    {
        var path = NewReplicaPath("managed-replica-journal-durable-boundaries");
        try
        {
            var journal = ManagedReplicaChangeJournal.Open(path);
            using (ManagedReplicaFaultInjection.Push(point =>
                   {
                       if (point == ManagedReplicaDurableBoundary.JournalAppendPersisted)
                           throw new InvalidOperationException("Injected journal append interruption.");
                   }))
            {
                Assert.Throws<InvalidOperationException>(
                    () => journal.AppendCommitted([ReplicaLocalChange.Schema("CREATE TABLE journal_events(value INTEGER);")]));
            }

            var reopenedAfterAppend = ManagedReplicaChangeJournal.Open(path);
            reopenedAfterAppend.ReadBatch(10).Changes.Select(change => change.Sequence).Should().Equal(1);

            using (ManagedReplicaFaultInjection.Push(point =>
                   {
                       if (point == ManagedReplicaDurableBoundary.JournalAcknowledgementPersisted)
                           throw new InvalidOperationException("Injected journal acknowledgement interruption.");
                   }))
            {
                Assert.Throws<InvalidOperationException>(() => reopenedAfterAppend.Acknowledge(2));
            }

            var reopenedAfterAcknowledgement = ManagedReplicaChangeJournal.Open(path);
            reopenedAfterAcknowledgement.ReadBatch(10).Changes.Should().BeEmpty();
            reopenedAfterAcknowledgement.AppendCommitted(
                [ReplicaLocalChange.Schema("CREATE TABLE journal_events_two(value INTEGER);")]);
            reopenedAfterAcknowledgement.ReadBatch(10).Changes.Select(change => change.Sequence).Should().Equal(2);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void CreateReplicaWithBootstrapDisabledDoesNotCreateAMissingDatabase()
    {
        var path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"managed-embedded-replica-missing-{Guid.NewGuid():N}.db");
        var options = new AhtolaReplicaOptions(
            path,
            new Uri("https://example.com"),
            authToken: null,
            bootstrapIfEmpty: false);

        try
        {
            using var connection = AhtolaConnection.CreateReplica(options);
            Assert.Throws<NotSupportedException>(() => connection.Open())!.Message.Should()
                .Be("Managed embedded replica bootstrap is disabled and the replica path does not contain an initialized managed database.");
            File.Exists(path).Should().BeFalse();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task SyncAsyncReportsUpToDateForTheStoredRevision()
    {
        var path = NewReplicaPath("managed-embedded-replica-up-to-date");
        var image = CreateDatabaseImage(path + ".source");
        var handler = new PullUpdatesHandler([
            CreatePullResponse("revision-42", image),
                CreatePullResponse("revision-42", [], declaredPages: 1),
            ], request =>
            {
                if (request.Content!.Headers.ContentLength == 0)
                    return;
                var fields = ReadFields(request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
                if (!fields.ContainsKey(3))
                {
                    fields[4].Number.Should().Be(3000);
                    return;
                }
                fields[3].Text.Should().Be("revision-42");
                fields[4].Number.Should().Be(3000);
            });
        var progress = new ProgressRecorder();
        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            connection.Open();
            connection.Capabilities.SupportsSync.Should().BeTrue();
            var result = await connection.SyncAsync(
                new AhtolaSyncOptions(progress),
                CancellationToken.None);
            result.Outcome.Should().Be(AhtolaSyncOutcome.UpToDate);
            result.Statistics.Revision.Should().Be("revision-42");
            result.Statistics.NetworkSentBytes.Should().BeGreaterThan(0);
            result.Statistics.NetworkReceivedBytes.Should().BeGreaterThan(0);
            progress.Stages.Should().Contain([AhtolaSyncProgressStage.Pulling, AhtolaSyncProgressStage.Completed]);
            handler.CallCount.Should().Be(2);
        }
        finally { DeleteReplicaFiles(path); }
    }

    [Test]
    public async Task SyncAsyncWithChangedRemotePublishesTheNewRevision()
    {
        var path = NewReplicaPath("managed-embedded-replica-changed");
        var image = CreateDatabaseImage(path + ".source");
        var handler = new PullUpdatesHandler([
            CreatePullResponse("revision-42", image),
                CreatePullResponse("revision-43", image),
            ]);
        try
        {
            using (var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler)))
            {
                connection.Open();
                var result = await connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
                result.Outcome.Should().Be(AhtolaSyncOutcome.RemoteChangesApplied);
                result.Statistics.Revision.Should().Be("revision-43");
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT value FROM bootstrap_marker;";
                command.ExecuteScalar().Should().Be(42L);
            }
            File.ReadAllBytes(path).Should().Equal(image);
            File.ReadAllText(path + ".ahtola-replica-meta")
                .Should().Contain("server_revision_base64=cmV2aXNpb24tNDM=");
        }
        finally { DeleteReplicaFiles(path); }
    }

    [Test]
    public async Task QuiesceManagedReplicaReopensOnlyWhenNoReaderOrTransactionIsActive()
    {
        var path = NewReplicaPath("managed-embedded-replica-quiesce");
        try
        {
            CreateInitializedDatabase(path);
            using var connection = AhtolaConnection.CreateReplica(
                new AhtolaReplicaOptions(path, new Uri("https://example.test"), authToken: null));
            connection.Open();
            await connection.QuiesceManagedReplicaAsync(_ => Task.CompletedTask);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT value FROM bootstrap_marker;";
                command.ExecuteScalar().Should().Be(42L);
            }

            using (var transaction = connection.BeginTransaction())
            {
                Assert.ThrowsAsync<InvalidOperationException>(
                    () => connection.QuiesceManagedReplicaAsync(_ => Task.CompletedTask))!
                    .Message.Should().Be("Managed embedded replica sync cannot run while a transaction is active.");
                transaction.Rollback();
            }

            using var readerCommand = connection.CreateCommand();
            readerCommand.CommandText = "SELECT value FROM bootstrap_marker;";
            using var reader = readerCommand.ExecuteReader();
            Assert.ThrowsAsync<InvalidOperationException>(
                () => connection.QuiesceManagedReplicaAsync(_ => Task.CompletedTask))!
                .Message.Should().Be("Managed embedded replica sync cannot run while a data reader is active.");
        }
        finally { DeleteReplicaFiles(path); }
    }

    [Test]
    public async Task SyncAsyncIsSingleFlightAcrossConnectionsForTheSameReplicaPath()
    {
        var path = NewReplicaPath("managed-replica-single-flight");
        var image = CreateDatabaseImage(path + ".source");
        var handler = new BlockingPullUpdatesHandler(
            CreatePullResponse("revision-42", image),
            CreatePullResponse("revision-42", [], declaredPages: 1));
        try
        {
            using var first = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            using var second = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            first.Open();
            second.Open();

            var firstSync = first.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
            await handler.SyncStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var secondSync = second.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
            await Task.Delay(100);
            handler.CallCount.Should().Be(2);

            handler.Release();
            var results = await Task.WhenAll(firstSync, secondSync).WaitAsync(TimeSpan.FromSeconds(5));
            results.Should().AllSatisfy(result => result.Outcome.Should().Be(AhtolaSyncOutcome.UpToDate));
            handler.CallCount.Should().Be(2);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task SyncAsyncWaitsForReadersAndTransactionsOnSiblingConnections()
    {
        var path = NewReplicaPath("managed-replica-reader-transaction-boundary");
        var image = CreateDatabaseImage(path + ".source");
        var handler = new PullUpdatesHandler([
            CreatePullResponse("revision-42", image),
            CreatePullResponse("revision-42", [], declaredPages: 1),
            CreatePullResponse("revision-42", [], declaredPages: 1),
            CreatePullResponse("revision-42", [], declaredPages: 1),
            CreatePullResponse("revision-42", [], declaredPages: 1),
        ]);
        try
        {
            using var local = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            using var synchronizer = AhtolaConnection.CreateReplica(CreateOptions(path, handler));
            local.Open();
            synchronizer.Open();

            using (var transaction = local.BeginTransaction())
            {
                var sync = synchronizer.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
                await Task.Delay(100);
                sync.IsCompleted.Should().BeFalse();
                transaction.Rollback();
                (await sync.WaitAsync(TimeSpan.FromSeconds(5))).Outcome.Should().Be(AhtolaSyncOutcome.UpToDate);
            }

            using var prepared = local.CreateCommand();
            prepared.CommandText = "SELECT value FROM bootstrap_marker;";
            prepared.Prepare();
            using var command = local.CreateCommand();
            command.CommandText = "SELECT value FROM bootstrap_marker;";
            using var reader = command.ExecuteReader();
            var readerSync = synchronizer.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
            await Task.Delay(100);
            readerSync.IsCompleted.Should().BeFalse();
            reader.Dispose();
            (await readerSync.WaitAsync(TimeSpan.FromSeconds(5))).Outcome.Should().Be(AhtolaSyncOutcome.UpToDate);

            prepared.ExecuteScalar().Should().Be(42L);

            using (var batch = (AhtolaBatch)local.CreateBatch())
            {
                batch.BatchCommands.Add(new AhtolaBatchCommand("SELECT value FROM bootstrap_marker;"));
                using var batchReader = batch.ExecuteReader();
                var batchSync = synchronizer.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
                await Task.Delay(100);
                batchSync.IsCompleted.Should().BeFalse();
                batchReader.Dispose();
                (await batchSync.WaitAsync(TimeSpan.FromSeconds(5))).Outcome.Should().Be(AhtolaSyncOutcome.UpToDate);
            }

            local.ExecuteNonQuery("BEGIN;");
            var sqlTransactionSync = synchronizer.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
            await Task.Delay(100);
            sqlTransactionSync.IsCompleted.Should().BeFalse();
            local.ExecuteNonQuery("ROLLBACK;");
            (await sqlTransactionSync.WaitAsync(TimeSpan.FromSeconds(5))).Outcome.Should().Be(AhtolaSyncOutcome.UpToDate);

            synchronizer.ExecuteNonQuery("SELECT value FROM bootstrap_marker;").Should().Be(0);
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task SyncAsyncDoesNotSerializeUnrelatedReplicaPaths()
    {
        var firstPath = NewReplicaPath("managed-replica-path-isolation-first");
        var secondPath = NewReplicaPath("managed-replica-path-isolation-second");
        var firstImage = CreateDatabaseImage(firstPath + ".source");
        var secondImage = CreateDatabaseImage(secondPath + ".source");
        var blockedHandler = new BlockingPullUpdatesHandler(
            CreatePullResponse("revision-42", firstImage),
            CreatePullResponse("revision-42", [], declaredPages: 1));
        var independentHandler = new PullUpdatesHandler([
            CreatePullResponse("revision-42", secondImage),
            CreatePullResponse("revision-42", [], declaredPages: 1),
        ]);
        try
        {
            using var blocked = AhtolaConnection.CreateReplica(CreateOptions(firstPath, blockedHandler));
            using var independent = AhtolaConnection.CreateReplica(CreateOptions(secondPath, independentHandler));
            blocked.Open();
            independent.Open();

            var blockedSync = blocked.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
            await blockedHandler.SyncStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            (await independent.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5))).Outcome.Should().Be(AhtolaSyncOutcome.UpToDate);
            blockedSync.IsCompleted.Should().BeFalse();

            blockedHandler.Release();
            (await blockedSync.WaitAsync(TimeSpan.FromSeconds(5))).Outcome.Should().Be(AhtolaSyncOutcome.UpToDate);
        }
        finally
        {
            DeleteReplicaFiles(firstPath);
            DeleteReplicaFiles(secondPath);
        }
    }

    [Test]
    public async Task ManagedReplicaSyncIntervalSchedulesSynchronizationAfterOpen()
    {
        var path = NewReplicaPath("managed-replica-automatic-schedule");
        var image = CreateDatabaseImage(path + ".source");
        var handler = new AutomaticPullUpdatesHandler(
            CreatePullResponse("revision-42", image),
            CreatePullResponse("revision-42", [], declaredPages: 1));
        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler, syncInterval: 1));
            connection.Open();

            await handler.SyncStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            handler.SyncCallCount.Should().Be(1);
            connection.Close();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task ManagedReplicaSyncIntervalUsesOneInFlightSyncPerPath()
    {
        var path = NewReplicaPath("managed-replica-automatic-single-flight");
        var image = CreateDatabaseImage(path + ".source");
        var handler = new AutomaticPullUpdatesHandler(
            CreatePullResponse("revision-42", image),
            CreatePullResponse("revision-42", [], declaredPages: 1),
            blockSync: true);
        try
        {
            using var first = AhtolaConnection.CreateReplica(CreateOptions(path, handler, syncInterval: 1));
            using var second = AhtolaConnection.CreateReplica(CreateOptions(path, handler, syncInterval: 1));
            first.Open();
            second.Open();

            await handler.SyncStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(150);
            handler.SyncCallCount.Should().Be(1);

            handler.Release();
            await handler.SyncCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            handler.SyncCallCount.Should().Be(1);
            first.Close();
            second.Close();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task ManagedReplicaSyncIntervalCancelsAndAwaitsTheBackgroundSyncOnClose()
    {
        var path = NewReplicaPath("managed-replica-automatic-close");
        var image = CreateDatabaseImage(path + ".source");
        var handler = new AutomaticPullUpdatesHandler(
            CreatePullResponse("revision-42", image),
            CreatePullResponse("revision-42", [], declaredPages: 1),
            blockSync: true);
        var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler, syncInterval: 1));
        try
        {
            connection.Open();
            await handler.SyncStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await Task.Run(connection.Close).WaitAsync(TimeSpan.FromSeconds(5));
            await handler.SyncCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            connection.State.Should().Be(System.Data.ConnectionState.Closed);
        }
        finally
        {
            connection.Dispose();
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task ManagedReplicaSyncIntervalRetriesTransientHttpFailures()
    {
        var path = NewReplicaPath("managed-replica-automatic-retry");
        var image = CreateDatabaseImage(path + ".source");
        var handler = new AutomaticPullUpdatesHandler(
            CreatePullResponse("revision-42", image),
            CreatePullResponse("revision-42", [], declaredPages: 1),
            transientFailures: 1);
        try
        {
            using var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler, syncInterval: 1));
            connection.Open();

            await handler.SyncCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            handler.SyncCallCount.Should().Be(2);
            connection.Close();
        }
        finally
        {
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public async Task ManagedReplicaSyncIntervalDoesNotRetryReplicaConflicts()
    {
        var path = NewReplicaPath("managed-replica-automatic-conflict");
        var image = CreateJournalDatabaseImage(path + ".source");
        var handler = new ReplicaPushHandler(
            [CreatePullResponse("revision-42", image)],
            _ => ReplicaPushHandler.BatchErrorResponse(5, 2, "conflicting local change", "SQLITE_CONSTRAINT"));
        var connection = AhtolaConnection.CreateReplica(CreateOptions(path, handler, syncInterval: 1));
        try
        {
            connection.Open();
            connection.ExecuteNonQuery("INSERT INTO journal_events VALUES (10);");

            await handler.PushStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(150);
            handler.PushCallCount.Should().Be(1);
            var exception = Assert.Throws<AhtolaReplicaConflictException>(() => connection.Close());
            exception!.ConflictKind.Should().Be(AhtolaReplicaConflictKind.RowWrite);
            exception.LocalChangeSequence.Should().Be(1);
            ManagedReplicaChangeJournal.Open(path).ReadBatch(10).Changes
                .Select(change => change.Sequence).Should().Equal(1);
        }
        finally
        {
            connection.Dispose();
            DeleteReplicaFiles(path);
        }
    }

    [Test]
    public void SyncIntervalRequiresAManagedEmbeddedReplica()
    {
        using var local = new AhtolaConnection(
            "Data Source=:memory:;Local Provider=Managed;Sync Interval=1");
        Assert.Throws<NotSupportedException>(() => local.Open())!.Message.Should()
            .Be("Sync Interval requires a managed embedded replica connection.");

        using var remote = new AhtolaConnection(
            "Data Source=https://example.test;Sync Interval=1");
        Assert.Throws<NotSupportedException>(() => remote.Open())!.Message.Should()
            .Be("Sync Interval requires a managed embedded replica connection.");
    }

    [Test]
    public async Task ManagedReplicaCloudStressQualifiesConcurrentReplicationAndCoordinatorBounds()
    {
        var paths = Enumerable.Range(0, 3)
            .Select(index => NewReplicaPath($"managed-replica-cloud-stress-{index}"))
            .ToArray();
        var sourcePath = paths[0] + ".cloud-source";
        var initialConnections = new List<AhtolaConnection>();
        try
        {
            CreateJournalDatabase(sourcePath);
            var handler = new DeterministicCloudReplicationHandler(sourcePath, paths.Length);
            var options = paths.Select(path => CreateOptions(path, handler)).ToArray();
            var replicas = options.Select(option =>
            {
                var connection = AhtolaConnection.CreateReplica(option);
                connection.Open();
                initialConnections.Add(connection);
                return connection;
            }).ToArray();

            var initialValues = new[] { 10, 20, 30 };
            await Task.WhenAll(replicas.Select((connection, index) =>
                    Task.Run(() => connection.ExecuteNonQuery(
                        $"INSERT INTO journal_events VALUES ({initialValues[index]});"))))
                .WaitAsync(TimeSpan.FromSeconds(5));
            foreach (var replica in replicas)
                replica.ReadManagedReplicaLocalChanges(10).Changes.Select(change => change.Sequence).Should().Equal(1);

            var coordinator = AhtolaConnection.CreateReplica(options[0]);
            var singleFlightObserver = AhtolaConnection.CreateReplica(options[0]);
            coordinator.Open();
            singleFlightObserver.Open();
            initialConnections.Add(coordinator);
            initialConnections.Add(singleFlightObserver);

            using var longReaderCommand = replicas[0].CreateCommand();
            longReaderCommand.CommandText = "SELECT value FROM journal_events;";
            using var longReader = longReaderCommand.ExecuteReader();
            var firstSync = coordinator.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
            var joinedSync = singleFlightObserver.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None);
            var otherSyncs = replicas.Skip(1)
                .Select(connection => connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None))
                .ToArray();

            await handler.TwoPushesArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            firstSync.IsCompleted.Should().BeFalse("the long reader must hold its path's publication lease");
            joinedSync.IsCompleted.Should().BeFalse();
            handler.PushCallCount.Should().Be(2, "same-path sync requests must share one coordinator flight");

            longReader.Dispose();
            _ = await Task.WhenAll([firstSync, joinedSync, .. otherSyncs])
                .WaitAsync(TimeSpan.FromSeconds(10));
            handler.PushCallCount.Should().Be(3, "one push is expected for each distinct replica path");
            handler.PullCallCount.Should().BeGreaterThan(paths.Length, "every synchronized path must also pull");
            foreach (var replica in replicas)
                replica.ReadManagedReplicaLocalChanges(10).Changes.Should().BeEmpty("a successful push acknowledges its journal");

            DisposeConnections(initialConnections);

            var expectedValues = initialValues.ToList();
            for (var round = 0; round < 2; round++)
            {
                var reopened = new List<AhtolaConnection>();
                try
                {
                    foreach (var option in options)
                    {
                        var connection = AhtolaConnection.CreateReplica(option);
                        connection.Open();
                        connection.ReadManagedReplicaLocalChanges(10).Changes.Should().BeEmpty(
                            "acknowledged entries must remain absent after reopening");
                        reopened.Add(connection);
                    }

                    var roundValues = Enumerable.Range(0, paths.Length)
                        .Select(index => 100 + round * 10 + index)
                        .ToArray();
                    await Task.WhenAll(reopened.Select((connection, index) =>
                            Task.Run(() => connection.ExecuteNonQuery(
                                $"INSERT INTO journal_events VALUES ({roundValues[index]});"))))
                        .WaitAsync(TimeSpan.FromSeconds(5));
                    foreach (var connection in reopened)
                    {
                        connection.ReadManagedReplicaLocalChanges(10).Changes
                            .Select(change => change.Sequence)
                            .Should()
                            .Equal(
                                new long[] { round + 2 },
                                "the next entry must not reuse an acknowledged sequence");
                    }

                    _ = await Task.WhenAll(reopened.Select(connection =>
                            connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None)))
                        .WaitAsync(TimeSpan.FromSeconds(10));
                    foreach (var connection in reopened)
                        connection.ReadManagedReplicaLocalChanges(10).Changes.Should().BeEmpty();

                    expectedValues.AddRange(roundValues);
                    _ = await Task.WhenAll(reopened.Select(connection =>
                            connection.SyncAsync(new AhtolaSyncOptions(), CancellationToken.None)))
                        .WaitAsync(TimeSpan.FromSeconds(10));
                    ReadJournalEventValues(sourcePath).Should().Equal(expectedValues.Order());
                }
                finally
                {
                    DisposeConnections(reopened);
                }
            }
        }
        finally
        {
            DisposeConnections(initialConnections);
            DeleteReplicaFiles(sourcePath);
            foreach (var path in paths)
                DeleteReplicaFiles(path);
        }
    }

    private static void CreateInitializedDatabase(string path, int marker = 42)
    {
        using var connection = new AhtolaConnection($"Data Source={path};Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE bootstrap_marker(value INTEGER NOT NULL);");
        connection.ExecuteNonQuery($"INSERT INTO bootstrap_marker VALUES ({marker});");
    }

    private static void CreateJournalDatabase(string path)
    {
        CreateInitializedDatabase(path);
        using var connection = new AhtolaConnection($"Data Source={path};Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE journal_events(value INTEGER NOT NULL);");
    }

    private static IReadOnlyList<int> ReadJournalEventValues(string path)
    {
        using var connection = new AhtolaConnection($"Data Source={path};Local Provider=Managed");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM journal_events ORDER BY value;";
        using var reader = command.ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add(checked((int)reader.GetInt64(0)));
        return values;
    }

    private static long ReadBootstrapMarker(AhtolaConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM bootstrap_marker;";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string NewReplicaPath(string prefix)
        => Path.Combine(TestContext.CurrentContext.WorkDirectory, $"{prefix}-{Guid.NewGuid():N}.db");

    private static byte[] CreateDatabaseImage(string path)
    {
        try { CreateInitializedDatabase(path); return File.ReadAllBytes(path); }
        finally { DeleteReplicaFiles(path); }
    }

    private static byte[] CreateDatabaseImageWithMarker(string path, int marker)
    {
        try { CreateInitializedDatabase(path, marker); return File.ReadAllBytes(path); }
        finally { DeleteReplicaFiles(path); }
    }

    private static byte[] CreateJournalDatabaseImage(string path)
    {
        try { CreateJournalDatabase(path); return File.ReadAllBytes(path); }
        finally { DeleteReplicaFiles(path); }
    }

    private static AhtolaReplicaOptions CreateOptions(
        string path,
        HttpMessageHandler handler,
        int syncInterval = 0,
        long? pushOperationsThreshold = null)
        => new(path, new Uri("https://example.test/cluster"), authToken: "token-42")
        {
            LongPollTimeout = TimeSpan.FromSeconds(3),
            SyncInterval = syncInterval,
            PushOperationsThreshold = pushOperationsThreshold,
            HttpPolicy = new AhtolaSyncHttpPolicy(handler),
        };

    private static AhtolaReplicaOptions CreateUnsupportedOptions(
        string path,
        HttpMessageHandler handler,
        UnsupportedReplicaMode mode)
    {
        var options = CreateOptions(path, handler);
        return mode switch
        {
            UnsupportedReplicaMode.RemoteEncryption => new AhtolaReplicaOptions(
                path,
                options.RemoteUri,
                options.AuthToken)
            {
                RemoteEncryption = new AhtolaRemoteEncryptionOptions(
                    "c2VjcmV0",
                    AhtolaRemoteEncryptionCipher.Aes256Gcm),
                HttpPolicy = options.HttpPolicy,
            },
            UnsupportedReplicaMode.PartialPrefix => new AhtolaReplicaOptions(
                path,
                options.RemoteUri,
                options.AuthToken)
            {
                PartialBootstrap = AhtolaPartialBootstrapOptions.Prefix(4096),
                HttpPolicy = options.HttpPolicy,
            },
            UnsupportedReplicaMode.PartialQuery => new AhtolaReplicaOptions(
                path,
                options.RemoteUri,
                options.AuthToken)
            {
                PartialBootstrap = AhtolaPartialBootstrapOptions.QueryPages("SELECT 1"),
                HttpPolicy = options.HttpPolicy,
            },
            UnsupportedReplicaMode.ChunkedBootstrap => new AhtolaReplicaOptions(
                path,
                options.RemoteUri,
                options.AuthToken)
            {
                PullBytesThreshold = 4096,
                HttpPolicy = options.HttpPolicy,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    private static void DeleteReplicaFiles(string path)
    {
        foreach (var file in new[]
                 {
                     path,
                     path + "-wal",
                     path + "-shm",
                     path + "-journal",
                     path + ".ahtola-replica-meta",
                     path + ManagedReplicaChangeJournal.Suffix,
                 })
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    private static void DisposeConnections(List<AhtolaConnection> connections)
    {
        foreach (var connection in connections.AsEnumerable().Reverse().ToArray())
            connection.Dispose();
        connections.Clear();
    }

    private static byte[] CreatePullResponse(
        string revision,
        byte[] databaseImage,
        bool zstd = false,
        ulong streamKind = 0,
        ulong? declaredPages = null,
        bool omitDefaultPageId = true,
        ulong? protocol = 1)
    {
        var header = new List<byte>();
        WriteLengthDelimitedField(header, 1, Encoding.UTF8.GetBytes(revision));
        WriteVarintField(header, 2, declaredPages ?? checked((ulong)((databaseImage.Length + 4095) / 4096)));
        if (!zstd)
            WriteLengthDelimitedField(header, 3, []);
        else
            WriteLengthDelimitedField(header, 4, []);
        WriteVarintField(header, 5, streamKind);
        WriteVarintField(header, 6, 1);
        if (protocol is { } protocolValue)
            WriteVarintField(header, 8, protocolValue);

        var response = new List<byte>();
        WriteDelimitedMessage(response, header);
        for (var offset = 0; offset < databaseImage.Length; offset += 4096)
        {
            var page = new List<byte>();
            if (!omitDefaultPageId || offset != 0)
                WriteVarintField(page, 1, checked((ulong)(offset / 4096)));
            var length = Math.Min(4096, databaseImage.Length - offset);
            WriteLengthDelimitedField(page, 2, databaseImage.AsSpan(offset, length));
            WriteDelimitedMessage(response, page);
        }
        return response.ToArray();
    }

    private static byte[] BuildLogicalLogRangeMessage(
        ulong generation, ulong startOffset, ulong endOffset, bool startsWithHeader, byte[]? crcSeed = null)
    {
        var range = new List<byte>();
        WriteVarintField(range, 1, generation);
        WriteVarintField(range, 2, startOffset);
        WriteVarintField(range, 3, endOffset);
        if (startsWithHeader)
            WriteVarintField(range, 4, 1);
        if (crcSeed is not null)
            WriteLengthDelimitedField(range, 5, crcSeed);
        return range.ToArray();
    }

    /// <summary>Builds a pull-updates response header (tag 5 = stream_kind MvccLogicalLog) plus its raw lml3 body appended verbatim (no further length-delimited framing).</summary>
    private static byte[] CreateLogicalPullResponse(
        string revision,
        byte[] body,
        IReadOnlyList<byte[]>? rangeMessages = null,
        ulong declaredPages = 1,
        ulong applyMode = 0,
        ulong protocol = 2,
        bool checkpointTransition = false)
    {
        var header = new List<byte>();
        WriteLengthDelimitedField(header, 1, Encoding.UTF8.GetBytes(revision));
        WriteVarintField(header, 2, declaredPages);
        WriteLengthDelimitedField(header, 3, []); // raw_encoding
        WriteVarintField(header, 5, 1); // stream_kind = MvccLogicalLog
        WriteVarintField(header, 6, applyMode);
        if (rangeMessages is not null)
        {
            var metadata = new List<byte>();
            WriteLengthDelimitedField(metadata, 1, Encoding.UTF8.GetBytes("lml3"));
            if (checkpointTransition)
                WriteVarintField(metadata, 2, 1);
            foreach (var range in rangeMessages)
                WriteLengthDelimitedField(metadata, 3, range);
            WriteLengthDelimitedField(header, 7, metadata.ToArray());
        }
        WriteVarintField(header, 8, protocol);

        var response = new List<byte>();
        WriteDelimitedMessage(response, header);
        response.AddRange(body);
        return response.ToArray();
    }

    private static byte[] CreateOversizedMessagePrefix()
    {
        var response = new List<byte>();
        WriteVarint(response, 64 * 1024 + 1);
        return response.ToArray();
    }

    private static byte[] AppendPage(byte[] response, ulong pageId, ReadOnlySpan<byte> pageData)
    {
        var result = new List<byte>(response);
        var page = new List<byte>();
        WriteVarintField(page, 1, pageId);
        WriteLengthDelimitedField(page, 2, pageData);
        WriteDelimitedMessage(result, page);
        return result.ToArray();
    }

    private static Dictionary<int, ulong> ReadVarintFields(byte[] payload)
    {
        var fields = new Dictionary<int, ulong>();
        var offset = 0;
        while (offset < payload.Length)
        {
            var key = ReadVarint(payload, ref offset);
            (key & 7).Should().Be(0);
            fields.Add(checked((int)(key >> 3)), ReadVarint(payload, ref offset));
        }
        return fields;
    }

    private static Dictionary<int, (ulong? Number, string? Text)> ReadFields(byte[] payload)
    {
        var fields = new Dictionary<int, (ulong?, string?)>();
        var offset = 0;
        while (offset < payload.Length)
        {
            var key = ReadVarint(payload, ref offset);
            var field = checked((int)(key >> 3));
            fields[field] = (key & 7) == 0
                ? (ReadVarint(payload, ref offset), null)
                : (null, Encoding.UTF8.GetString(payload, offset + 1, checked((int)payload[offset])));
            if ((key & 7) == 2)
                offset += 1 + payload[offset];
        }
        return fields;
    }
    private static ulong ReadVarint(byte[] source, ref int offset)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            var next = source[offset++];
            result |= (ulong)(next & 0x7f) << shift;
            if ((next & 0x80) == 0)
                return result;
        }
        throw new InvalidOperationException("Invalid test protobuf varint.");
    }

    private static void WriteDelimitedMessage(List<byte> destination, List<byte> message)
    {
        WriteVarint(destination, checked((ulong)message.Count));
        destination.AddRange(message);
    }

    private static void WriteLengthDelimitedField(List<byte> destination, int fieldNumber, ReadOnlySpan<byte> value)
    {
        WriteVarint(destination, checked((ulong)fieldNumber << 3 | 2));
        WriteVarint(destination, checked((ulong)value.Length));
        destination.AddRange(value.ToArray());
    }

    private static void WriteVarintField(List<byte> destination, int fieldNumber, ulong value)
    {
        WriteVarint(destination, checked((ulong)fieldNumber << 3));
        WriteVarint(destination, value);
    }

    private static void WriteVarint(List<byte> destination, ulong value)
    {
        while (value >= 0x80)
        {
            destination.Add((byte)((value & 0x7f) | 0x80));
            value >>= 7;
        }
        destination.Add((byte)value);
    }

    public enum PullResponseFailure
    {
        Zstd,
        InvalidPage,
        Non4KiBPage,
        LogicalStream,
    }

    public enum UnsupportedReplicaMode
    {
        RemoteEncryption,
        PartialPrefix,
        PartialQuery,
        ChunkedBootstrap,
    }

    public enum PullResponseFramingFailure
    {
        TruncatedLengthPrefix,
        TruncatedPayload,
        OversizedMessage,
    }

    public enum InvalidPageSet
    {
        Duplicate,
        OutOfRange,
    }

    private sealed class ProgressRecorder : IProgress<AhtolaSyncProgress>
    {
        public List<AhtolaSyncProgressStage> Stages { get; } = [];

        public void Report(AhtolaSyncProgress value) => Stages.Add(value.Stage);
    }

    private sealed class PullUpdatesHandler : HttpMessageHandler
    {
        private readonly Queue<byte[]> _responses;
        private readonly Action<HttpRequestMessage>? _assertRequest;

        public PullUpdatesHandler(byte[] response, Action<HttpRequestMessage>? assertRequest = null)
            : this([response], assertRequest) { }

        public PullUpdatesHandler(IEnumerable<byte[]> responses, Action<HttpRequestMessage>? assertRequest = null)
        {
            _responses = new Queue<byte[]>(responses);
            _assertRequest = assertRequest;
        }

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            _assertRequest?.Invoke(request);
            var message = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_responses.Dequeue()),
            };
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/protobuf");
            return Task.FromResult(message);
        }
    }

    private sealed class TrackingPullUpdatesHandler(byte[] response) : HttpMessageHandler
    {
        private readonly byte[] _response = response;

        public bool IsDisposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var message = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_response),
            };
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/protobuf");
            return Task.FromResult(message);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class BlockingPullUpdatesHandler(byte[] bootstrapResponse, byte[] syncResponse) : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> SyncStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _callCount);

        private int _callCount;

        public void Release() => _release.TrySetResult(true);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (call != 1)
            {
                SyncStarted.TrySetResult(true);
                await _release.Task.WaitAsync(cancellationToken);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(call == 1 ? bootstrapResponse : syncResponse),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/protobuf");
            return response;
        }
    }

    private sealed class AutomaticPullUpdatesHandler(
        byte[] bootstrapResponse,
        byte[] syncResponse,
        bool blockSync = false,
        int transientFailures = 0) : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestCount;
        private int _syncCallCount;

        public TaskCompletionSource<bool> SyncStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> SyncCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> SyncCanceled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int SyncCallCount => Volatile.Read(ref _syncCallCount);

        public void Release() => _release.TrySetResult(true);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requestCount) == 1)
                return CreateResponse(bootstrapResponse);

            var syncCall = Interlocked.Increment(ref _syncCallCount) - 1;
            SyncStarted.TrySetResult(true);
            if (syncCall < transientFailures)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

            if (blockSync)
            {
                try
                {
                    await _release.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    SyncCanceled.TrySetResult(true);
                    throw;
                }
            }

            SyncCompleted.TrySetResult(true);
            return CreateResponse(syncResponse);
        }

        private static HttpResponseMessage CreateResponse(byte[] payload)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/protobuf");
            return response;
        }
    }

    private sealed class DeterministicCloudReplicationHandler(string sourcePath, int replicasPerWave) : HttpMessageHandler
    {
        private readonly object _sourceGate = new();
        private int _bootstrapPullCount;
        private int _pushCallCount;
        private int _pullCallCount;

        public TaskCompletionSource<bool> TwoPushesArrived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PushCallCount => Volatile.Read(ref _pushCallCount);

        public int PullCallCount => Volatile.Read(ref _pullCallCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/pull-updates", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _pullCallCount);
                var bootstrap = Interlocked.Increment(ref _bootstrapPullCount) <= replicasPerWave;
                if (!bootstrap)
                    return CreateProtobufResponse(CreatePullResponse("revision-0", [], declaredPages: 1));

                byte[] image;
                lock (_sourceGate)
                    image = File.ReadAllBytes(sourcePath);
                return CreateProtobufResponse(CreatePullResponse("revision-0", image));
            }

            using var document = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var steps = document.RootElement.GetProperty("requests")[0].GetProperty("batch").GetProperty("steps");
            lock (_sourceGate)
            {
                foreach (var step in steps.EnumerateArray())
                {
                    if (!step.TryGetProperty("stmt", out var statement)
                        || !statement.TryGetProperty("sql", out var sqlElement))
                    {
                        continue;
                    }

                    var sql = sqlElement.GetString();
                    if (sql is not null
                        && sql.StartsWith("INSERT INTO journal_events VALUES", StringComparison.Ordinal))
                    {
                        using var connection = new AhtolaConnection($"Data Source={sourcePath};Local Provider=Managed");
                        connection.Open();
                        connection.ExecuteNonQuery(sql);
                    }
                }
            }

            var call = Interlocked.Increment(ref _pushCallCount);
            if (call == replicasPerWave - 1)
                TwoPushesArrived.TrySetResult(true);
            return ReplicaPushHandler.SuccessfulBatchResponse(steps.GetArrayLength());
        }

        private static HttpResponseMessage CreateProtobufResponse(byte[] payload)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/protobuf");
            return response;
        }
    }

    private sealed class ReplicaPushHandler(
        IEnumerable<byte[]> pullResponses,
        Func<HttpRequestMessage, HttpResponseMessage> pushResponse) : HttpMessageHandler
    {
        private readonly Queue<byte[]> _pullResponses = new(pullResponses);

        public int PullCallCount { get; private set; }

        public int PushCallCount { get; private set; }

        public TaskCompletionSource<bool> PushStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/pull-updates", StringComparison.Ordinal))
            {
                PullCallCount++;
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(_pullResponses.Dequeue()),
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/protobuf");
                return Task.FromResult(response);
            }

            PushCallCount++;
            PushStarted.TrySetResult(true);
            return Task.FromResult(pushResponse(request));
        }

        public static HttpResponseMessage SuccessfulBatchResponse(int stepCount)
            => BatchResponse(stepCount, errorStep: null, message: null, code: null);

        public static HttpResponseMessage BatchErrorResponse(int stepCount, int errorStep, string message, string code)
            => BatchResponse(stepCount, errorStep, message, code);

        private static HttpResponseMessage BatchResponse(int stepCount, int? errorStep, string? message, string? code)
        {
            var results = string.Join(",", Enumerable.Range(0, stepCount)
                .Select(index => index == errorStep ? "null" : "{}"));
            var errors = string.Join(",", Enumerable.Range(0, stepCount)
                .Select(index => index == errorStep
                    ? $"{{\"message\":\"{message}\",\"code\":\"{code}\"}}"
                    : "null"));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"results\":[{{\"type\":\"ok\",\"response\":{{\"type\":\"batch\",\"result\":{{\"step_results\":[{results}],\"step_errors\":[{errors}]}}}}}}]}}",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
