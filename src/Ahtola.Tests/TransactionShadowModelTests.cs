using AwesomeAssertions;
using Ahtola.Data.Sqlite;
using Ahtola.Tests.Oracle;

namespace Ahtola.Tests;

/// <summary>
/// Single-threaded deterministic scheduling modeled after Turso's
/// <c>tests/integration/fuzz_transaction/ShadowDb</c>.
/// </summary>
public sealed class TransactionShadowModelTests
{
    private const int ActorCount = 3;
    private const int RandomOperationCount = 72;

    [TestCase(31)]
    [TestCase(0x71ce)]
    public void MultiConnectionReplayMatchesTransactionShadowAfterEveryObservation(int defaultSeed)
    {
        var stream = StableTestSeed.Create((ulong)defaultSeed)
            .Derive($"transaction-shadow-{defaultSeed:x}");
        var trace = ReplayTrace.Create(TestContext.CurrentContext.Test.Name, stream);
        var path = DatabasePath(defaultSeed);

        try
        {
            OracleFailureArtifacts.Run(trace, () =>
            {
                using var runner = new ShadowRunner(path, ActorCount, trace);
                runner.Initialize();
                runner.RunRequiredCoverage();
                runner.RunGenerated(stream.Random, RandomOperationCount);
                runner.RollBackActiveTransactions();
                runner.AssertAllVisible();
            });
        }
        finally
        {
            DeleteDatabaseFiles(path);
        }
    }

    private static string DatabasePath(int seed)
    {
        var directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "model-testing");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"transaction-shadow-{seed:x}-{Guid.NewGuid():N}.db");
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

    private enum ShadowAction
    {
        Begin,
        Commit,
        Rollback,
        Insert,
        Update,
        Delete,
        Select,
        Savepoint,
        RollbackTo,
        Release,
        Checkpoint,
        Reopen,
    }

    private sealed record ShadowCommand(
        int Actor,
        ShadowAction Action,
        int Key = 0,
        int Value = 0,
        string? Savepoint = null);

    private sealed class ShadowRunner : IDisposable
    {
        private readonly string _path;
        private readonly int _actorCount;
        private readonly ReplayTrace _trace;
        private readonly TransactionShadow _model;
        private List<SqliteConnection> _connections = [];
        private int _savepointNumber;

        internal ShadowRunner(string path, int actorCount, ReplayTrace trace)
        {
            _path = path;
            _actorCount = actorCount;
            _trace = trace;
            _model = new TransactionShadow(actorCount);
        }

        internal void Initialize()
        {
            _trace.Add(
                "CREATE TABLE model_rows(id INTEGER PRIMARY KEY, value INTEGER NOT NULL);",
                comparison: "shadow setup",
                actor: 0,
                action: "setup");
            using (var setup = OpenConnection())
            {
                ExpectSuccess(
                    TypedSqliteOracle.Execute(
                        setup,
                        "CREATE TABLE model_rows(id INTEGER PRIMARY KEY, value INTEGER NOT NULL);"),
                    "setup");
            }

            OpenActors();
            AssertAllVisible();
        }

        internal void RunRequiredCoverage()
        {
            Execute(new ShadowCommand(0, ShadowAction.Begin));
            Execute(new ShadowCommand(0, ShadowAction.Insert, 1, 10));
            Execute(new ShadowCommand(0, ShadowAction.Savepoint, Savepoint: "required"));
            Execute(new ShadowCommand(0, ShadowAction.Update, 1, 99));
            Execute(new ShadowCommand(0, ShadowAction.RollbackTo, Savepoint: "required"));
            Execute(new ShadowCommand(0, ShadowAction.Release, Savepoint: "required"));
            Execute(new ShadowCommand(1, ShadowAction.Select));
            Execute(new ShadowCommand(0, ShadowAction.Commit));
            Execute(new ShadowCommand(0, ShadowAction.Insert, 1, 77)); // deterministic constraint error
            Execute(new ShadowCommand(1, ShadowAction.Begin));
            Execute(new ShadowCommand(1, ShadowAction.Select));
            Execute(new ShadowCommand(0, ShadowAction.Insert, 2, 20));
            Execute(new ShadowCommand(1, ShadowAction.Select));
            Execute(new ShadowCommand(1, ShadowAction.Commit));
            Execute(new ShadowCommand(2, ShadowAction.Commit)); // deterministic invalid control
            Execute(new ShadowCommand(0, ShadowAction.Checkpoint));
            Execute(new ShadowCommand(2, ShadowAction.Reopen));
        }

        internal void RunGenerated(StablePrng random, int operationCount)
        {
            for (var step = 0; step < operationCount; step++)
            {
                var actor = random.NextInt32(_actorCount);
                Execute(Generate(actor, step, random));
            }
        }

        internal void RollBackActiveTransactions()
        {
            for (var actor = 0; actor < _actorCount; actor++)
            {
                if (_model.IsActive(actor))
                    Execute(new ShadowCommand(actor, ShadowAction.Rollback));
            }
        }

        internal void AssertAllVisible()
        {
            for (var actor = 0; actor < _actorCount; actor++)
            {
                var expectedRows = _model.VisibleRows(actor)
                    .Select(static pair => new OracleRow(
                        [OracleValue.Integer(pair.Key), OracleValue.Integer(pair.Value)]))
                    .ToArray();
                var dependencies = _model.Dependencies(actor);
                const string sql = "SELECT id, value FROM model_rows ORDER BY id;";
                _trace.Add(
                    sql,
                    comparison: "transaction shadow visible rows",
                    actor: actor,
                    action: "observe",
                    dependencies: dependencies);
                var actual = TypedSqliteOracle.Execute(_connections[actor], sql);
                var expected = OracleExecutionResult.Success(true, ["id", "value"], expectedRows);
                TypedSqliteOracle.AssertEquivalent(
                    actual,
                    expected,
                    ordered: true,
                    $"{_trace.SeedDiagnostics}; actor={actor}; operation={_trace.Operations.Count - 1}");
            }
        }

        public void Dispose()
        {
            DisposeActors();
        }

        private ShadowCommand Generate(int actor, int step, StablePrng random)
        {
            if (step > 0 && step % 29 == 0)
                return new ShadowCommand(actor, ShadowAction.Reopen);
            if (_model.AllInactive && step > 0 && step % 17 == 0)
                return new ShadowCommand(actor, ShadowAction.Checkpoint);

            var choice = random.NextInt32(100);
            if (_model.IsActive(actor))
            {
                if (choice < 8)
                    return new ShadowCommand(actor, ShadowAction.Begin);
                if (choice < 20)
                    return new ShadowCommand(actor, ShadowAction.Commit);
                if (choice < 30)
                    return new ShadowCommand(actor, ShadowAction.Rollback);
                if (choice < 42)
                    return new ShadowCommand(
                        actor,
                        ShadowAction.Savepoint,
                        Savepoint: $"sp_{actor}_{_savepointNumber++}");
                if (choice < 50)
                {
                    return new ShadowCommand(
                        actor,
                        ShadowAction.RollbackTo,
                        Savepoint: _model.LastSavepoint(actor) ?? "missing");
                }

                if (choice < 58)
                {
                    return new ShadowCommand(
                        actor,
                        ShadowAction.Release,
                        Savepoint: _model.LastSavepoint(actor) ?? "missing");
                }

                if (choice < 72 || !_model.CanMutate(actor))
                    return new ShadowCommand(actor, ShadowAction.Select);
                return RandomMutation(actor, random);
            }

            if (choice < 20)
                return new ShadowCommand(actor, ShadowAction.Begin);
            if (choice < 27)
                return new ShadowCommand(actor, ShadowAction.Commit);
            if (choice < 34)
                return new ShadowCommand(actor, ShadowAction.Rollback);
            if (choice < 40)
                return new ShadowCommand(actor, ShadowAction.Release, Savepoint: "missing");
            if (choice < 53)
                return new ShadowCommand(actor, ShadowAction.Select);
            if (choice < 58 && _model.AllInactive)
                return new ShadowCommand(actor, ShadowAction.Checkpoint);
            if (choice < 63)
                return new ShadowCommand(actor, ShadowAction.Reopen);
            if (!_model.CanMutate(actor))
                return new ShadowCommand(actor, ShadowAction.Select);
            return RandomMutation(actor, random);
        }

        private static ShadowCommand RandomMutation(int actor, StablePrng random)
        {
            var key = random.NextInt32(1, 13);
            var value = random.NextInt32(-50, 51);
            return random.NextInt32(3) switch
            {
                0 => new ShadowCommand(actor, ShadowAction.Insert, key, value),
                1 => new ShadowCommand(actor, ShadowAction.Update, key, value),
                _ => new ShadowCommand(actor, ShadowAction.Delete, key),
            };
        }

        private void Execute(ShadowCommand command)
        {
            var dependencies = _model.Dependencies(command.Actor);
            switch (command.Action)
            {
                case ShadowAction.Reopen:
                    Trace("-- close and reopen every actor connection", command, dependencies);
                    DisposeActors();
                    _model.RollBackAll();
                    OpenActors();
                    break;

                case ShadowAction.Begin:
                    {
                        var expectedSuccess = !_model.IsActive(command.Actor);
                        var operationIndex = Trace("BEGIN;", command, dependencies);
                        ExpectOutcome(ExecuteSql(command.Actor, "BEGIN;"), expectedSuccess, command);
                        if (expectedSuccess)
                            _model.Begin(command.Actor, operationIndex);
                        break;
                    }

                case ShadowAction.Commit:
                    {
                        var expectedSuccess = _model.IsActive(command.Actor);
                        Trace("COMMIT;", command, dependencies);
                        ExpectOutcome(ExecuteSql(command.Actor, "COMMIT;"), expectedSuccess, command);
                        if (expectedSuccess)
                            _model.Commit(command.Actor);
                        break;
                    }

                case ShadowAction.Rollback:
                    {
                        var expectedSuccess = _model.IsActive(command.Actor);
                        Trace("ROLLBACK;", command, dependencies);
                        ExpectOutcome(ExecuteSql(command.Actor, "ROLLBACK;"), expectedSuccess, command);
                        if (expectedSuccess)
                            _model.Rollback(command.Actor);
                        break;
                    }

                case ShadowAction.Insert:
                    {
                        _model.EnsureCanMutate(command.Actor);
                        var expectedSuccess = !_model.Contains(command.Actor, command.Key);
                        var sql = $"INSERT INTO model_rows VALUES ({command.Key}, {command.Value});";
                        Trace(sql, command, dependencies);
                        ExpectOutcome(ExecuteSql(command.Actor, sql), expectedSuccess, command);
                        if (expectedSuccess)
                            _model.Insert(command.Actor, command.Key, command.Value);
                        else
                            _model.MarkFailedMutation(command.Actor);
                        break;
                    }

                case ShadowAction.Update:
                    {
                        _model.EnsureCanMutate(command.Actor);
                        var sql = $"UPDATE model_rows SET value = {command.Value} WHERE id = {command.Key};";
                        Trace(sql, command, dependencies);
                        ExpectSuccess(ExecuteSql(command.Actor, sql), command.ToString());
                        _model.Update(command.Actor, command.Key, command.Value);
                        break;
                    }

                case ShadowAction.Delete:
                    {
                        _model.EnsureCanMutate(command.Actor);
                        var sql = $"DELETE FROM model_rows WHERE id = {command.Key};";
                        Trace(sql, command, dependencies);
                        ExpectSuccess(ExecuteSql(command.Actor, sql), command.ToString());
                        _model.Delete(command.Actor, command.Key);
                        break;
                    }

                case ShadowAction.Select:
                    Trace("SELECT id, value FROM model_rows ORDER BY id;", command, dependencies);
                    ExpectSuccess(
                        ExecuteSql(command.Actor, "SELECT id, value FROM model_rows ORDER BY id;"),
                        command.ToString());
                    break;

                case ShadowAction.Savepoint:
                    {
                        var expectedSuccess = _model.IsActive(command.Actor);
                        var sql = $"SAVEPOINT \"{command.Savepoint}\";";
                        var operationIndex = Trace(sql, command, dependencies);
                        ExpectOutcome(ExecuteSql(command.Actor, sql), expectedSuccess, command);
                        if (expectedSuccess)
                            _model.Savepoint(command.Actor, command.Savepoint!, operationIndex);
                        break;
                    }

                case ShadowAction.RollbackTo:
                    {
                        var expectedSuccess = _model.HasSavepoint(command.Actor, command.Savepoint!);
                        var sql = $"ROLLBACK TO \"{command.Savepoint}\";";
                        Trace(sql, command, dependencies);
                        ExpectOutcome(ExecuteSql(command.Actor, sql), expectedSuccess, command);
                        if (expectedSuccess)
                            _model.RollbackTo(command.Actor, command.Savepoint!);
                        break;
                    }

                case ShadowAction.Release:
                    {
                        var expectedSuccess = _model.HasSavepoint(command.Actor, command.Savepoint!);
                        var sql = $"RELEASE \"{command.Savepoint}\";";
                        Trace(sql, command, dependencies);
                        ExpectOutcome(ExecuteSql(command.Actor, sql), expectedSuccess, command);
                        if (expectedSuccess)
                            _model.Release(command.Actor, command.Savepoint!);
                        break;
                    }

                case ShadowAction.Checkpoint:
                    _model.AllInactive.Should().BeTrue(because: "generated checkpoints are quiescent");
                    Trace("PRAGMA wal_checkpoint(PASSIVE);", command, dependencies);
                    ExpectSuccess(
                        ExecuteSql(command.Actor, "PRAGMA wal_checkpoint(PASSIVE);"),
                        command.ToString());
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(command));
            }

            AssertAllVisible();
        }

        private int Trace(string sql, ShadowCommand command, IReadOnlyList<int> dependencies)
        {
            var index = _trace.Operations.Count;
            _trace.Add(
                sql,
                comparison: "transaction shadow operation",
                actor: command.Actor,
                action: command.Action.ToString(),
                dependencies: dependencies);
            return index;
        }

        private OracleExecutionResult ExecuteSql(int actor, string sql)
            => TypedSqliteOracle.Execute(_connections[actor], sql);

        private void OpenActors()
        {
            for (var actor = 0; actor < _actorCount; actor++)
                _connections.Add(OpenConnection());
        }

        private SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection(
                $"Data Source={_path};Local Provider=Managed;Pooling=False;Default Timeout=1");
            connection.Open();
            return connection;
        }

        private void DisposeActors()
        {
            foreach (var connection in _connections)
                connection.Dispose();
            _connections.Clear();
        }

        private void ExpectOutcome(
            OracleExecutionResult result,
            bool expectedSuccess,
            ShadowCommand command)
        {
            if (expectedSuccess)
            {
                ExpectSuccess(result, command.ToString());
                return;
            }

            if (result.Kind != OracleExecutionKind.Error)
            {
                throw new AssertionException(
                    $"Expected modeled invalid operation to fail: {command}.{Environment.NewLine}"
                    + $"{_trace.SeedDiagnostics}{Environment.NewLine}{_trace.ToSql()}");
            }
        }

        private void ExpectSuccess(OracleExecutionResult result, string operation)
        {
            if (result.Kind != OracleExecutionKind.Success)
            {
                throw new AssertionException(
                    $"Expected modeled operation to succeed: {operation}; "
                    + $"error={result.Error?.Category}/{result.Error?.SqliteErrorCode}: {result.Error?.Message}"
                    + $"{Environment.NewLine}{_trace.SeedDiagnostics}{Environment.NewLine}{_trace.ToSql()}");
            }
        }
    }

    private sealed class TransactionShadow
    {
        private readonly ActorState[] _actors;
        private SortedDictionary<int, int> _committed = [];
        private int _committedVersion;

        internal TransactionShadow(int actorCount)
        {
            _actors = Enumerable.Range(0, actorCount).Select(static _ => new ActorState()).ToArray();
        }

        internal bool AllInactive => _actors.All(static actor => !actor.Active);

        internal bool IsActive(int actor) => _actors[actor].Active;

        internal bool CanMutate(int actor)
        {
            var state = _actors[actor];
            return !_actors.Where((_, index) => index != actor).Any(static other => other.HoldsWriteLease)
                && (!state.Active || state.SnapshotVersion == _committedVersion);
        }

        internal void EnsureCanMutate(int actor)
        {
            if (!CanMutate(actor))
                throw new InvalidOperationException($"Actor {actor} was scheduled for an invalid write-lock transition.");
        }

        internal IReadOnlyList<int> Dependencies(int actor)
        {
            var dependencies = new List<int> { 0 };
            var state = _actors[actor];
            if (state.Active)
                dependencies.Add(state.BeginOperation);
            if (state.Savepoints.Count > 0)
                dependencies.Add(state.Savepoints[^1].Operation);
            return dependencies;
        }

        internal void Begin(int actor, int operation)
        {
            var state = _actors[actor];
            state.Active = true;
            state.Snapshot = null;
            state.WorkingView = null;
            state.SnapshotVersion = -1;
            state.HoldsWriteLease = false;
            state.PendingChanges.Clear();
            state.BeginOperation = operation;
            state.Savepoints.Clear();
        }

        internal void Commit(int actor)
        {
            var state = _actors[actor];
            if (state.PendingChanges.Count > 0)
            {
                if (state.SnapshotVersion != _committedVersion)
                    throw new InvalidOperationException("The shadow scheduler allowed a stale writer to commit.");
                foreach (var change in state.PendingChanges)
                    change.Apply(_committed);
                _committedVersion++;
            }

            Reset(state);
        }

        internal void Rollback(int actor) => Reset(_actors[actor]);

        internal void RollBackAll()
        {
            foreach (var actor in _actors)
                Reset(actor);
        }

        internal IEnumerable<KeyValuePair<int, int>> VisibleRows(int actor)
        {
            EnsureSnapshot(actor);
            var state = _actors[actor];
            return state.Active ? state.WorkingView!.ToArray() : _committed.ToArray();
        }

        internal bool Contains(int actor, int key)
        {
            EnsureSnapshot(actor);
            return WorkingView(actor).ContainsKey(key);
        }

        internal void Insert(int actor, int key, int value)
        {
            EnsureSnapshot(actor);
            WorkingView(actor).Add(key, value);
            PublishOrPend(actor, new PendingMutation(ShadowAction.Insert, key, value), changed: true);
        }

        internal void Update(int actor, int key, int value)
        {
            EnsureSnapshot(actor);
            var view = WorkingView(actor);
            var changed = view.ContainsKey(key);
            if (changed)
                view[key] = value;
            PublishOrPend(actor, new PendingMutation(ShadowAction.Update, key, value), changed);
        }

        internal void Delete(int actor, int key)
        {
            EnsureSnapshot(actor);
            var changed = WorkingView(actor).Remove(key);
            PublishOrPend(actor, new PendingMutation(ShadowAction.Delete, key, 0), changed);
        }

        internal void MarkFailedMutation(int actor)
        {
            if (_actors[actor].Active)
                _actors[actor].HoldsWriteLease = true;
        }

        internal void Savepoint(int actor, string name, int operation)
        {
            EnsureSnapshot(actor);
            var state = _actors[actor];
            state.Savepoints.Add(
                new SavepointFrame(name, Clone(state.WorkingView!), state.PendingChanges.Count, operation));
        }

        internal bool HasSavepoint(int actor, string name)
            => _actors[actor].Active
                && _actors[actor].Savepoints.Exists(
                    savepoint => string.Equals(savepoint.Name, name, StringComparison.OrdinalIgnoreCase));

        internal string? LastSavepoint(int actor)
            => _actors[actor].Savepoints.LastOrDefault()?.Name;

        internal void RollbackTo(int actor, string name)
        {
            var state = _actors[actor];
            var index = FindSavepoint(state, name);
            var frame = state.Savepoints[index];
            state.WorkingView = Clone(frame.WorkingView);
            state.PendingChanges.RemoveRange(
                frame.PendingChangeCount,
                state.PendingChanges.Count - frame.PendingChangeCount);
            state.Savepoints.RemoveRange(index + 1, state.Savepoints.Count - index - 1);
        }

        internal void Release(int actor, string name)
        {
            var state = _actors[actor];
            var index = FindSavepoint(state, name);
            state.Savepoints.RemoveRange(index, state.Savepoints.Count - index);
        }

        private static int FindSavepoint(ActorState state, string name)
        {
            for (var index = state.Savepoints.Count - 1; index >= 0; index--)
            {
                if (string.Equals(state.Savepoints[index].Name, name, StringComparison.OrdinalIgnoreCase))
                    return index;
            }

            throw new InvalidOperationException($"Savepoint '{name}' does not exist in the shadow state.");
        }

        private void EnsureSnapshot(int actor)
        {
            var state = _actors[actor];
            if (!state.Active || state.WorkingView is not null)
                return;
            state.Snapshot = Clone(_committed);
            state.WorkingView = Clone(state.Snapshot);
            state.SnapshotVersion = _committedVersion;
        }

        private SortedDictionary<int, int> WorkingView(int actor)
            => _actors[actor].Active ? _actors[actor].WorkingView! : _committed;

        private void PublishOrPend(int actor, PendingMutation mutation, bool changed)
        {
            var state = _actors[actor];
            if (state.Active)
            {
                state.HoldsWriteLease = true;
                state.PendingChanges.Add(mutation);
                return;
            }

            if (changed)
                _committedVersion++;
        }

        private static SortedDictionary<int, int> Clone(IReadOnlyDictionary<int, int> rows)
        {
            var clone = new SortedDictionary<int, int>();
            foreach (var row in rows)
                clone.Add(row.Key, row.Value);
            return clone;
        }

        private static void Reset(ActorState state)
        {
            state.Active = false;
            state.Snapshot = null;
            state.WorkingView = null;
            state.SnapshotVersion = -1;
            state.HoldsWriteLease = false;
            state.PendingChanges.Clear();
            state.BeginOperation = -1;
            state.Savepoints.Clear();
        }

        private sealed class ActorState
        {
            internal bool Active;
            internal SortedDictionary<int, int>? Snapshot;
            internal SortedDictionary<int, int>? WorkingView;
            internal int SnapshotVersion = -1;
            internal bool HoldsWriteLease;
            internal int BeginOperation = -1;
            internal List<PendingMutation> PendingChanges { get; } = [];
            internal List<SavepointFrame> Savepoints { get; } = [];
        }

        private sealed record SavepointFrame(
            string Name,
            SortedDictionary<int, int> WorkingView,
            int PendingChangeCount,
            int Operation);

        private sealed record PendingMutation(ShadowAction Action, int Key, int Value)
        {
            internal void Apply(IDictionary<int, int> rows)
            {
                switch (Action)
                {
                    case ShadowAction.Insert:
                        rows.Add(Key, Value);
                        break;
                    case ShadowAction.Update:
                        if (rows.ContainsKey(Key))
                            rows[Key] = Value;
                        break;
                    case ShadowAction.Delete:
                        rows.Remove(Key);
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported pending mutation {Action}.");
                }
            }
        }
    }
}
