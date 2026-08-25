namespace Ahtola;

/// <summary>
/// Registers the optional native local-provider implementation.
/// </summary>
/// <remarks>
/// <para>
/// The companion package registers itself by calling <see cref="Register"/> — normally from a
/// <c>[ModuleInitializer]</c> in the companion assembly, or explicitly from application startup.
/// Nothing here probes for the companion by name: assembly/type/method lookups are invisible to
/// the trimmer and to NativeAOT, so this type stays statically analyzable and simply fails closed
/// when no factory has been registered.
/// </para>
/// <para>
/// <b>Compatibility.</b> Earlier versions discovered the companion by loading
/// <c>Turso.Data.Native</c> reflectively and invoking its
/// <c>NativeProviderRegistration.Register</c>. A companion built for that behavior never calls
/// <see cref="Register"/> itself, so it is now never activated and <c>Local Provider=Native</c>
/// fails closed with <see cref="NotSupportedException"/> even when the package is installed. Such
/// companions must ship a release that calls <see cref="Register"/> from a
/// <c>[ModuleInitializer]</c>, or instruct consumers to call it explicitly during startup.
/// </para>
/// </remarks>
public static class AhtolaNativeProvider
{
    /// <summary>
    /// Assembly that supplies the optional native local provider. Reported in the failure message
    /// so the diagnostic still names the companion; it is never loaded reflectively from here.
    /// </summary>
    internal const string NativeProviderAssemblyName = "Turso.Data.Native";

    /// <summary>
    /// Message used when <c>Provider=Native</c> is requested without a registered factory.
    /// </summary>
    internal const string MissingFactoryMessage =
        "Local Provider=Native requires the Turso.Data.Sqlite.Native companion package. " +
        "Add a matching PackageReference, and use a companion version that calls " +
        "AhtolaNativeProvider.Register(factory) from a module initializer or from application " +
        "startup: this provider never loads a companion by assembly name.";

    private static AhtolaNativeProviderFactory? s_factory;

    /// <summary>
    /// Registers the native local-provider factory supplied by the companion package.
    /// </summary>
    /// <remarks>
    /// Call this from a <c>[ModuleInitializer]</c> in the companion assembly so the registration
    /// happens before the first connection is opened, or explicitly during application startup.
    /// It is the only activation path: nothing is discovered by assembly name.
    /// </remarks>
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

    /// <summary>
    /// Gets the registered native local-provider factory, or <c>null</c> when the companion
    /// package has not registered one.
    /// </summary>
    internal static AhtolaNativeProviderFactory? Current => Volatile.Read(ref s_factory);

    internal static AhtolaNativeDatabase OpenDatabase(
        string path,
        AhtolaEncryptionCipher? cipher,
        string? encryptionKey)
        => (Current ?? throw new NotSupportedException(MissingFactoryMessage))
            .OpenDatabase(path, cipher, encryptionKey);
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
