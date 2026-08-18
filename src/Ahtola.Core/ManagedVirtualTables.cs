using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Text;
using Ahtola.Core.Search;
using Ahtola.Core.Spatial;

namespace Ahtola.Core;

/// <summary>SQLite affinity exposed by a managed virtual-table schema.</summary>
public enum ManagedVirtualTableAffinity
{
    Blob,
    Text,
    Numeric,
    Integer,
    Real,
}

/// <summary>Declares one column exposed by a managed virtual table.</summary>
public sealed record ManagedVirtualTableColumn(
    string Name,
    ManagedVirtualTableAffinity Affinity = ManagedVirtualTableAffinity.Blob,
    bool IsHidden = false);

/// <summary>
/// Immutable column shape returned by a managed virtual-table module. Hidden columns remain
/// addressable by predicates but are excluded from <c>SELECT *</c>, matching SQLite virtual tables.
/// </summary>
public sealed class ManagedVirtualTableSchema
{
    private readonly ReadOnlyCollection<ManagedVirtualTableColumn> _columns;

    public ManagedVirtualTableSchema(IEnumerable<ManagedVirtualTableColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var materialized = columns.ToArray();
        if (materialized.Length == 0)
            throw new ArgumentException("A virtual table must declare at least one column.", nameof(columns));
        if (materialized.Any(static column => string.IsNullOrWhiteSpace(column.Name)))
            throw new ArgumentException("A virtual table column name cannot be empty.", nameof(columns));
        if (materialized.Select(static column => column.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != materialized.Length)
            throw new ArgumentException("Virtual table column names must be unique.", nameof(columns));

        _columns = Array.AsReadOnly(materialized);
    }

    public IReadOnlyList<ManagedVirtualTableColumn> Columns => _columns;

    public IReadOnlyList<ManagedVirtualTableColumn> VisibleColumns
        => _columns.Where(static column => !column.IsHidden).ToArray();
}

/// <summary>SQLite-compatible operators passed to <see cref="ManagedVirtualTable.BestIndex"/>.</summary>
public enum ManagedVirtualTableConstraintOperator
{
    Equal,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Match,
    Like,
    Glob,
    IsNull,
    IsNotNull,
    Limit,
    Offset,
}

/// <summary>One planner-visible predicate on a virtual-table column.</summary>
public readonly record struct ManagedVirtualTableConstraint(
    int ColumnIndex,
    ManagedVirtualTableConstraintOperator Operator,
    bool Usable = true);

/// <summary>One planner-visible ORDER BY term.</summary>
public readonly record struct ManagedVirtualTableOrderBy(int ColumnIndex, bool Descending);

/// <summary>
/// Maps a constraint received by <see cref="ManagedVirtualTable.BestIndex"/> to the argument
/// vector supplied to <see cref="ManagedVirtualTableCursor.Filter"/>.
/// </summary>
public readonly record struct ManagedVirtualTableConstraintUsage(int ArgumentIndex, bool Omit = false)
{
    /// <summary>Leaves the constraint for the engine to evaluate after the module scan.</summary>
    public static ManagedVirtualTableConstraintUsage Unused => new(0);
}

/// <summary>Module-selected scan strategy, mirroring Turso's <c>IndexInfo</c>.</summary>
public sealed class ManagedVirtualTablePlan
{
    private readonly ReadOnlyCollection<ManagedVirtualTableConstraintUsage> _constraintUsages;

    public ManagedVirtualTablePlan(
        IEnumerable<ManagedVirtualTableConstraintUsage> constraintUsages,
        int indexNumber = 0,
        string? indexString = null,
        bool orderByConsumed = false,
        double estimatedCost = double.MaxValue,
        long estimatedRows = long.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(constraintUsages);
        if (double.IsNaN(estimatedCost) || estimatedCost < 0)
            throw new ArgumentOutOfRangeException(nameof(estimatedCost));
        if (estimatedRows < 0)
            throw new ArgumentOutOfRangeException(nameof(estimatedRows));

        _constraintUsages = Array.AsReadOnly(constraintUsages.ToArray());
        IndexNumber = indexNumber;
        IndexString = indexString;
        OrderByConsumed = orderByConsumed;
        EstimatedCost = estimatedCost;
        EstimatedRows = estimatedRows;
    }

    public IReadOnlyList<ManagedVirtualTableConstraintUsage> ConstraintUsages => _constraintUsages;
    public int IndexNumber { get; }
    public string? IndexString { get; }
    public bool OrderByConsumed { get; }
    public double EstimatedCost { get; }
    public long EstimatedRows { get; }

    internal void ValidateFor(IReadOnlyList<ManagedVirtualTableConstraint> constraints)
    {
        if (ConstraintUsages.Count != constraints.Count)
        {
            throw new InvalidOperationException(
                $"Virtual-table plan returned {ConstraintUsages.Count} constraint usage entries for {constraints.Count} constraints.");
        }

        var usedArguments = new HashSet<int>();
        foreach (var usage in ConstraintUsages)
        {
            if (usage.ArgumentIndex < 0)
                throw new InvalidOperationException("A virtual-table constraint argument index cannot be negative.");
            if (usage.Omit && usage.ArgumentIndex == 0)
            {
                throw new InvalidOperationException(
                    "A virtual-table plan can omit a constraint only when it receives that constraint's filter argument.");
            }
            if (usage.ArgumentIndex > 0 && !usedArguments.Add(usage.ArgumentIndex))
                throw new InvalidOperationException("A virtual-table plan cannot assign one filter argument to multiple constraints.");
        }
    }
}

/// <summary>Inputs passed to a module while CREATE VIRTUAL TABLE is being executed.</summary>
public sealed record ManagedVirtualTableCreateContext(string TableName, IReadOnlyList<string> Arguments);

/// <summary>
/// Opaque, module-owned state that the managed catalog persists for a virtual table. The engine
/// transports this value unchanged; a module owns both the version number and binary layout.
/// </summary>
public sealed class ManagedVirtualTablePersistencePayload
{
    internal const int MaximumLength = 64 * 1024 * 1024;
    private readonly byte[] _bytes;

    public ManagedVirtualTablePersistencePayload(int version, ReadOnlySpan<byte> bytes)
    {
        if (version <= 0)
            throw new ArgumentOutOfRangeException(nameof(version));
        if (bytes.Length > MaximumLength)
            throw new ArgumentOutOfRangeException(nameof(bytes));

        Version = version;
        _bytes = bytes.ToArray();
    }

    /// <summary>The module-defined, positive serialization version.</summary>
    public int Version { get; }

    /// <summary>A read-only view of the opaque module-owned state.</summary>
    public ReadOnlyMemory<byte> Bytes => _bytes;

    internal ManagedVirtualTablePersistencePayload Clone() => new(Version, _bytes);
}

/// <summary>
/// Explicit module registration contract. Implementations are instantiated directly and registered through
/// <see cref="ManagedVirtualTableModuleRegistry.Register"/>; the engine never discovers modules by reflection.
/// This keeps the mechanism NativeAOT and trimming safe.
/// </summary>
public abstract class ManagedVirtualTableModule
{
    public abstract string Name { get; }

    public abstract ManagedVirtualTable Create(ManagedVirtualTableCreateContext context);

    /// <summary>
    /// Recreates a table from the payload that this module previously produced. Modules that support
    /// durable or transactional virtual tables must override this method without reflection.
    /// </summary>
    public virtual ManagedVirtualTable Create(
        ManagedVirtualTableCreateContext context,
        ManagedVirtualTablePersistencePayload payload)
        => throw new EmbeddedSqlException(
            $"managed virtual table module '{Name}' does not support persistence payload version {payload.Version}");
}

/// <summary>
/// A created virtual-table instance. Implement this type for an eponymous module or a table created with
/// <c>CREATE VIRTUAL TABLE ... USING module</c>.
/// </summary>
public abstract class ManagedVirtualTable
{
    public abstract ManagedVirtualTableSchema Schema { get; }

    public abstract ManagedVirtualTablePlan BestIndex(
        IReadOnlyList<ManagedVirtualTableConstraint> constraints,
        IReadOnlyList<ManagedVirtualTableOrderBy> orderBy);

    public abstract ManagedVirtualTableCursor Open();

    /// <summary>
    /// Captures all module-owned mutable state in a deterministic, versioned payload. The catalog
    /// uses this snapshot for file persistence, transaction rollback, and savepoints.
    /// </summary>
    public virtual ManagedVirtualTablePersistencePayload GetPersistencePayload()
        => throw new EmbeddedSqlException(
            "managed virtual table does not support persistence; override GetPersistencePayload and Create(context, payload)");

    /// <summary>
    /// Receives SQLite's VUpdate argv layout: old rowid, new rowid, then declared column values.
    /// A NULL old rowid denotes INSERT and a NULL new rowid denotes DELETE.
    /// </summary>
    public virtual long? Update(IReadOnlyList<SqlValue> arguments)
        => throw new EmbeddedSqlException("attempt to write a readonly virtual table");

    /// <summary>
    /// Begins one autocommit virtual-table mutation statement. Explicit SQL transactions and
    /// savepoints are not supported for managed virtual-table mutations because the generic ABI
    /// has no reversible module-state contract.
    /// </summary>
    public virtual void Begin() { }
    public virtual void Sync() { }
    public virtual void Commit() { }
    public virtual void Rollback() { }
    public virtual void Rename(string newName) { }
    public virtual void Destroy() { }
}

/// <summary>Small deterministic binary writer shared by the built-in module payloads.</summary>
internal sealed class ManagedVirtualTablePayloadWriter
{
    private readonly MemoryStream _stream = new();

    public void WriteByte(byte value) => _stream.WriteByte(value);

    public void WriteInt32(int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        _stream.Write(bytes);
    }

    public void WriteInt64(long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        _stream.Write(bytes);
    }

    public void WriteDouble(double value) => WriteInt64(BitConverter.DoubleToInt64Bits(value));

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        WriteInt32(bytes.Length);
        _stream.Write(bytes);
    }

    public void WriteText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        WriteBytes(Encoding.UTF8.GetBytes(value));
    }

    public byte[] ToArray() => _stream.ToArray();
}

/// <summary>Strict bounds-checked reader shared by the built-in module payloads.</summary>
internal ref struct ManagedVirtualTablePayloadReader
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly ReadOnlySpan<byte> _bytes;
    private int _position;

    public ManagedVirtualTablePayloadReader(ReadOnlySpan<byte> bytes)
    {
        _bytes = bytes;
        _position = 0;
    }

    public byte ReadByte()
    {
        Require(sizeof(byte));
        return _bytes[_position++];
    }

    public int ReadInt32()
    {
        Require(sizeof(int));
        var value = BinaryPrimitives.ReadInt32LittleEndian(_bytes.Slice(_position, sizeof(int)));
        _position += sizeof(int);
        return value;
    }

    public long ReadInt64()
    {
        Require(sizeof(long));
        var value = BinaryPrimitives.ReadInt64LittleEndian(_bytes.Slice(_position, sizeof(long)));
        _position += sizeof(long);
        return value;
    }

    public double ReadDouble() => BitConverter.Int64BitsToDouble(ReadInt64());

    public byte[] ReadBytes()
    {
        var length = ReadLength();
        Require(length);
        var value = _bytes.Slice(_position, length).ToArray();
        _position += length;
        return value;
    }

    public string ReadText()
    {
        try
        {
            return StrictUtf8.GetString(ReadBytes());
        }
        catch (DecoderFallbackException exception)
        {
            throw new EmbeddedSqlException("invalid managed virtual-table persistence payload text", exception);
        }
    }

    public void RequireEnd()
    {
        if (_position != _bytes.Length)
            throw new EmbeddedSqlException("invalid managed virtual-table persistence payload trailing bytes");
    }

    public int ReadCount()
    {
        var value = ReadInt32();
        if (value < 0 || value > ManagedVirtualTablePersistencePayload.MaximumLength)
            throw new EmbeddedSqlException("invalid managed virtual-table persistence payload count");
        return value;
    }

    private int ReadLength()
    {
        var length = ReadInt32();
        if (length < 0 || length > ManagedVirtualTablePersistencePayload.MaximumLength)
            throw new EmbeddedSqlException("invalid managed virtual-table persistence payload length");
        return length;
    }

    private void Require(int length)
    {
        if (length < 0 || _position > _bytes.Length - length)
            throw new EmbeddedSqlException("truncated managed virtual-table persistence payload");
    }
}

/// <summary>Canonical value encoding used only inside module-private virtual-table payloads.</summary>
internal static class ManagedVirtualTablePayloadValues
{
    private const byte Null = 0;
    private const byte Integer = 1;
    private const byte Real = 2;
    private const byte Text = 3;
    private const byte Blob = 4;

    public static void Write(ManagedVirtualTablePayloadWriter writer, SqlValue value)
    {
        switch (value.Kind)
        {
            case SqlValueKind.Null:
                writer.WriteByte(Null);
                return;
            case SqlValueKind.Integer:
                writer.WriteByte(Integer);
                writer.WriteInt64(value.AsInteger());
                return;
            case SqlValueKind.Real:
                writer.WriteByte(Real);
                writer.WriteDouble(value.AsReal());
                return;
            case SqlValueKind.Text:
                writer.WriteByte(Text);
                writer.WriteText(value.AsText());
                return;
            case SqlValueKind.Blob:
                writer.WriteByte(Blob);
                writer.WriteBytes(value.AsBlob().Span);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    public static SqlValue Read(ref ManagedVirtualTablePayloadReader reader)
        => reader.ReadByte() switch
        {
            Null => SqlValue.Null,
            Integer => SqlValue.Integer(reader.ReadInt64()),
            Real => SqlValue.Real(reader.ReadDouble()),
            Text => SqlValue.Text(reader.ReadText()),
            Blob => SqlValue.Blob(reader.ReadBytes()),
            _ => throw new EmbeddedSqlException("invalid managed virtual-table persistence payload value type"),
        };
}

/// <summary>One open virtual-table scan. Filter positions on the first row, if any.</summary>
public abstract class ManagedVirtualTableCursor : IDisposable
{
    public abstract bool Filter(ManagedVirtualTablePlan plan, IReadOnlyList<SqlValue> arguments);
    public abstract void Next();
    public abstract bool Eof { get; }
    public abstract SqlValue Column(int columnIndex);
    public abstract long RowId { get; }
    public virtual void Dispose() { }
}

/// <summary>
/// Global explicit module registry. Registration is a direct managed call, not assembly scanning, so callers
/// must register shipped modules from statically reachable startup code.
/// </summary>
public static class ManagedVirtualTableModuleRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ManagedVirtualTableModule> Modules =
        new(StringComparer.OrdinalIgnoreCase);

    static ManagedVirtualTableModuleRegistry()
    {
        Register(ManagedFts5Module.Instance);
        Register(ManagedRTreeModule.Instance);
        Register(ManagedRTreeI32Module.Instance);
    }
    public static void Register(ManagedVirtualTableModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (string.IsNullOrWhiteSpace(module.Name))
            throw new ArgumentException("A virtual-table module name cannot be empty.", nameof(module));

        lock (Gate)
        {
            if (!Modules.TryAdd(module.Name, module))
                throw new InvalidOperationException($"A managed virtual-table module named '{module.Name}' is already registered.");
        }
    }

    public static bool TryResolve(string name, out ManagedVirtualTableModule module)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (Gate)
            return Modules.TryGetValue(name, out module!);
    }

    public static ManagedVirtualTableModule Resolve(string name)
        => TryResolve(name, out var module)
            ? module
            : throw new EmbeddedSqlException($"no such virtual table module: {name}");
}
