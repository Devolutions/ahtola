using Ahtola.Core.Execution;
using Ahtola.Core.Storage;

namespace Ahtola.Core.Compilation;

public sealed class StatementCompilationException : InvalidOperationException
{
    public StatementCompilationException(string message) : base(message)
    {
    }
}

/// <summary>
/// Lowers source-less projections and single-table scans into executable VDBE programs. Projection
/// expressions support constants, late-bound parameters, columns/rowid, comparisons, logical/range/list
/// operators, concatenation, nested arithmetic, CASE, and supported scalar functions; unsupported expression
/// families cause the whole statement to remain on the evaluator.
/// </summary>
internal sealed class SelectStatementCompiler
{
    private readonly Func<Expression, bool> _isConstant;
    private readonly Func<Expression, SqlValue> _fold;
    private readonly Func<TableSource, ScanTarget?> _resolveScanTarget;
    private readonly Func<Expression, ScanTarget, VdbeRowPredicate?> _compilePredicate;
    private readonly Func<Expression, ScanTarget, bool> _canEmitNativePredicate;
    private readonly Func<Expression, ScanTarget, VdbeRowIdPredicate?> _compileRowIdPredicate;
    private readonly Func<SelectStatement, ScanTarget, VdbeRowEquality?> _compileDistinctEquality;
    private readonly Func<FunctionExpression, VdbeScalarFunction?> _compileScalarFunction;
    private readonly VdbeNumericAffinity _numericAffinity;
    private readonly VdbeNumericAffinity _moduloAffinity;
    private readonly VdbeNumericAffinity _integerAffinity;

    private static string FormatOpenReadTable(ScanTarget target)
    {
        if (target.IndexName is null)
            return target.TableName;

        // Covering indexes append " COVERING" to the logical index name.
        if (target.IndexName.EndsWith(" COVERING", StringComparison.Ordinal))
        {
            var name = target.IndexName[..^" COVERING".Length];
            return $"{target.TableName} USING COVERING INDEX {name}";
        }

        if (target.AccessKind == ScanAccessKind.MultiIndexOr)
            return $"{target.TableName} USING MULTI-INDEX OR {target.IndexName}";

        if (target.AccessKind == ScanAccessKind.MultiIndexAnd)
            return $"{target.TableName} USING MULTI-INDEX AND {target.IndexName}";

        return $"{target.TableName} USING INDEX {target.IndexName}";
    }

    public SelectStatementCompiler(
        Func<Expression, bool> isConstant,
        Func<Expression, SqlValue> fold,
        Func<TableSource, ScanTarget?> resolveScanTarget,
        Func<Expression, ScanTarget, VdbeRowPredicate?> compilePredicate,
        Func<Expression, ScanTarget, bool> canEmitNativePredicate,
        Func<Expression, ScanTarget, VdbeRowIdPredicate?> compileRowIdPredicate,
        Func<SelectStatement, ScanTarget, VdbeRowEquality?> compileDistinctEquality,
        Func<FunctionExpression, VdbeScalarFunction?> compileScalarFunction,
        VdbeNumericAffinity numericAffinity,
        VdbeNumericAffinity moduloAffinity,
        VdbeNumericAffinity integerAffinity)
    {
        ArgumentNullException.ThrowIfNull(isConstant);
        ArgumentNullException.ThrowIfNull(fold);
        ArgumentNullException.ThrowIfNull(resolveScanTarget);
        ArgumentNullException.ThrowIfNull(compilePredicate);
        ArgumentNullException.ThrowIfNull(canEmitNativePredicate);
        ArgumentNullException.ThrowIfNull(compileRowIdPredicate);
        ArgumentNullException.ThrowIfNull(compileDistinctEquality);
        ArgumentNullException.ThrowIfNull(compileScalarFunction);
        ArgumentNullException.ThrowIfNull(numericAffinity);
        ArgumentNullException.ThrowIfNull(moduloAffinity);
        ArgumentNullException.ThrowIfNull(integerAffinity);
        _isConstant = isConstant;
        _fold = fold;
        _resolveScanTarget = resolveScanTarget;
        _compilePredicate = compilePredicate;
        _canEmitNativePredicate = canEmitNativePredicate;
        _compileRowIdPredicate = compileRowIdPredicate;
        _compileDistinctEquality = compileDistinctEquality;
        _compileScalarFunction = compileScalarFunction;
        _numericAffinity = numericAffinity;
        _moduloAffinity = moduloAffinity;
        _integerAffinity = integerAffinity;
    }

    public bool TryCompile(SelectStatement statement, out CompiledSelect compiled)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return statement.Source is null
            ? TryCompileSourceLess(statement, out compiled)
            : TryCompileScan(statement, out compiled);
    }

    private bool TryCompileSourceLess(SelectStatement statement, out CompiledSelect compiled)
    {
        compiled = null!;
        if (statement.Where is not null
            || statement.Having is not null
            || statement.Distinct
            || statement.GroupBy.Count != 0
            || statement.OrderBy.Count != 0
            || statement.Limit is not null
            || statement.Offset is not null
            || statement.Projections.Count == 0
            || statement.Projections.Any(projection =>
                projection.Expression is StarExpression or QualifiedStarExpression))
        {
            return false;
        }

        var outputCount = statement.Projections.Count;
        var body = new List<VdbeInstruction>();
        var emitter = CreateEmitter(target: null, cursor: null, outputCount, body, programCounterBase: 0);
        for (var index = 0; index < outputCount; index++)
        {
            if (!emitter.TryEmit(statement.Projections[index].Expression, new Register(index)))
                return false;
        }

        body.Add(new ResultRowInstruction(new RegisterRange(new Register(0), outputCount)));
        body.Add(new HaltInstruction());
        compiled = new CompiledSelect(
            new VdbeProgram(
                emitter.RegisterCount,
                cursorCount: 0,
                body,
                parameterSlotCount: emitter.ParameterIndices.Count),
            [],
            emitter.ParameterIndices);
        return true;
    }

    private bool TryCompileScan(SelectStatement statement, out CompiledSelect compiled)
    {
        compiled = null!;

        // ORDER BY elision (Turso order.rs eliminate_order_by subset):
        // Bare rowid / INTEGER PK alias:
        //   ASC  → plain Rewind/Next (physical rowid order)
        //   DESC → Last/Prev reverse scan
        // Secondary-index ORDER BY is elided by the caller stripping OrderBy after
        // materializing rows in index order (see TryCompileManagedIndexSelect).
        // WHERE/GROUP BY/HAVING/LIMIT/OFFSET/DISTINCT still block the plain scan path
        // except WHERE which is handled below as usual.
        if (TryGetBareRowidOrderBy(statement, out var rowidOrderTarget, out var rowidOrderDescending))
        {
            if (rowidOrderDescending)
                return TryCompileReverseRowidScan(statement, rowidOrderTarget, out compiled);

            // ASC: fall through into the forward-scan compiler with OrderBy elided.
        }
        else
        {
            rowidOrderTarget = null;
            rowidOrderDescending = false;
        }

        var elideOrderBy = rowidOrderTarget is not null && !rowidOrderDescending;

        if (statement.Having is not null
            || statement.GroupBy.Count != 0
            || (statement.OrderBy.Count != 0 && !elideOrderBy)
            || statement.Limit is not null
            || statement.Offset is not null
            || statement.Projections.Count == 0)
        {
            return false;
        }

        var target = _resolveScanTarget(statement.Source!);
        if (target is null)
            return false;

        VdbeRowEquality? distinctEquality = null;
        if (statement.Distinct)
        {
            distinctEquality = _compileDistinctEquality(statement, target);
            if (distinctEquality is null)
                return false;
        }

        if (!TryExpandProjections(statement.Projections, target, out var projections))
            return false;

        // P4-B: WHERE col IN (lit/param…) materializes the RHS into OpenEphemeral and
        // probes with NoConflict (Turso in_seek OpenEphemeral + membership).
        if (TryCompileInListMembership(statement, target, projections, distinctEquality, out compiled))
            return true;

        // Step 2: rowid-equality seek. When WHERE is `rowid = <int literal>` (bare rowid
        // or table-qualified, integer literal), emit a SeekRowid point-lookup instead of
        // a full scan + FilterRowId. Parameters (runtime-typed — a text-bound @p would
        // diverge: SeekRowid jumps NotFound while the scan coerces via affinity), rowid-
        // ALIAS equality (`id = N` on INTEGER PRIMARY KEY — IsTargetRowIdReference returns
        // false for declared columns), and range predicates (`>`, `BETWEEN` — Step 3)
        // stay on the scan path: still correct, just not seek-optimized. Distinct is gated
        // out (would need DistinctResultRow + a distinct set); the top-of-method guards
        // already exclude GroupBy/Having/OrderBy/Limit/Offset.
        if (!statement.Distinct
            && TryGetRowIdSeekOperand(statement.Where, target, out var rowIdValue))
        {
            var seekCursor = new Cursor(0);
            var seekBody = new List<VdbeInstruction>();
            // OpenRead(0), rhsLoad(1), SeekRowid(2), projections(3..), ResultRow, Close, Halt.
            // programCounterBase = 2 makes projection-internal jump targets line up: the
            // rhs load occupies body[0] with a phantom pc of 2 (harmless — LoadConstant has
            // no jumps), so projections start at body[1] -> pc 3, matching their actual slot
            // after the SeekRowid instruction inserted at pc 2.
            const int seekProgramCounterBase = 2;
            var seekEmitter = CreateEmitter(
                target,
                seekCursor,
                projections.Count + 1,
                seekBody,
                seekProgramCounterBase);
            var rowIdRegister = new Register(projections.Count);
            if (!seekEmitter.TryEmit(rowIdValue, rowIdRegister))
                return false;

            for (var index = 0; index < projections.Count; index++)
            {
                var projection = projections[index];
                if (projection.ColumnIndex is { } columnIndex)
                    seekBody.Add(new ColumnInstruction(seekCursor, columnIndex, new Register(index)));
                else if (!seekEmitter.TryEmit(projection.Expression!, new Register(index)))
                    return false;
            }

            if (seekBody.Count == 0 || seekBody[0] is not LoadConstantInstruction rhsLoad)
                return false;

            seekBody.RemoveAt(0);
            var projectionCount = seekBody.Count;
            var notFoundTarget = new ProgramCounter(projectionCount + 4);
            var seekInstructions = new List<VdbeInstruction>(projectionCount + 6)
            {
                new OpenReadCursorInstruction(
                    seekCursor,
                    FormatOpenReadTable(target),
                    target.Columns.Length),
                rhsLoad,
                new SeekRowidInstruction(
                    seekCursor,
                    rowIdRegister,
                    notFoundTarget,
                    $"seek cursor {seekCursor.Index} to rowid r[{rowIdRegister.Index}], goto {notFoundTarget.Offset} if not found"),
            };
            seekInstructions.AddRange(seekBody);
            seekInstructions.Add(new ResultRowInstruction(new RegisterRange(new Register(0), projections.Count)));
            seekInstructions.Add(new CloseCursorInstruction(seekCursor));
            seekInstructions.Add(new HaltInstruction());

            compiled = new CompiledSelect(
                new VdbeProgram(
                    Math.Max(seekEmitter.RegisterCount, rowIdRegister.Index + 1),
                    cursorCount: 1,
                    seekInstructions,
                    parameterSlotCount: seekEmitter.ParameterIndices.Count),
                [target.CreateCursorSource()],
                seekEmitter.ParameterIndices);
            return true;
        }

        // Step 2b: index equality SEARCH prefix. When the planner attached IndexSeek
        // (leading equality on a usable index), emit Load + SeekGE/IdxGE then residual
        // WHERE Filter + Next — not Rewind over the whole index-ordered cursor.
        if (!statement.Distinct
            && target.IndexSeek is { Bounds.Count: > 0 } indexSeek
            && indexSeek.KeyColumns.Count == indexSeek.Bounds.Count
            && statement.Where is not null)
        {
            if (TryCompileIndexEqualitySeek(statement, target, indexSeek, projections, out compiled))
                return true;
        }

        // Step 3: rowid-range seek. When WHERE is `rowid >|>=|<|<= <int literal>` (or the
        // swapped form) or `rowid BETWEEN <int literal> AND <int literal>` (non-negated),
        // emit a SeekRowidRange that lands on the first row whose rowid satisfies the start
        // predicate, followed by a FilterRowId enforcing the full WHERE over the matching
        // range, then the projection loop. Bounds must be integer literals (a text literal
        // '2' coerces via affinity on the scan path, so seeking on it would diverge; a
        // late-bound parameter is runtime-typed for the same reason). The same Distinct /
        // GroupBy / Having / OrderBy / Limit / Offset gates as Step 2 apply.
        if (!statement.Distinct
            && TryGetRowIdRangeSeekOperand(statement.Where, target, out var startBound, out var startOp, out var endBound, out var endOp))
        {
            var seekCursor = new Cursor(0);
            var seekBody = new List<VdbeInstruction>();
            // OpenRead(0), LoadConstant(start)(1), [LoadConstant(end)(2)], SeekRowidRange,
            // FilterRowId, projections.., ResultRow, Next(loopTarget=FilterRowId), Close, Halt.
            // programCounterBase = prefixCount + 2 so projection-internal jump targets line up
            // with their actual slots after the SeekRowidRange + FilterRowId instructions.
            var hasEnd = endBound is not null;
            var prefixCount = 2 + (hasEnd ? 1 : 0);
            var baseForProjections = prefixCount + 2;
            var seekEmitter = CreateEmitter(
                target,
                seekCursor,
                projections.Count + (hasEnd ? 2 : 1),
                seekBody,
                baseForProjections);
            var startRowIdRegister = new Register(projections.Count);
            Register? endRowIdRegister = hasEnd ? new Register(projections.Count + 1) : null;

            for (var index = 0; index < projections.Count; index++)
            {
                var projection = projections[index];
                if (projection.ColumnIndex is { } columnIndex)
                    seekBody.Add(new ColumnInstruction(seekCursor, columnIndex, new Register(index)));
                else if (!seekEmitter.TryEmit(projection.Expression!, new Register(index)))
                    return false;
            }

            // Fold the integer-literal bounds to longs for the FilterRowId closure. The bounds
            // are validated as SqlValueKind.Integer literals by TryGetRowIdRangeSeekOperand, so
            // no affinity/coercion path is exercised (a text '2' stays on the scan path).
            var startValue = ((LiteralExpression)startBound).Value.AsInteger();
            long? endValue = endBound is LiteralExpression endLiteral
                ? endLiteral.Value.AsInteger()
                : null;
            var capturedStartOp = startOp;
            var capturedEndOp = endOp;
            VdbeRowIdPredicate rangePredicate = (row, rowId) =>
            {
                if (!SatisfiesRowId(rowId, capturedStartOp, startValue))
                    return false;
                if (endValue is { } upper && capturedEndOp is { } upperOp
                    && !SatisfiesRowId(rowId, upperOp, upper))
                    return false;
                return true;
            };

            var rangeFilterAddr = prefixCount + 1;
            var rangeResultRowAddr = baseForProjections + seekBody.Count;
            var rangeNextAddr = rangeResultRowAddr + 1;
            var rangeCloseAddr = rangeNextAddr + 1;
            var seekInstructions = new List<VdbeInstruction>(rangeCloseAddr + 2)
            {
                new OpenReadCursorInstruction(
                    seekCursor,
                    FormatOpenReadTable(target),
                    target.Columns.Length),
                new LoadConstantInstruction(startRowIdRegister, ((LiteralExpression)startBound).Value),
            };
            if (hasEnd)
                seekInstructions.Add(new LoadConstantInstruction(endRowIdRegister!.Value, ((LiteralExpression)endBound!).Value));
            seekInstructions.Add(new SeekRowidRangeInstruction(
                seekCursor,
                startRowIdRegister,
                startOp,
                endRowIdRegister,
                endOp,
                new ProgramCounter(rangeCloseAddr),
                $"seek cursor {seekCursor.Index} to first rowid r[{startRowIdRegister.Index}] {DescribeOperator(startOp)}{DescribeBound(endOp)}"));
            seekInstructions.Add(new FilterRowIdInstruction(
                seekCursor,
                rangePredicate,
                new ProgramCounter(rangeNextAddr),
                $"skip row when range WHERE is false, goto {rangeNextAddr}"));
            seekInstructions.AddRange(seekBody);
            seekInstructions.Add(new ResultRowInstruction(new RegisterRange(new Register(0), projections.Count)));
            seekInstructions.Add(new NextInstruction(seekCursor, new ProgramCounter(rangeFilterAddr)));
            seekInstructions.Add(new CloseCursorInstruction(seekCursor));
            seekInstructions.Add(new HaltInstruction());

            compiled = new CompiledSelect(
                new VdbeProgram(
                    Math.Max(seekEmitter.RegisterCount, (hasEnd ? 2 : 1) + projections.Count),
                    cursorCount: 1,
                    seekInstructions,
                    parameterSlotCount: seekEmitter.ParameterIndices.Count),
                [target.CreateCursorSource()],
                seekEmitter.ParameterIndices);
            return true;
        }

        VdbeRowPredicate? predicate = null;
        VdbeRowIdPredicate? rowIdPredicate = null;
        Register? predicateRegister = null;
        var predicateInstructionCount = 0;
        var cursor = new Cursor(0);
        var body = new List<VdbeInstruction>();
        var nativePredicateRequested = statement.Where is not null
            && _canEmitNativePredicate(statement.Where, target);
        const int loopStart = 2;
        var bodyStart = loopStart + (statement.Where is null || nativePredicateRequested ? 0 : 1);
        var emitter = CreateEmitter(
            target,
            cursor,
            projections.Count,
            body,
            bodyStart + (nativePredicateRequested ? 1 : 0));
        if (statement.Where is not null)
        {
            if (nativePredicateRequested)
            {
                if (!emitter.CanEmitNativePredicate(statement.Where)
                    || !emitter.TryEmitPredicate(statement.Where, out var emittedPredicate))
                {
                    return false;
                }

                predicateRegister = emittedPredicate;
                predicateInstructionCount = body.Count;
            }
            else
            {
                predicate = _compilePredicate(statement.Where, target);
            }

            if (predicate is null && predicateRegister is null)
                rowIdPredicate = _compileRowIdPredicate(statement.Where, target);
            if (predicate is null && predicateRegister is null && rowIdPredicate is null)
                return false;
        }

        for (var index = 0; index < projections.Count; index++)
        {
            var projection = projections[index];
            if (projection.ColumnIndex is { } columnIndex)
            {
                body.Add(new ColumnInstruction(cursor, columnIndex, new Register(index)));
            }
            else if (!emitter.TryEmit(projection.Expression!, new Register(index)))
            {
                return false;
            }
        }

        var filterCount = bodyStart - loopStart;
        var resultRowAddr = loopStart + filterCount + body.Count + (predicateRegister is null ? 0 : 1);
        var nextAddr = resultRowAddr + 1;
        var closeAddr = nextAddr + 1;
        if (predicateRegister is { } register)
            body.Insert(
                predicateInstructionCount,
                new JumpIfNotTrueInstruction(register, new ProgramCounter(nextAddr)));
        var instructions = new List<VdbeInstruction>(closeAddr + 2)
        {
            new OpenReadCursorInstruction(
                cursor,
                FormatOpenReadTable(target),
                target.Columns.Length),
            new RewindCursorInstruction(cursor, new ProgramCounter(closeAddr)),
        };

        if (predicate is not null)
        {
            instructions.Add(new FilterInstruction(
                cursor,
                predicate,
                new ProgramCounter(nextAddr),
                $"skip row when WHERE is false, goto {nextAddr}"));
        }
        else if (rowIdPredicate is not null)
        {
            instructions.Add(new FilterRowIdInstruction(
                cursor,
                rowIdPredicate,
                new ProgramCounter(nextAddr),
                $"skip row when WHERE is false, goto {nextAddr}"));
        }

        instructions.AddRange(body);
        var output = new RegisterRange(new Register(0), projections.Count);
        instructions.Add(distinctEquality is null
            ? new ResultRowInstruction(output)
            : new DistinctResultRowInstruction(output, distinctEquality, DistinctSetIndex: 0));
        instructions.Add(new NextInstruction(cursor, new ProgramCounter(loopStart)));
        instructions.Add(new CloseCursorInstruction(cursor));
        instructions.Add(new HaltInstruction());

        compiled = new CompiledSelect(
            new VdbeProgram(
                emitter.RegisterCount,
                cursorCount: 1,
                instructions,
                distinctSetCount: distinctEquality is null ? 0 : 1,
                parameterSlotCount: emitter.ParameterIndices.Count),
            [target.CreateCursorSource()],
            emitter.ParameterIndices);
        return true;
    }

    /// <summary>
    /// Compiles an <c>ORDER BY rowid DESC</c> single-table scan as a backward table scan
    /// (<c>Last</c>/<c>Prev</c>) instead of the sorter route. Mirrors the forward-scan
    /// path of <see cref="TryCompileScan"/> but emits <c>Last</c>/<c>Prev</c> in place of
    /// <c>Rewind</c>/<c>Next</c> and carries no WHERE/predicate (the gate excludes it).
    /// </summary>
    private bool TryCompileReverseRowidScan(SelectStatement statement, ScanTarget target, out CompiledSelect compiled)
    {
        compiled = null!;
        if (!TryExpandProjections(statement.Projections, target, out var projections))
            return false;

        var cursor = new Cursor(0);
        var body = new List<VdbeInstruction>();
        const int loopStart = 2;
        var emitter = CreateEmitter(target, cursor, projections.Count, body, loopStart);

        for (var index = 0; index < projections.Count; index++)
        {
            var projection = projections[index];
            if (projection.ColumnIndex is { } columnIndex)
            {
                body.Add(new ColumnInstruction(cursor, columnIndex, new Register(index)));
            }
            else if (!emitter.TryEmit(projection.Expression!, new Register(index)))
            {
                return false;
            }
        }

        var resultRowAddr = loopStart + body.Count;
        var nextAddr = resultRowAddr + 1;
        var closeAddr = nextAddr + 1;
        var instructions = new List<VdbeInstruction>(closeAddr + 2)
        {
            new OpenReadCursorInstruction(
                cursor,
                FormatOpenReadTable(target),
                target.Columns.Length),
            new LastCursorInstruction(cursor, new ProgramCounter(closeAddr)),
        };

        instructions.AddRange(body);
        var output = new RegisterRange(new Register(0), projections.Count);
        instructions.Add(new ResultRowInstruction(output));
        instructions.Add(new PrevInstruction(cursor, new ProgramCounter(loopStart)));
        instructions.Add(new CloseCursorInstruction(cursor));
        instructions.Add(new HaltInstruction());

        compiled = new CompiledSelect(
            new VdbeProgram(
                emitter.RegisterCount,
                cursorCount: 1,
                instructions,
                distinctSetCount: 0,
                parameterSlotCount: emitter.ParameterIndices.Count),
            [target.CreateCursorSource()],
            emitter.ParameterIndices);
        return true;
    }

    /// <summary>
    /// WHERE &lt;col|rowid&gt; IN (literal/parameter…) → OpenEphemeral RHS + table scan with
    /// NoConflict membership (fall-through = member). NOT IN and mixed RHS shapes stay
    /// on the delegate/Filter path. DISTINCT uses DistinctResultRow after membership.
    /// </summary>
    private bool TryCompileInListMembership(
        SelectStatement statement,
        ScanTarget target,
        IReadOnlyList<ProjectionSource> projections,
        VdbeRowEquality? distinctEquality,
        out CompiledSelect compiled)
    {
        compiled = null!;
        if (statement.Where is not InExpression inExpr || inExpr.Negated || inExpr.Values.Count == 0)
            return false;

        var probeIsRowId = false;
        int? probeColumnIndex = null;
        switch (inExpr.Value)
        {
            case ColumnExpression column when IsTargetRowIdReference(column, target):
                probeIsRowId = true;
                break;
            case ColumnExpression column when target.ResolveColumnIndex(column.Name) is { } colIdx:
                probeColumnIndex = colIdx;
                break;
            default:
                return false;
        }

        foreach (var value in inExpr.Values)
        {
            if (value is not (LiteralExpression or ParameterExpression))
                return false;
        }

        if (statement.Distinct && distinctEquality is null)
            return false;

        var tableCursor = new Cursor(0);
        var ephCursor = new Cursor(1);
        var probeRegister = new Register(projections.Count);
        var registerFloor = projections.Count + 1;
        var insertRange = new RegisterRange(probeRegister, 1);

        // One shared instruction list + emitter so RHS and projection parameters share slots
        // and programCounterBase 0 tracks absolute PCs for any expression-internal jumps.
        var instructions = new List<VdbeInstruction>();
        var emitter = CreateEmitter(target, tableCursor, registerFloor, instructions, programCounterBase: 0);

        instructions.Add(new OpenEphemeralInstruction(ephCursor, ColumnCount: 1));
        foreach (var value in inExpr.Values)
        {
            if (!emitter.TryEmit(value, probeRegister))
                return false;
            instructions.Add(new EphemeralInsertInstruction(ephCursor, insertRange));
        }

        instructions.Add(
            new OpenReadCursorInstruction(
                tableCursor,
                FormatOpenReadTable(target),
                target.Columns.Length));

        var rewindIndex = instructions.Count;
        instructions.Add(new HaltInstruction()); // patched to Rewind

        var loopStart = instructions.Count;
        if (probeIsRowId)
            instructions.Add(new RowIdInstruction(tableCursor, probeRegister));
        else
            instructions.Add(new ColumnInstruction(tableCursor, probeColumnIndex!.Value, probeRegister));

        var noConflictIndex = instructions.Count;
        instructions.Add(new HaltInstruction()); // patched to NoConflict

        for (var index = 0; index < projections.Count; index++)
        {
            var projection = projections[index];
            if (projection.ColumnIndex is { } columnIndex)
                instructions.Add(new ColumnInstruction(tableCursor, columnIndex, new Register(index)));
            else if (!emitter.TryEmit(projection.Expression!, new Register(index)))
                return false;
        }

        var output = new RegisterRange(new Register(0), projections.Count);
        if (statement.Distinct)
        {
            instructions.Add(
                new DistinctResultRowInstruction(
                    output,
                    distinctEquality!,
                    DistinctSetIndex: 0));
        }
        else
        {
            instructions.Add(new ResultRowInstruction(output));
        }

        var nextIndex = instructions.Count;
        instructions.Add(new NextInstruction(tableCursor, new ProgramCounter(loopStart)));
        var closeTableIndex = instructions.Count;
        instructions.Add(new CloseCursorInstruction(tableCursor));
        instructions.Add(new CloseCursorInstruction(ephCursor));
        instructions.Add(new HaltInstruction());

        instructions[rewindIndex] = new RewindCursorInstruction(
            tableCursor,
            new ProgramCounter(closeTableIndex));
        instructions[noConflictIndex] = new NoConflictInstruction(
            ephCursor,
            insertRange,
            new ProgramCounter(nextIndex),
            $"skip non-member, goto {nextIndex}");

        compiled = new CompiledSelect(
            new VdbeProgram(
                emitter.RegisterCount,
                cursorCount: 2,
                instructions,
                distinctSetCount: statement.Distinct ? 1 : 0,
                parameterSlotCount: emitter.ParameterIndices.Count),
            [
                target.CreateCursorSource(),
                    // Ephemeral cursor is runtime-owned; empty placeholder keeps sources aligned.
                    new VdbeCursorSource([]),
            ],
            emitter.ParameterIndices);
        return true;
    }

    /// <summary>
    /// SEARCH equality prefix: OpenRead USING INDEX, load bounds, IdxGE/SeekGE, residual
    /// WHERE Filter, projections, Next. KeyColumns remap seeks onto table-row ordinals when
    /// the cursor holds full table rows ordered by a non-leading index key.
    /// </summary>
    private bool TryCompileIndexEqualitySeek(
    SelectStatement statement,
    ScanTarget target,
    IndexSeekPrefix indexSeek,
    IReadOnlyList<ProjectionSource> projections,
    out CompiledSelect compiled)
    {
        compiled = null!;
        var keyWidth = indexSeek.Bounds.Count;
        if (keyWidth <= 0 || indexSeek.KeyColumns.Count != keyWidth)
            return false;

        var cursor = new Cursor(0);
        var keyStartRegister = projections.Count;

        // Key loads + projection body share one emitter so parameter slots stay coherent.
        // programCounterBase is only needed for projection-internal jumps; key loads are
        // relocated to the program head, so use the post-seek Filter address as base.
        // Layout: OpenRead | keyLoads×W | SeekKey | Filter | body… | ResultRow | Next | Close | Halt
        var filterAddr = 2 + keyWidth;
        var body = new List<VdbeInstruction>();
        var emitter = CreateEmitter(
            target,
            cursor,
            projections.Count + keyWidth,
            body,
            programCounterBase: filterAddr + 1);

        var keyLoadInstructions = new List<VdbeInstruction>(keyWidth);
        for (var i = 0; i < keyWidth; i++)
        {
            var before = body.Count;
            if (!emitter.TryEmit(indexSeek.Bounds[i], new Register(keyStartRegister + i)))
                return false;
            var emitted = body.Count - before;
            if (emitted <= 0)
                return false;
            for (var j = before; j < body.Count; j++)
                keyLoadInstructions.Add(body[j]);
            body.RemoveRange(before, emitted);
        }

        var predicate = _compilePredicate(statement.Where!, target);
        VdbeRowIdPredicate? rowIdPredicate = null;
        if (predicate is null)
            rowIdPredicate = _compileRowIdPredicate(statement.Where!, target);
        if (predicate is null && rowIdPredicate is null)
            return false;

        for (var index = 0; index < projections.Count; index++)
        {
            var projection = projections[index];
            if (projection.ColumnIndex is { } columnIndex)
                body.Add(new ColumnInstruction(cursor, columnIndex, new Register(index)));
            else if (!emitter.TryEmit(projection.Expression!, new Register(index)))
                return false;
        }

        var isIndex = target.IndexName is not null;
        var keyColumns = indexSeek.KeyColumns as int[] ?? indexSeek.KeyColumns.ToArray();
        var keyRange = new RegisterRange(new Register(keyStartRegister), keyWidth);

        var instructions = new List<VdbeInstruction>(8 + keyLoadInstructions.Count + body.Count)
            {
                new OpenReadCursorInstruction(
                    cursor,
                    FormatOpenReadTable(target),
                    target.Columns.Length),
            };
        instructions.AddRange(keyLoadInstructions);

        var seekIndex = instructions.Count;
        instructions.Add(new SeekKeyInstruction(
            cursor,
            keyRange,
            VdbeKeySeekOperator.GreaterThanOrEqual,
            EqOnly: false,
            IsIndex: isIndex,
            NotFoundTarget: new ProgramCounter(0),
            Description: "seek",
            KeyColumns: keyColumns));

        var filterIndex = instructions.Count;
        if (predicate is not null)
        {
            instructions.Add(new FilterInstruction(
                cursor,
                predicate,
                new ProgramCounter(0),
                "skip row when WHERE is false"));
        }
        else
        {
            instructions.Add(new FilterRowIdInstruction(
                cursor,
                rowIdPredicate!,
                new ProgramCounter(0),
                "skip row when WHERE is false"));
        }

        var loopTarget = filterIndex;
        instructions.AddRange(body);
        instructions.Add(new ResultRowInstruction(new RegisterRange(new Register(0), projections.Count)));
        var nextIndex = instructions.Count;
        instructions.Add(new NextInstruction(cursor, new ProgramCounter(loopTarget)));
        var closeIndex = instructions.Count;
        instructions.Add(new CloseCursorInstruction(cursor));
        instructions.Add(new HaltInstruction());

        instructions[seekIndex] = ((SeekKeyInstruction)instructions[seekIndex]) with
        {
            NotFoundTarget = new ProgramCounter(closeIndex),
            Description =
                $"seek {(isIndex ? "idx" : "key")} c[{cursor.Index}] ge r[{keyStartRegister}] width {keyWidth}, goto {closeIndex} if not found",
        };

        instructions[filterIndex] = instructions[filterIndex] switch
        {
            FilterInstruction f => f with
            {
                FalseTarget = new ProgramCounter(nextIndex),
                Description = $"skip row when WHERE is false, goto {nextIndex}",
            },
            FilterRowIdInstruction f => f with
            {
                FalseTarget = new ProgramCounter(nextIndex),
                Description = $"skip row when WHERE is false, goto {nextIndex}",
            },
            _ => instructions[filterIndex],
        };

        compiled = new CompiledSelect(
            new VdbeProgram(
                Math.Max(emitter.RegisterCount, keyStartRegister + keyWidth),
                cursorCount: 1,
                instructions,
                parameterSlotCount: emitter.ParameterIndices.Count),
            [target.CreateCursorSource()],
            emitter.ParameterIndices);
        return true;
    }

    private ExpressionEmitter CreateEmitter(
        ScanTarget? target,
        Cursor? cursor,
        int outputCount,
        List<VdbeInstruction> instructions,
        int programCounterBase)
        => new(
            target,
            cursor,
            outputCount,
            instructions,
            programCounterBase,
            allowControlFlow: true,
            _isConstant,
            _fold,
            _compileScalarFunction,
            _numericAffinity,
            _moduloAffinity,
        _integerAffinity);

    internal static bool TryExpandProjections(
        IReadOnlyList<Projection> source,
        ScanTarget target,
        out List<ProjectionSource> expanded)
    {
        expanded = new List<ProjectionSource>();
        foreach (var projection in source)
        {
            switch (projection.Expression)
            {
                case StarExpression:
                    if (target.Columns.Length == 0)
                        return false;
                    for (var index = 0; index < target.Columns.Length; index++)
                        expanded.Add(ProjectionSource.ForColumn(index));
                    break;
                case QualifiedStarExpression qualified
                    when string.Equals(qualified.Qualifier, target.Qualifier, StringComparison.OrdinalIgnoreCase):
                    if (target.Columns.Length == 0)
                        return false;
                    for (var index = 0; index < target.Columns.Length; index++)
                        expanded.Add(ProjectionSource.ForColumn(index));
                    break;
                case QualifiedStarExpression:
                    return false;
                default:
                    expanded.Add(ProjectionSource.ForExpression(projection.Expression));
                    break;
            }
        }

        return expanded.Count != 0;
    }

    private static bool IsTargetRowIdReference(ColumnExpression column, ScanTarget target)
    {
        if (!target.HasRowId)
            return false;

        var separator = column.Name.IndexOf('.');
        if (separator >= 0
            && !string.Equals(
                column.Name[..separator],
                target.Qualifier,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var bareName = separator < 0 ? column.Name : column.Name[(separator + 1)..];

        // Bare rowid/_rowid_/oid that is not also a declared column name.
        if (target.ResolveColumnIndex(column.Name) is null)
            return EmbeddedTable.IsRowidAliasName(bareName);

        // INTEGER PRIMARY KEY column aliases the rowid; ORDER BY id is ORDER BY rowid.
        return IsIntegerPrimaryKeyRowidAlias(bareName, target);
    }

    /// <summary>
    /// True when <paramref name="bareName"/> is the single-column INTEGER PRIMARY KEY
    /// that aliases the table's rowid (SQLite's rowid-alias rule).
    /// </summary>
    private static bool IsIntegerPrimaryKeyRowidAlias(string bareName, ScanTarget target)
    {
        if (target.ColumnDefinitions is null || target.ColumnDefinitions.Count == 0)
            return false;

        var aliasIndex = -1;
        var primaryKeyCount = 0;
        for (var index = 0; index < target.ColumnDefinitions.Count; index++)
        {
            var definition = target.ColumnDefinitions[index];
            if (definition is null || !definition.PrimaryKey)
                continue;

            primaryKeyCount++;
            aliasIndex = index;
        }

        if (primaryKeyCount != 1 || aliasIndex < 0)
            return false;

        var alias = target.ColumnDefinitions[aliasIndex]!;
        if (alias.PrimaryKeyDescending || !EmbeddedTable.IsIntegerDeclaredType(alias.DeclaredType))
            return false;

        return string.Equals(alias.Name, bareName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(target.Columns[aliasIndex], bareName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the statement is a single-table scan whose only ordering key is
    /// bare rowid (ASC or DESC) and no other clause blocks elision.
    /// </summary>
    private bool TryGetBareRowidOrderBy(
        SelectStatement statement,
        out ScanTarget target,
        out bool descending)
    {
        target = null!;
        descending = false;
        if (statement.OrderBy.Count != 1
            || statement.Where is not null
            || statement.GroupBy.Count != 0
            || statement.Having is not null
            || statement.Limit is not null
            || statement.Offset is not null
            || statement.Distinct
            || _resolveScanTarget(statement.Source!) is not { } resolved
            || !resolved.HasRowId
            || statement.OrderBy[0].Expression is not ColumnExpression column
            || !IsTargetRowIdReference(column, resolved))
        {
            return false;
        }

        target = resolved;
        descending = statement.OrderBy[0].Descending;
        return true;
    }

    /// <summary>
    /// Detects a `rowid = integer-literal` (or `integer-literal = rowid`) equality predicate
    /// eligible for the SeekRowid point-lookup fast path. The rhs must be an integer
    /// literal known at compile time: a text literal '2' coerces to 2 via INTEGER
    /// affinity in the scan path today, so seeking on it would diverge (SeekRowid jumps
    /// NotFound while the scan matches); a late-bound parameter is runtime-typed and is
    /// left to the scan for the same reason. `IS` is treated like `=` (IS with an
    /// integer is equality).
    /// </summary>
    private static bool TryGetRowIdSeekOperand(Expression? where, ScanTarget target, out Expression rowIdValue)
    {
        rowIdValue = null!;
        if (where is not BinaryExpression { Operator: BinaryOperator.Equal or BinaryOperator.Is } binary)
            return false;

        if (binary.Left is ColumnExpression leftColumn
            && IsTargetRowIdReference(leftColumn, target)
            && binary.Right is LiteralExpression rightLiteral
            && rightLiteral.Value.Kind == SqlValueKind.Integer)
        {
            rowIdValue = binary.Right;
            return true;
        }

        if (binary.Right is ColumnExpression rightColumn
            && IsTargetRowIdReference(rightColumn, target)
            && binary.Left is LiteralExpression leftLiteral
            && leftLiteral.Value.Kind == SqlValueKind.Integer)
        {
            rowIdValue = binary.Left;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Detects a rowid range predicate eligible for the SeekRowidRange fast path:
    /// `rowid &gt;|&gt;=|&lt;|&lt;= &lt;int-literal&gt;` (or the swapped form) or
    /// `rowid BETWEEN &lt;int-literal&gt; AND &lt;int-literal&gt;` (non-negated). The
    /// bounds must be integer literals known at compile time: a text literal '2'
    /// coerces via affinity in the scan path today, so seeking on it would diverge
    /// (SeekRowidRange skips non-matching start positions while the scan coerces); a
    /// late-bound parameter is runtime-typed and stays on the scan for the same
    /// reason. Negated BETWEEN (`NOT BETWEEN`) is excluded because it is a
    /// disjunction of two open ranges, not a single contiguous range. Returns the
    /// start bound expression + operator and, for BETWEEN, the end bound + operator.
    /// </summary>
    private static bool TryGetRowIdRangeSeekOperand(
        Expression? where,
        ScanTarget target,
        out Expression startBound,
        out VdbeComparisonOperator startOp,
        out Expression? endBound,
        out VdbeComparisonOperator? endOp)
    {
        startBound = null!;
        startOp = default;
        endBound = null;
        endOp = null;

        if (where is BetweenExpression { Negated: false } between
            && between.Value is ColumnExpression betweenColumn
            && IsTargetRowIdReference(betweenColumn, target)
            && between.Lower is LiteralExpression lowerLiteral
            && lowerLiteral.Value.Kind == SqlValueKind.Integer
            && between.Upper is LiteralExpression upperLiteral
            && upperLiteral.Value.Kind == SqlValueKind.Integer)
        {
            startBound = between.Lower;
            startOp = VdbeComparisonOperator.GreaterThanOrEqual;
            endBound = between.Upper;
            endOp = VdbeComparisonOperator.LessThanOrEqual;
            return true;
        }

        if (where is not BinaryExpression
            {
                Operator: BinaryOperator.GreaterThan
                                                    or BinaryOperator.GreaterThanOrEqual
                                                    or BinaryOperator.LessThan
                                                    or BinaryOperator.LessThanOrEqual
            } binary)
            return false;

        var directOp = binary.Operator switch
        {
            BinaryOperator.GreaterThan => VdbeComparisonOperator.GreaterThan,
            BinaryOperator.GreaterThanOrEqual => VdbeComparisonOperator.GreaterThanOrEqual,
            BinaryOperator.LessThan => VdbeComparisonOperator.LessThan,
            BinaryOperator.LessThanOrEqual => VdbeComparisonOperator.LessThanOrEqual,
            _ => (VdbeComparisonOperator?)null,
        };
        if (directOp is not { } directComparison)
            return false;

        // `rowid OP literal`: the operator applies directly.
        if (binary.Left is ColumnExpression leftColumn
            && IsTargetRowIdReference(leftColumn, target)
            && binary.Right is LiteralExpression rightLiteral
            && rightLiteral.Value.Kind == SqlValueKind.Integer)
        {
            startBound = binary.Right;
            startOp = directComparison;
            return true;
        }

        // `literal OP rowid`: flip the operator so the rowid is on the left of the
        // comparison the SeekRowidRange handler evaluates (Satisfies(rowId, op, bound)).
        if (binary.Right is ColumnExpression rightColumn
            && IsTargetRowIdReference(rightColumn, target)
            && binary.Left is LiteralExpression leftLiteral
            && leftLiteral.Value.Kind == SqlValueKind.Integer)
        {
            startBound = binary.Left;
            startOp = FlipComparisonOperator(directComparison);
            return true;
        }

        return false;
    }

    private static VdbeComparisonOperator FlipComparisonOperator(VdbeComparisonOperator op)
        => op switch
        {
            VdbeComparisonOperator.GreaterThan => VdbeComparisonOperator.LessThan,
            VdbeComparisonOperator.GreaterThanOrEqual => VdbeComparisonOperator.LessThanOrEqual,
            VdbeComparisonOperator.LessThan => VdbeComparisonOperator.GreaterThan,
            VdbeComparisonOperator.LessThanOrEqual => VdbeComparisonOperator.GreaterThanOrEqual,
            VdbeComparisonOperator.Equal => VdbeComparisonOperator.Equal,
            VdbeComparisonOperator.NotEqual => VdbeComparisonOperator.NotEqual,
            VdbeComparisonOperator.Is => VdbeComparisonOperator.Is,
            VdbeComparisonOperator.IsNot => VdbeComparisonOperator.IsNot,
            _ => op,
        };

    // Whether a long rowid satisfies the supplied comparison against a bound. Mirrors the
    // runtime Satisfies helper in ResumableStatement so the compiler's FilterRowId closure
    // for the range-seek path uses the same semantics as the SeekRowidRange start search.
    private static bool SatisfiesRowId(long rowId, VdbeComparisonOperator op, long bound)
    {
        return op switch
        {
            VdbeComparisonOperator.GreaterThan => rowId > bound,
            VdbeComparisonOperator.GreaterThanOrEqual => rowId >= bound,
            VdbeComparisonOperator.LessThan => rowId < bound,
            VdbeComparisonOperator.LessThanOrEqual => rowId <= bound,
            VdbeComparisonOperator.Equal => rowId == bound,
            VdbeComparisonOperator.NotEqual => rowId != bound,
            VdbeComparisonOperator.Is => rowId == bound,
            VdbeComparisonOperator.IsNot => rowId != bound,
            _ => false,
        };
    }

    private static string DescribeOperator(VdbeComparisonOperator? op)
        => op switch
        {
            VdbeComparisonOperator.GreaterThan => ">",
            VdbeComparisonOperator.GreaterThanOrEqual => ">=",
            VdbeComparisonOperator.LessThan => "<",
            VdbeComparisonOperator.LessThanOrEqual => "<=",
            VdbeComparisonOperator.Equal => "=",
            VdbeComparisonOperator.NotEqual => "!=",
            VdbeComparisonOperator.Is => "IS",
            VdbeComparisonOperator.IsNot => "IS NOT",
            null => string.Empty,
            _ => string.Empty,
        };

    private static string DescribeBound(VdbeComparisonOperator? endOp)
        => endOp is null ? string.Empty : $"..{DescribeOperator(endOp)}";

    internal readonly record struct ProjectionSource(Expression? Expression, int? ColumnIndex)
    {
        public static ProjectionSource ForExpression(Expression expression) => new(expression, null);

        public static ProjectionSource ForColumn(int columnIndex) => new(null, columnIndex);
    }

    internal sealed class ExpressionEmitter
    {
        // These fixed value leaves intentionally reuse Function: it already snapshots register arguments,
        // validates arity, and exposes the operation name to EXPLAIN without widening the opcode contract.
        private static readonly VdbeScalarFunction NotOperation = new()
        {
            Name = "not",
            Arity = 1,
            Invoke = values => VdbeValueOperations.Not(values[0]),
        };
        private static readonly VdbeScalarFunction IsTrueOperation = TruthOperation(
            "is_true",
            nullValue: false,
            invert: false);
        private static readonly VdbeScalarFunction IsFalseOperation = TruthOperation(
            "is_false",
            nullValue: false,
            invert: true);
        private static readonly VdbeScalarFunction IsNotTrueOperation = TruthOperation(
            "is_not_true",
            nullValue: true,
            invert: true);
        private static readonly VdbeScalarFunction IsNotFalseOperation = TruthOperation(
            "is_not_false",
            nullValue: true,
            invert: false);
        private static readonly VdbeScalarFunction ConcatOperation = new()
        {
            Name = "concat",
            Arity = 2,
            Invoke = values => VdbeValueOperations.Concat(values[0], values[1]),
        };
        private static readonly VdbeScalarFunction AndOperation = new()
        {
            Name = "and",
            Arity = 2,
            Invoke = values => VdbeValueOperations.And(values[0], values[1]),
        };
        private static readonly VdbeScalarFunction OrOperation = new()
        {
            Name = "or",
            Arity = 2,
            Invoke = values => VdbeValueOperations.Or(values[0], values[1]),
        };

        private readonly ScanTarget? _target;
        private readonly Cursor? _cursor;
        private readonly List<VdbeInstruction> _instructions;
        private readonly int _programCounterBase;
        private readonly bool _allowControlFlow;
        private readonly Func<Expression, bool> _isConstant;
        private readonly Func<Expression, SqlValue> _fold;
        private readonly Func<FunctionExpression, VdbeScalarFunction?> _compileScalarFunction;
        private readonly VdbeNumericAffinity _numericAffinity;
        private readonly VdbeNumericAffinity _moduloAffinity;
        private readonly VdbeNumericAffinity _integerAffinity;
        private readonly Dictionary<int, int> _parameterSlots = [];
        private readonly List<int> _parameterIndices = [];
        private int _nextRegister;
        private int _constantFoldingSuppression;

        public ExpressionEmitter(
            ScanTarget? target,
            Cursor? cursor,
            int firstScratchRegister,
            List<VdbeInstruction> instructions,
            int programCounterBase,
            bool allowControlFlow,
            Func<Expression, bool> isConstant,
            Func<Expression, SqlValue> fold,
            Func<FunctionExpression, VdbeScalarFunction?> compileScalarFunction,
            VdbeNumericAffinity numericAffinity,
            VdbeNumericAffinity moduloAffinity,
            VdbeNumericAffinity integerAffinity)
        {
            _target = target;
            _cursor = cursor;
            _nextRegister = firstScratchRegister;
            _instructions = instructions;
            _programCounterBase = programCounterBase;
            _allowControlFlow = allowControlFlow;
            _isConstant = isConstant;
            _fold = fold;
            _compileScalarFunction = compileScalarFunction;
            _numericAffinity = numericAffinity;
            _moduloAffinity = moduloAffinity;
            _integerAffinity = integerAffinity;
        }

        public int RegisterCount => _nextRegister;

        public IReadOnlyList<int> ParameterIndices => _parameterIndices;

        public bool CanEmitNativePredicate(Expression expression)
            => expression is BinaryExpression binary && TryGetComparisonMetadata(binary, out _);

        public bool TryEmitPredicate(Expression expression, out Register destination)
        {
            destination = new Register(_nextRegister++);
            return TryEmitComparison(expression, destination);
        }

        public bool TryEmit(Expression expression, Register destination)
        {
            if (_constantFoldingSuppression == 0 && _isConstant(expression))
            {
                _instructions.Add(new LoadConstantInstruction(destination, _fold(expression)));
                return true;
            }

            switch (expression)
            {
                case LiteralExpression literal:
                    _instructions.Add(new LoadConstantInstruction(destination, literal.Value));
                    return true;
                case ParameterExpression parameter:
                    _instructions.Add(new LoadParameterInstruction(
                        destination,
                        new ParameterSlot(GetParameterSlot(parameter.Index))));
                    return true;
                case ColumnExpression column when _target is not null && _cursor is not null:
                    if (_target.ResolveColumnIndex(column.Name) is { } columnIndex)
                    {
                        _instructions.Add(new ColumnInstruction(_cursor.Value, columnIndex, destination));
                        return true;
                    }

                    if (IsTargetRowIdReference(column, _target))
                    {
                        _instructions.Add(new RowIdInstruction(_cursor.Value, destination));
                        return true;
                    }

                    return false;
                case BinaryExpression binary when TryMapArithmeticOperator(binary.Operator, out var arithmetic):
                    var operands = Allocate(2);
                    if (!TryEmit(binary.Left, operands.Start)
                        || !TryEmit(binary.Right, new Register(operands.Start.Index + 1)))
                    {
                        return false;
                    }

                    var affinity = GetAffinity(arithmetic);
                    _instructions.Add(new NumericAffinityInstruction(operands.Start, affinity));
                    _instructions.Add(new NumericAffinityInstruction(
                        new Register(operands.Start.Index + 1),
                        affinity));
                    _instructions.Add(new ArithmeticInstruction(destination, arithmetic, operands));
                    return true;
                case BinaryExpression binary when TryMapComparisonOperator(binary.Operator, out _):
                    return TryEmitComparison(binary, destination);
                case BinaryExpression { Operator: BinaryOperator.Concatenate } concat:
                    var concatOperands = Allocate(2);
                    if (!TryEmit(concat.Left, concatOperands.Start)
                        || !TryEmit(concat.Right, new Register(concatOperands.Start.Index + 1)))
                    {
                        return false;
                    }

                    _instructions.Add(new FunctionInstruction(destination, ConcatOperation, concatOperands));
                    return true;
                case BinaryExpression
                {
                    Operator: BinaryOperator.And or BinaryOperator.Or,
                } logical:
                    return TryEmitLogical(logical, destination);
                case UnaryExpression unary when TryMapArithmeticOperator(unary.Operator, out var unaryArithmetic):
                    var operand = Allocate(1);
                    if (!TryEmit(unary.Operand, operand.Start))
                        return false;

                    if (unaryArithmetic != ArithmeticOperator.Identity)
                        _instructions.Add(new NumericAffinityInstruction(operand.Start, GetAffinity(unaryArithmetic)));
                    _instructions.Add(new ArithmeticInstruction(destination, unaryArithmetic, operand));
                    return true;
                case UnaryExpression { Operator: UnaryOperator.Not } not:
                    var notOperand = Allocate(1).Start;
                    if (!TryEmit(not.Operand, notOperand))
                        return false;

                    _instructions.Add(new FunctionInstruction(
                        destination,
                        NotOperation,
                        new RegisterRange(notOperand, 1)));
                    return true;
                case BetweenExpression between:
                    return TryEmitBetween(between, destination);
                case InExpression @in:
                    return TryEmitInList(@in, destination);
                case LikeExpression like:
                    return TryEmitLike(like, destination);
                case GlobExpression glob:
                    return TryEmitGlob(glob, destination);
                case CastExpression cast:
                    if (!TryEmit(cast.Expression, destination))
                        return false;

                    _instructions.Add(new CastInstruction(destination, cast.TypeName));
                    return true;
                case CaseExpression @case when _allowControlFlow:
                    return TryEmitCase(@case, destination);
                case FunctionExpression function:
                    var scalar = _compileScalarFunction(function);
                    if (scalar is null)
                        return false;

                    var arguments = Allocate(function.Arguments.Count);
                    for (var index = 0; index < function.Arguments.Count; index++)
                    {
                        if (!TryEmit(
                                function.Arguments[index],
                                new Register(arguments.Start.Index + index)))
                        {
                            return false;
                        }
                    }

                    _instructions.Add(new FunctionInstruction(destination, scalar, arguments));
                    return true;
                case CollationExpression collation:
                    return TryEmit(collation.Expression, destination);
                default:
                    return false;
            }
        }

        private bool TryEmitComparison(Expression expression, Register destination)
        {
            if (expression is not BinaryExpression binary
                || !TryGetComparisonMetadata(binary, out var comparison))
            {
                return false;
            }

            if (TryGetTruthTest(binary, out var expected))
            {
                var operand = Allocate(1).Start;
                if (!TryEmit(binary.Left, operand))
                    return false;

                var isNot = binary.Operator == BinaryOperator.IsNot;
                _instructions.Add(new FunctionInstruction(
                    destination,
                    GetTruthOperation(expected, isNot),
                    new RegisterRange(operand, 1)));
                return true;
            }

            var operands = Allocate(2);
            if (!TryEmit(binary.Left, operands.Start)
                || !TryEmit(binary.Right, new Register(operands.Start.Index + 1)))
            {
                return false;
            }

            _instructions.Add(new CompareInstruction(
                destination,
                comparison.Operator,
                operands.Start,
                new Register(operands.Start.Index + 1),
                comparison.LeftAffinity,
                comparison.RightAffinity,
                comparison.Collation));
            return true;
        }

        private bool TryEmitLogical(BinaryExpression expression, Register destination)
        {
            if (!_allowControlFlow)
                return false;

            var operands = Allocate(2);
            var left = operands.Start;
            if (!TryEmit(expression.Left, left))
                return false;

            var shortCircuit = Allocate(1).Start;
            var isAnd = expression.Operator == BinaryOperator.And;
            _instructions.Add(new FunctionInstruction(
                shortCircuit,
                isAnd ? IsFalseOperation : IsTrueOperation,
                new RegisterRange(left, 1)));
            var shortCircuitJump = _instructions.Count;
            _instructions.Add(new JumpIfInstruction(shortCircuit, new ProgramCounter(0)));

            var right = new Register(left.Index + 1);
            _constantFoldingSuppression++;
            try
            {
                if (!TryEmit(expression.Right, right))
                    return false;
            }
            finally
            {
                _constantFoldingSuppression--;
            }

            _instructions.Add(new FunctionInstruction(
                destination,
                isAnd ? AndOperation : OrOperation,
                operands));
            var endJump = _instructions.Count;
            _instructions.Add(new GotoInstruction(new ProgramCounter(0)));

            _instructions[shortCircuitJump] = new JumpIfInstruction(shortCircuit, CurrentProgramCounter());
            _instructions.Add(new LoadConstantInstruction(
                destination,
                SqlValue.Integer(isAnd ? 0 : 1)));
            _instructions[endJump] = new GotoInstruction(CurrentProgramCounter());
            return true;
        }

        private bool TryEmitBetween(BetweenExpression expression, Register destination)
        {
            var lowerComparison = new BinaryExpression(
                expression.Value,
                BinaryOperator.GreaterThanOrEqual,
                expression.Lower);
            var upperComparison = new BinaryExpression(
                expression.Value,
                BinaryOperator.LessThanOrEqual,
                expression.Upper);
            if (!TryGetComparisonMetadata(lowerComparison, out var lowerMetadata)
                || !TryGetComparisonMetadata(upperComparison, out var upperMetadata))
            {
                return false;
            }

            var operands = Allocate(3);
            var value = operands.Start;
            var lower = new Register(value.Index + 1);
            var upper = new Register(value.Index + 2);
            if (!TryEmit(expression.Value, value)
                || !TryEmit(expression.Lower, lower)
                || !TryEmit(expression.Upper, upper))
            {
                return false;
            }

            var comparisons = Allocate(2);
            var lowerResult = comparisons.Start;
            var upperResult = new Register(lowerResult.Index + 1);
            _instructions.Add(CreateComparison(lowerResult, value, lower, lowerMetadata));
            _instructions.Add(CreateComparison(upperResult, value, upper, upperMetadata));
            _instructions.Add(new FunctionInstruction(destination, AndOperation, comparisons));
            if (expression.Negated)
            {
                _instructions.Add(new FunctionInstruction(
                    destination,
                    NotOperation,
                    new RegisterRange(destination, 1)));
            }
            return true;
        }

        private bool TryEmitInList(InExpression expression, Register destination)
        {
            if (expression.Values.Count == 0)
            {
                _instructions.Add(new LoadConstantInstruction(
                    destination,
                    SqlValue.Integer(expression.Negated ? 1 : 0)));
                return true;
            }
            if (!_allowControlFlow)
                return false;

            var collation = GetExplicitCollation(expression.Value)
                ?? GetDeclaredCollation(expression.Value);
            if (!SqliteIndexRecordComparer.IsSupportedCollation(collation))
                return false;

            var value = Allocate(1).Start;
            if (!TryEmit(expression.Value, value))
                return false;

            _instructions.Add(new LoadConstantInstruction(destination, SqlValue.Integer(0)));
            var matchJumps = new List<int>(expression.Values.Count);
            _constantFoldingSuppression++;
            try
            {
                foreach (var candidateExpression in expression.Values)
                {
                    var candidate = Allocate(1).Start;
                    var comparison = Allocate(1).Start;
                    if (!TryEmit(candidateExpression, candidate))
                        return false;

                    _instructions.Add(new CompareInstruction(
                        comparison,
                        VdbeComparisonOperator.Equal,
                        value,
                        candidate,
                        GetDeclaredAffinity(expression.Value),
                        RightAffinity: null,
                        collation));
                    var logicalOperands = Allocate(2);
                    _instructions.Add(new CopyInstruction(destination, logicalOperands.Start));
                    _instructions.Add(new CopyInstruction(
                        comparison,
                        new Register(logicalOperands.Start.Index + 1)));
                    _instructions.Add(new FunctionInstruction(destination, OrOperation, logicalOperands));
                    matchJumps.Add(_instructions.Count);
                    _instructions.Add(new JumpIfInstruction(destination, new ProgramCounter(0)));
                }
            }
            finally
            {
                _constantFoldingSuppression--;
            }

            var matchTarget = CurrentProgramCounter();
            foreach (var jump in matchJumps)
                _instructions[jump] = new JumpIfInstruction(destination, matchTarget);
            if (expression.Negated)
            {
                _instructions.Add(new FunctionInstruction(
                    destination,
                    NotOperation,
                    new RegisterRange(destination, 1)));
            }
            return true;
        }

        private bool TryEmitLike(LikeExpression expression, Register destination)
        {
            var argumentCount = expression.Escape is null ? 2 : 3;
            var arguments = Allocate(argumentCount);
            if (!TryEmit(expression.Value, arguments.Start)
                || !TryEmit(expression.Pattern, new Register(arguments.Start.Index + 1))
                || (expression.Escape is not null
                    && !TryEmit(expression.Escape, new Register(arguments.Start.Index + 2))))
            {
                return false;
            }

            var fold = _fold;
            var negated = expression.Negated;
            _instructions.Add(new FunctionInstruction(
                destination,
                new VdbeScalarFunction
                {
                    Name = negated ? "not_like" : "like",
                    Arity = argumentCount,
                    Invoke = values => fold(new LikeExpression(
                        new LiteralExpression(values[0]),
                        new LiteralExpression(values[1]),
                        values.Length == 3 ? new LiteralExpression(values[2]) : null,
                        negated)),
                },
                arguments));
            return true;
        }

        private bool TryEmitGlob(GlobExpression expression, Register destination)
        {
            var arguments = Allocate(2);
            if (!TryEmit(expression.Value, arguments.Start)
                || !TryEmit(expression.Pattern, new Register(arguments.Start.Index + 1)))
            {
                return false;
            }

            var fold = _fold;
            var negated = expression.Negated;
            _instructions.Add(new FunctionInstruction(
                destination,
                new VdbeScalarFunction
                {
                    Name = negated ? "not_glob" : "glob",
                    Arity = 2,
                    Invoke = values => fold(new GlobExpression(
                        new LiteralExpression(values[0]),
                        new LiteralExpression(values[1]),
                        negated)),
                },
                arguments));
            return true;
        }

        private static VdbeScalarFunction GetTruthOperation(bool expected, bool isNot)
            => (expected, isNot) switch
            {
                (true, false) => IsTrueOperation,
                (false, false) => IsFalseOperation,
                (true, true) => IsNotTrueOperation,
                (false, true) => IsNotFalseOperation,
            };

        private static VdbeScalarFunction TruthOperation(string name, bool nullValue, bool invert)
            => new()
            {
                Name = name,
                Arity = 1,
                Invoke = values => VdbeValueOperations.IsTrue(values[0], nullValue, invert),
            };

        private bool TryGetTruthTest(BinaryExpression expression, out bool expected)
        {
            if (expression.Operator is BinaryOperator.Is or BinaryOperator.IsNot
                && UnwrapCollation(expression.Right) is ColumnExpression
                {
                    BooleanKeyword: { } keyword,
                } column
                && (_target is null || _target.ResolveColumnIndex(column.Name) is null))
            {
                expected = keyword;
                return true;
            }

            expected = false;
            return false;
        }

        private static CompareInstruction CreateComparison(
            Register destination,
            Register left,
            Register right,
            VdbeComparisonMetadata metadata)
            => new(
                destination,
                metadata.Operator,
                left,
                right,
                metadata.LeftAffinity,
                metadata.RightAffinity,
                metadata.Collation);

        private bool TryEmitCase(CaseExpression expression, Register destination)
            => expression.Operand is null
                ? TryEmitSearchedCase(expression, destination)
                : TryEmitSimpleCase(expression, destination);

        private bool TryEmitSearchedCase(CaseExpression expression, Register destination)
        {
            if (expression.Clauses.Count == 0)
                return false;

            var endJumps = new List<int>(expression.Clauses.Count);
            _constantFoldingSuppression++;
            try
            {
                foreach (var clause in expression.Clauses)
                {
                    var condition = Allocate(1).Start;
                    if (!TryEmit(clause.When, condition))
                        return false;

                    var skipThenIndex = _instructions.Count;
                    _instructions.Add(new JumpIfNotTrueInstruction(condition, new ProgramCounter(0)));
                    if (!TryEmit(clause.Then, destination))
                        return false;

                    endJumps.Add(_instructions.Count);
                    _instructions.Add(new GotoInstruction(new ProgramCounter(0)));
                    _instructions[skipThenIndex] = new JumpIfNotTrueInstruction(
                        condition,
                        CurrentProgramCounter());
                }

                if (expression.Else is null)
                    _instructions.Add(new LoadConstantInstruction(destination, SqlValue.Null));
                else if (!TryEmit(expression.Else, destination))
                    return false;

                var end = CurrentProgramCounter();
                foreach (var jumpIndex in endJumps)
                    _instructions[jumpIndex] = new GotoInstruction(end);
                return true;
            }
            finally
            {
                _constantFoldingSuppression--;
            }
        }

        private bool TryEmitSimpleCase(CaseExpression expression, Register destination)
        {
            if (expression.Operand is null || expression.Clauses.Count == 0)
                return false;

            var endJumps = new List<int>(expression.Clauses.Count);
            _constantFoldingSuppression++;
            try
            {
                var operand = Allocate(1).Start;
                if (!TryEmit(expression.Operand, operand))
                    return false;

                foreach (var clause in expression.Clauses)
                {
                    var comparisonExpression = new BinaryExpression(
                        expression.Operand,
                        BinaryOperator.Equal,
                        clause.When);
                    if (!TryGetComparisonMetadata(comparisonExpression, out var comparisonMetadata))
                        return false;

                    var when = Allocate(1).Start;
                    var condition = Allocate(1).Start;
                    if (!TryEmit(clause.When, when))
                        return false;

                    _instructions.Add(CreateComparison(condition, operand, when, comparisonMetadata));
                    var skipThenIndex = _instructions.Count;
                    _instructions.Add(new JumpIfNotTrueInstruction(condition, new ProgramCounter(0)));
                    if (!TryEmit(clause.Then, destination))
                        return false;

                    endJumps.Add(_instructions.Count);
                    _instructions.Add(new GotoInstruction(new ProgramCounter(0)));
                    _instructions[skipThenIndex] = new JumpIfNotTrueInstruction(
                        condition,
                        CurrentProgramCounter());
                }

                if (expression.Else is null)
                    _instructions.Add(new LoadConstantInstruction(destination, SqlValue.Null));
                else if (!TryEmit(expression.Else, destination))
                    return false;

                var end = CurrentProgramCounter();
                foreach (var jumpIndex in endJumps)
                    _instructions[jumpIndex] = new GotoInstruction(end);
                return true;
            }
            finally
            {
                _constantFoldingSuppression--;
            }
        }

        private ProgramCounter CurrentProgramCounter()
            => new(_programCounterBase + _instructions.Count);

        private RegisterRange Allocate(int count)
        {
            var start = new Register(_nextRegister);
            _nextRegister += count;
            return new RegisterRange(start, count);
        }

        private int GetParameterSlot(int parameterIndex)
        {
            if (_parameterSlots.TryGetValue(parameterIndex, out var slot))
                return slot;

            slot = _parameterIndices.Count;
            _parameterSlots.Add(parameterIndex, slot);
            _parameterIndices.Add(parameterIndex);
            return slot;
        }

        private bool TryGetComparisonMetadata(
            BinaryExpression expression,
            out VdbeComparisonMetadata metadata)
        {
            metadata = null!;
            if (!TryMapComparisonOperator(expression.Operator, out var operation))
                return false;

            var collation = GetExplicitCollation(expression.Left)
                ?? GetExplicitCollation(expression.Right)
                ?? GetDeclaredCollation(expression.Left)
                ?? GetDeclaredCollation(expression.Right);
            if (!SqliteIndexRecordComparer.IsSupportedCollation(collation))
                return false;

            metadata = new VdbeComparisonMetadata(
                operation,
                GetDeclaredAffinity(expression.Left),
                GetDeclaredAffinity(expression.Right),
                collation);
            return true;
        }

        private VdbeValueAffinity? GetDeclaredAffinity(Expression expression)
        {
            var column = UnwrapCollation(expression) as ColumnExpression;
            if (column is null || _target?.ColumnDefinitions is null)
                return null;

            var index = _target.ResolveColumnIndex(column.Name);
            if (index is null || index.Value >= _target.ColumnDefinitions.Count)
                return null;

            return _target.ColumnDefinitions[index.Value] is { StrictAny: false } definition
                ? ToVdbeAffinity(EmbeddedTable.GetDeclaredColumnAffinity(definition))
                : null;
        }

        private string? GetDeclaredCollation(Expression expression)
        {
            var column = UnwrapCollation(expression) as ColumnExpression;
            if (column is null || _target?.ColumnDefinitions is null)
                return null;

            var index = _target.ResolveColumnIndex(column.Name);
            return index is { } value && value < _target.ColumnDefinitions.Count
                ? _target.ColumnDefinitions[value]?.Collation
                : null;
        }

        private static Expression UnwrapCollation(Expression expression)
        {
            while (expression is CollationExpression collation)
                expression = collation.Expression;
            return expression;
        }

        private static string? GetExplicitCollation(Expression expression)
        {
            string? collation = null;
            while (expression is CollationExpression wrapper)
            {
                collation ??= wrapper.Name;
                expression = wrapper.Expression;
            }

            return collation;
        }

        private static VdbeValueAffinity ToVdbeAffinity(ColumnAffinity affinity)
            => affinity switch
            {
                ColumnAffinity.Blob => VdbeValueAffinity.Blob,
                ColumnAffinity.Text => VdbeValueAffinity.Text,
                ColumnAffinity.Numeric => VdbeValueAffinity.Numeric,
                ColumnAffinity.Integer => VdbeValueAffinity.Integer,
                ColumnAffinity.Real => VdbeValueAffinity.Real,
                _ => throw new InvalidOperationException($"Unknown column affinity {affinity}."),
            };

        private static bool TryMapArithmeticOperator(BinaryOperator op, out ArithmeticOperator arithmetic)
        {
            switch (op)
            {
                case BinaryOperator.Add:
                    arithmetic = ArithmeticOperator.Add;
                    return true;
                case BinaryOperator.Subtract:
                    arithmetic = ArithmeticOperator.Subtract;
                    return true;
                case BinaryOperator.Multiply:
                    arithmetic = ArithmeticOperator.Multiply;
                    return true;
                case BinaryOperator.Divide:
                    arithmetic = ArithmeticOperator.Divide;
                    return true;
                case BinaryOperator.Modulo:
                    arithmetic = ArithmeticOperator.Modulo;
                    return true;
                case BinaryOperator.BitwiseAnd:
                    arithmetic = ArithmeticOperator.BitwiseAnd;
                    return true;
                case BinaryOperator.BitwiseOr:
                    arithmetic = ArithmeticOperator.BitwiseOr;
                    return true;
                case BinaryOperator.ShiftLeft:
                    arithmetic = ArithmeticOperator.ShiftLeft;
                    return true;
                case BinaryOperator.ShiftRight:
                    arithmetic = ArithmeticOperator.ShiftRight;
                    return true;
                default:
                    arithmetic = default;
                    return false;
            }
        }

        private static bool TryMapArithmeticOperator(UnaryOperator op, out ArithmeticOperator arithmetic)
        {
            switch (op)
            {
                case UnaryOperator.Plus:
                    arithmetic = ArithmeticOperator.Identity;
                    return true;
                case UnaryOperator.Negate:
                    arithmetic = ArithmeticOperator.Negate;
                    return true;
                case UnaryOperator.BitwiseNot:
                    arithmetic = ArithmeticOperator.BitwiseNot;
                    return true;
                default:
                    arithmetic = default;
                    return false;
            }
        }

        private static bool TryMapComparisonOperator(
            BinaryOperator op,
            out VdbeComparisonOperator comparison)
        {
            switch (op)
            {
                case BinaryOperator.Is:
                    comparison = VdbeComparisonOperator.Is;
                    return true;
                case BinaryOperator.IsNot:
                    comparison = VdbeComparisonOperator.IsNot;
                    return true;
                case BinaryOperator.Equal:
                    comparison = VdbeComparisonOperator.Equal;
                    return true;
                case BinaryOperator.NotEqual:
                    comparison = VdbeComparisonOperator.NotEqual;
                    return true;
                case BinaryOperator.LessThan:
                    comparison = VdbeComparisonOperator.LessThan;
                    return true;
                case BinaryOperator.LessThanOrEqual:
                    comparison = VdbeComparisonOperator.LessThanOrEqual;
                    return true;
                case BinaryOperator.GreaterThan:
                    comparison = VdbeComparisonOperator.GreaterThan;
                    return true;
                case BinaryOperator.GreaterThanOrEqual:
                    comparison = VdbeComparisonOperator.GreaterThanOrEqual;
                    return true;
                default:
                    comparison = default;
                    return false;
            }
        }

        private VdbeNumericAffinity GetAffinity(ArithmeticOperator op)
        {
            return op switch
            {
                ArithmeticOperator.Modulo => _moduloAffinity,
                ArithmeticOperator.BitwiseAnd
                    or ArithmeticOperator.BitwiseOr
                    or ArithmeticOperator.ShiftLeft
                    or ArithmeticOperator.ShiftRight
                    or ArithmeticOperator.BitwiseNot => _integerAffinity,
                _ => _numericAffinity,
            };
        }

        private sealed record VdbeComparisonMetadata(
            VdbeComparisonOperator Operator,
            VdbeValueAffinity? LeftAffinity,
            VdbeValueAffinity? RightAffinity,
            string? Collation);
    }
}
