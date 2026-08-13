using Ahtola.Core.Execution;

namespace Ahtola.Core.Compilation;

/// <summary>
/// The join shape a <see cref="JoinProgramBuilder"/> lowers.
/// </summary>
public enum JoinType
{
    /// <summary>An inner join: only outer/inner pairs that satisfy the join predicate are emitted.</summary>
    Inner,

    /// <summary>A left outer join: every inner-matching pair is emitted, and each outer row that
    /// matches no inner row (including when the inner table is empty) is emitted once with the
    /// inner columns null-extended.</summary>
    LeftOuter,
}

/// <summary>
/// One output column of a join result row: either a column read from the combined
/// <c>(left columns, right columns)</c> row or a folded compile-time constant. Mirrors the
/// scan and sorted-scan projection lowerings but indexes the combined row so the builder stays
/// free of AST and SQL semantics — the caller resolves a (possibly qualified) column reference
/// to its ordinal in the concatenated row.
/// </summary>
public readonly record struct JoinProjection
{
    private JoinProjection(bool isConstant, int columnIndex, SqlValue constant)
    {
        IsConstant = isConstant;
        ColumnIndex = columnIndex;
        Constant = constant;
    }

    public bool IsConstant { get; }

    /// <summary>The ordinal in the combined <c>(left ++ right)</c> row this output projects
    /// (column outputs). Left columns occupy <c>0..leftColumnCount-1</c>; right columns follow.</summary>
    public int ColumnIndex { get; }

    /// <summary>The value emitted for a constant output.</summary>
    public SqlValue Constant { get; }

    /// <summary>Projects the combined-row column at <paramref name="combinedColumnIndex"/>.</summary>
    public static JoinProjection ForColumn(int combinedColumnIndex)
    {
        if (combinedColumnIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(combinedColumnIndex));

        return new JoinProjection(false, combinedColumnIndex, default);
    }

    /// <summary>Projects a folded compile-time constant.</summary>
    public static JoinProjection ForConstant(SqlValue value) => new(true, 0, value);
}

/// <summary>
/// Lowers a two-table nested-loop join into a runnable <see cref="VdbeProgram"/>. The program
/// opens an outer and an inner read cursor, scans the inner table once per outer row, materializes
/// each pair into a combined register block, and projects a result row for every pair the join
/// predicate accepts — so the join runs entirely through the resumable state machine rather than
/// the tree-walking evaluator. <see cref="JoinType.LeftOuter"/> additionally maintains a per-outer-row
/// match flag and emits one inner-null-extended row for every outer row that matched nothing. An optional
/// post-join predicate runs after that null extension, preserving LEFT JOIN <c>WHERE</c> timing.
/// </summary>
/// <remarks>
/// The builder owns only the program's control flow and register/jump layout. Row-value semantics —
/// the join (ON) predicate over the combined row, expressed as a <see cref="VdbeRowPredicate"/> — are
/// supplied by the caller, exactly as the scan, sorted-scan, and aggregate builders delegate their
/// semantics. The predicate is the join condition: it decides which inner rows match an outer row, and
/// for a left outer join unmatched outer rows are still null-extended. The optional post-join predicate
/// then tests each matched or null-extended combined row. The emitted program is data-free: the two
/// scanned tables are bound at execution time as cursor sources 0 (outer/left) and 1 (inner/right).
/// <para>
/// The combined staging block <c>r[0..W-1]</c> (W = leftColumnCount + rightColumnCount) holds the current
/// pair; the output block <c>r[W..W+P-1]</c> holds the projected result row; the left outer match flag,
/// when present, lives in <c>r[W+P]</c>.
/// </para>
/// <code>
///   0            OpenReadCursor c0 (outer/left)
///   1            OpenReadCursor c1 (inner/right)
///   2            Rewind c0        -> closeAddr        (outer empty -> no rows)
///   outerLoop    [LoadConstant r[flag]=0]            (LEFT OUTER)
///                Rewind c1        -> nextOuter/noMatch (inner empty)
///   innerLoop    Column c0.* -> staging[0..WL-1]
///                Column c1.* -> staging[WL..W-1]
///                [FilterRegisters staging[0..W-1] -> nextInner]  (join predicate)
///                [LoadConstant r[flag]=1]            (LEFT OUTER: mark matched)
///                [FilterRegisters staging -> nextInner] (post-join WHERE)
///                Copy/LoadConstant per output -> out[0..P-1]
///                ResultRow out
///   nextInner    Next c1          -> innerLoop
///                [JumpIf r[flag]  -> nextOuter]      (LEFT OUTER: matched, skip null-extension)
///   noMatch      [Column c0.* -> staging[0..WL-1]]   (LEFT OUTER)
///                [LoadConstant NULL -> staging[WL..W-1]]
///                [FilterRegisters staging -> nextOuter] (post-join WHERE)
///                [Copy/LoadConstant per output -> out] [ResultRow out]
///   nextOuter    Next c0          -> outerLoop
///   closeAddr    CloseCursor c1; CloseCursor c0; Halt
/// </code>
/// </remarks>
public static class JoinProgramBuilder
{
    public static VdbeProgram Build(
        string leftTableName,
        int leftColumnCount,
        string rightTableName,
        int rightColumnCount,
        JoinType joinType,
        IReadOnlyList<JoinProjection> projections,
        VdbeRowPredicate? predicate = null,
            VdbeRowPredicate? postJoinPredicate = null,
            bool leftIsOuter = true)
        {
            ArgumentNullException.ThrowIfNull(leftTableName);
            ArgumentNullException.ThrowIfNull(rightTableName);
            ArgumentNullException.ThrowIfNull(projections);
            if (leftColumnCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(leftColumnCount), "A join needs at least one left column.");
            if (rightColumnCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(rightColumnCount), "A join needs at least one right column.");
            if (projections.Count == 0)
                throw new ArgumentException("A join must project at least one output column.", nameof(projections));
            if (joinType is not (JoinType.Inner or JoinType.LeftOuter))
                throw new ArgumentOutOfRangeException(nameof(joinType));
            if (!leftIsOuter && joinType != JoinType.Inner)
            {
                throw new ArgumentException(
                    "Only INNER joins may place the SQL right table as the nested-loop outer.",
                    nameof(leftIsOuter));
            }

            var width = leftColumnCount + rightColumnCount;
            foreach (var projection in projections)
            {
                if (!projection.IsConstant && projection.ColumnIndex >= width)
                {
                    throw new ArgumentException(
                        $"Projection reads combined column {projection.ColumnIndex} of a {width}-column joined row.",
                        nameof(projections));
                }
            }

            var isLeftOuter = joinType == JoinType.LeftOuter;
            var outer = new Cursor(0);
            var inner = new Cursor(1);
            var outputBase = width;
            var flag = new Register(width + projections.Count);
            var registerCount = width + projections.Count + (isLeftOuter ? 1 : 0);
            var combinedRange = new RegisterRange(new Register(0), width);
            var outputRange = new RegisterRange(new Register(outputBase), projections.Count);

            // Cursor 0 is always the nested-loop outer driver. For INNER joins the planner may put the
            // smaller estimated table outside while still staging registers as SQL left ++ right.
            var outerTable = leftIsOuter ? leftTableName : rightTableName;
            var outerColumns = leftIsOuter ? leftColumnCount : rightColumnCount;
            var innerTable = leftIsOuter ? rightTableName : leftTableName;
            var innerColumns = leftIsOuter ? rightColumnCount : leftColumnCount;
            var ins = new List<VdbeInstruction>
            {
                new OpenReadCursorInstruction(outer, outerTable, outerColumns),
                new OpenReadCursorInstruction(inner, innerTable, innerColumns),
            };

        var rewindOuterIndex = ins.Count;
        ins.Add(new RewindCursorInstruction(outer, new ProgramCounter(0)));

        var outerLoop = ins.Count;
        if (isLeftOuter)
            ins.Add(new LoadConstantInstruction(flag, SqlValue.Integer(0)));

        var rewindInnerIndex = ins.Count;
        ins.Add(new RewindCursorInstruction(inner, new ProgramCounter(0)));

        var innerLoop = ins.Count;
                EmitCombinedColumnReads(
                    ins,
                    outer,
                    inner,
                    leftColumnCount,
                    rightColumnCount,
                    leftIsOuter);

        var filterIndex = -1;
        if (predicate is not null)
        {
            filterIndex = ins.Count;
            ins.Add(new FilterRegistersInstruction(combinedRange, predicate, new ProgramCounter(0), string.Empty));
        }

        if (isLeftOuter)
            ins.Add(new LoadConstantInstruction(flag, SqlValue.Integer(1)));

        var postJoinMatchFilterIndex = -1;
        if (postJoinPredicate is not null)
        {
            postJoinMatchFilterIndex = ins.Count;
            ins.Add(new FilterRegistersInstruction(combinedRange, postJoinPredicate, new ProgramCounter(0), string.Empty));
        }

        EmitProjection(ins, projections, outputBase);
        ins.Add(new ResultRowInstruction(outputRange));

        var nextInnerAddr = ins.Count;
        ins.Add(new NextInstruction(inner, new ProgramCounter(innerLoop)));

        var jumpIfIndex = -1;
        var noMatchAddr = -1;
        var postJoinNoMatchFilterIndex = -1;
        if (isLeftOuter)
        {
            jumpIfIndex = ins.Count;
            ins.Add(new JumpIfInstruction(flag, new ProgramCounter(0)));

            noMatchAddr = ins.Count;
            for (var i = 0; i < leftColumnCount; i++)
                ins.Add(new ColumnInstruction(outer, i, new Register(i)));
            for (var j = 0; j < rightColumnCount; j++)
                ins.Add(new LoadConstantInstruction(new Register(leftColumnCount + j), SqlValue.Null));

            if (postJoinPredicate is not null)
            {
                postJoinNoMatchFilterIndex = ins.Count;
                ins.Add(new FilterRegistersInstruction(combinedRange, postJoinPredicate, new ProgramCounter(0), string.Empty));
            }

            EmitProjection(ins, projections, outputBase);
            ins.Add(new ResultRowInstruction(outputRange));
        }

        var nextOuterAddr = ins.Count;
        ins.Add(new NextInstruction(outer, new ProgramCounter(outerLoop)));

        var closeAddr = ins.Count;
        ins.Add(new CloseCursorInstruction(inner));
        ins.Add(new CloseCursorInstruction(outer));
        ins.Add(new HaltInstruction());

        // Backpatch the forward jumps now that every target address is known.
        ins[rewindOuterIndex] = new RewindCursorInstruction(outer, new ProgramCounter(closeAddr));
        ins[rewindInnerIndex] = new RewindCursorInstruction(
            inner,
            new ProgramCounter(isLeftOuter ? noMatchAddr : nextOuterAddr));
        if (filterIndex >= 0)
        {
            ins[filterIndex] = new FilterRegistersInstruction(
                combinedRange,
                predicate!,
                new ProgramCounter(nextInnerAddr),
                $"skip pair when join predicate is false, goto {nextInnerAddr}");
        }

        if (postJoinMatchFilterIndex >= 0)
        {
            ins[postJoinMatchFilterIndex] = new FilterRegistersInstruction(
                combinedRange,
                postJoinPredicate!,
                new ProgramCounter(nextInnerAddr),
                $"skip result when post-join WHERE is false, goto {nextInnerAddr}");
        }

        if (postJoinNoMatchFilterIndex >= 0)
        {
            ins[postJoinNoMatchFilterIndex] = new FilterRegistersInstruction(
                combinedRange,
                postJoinPredicate!,
                new ProgramCounter(nextOuterAddr),
                $"skip result when post-join WHERE is false, goto {nextOuterAddr}");
        }

        if (jumpIfIndex >= 0)
            ins[jumpIfIndex] = new JumpIfInstruction(flag, new ProgramCounter(nextOuterAddr));

        return new VdbeProgram(registerCount, cursorCount: 2, ins);
    }

    // Materializes the current outer/inner pair into the combined staging block as SQL
        // left ++ right, regardless of which physical table drives the outer loop.
    private static void EmitCombinedColumnReads(
        List<VdbeInstruction> ins,
        Cursor outer,
            Cursor inner,
            int leftColumnCount,
            int rightColumnCount,
            bool leftIsOuter)
        {
            var leftCursor = leftIsOuter ? outer : inner;
            var rightCursor = leftIsOuter ? inner : outer;
            for (var i = 0; i < leftColumnCount; i++)
                ins.Add(new ColumnInstruction(leftCursor, i, new Register(i)));
            for (var j = 0; j < rightColumnCount; j++)
                ins.Add(new ColumnInstruction(rightCursor, j, new Register(leftColumnCount + j)));
        }

    // Builds the result row into the output block from the combined staging registers: column
    // outputs copy their combined-row register, constant outputs load their folded value.
    private static void EmitProjection(
        List<VdbeInstruction> ins,
        IReadOnlyList<JoinProjection> projections,
        int outputBase)
    {
        for (var o = 0; o < projections.Count; o++)
        {
            var projection = projections[o];
            var destination = new Register(outputBase + o);
            ins.Add(projection.IsConstant
                ? new LoadConstantInstruction(destination, projection.Constant)
                : new CopyInstruction(new Register(projection.ColumnIndex), destination));
        }
    }
}
