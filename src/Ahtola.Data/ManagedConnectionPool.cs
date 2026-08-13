using Ahtola.Core;

namespace Ahtola;

internal readonly record struct ManagedConnectionPoolKey(string DataSource, bool ReadOnly)
{
    public static ManagedConnectionPoolKey Create(string dataSource, bool readOnly)
        => new(Path.GetFullPath(dataSource), readOnly);
}

internal static class ManagedConnectionPool
{
    private const int MaximumIdleConnectionsPerPool = 32;
    private const int MaximumPools = 64;
    private static readonly object PoolsGate = new();
    private static readonly Dictionary<ManagedConnectionPoolKey, Pool> Pools = new(new PoolKeyComparer());

    public static ManagedConnectionPoolLease Rent(
        ManagedConnectionPoolKey key,
        Func<IManagedDatabaseAdapter> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        while (true)
        {
            Pool pool;
            Pool? evictedPool = null;
            lock (PoolsGate)
            {
                if (!Pools.TryGetValue(key, out pool!))
                {
                    if (Pools.Count >= MaximumPools)
                    {
                        var evicted = Pools.First();
                        Pools.Remove(evicted.Key);
                        evictedPool = evicted.Value;
                    }

                    pool = new Pool();
                    Pools.Add(key, pool);
                }
            }

            evictedPool?.Clear();
            if (!pool.TryRent(out var database))
                continue;
            if (database is null)
                return new ManagedConnectionPoolLease(pool, factory());

            try
            {
                database.Connection.ResetForPooling();
                return new ManagedConnectionPoolLease(pool, database);
            }
            catch
            {
                database.Dispose();
                throw;
            }
        }
    }

    public static void Clear(ManagedConnectionPoolKey key)
    {
        Pool? pool;
        lock (PoolsGate)
        {
            if (!Pools.Remove(key, out pool))
                return;
        }

        pool.Clear();
    }

    public static void ClearAll()
    {
        Pool[] pools;
        lock (PoolsGate)
        {
            pools = Pools.Values.ToArray();
            Pools.Clear();
        }

        List<Exception>? errors = null;
        foreach (var pool in pools)
        {
            try
            {
                pool.Clear();
            }
            catch (Exception exception)
            {
                (errors ??= []).Add(exception);
            }
        }

        if (errors is not null)
            throw new AggregateException("One or more managed connection pools could not be cleared.", errors);
    }

    internal sealed class Pool
    {
        private readonly object _gate = new();
        private readonly Stack<IManagedDatabaseAdapter> _idle = [];
        private bool _cleared;

        public bool TryRent(out IManagedDatabaseAdapter? database)
        {
            lock (_gate)
            {
                if (_cleared)
                {
                    database = null;
                    return false;
                }

                database = _idle.Count == 0 ? null : _idle.Pop();
                return true;
            }
        }

        public void Return(IManagedDatabaseAdapter database)
        {
            var dispose = false;
            lock (_gate)
            {
                if (_cleared || _idle.Count >= MaximumIdleConnectionsPerPool)
                    dispose = true;
                else
                    _idle.Push(database);
            }

            if (dispose)
                database.Dispose();
        }

        public void Clear()
        {
            IManagedDatabaseAdapter[] idle;
            lock (_gate)
            {
                if (_cleared)
                    return;

                _cleared = true;
                idle = _idle.ToArray();
                _idle.Clear();
            }

            List<Exception>? errors = null;
            foreach (var database in idle)
            {
                try
                {
                    database.Dispose();
                }
                catch (Exception exception)
                {
                    (errors ??= []).Add(exception);
                }
            }

            if (errors is not null)
                throw new AggregateException("One or more pooled managed connections could not be disposed.", errors);
        }
    }

    private sealed class PoolKeyComparer : IEqualityComparer<ManagedConnectionPoolKey>
    {
        private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        public bool Equals(ManagedConnectionPoolKey left, ManagedConnectionPoolKey right)
            => left.ReadOnly == right.ReadOnly
               && PathComparer.Equals(left.DataSource, right.DataSource);

        public int GetHashCode(ManagedConnectionPoolKey key)
            => HashCode.Combine(PathComparer.GetHashCode(key.DataSource), key.ReadOnly);
    }
}

internal sealed class ManagedConnectionPoolLease
{
    private ManagedConnectionPool.Pool? _pool;
    private IManagedDatabaseAdapter? _database;

    internal ManagedConnectionPoolLease(
        ManagedConnectionPool.Pool pool,
        IManagedDatabaseAdapter database)
    {
        _pool = pool;
        _database = database;
    }

    public IManagedDatabaseAdapter Database
        => _database ?? throw new ObjectDisposedException(nameof(ManagedConnectionPoolLease));

    public void Release(bool reusable)
    {
        var database = Interlocked.Exchange(ref _database, null);
        var pool = Interlocked.Exchange(ref _pool, null);
        if (database is null)
            return;

        if (!reusable || pool is null)
        {
            database.Dispose();
            return;
        }

        try
        {
            database.Connection.ResetForPooling();
            pool.Return(database);
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }
}
