using Ahtola.Core;

namespace Ahtola;

/// <param name="DataSource">The full path of the pooled database file.</param>
/// <param name="ReadOnly">Whether the pooled databases were opened read-only.</param>
/// <param name="Encryption">
/// The cipher-and-key identity of an encrypted database (<c>AhtolaEncryptionOptions.CreatePoolIdentity</c>),
/// so a pooled encrypted database is only ever handed to a connection configured with the same key;
/// <see langword="null"/> for a plain database.
/// </param>
internal readonly record struct ManagedConnectionPoolKey(string DataSource, bool ReadOnly, string? Encryption = null)
{
    public static ManagedConnectionPoolKey Create(string dataSource, bool readOnly, string? encryption = null)
        => new(Path.GetFullPath(dataSource), readOnly, encryption);
}

/// <summary>
/// One pooled database together with the resources that must live exactly as long as it, such as
/// the encryption file system an encrypted database reads its pages through.
/// </summary>
internal readonly record struct ManagedPooledDatabase(IManagedDatabaseAdapter Database, IDisposable? Companion = null)
{
    public void Dispose()
    {
        try
        {
            Database.Dispose();
        }
        finally
        {
            Companion?.Dispose();
        }
    }
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
        return Rent(key, () => new ManagedPooledDatabase(factory()));
    }

    public static ManagedConnectionPoolLease Rent(
        ManagedConnectionPoolKey key,
        Func<ManagedPooledDatabase> factory)
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
            if (!pool.TryRent(out var pooled))
                continue;
            if (pooled is not { } database)
                return new ManagedConnectionPoolLease(pool, factory());

            try
            {
                database.Database.Connection.ResetForPooling();
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
        private readonly Stack<ManagedPooledDatabase> _idle = [];
        private bool _cleared;

        public bool TryRent(out ManagedPooledDatabase? database)
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

        public void Return(ManagedPooledDatabase database)
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
            ManagedPooledDatabase[] idle;
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
               && PathComparer.Equals(left.DataSource, right.DataSource)
               && string.Equals(left.Encryption, right.Encryption, StringComparison.Ordinal);

        public int GetHashCode(ManagedConnectionPoolKey key)
            => HashCode.Combine(
                PathComparer.GetHashCode(key.DataSource),
                key.ReadOnly,
                key.Encryption is null ? 0 : StringComparer.Ordinal.GetHashCode(key.Encryption));
    }
}

internal sealed class ManagedConnectionPoolLease
{
    private readonly object _gate = new();
    private ManagedConnectionPool.Pool? _pool;
    private ManagedPooledDatabase? _database;

    internal ManagedConnectionPoolLease(
        ManagedConnectionPool.Pool pool,
        ManagedPooledDatabase database)
    {
        _pool = pool;
        _database = database;
    }

    public IManagedDatabaseAdapter Database
        => _database?.Database ?? throw new ObjectDisposedException(nameof(ManagedConnectionPoolLease));

    public void Release(bool reusable)
    {
        ManagedPooledDatabase? database;
        ManagedConnectionPool.Pool? pool;
        lock (_gate)
        {
            database = _database;
            pool = _pool;
            _database = null;
            _pool = null;
        }

        if (database is not { } released)
            return;

        if (!reusable || pool is null)
        {
            released.Dispose();
            return;
        }

        try
        {
            released.Database.Connection.ResetForPooling();
            pool.Return(released);
        }
        catch
        {
            released.Dispose();
            throw;
        }
    }
}
