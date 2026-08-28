using System.Globalization;
using AwesomeAssertions;
using Ahtola.Data.Sqlite;
using Ahtola.Tests.Oracle;

namespace Ahtola.Tests;

/// <summary>
/// Bounded cooperative interleavings derived from Turso's concurrent simulator,
/// stress shuttle tests, and MVCC fixed-yield tests.
/// </summary>
public sealed class ConcurrencyModelTests
{
    private const int ExhaustiveScheduleLimit = 32;
    private const int RandomScheduleCount = 4;

    [Test]
    public async Task TransactionInterleavingsPreserveCommitRollbackAndSnapshots()
    {
        var root = StableTestSeed.Create(0xc011ab1eUL);
        var runNumber = 0;
        var sawRollbackPendingRead = false;
        var sawReaderSpanCommit = false;

        var exploration = await CooperativeScheduleExplorer.ExploreDepthFirstAsync(
            prefix => RunTransactionScheduleAsync(
                root.Derive($"transaction-dfs-{runNumber++}"),
                replayChoices: prefix,
                allowReplayPrefix: true),
            ExhaustiveScheduleLimit);

        exploration.Exhaustive.Should().BeTrue(
            because: "the two bounded actor scripts have only fifteen order-preserving interleavings");
        exploration.Runs.Should().HaveCount(15);
        foreach (var run in exploration.Runs)
            RecordCoverage(run, ref sawRollbackPendingRead, ref sawReaderSpanCommit);

        for (var index = 0; index < RandomScheduleCount; index++)
        {
            var stream = root.Derive($"transaction-random-{index}");
            var run = await RunTransactionScheduleAsync(
                stream,
                choose: (_, enabled) => stream.Random.NextInt32(enabled));
            RecordCoverage(run, ref sawRollbackPendingRead, ref sawReaderSpanCommit);
        }

        sawRollbackPendingRead.Should().BeTrue(
            because: "at least one explored reader ran while the rollback transaction was pending");
        sawReaderSpanCommit.Should().BeTrue(
            because: "at least one explored snapshot straddled the writer's commit");
    }

    private static async Task<CooperativeScheduleResult> RunTransactionScheduleAsync(
        StableRandomStream stream,
        IReadOnlyList<int>? replayChoices = null,
        Func<int, int, int>? choose = null,
        bool allowReplayPrefix = false)
    {
        var path = DatabasePath(stream.Seed);
        var trace = ReplayTrace.Create(TestContext.CurrentContext.Test.Name, stream);
        try
        {
            using (var setup = Open(path))
            {
                Execute(
                    setup,
                    "CREATE TABLE schedule_rows(id INTEGER PRIMARY KEY, value INTEGER NOT NULL);"
                    + "INSERT INTO schedule_rows VALUES(1, 0);"
                    + "INSERT INTO schedule_rows VALUES(2, 0);",
                    trace,
                    actor: -1,
                    action: "setup");
            }

            using var writer = Open(path);
            using var reader = Open(path);
            var observations = new List<ReaderObservation>();
            var scheduler = new DeterministicCooperativeScheduler(
                maxSteps: 16,
                maxStepsWithoutProgress: 8);

            scheduler.AddActor("writer", async actor =>
            {
                await actor.YieldAsync("writer.before-rollback");
                Execute(writer, "BEGIN;", trace, actor.ActorId, "rollback.begin");
                Execute(
                    writer,
                    "INSERT INTO schedule_rows VALUES(99, 99);",
                    trace,
                    actor.ActorId,
                    "rollback.write");
                actor.NoteProgress();

                await actor.YieldAsync("writer.rollback-pending");
                Execute(writer, "ROLLBACK;", trace, actor.ActorId, "rollback.finish");
                Execute(writer, "BEGIN;", trace, actor.ActorId, "commit.begin");
                Execute(writer, "UPDATE schedule_rows SET value = 1;", trace, actor.ActorId, "commit.update");
                Execute(
                    writer,
                    "INSERT INTO schedule_rows VALUES(10, 10);",
                    trace,
                    actor.ActorId,
                    "commit.insert");
                actor.NoteProgress();

                await actor.YieldAsync("writer.commit-pending");
                Execute(writer, "COMMIT;", trace, actor.ActorId, "commit.finish");
                actor.NoteProgress();

                await actor.YieldAsync("writer.after-commit");
            });

            scheduler.AddActor("reader", async actor =>
            {
                await actor.YieldAsync("reader.before-snapshot");
                Execute(reader, "BEGIN;", trace, actor.ActorId, "snapshot.begin");
                var first = Scalar(reader, "SELECT value FROM schedule_rows WHERE id = 1;", trace, actor.ActorId);
                var rollbackFirst = Scalar(
                    reader,
                    "SELECT COUNT(*) FROM schedule_rows WHERE id = 99;",
                    trace,
                    actor.ActorId);
                actor.NoteProgress();

                await actor.YieldAsync("reader.between-snapshot-reads");
                var second = Scalar(reader, "SELECT value FROM schedule_rows WHERE id = 2;", trace, actor.ActorId);
                var rollbackSecond = Scalar(
                    reader,
                    "SELECT COUNT(*) FROM schedule_rows WHERE id = 99;",
                    trace,
                    actor.ActorId);
                Execute(reader, "COMMIT;", trace, actor.ActorId, "snapshot.finish");
                observations.Add(new ReaderObservation(first, second, rollbackFirst, rollbackSecond));
                actor.NoteProgress();
            });

            var result = await scheduler.RunAsync(replayChoices, choose, allowReplayPrefix);
            foreach (var step in result.Steps)
                trace.AddScheduleStep(step);

            OracleFailureArtifacts.Run(trace, () =>
            {
                result.EnsureSuccessful();
                result.Actors.Should().HaveCount(2)
                    .And.OnlyContain(static actor => actor.Completed && actor.Crash == null);
                result.Steps.Should().HaveCount(6);
                observations.Should().ContainSingle();
                observations[0].FirstValue.Should().Be(observations[0].SecondValue,
                    because: "both reads belong to one snapshot");
                observations[0].FirstValue.Should().BeOneOf(0L, 1L);
                observations[0].RollbackCountBeforeYield.Should().Be(0);
                observations[0].RollbackCountAfterYield.Should().Be(0);

                using var verify = Open(path);
                Scalar(verify, "SELECT COUNT(*) FROM schedule_rows WHERE id = 10;", trace, -1)
                    .Should().Be(1, because: "a successfully committed key cannot disappear");
                Scalar(verify, "SELECT COUNT(*) FROM schedule_rows WHERE id = 99;", trace, -1)
                    .Should().Be(0, because: "a rolled-back key must never become externally visible");
                Scalar(verify, "SELECT COUNT(*) FROM schedule_rows WHERE id IN (1, 2) AND value = 1;", trace, -1)
                    .Should().Be(2, because: "the committed multi-row update is atomic");
            });

            return result;
        }
        finally
        {
            DeleteDatabaseFiles(path);
        }
    }

    private static void RecordCoverage(
        CooperativeScheduleResult result,
        ref bool sawRollbackPendingRead,
        ref bool sawReaderSpanCommit)
    {
        result.EnsureSuccessful();
        var points = result.Steps.Select(static step => step.YieldPoint).ToArray();
        sawRollbackPendingRead |= Ordered(
            points,
            "writer.before-rollback",
            "reader.before-snapshot",
            "writer.rollback-pending");
        sawReaderSpanCommit |= Ordered(
            points,
            "reader.before-snapshot",
            "writer.commit-pending",
            "reader.between-snapshot-reads");
    }

    private static bool Ordered(string[] points, string first, string second, string third)
    {
        var firstIndex = Array.IndexOf(points, first);
        var secondIndex = Array.IndexOf(points, second);
        var thirdIndex = Array.IndexOf(points, third);
        return firstIndex < secondIndex && secondIndex < thirdIndex;
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(
            $"Data Source={path};Local Provider=Managed;Pooling=False;Default Timeout=1");
        connection.Open();
        return connection;
    }

    private static void Execute(
        SqliteConnection connection,
        string sql,
        ReplayTrace trace,
        int actor,
        string action)
    {
        trace.Add(sql, comparison: "cooperative schedule", actor: actor, action: action);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(
        SqliteConnection connection,
        string sql,
        ReplayTrace trace,
        int actor)
    {
        trace.Add(sql, comparison: "cooperative schedule", actor: actor, action: "observe");
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string DatabasePath(ulong seed)
    {
        var directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "concurrency-model");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"schedule-{seed:x16}-{Guid.NewGuid():N}.db");
    }

    private static void DeleteDatabaseFiles(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }

    private sealed record ReaderObservation(
        long FirstValue,
        long SecondValue,
        long RollbackCountBeforeYield,
        long RollbackCountAfterYield);
}
