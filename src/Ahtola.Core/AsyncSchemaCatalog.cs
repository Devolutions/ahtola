using Ahtola.Core.Storage;

namespace Ahtola.Core;

/// <summary>
/// One base table's bounded-scan-relevant schema facts, reconstructed from a
/// <c>sqlite_schema</c> row by <see cref="AsyncSchemaCatalogLoader"/>.
/// </summary>
internal sealed record AsyncSchemaCatalogEntry(EmbeddedTable Table, uint RootPage, bool HasAnyIndex);

/// <summary>
/// A minimal, schema-only catalog built by walking <c>sqlite_schema</c>'s own b-tree
/// asynchronously (see <see cref="AsyncSchemaCatalogLoader"/>). Sized proportionally to the
/// number of tables/indexes declared, never to row count.
/// </summary>
internal sealed class AsyncSchemaCatalog(IReadOnlyDictionary<string, AsyncSchemaCatalogEntry> tablesByName)
{
    private readonly IReadOnlyDictionary<string, AsyncSchemaCatalogEntry> _tablesByName = tablesByName;

    /// <summary>Looks up a base table by name (case-insensitive, matching SQL identifier rules).</summary>
    public bool TryGetTable(string name, out AsyncSchemaCatalogEntry entry)
        => _tablesByName.TryGetValue(name, out entry!);
}
