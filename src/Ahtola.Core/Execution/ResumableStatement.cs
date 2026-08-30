using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.ExceptionServices;
using Ahtola.Core.Parsing;
using Ahtola.Core.Storage;

namespace Ahtola.Core.Execution;

public enum ResumableStatementState
{
    Ready,
    Row,
    Yielded,
    Done,
    Disposed,
    Faulted,
}

public enum ResumableStatementStepResult
{
    Row,
    Done,
    Yielded,
}

public sealed class StatementYieldedException : InvalidOperationException
{
    public StatementYieldedException()
        : base("Statement yielded. Call Resume before stepping again.")
    {
    }
}

public sealed class ResumableStatement : IDisposable
{
    private readonly SqlValue[] _registers;
    private readonly bool[] _openCursors;
    private readonly int[] _cursorPositions;
    private readonly bool[] _skipLastInsertRowId;
    private readonly SqlValue[]?[] _materializedRows;
    private readonly long[] _materializedRowIds;
    private readonly JoinCursorState?[] _joinCursorStates;
    private readonly SorterRuntime?[] _sorters;
    private readonly object?[] _accumulatorContexts;
    private readonly bool[] _accumulatorInitialized;
    private readonly List<SqlValue[]>?[] _distinctSets;
    private readonly Dictionary<SqlValue[], int>?[] _groupIndexes;
    private readonly int[] _rowSetPositions;
    private readonly Dictionary<int, IntegerRowSet> _integerRowSets = [];
    private readonly Dictionary<int, ResumableStatement> _subprogramStatements = [];
    private readonly WorkTableRuntime?[] _workTables;
    private readonly WindowBufferRuntime?[] _windowBuffers;
    private readonly EphemeralTableRuntime?[] _ephemeralTables;
    private readonly ManagedVirtualTableCursor?[] _virtualCursors;
    private readonly Indexing.ManagedIndexMethodCursor?[] _indexMethodCursors;
    private readonly IReadOnlyList<VdbeCursorSource?>? _cursorSources;
    private readonly IReadOnlyList<VdbeWriteTarget?>? _writeTargets;
    private readonly IReadOnlyList<VdbeVirtualTableBinding?>? _virtualTableBindings;
    private readonly VdbeExecutionOptions _executionOptions;
    private readonly VdbeExecutionMemory _memory;
    private readonly VdbeTransactionContext _transaction;
    private readonly bool _ownsTransaction;
    private VdbeParameterBinding? _binding;
    private ProgramCounter _instructionPointer;
    private ReadOnlyCollection<SqlValue>? _currentRow;
    private int _fkImmediateViolations;
    private int _fkDeferredViolations;
    private bool _hasExecutedInstruction;
    private bool _disposed;

    public ResumableStatement(
        VdbeProgram program,
        IReadOnlyList<VdbeCursorSource?>? cursorSources = null,
        IReadOnlyList<VdbeWriteTarget?>? writeTargets = null,
        VdbeParameterBinding? parameterBinding = null,
        VdbeTransactionContext? sharedTransaction = null,
        IReadOnlyList<VdbeVirtualTableBinding?>? virtualTableBindings = null)
        : this(
            program,
            cursorSources,
            writeTargets,
            parameterBinding,
            sharedTransaction,
            virtualTableBindings,
            VdbeExecutionOptions.Default)
    {
    }

    /// <summary>
    /// Creates a statement with explicit temporary execution resources without changing
    /// the legacy constructor's positional parameter contract.
    /// </summary>
    public static ResumableStatement CreateWithExecutionOptions(
        VdbeProgram program,
        VdbeExecutionOptions executionOptions,
        IReadOnlyList<VdbeCursorSource?>? cursorSources = null,
        IReadOnlyList<VdbeWriteTarget?>? writeTargets = null,
        VdbeParameterBinding? parameterBinding = null,
        VdbeTransactionContext? sharedTransaction = null,
        IReadOnlyList<VdbeVirtualTableBinding?>? virtualTableBindings = null)
        => new(
            program,
            cursorSources,
            writeTargets,
            parameterBinding,
            sharedTransaction,
            virtualTableBindings,
            executionOptions);

    internal ResumableStatement(
        VdbeProgram program,
        IReadOnlyList<VdbeCursorSource?>? cursorSources,
        IReadOnlyList<VdbeWriteTarget?>? writeTargets,
        VdbeParameterBinding? parameterBinding,
        VdbeTransactionContext? sharedTransaction,
        IReadOnlyList<VdbeVirtualTableBinding?>? virtualTableBindings,
        VdbeExecutionOptions executionOptions,
        VdbeExecutionMemory? executionMemory = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(executionOptions);
        if (cursorSources is not null && cursorSources.Count != program.CursorCount)
        {
            throw new ArgumentException(
                $"Expected {program.CursorCount} cursor sources but received {cursorSources.Count}.",
                nameof(cursorSources));
        }

        if (writeTargets is not null && writeTargets.Count != program.CursorCount)
        {
            throw new ArgumentException(
                $"Expected {program.CursorCount} write targets but received {writeTargets.Count}.",
                nameof(writeTargets));
        }
        if (virtualTableBindings is not null && virtualTableBindings.Count != program.CursorCount)
        {
            throw new ArgumentException(
                $"Expected {program.CursorCount} virtual-table bindings but received {virtualTableBindings.Count}.",
                nameof(virtualTableBindings));
        }

        if (parameterBinding is not null)
            ValidateBindingWidth(program, parameterBinding);

        Program = program;
        _registers = new SqlValue[program.RegisterCount];
        _openCursors = new bool[program.CursorCount];
        _cursorPositions = new int[program.CursorCount];
        _skipLastInsertRowId = new bool[program.CursorCount];
        _materializedRows = new SqlValue[program.CursorCount][];
        _materializedRowIds = new long[program.CursorCount];
        _joinCursorStates = new JoinCursorState?[program.CursorCount];
        _sorters = new SorterRuntime?[program.SorterCount];
        _accumulatorContexts = new object?[program.AccumulatorCount];
        _accumulatorInitialized = new bool[program.AccumulatorCount];
        _distinctSets = new List<SqlValue[]>?[program.DistinctSetCount];
        _groupIndexes = new Dictionary<SqlValue[], int>?[program.DistinctSetCount];
        _rowSetPositions = new int[program.DistinctSetCount];
        _workTables = new WorkTableRuntime?[program.WorkTableCount];
        _windowBuffers = new WindowBufferRuntime?[program.WindowBufferCount];
        _ephemeralTables = new EphemeralTableRuntime?[program.CursorCount];
        _virtualCursors = new ManagedVirtualTableCursor?[program.CursorCount];
        _indexMethodCursors = new Indexing.ManagedIndexMethodCursor?[program.CursorCount];
        _cursorSources = cursorSources;
        _writeTargets = writeTargets;
        _virtualTableBindings = virtualTableBindings;
        _executionOptions = executionOptions;
        _memory = executionMemory
            ?? new VdbeExecutionMemory(executionOptions.MemoryLimitBytes, executionOptions.Metrics);
        _binding = parameterBinding;
        _ownsTransaction = sharedTransaction is null;
        _transaction = sharedTransaction ?? new VdbeTransactionContext();
        State = ResumableStatementState.Ready;
    }

    /// <summary>
    /// The transaction/savepoint + deferred-FK counter this statement uses. When constructed with a
    /// shared context, multiple statements accumulate deferred FK violations until the shared
    /// context is committed or rolled back.
    /// </summary>
    public VdbeTransactionContext Transaction => _transaction;

    public VdbeProgram Program { get; }

    /// <summary>The parameter binding the program's <c>LoadParameter</c> opcodes read, or
    /// <see langword="null"/> when none has been supplied yet. A <see cref="Reset"/> preserves it (matching
    /// SQLite's <c>sqlite3_reset</c>, which does not clear bindings); <see cref="Rebind"/> replaces it.</summary>
    public VdbeParameterBinding? ParameterBinding => _binding;

    public ResumableStatementState State { get; private set; }

    public ProgramCounter InstructionPointer => _instructionPointer;

    public IReadOnlyList<SqlValue>? CurrentRow => _currentRow;

    /// <summary>The number of rows a write program has mutated so far, i.e. the
    /// rows-affected count an INSERT/UPDATE/DELETE reports.</summary>
    public int RowsAffected { get; private set; }

    /// <summary>The rowid recorded by the most recent <c>Commit</c> of an INSERT
    /// program, or <see langword="null"/> for UPDATE/DELETE and empty inserts.</summary>
    public long? LastInsertRowId { get; private set; }

    /// <summary>The rowid returned by the most recently executed VUpdate.</summary>
    public long? LastVirtualTableRowId { get; private set; }

    /// <summary>Whether the program currently has a transaction open through a
    /// <c>BeginTransaction</c> or <c>Savepoint</c> opcode that has not yet been committed or rolled back.
    /// This tracks the interpreter's register-scoped transaction state machine, not any durable store.</summary>
    public bool InTransaction => _transaction.InTransaction;

    /// <summary>The number of open transaction/savepoint frames: the outermost transaction plus any nested
    /// savepoints. Zero when no transaction is open.</summary>
    public int TransactionDepth => _transaction.Depth;

    /// <summary>The open savepoint names from outermost to innermost, with the anonymous
    /// <c>BeginTransaction</c> root reported as <see langword="null"/>. Exposed so callers can observe the
    /// transaction state machine directly.</summary>
    public IReadOnlyList<string?> TransactionSavepoints => _transaction.SavepointNames;

    public StatementStepResult Step()
    {
        return StepResumable() switch
        {
            ResumableStatementStepResult.Row => StatementStepResult.Row,
            ResumableStatementStepResult.Done => StatementStepResult.Done,
            ResumableStatementStepResult.Yielded => throw new StatementYieldedException(),
            _ => throw new InvalidOperationException("Unknown resumable statement step result."),
        };
    }

    public ResumableStatementStepResult StepResumable() =>
        StepResumable(CancellationToken.None);

    public ResumableStatementStepResult StepResumable(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (cancellationToken.IsCancellationRequested)
            FailExecution(new OperationCanceledException(cancellationToken));

        if (State == ResumableStatementState.Yielded)
        {
            throw new InvalidOperationException(
                "Statement is yielded. Call Resume before stepping again.");
        }

        if (State == ResumableStatementState.Done)
            return ResumableStatementStepResult.Done;
        if (State == ResumableStatementState.Faulted)
        {
            throw new InvalidOperationException(
                "Statement execution faulted. Call Reset before stepping it again.");
        }

        _currentRow = null;
        while (_instructionPointer.Offset < Program.Instructions.Count)
        {
            if (cancellationToken.IsCancellationRequested)
                FailExecution(new OperationCanceledException(cancellationToken));
            var instruction = Program.Instructions[_instructionPointer.Offset];
            _hasExecutedInstruction = true;
            switch (instruction)
            {
                case LoadConstantInstruction loadConstant:
                    _registers[loadConstant.Destination.Index] = loadConstant.Value;
                    AdvanceInstructionPointer();
                    break;
                case LoadParameterInstruction loadParameter:
                    _registers[loadParameter.Destination.Index] = RequireBinding().Get(loadParameter.Slot);
                    AdvanceInstructionPointer();
                    break;
                case CopyInstruction copy:
                    _registers[copy.Destination.Index] = _registers[copy.Source.Index];
                    AdvanceInstructionPointer();
                    break;
                case FunctionInstruction function:
                    {
                        // Snapshot the argument registers into a private tuple before invoking the
                        // delegate, so the function can neither observe a later register write nor mutate
                        // the register file, and write the (immutable) result only on success — a throwing
                        // delegate propagates out of the step with the destination register untouched.
                        var arguments = ReadRegisters(function.Arguments);
                        _registers[function.Destination.Index] = function.Function.Invoke(arguments);
                        AdvanceInstructionPointer();
                        break;
                    }
                case ArithmeticInstruction arithmetic:
                    {
                        // Snapshot the operand registers before computing so the destination may overlap an
                        // operand and a throwing evaluation (a type error) propagates out of the step with
                        // the destination register left untouched — no half-computed result is published.
                        var operands = ReadRegisters(arithmetic.Operands);
                        _registers[arithmetic.Destination.Index] =
                            VdbeArithmetic.Evaluate(arithmetic.Operator, operands);
                        AdvanceInstructionPointer();
                        break;
                    }
                case NumericAffinityInstruction numericAffinity:
                    {
                        var value = _registers[numericAffinity.Value.Index];
                        _registers[numericAffinity.Value.Index] = numericAffinity.Affinity.Apply(value);
                        AdvanceInstructionPointer();
                        break;
                    }
                case CompareInstruction compare:
                    _registers[compare.Destination.Index] = VdbeValueOperations.Compare(
                        compare.Operator,
                        _registers[compare.Left.Index],
                        _registers[compare.Right.Index],
                        compare.LeftAffinity,
                        compare.RightAffinity,
                        compare.Collation);
                    AdvanceInstructionPointer();
                    break;
                case JumpIfNotTrueInstruction jumpIfNotTrue:
                    if (EmbeddedDatabase.IsTrue(_registers[jumpIfNotTrue.Value.Index]))
                        AdvanceInstructionPointer();
                    else
                        _instructionPointer = jumpIfNotTrue.FalseTarget;
                    break;
                case CastInstruction cast:
                    _registers[cast.Value.Index] = VdbeValueOperations.Cast(
                        _registers[cast.Value.Index],
                        cast.TypeName);
                    AdvanceInstructionPointer();
                    break;
                case OpenReadCursorInstruction open:
                    OpenCursor(open.Cursor);
                    _cursorPositions[open.Cursor.Index] = -1;
                    _materializedRows[open.Cursor.Index] = null;
                    AdvanceInstructionPointer();
                    break;
                case OpenJoinCursorInstruction openJoin:
                    {
                        OpenCursor(openJoin.Cursor);
                        var state = new JoinCursorState();
                        state.Open(openJoin.Plan, _executionOptions, _memory);
                        _joinCursorStates[openJoin.Cursor.Index] = state;
                        _cursorPositions[openJoin.Cursor.Index] = -1;
                        _materializedRows[openJoin.Cursor.Index] = null;
                        AdvanceInstructionPointer();
                        break;
                    }
                case OpenWriteCursorInstruction openWrite:
                    OpenCursor(openWrite.Cursor);
                    _cursorPositions[openWrite.Cursor.Index] = -1;
                    _materializedRows[openWrite.Cursor.Index] = null;
                    AdvanceInstructionPointer();
                    break;
                case VOpenInstruction vOpen:
                    {
                        try
                        {
                            OpenCursor(vOpen.Cursor);
                            _virtualCursors[vOpen.Cursor.Index] = RequireVirtualTable(vOpen.Cursor).Open();
                            _cursorPositions[vOpen.Cursor.Index] = -1;
                            AdvanceInstructionPointer();
                        }
                        catch (Exception exception)
                        {
                            FailExecution(exception);
                        }
                        break;
                    }
                case OpenEphemeralInstruction openEphemeral:
                    OpenCursor(openEphemeral.Cursor);
                    _ephemeralTables[openEphemeral.Cursor.Index] = new EphemeralTableRuntime(openEphemeral.ColumnCount);
                    _cursorPositions[openEphemeral.Cursor.Index] = -1;
                    _materializedRows[openEphemeral.Cursor.Index] = null;
                    AdvanceInstructionPointer();
                    break;
                case EphemeralInsertInstruction ephemeralInsert:
                    {
                        var table = RequireEphemeralTable(ephemeralInsert.Cursor);
                        var row = ReadRegisters(ephemeralInsert.Values);
                        table.Insert(row);
                        AdvanceInstructionPointer();
                        break;
                    }
                case CloseCursorInstruction close:
                    {
                        CloseCursor(close.Cursor);
                        _virtualCursors[close.Cursor.Index]?.Dispose();
                        _virtualCursors[close.Cursor.Index] = null;
                        var joinState = _joinCursorStates[close.Cursor.Index];
                        try
                        {
                            joinState?.Close();
                            _joinCursorStates[close.Cursor.Index] = null;
                        }
                        catch
                        {
                            State = ResumableStatementState.Faulted;
                            throw;
                        }
                        _ephemeralTables[close.Cursor.Index] = null;
                        AdvanceInstructionPointer();
                        break;
                    }
                case RewindCursorInstruction rewind:
                    {
                        _materializedRows[rewind.Cursor.Index] = null;
                        if (_joinCursorStates[rewind.Cursor.Index] is { } joinState)
                        {
                            // Streaming join cursor: the row count is not known up front, so
                            // emptiness is decided by pulling the first row. A successful pull
                            // also positions the cursor on that first row.
                            try
                            {
                                if (joinState.MoveNext(cancellationToken))
                                {
                                    _cursorPositions[rewind.Cursor.Index] = 0;
                                    AdvanceInstructionPointer();
                                }
                                else
                                {
                                    _instructionPointer = rewind.EmptyTarget;
                                }
                            }
                            catch
                            {
                                State = ResumableStatementState.Faulted;
                                throw;
                            }
                        }
                        else if (CursorRowCount(rewind.Cursor) == 0)
                        {
                            _instructionPointer = rewind.EmptyTarget;
                        }
                        else
                        {
                            _cursorPositions[rewind.Cursor.Index] = 0;
                            AdvanceInstructionPointer();
                        }

                        break;
                    }
                case LastCursorInstruction last:
                    {
                        _materializedRows[last.Cursor.Index] = null;
                        if (_joinCursorStates[last.Cursor.Index] is not null)
                        {
                            throw new InvalidOperationException(
                                $"Cursor {last.Cursor.Index} is a streaming join cursor; Last (reverse traversal) is not supported.");
                        }

                        var count = CursorRowCount(last.Cursor);
                        if (count == 0)
                        {
                            _instructionPointer = last.EmptyTarget;
                        }
                        else
                        {
                            _cursorPositions[last.Cursor.Index] = count - 1;
                            AdvanceInstructionPointer();
                        }

                        break;
                    }
                case ColumnInstruction column:
                    {
                        var row = CurrentCursorRow(column.Cursor);
                        _registers[column.Destination.Index] = row[column.ColumnIndex];
                        AdvanceInstructionPointer();
                        break;
                    }
                case VColumnInstruction vColumn:
                    {
                        try
                        {
                            _registers[vColumn.Destination.Index] =
                                RequireVirtualCursor(vColumn.Cursor).Column(vColumn.ColumnIndex);
                            AdvanceInstructionPointer();
                        }
                        catch (Exception exception)
                        {
                            FailExecution(exception);
                        }
                        break;
                    }
                case RowIdInstruction rowId:
                    {
                        _registers[rowId.Destination.Index] = SqlValue.Integer(CurrentCursorRowId(rowId.Cursor));
                        AdvanceInstructionPointer();
                        break;
                    }
                case RowCountInstruction rowCount:
                    {
                        var rowCountValue = CursorRowCount(rowCount.Cursor);
                        // When a progress handler is registered, pump it once per counted row so an
                        // interruptible SELECT count(*) raises SQLITE_INTERRUPT at the same point the
                        // scan+accumulator path would. Null in the common (no-handler) case keeps this O(1).
                        if (rowCount.DriveProgress is { } driveProgress)
                        {
                            for (var i = 0; i < rowCountValue; i++)
                                driveProgress();
                        }

                        _registers[rowCount.Destination.Index] = SqlValue.Integer(rowCountValue);
                        AdvanceInstructionPointer();
                        break;
                    }
                case FilterInstruction filter:
                    {
                        var row = CurrentCursorRow(filter.Cursor);
                        if (filter.Predicate(row))
                            AdvanceInstructionPointer();
                        else
                            _instructionPointer = filter.FalseTarget;

                        break;
                    }
                case VFilterInstruction vFilter:
                    {
                        try
                        {
                            var cursor = RequireVirtualCursor(vFilter.Cursor);
                            var positioned = cursor.Filter(vFilter.Plan, ReadRegisters(vFilter.Arguments));
                            if (positioned && !cursor.Eof)
                                AdvanceInstructionPointer();
                            else
                                _instructionPointer = vFilter.EmptyTarget;
                        }
                        catch (Exception exception)
                        {
                            FailExecution(exception);
                        }
                        break;
                    }
                case IndexMethodCreateInstruction methodCreate:
                    {
                        var cursor = OpenIndexMethodCursor(methodCreate.Cursor, methodCreate.Binding);
                        cursor.Create();
                        AdvanceInstructionPointer();
                        break;
                    }
                case IndexMethodDestroyInstruction methodDestroy:
                    {
                        var cursor = OpenIndexMethodCursor(methodDestroy.Cursor, methodDestroy.Binding);
                        cursor.Destroy();
                        AdvanceInstructionPointer();
                        break;
                    }
                case IndexMethodOptimizeInstruction methodOptimize:
                    {
                        var cursor = OpenIndexMethodCursor(methodOptimize.Cursor, methodOptimize.Binding);
                        cursor.OpenWrite();
                        cursor.Optimize();
                        AdvanceInstructionPointer();
                        break;
                    }
                case IndexMethodQueryInstruction methodQuery:
                    {
                        var cursor = OpenIndexMethodCursor(methodQuery.Cursor, methodQuery.Binding);
                        cursor.OpenRead();
                        var arguments = ReadRegisters(methodQuery.Arguments);
                        var positioned = cursor.QueryStart(methodQuery.PatternIndex, arguments.ToArray());
                        if (positioned)
                            AdvanceInstructionPointer();
                        else
                            _instructionPointer = methodQuery.EmptyTarget;
                        break;
                    }
                case IndexMethodNextInstruction methodNext:
                    {
                        if (RequireIndexMethodCursor(methodNext.Cursor).QueryNext())
                            _instructionPointer = methodNext.LoopTarget;
                        else
                            AdvanceInstructionPointer();
                        break;
                    }
                case IndexMethodColumnInstruction methodColumn:
                    {
                        _registers[methodColumn.Destination.Index] =
                            RequireIndexMethodCursor(methodColumn.Cursor).Column(methodColumn.ColumnIndex);
                        AdvanceInstructionPointer();
                        break;
                    }
                case IndexMethodRowIdInstruction methodRowId:
                    {
                        var rowId = RequireIndexMethodCursor(methodRowId.Cursor).RowId();
                        _registers[methodRowId.Destination.Index] =
                            rowId is { } value ? SqlValue.Integer(value) : SqlValue.Null;
                        AdvanceInstructionPointer();
                        break;
                    }
                case IndexMethodInsertInstruction methodInsert:
                    {
                        var cursor = RequireIndexMethodCursor(methodInsert.Cursor);
                        cursor.OpenWrite();
                        cursor.Insert(ReadRegisters(methodInsert.Values).ToArray());
                        AdvanceInstructionPointer();
                        break;
                    }
                case IndexMethodDeleteInstruction methodDelete:
                    {
                        var cursor = RequireIndexMethodCursor(methodDelete.Cursor);
                        cursor.OpenWrite();
                        cursor.Delete(ReadRegisters(methodDelete.Values).ToArray());
                        AdvanceInstructionPointer();
                        break;
                    }
                case FilterRowIdInstruction filterRowId:
                    {
                        var row = CurrentCursorRow(filterRowId.Cursor);
                        var rowId = CurrentCursorRowId(filterRowId.Cursor);
                        if (filterRowId.Predicate(row, rowId))
                            AdvanceInstructionPointer();
                        else
                            _instructionPointer = filterRowId.FalseTarget;

                        break;
                    }
                case SeekRowidInstruction seekRowid:
                    {
                        // Position the cursor on the rowid in RowIdRegister; jump if absent.
                        // Linear search: CommitInserts keeps insert order, not rowid order.
                        if (TryPositionCursorOnRowId(seekRowid.Cursor, seekRowid.RowIdRegister))
                            AdvanceInstructionPointer();
                        else
                            _instructionPointer = seekRowid.NotFoundTarget;
                        break;
                    }
                case NotExistsInstruction notExists:
                    {
                        // Jump when the rowid is absent; leave cursor positioned when present.
                        if (TryPositionCursorOnRowId(notExists.Cursor, notExists.RowIdRegister))
                            AdvanceInstructionPointer();
                        else
                            _instructionPointer = notExists.JumpTarget;
                        break;
                    }
                case FoundInstruction found:
                    {
                        // Jump when the rowid is present; fall through when absent.
                        if (TryPositionCursorOnRowId(found.Cursor, found.RowIdRegister))
                            _instructionPointer = found.FoundTarget;
                        else
                            AdvanceInstructionPointer();
                        break;
                    }
                case NoConflictInstruction noConflict:
                    {
                        // Jump when no matching key (or any NULL key); position on match.
                        if (TryPositionCursorOnKeyPrefix(noConflict.Cursor, noConflict.Key))
                            AdvanceInstructionPointer();
                        else
                            _instructionPointer = noConflict.NoConflictTarget;
                        break;
                    }
                case FkCounterInstruction fkCounter:
                    {
                        if (fkCounter.Deferred)
                        {
                            // Deferred counters live on the transaction while one is open so they
                            // survive statement Reset and are checked at Commit.
                            if (_transaction.InTransaction)
                            {
                                _transaction.DeferredForeignKeyViolations = checked(
                                    _transaction.DeferredForeignKeyViolations + fkCounter.Increment);
                            }
                            else
                            {
                                _fkDeferredViolations = checked(_fkDeferredViolations + fkCounter.Increment);
                            }
                        }
                        else
                        {
                            _fkImmediateViolations = checked(_fkImmediateViolations + fkCounter.Increment);
                        }

                        AdvanceInstructionPointer();
                        break;
                    }
                case FkIfZeroInstruction fkIfZero:
                    {
                        var count = fkIfZero.Deferred
                            ? GetDeferredForeignKeyViolations()
                            : _fkImmediateViolations;
                        if (count == 0)
                            _instructionPointer = fkIfZero.Target;
                        else
                            AdvanceInstructionPointer();
                        break;
                    }
                case FkCheckInstruction fkCheck:
                    {
                        // Deferred checks inside a transaction are deferred to Commit; only
                        // autocommit statements enforce deferred counters at statement end.
                        if (fkCheck.Deferred && _transaction.InTransaction)
                        {
                            AdvanceInstructionPointer();
                            break;
                        }

                        var count = fkCheck.Deferred
                            ? GetDeferredForeignKeyViolations()
                            : _fkImmediateViolations;
                        if (count != 0)
                        {
                            throw new EmbeddedSqlException(
                                "FOREIGN KEY constraint failed",
                                SqliteResultCode.ConstraintForeignKey,
                                InsertConflictAlgorithm.Abort);
                        }

                        AdvanceInstructionPointer();
                        break;
                    }
                case SeekKeyInstruction seekKey:
                    {
                        if (TrySeekKey(
                                seekKey.Cursor,
                                seekKey.Key,
                                seekKey.Operator,
                                seekKey.EqOnly,
                                seekKey.KeyColumns))
                            AdvanceInstructionPointer();
                        else
                            _instructionPointer = seekKey.NotFoundTarget;
                        break;
                    }
                case IdxRowIdInstruction idxRowId:
                    {
                        _registers[idxRowId.Destination.Index] = SqlValue.Integer(CurrentCursorRowId(idxRowId.Cursor));
                        AdvanceInstructionPointer();
                        break;
                    }
                case RowDataInstruction rowData:
                    {
                        var row = CurrentCursorRow(rowData.Cursor);
                        var dest = rowData.Destination;
                        for (var i = 0; i < dest.Count; i++)
                        {
                            _registers[dest.Start.Index + i] = i < row.Length
                                ? row[i]
                                : SqlValue.Null;
                        }

                        AdvanceInstructionPointer();
                        break;
                    }
                case IdxInsertInstruction idxInsert:
                    {
                        ExecuteIdxInsert(idxInsert);
                        AdvanceInstructionPointer();
                        break;
                    }
                case IdxDeleteInstruction idxDelete:
                    {
                        ExecuteIdxDelete(idxDelete);
                        AdvanceInstructionPointer();
                        break;
                    }
                case SeekRowidRangeInstruction seekRowidRange:
                    {
                        // Position the cursor on the first row whose rowid satisfies StartOp relative
                        // to StartRowIdRegister, jumping to NotFoundTarget when no such row exists.
                        // The search is linear for the same reason as SeekRowid: the rowid sort
                        // invariant is not maintained for explicit out-of-order rowid INSERTs
                        // (CommitInserts appends in insert order). The upper bound (EndOp/EndRowIdRegister)
                        // is enforced by a following FilterRowIdInstruction emitted by the compiler, not
                        // here — this instruction only finds the starting position.
                        _materializedRows[seekRowidRange.Cursor.Index] = null;
                        var source = RequireCursorSource(seekRowidRange.Cursor);
                        var rowIds = source.RowIds;
                        if (rowIds is null)
                        {
                            _instructionPointer = seekRowidRange.NotFoundTarget;
                            break;
                        }

                        var startBound = _registers[seekRowidRange.StartRowIdRegister.Index];
                        if (startBound.Kind != SqlValueKind.Integer)
                        {
                            _instructionPointer = seekRowidRange.NotFoundTarget;
                            break;
                        }

                        var startValue = startBound.AsInteger();
                        var found = -1;
                        for (var i = 0; i < rowIds.Count; i++)
                        {
                            if (Satisfies(rowIds[i], seekRowidRange.StartOp, startValue))
                            {
                                found = i;
                                break;
                            }
                        }

                        if (found >= 0)
                        {
                            _cursorPositions[seekRowidRange.Cursor.Index] = found;
                            AdvanceInstructionPointer();
                        }
                        else
                        {
                            _instructionPointer = seekRowidRange.NotFoundTarget;
                        }

                        break;
                    }
                case FilterRegistersInstruction filterRegisters:
                    {
                        var row = ReadRegisters(filterRegisters.Row);
                        if (filterRegisters.Predicate(row))
                            AdvanceInstructionPointer();
                        else
                            _instructionPointer = filterRegisters.FalseTarget;

                        break;
                    }
                case GroupKeyInstruction groupKey:
                    {
                        var key = groupKey.Projector(ReadRegisters(groupKey.Row));
                        if (key.Length != groupKey.KeyCount)
                        {
                            throw new InvalidOperationException(
                                $"GROUP BY projector returned {key.Length} value(s), expected {groupKey.KeyCount}.");
                        }

                        var groups = _distinctSets[groupKey.GroupSetIndex] ??= [];
                        var groupIndex = -1;
                        if (groupKey.Hasher is not null)
                        {
                            var index = _groupIndexes[groupKey.GroupSetIndex] ??=
                                new Dictionary<SqlValue[], int>(
                                    new GroupKeyEqualityComparer(
                                        groupKey.Equality,
                                        groupKey.Hasher));
                            if (index.TryGetValue(key, out var existing))
                                groupIndex = existing;
                        }
                        else
                        {
                            for (var index = 0; index < groups.Count; index++)
                            {
                                if (groupKey.Equality(groups[index], key))
                                {
                                    groupIndex = index;
                                    break;
                                }
                            }
                        }

                        if (groupIndex < 0)
                        {
                            groupIndex = groups.Count;
                            var storedKey = key.ToArray();
                            groups.Add(storedKey);
                            _groupIndexes[groupKey.GroupSetIndex]?.Add(storedKey, groupIndex);
                        }

                        _registers[groupKey.Destination.Index] = SqlValue.Integer(groupIndex);
                        if (groupKey.KeyOutput is { } keyOutput)
                        {
                            Array.Copy(key, 0, _registers, keyOutput.Start.Index, key.Length);
                        }

                        AdvanceInstructionPointer();
                        break;
                    }
                case ProjectRegistersInstruction project:
                    {
                        var input = ReadRegisters(project.Input);
                        var output = project.Transform(input)
                            ?? throw new InvalidOperationException("A register projection returned null.");
                        if (output.Length != project.Output.Count)
                        {
                            throw new InvalidOperationException(
                                $"A register projection declared {project.Output.Count} outputs but returned {output.Length}.");
                        }

                        Array.Copy(output, 0, _registers, project.Output.Start.Index, output.Length);
                        AdvanceInstructionPointer();
                        break;
                    }
                case DistinctFilterInstruction distinctFilter:
                    {
                        var candidate = ReadRegisters(distinctFilter.Values);
                        if (RowSetContains(
                                distinctFilter.DistinctSetIndex,
                                candidate,
                                distinctFilter.Equality))
                        {
                            _instructionPointer = distinctFilter.DuplicateTarget;
                        }
                        else
                        {
                            (_distinctSets[distinctFilter.DistinctSetIndex] ??= []).Add(candidate);
                            AdvanceInstructionPointer();
                        }

                        break;
                    }
                case NextInstruction next:
                    {
                        _materializedRows[next.Cursor.Index] = null;
                        if (_joinCursorStates[next.Cursor.Index] is { } joinState)
                        {
                            // Streaming join cursor: advance the enumerator and loop back while
                            // another row exists; the count is not known up front.
                            try
                            {
                                if (joinState.MoveNext(cancellationToken))
                                    _instructionPointer = next.LoopTarget;
                                else
                                    AdvanceInstructionPointer();
                            }
                            catch
                            {
                                State = ResumableStatementState.Faulted;
                                throw;
                            }
                        }
                        else
                        {
                            var position = _cursorPositions[next.Cursor.Index] + 1;
                            _cursorPositions[next.Cursor.Index] = position;
                            if (position < CursorRowCount(next.Cursor))
                                _instructionPointer = next.LoopTarget;
                            else
                                AdvanceInstructionPointer();
                        }

                        break;
                    }
                case VNextInstruction vNext:
                    {
                        try
                        {
                            var cursor = RequireVirtualCursor(vNext.Cursor);
                            cursor.Next();
                            if (!cursor.Eof)
                                _instructionPointer = vNext.LoopTarget;
                            else
                                AdvanceInstructionPointer();
                        }
                        catch (Exception exception)
                        {
                            FailExecution(exception);
                        }
                        break;
                    }
                case VCreateInstruction vCreate:
                    {
                        var table = ManagedVirtualTableModuleRegistry.Resolve(vCreate.ModuleName).Create(vCreate.Context);
                        ArgumentNullException.ThrowIfNull(table);
                        try
                        {
                            vCreate.Publish(table);
                        }
                        catch
                        {
                            table.DisconnectInstance();
                            throw;
                        }

                        AdvanceInstructionPointer();
                        break;
                    }
                case VDestroyInstruction vDestroy:
                    RequireVirtualTable(vDestroy.Cursor).DestroyInstance();
                    AdvanceInstructionPointer();
                    break;
                case VRenameInstruction vRename:
                    {
                        var value = _registers[vRename.NewName.Index];
                        if (value.Kind != SqlValueKind.Text)
                            throw new InvalidOperationException("VRename requires a text table name.");
                        RequireVirtualTable(vRename.Cursor).Rename(value.AsText());
                        AdvanceInstructionPointer();
                        break;
                    }
                case PrevInstruction prev:
                    {
                        _materializedRows[prev.Cursor.Index] = null;
                        if (_joinCursorStates[prev.Cursor.Index] is not null)
                        {
                            throw new InvalidOperationException(
                                $"Cursor {prev.Cursor.Index} is a streaming join cursor; Prev (reverse traversal) is not supported.");
                        }

                        var position = _cursorPositions[prev.Cursor.Index] - 1;
                        _cursorPositions[prev.Cursor.Index] = position;
                        if (position >= 0)
                            _instructionPointer = prev.LoopTarget;
                        else
                            AdvanceInstructionPointer();

                        break;
                    }
                case DeleteInstruction delete:
                    {
                        var target = RequireWriteTarget(delete.Cursor);
                        var position = _cursorPositions[delete.Cursor.Index];
                        if (target.TryDeleteRow is { } tryDeleteRow)
                        {
                            if (tryDeleteRow(position))
                                RowsAffected = checked(RowsAffected + 1);
                        }
                        else
                        {
                            var deleteRow = target.DeleteRow
                                ?? throw new InvalidOperationException(
                                    $"Cursor {delete.Cursor.Index} has no delete action bound.");
                            deleteRow(position);
                            RowsAffected = checked(RowsAffected + 1);
                        }
                        AdvanceInstructionPointer();
                        break;
                    }
                case InsertInstruction insert:
                    {
                        MutateCursorRow(insert.Cursor, insert.Flags);
                        AdvanceInstructionPointer();
                        break;
                    }
                case UpdateInstruction update:
                    {
                        MutateCursorRow(update.Cursor, update.Flags);
                        AdvanceInstructionPointer();
                        break;
                    }
                case VUpdateInstruction vUpdate:
                    {
                        try
                        {
                            var arguments = ReadRegisters(vUpdate.Arguments);
                            var result = RequireVirtualTable(vUpdate.Cursor).Update(arguments, vUpdate.ConflictMode);
                            var rowId = result.RowId;
                            LastVirtualTableRowId = rowId;
                            if (vUpdate.NewRowIdDestination is { } destination)
                                _registers[destination.Index] = rowId is { } value ? SqlValue.Integer(value) : SqlValue.Null;
                            if (result.Changed
                                && arguments[0].Kind == SqlValueKind.Null
                                && rowId is { } insertedRowId)
                            {
                                LastInsertRowId = insertedRowId;
                            }
                            if (result.Changed)
                                RowsAffected = checked(RowsAffected + 1);
                            AdvanceInstructionPointer();
                        }
                        catch (Exception exception)
                        {
                            FailExecution(exception);
                        }
                        break;
                    }
                case VBeginInstruction vBegin:
                    RequireVirtualTable(vBegin.Cursor).Begin();
                    AdvanceInstructionPointer();
                    break;
                case VSyncInstruction vSync:
                    RequireVirtualTable(vSync.Cursor).Sync();
                    AdvanceInstructionPointer();
                    break;
                case VCommitInstruction vCommit:
                    RequireVirtualTable(vCommit.Cursor).Commit();
                    AdvanceInstructionPointer();
                    break;
                case VRollbackInstruction vRollback:
                    RequireVirtualTable(vRollback.Cursor).Rollback();
                    AdvanceInstructionPointer();
                    break;
                case ProgramInstruction program:
                    if (ExecuteSubprogram(program, cancellationToken))
                        return ResumableStatementStepResult.Yielded;
                    break;
                case CommitInstruction commit:
                    {
                        try
                        {
                            var target = RequireWriteTarget(commit.Cursor);
                            var committedRowId = target.Commit();
                            // Turso InsertFlags::SKIP_LAST_ROWID: keep last_insert_rowid() unchanged.
                            if (!_skipLastInsertRowId[commit.Cursor.Index])
                                LastInsertRowId = committedRowId;
                            AdvanceInstructionPointer();
                        }
                        catch
                        {
                            State = ResumableStatementState.Faulted;
                            throw;
                        }

                        break;
                    }
                case OpenSorterInstruction openSorter:
                    OpenSorter(openSorter);
                    AdvanceInstructionPointer();
                    break;
                case SorterInsertInstruction sorterInsert:
                    {
                        var runtime = RequireOpenSorter(sorterInsert.Sorter);
                        runtime.Insert(ReadRegisters(sorterInsert.Record), cancellationToken);
                        AdvanceInstructionPointer();
                        break;
                    }
                case SorterSortInstruction sorterSort:
                    {
                        var runtime = RequireOpenSorter(sorterSort.Sorter);
                        if (runtime.Sort(cancellationToken))
                            AdvanceInstructionPointer();
                        else
                            _instructionPointer = sorterSort.EmptyTarget;

                        break;
                    }
                case SorterDataInstruction sorterData:
                    {
                        var runtime = RequireOpenSorter(sorterData.Sorter);
                        var record = runtime.Current();
                        Array.Copy(record, 0, _registers, sorterData.Destination.Start.Index, record.Length);
                        AdvanceInstructionPointer();
                        break;
                    }
                case SorterNextInstruction sorterNext:
                    {
                        var runtime = RequireOpenSorter(sorterNext.Sorter);
                        if (runtime.MoveNext(cancellationToken))
                            _instructionPointer = sorterNext.LoopTarget;
                        else
                            AdvanceInstructionPointer();

                        break;
                    }
                case CloseSorterInstruction closeSorter:
                    CloseSorter(closeSorter.Sorter);
                    AdvanceInstructionPointer();
                    break;
                case GotoInstruction gotoInstruction:
                    _instructionPointer = gotoInstruction.Target;
                    break;
                case JumpIfInstruction jumpIf:
                    {
                        var flag = _registers[jumpIf.Register.Index];
                        if (flag.Kind == SqlValueKind.Integer && flag.AsInteger() != 0)
                            _instructionPointer = jumpIf.Target;
                        else
                            AdvanceInstructionPointer();

                        break;
                    }
                case AggResetInstruction aggReset:
                    _accumulatorInitialized[aggReset.Accumulator.Index] = false;
                    _accumulatorContexts[aggReset.Accumulator.Index] = null;
                    AdvanceInstructionPointer();
                    break;
                case AggStepInstruction aggStep:
                    {
                        try
                        {
                            var index = aggStep.Accumulator.Index;
                            if (!_accumulatorInitialized[index])
                            {
                                _accumulatorContexts[index] = aggStep.Aggregate.CreateContext();
                                _accumulatorInitialized[index] = true;
                            }

                            _accumulatorContexts[index] = aggStep.Aggregate.Accumulate(
                                _accumulatorContexts[index],
                                ReadRegisters(aggStep.Arguments));
                            AdvanceInstructionPointer();
                        }
                        catch
                        {
                            State = ResumableStatementState.Faulted;
                            throw;
                        }

                        break;
                    }
                case AggFinalizeInstruction aggFinalize:
                    {
                        try
                        {
                            var index = aggFinalize.Accumulator.Index;
                            // Finalizing an accumulator that was reset but never stepped yields the
                            // aggregate's empty-input value, so empty groups still produce a result.
                            var context = _accumulatorInitialized[index]
                                ? _accumulatorContexts[index]
                                : aggFinalize.Aggregate.CreateContext();
                            _registers[aggFinalize.Destination.Index] =
                                aggFinalize.Aggregate.Finalize(context);
                            AdvanceInstructionPointer();
                        }
                        catch
                        {
                            State = ResumableStatementState.Faulted;
                            throw;
                        }

                        break;
                    }
                case SameGroupInstruction sameGroup:
                    {
                        var current = ReadRegisters(sameGroup.CurrentKey);
                        var saved = ReadRegisters(sameGroup.SavedKey);
                        if (sameGroup.Comparer(current, saved))
                            _instructionPointer = sameGroup.SameGroupTarget;
                        else
                            AdvanceInstructionPointer();

                        break;
                    }
                case YieldInstruction:
                    AdvanceInstructionPointer();
                    State = ResumableStatementState.Yielded;
                    return ResumableStatementStepResult.Yielded;
                case ResultRowInstruction resultRow:
                    _currentRow = Array.AsReadOnly(ReadRegisters(resultRow.Values));
                    AdvanceInstructionPointer();
                    State = ResumableStatementState.Row;
                    return ResumableStatementStepResult.Row;
                case DistinctResultRowInstruction distinctRow:
                    {
                        // Compare the candidate against every row already emitted through this
                        // distinct set. Duplicates advance without producing a row (continuing the
                        // dispatch loop); the first occurrence of a row is recorded and yielded.
                        var candidate = ReadRegisters(distinctRow.Values);
                        var seen = _distinctSets[distinctRow.DistinctSetIndex] ??= [];
                        var duplicate = false;
                        foreach (var emitted in seen)
                        {
                            if (distinctRow.Equality(emitted, candidate))
                            {
                                duplicate = true;
                                break;
                            }
                        }

                        AdvanceInstructionPointer();
                        if (duplicate)
                            break;

                        seen.Add(candidate);
                        _currentRow = Array.AsReadOnly(candidate);
                        State = ResumableStatementState.Row;
                        return ResumableStatementStepResult.Row;
                    }
                case DistinctGateInstruction distinctGate:
                    {
                        var candidate = ReadRegisters(distinctGate.Values);
                        var seen = _distinctSets[distinctGate.DistinctSetIndex] ??= [];
                        var duplicate = false;
                        foreach (var emitted in seen)
                        {
                            if (distinctGate.Equality(emitted, candidate))
                            {
                                duplicate = true;
                                break;
                            }
                        }

                        if (duplicate)
                        {
                            _instructionPointer = distinctGate.DuplicateTarget;
                        }
                        else
                        {
                            seen.Add(candidate);
                            AdvanceInstructionPointer();
                        }

                        break;
                    }
                case RowSetInsertInstruction rowSetInsert:
                    {
                        // A temporary B-tree overwrites an equal key with the later record, so retain the
                        // latest representative while preserving one row per distinct tuple.
                        var candidate = ReadRegisters(rowSetInsert.Values);
                        var set = _distinctSets[rowSetInsert.RowSetIndex] ??= [];
                        var existingIndex = -1;
                        for (var index = 0; index < set.Count; index++)
                        {
                            if (rowSetInsert.Equality(set[index], candidate))
                            {
                                existingIndex = index;
                                break;
                            }
                        }

                        if (existingIndex < 0)
                            set.Add(candidate);
                        else
                            set[existingIndex] = candidate;

                        AdvanceInstructionPointer();
                        break;
                    }
                case RowSetRewindInstruction rowSetRewind:
                    {
                        var set = _distinctSets[rowSetRewind.RowSetIndex];
                        _rowSetPositions[rowSetRewind.RowSetIndex] = 0;
                        if (set is null || set.Count == 0)
                        {
                            _instructionPointer = rowSetRewind.EmptyTarget;
                            break;
                        }

                        if (rowSetRewind.Comparer is not null && set.Count > 1)
                            set.Sort((left, right) => rowSetRewind.Comparer(left, right));

                        CopyRowSetRow(set[0], rowSetRewind.Destination);
                        AdvanceInstructionPointer();
                        break;
                    }
                case RowSetNextInstruction rowSetNext:
                    {
                        var set = _distinctSets[rowSetNext.RowSetIndex]
                            ?? throw new InvalidOperationException(
                                $"Cannot advance unopened row set {rowSetNext.RowSetIndex}.");
                        var position = checked(++_rowSetPositions[rowSetNext.RowSetIndex]);
                        if (position < set.Count)
                        {
                            CopyRowSetRow(set[position], rowSetNext.Destination);
                            _instructionPointer = rowSetNext.LoopTarget;
                        }
                        else
                        {
                            AdvanceInstructionPointer();
                        }

                        break;
                    }
                case RowSetTestInstruction rowSetTest:
                    {
                        try
                        {
                            var value = _registers[rowSetTest.ValueRegister.Index];
                            if (value.Kind != SqlValueKind.Integer)
                                throw new InvalidOperationException("RowSetTest: P3 must be an integer");

                            var rowSet = _integerRowSets.TryGetValue(rowSetTest.RowSetRegister.Index, out var existing)
                                ? existing
                                : _integerRowSets[rowSetTest.RowSetRegister.Index] = new IntegerRowSet();
                            if (rowSetTest.Batch != 0 && rowSet.ContainsEarlierBatch(value.AsInteger(), rowSetTest.Batch))
                            {
                                _instructionPointer = rowSetTest.FoundTarget;
                            }
                            else
                            {
                                if (rowSetTest.Batch != -1)
                                    rowSet.Insert(value.AsInteger());

                                AdvanceInstructionPointer();
                            }
                        }
                        catch
                        {
                            State = ResumableStatementState.Faulted;
                            throw;
                        }

                        break;
                    }
                case CompoundResultRowInstruction compoundRow:
                    {
                        // Emit the candidate only when it satisfies the membership condition against every
                        // probe set and is novel to the output set. A failing or duplicate candidate advances
                        // without producing a row, so the primary term keeps streaming in its own order.
                        var candidate = ReadRegisters(compoundRow.Values);
                        var passesMembership = true;
                        foreach (var membershipSetIndex in compoundRow.MembershipSetIndices)
                        {
                            var contained = RowSetContains(membershipSetIndex, candidate, compoundRow.Equality);
                            var required = compoundRow.Mode == CompoundMembershipMode.PresentInAll;
                            if (contained != required)
                            {
                                passesMembership = false;
                                break;
                            }
                        }

                        AdvanceInstructionPointer();
                        if (!passesMembership)
                            break;

                        var output = _distinctSets[compoundRow.OutputSetIndex] ??= [];
                        var duplicate = false;
                        foreach (var emitted in output)
                        {
                            if (compoundRow.Equality(emitted, candidate))
                            {
                                duplicate = true;
                                break;
                            }
                        }

                        if (duplicate)
                            break;

                        output.Add(candidate);
                        _currentRow = Array.AsReadOnly(candidate);
                        State = ResumableStatementState.Row;
                        return ResumableStatementStepResult.Row;
                    }
                case GuardedRowInstruction guardedRow:
                    {
                        var candidate = ReadRegisters(guardedRow.Values);
                        var accepted = EvaluateRowGuards(guardedRow.Guards, candidate);

                        AdvanceInstructionPointer();
                        if (!accepted)
                            break;

                        switch (guardedRow.Destination)
                        {
                            case ResultRowDestination:
                                _currentRow = Array.AsReadOnly(candidate);
                                State = ResumableStatementState.Row;
                                return ResumableStatementStepResult.Row;
                            case RowSetDestination destination:
                                TryInsertRowSet(destination.RowSetIndex, candidate, destination.Equality);
                                break;
                            default:
                                throw new InvalidOperationException(
                                    $"Validated guarded row contains unsupported destination {guardedRow.Destination.GetType().Name}.");
                        }

                        break;
                    }
                case RowGateInstruction rowGate:
                    {
                        // Same guard pipeline as GuardedRow, but emission is left to the plain ResultRow that
                        // follows. A rejected candidate jumps past the whole emit block, so it never reaches
                        // the offset/limit counters and is not charged against them.
                        var candidate = ReadRegisters(rowGate.Values);
                        if (EvaluateRowGuards(rowGate.Guards, candidate))
                            AdvanceInstructionPointer();
                        else
                            _instructionPointer = rowGate.RejectTarget;

                        break;
                    }
                case OffsetGateInstruction offsetGate:
                    {
                        // Skip the first `offset` candidate rows: while the counter is positive, decrement
                        // it and jump to the loop-advance instruction after the gated result row. Skipped
                        // rows never reach the limit gate, so they are not counted against LIMIT.
                        var counter = _registers[offsetGate.Counter.Index];
                        if (counter.Kind == SqlValueKind.Integer && counter.AsInteger() > 0)
                        {
                            _registers[offsetGate.Counter.Index] = SqlValue.Integer(counter.AsInteger() - 1);
                            _instructionPointer = offsetGate.SkipTarget;
                        }
                        else
                        {
                            AdvanceInstructionPointer();
                        }

                        break;
                    }
                case LimitGateInstruction limitGate:
                    {
                        // Emit exactly `limit` rows: while the counter is positive, decrement it and fall
                        // through so the gated result row is emitted; once it reaches zero, jump to the
                        // program's terminating Halt so no further rows are produced.
                        var counter = _registers[limitGate.Counter.Index];
                        if (counter.Kind == SqlValueKind.Integer && counter.AsInteger() > 0)
                        {
                            _registers[limitGate.Counter.Index] = SqlValue.Integer(counter.AsInteger() - 1);
                            AdvanceInstructionPointer();
                        }
                        else
                        {
                            _instructionPointer = limitGate.DoneTarget;
                        }

                        break;
                    }
                case BeginTransactionInstruction:
                    _transaction.Begin(_registers);
                    AdvanceInstructionPointer();
                    break;
                case CommitTransactionInstruction:
                    _transaction.Commit();
                    AdvanceInstructionPointer();
                    break;
                case RollbackTransactionInstruction:
                    _transaction.Rollback(_registers);
                    AdvanceInstructionPointer();
                    break;
                case SavepointInstruction savepoint:
                    _transaction.Savepoint(savepoint.Name, _registers);
                    AdvanceInstructionPointer();
                    break;
                case ReleaseSavepointInstruction release:
                    _transaction.Release(release.Name);
                    AdvanceInstructionPointer();
                    break;
                case RollbackToSavepointInstruction rollbackTo:
                    _transaction.RollbackTo(rollbackTo.Name, _registers);
                    AdvanceInstructionPointer();
                    break;
                case OpenWorkTableInstruction openWorkTable:
                    OpenWorkTable(openWorkTable);
                    AdvanceInstructionPointer();
                    break;
                case SeedWorkTableInstruction seed:
                    {
                        var runtime = RequireOpenWorkTable(seed.WorkTable);
                        runtime.Seed(ReadRegisters(seed.Row));
                        AdvanceInstructionPointer();
                        break;
                    }
                case WorkTableStepInstruction step:
                    {
                        // Dequeue the next frontier row in FIFO (breadth-first) order into the destination
                        // register block and remember it as the worktable's current row, or fall through to
                        // the loop-exit target when the frontier is drained.
                        var runtime = RequireOpenWorkTable(step.WorkTable);
                        if (runtime.TryStep(out var row))
                        {
                            Array.Copy(row, 0, _registers, step.Destination.Start.Index, row.Length);
                            AdvanceInstructionPointer();
                        }
                        else
                        {
                            _instructionPointer = step.DoneTarget;
                        }

                        break;
                    }
                case WorkTableExpandInstruction expand:
                    {
                        // Expand the current frontier row (held in the source registers) one generation
                        // deeper, enqueuing each descendant under the worktable's dedup and guards. Produces
                        // no result row: the loop's ResultRow emits the dequeued row, this only grows the queue.
                        var runtime = RequireOpenWorkTable(expand.WorkTable);
                        runtime.Expand(ReadRegisters(expand.Source), expand.Transform);
                        AdvanceInstructionPointer();
                        break;
                    }
                case WorkTableExpandGenerationInstruction expandGeneration:
                    {
                        var runtime = RequireOpenWorkTable(expandGeneration.WorkTable);
                        runtime.ExpandGeneration(
                            ReadRegisters(expandGeneration.Source),
                            expandGeneration.Transform);
                        AdvanceInstructionPointer();
                        break;
                    }
                case CloseWorkTableInstruction closeWorkTable:
                    CloseWorkTable(closeWorkTable.WorkTable);
                    AdvanceInstructionPointer();
                    break;
                case OpenWindowBufferInstruction openWindowBuffer:
                    OpenWindowBuffer(openWindowBuffer);
                    AdvanceInstructionPointer();
                    break;
                case WindowBufferInsertInstruction windowInsert:
                    {
                        var runtime = RequireOpenWindowBuffer(windowInsert.Buffer);
                        runtime.Insert(ReadRegisters(windowInsert.Record));
                        AdvanceInstructionPointer();
                        break;
                    }
                case WindowBufferComputeInstruction windowCompute:
                    {
                        // Ends the buffered phase: the whole buffer is handed to the window evaluator once,
                        // which is what makes forward-looking and peer-relative frames representable, then
                        // the buffer positions on its first row so the drain loop can emit.
                        var runtime = RequireOpenWindowBuffer(windowCompute.Buffer);
                        if (runtime.Compute())
                            AdvanceInstructionPointer();
                        else
                            _instructionPointer = windowCompute.EmptyTarget;

                        break;
                    }
                case WindowBufferDataInstruction windowData:
                    {
                        var runtime = RequireOpenWindowBuffer(windowData.Buffer);
                        var record = runtime.Current();
                        Array.Copy(record, 0, _registers, windowData.Destination.Start.Index, record.Length);
                        AdvanceInstructionPointer();
                        break;
                    }
                case WindowBufferNextInstruction windowNext:
                    {
                        var runtime = RequireOpenWindowBuffer(windowNext.Buffer);
                        if (runtime.MoveNext())
                            _instructionPointer = windowNext.LoopTarget;
                        else
                            AdvanceInstructionPointer();

                        break;
                    }
                case CloseWindowBufferInstruction closeWindowBuffer:
                    CloseWindowBuffer(closeWindowBuffer.Buffer);
                    AdvanceInstructionPointer();
                    break;
                case HaltInstruction halt:
                    {
                        Array.Clear(_openCursors);
                        try
                        {
                            DisposeExecutionResources();
                        }
                        catch
                        {
                            State = ResumableStatementState.Faulted;
                            throw;
                        }
                        Array.Clear(_windowBuffers);
                        Array.Clear(_ephemeralTables);
                        if (halt.ErrorCode != 0)
                            throw CreateHaltException(halt);

                        AdvanceInstructionPointer();
                        State = ResumableStatementState.Done;
                        return ResumableStatementStepResult.Done;
                    }
                case HaltIfNullInstruction haltIfNull:
                    {
                        if (_registers[haltIfNull.Target.Index].Kind == SqlValueKind.Null)
                        {
                            Array.Clear(_openCursors);
                            try
                            {
                                DisposeExecutionResources();
                            }
                            catch
                            {
                                State = ResumableStatementState.Faulted;
                                throw;
                            }
                            Array.Clear(_windowBuffers);
                            Array.Clear(_ephemeralTables);
                            throw CreateHaltIfNullException(haltIfNull);
                        }

                        AdvanceInstructionPointer();
                        break;
                    }
                default:
                    throw new InvalidOperationException(
                        $"Validated VDBE program contains unsupported opcode {instruction.Opcode}.");
            }
        }

        throw new InvalidOperationException("Validated VDBE program ended without halting.");
    }

    public void Resume()
    {
        ThrowIfDisposed();
        if (State != ResumableStatementState.Yielded)
            throw new InvalidOperationException("Only a yielded statement can be resumed.");

        State = ResumableStatementState.Ready;
    }

    public void Reset()
    {
        ThrowIfDisposed();

        Array.Clear(_registers);
        Array.Clear(_openCursors);
        Array.Clear(_cursorPositions);
        Array.Clear(_skipLastInsertRowId);
        Array.Clear(_materializedRows);
        Array.Clear(_materializedRowIds);
        DisposeExecutionResources();
        Array.Clear(_accumulatorContexts);
        Array.Clear(_accumulatorInitialized);
        Array.Clear(_distinctSets);
        Array.Clear(_groupIndexes);
        Array.Clear(_rowSetPositions);
        _integerRowSets.Clear();
        foreach (var subprogram in _subprogramStatements.Values)
            subprogram.Reset();
        Array.Clear(_workTables);
        Array.Clear(_windowBuffers);
        Array.Clear(_ephemeralTables);
        // Owned transactions reset with the statement. A shared connection-scoped transaction
        // keeps its frames and deferred FK counter so multi-statement VDBE programs can share them.
        if (_ownsTransaction)
            _transaction.Reset();
        _currentRow = null;
        _instructionPointer = default;
        _hasExecutedInstruction = false;
        _fkImmediateViolations = 0;
        // Deferred FK violations live on the transaction while open; the statement-local
        // copy is only used in autocommit and is cleared on every Reset.
        if (!_transaction.InTransaction)
            _fkDeferredViolations = 0;
        RowsAffected = 0;
        LastInsertRowId = null;
        LastVirtualTableRowId = null;
        // The parameter binding is intentionally preserved across Reset, mirroring SQLite's
        // sqlite3_reset (which rewinds execution but keeps bindings), so a program re-runs with the same
        // parameters. Rebind replaces it explicitly.
        State = ResumableStatementState.Ready;
    }

    private int GetDeferredForeignKeyViolations()
        => _transaction.InTransaction
            ? _transaction.DeferredForeignKeyViolations
            : _fkDeferredViolations;

    /// <summary>
    /// Replaces the statement's parameter binding, so the next run reads fresh late-bound values without
    /// rebuilding the program. The binding's width must match the program's
    /// <see cref="VdbeProgram.ParameterSlotCount"/>. Rebinding is only allowed from the
    /// <see cref="ResumableStatementState.Ready"/> state (a freshly constructed statement or one that has
    /// been <see cref="Reset"/>), so it can never change parameters that an in-flight run has already read.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="parameterBinding"/> is null.</exception>
    /// <exception cref="VdbeParameterBindingException">The binding's width does not match the program.</exception>
    /// <exception cref="InvalidOperationException">The statement is not in the Ready state.</exception>
    public void Rebind(VdbeParameterBinding parameterBinding)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(parameterBinding);
        if (State != ResumableStatementState.Ready || _hasExecutedInstruction)
        {
            throw new InvalidOperationException(
                "Parameters can only be rebound from the Ready state; call Reset before rebinding a statement that has started, yielded, or finished.");
        }

        ValidateBindingWidth(Program, parameterBinding);
        _binding = parameterBinding;
    }

    public SqlValue GetRegister(Register register)
    {
        ThrowIfDisposed();
        ValidateRegister(register);
        return _registers[register.Index];
    }

    public bool IsCursorOpen(Cursor cursor)
    {
        ThrowIfDisposed();
        ValidateCursor(cursor);
        return _openCursors[cursor.Index];
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Array.Clear(_registers);
        Array.Clear(_openCursors);
        Array.Clear(_materializedRows);
        DisposeExecutionResources();
        Array.Clear(_accumulatorContexts);
        Array.Clear(_accumulatorInitialized);
        Array.Clear(_distinctSets);
        Array.Clear(_groupIndexes);
        Array.Clear(_rowSetPositions);
        _integerRowSets.Clear();
        foreach (var subprogram in _subprogramStatements.Values)
            subprogram.Dispose();
        _subprogramStatements.Clear();
        Array.Clear(_workTables);
        Array.Clear(_windowBuffers);
        if (_ownsTransaction)
            _transaction.Reset();
        _binding = null;
        _currentRow = null;
        State = ResumableStatementState.Disposed;
        _disposed = true;
    }

    private void OpenCursor(Cursor cursor)
    {
        if (_openCursors[cursor.Index])
            throw new InvalidOperationException($"Cursor {cursor.Index} is already open.");

        _openCursors[cursor.Index] = true;
    }

    private void CloseCursor(Cursor cursor)
    {
        if (!_openCursors[cursor.Index])
            throw new InvalidOperationException($"Cursor {cursor.Index} is not open.");

        _openCursors[cursor.Index] = false;
        _virtualCursors[cursor.Index]?.Dispose();
        _virtualCursors[cursor.Index] = null;
        _indexMethodCursors[cursor.Index]?.Dispose();
        _indexMethodCursors[cursor.Index] = null;
    }

    private bool ExecuteSubprogram(ProgramInstruction instruction, CancellationToken cancellationToken)
    {
        var instructionOffset = _instructionPointer.Offset;
        var hasCachedSubprogram = _subprogramStatements.TryGetValue(instructionOffset, out var subprogram);
        if (!hasCachedSubprogram
            || (instruction.Subprogram.RequiresFreshRuntime
                && subprogram!.State != ResumableStatementState.Yielded))
        {
            subprogram?.Dispose();
            subprogram = instruction.Subprogram.CreateRuntime(
                CreateSubprogramBinding(instruction),
                _executionOptions,
                _memory);
            _subprogramStatements[instructionOffset] = subprogram;
        }
        else if (subprogram!.State == ResumableStatementState.Yielded)
        {
            subprogram.Resume();
        }
        else
        {
            subprogram.Reset();
            if (instruction.ParameterRegisters.Count != 0)
                subprogram.Rebind(CreateSubprogramBinding(instruction)!);
        }

        while (true)
        {
            switch (subprogram.StepResumable(cancellationToken))
            {
                case ResumableStatementStepResult.Row:
                    continue;
                case ResumableStatementStepResult.Yielded:
                    State = ResumableStatementState.Yielded;
                    return true;
                case ResumableStatementStepResult.Done:
                    AdvanceInstructionPointer();
                    return false;
                default:
                    throw new InvalidOperationException("Nested VDBE program returned an unknown step result.");
            }
        }
    }

    private VdbeParameterBinding? CreateSubprogramBinding(ProgramInstruction instruction)
    {
        if (instruction.ParameterRegisters.Count == 0)
            return null;

        var values = new SqlValue[instruction.ParameterRegisters.Count];
        for (var index = 0; index < values.Length; index++)
            values[index] = _registers[instruction.ParameterRegisters[index].Index];
        return VdbeParameterBinding.FromValues(values);
    }

    private void OpenSorter(OpenSorterInstruction instruction)
    {
        if (_sorters[instruction.Sorter.Index] is not null)
            throw new InvalidOperationException($"Sorter {instruction.Sorter.Index} is already open.");

        _sorters[instruction.Sorter.Index] = new SorterRuntime(
            instruction.Comparer,
            instruction.ColumnCount,
            instruction.BufferRowCapacity,
            _executionOptions,
            _memory);
    }

    private void CloseSorter(Sorter sorter)
    {
        var runtime = _sorters[sorter.Index];
        if (runtime is null)
            throw new InvalidOperationException($"Sorter {sorter.Index} is not open.");

        runtime.Dispose();
        _sorters[sorter.Index] = null;
    }

    // Disposes every non-null sorter (releasing any spill temp files) and clears the
    // slots. Called from Halt, Reset, and Dispose so a spilled sorter never leaks its
    // temp file, even when the program ends mid-drain or is aborted.
    private void DisposeAllSorters()
    {
        Exception? cleanupFailure = null;
        for (var index = 0; index < _sorters.Length; index++)
        {
            var sorter = _sorters[index];
            if (sorter is null)
                continue;

            try
            {
                sorter.Dispose();
                _sorters[index] = null;
            }
            catch (Exception exception)
            {
                cleanupFailure ??= exception;
            }
        }

        if (cleanupFailure is not null)
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }

    private void DisposeAllJoinCursors()
    {
        Exception? cleanupFailure = null;
        for (var index = 0; index < _joinCursorStates.Length; index++)
        {
            var joinState = _joinCursorStates[index];
            try
            {
                joinState?.Close();
                _joinCursorStates[index] = null;
            }
            catch (Exception exception)
            {
                cleanupFailure ??= exception;
            }
        }

        if (cleanupFailure is not null)
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }

    private void DisposeAllVirtualCursors()
    {
        List<Exception>? cleanupFailures = null;
        for (var index = 0; index < _virtualCursors.Length; index++)
        {
            try
            {
                _virtualCursors[index]?.Dispose();
                _virtualCursors[index] = null;
            }
            catch (Exception exception)
            {
                (cleanupFailures ??= []).Add(exception);
            }
            try
            {
                _indexMethodCursors[index]?.Dispose();
                _indexMethodCursors[index] = null;
            }
            catch (Exception exception)
            {
                (cleanupFailures ??= []).Add(exception);
            }
        }

        ThrowCleanupFailures(cleanupFailures);
    }

    private void DisposeExecutionResources()
    {
        List<Exception>? cleanupFailures = null;
        TryDispose(DisposeAllJoinCursors, ref cleanupFailures);
        TryDispose(DisposeAllSorters, ref cleanupFailures);
        TryDispose(DisposeAllVirtualCursors, ref cleanupFailures);
        ThrowCleanupFailures(cleanupFailures);
    }

    private static void TryDispose(Action dispose, ref List<Exception>? failures)
    {
        try
        {
            dispose();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }
    }

    private static void ThrowCleanupFailures(List<Exception>? failures)
    {
        if (failures is [var failure])
            ExceptionDispatchInfo.Capture(failure).Throw();
        if (failures is { Count: > 1 })
            throw new AggregateException(failures);
    }

    private SorterRuntime RequireOpenSorter(Sorter sorter)
        => _sorters[sorter.Index]
            ?? throw new InvalidOperationException($"Sorter {sorter.Index} is not open.");

    private void OpenWindowBuffer(OpenWindowBufferInstruction instruction)
    {
        if (_windowBuffers[instruction.Buffer.Index] is not null)
            throw new InvalidOperationException($"Window buffer {instruction.Buffer.Index} is already open.");

        _windowBuffers[instruction.Buffer.Index] = new WindowBufferRuntime(
            instruction.ColumnCount,
            instruction.WindowCount,
            instruction.Evaluator);
    }

    private void CloseWindowBuffer(WindowBuffer buffer)
    {
        if (_windowBuffers[buffer.Index] is null)
            throw new InvalidOperationException($"Window buffer {buffer.Index} is not open.");

        _windowBuffers[buffer.Index] = null;
    }

    private WindowBufferRuntime RequireOpenWindowBuffer(WindowBuffer buffer)
        => _windowBuffers[buffer.Index]
            ?? throw new InvalidOperationException($"Window buffer {buffer.Index} is not open.");

    private void OpenWorkTable(OpenWorkTableInstruction instruction)
    {
        if (_workTables[instruction.WorkTable.Index] is not null)
            throw new InvalidOperationException($"Work table {instruction.WorkTable.Index} is already open.");

        _workTables[instruction.WorkTable.Index] = new WorkTableRuntime(
            instruction.ColumnCount,
            instruction.Mode,
            instruction.MaxRows,
            instruction.MaxDepth,
            instruction.Equality);
    }

    private void CloseWorkTable(WorkTable workTable)
    {
        if (_workTables[workTable.Index] is null)
            throw new InvalidOperationException($"Work table {workTable.Index} is not open.");

        _workTables[workTable.Index] = null;
    }

    private WorkTableRuntime RequireOpenWorkTable(WorkTable workTable)
        => _workTables[workTable.Index]
            ?? throw new InvalidOperationException($"Work table {workTable.Index} is not open.");

    // The binding a LoadParameter opcode reads. A program that references parameter slots must be run
    // with a matching binding; a missing binding is a hard error rather than a silent NULL, so an unbound
    // parameter can never be mistaken for a bound NULL value.
    private VdbeParameterBinding RequireBinding()
        => _binding
            ?? throw new VdbeParameterBindingException(
                $"The program reads {Program.ParameterSlotCount} parameter slot(s) but no binding was supplied; construct the statement with a binding or call Rebind.");

    private static void ValidateBindingWidth(VdbeProgram program, VdbeParameterBinding binding)
    {
        if (binding.Count != program.ParameterSlotCount)
        {
            throw new VdbeParameterBindingException(
                $"The binding supplies {binding.Count} parameter slot(s) but the program declares {program.ParameterSlotCount}.");
        }
    }

    private VdbeCursorSource RequireCursorSource(Cursor cursor)
    {
        if (_ephemeralTables[cursor.Index] is { } ephemeral)
            return ephemeral.AsCursorSource();

        var source = _cursorSources is not null && cursor.Index < _cursorSources.Count
            ? _cursorSources[cursor.Index]
            : null;

        return source
            ?? throw new InvalidOperationException(
                $"Cursor {cursor.Index} has no bound row source.");
    }

    private EphemeralTableRuntime RequireEphemeralTable(Cursor cursor)
        => _ephemeralTables[cursor.Index]
            ?? throw new InvalidOperationException(
                $"Cursor {cursor.Index} is not an open ephemeral table.");

    private VdbeWriteTarget? WriteTargetOrNull(Cursor cursor)
        => _writeTargets is not null && cursor.Index < _writeTargets.Count
            ? _writeTargets[cursor.Index]
            : null;

    private VdbeWriteTarget RequireWriteTarget(Cursor cursor)
        => WriteTargetOrNull(cursor)
            ?? throw new InvalidOperationException(
                $"Cursor {cursor.Index} has no bound write target.");

    private ManagedVirtualTable RequireVirtualTable(Cursor cursor)
    {
        var binding = _virtualTableBindings is not null && cursor.Index < _virtualTableBindings.Count
            ? _virtualTableBindings[cursor.Index]
            : null;
        return binding?.Table
            ?? throw new InvalidOperationException(
                $"Cursor {cursor.Index} has no bound managed virtual table.");
    }

    private ManagedVirtualTableCursor RequireVirtualCursor(Cursor cursor)
        => _virtualCursors[cursor.Index]
            ?? throw new InvalidOperationException(
                $"Cursor {cursor.Index} is not an open managed virtual-table cursor.");

    private Indexing.ManagedIndexMethodCursor OpenIndexMethodCursor(Cursor cursor, VdbeIndexMethodBinding binding)
    {
        if (_indexMethodCursors[cursor.Index] is { } existing)
            return existing;

        var opened = binding.Attachment.Open(binding.Source);
        _indexMethodCursors[cursor.Index] = opened;
        return opened;
    }

    private Indexing.ManagedIndexMethodCursor RequireIndexMethodCursor(Cursor cursor)
        => _indexMethodCursors[cursor.Index]
            ?? throw new InvalidOperationException(
                $"Cursor {cursor.Index} is not an open managed index-method cursor.");

    // A cursor's iteration length comes from its write target (INSERT value rows or
    // scanned UPDATE/DELETE rows) or, failing that, its read source. Streaming join
    // cursors never call this: Rewind/Next branch on a join state first and advance
    // the enumerator directly, since the row count is not known up front.
    private int CursorRowCount(Cursor cursor)
    {
        if (_joinCursorStates[cursor.Index] is not null)
        {
            throw new InvalidOperationException(
                $"Join cursor {cursor.Index} has no precomputed row count; it must be advanced via the streaming enumerator.");
        }

        var writeTarget = WriteTargetOrNull(cursor);
        return writeTarget is not null
            ? writeTarget.RowCount
            : RequireCursorSource(cursor).Rows.Count;
    }

    // Runs a mutation delegate for the current position and materializes the written
    // (row, rowid) so a following Column/RowId observes the new values, not the source.
    private void MutateCursorRow(Cursor cursor, VdbeInsertFlags flags = VdbeInsertFlags.None)
    {
        if ((flags & VdbeInsertFlags.RequireSeek) != 0)
            EnsureCursorPositioned(cursor);

        var target = RequireWriteTarget(cursor);
        var mutate = target.MutateRow
            ?? throw new InvalidOperationException(
                $"Cursor {cursor.Index} has no mutation action bound.");

        // Capture pre-mutation rowid when UPDATE changes the row's rowid (Turso UPDATE_ROWID_CHANGE).
        // Forces a positioned read of the old key before the write mutates the cursor.
        if ((flags & VdbeInsertFlags.UpdateRowidChange) != 0
            && IsCursorPositioned(cursor))
        {
            _ = CurrentCursorRowId(cursor);
        }

        var mutation = mutate(_cursorPositions[cursor.Index]);
        _materializedRows[cursor.Index] = mutation.Row;
        _materializedRowIds[cursor.Index] = mutation.RowId;

        // Track whether last_insert_rowid() must stay frozen across Commit for this cursor.
        // Intermediate multi-row INSERT steps set SkipLastRowid; only the final write clears it.
        if ((flags & VdbeInsertFlags.SkipLastRowid) != 0)
            _skipLastInsertRowId[cursor.Index] = true;
        else if (target.Commit is not null)
            _skipLastInsertRowId[cursor.Index] = false;

        // SkipLastRowid is honored at Commit; mutation still records the rowid for Column/RowId.
        if ((flags & (VdbeInsertFlags.SkipStatementChangeCount | VdbeInsertFlags.SkipAllChangeCounts)) == 0)
            RowsAffected = checked(RowsAffected + 1);
    }

    private void EnsureCursorPositioned(Cursor cursor)
    {
        if (IsCursorPositioned(cursor))
            return;

        throw new InvalidOperationException(
            $"Cursor {cursor.Index} requires a prior seek before this write (VdbeInsertFlags.RequireSeek).");
    }

    private bool IsCursorPositioned(Cursor cursor)
    {
        if (_materializedRows[cursor.Index] is not null)
            return true;

        if (_joinCursorStates[cursor.Index] is { CurrentRow: not null })
            return true;

        var position = _cursorPositions[cursor.Index];
        return position >= 0 && position < CursorRowCount(cursor);
    }

    private SqlValue[] CurrentCursorRow(Cursor cursor)
    {
        // A mutation opcode materializes the written row; until then the row comes
        // from the write target's scan rows (UPDATE/DELETE) or the read source.
        if (_materializedRows[cursor.Index] is { } materialized)
            return materialized;

        // A streaming join cursor serves the row the enumerator currently rests on; it
        // has no random-access row list and no precomputed count.
        if (_joinCursorStates[cursor.Index] is { } joinState)
            return joinState.CurrentRow
                ?? throw new InvalidOperationException($"Cursor {cursor.Index} is not positioned on a row.");

        var position = _cursorPositions[cursor.Index];
        var count = CursorRowCount(cursor);
        if (position < 0 || position >= count)
            throw new InvalidOperationException($"Cursor {cursor.Index} is not positioned on a row.");

        var writeTarget = WriteTargetOrNull(cursor);
        if (writeTarget?.GetRow is { } getRow)
            return getRow(position);

        return RequireCursorSource(cursor).Rows[position];
    }

    private long CurrentCursorRowId(Cursor cursor)
    {
        if (_virtualCursors[cursor.Index] is { } virtualCursor)
            return virtualCursor.RowId;

        if (_joinCursorStates[cursor.Index] is not null)
        {
            throw new InvalidOperationException(
                $"Join cursor {cursor.Index} exposes source rowids as hidden columns, not as one cursor rowid.");
        }

        if (_materializedRows[cursor.Index] is not null)
            return _materializedRowIds[cursor.Index];

        var position = _cursorPositions[cursor.Index];
        var count = CursorRowCount(cursor);
        if (position < 0 || position >= count)
            throw new InvalidOperationException($"Cursor {cursor.Index} is not positioned on a row.");

        var source = _cursorSources is not null && cursor.Index < _cursorSources.Count
            ? _cursorSources[cursor.Index]
            : null;
        if (source?.RowIds is { } rowIds)
            return rowIds[position];

        var target = RequireWriteTarget(cursor);
        var getRowId = target.GetRowId
            ?? throw new InvalidOperationException(
                $"Cursor {cursor.Index} has no rowid source bound.");
        return getRowId(position);
    }

    private SqlValue[] ReadRegisters(RegisterRange range)
    {
        var values = new SqlValue[range.Count];
        Array.Copy(_registers, range.Start.Index, values, 0, range.Count);
        return values;
    }

    // Whether a long rowid satisfies the supplied comparison against a bound. Used by the
    // SeekRowidRange handler to find the first rowid that satisfies the start predicate.
    private static bool Satisfies(long rowId, VdbeComparisonOperator op, long bound)
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

    // Whether the candidate is present in the row set under the supplied equality. An unpopulated set
    // (null) holds no rows, so membership is false — INTERSECT against an empty term yields nothing and
    // EXCEPT against an empty term keeps every candidate.
    // Runs a guard list over one candidate tuple, in order, with the same accept/insert semantics for both
    // GuardedRow (which emits or row-set-inserts inline) and RowGate (which defers emission to a following
    // ResultRow). A DistinctRowGuard inserts on accept, so a tuple later discarded by OFFSET still counts as
    // seen — matching the evaluator, which de-duplicates before applying OFFSET.
    private bool EvaluateRowGuards(IReadOnlyList<VdbeRowGuard> guards, SqlValue[] candidate)
    {
        foreach (var guard in guards)
        {
            switch (guard)
            {
                case DistinctRowGuard distinctGuard:
                    if (!TryInsertRowSet(distinctGuard.RowSetIndex, candidate, distinctGuard.Equality))
                        return false;

                    break;
                case MembershipRowGuard membershipGuard:
                    foreach (var rowSetIndex in membershipGuard.RowSetIndices)
                    {
                        var contained = RowSetContains(rowSetIndex, candidate, membershipGuard.Equality);
                        var required = membershipGuard.Mode == CompoundMembershipMode.PresentInAll;
                        if (contained != required)
                            return false;
                    }

                    break;
                default:
                    throw new InvalidOperationException(
                        $"Validated row guard list contains unsupported guard {guard.GetType().Name}.");
            }
        }

        return true;
    }

    private bool RowSetContains(int rowSetIndex, SqlValue[] candidate, VdbeRowEquality equality)
    {
        var set = _distinctSets[rowSetIndex];
        if (set is null)
            return false;

        foreach (var stored in set)
        {
            if (equality(stored, candidate))
                return true;
        }

        return false;
    }

    private bool TryInsertRowSet(int rowSetIndex, SqlValue[] candidate, VdbeRowEquality equality)
    {
        if (RowSetContains(rowSetIndex, candidate, equality))
            return false;

        (_distinctSets[rowSetIndex] ??= []).Add(candidate);
        return true;
    }

    private void CopyRowSetRow(SqlValue[] row, RegisterRange destination)
    {
        if (row.Length != destination.Count)
        {
            throw new InvalidOperationException(
                $"Row-set row has {row.Length} columns but destination has {destination.Count} registers.");
        }

        Array.Copy(row, 0, _registers, destination.Start.Index, row.Length);
    }

    /// <summary>
    /// Positions <paramref name="cursor"/> on the integer rowid held in
    /// <paramref name="rowIdRegister"/>. Returns false when the key is missing or
    /// not an integer (caller jumps to the not-found target).
    /// </summary>
    private bool TryPositionCursorOnRowId(Cursor cursor, Register rowIdRegister)
    {
        _materializedRows[cursor.Index] = null;
        var source = RequireCursorSource(cursor);
        var rowIds = source.RowIds;
        if (rowIds is null)
            return false;

        var sought = _registers[rowIdRegister.Index];
        if (sought.Kind != SqlValueKind.Integer)
            return false;

        var target = sought.AsInteger();
        for (var i = 0; i < rowIds.Count; i++)
        {
            if (rowIds[i] != target)
                continue;

            _cursorPositions[cursor.Index] = i;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Positions <paramref name="cursor"/> on the first row whose leading columns equal
    /// <paramref name="key"/>. Returns false when any key register is NULL (Turso
    /// NoConflict: NULL never conflicts) or when no row matches.
    /// </summary>
    private bool TryPositionCursorOnKeyPrefix(Cursor cursor, RegisterRange key)
    {
        _materializedRows[cursor.Index] = null;
        var keyValues = ReadRegisters(key);
        for (var i = 0; i < keyValues.Length; i++)
        {
            if (keyValues[i].Kind == SqlValueKind.Null)
                return false;
        }

        var source = RequireCursorSource(cursor);
        var rows = source.Rows;
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            if (!RowMatchesKeyPrefix(rows[rowIndex], keyValues))
                continue;

            _cursorPositions[cursor.Index] = rowIndex;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Positions the cursor for SeekGE/GT/LE/LT (and Idx* aliases). GE/GT take the first
    /// qualifying row in scan order; LE/LT take the last. EqOnly requires an exact match
    /// on GE/LE (Turso eq_only). Comparison uses SqlValue binary equality/order via
    /// <see cref="CompareKeyPrefix"/>.
    /// </summary>
    private bool TrySeekKey(
        Cursor cursor,
        RegisterRange key,
        VdbeKeySeekOperator op,
            bool eqOnly,
            IReadOnlyList<int>? keyColumns = null)
    {
        _materializedRows[cursor.Index] = null;
        var keyValues = ReadRegisters(key);
        if (keyColumns is not null && keyColumns.Count != keyValues.Length)
        {
            throw new InvalidOperationException(
                $"SeekKey key width {keyValues.Length} does not match KeyColumns length {keyColumns.Count}.");
        }

        var source = RequireCursorSource(cursor);
        var rows = source.Rows;
        var found = -1;

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var cmp = CompareKeyPrefix(rows[rowIndex], keyValues, keyColumns);
            var qualifies = op switch
            {
                VdbeKeySeekOperator.GreaterThanOrEqual => cmp >= 0,
                VdbeKeySeekOperator.GreaterThan => cmp > 0,
                VdbeKeySeekOperator.LessThanOrEqual => cmp <= 0,
                VdbeKeySeekOperator.LessThan => cmp < 0,
                _ => false,
            };
            if (!qualifies)
                continue;

            if (eqOnly
                && op is VdbeKeySeekOperator.GreaterThanOrEqual or VdbeKeySeekOperator.LessThanOrEqual
                && cmp != 0)
            {
                continue;
            }

            // GE/GT: first match; LE/LT: last match.
            if (op is VdbeKeySeekOperator.GreaterThanOrEqual or VdbeKeySeekOperator.GreaterThan)
            {
                found = rowIndex;
                break;
            }

            found = rowIndex;
        }

        if (found < 0)
            return false;

        _cursorPositions[cursor.Index] = found;
        return true;
    }

    private static bool RowMatchesKeyPrefix(SqlValue[] row, SqlValue[] key)
    {
        if (row.Length < key.Length)
            return false;

        for (var column = 0; column < key.Length; column++)
        {
            if (!row[column].Equals(key[column]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Lexicographic comparison of selected row columns against key. Nulls sort
    /// lowest (SQLite default BINARY). Returns negative if row &lt; key, 0 if equal
    /// for the key width, positive if row &gt; key. When <paramref name="keyColumns"/>
    /// is null, uses leading columns <c>0..key.Length-1</c>.
    /// </summary>
    private static int CompareKeyPrefix(
        SqlValue[] row,
        SqlValue[] key,
        IReadOnlyList<int>? keyColumns = null)
    {
        var width = key.Length;
        for (var column = 0; column < width; column++)
        {
            var rowOrdinal = keyColumns is null ? column : keyColumns[column];
            if (rowOrdinal < 0 || rowOrdinal >= row.Length)
                return -1;

            var cmp = CompareSqlValues(row[rowOrdinal], key[column]);
            if (cmp != 0)
                return cmp;
        }

        return 0;
    }

    private static int CompareSqlValues(SqlValue left, SqlValue right)
    {
        if (left.Kind == SqlValueKind.Null && right.Kind == SqlValueKind.Null)
            return 0;
        if (left.Kind == SqlValueKind.Null)
            return -1;
        if (right.Kind == SqlValueKind.Null)
            return 1;
        if (left.Equals(right))
            return 0;

        // Prefer numeric order when both are numeric; otherwise ordinal text / kind order.
        if (TryAsDouble(left, out var leftNum) && TryAsDouble(right, out var rightNum))
            return leftNum.CompareTo(rightNum);

        if (left.Kind == SqlValueKind.Text && right.Kind == SqlValueKind.Text)
            return string.CompareOrdinal(left.AsText(), right.AsText());

        // Mixed/non-text kinds: order by kind then fall back to inequality.
        var kindOrder = left.Kind.CompareTo(right.Kind);
        return kindOrder != 0 ? kindOrder : left.Equals(right) ? 0 : -1;
    }

    private static bool TryAsDouble(SqlValue value, out double number)
    {
        switch (value.Kind)
        {
            case SqlValueKind.Integer:
                number = value.AsInteger();
                return true;
            case SqlValueKind.Real:
                number = value.AsReal();
                return true;
            default:
                number = 0;
                return false;
        }
    }

    private void ExecuteIdxInsert(IdxInsertInstruction idxInsert)
    {
        var ephemeral = RequireEphemeralTable(idxInsert.Cursor);
        var key = ReadRegisters(idxInsert.Key);
        if ((idxInsert.Flags & VdbeIdxInsertFlags.NoOpDuplicate) != 0
            && ephemeral.ContainsKeyPrefix(key))
        {
            return;
        }

        ephemeral.Insert(key);
        if ((idxInsert.Flags & VdbeIdxInsertFlags.NChange) != 0)
            RowsAffected = checked(RowsAffected + 1);
    }

    private void ExecuteIdxDelete(IdxDeleteInstruction idxDelete)
    {
        var ephemeral = RequireEphemeralTable(idxDelete.Cursor);
        if (idxDelete.Key is { } keyRange)
        {
            var key = ReadRegisters(keyRange);
            if (!ephemeral.TryDeleteKeyPrefix(key))
                return;
        }
        else
        {
            var position = _cursorPositions[idxDelete.Cursor.Index];
            if (!ephemeral.TryDeleteAt(position))
                return;
            _cursorPositions[idxDelete.Cursor.Index] = -1;
        }

        _materializedRows[idxDelete.Cursor.Index] = null;
    }

    private Exception CreateHaltException(HaltInstruction halt)
    {
        var message = halt.DescriptionRegister is { } descReg
            ? FormatHaltMessage(_registers[descReg.Index])
            : halt.Description ?? string.Empty;
        message = FormatConstraintHaltMessage(halt.ErrorCode, message);

        var algorithm = halt.OnError switch
        {
            VdbeHaltOnError.Rollback => InsertConflictAlgorithm.Rollback,
            VdbeHaltOnError.Fail => InsertConflictAlgorithm.Fail,
            VdbeHaltOnError.Ignore => InsertConflictAlgorithm.Ignore,
            VdbeHaltOnError.Abort => InsertConflictAlgorithm.Abort,
            null => InsertConflictAlgorithm.Abort,
            _ => InsertConflictAlgorithm.Abort,
        };

        if (algorithm == InsertConflictAlgorithm.Ignore)
            return new TriggerIgnoreException();

        var error = new EmbeddedSqlException(message, halt.ErrorCode, algorithm);
        return algorithm switch
        {
            InsertConflictAlgorithm.Rollback => new EmbeddedConflictRollbackException(error),
            InsertConflictAlgorithm.Fail => new EmbeddedConflictFailException(error, lastInsertRowId: 0),
            _ => error,
        };
    }

    private static Exception CreateHaltIfNullException(HaltIfNullInstruction haltIfNull)
    {
        var message = FormatConstraintHaltMessage(haltIfNull.ErrorCode, haltIfNull.Description);
        return new EmbeddedSqlException(message, haltIfNull.ErrorCode, InsertConflictAlgorithm.Abort);
    }

    private static string FormatHaltMessage(SqlValue value)
        => value.Kind == SqlValueKind.Null ? string.Empty : value.AsText();

    private static string FormatConstraintHaltMessage(int errorCode, string description)
    {
        if (string.IsNullOrEmpty(description))
            description = "constraint failed";

        return errorCode switch
        {
            SqliteResultCode.ConstraintPrimaryKey
                => $"UNIQUE constraint failed: {description} ({errorCode})",
            SqliteResultCode.ConstraintUnique
                => $"UNIQUE constraint failed: {description} ({errorCode})",
            SqliteResultCode.ConstraintCheck
                => $"CHECK constraint failed: {description} ({errorCode})",
            SqliteResultCode.ConstraintNotNull
                => $"NOT NULL constraint failed: {description} ({errorCode})",
            SqliteResultCode.ConstraintForeignKey
                => description,
            SqliteResultCode.Constraint or SqliteResultCode.ConstraintTrigger
                => description.Contains("constraint", StringComparison.OrdinalIgnoreCase)
                    ? description
                    : $"constraint failed: {description}",
            _ => description,
        };
    }

    private void AdvanceInstructionPointer()
    {
        _instructionPointer = new ProgramCounter(checked(_instructionPointer.Offset + 1));
    }

    private void ValidateRegister(Register register)
    {
        if (register.Index >= Program.RegisterCount)
            throw new ArgumentOutOfRangeException(nameof(register));
    }

    private void ValidateCursor(Cursor cursor)
    {
        if (cursor.Index >= Program.CursorCount)
            throw new ArgumentOutOfRangeException(nameof(cursor));
    }

    private void FailExecution(Exception executionFailure)
    {
        State = ResumableStatementState.Faulted;
        try
        {
            Array.Clear(_openCursors);
            DisposeExecutionResources();
        }
        catch (Exception cleanupFailure)
        {
            throw new AggregateException(executionFailure, cleanupFailure);
        }

        ExceptionDispatchInfo.Capture(executionFailure).Throw();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>
    /// In-memory ephemeral table backing an <see cref="OpenEphemeralInstruction"/> cursor.
    /// Rows are append-only with sequential 1-based rowids for SeekRowid/NotExists/Found,
    /// and support IdxInsert/IdxDelete key maintenance.
    /// </summary>
    private sealed class EphemeralTableRuntime
    {
        private readonly int _columnCount;
        private readonly List<SqlValue[]> _rows = [];
        private readonly List<long> _rowIds = [];
        private VdbeCursorSource? _sourceView;
        private long _nextRowId = 1;

        public EphemeralTableRuntime(int columnCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columnCount);
            _columnCount = columnCount;
        }

        public int ColumnCount => _columnCount;

        public int RowCount => _rows.Count;

        public void Insert(SqlValue[] row)
        {
            ArgumentNullException.ThrowIfNull(row);
            if (row.Length != _columnCount)
            {
                throw new InvalidOperationException(
                    $"Ephemeral insert has {row.Length} columns but the table has {_columnCount}.");
            }

            _rows.Add(row.ToArray());
            _rowIds.Add(_nextRowId);
            _nextRowId = checked(_nextRowId + 1);
            _sourceView = null;
        }

        public bool ContainsKeyPrefix(SqlValue[] key)
        {
            foreach (var row in _rows)
            {
                if (RowMatchesKeyPrefix(row, key))
                    return true;
            }

            return false;
        }

        public bool TryDeleteKeyPrefix(SqlValue[] key)
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                if (!RowMatchesKeyPrefix(_rows[i], key))
                    continue;

                _rows.RemoveAt(i);
                _rowIds.RemoveAt(i);
                _sourceView = null;
                return true;
            }

            return false;
        }

        public bool TryDeleteAt(int position)
        {
            if (position < 0 || position >= _rows.Count)
                return false;

            _rows.RemoveAt(position);
            _rowIds.RemoveAt(position);
            _sourceView = null;
            return true;
        }

        public VdbeCursorSource AsCursorSource()
            => _sourceView ??= new VdbeCursorSource(_rows, _rowIds);
    }

    // Turso's RowSetTest keeps inserts from the current batch separate so a batch cannot match itself.
    // It is deliberately not shared with _distinctSets: that store has tuple equality and drain semantics.
    private sealed class IntegerRowSet
    {
        private readonly List<long> _pending = [];
        private readonly HashSet<long> _priorBatchValues = [];
        private int _batch;

        public void Insert(long value) => _pending.Add(value);

        public bool ContainsEarlierBatch(long value, int batch)
        {
            if (_batch != batch)
            {
                foreach (var pending in _pending)
                    _priorBatchValues.Add(pending);

                _pending.Clear();
                _batch = batch;
            }

            return _priorBatchValues.Contains(value);
        }
    }

    private sealed class GroupKeyEqualityComparer(
        VdbeGroupComparer equality,
        VdbeGroupHasher hasher) : IEqualityComparer<SqlValue[]>
    {
        public bool Equals(SqlValue[]? left, SqlValue[]? right) =>
            left is not null
            && right is not null
            && equality(left, right);

        public int GetHashCode(SqlValue[] key) => hasher(key);
    }

    // Holds one sorter's buffered records and its drain cursor. Records are copied on
    // insert so overwriting the source registers between iterations cannot mutate rows
    // already stored. Sorting is stable: equal-key rows keep their insertion order.
    //
    // When BufferRowCapacity is positive the sorter spills: once the in-memory buffer
    // exceeds the capacity it is stably sorted and flushed to a temp-file run, and Sort
    // drives a lazy k-way merge over all runs so the merged output is never materialized
    // in memory (the OOM fix). The default capacity (0 -> int.MaxValue) never spills,
    // preserving the historical in-memory behavior for every existing call site.
    private sealed class SorterRuntime : IDisposable
    {
        private readonly VdbeRowComparer _comparer;
        private readonly int _columnCount;
        private readonly int _bufferRowCapacity;
        private readonly long _bufferMemoryLimitBytes;
        private readonly VdbeExecutionOptions _executionOptions;
        private readonly VdbeExecutionMemory _memory;
        private readonly VdbePendingCleanupRegistry _pendingCleanup = new();
        private readonly List<SqlValue[]> _rows = [];
        private long _bufferedBytes;
        private long _sortWorkspaceBytes;
        private SorterSpill? _spill;
        private bool _sorted;
        private int _position = -1;
        private PriorityQueue<int, MergeKey>? _merge;
        private SorterSpill.RunReader[]? _readers;
        private SorterSpill.RowLease?[]? _mergeHeads;
        private SorterSpill.RowLease? _pending;
        private int _pendingRunIndex;
        private long _mergeInfrastructureBytes;

        public SorterRuntime(
            VdbeRowComparer comparer,
            int columnCount,
            int bufferRowCapacity,
            VdbeExecutionOptions executionOptions,
            VdbeExecutionMemory memory)
        {
            _comparer = comparer;
            _columnCount = columnCount;
            _executionOptions = executionOptions;
            _memory = memory;
            // 0 means "no spill" (the historical in-memory default). Treat anything
            // non-positive the same way so a stray negative capacity can never force a
            // single-row spill loop.
            _bufferRowCapacity = bufferRowCapacity > 0 ? bufferRowCapacity : int.MaxValue;
            _bufferMemoryLimitBytes = executionOptions.SorterMemoryLimitBytes;
        }

        public void Insert(SqlValue[] record, CancellationToken cancellationToken)
        {
            if (_sorted)
                throw new InvalidOperationException("Cannot insert into a sorter after it has been sorted.");
            if (record.Length != _columnCount)
            {
                throw new InvalidOperationException(
                    $"Sorter stores {_columnCount}-column records but received {record.Length} values.");
            }

            var recordBytes = EstimateRecordBytes(record);
            if (recordBytes > _memory.LimitBytes)
                throw new VdbeMemoryLimitExceededException(_memory.LimitBytes, recordBytes);
            if (_rows.Count >= _bufferRowCapacity)
                FlushBuffered(cancellationToken);

            if (!TryBuffer(record, recordBytes))
            {
                if (_rows.Count > 0)
                {
                    FlushBuffered(cancellationToken);
                    if (TryBuffer(record, recordBytes))
                        return;
                }

                if (!_executionOptions.AllowTemporaryFileSpill)
                    throw new VdbeMemoryLimitExceededException(_bufferMemoryLimitBytes, recordBytes);

                _memory.RetainOrThrow(recordBytes);
                try
                {
                    _spill ??= CreateSpill();
                    _spill.WriteSingleRow(record, recordBytes, cancellationToken);
                    _spill.CompactRunTiers(
                        _comparer,
                        _memory,
                        cancellationToken);
                }
                finally
                {
                    _memory.Release(recordBytes);
                }
                return;
            }
        }

        // Sorts the buffered records and positions on the first one. Returns false (and
        // leaves the sorter unpositioned) when there is nothing to drain.
        public bool Sort(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Flush the final partial buffer as one more run so Sort always drains from
            // the spill when any runs exist. An empty tail is skipped (no zero-row run).
            if ((_spill is not null || _bufferedBytes > _bufferMemoryLimitBytes) && _rows.Count > 0)
            {
                FlushBuffered(cancellationToken);
            }

            if (_spill is not null)
            {
                _sorted = true;
                if (_spill.RunCount == 0)
                {
                    _position = -1;
                    return false;
                }

                _spill.PrepareFinalMerge(
                    _comparer,
                    _executionOptions.SorterMergeFanIn,
                    _memory,
                    cancellationToken);
                StartMerge(cancellationToken);
                _position = 0;
                return true;
            }

            if (_rows.Count == 0)
            {
                _sorted = true;
                _position = -1;
                return false;
            }

            var sorted = SortBufferedRows(cancellationToken);
            _rows.Clear();
            _rows.AddRange(sorted);
            if (_sortWorkspaceBytes > 0)
            {
                _memory.Release(_sortWorkspaceBytes, rows: 0);
                _bufferedBytes -= _sortWorkspaceBytes;
                _sortWorkspaceBytes = 0;
            }
            _sorted = true;
            _position = 0;
            return true;
        }

        public SqlValue[] Current()
        {
            if (!_sorted)
                throw new InvalidOperationException("Sorter must be sorted before reading a record.");
            if (_position < 0)
                throw new InvalidOperationException("Sorter is not positioned on a record.");

            // Spill path: the current record is the head of the merge heap, staged in
            // _pending. MoveNext refills the heap and re-stages the next head.
            if (_merge is not null)
                return _pending?.Record
                    ?? throw new InvalidOperationException("Sorter is not positioned on a record.");

            if (_position >= _rows.Count)
                throw new InvalidOperationException("Sorter is not positioned on a record.");

            return _rows[_position];
        }

        // Advances to the next ordered record, returning whether one remains.
        public bool MoveNext(CancellationToken cancellationToken)
        {
            if (!_sorted)
                throw new InvalidOperationException("Sorter must be sorted before advancing.");

            // Spill path: refill the run whose head we just consumed, then pop the new
            // heap head (if any) and stage it. The run index is tracked so the refill
            // reads from the correct run — the bug this fixes is that a plain dequeue
            // drops the run association and would emit at most one record per run.
            if (_merge is not null)
            {
                var refillRunIndex = _pendingRunIndex;
                _pending?.Dispose();
                _pending = null;
                if (_readers![refillRunIndex].TryReadNext(
                    _memory,
                    out var next,
                    cancellationToken))
                {
                    _mergeHeads![refillRunIndex] = next;
                    _merge.Enqueue(
                        refillRunIndex,
                        new MergeKey(next.Record, refillRunIndex, _comparer));
                }

                if (!_merge.TryDequeue(out _pendingRunIndex, out var key))
                {
                    return false;
                }

                _pending = _mergeHeads![_pendingRunIndex];
                _mergeHeads[_pendingRunIndex] = null;
                return true;
            }

            _position++;
            return _position < _rows.Count;
        }

        // Stably sorts the in-memory buffer. Equal-key rows keep their insertion order
        // via an insertion-index tiebreak so the underlying unstable Array.Sort cannot
        // reorder them — the same invariant each spilled run preserves, which makes the
        // k-way merge globally stable.
        private List<SqlValue[]> SortBufferedRows(CancellationToken cancellationToken)
        {
            var order = new int[_rows.Count];
            for (var index = 0; index < order.Length; index++)
                order[index] = index;

            try
            {
                Array.Sort(order, (left, right) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var comparison = _comparer(_rows[left], _rows[right]);
                    cancellationToken.ThrowIfCancellationRequested();
                    return comparison != 0 ? comparison : left.CompareTo(right);
                });
            }
            catch (InvalidOperationException exception)
                when (exception.InnerException is OperationCanceledException cancellation)
            {
                ExceptionDispatchInfo.Capture(cancellation).Throw();
                throw;
            }

            var sorted = new List<SqlValue[]>(_rows.Count);
            foreach (var index in order)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sorted.Add(_rows[index]);
            }

            return sorted;
        }

        // Seeds the k-way merge heap with the first record of every run. The heap orders
        // by the row comparer, breaking ties on RunIndex (lower = earlier insertion) so
        // equal-key rows across runs keep their global insertion order — stability.
        private void StartMerge(CancellationToken cancellationToken)
        {
            var runCount = _spill!.RunCount;
            var infrastructureBytes = VdbeManagedFootprint.EstimateMergeInfrastructure(runCount);
            _memory.RetainOrThrow(infrastructureBytes, rows: 0);
            _mergeInfrastructureBytes = infrastructureBytes;
            try
            {
                _merge = new PriorityQueue<int, MergeKey>(runCount, MergeKey.Comparer);
                _readers = new SorterSpill.RunReader[runCount];
                _mergeHeads = new SorterSpill.RowLease[runCount];

                for (var runIndex = 0; runIndex < runCount; runIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var reader = _spill.OpenRunReader(runIndex);
                    _readers[runIndex] = reader;
                    if (reader.TryReadNext(_memory, out var first, cancellationToken))
                    {
                        _mergeHeads[runIndex] = first;
                        _merge.Enqueue(
                            runIndex,
                            new MergeKey(first.Record, runIndex, _comparer));
                    }
                }

                // Stage the first head so Current() can return it before the first MoveNext.
                if (!_merge.TryDequeue(out _pendingRunIndex, out _))
                    return;
                _pending = _mergeHeads[_pendingRunIndex];
                _mergeHeads[_pendingRunIndex] = null;
            }
            catch
            {
                DisposeMerge();
                throw;
            }
        }

        public void Dispose()
        {
            List<Exception>? cleanupFailures = null;
            try
            {
                TryDispose(DisposeMerge, ref cleanupFailures);
                if (_spill is not null)
                {
                    try
                    {
                        _spill.Dispose();
                        _spill = null;
                    }
                    catch (Exception exception)
                    {
                        (cleanupFailures ??= []).Add(exception);
                    }
                }
                TryDispose(_pendingCleanup.Retry, ref cleanupFailures);
            }
            finally
            {
                if (_bufferedBytes > 0)
                    _memory.Release(_bufferedBytes, _rows.Count);
                _rows.Clear();
                _rows.Capacity = 0;
                _bufferedBytes = 0;
                _sortWorkspaceBytes = 0;
                _merge = null;
                _pending = null;
            }
            ThrowCleanupFailures(cleanupFailures);
        }

        private void FlushBuffered(CancellationToken cancellationToken)
        {
            if (_rows.Count == 0)
                return;

            if (!_executionOptions.AllowTemporaryFileSpill)
            {
                throw new VdbeMemoryLimitExceededException(
                    _bufferMemoryLimitBytes,
                    _bufferedBytes);
            }

            _spill ??= CreateSpill();
            _spill.WriteRun(SortBufferedRows(cancellationToken), cancellationToken);
            _memory.Release(_bufferedBytes, _rows.Count);
            _rows.Clear();
            _rows.Capacity = 0;
            _bufferedBytes = 0;
            _sortWorkspaceBytes = 0;
            _spill.CompactRunTiers(
                _comparer,
                _memory,
                cancellationToken);
        }

        private SorterSpill CreateSpill()
        {
            VdbeMemoryReservation? infrastructureReservation =
                VdbeMemoryReservation.Create(
                    _memory,
                    VdbeManagedFootprint.EstimateSorterSpillInfrastructure(
                        _executionOptions.TemporaryDirectory));
            try
            {
                return SorterSpill.Create(
                    _columnCount,
                    _executionOptions,
                    _memory,
                    _pendingCleanup,
                    ref infrastructureReservation);
            }
            finally
            {
                infrastructureReservation?.Dispose();
            }
        }

        private static long EstimateRecordBytes(SqlValue[] record) =>
            Math.Max(
                VdbeManagedFootprint.EstimateSorterRow(record),
                VdbeManagedFootprint.EstimateSorterRowFromEncodedLength(
                    VdbeSpillRecordCodec.EstimateEncodedValuesLength(record),
                    record.Length));

        private bool TryBuffer(SqlValue[] record, long recordBytes)
        {
            var requiredCount = checked(_rows.Count + 1);
            var capacity = VdbeManagedFootprint.GetListCapacityForCount(
                _rows.Capacity,
                requiredCount);
            var currentListBytes =
                VdbeManagedFootprint.EstimateReferenceListStorage(_rows.Capacity);
            var listGrowth = VdbeManagedFootprint.EstimateContainerReplacement(
                currentListBytes,
                VdbeManagedFootprint.EstimateReferenceListStorage(capacity));
            var replacedListBytes = listGrowth > 0 ? currentListBytes : 0;
            var workspaceBytes = VdbeManagedFootprint.EstimateSortWorkspace(requiredCount);
            var workspaceGrowth = checked(workspaceBytes - _sortWorkspaceBytes);
            var retainedBytes = checked(recordBytes + listGrowth + workspaceGrowth);
            if (_rows.Count > 0
                && _bufferRowCapacity == int.MaxValue
                && _spill is null
                && _executionOptions.AllowTemporaryFileSpill
                && retainedBytes > _memory.AvailableBytes
                    - VdbeManagedFootprint.EstimateSorterSpillInfrastructure(
                        _executionOptions.TemporaryDirectory))
            {
                return false;
            }
            if (!_memory.TryRetain(retainedBytes))
                return false;

            try
            {
                if (capacity != _rows.Capacity)
                    _rows.Capacity = capacity;
                _rows.Add(record);
                if (replacedListBytes > 0)
                    _memory.Release(replacedListBytes, rows: 0);
                _bufferedBytes = checked(
                    _bufferedBytes
                    + retainedBytes
                    - replacedListBytes);
                _sortWorkspaceBytes = workspaceBytes;
                return true;
            }
            catch
            {
                _memory.Release(retainedBytes);
                throw;
            }
        }

        private void DisposeMerge()
        {
            _pending?.Dispose();
            _pending = null;
            if (_mergeHeads is not null)
            {
                foreach (var head in _mergeHeads)
                    head?.Dispose();
                _mergeHeads = null;
            }
            if (_readers is not null)
            {
                foreach (var reader in _readers)
                    reader?.Dispose();
                _readers = null;
            }
            _merge = null;
            if (_mergeInfrastructureBytes > 0)
            {
                _memory.Release(_mergeInfrastructureBytes, rows: 0);
                _mergeInfrastructureBytes = 0;
            }
        }

        // Heap priority: orders by the row comparer, then by RunIndex so equal-key rows
        // across runs keep their global insertion order. The record is carried alongside
        // so the heap never has to re-read a run to compare its head.
        private readonly struct MergeKey
        {
            public readonly SqlValue[] Record;
            public readonly int RunIndex;
            private readonly VdbeRowComparer _comparer;

            public MergeKey(SqlValue[] record, int runIndex, VdbeRowComparer comparer)
            {
                Record = record;
                RunIndex = runIndex;
                _comparer = comparer;
            }

            public static IComparer<MergeKey> Comparer { get; } =
                Comparer<MergeKey>.Create((left, right) =>
                {
                    var comparison = left._comparer(left.Record, right.Record);
                    return comparison != 0 ? comparison : left.RunIndex.CompareTo(right.RunIndex);
                });
        }
    }

    // Backing store for spilled sorter runs: one transient IFileSystem file holding
    // concatenated runs. Every handle is explicitly disposed before DeleteFile so the
    // cleanup path works uniformly on Windows, Unix, and InMemoryFileSystem.
    private sealed class SorterSpill : IDisposable
    {
        private readonly int _columnCount;
        private readonly VdbeExecutionOptions _executionOptions;
        private readonly VdbeExecutionMemory _memory;
        private readonly VdbeMemoryReservation _infrastructureReservation;
        private VdbeTemporaryFile? _temporaryFile;
        private IFile? _file;
        private List<RunDescriptor>? _runs;
        private long _writePosition;
        private long _runDescriptorBytes;
        private bool _disposed;

        private IFile File =>
            _file ?? throw new ObjectDisposedException(nameof(SorterSpill));

        private SorterSpill(
            int columnCount,
            VdbeExecutionOptions executionOptions,
            VdbeExecutionMemory memory,
            VdbeMemoryReservation infrastructureReservation)
        {
            _columnCount = columnCount;
            _executionOptions = executionOptions;
            _memory = memory;
            _infrastructureReservation = infrastructureReservation;
        }

        public static SorterSpill Create(
            int columnCount,
            VdbeExecutionOptions executionOptions,
            VdbeExecutionMemory memory,
            VdbePendingCleanupRegistry pendingCleanup,
            ref VdbeMemoryReservation? infrastructureReservation)
        {
            var reservation = infrastructureReservation
                ?? throw new InvalidOperationException("Sorter spill infrastructure was not reserved.");
            SorterSpill spill;
            try
            {
                spill = new SorterSpill(
                    columnCount,
                    executionOptions,
                    memory,
                    reservation);
            }
            catch
            {
                reservation.Dispose();
                infrastructureReservation = null;
                throw;
            }

            infrastructureReservation = null;
            try
            {
                pendingCleanup.Register(spill);
            }
            catch
            {
                spill.Dispose();
                throw;
            }
            try
            {
                spill.Initialize();
                pendingCleanup.Unregister(spill);
                return spill;
            }
            catch (Exception primaryFailure)
            {
                try
                {
                    spill.Dispose();
                    pendingCleanup.Unregister(spill);
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException(primaryFailure, cleanupFailure);
                }
                ExceptionDispatchInfo.Capture(primaryFailure).Throw();
                throw;
            }
        }

        private void Initialize()
        {
            _runs = new List<RunDescriptor>(
                VdbeManagedFootprint.GetListCapacityForCount(
                    currentCapacity: 0,
                    requiredCount: 1));
            _temporaryFile = VdbeTemporaryFile.Create(_executionOptions, "sorter");
            _file = _temporaryFile.File;
            _writePosition = VdbeSpillRecordCodec.InitializeFile(
                File,
                VdbeSpillFileKind.SorterRun,
                _executionOptions.Metrics);
        }

        public int RunCount => _runs?.Count ?? 0;

        public void WriteSingleRow(
            SqlValue[] row,
            long retainedBytes,
            CancellationToken cancellationToken)
        {
            var offset = _writePosition;
            ReserveRunDescriptor();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var recordStart = VdbeSpillRecordCodec.BeginRecord(ref _writePosition);
                VdbeSpillRecordCodec.WriteValues(
                    File,
                    ref _writePosition,
                    row,
                    _executionOptions.Metrics);
                VdbeSpillRecordCodec.CompleteRecord(
                    File,
                    recordStart,
                    _writePosition,
                    _executionOptions.Metrics);
                File.FlushToDisk();
                _runs!.Add(new RunDescriptor(offset, 1, retainedBytes, MergeLevel: 0));
                _executionOptions.Metrics.SorterRunWritten();
            }
            catch (Exception primaryFailure)
            {
                try
                {
                    File.SetLength(offset);
                    _writePosition = offset;
                }
                catch (Exception rollbackFailure)
                {
                    throw new AggregateException(primaryFailure, rollbackFailure);
                }
                ExceptionDispatchInfo.Capture(primaryFailure).Throw();
                throw;
            }
        }

        // Appends one stably-sorted run and remembers its descriptor. The caller hands
        // in already-sorted rows; this store is format-only and does not re-sort.
        public void WriteRun(List<SqlValue[]> sorted, CancellationToken cancellationToken)
        {
            var offset = _writePosition;
            var maximumRetainedRowBytes = 0L;
            ReserveRunDescriptor();
            try
            {
                foreach (var row in sorted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var encodedLength = VdbeSpillRecordCodec.EstimateEncodedValuesLength(row);
                    maximumRetainedRowBytes = Math.Max(
                        maximumRetainedRowBytes,
                        Math.Max(
                            VdbeManagedFootprint.EstimateSorterRow(row),
                            VdbeManagedFootprint.EstimateSorterRowFromEncodedLength(
                                encodedLength,
                                row.Length)));
                    var recordStart = VdbeSpillRecordCodec.BeginRecord(ref _writePosition);
                    VdbeSpillRecordCodec.WriteValues(
                        File,
                        ref _writePosition,
                        row,
                        _executionOptions.Metrics);
                    VdbeSpillRecordCodec.CompleteRecord(
                        File,
                        recordStart,
                        _writePosition,
                        _executionOptions.Metrics);
                }

                File.FlushToDisk();
                _runs!.Add(new RunDescriptor(
                    offset,
                    sorted.Count,
                    maximumRetainedRowBytes,
                    MergeLevel: 0));
                _executionOptions.Metrics.SorterRunWritten();
            }
            catch (Exception primaryFailure)
            {
                try
                {
                    File.SetLength(offset);
                    _writePosition = offset;
                }
                catch (Exception rollbackFailure)
                {
                    throw new AggregateException(primaryFailure, rollbackFailure);
                }
                ExceptionDispatchInfo.Capture(primaryFailure).Throw();
                throw;
            }
        }

        public void CompactRunTiers(
            VdbeRowComparer comparer,
            VdbeExecutionMemory memory,
            CancellationToken cancellationToken)
        {
            var runs = _runs
                ?? throw new ObjectDisposedException(nameof(SorterSpill));
            while (true)
            {
                var start = -1;
                for (var index = runs.Count - 2; index >= 0; index--)
                {
                    if (runs[index].MergeLevel == runs[index + 1].MergeLevel)
                    {
                        start = index;
                        break;
                    }
                }

                if (start < 0)
                    return;
                if (GetEffectiveFanIn(start, 2, memory.AvailableBytes) < 2)
                {
                    throw new VdbeMemoryLimitExceededException(
                        memory.LimitBytes,
                        EstimateMergeBytes(start, 2));
                }

                var merged = MergeRunGroup(
                    start,
                    count: 2,
                    comparer,
                    memory,
                    cancellationToken);
                runs[start] = merged;
                runs.RemoveAt(start + 1);
            }
        }

        public void PrepareFinalMerge(
            VdbeRowComparer comparer,
            int maximumFanIn,
            VdbeExecutionMemory memory,
            CancellationToken cancellationToken)
        {
            CompactRunTiers(comparer, memory, cancellationToken);
            var runs = _runs
                ?? throw new ObjectDisposedException(nameof(SorterSpill));
            while (runs.Count > 1)
            {
                var finalFanIn = GetEffectiveFanIn(
                    start: 0,
                    Math.Min(maximumFanIn, runs.Count),
                    memory.AvailableBytes);
                if (runs.Count <= maximumFanIn && finalFanIn == runs.Count)
                    return;

                var passOffset = _writePosition;
                var consolidatedBytes = checked(
                    VdbeManagedFootprint.ListObjectBytes
                    + VdbeManagedFootprint.EstimateRunDescriptorListStorage(runs.Count));
                memory.RetainOrThrow(consolidatedBytes, rows: 0);
                try
                {
                    var consolidated = new List<RunDescriptor>(runs.Count);
                    for (var start = 0; start < runs.Count;)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var remaining = runs.Count - start;
                        if (remaining == 1)
                        {
                            consolidated.Add(runs[start]);
                            start++;
                            continue;
                        }

                        var count = GetEffectiveFanIn(
                            start,
                            Math.Min(maximumFanIn, remaining),
                            memory.AvailableBytes);
                        if (count < 2)
                        {
                            var requestedBytes = EstimateMergeBytes(start, 2);
                            throw new VdbeMemoryLimitExceededException(
                                memory.LimitBytes,
                                requestedBytes);
                        }
                        consolidated.Add(MergeRunGroup(
                            start,
                            count,
                            comparer,
                            memory,
                            cancellationToken));
                        start += count;
                    }

                    runs.Clear();
                    runs.AddRange(consolidated);
                }
                catch (Exception primaryFailure)
                {
                    try
                    {
                        File.SetLength(passOffset);
                        _writePosition = passOffset;
                    }
                    catch (Exception rollbackFailure)
                    {
                        throw new AggregateException(primaryFailure, rollbackFailure);
                    }
                    ExceptionDispatchInfo.Capture(primaryFailure).Throw();
                    throw;
                }
                finally
                {
                    memory.Release(consolidatedBytes, rows: 0);
                }
            }
        }

        private RunDescriptor MergeRunGroup(
            int start,
            int count,
            VdbeRowComparer comparer,
            VdbeExecutionMemory memory,
            CancellationToken cancellationToken)
        {
            var offset = _writePosition;
            RunReader[]? readers = null;
            RowLease[]? heads = null;
            RowLease? current = null;
            var infrastructureBytes = VdbeManagedFootprint.EstimateMergeInfrastructure(count);
            memory.RetainOrThrow(infrastructureBytes, rows: 0);
            try
            {
                readers = new RunReader[count];
                heads = new RowLease[count];
                var heap = new PriorityQueue<int, SpillMergeKey>(
                    count,
                    SpillMergeKey.Comparer);
                for (var index = 0; index < count; index++)
                {
                    var input = _runs![start + index];
                    var reader = new RunReader(
                        File,
                        input.Offset,
                        input.RowCount,
                        _columnCount,
                        _executionOptions.Metrics);
                    readers[index] = reader;
                    if (reader.TryReadNext(memory, out var row, cancellationToken))
                    {
                        heads[index] = row;
                        heap.Enqueue(
                            index,
                            new SpillMergeKey(row.Record, index, comparer));
                    }
                }

                var rowsWritten = 0;
                while (heap.TryDequeue(out var readerIndex, out var key))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    current = heads[readerIndex];
                    heads[readerIndex] = null!;
                    var recordStart = VdbeSpillRecordCodec.BeginRecord(ref _writePosition);
                    VdbeSpillRecordCodec.WriteValues(
                        File,
                        ref _writePosition,
                        key.Record,
                        _executionOptions.Metrics);
                    VdbeSpillRecordCodec.CompleteRecord(
                        File,
                        recordStart,
                        _writePosition,
                        _executionOptions.Metrics);
                    rowsWritten = checked(rowsWritten + 1);

                    current.Dispose();
                    current = null;
                    if (readers[readerIndex].TryReadNext(memory, out var next, cancellationToken))
                    {
                        heads[readerIndex] = next;
                        heap.Enqueue(
                            readerIndex,
                            new SpillMergeKey(next.Record, readerIndex, comparer));
                    }
                }

                File.FlushToDisk();
                _executionOptions.Metrics.SorterRunWritten();
                return new RunDescriptor(
                    offset,
                    rowsWritten,
                    GetMaximumRetainedRowBytes(start, count),
                    checked(GetMaximumMergeLevel(start, count) + 1));
            }
            catch (Exception primaryFailure)
            {
                try
                {
                    File.SetLength(offset);
                    _writePosition = offset;
                }
                catch (Exception rollbackFailure)
                {
                    throw new AggregateException(primaryFailure, rollbackFailure);
                }
                ExceptionDispatchInfo.Capture(primaryFailure).Throw();
                throw;
            }
            finally
            {
                current?.Dispose();
                if (heads is not null)
                {
                    foreach (var head in heads)
                        head?.Dispose();
                }
                if (readers is not null)
                {
                    foreach (var reader in readers)
                        reader?.Dispose();
                }
                memory.Release(infrastructureBytes, rows: 0);
            }
        }

        public RunReader OpenRunReader(int runIndex)
        {
            var run = _runs![runIndex];
            VdbeSpillRecordCodec.ValidateFile(
                File,
                VdbeSpillFileKind.SorterRun,
                _executionOptions.Metrics);
            return new RunReader(
                File,
                run.Offset,
                run.RowCount,
                _columnCount,
                _executionOptions.Metrics);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _temporaryFile?.Dispose();
            if (_runDescriptorBytes > 0)
            {
                _memory.Release(_runDescriptorBytes, rows: 0);
                _runDescriptorBytes = 0;
            }
            _infrastructureReservation.Dispose();
            _temporaryFile = null;
            _runs = null;
            _file = null;
            _disposed = true;
        }

        private int GetEffectiveFanIn(int start, int maximumFanIn, long availableBytes)
        {
            var fanIn = 0;
            for (var count = 1; count <= maximumFanIn; count++)
            {
                if (EstimateMergeBytes(start, count) > availableBytes)
                    break;
                fanIn = count;
            }
            return fanIn;
        }

        private long EstimateMergeBytes(int start, int count)
        {
            var total = VdbeManagedFootprint.EstimateMergeInfrastructure(count);
            for (var index = 0; index < count; index++)
            {
                total = checked(
                    total
                    + _runs![start + index].MaximumRetainedRowBytes);
            }
            return total;
        }

        private long GetMaximumRetainedRowBytes(int start, int count)
        {
            var maximum = 0L;
            for (var index = 0; index < count; index++)
                maximum = Math.Max(maximum, _runs![start + index].MaximumRetainedRowBytes);
            return maximum;
        }

        private int GetMaximumMergeLevel(int start, int count)
        {
            var maximum = 0;
            for (var index = 0; index < count; index++)
                maximum = Math.Max(maximum, _runs![start + index].MergeLevel);
            return maximum;
        }

        private void ReserveRunDescriptor()
        {
            var runs = _runs
                ?? throw new ObjectDisposedException(nameof(SorterSpill));
            var capacity = VdbeManagedFootprint.GetListCapacityForCount(
                runs.Capacity,
                runs.Count + 1);
            if (capacity == runs.Capacity)
                return;
            var currentStorageBytes =
                VdbeManagedFootprint.EstimateRunDescriptorListStorage(runs.Capacity);
            var growthBytes = VdbeManagedFootprint.EstimateContainerReplacement(
                currentStorageBytes,
                VdbeManagedFootprint.EstimateRunDescriptorListStorage(capacity));
            _memory.RetainOrThrow(growthBytes, rows: 0);
            try
            {
                runs.Capacity = capacity;
                if (_runDescriptorBytes > 0)
                    _memory.Release(_runDescriptorBytes, rows: 0);
                _runDescriptorBytes = growthBytes;
            }
            catch
            {
                _memory.Release(growthBytes, rows: 0);
                throw;
            }
        }

        private readonly record struct RunDescriptor(
            long Offset,
            int RowCount,
            long MaximumRetainedRowBytes,
            int MergeLevel);

        private readonly struct SpillMergeKey
        {
            public readonly SqlValue[] Record;
            public readonly int InputIndex;
            private readonly VdbeRowComparer _comparer;

            public SpillMergeKey(SqlValue[] record, int inputIndex, VdbeRowComparer comparer)
            {
                Record = record;
                InputIndex = inputIndex;
                _comparer = comparer;
            }

            public static IComparer<SpillMergeKey> Comparer { get; } =
                Comparer<SpillMergeKey>.Create((left, right) =>
                {
                    var comparison = left._comparer(left.Record, right.Record);
                    return comparison != 0 ? comparison : left.InputIndex.CompareTo(right.InputIndex);
                });
        }

        // Reads one run's records back one at a time using positional IFile access. Multiple
        // readers share the file safely because each carries its own explicit offset.
        public sealed class RowLease(
            SqlValue[] record,
            VdbeExecutionMemory memory,
            long retainedBytes) : IDisposable
        {
            private bool _disposed;

            public SqlValue[] Record { get; } = record;

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                memory.Release(retainedBytes);
            }
        }

        public sealed class RunReader : IDisposable
        {
            private readonly IFile _file;
            private readonly int _columnCount;
            private readonly VdbeExecutionMetrics _metrics;
            private long _position;

            public RunReader(
                IFile file,
                long offset,
                int rowCount,
                int columnCount,
                VdbeExecutionMetrics metrics)
            {
                _file = file;
                _columnCount = columnCount;
                _metrics = metrics;
                _position = offset;
                RowsRemaining = rowCount;
            }

            public int RowsRemaining { get; private set; }

            public bool TryReadNext(
                VdbeExecutionMemory memory,
                out RowLease row,
                CancellationToken cancellationToken)
            {
                if (RowsRemaining <= 0)
                {
                    row = null!;
                    return false;
                }

                var rowStart = _position;
                var retainedBytes = 0L;
                var retained = false;
                try
                {
                    var recordEnd = VdbeSpillRecordCodec.ReadRecordEnd(
                        _file,
                        ref _position,
                        _metrics);
                    retainedBytes = VdbeManagedFootprint.EstimateSorterRowFromEncodedLength(
                        recordEnd - _position,
                        _columnCount);
                    memory.RetainOrThrow(retainedBytes);
                    retained = true;
                    var values = VdbeSpillRecordCodec.ReadValues(
                        _file,
                        ref _position,
                        _columnCount,
                        recordEnd,
                        _metrics,
                        cancellationToken);
                    VdbeSpillRecordCodec.RequireRecordEnd(_position, recordEnd);
                    RowsRemaining--;
                    row = new RowLease(values, memory, retainedBytes);
                    return true;
                }
                catch
                {
                    _position = rowStart;
                    if (retained)
                        memory.Release(retainedBytes);
                    throw;
                }
            }

            public void Dispose()
            {
                // The IFile is shared and owned by SorterSpill.
            }
        }
    }

    // Holds one window buffer's scanned rows, the window values computed over them, and the drain cursor.
    // Rows are copied on insert so overwriting the staging registers between iterations cannot mutate a
    // buffered row. Compute runs the caller-supplied evaluator exactly once over the whole buffer — the
    // step that makes a full-partition frame (forward-looking ROWS, peer-relative RANGE/GROUPS, ranking and
    // navigation functions) representable — and pins its result shape so a misbehaving evaluator fails
    // loudly instead of producing short or ragged rows. Draining then walks the buffer in insertion order,
    // handing out each row concatenated with its window values.
    private sealed class WindowBufferRuntime
    {
        private readonly int _columnCount;
        private readonly int _windowCount;
        private readonly VdbeWindowEvaluator _evaluator;
        private readonly List<SqlValue[]> _rows = [];
        private SqlValue[][]? _windowValues;
        private int _position = -1;

        public WindowBufferRuntime(int columnCount, int windowCount, VdbeWindowEvaluator evaluator)
        {
            _columnCount = columnCount;
            _windowCount = windowCount;
            _evaluator = evaluator;
        }

        public void Insert(SqlValue[] row)
        {
            if (_windowValues is not null)
            {
                throw new InvalidOperationException(
                    "Cannot insert into a window buffer after its window values have been computed.");
            }

            if (row.Length != _columnCount)
            {
                throw new InvalidOperationException(
                    $"Window buffer stores {_columnCount}-column rows but received {row.Length} values.");
            }

            _rows.Add(row);
        }

        // Computes every buffered row's window values and positions on the first row. Returns false (and
        // leaves the buffer unpositioned) when there is nothing to drain.
        public bool Compute()
        {
            var computed = _evaluator(_rows)
                ?? throw new InvalidOperationException("A window evaluator returned null.");
            if (computed.Count != _rows.Count)
            {
                throw new InvalidOperationException(
                    $"A window evaluator returned {computed.Count} window tuples for {_rows.Count} buffered rows.");
            }

            var values = new SqlValue[computed.Count][];
            for (var index = 0; index < computed.Count; index++)
            {
                var tuple = computed[index]
                    ?? throw new InvalidOperationException("A window evaluator returned a null window tuple.");
                if (tuple.Length != _windowCount)
                {
                    throw new InvalidOperationException(
                        $"A window evaluator returned a {tuple.Length}-wide window tuple for a buffer declaring {_windowCount} window functions.");
                }

                values[index] = tuple;
            }

            _windowValues = values;
            _position = _rows.Count == 0 ? -1 : 0;
            return _position >= 0;
        }

        // The current row followed by that row's computed window values, as one contiguous record.
        public SqlValue[] Current()
        {
            if (_windowValues is null)
            {
                throw new InvalidOperationException(
                    "Window buffer must compute its window values before reading a record.");
            }

            if (_position < 0 || _position >= _rows.Count)
                throw new InvalidOperationException("Window buffer is not positioned on a row.");

            var record = new SqlValue[_columnCount + _windowCount];
            Array.Copy(_rows[_position], record, _columnCount);
            Array.Copy(_windowValues[_position], 0, record, _columnCount, _windowCount);
            return record;
        }

        // Advances to the next buffered row, returning whether one remains.
        public bool MoveNext()
        {
            if (_windowValues is null)
            {
                throw new InvalidOperationException(
                    "Window buffer must compute its window values before advancing.");
            }

            _position++;
            return _position < _rows.Count;
        }
    }

    // Holds one recursive worktable's runtime state: the FIFO frontier of (row, depth) pairs, the optional
    // de-duplication set (for UNION/DISTINCT), the admitted-row count for the row guard, and the depth of
    // the row most recently dequeued by Step (which the following Expand expands from). Every admitted row is
    // snapshotted on admission (see TryAdmit), so neither overwriting the source registers between iterations
    // nor a transform that reuses a single output buffer across the rows it emits can mutate a queued
    // frontier row or a recorded distinct representative. The recursion itself — FIFO ordering, re-feeding
    // descendants, de-duplication, depth bounding, and the row cap — lives here and is driven step by step by
    // the interpreter loop; the transform delegate only computes one generation from one row.
    private sealed class WorkTableRuntime
    {
        private readonly int _columnCount;
        private readonly WorkTableDedupMode _mode;
        private readonly int _maxRows;
        private readonly int _maxDepth;
        private readonly VdbeRowEquality? _equality;
        private readonly Queue<(SqlValue[] Row, int Depth)> _frontier = new();
        private readonly List<SqlValue[]>? _seen;
        private readonly List<SqlValue[]> _generation = [];
        private int _admitted;
        private bool _hasCurrent;
        private int _currentDepth;

        public WorkTableRuntime(
            int columnCount,
            WorkTableDedupMode mode,
            int maxRows,
            int maxDepth,
            VdbeRowEquality? equality)
        {
            _columnCount = columnCount;
            _mode = mode;
            _maxRows = maxRows;
            _maxDepth = maxDepth;
            _equality = equality;
            _seen = mode == WorkTableDedupMode.Distinct ? [] : null;
        }

        // Admits a seed (anchor) row at depth 0. Distinct duplicates are dropped; admission counts against
        // the row guard.
        public void Seed(SqlValue[] row)
        {
            RequireWidth(row);
            TryAdmit(row, depth: 0);
        }

        // Dequeues the next frontier row and records its depth as the current expansion depth. Returns false
        // (and clears the current row) when the frontier is drained.
        public bool TryStep(out SqlValue[] row)
        {
            if (_frontier.Count == 0)
            {
                _hasCurrent = false;
                row = [];
                return false;
            }

            var (dequeued, depth) = _frontier.Dequeue();
            _hasCurrent = true;
            _currentDepth = depth;
            row = dequeued;
            return true;
        }

        // Expands the current frontier row one generation deeper. The depth guard cuts expansion off once the
        // current row sits at MaxDepth, so no descendant beyond the bounded slice is ever produced.
        public void Expand(SqlValue[] frontierRow, VdbeRecursiveTransform transform)
        {
            if (!_hasCurrent)
            {
                throw new InvalidOperationException(
                    "Work table has no current row to expand; a WorkTableStep must dequeue a row before WorkTableExpand.");
            }

            if (_currentDepth >= _maxDepth)
                return;

            var children = transform(frontierRow)
                ?? throw new InvalidOperationException("A recursive transform must not return a null row list.");

            var childDepth = checked(_currentDepth + 1);
            foreach (var child in children)
            {
                if (child is null)
                    throw new InvalidOperationException("A recursive transform must not return a null row.");

                RequireWidth(child);
                TryAdmit(child, childDepth);
            }
        }

        public void ExpandGeneration(
            SqlValue[] frontierRow,
            VdbeRecursiveGenerationTransform transform)
        {
            if (!_hasCurrent)
            {
                throw new InvalidOperationException(
                    "Work table has no current row to expand; a WorkTableStep must dequeue a row before WorkTableExpandGeneration.");
            }

            if (_currentDepth >= _maxDepth)
                return;

            RequireWidth(frontierRow);
            _generation.Add([.. frontierRow]);
            if (_frontier.TryPeek(out var next) && next.Depth == _currentDepth)
                return;

            var frontier = _generation.ToArray();
            _generation.Clear();
            var children = transform(frontier)
                ?? throw new InvalidOperationException(
                    "A recursive generation transform must not return a null row list.");
            var childDepth = checked(_currentDepth + 1);
            foreach (var child in children)
            {
                if (child is null)
                    throw new InvalidOperationException(
                        "A recursive generation transform must not return a null row.");

                RequireWidth(child);
                TryAdmit(child, childDepth);
            }
        }

        // Admits a row: dropped as a duplicate under Distinct, otherwise counted against the row guard,
        // recorded for future de-duplication, and enqueued for later draining. Returns whether it was admitted.
        //
        // Admission is the ownership boundary. `row` is transient storage the caller may keep mutating: a
        // seed's register snapshot is discarded after this call, but more importantly a recursive transform
        // is free to reuse one output buffer across the rows it emits and across successive expansions.
        // Snapshot the row here so the de-duplication representative and the queued frontier entry reference
        // storage this runtime owns and never mutates in place. Without the copy a later overwrite of that
        // buffer would rewrite an already-admitted row, corrupting the frontier (a queued row would surface
        // with the wrong values) and the distinct set (a genuinely new row would be misread as a duplicate).
        // The dedup scan compares the caller's `row` before copying, so the snapshot adds no work to the
        // rejection path.
        private bool TryAdmit(SqlValue[] row, int depth)
        {
            if (_seen is not null)
            {
                foreach (var stored in _seen)
                {
                    if (_equality!(stored, row))
                        return false;
                }
            }

            if (_admitted >= _maxRows)
                throw new RecursiveWorkTableOverflowException(_maxRows);

            var owned = CloneRow(row);
            _admitted++;
            _seen?.Add(owned);
            _frontier.Enqueue((owned, depth));
            return true;
        }

        // Shallow snapshot of a record. SqlValue is an immutable value type and blob payloads are exposed as
        // read-only memory, so copying the array elements clones the row faithfully without duplicating (or
        // ever exposing mutable) blob storage.
        private static SqlValue[] CloneRow(SqlValue[] row)
        {
            var copy = new SqlValue[row.Length];
            Array.Copy(row, copy, row.Length);
            return copy;
        }

        private void RequireWidth(SqlValue[] row)
        {
            if (row.Length != _columnCount)
            {
                throw new InvalidOperationException(
                    $"Work table stores {_columnCount}-column records but received {row.Length} values.");
            }
        }
    }

    // A streaming join cursor does not materialize its (potentially unbounded) output. Instead it
    // holds the lazy enumerator produced by VdbeJoinPlan.Enumerate and the row it currently rests on.
    // The cursor access pattern is strictly sequential forward-only (Rewind -> Column* -> Next ->
    // Close), so a single forward enumerator is sufficient: Rewind primes the first row (and
    // detects emptiness), Next advances it, and CurrentCursorRow returns the cached current row.
    private sealed class JoinCursorState
    {
        private IEnumerator<SqlValue[]>? _enumerator;

        public SqlValue[]? CurrentRow { get; private set; }

        private VdbeJoinExecutionContext? _context;

        public void Open(
            VdbeJoinPlan plan,
            VdbeExecutionOptions executionOptions,
            VdbeExecutionMemory memory)
        {
            _context = new VdbeJoinExecutionContext(executionOptions, memory);
            _enumerator = plan.Enumerate(_context).GetEnumerator();
            CurrentRow = null;
        }

        public bool MoveNext(CancellationToken cancellationToken)
        {
            if (_enumerator is null)
                return false;

            _context!.SetCancellationToken(cancellationToken);
            try
            {
                if (_enumerator.MoveNext())
                {
                    CurrentRow = _enumerator.Current;
                    return true;
                }

                CurrentRow = null;
                if (_context.TakeCleanupFailure() is { } cleanupFailure)
                    ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
                return false;
            }
            catch (Exception executionFailure)
            {
                try
                {
                    Close();
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException(executionFailure, cleanupFailure);
                }
                throw;
            }
        }

        public void Close()
        {
            Exception? failure = null;
            try
            {
                _enumerator?.Dispose();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                _enumerator = null;
            }

            if (_context?.TakeCleanupFailure() is { } cleanupFailure)
            {
                failure = failure is null
                    ? cleanupFailure
                    : new AggregateException(failure, cleanupFailure);
            }

            if (_context?.HasPendingCleanup == true)
            {
                try
                {
                    _context.RetryPendingCleanup();
                }
                catch (Exception retryFailure)
                {
                    failure = failure is null
                        ? retryFailure
                        : new AggregateException(failure, retryFailure);
                }
            }

            if (_context?.HasPendingCleanup != true)
            {
                _context = null;
                CurrentRow = null;
            }
            if (failure is not null)
                ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
