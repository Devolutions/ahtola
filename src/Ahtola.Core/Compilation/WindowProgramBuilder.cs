using Ahtola.Core.Execution;

namespace Ahtola.Core.Compilation;

/// <summary>The frame kind a window's rows are drawn from. <see cref="Rows"/> is the physical
/// frame; <see cref="Range"/> and <see cref="Groups"/> share a peer-group delay for the
/// unbounded-preceding and current-row bounds the streaming builder models.</summary>
public enum WindowFrameMode
{
    /// <summary>Physical <c>ROWS</c> framing: each row is an independent frame position,
    /// so ties in the ORDER BY key are not grouped into peers.</summary>
    Rows,

    /// <summary>Logical <c>RANGE</c> framing (peer-inclusive).</summary>
    Range,

    /// <summary>Peer-group <c>GROUPS</c> framing.</summary>
    Groups,
}

/// <summary>One boundary of a window frame. Mirrors the five SQL frame bounds so an
/// unsupported bound can be named and rejected instead of misinterpreted.</summary>
public enum WindowBound
{
    UnboundedPreceding,
    Preceding,
    CurrentRow,
    Following,
    UnboundedFollowing,
}

/// <summary>SQL <c>EXCLUDE</c> clause. Streaming currently models <see cref="NoOthers"/> and
/// <see cref="CurrentRow"/> on ROWS running/current-row frames.</summary>
public enum WindowExclusion
{
    NoOthers,
    CurrentRow,
    Group,
    Ties,
}

/// <summary>
/// The frame a window function is evaluated over. <see cref="WindowProgramBuilder"/> models the running
/// prefix (<see cref="Running"/>), the exact current row (<see cref="CurrentRow"/>), bounded
/// <c>ROWS n PRECEDING / m FOLLOWING</c> frames, and peer-inclusive
/// <c>RANGE/GROUPS UNBOUNDED PRECEDING TO CURRENT ROW</c> / <c>CURRENT ROW</c> frames,
/// and <c>GROUPS n PRECEDING TO CURRENT ROW</c>.
/// Other frames are rejected because they need RANGE value offsets, GROUPS FOLLOWING,
/// UNBOUNDED FOLLOWING, or EXCLUDE.
/// </summary>
/// <remarks>
/// This is a VDBE-lowering primitive, distinct from the evaluator's SQL-level frame AST: it exists so the
/// builder's caller can state the frame it lowered and the builder can honestly reject frames it does not
/// implement (RANGE/GROUPS value or group offsets, UNBOUNDED FOLLOWING, EXCLUDE clauses). A window's
/// ORDER BY and PARTITION BY are supplied separately through the comparer delegates.
/// </remarks>
public readonly record struct WindowFrameSpec(
    WindowFrameMode Mode,
    WindowBound Start,
    WindowBound End,
    long? StartOffset = null,
    long? EndOffset = null,
    WindowExclusion Exclusion = WindowExclusion.NoOthers)
{
    /// <summary>
    /// The largest <c>n</c> a streaming <c>ROWS n PRECEDING</c> program will retain as departing
    /// argument slots. Larger offsets stay on the buffered evaluator.
    /// </summary>
    public const int MaxStreamingPreceding = 1024;

    /// <summary>A running frame: <c>ROWS UNBOUNDED PRECEDING TO CURRENT ROW</c>.</summary>
    public static WindowFrameSpec Running => new(WindowFrameMode.Rows, WindowBound.UnboundedPreceding, WindowBound.CurrentRow);

    /// <summary>A one-row moving frame: <c>ROWS CURRENT ROW TO CURRENT ROW</c>.</summary>
    public static WindowFrameSpec CurrentRow => new(WindowFrameMode.Rows, WindowBound.CurrentRow, WindowBound.CurrentRow);

    /// <summary>A two-row moving frame: <c>ROWS 1 PRECEDING TO CURRENT ROW</c>.</summary>
    public static WindowFrameSpec OnePreceding => Preceding(1);

    /// <summary>A moving frame: <c>ROWS n PRECEDING TO CURRENT ROW</c>.</summary>
    public static WindowFrameSpec Preceding(long n) => new(
        WindowFrameMode.Rows,
        WindowBound.Preceding,
        WindowBound.CurrentRow,
        StartOffset: n);

    /// <summary>A moving frame: <c>ROWS CURRENT ROW TO m FOLLOWING</c>.</summary>
    public static WindowFrameSpec Following(long m) => new(
        WindowFrameMode.Rows,
        WindowBound.CurrentRow,
        WindowBound.Following,
        EndOffset: m);

    /// <summary>A moving frame: <c>ROWS n PRECEDING TO m FOLLOWING</c>.</summary>
    public static WindowFrameSpec PrecedingAndFollowing(long n, long m) => new(
        WindowFrameMode.Rows,
        WindowBound.Preceding,
        WindowBound.Following,
        StartOffset: n,
        EndOffset: m);

    /// <summary>Whether this frame is the running-rows frame.</summary>
    public bool IsRunning => Mode == WindowFrameMode.Rows
        && Start == WindowBound.UnboundedPreceding
        && End == WindowBound.CurrentRow
        && StartOffset is null
        && EndOffset is null;

    /// <summary>Whether this frame contains exactly the current physical row.</summary>
    public bool IsCurrentRow => Mode == WindowFrameMode.Rows
        && Start == WindowBound.CurrentRow
        && End == WindowBound.CurrentRow
        && StartOffset is null
        && EndOffset is null;

    /// <summary>Whether this frame contains the current row and its immediate predecessor.</summary>
    public bool IsOnePreceding => IsBoundedPreceding && StartOffset == 1;

    /// <summary>Whether this frame is <c>ROWS n PRECEDING TO CURRENT ROW</c> for a streaming-safe n.</summary>
    public bool IsBoundedPreceding => Mode == WindowFrameMode.Rows
        && Start == WindowBound.Preceding
        && End == WindowBound.CurrentRow
        && StartOffset is > 0 and <= MaxStreamingPreceding
        && EndOffset is null;

    /// <summary>Whether this frame is <c>ROWS CURRENT ROW TO m FOLLOWING</c> for a streaming-safe m.</summary>
    public bool IsBoundedFollowing => Mode == WindowFrameMode.Rows
        && Start == WindowBound.CurrentRow
        && End == WindowBound.Following
        && StartOffset is null
        && EndOffset is > 0 and <= MaxStreamingPreceding;

    /// <summary>Whether this frame is <c>ROWS n PRECEDING TO m FOLLOWING</c> for streaming-safe n, m.</summary>
    public bool IsBoundedPrecedingFollowing => Mode == WindowFrameMode.Rows
        && Start == WindowBound.Preceding
        && End == WindowBound.Following
        && StartOffset is > 0 and <= MaxStreamingPreceding
        && EndOffset is > 0 and <= MaxStreamingPreceding;

    /// <summary>The <c>n</c> in a bounded preceding frame; 0 when the frame is not one.</summary>
    public int PrecedingCount => IsBoundedPreceding || IsBoundedPrecedingFollowing
        ? (int)StartOffset!.Value
        : 0;

    /// <summary>The <c>m</c> in a bounded following frame; 0 when the frame is not one.</summary>
    public int FollowingCount => IsBoundedFollowing || IsBoundedPrecedingFollowing
        ? (int)EndOffset!.Value
        : 0;

    /// <summary>
    /// Peer-inclusive running frame: <c>RANGE/GROUPS UNBOUNDED PRECEDING TO CURRENT ROW</c>.
    /// Delayed until the current ORDER BY peer group ends.
    /// </summary>
    public static WindowFrameSpec RangeRunning => new(
        WindowFrameMode.Range,
        WindowBound.UnboundedPreceding,
        WindowBound.CurrentRow);

    /// <summary>Peer-inclusive running frame using <c>GROUPS</c> (same delay as <see cref="RangeRunning"/>).</summary>
    public static WindowFrameSpec GroupsRunning => new(
        WindowFrameMode.Groups,
        WindowBound.UnboundedPreceding,
        WindowBound.CurrentRow);

    /// <summary>Current peer group only: <c>RANGE CURRENT ROW TO CURRENT ROW</c>.</summary>
    public static WindowFrameSpec RangeCurrentPeer => new(
        WindowFrameMode.Range,
        WindowBound.CurrentRow,
        WindowBound.CurrentRow);

    /// <summary>Current peer group only: <c>GROUPS CURRENT ROW TO CURRENT ROW</c>.</summary>
    public static WindowFrameSpec GroupsCurrentPeer => new(
        WindowFrameMode.Groups,
        WindowBound.CurrentRow,
        WindowBound.CurrentRow);

    /// <summary>A moving frame: <c>GROUPS n PRECEDING TO CURRENT ROW</c>.</summary>
    public static WindowFrameSpec GroupsPreceding(long n) => new(
        WindowFrameMode.Groups,
        WindowBound.Preceding,
        WindowBound.CurrentRow,
        StartOffset: n);

    /// <summary>A moving frame: <c>RANGE n PRECEDING TO CURRENT ROW</c>.</summary>
    public static WindowFrameSpec RangePreceding(long n) => new(
        WindowFrameMode.Range,
        WindowBound.Preceding,
        WindowBound.CurrentRow,
        StartOffset: n);

    /// <summary>A moving frame: <c>GROUPS CURRENT ROW TO m FOLLOWING</c>.</summary>
    public static WindowFrameSpec GroupsFollowing(long n) => new(
        WindowFrameMode.Groups,
        WindowBound.CurrentRow,
        WindowBound.Following,
        EndOffset: n);

    /// <summary><c>ROWS CURRENT ROW TO UNBOUNDED FOLLOWING</c>.</summary>
    public static WindowFrameSpec RowsUnboundedFollowing => new(
        WindowFrameMode.Rows,
        WindowBound.CurrentRow,
        WindowBound.UnboundedFollowing);

    /// <summary><c>ROWS UNBOUNDED PRECEDING TO UNBOUNDED FOLLOWING</c>.</summary>
    public static WindowFrameSpec RowsFullPartition => new(
        WindowFrameMode.Rows,
        WindowBound.UnboundedPreceding,
        WindowBound.UnboundedFollowing);

    /// <summary>A moving frame: <c>RANGE CURRENT ROW TO n FOLLOWING</c>.</summary>
    public static WindowFrameSpec RangeFollowing(long n) => new(
        WindowFrameMode.Range,
        WindowBound.CurrentRow,
        WindowBound.Following,
        EndOffset: n);

    /// <summary><c>RANGE CURRENT ROW TO UNBOUNDED FOLLOWING</c>.</summary>
    public static WindowFrameSpec RangeUnboundedFollowing => new(
        WindowFrameMode.Range,
        WindowBound.CurrentRow,
        WindowBound.UnboundedFollowing);

    /// <summary><c>GROUPS CURRENT ROW TO UNBOUNDED FOLLOWING</c>.</summary>
    public static WindowFrameSpec GroupsUnboundedFollowing => new(
        WindowFrameMode.Groups,
        WindowBound.CurrentRow,
        WindowBound.UnboundedFollowing);

    /// <summary><c>RANGE UNBOUNDED PRECEDING TO UNBOUNDED FOLLOWING</c> (whole partition).</summary>
    public static WindowFrameSpec RangeFullPartition => new(
        WindowFrameMode.Range,
        WindowBound.UnboundedPreceding,
        WindowBound.UnboundedFollowing);

    /// <summary><c>GROUPS UNBOUNDED PRECEDING TO UNBOUNDED FOLLOWING</c> (whole partition).</summary>
    public static WindowFrameSpec GroupsFullPartition => new(
        WindowFrameMode.Groups,
        WindowBound.UnboundedPreceding,
        WindowBound.UnboundedFollowing);

    /// <summary>Whether this frame is RANGE/GROUPS UNBOUNDED PRECEDING TO CURRENT ROW.</summary>
    public bool IsPeerRunning => Mode is WindowFrameMode.Range or WindowFrameMode.Groups
        && Start == WindowBound.UnboundedPreceding
        && End == WindowBound.CurrentRow
        && StartOffset is null
        && EndOffset is null;

    /// <summary>Whether this frame is RANGE/GROUPS CURRENT ROW TO CURRENT ROW.</summary>
    public bool IsPeerCurrent => Mode is WindowFrameMode.Range or WindowFrameMode.Groups
        && Start == WindowBound.CurrentRow
        && End == WindowBound.CurrentRow
        && StartOffset is null
        && EndOffset is null;

    /// <summary>Whether this frame is <c>GROUPS n PRECEDING TO CURRENT ROW</c> for a streaming-safe n.</summary>
    public bool IsGroupsPreceding => Mode == WindowFrameMode.Groups
        && Start == WindowBound.Preceding
        && End == WindowBound.CurrentRow
        && StartOffset is > 0 and <= MaxStreamingPreceding
        && EndOffset is null;

    /// <summary>The <c>n</c> in a GROUPS preceding frame; 0 when the frame is not one.</summary>
    public int GroupsPrecedingCount => IsGroupsPreceding ? (int)StartOffset!.Value : 0;

    /// <summary>Whether this frame is <c>RANGE n PRECEDING TO CURRENT ROW</c> for a streaming-safe n.</summary>
    public bool IsRangePreceding => Mode == WindowFrameMode.Range
        && Start == WindowBound.Preceding
        && End == WindowBound.CurrentRow
        && StartOffset is > 0 and <= MaxStreamingPreceding
        && EndOffset is null;

    /// <summary>The <c>n</c> in a RANGE preceding frame; 0 when the frame is not one.</summary>
    public int RangePrecedingOffset => IsRangePreceding ? (int)StartOffset!.Value : 0;

    /// <summary>Whether this frame is <c>GROUPS CURRENT ROW TO m FOLLOWING</c> for a streaming-safe m.</summary>
    public bool IsGroupsFollowing => Mode == WindowFrameMode.Groups
        && Start == WindowBound.CurrentRow
        && End == WindowBound.Following
        && StartOffset is null
        && EndOffset is > 0 and <= MaxStreamingPreceding;

    /// <summary>The <c>m</c> in a GROUPS following frame; 0 when the frame is not one.</summary>
    public int GroupsFollowingCount => IsGroupsFollowing ? (int)EndOffset!.Value : 0;

    /// <summary>Whether this frame is <c>RANGE CURRENT ROW TO n FOLLOWING</c> for a streaming-safe n.</summary>
    public bool IsRangeFollowing => Mode == WindowFrameMode.Range
        && Start == WindowBound.CurrentRow
        && End == WindowBound.Following
        && StartOffset is null
        && EndOffset is > 0 and <= MaxStreamingPreceding;

    /// <summary>The <c>n</c> in a RANGE following frame; 0 when the frame is not one.</summary>
    public int RangeFollowingOffset => IsRangeFollowing ? (int)EndOffset!.Value : 0;

    /// <summary>Whether this frame is RANGE/GROUPS CURRENT ROW TO UNBOUNDED FOLLOWING.</summary>
    public bool IsUnboundedFollowing => Mode is WindowFrameMode.Range or WindowFrameMode.Groups
        && Start == WindowBound.CurrentRow
        && End == WindowBound.UnboundedFollowing
        && StartOffset is null
        && EndOffset is null;

    /// <summary>Whether this frame is RANGE/GROUPS UNBOUNDED PRECEDING TO UNBOUNDED FOLLOWING.</summary>
    public bool IsFullPartition => Mode is WindowFrameMode.Range or WindowFrameMode.Groups
        && Start == WindowBound.UnboundedPreceding
        && End == WindowBound.UnboundedFollowing
        && StartOffset is null
        && EndOffset is null;

    /// <summary>Whether emit is delayed until the current ORDER BY peer group ends.</summary>
    public bool IsPeerFrame => IsPeerRunning
        || IsPeerCurrent
        || IsGroupsPreceding
        || IsRangePreceding
        || IsGroupsFollowing
        || IsRangeFollowing
        || IsUnboundedFollowing
        || IsFullPartition
        || (IsRunning && Exclusion is WindowExclusion.Group or WindowExclusion.Ties);

    /// <summary>Whether EXCLUDE GROUP/TIES is representable on this frame.</summary>
    public bool SupportsPeerExclusion => IsPeerRunning
        || IsPeerCurrent
        || IsRunning;

    /// <summary>Whether this frame is <c>ROWS CURRENT ROW TO UNBOUNDED FOLLOWING</c>.</summary>
    public bool IsRowsUnboundedFollowing => Mode == WindowFrameMode.Rows
        && Start == WindowBound.CurrentRow
        && End == WindowBound.UnboundedFollowing
        && StartOffset is null
        && EndOffset is null;

    /// <summary>Whether this frame is <c>ROWS UNBOUNDED PRECEDING TO UNBOUNDED FOLLOWING</c>.</summary>
    public bool IsRowsFullPartition => Mode == WindowFrameMode.Rows
        && Start == WindowBound.UnboundedPreceding
        && End == WindowBound.UnboundedFollowing
        && StartOffset is null
        && EndOffset is null;

    /// <summary>Whether the frame omits the current physical row via <c>EXCLUDE CURRENT ROW</c>.</summary>
    public bool ExcludesCurrentRow => Exclusion == WindowExclusion.CurrentRow;

    /// <summary>Whether this frame can be lowered by the streaming builder.</summary>
    public bool IsSupported => (Exclusion switch
        {
            WindowExclusion.NoOthers => true,
            WindowExclusion.CurrentRow => IsRunning || IsCurrentRow || IsPeerRunning || IsPeerCurrent,
            WindowExclusion.Group or WindowExclusion.Ties => SupportsPeerExclusion,
            _ => false,
        })
        && (IsRunning
            || IsCurrentRow
            || IsBoundedPreceding
            || IsBoundedFollowing
            || IsBoundedPrecedingFollowing
            || IsPeerFrame
            || IsRowsUnboundedFollowing
            || IsRowsFullPartition);

    /// <summary>Whether rows leave this frame and require an inverse-capable aggregate.</summary>
    public bool RequiresInverse => IsCurrentRow
        || IsBoundedPreceding
        || IsBoundedFollowing
        || IsBoundedPrecedingFollowing
        || IsGroupsPreceding
        || IsRangePreceding
        || IsGroupsFollowing
        || IsRangeFollowing
        || IsUnboundedFollowing
        || IsRowsUnboundedFollowing
        || Exclusion is WindowExclusion.Ties
        || (Exclusion is WindowExclusion.CurrentRow && (IsPeerRunning || IsPeerCurrent));
}

/// <summary>The kind of value a window result column projects.</summary>
public enum WindowOutputKind
{
    /// <summary>A pass-through column of the current (sorted) row, e.g. a partition or order column.</summary>
    Column,

    /// <summary>The finalized value of one window function at the current row.</summary>
    Window,

    /// <summary>A folded compile-time constant.</summary>
    Constant,
}

/// <summary>
/// One output column of a window result row: a pass-through scanned column of the current row, the
/// finalized value of one window function at that row, or a folded constant. Mirrors the aggregate and
/// sorted-scan output primitives so the builder stays free of AST and SQL semantics.
/// </summary>
public readonly record struct WindowOutput
{
    private WindowOutput(WindowOutputKind kind, int index, SqlValue constant)
    {
        Kind = kind;
        Index = index;
        Constant = constant;
    }

    public WindowOutputKind Kind { get; }

    /// <summary>The scanned-column ordinal (<see cref="WindowOutputKind.Column"/>) or the window-function
    /// ordinal (<see cref="WindowOutputKind.Window"/>) this output reads.</summary>
    public int Index { get; }

    /// <summary>The value emitted for a constant output.</summary>
    public SqlValue Constant { get; }

    /// <summary>Projects the current row's value of the scanned column at <paramref name="columnIndex"/>.</summary>
    public static WindowOutput ForColumn(int columnIndex)
    {
        if (columnIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(columnIndex));

        return new WindowOutput(WindowOutputKind.Column, columnIndex, default);
    }

    /// <summary>Projects the finalized value of the window function at <paramref name="windowIndex"/>.</summary>
    public static WindowOutput ForWindow(int windowIndex)
    {
        if (windowIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(windowIndex));

        return new WindowOutput(WindowOutputKind.Window, windowIndex, default);
    }

    /// <summary>Projects a folded compile-time constant.</summary>
    public static WindowOutput ForConstant(SqlValue value) => new(WindowOutputKind.Constant, 0, value);
}

/// <summary>
/// Lowers a partitioned streaming aggregate window into a runnable <see cref="VdbeProgram"/> built from the
/// sorter and aggregate opcode families. The program materializes every scanned row into a sorter ordered
/// by <c>(PARTITION BY keys, ORDER BY keys)</c> so each partition is a contiguous, in-order run, then walks
/// the sorted rows once: it resets the accumulators at each partition boundary, folds the current row into
/// them, finalizes them, and emits one result row per input row. So a supported window
/// (<c>func(...) OVER (PARTITION BY ... ORDER BY ...)</c>) runs entirely through the resumable state machine
/// rather than the tree-walking evaluator, with no precomputed output.
/// </summary>
/// <remarks>
/// <para>
/// The builder owns only the program's control flow and register/jump layout. Every SQL semantic is supplied
/// by the caller through the same delegate contracts the aggregate and sorted-scan builders use: the
/// per-function accumulation semantics (<see cref="VdbeAggregate"/>), the <c>(partition, order)</c> ordering
/// that makes partitions contiguous and rows within a partition window-ordered (<see cref="VdbeRowComparer"/>),
/// the partition-key equality used to detect partition boundaries (<see cref="VdbeGroupComparer"/>), and the
/// optional WHERE predicate (<see cref="VdbeRowPredicate"/>). The emitted program is data-free: the scanned
/// rows are bound at execution time through a <see cref="VdbeCursorSource"/>.
/// </para>
/// <para>
/// The running frame (<see cref="WindowFrameSpec.Running"/>) folds every partition row up to and including
/// the current row, restarting per partition. <c>row_number()</c> is expressed as a running
/// <c>count(*)</c> (a nullary window function), and running <c>sum</c>/<c>count</c>/<c>avg</c>/<c>min</c>/<c>max</c>
/// follow from the corresponding accumulators. Because the finalize step runs once per row against the
/// still-open accumulator, each window function's <see cref="VdbeAggregate.Finalize"/> must be side-effect
/// free (as the standard aggregates are). Peer frames delay emit until the current ORDER BY peer
/// group ends: rows of the group are stored in an ephemeral table, the aggregate is stepped for
/// every peer, then the delayed rows are flushed with one shared Finalize. RANGE CURRENT ROW
/// resets the accumulator after that flush. Moving frames step, finalize, emit, and then apply
/// <c>AggInverse</c> to the departing row, so their aggregates must supply
/// <see cref="VdbeAggregate.Inverse"/>. A bounded <c>ROWS n PRECEDING</c> frame retains n argument
/// tuples and suppresses inverse for the first n rows of each partition.
/// </para>
/// <code>
///   0            OpenReadCursor
///   1            OpenSorter                                  (comparer orders by partition then order keys)
///   2            Rewind        -> sortAddr                   (empty table)
///   loopStart    [Filter       -> nextIngest]               (WHERE)
///                Column c0.i -> r[i]                         (materialize full row: i in 0..W-1)
///                SorterInsert  r[0..W-1]
///   nextIngest   Next          -> loopStart
///                CloseCursor
///   sortAddr     SorterSort    -> doneAddr                   (empty sorter: no rows)
///   prime        SorterData    -> r[0..W-1]
///                [Copy partition keys -> savedKey]           (when PARTITION BY present)
///                AggReset (per window)
///                Goto          -> emit
///   drainLoop    SorterData    -> r[0..W-1]
///                [Copy partition keys -> currentKey
///                 SameGroup currentKey==savedKey -> emit     (same partition: keep accumulating)
///                 AggReset (per window)                      (new partition: restart)
///                 Copy currentKey -> savedKey]
///   emit         [Copy args] AggStep; AggFinalize -> aggOut  (per window)
///                Copy/LoadConstant per output register
///                ResultRow
///                [JumpIf first n rows -> save arguments]    (bounded preceding frames)
///                [AggInverse oldest departing arguments]    (moving frames only)
///                [Shift departing ring; copy current args] (bounded preceding frames)
///                SorterNext    -> drainLoop
///   doneAddr     CloseSorter
///                Halt
/// </code>
/// </remarks>
public static class WindowProgramBuilder
{
    public static VdbeProgram Build(
        string tableName,
        int tableColumnCount,
        IReadOnlyList<int> partitionColumns,
        IReadOnlyList<AggregateFunctionSpec> windows,
        IReadOnlyList<WindowOutput> outputs,
        VdbeRowComparer orderComparer,
        VdbeGroupComparer? partitionComparer = null,
        VdbeRowPredicate? predicate = null,
        WindowFrameSpec? frame = null,
        IReadOnlyList<int>? orderColumns = null,
        VdbeGroupComparer? peerComparer = null,
        bool descendingOrder = false)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(partitionColumns);
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(orderComparer);

        var effectiveFrame = frame ?? WindowFrameSpec.Running;
        if (!effectiveFrame.IsSupported)
        {
            throw new ArgumentException(
                "WindowProgramBuilder only models ROWS UNBOUNDED PRECEDING TO CURRENT ROW, " +
                "ROWS CURRENT ROW TO CURRENT ROW, ROWS n PRECEDING TO CURRENT ROW, " +
                "ROWS CURRENT ROW TO m FOLLOWING, ROWS n PRECEDING TO m FOLLOWING, " +
                "RANGE/GROUPS UNBOUNDED PRECEDING or CURRENT ROW peer frames, " +
                "GROUPS n PRECEDING TO CURRENT ROW, RANGE n PRECEDING TO CURRENT ROW, " +
                "GROUPS CURRENT ROW TO m FOLLOWING, RANGE CURRENT ROW TO n FOLLOWING, " +
                "RANGE/GROUPS CURRENT ROW or UNBOUNDED PRECEDING TO UNBOUNDED FOLLOWING, " +
                "ROWS unbounded FOLLOWING, and ROWS running EXCLUDE CURRENT ROW " +
                $"(1 <= n,m <= {WindowFrameSpec.MaxStreamingPreceding}); " +
                $"frame ({effectiveFrame.Mode}, {effectiveFrame.Start}, {effectiveFrame.End}, " +
                $"{effectiveFrame.StartOffset}, {effectiveFrame.EndOffset}) is not representable.",
                nameof(frame));
        }

        if (tableColumnCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(tableColumnCount), "A window scan needs at least one column.");
        if (windows.Count == 0)
            throw new ArgumentException("A window scan must declare at least one window function.", nameof(windows));
        if (outputs.Count == 0)
            throw new ArgumentException("A window scan must project at least one output column.", nameof(outputs));

        foreach (var spec in windows)
        {
            if (spec is null)
                throw new ArgumentException("Window function specifications must not be null.", nameof(windows));
            if (spec.Aggregate is null)
                throw new ArgumentException("Window function specifications must supply an aggregate.", nameof(windows));
            ArgumentNullException.ThrowIfNull(spec.ArgumentColumns);
            if (effectiveFrame.RequiresInverse && spec.Aggregate.Inverse is null)
            {
                throw new ArgumentException(
                    "Moving ROWS/GROUPS/RANGE frames require every window aggregate to supply an inverse delegate.",
                    nameof(windows));
            }

            foreach (var column in spec.ArgumentColumns)
            {
                if (column < 0 || column >= tableColumnCount)
                {
                    throw new ArgumentException(
                        $"Window argument column {column} is outside the {tableColumnCount}-column table.",
                        nameof(windows));
                }
            }
        }

        foreach (var column in partitionColumns)
        {
            if (column < 0 || column >= tableColumnCount)
            {
                throw new ArgumentException(
                    $"Partition column {column} is outside the {tableColumnCount}-column table.",
                    nameof(partitionColumns));
            }
        }

        if (partitionColumns.Count > 0 && partitionComparer is null)
        {
            throw new ArgumentException(
                "A partitioned window needs a partition comparer to detect partition boundaries.",
                nameof(partitionComparer));
        }

        foreach (var output in outputs)
            ValidateOutput(output, tableColumnCount, windows.Count);

        orderColumns ??= [];
        if (effectiveFrame.IsPeerFrame)
        {
            foreach (var column in orderColumns)
            {
                if (column < 0 || column >= tableColumnCount)
                {
                    throw new ArgumentException(
                        $"Order column {column} is outside the {tableColumnCount}-column table.",
                        nameof(orderColumns));
                }
            }

            if (orderColumns.Count > 0 && peerComparer is null)
            {
                throw new ArgumentException(
                    "A RANGE/GROUPS peer frame with ORDER BY columns needs a peer comparer.",
                    nameof(peerComparer));
            }

            if ((effectiveFrame.IsRangePreceding || effectiveFrame.IsRangeFollowing)
                && orderColumns.Count != 1)
            {
                throw new ArgumentException(
                    "RANGE n PRECEDING/FOLLOWING requires exactly one ORDER BY column.",
                    nameof(orderColumns));
            }
        }

        return BuildProgram(
            tableName,
            tableColumnCount,
            partitionColumns,
            windows,
            outputs,
            orderComparer,
            partitionComparer,
            predicate,
            effectiveFrame,
            orderColumns,
            peerComparer,
            descendingOrder);
    }

    private static VdbeProgram BuildProgram(
        string tableName,
        int tableColumnCount,
        IReadOnlyList<int> partitionColumns,
        IReadOnlyList<AggregateFunctionSpec> windows,
        IReadOnlyList<WindowOutput> outputs,
        VdbeRowComparer orderComparer,
        VdbeGroupComparer? partitionComparer,
        VdbeRowPredicate? predicate,
        WindowFrameSpec frame,
        IReadOnlyList<int> orderColumns,
        VdbeGroupComparer? peerComparer,
        bool descendingOrder)
    {
        var width = tableColumnCount;
        var partition = partitionColumns.Count;
        var peer = frame.IsPeerFrame ? orderColumns.Count : 0;
        var argOffsets = ComputeArgOffsets(windows, out var totalArgs);

        // Register layout mirrors the grouped-aggregate builder: the full sorted row stages at r[0..W-1],
        // followed by partition keys, current argument blocks, the optional departing-argument ring and
        // skip-inverse counter (plus a constant 1 for n>1 decrement), finalized window values, and the
        // projected output block.
        var precedingCount = frame.IsPeerFrame ? 0 : frame.PrecedingCount;
        var followingCount = frame.IsPeerFrame ? 0 : frame.FollowingCount;
        var groupsPreceding = frame.IsGroupsPreceding ? frame.GroupsPrecedingCount : 0;
        var rangePreceding = frame.IsRangePreceding ? frame.RangePrecedingOffset : 0;
        var rangeFollowing = frame.IsRangeFollowing ? frame.RangeFollowingOffset : 0;
        var groupsFollowing = frame.IsGroupsFollowing ? frame.GroupsFollowingCount : 0;
        var unboundedFollowing = frame.IsUnboundedFollowing;
        var fullPartition = frame.IsFullPartition;
        var stagingBase = 0;
        var savedKeyBase = width;
        var currentKeyBase = width + partition;
        var savedPeerBase = width + (2 * partition);
        var currentPeerBase = savedPeerBase + peer;
        var argBase = currentPeerBase + peer;
        var departingArgBase = argBase + totalArgs;
        var delayBase = departingArgBase + (precedingCount * totalArgs);
        var delayCountIndex = delayBase + (followingCount * width);
        var followingOneIndex = delayCountIndex + 1;
        var followingConstIndex = delayCountIndex + 2;
        var followingTmpIndex = delayCountIndex + 3;
        var followingControl = followingCount == 0 ? 0 : 5;
        var skipInverseIndex = delayCountIndex + followingControl;
        var oneIndex = followingCount == 0 ? skipInverseIndex + 1 : followingOneIndex;
        var precedingControl = precedingCount == 0 ? 0 : precedingCount == 1 ? 1 : 2;
        var groupsControl = groupsPreceding == 0 && groupsFollowing == 0
            ? 0
            : groupsFollowing == 0 ? 2 : 4;
        var rangeControl = rangePreceding == 0 && rangeFollowing == 0 ? 0 : 1;
        var aggOutBase = skipInverseIndex + precedingControl + groupsControl + rangeControl;
        var outBase = aggOutBase + windows.Count;
        var registerCount = outBase + outputs.Count;

        var cursor = new Cursor(0);
        var sorter = new Sorter(0);
        var stagingRange = new RegisterRange(new Register(stagingBase), width);
        var savedKeyRange = new RegisterRange(new Register(savedKeyBase), partition);
        var currentKeyRange = new RegisterRange(new Register(currentKeyBase), partition);

        var ins = new List<VdbeInstruction>
        {
            new OpenReadCursorInstruction(cursor, tableName, width),
            new OpenSorterInstruction(sorter, orderComparer, width),
        };

        var rewindIndex = ins.Count;
        ins.Add(new RewindCursorInstruction(cursor, new ProgramCounter(0)));

        var loopStart = ins.Count;
        var filterIndex = -1;
        if (predicate is not null)
        {
            filterIndex = ins.Count;
            ins.Add(new FilterInstruction(cursor, predicate, new ProgramCounter(0), string.Empty));
        }

        for (var column = 0; column < width; column++)
            ins.Add(new ColumnInstruction(cursor, column, new Register(stagingBase + column)));

        ins.Add(new SorterInsertInstruction(sorter, stagingRange));

        var nextIngestAddr = ins.Count;
        ins.Add(new NextInstruction(cursor, new ProgramCounter(loopStart)));
        ins.Add(new CloseCursorInstruction(cursor));

        var sortIndex = ins.Count;
        ins.Add(new SorterSortInstruction(sorter, new ProgramCounter(0)));

        // Backpatch the ingest-phase jumps now that their targets are known.
        ins[rewindIndex] = new RewindCursorInstruction(cursor, new ProgramCounter(sortIndex));
        if (filterIndex >= 0)
        {
            ins[filterIndex] = new FilterInstruction(
                cursor,
                predicate!,
                new ProgramCounter(nextIngestAddr),
                $"skip row when WHERE is false, goto {nextIngestAddr}");
        }

        if (frame.IsPeerFrame)
        {
            return CompletePeerFrame(
                ins,
                rewindIndex,
                sortIndex,
                sorter,
                stagingRange,
                stagingBase,
                width,
                partitionColumns,
                savedKeyBase,
                currentKeyBase,
                partitionComparer,
                orderColumns,
                savedPeerBase,
                currentPeerBase,
                peerComparer,
                windows,
                argOffsets,
                argBase,
                aggOutBase,
                outBase,
                outputs,
                resetAccumulatorOnPeerChange: frame.IsPeerCurrent,
                groupsPreceding,
                skipInverseIndex,
                oneIndex,
                rangePreceding,
                descendingOrder,
                groupsFollowing,
                rangeFollowing,
                unboundedFollowing,
                fullPartition,
                frame.Exclusion);
        }

        var rowsQueue = frame.IsRowsUnboundedFollowing || frame.IsRowsFullPartition;
        var delay = new Cursor(1);
        if (rowsQueue)
        {
            ins.Insert(sortIndex, new OpenEphemeralInstruction(delay, width));
            sortIndex++;
            ins[rewindIndex] = new RewindCursorInstruction(cursor, new ProgramCounter(sortIndex));
        }

        // Prime the first partition from the first sorted row, then jump into the shared emit block so the
        // first row also produces a result.
        ins.Add(new SorterDataInstruction(sorter, stagingRange));
        for (var j = 0; j < partition; j++)
            ins.Add(new CopyInstruction(new Register(stagingBase + partitionColumns[j]), new Register(savedKeyBase + j)));

        for (var i = 0; i < windows.Count; i++)
            ins.Add(new AggResetInstruction(new Accumulator(i)));
        EmitPrecedingFrameReset(ins, frame, skipInverseIndex, oneIndex);
        EmitFollowingFrameReset(ins, frame, delayCountIndex, followingConstIndex, followingOneIndex);

        var primeGotoIndex = ins.Count;
        ins.Add(new GotoInstruction(new ProgramCounter(0)));

        var drainLoop = ins.Count;
        ins.Add(new SorterDataInstruction(sorter, stagingRange));
        if (frame.IsOnePreceding && followingCount == 0)
            ins.Add(new LoadConstantInstruction(new Register(skipInverseIndex), SqlValue.Integer(0)));

        var sameGroupIndex = -1;
        if (partition > 0)
        {
            for (var j = 0; j < partition; j++)
                ins.Add(new CopyInstruction(new Register(stagingBase + partitionColumns[j]), new Register(currentKeyBase + j)));

            sameGroupIndex = ins.Count;
            ins.Add(new SameGroupInstruction(currentKeyRange, savedKeyRange, partitionComparer!, new ProgramCounter(0)));

            if (rowsQueue)
            {
                EmitRowsQueueDrain(
                    ins,
                    delay,
                    windows,
                    outputs,
                    argOffsets,
                    argBase,
                    width,
                    aggOutBase,
                    outBase,
                    outBase + outputs.Count,
                    inverse: frame.IsRowsUnboundedFollowing);
            }

            // New partition boundary: restart the accumulators and adopt the new key, then fall into emit.
            for (var i = 0; i < windows.Count; i++)
                ins.Add(new AggResetInstruction(new Accumulator(i)));
            for (var j = 0; j < partition; j++)
                ins.Add(new CopyInstruction(new Register(currentKeyBase + j), new Register(savedKeyBase + j)));
            if (followingCount > 0)
            {
                EmitFollowingDrain(
                    ins,
                    windows,
                    outputs,
                    argOffsets,
                    delayBase,
                    width,
                    followingCount,
                    delayCountIndex,
                    followingOneIndex,
                    argBase,
                    aggOutBase,
                    outBase,
                    precedingCount,
                    departingArgBase,
                    skipInverseIndex,
                    totalArgs);
            }

            EmitPrecedingFrameReset(ins, frame, skipInverseIndex, oneIndex);
            EmitFollowingFrameReset(ins, frame, delayCountIndex, followingConstIndex, followingOneIndex);
        }

        var emit = ins.Count;
        if (rowsQueue)
        {
            EmitWindowSteps(ins, windows, argOffsets, argBase, stagingBase);
            ins.Add(new EphemeralInsertInstruction(delay, stagingRange));
        }
        else if (frame.ExcludesCurrentRow && frame.IsRunning)
        {
            EmitCurrentRowResult(ins, windows, outputs, stagingBase, aggOutBase, outBase);
            EmitWindowSteps(ins, windows, argOffsets, argBase, stagingBase);
        }
        else if (frame.ExcludesCurrentRow && frame.IsCurrentRow)
        {
            EmitCurrentRowResult(ins, windows, outputs, stagingBase, aggOutBase, outBase);
        }
        else
        {
        EmitWindowSteps(ins, windows, argOffsets, argBase, stagingBase);
        if (followingCount > 0)
        {
            EmitFollowingStep(
                ins,
                windows,
                outputs,
                argOffsets,
                stagingBase,
                delayBase,
                width,
                followingCount,
                delayCountIndex,
                followingConstIndex,
                followingTmpIndex,
                followingOneIndex,
                argBase,
                aggOutBase,
                outBase,
                precedingCount,
                departingArgBase,
                skipInverseIndex,
                totalArgs);
        }
        else
        {
            EmitCurrentRowResult(
                ins,
                windows,
                outputs,
                stagingBase,
                aggOutBase,
                outBase);
            if (frame.IsCurrentRow)
            {
                EmitWindowInverses(ins, windows, argOffsets, argBase);
            }
            else if (frame.IsOnePreceding)
            {
                var skipInverseInstruction = ins.Count;
                ins.Add(new JumpIfInstruction(new Register(skipInverseIndex), new ProgramCounter(0)));
                EmitWindowInverses(ins, windows, argOffsets, departingArgBase);

                var saveDepartingArguments = ins.Count;
                for (var i = 0; i < totalArgs; i++)
                    ins.Add(new CopyInstruction(new Register(argBase + i), new Register(departingArgBase + i)));

                ins[skipInverseInstruction] = new JumpIfInstruction(
                    new Register(skipInverseIndex),
                    new ProgramCounter(saveDepartingArguments));
            }
            else if (frame.IsBoundedPreceding)
            {
                EmitBoundedPrecedingInverse(
                    ins,
                    windows,
                    argOffsets,
                    argBase,
                    departingArgBase,
                    skipInverseIndex,
                    totalArgs,
                    precedingCount);
            }
        }
        }

        ins.Add(new SorterNextInstruction(sorter, new ProgramCounter(drainLoop)));

        if (rowsQueue)
        {
            EmitRowsQueueDrain(
                ins,
                delay,
                windows,
                outputs,
                argOffsets,
                argBase,
                width,
                aggOutBase,
                outBase,
                outBase + outputs.Count,
                inverse: frame.IsRowsUnboundedFollowing);
            ins.Add(new CloseCursorInstruction(delay));
        }
        else if (followingCount > 0)
        {
            EmitFollowingDrain(
                ins,
                windows,
                outputs,
                argOffsets,
                delayBase,
                width,
                followingCount,
                delayCountIndex,
                followingOneIndex,
                argBase,
                aggOutBase,
                outBase,
                precedingCount,
                departingArgBase,
                skipInverseIndex,
                totalArgs);
        }

        var doneAddr = ins.Count;
        ins.Add(new CloseSorterInstruction(sorter));
        ins.Add(new HaltInstruction());

        // Backpatch the forward jumps of the drain phase.
        ins[sortIndex] = new SorterSortInstruction(sorter, new ProgramCounter(doneAddr));
        ins[primeGotoIndex] = new GotoInstruction(new ProgramCounter(emit));
        if (sameGroupIndex >= 0)
        {
            ins[sameGroupIndex] = new SameGroupInstruction(
                currentKeyRange,
                savedKeyRange,
                partitionComparer!,
                new ProgramCounter(emit));
        }

        return new VdbeProgram(
            rowsQueue ? outBase + outputs.Count + width : registerCount,
            cursorCount: rowsQueue ? 2 : 1,
            ins,
            sorterCount: 1,
            accumulatorCount: windows.Count);
    }

    private static void EmitRowsQueueDrain(
        List<VdbeInstruction> ins,
        Cursor queue,
        IReadOnlyList<AggregateFunctionSpec> windows,
        IReadOnlyList<WindowOutput> outputs,
        int[] argOffsets,
        int argBase,
        int width,
        int aggOutBase,
        int outBase,
        int flushBase,
        bool inverse)
    {
        if (!inverse)
        {
            EmitPeerFlush(ins, queue, windows, outputs, width, aggOutBase, outBase, flushBase);
            return;
        }

        var rewind = ins.Count;
        ins.Add(new RewindCursorInstruction(queue, new ProgramCounter(0)));
        var loop = ins.Count;
        if (width > 0)
            ins.Add(new ColumnRangeInstruction(queue, 0, new Register(flushBase), width));
        for (var i = 0; i < windows.Count; i++)
        {
            ins.Add(new AggFinalizeInstruction(
                new Accumulator(i),
                windows[i].Aggregate,
                new Register(aggOutBase + i)));
        }

        for (var o = 0; o < outputs.Count; o++)
        {
            var output = outputs[o];
            var destination = new Register(outBase + o);
            ins.Add(output.Kind switch
            {
                WindowOutputKind.Column => new CopyInstruction(
                    new Register(flushBase + output.Index),
                    destination),
                WindowOutputKind.Window => new CopyInstruction(
                    new Register(aggOutBase + output.Index),
                    destination),
                _ => new LoadConstantInstruction(destination, output.Constant),
            });
        }

        ins.Add(new ResultRowInstruction(new RegisterRange(new Register(outBase), outputs.Count)));
        GatherArgumentsFromRow(ins, windows, argOffsets, argBase, flushBase);
        EmitWindowInverses(ins, windows, argOffsets, argBase);
        ins.Add(new NextInstruction(queue, new ProgramCounter(loop)));
        ins[rewind] = new RewindCursorInstruction(queue, new ProgramCounter(ins.Count));
        ins.Add(new CloseCursorInstruction(queue));
        ins.Add(new OpenEphemeralInstruction(queue, width));
    }

    private static void EmitCurrentRowResult(
        List<VdbeInstruction> ins,
        IReadOnlyList<AggregateFunctionSpec> windows,
        IReadOnlyList<WindowOutput> outputs,
        int rowBase,
        int aggOutBase,
        int outBase)
    {
        for (var i = 0; i < windows.Count; i++)
        {
            ins.Add(new AggFinalizeInstruction(
                new Accumulator(i),
                windows[i].Aggregate,
                new Register(aggOutBase + i)));
        }

        for (var o = 0; o < outputs.Count; o++)
        {
            var output = outputs[o];
            var destination = new Register(outBase + o);
            ins.Add(output.Kind switch
            {
                WindowOutputKind.Column => new CopyInstruction(new Register(rowBase + output.Index), destination),
                WindowOutputKind.Window => new CopyInstruction(new Register(aggOutBase + output.Index), destination),
                _ => new LoadConstantInstruction(destination, output.Constant),
            });
        }

        ins.Add(new ResultRowInstruction(new RegisterRange(new Register(outBase), outputs.Count)));
    }

    private static void EmitFollowingFrameReset(
        List<VdbeInstruction> ins,
        WindowFrameSpec frame,
        int delayCountIndex,
        int followingConstIndex,
        int followingOneIndex)
    {
        if (frame.FollowingCount == 0)
            return;

        ins.Add(new LoadConstantInstruction(new Register(delayCountIndex), SqlValue.Integer(0)));
        ins.Add(new LoadConstantInstruction(new Register(followingConstIndex), SqlValue.Integer(frame.FollowingCount)));
        ins.Add(new LoadConstantInstruction(new Register(followingOneIndex), SqlValue.Integer(1)));
    }

    private static void EmitFollowingStep(
        List<VdbeInstruction> ins,
        IReadOnlyList<AggregateFunctionSpec> windows,
        IReadOnlyList<WindowOutput> outputs,
        int[] argOffsets,
        int stagingBase,
        int delayBase,
        int width,
        int followingCount,
        int delayCountIndex,
        int followingConstIndex,
        int followingTmpIndex,
        int followingOneIndex,
        int argBase,
        int aggOutBase,
        int outBase,
        int precedingCount,
        int departingArgBase,
        int skipInverseIndex,
        int totalArgs)
    {
        // tmp = followingConst - delayCount; nonzero means the delay ring is not yet full.
        ins.Add(new CopyInstruction(new Register(followingConstIndex), new Register(followingTmpIndex)));
        ins.Add(new CopyInstruction(new Register(delayCountIndex), new Register(followingTmpIndex + 1)));
        ins.Add(new ArithmeticInstruction(
            new Register(followingTmpIndex),
            ArithmeticOperator.Subtract,
            new RegisterRange(new Register(followingTmpIndex), 2)));
        var notFullJump = ins.Count;
        ins.Add(new JumpIfInstruction(new Register(followingTmpIndex), new ProgramCounter(0)));

        EmitFollowingEmitOldest(
            ins,
            windows,
            outputs,
            argOffsets,
            delayBase,
            width,
            followingCount,
            argBase,
            aggOutBase,
            outBase,
            precedingCount,
            departingArgBase,
            skipInverseIndex,
            totalArgs);
        EmitShiftRowRing(ins, delayBase, width, followingCount);
        for (var column = 0; column < width; column++)
        {
            ins.Add(new CopyInstruction(
                new Register(stagingBase + column),
                new Register(delayBase + ((followingCount - 1) * width) + column)));
        }

        var afterEnqueue = ins.Count;
        ins.Add(new GotoInstruction(new ProgramCounter(0)));

        var notFull = ins.Count;
        ins[notFullJump] = new JumpIfInstruction(
            new Register(followingTmpIndex),
            new ProgramCounter(notFull));
        EmitFollowingEnqueue(ins, stagingBase, delayBase, width, followingCount, delayCountIndex, followingOneIndex);
        ins[afterEnqueue] = new GotoInstruction(new ProgramCounter(ins.Count));
    }

    private static void EmitFollowingEnqueue(
        List<VdbeInstruction> ins,
        int stagingBase,
        int delayBase,
        int width,
        int followingCount,
        int delayCountIndex,
        int followingOneIndex)
    {
        var doneJumps = new int[followingCount];
        var tmp = followingOneIndex + 2;
        for (var slot = 0; slot < followingCount; slot++)
        {
            if (slot == 0)
            {
                var skip = ins.Count;
                ins.Add(new JumpIfInstruction(new Register(delayCountIndex), new ProgramCounter(0)));
                EmitCopyRow(ins, stagingBase, delayBase, width);
                ins.Add(new ArithmeticInstruction(
                    new Register(delayCountIndex),
                    ArithmeticOperator.Add,
                    new RegisterRange(new Register(delayCountIndex), 2)));
                doneJumps[slot] = ins.Count;
                ins.Add(new GotoInstruction(new ProgramCounter(0)));
                ins[skip] = new JumpIfInstruction(new Register(delayCountIndex), new ProgramCounter(ins.Count));
                continue;
            }

            ins.Add(new CopyInstruction(new Register(delayCountIndex), new Register(tmp)));
            ins.Add(new LoadConstantInstruction(new Register(tmp + 1), SqlValue.Integer(slot)));
            ins.Add(new ArithmeticInstruction(
                new Register(tmp),
                ArithmeticOperator.Subtract,
                new RegisterRange(new Register(tmp), 2)));
            var skipSlot = ins.Count;
            ins.Add(new JumpIfInstruction(new Register(tmp), new ProgramCounter(0)));
            EmitCopyRow(ins, stagingBase, delayBase + (slot * width), width);
            ins.Add(new ArithmeticInstruction(
                new Register(delayCountIndex),
                ArithmeticOperator.Add,
                new RegisterRange(new Register(delayCountIndex), 2)));
            doneJumps[slot] = ins.Count;
            ins.Add(new GotoInstruction(new ProgramCounter(0)));
            ins[skipSlot] = new JumpIfInstruction(new Register(tmp), new ProgramCounter(ins.Count));
        }

        var after = ins.Count;
        foreach (var done in doneJumps)
            ins[done] = new GotoInstruction(new ProgramCounter(after));
    }

    private static void EmitCopyRow(List<VdbeInstruction> ins, int sourceBase, int destinationBase, int width)
    {
        for (var column = 0; column < width; column++)
        {
            ins.Add(new CopyInstruction(
                new Register(sourceBase + column),
                new Register(destinationBase + column)));
        }
    }

    private static void EmitFollowingDrain(
        List<VdbeInstruction> ins,
        IReadOnlyList<AggregateFunctionSpec> windows,
        IReadOnlyList<WindowOutput> outputs,
        int[] argOffsets,
        int delayBase,
        int width,
        int followingCount,
        int delayCountIndex,
        int followingOneIndex,
        int argBase,
        int aggOutBase,
        int outBase,
        int precedingCount,
        int departingArgBase,
        int skipInverseIndex,
        int totalArgs)
    {
        var drainTop = ins.Count;
        ins.Add(new JumpIfInstruction(new Register(delayCountIndex), new ProgramCounter(0)));
        var skipDrain = ins.Count;
        ins.Add(new GotoInstruction(new ProgramCounter(0)));
        var drainBody = ins.Count;
        ins[drainTop] = new JumpIfInstruction(new Register(delayCountIndex), new ProgramCounter(drainBody));
        EmitFollowingEmitOldest(
            ins,
            windows,
            outputs,
            argOffsets,
            delayBase,
            width,
            followingCount,
            argBase,
            aggOutBase,
            outBase,
            precedingCount,
            departingArgBase,
            skipInverseIndex,
            totalArgs);
        EmitShiftRowRing(ins, delayBase, width, followingCount);
        ins.Add(new ArithmeticInstruction(
            new Register(delayCountIndex),
            ArithmeticOperator.Subtract,
            new RegisterRange(new Register(delayCountIndex), 2)));
        ins.Add(new GotoInstruction(new ProgramCounter(drainTop)));
        ins[skipDrain] = new GotoInstruction(new ProgramCounter(ins.Count));
    }

    private static void EmitFollowingEmitOldest(
        List<VdbeInstruction> ins,
        IReadOnlyList<AggregateFunctionSpec> windows,
        IReadOnlyList<WindowOutput> outputs,
        int[] argOffsets,
        int delayBase,
        int width,
        int followingCount,
        int argBase,
        int aggOutBase,
        int outBase,
        int precedingCount,
        int departingArgBase,
        int skipInverseIndex,
        int totalArgs)
    {
        _ = width;
        _ = followingCount;
        EmitCurrentRowResult(ins, windows, outputs, delayBase, aggOutBase, outBase);
        GatherArgumentsFromRow(ins, windows, argOffsets, argBase, delayBase);
        if (precedingCount == 0)
        {
            EmitWindowInverses(ins, windows, argOffsets, argBase);
            return;
        }

        if (precedingCount == 1)
        {
            var skipInverseInstruction = ins.Count;
            ins.Add(new JumpIfInstruction(new Register(skipInverseIndex), new ProgramCounter(0)));
            EmitWindowInverses(ins, windows, argOffsets, departingArgBase);
            var saveDepartingArguments = ins.Count;
            for (var i = 0; i < totalArgs; i++)
                ins.Add(new CopyInstruction(new Register(argBase + i), new Register(departingArgBase + i)));
            ins[skipInverseInstruction] = new JumpIfInstruction(
                new Register(skipInverseIndex),
                new ProgramCounter(saveDepartingArguments));
            ins.Add(new LoadConstantInstruction(new Register(skipInverseIndex), SqlValue.Integer(0)));
            return;
        }

        EmitBoundedPrecedingInverse(
            ins,
            windows,
            argOffsets,
            argBase,
            departingArgBase,
            skipInverseIndex,
            totalArgs,
            precedingCount);
    }

    private static void EmitShiftRowRing(List<VdbeInstruction> ins, int delayBase, int width, int followingCount)
    {
        for (var slot = 0; slot < followingCount - 1; slot++)
        {
            for (var column = 0; column < width; column++)
            {
                ins.Add(new CopyInstruction(
                    new Register(delayBase + ((slot + 1) * width) + column),
                    new Register(delayBase + (slot * width) + column)));
            }
        }
    }

    private static void GatherArgumentsFromRow(
        List<VdbeInstruction> ins,
        IReadOnlyList<AggregateFunctionSpec> windows,
        int[] argOffsets,
        int argBase,
        int rowBase)
    {
        for (var i = 0; i < windows.Count; i++)
        {
            var spec = windows[i];
            for (var k = 0; k < spec.Arity; k++)
            {
                ins.Add(new CopyInstruction(
                    new Register(rowBase + spec.ArgumentColumns[k]),
                    new Register(argBase + argOffsets[i] + k)));
            }
        }
    }

    private static VdbeProgram CompletePeerFrame(
        List<VdbeInstruction> ins,
        int rewindIndex,
        int sortIndex,
        Sorter sorter,
        RegisterRange stagingRange,
        int stagingBase,
        int width,
        IReadOnlyList<int> partitionColumns,
        int savedKeyBase,
        int currentKeyBase,
        VdbeGroupComparer? partitionComparer,
        IReadOnlyList<int> orderColumns,
        int savedPeerBase,
        int currentPeerBase,
        VdbeGroupComparer? peerComparer,
        IReadOnlyList<AggregateFunctionSpec> windows,
        int[] argOffsets,
        int argBase,
        int aggOutBase,
        int outBase,
        IReadOnlyList<WindowOutput> outputs,
        bool resetAccumulatorOnPeerChange,
        int groupsPreceding,
        int skipInverseIndex,
        int oneIndex,
        int rangePreceding,
        bool descendingOrder,
        int groupsFollowing,
        int rangeFollowing,
        bool unboundedFollowing,
        bool fullPartition,
        WindowExclusion exclusion)
    {
        var cursor = new Cursor(0);
        var partition = partitionColumns.Count;
        var peer = orderColumns.Count;
        var savedKeyRange = new RegisterRange(new Register(savedKeyBase), partition);
        var currentKeyRange = new RegisterRange(new Register(currentKeyBase), partition);
        var savedPeerRange = new RegisterRange(new Register(savedPeerBase), peer);
        var currentPeerRange = new RegisterRange(new Register(currentPeerBase), peer);
        var flushBase = outBase + outputs.Count;
        var ringSlots = groupsPreceding == 0
            ? groupsFollowing == 0 ? 0 : groupsFollowing + 1
            : groupsPreceding + 1;
        var rangeOffset = rangePreceding == 0 ? rangeFollowing : rangePreceding;
        var usesQueue = rangeOffset > 0 || unboundedFollowing || fullPartition;
        var excludeScratchNeeded = exclusion != WindowExclusion.NoOthers
            && !usesQueue
            && groupsPreceding == 0
            && groupsFollowing == 0;
        var rangeCursors = usesQueue ? 2 : excludeScratchNeeded ? 1 : 0;
        var history = new Cursor(1);
        var scratch = new Cursor(usesQueue ? 2 : 1);
        var orderColumn = orderColumns.Count == 0 ? 0 : orderColumns[0];
        var rowReg = flushBase + width;
        var boundReg = rowReg + 1;
        var flagReg = rowReg + 2;
        var oldestKeyReg = flagReg + 1;

        // Open the delay buffer before SorterSort so a fully-filtered ingest still has a cursor
        // to close at Halt (CloseCursor is not idempotent). Empty-table Rewind must skip this
        // open — the table cursor is still live — and land on SorterSort instead.
        ins.Insert(sortIndex, new OpenEphemeralInstruction(cursor, width));
        sortIndex++;
        if (usesQueue)
        {
            ins.Insert(sortIndex, new OpenEphemeralInstruction(history, width));
            sortIndex++;
            ins.Insert(sortIndex, new OpenEphemeralInstruction(scratch, width));
            sortIndex++;
        }
        else if (excludeScratchNeeded)
        {
            ins.Insert(sortIndex, new OpenEphemeralInstruction(scratch, width));
            sortIndex++;
        }

        ins[rewindIndex] = new RewindCursorInstruction(cursor, new ProgramCounter(sortIndex));

        ins.Add(new SorterDataInstruction(sorter, stagingRange));
        for (var j = 0; j < partition; j++)
        {
            ins.Add(new CopyInstruction(
                new Register(stagingBase + partitionColumns[j]),
                new Register(savedKeyBase + j)));
        }

        for (var j = 0; j < peer; j++)
        {
            ins.Add(new CopyInstruction(
                new Register(stagingBase + orderColumns[j]),
                new Register(savedPeerBase + j)));
        }

        for (var i = 0; i < windows.Count; i++)
            ins.Add(new AggResetInstruction(new Accumulator(i)));
        EmitGroupsPrecedingReset(
            ins, ringSlots, width, skipInverseIndex, oneIndex, groupsPreceding, openRing: true);
        EmitGroupsFollowingReset(
            ins, ringSlots, width, skipInverseIndex, oneIndex, groupsFollowing, openRing: true);
        EmitRangeHistoryReset(ins, width, skipInverseIndex, rangeOffset, openHistory: true, needsQueue: usesQueue);

        var primeGoto = ins.Count;
        ins.Add(new GotoInstruction(new ProgramCounter(0)));

        var drainLoop = ins.Count;
        ins.Add(new SorterDataInstruction(sorter, stagingRange));

        var accumulate = 0;
        var samePartition = -1;
        if (partition > 0)
        {
            for (var j = 0; j < partition; j++)
            {
                ins.Add(new CopyInstruction(
                    new Register(stagingBase + partitionColumns[j]),
                    new Register(currentKeyBase + j)));
            }

            samePartition = ins.Count;
            ins.Add(new SameGroupInstruction(
                currentKeyRange,
                savedKeyRange,
                partitionComparer!,
                new ProgramCounter(0)));
            if (rangeFollowing > 0 || unboundedFollowing || fullPartition)
            {
                EmitRangeFollowingBoundary(
                    ins,
                    cursor,
                    history,
                    scratch,
                    windows,
                    outputs,
                    argOffsets,
                    argBase,
                    width,
                    aggOutBase,
                    outBase,
                    flushBase,
                    rangeFollowing,
                    descendingOrder,
                    orderColumn,
                    currentPeerBase,
                    skipInverseIndex,
                    rowReg,
                    boundReg,
                    flagReg,
                    oldestKeyReg,
                    peerComparer,
                    drainRemaining: true,
                    fullPartition);
            }
            else
            {
                EmitRangePeerBoundary(
                    ins,
                    cursor,
                    history,
                    scratch,
                    windows,
                    outputs,
                    argOffsets,
                    argBase,
                    width,
                    aggOutBase,
                    outBase,
                    flushBase,
                    rangePreceding,
                    descendingOrder,
                    orderColumn,
                    savedPeerBase,
                    skipInverseIndex,
                    rowReg,
                    boundReg,
                    flagReg,
                    retainFlushedGroup: false);
            }

            for (var i = 0; i < windows.Count; i++)
                ins.Add(new AggResetInstruction(new Accumulator(i)));
            EmitGroupsPrecedingReset(
                ins, ringSlots, width, skipInverseIndex, oneIndex, groupsPreceding, openRing: false);
            EmitGroupsFollowingReset(
                ins, ringSlots, width, skipInverseIndex, oneIndex, groupsFollowing, openRing: false);
            EmitRangeHistoryReset(ins, width, skipInverseIndex, rangeOffset, openHistory: false, needsQueue: usesQueue);
            for (var j = 0; j < partition; j++)
            {
                ins.Add(new CopyInstruction(
                    new Register(currentKeyBase + j),
                    new Register(savedKeyBase + j)));
            }

            for (var j = 0; j < peer; j++)
            {
                ins.Add(new CopyInstruction(
                    new Register(stagingBase + orderColumns[j]),
                    new Register(savedPeerBase + j)));
            }

            var skipPeerCheck = ins.Count;
            ins.Add(new GotoInstruction(new ProgramCounter(0)));
            accumulate = 0;
            var afterPartition = ins.Count;
            ins[samePartition] = new SameGroupInstruction(
                currentKeyRange,
                savedKeyRange,
                partitionComparer!,
                new ProgramCounter(afterPartition));

            if (peer > 0)
            {
                for (var j = 0; j < peer; j++)
                {
                    ins.Add(new CopyInstruction(
                        new Register(stagingBase + orderColumns[j]),
                        new Register(currentPeerBase + j)));
                }

                var samePeer = ins.Count;
                ins.Add(new SameGroupInstruction(
                    currentPeerRange,
                    savedPeerRange,
                    peerComparer!,
                    new ProgramCounter(0)));
                EmitMovingPeerBoundary(
                    ins,
                    cursor,
                    history,
                    scratch,
                    windows,
                    outputs,
                    argOffsets,
                    argBase,
                    width,
                    aggOutBase,
                    outBase,
                    flushBase,
                    groupsPreceding,
                    skipInverseIndex,
                    rangePreceding,
                    descendingOrder,
                    orderColumn,
                    currentPeerBase,
                    rowReg,
                    boundReg,
                    flagReg,
                    groupsFollowing,
                    rangeFollowing,
                    oldestKeyReg,
                    peerComparer,
                    unboundedFollowing,
                    fullPartition,
                    exclusion);
                if (resetAccumulatorOnPeerChange)
                {
                    for (var i = 0; i < windows.Count; i++)
                        ins.Add(new AggResetInstruction(new Accumulator(i)));
                }

                for (var j = 0; j < peer; j++)
                {
                    ins.Add(new CopyInstruction(
                        new Register(currentPeerBase + j),
                        new Register(savedPeerBase + j)));
                }

                accumulate = ins.Count;
                ins[samePeer] = new SameGroupInstruction(
                    currentPeerRange,
                    savedPeerRange,
                    peerComparer!,
                    new ProgramCounter(accumulate));
            }
            else
            {
                accumulate = ins.Count;
            }

            ins[skipPeerCheck] = new GotoInstruction(new ProgramCounter(accumulate));
        }
        else if (peer > 0)
        {
            for (var j = 0; j < peer; j++)
            {
                ins.Add(new CopyInstruction(
                    new Register(stagingBase + orderColumns[j]),
                    new Register(currentPeerBase + j)));
            }

            var samePeer = ins.Count;
            ins.Add(new SameGroupInstruction(
                currentPeerRange,
                savedPeerRange,
                peerComparer!,
                new ProgramCounter(0)));
            EmitMovingPeerBoundary(
                ins,
                cursor,
                history,
                scratch,
                windows,
                outputs,
                argOffsets,
                argBase,
                width,
                aggOutBase,
                outBase,
                flushBase,
                groupsPreceding,
                skipInverseIndex,
                rangePreceding,
                descendingOrder,
                orderColumn,
                currentPeerBase,
                rowReg,
                boundReg,
                flagReg,
                groupsFollowing,
                rangeFollowing,
                oldestKeyReg,
                peerComparer,
                unboundedFollowing,
                fullPartition,
                exclusion);
            if (resetAccumulatorOnPeerChange)
            {
                for (var i = 0; i < windows.Count; i++)
                    ins.Add(new AggResetInstruction(new Accumulator(i)));
            }

            for (var j = 0; j < peer; j++)
            {
                ins.Add(new CopyInstruction(
                    new Register(currentPeerBase + j),
                    new Register(savedPeerBase + j)));
            }

            accumulate = ins.Count;
            ins[samePeer] = new SameGroupInstruction(
                currentPeerRange,
                savedPeerRange,
                peerComparer!,
                new ProgramCounter(accumulate));
        }
        else
        {
            accumulate = ins.Count;
        }

        ins[primeGoto] = new GotoInstruction(new ProgramCounter(accumulate));
        if (exclusion is not (WindowExclusion.Group or WindowExclusion.Ties))
            EmitWindowSteps(ins, windows, argOffsets, argBase, stagingBase);
        ins.Add(new EphemeralInsertInstruction(cursor, stagingRange));
        ins.Add(new SorterNextInstruction(sorter, new ProgramCounter(drainLoop)));

        if (rangeFollowing > 0 || unboundedFollowing || fullPartition)
        {
            EmitRangeFollowingBoundary(
                ins,
                cursor,
                history,
                scratch,
                windows,
                outputs,
                argOffsets,
                argBase,
                width,
                aggOutBase,
                outBase,
                flushBase,
                rangeFollowing,
                descendingOrder,
                orderColumn,
                currentPeerBase,
                skipInverseIndex,
                rowReg,
                boundReg,
                flagReg,
                oldestKeyReg,
                peerComparer,
                drainRemaining: true,
                fullPartition);
        }
        else if (groupsFollowing > 0)
        {
            EmitGroupsFollowingBoundary(
                ins,
                cursor,
                windows,
                outputs,
                argOffsets,
                argBase,
                width,
                aggOutBase,
                outBase,
                flushBase,
                groupsFollowing,
                skipInverseIndex,
                drainRemaining: true);
        }
        else if (excludeScratchNeeded)
        {
            EmitExclusionPeerBoundary(
                ins,
                cursor,
                windows,
                outputs,
                argOffsets,
                argBase,
                width,
                aggOutBase,
                outBase,
                flushBase,
                exclusion,
                drainRemaining: true);
        }
        else
        {
            EmitRangePeerBoundary(
                ins,
                cursor,
                history,
                scratch,
                windows,
                outputs,
                argOffsets,
                argBase,
                width,
                aggOutBase,
                outBase,
                flushBase,
                rangePreceding,
                descendingOrder,
                orderColumn,
                savedPeerBase,
                skipInverseIndex,
                rowReg,
                boundReg,
                flagReg,
                retainFlushedGroup: false);
        }

        for (var slot = 1; slot <= ringSlots + rangeCursors; slot++)
            ins.Add(new CloseCursorInstruction(new Cursor(slot)));

        var doneAddr = ins.Count;
        ins.Add(new CloseCursorInstruction(cursor));
        ins.Add(new CloseSorterInstruction(sorter));
        ins.Add(new HaltInstruction());
        ins[sortIndex] = new SorterSortInstruction(sorter, new ProgramCounter(doneAddr));

        return new VdbeProgram(
            usesQueue ? oldestKeyReg + 2 : rangePreceding == 0 ? flushBase + width : flagReg + 1,
            cursorCount: 1 + ringSlots + rangeCursors,
            ins,
            sorterCount: 1,
            accumulatorCount: windows.Count);
    }

    private static void EmitGroupsPrecedingReset(
        List<VdbeInstruction> ins,
        int ringSlots,
        int width,
        int skipInverseIndex,
        int oneIndex,
        int groupsPreceding,
        bool openRing)
    {
        if (groupsPreceding == 0)
            return;

        for (var slot = 1; slot <= ringSlots; slot++)
        {
            var ring = new Cursor(slot);
            if (!openRing)
                ins.Add(new CloseCursorInstruction(ring));
            ins.Add(new OpenEphemeralInstruction(ring, width));
        }

        ins.Add(new LoadConstantInstruction(new Register(skipInverseIndex), SqlValue.Integer(groupsPreceding)));
        ins.Add(new LoadConstantInstruction(new Register(oneIndex), SqlValue.Integer(1)));
    }

    private static void EmitGroupsFollowingReset(
        List<VdbeInstruction> ins,
        int ringSlots,
        int width,
        int skipInverseIndex,
        int oneIndex,
        int groupsFollowing,
        bool openRing)
    {
        if (groupsFollowing == 0)
            return;

        for (var slot = 1; slot <= ringSlots; slot++)
        {
            var ring = new Cursor(slot);
            if (!openRing)
                ins.Add(new CloseCursorInstruction(ring));
            ins.Add(new OpenEphemeralInstruction(ring, width));
        }

        ins.Add(new LoadConstantInstruction(new Register(skipInverseIndex), SqlValue.Integer(groupsFollowing)));
        ins.Add(new LoadConstantInstruction(new Register(oneIndex), SqlValue.Integer(1)));
    }

    private static void EmitGroupsFollowingBoundary(
        List<VdbeInstruction> ins,
        Cursor delay,
        IReadOnlyList<AggregateFunctionSpec> windows,
        IReadOnlyList<WindowOutput> outputs,
        int[] argOffsets,
        int argBase,
        int width,
        int aggOutBase,
        int outBase,
        int flushBase,
        int groupsFollowing,
        int skipInverseIndex,
        bool drainRemaining)
    {
        var scratch = new Cursor(groupsFollowing + 1);
        var tmpIndex = skipInverseIndex + 2;
        var oneIndex = skipInverseIndex + 1;
        EmitCopyEphemeral(ins, delay, scratch, flushBase, width);
        ins.Add(new CloseCursorInstruction(delay));
        ins.Add(new OpenEphemeralInstruction(delay, width));

        var skipFlush = ins.Count;
        ins.Add(new JumpIfInstruction(new Register(skipInverseIndex), new ProgramCounter(0)));
        var oldest = new Cursor(1);
        EmitCopyEphemeral(ins, oldest, delay, flushBase, width);
        EmitPeerFlush(ins, oldest, windows, outputs, width, aggOutBase, outBase, flushBase);
        EmitInverseEphemeral(ins, delay, windows, argOffsets, argBase, flushBase, width);
        ins.Add(new CloseCursorInstruction(delay));
        ins.Add(new OpenEphemeralInstruction(delay, width));
        for (var slot = 1; slot < groupsFollowing; slot++)
        {
            var source = new Cursor(slot + 1);
            var destination = new Cursor(slot);
            EmitCopyEphemeral(ins, source, destination, flushBase, width);
            ins.Add(new CloseCursorInstruction(source));
            ins.Add(new OpenEphemeralInstruction(source, width));
        }

        EmitCopyEphemeral(ins, scratch, new Cursor(groupsFollowing), flushBase, width);
        ins.Add(new CloseCursorInstruction(scratch));
        ins.Add(new OpenEphemeralInstruction(scratch, width));
        var afterFlush = ins.Count;
        ins.Add(new GotoInstruction(new ProgramCounter(0)));
        ins[skipFlush] = new JumpIfInstruction(
            new Register(skipInverseIndex),
            new ProgramCounter(ins.Count));
        EmitGroupsFollowingEnqueue(
            ins, scratch, groupsFollowing, skipInverseIndex, tmpIndex, flushBase, width);
        ins[afterFlush] = new GotoInstruction(new ProgramCounter(ins.Count));
        if (drainRemaining)
        {
            EmitGroupsFollowingDrain(
                ins, windows, outputs, argOffsets, argBase, width, aggOutBase, outBase, flushBase,
                groupsFollowing, skipInverseIndex, oneIndex);
        }
    }

    private static void EmitGroupsFollowingEnqueue(
        List<VdbeInstruction> ins,
        Cursor scratch,
        int groupsFollowing,
        int skipInverseIndex,
        int tmpIndex,
        int flushBase,
        int width)
    {
        var doneJumps = new int[groupsFollowing];
        for (var slot = 1; slot <= groupsFollowing; slot++)
        {
            ins.Add(new CopyInstruction(new Register(skipInverseIndex), new Register(tmpIndex)));
            ins.Add(new LoadConstantInstruction(
                new Register(tmpIndex + 1),
                SqlValue.Integer(groupsFollowing - slot + 1)));
            ins.Add(new ArithmeticInstruction(
                new Register(tmpIndex),
                ArithmeticOperator.Subtract,
                new RegisterRange(new Register(tmpIndex), 2)));
            var skipSlot = ins.Count;
            ins.Add(new JumpIfInstruction(new Register(tmpIndex), new ProgramCounter(0)));
            EmitCopyEphemeral(ins, scratch, new Cursor(slot), flushBase, width);
            ins.Add(new CloseCursorInstruction(scratch));
            ins.Add(new OpenEphemeralInstruction(scratch, width));
            ins.Add(new ArithmeticInstruction(
                new Register(skipInverseIndex),
                ArithmeticOperator.Subtract,
                new RegisterRange(new Register(skipInverseIndex), 2)));
            doneJumps[slot - 1] = ins.Count;
            ins.Add(new GotoInstruction(new ProgramCounter(0)));
            ins[skipSlot] = new JumpIfInstruction(new Register(tmpIndex), new ProgramCounter(ins.Count));
        }

        var after = ins.Count;
        foreach (var done in doneJumps)
            ins[done] = new GotoInstruction(new ProgramCounter(after));
    }

    private static void EmitGroupsFollowingDrain(
        List<VdbeInstruction> ins,
        IReadOnlyList<AggregateFunctionSpec> windows,
        IReadOnlyList<WindowOutput> outputs,
        int[] argOffsets,
        int argBase,
        int width,
        int aggOutBase,
        int outBase,
        int flushBase,
        int groupsFollowing,
        int skipInverseIndex,
        int oneIndex)
    {
        _ = skipInverseIndex;
        _ = oneIndex;
        var scratch = new Cursor(groupsFollowing + 1);
        for (var slot = 1; slot <= groupsFollowing; slot++)
        {
            var cursor = new Cursor(slot);
            EmitCopyEphemeral(ins, cursor, scratch, flushBase, width);
            EmitPeerFlush(ins, cursor, windows, outputs, width, aggOutBase, outBase, flushBase);
            EmitInverseEphemeral(ins, scratch, windows, argOffsets, argBase, flushBase, width);
            ins.Add(new CloseCursorInstruction(scratch));
            ins.Add(new OpenEphemeralInstruction(scratch, width));
        }
    }

    private static void EmitRangeHistoryReset(
        List<VdbeInstruction> ins,
        int width,
        int offsetIndex,
        int rangePreceding,
        bool openHistory,
        bool needsQueue = false)
    {
        if (rangePreceding == 0 && !needsQueue)
            return;

        var history = new Cursor(1);
        var scratch = new Cursor(2);
        if (!openHistory)
        {
            ins.Add(new CloseCursorInstruction(history));
            ins.Add(new CloseCursorInstruction(scratch));
            ins.Add(new OpenEphemeralInstruction(history, width));
            ins.Add(new OpenEphemeralInstruction(scratch, width));
        }

        if (rangePreceding > 0)
            ins.Add(new LoadConstantInstruction(new Register(offsetIndex), SqlValue.Integer(rangePreceding)));
    }

    private static void EmitRangeFollowingBoundary(
        List<VdbeInstruction> ins,
        Cursor delay,
        Cursor queue,
        Cursor scratch,
        IReadOnlyList<AggregateFunctionSpec> windows,
        IReadOnlyList<WindowOutput> outputs,
        int[] argOffsets,
        int argBase,
        int width,
        int aggOutBase,
        int outBase,
        int flushBase,
        int rangeFollowing,
        bool descendingOrder,
        int orderColumn,
        int currentKeyIndex,
        int offsetIndex,
        int rowReg,
        int boundReg,
        int flagReg,
        int oldestKeyReg,
        VdbeGroupComparer? peerComparer,
        bool drainRemaining,
        bool fullPartition = false)
    {
        EmitCopyEphemeral(ins, delay, scratch, flushBase, width);
        ins.Add(new CloseCursorInstruction(delay));
        ins.Add(new OpenEphemeralInstruction(delay, width));
        EmitCopyEphemeral(ins, scratch, queue, flushBase, width);
        ins.Add(new CloseCursorInstruction(scratch));
        ins.Add(new OpenEphemeralInstruction(scratch, width));

        if (drainRemaining)
        {
            if (fullPartition || peerComparer is null)
            {
                EmitPeerFlush(ins, queue, windows, outputs, width, aggOutBase, outBase, flushBase);
                return;
            }

            var drainLoop = ins.Count;
            var drainRewind = ins.Count;
            ins.Add(new RewindCursorInstruction(queue, new ProgramCounter(0)));
            EmitFlushOldestPeerGroup(
                ins,
                queue,
                scratch,
                windows,
                outputs,
                argOffsets,
                argBase,
                width,
                aggOutBase,
                outBase,
                flushBase,
                orderColumn,
                oldestKeyReg,
                peerComparer!);
            ins.Add(new GotoInstruction(new ProgramCounter(drainLoop)));
            ins[drainRewind] = new RewindCursorInstruction(queue, new ProgramCounter(ins.Count));
            return;
        }

        if (rangeFollowing == 0)
            return;

        var flushLoop = ins.Count;
        var probeRewind = ins.Count;
        ins.Add(new RewindCursorInstruction(queue, new ProgramCounter(0)));
        if (width > 0)
            ins.Add(new ColumnRangeInstruction(queue, 0, new Register(flushBase), width));
        var drop = EmitRangeOffsetCompare(
            ins,
            flushBase,
            orderColumn,
            currentKeyIndex,
            offsetIndex,
            descendingOrder,
            rowReg,
            boundReg,
            flagReg);
        var inRange = ins.Count;
        ins.Add(new GotoInstruction(new ProgramCounter(0)));
        ins[drop] = new JumpIfNotTrueInstruction(new Register(flagReg), new ProgramCounter(ins.Count));
        EmitFlushOldestPeerGroup(
            ins,
            queue,
            scratch,
            windows,
            outputs,
            argOffsets,
            argBase,
            width,
            aggOutBase,
            outBase,
            flushBase,
            orderColumn,
            oldestKeyReg,
            peerComparer!);
        ins.Add(new GotoInstruction(new ProgramCounter(flushLoop)));
        var after = ins.Count;
        ins[probeRewind] = new RewindCursorInstruction(queue, new ProgramCounter(after));
        ins[inRange] = new GotoInstruction(new ProgramCounter(after));
    }

    private static void EmitFlushOldestPeerGroup(
        List<VdbeInstruction> ins,
        Cursor queue,
        Cursor scratch,
        IReadOnlyList<AggregateFunctionSpec> windows,
        IReadOnlyList<WindowOutput> outputs,
        int[] argOffsets,
        int argBase,
        int width,
        int aggOutBase,
        int outBase,
        int flushBase,
        int orderColumn,
        int oldestKeyReg,
        VdbeGroupComparer peerComparer)
    {
        var rewind = ins.Count;
        ins.Add(new RewindCursorInstruction(queue, new ProgramCounter(0)));
        if (width > 0)
            ins.Add(new ColumnRangeInstruction(queue, 0, new Register(flushBase), width));
        ins.Add(new CopyInstruction(new Register(flushBase + orderColumn), new Register(oldestKeyReg)));

        var emit = ins.Count;
        for (var i = 0; i < windows.Count; i++)
        {
            ins.Add(new AggFinalizeInstruction(
                new Accumulator(i),
                windows[i].Aggregate,
                new Register(aggOutBase + i)));
        }

        for (var o = 0; o < outputs.Count; o++)
        {
            var output = outputs[o];
            var destination = new Register(outBase + o);
            ins.Add(output.Kind switch
            {
                WindowOutputKind.Column => new CopyInstruction(
                    new Register(flushBase + output.Index),
                    destination),
                WindowOutputKind.Window => new CopyInstruction(
                    new Register(aggOutBase + output.Index),
                    destination),
                _ => new LoadConstantInstruction(destination, output.Constant),
            });
        }

        ins.Add(new ResultRowInstruction(new RegisterRange(new Register(outBase), outputs.Count)));
        GatherArgumentsFromRow(ins, windows, argOffsets, argBase, flushBase);
        EmitWindowInverses(ins, windows, argOffsets, argBase);

        var next = ins.Count;
        ins.Add(new NextInstruction(queue, new ProgramCounter(0)));
        ins.Add(new CloseCursorInstruction(queue));
        ins.Add(new OpenEphemeralInstruction(queue, width));
        var gotoDoneFromEmpty = ins.Count;
        ins.Add(new GotoInstruction(new ProgramCounter(0)));

        var loadNext = ins.Count;
        ins[next] = new NextInstruction(queue, new ProgramCounter(loadNext));
        if (width > 0)
            ins.Add(new ColumnRangeInstruction(queue, 0, new Register(flushBase), width));
        ins.Add(new SameGroupInstruction(
            new RegisterRange(new Register(flushBase + orderColumn), 1),
            new RegisterRange(new Register(oldestKeyReg), 1),
            peerComparer,
            new ProgramCounter(emit)));

        ins.Add(new EphemeralInsertInstruction(scratch, new RegisterRange(new Register(flushBase), width)));
        var leftoverNext = ins.Count;
        ins.Add(new NextInstruction(queue, new ProgramCounter(0)));
        var skipLeftoverBody = ins.Count;
        ins.Add(new GotoInstruction(new ProgramCounter(0)));
        var leftoverBody = ins.Count;
        ins[leftoverNext] = new NextInstruction(queue, new ProgramCounter(leftoverBody));
        if (width > 0)
            ins.Add(new ColumnRangeInstruction(queue, 0, new Register(flushBase), width));
        ins.Add(new EphemeralInsertInstruction(scratch, new RegisterRange(new Register(flushBase), width)));
        ins.Add(new NextInstruction(queue, new ProgramCounter(leftoverBody)));
        var compact = ins.Count;
        ins[skipLeftoverBody] = new GotoInstruction(new ProgramCounter(compact));
        ins.Add(new CloseCursorInstruction(queue));
        ins.Add(new OpenEphemeralInstruction(queue, width));
        EmitCopyEphemeral(ins, scratch, queue, flushBase, width);
        ins.Add(new CloseCursorInstruction(scratch));
        ins.Add(new OpenEphemeralInstruction(scratch, width));
        var done = ins.Count;
        ins[rewind] = new RewindCursorInstruction(queue, new ProgramCounter(done));
        ins[gotoDoneFromEmpty] = new GotoInstruction(new ProgramCounter(done));
    }

    private static void EmitMovingPeerBoundary(
        List<VdbeInstruction> ins,
        Cursor delay,
        Cursor history,
        Cursor scratch,
        IReadOnlyList<AggregateFunctionSpec> windows,
        IReadOnlyList<WindowOutput> outputs,
        int[] argOffsets,
        int argBase,
        int width,
        int aggOutBase,
        int outBase,
        int flushBase,
        int groupsPreceding,
        int skipInverseIndex,
        int rangePreceding,
        bool descendingOrder,
        int orderColumn,
        int currentKeyIndex,
        int rowReg,
        int boundReg,
        int flagReg,
        int groupsFollowing,
        int rangeFollowing,
        int oldestKeyReg,
        VdbeGroupComparer? peerComparer,
        bool unboundedFollowing = false,
        bool fullPartition = false,
        WindowExclusion exclusion = WindowExclusion.NoOthers)
    {
        if (exclusion != WindowExclusion.NoOthers
            && rangePreceding == 0
            && rangeFollowing == 0
            && groupsPreceding == 0
            && groupsFollowing == 0
            && !unboundedFollowing
            && !fullPartition)
        {
            EmitExclusionPeerBoundary(
                ins,
                delay,
                windows,
                outputs,
                argOffsets,
                argBase,
                width,
                aggOutBase,
                outBase,
                flushBase,
                exclusion,
                drainRemaining: false);
            return;
        }

        if (rangeFollowing > 0 || unboundedFollowing || fullPartition)
        {
            EmitRangeFollowingBoundary(
                ins,
                delay,
                history,
                scratch,
                windows,
                outputs,
                argOffsets,
                argBase,
                width,
                aggOutBase,
                outBase,
                flushBase,
                rangeFollowing,
                descendingOrder,
                orderColumn,
                currentKeyIndex,
                skipInverseIndex,
                rowReg,
                boundReg,
                flagReg,
                oldestKeyReg,
                peerComparer,
                drainRemaining: false,
                fullPartition);
            return;
        }

        if (groupsFollowing > 0)
        {
            EmitGroupsFollowingBoundary(
                ins,
                delay,
                windows,
                outputs,
                argOffsets,
                argBase,
                width,
                aggOutBase,
                outBase,
                flushBase,
                groupsFollowing,
                skipInverseIndex,
                drainRemaining: false);
            return;
        }

        if (rangePreceding == 0)
        {
            EmitPeerGroupBoundary(
                ins,
                delay,
                windows,
                outputs,
                argOffsets,
                argBase,
                width,
                aggOutBase,
                outBase,
                flushBase,
                groupsPreceding,
                skipInverseIndex);
            return;
        }

        EmitCopyEphemeral(ins, delay, scratch, flushBase, width);
        EmitPeerFlush(ins, delay, windows, outputs, width, aggOutBase, outBase, flushBase);
        EmitRangeCompact(
            ins,
            history,
            delay,
            windows,
            argOffsets,
            argBase,
            width,
            flushBase,
            rangePreceding,
            descendingOrder,
            orderColumn,
            currentKeyIndex,
            skipInverseIndex,
            rowReg,
            boundReg,
            flagReg);
        EmitRangeRetainIfInRange(
            ins,
            scratch,
            history,
            windows,
            argOffsets,
            argBase,
            width,
            flushBase,
            descendingOrder,
            orderColumn,
            currentKeyIndex,
            skipInverseIndex,
            rowReg,
            boundReg,
            flagReg);
    }

    private static void EmitRangePeerBoundary(
        List<VdbeInstruction> ins,
        Cursor delay,
        Cursor history,
        Cursor scratch,
        IReadOnlyList<AggregateFunctionSpec> windows,
        IReadOnlyList<WindowOutput> outputs,
        int[] argOffsets,
        int argBase,
        int width,
        int aggOutBase,
        int outBase,
        int flushBase,
        int rangePreceding,
        bool descendingOrder,
        int orderColumn,
        int currentKeyIndex,
        int offsetIndex,
        int rowReg,
        int boundReg,
        int flagReg,
        bool retainFlushedGroup)
    {
        if (rangePreceding == 0 || !retainFlushedGroup)
        {
            EmitPeerFlush(ins, delay, windows, outputs, width, aggOutBase, outBase, flushBase);
            return;
        }

        EmitMovingPeerBoundary(
            ins,
            delay,
            history,
            scratch,
            windows,
            outputs,
            argOffsets,
            argBase,
            width,
            aggOutBase,
            outBase,
            flushBase,
            groupsPreceding: 0,
            offsetIndex,
            rangePreceding,
            descendingOrder,
            orderColumn,
            currentKeyIndex,
            rowReg,
            boundReg,
            flagReg,
            groupsFollowing: 0,
            rangeFollowing: 0,
            oldestKeyReg: 0,
            peerComparer: null);
    }

    private static void EmitRangeCompact(
        List<VdbeInstruction> ins,
        Cursor history,
        Cursor destination,
        IReadOnlyList<AggregateFunctionSpec> windows,
        int[] argOffsets,
        int argBase,
        int width,
        int rowBase,
        int rangePreceding,
        bool descendingOrder,
        int orderColumn,
        int currentKeyIndex,
        int offsetIndex,
        int rowReg,
        int boundReg,
        int flagReg)
    {
        _ = rangePreceding;
        var rewind = ins.Count;
        ins.Add(new RewindCursorInstruction(history, new ProgramCounter(0)));
        var loop = ins.Count;
        if (width > 0)
            ins.Add(new ColumnRangeInstruction(history, 0, new Register(rowBase), width));

        var drop = EmitRangeOffsetCompare(
            ins,
            rowBase,
            orderColumn,
            currentKeyIndex,
            offsetIndex,
            descendingOrder,
            rowReg,
            boundReg,
            flagReg);
        ins.Add(new EphemeralInsertInstruction(destination, new RegisterRange(new Register(rowBase), width)));
        var toNext = ins.Count;
        ins.Add(new GotoInstruction(new ProgramCounter(0)));
        ins[drop] = new JumpIfNotTrueInstruction(new Register(flagReg), new ProgramCounter(ins.Count));
        GatherArgumentsFromRow(ins, windows, argOffsets, argBase, rowBase);
        EmitWindowInverses(ins, windows, argOffsets, argBase);
        ins[toNext] = new GotoInstruction(new ProgramCounter(ins.Count));
        ins.Add(new NextInstruction(history, new ProgramCounter(loop)));
        ins[rewind] = new RewindCursorInstruction(history, new ProgramCounter(ins.Count));

        ins.Add(new CloseCursorInstruction(history));
        ins.Add(new OpenEphemeralInstruction(history, width));
        EmitCopyEphemeral(ins, destination, history, rowBase, width);
        ins.Add(new CloseCursorInstruction(destination));
        ins.Add(new OpenEphemeralInstruction(destination, width));
    }

    private static void EmitRangeRetainIfInRange(
        List<VdbeInstruction> ins,
        Cursor source,
        Cursor history,
        IReadOnlyList<AggregateFunctionSpec> windows,
        int[] argOffsets,
        int argBase,
        int width,
        int rowBase,
        bool descendingOrder,
        int orderColumn,
        int currentKeyIndex,
        int offsetIndex,
        int rowReg,
        int boundReg,
        int flagReg)
    {
        var rewind = ins.Count;
        ins.Add(new RewindCursorInstruction(source, new ProgramCounter(0)));
        if (width > 0)
            ins.Add(new ColumnRangeInstruction(source, 0, new Register(rowBase), width));

        var drop = EmitRangeOffsetCompare(
            ins,
            rowBase,
            orderColumn,
            currentKeyIndex,
            offsetIndex,
            descendingOrder,
            rowReg,
            boundReg,
            flagReg);
        EmitCopyEphemeral(ins, source, history, rowBase, width);
        var skipInverse = ins.Count;
        ins.Add(new GotoInstruction(new ProgramCounter(0)));
        ins[drop] = new JumpIfNotTrueInstruction(new Register(flagReg), new ProgramCounter(ins.Count));
        EmitInverseEphemeral(ins, source, windows, argOffsets, argBase, rowBase, width);
        var close = ins.Count;
        ins[skipInverse] = new GotoInstruction(new ProgramCounter(close));
        ins[rewind] = new RewindCursorInstruction(source, new ProgramCounter(close));
        ins.Add(new CloseCursorInstruction(source));
        ins.Add(new OpenEphemeralInstruction(source, width));
    }

    /// <summary>
    /// Writes the RANGE offset predicate into <paramref name="flagReg"/> and emits a placeholder
    /// <see cref="JumpIfNotTrueInstruction"/> whose target the caller patches. Keep the row when
    /// the flag is true (ASC: <c>row + n &gt;= current</c>; DESC: <c>row - n &lt;= current</c>).
    /// </summary>
    private static int EmitRangeOffsetCompare(
        List<VdbeInstruction> ins,
        int rowBase,
        int orderColumn,
        int currentKeyIndex,
        int offsetIndex,
        bool descendingOrder,
        int rowReg,
        int boundReg,
        int flagReg)
    {
        ins.Add(new CopyInstruction(new Register(rowBase + orderColumn), new Register(rowReg)));
        ins.Add(new CopyInstruction(new Register(offsetIndex), new Register(boundReg)));
        ins.Add(new ArithmeticInstruction(
            new Register(boundReg),
            descendingOrder ? ArithmeticOperator.Subtract : ArithmeticOperator.Add,
            new RegisterRange(new Register(rowReg), 2)));
        ins.Add(new CompareInstruction(
            new Register(flagReg),
            descendingOrder
                ? VdbeComparisonOperator.LessThanOrEqual
                : VdbeComparisonOperator.GreaterThanOrEqual,
            new Register(boundReg),
            new Register(currentKeyIndex),
            VdbeValueAffinity.Numeric,
            VdbeValueAffinity.Numeric,
            Collation: null));
        var jump = ins.Count;
        ins.Add(new JumpIfNotTrueInstruction(new Register(flagReg), new ProgramCounter(0)));
        return jump;
    }

    private static void EmitPeerGroupBoundary(
        List<VdbeInstruction> ins,
        Cursor delay,
        IReadOnlyList<AggregateFunctionSpec> windows,
        IReadOnlyList<WindowOutput> outputs,
        int[] argOffsets,
        int argBase,
        int width,
        int aggOutBase,
        int outBase,
        int flushBase,
        int groupsPreceding,
        int skipInverseIndex)
    {
        if (groupsPreceding == 0)
        {
            EmitPeerFlush(ins, delay, windows, outputs, width, aggOutBase, outBase, flushBase);
            return;
        }

        var scratch = new Cursor(groupsPreceding + 1);
        EmitCopyEphemeral(ins, delay, scratch, flushBase, width);
        EmitPeerFlush(ins, delay, windows, outputs, width, aggOutBase, outBase, flushBase);

        var skipInverse = ins.Count;
        ins.Add(new JumpIfInstruction(new Register(skipInverseIndex), new ProgramCounter(0)));
        var oldest = new Cursor(1);
        EmitInverseEphemeral(ins, oldest, windows, argOffsets, argBase, flushBase, width);
        ins.Add(new CloseCursorInstruction(oldest));
        ins.Add(new OpenEphemeralInstruction(oldest, width));
        ins[skipInverse] = new JumpIfInstruction(
            new Register(skipInverseIndex),
            new ProgramCounter(ins.Count));

        for (var slot = 1; slot < groupsPreceding; slot++)
        {
            var source = new Cursor(slot + 1);
            var destination = new Cursor(slot);
            EmitCopyEphemeral(ins, source, destination, flushBase, width);
            ins.Add(new CloseCursorInstruction(source));
            ins.Add(new OpenEphemeralInstruction(source, width));
        }

        EmitCopyEphemeral(ins, scratch, new Cursor(groupsPreceding), flushBase, width);
        ins.Add(new CloseCursorInstruction(scratch));
        ins.Add(new OpenEphemeralInstruction(scratch, width));

        var skipDecrement = ins.Count;
        ins.Add(new JumpIfInstruction(new Register(skipInverseIndex), new ProgramCounter(0)));
        var gotoAfterDecrement = ins.Count;
        ins.Add(new GotoInstruction(new ProgramCounter(0)));
        var decrement = ins.Count;
        ins.Add(new ArithmeticInstruction(
            new Register(skipInverseIndex),
            ArithmeticOperator.Subtract,
            new RegisterRange(new Register(skipInverseIndex), 2)));
        ins[skipDecrement] = new JumpIfInstruction(
            new Register(skipInverseIndex),
            new ProgramCounter(decrement));
        ins[gotoAfterDecrement] = new GotoInstruction(new ProgramCounter(decrement + 1));
    }

    private static void EmitCopyEphemeral(
        List<VdbeInstruction> ins,
        Cursor source,
        Cursor destination,
        int tmpBase,
        int width)
    {
        var rewind = ins.Count;
        ins.Add(new RewindCursorInstruction(source, new ProgramCounter(0)));
        var loop = ins.Count;
        if (width > 0)
            ins.Add(new ColumnRangeInstruction(source, 0, new Register(tmpBase), width));
        ins.Add(new EphemeralInsertInstruction(destination, new RegisterRange(new Register(tmpBase), width)));
        ins.Add(new NextInstruction(source, new ProgramCounter(loop)));
        ins[rewind] = new RewindCursorInstruction(source, new ProgramCounter(ins.Count));
    }

    private static void EmitInverseEphemeral(
        List<VdbeInstruction> ins,
        Cursor source,
        IReadOnlyList<AggregateFunctionSpec> windows,
        int[] argOffsets,
        int argBase,
        int rowBase,
        int width)
    {
        var rewind = ins.Count;
        ins.Add(new RewindCursorInstruction(source, new ProgramCounter(0)));
        var loop = ins.Count;
        if (width > 0)
            ins.Add(new ColumnRangeInstruction(source, 0, new Register(rowBase), width));
        GatherArgumentsFromRow(ins, windows, argOffsets, argBase, rowBase);
        EmitWindowInverses(ins, windows, argOffsets, argBase);
        ins.Add(new NextInstruction(source, new ProgramCounter(loop)));
        ins[rewind] = new RewindCursorInstruction(source, new ProgramCounter(ins.Count));
    }

    private static void EmitPeerFlush(
        List<VdbeInstruction> ins,
        Cursor cursor,
        IReadOnlyList<AggregateFunctionSpec> windows,
        IReadOnlyList<WindowOutput> outputs,
        int width,
        int aggOutBase,
        int outBase,
        int flushBase)
    {
        var rewind = ins.Count;
        ins.Add(new RewindCursorInstruction(cursor, new ProgramCounter(0)));
        var flushLoop = ins.Count;
        if (width > 0)
            ins.Add(new ColumnRangeInstruction(cursor, 0, new Register(flushBase), width));

        for (var i = 0; i < windows.Count; i++)
        {
            ins.Add(new AggFinalizeInstruction(
                new Accumulator(i),
                windows[i].Aggregate,
                new Register(aggOutBase + i)));
        }

        for (var o = 0; o < outputs.Count; o++)
        {
            var output = outputs[o];
            var destination = new Register(outBase + o);
            ins.Add(output.Kind switch
            {
                WindowOutputKind.Column => new CopyInstruction(
                    new Register(flushBase + output.Index),
                    destination),
                WindowOutputKind.Window => new CopyInstruction(
                    new Register(aggOutBase + output.Index),
                    destination),
                _ => new LoadConstantInstruction(destination, output.Constant),
            });
        }

        ins.Add(new ResultRowInstruction(new RegisterRange(new Register(outBase), outputs.Count)));
        ins.Add(new NextInstruction(cursor, new ProgramCounter(flushLoop)));
        var afterFlush = ins.Count;
        ins[rewind] = new RewindCursorInstruction(cursor, new ProgramCounter(afterFlush));
        ins.Add(new CloseCursorInstruction(cursor));
        ins.Add(new OpenEphemeralInstruction(cursor, width));
    }

    private static void EmitStepEphemeral(
        List<VdbeInstruction> ins,
        Cursor source,
        IReadOnlyList<AggregateFunctionSpec> windows,
        int[] argOffsets,
        int argBase,
        int rowBase,
        int width)
    {
        var rewind = ins.Count;
        ins.Add(new RewindCursorInstruction(source, new ProgramCounter(0)));
        var loop = ins.Count;
        if (width > 0)
            ins.Add(new ColumnRangeInstruction(source, 0, new Register(rowBase), width));
        GatherArgumentsFromRow(ins, windows, argOffsets, argBase, rowBase);
        EmitWindowSteps(ins, windows, argOffsets, argBase, rowBase);
        ins.Add(new NextInstruction(source, new ProgramCounter(loop)));
        ins[rewind] = new RewindCursorInstruction(source, new ProgramCounter(ins.Count));
    }

    private static void EmitExclusionPeerBoundary(
        List<VdbeInstruction> ins,
        Cursor delay,
        IReadOnlyList<AggregateFunctionSpec> windows,
        IReadOnlyList<WindowOutput> outputs,
        int[] argOffsets,
        int argBase,
        int width,
        int aggOutBase,
        int outBase,
        int flushBase,
        WindowExclusion exclusion,
        bool drainRemaining)
    {
        var rewind = ins.Count;
        ins.Add(new RewindCursorInstruction(delay, new ProgramCounter(0)));
        var loop = ins.Count;
        if (width > 0)
            ins.Add(new ColumnRangeInstruction(delay, 0, new Register(flushBase), width));

        if (exclusion == WindowExclusion.Ties)
        {
            GatherArgumentsFromRow(ins, windows, argOffsets, argBase, flushBase);
            EmitWindowSteps(ins, windows, argOffsets, argBase, flushBase);
        }
        else if (exclusion == WindowExclusion.CurrentRow)
        {
            GatherArgumentsFromRow(ins, windows, argOffsets, argBase, flushBase);
            EmitWindowInverses(ins, windows, argOffsets, argBase);
        }

        for (var i = 0; i < windows.Count; i++)
        {
            ins.Add(new AggFinalizeInstruction(
                new Accumulator(i),
                windows[i].Aggregate,
                new Register(aggOutBase + i)));
        }

        for (var o = 0; o < outputs.Count; o++)
        {
            var output = outputs[o];
            var destination = new Register(outBase + o);
            ins.Add(output.Kind switch
            {
                WindowOutputKind.Column => new CopyInstruction(
                    new Register(flushBase + output.Index),
                    destination),
                WindowOutputKind.Window => new CopyInstruction(
                    new Register(aggOutBase + output.Index),
                    destination),
                _ => new LoadConstantInstruction(destination, output.Constant),
            });
        }

        ins.Add(new ResultRowInstruction(new RegisterRange(new Register(outBase), outputs.Count)));

        if (exclusion == WindowExclusion.Ties)
        {
            GatherArgumentsFromRow(ins, windows, argOffsets, argBase, flushBase);
            EmitWindowInverses(ins, windows, argOffsets, argBase);
        }
        else if (exclusion == WindowExclusion.CurrentRow)
        {
            GatherArgumentsFromRow(ins, windows, argOffsets, argBase, flushBase);
            EmitWindowSteps(ins, windows, argOffsets, argBase, flushBase);
        }

        ins.Add(new NextInstruction(delay, new ProgramCounter(loop)));
        ins[rewind] = new RewindCursorInstruction(delay, new ProgramCounter(ins.Count));

        if (exclusion is WindowExclusion.Group or WindowExclusion.Ties && !drainRemaining)
            EmitStepEphemeral(ins, delay, windows, argOffsets, argBase, flushBase, width);

        ins.Add(new CloseCursorInstruction(delay));
        ins.Add(new OpenEphemeralInstruction(delay, width));
    }

    private static void EmitPrecedingFrameReset(
        List<VdbeInstruction> ins,
        WindowFrameSpec frame,
        int skipInverseIndex,
        int oneIndex)
    {
        if (frame.PrecedingCount == 1)
        {
            ins.Add(new LoadConstantInstruction(new Register(skipInverseIndex), SqlValue.Integer(1)));
            return;
        }

        if (frame.PrecedingCount == 0)
            return;

        ins.Add(new LoadConstantInstruction(new Register(skipInverseIndex), SqlValue.Integer(frame.PrecedingCount)));
        ins.Add(new LoadConstantInstruction(new Register(oneIndex), SqlValue.Integer(1)));
    }

    private static void EmitBoundedPrecedingInverse(
        List<VdbeInstruction> ins,
        IReadOnlyList<AggregateFunctionSpec> windows,
        int[] argOffsets,
        int argBase,
        int departingArgBase,
        int skipInverseIndex,
        int totalArgs,
        int precedingCount)
    {
        var skipInverseInstruction = ins.Count;
        ins.Add(new JumpIfInstruction(new Register(skipInverseIndex), new ProgramCounter(0)));
        EmitWindowInverses(ins, windows, argOffsets, departingArgBase);

        var shiftAndSave = ins.Count;
        for (var slot = 0; slot < precedingCount - 1; slot++)
        {
            for (var i = 0; i < totalArgs; i++)
            {
                ins.Add(new CopyInstruction(
                    new Register(departingArgBase + ((slot + 1) * totalArgs) + i),
                    new Register(departingArgBase + (slot * totalArgs) + i)));
            }
        }

        var lastSlot = departingArgBase + ((precedingCount - 1) * totalArgs);
        for (var i = 0; i < totalArgs; i++)
            ins.Add(new CopyInstruction(new Register(argBase + i), new Register(lastSlot + i)));

        ins[skipInverseInstruction] = new JumpIfInstruction(
            new Register(skipInverseIndex),
            new ProgramCounter(shiftAndSave));

        var skipDecrementInstruction = ins.Count;
        ins.Add(new JumpIfInstruction(new Register(skipInverseIndex), new ProgramCounter(0)));
        var gotoAfterDecrement = ins.Count;
        ins.Add(new GotoInstruction(new ProgramCounter(0)));
        var decrement = ins.Count;
        ins.Add(new ArithmeticInstruction(
            new Register(skipInverseIndex),
            ArithmeticOperator.Subtract,
            new RegisterRange(new Register(skipInverseIndex), 2)));
        ins[skipDecrementInstruction] = new JumpIfInstruction(
            new Register(skipInverseIndex),
            new ProgramCounter(decrement));
        ins[gotoAfterDecrement] = new GotoInstruction(new ProgramCounter(decrement + 1));
    }

    // Steps every window function from the materialized staging row: gathers each function's argument
    // columns out of staging into its argument block, then folds the block into its accumulator. A nullary
    // function such as the count(*) behind row_number() steps a zero-width range.
    private static void EmitWindowSteps(
        List<VdbeInstruction> ins,
        IReadOnlyList<AggregateFunctionSpec> windows,
        int[] argOffsets,
        int argBase,
        int stagingBase)
    {
        for (var i = 0; i < windows.Count; i++)
        {
            var spec = windows[i];
            for (var k = 0; k < spec.Arity; k++)
                ins.Add(new CopyInstruction(new Register(stagingBase + spec.ArgumentColumns[k]), new Register(argBase + argOffsets[i] + k)));

            ins.Add(new AggStepInstruction(
                new Accumulator(i),
                spec.Aggregate,
                new RegisterRange(new Register(argBase + argOffsets[i]), spec.Arity)));
        }
    }

    private static void EmitWindowInverses(
        List<VdbeInstruction> ins,
        IReadOnlyList<AggregateFunctionSpec> windows,
        int[] argOffsets,
        int argumentBase)
    {
        for (var i = 0; i < windows.Count; i++)
        {
            ins.Add(new AggInverseInstruction(
                new Accumulator(i),
                windows[i].Aggregate,
                new RegisterRange(new Register(argumentBase + argOffsets[i]), windows[i].Arity)));
        }
    }

    private static int[] ComputeArgOffsets(IReadOnlyList<AggregateFunctionSpec> windows, out int totalArgs)
    {
        var offsets = new int[windows.Count];
        var running = 0;
        for (var i = 0; i < windows.Count; i++)
        {
            offsets[i] = running;
            running += windows[i].Arity;
        }

        totalArgs = running;
        return offsets;
    }

    private static void ValidateOutput(WindowOutput output, int tableColumnCount, int windowCount)
    {
        switch (output.Kind)
        {
            case WindowOutputKind.Column when output.Index >= tableColumnCount:
                throw new ArgumentException(
                    $"Output projects column {output.Index}, but the table has {tableColumnCount} columns.",
                    nameof(output));
            case WindowOutputKind.Window when output.Index >= windowCount:
                throw new ArgumentException(
                    $"Output projects window {output.Index}, but the scan declares {windowCount} window functions.",
                    nameof(output));
            default:
                break;
        }
    }
}
