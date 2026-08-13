namespace Ahtola;

/// <summary>
/// Selects the implementation used for local database connections.
/// </summary>
public enum AhtolaLocalProvider
{
    /// <summary>
    /// Uses the native Ahtola SDK.
    /// </summary>
    Native,

    /// <summary>
    /// Uses the managed local engine.
    /// </summary>
    Managed,
}
