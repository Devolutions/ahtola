using Ahtola;

namespace Ahtola.Data.Sqlite;

/// <summary>
/// Registers native-only SQLite facade operations supplied by the optional native companion package.
/// </summary>
/// <remarks>
/// Activation is explicit: the companion calls <see cref="Register"/>, normally from a
/// <c>[ModuleInitializer]</c>. Nothing is discovered by assembly name, because reflective probing
/// is invisible to the trimmer and to NativeAOT. A companion built for the earlier
/// load-by-name behavior must publish a release that calls <see cref="Register"/> itself;
/// otherwise these operations fail closed even when the package is installed.
/// </remarks>
public static class SqliteNativeProvider
{
    private static SqliteNativeProviderFactory? s_factory;

    /// <summary>
    /// Registers native SQLite facade operations.
    /// </summary>
    /// <remarks>
    /// Call this from a <c>[ModuleInitializer]</c> in the companion assembly, or explicitly during
    /// application startup. It is the only activation path.
    /// </remarks>
    public static void Register(SqliteNativeProviderFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var registeredFactory = Interlocked.CompareExchange(ref s_factory, factory, null);
        if (registeredFactory is not null && registeredFactory.GetType() != factory.GetType())
        {
            throw new InvalidOperationException(
                $"A native SQLite provider factory of type {registeredFactory.GetType().FullName} is already registered.");
        }
    }

    internal static SqliteNativeProviderFactory Current
        => Volatile.Read(ref s_factory)
           ?? throw new NotSupportedException(
               "Local Provider=Native requires the Turso.Data.Sqlite.Native companion package. " +
               "Add a matching PackageReference, and use a companion version that calls " +
               "SqliteNativeProvider.Register(factory) from a module initializer or from " +
               "application startup: this provider never loads a companion by assembly name.");
}

/// <summary>
/// Contract implemented by the optional native SQLite facade companion assembly.
/// </summary>
public abstract class SqliteNativeProviderFactory
{
    /// <summary>Registers a scalar function.</summary>
    public abstract void RegisterScalarFunction(
        AhtolaNativeDatabase database,
        string name,
        int argc,
        bool isDeterministic,
        Func<object?[], object?> invoke);

    /// <summary>Registers an aggregate function.</summary>
    public abstract void RegisterAggregateFunction(
        AhtolaNativeDatabase database,
        string name,
        int argc,
        bool isDeterministic,
        object? seed,
        Func<object?, object?[], object?> step,
        Func<object?, object?> resultSelector);

    /// <summary>Unregisters scalar and aggregate functions with a name.</summary>
    public abstract void UnregisterFunctions(AhtolaNativeDatabase database, string name);

    /// <summary>Registers a collation.</summary>
    public abstract void RegisterCollation(
        AhtolaNativeDatabase database,
        string name,
        Func<string, string, int> compare);

    /// <summary>Unregisters a collation.</summary>
    public abstract void UnregisterCollation(AhtolaNativeDatabase database, string name);

    /// <summary>Enables or disables extension loading.</summary>
    public abstract void EnableExtensions(AhtolaNativeDatabase database, bool enable);

    /// <summary>Loads a SQLite extension.</summary>
    public abstract void LoadExtension(AhtolaNativeDatabase database, string file);
}
