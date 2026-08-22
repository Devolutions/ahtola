using Ahtola.Core;

namespace Ahtola;

internal interface IManagedSchemaConnection
{
    /// <summary>
    /// The managed connection adapter backing this connection, or <c>null</c> when the
    /// connection is not actually backed by the managed engine (native local or remote
    /// connections implement this interface too, but have no managed adapter to dispatch to).
    /// </summary>
    IManagedConnectionAdapter? ManagedSchemaConnection { get; }
}
