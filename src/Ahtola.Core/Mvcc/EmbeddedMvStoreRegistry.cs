using System.Runtime.CompilerServices;
using Ahtola.Core.Storage;

namespace Ahtola.Core.Mvcc;

/// <summary>
/// Shares one <see cref="MvStore"/> (and its durable log handle) per file-system
/// database identity so concurrent connections observe the same version store.
/// </summary>
internal static class EmbeddedMvStoreRegistry
{
    private sealed class Entry
    {
        internal required MvStore Store { get; init; }
        internal int RefCount;
    }

    private sealed class Scope
    {
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

        internal MvStore GetOrCreate(string key, Func<MvStore> factory)
        {
            lock (_entries)
            {
                if (_entries.TryGetValue(key, out var existing))
                {
                    existing.RefCount++;
                    return existing.Store;
                }

                var created = factory();
                _entries.Add(key, new Entry { Store = created, RefCount = 1 });
                return created;
            }
        }

        internal bool TryGet(string key, out MvStore? store)
        {
            lock (_entries)
            {
                if (_entries.TryGetValue(key, out var entry))
                {
                    entry.RefCount++;
                    store = entry.Store;
                    return true;
                }

                store = null;
                return false;
            }
        }

        /// <summary>
        /// Drops one attachment. When the last reference is released, removes the
        /// entry and returns the store so the caller can dispose the logical log.
        /// </summary>
        internal MvStore? Release(string key)
        {
            lock (_entries)
            {
                if (!_entries.TryGetValue(key, out var entry))
                    return null;
                entry.RefCount--;
                if (entry.RefCount > 0)
                    return null;
                _entries.Remove(key);
                return entry.Store;
            }
        }
    }

    private static readonly ConditionalWeakTable<IFileSystem, Scope> FileSystemScopes = new();
    private static readonly Scope PhysicalFileSystemScope = new();

    internal static MvStore GetOrCreate(
        IFileSystem fileSystem,
        string databasePath,
        Func<MvStore> factory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        ArgumentNullException.ThrowIfNull(factory);

        return ResolveScope(fileSystem, databasePath, out var key).GetOrCreate(key, factory);
    }

    /// <summary>
    /// Attaches to an existing shared store and increments its refcount.
    /// </summary>
    internal static bool TryGet(IFileSystem fileSystem, string databasePath, out MvStore? store)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrEmpty(databasePath);

        return ResolveScope(fileSystem, databasePath, out var key).TryGet(key, out store);
    }

    /// <summary>
    /// Releases one attachment. Returns the store when this was the last reference
    /// (caller should dispose the logical log).
    /// </summary>
    internal static MvStore? Release(IFileSystem fileSystem, string databasePath)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrEmpty(databasePath);

        return ResolveScope(fileSystem, databasePath, out var key).Release(key);
    }

    private static Scope ResolveScope(IFileSystem fileSystem, string databasePath, out string key)
    {
        var unwrapped = AhtolaEncryptionFileSystem.Unwrap(fileSystem);
        if (unwrapped is not PhysicalFileSystem)
        {
            key = databasePath;
            return FileSystemScopes.GetValue(unwrapped, static _ => new Scope());
        }

        key = Path.GetFullPath(databasePath);
        if (OperatingSystem.IsWindows())
            key = key.ToUpperInvariant();
        return PhysicalFileSystemScope;
    }
}
