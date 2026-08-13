using Ahtola.Core;

namespace Ahtola;

internal static class ManagedSharedMemoryDatabase
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Databases = new(StringComparer.Ordinal);

    public static IManagedDatabaseAdapter Open(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Entry entry;
        lock (Gate)
        {
            if (!Databases.TryGetValue(name, out entry!))
            {
                entry = new Entry(new EmbeddedDatabase());
                Databases.Add(name, entry);
            }

            checked
            {
                entry.References++;
            }
        }

        try
        {
            return new Lease(name, entry, ManagedDatabaseAdapter.FromConnection(entry.Database.Connect()));
        }
        catch
        {
            Release(name, entry);
            throw;
        }
    }

    private static void Release(string name, Entry entry)
    {
        EmbeddedDatabase? database = null;
        lock (Gate)
        {
            if (!Databases.TryGetValue(name, out var current) || !ReferenceEquals(current, entry))
                throw new InvalidOperationException("The managed shared-memory database lease is not registered.");

            entry.References--;
            if (entry.References < 0)
                throw new InvalidOperationException("The managed shared-memory database reference count became negative.");
            if (entry.References == 0)
            {
                Databases.Remove(name);
                database = entry.Database;
            }
        }

        database?.Dispose();
    }

    private sealed class Entry(EmbeddedDatabase database)
    {
        public EmbeddedDatabase Database { get; } = database;

        public int References { get; set; }
    }

    private sealed class Lease(
        string name,
        Entry entry,
        IManagedDatabaseAdapter database) : IManagedDatabaseAdapter
    {
        private IManagedDatabaseAdapter? _database = database;

        public IManagedConnectionAdapter Connect()
            => GetDatabase().Connect();

        public IManagedConnectionAdapter Connection
            => GetDatabase().Connection;

        public void Dispose()
        {
            var ownedDatabase = Interlocked.Exchange(ref _database, null);
            if (ownedDatabase is null)
                return;

            try
            {
                ownedDatabase.Dispose();
            }
            finally
            {
                Release(name, entry);
            }
        }

        private IManagedDatabaseAdapter GetDatabase()
            => _database ?? throw new ObjectDisposedException(nameof(Lease));
    }
}
