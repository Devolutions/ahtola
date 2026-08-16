# Managed FTS and R-Tree integration

## Upstream reference

Turso `v0.7.2` parses `CREATE VIRTUAL TABLE` into `CreateVirtualTable`
(`turso-src/sqlite/parser/src/ast.rs`) and routes module instances through
`VirtualTable` (`turso-src/core/vtab.rs`). Its planner calls `best_index` with
constraints and ordering, then sends the plan and bound arguments to cursor
`filter`. Turso's built-in FTS is an **index method** (`CREATE INDEX ... USING
fts`), not an FTS5 virtual table (`turso-src/core/index_method/fts.rs`), and
is implemented with Tantivy. The pinned source has no R-Tree module.

The managed port must preserve the planner lifecycle, but it must not copy the
native/unsafe extension ABI or Tantivy dependency. All module discovery and
dispatch must be static so NativeAOT and trimming retain every implementation.

## Required foundation contract

The virtual-table foundation should expose these internal, managed contracts
in `Ahtola.Core.VirtualTables`. The names may differ, but the data and
lifecycle are required for FTS and R-Tree integration:

```csharp
internal interface IManagedVirtualTableModule
{
    string Name { get; }
    ManagedVirtualTableDefinition Create(ManagedVirtualTableCreateContext context);
}

internal interface IManagedVirtualTable
{
    ManagedVirtualTableSchema Schema { get; }
    ManagedVirtualTablePlan BestIndex(in ManagedVirtualTableBestIndexRequest request);
    IManagedVirtualTableCursor Open(ManagedVirtualTableOpenContext context);
    long? Update(in ManagedVirtualTableUpdate update);
    void Begin();
    void Commit();
    void Rollback();
}

internal interface IManagedVirtualTableCursor : IDisposable
{
    void Filter(in ManagedVirtualTablePlan plan, ReadOnlySpan<SqlValue> arguments);
    bool MoveNext();
    long RowId { get; }
    SqlValue GetColumn(int ordinal);
}
```

`ManagedVirtualTableBestIndexRequest` must retain each constraint's column
ordinal, SQLite operation (including `Match`, equality, and all range
operations), usability, collation, and requested ordering. The returned plan
must specify a stable opaque plan identifier, an ordinal `argv` mapping, which
constraints are omitted after filtering, estimated cost, estimated rows, and
whether order is consumed. `Filter` receives arguments in exactly that `argv`
order.

`ManagedVirtualTableUpdate` must distinguish insert, delete, and update; carry
the old rowid, requested new rowid, and all declared column values. The
foundation must invoke it under the same savepoint/rollback lifecycle as
ordinary table writes. A module does not discover a transaction through an
ambient connection.

The create context must include the virtual-table name, schema, `IF NOT
EXISTS`, and the raw comma-separated module arguments without interpreting
them. FTS5 column declarations and tokenizer options are module grammar;
R-Tree's `id, minX, maxX, ...` declaration is module grammar. The definition
must provide declared visible/hidden columns and a durable module-specific
storage handle. It must also serialize the original `CREATE VIRTUAL TABLE`
statement into `sqlite_schema`, making `DROP`, `ALTER ... RENAME`, backups,
and attached schemas behave as normal catalog objects.

Registration must be compile-time static:

```csharp
internal static partial class ManagedVirtualTableModuleRegistry
{
    public static IManagedVirtualTableModule Resolve(string name) => name switch
    {
        "fts5" => ManagedFts5Module.Instance,
        "rtree" => ManagedRTreeModule.Instance,
        "rtree_i32" => ManagedRTreeI32Module.Instance,
        _ => throw new EmbeddedSqlException($"no such module: {name}"),
    };
}
```

No reflection, assembly loading, native extension ABI, or P/Invoke is
permitted. If the static registry is source-generated, its generated calls
must still directly reference every module type.

## Components supplied by this change

`Ahtola.Core.Search.ManagedFtsTokenizer`, `ManagedFtsQueryParser`, and
`ManagedFtsIndex` supply deterministic tokenization, term/phrase/prefix
boolean parsing, and an updateable posting store. An FTS5 module should call
`Upsert`/`Remove` only after durable shadow-storage changes have entered the
same transaction, and rebuild its in-memory index from that storage when
opened.

`Ahtola.Core.Spatial.ManagedRTreeBounds` and `ManagedRTreeIndex` supply
inclusive N-dimensional geometry, deterministic tree splitting, updates, and
intersection/containment filtering. An R-Tree module supplies the schema
validation, exact SQLite R-Tree shadow-table layout, and numeric affinity
rules; it should translate usable range constraints into a plan before calling
the index.

Neither component registers a module or changes SQL parsing. They can land
before the generic foundation without creating a competing virtual-table
stack.
