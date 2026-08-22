using Ahtola.Core;

namespace Ahtola;

internal interface IManagedSchemaConnection
{
    IManagedConnectionAdapter ManagedSchemaConnection { get; }
}
