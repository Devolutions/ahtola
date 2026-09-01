using System.Collections.ObjectModel;
using Ahtola.Core.Storage;

namespace Ahtola.Core.Execution;

public readonly record struct Register
{
    public Register(int index)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index));

        Index = index;
    }

    public int Index { get; }
}

public readonly record struct Cursor
{
    public Cursor(int index)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index));

        Index = index;
    }

    public int Index { get; }
}

public readonly record struct Sorter
{
    public Sorter(int index)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index));

        Index = index;
    }

    public int Index { get; }
}

public readonly record struct Accumulator
{
    public Accumulator(int index)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index));

        Index = index;
    }

    public int Index { get; }
}

/// <summary>
/// A recursive worktable resource: the FIFO frontier plus optional de-duplication set that drives a
/// bounded recursive CTE evaluation. It is the recursion analogue of <see cref="Sorter"/> — an index into
/// a fixed-width table of runtime state the interpreter owns — except its buffer is a queue the program
/// both seeds and drains, and the drain step re-feeds the queue through a caller-supplied transform. A
/// program declaring <see cref="VdbeProgram.WorkTableCount"/> = N has worktables <c>0..N-1</c>.
/// </summary>
public readonly record struct WorkTable
{
    public WorkTable(int index)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index));

        Index = index;
    }

    public int Index { get; }
}

/// <summary>
/// A buffered-window resource: the ordered row buffer plus the per-row window-value block a windowed
/// SELECT computes over. It is the windowing analogue of <see cref="Sorter"/> — an index into a
/// fixed-width table of runtime state the interpreter owns — except its buffer is filled row by row,
/// then transformed once into a parallel block of window-function values, and finally drained in
/// insertion order. A program declaring <see cref="VdbeProgram.WindowBufferCount"/> = N has window
/// buffers <c>0..N-1</c>.
/// </summary>
/// <remarks>
/// The buffer exists because a window frame is not expressible as a streaming fold: a frame may look
/// forward (<c>FOLLOWING</c>), may be peer-relative (<c>RANGE</c>/<c>GROUPS</c>), and ranking and
/// navigation functions need the whole partition before the first row's value is known. Buffering the
/// scanned rows and computing every window value in one pass is what lets the emitted program preserve
/// full-partition frame semantics exactly, while emission (ordering, projection, LIMIT/OFFSET gating,
/// and <c>ResultRow</c>) stays ordinary opcode-driven work.
/// </remarks>
public readonly record struct WindowBuffer
{
    public WindowBuffer(int index)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index));

        Index = index;
    }

    public int Index { get; }
}

public readonly record struct ProgramCounter
{
    public ProgramCounter(int offset)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));

        Offset = offset;
    }

    public int Offset { get; }
}

public readonly record struct RegisterRange
{
    public RegisterRange(Register start, int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        Start = start;
        Count = count;
    }

    public Register Start { get; }

    public int Count { get; }
}

/// <summary>
/// A late-bound parameter position in a program's parameter space, the operand a
/// <see cref="LoadParameterInstruction"/> reads from the statement's
/// <see cref="VdbeParameterBinding"/> at execution time. Slots are zero-based and dense within a
/// program: a program declaring <see cref="VdbeProgram.ParameterSlotCount"/> = N has slots
/// <c>0..N-1</c>, and every binding must supply a value for each of them. It is the parameter-space
/// analogue of <see cref="Register"/>: an index into a fixed-width table the interpreter fills, except
/// its contents come from the caller's binding rather than from executed instructions.
/// </summary>
public readonly record struct ParameterSlot
{
    public ParameterSlot(int index)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index));

        Index = index;
    }

    public int Index { get; }
}

public enum VdbeOpcode
{
    LoadConstant = 0,
    LoadParameter = 1,
    Copy = 2,
    Function = 3,
    Arithmetic = 4,
    NumericAffinity = 5,
    OpenReadCursor = 6,
    OpenJoinCursor = 7,
    OpenWriteCursor = 8,
    Rewind = 9,
    Column = 10,
    RowId = 11,
    Filter = 12,
    FilterRowId = 13,
    FilterRegisters = 14,
    ProjectRegisters = 15,
    DistinctFilter = 16,
    Next = 17,
    Delete = 18,
    Insert = 19,
    Update = 20,
    Commit = 21,
    CloseCursor = 22,
    OpenSorter = 23,
    SorterInsert = 24,
    SorterSort = 25,
    SorterData = 26,
    SorterNext = 27,
    CloseSorter = 28,
    Goto = 29,
    JumpIf = 30,
    AggReset = 31,
    AggStep = 32,
    AggFinalize = 33,
    SameGroup = 34,
    Yield = 35,
    ResultRow = 36,
    DistinctResultRow = 37,
    RowSetInsert = 38,
    RowSetRewind = 39,
    RowSetNext = 40,
    CompoundResultRow = 41,
    GuardedRow = 42,
    OffsetGate = 43,
    LimitGate = 44,
    BeginTransaction = 45,
    CommitTransaction = 46,
    RollbackTransaction = 47,
    Savepoint = 48,
    ReleaseSavepoint = 49,
    RollbackToSavepoint = 50,
    OpenWorkTable = 51,
    SeedWorkTable = 52,
    WorkTableStep = 53,
    WorkTableExpand = 54,
    WorkTableExpandGeneration = 55,
    CloseWorkTable = 56,
    Halt = 57,
    GroupKey = 58,
    DistinctGate = 59,
    OpenWindowBuffer = 60,
    WindowBufferInsert = 61,
    WindowBufferCompute = 62,
    WindowBufferData = 63,
    WindowBufferNext = 64,
    CloseWindowBuffer = 65,
    Compare = 66,
    JumpIfNotTrue = 67,
    Cast = 68,
    SeekRowid = 69,
    SeekRowidRange = 70,
    RowCount = 71,
    Last = 72,
    Prev = 73,
    RowSetTest = 74,
    Program = 75,
    /// <summary>Jump if the integer key in P3 is absent from cursor P1 (Turso/SQLite <c>NotExists</c>).</summary>
    NotExists = 76,
    /// <summary>Jump if the integer key in P3 is present on cursor P1 (Turso/SQLite <c>Found</c>).</summary>
    Found = 77,
    /// <summary>Halt when register P3 is NULL (Turso/SQLite <c>HaltIfNull</c> / NOT NULL checks).</summary>
    HaltIfNull = 78,
    /// <summary>Open a general-purpose in-memory ephemeral table bound to a cursor (Turso <c>OpenEphemeral</c>).</summary>
    OpenEphemeral = 79,
    /// <summary>Append one row from registers into an ephemeral table cursor.</summary>
    EphemeralInsert = 80,
    /// <summary>
    /// Jump if the key in registers has no matching row (or contains NULL); leave the cursor
    /// positioned when a match is found (Turso/SQLite <c>NoConflict</c>).
    /// </summary>
    NoConflict = 81,
    /// <summary>Add P2 to the deferred or statement FK constraint counter (Turso <c>FkCounter</c>).</summary>
    FkCounter = 82,
    /// <summary>Jump if the deferred or statement FK counter is zero (Turso <c>FkIfZero</c>).</summary>
    FkIfZero = 83,
    /// <summary>Halt with SQLITE_CONSTRAINT_FOREIGNKEY when the FK counter is non-zero (Turso <c>FkCheck</c>).</summary>
    FkCheck = 84,
    /// <summary>Seek to first key ≥ bound (Turso <c>SeekGE</c>).</summary>
    SeekGE = 85,
    /// <summary>Seek to first key &gt; bound (Turso <c>SeekGT</c>).</summary>
    SeekGT = 86,
    /// <summary>Seek to last key ≤ bound (Turso <c>SeekLE</c>).</summary>
    SeekLE = 87,
    /// <summary>Seek to last key &lt; bound (Turso <c>SeekLT</c>).</summary>
    SeekLT = 88,
    /// <summary>Index-cursor SeekGE (Turso <c>IdxGE</c>).</summary>
    IdxGE = 89,
    /// <summary>Index-cursor SeekGT (Turso <c>IdxGT</c>).</summary>
    IdxGT = 90,
    /// <summary>Index-cursor SeekLE (Turso <c>IdxLE</c>).</summary>
    IdxLE = 91,
    /// <summary>Index-cursor SeekLT (Turso <c>IdxLT</c>).</summary>
    IdxLT = 92,
    /// <summary>Load the current index entry's rowid into a register (Turso <c>IdxRowid</c>).</summary>
    IdxRowId = 93,
    /// <summary>Copy the current cursor row's packed payload into registers (Turso <c>RowData</c>).</summary>
    RowData = 94,
    /// <summary>Insert a key into an index/ephemeral cursor (Turso <c>IdxInsert</c>).</summary>
    IdxInsert = 95,
    /// <summary>Delete the current index entry (Turso <c>IdxDelete</c>).</summary>
    IdxDelete = 96,
    /// <summary>Reject a candidate row that fails its de-duplication/membership guards, without emitting it.</summary>
    RowGate = 97,
    /// <summary>Open a managed virtual-table cursor (Turso <c>VOpen</c>).</summary>
    VOpen = 98,
    /// <summary>Filter and position a managed virtual-table cursor (Turso <c>VFilter</c>).</summary>
    VFilter = 99,
    /// <summary>Read a managed virtual-table column (Turso <c>VColumn</c>).</summary>
    VColumn = 100,
    /// <summary>Apply a managed virtual-table mutation (Turso <c>VUpdate</c>).</summary>
    VUpdate = 101,
    /// <summary>Advance a managed virtual-table cursor (Turso <c>VNext</c>).</summary>
    VNext = 102,
    /// <summary>Begin a managed virtual-table transaction.</summary>
    VBegin = 103,
    /// <summary>Synchronize pending managed virtual-table changes.</summary>
    VSync = 104,
    /// <summary>Commit a managed virtual-table transaction.</summary>
    VCommit = 105,
    /// <summary>Rollback a managed virtual-table transaction.</summary>
    VRollback = 106,

    /// <summary>Create the state of a managed index method (Turso <c>IndexMethodCreate</c>, insn.rs:1437).</summary>
    IndexMethodCreate = 107,

    /// <summary>Destroy the state of a managed index method (Turso <c>IndexMethodDestroy</c>, insn.rs:1442).</summary>
    IndexMethodDestroy = 108,

    /// <summary>Compact the state of a managed index method (Turso <c>IndexMethodOptimize</c>, insn.rs:1447).</summary>
    IndexMethodOptimize = 109,

    /// <summary>Start a managed index-method query pattern (Turso <c>IndexMethodQuery</c>, insn.rs:1452).</summary>
    IndexMethodQuery = 110,

    /// <summary>Advance a managed index-method cursor (Ahtola: Turso folds advance into the query cursor).</summary>
    IndexMethodNext = 111,

    /// <summary>Read a managed index-method result column; column 0 is the method score.</summary>
    IndexMethodColumn = 112,

    /// <summary>Load the current managed index-method result's base rowid into a register.</summary>
    IndexMethodRowId = 113,

    /// <summary>Apply one base-row insert to a managed index method.</summary>
    IndexMethodInsert = 114,

    /// <summary>Apply one base-row delete to a managed index method.</summary>
    IndexMethodDelete = 115,
    /// <summary>Create and publish a managed virtual-table instance (Turso <c>VCreate</c>).</summary>
    VCreate = 116,
    /// <summary>Destroy a managed virtual-table instance (Turso <c>VDestroy</c>).</summary>
    VDestroy = 117,
    /// <summary>Rename a managed virtual-table instance (Turso <c>VRename</c>).</summary>
    VRename = 118,

    /// <summary>Pack a register block into one record register (Turso <c>MakeRecord</c>, insn.rs:912).</summary>
    MakeRecord = 119,

    /// <summary>Allocate the next unused rowid for a cursor (Turso <c>NewRowid</c>, insn.rs:1368).</summary>
    NewRowid = 120,

    /// <summary>Allocate a new b-tree root page (Turso <c>CreateBtree</c>, insn.rs:1427).</summary>
    CreateBtree = 121,

    /// <summary>Delete every entry under a b-tree root, keeping the root (Turso <c>ClearBtree</c>, insn.rs:1461).</summary>
    ClearBtree = 122,

    /// <summary>Destroy a b-tree root and reclaim its pages (Turso <c>Destroy</c>, insn.rs:1467).</summary>
    Destroy = 123,

    /// <summary>Read a database header cookie into a register (Turso <c>ReadCookie</c>, insn.rs:1766).</summary>
    ReadCookie = 124,

    /// <summary>Stage a database header cookie value (Turso <c>SetCookie</c>, insn.rs:1773).</summary>
    SetCookie = 125,

    /// <summary>Reparse selected <c>sqlite_schema</c> rows into the live schema (Turso <c>ParseSchema</c>, insn.rs:1661).</summary>
    ParseSchema = 126,

    /// <summary>Evict a table from the in-memory schema (Turso <c>DropTable</c>, insn.rs:1491).</summary>
    DropTable = 127,

    /// <summary>Evict a view from the in-memory schema (Turso <c>DropView</c>, insn.rs:1501).</summary>
    DropView = 128,

    /// <summary>Evict an index from the in-memory schema (Turso <c>DropIndex</c>, insn.rs:1507).</summary>
    DropIndex = 129,

    /// <summary>Evict a trigger from the in-memory schema (Turso <c>DropTrigger</c>, insn.rs:1514).</summary>
    DropTrigger = 130,

    /// <summary>Rename a table in the in-memory schema (Turso <c>RenameTable</c>, insn.rs:1863).</summary>
    RenameTable = 131,

    /// <summary>Append a column to a table in the in-memory schema (Turso <c>AddColumn</c>, insn.rs:1873).</summary>
    AddColumn = 132,

    /// <summary>Remove a column from a table in the in-memory schema (Turso <c>DropColumn</c>, insn.rs:1868).</summary>
    DropColumn = 133,

    /// <summary>Rewrite a column definition in the in-memory schema (Turso <c>AlterColumn</c>, insn.rs:1876).</summary>
    AlterColumn = 134,

    /// <summary>
    /// Refill an index from its base table (Turso's <c>emit_refill_index</c> block, index.rs:326).
    /// </summary>
    IndexBuild = 135,
}

/// <summary>
/// The b-tree kind <see cref="CreateBtreeInstruction"/> allocates, mirroring Turso's
/// <c>CreateBTreeFlags</c> bit values (pager.rs:5940) so a compiled program's P3 matches upstream.
/// </summary>
[Flags]
public enum VdbeCreateBtreeFlags
{
    /// <summary>No kind selected. Never valid in a compiled program.</summary>
    None = 0,

    /// <summary>A rowid table b-tree (<c>CreateBTreeFlags::TABLE</c>).</summary>
    Table = 0b0001,

    /// <summary>An index b-tree (<c>CreateBTreeFlags::INDEX</c>).</summary>
    Index = 0b0010,
}

/// <summary>
/// The database header cookie a <see cref="ReadCookieInstruction"/> or <see cref="SetCookieInstruction"/>
/// addresses. Values match Turso's <c>Cookie</c> enum (insn.rs:2429), which in turn matches the SQLite
/// file-header cookie numbering.
/// </summary>
public enum VdbeSchemaCookie
{
    /// <summary>The schema cookie, bumped by every schema change.</summary>
    SchemaVersion = 1,

    /// <summary>The schema format number (1, 2, 3, or 4).</summary>
    DatabaseFormat = 2,

    /// <summary>The default page cache size.</summary>
    DefaultPageCacheSize = 3,

    /// <summary>The largest root b-tree page under auto/incremental vacuum, otherwise zero.</summary>
    LargestRootPageNumber = 4,

    /// <summary>The database text encoding (1 = UTF-8, 2 = UTF-16le, 3 = UTF-16be).</summary>
    DatabaseTextEncoding = 5,

    /// <summary>The user version, as read and written by <c>PRAGMA user_version</c>.</summary>
    UserVersion = 6,

    /// <summary>The auto-vacuum mode setting.</summary>
    IncrementalVacuum = 7,

    /// <summary>The application id, as read and written by <c>PRAGMA application_id</c>.</summary>
    ApplicationId = 8,
}

/// <summary>
/// The schema object a Drop opcode evicts from the live schema. Turso spells each one as its own
/// instruction (<c>DropTable</c>, <c>DropView</c>, <c>DropIndex</c>, <c>DropTrigger</c>); this enum lets the
/// managed schema context dispatch them through one entry point without losing the distinction.
/// </summary>
internal enum VdbeSchemaObjectKind
{
    Table,
    View,
    Index,
    Trigger,
}

/// <summary>Key-order seek comparison used by SeekGE/GT/LE/LT and IdxGE/GT/LE/LT.</summary>
public enum VdbeKeySeekOperator
{
    GreaterThanOrEqual = 0,
    GreaterThan = 1,
    LessThanOrEqual = 2,
    LessThan = 3,
}

/// <summary>
/// Disposition applied when a <see cref="HaltInstruction"/> carries a non-zero error code
/// (Turso <c>ResolveType</c> / SQLite ON CONFLICT / RAISE action).
/// </summary>
public enum VdbeHaltOnError
{
    Abort = 0,
    Fail = 1,
    Ignore = 2,
    Rollback = 3,
}

/// <summary>Well-known SQLite result codes used by Halt / HaltIfNull.</summary>
public static class SqliteResultCode
{
    public const int Ok = 0;
    public const int Error = 1;
    public const int Constraint = 19;
    public const int ConstraintCheck = 275;
    public const int ConstraintNotNull = 1299;
    public const int ConstraintPrimaryKey = 1555;
    public const int ConstraintUnique = 2067;
    public const int ConstraintTrigger = 1811;
    public const int ConstraintForeignKey = 787;
}

/// <summary>
/// The membership condition a <see cref="CompoundResultRowInstruction"/> requires of a candidate row
/// against its probe sets, which is what distinguishes the two multi-term compound set operations.
/// </summary>
public enum CompoundMembershipMode
{
    /// <summary>The candidate must be present in every probe set to be emitted — <c>INTERSECT</c>
    /// semantics, where a distinct primary-term row survives only if it also appears in each of the
    /// other terms.</summary>
    PresentInAll,

    /// <summary>The candidate must be absent from every probe set to be emitted — <c>EXCEPT</c>
    /// semantics, where a distinct primary-term row survives only if it appears in none of the other
    /// terms (equivalently, is not in their union).</summary>
    AbsentFromAll,
}

/// <summary>
/// How a recursive worktable treats a row that duplicates one already admitted, which is what
/// distinguishes the two supported recursive compound operators.
/// </summary>
public enum WorkTableDedupMode
{
    /// <summary>Every produced row is admitted and later emitted — <c>UNION ALL</c> semantics. The
    /// worktable performs no de-duplication, so termination relies entirely on the depth and row guards
    /// (or on the recursive transform eventually producing no rows).</summary>
    KeepAll,

    /// <summary>Only the first occurrence of each distinct row is admitted — <c>UNION</c>/<c>DISTINCT</c>
    /// semantics. A row equal (under the worktable's <see cref="VdbeRowEquality"/>) to any previously
    /// admitted row — seed or descendant — is dropped, which also breaks cycles so the recursion
    /// terminates naturally on a finite reachable set.</summary>
    Distinct,
}

/// <summary>
/// Evaluates a single scanned row against a compiled predicate. The compiler
/// supplies the delegate so the emitted program matches the evaluator's SQL
/// semantics exactly rather than re-deriving comparison rules in the executor.
/// </summary>
public delegate bool VdbeRowPredicate(SqlValue[] row);

/// <summary>
/// Evaluates a scanned row together with its hidden rowid. This keeps rowid predicates on the
/// cursor path without appending implementation-only values to the row's declared-column tuple.
/// </summary>
public delegate bool VdbeRowIdPredicate(SqlValue[] row, long rowId);

public enum VdbeComparisonOperator
{
    Is,
    IsNot,
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
}

internal enum VdbeValueAffinity
{
    Blob,
    Text,
    Numeric,
    Integer,
    Real,
}

/// <summary>
/// Orders two materialized rows for a sorter. The compiler supplies the delegate so
/// the emitted <c>SorterSort</c> uses the evaluator's ORDER BY semantics (key
/// extraction, collation, direction, and NULL ordering) rather than re-deriving them
/// in the executor. It must return a negative, zero, or positive value when
/// <paramref name="left"/> sorts before, equal to, or after <paramref name="right"/>.
/// Equal-key rows keep their insertion order, so the sort is stable.
/// </summary>
public delegate int VdbeRowComparer(SqlValue[] left, SqlValue[] right);

/// <summary>
/// Decides whether two group-key tuples belong to the same aggregate group. The
/// compiler supplies the delegate so the emitted <c>SameGroup</c> uses the evaluator's
/// grouping equality (affinity, collation, and the rule that NULL keys group together)
/// rather than re-deriving them in the executor. It returns <see langword="true"/> when
/// <paramref name="left"/> and <paramref name="right"/> fall in the same group.
/// </summary>
/// <remarks>
/// The comparer must be consistent with the <see cref="VdbeRowComparer"/> used to order
/// the sorter that feeds a grouped aggregation: rows of one group must sort adjacently
/// so a single linear pass sees each group as a contiguous run.
/// </remarks>
public delegate bool VdbeGroupComparer(SqlValue[] left, SqlValue[] right);

/// <summary>
/// Computes a GROUP BY key tuple from one materialized source row. The executor
/// invokes it once per filtered source row, before sorting, so expression
/// callbacks and errors retain source order.
/// </summary>
public delegate SqlValue[] VdbeGroupKeyProjector(SqlValue[] row);

/// <summary>Computes a hash code consistent with a GROUP BY equality delegate.</summary>
public delegate int VdbeGroupHasher(SqlValue[] key);

/// <summary>
/// Decides whether two result-row tuples are duplicates for compound-select de-duplication
/// (<c>UNION</c>/<c>DISTINCT</c>). The compiler supplies the delegate so the emitted
/// <c>DistinctResultRow</c> uses the evaluator's row-equality contract exactly — the rule that
/// two NULLs are equal and that other values compare under their column's affinity and collation —
/// rather than re-deriving comparison rules in the executor. It returns <see langword="true"/>
/// when <paramref name="left"/> and <paramref name="right"/> are the same row and so only one of
/// them should be emitted.
/// </summary>
/// <remarks>
/// The delegate must be a consistent equivalence relation (reflexive, symmetric, transitive) over
/// the emitted result rows, which all share one fixed column count. It is the compound analogue of
/// <see cref="VdbeGroupComparer"/>: where that groups adjacent sorted rows, this recognizes a
/// previously emitted row anywhere in the stream so duplicates are dropped while first occurrences
/// are preserved in arrival order.
/// </remarks>
public delegate bool VdbeRowEquality(SqlValue[] left, SqlValue[] right);

/// <summary>Projects one fixed-width register tuple into another.</summary>
public delegate SqlValue[] VdbeRowTransform(SqlValue[] row);

/// <summary>
/// Expands one recursive-worktable frontier row into its immediate descendant rows. The compiler supplies
/// the delegate so a <see cref="WorkTableExpandInstruction"/> reuses the evaluator's exact recursive-term
/// semantics — projecting, filtering, and computing the next generation from a single working-set row —
/// rather than the executor re-deriving them. It models one linear recursive term evaluated with the CTE
/// bound to the single supplied row: given that row it returns zero or more child rows, each of the
/// worktable's fixed column width, in the order they should enter the queue.
/// </summary>
/// <remarks>
/// The delegate is a leaf, exactly like <see cref="VdbeRowPredicate"/> or <see cref="VdbeAggregate"/>: it
/// computes one generation from one row and knows nothing of the queue, the de-duplication set, the depth,
/// or the guards. The recursion itself — seeding the frontier, dequeuing in FIFO (breadth-first) order,
/// re-feeding descendants, de-duplicating, bounding depth, and capping total rows — is performed by the
/// interpreter's observable instruction loop over <see cref="WorkTableStepInstruction"/> /
/// <see cref="WorkTableExpandInstruction"/>, not by this delegate. Returning an empty list ends a branch;
/// a row whose width differs from the worktable's column count is a hard error.
/// </remarks>
public delegate IReadOnlyList<SqlValue[]> VdbeRecursiveTransform(SqlValue[] frontierRow);

/// <summary>
/// Expands one complete recursive-worktable generation into the next generation. Joined and DISTINCT
/// recursive terms use this contract so they execute once over the evaluator's full working set.
/// </summary>
public delegate IReadOnlyList<SqlValue[]> VdbeRecursiveGenerationTransform(
    IReadOnlyList<SqlValue[]> frontierRows);

/// <summary>
/// Computes every window function's value for every buffered row of a windowed SELECT. The compiler
/// supplies the delegate so a <see cref="WindowBufferComputeInstruction"/> reuses the evaluator's exact
/// window semantics — partitioning, per-partition window ordering with stable ties, peer groups, ROWS /
/// RANGE / GROUPS frame resolution, frame exclusion, <c>FILTER</c>, and the ranking, navigation and
/// aggregate function families — rather than the executor re-deriving them.
/// </summary>
/// <remarks>
/// The delegate is a leaf in exactly the sense <see cref="VdbeRowPredicate"/> and
/// <see cref="VdbeAggregate"/> are: it maps the buffer's rows (in the insertion order the ingest loop
/// produced, which is scan order) to one window-value tuple per row, and knows nothing of the buffer,
/// the drain cursor, the output ordering, the projection, or the row gates — all of which are the
/// program's own opcodes. It must return exactly one tuple per input row, in the same order, each of the
/// buffer's declared window-value width. Returning a different shape is a hard error.
/// </remarks>
public delegate IReadOnlyList<SqlValue[]> VdbeWindowEvaluator(IReadOnlyList<SqlValue[]> bufferedRows);

/// <summary>
/// A single aggregate function expressed as the three lifecycle operations the
/// aggregate opcodes drive: create a fresh accumulator context, fold one argument
/// tuple into it, and finalize it into a result value. The caller supplies the
/// delegates so the emitted program reuses the evaluator's exact accumulation and
/// null/type semantics (e.g. <c>COUNT</c> ignoring NULLs, <c>SUM</c> of no rows being
/// NULL) rather than re-deriving them in the executor.
/// </summary>
/// <remarks>
/// The context is an opaque <see cref="object"/> owned entirely by the delegates; the
/// executor only threads it through the accumulator's lifecycle. <see cref="Finalize"/>
/// runs against a context returned by <see cref="CreateContext"/> even when
/// <see cref="Accumulate"/> was never called, which is how empty input yields the
/// aggregate's identity value (COUNT → 0, SUM → NULL).
/// </remarks>
public sealed class VdbeAggregate
{
    /// <summary>The function name surfaced by <c>EXPLAIN</c>, e.g. <c>"count"</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Produces a fresh accumulator context for a new group.</summary>
    public required Func<object?> CreateContext { get; init; }

    /// <summary>Folds one argument tuple into the accumulator, returning the next context.</summary>
    public required Func<object?, SqlValue[], object?> Accumulate { get; init; }

    /// <summary>Produces the group's result value from its accumulator context.</summary>
    public required Func<object?, SqlValue> Finalize { get; init; }
}

/// <summary>
/// A single scalar SQL function expressed as a pure mapping from an argument tuple to one result value.
/// The caller supplies the delegate so a compiled program reuses the evaluator's exact per-function
/// semantics (NULL propagation, affinity, text/blob rules) rather than the executor re-deriving them. It
/// is the stateless, one-step sibling of <see cref="VdbeAggregate"/>: where an aggregate folds many
/// argument tuples into a running context, a scalar function maps exactly one argument tuple to exactly
/// one value with no cross-invocation state, which is why the same descriptor serves both user-defined
/// functions and builtins such as <c>abs</c>, <c>upper</c>, or <c>coalesce</c>.
/// </summary>
/// <remarks>
/// A <see cref="FunctionInstruction"/> hands <see cref="Invoke"/> a private copy of its argument
/// registers, so the delegate can neither observe a later register write nor mutate the interpreter's
/// register file; the returned <see cref="SqlValue"/> is itself immutable (text is an immutable string, a
/// blob is copied on construction), so writing it into the destination register shares no mutable storage.
/// The delegate owns SQL semantics entirely: it decides how NULL arguments propagate and may throw — for
/// example a <see cref="VdbeFunctionException"/> — to raise a function error, which the executor surfaces
/// to the caller of the step rather than swallowing.
/// </remarks>
public sealed class VdbeScalarFunction
{
    /// <summary>The function name surfaced by <c>EXPLAIN</c>, e.g. <c>"abs"</c>.</summary>
    public required string Name { get; init; }

    /// <summary>The exact number of arguments the function accepts, or <see langword="null"/> for a
    /// variadic function (e.g. <c>coalesce</c>) that accepts any argument count. When set, a
    /// <see cref="FunctionInstruction"/> whose argument range width differs from this arity is rejected at
    /// program-construction time, so an arity error can never reach execution as a silently truncated or
    /// padded argument tuple.</summary>
    public int? Arity { get; init; }

    /// <summary>Maps one argument tuple to the function's result value. The tuple is a fresh copy of the
    /// argument registers in argument order, so the delegate may read (and even scribble over) it freely
    /// without disturbing the register file.</summary>
    public required Func<SqlValue[], SqlValue> Invoke { get; init; }
}

/// <summary>
/// Applies one named SQLite numeric-affinity rule to a materialized value. The compiler supplies the
/// transformation so the opcode and tree-walking evaluator share one coercion implementation.
/// </summary>
public sealed class VdbeNumericAffinity
{
    /// <summary>The affinity name surfaced by <c>EXPLAIN</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Coerces one value while preserving NULL.</summary>
    public required Func<SqlValue, SqlValue> Apply { get; init; }
}

/// <summary>
/// Raised by a scalar-function delegate to signal a function-level error (a domain error, an argument-type
/// error, an overflow, and so on), the managed analogue of a SQLite function raising an error through
/// <c>sqlite3_result_error</c>. The executor does not catch it: invoking a <see cref="FunctionInstruction"/>
/// whose delegate throws propagates the exception out of the step with the register file left as it was
/// before the destination write, so a failed function never publishes a half-computed result. Callers may
/// also let any other exception escape a delegate; this type merely gives function errors a single,
/// catchable shape.
/// </summary>
public sealed class VdbeFunctionException : InvalidOperationException
{
    public VdbeFunctionException(string message) : base(message)
    {
    }

    public VdbeFunctionException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

internal static class VdbeValueOperations
{
    public static SqlValue Compare(
        VdbeComparisonOperator operation,
        SqlValue left,
        SqlValue right,
        VdbeValueAffinity? leftAffinity,
        VdbeValueAffinity? rightAffinity,
        string? collation)
    {
        ApplyComparisonAffinities(ref left, ref right, leftAffinity, rightAffinity);
        if (operation is VdbeComparisonOperator.Is or VdbeComparisonOperator.IsNot)
        {
            var equal = left.Kind == SqlValueKind.Null || right.Kind == SqlValueKind.Null
                ? left.Kind == right.Kind
                : CompareValues(left, right, collation) == 0;
            return SqlValue.Integer((operation == VdbeComparisonOperator.Is) == equal ? 1 : 0);
        }

        if (left.Kind == SqlValueKind.Null || right.Kind == SqlValueKind.Null)
            return SqlValue.Null;

        var comparison = CompareValues(left, right, collation);
        var result = operation switch
        {
            VdbeComparisonOperator.Equal => comparison == 0,
            VdbeComparisonOperator.NotEqual => comparison != 0,
            VdbeComparisonOperator.LessThan => comparison < 0,
            VdbeComparisonOperator.LessThanOrEqual => comparison <= 0,
            VdbeComparisonOperator.GreaterThan => comparison > 0,
            VdbeComparisonOperator.GreaterThanOrEqual => comparison >= 0,
            _ => throw new InvalidOperationException($"Unknown comparison operator {operation}."),
        };
        return SqlValue.Integer(result ? 1 : 0);
    }

    public static SqlValue Cast(SqlValue value, string typeName)
        => EmbeddedDatabase.CastValue(value, typeName);

    public static SqlValue Not(SqlValue value)
        => value.Kind == SqlValueKind.Null
            ? SqlValue.Null
            : SqlValue.Integer(EmbeddedDatabase.IsTrue(value) ? 0 : 1);

    public static SqlValue IsTrue(SqlValue value, bool nullValue, bool invert)
    {
        if (value.Kind == SqlValueKind.Null)
            return SqlValue.Integer(nullValue ? 1 : 0);

        var truth = EmbeddedDatabase.IsTrue(value);
        return SqlValue.Integer(truth ^ invert ? 1 : 0);
    }

    public static SqlValue Concat(SqlValue left, SqlValue right)
    {
        if (left.Kind == SqlValueKind.Null || right.Kind == SqlValueKind.Null)
            return SqlValue.Null;

        return SqlValue.Text(EmbeddedDatabase.ToSqlText(left) + EmbeddedDatabase.ToSqlText(right));
    }

    public static SqlValue And(SqlValue left, SqlValue right)
    {
        if (left.Kind != SqlValueKind.Null && !EmbeddedDatabase.IsTrue(left)
            || right.Kind != SqlValueKind.Null && !EmbeddedDatabase.IsTrue(right))
        {
            return SqlValue.Integer(0);
        }

        return left.Kind == SqlValueKind.Null || right.Kind == SqlValueKind.Null
            ? SqlValue.Null
            : SqlValue.Integer(1);
    }

    public static SqlValue Or(SqlValue left, SqlValue right)
    {
        if (left.Kind != SqlValueKind.Null && EmbeddedDatabase.IsTrue(left)
            || right.Kind != SqlValueKind.Null && EmbeddedDatabase.IsTrue(right))
        {
            return SqlValue.Integer(1);
        }

        return left.Kind == SqlValueKind.Null || right.Kind == SqlValueKind.Null
            ? SqlValue.Null
            : SqlValue.Integer(0);
    }

    private static void ApplyComparisonAffinities(
        ref SqlValue left,
        ref SqlValue right,
        VdbeValueAffinity? leftAffinity,
        VdbeValueAffinity? rightAffinity)
    {
        if (leftAffinity is { } leftNumeric
            && IsNumeric(leftNumeric)
            && (rightAffinity is null || !IsNumeric(rightAffinity.Value)))
        {
            right = ApplyAffinity(leftNumeric, right);
        }
        else if (rightAffinity is { } rightNumeric
            && IsNumeric(rightNumeric)
            && (leftAffinity is null || !IsNumeric(leftAffinity.Value)))
        {
            left = ApplyAffinity(rightNumeric, left);
        }
        else if (leftAffinity == VdbeValueAffinity.Text && rightAffinity is null)
        {
            right = ApplyAffinity(VdbeValueAffinity.Text, right);
        }
        else if (rightAffinity == VdbeValueAffinity.Text && leftAffinity is null)
        {
            left = ApplyAffinity(VdbeValueAffinity.Text, left);
        }
    }

    private static SqlValue ApplyAffinity(VdbeValueAffinity affinity, SqlValue value)
        => EmbeddedTable.ApplyColumnAffinity(
            affinity switch
            {
                VdbeValueAffinity.Blob => ColumnAffinity.Blob,
                VdbeValueAffinity.Text => ColumnAffinity.Text,
                VdbeValueAffinity.Numeric => ColumnAffinity.Numeric,
                VdbeValueAffinity.Integer => ColumnAffinity.Integer,
                VdbeValueAffinity.Real => ColumnAffinity.Real,
                _ => throw new InvalidOperationException($"Unknown value affinity {affinity}."),
            },
            value);

    private static bool IsNumeric(VdbeValueAffinity affinity)
        => affinity is VdbeValueAffinity.Integer or VdbeValueAffinity.Real or VdbeValueAffinity.Numeric;

    private static int CompareValues(SqlValue left, SqlValue right, string? collation)
    {
        if (left.Kind == SqlValueKind.Integer && right.Kind == SqlValueKind.Integer)
            return left.AsInteger().CompareTo(right.AsInteger());
        if (left.Kind == SqlValueKind.Integer && right.Kind == SqlValueKind.Real)
            return CompareIntegerAndReal(left.AsInteger(), right.AsReal());
        if (left.Kind == SqlValueKind.Real && right.Kind == SqlValueKind.Integer)
            return -CompareIntegerAndReal(right.AsInteger(), left.AsReal());
        if (left.Kind == SqlValueKind.Real && right.Kind == SqlValueKind.Real)
            return left.AsReal().CompareTo(right.AsReal());
        if (left.Kind == SqlValueKind.Text && right.Kind == SqlValueKind.Text)
        {
            if (collation is null || string.Equals(collation, "BINARY", StringComparison.OrdinalIgnoreCase))
                return string.CompareOrdinal(left.AsText(), right.AsText());
            if (string.Equals(collation, "NOCASE", StringComparison.OrdinalIgnoreCase))
                return SqliteIndexRecordComparer.CompareNoCaseText(left.AsText(), right.AsText());
            if (string.Equals(collation, "RTRIM", StringComparison.OrdinalIgnoreCase))
                return SqliteIndexRecordComparer.CompareRTrimText(left.AsText(), right.AsText());

            throw new InvalidOperationException($"Unsupported compiled collation {collation}.");
        }
        if (left.Kind == SqlValueKind.Blob && right.Kind == SqlValueKind.Blob)
            return left.AsBlob().Span.SequenceCompareTo(right.AsBlob().Span);

        return left.Kind.CompareTo(right.Kind);
    }

    private static int CompareIntegerAndReal(long integer, double real)
    {
        if (real < long.MinValue)
            return 1;
        if (real >= -(double)long.MinValue)
            return -1;

        var truncated = (long)real;
        var comparison = integer.CompareTo(truncated);
        if (comparison != 0)
            return comparison;

        return real == truncated ? 0 : real > truncated ? -1 : 1;
    }
}

/// <summary>
/// Binds a read cursor to the concrete rows it iterates at execution time. The
/// program references the cursor by index; the row source is supplied to the
/// <see cref="ResumableStatement"/> so the bytecode stays free of live data.
/// </summary>
public sealed class VdbeCursorSource
{
    public VdbeCursorSource(IReadOnlyList<SqlValue[]> rows, IReadOnlyList<long>? rowIds = null)
        : this(rows, rowIds, largestRowId: null)
    {
    }

    internal VdbeCursorSource(
        IReadOnlyList<SqlValue[]> rows,
        IReadOnlyList<long>? rowIds,
        Func<long?>? largestRowId)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rowIds is not null && rowIds.Count != rows.Count)
        {
            throw new ArgumentException(
                "A cursor source with rowids must have exactly one rowid per row.",
                nameof(rowIds));
        }

        Rows = rows;
        RowIds = rowIds;
        LargestRowId = largestRowId;
    }

    public IReadOnlyList<SqlValue[]> Rows { get; }

    /// <summary>
    /// Optional hidden rowids aligned with <see cref="Rows"/>. A source without
    /// these is a value-only cursor and cannot satisfy <see cref="RowIdInstruction"/>.
    /// </summary>
    public IReadOnlyList<long>? RowIds { get; }

    internal Func<long?>? LargestRowId { get; }
}

/// <summary>
/// Runtime binding for a VDBE virtual-table cursor. It carries a concrete table instance instead of
/// a module name so bytecode never uses reflection or string-based type activation.
/// </summary>
public sealed class VdbeVirtualTableBinding
{
    public VdbeVirtualTableBinding(ManagedVirtualTable table)
        => Table = table ?? throw new ArgumentNullException(nameof(table));

    public ManagedVirtualTable Table { get; }
}

public enum VdbeJoinKind
{
    Inner,
    Left,
    Right,
    Full,
}

/// <summary>A materialized row in a join plan, including one optional hidden rowid per leaf source.</summary>
public sealed class VdbeJoinRow
{
    public VdbeJoinRow(SqlValue[] values, long?[] rowIds)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(rowIds);
        Values = values;
        RowIds = rowIds;
    }

    public SqlValue[] Values { get; }

    public long?[] RowIds { get; }
}

public delegate bool VdbeJoinCondition(VdbeJoinRow left, VdbeJoinRow right, VdbeJoinRow combined);

public delegate bool VdbeJoinedRowPredicate(VdbeJoinRow row);

public delegate string VdbeJoinGroupKey(VdbeJoinRow row);

/// <summary>A node in a left-deep materializing join plan.</summary>
public abstract class VdbeJoinPlanNode
{
    protected VdbeJoinPlanNode(int columnCount, int sourceCount)
    {
        if (columnCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(columnCount));
        if (sourceCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceCount));

        ColumnCount = columnCount;
        SourceCount = sourceCount;
    }

    public int ColumnCount { get; }

    public int SourceCount { get; }

    internal abstract IReadOnlyList<VdbeJoinRow> Materialize(int? maximumRows);

    /// <summary>
    /// Lazily enumerates the joined rows. The runtime join cursor streams this instead of
    /// materializing, so a cross/outer join of two large tables never buffers the full L*R
    /// output before the first row is consumed.
    /// </summary>
    internal abstract IEnumerable<VdbeJoinRow> Enumerate(int? maximumRows);

    internal virtual IEnumerable<VdbeJoinRow> Enumerate(
        int? maximumRows,
        VdbeJoinExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Enumerate(maximumRows);
    }
}

/// <summary>A base-table leaf in a materializing join plan.</summary>
public sealed class VdbeJoinScanPlan : VdbeJoinPlanNode
{
    public VdbeJoinScanPlan(string tableName, int columnCount, VdbeCursorSource source)
        : base(columnCount, sourceCount: 1)
    {
        ArgumentException.ThrowIfNullOrEmpty(tableName);
        ArgumentNullException.ThrowIfNull(source);
        TableName = tableName;
        Source = source;
    }

    public string TableName { get; }

    public VdbeCursorSource Source { get; }

    internal override IReadOnlyList<VdbeJoinRow> Materialize(int? maximumRows) => Enumerate(maximumRows).ToList();

    internal override IEnumerable<VdbeJoinRow> Enumerate(int? maximumRows)
        => Enumerate(maximumRows, VdbeJoinExecutionContext.CreateDefault());

    internal override IEnumerable<VdbeJoinRow> Enumerate(
        int? maximumRows,
        VdbeJoinExecutionContext context)
    {
        if (Source.RowIds is not null && Source.RowIds.Count != Source.Rows.Count)
        {
            throw new InvalidOperationException(
                $"Join source '{TableName}' has {Source.Rows.Count} rows but {Source.RowIds.Count} rowids.");
        }

        var count = maximumRows is { } maximum
            ? Math.Min(Source.Rows.Count, maximum)
            : Source.Rows.Count;
        for (var index = 0; index < count; index++)
        {
            context.ThrowIfCancellationRequested();
            var values = Source.Rows[index];
            if (values.Length != ColumnCount)
            {
                throw new InvalidOperationException(
                    $"Join source '{TableName}' declares {ColumnCount} columns but row {index} has {values.Length}.");
            }

            yield return new VdbeJoinRow(
                [.. values],
                [Source.RowIds is null ? null : Source.RowIds[index]]);
        }
    }
}

/// <summary>Thread-safe evidence that an index join performed bounded probes rather than scans.</summary>
public sealed class VdbeJoinIndexSeekMetrics
{
    private long _plansCreated;
    private long _indexRowsMaterialized;
    private long _seeksAttempted;
    private long _keyComparisons;
    private long _candidateRowsVisited;
    private long _automaticIndexesBuilt;
    private long _durableCursorPlans;
    private long _indexPagesRead;
    private long _tableRowsFetched;
    private long _overlayRowsExamined;
    private long _baseRowsSuppressed;

    public long PlansCreated => Interlocked.Read(ref _plansCreated);

    public long IndexRowsMaterialized => Interlocked.Read(ref _indexRowsMaterialized);

    public long SeeksAttempted => Interlocked.Read(ref _seeksAttempted);

    public long KeyComparisons => Interlocked.Read(ref _keyComparisons);

    public long CandidateRowsVisited => Interlocked.Read(ref _candidateRowsVisited);

    public long AutomaticIndexesBuilt => Interlocked.Read(ref _automaticIndexesBuilt);

    public long DurableCursorPlans => Interlocked.Read(ref _durableCursorPlans);

    public long IndexPagesRead => Interlocked.Read(ref _indexPagesRead);

    public long TableRowsFetched => Interlocked.Read(ref _tableRowsFetched);

    /// <summary>Visible MVCC/overlay candidate rows examined while merging a durable base stream.</summary>
    public long OverlayRowsExamined => Interlocked.Read(ref _overlayRowsExamined);

    /// <summary>Durable base rows skipped because a visible overlay effect shadows their identity.</summary>
    public long BaseRowsSuppressed => Interlocked.Read(ref _baseRowsSuppressed);

    internal void PlanCreated(int rowCount, bool automatic = false)
    {
        Interlocked.Increment(ref _plansCreated);
        Interlocked.Add(ref _indexRowsMaterialized, rowCount);
        if (automatic)
            Interlocked.Increment(ref _automaticIndexesBuilt);
    }

    internal void DurableCursorPlanCreated() => Interlocked.Increment(ref _durableCursorPlans);

    internal void SeekAttempted() => Interlocked.Increment(ref _seeksAttempted);

    internal void KeyCompared() => Interlocked.Increment(ref _keyComparisons);

    internal void CandidateVisited() => Interlocked.Increment(ref _candidateRowsVisited);

    internal void IndexPageRead() => Interlocked.Increment(ref _indexPagesRead);

    internal void TableRowFetched() => Interlocked.Increment(ref _tableRowsFetched);

    internal void OverlayRowExamined() => Interlocked.Increment(ref _overlayRowsExamined);

    internal void BaseRowSuppressed() => Interlocked.Increment(ref _baseRowsSuppressed);

    internal void Reset()
    {
        Interlocked.Exchange(ref _plansCreated, 0);
        Interlocked.Exchange(ref _indexRowsMaterialized, 0);
        Interlocked.Exchange(ref _seeksAttempted, 0);
        Interlocked.Exchange(ref _keyComparisons, 0);
        Interlocked.Exchange(ref _candidateRowsVisited, 0);
        Interlocked.Exchange(ref _automaticIndexesBuilt, 0);
        Interlocked.Exchange(ref _durableCursorPlans, 0);
        Interlocked.Exchange(ref _indexPagesRead, 0);
        Interlocked.Exchange(ref _tableRowsFetched, 0);
        Interlocked.Exchange(ref _overlayRowsExamined, 0);
        Interlocked.Exchange(ref _baseRowsSuppressed, 0);
    }
}

internal interface IVdbeJoinSeekPlan : IDisposable
{
    string SearchDescription { get; }

    void Open();

    IEnumerable<VdbeJoinRow> Seek(VdbeJoinRow outer);
}

/// <summary>
/// Statement-scoped materialized index view. Keys and base rows are aligned in index order;
/// every outer row performs a lower-bound search followed by an equality-bounded walk. It is the
/// fallback for non-durable snapshots and the build-once representation of automatic indexes.
/// </summary>
public sealed class VdbeJoinIndexScanPlan : VdbeJoinPlanNode, IVdbeJoinSeekPlan
{
    private readonly Lazy<(VdbeCursorSource Source, IReadOnlyList<SqlValue[]> Keys)> _rows;
    private readonly Func<VdbeJoinRow, SqlValue[]?> _buildSeekKey;
    private readonly Func<SqlValue[], SqlValue[], int> _comparePrefix;
    private readonly VdbeJoinIndexSeekMetrics _metrics;
    private readonly bool _automatic;

    public VdbeJoinIndexScanPlan(
        string tableName,
        string indexName,
        string searchDescription,
        int columnCount,
        VdbeCursorSource source,
        IReadOnlyList<SqlValue[]> keys,
        Func<VdbeJoinRow, SqlValue[]?> buildSeekKey,
        Func<SqlValue[], SqlValue[], int> comparePrefix,
        VdbeJoinIndexSeekMetrics metrics)
        : this(
            tableName,
            indexName,
            searchDescription,
            columnCount,
            () => (source, keys),
            buildSeekKey,
            comparePrefix,
            metrics,
            automatic: false)
    {
    }

    internal VdbeJoinIndexScanPlan(
        string tableName,
        string indexName,
        string searchDescription,
        int columnCount,
        Func<(VdbeCursorSource Source, IReadOnlyList<SqlValue[]> Keys)> materialize,
        Func<VdbeJoinRow, SqlValue[]?> buildSeekKey,
        Func<SqlValue[], SqlValue[], int> comparePrefix,
        VdbeJoinIndexSeekMetrics metrics,
        bool automatic = false)
        : base(columnCount, sourceCount: 1)
    {
        ArgumentException.ThrowIfNullOrEmpty(tableName);
        ArgumentException.ThrowIfNullOrEmpty(indexName);
        ArgumentException.ThrowIfNullOrEmpty(searchDescription);
        ArgumentNullException.ThrowIfNull(materialize);
        ArgumentNullException.ThrowIfNull(buildSeekKey);
        ArgumentNullException.ThrowIfNull(comparePrefix);
        ArgumentNullException.ThrowIfNull(metrics);

        TableName = tableName;
        IndexName = indexName;
        SearchDescription = searchDescription;
        _buildSeekKey = buildSeekKey;
        _comparePrefix = comparePrefix;
        _metrics = metrics;
        _automatic = automatic;
        _rows = new Lazy<(VdbeCursorSource Source, IReadOnlyList<SqlValue[]> Keys)>(
            () =>
            {
                var rows = materialize();
                ArgumentNullException.ThrowIfNull(rows.Source);
                ArgumentNullException.ThrowIfNull(rows.Keys);
                if (rows.Source.Rows.Count != rows.Keys.Count)
                {
                    throw new InvalidOperationException(
                        "Index keys must align with base rows.");
                }

                _metrics.PlanCreated(rows.Source.Rows.Count, _automatic);
                return rows;
            },
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string TableName { get; }

    public string IndexName { get; }

    public string SearchDescription { get; }

    public VdbeCursorSource Source => _rows.Value.Source;

    internal override IReadOnlyList<VdbeJoinRow> Materialize(int? maximumRows)
        => Enumerate(maximumRows).ToArray();

    internal override IEnumerable<VdbeJoinRow> Enumerate(int? maximumRows)
    {
        var rows = _rows.Value;
        var count = maximumRows is { } maximum
            ? Math.Min(rows.Source.Rows.Count, maximum)
            : rows.Source.Rows.Count;
        for (var index = 0; index < count; index++)
        {
            yield return new VdbeJoinRow(
                [.. rows.Source.Rows[index]],
                [rows.Source.RowIds is null ? null : rows.Source.RowIds[index]]);
        }
    }

    public IEnumerable<VdbeJoinRow> Seek(VdbeJoinRow outer)
    {
        _metrics.SeekAttempted();
        var seekKey = _buildSeekKey(outer);
        if (seekKey is null)
            yield break;

        var rows = _rows.Value;
        var low = 0;
        var high = rows.Keys.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            _metrics.KeyCompared();
            if (_comparePrefix(rows.Keys[middle], seekKey) < 0)
                low = middle + 1;
            else
                high = middle;
        }

        for (var index = low; index < rows.Keys.Count; index++)
        {
            _metrics.KeyCompared();
            if (_comparePrefix(rows.Keys[index], seekKey) != 0)
                yield break;

            _metrics.CandidateVisited();
            yield return new VdbeJoinRow(
                [.. rows.Source.Rows[index]],
                [rows.Source.RowIds is null ? null : rows.Source.RowIds[index]]);
        }
    }

    public void Dispose()
    {
    }

    public void Open()
    {
    }
}

internal sealed class VdbeJoinAutomaticIndexPlan : VdbeJoinPlanNode, IVdbeJoinSeekPlan
{
    private readonly Lazy<(
        VdbeCursorSource Source,
        IReadOnlyDictionary<string, List<int>> Buckets)> _index;
    private readonly Func<VdbeJoinRow, SqlValue[]?> _buildSeekKey;
    private readonly Func<SqlValue[], string?> _canonicalizeKey;
    private readonly VdbeJoinIndexSeekMetrics _metrics;

    public VdbeJoinAutomaticIndexPlan(
        string tableName,
        string searchDescription,
        int columnCount,
        Func<(VdbeCursorSource Source, IReadOnlyList<SqlValue[]> Keys)> materialize,
        Func<VdbeJoinRow, SqlValue[]?> buildSeekKey,
        Func<SqlValue[], string?> canonicalizeKey,
        VdbeJoinIndexSeekMetrics metrics)
        : base(columnCount, sourceCount: 1)
    {
        ArgumentException.ThrowIfNullOrEmpty(tableName);
        ArgumentException.ThrowIfNullOrEmpty(searchDescription);
        ArgumentNullException.ThrowIfNull(materialize);
        ArgumentNullException.ThrowIfNull(buildSeekKey);
        ArgumentNullException.ThrowIfNull(canonicalizeKey);
        ArgumentNullException.ThrowIfNull(metrics);
        TableName = tableName;
        SearchDescription = searchDescription;
        _buildSeekKey = buildSeekKey;
        _canonicalizeKey = canonicalizeKey;
        _metrics = metrics;
        _index = new Lazy<(
            VdbeCursorSource Source,
            IReadOnlyDictionary<string, List<int>> Buckets)>(
            () =>
            {
                var rows = materialize();
                if (rows.Source.Rows.Count != rows.Keys.Count)
                    throw new InvalidOperationException("Automatic-index keys must align with source rows.");
                var buckets = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                for (var position = 0; position < rows.Keys.Count; position++)
                {
                    var key = _canonicalizeKey(rows.Keys[position]);
                    if (key is null)
                        continue;
                    if (!buckets.TryGetValue(key, out var bucket))
                    {
                        bucket = [];
                        buckets.Add(key, bucket);
                    }
                    bucket.Add(position);
                }

                _metrics.PlanCreated(rows.Source.Rows.Count, automatic: true);
                return (rows.Source, buckets);
            },
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string TableName { get; }

    public string SearchDescription { get; }

    internal override IReadOnlyList<VdbeJoinRow> Materialize(int? maximumRows)
        => Enumerate(maximumRows).ToArray();

    internal override IEnumerable<VdbeJoinRow> Enumerate(int? maximumRows)
    {
        var source = _index.Value.Source;
        var count = maximumRows is { } maximum
            ? Math.Min(source.Rows.Count, maximum)
            : source.Rows.Count;
        for (var position = 0; position < count; position++)
        {
            yield return new VdbeJoinRow(
                [.. source.Rows[position]],
                [source.RowIds is null ? null : source.RowIds[position]]);
        }
    }

    public IEnumerable<VdbeJoinRow> Seek(VdbeJoinRow outer)
    {
        _metrics.SeekAttempted();
        var values = _buildSeekKey(outer);
        if (values is null || _canonicalizeKey(values) is not { } key)
            yield break;

        var index = _index.Value;
        if (!index.Buckets.TryGetValue(key, out var positions))
            yield break;
        foreach (var position in positions)
        {
            _metrics.CandidateVisited();
            yield return new VdbeJoinRow(
                [.. index.Source.Rows[position]],
                [index.Source.RowIds is null ? null : index.Source.RowIds[position]]);
        }
    }

    public void Dispose()
    {
    }

    public void Open()
    {
    }
}

internal sealed class VdbeJoinPagerIndexSeekPlan : VdbeJoinPlanNode, IVdbeJoinSeekPlan
{
    private readonly Func<VdbeJoinRow, SqlValue[]?> _buildSeekKey;
    private readonly Func<SqlValue[], IEnumerable<VdbeJoinRow>> _seek;
    private readonly Action _open;
    private readonly Action _close;
    private readonly VdbeJoinIndexSeekMetrics _metrics;
    private int _started;

    public VdbeJoinPagerIndexSeekPlan(
        string tableName,
        string indexName,
        string searchDescription,
        int columnCount,
        Func<VdbeJoinRow, SqlValue[]?> buildSeekKey,
        Func<SqlValue[], IEnumerable<VdbeJoinRow>> seek,
        Action open,
        Action close,
        VdbeJoinIndexSeekMetrics metrics)
        : base(columnCount, sourceCount: 1)
    {
        ArgumentException.ThrowIfNullOrEmpty(tableName);
        ArgumentException.ThrowIfNullOrEmpty(indexName);
        ArgumentException.ThrowIfNullOrEmpty(searchDescription);
        ArgumentNullException.ThrowIfNull(buildSeekKey);
        ArgumentNullException.ThrowIfNull(seek);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(close);
        ArgumentNullException.ThrowIfNull(metrics);
        TableName = tableName;
        IndexName = indexName;
        SearchDescription = searchDescription;
        _buildSeekKey = buildSeekKey;
        _seek = seek;
        _open = open;
        _close = close;
        _metrics = metrics;
    }

    public string TableName { get; }

    public string IndexName { get; }

    public string SearchDescription { get; }

    internal override IReadOnlyList<VdbeJoinRow> Materialize(int? maximumRows)
        => Enumerate(maximumRows).ToArray();

    internal override IEnumerable<VdbeJoinRow> Enumerate(int? maximumRows)
        => throw new InvalidOperationException("A pager index seek requires an outer-row key.");

    public IEnumerable<VdbeJoinRow> Seek(VdbeJoinRow outer)
    {
        _metrics.SeekAttempted();
        var seekKey = _buildSeekKey(outer);
        if (seekKey is null)
            yield break;

        if (Interlocked.Exchange(ref _started, 1) == 0)
            _metrics.DurableCursorPlanCreated();
        foreach (var row in _seek(seekKey))
        {
            _metrics.CandidateVisited();
            yield return row;
        }
    }

    public void Dispose() => _close();

    public void Open() => _open();
}

/// <summary>
/// Optional equijoin probe for <see cref="VdbeJoinOperatorPlan"/>: hashes one side
/// once and probes from the other. The full <see cref="VdbeJoinCondition"/> still runs so
/// affinity/collation edge cases stay correct; the probe is only a candidate filter.
/// </summary>
public sealed class VdbeJoinEquiProbe
{
    public VdbeJoinEquiProbe(
        Func<VdbeJoinRow, string?> buildLeftKey,
        Func<VdbeJoinRow, string?> buildRightKey)
    {
        BuildLeftKey = buildLeftKey ?? throw new ArgumentNullException(nameof(buildLeftKey));
        BuildRightKey = buildRightKey ?? throw new ArgumentNullException(nameof(buildRightKey));
    }

    public Func<VdbeJoinRow, string?> BuildLeftKey { get; }

    public Func<VdbeJoinRow, string?> BuildRightKey { get; }
}

/// <summary>An INNER, LEFT, RIGHT, or FULL node in a materializing join plan.</summary>
public sealed class VdbeJoinOperatorPlan : VdbeJoinPlanNode
{
    public VdbeJoinOperatorPlan(
        VdbeJoinPlanNode left,
        VdbeJoinPlanNode right,
        VdbeJoinKind kind,
        VdbeJoinCondition? condition,
        VdbeJoinEquiProbe? equiProbe = null,
        bool hashBuildRight = true)
        : base(
            checked((left ?? throw new ArgumentNullException(nameof(left))).ColumnCount
                + (right ?? throw new ArgumentNullException(nameof(right))).ColumnCount),
            checked(left.SourceCount + right.SourceCount))
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (!hashBuildRight && kind is not VdbeJoinKind.Inner)
            throw new ArgumentException("Hash-build-left is only valid for INNER joins.", nameof(hashBuildRight));
        if (!hashBuildRight && equiProbe is null)
            throw new ArgumentException("Hash-build-left requires an equijoin probe.", nameof(hashBuildRight));

        Left = left;
        Right = right;
        Kind = kind;
        Condition = condition;
        EquiProbe = equiProbe;
        HashBuildRight = hashBuildRight;
    }

    public VdbeJoinPlanNode Left { get; }

    public VdbeJoinPlanNode Right { get; }

    public VdbeJoinKind Kind { get; }

    public VdbeJoinCondition? Condition { get; }

    public VdbeJoinEquiProbe? EquiProbe { get; }

    /// <summary>
    /// When true (default), the right input is hashed/materialized and the left streams.
    /// When false (INNER equijoin only), the left is hashed and the right streams — used when
    /// cardinality estimates prefer building the smaller left side.
    /// </summary>
    public bool HashBuildRight { get; }

    internal override IReadOnlyList<VdbeJoinRow> Materialize(int? maximumRows) => Enumerate(maximumRows).ToList();

    internal override IEnumerable<VdbeJoinRow> Enumerate(int? maximumRows)
        => Enumerate(maximumRows, VdbeJoinExecutionContext.CreateDefault());

    internal override IEnumerable<VdbeJoinRow> Enumerate(
        int? maximumRows,
        VdbeJoinExecutionContext context)
    {
        if (Right is IVdbeJoinSeekPlan indexSeek)
            return EnumerateIndexSeekRight(indexSeek, maximumRows, context);

        if (EquiProbe is not null)
            return VdbeHashJoinRuntime.Enumerate(this, maximumRows, context);

        // Default: materialize right once; stream left (OOM-safe for large outer × small inner).
        // INNER equijoin may flip to hash-build left when stats say left is smaller.
        if (!HashBuildRight)
            return EnumerateHashBuildLeft(maximumRows, context);
        return EnumerateHashBuildRight(maximumRows, context);
    }

    private IEnumerable<VdbeJoinRow> EnumerateIndexSeekRight(
        IVdbeJoinSeekPlan indexSeek,
        int? maximumRows,
        VdbeJoinExecutionContext context)
    {
        if (Kind is not (VdbeJoinKind.Inner or VdbeJoinKind.Left))
            throw new InvalidOperationException("An index-seek right input requires an INNER or LEFT join.");

        try
        {
            indexSeek.Open();
            var emitted = 0;
            foreach (var left in Left.Enumerate(maximumRows: null, context))
            {
                var matched = false;
                foreach (var right in indexSeek.Seek(left))
                {
                    var combined = Combine(left, right);
                    if (Condition is not null && !Condition(left, right, combined))
                        continue;

                    matched = true;
                    yield return combined;
                    emitted++;
                    if (maximumRows is { } maximum && emitted >= maximum)
                        yield break;
                }

                if (Kind == VdbeJoinKind.Left && !matched)
                {
                    yield return Combine(left, NullRow(Right));
                    emitted++;
                    if (maximumRows is { } maximum && emitted >= maximum)
                        yield break;
                }
            }
        }
        finally
        {
            indexSeek.Dispose();
        }
    }

    private IEnumerable<VdbeJoinRow> EnumerateHashBuildRight(
        int? maximumRows,
        VdbeJoinExecutionContext context)
    {
        var rightRows = Right.Enumerate(maximumRows: null, context).ToList();
        var rightMatched = Kind is VdbeJoinKind.Right or VdbeJoinKind.Full
            ? new bool[rightRows.Count]
            : null;
        var emitted = 0;

        // Optional equijoin hash: bucket right rows by canonical key, probe per left row.
        Dictionary<string, List<int>>? buckets = null;
        if (EquiProbe is not null && rightRows.Count > 0)
        {
            buckets = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (var rightIndex = 0; rightIndex < rightRows.Count; rightIndex++)
            {
                var key = EquiProbe.BuildRightKey(rightRows[rightIndex]);
                if (key is null)
                    continue;
                if (!buckets.TryGetValue(key, out var bucket))
                {
                    bucket = [];
                    buckets[key] = bucket;
                }

                bucket.Add(rightIndex);
            }
        }

        foreach (var left in Left.Enumerate(maximumRows: null, context))
        {
            context.ThrowIfCancellationRequested();
            var matched = false;
            IEnumerable<int> candidateIndices;
            if (buckets is not null && EquiProbe is not null)
            {
                var key = EquiProbe.BuildLeftKey(left);
                if (key is not null && buckets.TryGetValue(key, out var bucket))
                    candidateIndices = bucket;
                else
                    candidateIndices = Array.Empty<int>();
            }
            else
            {
                candidateIndices = Enumerable.Range(0, rightRows.Count);
            }

            foreach (var rightIndex in candidateIndices)
            {
                var right = rightRows[rightIndex];
                var combined = Combine(left, right);
                if (Condition is not null && !Condition(left, right, combined))
                    continue;

                matched = true;
                if (rightMatched is not null)
                    rightMatched[rightIndex] = true;
                yield return combined;
                if (maximumRows is { } maximum && ++emitted >= maximum)
                    yield break;
            }

            if (!matched && Kind is VdbeJoinKind.Left or VdbeJoinKind.Full)
            {
                yield return Combine(left, NullRow(Right));
                if (maximumRows is { } maximum && ++emitted >= maximum)
                    yield break;
            }
        }

        if (Kind is VdbeJoinKind.Right or VdbeJoinKind.Full)
        {
            var nullLeft = NullRow(Left);
            for (var rightIndex = 0; rightIndex < rightRows.Count; rightIndex++)
            {
                if (rightMatched![rightIndex])
                    continue;

                yield return Combine(nullLeft, rightRows[rightIndex]);
                if (maximumRows is { } maximum && ++emitted >= maximum)
                    yield break;
            }
        }
    }

    private IEnumerable<VdbeJoinRow> EnumerateHashBuildLeft(
        int? maximumRows,
        VdbeJoinExecutionContext context)
    {
        // INNER only: materialize left, stream right, probe left buckets. Output column order
        // stays left||right via Combine.
        var leftRows = Left.Enumerate(maximumRows: null, context).ToList();
        var emitted = 0;
        Dictionary<string, List<int>>? buckets = null;
        if (EquiProbe is not null && leftRows.Count > 0)
        {
            buckets = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (var leftIndex = 0; leftIndex < leftRows.Count; leftIndex++)
            {
                var key = EquiProbe.BuildLeftKey(leftRows[leftIndex]);
                if (key is null)
                    continue;
                if (!buckets.TryGetValue(key, out var bucket))
                {
                    bucket = [];
                    buckets[key] = bucket;
                }

                bucket.Add(leftIndex);
            }
        }

        foreach (var right in Right.Enumerate(maximumRows: null, context))
        {
            context.ThrowIfCancellationRequested();
            IEnumerable<int> candidateIndices;
            if (buckets is not null && EquiProbe is not null)
            {
                var key = EquiProbe.BuildRightKey(right);
                if (key is not null && buckets.TryGetValue(key, out var bucket))
                    candidateIndices = bucket;
                else
                    candidateIndices = Array.Empty<int>();
            }
            else
            {
                candidateIndices = Enumerable.Range(0, leftRows.Count);
            }

            foreach (var leftIndex in candidateIndices)
            {
                var left = leftRows[leftIndex];
                var combined = Combine(left, right);
                if (Condition is not null && !Condition(left, right, combined))
                    continue;

                yield return combined;
                if (maximumRows is { } maximum && ++emitted >= maximum)
                    yield break;
            }
        }
    }

    private static VdbeJoinRow Combine(VdbeJoinRow left, VdbeJoinRow right)
        => new(
            [.. left.Values, .. right.Values],
            [.. left.RowIds, .. right.RowIds]);

    private static VdbeJoinRow NullRow(VdbeJoinPlanNode node)
        => new(
            Enumerable.Repeat(SqlValue.Null, node.ColumnCount).ToArray(),
            new long?[node.SourceCount]);
}

/// <summary>
/// A complete materializing join cursor plan. The root reproduces recursive FROM/JOIN ordering, the
/// optional filter runs over every joined row before the cursor becomes visible, and an optional group
/// key appends a first-seen ordinal used by grouped aggregation.
/// </summary>
public sealed class VdbeJoinPlan
{
    public VdbeJoinPlan(
        VdbeJoinPlanNode root,
        string description,
        VdbeJoinedRowPredicate? filter = null,
        VdbeJoinGroupKey? groupKey = null,
        int? maximumRows = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrEmpty(description);
        if (root is not VdbeJoinOperatorPlan)
            throw new ArgumentException("A join cursor plan root must be a join operator.", nameof(root));
        if (maximumRows is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumRows));
        if (filter is not null && maximumRows is not null)
        {
            throw new ArgumentException(
                "A join plan cannot cap raw joined rows before applying a post-join filter.",
                nameof(maximumRows));
        }

        Root = root;
        Description = description;
        Filter = filter;
        GroupKey = groupKey;
        MaximumRows = maximumRows;
    }

    public VdbeJoinPlanNode Root { get; }

    public string Description { get; }

    public VdbeJoinedRowPredicate? Filter { get; }

    public VdbeJoinGroupKey? GroupKey { get; }

    public int? MaximumRows { get; }

    public int ColumnCount => Root.ColumnCount;

    public int SourceCount => Root.SourceCount;

    public int RecordColumnCount => checked(ColumnCount + SourceCount + (GroupKey is null ? 0 : 1));

    internal IReadOnlyList<SqlValue[]> Materialize() => Enumerate().ToList();

    /// <summary>
    /// Lazily enumerates the joined, filtered, group-keyed records as <see cref="SqlValue"/> arrays.
    /// The runtime join cursor streams this so the full result is never buffered before the first
    /// row is consumed; the group-key ordinal map is built as rows arrive.
    /// </summary>
    internal IEnumerable<SqlValue[]> Enumerate()
        => Enumerate(VdbeJoinExecutionContext.CreateDefault());

    internal IEnumerable<SqlValue[]> Enumerate(VdbeJoinExecutionContext context)
    {
        var groupOrdinals = GroupKey is null
            ? null
            : new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var raw in Root.Enumerate(MaximumRows, context))
        {
            context.ThrowIfCancellationRequested();
            if (Filter is not null && !Filter(raw))
                continue;

            var record = new SqlValue[RecordColumnCount];
            Array.Copy(raw.Values, record, raw.Values.Length);
            for (var index = 0; index < raw.RowIds.Length; index++)
            {
                record[ColumnCount + index] = raw.RowIds[index] is { } rowId
                    ? SqlValue.Integer(rowId)
                    : SqlValue.Null;
            }

            if (GroupKey is not null)
            {
                var key = GroupKey(raw);
                if (!groupOrdinals!.TryGetValue(key, out var ordinal))
                {
                    ordinal = groupOrdinals.Count;
                    groupOrdinals.Add(key, ordinal);
                }

                record[^1] = SqlValue.Integer(ordinal);
            }

            yield return record;
        }
    }
}

/// <summary>
/// The row and rowid a mutation opcode (<c>Insert</c>/<c>Update</c>) materializes
/// for its cursor, so a following <c>Column</c>/<c>RowId</c> observes the written
/// values rather than the pre-mutation source row.
/// </summary>
public readonly record struct VdbeRowMutation(SqlValue[] Row, long RowId);

/// <summary>
/// Binds a write cursor to the concrete rows an INSERT/UPDATE/DELETE program
/// touches at execution time. The program references the cursor by index and
/// invokes these delegates through the mutation opcodes; the executor stays free
/// of catalog types so the emitted bytecode owns only control flow.
/// </summary>
/// <remarks>
/// The caller supplies the delegates so the compiled write path reuses the exact
/// row-building, constraint, and commit logic the tree-walking evaluator uses.
/// <see cref="GetRow"/>/<see cref="GetRowId"/> expose the pre-mutation scan rows
/// (UPDATE/DELETE); INSERT never scans, so they may be <see langword="null"/>.
/// </remarks>
public sealed class VdbeWriteTarget
{
    /// <summary>The catalog name of the mutated table, surfaced for EXPLAIN.</summary>
    public required string TableName { get; init; }

    /// <summary>The number of rows the cursor iterates: scanned rows for
    /// UPDATE/DELETE, or the count of inserted rows for INSERT.</summary>
    public required int RowCount { get; init; }

    /// <summary>
    /// Reports the cursor's row count at the moment it is asked, superseding <see cref="RowCount"/>.
    /// </summary>
    /// <remarks>
    /// A binding whose rows the program itself writes cannot state a count up front: a schema program
    /// inserts a <c>sqlite_schema</c> row and then scans the row set it just extended. Supplying the count
    /// as a delegate keeps such a cursor honest without forcing every write target to become dynamic.
    /// </remarks>
    public Func<int>? LiveRowCount { get; init; }

    /// <summary>Reads the pre-mutation row at a scan position (UPDATE/DELETE).</summary>
    public Func<int, SqlValue[]>? GetRow { get; init; }

    /// <summary>Reads the pre-mutation rowid at a scan position (UPDATE/DELETE).</summary>
    public Func<int, long>? GetRowId { get; init; }

    /// <summary>Marks the scan row at a position for deletion (DELETE).</summary>
    public Action<int>? DeleteRow { get; init; }

    /// <summary>
    /// Deletes the scan row at a position and returns whether it still existed. This is used by a
    /// snapshot-scanning cascade action, where an earlier recursive action may already have removed a row
    /// that was present when the current action began.
    /// </summary>
    public Func<int, bool>? TryDeleteRow { get; init; }

    /// <summary>Builds and records the new row for a position, returning the values
    /// a following <c>Column</c>/<c>RowId</c> should observe (INSERT/UPDATE).</summary>
    public Func<int, VdbeRowMutation>? MutateRow { get; init; }

    /// <summary>
    /// Stores one register-built record under the supplied rowid and returns the rowid actually written.
    /// This is the binding for the register-backed <see cref="InsertInstruction"/> form: unlike
    /// <see cref="MutateRow"/> it takes the row from the program's registers instead of a scan position,
    /// so a program can write rows it constructed rather than rows it read.
    /// </summary>
    public Func<long, SqlValue[], long>? InsertRecord { get; init; }

    /// <summary>Applies all buffered mutations to the table under the statement's
    /// constraints, returning the last inserted rowid (INSERT) or
    /// <see langword="null"/> (UPDATE/DELETE).</summary>
    public required Func<long?> Commit { get; init; }
}

/// <summary>
/// The compiled program and fixed runtime bindings invoked by a <see cref="ProgramInstruction"/>.
/// Parameters are supplied by the caller instruction from its parent register file for each invocation.
/// </summary>
public sealed class VdbeSubprogram
{
    private readonly object _syncRoot = new();
    private readonly Func<VdbeParameterBinding?, IReadOnlyList<VdbeWriteTarget?>>? _dynamicWriteTargets;
    private ReadOnlyCollection<VdbeCursorSource?>? _cursorSources;
    private ReadOnlyCollection<VdbeWriteTarget?>? _writeTargets;
    private VdbeProgram? _program;

    public VdbeSubprogram(
        VdbeProgram program,
        IEnumerable<VdbeCursorSource?>? cursorSources = null,
        IEnumerable<VdbeWriteTarget?>? writeTargets = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        ParameterSlotCount = program.ParameterSlotCount;
        Resolve(program, cursorSources, writeTargets);
    }

    private VdbeSubprogram(int parameterSlotCount)
    {
        if (parameterSlotCount < 0)
            throw new ArgumentOutOfRangeException(nameof(parameterSlotCount));

        ParameterSlotCount = parameterSlotCount;
    }

    private VdbeSubprogram(
        int parameterSlotCount,
        Func<VdbeParameterBinding?, IReadOnlyList<VdbeWriteTarget?>> dynamicWriteTargets)
        : this(parameterSlotCount)
    {
        _dynamicWriteTargets = dynamicWriteTargets
            ?? throw new ArgumentNullException(nameof(dynamicWriteTargets));
    }

    /// <summary>
    /// Creates a subprogram reference that can be resolved once after its body has been built. This is
    /// required when a foreign-key action recursively invokes the subprogram currently being compiled.
    /// </summary>
    public static VdbeSubprogram CreateDeferred(int parameterSlotCount) => new(parameterSlotCount);

    /// <summary>
    /// Creates a deferred action program whose write targets are rebuilt from its bound values for every
    /// invocation. Foreign-key cascade actions need this because each recursive call scans the live child
    /// rows matching a different parent key.
    /// </summary>
    internal static VdbeSubprogram CreateDeferred(
        int parameterSlotCount,
        Func<VdbeParameterBinding?, IReadOnlyList<VdbeWriteTarget?>> dynamicWriteTargets)
        => new(parameterSlotCount, dynamicWriteTargets);

    /// <summary>
    /// Resolves a deferred subprogram exactly once. The program's parameter-slot count must match the
    /// deferred reference's declared count so every <see cref="ProgramInstruction"/> remains valid before
    /// the recursive body is available.
    /// </summary>
    public void Resolve(
        VdbeProgram program,
        IEnumerable<VdbeCursorSource?>? cursorSources = null,
        IEnumerable<VdbeWriteTarget?>? writeTargets = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (program.ParameterSlotCount != ParameterSlotCount)
        {
            throw new ArgumentException(
                $"Expected a subprogram with {ParameterSlotCount} parameter slot(s) but received {program.ParameterSlotCount}.",
                nameof(program));
        }

        var sources = cursorSources is null ? null : Array.AsReadOnly(cursorSources.ToArray());
        if (sources is not null && sources.Count != program.CursorCount)
        {
            throw new ArgumentException(
                $"Expected {program.CursorCount} cursor sources but received {sources.Count}.",
                nameof(cursorSources));
        }

        var targets = writeTargets is null ? null : Array.AsReadOnly(writeTargets.ToArray());
        if (targets is not null && targets.Count != program.CursorCount)
        {
            throw new ArgumentException(
                $"Expected {program.CursorCount} write targets but received {targets.Count}.",
                nameof(writeTargets));
        }

        lock (_syncRoot)
        {
            if (_program is not null)
            {
                throw new InvalidOperationException(
                    "A VDBE subprogram can only be resolved once.");
            }

            _cursorSources = sources;
            _writeTargets = targets;
            _program = program;
        }
    }

    public int ParameterSlotCount { get; }

    public VdbeProgram Program
    {
        get
        {
            lock (_syncRoot)
            {
                return _program ?? throw new InvalidOperationException(
                    "The recursive VDBE subprogram was not resolved before execution.");
            }
        }
    }

    public IReadOnlyList<VdbeCursorSource?>? CursorSources => _cursorSources;

    public IReadOnlyList<VdbeWriteTarget?>? WriteTargets => _writeTargets;

    internal bool RequiresFreshRuntime => _dynamicWriteTargets is not null;

    internal ResumableStatement CreateRuntime(
        VdbeParameterBinding? parameterBinding,
        VdbeExecutionOptions executionOptions,
        VdbeExecutionMemory executionMemory,
        VdbeSchemaExecutionContext? schemaContext = null)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);
        ArgumentNullException.ThrowIfNull(executionMemory);
        lock (_syncRoot)
        {
            var program = _program ?? throw new InvalidOperationException(
                "The recursive VDBE subprogram was not resolved before execution.");
            var writeTargets = _dynamicWriteTargets?.Invoke(parameterBinding) ?? _writeTargets;
            return new ResumableStatement(
                program,
                _cursorSources,
                writeTargets,
                parameterBinding,
                sharedTransaction: null,
                virtualTableBindings: null,
                executionOptions: executionOptions,
                executionMemory: executionMemory,
                // A nested program shares the caller's schema context so a DDL program and the
                // subprograms it drives stage into one transaction-local schema, but it does not own it:
                // resetting the nested statement must not discard the outer program's staged effects.
                schemaContext: schemaContext,
                ownsSchemaContext: false);
        }
    }
}

public abstract record VdbeInstruction
{
    public abstract VdbeOpcode Opcode { get; }
}

public sealed record LoadConstantInstruction(Register Destination, SqlValue Value) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.LoadConstant;
}

/// <summary>
/// Loads the late-bound value of parameter slot <paramref name="Slot"/> into
/// <paramref name="Destination"/>. It is the late-binding sibling of <see cref="LoadConstantInstruction"/>:
/// where <c>LoadConstant</c> bakes a fixed <see cref="SqlValue"/> into the program, this defers the value
/// to execution time and reads it from the <see cref="VdbeParameterBinding"/> supplied to the
/// <see cref="ResumableStatement"/>. One compiled program can therefore be re-executed with different
/// bindings — e.g. a prepared <c>VALUES (?1, ?2)</c> re-run with fresh parameters after a
/// <see cref="ResumableStatement.Reset"/>/<see cref="ResumableStatement.Rebind"/> — without being rebuilt.
/// </summary>
/// <remarks>
/// The instruction carries only the slot index; it never inspects SQL types, so the value's kind
/// (integer, real, text, blob, null) is whatever the binding holds. Executing it without a bound
/// statement, or against a binding whose width does not match the program's
/// <see cref="VdbeProgram.ParameterSlotCount"/>, is a hard error rather than a silent NULL, so a missing
/// binding can never be mistaken for a bound NULL.
/// </remarks>
public sealed record LoadParameterInstruction(Register Destination, ParameterSlot Slot) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.LoadParameter;
}

public sealed record CopyInstruction(Register Source, Register Destination) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.Copy;
}

/// <summary>
/// Applies scalar function <paramref name="Function"/> to the argument tuple held in the register block
/// <paramref name="Arguments"/> and writes its single result into <paramref name="Destination"/>. It is the
/// scalar analogue of <see cref="AggStepInstruction"/>: where <c>AggStep</c> folds one argument tuple into a
/// running accumulator, this maps one argument tuple to one value in a single step with no cross-row state,
/// which is what evaluating a function call inside a projection or predicate needs. The interpreter copies
/// the argument registers before the call and writes the result only on success, so the delegate cannot
/// disturb the register file and a throwing delegate leaves the destination untouched. A zero-width
/// argument range invokes a nullary function.
/// </summary>
/// <remarks>
/// The destination may lie inside, overlap, or sit outside the argument range: because the arguments are
/// snapshotted into a fresh tuple before the delegate runs, overwriting an argument register with the
/// result (the common single-register <c>r[i]=f(r[i])</c> shape) is well defined. The instruction carries
/// no SQL types; argument count, NULL handling, and the result kind are entirely the delegate's contract.
/// </remarks>
public sealed record FunctionInstruction(
    Register Destination,
    VdbeScalarFunction Function,
    RegisterRange Arguments) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.Function;
}

/// <summary>
/// Applies arithmetic operator <paramref name="Operator"/> to the operand tuple held in the register block
/// <paramref name="Operands"/> and writes its single result into <paramref name="Destination"/>. It is the
/// arithmetic sibling of <see cref="FunctionInstruction"/>: where <c>Function</c> maps an argument tuple
/// through a caller-supplied delegate, this maps an operand tuple through the fixed
/// <see cref="VdbeArithmetic"/> value semantics — NULL propagation, integer/real typing, overflow-to-real,
/// division/modulo by zero yielding NULL, and a type error on a non-numeric operand. The binary operators
/// read a two-register operand block and the unary sign operators a one-register block; the program
/// validator pins the block width against <see cref="VdbeArithmetic.Arity"/> so an arity mismatch can never
/// reach execution. The interpreter snapshots the operand registers before computing and writes the result
/// only on success, so the destination may overlap an operand (the common single-register
/// <c>r[i]=-r[i]</c> shape) and a throwing evaluation leaves the register file untouched.
/// </summary>
/// <remarks>
/// The instruction owns no SQL affinity: text and blob operands are type errors rather than being coerced to
/// numbers, so a compiler routing SQL arithmetic through this opcode must materialize numeric operands (or a
/// coercion step) itself. Every other value decision — result kind, overflow behavior, by-zero handling —
/// lives entirely in <see cref="VdbeArithmetic.Evaluate"/>, exactly as the scan, join, and aggregate
/// families delegate their value semantics.
/// </remarks>
public sealed record ArithmeticInstruction(
    Register Destination,
    ArithmeticOperator Operator,
    RegisterRange Operands) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.Arithmetic;
}

/// <summary>
/// Applies SQLite numeric affinity to one register before arithmetic consumes it. The value is replaced only
/// after the supplied transformation succeeds, so a coercion error cannot publish a partial result.
/// </summary>
public sealed record NumericAffinityInstruction(Register Value, VdbeNumericAffinity Affinity) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.NumericAffinity;
}

/// <summary>
/// Compares two scalar registers using SQLite NULL, affinity, and built-in
/// collation rules. Application-defined collations remain evaluator-owned.
/// </summary>
internal sealed record CompareInstruction(
    Register Destination,
    VdbeComparisonOperator Operator,
    Register Left,
    Register Right,
    VdbeValueAffinity? LeftAffinity,
    VdbeValueAffinity? RightAffinity,
    string? Collation) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.Compare;
}

internal sealed record JumpIfNotTrueInstruction(Register Value, ProgramCounter FalseTarget) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.JumpIfNotTrue;
}

internal sealed record CastInstruction(Register Value, string TypeName) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.Cast;
}

public sealed record OpenReadCursorInstruction(Cursor Cursor, string? TableName = null, int ColumnCount = 0)
    : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.OpenReadCursor;
}

/// <summary>Materializes a recursive join plan and opens its result as a read cursor.</summary>
public sealed record OpenJoinCursorInstruction(Cursor Cursor, VdbeJoinPlan Plan) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.OpenJoinCursor;
}

public sealed record OpenWriteCursorInstruction(Cursor Cursor, string TableName, int ColumnCount)
    : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.OpenWriteCursor;
}

/// <summary>Opens the statically bound managed virtual table associated with <paramref name="Cursor"/>.</summary>
public sealed record VOpenInstruction(Cursor Cursor) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.VOpen;
}

/// <summary>
/// Passes the module-selected plan and its argument registers to a managed virtual-table cursor.
/// Filter positions on the first row and branches when the module reports EOF.
/// </summary>
public sealed record VFilterInstruction(
    Cursor Cursor,
    ManagedVirtualTablePlan Plan,
    RegisterRange Arguments,
    ProgramCounter EmptyTarget) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.VFilter;
}

/// <summary>Copies one managed virtual-table column into a register.</summary>
public sealed record VColumnInstruction(Cursor Cursor, int ColumnIndex, Register Destination) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.VColumn;
}

/// <summary>
/// Applies SQLite's VUpdate argv layout from a contiguous register range and optionally receives
/// the module-provided rowid for INSERT.
/// </summary>
public sealed record VUpdateInstruction(
    Cursor Cursor,
    RegisterRange Arguments,
    Register? NewRowIdDestination = null,
    ManagedVirtualTableConflictMode ConflictMode = ManagedVirtualTableConflictMode.Abort) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.VUpdate;
}

/// <summary>Advances a virtual cursor and loops while it has another row.</summary>
public sealed record VNextInstruction(Cursor Cursor, ProgramCounter LoopTarget) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.VNext;
}

/// <summary>
/// Creates a managed virtual table through its statically registered module and publishes it through
/// the catalog callback supplied by the compiler.
/// </summary>
public sealed record VCreateInstruction(
    string ModuleName,
    ManagedVirtualTableCreateContext Context,
    Action<ManagedVirtualTable> Publish) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.VCreate;
}

/// <summary>Invokes the destructive lifecycle callback for the virtual table bound to a cursor slot.</summary>
public sealed record VDestroyInstruction(Cursor Cursor, string TableName) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.VDestroy;
}

/// <summary>Renames the virtual table bound to a cursor slot using a text value held in a register.</summary>
public sealed record VRenameInstruction(Cursor Cursor, Register NewName) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.VRename;
}

/// <summary>
/// Packs the <paramref name="Values"/> register block into the single record register
/// <paramref name="Destination"/> (Turso <c>MakeRecord</c>, insn.rs:912). The destination holds an
/// ordered tuple, not a scalar: <see cref="ResumableStatement.GetRegister"/> reports it as
/// <see cref="SqlValueKind.Null"/> rather than inventing a blob encoding, and only record-aware opcodes
/// read it back. <paramref name="IndexName"/> carries the index a key record is being built for, exactly
/// as upstream's <c>index_name</c> does, and appears in <c>EXPLAIN</c>.
/// </summary>
/// <remarks>
/// Upstream rejects a destination that overlaps its source range at run time; because both operands are
/// compile-time constants here, the same rule is enforced when the program is validated.
/// </remarks>
public sealed record MakeRecordInstruction(
    RegisterRange Values,
    Register Destination,
    string? IndexName = null) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.MakeRecord;
}

/// <summary>
/// Writes the next unused rowid of <paramref name="Cursor"/> into <paramref name="Destination"/>
/// (Turso <c>NewRowid</c>, insn.rs:1368). The value is one past the largest rowid the cursor's bound
/// source currently exposes, so an empty table yields 1. When <paramref name="PreviousLargest"/> is
/// supplied it receives that largest existing rowid (zero for an empty cursor), which is the operand
/// upstream reserves as <c>prev_largest_reg</c>.
/// </summary>
/// <remarks>
/// A cursor whose source carries no rowids cannot answer this instruction; that is a hard error rather
/// than a silent zero, so a value-only cursor can never be mistaken for an empty table.
/// </remarks>
public sealed record NewRowidInstruction(
    Cursor Cursor,
    Register Destination,
    Register? PreviousLargest = null) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.NewRowid;
}

/// <summary>
/// Marks the instructions whose effect belongs to a <see cref="VdbeSchemaExecutionContext"/> rather than
/// to the register file or a cursor. The interpreter dispatches them as one group so every one of them
/// gets the same treatment: a missing context is a hard error, and a failure faults the statement instead
/// of leaving it resumable with a half-applied schema effect.
/// </summary>
internal interface IVdbeSchemaInstruction;

/// <summary>
/// Allocates a new b-tree root in database <paramref name="Database"/> and writes its page number into
/// <paramref name="RootDestination"/> (Turso <c>CreateBtree</c>, insn.rs:1427). <paramref name="Flags"/>
/// selects a table or index b-tree and is reported as P3 exactly as upstream reports
/// <c>CreateBTreeFlags::get_flags</c>.
/// </summary>
public sealed record CreateBtreeInstruction(
    int Database,
    Register RootDestination,
    VdbeCreateBtreeFlags Flags) : VdbeInstruction, IVdbeSchemaInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.CreateBtree;
}

/// <summary>
/// Deletes every entry beneath root page <paramref name="RootPage"/> of database
/// <paramref name="Database"/> while keeping the root itself allocated (Turso <c>ClearBtree</c>,
/// insn.rs:1461). This is the <c>DELETE FROM</c>/<c>ALTER</c> rebuild primitive; use
/// <see cref="DestroyInstruction"/> to also reclaim the root.
/// </summary>
public sealed record ClearBtreeInstruction(int Database, long RootPage)
    : VdbeInstruction, IVdbeSchemaInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.ClearBtree;
}

/// <summary>
/// Destroys root page <paramref name="RootPage"/> of database <paramref name="Database"/> and reclaims
/// its pages (Turso <c>Destroy</c>, insn.rs:1467). <paramref name="FormerRootDestination"/> receives the
/// page number of any root that had to move to fill the hole, or zero when nothing moved — the operand
/// upstream names <c>former_root_reg</c>. <paramref name="IsTemporary"/> distinguishes a temp-database
/// root from a main-database one.
/// </summary>
/// <remarks>
/// Upstream always knows the root as a translate-time literal because SQLite assigns a root page when the
/// b-tree is created. Ahtola assigns roots at commit, so a program that has to retire a tree it did not
/// create discovers the root by reading it out of the object's <c>sqlite_schema</c> row and passes it in
/// <paramref name="RootRegister"/>. Exactly one of the two forms is used: with a register the literal
/// <paramref name="RootPage"/> must be zero.
/// </remarks>
public sealed record DestroyInstruction(
    int Database,
    long RootPage,
    Register FormerRootDestination,
    bool IsTemporary = false,
    Register? RootRegister = null) : VdbeInstruction, IVdbeSchemaInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.Destroy;
}

/// <summary>
/// Rebuilds index <paramref name="IndexName"/> of table <paramref name="TableName"/> in database
/// <paramref name="Database"/> from the table's current rows. It is the managed counterpart of the
/// sorter-driven refill block Turso emits from <c>emit_refill_index</c> (index.rs:326) for an ordinary
/// b-tree index.
/// </summary>
/// <remarks>
/// <para>
/// An ordinary index's <em>content</em> is derived state in Ahtola: the planner reads keys straight from
/// the base rows and <c>EmbeddedFileStore.Persist</c> rebuilds the index b-tree from them on every commit.
/// There is therefore no separate row store for the refill to fill, and what upstream's sorter loop
/// observably contributes — projecting each qualifying row's key and rejecting a duplicate in a
/// <see cref="Unique"/> index — is what this instruction performs, against the staged catalog, using the
/// engine's own collation-aware key projection.
/// </para>
/// <para>
/// The index must already be adopted into the staged schema when this runs; a program emits it after the
/// <c>ParseSchema</c> that publishes the index it names.
/// </para>
/// </remarks>
public sealed record IndexBuildInstruction(
    int Database,
    string TableName,
    string IndexName,
    bool Unique = false) : VdbeInstruction, IVdbeSchemaInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.IndexBuild;
}

/// <summary>
/// Reads header cookie <paramref name="Cookie"/> of database <paramref name="Database"/> into
/// <paramref name="Destination"/> (Turso <c>ReadCookie</c>, insn.rs:1766).
/// </summary>
public sealed record ReadCookieInstruction(
    int Database,
    Register Destination,
    VdbeSchemaCookie Cookie) : VdbeInstruction, IVdbeSchemaInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.ReadCookie;
}

/// <summary>
/// Stages <paramref name="Value"/> into header cookie <paramref name="Cookie"/> of database
/// <paramref name="Database"/> (Turso <c>SetCookie</c>, insn.rs:1773). <paramref name="P5"/> carries the
/// upstream P5 flag word; because <see cref="VdbeExplain"/> exposes no P5 column it is rendered in the
/// instruction comment instead of being dropped.
/// </summary>
public sealed record SetCookieInstruction(
    int Database,
    VdbeSchemaCookie Cookie,
    int Value,
    int P5 = 0) : VdbeInstruction, IVdbeSchemaInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.SetCookie;
}

/// <summary>
/// Reparses the <c>sqlite_schema</c> rows of database <paramref name="Database"/> that match
/// <paramref name="WhereClause"/> and republishes them into the live schema (Turso <c>ParseSchema</c>,
/// insn.rs:1661). A null clause reparses the whole schema.
/// <paramref name="TriggerTargetDatabase"/> mirrors upstream's <c>trigger_target_database_id</c>: it
/// records which database supplied the table an unqualified TEMP trigger fires on, because the stored
/// SQL alone cannot say.
/// </summary>
public sealed record ParseSchemaInstruction(
    int Database,
    string? WhereClause = null,
    int? TriggerTargetDatabase = null) : VdbeInstruction, IVdbeSchemaInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.ParseSchema;
}

/// <summary>Evicts table <paramref name="TableName"/> from the live schema of database
/// <paramref name="Database"/> (Turso <c>DropTable</c>, insn.rs:1491).</summary>
public sealed record DropTableInstruction(int Database, string TableName)
    : VdbeInstruction, IVdbeSchemaInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.DropTable;
}

/// <summary>Evicts view <paramref name="ViewName"/> from the live schema of database
/// <paramref name="Database"/> (Turso <c>DropView</c>, insn.rs:1501).</summary>
public sealed record DropViewInstruction(int Database, string ViewName)
    : VdbeInstruction, IVdbeSchemaInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.DropView;
}

/// <summary>Evicts index <paramref name="IndexName"/> from the live schema of database
/// <paramref name="Database"/> (Turso <c>DropIndex</c>, insn.rs:1507).</summary>
public sealed record DropIndexInstruction(int Database, string IndexName)
    : VdbeInstruction, IVdbeSchemaInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.DropIndex;
}

/// <summary>Evicts trigger <paramref name="TriggerName"/> from the live schema of database
/// <paramref name="Database"/> (Turso <c>DropTrigger</c>, insn.rs:1514).</summary>
public sealed record DropTriggerInstruction(int Database, string TriggerName)
    : VdbeInstruction, IVdbeSchemaInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.DropTrigger;
}

/// <summary>Renames table <paramref name="From"/> to <paramref name="To"/> in the live schema of database
/// <paramref name="Database"/> (Turso <c>RenameTable</c>, insn.rs:1863).</summary>
public sealed record RenameTableInstruction(int Database, string From, string To)
    : VdbeInstruction, IVdbeSchemaInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.RenameTable;
}

/// <summary>
/// Appends column <paramref name="ColumnName"/>, declared by <paramref name="ColumnDefinition"/>, to table
/// <paramref name="TableName"/> in the live schema of database <paramref name="Database"/>
/// (Turso <c>AddColumn</c>, insn.rs:1873). The definition stays SQL text so the same rewrite rules apply
/// whether the schema is being altered or reloaded from <c>sqlite_schema</c>.
/// </summary>
/// <remarks>
/// <paramref name="ColumnSql"/> is the added column's source text as the statement wrote it. SQLite edits
/// a table's stored <c>CREATE TABLE</c> by inserting that text rather than regenerating the statement, so
/// the opcode needs it to keep the stored SQL byte-identical to what the direct rewrite produced. A null
/// value means the table has no faithful stored text to extend, which is exactly when SQLite falls back to
/// regenerating it.
/// </remarks>
public sealed record AddColumnInstruction(
    int Database,
    string TableName,
    string ColumnName,
    string ColumnDefinition,
    string? ColumnSql = null) : VdbeInstruction, IVdbeSchemaInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.AddColumn;
}

/// <summary>Removes the column at <paramref name="ColumnIndex"/> from table <paramref name="TableName"/>
/// in the live schema of database <paramref name="Database"/> (Turso <c>DropColumn</c>, insn.rs:1868).</summary>
public sealed record DropColumnInstruction(
    int Database,
    string TableName,
    int ColumnIndex) : VdbeInstruction, IVdbeSchemaInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.DropColumn;
}

/// <summary>
/// Rewrites the column at <paramref name="ColumnIndex"/> of table <paramref name="TableName"/> to
/// <paramref name="ColumnDefinition"/> in the live schema of database <paramref name="Database"/>
/// (Turso <c>AlterColumn</c>, insn.rs:1876). <paramref name="Rename"/> distinguishes
/// <c>RENAME COLUMN</c>, which only changes the name and must be propagated to dependent objects, from a
/// full definition replacement.
/// </summary>
/// <remarks>
/// <paramref name="QuoteNewName"/> mirrors SQLite's <c>bQuote</c> and only applies to a rename: when the
/// replacement name was written quoted, every reference the token-aware rewrite touches is emitted quoted
/// too. It is a property of the statement rather than of the schema, so it travels with the opcode.
/// </remarks>
public sealed record AlterColumnInstruction(
    int Database,
    string TableName,
    int ColumnIndex,
    string ColumnDefinition,
    bool Rename = false,
    bool QuoteNewName = false) : VdbeInstruction, IVdbeSchemaInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.AlterColumn;
}

public sealed record VBeginInstruction(Cursor Cursor) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.VBegin;
}

public sealed record VSyncInstruction(Cursor Cursor) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.VSync;
}

public sealed record VCommitInstruction(Cursor Cursor) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.VCommit;
}

public sealed record VRollbackInstruction(Cursor Cursor) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.VRollback;
}

/// <summary>
/// The managed index method a method-index instruction operates on. Binding the attachment and its
/// base-row source into the instruction keeps the interpreter free of any global lookup and keeps the
/// opcodes reflection-free.
/// </summary>
/// <remarks>
/// <para>
/// A query program knows both operands when it is compiled and uses the eager form. A schema program
/// does not: the attachment a <c>CREATE INDEX</c>/<c>DROP INDEX</c> program acts on belongs to the
/// transaction-local catalog the program itself is building, which does not exist until the program is
/// bound to a stage and run. Those programs use <see cref="Deferred"/>, whose resolver runs on first use
/// inside the interpreter.
/// </para>
/// <para>
/// Deferral is also what keeps <c>EXPLAIN</c> inert: describing a program reads only
/// <see cref="MethodName"/> and <see cref="IndexName"/>, so a resolver that would attach or mutate method
/// state is never invoked.
/// </para>
/// </remarks>
internal sealed record VdbeIndexMethodBinding
{
    private readonly Func<(Indexing.ManagedIndexMethodAttachment Attachment, Indexing.IManagedIndexSource Source)>?
        _resolver;
    private Indexing.ManagedIndexMethodAttachment? _attachment;
    private Indexing.IManagedIndexSource? _source;

    public VdbeIndexMethodBinding(
        string methodName,
        string indexName,
        Indexing.ManagedIndexMethodAttachment attachment,
        Indexing.IManagedIndexSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(source);
        MethodName = methodName;
        IndexName = indexName;
        _attachment = attachment;
        _source = source;
    }

    private VdbeIndexMethodBinding(
        string methodName,
        string indexName,
        Func<(Indexing.ManagedIndexMethodAttachment Attachment, Indexing.IManagedIndexSource Source)> resolver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        ArgumentNullException.ThrowIfNull(resolver);
        MethodName = methodName;
        IndexName = indexName;
        _resolver = resolver;
    }

    /// <summary>Binds a method whose attachment only exists once the program runs.</summary>
    public static VdbeIndexMethodBinding Deferred(
        string methodName,
        string indexName,
        Func<(Indexing.ManagedIndexMethodAttachment Attachment, Indexing.IManagedIndexSource Source)> resolver)
        => new(methodName, indexName, resolver);

    public string MethodName { get; }

    public string IndexName { get; }

    /// <summary>Whether the binding can produce an attachment, without producing one.</summary>
    public bool IsResolvable => _resolver is not null || (_attachment is not null && _source is not null);

    public Indexing.ManagedIndexMethodAttachment Attachment
    {
        get
        {
            Resolve();
            return _attachment!;
        }
    }

    public Indexing.IManagedIndexSource Source
    {
        get
        {
            Resolve();
            return _source!;
        }
    }

    private void Resolve()
    {
        if (_attachment is not null && _source is not null)
            return;

        if (_resolver is null)
        {
            throw new VdbeSchemaExecutionException(
                $"The index-method binding for '{IndexName}' has neither an attachment nor a resolver.");
        }

        var (attachment, source) = _resolver();
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(source);
        _attachment = attachment;
        _source = source;
    }
}

/// <summary>Allocates method state for a freshly created method index (Turso <c>IndexMethodCreate</c>).</summary>
internal sealed record IndexMethodCreateInstruction(Cursor Cursor, VdbeIndexMethodBinding Binding) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.IndexMethodCreate;
}

/// <summary>Releases all method state for a dropped method index (Turso <c>IndexMethodDestroy</c>).</summary>
internal sealed record IndexMethodDestroyInstruction(Cursor Cursor, VdbeIndexMethodBinding Binding) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.IndexMethodDestroy;
}

/// <summary>Compacts method state (Turso <c>IndexMethodOptimize</c>).</summary>
internal sealed record IndexMethodOptimizeInstruction(Cursor Cursor, VdbeIndexMethodBinding Binding) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.IndexMethodOptimize;
}

/// <summary>
/// Starts one declared query pattern on a method cursor, branching to
/// <paramref name="EmptyTarget"/> when the method reports no results.
/// </summary>
internal sealed record IndexMethodQueryInstruction(
    Cursor Cursor,
    VdbeIndexMethodBinding Binding,
    int PatternIndex,
    RegisterRange Arguments,
    ProgramCounter EmptyTarget) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.IndexMethodQuery;
}

/// <summary>Advances a method cursor and loops while it has another result.</summary>
internal sealed record IndexMethodNextInstruction(Cursor Cursor, ProgramCounter LoopTarget) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.IndexMethodNext;
}

/// <summary>Copies one method result column into a register. Column 0 is the method score.</summary>
internal sealed record IndexMethodColumnInstruction(Cursor Cursor, int ColumnIndex, Register Destination)
    : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.IndexMethodColumn;
}

/// <summary>Loads the current method result's base-table rowid so the program can seek the base row.</summary>
internal sealed record IndexMethodRowIdInstruction(Cursor Cursor, Register Destination) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.IndexMethodRowId;
}

/// <summary>Applies one base-row insert: index columns in declaration order, rowid last.</summary>
internal sealed record IndexMethodInsertInstruction(Cursor Cursor, RegisterRange Values) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.IndexMethodInsert;
}

/// <summary>Applies one base-row delete with the same layout as <see cref="IndexMethodInsertInstruction"/>.</summary>
internal sealed record IndexMethodDeleteInstruction(Cursor Cursor, RegisterRange Values) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.IndexMethodDelete;
}

public sealed record CloseCursorInstruction(Cursor Cursor) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.CloseCursor;
}

/// <summary>Opens a sorter that materializes rows and orders them with
/// <paramref name="Comparer"/> when <c>SorterSort</c> runs. <paramref name="ColumnCount"/>
/// is the fixed width of every record the sorter stores. The owning
/// <see cref="ResumableStatement"/> spills buffered payloads at its
/// <see cref="VdbeExecutionOptions.SorterMemoryLimitBytes"/> and drains through
/// a k-way merge, so large <c>ORDER BY</c>/<c>GROUP BY</c> result sets do not
/// materialize in memory. A positive <paramref name="BufferRowCapacity"/> is a
/// stricter deterministic row-cap override used by opcode-level tests.</summary>
public sealed record OpenSorterInstruction(
    Sorter Sorter,
    VdbeRowComparer Comparer,
    int ColumnCount,
    int BufferRowCapacity = 0)
    : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.OpenSorter;
}

/// <summary>Appends a snapshot of the registers in <paramref name="Record"/> to the
/// sorter. The values are copied, so later writes to those registers do not disturb
/// rows already stored.</summary>
public sealed record SorterInsertInstruction(Sorter Sorter, RegisterRange Record) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.SorterInsert;
}

/// <summary>Stably sorts the accumulated records and positions the sorter on the first
/// one. Jumps to <paramref name="EmptyTarget"/> when the sorter holds no rows.</summary>
public sealed record SorterSortInstruction(Sorter Sorter, ProgramCounter EmptyTarget) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.SorterSort;
}

/// <summary>Copies the sorter's current record into the contiguous register block that
/// <paramref name="Destination"/> spans. The range width must equal the sorter's
/// column count.</summary>
public sealed record SorterDataInstruction(Sorter Sorter, RegisterRange Destination) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.SorterData;
}

/// <summary>Advances the sorter to the next ordered record, jumping to
/// <paramref name="LoopTarget"/> while more rows remain and falling through once the
/// sorter is drained.</summary>
public sealed record SorterNextInstruction(Sorter Sorter, ProgramCounter LoopTarget) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.SorterNext;
}

/// <summary>Releases the sorter's buffered rows.</summary>
public sealed record CloseSorterInstruction(Sorter Sorter) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.CloseSorter;
}

/// <summary>Unconditionally transfers control to <paramref name="Target"/>.</summary>
public sealed record GotoInstruction(ProgramCounter Target) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.Goto;
}

/// <summary>
/// Transfers control to <paramref name="Target"/> when <paramref name="Register"/> holds a
/// truthy value — a non-zero <see cref="SqlValueKind.Integer"/> — and falls through otherwise
/// (including <c>Integer(0)</c>, NULL, and every non-integer kind). It is a pure control-flow
/// primitive that branches on a boolean flag the program itself maintains with
/// <c>LoadConstant</c>; it never interprets SQL truthiness of arbitrary values. The LEFT OUTER
/// join uses it to branch on its per-outer-row "a right row matched" flag.
/// </summary>
public sealed record JumpIfInstruction(Register Register, ProgramCounter Target) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.JumpIf;
}

/// <summary>Resets an aggregate accumulator to its uninitialized state so the next
/// <c>AggStep</c> starts a fresh group. A following <c>AggFinalize</c> on an accumulator
/// that was reset but never stepped yields the aggregate's empty-input value.</summary>
public sealed record AggResetInstruction(Accumulator Accumulator) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.AggReset;
}

/// <summary>Folds the argument tuple in <paramref name="Arguments"/> into
/// <paramref name="Accumulator"/> using <paramref name="Aggregate"/>. The accumulator's
/// context is created lazily on the first step after a reset. A zero-width range steps a
/// nullary aggregate such as <c>COUNT(*)</c>.</summary>
public sealed record AggStepInstruction(
    Accumulator Accumulator,
    VdbeAggregate Aggregate,
    RegisterRange Arguments) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.AggStep;
}

/// <summary>Finalizes <paramref name="Accumulator"/> with <paramref name="Aggregate"/>
/// and writes the result into <paramref name="Destination"/>. It does not reset the
/// accumulator; grouped programs emit an explicit <c>AggReset</c> before the next group.</summary>
public sealed record AggFinalizeInstruction(
    Accumulator Accumulator,
    VdbeAggregate Aggregate,
    Register Destination) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.AggFinalize;
}

/// <summary>Compares the group-key tuple in <paramref name="CurrentKey"/> against the
/// saved tuple in <paramref name="SavedKey"/> with <paramref name="Comparer"/> and jumps
/// to <paramref name="SameGroupTarget"/> when they fall in the same group, falling through
/// otherwise (a new group boundary). The two ranges must be the same width.</summary>
public sealed record SameGroupInstruction(
    RegisterRange CurrentKey,
    RegisterRange SavedKey,
    VdbeGroupComparer Comparer,
    ProgramCounter SameGroupTarget) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.SameGroup;
}

public sealed record RewindCursorInstruction(Cursor Cursor, ProgramCounter EmptyTarget) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.Rewind;
}

/// <summary>
/// Positions <paramref name="Cursor"/> on its last row, jumping to <paramref name="EmptyTarget"/>
/// when the cursor has no rows. It is the reverse-scan counterpart to
/// <see cref="RewindCursorInstruction"/>: the compiler emits it for a single-table
/// <c>ORDER BY rowid DESC</c> scan so rows are visited in descending rowid order without a
/// sorter. The cursor must be a plain table scan — backward iteration over a forward-only
/// streaming join enumerator is not defined, and the compiler never emits this opcode for
/// joins.
/// </summary>
public sealed record LastCursorInstruction(Cursor Cursor, ProgramCounter EmptyTarget) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.Last;
}

public sealed record ColumnInstruction(Cursor Cursor, int ColumnIndex, Register Destination) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.Column;
}

public sealed record RowIdInstruction(Cursor Cursor, Register Destination) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.RowId;
}

/// <summary>
/// Loads the total number of rows bound to <paramref name="Cursor"/> into <paramref name="Destination"/>.
/// It is the O(1) fast path for <c>SELECT COUNT(*) FROM &lt;source&gt;</c> with no WHERE/GROUP BY/HAVING/
/// ORDER BY/DISTINCT/LIMIT/OFFSET and no FILTER/OVER on the COUNT(*): instead of scanning and
/// accumulating, the cursor reads its bound row source's <see cref="VdbeCursorSource.Rows"/> count
/// directly. The cursor is never iterated, so a tracking row source records no index access. The
/// <see cref="HaltInstruction"/> handler disposes any cursor left open, so the emitted shortcut program
/// needs no explicit <see cref="CloseCursorInstruction"/>.
/// </summary>
/// <remarks>
/// <see cref="DriveProgress"/>, when non-null, is invoked once per counted row before the result is
/// written. This keeps a registered progress handler firing at the same cadence as the scan+accumulator
/// path the shortcut replaces (once per row), so an interruptible <c>SELECT count(*) FROM t</c> still
/// raises <c>SQLITE_INTERRUPT</c> instead of completing in O(1) and never ticking the handler. It is
/// null when no progress handler is registered, so the common case stays O(1) with no per-row work;
/// cooperative cancellation is still honored per opcode by the interpreter loop.
/// </remarks>
public sealed record RowCountInstruction(
    Cursor Cursor,
    Register Destination,
    Action? DriveProgress = null) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.RowCount;
}

public sealed record DeleteInstruction(Cursor Cursor) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.Delete;
}

/// <summary>
/// Turso/SQLite <c>InsertFlags</c> bitfield carried on Insert/Update opcodes.
/// </summary>
[Flags]
public enum VdbeInsertFlags : byte
{
    None = 0,
    /// <summary>This mutation is part of an UPDATE that changes the row's rowid.</summary>
    UpdateRowidChange = 0x01,
    /// <summary>Cursor must already be positioned on the target row before the write.</summary>
    RequireSeek = 0x02,
    /// <summary>Insert targets an ephemeral table (not the durable catalog).</summary>
    EphemeralTableInsert = 0x04,
    /// <summary>Do not update last_insert_rowid() from this write.</summary>
    SkipLastRowid = 0x08,
    /// <summary>Do not increment the statement-level changes() counter.</summary>
    SkipStatementChangeCount = 0x10,
    /// <summary>Do not increment changes() or total_changes().</summary>
    SkipAllChangeCounts = 0x20,
}

/// <summary>
/// Writes a row through <paramref name="Cursor"/>.
/// </summary>
/// <remarks>
/// <para>
/// Two addressing modes share this opcode, exactly as they do upstream. The cursor-only form (no
/// <see cref="Record"/>) is Ahtola's existing DML shape: the bound
/// <see cref="VdbeWriteTarget.MutateRow"/> has already staged the row at the cursor's scan position and
/// this instruction applies it. The register-backed form supplies Turso's
/// <c>Insert { cursor, key_reg, record_reg }</c> operands (insn.rs:1315) — a record built by
/// <see cref="MakeRecordInstruction"/> plus the rowid it is stored under — and is what schema and CTAS
/// programs use to write rows they constructed in registers rather than scanned.
/// </para>
/// <para>
/// <see cref="RowId"/> is required whenever <see cref="Record"/> is supplied; a record with no
/// key has nowhere to go. <see cref="TableName"/> mirrors upstream's <c>table_name</c> operand and
/// appears in <c>EXPLAIN</c>.
/// </para>
/// </remarks>
public sealed record InsertInstruction(
    Cursor Cursor,
    VdbeInsertFlags Flags = VdbeInsertFlags.None) : VdbeInstruction
{
    public InsertInstruction(
        Cursor cursor,
        VdbeInsertFlags flags,
        Register? record,
        Register? rowId,
        string? tableName)
        : this(cursor, flags)
    {
        Record = record;
        RowId = rowId;
        TableName = tableName;
    }

    public Register? Record { get; init; }

    public Register? RowId { get; init; }

    public string? TableName { get; init; }

    public override VdbeOpcode Opcode => VdbeOpcode.Insert;
}

public sealed record UpdateInstruction(Cursor Cursor, VdbeInsertFlags Flags = VdbeInsertFlags.None) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.Update;
}

/// <summary>
/// Invokes a nested VDBE program. Each register in <see cref="ParameterRegisters"/> becomes the value
/// of the equally positioned parameter slot in <see cref="Subprogram"/>; rows produced by the child are
/// consumed internally, as required for trigger and foreign-key action programs.
/// </summary>
public sealed record ProgramInstruction : VdbeInstruction
{
    private readonly ReadOnlyCollection<Register> _parameterRegisters;

    public ProgramInstruction(
        IEnumerable<Register> parameterRegisters,
        VdbeSubprogram subprogram)
    {
        ArgumentNullException.ThrowIfNull(parameterRegisters);
        ArgumentNullException.ThrowIfNull(subprogram);

        _parameterRegisters = Array.AsReadOnly(parameterRegisters.ToArray());
        Subprogram = subprogram;
    }

    public IReadOnlyList<Register> ParameterRegisters => _parameterRegisters;

    public VdbeSubprogram Subprogram { get; }

    public override VdbeOpcode Opcode => VdbeOpcode.Program;
}

public sealed record CommitInstruction(Cursor Cursor) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.Commit;
}

public sealed record FilterInstruction(
    Cursor Cursor,
    VdbeRowPredicate Predicate,
    ProgramCounter FalseTarget,
    string Description) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.Filter;
}

/// <summary>
/// Evaluates <paramref name="Predicate"/> against the current cursor row and its hidden rowid,
/// jumping to <paramref name="FalseTarget"/> when false. It is the rowid-aware counterpart to
/// <see cref="FilterInstruction"/> for rowid-table DML scans.
/// </summary>
public sealed record FilterRowIdInstruction(
    Cursor Cursor,
    VdbeRowIdPredicate Predicate,
    ProgramCounter FalseTarget,
    string Description) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.FilterRowId;
}

/// <summary>
/// Positions <paramref name="Cursor"/> directly on the row whose hidden rowid equals the
/// value held in <paramref name="RowIdRegister"/>, jumping to <paramref name="NotFoundTarget"/>
/// when no such row exists. It is the seek counterpart to a <see cref="RewindCursorInstruction"/>
/// + <see cref="FilterRowIdInstruction"/> scan for rowid-equality point lookups: instead of
/// iterating every row and post-filtering, the cursor lands on the single matching position.
/// The compiler is responsible for evaluating the sought rowid expression into the register
/// before emitting this instruction (Step 3 emission wiring; the scaffolding here is not yet
/// emitted by <c>SelectStatementCompiler</c>).
/// </summary>
public sealed record SeekRowidInstruction(
    Cursor Cursor,
    Register RowIdRegister,
    ProgramCounter NotFoundTarget,
    string Description) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.SeekRowid;
}

/// <summary>
/// Positions <paramref name="Cursor"/> on the first row whose hidden rowid satisfies the
/// <paramref name="StartOp"/> comparison against the value held in <paramref name="StartRowIdRegister"/>,
/// jumping to <paramref name="NotFoundTarget"/> when no such row exists. When
/// <paramref name="EndRowIdRegister"/> is supplied, iteration continues while the rowid satisfies
/// <paramref name="EndOp"/>; the emitted <see cref="FilterRowIdInstruction"/> immediately after
/// the seek enforces that upper bound and jumps out when exceeded.
/// It is the seek counterpart to a <see cref="RewindCursorInstruction"/> + <see cref="FilterRowIdInstruction"/>
/// scan for rowid-range predicates (<c>rowid &gt; N</c>, <c>rowid BETWEEN A AND B</c>): instead of
/// iterating every row and post-filtering, the cursor lands on the first matching position and only
/// walks the matching range. The compiler is responsible for evaluating the bound expressions into
/// the registers before emitting this instruction.
/// </summary>
public sealed record SeekRowidRangeInstruction(
    Cursor Cursor,
    Register StartRowIdRegister,
    VdbeComparisonOperator StartOp,
    Register? EndRowIdRegister,
    VdbeComparisonOperator? EndOp,
    ProgramCounter NotFoundTarget,
    string Description) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.SeekRowidRange;
}

/// <summary>
/// Evaluates <paramref name="Predicate"/> against the tuple held in the register block
/// <paramref name="Row"/> and jumps to <paramref name="FalseTarget"/> when it is false,
/// falling through otherwise. It is the register-range analogue of <see cref="FilterInstruction"/>:
/// where <c>Filter</c> tests a single cursor's current row, this tests a materialized tuple
/// assembled in registers, which is what a join predicate over the combined
/// <c>(left columns, right columns)</c> row needs. The compiler supplies the delegate so the
/// emitted program matches the evaluator's SQL comparison semantics exactly.
/// </summary>
public sealed record FilterRegistersInstruction(
    RegisterRange Row,
    VdbeRowPredicate Predicate,
    ProgramCounter FalseTarget,
    string Description) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.FilterRegisters;
}

/// <summary>
/// Projects a GROUP BY key from <paramref name="Row"/>, finds or creates its
/// first-seen group in <paramref name="GroupSetIndex"/>, and writes that stable
/// zero-based group id to <paramref name="Destination"/>. When
/// <paramref name="KeyOutput"/> is set, the projected key itself is also written
/// to that register range (whose width must equal <paramref name="KeyCount"/>).
/// </summary>
public sealed record GroupKeyInstruction(
    RegisterRange Row,
    Register Destination,
    int KeyCount,
    VdbeGroupKeyProjector Projector,
    VdbeGroupComparer Equality,
    int GroupSetIndex,
    VdbeGroupHasher? Hasher = null,
    RegisterRange? KeyOutput = null) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.GroupKey;
}

/// <summary>Projects one register tuple into another with caller-supplied SQL value semantics.</summary>
public sealed record ProjectRegistersInstruction(
    RegisterRange Input,
    RegisterRange Output,
    VdbeRowTransform Transform,
    string Description) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.ProjectRegisters;
}

/// <summary>Records novel tuples and jumps over duplicates without emitting a row.</summary>
public sealed record DistinctFilterInstruction(
    RegisterRange Values,
    VdbeRowEquality Equality,
    int DistinctSetIndex,
    ProgramCounter DuplicateTarget) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.DistinctFilter;
}

public sealed record NextInstruction(Cursor Cursor, ProgramCounter LoopTarget) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.Next;
}

/// <summary>
/// Advances <paramref name="Cursor"/> to the previous row, looping back to
/// <paramref name="LoopTarget"/> while a previous row exists and falling through otherwise.
/// It is the reverse-scan counterpart to <see cref="NextInstruction"/> and pairs with
/// <see cref="LastCursorInstruction"/> to walk a table cursor backward.
/// </summary>
public sealed record PrevInstruction(Cursor Cursor, ProgramCounter LoopTarget) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.Prev;
}

public sealed record YieldInstruction : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.Yield;
}

public sealed record ResultRowInstruction(RegisterRange Values) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.ResultRow;
}

/// <summary>
/// Emits the tuple held in the register block <paramref name="Values"/> as a result row, but only
/// the first time an equal tuple is seen: it tests the tuple against the running set of rows already
/// emitted through distinct set <paramref name="DistinctSetIndex"/> using <paramref name="Equality"/>,
/// records and yields novel rows, and silently skips duplicates (advancing without producing a row).
/// It is the compound-select de-duplication primitive: replacing a term's <see cref="ResultRowInstruction"/>
/// with this opcode against a shared distinct set turns <c>UNION ALL</c> sequencing into <c>UNION</c>/<c>DISTINCT</c>.
/// The compiler supplies the equality delegate so the emitted program matches the evaluator's row-equality
/// semantics (NULL==NULL, affinity, and collation) exactly.
/// </summary>
public sealed record DistinctResultRowInstruction(
    RegisterRange Values,
    VdbeRowEquality Equality,
    int DistinctSetIndex) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.DistinctResultRow;
}

/// <summary>
/// Records <paramref name="Values"/> in a distinct set and falls through when
/// they are novel, or jumps to <paramref name="DuplicateTarget"/> when an equal
/// row was already recorded. Keeping de-duplication separate from ResultRow lets
/// LIMIT/OFFSET gates count only rows that will actually be emitted.
/// </summary>
public sealed record DistinctGateInstruction(
    RegisterRange Values,
    VdbeRowEquality Equality,
    int DistinctSetIndex,
    ProgramCounter DuplicateTarget) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.DistinctGate;
}

/// <summary>
/// Records the tuple held in the register block <paramref name="Values"/> into row set
/// <paramref name="RowSetIndex"/> for later membership tests, without ever producing a result row.
/// The set holds one representative per distinct tuple, replacing an equal tuple through
/// <paramref name="Equality"/> with the later row, matching a SQLite/Turso temporary B-tree insert.
/// It is the compound set-operation primitive that materializes a non-primary term (the right-hand
/// operand of <c>INTERSECT</c>/<c>EXCEPT</c>) into a probe set that a following
/// <see cref="CompoundResultRowInstruction"/> tests the primary term's rows against. It reuses the same
/// row-set resource pool as <see cref="DistinctResultRowInstruction"/> (<see cref="VdbeProgram.DistinctSetCount"/>),
/// so <c>Reset</c>/<c>Dispose</c> clear it identically. The compiler supplies the equality delegate so
/// membership matches the evaluator's row-equality contract (NULL==NULL together with affinity- and
/// collation-aware comparison) exactly.
/// </summary>
public sealed record RowSetInsertInstruction(
    RegisterRange Values,
    VdbeRowEquality Equality,
    int RowSetIndex) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.RowSetInsert;
}

/// <summary>
/// Positions a row set at its first stored row and copies that row into <paramref name="Destination"/>.
/// Empty sets jump to <paramref name="EmptyTarget"/>. When <paramref name="Comparer"/> is present, the
/// row set is ordered before the output pass; compound set operations use this to mirror SQLite's
/// temporary B-tree traversal order.
/// </summary>
public sealed record RowSetRewindInstruction(
    int RowSetIndex,
    RegisterRange Destination,
    ProgramCounter EmptyTarget,
    VdbeRowComparer? Comparer = null) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.RowSetRewind;
}

/// <summary>
/// Advances a row-set iteration, copies the next row into <paramref name="Destination"/>, and jumps to
/// <paramref name="LoopTarget"/> while another row exists. It falls through when the set is exhausted.
/// </summary>
public sealed record RowSetNextInstruction(
    int RowSetIndex,
    RegisterRange Destination,
    ProgramCounter LoopTarget) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.RowSetNext;
}

/// <summary>
/// Tests the integer rowid in <paramref name="ValueRegister"/> against an integer row set associated with
/// <paramref name="RowSetRegister"/>. A match in an earlier batch jumps to <paramref name="FoundTarget"/>.
/// Batch <c>0</c> skips the membership probe and inserts the value; a positive batch probes only values
/// inserted by earlier batches and then inserts the value; batch <c>-1</c> probes only and never inserts.
/// This is Turso's <c>RowSetTest</c> primitive for multi-index scans. It is intentionally separate from the
/// tuple row sets used by compound queries.
/// </summary>
public sealed record RowSetTestInstruction(
    Register RowSetRegister,
    ProgramCounter FoundTarget,
    Register ValueRegister,
    int Batch) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.RowSetTest;
}

/// <summary>
/// Emits the tuple held in the register block <paramref name="Values"/> as a result row for a compound
/// set operation, but only the first time an equal tuple both satisfies the membership condition
/// <paramref name="Mode"/> against the probe sets <paramref name="MembershipSetIndices"/> and is novel to
/// the output set <paramref name="OutputSetIndex"/>:
/// <see cref="CompoundMembershipMode.PresentInAll"/> (<c>INTERSECT</c>) requires the tuple to be present
/// in every probe set; <see cref="CompoundMembershipMode.AbsentFromAll"/> (<c>EXCEPT</c>) requires it to
/// be present in none. A tuple that fails the condition, or that duplicates a row already emitted through
/// the output set, advances without producing a row. It is the primary-term emit of a compound set
/// operation: the probe sets are built by <see cref="RowSetInsertInstruction"/> over the non-primary
/// terms before the primary term runs, so the primary term streams in its own cursor order and the output
/// preserves first-term first-occurrence order. Every probe set, plus the output set, is drawn from the
/// shared row-set pool (<see cref="VdbeProgram.DistinctSetCount"/>). The compiler supplies the equality
/// delegate so every membership and de-duplication comparison uses the evaluator's row-equality contract
/// (NULL==NULL together with affinity- and collation-aware comparison) exactly.
/// </summary>
/// <remarks>
/// The output set must be disjoint from every probe set; an empty <paramref name="MembershipSetIndices"/>
/// makes the condition vacuously true, degenerating to plain distinct output.
/// </remarks>
public sealed record CompoundResultRowInstruction(
    RegisterRange Values,
    VdbeRowEquality Equality,
    int OutputSetIndex,
    IReadOnlyList<int> MembershipSetIndices,
    CompoundMembershipMode Mode) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.CompoundResultRow;
}

/// <summary>
/// A composable result-row pipeline used when a compound program is embedded as another compound term.
/// Guards run in order, preserving the nested program's de-duplication and membership checks. A surviving
/// row is either emitted or inserted into another row set, allowing an outer operation to add its own
/// semantics without flattening or bypassing the inner operation.
/// </summary>
public sealed record GuardedRowInstruction(
    RegisterRange Values,
    IReadOnlyList<VdbeRowGuard> Guards,
    VdbeRowDestination Destination) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.GuardedRow;
}

/// <summary>
/// Splits de-duplication/membership testing away from row emission so a conditional emitter composes with
/// the LIMIT/OFFSET gate family. Guards run in order over <paramref name="Values"/> exactly as they do for
/// <see cref="GuardedRowInstruction"/>: a <see cref="DistinctRowGuard"/> accepts only a tuple that is novel
/// to its row set (inserting it), and a <see cref="MembershipRowGuard"/> accepts only a tuple satisfying its
/// <see cref="CompoundMembershipMode"/> against every probe set. A candidate that fails any guard jumps to
/// <paramref name="RejectTarget"/> — the instruction after the emit block — so it is discarded silently; a
/// candidate that passes falls through to the gates and the plain <see cref="ResultRowInstruction"/> that
/// follow. Because rejection happens before the offset/limit counters are touched, a duplicate or excluded
/// tuple is never charged against OFFSET or LIMIT, which is what makes <c>DISTINCT</c>, <c>UNION</c>,
/// <c>INTERSECT</c>, and <c>EXCEPT</c> streams gateable exactly.
/// </summary>
public sealed record RowGateInstruction(
    RegisterRange Values,
    IReadOnlyList<VdbeRowGuard> Guards,
    ProgramCounter RejectTarget) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.RowGate;
}

public abstract record VdbeRowGuard;
public sealed record DistinctRowGuard(
    VdbeRowEquality Equality,
    int RowSetIndex) : VdbeRowGuard;

public sealed record MembershipRowGuard(
    VdbeRowEquality Equality,
    IReadOnlyList<int> RowSetIndices,
    CompoundMembershipMode Mode) : VdbeRowGuard;

public abstract record VdbeRowDestination;

public sealed record ResultRowDestination : VdbeRowDestination;

public sealed record RowSetDestination(
    VdbeRowEquality Equality,
    int RowSetIndex) : VdbeRowDestination;

/// <summary>
/// Skips the first N candidate result rows of a LIMIT/OFFSET pipeline. When <paramref name="Counter"/>
/// holds a positive <see cref="SqlValueKind.Integer"/>, it decrements the counter and jumps to
/// <paramref name="SkipTarget"/> — the loop-advance instruction that immediately follows the gated
/// result row — so the candidate is discarded without being emitted; once the counter reaches zero it
/// falls through and the row is emitted. It is the OFFSET half of the limit/offset family: the counter
/// is a register seeded with the resolved non-negative offset, so the first <c>offset</c> candidates
/// that reach the gate are skipped. Skipped candidates never reach the following
/// <see cref="LimitGateInstruction"/>, so an OFFSET row is never counted against LIMIT — preserving the
/// evaluator's OFFSET-then-LIMIT order.
/// </summary>
public sealed record OffsetGateInstruction(Register Counter, ProgramCounter SkipTarget) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.OffsetGate;
}

/// <summary>
/// Stops a LIMIT/OFFSET pipeline once it has emitted its allowance. When <paramref name="Counter"/>
/// holds a positive <see cref="SqlValueKind.Integer"/>, it decrements the counter and falls through so
/// the gated result row is emitted; when the counter is zero (or non-positive) it jumps to
/// <paramref name="DoneTarget"/> — the program's terminating <c>Halt</c> — so no further rows are
/// produced. It is the LIMIT half of the limit/offset family: the counter is a register seeded with the
/// resolved non-negative limit, so exactly that many rows survive the gate. A seed of zero (LIMIT 0)
/// jumps on the very first candidate and emits nothing; an unbounded or negative limit is lowered by
/// simply omitting this gate, so every row surviving OFFSET is emitted.
/// </summary>
public sealed record LimitGateInstruction(Register Counter, ProgramCounter DoneTarget) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.LimitGate;
}

/// <summary>
/// Stops the program. With <see cref="ErrorCode"/> 0 this is a clean halt (normal end).
/// A non-zero code raises <see cref="Ahtola.Core.EmbeddedSqlException"/> carrying the SQLite
/// result code, optional message, and <see cref="OnError"/> disposition (Turso
/// <c>op_halt</c> / RAISE / constraint Halt).
/// </summary>
public sealed record HaltInstruction(
    int ErrorCode = 0,
    string? Description = null,
    Register? DescriptionRegister = null,
    VdbeHaltOnError? OnError = null) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.Halt;
}

/// <summary>
/// Halts with <see cref="ErrorCode"/> when <see cref="Target"/> holds NULL; otherwise falls
/// through. Used for NOT NULL enforcement (Turso <c>op_halt_if_null</c>).
/// </summary>
public sealed record HaltIfNullInstruction(
    Register Target,
    int ErrorCode,
    string Description) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.HaltIfNull;
}

/// <summary>
/// Positions <paramref name="Cursor"/> on the rowid held in <paramref name="RowIdRegister"/>
/// when present and falls through; jumps to <paramref name="JumpTarget"/> when absent
/// (Turso/SQLite <c>NotExists</c>). Same probe as <see cref="SeekRowidInstruction"/> with
/// inverted naming for compiler/EXPLAIN parity on uniqueness probes.
/// </summary>
public sealed record NotExistsInstruction(
    Cursor Cursor,
    Register RowIdRegister,
    ProgramCounter JumpTarget,
    string Description) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.NotExists;
}

/// <summary>
/// Positions <paramref name="Cursor"/> on the rowid held in <paramref name="RowIdRegister"/>
/// and jumps to <paramref name="FoundTarget"/> when present; falls through when absent
/// (Turso/SQLite <c>Found</c>).
/// </summary>
public sealed record FoundInstruction(
    Cursor Cursor,
    Register RowIdRegister,
    ProgramCounter FoundTarget,
    string Description) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.Found;
}

/// <summary>
/// Opens a general-purpose in-memory ephemeral table on <paramref name="Cursor"/> with
/// <paramref name="ColumnCount"/> columns (Turso/SQLite <c>OpenEphemeral</c>). Rows are
/// appended with <see cref="EphemeralInsertInstruction"/> and scanned with the normal
/// Rewind/Next/Column/SeekRowid/NotExists/Found family. Unlike
/// <see cref="OpenWorkTableInstruction"/> this is not recursion-specific — it is the
/// store used for IN-list materialization, DISTINCT intermediates, and subquery results.
/// </summary>
public sealed record OpenEphemeralInstruction(Cursor Cursor, int ColumnCount) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.OpenEphemeral;
}

/// <summary>
/// Appends the values in <paramref name="Values"/> as one row of the ephemeral table
/// opened on <paramref name="Cursor"/>, assigning the next sequential rowid.
/// </summary>
public sealed record EphemeralInsertInstruction(Cursor Cursor, RegisterRange Values) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.EphemeralInsert;
}

/// <summary>
/// Probes <paramref name="Cursor"/> for a row whose leading columns equal
/// <paramref name="Key"/>. Jumps to <paramref name="NoConflictTarget"/> when any key
/// register is NULL or no match exists (Turso/SQLite <c>NoConflict</c> — NULL never
/// conflicts). Falls through with the cursor positioned on the match when found.
/// </summary>
public sealed record NoConflictInstruction(
    Cursor Cursor,
    RegisterRange Key,
    ProgramCounter NoConflictTarget,
    string Description) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.NoConflict;
}

/// <summary>
/// Adds <paramref name="Increment"/> (may be negative) to the deferred or statement-level
/// foreign-key constraint counter (Turso/SQLite <c>FkCounter</c>).
/// </summary>
public sealed record FkCounterInstruction(int Increment, bool Deferred) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.FkCounter;
}

/// <summary>
/// Jumps to <paramref name="Target"/> when the deferred or statement FK counter is zero
/// (Turso/SQLite <c>FkIfZero</c>).
/// </summary>
public sealed record FkIfZeroInstruction(bool Deferred, ProgramCounter Target) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.FkIfZero;
}

/// <summary>
/// Halts with <see cref="SqliteResultCode.ConstraintForeignKey"/> when the deferred or
/// statement FK counter is non-zero (Turso/SQLite <c>FkCheck</c>).
/// </summary>
public sealed record FkCheckInstruction(bool Deferred) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.FkCheck;
}

/// <summary>
/// Positions <paramref name="Cursor"/> on the first (GE/GT) or last (LE/LT) row whose
/// leading columns compare to <paramref name="Key"/> per <paramref name="Operator"/>.
/// When <paramref name="EqOnly"/> is set, a GE/LE seek requires an exact match (Turso
/// <c>eq_only</c>). Jumps to <paramref name="NotFoundTarget"/> when no qualifying row
/// exists. <paramref name="IsIndex"/> selects the Idx* opcode names for EXPLAIN parity;
/// runtime semantics are the same for materialization-backed cursors.
/// </summary>
/// <param name="Cursor">Table or index cursor to position.</param>
/// <param name="Key">Register range holding the seek key values.</param>
/// <param name="Operator">Comparison operator (GE/GT/LE/LT).</param>
/// <param name="EqOnly">When true, GE/LE requires an exact key match.</param>
/// <param name="IsIndex">When true, EXPLAIN reports Idx* opcode names.</param>
/// <param name="NotFoundTarget">PC jumped to when no qualifying row exists.</param>
/// <param name="Description">Human-readable EXPLAIN comment.</param>
/// <param name="KeyColumns">
/// Optional table-column ordinals for each key register. When null, the key compares
/// against row columns <c>0..Key.Count-1</c> (index-shaped / leading-key cursors).
/// When set, length must equal <see cref="Key"/>.Count and each entry is the row
/// ordinal used for that key part (table-row cursors ordered by a non-leading index).
/// </param>
public sealed record SeekKeyInstruction(
    Cursor Cursor,
    RegisterRange Key,
    VdbeKeySeekOperator Operator,
    bool EqOnly,
    bool IsIndex,
    ProgramCounter NotFoundTarget,
    string Description,
    IReadOnlyList<int>? KeyColumns = null) : VdbeInstruction
{
    public override VdbeOpcode Opcode => (IsIndex, Operator) switch
    {
        (false, VdbeKeySeekOperator.GreaterThanOrEqual) => VdbeOpcode.SeekGE,
        (false, VdbeKeySeekOperator.GreaterThan) => VdbeOpcode.SeekGT,
        (false, VdbeKeySeekOperator.LessThanOrEqual) => VdbeOpcode.SeekLE,
        (false, VdbeKeySeekOperator.LessThan) => VdbeOpcode.SeekLT,
        (true, VdbeKeySeekOperator.GreaterThanOrEqual) => VdbeOpcode.IdxGE,
        (true, VdbeKeySeekOperator.GreaterThan) => VdbeOpcode.IdxGT,
        (true, VdbeKeySeekOperator.LessThanOrEqual) => VdbeOpcode.IdxLE,
        (true, VdbeKeySeekOperator.LessThan) => VdbeOpcode.IdxLT,
        _ => VdbeOpcode.SeekGE,
    };
}

/// <summary>
/// Writes the current cursor row's rowid into <paramref name="Destination"/> (Turso
/// <c>IdxRowid</c>). Works for any cursor that exposes rowids.
/// </summary>
public sealed record IdxRowIdInstruction(Cursor Cursor, Register Destination) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.IdxRowId;
}

/// <summary>
/// Copies the current cursor row into <paramref name="Destination"/> (width =
/// destination count), starting at column 0 (Turso <c>RowData</c> simplified to
/// register columns rather than a packed record blob).
/// </summary>
public sealed record RowDataInstruction(Cursor Cursor, RegisterRange Destination) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.RowData;
}

/// <summary>Turso <c>IdxInsertFlags</c> bitfield.</summary>
[Flags]
public enum VdbeIdxInsertFlags : byte
{
    None = 0,
    Append = 0x01,
    UseSeek = 0x02,
    NChange = 0x04,
    NoOpDuplicate = 0x08,
}

/// <summary>
/// Inserts <paramref name="Key"/> into an ephemeral/index cursor. With
/// <see cref="VdbeIdxInsertFlags.NoOpDuplicate"/>, a matching key is a no-op
/// instead of a second insert (Turso <c>IdxInsert</c>).
/// </summary>
public sealed record IdxInsertInstruction(
    Cursor Cursor,
    RegisterRange Key,
    VdbeIdxInsertFlags Flags = VdbeIdxInsertFlags.None) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.IdxInsert;
}

/// <summary>
/// Deletes the entry at the current cursor position from an ephemeral/index
/// cursor (Turso <c>IdxDelete</c>). Optional <paramref name="Key"/> seeks first.
/// </summary>
public sealed record IdxDeleteInstruction(
    Cursor Cursor,
    RegisterRange? Key = null) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.IdxDelete;
}

/// <summary>
/// Opens the statement's outermost transaction over the interpreter's mutable register state,
/// snapshotting the register file so a later <see cref="RollbackTransactionInstruction"/> can restore
/// it. It fails at run time when a transaction is already open, mirroring SQLite's "cannot start a
/// transaction within a transaction". This transacts only the interpreter's own scalar registers; it
/// makes no claim on database durability and never touches storage.
/// </summary>
public sealed record BeginTransactionInstruction : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.BeginTransaction;
}

/// <summary>
/// Ends the active transaction by discarding every savepoint snapshot and keeping the current register
/// values — the committed state. It fails at run time when no transaction is active. Like the whole
/// family it commits only the interpreter's in-memory register state, not any durable store.
/// </summary>
public sealed record CommitTransactionInstruction : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.CommitTransaction;
}

/// <summary>
/// Ends the active transaction by restoring the register file to the snapshot taken when the outermost
/// transaction was opened and discarding every savepoint. It fails at run time when no transaction is
/// active. The rollback is observable purely through the restored register values; no storage is involved.
/// </summary>
public sealed record RollbackTransactionInstruction : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.RollbackTransaction;
}

/// <summary>
/// Opens a named savepoint, pushing a register-file snapshot onto the savepoint stack. A savepoint may be
/// opened outside a transaction, in which case it implicitly opens one (matching SQLite). Names are matched
/// with ordinal (case-sensitive, exact) comparison and must be non-empty.
/// </summary>
public sealed record SavepointInstruction(string Name) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.Savepoint;
}

/// <summary>
/// Releases the named savepoint and every savepoint opened after it, discarding their snapshots without
/// restoring any register values — the nested savepoints are folded into the enclosing scope. It fails at
/// run time when no savepoint with <paramref name="Name"/> is open. Releasing the savepoint that opened the
/// transaction ends the transaction.
/// </summary>
public sealed record ReleaseSavepointInstruction(string Name) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.ReleaseSavepoint;
}

/// <summary>
/// Restores the register file to the snapshot taken at the named savepoint and cancels every savepoint
/// opened after it, but keeps the named savepoint itself so it can be rolled back to again. It fails at run
/// time when no savepoint with <paramref name="Name"/> is open. The transaction stays open.
/// </summary>
public sealed record RollbackToSavepointInstruction(string Name) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.RollbackToSavepoint;
}

/// <summary>
/// Opens recursive worktable <paramref name="WorkTable"/>: a FIFO frontier queue of
/// <paramref name="ColumnCount"/>-wide rows that a <see cref="SeedWorkTableInstruction"/> fills with the
/// anchor generation and a <see cref="WorkTableStepInstruction"/>/<see cref="WorkTableExpandInstruction"/>
/// loop drains and re-feeds. It is the recursive-CTE analogue of <see cref="OpenSorterInstruction"/>: it
/// allocates the runtime buffer and fixes its shape and safety bounds up front.
/// </summary>
/// <remarks>
/// <para><paramref name="Mode"/> selects <c>UNION ALL</c> (<see cref="WorkTableDedupMode.KeepAll"/>) or
/// <c>UNION</c>/<c>DISTINCT</c> (<see cref="WorkTableDedupMode.Distinct"/>) de-duplication. A distinct
/// worktable requires a non-null <paramref name="Equality"/>, which owns the row-equality contract
/// (NULL==NULL, affinity, collation) exactly as the compound opcodes do; a keep-all worktable must carry
/// no equality.</para>
/// <para>The two guards make the recursion safe by construction. <paramref name="MaxRows"/> (which must be
/// positive) is a hard cap on the number of rows admitted to the worktable — seeds plus descendants,
/// counting only admitted (non-duplicate) rows; admitting one more throws a
/// <see cref="RecursiveWorkTableOverflowException"/>, which is how an unbounded <c>UNION ALL</c> recursion
/// fails loudly instead of looping forever. <paramref name="MaxDepth"/> (which must be non-negative) bounds
/// the recursion depth: a frontier row at depth <c>d</c> is expanded only while <c>d &lt; MaxDepth</c>, so
/// seeds are depth 0, their descendants depth 1, and the deepest emitted rows sit at depth
/// <c>MaxDepth</c>. <c>MaxDepth</c> = 0 emits only the anchor generation; a very large value defers
/// termination to the row guard or to the transform running dry.</para>
/// </remarks>
public sealed record OpenWorkTableInstruction(
    WorkTable WorkTable,
    int ColumnCount,
    WorkTableDedupMode Mode,
    int MaxRows,
    int MaxDepth,
    VdbeRowEquality? Equality = null) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.OpenWorkTable;
}

/// <summary>
/// Admits the tuple held in the register block <paramref name="Row"/> to worktable
/// <paramref name="WorkTable"/> as a depth-0 (anchor/seed) frontier row. The values are snapshotted, so a
/// later reload of those registers cannot disturb a queued row. Under
/// <see cref="WorkTableDedupMode.Distinct"/> a seed equal to one already admitted is silently dropped;
/// otherwise the seed counts against the worktable's row guard and enqueues for later draining. Seeds do
/// not themselves produce a result row — the drain loop emits every admitted row exactly once, so anchor
/// rows surface first in seed order (breadth-first).
/// </summary>
public sealed record SeedWorkTableInstruction(WorkTable WorkTable, RegisterRange Row) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.SeedWorkTable;
}

/// <summary>
/// Dequeues the next frontier row of worktable <paramref name="WorkTable"/> into the register block
/// <paramref name="Destination"/> and records it as the worktable's current frontier row (establishing the
/// depth a following <see cref="WorkTableExpandInstruction"/> expands from), then falls through. When the
/// frontier is empty it jumps to <paramref name="DoneTarget"/> instead, ending the drain loop. It is the
/// head of the recursive loop and the recursion analogue of <c>SorterSort</c>/<c>Next</c>: the FIFO order
/// is what makes the emitted stream breadth-first (an entire generation before the next), matching the
/// evaluator's level-by-level working-set iteration for a linear recursive term.
/// </summary>
public sealed record WorkTableStepInstruction(
    WorkTable WorkTable,
    RegisterRange Destination,
    ProgramCounter DoneTarget) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.WorkTableStep;
}

/// <summary>
/// Expands the worktable's current frontier row — the tuple held in the register block
/// <paramref name="Source"/>, which the preceding <see cref="WorkTableStepInstruction"/> dequeued — into
/// its descendants by invoking <paramref name="Transform"/>, and enqueues each descendant one depth deeper,
/// subject to the worktable's de-duplication and guards. When the current row is already at the depth guard
/// (<c>depth &gt;= MaxDepth</c>) the transform is not invoked, bounding the recursion. Each admitted
/// descendant that would exceed the row guard throws a <see cref="RecursiveWorkTableOverflowException"/>;
/// each duplicate (under <see cref="WorkTableDedupMode.Distinct"/>) is dropped. It never produces a result
/// row itself — the loop's <c>ResultRow</c> emits the dequeued row and this opcode only grows the frontier,
/// so the recursion unfolds observably one <c>Step</c>/<c>Expand</c> generation at a time.
/// </summary>
public sealed record WorkTableExpandInstruction(
    WorkTable WorkTable,
    VdbeRecursiveTransform Transform,
    RegisterRange Source) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.WorkTableExpand;
}

/// <summary>
/// Collects the current frontier row and invokes <paramref name="Transform"/> after the final row at the
/// same depth, admitting the returned rows as the next generation.
/// </summary>
public sealed record WorkTableExpandGenerationInstruction(
    WorkTable WorkTable,
    VdbeRecursiveGenerationTransform Transform,
    RegisterRange Source) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.WorkTableExpandGeneration;
}

/// <summary>Releases worktable <paramref name="WorkTable"/>'s frontier and de-duplication buffers.</summary>
public sealed record CloseWorkTableInstruction(WorkTable WorkTable) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.CloseWorkTable;
}

/// <summary>
/// Opens window buffer <paramref name="Buffer"/>: an insertion-ordered buffer of
/// <paramref name="ColumnCount"/>-wide scanned rows that a <see cref="WindowBufferInsertInstruction"/>
/// loop fills, a <see cref="WindowBufferComputeInstruction"/> transforms into a parallel block of
/// <paramref name="WindowCount"/> window values per row through <paramref name="Evaluator"/>, and a
/// <see cref="WindowBufferDataInstruction"/>/<see cref="WindowBufferNextInstruction"/> loop drains. It is
/// the windowing analogue of <see cref="OpenSorterInstruction"/>: it allocates the runtime buffer and
/// fixes its shape up front.
/// </summary>
/// <remarks>
/// <paramref name="WindowCount"/> may be zero only in the degenerate sense that a program declaring no
/// window functions has nothing to compute; the validator requires it to be positive because a window
/// buffer exists precisely to hold window values. The evaluator delegate is invoked exactly once, by
/// <c>WindowBufferCompute</c>, over the whole buffer, which is what makes forward-looking and
/// peer-relative frames representable.
/// </remarks>
public sealed record OpenWindowBufferInstruction(
    WindowBuffer Buffer,
    int ColumnCount,
    int WindowCount,
    VdbeWindowEvaluator Evaluator) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.OpenWindowBuffer;
}

/// <summary>Appends a snapshot of the registers in <paramref name="Record"/> to window buffer
/// <paramref name="Buffer"/>. The values are copied, so later writes to those registers cannot disturb a
/// buffered row. The range width must equal the buffer's scanned-column count.</summary>
public sealed record WindowBufferInsertInstruction(WindowBuffer Buffer, RegisterRange Record)
    : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.WindowBufferInsert;
}

/// <summary>
/// Runs the buffer's <see cref="VdbeWindowEvaluator"/> over every buffered row, storing the resulting
/// per-row window values alongside them, then positions the buffer on its first row. Jumps to
/// <paramref name="EmptyTarget"/> when the buffer holds no rows. It is the windowing analogue of
/// <c>SorterSort</c>: the single point at which the buffered phase ends and the drain phase begins.
/// </summary>
public sealed record WindowBufferComputeInstruction(WindowBuffer Buffer, ProgramCounter EmptyTarget)
    : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.WindowBufferCompute;
}

/// <summary>Copies the buffer's current row followed by that row's computed window values into the
/// contiguous register block <paramref name="Destination"/> spans. The range width must equal the
/// buffer's column count plus its window count.</summary>
public sealed record WindowBufferDataInstruction(WindowBuffer Buffer, RegisterRange Destination)
    : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.WindowBufferData;
}

/// <summary>Advances the window buffer to the next buffered row, jumping to
/// <paramref name="LoopTarget"/> while more rows remain and falling through once the buffer is
/// drained.</summary>
public sealed record WindowBufferNextInstruction(WindowBuffer Buffer, ProgramCounter LoopTarget)
    : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.WindowBufferNext;
}

/// <summary>Releases window buffer <paramref name="Buffer"/>'s rows and computed window values.</summary>
public sealed record CloseWindowBufferInstruction(WindowBuffer Buffer) : VdbeInstruction
{
    public override VdbeOpcode Opcode => VdbeOpcode.CloseWindowBuffer;
}

/// <summary>
/// Thrown when a recursive worktable would admit more rows than its <see cref="OpenWorkTableInstruction.MaxRows"/>
/// guard allows. It is the loud, bounded failure that keeps a runaway (or genuinely non-terminating)
/// <c>UNION ALL</c> recursion from exhausting memory, mirroring the tree-walking evaluator's recursive-row cap.
/// </summary>
public sealed class RecursiveWorkTableOverflowException : InvalidOperationException
{
    public RecursiveWorkTableOverflowException(int maxRows)
        : base($"Recursive work table exceeded its row guard of {maxRows} rows.")
    {
        MaxRows = maxRows;
    }

    /// <summary>The row guard that was exceeded.</summary>
    public int MaxRows { get; }
}

public sealed class VdbeProgramValidationException : InvalidOperationException
{
    public VdbeProgramValidationException(string message) : base(message)
    {
    }
}

public sealed class VdbeProgram
{
    private readonly ReadOnlyCollection<VdbeInstruction> _instructions;

    public VdbeProgram(
        int registerCount,
        int cursorCount,
        IEnumerable<VdbeInstruction> instructions,
        int sorterCount,
        int accumulatorCount,
        int distinctSetCount,
        int parameterSlotCount,
        int workTableCount)
        : this(
            registerCount,
            cursorCount,
            instructions,
            sorterCount,
            accumulatorCount,
            distinctSetCount,
            parameterSlotCount,
            workTableCount,
            windowBufferCount: 0)
    {
    }

    public VdbeProgram(
        int registerCount,
        int cursorCount,
        IEnumerable<VdbeInstruction> instructions,
        int sorterCount = 0,
        int accumulatorCount = 0,
        int distinctSetCount = 0,
        int parameterSlotCount = 0,
        int workTableCount = 0,
        int windowBufferCount = 0)
    {
        if (registerCount < 0)
            throw new ArgumentOutOfRangeException(nameof(registerCount));
        if (cursorCount < 0)
            throw new ArgumentOutOfRangeException(nameof(cursorCount));
        if (sorterCount < 0)
            throw new ArgumentOutOfRangeException(nameof(sorterCount));
        if (accumulatorCount < 0)
            throw new ArgumentOutOfRangeException(nameof(accumulatorCount));
        if (distinctSetCount < 0)
            throw new ArgumentOutOfRangeException(nameof(distinctSetCount));
        if (parameterSlotCount < 0)
            throw new ArgumentOutOfRangeException(nameof(parameterSlotCount));
        if (workTableCount < 0)
            throw new ArgumentOutOfRangeException(nameof(workTableCount));
        if (windowBufferCount < 0)
            throw new ArgumentOutOfRangeException(nameof(windowBufferCount));
        ArgumentNullException.ThrowIfNull(instructions);

        RegisterCount = registerCount;
        CursorCount = cursorCount;
        SorterCount = sorterCount;
        AccumulatorCount = accumulatorCount;
        DistinctSetCount = distinctSetCount;
        ParameterSlotCount = parameterSlotCount;
        WorkTableCount = workTableCount;
        WindowBufferCount = windowBufferCount;
        _instructions = Array.AsReadOnly(instructions.ToArray());
        Validate();
    }

    public int RegisterCount { get; }

    public int CursorCount { get; }

    public int SorterCount { get; }

    public int AccumulatorCount { get; }

    public int DistinctSetCount { get; }

    /// <summary>The number of late-bound parameter slots the program reads, i.e. the width of the
    /// <see cref="VdbeParameterBinding"/> a <see cref="ResumableStatement"/> must supply. Zero for a
    /// fully constant program that references no parameters.</summary>
    public int ParameterSlotCount { get; }

    /// <summary>The number of recursive worktables the program opens, i.e. the width of the frontier/queue
    /// resource pool a <see cref="ResumableStatement"/> allocates. Zero for a program that drives no
    /// recursive-CTE evaluation.</summary>
    public int WorkTableCount { get; }

    /// <summary>The number of buffered-window resources the program opens, i.e. the width of the window
    /// buffer pool a <see cref="ResumableStatement"/> allocates. Zero for a program that evaluates no
    /// window functions.</summary>
    public int WindowBufferCount { get; }

    public IReadOnlyList<VdbeInstruction> Instructions => _instructions;

    public void Validate()
    {
        if (_instructions.Count == 0)
            throw new VdbeProgramValidationException("A VDBE program must contain a halt instruction.");
        if (_instructions[^1] is not HaltInstruction)
            throw new VdbeProgramValidationException("A VDBE program must end with a halt instruction.");

        var openCursors = new bool[CursorCount];
        var cursorColumnCounts = new int[CursorCount];
        var openSorters = new bool[SorterCount];
        var sorterColumnCounts = new int[SorterCount];
        var openWorkTables = new bool[WorkTableCount];
        var workTableColumnCounts = new int[WorkTableCount];
        var openWindowBuffers = new bool[WindowBufferCount];
        var windowBufferColumnCounts = new int[WindowBufferCount];
        var windowBufferRecordWidths = new int[WindowBufferCount];
        for (var instructionIndex = 0; instructionIndex < _instructions.Count; instructionIndex++)
        {
            var instruction = _instructions[instructionIndex]
                ?? throw new VdbeProgramValidationException(
                    $"VDBE instruction {instructionIndex} must not be null.");

            switch (instruction)
            {
                case LoadConstantInstruction loadConstant:
                    ValidateRegister(loadConstant.Destination, instructionIndex);
                    break;
                case LoadParameterInstruction loadParameter:
                    ValidateRegister(loadParameter.Destination, instructionIndex);
                    ValidateParameterSlot(loadParameter.Slot, instructionIndex);
                    break;
                case CopyInstruction copy:
                    ValidateRegister(copy.Source, instructionIndex);
                    ValidateRegister(copy.Destination, instructionIndex);
                    break;
                case FunctionInstruction function:
                    ValidateRegister(function.Destination, instructionIndex);
                    if (function.Function is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} applies a null scalar function.");
                    }

                    if (function.Function.Invoke is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} applies scalar function '{function.Function.Name}' with a null invoke delegate.");
                    }

                    ValidateRegisterRange(function.Arguments, instructionIndex);
                    if (function.Function.Arity is { } arity)
                    {
                        if (arity < 0)
                        {
                            throw new VdbeProgramValidationException(
                                $"VDBE instruction {instructionIndex} applies scalar function '{function.Function.Name}' declaring a negative arity {arity}.");
                        }

                        if (function.Arguments.Count != arity)
                        {
                            throw new VdbeProgramValidationException(
                                $"VDBE instruction {instructionIndex} applies scalar function '{function.Function.Name}' of arity {arity} to {function.Arguments.Count} argument(s).");
                        }
                    }

                    break;
                case ArithmeticInstruction arithmetic:
                    ValidateRegister(arithmetic.Destination, instructionIndex);
                    if (!Enum.IsDefined(arithmetic.Operator))
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} applies an undefined arithmetic operator.");
                    }

                    ValidateRegisterRange(arithmetic.Operands, instructionIndex);
                    var arithmeticArity = VdbeArithmetic.Arity(arithmetic.Operator);
                    if (arithmetic.Operands.Count != arithmeticArity)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} applies arithmetic operator '{VdbeArithmetic.Symbol(arithmetic.Operator)}' of arity {arithmeticArity} to {arithmetic.Operands.Count} operand(s).");
                    }

                    break;
                case NumericAffinityInstruction numericAffinity:
                    ValidateRegister(numericAffinity.Value, instructionIndex);
                    if (numericAffinity.Affinity is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} applies a null numeric affinity.");
                    }

                    if (string.IsNullOrEmpty(numericAffinity.Affinity.Name))
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} applies an unnamed numeric affinity.");
                    }

                    if (numericAffinity.Affinity.Apply is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} applies numeric affinity '{numericAffinity.Affinity.Name}' with a null delegate.");
                    }

                    break;
                case CompareInstruction compare:
                    ValidateRegister(compare.Destination, instructionIndex);
                    ValidateRegister(compare.Left, instructionIndex);
                    ValidateRegister(compare.Right, instructionIndex);
                    if (!Enum.IsDefined(compare.Operator))
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} applies an undefined comparison operator.");
                    }

                    ValidateValueAffinity(compare.LeftAffinity, instructionIndex);
                    ValidateValueAffinity(compare.RightAffinity, instructionIndex);
                    if (!SqliteIndexRecordComparer.IsSupportedCollation(compare.Collation))
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} compares with unsupported compiled collation '{compare.Collation}'.");
                    }

                    break;
                case JumpIfNotTrueInstruction jumpIfNotTrue:
                    ValidateRegister(jumpIfNotTrue.Value, instructionIndex);
                    ValidateJumpTarget(jumpIfNotTrue.FalseTarget, instructionIndex);
                    break;
                case CastInstruction cast:
                    ValidateRegister(cast.Value, instructionIndex);
                    if (cast.TypeName is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} casts without a type-name value.");
                    }

                    break;
                case OpenReadCursorInstruction open:
                    ValidateCursor(open.Cursor, instructionIndex);
                    if (openCursors[open.Cursor.Index])
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} opens cursor {open.Cursor.Index} twice.");
                    }

                    if (open.ColumnCount < 0)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} opens cursor {open.Cursor.Index} with a negative column count.");
                    }

                    openCursors[open.Cursor.Index] = true;
                    cursorColumnCounts[open.Cursor.Index] = open.ColumnCount;
                    break;
                case OpenJoinCursorInstruction openJoin:
                    ValidateCursor(openJoin.Cursor, instructionIndex);
                    if (openCursors[openJoin.Cursor.Index])
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} opens cursor {openJoin.Cursor.Index} twice.");
                    }

                    if (openJoin.Plan is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} opens a null join plan.");
                    }

                    openCursors[openJoin.Cursor.Index] = true;
                    cursorColumnCounts[openJoin.Cursor.Index] = openJoin.Plan.RecordColumnCount;
                    break;
                case OpenWriteCursorInstruction openWrite:
                    ValidateCursor(openWrite.Cursor, instructionIndex);
                    if (openCursors[openWrite.Cursor.Index])
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} opens cursor {openWrite.Cursor.Index} twice.");
                    }

                    if (openWrite.ColumnCount < 0)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} opens cursor {openWrite.Cursor.Index} with a negative column count.");
                    }

                    openCursors[openWrite.Cursor.Index] = true;
                    cursorColumnCounts[openWrite.Cursor.Index] = openWrite.ColumnCount;
                    break;
                case IndexMethodCreateInstruction methodCreate:
                    ValidateCursor(methodCreate.Cursor, instructionIndex);
                    ValidateIndexMethodBinding(methodCreate.Binding, instructionIndex);
                    if (openCursors[methodCreate.Cursor.Index])
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} opens cursor {methodCreate.Cursor.Index} twice.");
                    }

                    openCursors[methodCreate.Cursor.Index] = true;
                    break;
                case IndexMethodDestroyInstruction methodDestroy:
                    ValidateCursor(methodDestroy.Cursor, instructionIndex);
                    ValidateIndexMethodBinding(methodDestroy.Binding, instructionIndex);
                    if (openCursors[methodDestroy.Cursor.Index])
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} opens cursor {methodDestroy.Cursor.Index} twice.");
                    }

                    openCursors[methodDestroy.Cursor.Index] = true;
                    break;
                case IndexMethodOptimizeInstruction methodOptimize:
                    ValidateCursor(methodOptimize.Cursor, instructionIndex);
                    ValidateIndexMethodBinding(methodOptimize.Binding, instructionIndex);
                    if (openCursors[methodOptimize.Cursor.Index])
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} opens cursor {methodOptimize.Cursor.Index} twice.");
                    }

                    openCursors[methodOptimize.Cursor.Index] = true;
                    break;
                case IndexMethodQueryInstruction methodQuery:
                    ValidateCursor(methodQuery.Cursor, instructionIndex);
                    ValidateIndexMethodBinding(methodQuery.Binding, instructionIndex);
                    ValidateRegisterRange(methodQuery.Arguments, instructionIndex);
                    ValidateJumpTarget(methodQuery.EmptyTarget, instructionIndex);
                    if (methodQuery.PatternIndex < 0
                        || methodQuery.PatternIndex >= methodQuery.Binding.Attachment.Definition.Patterns.Count)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} names an undeclared index-method pattern {methodQuery.PatternIndex}.");
                    }

                    openCursors[methodQuery.Cursor.Index] = true;
                    break;
                case IndexMethodNextInstruction methodNext:
                    ValidateOpenCursor(methodNext.Cursor, openCursors, instructionIndex);
                    ValidateJumpTarget(methodNext.LoopTarget, instructionIndex);
                    break;
                case IndexMethodColumnInstruction methodColumn:
                    ValidateOpenCursor(methodColumn.Cursor, openCursors, instructionIndex);
                    ValidateRegister(methodColumn.Destination, instructionIndex);
                    if (methodColumn.ColumnIndex < 0)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} reads a negative index-method column.");
                    }

                    break;
                case IndexMethodRowIdInstruction methodRowId:
                    ValidateOpenCursor(methodRowId.Cursor, openCursors, instructionIndex);
                    ValidateRegister(methodRowId.Destination, instructionIndex);
                    break;
                case IndexMethodInsertInstruction methodInsert:
                    ValidateOpenCursor(methodInsert.Cursor, openCursors, instructionIndex);
                    ValidateRegisterRange(methodInsert.Values, instructionIndex);
                    if (methodInsert.Values.Count < 2)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} IndexMethodInsert requires at least one column and a rowid.");
                    }

                    break;
                case IndexMethodDeleteInstruction methodDelete:
                    ValidateOpenCursor(methodDelete.Cursor, openCursors, instructionIndex);
                    ValidateRegisterRange(methodDelete.Values, instructionIndex);
                    if (methodDelete.Values.Count < 2)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} IndexMethodDelete requires at least one column and a rowid.");
                    }

                    break;
                case VOpenInstruction vOpen:
                    ValidateCursor(vOpen.Cursor, instructionIndex);
                    if (openCursors[vOpen.Cursor.Index])
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} opens cursor {vOpen.Cursor.Index} twice.");
                    }

                    openCursors[vOpen.Cursor.Index] = true;
                    break;
                case VFilterInstruction vFilter:
                    ValidateOpenCursor(vFilter.Cursor, openCursors, instructionIndex);
                    ValidateRegisterRange(vFilter.Arguments, instructionIndex);
                    ValidateJumpTarget(vFilter.EmptyTarget, instructionIndex);
                    if (vFilter.Plan is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} filters with a null virtual-table plan.");
                    }

                    break;
                case VColumnInstruction vColumn:
                    ValidateOpenCursor(vColumn.Cursor, openCursors, instructionIndex);
                    ValidateRegister(vColumn.Destination, instructionIndex);
                    if (vColumn.ColumnIndex < 0)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} reads a negative virtual-table column.");
                    }

                    break;
                case VUpdateInstruction vUpdate:
                    ValidateOpenCursor(vUpdate.Cursor, openCursors, instructionIndex);
                    ValidateRegisterRange(vUpdate.Arguments, instructionIndex);
                    if (vUpdate.Arguments.Count < 2)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} VUpdate requires old and new rowid arguments.");
                    }

                    if (vUpdate.NewRowIdDestination is { } destination)
                        ValidateRegister(destination, instructionIndex);
                    if (!Enum.IsDefined(vUpdate.ConflictMode))
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} has an invalid VUpdate conflict mode.");
                    }
                    break;
                case VNextInstruction vNext:
                    ValidateOpenCursor(vNext.Cursor, openCursors, instructionIndex);
                    ValidateJumpTarget(vNext.LoopTarget, instructionIndex);
                    break;
                case VCreateInstruction vCreate:
                    if (string.IsNullOrWhiteSpace(vCreate.ModuleName)
                        || vCreate.Context is null
                        || string.IsNullOrWhiteSpace(vCreate.Context.TableName)
                        || vCreate.Context.Arguments is null
                        || vCreate.Publish is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} has an invalid VCreate binding.");
                    }

                    break;
                case VDestroyInstruction vDestroy:
                    ValidateCursor(vDestroy.Cursor, instructionIndex);
                    if (string.IsNullOrWhiteSpace(vDestroy.TableName))
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} has an empty VDestroy table name.");
                    }

                    break;
                case VRenameInstruction vRename:
                    ValidateCursor(vRename.Cursor, instructionIndex);
                    ValidateRegister(vRename.NewName, instructionIndex);
                    break;
                case MakeRecordInstruction makeRecord:
                    ValidateRegisterRange(makeRecord.Values, instructionIndex);
                    ValidateRegister(makeRecord.Destination, instructionIndex);
                    if (makeRecord.Values.Count <= 0)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} builds a record from an empty register range.");
                    }

                    if (makeRecord.Destination.Index >= makeRecord.Values.Start.Index
                        && makeRecord.Destination.Index < makeRecord.Values.Start.Index + makeRecord.Values.Count)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} writes its record to register {makeRecord.Destination.Index}, which overlaps its source range {FormatRegisterRange(makeRecord.Values)}.");
                    }

                    if (makeRecord.IndexName is not null && makeRecord.IndexName.Length == 0)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} has an empty MakeRecord index name.");
                    }

                    break;
                case NewRowidInstruction newRowid:
                    ValidateOpenCursor(newRowid.Cursor, openCursors, instructionIndex);
                    ValidateRegister(newRowid.Destination, instructionIndex);
                    if (newRowid.PreviousLargest is { } previousLargest)
                    {
                        ValidateRegister(previousLargest, instructionIndex);
                        if (previousLargest.Index == newRowid.Destination.Index)
                        {
                            throw new VdbeProgramValidationException(
                                $"VDBE instruction {instructionIndex} writes the new and previous largest rowid to the same register {previousLargest.Index}.");
                        }
                    }

                    break;
                case CreateBtreeInstruction createBtree:
                    ValidateSchemaDatabase(createBtree.Database, instructionIndex);
                    ValidateRegister(createBtree.RootDestination, instructionIndex);
                    if (createBtree.Flags is not (VdbeCreateBtreeFlags.Table or VdbeCreateBtreeFlags.Index))
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} must create exactly one of a table or index b-tree.");
                    }

                    break;
                case ClearBtreeInstruction clearBtree:
                    ValidateSchemaDatabase(clearBtree.Database, instructionIndex);
                    ValidateSchemaRootPage(clearBtree.RootPage, instructionIndex);
                    break;
                case DestroyInstruction destroy:
                    ValidateSchemaDatabase(destroy.Database, instructionIndex);
                    ValidateRegister(destroy.FormerRootDestination, instructionIndex);
                    if (destroy.RootRegister is { } destroyRootRegister)
                    {
                        ValidateRegister(destroyRootRegister, instructionIndex);
                        if (destroy.RootPage != 0)
                        {
                            throw new VdbeProgramValidationException(
                                $"VDBE instruction {instructionIndex} supplies both a literal root page and a root register to Destroy.");
                        }
                    }
                    else
                    {
                        ValidateSchemaRootPage(destroy.RootPage, instructionIndex);
                    }

                    break;
                case IndexBuildInstruction indexBuild:
                    ValidateSchemaDatabase(indexBuild.Database, instructionIndex);
                    ValidateSchemaObjectName(indexBuild.TableName, "IndexBuild", instructionIndex);
                    ValidateSchemaObjectName(indexBuild.IndexName, "IndexBuild", instructionIndex);
                    break;
                case ReadCookieInstruction readCookie:
                    ValidateSchemaDatabase(readCookie.Database, instructionIndex);
                    ValidateRegister(readCookie.Destination, instructionIndex);
                    ValidateSchemaCookie(readCookie.Cookie, instructionIndex);
                    break;
                case SetCookieInstruction setCookie:
                    ValidateSchemaDatabase(setCookie.Database, instructionIndex);
                    ValidateSchemaCookie(setCookie.Cookie, instructionIndex);
                    if (setCookie.P5 < 0)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} has a negative SetCookie P5 flag word.");
                    }

                    break;
                case ParseSchemaInstruction parseSchema:
                    ValidateSchemaDatabase(parseSchema.Database, instructionIndex);
                    if (parseSchema.WhereClause is not null && string.IsNullOrWhiteSpace(parseSchema.WhereClause))
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} has a blank ParseSchema where clause; use null to reparse the whole schema.");
                    }

                    if (parseSchema.TriggerTargetDatabase is { } triggerTargetDatabase)
                        ValidateSchemaDatabase(triggerTargetDatabase, instructionIndex);
                    break;
                case DropTableInstruction dropTable:
                    ValidateSchemaDatabase(dropTable.Database, instructionIndex);
                    ValidateSchemaObjectName(dropTable.TableName, "DropTable", instructionIndex);
                    break;
                case DropViewInstruction dropView:
                    ValidateSchemaDatabase(dropView.Database, instructionIndex);
                    ValidateSchemaObjectName(dropView.ViewName, "DropView", instructionIndex);
                    break;
                case DropIndexInstruction dropIndex:
                    ValidateSchemaDatabase(dropIndex.Database, instructionIndex);
                    ValidateSchemaObjectName(dropIndex.IndexName, "DropIndex", instructionIndex);
                    break;
                case DropTriggerInstruction dropTrigger:
                    ValidateSchemaDatabase(dropTrigger.Database, instructionIndex);
                    ValidateSchemaObjectName(dropTrigger.TriggerName, "DropTrigger", instructionIndex);
                    break;
                case RenameTableInstruction renameTable:
                    ValidateSchemaDatabase(renameTable.Database, instructionIndex);
                    ValidateSchemaObjectName(renameTable.From, "RenameTable", instructionIndex);
                    ValidateSchemaObjectName(renameTable.To, "RenameTable", instructionIndex);
                    break;
                case AddColumnInstruction addColumn:
                    ValidateSchemaDatabase(addColumn.Database, instructionIndex);
                    ValidateSchemaObjectName(addColumn.TableName, "AddColumn", instructionIndex);
                    ValidateSchemaObjectName(addColumn.ColumnName, "AddColumn", instructionIndex);
                    ValidateSchemaObjectName(addColumn.ColumnDefinition, "AddColumn", instructionIndex);
                    break;
                case DropColumnInstruction dropColumn:
                    ValidateSchemaDatabase(dropColumn.Database, instructionIndex);
                    ValidateSchemaObjectName(dropColumn.TableName, "DropColumn", instructionIndex);
                    ValidateSchemaColumnIndex(dropColumn.ColumnIndex, instructionIndex);
                    break;
                case AlterColumnInstruction alterColumn:
                    ValidateSchemaDatabase(alterColumn.Database, instructionIndex);
                    ValidateSchemaObjectName(alterColumn.TableName, "AlterColumn", instructionIndex);
                    ValidateSchemaColumnIndex(alterColumn.ColumnIndex, instructionIndex);
                    ValidateSchemaObjectName(alterColumn.ColumnDefinition, "AlterColumn", instructionIndex);
                    break;
                case VBeginInstruction vBegin:
                    ValidateCursor(vBegin.Cursor, instructionIndex);
                    break;
                case VSyncInstruction vSync:
                    ValidateCursor(vSync.Cursor, instructionIndex);
                    break;
                case VCommitInstruction vCommit:
                    ValidateCursor(vCommit.Cursor, instructionIndex);
                    break;
                case VRollbackInstruction vRollback:
                    ValidateCursor(vRollback.Cursor, instructionIndex);
                    break;
                case OpenEphemeralInstruction openEphemeral:
                    ValidateCursor(openEphemeral.Cursor, instructionIndex);
                    if (openCursors[openEphemeral.Cursor.Index])
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} opens cursor {openEphemeral.Cursor.Index} twice.");
                    }

                    if (openEphemeral.ColumnCount <= 0)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} opens ephemeral cursor {openEphemeral.Cursor.Index} with a non-positive column count.");
                    }

                    openCursors[openEphemeral.Cursor.Index] = true;
                    cursorColumnCounts[openEphemeral.Cursor.Index] = openEphemeral.ColumnCount;
                    break;
                case EphemeralInsertInstruction ephemeralInsert:
                    ValidateOpenCursor(ephemeralInsert.Cursor, openCursors, instructionIndex);
                    ValidateRegisterRange(ephemeralInsert.Values, instructionIndex);
                    if (ephemeralInsert.Values.Count != cursorColumnCounts[ephemeralInsert.Cursor.Index])
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} inserts {ephemeralInsert.Values.Count} columns into ephemeral cursor {ephemeralInsert.Cursor.Index}, which has {cursorColumnCounts[ephemeralInsert.Cursor.Index]} columns.");
                    }

                    break;
                case CloseCursorInstruction close:
                    ValidateCursor(close.Cursor, instructionIndex);
                    if (!openCursors[close.Cursor.Index])
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} closes cursor {close.Cursor.Index} before opening it.");
                    }

                    openCursors[close.Cursor.Index] = false;
                    break;
                case RewindCursorInstruction rewind:
                    ValidateOpenCursor(rewind.Cursor, openCursors, instructionIndex);
                    ValidateJumpTarget(rewind.EmptyTarget, instructionIndex);
                    break;
                case LastCursorInstruction last:
                    ValidateOpenCursor(last.Cursor, openCursors, instructionIndex);
                    ValidateJumpTarget(last.EmptyTarget, instructionIndex);
                    break;
                case ColumnInstruction column:
                    ValidateOpenCursor(column.Cursor, openCursors, instructionIndex);
                    ValidateRegister(column.Destination, instructionIndex);
                    ValidateColumnIndex(column, cursorColumnCounts[column.Cursor.Index], instructionIndex);
                    break;
                case RowIdInstruction rowId:
                    ValidateOpenCursor(rowId.Cursor, openCursors, instructionIndex);
                    ValidateRegister(rowId.Destination, instructionIndex);
                    break;
                case RowCountInstruction rowCount:
                    ValidateOpenCursor(rowCount.Cursor, openCursors, instructionIndex);
                    ValidateRegister(rowCount.Destination, instructionIndex);
                    break;
                case DeleteInstruction delete:
                    ValidateOpenCursor(delete.Cursor, openCursors, instructionIndex);
                    break;
                case InsertInstruction insert:
                    ValidateOpenCursor(insert.Cursor, openCursors, instructionIndex);
                    if (insert.Record is { } insertRecord)
                    {
                        ValidateRegister(insertRecord, instructionIndex);
                        if (insert.RowId is not { } insertRowId)
                        {
                            throw new VdbeProgramValidationException(
                                $"VDBE instruction {instructionIndex} inserts a record without a rowid register.");
                        }

                        ValidateRegister(insertRowId, instructionIndex);
                        if (insertRowId.Index == insertRecord.Index)
                        {
                            throw new VdbeProgramValidationException(
                                $"VDBE instruction {instructionIndex} reads its record and its rowid from the same register {insertRecord.Index}.");
                        }
                    }
                    else if (insert.RowId is not null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} supplies a rowid register without a record register.");
                    }

                    if (insert.TableName is not null && string.IsNullOrWhiteSpace(insert.TableName))
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} has a blank Insert table name.");
                    }

                    break;
                case UpdateInstruction update:
                    ValidateOpenCursor(update.Cursor, openCursors, instructionIndex);
                    break;
                case ProgramInstruction program:
                    if (program.ParameterRegisters.Count != program.Subprogram.ParameterSlotCount)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} passes {program.ParameterRegisters.Count} parameter register(s) to a subprogram declaring {program.Subprogram.ParameterSlotCount} slot(s).");
                    }

                    foreach (var parameterRegister in program.ParameterRegisters)
                        ValidateRegister(parameterRegister, instructionIndex);
                    break;
                case CommitInstruction commit:
                    ValidateOpenCursor(commit.Cursor, openCursors, instructionIndex);
                    break;
                case FilterInstruction filter:
                    ValidateOpenCursor(filter.Cursor, openCursors, instructionIndex);
                    if (filter.Predicate is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} filters with a null predicate.");
                    }

                    ValidateJumpTarget(filter.FalseTarget, instructionIndex);
                    break;
                case FilterRowIdInstruction filterRowId:
                    ValidateOpenCursor(filterRowId.Cursor, openCursors, instructionIndex);
                    if (filterRowId.Predicate is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} filters with a null rowid predicate.");
                    }

                    ValidateJumpTarget(filterRowId.FalseTarget, instructionIndex);
                    break;
                case SeekRowidInstruction seekRowid:
                    ValidateOpenCursor(seekRowid.Cursor, openCursors, instructionIndex);
                    ValidateRegister(seekRowid.RowIdRegister, instructionIndex);
                    ValidateJumpTarget(seekRowid.NotFoundTarget, instructionIndex);
                    break;
                case NotExistsInstruction notExists:
                    ValidateOpenCursor(notExists.Cursor, openCursors, instructionIndex);
                    ValidateRegister(notExists.RowIdRegister, instructionIndex);
                    ValidateJumpTarget(notExists.JumpTarget, instructionIndex);
                    break;
                case FoundInstruction found:
                    ValidateOpenCursor(found.Cursor, openCursors, instructionIndex);
                    ValidateRegister(found.RowIdRegister, instructionIndex);
                    ValidateJumpTarget(found.FoundTarget, instructionIndex);
                    break;
                case NoConflictInstruction noConflict:
                    ValidateOpenCursor(noConflict.Cursor, openCursors, instructionIndex);
                    ValidateRegisterRange(noConflict.Key, instructionIndex);
                    if (noConflict.Key.Count <= 0)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} NoConflict requires a positive key width.");
                    }

                    ValidateJumpTarget(noConflict.NoConflictTarget, instructionIndex);
                    break;
                case FkCounterInstruction:
                    break;
                case FkIfZeroInstruction fkIfZero:
                    ValidateJumpTarget(fkIfZero.Target, instructionIndex);
                    break;
                case FkCheckInstruction:
                    break;
                case SeekKeyInstruction seekKey:
                    ValidateOpenCursor(seekKey.Cursor, openCursors, instructionIndex);
                    ValidateRegisterRange(seekKey.Key, instructionIndex);
                    if (seekKey.Key.Count <= 0)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} SeekKey requires a positive key width.");
                    }

                    if (seekKey.KeyColumns is not null)
                    {
                        if (seekKey.KeyColumns.Count != seekKey.Key.Count)
                        {
                            throw new VdbeProgramValidationException(
                                $"VDBE instruction {instructionIndex} SeekKey KeyColumns length must match key width.");
                        }

                        for (var i = 0; i < seekKey.KeyColumns.Count; i++)
                        {
                            if (seekKey.KeyColumns[i] < 0)
                            {
                                throw new VdbeProgramValidationException(
                                    $"VDBE instruction {instructionIndex} SeekKey KeyColumns[{i}] is negative.");
                            }
                        }
                    }

                    if (!Enum.IsDefined(seekKey.Operator))
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} has an undefined SeekKey operator.");
                    }

                    ValidateJumpTarget(seekKey.NotFoundTarget, instructionIndex);
                    break;
                case IdxRowIdInstruction idxRowId:
                    ValidateOpenCursor(idxRowId.Cursor, openCursors, instructionIndex);
                    ValidateRegister(idxRowId.Destination, instructionIndex);
                    break;
                case RowDataInstruction rowData:
                    ValidateOpenCursor(rowData.Cursor, openCursors, instructionIndex);
                    ValidateRegisterRange(rowData.Destination, instructionIndex);
                    if (rowData.Destination.Count <= 0)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} RowData requires a positive destination width.");
                    }

                    break;
                case IdxInsertInstruction idxInsert:
                    ValidateOpenCursor(idxInsert.Cursor, openCursors, instructionIndex);
                    ValidateRegisterRange(idxInsert.Key, instructionIndex);
                    if (idxInsert.Key.Count <= 0)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} IdxInsert requires a positive key width.");
                    }

                    break;
                case IdxDeleteInstruction idxDelete:
                    ValidateOpenCursor(idxDelete.Cursor, openCursors, instructionIndex);
                    if (idxDelete.Key is { } deleteKey)
                    {
                        ValidateRegisterRange(deleteKey, instructionIndex);
                        if (deleteKey.Count <= 0)
                        {
                            throw new VdbeProgramValidationException(
                                $"VDBE instruction {instructionIndex} IdxDelete key requires a positive width.");
                        }
                    }

                    break;
                case SeekRowidRangeInstruction seekRowidRange:
                    ValidateOpenCursor(seekRowidRange.Cursor, openCursors, instructionIndex);
                    ValidateRegister(seekRowidRange.StartRowIdRegister, instructionIndex);
                    if (seekRowidRange.EndRowIdRegister is not null)
                    {
                        ValidateRegister(seekRowidRange.EndRowIdRegister.Value, instructionIndex);
                    }

                    ValidateJumpTarget(seekRowidRange.NotFoundTarget, instructionIndex);
                    break;
                case FilterRegistersInstruction filterRegisters:
                    if (filterRegisters.Predicate is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} filters with a null predicate.");
                    }

                    ValidateRegisterRange(filterRegisters.Row, instructionIndex);
                    ValidateJumpTarget(filterRegisters.FalseTarget, instructionIndex);
                    break;
                case GroupKeyInstruction groupKey:
                    ValidateRegisterRange(groupKey.Row, instructionIndex);
                    ValidateRegister(groupKey.Destination, instructionIndex);
                    if (groupKey.KeyCount <= 0)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} projects a non-positive GROUP BY key width.");
                    }

                    if (groupKey.Projector is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} projects a GROUP BY key with a null projector.");
                    }

                    if (groupKey.Equality is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} projects a GROUP BY key with a null equality.");
                    }

                    if (groupKey.KeyOutput is { } keyOutput)
                    {
                        ValidateRegisterRange(keyOutput, instructionIndex);
                        if (keyOutput.Count != groupKey.KeyCount)
                        {
                            throw new VdbeProgramValidationException(
                                $"VDBE instruction {instructionIndex} writes a GROUP BY key of width {groupKey.KeyCount} to a register range of width {keyOutput.Count}.");
                        }
                    }

                    ValidateDistinctSet(groupKey.GroupSetIndex, instructionIndex);
                    break;
                case ProjectRegistersInstruction project:
                    ValidateRegisterRange(project.Input, instructionIndex);
                    ValidateRegisterRange(project.Output, instructionIndex);
                    if (project.Transform is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} projects with a null transform.");
                    }

                    break;
                case DistinctFilterInstruction distinctFilter:
                    ValidateRegisterRange(distinctFilter.Values, instructionIndex);
                    if (distinctFilter.Equality is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} filters distinct rows with a null equality.");
                    }

                    ValidateDistinctSet(distinctFilter.DistinctSetIndex, instructionIndex);
                    ValidateJumpTarget(distinctFilter.DuplicateTarget, instructionIndex);
                    break;
                case NextInstruction next:
                    ValidateOpenCursor(next.Cursor, openCursors, instructionIndex);
                    ValidateJumpTarget(next.LoopTarget, instructionIndex);
                    break;
                case PrevInstruction prev:
                    ValidateOpenCursor(prev.Cursor, openCursors, instructionIndex);
                    ValidateJumpTarget(prev.LoopTarget, instructionIndex);
                    break;
                case OpenSorterInstruction openSorter:
                    ValidateSorter(openSorter.Sorter, instructionIndex);
                    if (openSorters[openSorter.Sorter.Index])
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} opens sorter {openSorter.Sorter.Index} twice.");
                    }

                    if (openSorter.Comparer is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} opens sorter {openSorter.Sorter.Index} with a null comparer.");
                    }

                    if (openSorter.ColumnCount <= 0)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} opens sorter {openSorter.Sorter.Index} with a non-positive column count.");
                    }

                    openSorters[openSorter.Sorter.Index] = true;
                    sorterColumnCounts[openSorter.Sorter.Index] = openSorter.ColumnCount;
                    break;
                case SorterInsertInstruction sorterInsert:
                    ValidateOpenSorter(sorterInsert.Sorter, openSorters, instructionIndex);
                    ValidateRegisterRange(sorterInsert.Record, instructionIndex);
                    ValidateSorterRecordWidth(
                        sorterInsert.Sorter,
                        sorterInsert.Record,
                        sorterColumnCounts[sorterInsert.Sorter.Index],
                        instructionIndex);
                    break;
                case SorterSortInstruction sorterSort:
                    ValidateOpenSorter(sorterSort.Sorter, openSorters, instructionIndex);
                    ValidateJumpTarget(sorterSort.EmptyTarget, instructionIndex);
                    break;
                case SorterDataInstruction sorterData:
                    ValidateOpenSorter(sorterData.Sorter, openSorters, instructionIndex);
                    ValidateRegisterRange(sorterData.Destination, instructionIndex);
                    ValidateSorterRecordWidth(
                        sorterData.Sorter,
                        sorterData.Destination,
                        sorterColumnCounts[sorterData.Sorter.Index],
                        instructionIndex);
                    break;
                case SorterNextInstruction sorterNext:
                    ValidateOpenSorter(sorterNext.Sorter, openSorters, instructionIndex);
                    ValidateJumpTarget(sorterNext.LoopTarget, instructionIndex);
                    break;
                case CloseSorterInstruction closeSorter:
                    ValidateOpenSorter(closeSorter.Sorter, openSorters, instructionIndex);
                    openSorters[closeSorter.Sorter.Index] = false;
                    break;
                case GotoInstruction gotoInstruction:
                    ValidateJumpTarget(gotoInstruction.Target, instructionIndex);
                    break;
                case JumpIfInstruction jumpIf:
                    ValidateRegister(jumpIf.Register, instructionIndex);
                    ValidateJumpTarget(jumpIf.Target, instructionIndex);
                    break;
                case AggResetInstruction aggReset:
                    ValidateAccumulator(aggReset.Accumulator, instructionIndex);
                    break;
                case AggStepInstruction aggStep:
                    ValidateAccumulator(aggStep.Accumulator, instructionIndex);
                    if (aggStep.Aggregate is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} steps accumulator {aggStep.Accumulator.Index} with a null aggregate.");
                    }

                    ValidateRegisterRange(aggStep.Arguments, instructionIndex);
                    break;
                case AggFinalizeInstruction aggFinalize:
                    ValidateAccumulator(aggFinalize.Accumulator, instructionIndex);
                    if (aggFinalize.Aggregate is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} finalizes accumulator {aggFinalize.Accumulator.Index} with a null aggregate.");
                    }

                    ValidateRegister(aggFinalize.Destination, instructionIndex);
                    break;
                case SameGroupInstruction sameGroup:
                    if (sameGroup.Comparer is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} compares groups with a null comparer.");
                    }

                    ValidateRegisterRange(sameGroup.CurrentKey, instructionIndex);
                    ValidateRegisterRange(sameGroup.SavedKey, instructionIndex);
                    if (sameGroup.CurrentKey.Count != sameGroup.SavedKey.Count)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} compares a {sameGroup.CurrentKey.Count}-column key against a {sameGroup.SavedKey.Count}-column key.");
                    }

                    ValidateJumpTarget(sameGroup.SameGroupTarget, instructionIndex);
                    break;
                case ResultRowInstruction resultRow:
                    ValidateRegisterRange(resultRow.Values, instructionIndex);
                    break;
                case DistinctResultRowInstruction distinctRow:
                    ValidateRegisterRange(distinctRow.Values, instructionIndex);
                    if (distinctRow.Equality is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} emits a distinct row with a null equality.");
                    }

                    ValidateDistinctSet(distinctRow.DistinctSetIndex, instructionIndex);
                    break;
                case DistinctGateInstruction distinctGate:
                    ValidateRegisterRange(distinctGate.Values, instructionIndex);
                    if (distinctGate.Equality is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} gates a distinct row with a null equality.");
                    }

                    ValidateDistinctSet(distinctGate.DistinctSetIndex, instructionIndex);
                    ValidateJumpTarget(distinctGate.DuplicateTarget, instructionIndex);
                    break;
                case RowSetInsertInstruction rowSetInsert:
                    ValidateRegisterRange(rowSetInsert.Values, instructionIndex);
                    if (rowSetInsert.Equality is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} inserts into a row set with a null equality.");
                    }

                    ValidateDistinctSet(rowSetInsert.RowSetIndex, instructionIndex);
                    break;
                case RowSetRewindInstruction rowSetRewind:
                    ValidateDistinctSet(rowSetRewind.RowSetIndex, instructionIndex);
                    ValidateRegisterRange(rowSetRewind.Destination, instructionIndex);
                    ValidateJumpTarget(rowSetRewind.EmptyTarget, instructionIndex);
                    break;
                case RowSetNextInstruction rowSetNext:
                    ValidateDistinctSet(rowSetNext.RowSetIndex, instructionIndex);
                    ValidateRegisterRange(rowSetNext.Destination, instructionIndex);
                    ValidateJumpTarget(rowSetNext.LoopTarget, instructionIndex);
                    break;
                case RowSetTestInstruction rowSetTest:
                    ValidateRegister(rowSetTest.RowSetRegister, instructionIndex);
                    ValidateJumpTarget(rowSetTest.FoundTarget, instructionIndex);
                    ValidateRegister(rowSetTest.ValueRegister, instructionIndex);
                    break;
                case CompoundResultRowInstruction compoundRow:
                    ValidateRegisterRange(compoundRow.Values, instructionIndex);
                    if (compoundRow.Equality is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} emits a compound row with a null equality.");
                    }

                    if (compoundRow.MembershipSetIndices is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} emits a compound row with a null membership set list.");
                    }

                    if (!Enum.IsDefined(compoundRow.Mode))
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} emits a compound row with an undefined membership mode.");
                    }

                    ValidateDistinctSet(compoundRow.OutputSetIndex, instructionIndex);
                    foreach (var membershipSet in compoundRow.MembershipSetIndices)
                    {
                        ValidateDistinctSet(membershipSet, instructionIndex);
                        if (membershipSet == compoundRow.OutputSetIndex)
                        {
                            throw new VdbeProgramValidationException(
                                $"VDBE instruction {instructionIndex} emits a compound row whose output set {compoundRow.OutputSetIndex} is also a membership set.");
                        }
                    }

                    break;
                case GuardedRowInstruction guardedRow:
                    ValidateRegisterRange(guardedRow.Values, instructionIndex);
                    if (guardedRow.Guards is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} has a null guarded-row condition list.");
                    }

                    ValidateRowGuards(guardedRow.Guards, instructionIndex);

                    switch (guardedRow.Destination)
                    {
                        case ResultRowDestination:
                            break;
                        case RowSetDestination rowSetDestination:
                            if (rowSetDestination.Equality is null)
                            {
                                throw new VdbeProgramValidationException(
                                    $"VDBE instruction {instructionIndex} has a row-set destination with a null equality.");
                            }

                            ValidateDistinctSet(rowSetDestination.RowSetIndex, instructionIndex);
                            break;
                        case null:
                            throw new VdbeProgramValidationException(
                                $"VDBE instruction {instructionIndex} has a null row destination.");
                        default:
                            throw new VdbeProgramValidationException(
                                $"VDBE instruction {instructionIndex} has unsupported row destination {guardedRow.Destination.GetType().Name}.");
                    }

                    break;
                case RowGateInstruction rowGate:
                    ValidateRegisterRange(rowGate.Values, instructionIndex);
                    ValidateJumpTarget(rowGate.RejectTarget, instructionIndex);
                    if (rowGate.Guards is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} has a null row-gate condition list.");
                    }

                    if (rowGate.Guards.Count == 0)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} has an empty row-gate condition list.");
                    }

                    ValidateRowGuards(rowGate.Guards, instructionIndex);
                    break;
                case OffsetGateInstruction offsetGate:
                    ValidateRegister(offsetGate.Counter, instructionIndex);
                    ValidateJumpTarget(offsetGate.SkipTarget, instructionIndex);
                    break;
                case LimitGateInstruction limitGate:
                    ValidateRegister(limitGate.Counter, instructionIndex);
                    ValidateJumpTarget(limitGate.DoneTarget, instructionIndex);
                    break;
                case YieldInstruction:
                    break;
                case BeginTransactionInstruction:
                case CommitTransactionInstruction:
                case RollbackTransactionInstruction:
                    break;
                case SavepointInstruction savepoint:
                    ValidateSavepointName(savepoint.Name, instructionIndex);
                    break;
                case ReleaseSavepointInstruction release:
                    ValidateSavepointName(release.Name, instructionIndex);
                    break;
                case RollbackToSavepointInstruction rollbackTo:
                    ValidateSavepointName(rollbackTo.Name, instructionIndex);
                    break;
                case OpenWorkTableInstruction openWorkTable:
                    ValidateWorkTable(openWorkTable.WorkTable, instructionIndex);
                    if (openWorkTables[openWorkTable.WorkTable.Index])
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} opens work table {openWorkTable.WorkTable.Index} twice.");
                    }

                    if (openWorkTable.ColumnCount <= 0)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} opens work table {openWorkTable.WorkTable.Index} with a non-positive column count.");
                    }

                    if (openWorkTable.MaxRows <= 0)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} opens work table {openWorkTable.WorkTable.Index} with a non-positive row guard.");
                    }

                    if (openWorkTable.MaxDepth < 0)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} opens work table {openWorkTable.WorkTable.Index} with a negative depth guard.");
                    }

                    if (!Enum.IsDefined(openWorkTable.Mode))
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} opens work table {openWorkTable.WorkTable.Index} with an undefined dedup mode.");
                    }

                    if (openWorkTable.Mode == WorkTableDedupMode.Distinct && openWorkTable.Equality is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} opens a distinct work table {openWorkTable.WorkTable.Index} with a null equality.");
                    }

                    if (openWorkTable.Mode == WorkTableDedupMode.KeepAll && openWorkTable.Equality is not null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} opens a keep-all work table {openWorkTable.WorkTable.Index} with a non-null equality; keep-all performs no de-duplication.");
                    }

                    openWorkTables[openWorkTable.WorkTable.Index] = true;
                    workTableColumnCounts[openWorkTable.WorkTable.Index] = openWorkTable.ColumnCount;
                    break;
                case SeedWorkTableInstruction seed:
                    ValidateOpenWorkTable(seed.WorkTable, openWorkTables, instructionIndex);
                    ValidateRegisterRange(seed.Row, instructionIndex);
                    ValidateWorkTableRecordWidth(
                        seed.WorkTable,
                        seed.Row,
                        workTableColumnCounts[seed.WorkTable.Index],
                        instructionIndex);
                    break;
                case WorkTableStepInstruction step:
                    ValidateOpenWorkTable(step.WorkTable, openWorkTables, instructionIndex);
                    ValidateRegisterRange(step.Destination, instructionIndex);
                    ValidateWorkTableRecordWidth(
                        step.WorkTable,
                        step.Destination,
                        workTableColumnCounts[step.WorkTable.Index],
                        instructionIndex);
                    ValidateJumpTarget(step.DoneTarget, instructionIndex);
                    break;
                case WorkTableExpandInstruction expand:
                    ValidateOpenWorkTable(expand.WorkTable, openWorkTables, instructionIndex);
                    if (expand.Transform is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} expands work table {expand.WorkTable.Index} with a null transform.");
                    }

                    ValidateRegisterRange(expand.Source, instructionIndex);
                    ValidateWorkTableRecordWidth(
                        expand.WorkTable,
                        expand.Source,
                        workTableColumnCounts[expand.WorkTable.Index],
                        instructionIndex);
                    break;
                case WorkTableExpandGenerationInstruction expandGeneration:
                    ValidateOpenWorkTable(expandGeneration.WorkTable, openWorkTables, instructionIndex);
                    if (expandGeneration.Transform is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} expands work table {expandGeneration.WorkTable.Index} with a null generation transform.");
                    }

                    ValidateRegisterRange(expandGeneration.Source, instructionIndex);
                    ValidateWorkTableRecordWidth(
                        expandGeneration.WorkTable,
                        expandGeneration.Source,
                        workTableColumnCounts[expandGeneration.WorkTable.Index],
                        instructionIndex);
                    break;
                case CloseWorkTableInstruction closeWorkTable:
                    ValidateOpenWorkTable(closeWorkTable.WorkTable, openWorkTables, instructionIndex);
                    openWorkTables[closeWorkTable.WorkTable.Index] = false;
                    break;
                case OpenWindowBufferInstruction openWindowBuffer:
                    ValidateWindowBuffer(openWindowBuffer.Buffer, instructionIndex);
                    if (openWindowBuffers[openWindowBuffer.Buffer.Index])
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} opens window buffer {openWindowBuffer.Buffer.Index} twice.");
                    }

                    if (openWindowBuffer.ColumnCount <= 0)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} opens window buffer {openWindowBuffer.Buffer.Index} with a non-positive column count.");
                    }

                    if (openWindowBuffer.WindowCount <= 0)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} opens window buffer {openWindowBuffer.Buffer.Index} with a non-positive window count.");
                    }

                    if (openWindowBuffer.Evaluator is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} opens window buffer {openWindowBuffer.Buffer.Index} with a null window evaluator.");
                    }

                    openWindowBuffers[openWindowBuffer.Buffer.Index] = true;
                    windowBufferColumnCounts[openWindowBuffer.Buffer.Index] = openWindowBuffer.ColumnCount;
                    windowBufferRecordWidths[openWindowBuffer.Buffer.Index] =
                        openWindowBuffer.ColumnCount + openWindowBuffer.WindowCount;
                    break;
                case WindowBufferInsertInstruction windowInsert:
                    ValidateOpenWindowBuffer(windowInsert.Buffer, openWindowBuffers, instructionIndex);
                    ValidateRegisterRange(windowInsert.Record, instructionIndex);
                    ValidateWindowBufferWidth(
                        windowInsert.Buffer,
                        windowInsert.Record,
                        windowBufferColumnCounts[windowInsert.Buffer.Index],
                        "scanned row",
                        instructionIndex);
                    break;
                case WindowBufferComputeInstruction windowCompute:
                    ValidateOpenWindowBuffer(windowCompute.Buffer, openWindowBuffers, instructionIndex);
                    ValidateJumpTarget(windowCompute.EmptyTarget, instructionIndex);
                    break;
                case WindowBufferDataInstruction windowData:
                    ValidateOpenWindowBuffer(windowData.Buffer, openWindowBuffers, instructionIndex);
                    ValidateRegisterRange(windowData.Destination, instructionIndex);
                    ValidateWindowBufferWidth(
                        windowData.Buffer,
                        windowData.Destination,
                        windowBufferRecordWidths[windowData.Buffer.Index],
                        "row-and-window record",
                        instructionIndex);
                    break;
                case WindowBufferNextInstruction windowNext:
                    ValidateOpenWindowBuffer(windowNext.Buffer, openWindowBuffers, instructionIndex);
                    ValidateJumpTarget(windowNext.LoopTarget, instructionIndex);
                    break;
                case CloseWindowBufferInstruction closeWindowBuffer:
                    ValidateOpenWindowBuffer(closeWindowBuffer.Buffer, openWindowBuffers, instructionIndex);
                    openWindowBuffers[closeWindowBuffer.Buffer.Index] = false;
                    break;
                case HaltInstruction halt:
                    // Clean halt (error code 0) is only legal as the terminal instruction.
                    // Error Halt (constraint / RAISE) may appear mid-program, matching Turso.
                    if (halt.ErrorCode == 0 && instructionIndex != _instructions.Count - 1)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} halts before the end of the program.");
                    }

                    if (halt.ErrorCode < 0)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} has a negative Halt error code.");
                    }

                    if (halt.DescriptionRegister is { } descReg)
                        ValidateRegister(descReg, instructionIndex);
                    break;
                case HaltIfNullInstruction haltIfNull:
                    ValidateRegister(haltIfNull.Target, instructionIndex);
                    if (haltIfNull.ErrorCode <= 0)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} HaltIfNull requires a positive error code.");
                    }

                    break;
                default:
                    throw new VdbeProgramValidationException(
                        $"VDBE instruction {instructionIndex} has unsupported opcode {instruction.Opcode}.");
            }
        }
    }

    private void ValidateRegister(Register register, int instructionIndex)
    {
        if (register.Index >= RegisterCount)
        {
            throw new VdbeProgramValidationException(
                $"VDBE instruction {instructionIndex} references register {register.Index}, but the program has {RegisterCount} registers.");
        }
    }

    private static void ValidateIndexMethodBinding(VdbeIndexMethodBinding? binding, int instructionIndex)
    {
        // Resolvability is checked instead of the operands themselves: a deferred binding cannot produce
        // an attachment until the program is bound and run, and forcing it here would attach a method
        // just to describe a program.
        if (binding is null || !binding.IsResolvable)
        {
            throw new VdbeProgramValidationException(
                $"VDBE instruction {instructionIndex} references a null index-method binding.");
        }
    }

    private static void ValidateValueAffinity(VdbeValueAffinity? affinity, int instructionIndex)
    {
        if (affinity is { } value && !Enum.IsDefined(value))
        {
            throw new VdbeProgramValidationException(
                $"VDBE instruction {instructionIndex} applies an undefined value affinity.");
        }
    }

    private void ValidateRegisterRange(RegisterRange range, int instructionIndex)
    {
        if (range.Start.Index > RegisterCount || range.Count > RegisterCount - range.Start.Index)
        {
            throw new VdbeProgramValidationException(
                $"VDBE instruction {instructionIndex} references registers outside the program register range.");
        }
    }

    private void ValidateCursor(Cursor cursor, int instructionIndex)
    {
        if (cursor.Index >= CursorCount)
        {
            throw new VdbeProgramValidationException(
                $"VDBE instruction {instructionIndex} references cursor {cursor.Index}, but the program has {CursorCount} cursors.");
        }
    }

    private void ValidateOpenCursor(Cursor cursor, bool[] openCursors, int instructionIndex)
    {
        ValidateCursor(cursor, instructionIndex);
        if (!openCursors[cursor.Index])
        {
            throw new VdbeProgramValidationException(
                $"VDBE instruction {instructionIndex} uses cursor {cursor.Index} before opening it.");
        }
    }

    private static void ValidateColumnIndex(ColumnInstruction column, int cursorColumnCount, int instructionIndex)
    {
        if (column.ColumnIndex < 0)
        {
            throw new VdbeProgramValidationException(
                $"VDBE instruction {instructionIndex} reads a negative column index.");
        }

        if (cursorColumnCount > 0 && column.ColumnIndex >= cursorColumnCount)
        {
            throw new VdbeProgramValidationException(
                $"VDBE instruction {instructionIndex} reads column {column.ColumnIndex} of cursor {column.Cursor.Index}, which exposes {cursorColumnCount} columns.");
        }
    }

    private void ValidateJumpTarget(ProgramCounter target, int instructionIndex)
    {
        if (target.Offset >= _instructions.Count)
        {
            throw new VdbeProgramValidationException(
                $"VDBE instruction {instructionIndex} jumps to {target.Offset}, outside the {_instructions.Count}-instruction program.");
        }
    }

    private void ValidateSorter(Sorter sorter, int instructionIndex)
    {
        if (sorter.Index >= SorterCount)
        {
            throw new VdbeProgramValidationException(
                $"VDBE instruction {instructionIndex} references sorter {sorter.Index}, but the program has {SorterCount} sorters.");
        }
    }

    private void ValidateOpenSorter(Sorter sorter, bool[] openSorters, int instructionIndex)
    {
        ValidateSorter(sorter, instructionIndex);
        if (!openSorters[sorter.Index])
        {
            throw new VdbeProgramValidationException(
                $"VDBE instruction {instructionIndex} uses sorter {sorter.Index} before opening it.");
        }
    }

    private static void ValidateSorterRecordWidth(
        Sorter sorter,
        RegisterRange range,
        int sorterColumnCount,
        int instructionIndex)
    {
        if (range.Count != sorterColumnCount)
        {
            throw new VdbeProgramValidationException(
                $"VDBE instruction {instructionIndex} moves {range.Count} registers for sorter {sorter.Index}, which stores {sorterColumnCount}-column records.");
        }
    }

    private void ValidateWorkTable(WorkTable workTable, int instructionIndex)
    {
        if (workTable.Index >= WorkTableCount)
        {
            throw new VdbeProgramValidationException(
                $"VDBE instruction {instructionIndex} references work table {workTable.Index}, but the program has {WorkTableCount} work tables.");
        }
    }

    private void ValidateOpenWorkTable(WorkTable workTable, bool[] openWorkTables, int instructionIndex)
    {
        ValidateWorkTable(workTable, instructionIndex);
        if (!openWorkTables[workTable.Index])
        {
            throw new VdbeProgramValidationException(
                $"VDBE instruction {instructionIndex} uses work table {workTable.Index} before opening it.");
        }
    }

    private static void ValidateWorkTableRecordWidth(
        WorkTable workTable,
        RegisterRange range,
        int workTableColumnCount,
        int instructionIndex)
    {
        if (range.Count != workTableColumnCount)
        {
            throw new VdbeProgramValidationException(
                $"VDBE instruction {instructionIndex} moves {range.Count} registers for work table {workTable.Index}, which stores {workTableColumnCount}-column records.");
        }
    }

    private void ValidateWindowBuffer(WindowBuffer buffer, int instructionIndex)
    {
        if (buffer.Index >= WindowBufferCount)
        {
            throw new VdbeProgramValidationException(
                $"VDBE instruction {instructionIndex} references window buffer {buffer.Index}, but the program has {WindowBufferCount} window buffers.");
        }
    }

    private void ValidateOpenWindowBuffer(WindowBuffer buffer, bool[] openWindowBuffers, int instructionIndex)
    {
        ValidateWindowBuffer(buffer, instructionIndex);
        if (!openWindowBuffers[buffer.Index])
        {
            throw new VdbeProgramValidationException(
                $"VDBE instruction {instructionIndex} uses window buffer {buffer.Index} before opening it.");
        }
    }

    private static void ValidateWindowBufferWidth(
        WindowBuffer buffer,
        RegisterRange range,
        int expectedWidth,
        string shape,
        int instructionIndex)
    {
        if (range.Count != expectedWidth)
        {
            throw new VdbeProgramValidationException(
                $"VDBE instruction {instructionIndex} moves {range.Count} registers for window buffer {buffer.Index}, whose {shape} is {expectedWidth} columns wide.");
        }
    }

    // Shared by GuardedRow and RowGate: both drive the same guard pipeline, so they must agree on which
    // guard shapes the executor may see.
    private void ValidateRowGuards(IReadOnlyList<VdbeRowGuard> guards, int instructionIndex)
    {
        foreach (var guard in guards)
        {
            switch (guard)
            {
                case DistinctRowGuard distinctGuard:
                    if (distinctGuard.Equality is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} has a distinct row guard with a null equality.");
                    }

                    ValidateDistinctSet(distinctGuard.RowSetIndex, instructionIndex);
                    break;
                case MembershipRowGuard membershipGuard:
                    if (membershipGuard.Equality is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} has a membership row guard with a null equality.");
                    }

                    if (membershipGuard.RowSetIndices is null)
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} has a null membership row-set list.");
                    }

                    if (!Enum.IsDefined(membershipGuard.Mode))
                    {
                        throw new VdbeProgramValidationException(
                            $"VDBE instruction {instructionIndex} has an undefined membership mode.");
                    }

                    foreach (var rowSetIndex in membershipGuard.RowSetIndices)
                        ValidateDistinctSet(rowSetIndex, instructionIndex);
                    break;
                case null:
                    throw new VdbeProgramValidationException(
                        $"VDBE instruction {instructionIndex} has a null row guard.");
                default:
                    throw new VdbeProgramValidationException(
                        $"VDBE instruction {instructionIndex} has unsupported row guard {guard.GetType().Name}.");
            }
        }
    }

    private void ValidateAccumulator(Accumulator accumulator, int instructionIndex)
    {
        if (accumulator.Index >= AccumulatorCount)
        {
            throw new VdbeProgramValidationException(
                $"VDBE instruction {instructionIndex} references accumulator {accumulator.Index}, but the program has {AccumulatorCount} accumulators.");
        }
    }

    private void ValidateDistinctSet(int distinctSetIndex, int instructionIndex)
    {
        if (distinctSetIndex < 0 || distinctSetIndex >= DistinctSetCount)
        {
            throw new VdbeProgramValidationException(
                $"VDBE instruction {instructionIndex} references distinct set {distinctSetIndex}, but the program has {DistinctSetCount} distinct sets.");
        }
    }

    private void ValidateParameterSlot(ParameterSlot slot, int instructionIndex)
    {
        if (slot.Index >= ParameterSlotCount)
        {
            throw new VdbeProgramValidationException(
                $"VDBE instruction {instructionIndex} references parameter slot {slot.Index}, but the program has {ParameterSlotCount} parameter slots.");
        }
    }

    private static void ValidateSavepointName(string name, int instructionIndex)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new VdbeProgramValidationException(
                $"VDBE instruction {instructionIndex} references a savepoint with a null or empty name.");
        }
    }

    private static string FormatRegisterRange(RegisterRange range)
        => range.Count == 1
            ? $"r[{range.Start.Index}]"
            : $"r[{range.Start.Index}..{range.Start.Index + range.Count - 1}]";

    private static void ValidateSchemaDatabase(int database, int instructionIndex)
    {
        if (database < 0)
        {
            throw new VdbeProgramValidationException(
                $"VDBE instruction {instructionIndex} addresses a negative database index {database}.");
        }
    }

    private static void ValidateSchemaRootPage(long rootPage, int instructionIndex)
    {
        // Page 1 is the database header/schema root and can never be cleared or destroyed by bytecode.
        if (rootPage <= 1)
        {
            throw new VdbeProgramValidationException(
                $"VDBE instruction {instructionIndex} addresses root page {rootPage}, which is not an allocatable b-tree root.");
        }
    }

    private static void ValidateSchemaCookie(VdbeSchemaCookie cookie, int instructionIndex)
    {
        if (!Enum.IsDefined(cookie))
        {
            throw new VdbeProgramValidationException(
                $"VDBE instruction {instructionIndex} addresses an undefined header cookie {(int)cookie}.");
        }
    }

    private static void ValidateSchemaObjectName(string name, string opcodeName, int instructionIndex)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new VdbeProgramValidationException(
                $"VDBE instruction {instructionIndex} has a null or blank {opcodeName} name.");
        }
    }

    private static void ValidateSchemaColumnIndex(int columnIndex, int instructionIndex)
    {
        if (columnIndex < 0)
        {
            throw new VdbeProgramValidationException(
                $"VDBE instruction {instructionIndex} addresses a negative column index {columnIndex}.");
        }
    }
}
