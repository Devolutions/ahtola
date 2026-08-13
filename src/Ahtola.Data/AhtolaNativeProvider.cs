using System.Reflection;
using System.Runtime.Loader;

namespace Ahtola;

/// <summary>
/// Registers the optional native local-provider implementation.
/// </summary>
public static class AhtolaNativeProvider
{
    private const string NativeProviderAssemblyName = "Turso.Data.Native";
    private const string NativeProviderRegistrationTypeName = "Turso.Data.Native.NativeProviderRegistration";
    private static AhtolaNativeProviderFactory? s_factory;

    /// <summary>
    /// Registers the native local-provider factory supplied by the companion package.
    /// </summary>
    public static void Register(AhtolaNativeProviderFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var registeredFactory = Interlocked.CompareExchange(ref s_factory, factory, null);
        if (registeredFactory is not null && registeredFactory.GetType() != factory.GetType())
        {
            throw new InvalidOperationException(
                $"A native provider factory of type {registeredFactory.GetType().FullName} is already registered.");
        }
    }

    internal static AhtolaNativeDatabase OpenDatabase(
        string path,
        AhtolaEncryptionCipher? cipher,
        string? encryptionKey)
    {
        var factory = Volatile.Read(ref s_factory);
        if (factory is null)
        {
            try
            {
                var loadContext = AssemblyLoadContext.GetLoadContext(typeof(AhtolaNativeProvider).Assembly);
                var assembly = loadContext?.LoadFromAssemblyName(new AssemblyName(NativeProviderAssemblyName))
                    ?? Assembly.Load(new AssemblyName(NativeProviderAssemblyName));
                var registrationType = assembly.GetType(NativeProviderRegistrationTypeName, throwOnError: true)!;
                var register = registrationType.GetMethod(
                    "Register",
                    BindingFlags.Public | BindingFlags.Static)
                    ?? throw new MissingMethodException(NativeProviderRegistrationTypeName, "Register");
                register.Invoke(null, null);
            }
            catch (FileNotFoundException)
            {
            }

            factory = Volatile.Read(ref s_factory);
        }

        return factory?.OpenDatabase(path, cipher, encryptionKey)
            ?? throw new NotSupportedException(
                "Local Provider=Native requires the Turso.Data.Sqlite.Native companion package. " +
                "Add a matching PackageReference to use the native Ahtola SDK.");
    }
}

/// <summary>
/// Contract implemented by the optional native local-provider companion assembly.
/// </summary>
public abstract class AhtolaNativeProviderFactory
{
    /// <summary>
    /// Opens a database through the native Ahtola SDK.
    /// </summary>
    public abstract AhtolaNativeDatabase OpenDatabase(
        string path,
        AhtolaEncryptionCipher? cipher,
        string? encryptionKey);
}

/// <summary>
/// Native local database contract used by the optional provider companion assembly.
/// </summary>
public abstract class AhtolaNativeDatabase : IDisposable
{
    /// <summary>
    /// Indicates whether the native database has been closed.
    /// </summary>
    public abstract bool IsInvalid { get; }

    /// <summary>
    /// Creates a native statement.
    /// </summary>
    public abstract AhtolaNativeStatement PrepareStatement(string sql);

    /// <summary>
    /// Sets the native connection busy timeout.
    /// </summary>
    public abstract void SetBusyTimeout(TimeSpan timeout);

    /// <inheritdoc />
    public abstract void Dispose();
}

/// <summary>
/// Native local statement contract used by the optional provider companion assembly.
/// </summary>
public abstract class AhtolaNativeStatement : IDisposable
{
    /// <summary>
    /// Indicates whether the native statement has been finalized.
    /// </summary>
    public abstract bool IsInvalid { get; }

    /// <summary>
    /// Gets the number of statement parameters.
    /// </summary>
    public abstract int ParameterCount { get; }

    /// <summary>
    /// Binds a value at a one-based parameter index.
    /// </summary>
    public abstract void BindParameter(int index, AhtolaValue value);

    /// <summary>
    /// Binds a value by parameter name.
    /// </summary>
    public abstract int BindNamedParameter(string name, AhtolaValue value);

    /// <summary>
    /// Gets the parameter name for a one-based index.
    /// </summary>
    public abstract string? GetParameterName(int index);

    /// <summary>
    /// Advances the statement to its next row.
    /// </summary>
    public abstract bool Read();

    /// <summary>
    /// Requests interruption of an in-flight statement operation.
    /// </summary>
    public abstract void Interrupt();

    /// <summary>
    /// Gets the current-row value at a zero-based column index.
    /// </summary>
    public abstract AhtolaValue GetValue(int ordinal);

    /// <summary>
    /// Gets the result column name at a zero-based column index.
    /// </summary>
    public abstract string GetName(int ordinal);

    /// <summary>
    /// Gets the result column count.
    /// </summary>
    public abstract int FieldCount { get; }

    /// <summary>
    /// Gets the affected-row count.
    /// </summary>
    public abstract int RowsAffected { get; }

    /// <summary>
    /// Indicates whether the statement has result rows.
    /// </summary>
    public abstract bool HasRows { get; }

    /// <inheritdoc />
    public abstract void Dispose();
}
