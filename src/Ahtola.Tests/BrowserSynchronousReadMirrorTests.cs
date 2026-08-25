#pragma warning disable CA1416

using System.Data;
using Ahtola.Core;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite;
using Ahtola.Data.Sqlite.Browser;
using Ahtola.Data.Sqlite.Browser.Storage;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Covers the opt-in browser synchronous read-mirror profile: which statements
/// may run synchronously, that they never reach the persistent store, and that
/// everything able to mutate the database still goes through the asynchronous,
/// durable path.
/// </summary>
public sealed class BrowserSynchronousReadMirrorTests
{
    [Test]
    public async Task DefaultAsyncOnlyModeStillRejectsSynchronousReads()
    {
        await using var factory = await BrowserMirrorFactory.CreateAsync(supportsSynchronousReads: false);
        var connection = factory.CreateSqliteConnection();
        await connection.OpenAsync();
        await SeedAsync(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM probe WHERE id = 1";

        command.Invoking(static value => value.ExecuteScalar())
            .Should().Throw<PlatformNotSupportedException>()
            .WithMessage("*ReadOnlyMirror*");
        connection.Invoking(static value => value.Close())
            .Should().Throw<PlatformNotSupportedException>()
            .WithMessage("*CloseAsync*");

        await connection.DisposeAsync();
    }

    [Test]
    public async Task SqliteConnectionExecutesProvenReadOnlyStatementsSynchronously()
    {
        await using var factory = await BrowserMirrorFactory.CreateAsync();
        var connection = factory.CreateSqliteConnection();
        await connection.OpenAsync();
        await SeedAsync(connection);

        using (var scalar = connection.CreateCommand())
        {
            scalar.CommandText = "SELECT value FROM probe WHERE id = 2";
            scalar.ExecuteScalar().Should().Be(20L);
        }

        using (var values = connection.CreateCommand())
        {
            values.CommandText = "VALUES (7)";
            values.ExecuteScalar().Should().Be(7L);
        }

        using (var cte = connection.CreateCommand())
        {
            cte.CommandText =
                "WITH ranked AS (SELECT value FROM probe ORDER BY value DESC) SELECT value FROM ranked LIMIT 1";
            cte.ExecuteScalar().Should().Be(30L);
        }

        using (var reader = connection.CreateCommand())
        {
            reader.CommandText = "SELECT id, value FROM probe ORDER BY id";
            using var rows = reader.ExecuteReader();
            var read = new List<long>();
            while (rows.Read())
                read.Add(rows.GetInt64(1));
            read.Should().Equal(10L, 20L, 30L);
        }

        connection.Close();
        connection.State.Should().Be(ConnectionState.Closed);
        connection.Dispose();
    }

    [Test]
    public async Task AhtolaConnectionExecutesProvenReadOnlyStatementsSynchronously()
    {
        await using var factory = await BrowserMirrorFactory.CreateAsync();
        var connection = factory.CreateAhtolaConnection();
        await connection.OpenAsync();
        await SeedAsync(connection);

        using (var scalar = connection.CreateCommand())
        {
            scalar.CommandText = "SELECT value FROM probe WHERE id = 3";
            scalar.ExecuteScalar().Should().Be(30L);
        }

        using (var cte = connection.CreateCommand())
        {
            cte.CommandText =
                "WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < 4) "
                + "SELECT count(*) FROM seq";
            cte.ExecuteScalar().Should().Be(4L);
        }

        using (var reader = connection.CreateCommand())
        {
            reader.CommandText = "SELECT value FROM probe ORDER BY id";
            using var rows = reader.ExecuteReader();
            rows.Read().Should().BeTrue();
            rows.GetInt64(0).Should().Be(10L);
        }

        connection.Close();
        connection.State.Should().Be(ConnectionState.Closed);
        connection.Dispose();
    }

    [TestCase("INSERT INTO probe(id, value) VALUES (4, 40)")]
    [TestCase("UPDATE probe SET value = 99 WHERE id = 1")]
    [TestCase("DELETE FROM probe WHERE id = 1")]
    [TestCase("CREATE TABLE extra(value INTEGER)")]
    [TestCase("DROP TABLE probe")]
    [TestCase("ALTER TABLE probe ADD COLUMN extra INTEGER")]
    [TestCase("PRAGMA user_version = 5")]
    [TestCase("PRAGMA journal_mode")]
    [TestCase("EXPLAIN SELECT value FROM probe")]
    [TestCase("EXPLAIN QUERY PLAN SELECT value FROM probe")]
    [TestCase("BEGIN")]
    [TestCase("COMMIT")]
    [TestCase("ROLLBACK")]
    [TestCase("SAVEPOINT s1")]
    [TestCase("ATTACH DATABASE 'other.db' AS other")]
    [TestCase("DETACH DATABASE other")]
    [TestCase("VACUUM")]
    [TestCase("SELECT value FROM probe; INSERT INTO probe(id, value) VALUES (4, 40)")]
    [TestCase("WITH cte AS (SELECT 1 AS x) INSERT INTO probe(id, value) SELECT 4, x FROM cte")]
    [TestCase("WITH cte AS (DELETE FROM probe RETURNING id) SELECT id FROM cte")]
    public async Task RejectsUnprovenSynchronousStatementsBeforeAnyMutation(string sql)
    {
        await using var factory = await BrowserMirrorFactory.CreateAsync();
        var connection = factory.CreateSqliteConnection();
        await connection.OpenAsync();
        await SeedAsync(connection);
        var operationsBefore = factory.PersistentOperations;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            command.Invoking(static value => value.ExecuteNonQuery())
                .Should().Throw<PlatformNotSupportedException>();
            command.Invoking(static value => value.ExecuteScalar())
                .Should().Throw<PlatformNotSupportedException>();
            command.Invoking(static value => value.ExecuteReader())
                .Should().Throw<PlatformNotSupportedException>();
            command.Invoking(static value => value.Prepare())
                .Should().Throw<PlatformNotSupportedException>();
        }

        factory.Mirror.HasUnflushedWork.Should().BeFalse();
        factory.PersistentOperations.Should().Be(operationsBefore);
        await using (var verify = connection.CreateCommand())
        {
            verify.CommandText = "SELECT count(*), sum(value) FROM probe";
            using var rows = verify.ExecuteReader();
            rows.Read().Should().BeTrue();
            rows.GetInt64(0).Should().Be(3);
            rows.GetInt64(1).Should().Be(60);
        }

        await connection.DisposeAsync();
    }

    [Test]
    public async Task SynchronousTransactionsRemainRejected()
    {
        await using var factory = await BrowserMirrorFactory.CreateAsync();
        await using var connection = factory.CreateSqliteConnection();
        await connection.OpenAsync();
        await SeedAsync(connection);

        connection.Invoking(static value => value.BeginTransaction())
            .Should().Throw<PlatformNotSupportedException>()
            .WithMessage("*BeginTransactionAsync*");
    }

    [Test]
    public async Task SynchronousReadObservesEarlierAsynchronousWrite()
    {
        await using var factory = await BrowserMirrorFactory.CreateAsync();
        await using var connection = factory.CreateSqliteConnection();
        await connection.OpenAsync();
        await SeedAsync(connection);

        await using (var write = connection.CreateCommand())
        {
            write.CommandText = "INSERT INTO probe(id, value) VALUES (4, 40)";
            (await write.ExecuteNonQueryAsync()).Should().Be(1);
        }

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT sum(value) FROM probe";
        read.ExecuteScalar().Should().Be(100L);
    }

    [Test]
    public async Task AsynchronousWritesStillFlushToThePersistentStore()
    {
        var store = new FakeBrowserPersistentStore();
        await using var factory = await BrowserMirrorFactory.CreateAsync(store: store);
        await using var connection = factory.CreateSqliteConnection();
        await connection.OpenAsync();
        await SeedAsync(connection);

        factory.Mirror.HasUnflushedWork.Should().BeFalse();
        factory.PersistentOperations.Should().BeGreaterThan(0);
        store.Contains(BrowserMirrorFactory.DatabasePath).Should().BeTrue();
        store.Read(BrowserMirrorFactory.DatabasePath).Length.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task ThousandSynchronousPointReadsNeverReachThePersistentStore()
    {
        await using var factory = await BrowserMirrorFactory.CreateAsync();
        await using var connection = factory.CreateSqliteConnection();
        await connection.OpenAsync();
        await SeedAsync(connection);

        // Warm the statement so preparation costs are outside the measured window.
        using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT value FROM probe WHERE id = $id";
        var parameter = probe.CreateParameter();
        parameter.ParameterName = "$id";
        probe.Parameters.Add(parameter);
        parameter.Value = 1;
        probe.ExecuteScalar().Should().Be(10L);

        var operationsBefore = factory.PersistentOperations;
        var flushPathsBefore = factory.Store.FlushPaths.Count;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long checksum = 0;
        for (var index = 0; index < 1000; index++)
        {
            parameter.Value = (index % 3) + 1;
            checksum += Convert.ToInt64(probe.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        checksum.Should().Be(19990L);
        factory.PersistentOperations.Should().Be(operationsBefore);
        factory.Store.FlushPaths.Count.Should().Be(flushPathsBefore);
        factory.Mirror.HasUnflushedWork.Should().BeFalse();

        // Zero worker crossings is the contract; this bound only rules out a
        // return to per-command asynchronous storage latency (tens of
        // milliseconds each), so it stays far away from a brittle CI threshold.
        stopwatch.Stop();
        stopwatch.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(8),
            "1000 mirror-served point reads must not cost per-command storage latency");
    }

    [Test]
    public async Task FlushCompletesSynchronouslyWhenNothingIsPending()
    {
        await using var factory = await BrowserMirrorFactory.CreateAsync();
        await using var connection = factory.CreateSqliteConnection();
        await connection.OpenAsync();
        await SeedAsync(connection);
        var operationsBefore = factory.PersistentOperations;

        for (var index = 0; index < 100; index++)
        {
            var flush = factory.Mirror.FlushPendingAsync();
            flush.IsCompletedSuccessfully.Should().BeTrue();
            await flush;
        }

        factory.PersistentOperations.Should().Be(operationsBefore);
    }

    [Test]
    public async Task SynchronousCloseAndDisposeSucceedWhenNothingIsPending()
    {
        await using var factory = await BrowserMirrorFactory.CreateAsync();
        var connection = factory.CreateSqliteConnection();
        await connection.OpenAsync();
        await SeedAsync(connection);

        connection.Close();

        connection.State.Should().Be(ConnectionState.Closed);
        connection.Dispose();

        var ahtola = factory.CreateAhtolaConnection();
        await ahtola.OpenAsync();
        ahtola.Close();
        ahtola.State.Should().Be(ConnectionState.Closed);
        ahtola.Dispose();
    }

    [Test]
    public async Task SynchronousCloseFailsClosedWhileAMutationIsPending()
    {
        await using var factory = await BrowserMirrorFactory.CreateAsync();
        var connection = factory.CreateSqliteConnection();
        await connection.OpenAsync();
        await SeedAsync(connection);
        factory.EnqueuePendingMutation();

        connection.Invoking(static value => value.Close())
            .Should().Throw<PlatformNotSupportedException>()
            .WithMessage("*CloseAsync*");
        connection.State.Should().Be(ConnectionState.Open);
        connection.Invoking(static value => value.Dispose())
            .Should().Throw<PlatformNotSupportedException>()
            .WithMessage("*DisposeAsync*");
        connection.State.Should().Be(ConnectionState.Open);

        await connection.DisposeAsync();

        connection.State.Should().Be(ConnectionState.Closed);
        factory.Mirror.HasUnflushedWork.Should().BeFalse();
        factory.Store.Contains(BrowserMirrorFactory.PendingPath).Should().BeTrue();
    }

    [Test]
    public async Task AsynchronousDisposalDrainsPendingOperations()
    {
        await using var factory = await BrowserMirrorFactory.CreateAsync();
        var connection = factory.CreateSqliteConnection();
        await connection.OpenAsync();
        await SeedAsync(connection);
        factory.EnqueuePendingMutation();

        await connection.DisposeAsync();

        factory.Mirror.HasUnflushedWork.Should().BeFalse();
        factory.Store.Contains(BrowserMirrorFactory.PendingPath).Should().BeTrue();
    }

    [Test]
    public async Task MultipleConnectionsShareTheMirrorForSynchronousReads()
    {
        await using var factory = await BrowserMirrorFactory.CreateAsync();
        await using var writer = factory.CreateSqliteConnection();
        await writer.OpenAsync();
        await SeedAsync(writer);

        await using var reader = factory.CreateSqliteConnection();
        await reader.OpenAsync();
        var operationsBefore = factory.PersistentOperations;

        using (var first = writer.CreateCommand())
        {
            first.CommandText = "SELECT count(*) FROM probe";
            first.ExecuteScalar().Should().Be(3L);
        }
        using (var second = reader.CreateCommand())
        {
            second.CommandText = "SELECT sum(value) FROM probe";
            second.ExecuteScalar().Should().Be(60L);
        }

        factory.PersistentOperations.Should().Be(operationsBefore);
    }

    [Test]
    public async Task EncryptedMirrorServesSynchronousReadsWithoutReachingThePersistentStore()
    {
        var store = new FakeBrowserPersistentStore();
        await using var harness = await BrowserCipherHarness.CreateAsync(
            store,
            Core.Storage.AhtolaEncryptionCipher.Aes256Gcm,
            new string('a', 64),
            BrowserMirrorFactory.Root);
        await using var factory = BrowserMirrorFactory.FromMirror(store, harness.Mirror);
        await using var connection = factory.CreateSqliteConnection();
        await connection.OpenAsync();
        await SeedAsync(connection);

        // The encrypted image on the persistent store must not be plaintext.
        var persisted = store.Read(BrowserMirrorFactory.DatabasePath);
        persisted.Length.Should().BeGreaterThan(0);
        System.Text.Encoding.ASCII.GetString(persisted, 0, 5).Should().Be("AHTLA");

        var operationsBefore = factory.PersistentOperations;
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT sum(value) FROM probe";
        for (var index = 0; index < 50; index++)
            read.ExecuteScalar().Should().Be(60L);

        factory.PersistentOperations.Should().Be(operationsBefore);
    }

    [Test]
    public async Task RegisteredScalarFunctionsRunInsideSynchronousReadsAndKeepTheirExceptions()
    {
        await using var factory = await BrowserMirrorFactory.CreateAsync();
        await using var connection = factory.CreateSqliteConnection();
        await connection.OpenAsync();
        await SeedAsync(connection);
        var invocations = 0;
        connection.CreateFunction<long, long>(
            "triple",
            value =>
            {
                invocations++;
                return value == 30 ? throw new InvalidOperationException("boom") : value * 3;
            },
            isDeterministic: true);
        var operationsBefore = factory.PersistentOperations;

        using (var ok = connection.CreateCommand())
        {
            ok.CommandText = "SELECT triple(value) FROM probe WHERE id = 1";
            ok.ExecuteScalar().Should().Be(30L);
        }

        using (var boom = connection.CreateCommand())
        {
            boom.CommandText = "SELECT triple(value) FROM probe WHERE id = 3";
            boom.Invoking(static value => value.ExecuteScalar())
                .Should().Throw<Exception>()
                .Where(exception => Flatten(exception).Contains("boom", StringComparison.Ordinal));
        }

        invocations.Should().BeGreaterThanOrEqualTo(2);
        factory.PersistentOperations.Should().Be(operationsBefore);
        factory.Mirror.HasUnflushedWork.Should().BeFalse();
    }

    private static string Flatten(Exception exception)
    {
        var text = exception.Message;
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
            text += " | " + inner.Message;
        return text;
    }
    /// <summary>
    /// The exploit the immutable capture closes: prepare/execute a write asynchronously, keep the
    /// reader, then point the command at a proven read-only statement. If authorization were
    /// re-derived from <c>CommandText</c> the reader would happily step the *write* synchronously,
    /// crossing the OPFS boundary from a synchronous call.
    /// </summary>
    [Test]
    public async Task ChangingCommandTextCannotAuthorizeASynchronousReaderOverAWrite()
    {
        await using var factory = await BrowserMirrorFactory.CreateAsync();
        await using var connection = factory.CreateSqliteConnection();
        await connection.OpenAsync();
        await SeedAsync(connection);

        await using var write = connection.CreateCommand();
        write.CommandText = "INSERT INTO probe(id, value) VALUES (4, 40) RETURNING id";
        var reader = await write.ExecuteReaderAsync();

        // Microsoft.Data.Sqlite refuses this outright, and so does the facade.
        write.Invoking(static value => value.CommandText = "SELECT 1")
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*reader is open*");

        reader.Invoking(static value => value.Read())
            .Should().Throw<PlatformNotSupportedException>()
            .WithMessage("*ReadAsync*");
        reader.Invoking(static value => value.Close())
            .Should().Throw<PlatformNotSupportedException>()
            .WithMessage("*ReadAsync*");
        reader.Invoking(static value => value.Dispose())
            .Should().Throw<PlatformNotSupportedException>()
            .WithMessage("*DisposeAsync*");

        await reader.DisposeAsync();
    }

    /// <summary>
    /// The same exploit through the <see cref="global::Ahtola.AhtolaConnection"/> facade, which by
    /// design lets <c>CommandText</c> change while a reader is open (see
    /// <c>ReaderSnapshotsTransactionCompletionBeforeCommandMutation</c>). Its safety therefore
    /// rests entirely on the reader's immutable capture: pointing the command at a proven
    /// read-only statement must not retroactively authorize the open write reader.
    /// </summary>
    [Test]
    public async Task ChangingAhtolaCommandTextCannotAuthorizeASynchronousReaderOverAWrite()
    {
        await using var factory = await BrowserMirrorFactory.CreateAsync();
        await using var connection = factory.CreateAhtolaConnection();
        await connection.OpenAsync();
        await SeedAsync(connection);

        await using var write = (global::Ahtola.AhtolaCommand)connection.CreateCommand();
        write.CommandText = "INSERT INTO probe(id, value) VALUES (5, 50) RETURNING id";
        var reader = await write.ExecuteReaderAsync();

        // The swap the exploit relies on: it is allowed, and it must change nothing.
        write.CommandText = "SELECT 1";
        write.AllowsSynchronousExecution.Should().BeTrue("the command's new text is provably read-only");

        reader.Invoking(static value => value.Read())
            .Should().Throw<PlatformNotSupportedException>()
            .WithMessage("*ReadAsync*");
        reader.Invoking(static value => value.NextResult())
            .Should().Throw<PlatformNotSupportedException>();
        reader.Invoking(static value => value.Close())
            .Should().Throw<PlatformNotSupportedException>();
        reader.Invoking(static value => value.Dispose())
            .Should().Throw<PlatformNotSupportedException>()
            .WithMessage("*DisposeAsync*");

        await reader.DisposeAsync();
        factory.Mirror.HasUnflushedWork.Should().BeFalse();
    }

    /// <summary>
    /// The mirror image of the exploit: a reader opened over a proven read-only statement carries
    /// its own authorization for its whole lifetime, and the command becomes writable again — and
    /// re-classifiable — only once the reader has closed.
    /// </summary>
    [Test]
    public async Task AProvenReadOnlyReaderKeepsItsAuthorizationForItsWholeLifetime()
    {
        await using var factory = await BrowserMirrorFactory.CreateAsync();
        await using var connection = factory.CreateSqliteConnection();
        await connection.OpenAsync();
        await SeedAsync(connection);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, value FROM probe ORDER BY id";
        var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();

        command.Invoking(static value => value.CommandText = "DELETE FROM probe")
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*reader is open*");

        reader.Read().Should().BeTrue();
        reader.Read().Should().BeTrue();
        reader.Read().Should().BeFalse();
        reader.Close();
        reader.Dispose();

        // The command is writable again, and the new text is classified on its own terms.
        command.CommandText = "DELETE FROM probe";
        command.Invoking(static value => value.ExecuteNonQuery())
            .Should().Throw<PlatformNotSupportedException>();

        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT count(*) FROM probe";
        verify.ExecuteScalar().Should().Be(3L);
    }

    /// <summary>
    /// A batch whose commands are all proven read-only is served entirely from the managed mirror,
    /// so it may be iterated, closed and disposed synchronously.
    /// </summary>
    [Test]
    public async Task ReadOnlyBatchesSupportSynchronousIterationAndTeardown()
    {
        await using var factory = await BrowserMirrorFactory.CreateAsync();
        await using var connection = factory.CreateSqliteConnection();
        await connection.OpenAsync();
        await SeedAsync(connection);
        var operationsBefore = factory.PersistentOperations;

        using var batch = connection.CreateBatch();
        batch.BatchCommands.Add(new SqliteBatchCommand("SELECT count(*) FROM probe"));
        batch.BatchCommands.Add(new SqliteBatchCommand("SELECT sum(value) FROM probe"));

        using var reader = batch.ExecuteReader();
        reader.Read().Should().BeTrue();
        reader.GetInt64(0).Should().Be(3);
        reader.NextResult().Should().BeTrue();
        reader.Read().Should().BeTrue();
        reader.GetInt64(0).Should().Be(60);
        reader.NextResult().Should().BeFalse();

        reader.Close();
        reader.Dispose();

        factory.PersistentOperations.Should().Be(operationsBefore);
        factory.Mirror.HasUnflushedWork.Should().BeFalse();
    }

    /// <summary>
    /// A batch containing even one unproven command must fail closed before a single step, and
    /// must stay refused for synchronous close and disposal too.
    /// </summary>
    [Test]
    public async Task MixedBatchesStayRejectedBeforeAnyStep()
    {
        await using var factory = await BrowserMirrorFactory.CreateAsync();
        await using var connection = factory.CreateSqliteConnection();
        await connection.OpenAsync();
        await SeedAsync(connection);
        var operationsBefore = factory.PersistentOperations;

        await using var batch = connection.CreateBatch();
        batch.BatchCommands.Add(new SqliteBatchCommand("SELECT count(*) FROM probe"));
        batch.BatchCommands.Add(new SqliteBatchCommand("INSERT INTO probe(id, value) VALUES (7, 70)"));

        batch.Invoking(static value => value.ExecuteReader())
            .Should().Throw<PlatformNotSupportedException>();
        batch.Invoking(static value => value.ExecuteNonQuery())
            .Should().Throw<PlatformNotSupportedException>();

        factory.PersistentOperations.Should().Be(operationsBefore);
        factory.Mirror.HasUnflushedWork.Should().BeFalse();

        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT count(*) FROM probe";
        verify.ExecuteScalar().Should().Be(3L);
    }

    /// <summary>
    /// A batch that is asynchronously executed but whose commands are not all proven read-only
    /// must still refuse synchronous iteration and teardown of its reader.
    /// </summary>
    [Test]
    public async Task UnprovenBatchReadersRefuseSynchronousIterationAndTeardown()
    {
        await using var factory = await BrowserMirrorFactory.CreateAsync();
        await using var connection = factory.CreateSqliteConnection();
        await connection.OpenAsync();
        await SeedAsync(connection);

        await using var batch = connection.CreateBatch();
        batch.BatchCommands.Add(new SqliteBatchCommand("INSERT INTO probe(id, value) VALUES (8, 80) RETURNING id"));
        batch.BatchCommands.Add(new SqliteBatchCommand("SELECT count(*) FROM probe"));

        var reader = await batch.ExecuteReaderAsync();
        reader.Invoking(static value => value.Read())
            .Should().Throw<PlatformNotSupportedException>()
            .WithMessage("*proven read-only*");
        reader.Invoking(static value => value.NextResult())
            .Should().Throw<PlatformNotSupportedException>();
        reader.Invoking(static value => value.Close())
            .Should().Throw<PlatformNotSupportedException>();
        reader.Invoking(static value => value.Dispose())
            .Should().Throw<PlatformNotSupportedException>();

        await reader.DisposeAsync();
    }

    /// <summary>
    /// Synchronous authorization is proven once per execution and carried on the reader. Iterating
    /// a large result set produced from a very long statement must not re-tokenize the SQL, which
    /// would put the classifier on the per-row hot path.
    /// </summary>
    [Test]
    public async Task SynchronousAuthorizationIsClassifiedOncePerExecutionNotPerRow()
    {
        await using var factory = await BrowserMirrorFactory.CreateAsync();
        await using var connection = factory.CreateSqliteConnection();
        await connection.OpenAsync();
        await SeedAsync(connection);

        // A deliberately long statement: re-tokenizing it per row would be obvious in the counter
        // and expensive in wall time.
        var padding = string.Join(", ", Enumerable.Range(0, 400).Select(i => $"'{new string('p', 40)}{i}' AS c{i}"));
        var sql =
            "WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < 2000) "
            + $"SELECT n, {padding} FROM seq";

        using var command = connection.CreateCommand();
        command.CommandText = sql;

        var before = AhtolaReadOnlySqlClassifier.ClassificationCount;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var rows = 0;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                rows++;
        }

        stopwatch.Stop();
        var classifications = AhtolaReadOnlySqlClassifier.ClassificationCount - before;

        rows.Should().Be(2000);

        // ExecuteReader classifies the command text, and the reader captures that decision once.
        // A handful of command-level checks is expected; anything proportional to the row count is
        // the regression this guards.
        classifications.Should().BeLessThan(
            16,
            $"authorization must be captured per execution, not per row (rows={rows})");
        stopwatch.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(20),
            "re-tokenizing a multi-kilobyte statement per row would dominate the run");
    }

    /// <summary>
    /// Storage metrics must count work already handed to a running flush. Reporting only the queue
    /// says "nothing pending" for the whole duration of a flush, which is exactly when the durable
    /// store is owed the most, and contradicts the predicate synchronous teardown fails closed on.
    /// </summary>
    [Test]
    public async Task StorageMetricsCountInFlightFlushWorkAndAgreeWithTeardownPolicy()
    {
        var store = new FakeBrowserPersistentStore();
        await using var factory = await BrowserMirrorFactory.CreateAsync(store: store);
        await using var connection = factory.CreateSqliteConnection();
        await connection.OpenAsync();
        await SeedAsync(connection);

        var settled = factory.Mirror.GetMetrics();
        settled.PendingMutations.Should().Be(0);
        settled.HasUnflushedWork.Should().BeFalse();

        factory.EnqueuePendingMutation();
        var queued = factory.Mirror.GetMetrics();
        queued.QueuedMutations.Should().BeGreaterThan(0);
        queued.PendingMutations.Should().Be(queued.QueuedMutations + queued.InFlightMutations);
        queued.HasUnflushedWork.Should().BeTrue();

        // Park the store inside the flush: the queue has been claimed but the work is still owed.
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeWrite = async (_, _) =>
        {
            store.BeforeWrite = null;
            blocked.TrySetResult();
            await release.Task;
        };

        var flush = factory.Mirror.FlushPendingAsync().AsTask();
        await blocked.Task;

        var inFlight = factory.Mirror.GetMetrics();
        inFlight.QueuedMutations.Should().Be(0, "the flush has claimed the queue");
        inFlight.InFlightMutations.Should().BeGreaterThan(0);
        inFlight.PendingMutations.Should().Be(inFlight.InFlightMutations);
        inFlight.HasUnflushedWork.Should().BeTrue();
        factory.Mirror.HasUnflushedWork.Should().BeTrue(
            "PendingMutations and HasUnflushedWork must agree with the synchronous teardown policy");

        release.TrySetResult();
        await flush;

        var done = factory.Mirror.GetMetrics();
        done.PendingMutations.Should().Be(0);
        done.InFlightMutations.Should().Be(0);
        done.HasUnflushedWork.Should().BeFalse();
    }

    private static async Task SeedAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS probe(id INTEGER PRIMARY KEY, value INTEGER NOT NULL);
            INSERT INTO probe(id, value) VALUES (1, 10), (2, 20), (3, 30);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedAsync(global::Ahtola.AhtolaConnection connection)
    {
        await using (var create = connection.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE IF NOT EXISTS probe(id INTEGER PRIMARY KEY, value INTEGER NOT NULL)";
            await create.ExecuteNonQueryAsync();
        }
        await using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO probe(id, value) VALUES (1, 10), (2, 20), (3, 30)";
        await insert.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Stands in for <see cref="AhtolaBrowserDataSource"/> off-browser: the same
    /// mirrored file system and browser adapters over a fake persistent store, so
    /// the provider takes exactly the browser code paths.
    /// </summary>
    private sealed class BrowserMirrorFactory : IManagedDatabaseFactory, IAsyncDisposable
    {
        internal const string Root = "owned";
        internal const string DatabasePath = "owned/main.db";
        internal const string PendingPath = "owned/pending.bin";

        private readonly List<IManagedDatabaseAdapter> _databases = [];
        private readonly bool _ownsMirror;
        private readonly bool _supportsSynchronousReads;

        private BrowserMirrorFactory(
            FakeBrowserPersistentStore store,
            BrowserMirroredFileSystem mirror,
            bool supportsSynchronousReads,
            bool ownsMirror)
        {
            Store = store;
            Mirror = mirror;
            _supportsSynchronousReads = supportsSynchronousReads;
            _ownsMirror = ownsMirror;
        }

        internal FakeBrowserPersistentStore Store { get; }

        internal BrowserMirroredFileSystem Mirror { get; }

        internal long PersistentOperations => Mirror.PersistentOperationCount;

        internal static async ValueTask<BrowserMirrorFactory> CreateAsync(
            bool supportsSynchronousReads = true,
            FakeBrowserPersistentStore? store = null)
        {
            store ??= new FakeBrowserPersistentStore();
            var mirror = await BrowserMirroredFileSystem.CreateAsync(store, Root);
            return new BrowserMirrorFactory(store, mirror, supportsSynchronousReads, ownsMirror: true);
        }

        internal static BrowserMirrorFactory FromMirror(
            FakeBrowserPersistentStore store,
            BrowserMirroredFileSystem mirror)
            => new(store, mirror, supportsSynchronousReads: true, ownsMirror: false);

        /// <summary>
        /// Queues a mutation the mirror still owes the persistent store, which is
        /// the state synchronous teardown must refuse.
        /// </summary>
        internal void EnqueuePendingMutation()
        {
            using var file = ((IFileSystem)Mirror).OpenFile(PendingPath, FileOpenMode.CreateNew);
            file.Write(0, [1, 2, 3, 4]);
            Mirror.HasUnflushedWork.Should().BeTrue();
        }

        public string DataSource => DatabasePath;

        public bool IsReadOnly => false;

        public bool SupportsSynchronousReads => _supportsSynchronousReads;

        public bool HasPendingDurableWork => Mirror.HasUnflushedWork;

        public ValueTask<IManagedDatabaseAdapter> OpenDatabaseAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inner = ManagedDatabaseAdapter.OpenFile(DatabasePath, Mirror);
            var adapter = new BrowserManagedDatabaseAdapter(inner, Mirror, static () => { });
            _databases.Add(adapter);
            return ValueTask.FromResult<IManagedDatabaseAdapter>(adapter);
        }

        internal SqliteConnection CreateSqliteConnection()
            => new($"Data Source={DatabasePath};Local Provider=Managed;Pooling=False", this);

        internal global::Ahtola.AhtolaConnection CreateAhtolaConnection()
            => new($"Data Source={DatabasePath};Local Provider=Managed", this);

        public async ValueTask DisposeAsync()
        {
            foreach (var database in _databases)
            {
                try
                {
                    await database.DisposeAsync();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            if (_ownsMirror)
                await Mirror.DisposeAsync();
        }
    }
}
