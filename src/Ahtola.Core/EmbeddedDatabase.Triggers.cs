using Ahtola.Core.Parsing;

namespace Ahtola.Core;

public sealed partial class EmbeddedDatabase
{
    internal sealed class TriggerStatementState(long lastInsertRowId)
    {
        public bool Changed { get; set; }

        public long LiveLastInsertRowId { get; set; } = lastInsertRowId;

        public bool RequiresStatementRollback { get; set; }
    }

    private sealed record TriggerRowIdentity(long? RowId, SqlValue[] PrimaryKey);

    private sealed record TriggerMutationEdge(
        string TableName,
        TriggerEvent Event,
        IReadOnlySet<string>? UpdatedColumns = null);

    internal sealed record TriggerRowImage(
        string[] Columns,
        SqlValue[] Values,
        bool HasRowid,
        long RowId,
        int RowidAliasColumnIndex = -1)
    {
        public SqlValue GetValue(string name)
        {
            for (var index = 0; index < Columns.Length; index++)
            {
                if (string.Equals(Columns[index], name, StringComparison.OrdinalIgnoreCase))
                    return Values[index];
            }

            if (HasRowid && EmbeddedTable.IsRowidAliasName(name))
                return SqlValue.Integer(RowId);

            throw new EmbeddedSqlException($"no such column: {name}");
        }

        // NEW/OLD columns carry no comparison affinity; only the rowid and rowid aliases
        // keep INTEGER affinity (https://www.sqlite.org/forum/forumpost/819f2d6627; Turso
        // translate/trigger_exec.rs populate_trigger_row_register_affinities).
        public ColumnAffinity? GetComparisonAffinity(string name)
        {
            for (var index = 0; index < Columns.Length; index++)
            {
                if (!string.Equals(Columns[index], name, StringComparison.OrdinalIgnoreCase))
                    continue;

                return index == RowidAliasColumnIndex ? ColumnAffinity.Integer : null;
            }

            if (HasRowid && EmbeddedTable.IsRowidAliasName(name))
                return ColumnAffinity.Integer;

            return null;
        }
    }

    internal sealed record TriggerRowFrame
    {
        public TriggerRowFrame(TriggerRowImage? Old, TriggerRowImage? New)
        {
            this.Old = Old;
            this.New = New;
        }

        // The main evaluator's legacy no-row-trigger path still carries raw row arrays.
        // Keeping that adapter here lets all trigger execution share one frame type.
        public TriggerRowFrame(
            SqlValue[]? OldRow,
            long? OldRowId,
            SqlValue[]? NewRow,
            long? NewRowId)
        {
            this.OldRow = OldRow;
            this.OldRowId = OldRowId;
            this.NewRow = NewRow;
            this.NewRowId = NewRowId;
        }

        public TriggerRowImage? Old { get; }

        public TriggerRowImage? New { get; }

        public SqlValue[]? OldRow { get; }

        public long? OldRowId { get; }

        public SqlValue[]? NewRow { get; }

        public long? NewRowId { get; }

        public static TriggerRowFrame Empty { get; } = new(null, null, null, null);

        public bool IsEmpty => Old is null && New is null && OldRow is null && NewRow is null;

        public static bool IsTriggerQualifier(string? qualifier)
            => string.Equals(qualifier, "OLD", StringComparison.OrdinalIgnoreCase)
                || string.Equals(qualifier, "NEW", StringComparison.OrdinalIgnoreCase);

        public SqlValue GetValue(ColumnExpression column)
        {
            var image = string.Equals(column.Qualifier, "OLD", StringComparison.OrdinalIgnoreCase)
                ? Old
                : string.Equals(column.Qualifier, "NEW", StringComparison.OrdinalIgnoreCase)
                    ? New
                    : null;
            var name = column.UnqualifiedName ?? column.Name;
            return image?.GetValue(name)
                ?? throw new EmbeddedSqlException($"no such column: {column.Name}");
        }

        public ColumnAffinity? GetComparisonAffinity(ColumnExpression column)
        {
            var image = string.Equals(column.Qualifier, "OLD", StringComparison.OrdinalIgnoreCase)
                ? Old
                : string.Equals(column.Qualifier, "NEW", StringComparison.OrdinalIgnoreCase)
                    ? New
                    : null;
            if (image is null)
                return null;

            return image.GetComparisonAffinity(column.UnqualifiedName ?? column.Name);
        }
    }

    private bool FireRowTriggers(
        IReadOnlyList<TriggerDefinition> triggers,
        TriggerRowFrame frame,
        QueryContext context)
    {
        foreach (var trigger in triggers)
        {
            var identity = GetTriggerIdentity(trigger);
            if (!context.RecursiveTriggersEnabled
                && context.ActiveTriggers?.Contains(identity) == true)
            {
                continue;
            }
            if (context.TriggerDepth >= MaximumTriggerDepth)
            {
                throw new EmbeddedTriggerDepthException(
                    context.TriggerState?.LiveLastInsertRowId ?? context.LastInsertRowId);
            }

            var activeTriggers = context.ActiveTriggers is null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(context.ActiveTriggers, StringComparer.OrdinalIgnoreCase);
            activeTriggers.Add(identity);
            var state = context.TriggerState
                ?? throw new InvalidOperationException("Row trigger execution lost its statement state.");
            var savedLastInsertRowId = state.LiveLastInsertRowId;
            // The connection-level changes() value is saved when a trigger fires and restored
            // when the trigger returns. Trigger-body statements temporarily replace it (see the
            // flush below), mirroring Turso's saved_changes_value around a trigger subprogram.
            var savedChanges = _changes;
            var triggerContext = context with
            {
                CommonTableExpressions = new Dictionary<string, SourceData>(StringComparer.OrdinalIgnoreCase),
                InsideTrigger = true,
                ActiveTriggers = activeTriggers,
                TriggerDepth = context.TriggerDepth + 1,
                TriggerRow = frame,
            };

            try
            {
                if (trigger.When is not null
                    && !IsTrue(Evaluate(trigger.When, EmptyParameters, row: null, triggerContext)))
                {
                    continue;
                }

                foreach (var bodyStatement in trigger.Body)
                {
                    var localStatement = LocalizeTriggerBodyStatement(context, trigger, bodyStatement);
                    var result = localStatement is null
                        ? context.TempTriggers!.ExecuteForeign(bodyStatement, triggerContext)
                        : localStatement switch
                        {
                            InsertStatement insert => ExecuteInsert(insert, EmptyParameters, triggerContext),
                            UpdateStatement update => ExecuteUpdate(update, EmptyParameters, triggerContext),
                            DeleteStatement delete => ExecuteDelete(delete, EmptyParameters, triggerContext),
                            QueryStatement query => ExecuteQuery(query, EmptyParameters, triggerContext, outerRow: null),
                            _ => throw new EmbeddedSqlException(
                                $"unsupported trigger body statement {bodyStatement.GetType().Name}"),
                        };
                    state.Changed |= result.Changed;
                    if (result.LastInsertRowId is { } insertedRowId)
                        state.LiveLastInsertRowId = insertedRowId;
                    // A trigger-body INSERT/UPDATE/DELETE replaces the changes() value visible to
                    // subsequent body statements and counts toward total_changes(), per SQLite.
                    if (bodyStatement is InsertStatement or UpdateStatement or DeleteStatement)
                    {
                        _changes = result.RowsAffected;
                        _totalChanges += result.RowsAffected;
                    }
                }
            }
            catch (EmbeddedConflictFailException exception)
            {
                throw new EmbeddedConflictFailException(exception, savedLastInsertRowId);
            }
            catch (EmbeddedTriggerDepthException exception)
            {
                throw new EmbeddedTriggerDepthException(exception, savedLastInsertRowId);
            }
            catch (TriggerIgnoreException)
            {
                return true;
            }
            finally
            {
                state.LiveLastInsertRowId = savedLastInsertRowId;
                // Restore the caller's changes() value; total_changes() is intentionally not
                // restored so trigger-body rows remain counted.
                _changes = savedChanges;
            }
        }

        return false;
    }

    // Only a temp trigger can have a body statement that leaves the executing database, and only
    // the owning connection can decide where such a statement belongs. Returns null when the
    // statement is foreign, otherwise the statement with any schema qualifier that names the
    // executing database stripped off.
    private static ParsedStatement? LocalizeTriggerBodyStatement(
        QueryContext context,
        TriggerDefinition trigger,
        ParsedStatement statement)
    {
        if (!trigger.Temporary || context.TempTriggers is not { } bridge)
            return statement;

        return bridge.IsForeign(statement) ? null : bridge.Localize(statement);
    }

    private static IReadOnlyList<TriggerDefinition> GetRowTriggers(
        QueryContext context,
        string targetName,
        TriggerTiming timing,
        TriggerEvent triggerEvent,
        IReadOnlySet<string>? updatedColumns = null)
    {
        var candidates = EnumerateVisibleTriggers(context);
        if (candidates is null)
            return [];

        return candidates
            .Where(trigger =>
                trigger.Timing == timing
                && trigger.Event == triggerEvent
                && string.Equals(trigger.TableName, targetName, StringComparison.OrdinalIgnoreCase)
                && (trigger.UpdateOfColumns is null
                    || updatedColumns is not null
                    && trigger.UpdateOfColumns.Any(updatedColumns.Contains)))
            .OrderByDescending(trigger => trigger.Temporary)
            .ThenByDescending(trigger => trigger.DeclarationOrder)
            .ThenByDescending(trigger => trigger.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // A temp trigger watching a table in another schema is stored in the temp database but has to
    // fire for statements executed by the database that owns the table, so the connection hands it
    // over as an overlay instead of it appearing in the executing catalog.
    private static IEnumerable<TriggerDefinition>? EnumerateVisibleTriggers(QueryContext context)
    {
        // A trigger stored here with a foreign target watches a table in another database and must
        // never match a same-named local table.
        var local = context.Triggers is { Count: > 0 } triggers
            ? triggers.Values.Where(trigger => trigger.TargetSchema is null)
            : null;
        var overlay = context.TempTriggers is { Overlay.Count: > 0 } bridge ? bridge.Overlay : null;
        if (overlay is null)
            return local;

        return local is null ? overlay : overlay.Concat(local);
    }

    // Temp and persistent triggers can share a name, so recursion suppression has to tell them
    // apart or one would silently mask the other.
    private static string GetTriggerIdentity(TriggerDefinition trigger)
        => trigger.Temporary ? ManagedSchemaName.Create("temp", trigger.Name) : trigger.Name;

    private static bool HasRowTriggers(
        QueryContext context,
        string targetName,
        TriggerEvent triggerEvent,
        IReadOnlySet<string>? updatedColumns = null)
        => GetRowTriggers(context, targetName, TriggerTiming.Before, triggerEvent, updatedColumns).Count != 0
            || GetRowTriggers(context, targetName, TriggerTiming.After, triggerEvent, updatedColumns).Count != 0
            || GetRowTriggers(context, targetName, TriggerTiming.InsteadOf, triggerEvent, updatedColumns).Count != 0;

    private static bool HasTriggerEvent(
        QueryContext context,
        string targetName,
        TriggerEvent triggerEvent)
        => EnumerateVisibleTriggers(context)?.Any(trigger =>
            trigger.Event == triggerEvent
            && string.Equals(trigger.TableName, targetName, StringComparison.OrdinalIgnoreCase)) == true;

    private ExecutionResult ExecuteRowTriggeredInsert(
        InsertStatement statement,
        SqlValue[] parameters,
        QueryContext context)
        => ExecuteRowTriggerStatement(
            context,
            triggerContext =>
            {
                if (statement.ConflictAlgorithm is { } conflictAlgorithm)
                    triggerContext = triggerContext with { ConflictAlgorithmOverride = conflictAlgorithm };
                return triggerContext.Views?.ContainsKey(statement.TableName) == true
                ? PerformInsteadOfInsert(statement, parameters, triggerContext)
                : statement.Upsert is null
                    ? PerformRowTriggeredInsert(statement, parameters, triggerContext)
                    : PerformRowTriggeredUpsert(statement, parameters, triggerContext);
            });

    private ExecutionResult ExecuteRowTriggeredUpdate(
        UpdateStatement statement,
        SqlValue[] parameters,
        QueryContext context,
        IReadOnlySet<string> updatedColumns)
        => ExecuteRowTriggerStatement(
            context,
            triggerContext =>
            {
                // A trigger body ignores its own OR clause, so an inherited policy already present
                // on the context wins over the one written on this statement.
                if (statement.ConflictAlgorithm is { } conflictAlgorithm
                    && triggerContext.ConflictAlgorithmOverride is null
                    && !triggerContext.InsideTrigger)
                {
                    triggerContext = triggerContext with { ConflictAlgorithmOverride = conflictAlgorithm };
                }

                return triggerContext.Views?.ContainsKey(statement.TableName) == true
                    ? PerformInsteadOfUpdate(statement, parameters, triggerContext, updatedColumns)
                    : PerformRowTriggeredUpdate(statement, parameters, triggerContext, updatedColumns);
            });

    private ExecutionResult ExecuteRowTriggeredDelete(
        DeleteStatement statement,
        SqlValue[] parameters,
        QueryContext context)
        => ExecuteRowTriggerStatement(
            context,
            triggerContext => triggerContext.Views?.ContainsKey(statement.TableName) == true
                ? PerformInsteadOfDelete(statement, parameters, triggerContext)
                : PerformRowTriggeredDelete(statement, parameters, triggerContext));

    private ExecutionResult ExecuteRowTriggerStatement(
        QueryContext context,
        Func<QueryContext, ExecutionResult> operation)
    {
        if (context.TriggerState is not null)
            return operation(context);

        var backup = CloneTables(context.Tables);
        var state = new TriggerStatementState(context.LastInsertRowId);
        var initialLastInsertRowId = state.LiveLastInsertRowId;
        var triggerContext = context with { TriggerState = state };
        try
        {
            var result = context.ForeignKeysEnabled
                ? ExecuteWithForeignKeyStatement(triggerContext, () => operation(triggerContext))
                : operation(triggerContext);
            return result with { Changed = result.Changed || state.Changed };
        }
        catch (EmbeddedConflictFailException)
        {
            throw;
        }
        catch (EmbeddedConflictRollbackException exception)
        {
            RestoreTables(context.Tables, backup);
            throw new EmbeddedConflictRollbackException(
                new EmbeddedSqlException(exception.Message, exception.InnerException ?? exception),
                state.LiveLastInsertRowId);
        }
        catch (EmbeddedTriggerDepthException exception)
        {
            if (context.InTransaction && !state.RequiresStatementRollback)
            {
                throw new EmbeddedTriggerDepthException(
                    exception,
                    exception.LastInsertRowId,
                    preserveChanges: state.Changed);
            }

            RestoreTables(context.Tables, backup);
            throw;
        }
        catch (EmbeddedSqlException exception)
        {
            RestoreTables(context.Tables, backup);
            if (state.LiveLastInsertRowId != initialLastInsertRowId)
                throw new EmbeddedStatementAbortException(exception, state.LiveLastInsertRowId);
            throw;
        }
        catch
        {
            RestoreTables(context.Tables, backup);
            throw;
        }
    }

    private ExecutionResult PerformRowTriggeredInsert(
        InsertStatement statement,
        SqlValue[] parameters,
        QueryContext context)
    {
        if (!context.Tables.TryGetValue(statement.TableName, out var table))
            throw new EmbeddedSqlException($"no such table: {statement.TableName}");
        MarkTriggerStatementRollbackRequirement(
            context,
            table,
            TriggerMutationKind.Insert);
        if (context.TriggerState is { } insertState
            && TriggerInsertUsesAbortCapableDefault(statement, table))
        {
            insertState.RequiresStatementRollback = true;
        }
        if (context.ForeignKeysEnabled
            && (statement.ConflictAlgorithm == InsertConflictAlgorithm.Replace
                || table.HasNonDefaultConflictAlgorithms))
        {
            ValidateForeignKeyActionTriggerPrograms(
                context,
                statement.TableName,
                TriggerEvent.Delete);
        }

        var beforeTriggers = GetRowTriggers(
            context,
            statement.TableName,
            TriggerTiming.Before,
            TriggerEvent.Insert);
        var afterTriggers = GetRowTriggers(
            context,
            statement.TableName,
            TriggerTiming.After,
            TriggerEvent.Insert);
        ValidateTriggerPrograms(
            context,
            statement.TableName,
            TriggerEvent.Insert,
            beforeTriggers.Concat(afterTriggers));
        if (context.RecursiveTriggersEnabled
            && (statement.ConflictAlgorithm == InsertConflictAlgorithm.Replace
                || table.HasNonDefaultConflictAlgorithms)
            && !context.InheritedTriggerConflict)
        {
            var beforeDelete = GetRowTriggers(
                context,
                statement.TableName,
                TriggerTiming.Before,
                TriggerEvent.Delete);
            var afterDelete = GetRowTriggers(
                context,
                statement.TableName,
                TriggerTiming.After,
                TriggerEvent.Delete);
            ValidateTriggerPrograms(
                context,
                statement.TableName,
                TriggerEvent.Delete,
                beforeDelete.Concat(afterDelete));
        }
        var plan = PrepareInsert(statement, table, context);
        var sourceRows = statement.Source is null
            ? null
            : ExecuteQuery(statement.Source, parameters, context, outerRow: null).Rows;
        var inputRows = sourceRows is null
            ? statement.Rows.Select(row =>
                (IReadOnlyList<SqlValue>)row.Select(expression =>
                    Evaluate(expression, parameters, row: null, context)).ToArray()).ToArray()
            : sourceRows.Select(row => (IReadOnlyList<SqlValue>)row.ToArray()).ToArray();
        var returningRows = new List<SqlValue[]>();
        string[]? returningColumns = null;
        var rowsAffected = 0;
        long? lastInsertRowId = null;

        foreach (var values in inputRows)
        {
            context.CheckInterrupt();
            ResetInsertPlan(table, plan);
            var (row, rowId) = BuildInsertRow(
                statement,
                table,
                plan,
                values,
                parameters,
                context,
                allowExistingRowid: true,
                validateCheckConstraints: false,
                resolveNotNullReplace: false,
                deferRowidTracking: true);
            var automaticRowId = UsesAutomaticRowId(table, plan, values);
            var beforeFrame = new TriggerRowFrame(
                Old: null,
                New: CreateBeforeInsertImage(table, row, rowId, automaticRowId, parameters, context));
            if (FireRowTriggers(beforeTriggers, beforeFrame, context))
            {
                ResetInsertPlan(table, plan);
                continue;
            }
            if (automaticRowId)
                rowId = FinalizeAutomaticRowId(statement.TableName, table, plan, row, parameters, context);
            else if (table.HasRowid)
                FinalizeExplicitRowId(plan, rowId);

            var replacementAttempted = false;
            try
            {
                ResolveNotNullReplaceDefaults(statement, table, row, context);
                ComputeGeneratedColumns(table, statement.TableName, row, parameters, context);
                ValidateCheckConstraints(statement.TableName, table, row, rowId, parameters, context);
                if (statement.ConflictAlgorithm == InsertConflictAlgorithm.Replace)
                {
                    replacementAttempted = true;
                    CommitRowTriggeredReplacement(context, statement.TableName, table, row, rowId);
                }
                else
                {
                    CommitInserts(context, statement.TableName, table, [row], [rowId]);
                }
            }
            catch (EmbeddedSqlException exception)
            {
                var algorithm = ResolveConstraintConflictAlgorithm(
                    exception,
                    statement.ConflictAlgorithm);
                switch (algorithm)
                {
                    case InsertConflictAlgorithm.Ignore:
                        ResetInsertPlan(table, plan);
                        continue;
                    case InsertConflictAlgorithm.Fail:
                        throw new EmbeddedConflictFailException(
                            exception,
                            context.TriggerState?.LiveLastInsertRowId ?? context.LastInsertRowId);
                    case InsertConflictAlgorithm.Rollback:
                        throw new EmbeddedConflictRollbackException(exception);
                    case InsertConflictAlgorithm.Replace
                        when !replacementAttempted
                            && exception.Message.StartsWith("UNIQUE constraint failed:", StringComparison.Ordinal):
                        CommitRowTriggeredReplacement(context, statement.TableName, table, row, rowId);
                        break;
                    case InsertConflictAlgorithm.Replace:
                    case InsertConflictAlgorithm.Abort:
                        throw;
                    default:
                        throw new InvalidOperationException($"Unknown conflict algorithm {algorithm}.");
                }
            }

            rowsAffected++;
            context.TriggerState!.Changed = true;
            if (table.HasRowid)
            {
                lastInsertRowId = rowId;
                context.TriggerState.LiveLastInsertRowId = rowId;
            }
            AppendReturningRow(
                statement.Returning,
                statement.TableName,
                table,
                row,
                rowId,
                parameters,
                context,
                returningRows,
                ref returningColumns,
                lastInsertRowId);
            var afterFrame = new TriggerRowFrame(
                Old: null,
                New: CreateTriggerRowImage(table, row, rowId));
            _ = FireRowTriggers(afterTriggers, afterFrame, context);
        }

        if (statement.Returning is not null && returningColumns is null)
        {
            returningColumns = BuildReturningResult(
                statement.Returning,
                statement.TableName,
                table,
                [],
                [],
                0,
                context.TriggerState!.Changed,
                parameters,
                context,
                lastInsertRowId).Columns;
        }

        return new ExecutionResult(
            returningColumns ?? [],
            returningRows,
            rowsAffected,
            rowsAffected > 0 || context.TriggerState!.Changed)
        {
            LastInsertRowId = lastInsertRowId,
        };
    }

    private ExecutionResult PerformRowTriggeredUpdate(
        UpdateStatement statement,
        SqlValue[] parameters,
        QueryContext context,
        IReadOnlySet<string> updatedColumns)
    {
        if (!context.Tables.TryGetValue(statement.TableName, out var table))
            throw new EmbeddedSqlException($"no such table: {statement.TableName}");
        var beforeTriggers = GetRowTriggers(
            context,
            statement.TableName,
            TriggerTiming.Before,
            TriggerEvent.Update,
            updatedColumns);
        var afterTriggers = GetRowTriggers(
            context,
            statement.TableName,
            TriggerTiming.After,
            TriggerEvent.Update,
            updatedColumns);
        ValidateTriggerPrograms(
            context,
            statement.TableName,
            TriggerEvent.Update,
            beforeTriggers.Concat(afterTriggers));
        var plan = PrepareUpdate(statement, table, context);
        MarkTriggerStatementRollbackRequirement(
            context,
            table,
            TriggerMutationKind.Update,
            plan);
        var selectedPositions = statement.Limit is null
            ? null
            : SelectLimitedDmlPositions(
                statement.TargetQualifier,
                table,
                statement.Where,
                statement.EffectiveOrderBy,
                statement.Limit,
                statement.Offset,
                statement.Assignments.Select(assignment => assignment.Value),
                statement.Returning,
                parameters,
                context);
        IReadOnlyList<TriggerRowIdentity> candidates;
        IReadOnlyList<SourceRow?> evaluationRows = [];
        if (statement.From is not null)
        {
            var matches = MatchUpdateFromRows(statement, table, parameters, context);
            candidates = [.. matches.Select(match => CaptureTriggerRowIdentity(table, match.Position))];
            evaluationRows = [.. matches.Select(match => (SourceRow?)match.Row)];
        }
        else
        {
            candidates = selectedPositions is null
                ? CaptureMatchingTriggerRowIdentities(
                    table,
                    statement.TargetQualifier,
                    statement.Where,
                    parameters,
                    context)
                : CaptureTriggerRowIdentities(table, selectedPositions);
        }
        var returningRows = new List<SqlValue[]>();
        string[]? returningColumns = null;
        var rowsAffected = 0;

        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var identity = candidates[candidateIndex];
            var evaluationRow = candidateIndex < evaluationRows.Count ? evaluationRows[candidateIndex] : null;
            context.CheckInterrupt();
            var position = FindTriggerRowPosition(table, identity);
            if (position < 0)
                continue;

            var original = table.Rows[position].ToArray();
            var oldRowId = table.HasRowid ? table.RowIds[position] : position + 1;
            var (updated, newRowId) = BuildUpdatedRow(
                statement,
                table,
                plan,
                original,
                oldRowId,
                parameters,
                context with { PreserveSubqueryMemoSnapshot = true },
                validateCheckConstraints: false,
                enforceGeneratedNotNull: false,
                evaluationRow: evaluationRow);
            var frame = new TriggerRowFrame(
                CreateTriggerRowImage(table, original, oldRowId),
                CreateTriggerRowImage(table, updated, newRowId));
            if (FireRowTriggers(beforeTriggers, frame, context))
                continue;

            position = FindTriggerRowPosition(table, identity);
            if (position < 0)
                continue;
            updated = ReloadColumnsTheUpdateDoesNotAssign(table, plan, updated, table.Rows[position]);
            var replacementAttempted = false;
            try
            {
                ResolveNotNullReplaceDefaults(context.ConflictAlgorithmOverride, table, updated, context);
                // Deferred from BuildUpdatedRow so a BEFORE UPDATE trigger can observe/suppress
                // the row first; RAISE(IGNORE) skips the update and the check entirely.
                EnforceGeneratedNotNullConstraints(table, statement.TableName, updated);
                ValidateCheckConstraints(statement.TableName, table, updated, newRowId, parameters, context);
                if (context.ConflictAlgorithmOverride == InsertConflictAlgorithm.Replace)
                {
                    replacementAttempted = true;
                    CommitRowTriggeredUpdateReplacement(
                        context,
                        statement.TableName,
                        table,
                        plan,
                        identity,
                        updated,
                        newRowId);
                }
                else
                {
                    CommitTriggerRowUpdate(context, statement.TableName, table, plan, position, updated, newRowId);
                }
            }
            catch (EmbeddedSqlException exception)
                when (exception is not EmbeddedStatementAbortException)
            {
                var algorithm = ResolveConstraintConflictAlgorithm(
                    exception,
                    context.ConflictAlgorithmOverride);
                switch (algorithm)
                {
                    case InsertConflictAlgorithm.Ignore:
                        continue;
                    case InsertConflictAlgorithm.Fail:
                        throw new EmbeddedConflictFailException(
                            exception,
                            context.TriggerState?.LiveLastInsertRowId ?? context.LastInsertRowId);
                    case InsertConflictAlgorithm.Rollback:
                        throw new EmbeddedConflictRollbackException(exception);
                    case InsertConflictAlgorithm.Replace
                        when !replacementAttempted
                            && exception.Message.StartsWith("UNIQUE constraint failed:", StringComparison.Ordinal):
                        CommitRowTriggeredUpdateReplacement(
                            context,
                            statement.TableName,
                            table,
                            plan,
                            identity,
                            updated,
                            newRowId);
                        break;
                    case InsertConflictAlgorithm.Replace:
                    case InsertConflictAlgorithm.Abort:
                        throw;
                    default:
                        throw new InvalidOperationException($"Unknown conflict algorithm {algorithm}.");
                }
            }
            rowsAffected++;
            context.TriggerState!.Changed = true;
            _ = FireRowTriggers(afterTriggers, frame, context);
            position = FindTriggerRowPosition(table, identity);
            var returningRow = position >= 0 ? table.Rows[position] : updated;
            var returningRowId = position >= 0 && table.HasRowid
                ? table.RowIds[position]
                : newRowId;
            AppendReturningRow(
                statement.Returning,
                statement.TableName,
                table,
                returningRow,
                returningRowId,
                parameters,
                context,
                returningRows,
                ref returningColumns);
        }

        if (statement.Returning is not null && returningColumns is null)
        {
            returningColumns = BuildReturningResult(
                statement.Returning,
                statement.TableName,
                table,
                [],
                [],
                0,
                context.TriggerState!.Changed,
                parameters,
                context).Columns;
        }

        return new ExecutionResult(
            returningColumns ?? [],
            returningRows,
            rowsAffected,
            rowsAffected > 0 || context.TriggerState!.Changed);
    }

    // SQLite reloads every column an UPDATE does not assign after its BEFORE triggers have run,
    // so a trigger that rewrote the row being updated is not silently reverted by the image that
    // was computed before the trigger fired. Assigned columns keep the value the UPDATE produced,
    // and the rowid alias is excluded because SQLite drives it from the pre-trigger rowid.
    private static SqlValue[] ReloadColumnsTheUpdateDoesNotAssign(
        EmbeddedTable table,
        UpdatePlan plan,
        SqlValue[] updated,
        SqlValue[] current)
    {
        var assigned = plan.ColumnAssignments.Select(assignment => assignment.Index).ToHashSet();
        var reloaded = updated.ToArray();
        for (var index = 0; index < table.Columns.Length && index < current.Length; index++)
        {
            if (assigned.Contains(index)
                || index == plan.AliasIndex
                || table.ColumnDefinitions[index].IsGenerated)
            {
                continue;
            }

            reloaded[index] = current[index];
        }

        return reloaded;
    }

    private ExecutionResult PerformRowTriggeredDelete(
        DeleteStatement statement,
        SqlValue[] parameters,
        QueryContext context)
    {
        if (!context.Tables.TryGetValue(statement.TableName, out var table))
            throw new EmbeddedSqlException($"no such table: {statement.TableName}");
        MarkTriggerStatementRollbackRequirement(
            context,
            table,
            TriggerMutationKind.Delete);

        var beforeTriggers = GetRowTriggers(
            context,
            statement.TableName,
            TriggerTiming.Before,
            TriggerEvent.Delete);
        var afterTriggers = GetRowTriggers(
            context,
            statement.TableName,
            TriggerTiming.After,
            TriggerEvent.Delete);
        ValidateTriggerPrograms(
            context,
            statement.TableName,
            TriggerEvent.Delete,
            beforeTriggers.Concat(afterTriggers));
        var selectedPositions = statement.Limit is null
            ? null
            : SelectLimitedDmlPositions(
                statement.TargetQualifier,
                table,
                statement.Where,
                statement.EffectiveOrderBy,
                statement.Limit,
                statement.Offset,
                [],
                statement.Returning,
                parameters,
                context);
        var candidates = selectedPositions is null
            ? CaptureMatchingTriggerRowIdentities(
                table,
                statement.TargetQualifier,
                statement.Where,
                parameters,
                context)
            : CaptureTriggerRowIdentities(table, selectedPositions);
        var returningRows = new List<SqlValue[]>();
        string[]? returningColumns = null;
        var rowsAffected = 0;

        foreach (var identity in candidates)
        {
            context.CheckInterrupt();
            var position = FindTriggerRowPosition(table, identity);
            if (position < 0)
                continue;

            var row = table.Rows[position].ToArray();
            var rowId = table.HasRowid ? table.RowIds[position] : position + 1;
            var frame = new TriggerRowFrame(
                CreateTriggerRowImage(table, row, rowId),
                New: null);
            if (FireRowTriggers(beforeTriggers, frame, context))
                continue;

            position = FindTriggerRowPosition(table, identity);
            if (position < 0)
                continue;
            DeleteTriggerRow(context, statement.TableName, table, position, row);
            rowsAffected++;
            context.TriggerState!.Changed = true;
            AppendReturningRow(
                statement.Returning,
                statement.TableName,
                table,
                row,
                rowId,
                parameters,
                context,
                returningRows,
                ref returningColumns);
            _ = FireRowTriggers(afterTriggers, frame, context);
        }

        if (statement.Returning is not null && returningColumns is null)
        {
            returningColumns = BuildReturningResult(
                statement.Returning,
                statement.TableName,
                table,
                [],
                [],
                0,
                context.TriggerState!.Changed,
                parameters,
                context).Columns;
        }

        return new ExecutionResult(
            returningColumns ?? [],
            returningRows,
            rowsAffected,
            rowsAffected > 0 || context.TriggerState!.Changed);
    }

    private void CommitRowTriggeredReplacement(
        QueryContext context,
        string tableName,
        EmbeddedTable table,
        SqlValue[] candidate,
        long candidateRowId)
    {
        table.ValidateRows(tableName, [candidate]);
        ValidatePrimaryKey(tableName, table, [candidate]);
        DeleteRowsReplacedBy(context, tableName, table, candidate, candidateRowId, keptPosition: -1);
        CommitInserts(context, tableName, table, [candidate], [candidateRowId]);
    }

    /// <summary>
    /// Applies an UPDATE OR REPLACE row by first deleting the rows its new image displaces, then
    /// rewriting the target in place. The target itself is excluded from the displaced set: with an
    /// unchanged rowid it always reports a rowid conflict against its own position.
    /// </summary>
    private void CommitRowTriggeredUpdateReplacement(
        QueryContext context,
        string tableName,
        EmbeddedTable table,
        UpdatePlan plan,
        TriggerRowIdentity identity,
        SqlValue[] updated,
        long newRowId)
    {
        var position = FindTriggerRowPosition(table, identity);
        if (position < 0)
            return;

        table.ValidateRows(tableName, [updated]);
        DeleteRowsReplacedBy(context, tableName, table, updated, newRowId, position);
        position = FindTriggerRowPosition(table, identity);
        if (position < 0)
            return;

        CommitTriggerRowUpdate(context, tableName, table, plan, position, updated, newRowId);
    }

    private void CommitTriggerRowUpdate(
        QueryContext context,
        string tableName,
        EmbeddedTable table,
        UpdatePlan plan,
        int position,
        SqlValue[] updated,
        long newRowId)
    {
        var rows = table.Rows.Select(row => row.ToArray()).ToList();
        var rowIds = table.RowIds.Count == table.Rows.Count
            ? table.RowIds.ToList()
            : Enumerable.Range(1, table.Rows.Count).Select(index => (long)index).ToList();
        rows[position] = updated;
        if (table.HasRowid)
            rowIds[position] = newRowId;
        CommitUpdates(context, tableName, table, table.Rows, rows, rowIds, plan, [position]);
    }

    /// <summary>
    /// Removes the rows an OR REPLACE mutation displaces. <paramref name="keptPosition"/> is the
    /// position of the row being rewritten by an UPDATE, which must survive even though it can
    /// report a rowid conflict against itself; INSERT passes -1 because it has no such row.
    /// </summary>
    private void DeleteRowsReplacedBy(
        QueryContext context,
        string tableName,
        EmbeddedTable table,
        SqlValue[] candidate,
        long candidateRowId,
        int keptPosition)
    {
        var conflicts = FindInsertUniqueConflicts(tableName, table, candidate, candidateRowId)
            .Select(conflict => conflict.RowPosition)
            .Where(position => position != keptPosition)
            .Distinct()
            .Select(position => CaptureTriggerRowIdentity(table, position))
            .ToArray();
        foreach (var identity in conflicts)
        {
            var position = FindTriggerRowPosition(table, identity);
            if (position < 0)
                continue;
            var row = table.Rows[position].ToArray();
            var rowId = table.HasRowid ? table.RowIds[position] : position + 1;
            var frame = new TriggerRowFrame(
                CreateTriggerRowImage(table, row, rowId),
                New: null);
            if (context.RecursiveTriggersEnabled)
            {
                var beforeDelete = GetRowTriggers(
                    context,
                    tableName,
                    TriggerTiming.Before,
                    TriggerEvent.Delete);
                if (FireRowTriggers(beforeDelete, frame, context))
                    continue;
            }

            DeleteTriggerRow(context, tableName, table, position, row);
            if (context.RecursiveTriggersEnabled)
            {
                var afterDelete = GetRowTriggers(
                    context,
                    tableName,
                    TriggerTiming.After,
                    TriggerEvent.Delete);
                _ = FireRowTriggers(afterDelete, frame, context);
            }
        }
    }

    private void DeleteTriggerRow(
        QueryContext context,
        string tableName,
        EmbeddedTable table,
        int position,
        SqlValue[] deletedRow)
    {
        var originalRows = table.Rows.Select(row => row.ToArray()).ToArray();
        var deletedRowId = table.HasRowid ? table.RowIds[position] : position + 1;
        MarkTriggerStatementRollbackRequirement(
            context,
            table,
            TriggerMutationKind.Delete);
        table.Rows.RemoveAt(position);
        if (position < table.RowIds.Count)
            table.RowIds.RemoveAt(position);
        ValidateForeignKeysAfterDelete(
            context,
            tableName,
            table,
            originalRows,
            table.Rows,
            [deletedRow]);
        if (table.HasRowid)
            RecordBlobMutation(tableName, deletedRowId);
        context.ReportRowChange(SqliteChangeOperation.Delete, tableName, table, deletedRowId);
    }

    private void AppendReturningRow(
        IReadOnlyList<Projection>? returning,
        string tableName,
        EmbeddedTable table,
        SqlValue[] row,
        long rowId,
        SqlValue[] parameters,
        QueryContext context,
        ICollection<SqlValue[]> outputRows,
        ref string[]? outputColumns,
        long? lastInsertRowId = null)
    {
        if (returning is null)
            return;

        var result = BuildReturningResult(
            returning,
            tableName,
            table,
            [row],
            [rowId],
            1,
            true,
            parameters,
            context,
            lastInsertRowId);
        outputColumns ??= result.Columns;
        outputRows.Add(result.Rows[0]);
    }

    private static InsertConflictAlgorithm ResolveConstraintConflictAlgorithm(
        EmbeddedSqlException exception,
        InsertConflictAlgorithm? statementAlgorithm)
        => IsConflictAlgorithmConstraint(exception)
            ? statementAlgorithm
                ?? exception.ConflictAlgorithm
                ?? InsertConflictAlgorithm.Abort
            : InsertConflictAlgorithm.Abort;

    private TriggerRowImage CreateTriggerRowImage(
        EmbeddedTable table,
        SqlValue[] row,
        long rowId)
        => new(
            table.Columns,
            row.ToArray(),
            table.HasRowid,
            rowId,
            table.RowidAliasColumnIndex);

    private TriggerRowImage CreateBeforeInsertImage(
        EmbeddedTable table,
        SqlValue[] row,
        long rowId,
        bool automaticRowId,
        SqlValue[] parameters,
        QueryContext context)
    {
        if (!automaticRowId)
            return CreateTriggerRowImage(table, row, rowId);

        var values = row.ToArray();
        if (table.RowidAliasColumnIndex >= 0)
            values[table.RowidAliasColumnIndex] = SqlValue.Integer(-1);
        ComputeGeneratedColumns(table, table.Name, values, parameters, context);
        return new TriggerRowImage(
            table.Columns,
            values,
            HasRowid: true,
            RowId: -1,
            RowidAliasColumnIndex: table.RowidAliasColumnIndex);
    }

    private static bool UsesAutomaticRowId(
        EmbeddedTable table,
        InsertPlan plan,
        IReadOnlyList<SqlValue> values)
    {
        if (!table.HasRowid)
            return false;
        if (plan.RowidTargetPosition >= 0)
            return values[plan.RowidTargetPosition].Kind == SqlValueKind.Null;
        if (plan.AliasIndex < 0)
            return true;

        for (var index = 0; index < plan.TargetIndices.Length; index++)
        {
            if (plan.TargetIndices[index] == plan.AliasIndex)
                return values[index].Kind == SqlValueKind.Null;
        }

        return true;
    }

    private long FinalizeAutomaticRowId(
        string tableName,
        EmbeddedTable table,
        InsertPlan plan,
        SqlValue[] row,
        SqlValue[] parameters,
        QueryContext context)
    {
        ResetInsertPlan(table, plan);
        var rowId = plan.AutoIncrement is null
            ? plan.AnyRow ? NextAutoRowId(plan.LargestRowId, plan.Used) : 1
            : plan.AutoIncrement.NextRowId(plan.AnyRow, plan.LargestRowId);
        plan.Used.Add(rowId);
        plan.AnyRow = true;
        if (rowId > plan.LargestRowId)
            plan.LargestRowId = rowId;
        if (plan.AliasIndex >= 0)
            row[plan.AliasIndex] = SqlValue.Integer(rowId);
        ComputeGeneratedColumns(table, tableName, row, parameters, context);
        return rowId;
    }

    private static void ResetInsertPlan(EmbeddedTable table, InsertPlan plan)
    {
        plan.Used.Clear();
        plan.Used.UnionWith(table.RowIds);
        plan.AnyRow = table.RowIds.Count > 0;
        plan.LargestRowId = plan.AnyRow ? table.RowIds.Max() : long.MinValue;
    }

    private static void FinalizeExplicitRowId(InsertPlan plan, long rowId)
    {
        plan.AutoIncrement?.Observe(rowId);
        plan.Used.Add(rowId);
        if (!plan.AnyRow || rowId > plan.LargestRowId)
            plan.LargestRowId = rowId;
        plan.AnyRow = true;
    }

    private IReadOnlyList<TriggerRowIdentity> CaptureTriggerRowIdentities(
        EmbeddedTable table,
        IReadOnlySet<int>? selectedPositions)
    {
        var identities = new List<TriggerRowIdentity>(selectedPositions?.Count ?? table.Rows.Count);
        for (var position = 0; position < table.Rows.Count; position++)
        {
            if (selectedPositions is null || selectedPositions.Contains(position))
                identities.Add(CaptureTriggerRowIdentity(table, position));
        }

        return identities;
    }

    private IReadOnlyList<TriggerRowIdentity> CaptureMatchingTriggerRowIdentities(
        EmbeddedTable table,
        string qualifier,
        Expression? where,
        SqlValue[] parameters,
        QueryContext context)
    {
        var identities = new List<TriggerRowIdentity>(table.Rows.Count);
        for (var position = 0; position < table.Rows.Count; position++)
        {
            var rowId = table.HasRowid ? table.RowIds[position] : position + 1;
            var source = CreateDmlTargetRow(table, qualifier, table.Rows[position], rowId);
            if (where is null || IsTrue(Evaluate(where, parameters, source, context)))
                identities.Add(CaptureTriggerRowIdentity(table, position));
        }

        return identities;
    }

    private TriggerRowIdentity CaptureTriggerRowIdentity(EmbeddedTable table, int position)
        => table.HasRowid
            ? new TriggerRowIdentity(table.RowIds[position], [])
            : new TriggerRowIdentity(
                RowId: null,
                table.PrimaryKeyColumns
                    .Select(column => table.Rows[position][column.Index])
                    .ToArray());

    private int FindTriggerRowPosition(EmbeddedTable table, TriggerRowIdentity identity)
    {
        if (identity.RowId is { } rowId)
            return table.RowIds.IndexOf(rowId);

        var schema = table.PrimaryKeySchema
            ?? throw new InvalidOperationException($"WITHOUT ROWID table {table.Name} lost its primary-key schema.");
        for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            var matches = true;
            for (var keyIndex = 0; keyIndex < schema.Terms.Count; keyIndex++)
            {
                var term = schema.Terms[keyIndex];
                if (Compare(
                        table.Rows[rowIndex][term.ColumnIndex],
                        identity.PrimaryKey[keyIndex],
                        term.Collation.Name) == 0)
                {
                    continue;
                }

                matches = false;
                break;
            }

            if (matches)
                return rowIndex;
        }

        return -1;
    }

    private ExecutionResult PerformRowTriggeredUpsert(
        InsertStatement statement,
        SqlValue[] parameters,
        QueryContext context)
    {
        if (statement.Upsert is null)
            throw new InvalidOperationException("UPSERT execution requires an UPSERT clause.");
        if (!context.Tables.TryGetValue(statement.TableName, out var table))
            throw new EmbeddedSqlException($"no such table: {statement.TableName}");

        var insertPlan = PrepareInsert(statement, table, context);
        MarkTriggerStatementRollbackRequirement(
            context,
            table,
            TriggerMutationKind.Insert);
        if (context.TriggerState is { } insertState
            && TriggerInsertUsesAbortCapableDefault(statement, table))
        {
            insertState.RequiresStatementRollback = true;
        }
        var doUpdateContext = context with
        {
            ConflictAlgorithmOverride = InsertConflictAlgorithm.Abort,
        };
        var upserts = statement.Upsert.Clauses()
            .Select(upsert =>
            {
                var update = upsert.Action as DoUpdateUpsertAction;
                if (update is null && upsert.Action is not DoNothingUpsertAction)
                    throw new InvalidOperationException("Unknown UPSERT action.");

                UpdatePlan? plan = null;
                if (update is not null)
                {
                    var updatedColumns = update.Assignments
                        .Select(assignment => assignment.Column)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var updateStatement = new UpdateStatement(
                        statement.TableName,
                        update.Assignments,
                        Where: null);
                    plan = PrepareUpdate(updateStatement, table, doUpdateContext);
                    MarkTriggerStatementRollbackRequirement(
                        doUpdateContext,
                        table,
                        TriggerMutationKind.Update,
                        plan);
                    ValidateUpsertUpdateExpressions(
                        statement.TableName,
                        update.Assignments,
                        update.Where,
                        allowTriggerQualifiers: context.InsideTrigger);
                    ValidateForeignKeyActionTriggerPrograms(
                        doUpdateContext,
                        statement.TableName,
                        TriggerEvent.Update,
                        updatedColumns);
                    ValidateTriggerPrograms(
                        doUpdateContext,
                        statement.TableName,
                        TriggerEvent.Update,
                        GetRowTriggers(
                            context,
                            statement.TableName,
                            TriggerTiming.Before,
                            TriggerEvent.Update,
                            updatedColumns).Concat(GetRowTriggers(
                                context,
                                statement.TableName,
                                TriggerTiming.After,
                                TriggerEvent.Update,
                                updatedColumns)));
                }

                return new ResolvedUpsertClause(
                    upsert,
                    ResolveUpsertConflictTarget(statement.TableName, table, upsert),
                    plan);
            })
            .ToArray();
        if (upserts.Length == 0)
            throw new InvalidOperationException("UPSERT execution requires an UPSERT clause.");
        if (upserts.Take(upserts.Length - 1).Any(upsert => upsert.Clause.Target.Count == 0))
        {
            throw new EmbeddedSqlException(
                "ON CONFLICT clause without a conflict target must be the last clause in the UPSERT chain.");
        }

        var beforeInsert = GetRowTriggers(
            context,
            statement.TableName,
            TriggerTiming.Before,
            TriggerEvent.Insert);
        var afterInsert = GetRowTriggers(
            context,
            statement.TableName,
            TriggerTiming.After,
            TriggerEvent.Insert);
        ValidateTriggerPrograms(
            context,
            statement.TableName,
            TriggerEvent.Insert,
            beforeInsert.Concat(afterInsert));
        IReadOnlyList<SqlValue[]> inputRows = statement.Source is null
            ? statement.Rows
                .Select(expressions => expressions
                    .Select(expression => Evaluate(expression, parameters, row: null, context))
                    .ToArray())
                .ToArray()
            : ExecuteQuery(statement.Source, parameters, context, outerRow: null).Rows;
        var returningRows = new List<SqlValue[]>();
        string[]? returningColumns = null;
        var rowsAffected = 0;
        long? lastInsertRowId = null;

        foreach (var values in inputRows)
        {
            ResetInsertPlan(table, insertPlan);
            var (candidate, candidateRowId) = BuildInsertRow(
                statement,
                table,
                insertPlan,
                values,
                parameters,
                context,
                allowExistingRowid: true,
                validateCheckConstraints: false,
                resolveNotNullReplace: false,
                deferRowidTracking: true);
            var automaticRowId = UsesAutomaticRowId(table, insertPlan, values);
            var beforeInsertFrame = new TriggerRowFrame(
                Old: null,
                CreateBeforeInsertImage(
                    table,
                    candidate,
                    candidateRowId,
                    automaticRowId,
                    parameters,
                    context));
            if (FireRowTriggers(beforeInsert, beforeInsertFrame, context))
            {
                ResetInsertPlan(table, insertPlan);
                continue;
            }
            if (automaticRowId)
            {
                candidateRowId = FinalizeAutomaticRowId(
                    statement.TableName,
                    table,
                    insertPlan,
                    candidate,
                    parameters,
                    context);
            }
            else if (table.HasRowid)
            {
                FinalizeExplicitRowId(insertPlan, candidateRowId);
            }

            try
            {
                ResolveNotNullReplaceDefaults(statement, table, candidate, context);
                ComputeGeneratedColumns(table, statement.TableName, candidate, parameters, context);
            }
            catch (EmbeddedSqlException exception)
            {
                if (HandleUpsertInsertConstraint(
                        statement,
                        table,
                        insertPlan,
                        candidate,
                        candidateRowId,
                        context,
                        exception))
                {
                    throw new InvalidOperationException(
                        "A pre-uniqueness UPSERT constraint unexpectedly performed replacement.");
                }
                continue;
            }
            ResolvedUpsertClause? selectedUpsert = null;
            var conflictPosition = -1;
            foreach (var upsert in upserts)
            {
                var position = FindUpsertConflictPosition(
                    table,
                    upsert.Target,
                    candidate,
                    candidateRowId,
                    table.Rows,
                    table.RowIds);
                if (position < 0)
                    continue;

                selectedUpsert = upsert;
                conflictPosition = position;
                break;
            }
            if (conflictPosition < 0)
            {
                var inserted = false;
                try
                {
                    ValidateCheckConstraints(
                        statement.TableName,
                        table,
                        candidate,
                        candidateRowId,
                        parameters,
                        context);
                    CommitInserts(
                        context,
                        statement.TableName,
                        table,
                        [candidate],
                        [candidateRowId]);
                    inserted = true;
                }
                catch (EmbeddedSqlException exception)
                {
                    inserted = HandleUpsertInsertConstraint(
                        statement,
                        table,
                        insertPlan,
                        candidate,
                        candidateRowId,
                        context,
                        exception);
                }
                if (!inserted)
                    continue;
                rowsAffected++;
                context.TriggerState!.Changed = true;
                if (table.HasRowid)
                {
                    lastInsertRowId = candidateRowId;
                    context.TriggerState.LiveLastInsertRowId = candidateRowId;
                }
                AppendReturningRow(
                    statement.Returning,
                    statement.TableName,
                    table,
                    candidate,
                    candidateRowId,
                    parameters,
                    context,
                    returningRows,
                    ref returningColumns,
                    lastInsertRowId);
                var afterInsertFrame = new TriggerRowFrame(
                    Old: null,
                    CreateTriggerRowImage(table, candidate, candidateRowId));
                _ = FireRowTriggers(afterInsert, afterInsertFrame, context);
                continue;
            }

            var updateAction = selectedUpsert!.Clause.Action as DoUpdateUpsertAction;
            var updatePlan = selectedUpsert.UpdatePlan;
            if (updateAction is null)
            {
                ResetInsertPlan(table, insertPlan);
                continue;
            }
            var updatedColumns = updateAction.Assignments
                .Select(assignment => assignment.Column)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var beforeUpdate = GetRowTriggers(
                context,
                statement.TableName,
                TriggerTiming.Before,
                TriggerEvent.Update,
                updatedColumns);
            var afterUpdate = GetRowTriggers(
                context,
                statement.TableName,
                TriggerTiming.After,
                TriggerEvent.Update,
                updatedColumns);

            var original = table.Rows[conflictPosition].ToArray();
            var originalRowId = table.HasRowid ? table.RowIds[conflictPosition] : conflictPosition + 1;
            var source = CreateUpsertSourceRow(
                statement.TableName,
                table,
                original,
                originalRowId,
                candidate);
            if (updateAction.Where is not null
                && !IsTrue(Evaluate(updateAction.Where, parameters, source, doUpdateContext)))
            {
                ResetInsertPlan(table, insertPlan);
                continue;
            }

            var (updated, updatedRowId) = BuildUpsertUpdatedRow(
                statement.TableName,
                table,
                updatePlan!,
                original,
                originalRowId,
                source,
                parameters,
                doUpdateContext);
            var updateFrame = new TriggerRowFrame(
                CreateTriggerRowImage(table, original, originalRowId),
                CreateTriggerRowImage(table, updated, updatedRowId));
            if (FireRowTriggers(beforeUpdate, updateFrame, doUpdateContext))
                continue;

            var identity = CaptureTriggerRowIdentity(table, conflictPosition);
            conflictPosition = FindTriggerRowPosition(table, identity);
            if (conflictPosition < 0)
                continue;
            ValidateCheckConstraints(
                statement.TableName,
                table,
                updated,
                updatedRowId,
                parameters,
                doUpdateContext);
            var updatedRows = table.Rows.Select(row => row.ToArray()).ToList();
            updatedRows[conflictPosition] = updated;
            var updatedRowIds = table.RowIds.Count == table.Rows.Count
                ? table.RowIds.ToList()
                : Enumerable.Range(1, table.Rows.Count).Select(index => (long)index).ToList();
            if (table.HasRowid && conflictPosition < updatedRowIds.Count)
                updatedRowIds[conflictPosition] = updatedRowId;
            CommitUpdates(
                doUpdateContext,
                statement.TableName,
                table,
                table.Rows,
                updatedRows,
                updatedRowIds,
                updatePlan!,
                [conflictPosition]);
            rowsAffected++;
            context.TriggerState!.Changed = true;
            AppendReturningRow(
                statement.Returning,
                statement.TableName,
                table,
                updated,
                updatedRowId,
                parameters,
                doUpdateContext,
                returningRows,
                ref returningColumns,
                lastInsertRowId);
            _ = FireRowTriggers(afterUpdate, updateFrame, doUpdateContext);
        }

        if (statement.Returning is not null && returningColumns is null)
        {
            returningColumns = BuildReturningResult(
                statement.Returning,
                statement.TableName,
                table,
                [],
                [],
                0,
                context.TriggerState!.Changed,
                parameters,
                context,
                lastInsertRowId).Columns;
        }

        return new ExecutionResult(
            returningColumns ?? [],
            returningRows,
            rowsAffected,
            rowsAffected > 0 || context.TriggerState!.Changed)
        {
            LastInsertRowId = lastInsertRowId,
        };
    }

    private bool HandleUpsertInsertConstraint(
        InsertStatement statement,
        EmbeddedTable table,
        InsertPlan insertPlan,
        SqlValue[] candidate,
        long candidateRowId,
        QueryContext context,
        EmbeddedSqlException exception)
    {
        var algorithm = ResolveConstraintConflictAlgorithm(
            exception,
            statement.ConflictAlgorithm);
        switch (algorithm)
        {
            case InsertConflictAlgorithm.Ignore:
                ResetInsertPlan(table, insertPlan);
                return false;
            case InsertConflictAlgorithm.Fail:
                throw new EmbeddedConflictFailException(
                    exception,
                    context.TriggerState?.LiveLastInsertRowId ?? context.LastInsertRowId);
            case InsertConflictAlgorithm.Rollback:
                throw new EmbeddedConflictRollbackException(exception);
            case InsertConflictAlgorithm.Replace
                when exception.Message.StartsWith("UNIQUE constraint failed:", StringComparison.Ordinal):
                CommitRowTriggeredReplacement(
                    context,
                    statement.TableName,
                    table,
                    candidate,
                    candidateRowId);
                return true;
            case InsertConflictAlgorithm.Replace:
            case InsertConflictAlgorithm.Abort:
                throw exception;
            default:
                throw new InvalidOperationException($"Unknown conflict algorithm {algorithm}.");
        }
    }

    private ExecutionResult PerformInsteadOfInsert(
        InsertStatement statement,
        SqlValue[] parameters,
        QueryContext context)
    {
        if (statement.Upsert is not null)
            throw new EmbeddedSqlException("cannot UPSERT a view");
        if (context.Views is null || !context.Views.TryGetValue(statement.TableName, out var view))
            throw new EmbeddedSqlException($"no such view: {statement.TableName}");

        var triggers = GetRowTriggers(
            context,
            statement.TableName,
            TriggerTiming.InsteadOf,
            TriggerEvent.Insert);
        if (triggers.Count == 0)
            throw new EmbeddedSqlException($"cannot modify {statement.TableName} because it is a view");
        var columns = ResolveViewColumns(view, EnterView(context, view.Name));
        ValidateTriggerPrograms(
            context,
            statement.TableName,
            TriggerEvent.Insert,
            triggers,
            columns,
            hasRowid: false);
        var targetColumns = statement.Columns ?? columns;
        var targetIndices = targetColumns.Select(column =>
        {
            var index = Array.FindIndex(
                columns,
                candidate => string.Equals(candidate, column, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                throw new EmbeddedSqlException($"table {statement.TableName} has no column named {column}");
            return index;
        }).ToArray();
        var sourceRows = statement.Source is null
            ? null
            : ExecuteQuery(statement.Source, parameters, context, outerRow: null).Rows;
        var inputs = sourceRows is null
            ? statement.Rows.Select(row =>
                (IReadOnlyList<SqlValue>)row.Select(expression =>
                    Evaluate(expression, parameters, row: null, context)).ToArray()).ToArray()
            : sourceRows.Select(row => (IReadOnlyList<SqlValue>)row.ToArray()).ToArray();
        var returningRows = new List<SqlValue[]>();
        var outputColumns = BuildOutputColumns(statement.TableName, columns);
        foreach (var input in inputs)
        {
            if (input.Count != targetIndices.Length)
                throw new EmbeddedSqlException(
                    $"table {statement.TableName} has {targetIndices.Length} columns but {input.Count} values were supplied");
            var values = Enumerable.Repeat(SqlValue.Null, columns.Length).ToArray();
            var assignedColumns = new HashSet<int>();
            for (var index = 0; index < input.Count; index++)
            {
                if (assignedColumns.Add(targetIndices[index]))
                    values[targetIndices[index]] = input[index];
            }
            var frame = new TriggerRowFrame(
                Old: null,
                new TriggerRowImage(columns, values, HasRowid: false, RowId: 0));
            if (FireRowTriggers(triggers, frame, context))
                continue;
            if (statement.Returning is null)
                continue;

            var source = new SourceRow(
                columns,
                values,
                BuildQualifiedColumns(statement.TableName, columns),
                OutputColumns: outputColumns);
            var output = new List<SqlValue>();
            foreach (var projection in statement.Returning)
            {
                if (projection.Expression is QualifiedStarExpression)
                    throw new EmbeddedSqlException("RETURNING may not use TABLE.* wildcards");
                if (projection.Expression is StarExpression)
                    output.AddRange(values);
                else
                    output.Add(Evaluate(projection.Expression, parameters, source, context));
            }
            returningRows.Add(output.ToArray());
        }

        return new ExecutionResult(
            statement.Returning is null
                ? []
                : GetColumnNames(statement.Returning, outputColumns, outputColumns),
            returningRows,
            0,
            context.TriggerState!.Changed);
    }

    private ExecutionResult PerformInsteadOfUpdate(
        UpdateStatement statement,
        SqlValue[] parameters,
        QueryContext context,
        IReadOnlySet<string> updatedColumns)
    {
        if (statement.Returning is not null || statement.Limit is not null)
        {
            throw new EmbeddedSqlException(
                "Managed INSTEAD OF UPDATE triggers do not support limited DML or RETURNING.");
        }
        if (context.Views is null || !context.Views.TryGetValue(statement.TableName, out var view))
            throw new EmbeddedSqlException($"no such view: {statement.TableName}");

        var triggers = GetRowTriggers(
            context,
            statement.TableName,
            TriggerTiming.InsteadOf,
            TriggerEvent.Update,
            updatedColumns);
        if (triggers.Count == 0)
            throw new EmbeddedSqlException($"cannot modify {statement.TableName} because it is a view");
        var columns = ResolveViewColumns(view, EnterView(context, view.Name));
        ValidateTriggerPrograms(
            context,
            statement.TableName,
            TriggerEvent.Update,
            triggers,
            columns,
            hasRowid: false);
        var result = ExecuteQuery(view.Query, parameters, EnterView(context, view.Name), outerRow: null);
        var assignments = statement.Assignments.Select(assignment =>
        {
            var index = Array.FindIndex(
                columns,
                column => string.Equals(column, assignment.Column, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                throw new EmbeddedSqlException($"no such column: {assignment.Column}");
            return (Index: index, assignment.Value);
        }).ToArray();
        foreach (var resultRow in result.Rows)
        {
            var oldValues = resultRow.ToArray();
            var source = new SourceRow(
                columns,
                oldValues,
                BuildQualifiedColumns(statement.TableName, columns));
            if (statement.Where is not null
                && !IsTrue(Evaluate(statement.Where, parameters, source, context)))
            {
                continue;
            }

            var newValues = oldValues.ToArray();
            foreach (var assignment in assignments)
                newValues[assignment.Index] = Evaluate(assignment.Value, parameters, source, context);
            var frame = new TriggerRowFrame(
                new TriggerRowImage(columns, oldValues, HasRowid: false, RowId: 0),
                new TriggerRowImage(columns, newValues, HasRowid: false, RowId: 0));
            _ = FireRowTriggers(triggers, frame, context);
        }

        return new ExecutionResult([], [], 0, context.TriggerState!.Changed);
    }

    private ExecutionResult PerformInsteadOfDelete(
        DeleteStatement statement,
        SqlValue[] parameters,
        QueryContext context)
    {
        if (statement.Returning is not null || statement.Limit is not null)
        {
            throw new EmbeddedSqlException(
                "Managed INSTEAD OF DELETE triggers do not support limited DML or RETURNING.");
        }
        if (context.Views is null || !context.Views.TryGetValue(statement.TableName, out var view))
            throw new EmbeddedSqlException($"no such view: {statement.TableName}");

        var triggers = GetRowTriggers(
            context,
            statement.TableName,
            TriggerTiming.InsteadOf,
            TriggerEvent.Delete);
        if (triggers.Count == 0)
            throw new EmbeddedSqlException($"cannot modify {statement.TableName} because it is a view");
        var columns = ResolveViewColumns(view, EnterView(context, view.Name));
        ValidateTriggerPrograms(
            context,
            statement.TableName,
            TriggerEvent.Delete,
            triggers,
            columns,
            hasRowid: false);
        var result = ExecuteQuery(view.Query, parameters, EnterView(context, view.Name), outerRow: null);
        foreach (var resultRow in result.Rows)
        {
            var values = resultRow.ToArray();
            var source = new SourceRow(
                columns,
                values,
                BuildQualifiedColumns(statement.TableName, columns));
            if (statement.Where is not null
                && !IsTrue(Evaluate(statement.Where, parameters, source, context)))
            {
                continue;
            }

            var frame = new TriggerRowFrame(
                new TriggerRowImage(columns, values, HasRowid: false, RowId: 0),
                New: null);
            _ = FireRowTriggers(triggers, frame, context);
        }

        return new ExecutionResult([], [], 0, context.TriggerState!.Changed);
    }

    private void ValidateTriggerPrograms(
        QueryContext context,
        string targetName,
        TriggerEvent triggerEvent,
        IEnumerable<TriggerDefinition> triggers)
    {
        if (!context.Tables.TryGetValue(targetName, out var table))
            throw new EmbeddedSqlException($"no such table: {targetName}");
        ValidateTriggerPrograms(
            context,
            targetName,
            triggerEvent,
            triggers,
            table.Columns,
            table.HasRowid);
    }

    private void ValidateTriggerPrograms(
        QueryContext context,
        string targetName,
        TriggerEvent triggerEvent,
        IEnumerable<TriggerDefinition> triggers,
        string[] columns,
        bool hasRowid)
    {
        var programs = triggers.ToArray();
        ValidateTriggerBodyTargets(context, programs);
        if (context.TriggerState is { } state
            && TriggerGraphRequiresStatementRollback(context, programs))
        {
            state.RequiresStatementRollback = true;
        }
        if (context.RecursiveTriggersEnabled)
            RejectUnboundedRecursiveTriggerCycles(context, programs);
        foreach (var trigger in programs)
        {
            ValidateTriggerSchema(trigger, context, context.CancellationToken);
        }
    }

    private void ValidateForeignKeyActionTriggerPrograms(
        QueryContext context,
        string parentTableName,
        TriggerEvent parentEvent,
        IReadOnlySet<string>? parentUpdatedColumns = null)
    {
        if (!context.ForeignKeysEnabled)
            return;
        var root = new TriggerMutationEdge(
            parentTableName,
            parentEvent,
            parentUpdatedColumns);
        foreach (var mutation in GetTransitiveForeignKeyActionMutationEdges(context, root))
        {
            ValidateTriggerPrograms(
                context,
                mutation.TableName,
                mutation.Event,
                GetMutationEdgeTriggers(context, mutation));
        }
    }

    private bool HasForeignKeyActionTriggers(
        QueryContext context,
        string parentTableName,
        TriggerEvent parentEvent,
        IReadOnlySet<string>? parentUpdatedColumns = null)
    {
        if (!context.ForeignKeysEnabled)
            return false;
        var root = new TriggerMutationEdge(
            parentTableName,
            parentEvent,
            parentUpdatedColumns);
        return GetTransitiveForeignKeyActionMutationEdges(context, root)
            .Any(mutation => GetMutationEdgeTriggers(context, mutation).Any());
    }

    private void ValidateTriggerBodyTargets(
        QueryContext context,
        IEnumerable<TriggerDefinition> triggers)
    {
        foreach (var trigger in triggers)
        {
            foreach (var bodyStatement in trigger.Body)
            {
                if (LocalizeTriggerBodyStatement(context, trigger, bodyStatement) is not { } statement)
                    continue;

                ValidateTriggerStatementQuerySources(context, statement);
                switch (statement)
                {
                    case InsertStatement insert when context.Tables.TryGetValue(insert.TableName, out var insertTable):
                        _ = PrepareInsert(insert, insertTable, context);
                        foreach (var upsert in insert.Upsert?.Clauses() ?? [])
                        {
                            _ = ResolveUpsertConflictTarget(
                                insert.TableName,
                                insertTable,
                                upsert);
                        }
                        break;
                    case InsertStatement insert when context.Views?.ContainsKey(insert.TableName) == true:
                        break;
                    case InsertStatement insert:
                        throw new EmbeddedSqlException($"no such table: {insert.TableName}");
                    case UpdateStatement update when context.Tables.TryGetValue(update.TableName, out var updateTable):
                        _ = PrepareUpdate(update, updateTable, context);
                        break;
                    case UpdateStatement update when context.Views?.ContainsKey(update.TableName) == true:
                        break;
                    case UpdateStatement update:
                        throw new EmbeddedSqlException($"no such table: {update.TableName}");
                    case DeleteStatement delete
                        when context.Tables.ContainsKey(delete.TableName)
                            || context.Views?.ContainsKey(delete.TableName) == true:
                        break;
                    case DeleteStatement delete:
                        throw new EmbeddedSqlException($"no such table: {delete.TableName}");
                    case QueryStatement query:
                        _ = DescribeQuery(query, context);
                        break;
                }
            }
        }
    }

    private void ValidateTriggerStatementQuerySources(
                QueryContext context,
                ParsedStatement statement)
    {
        var commonTableExpressions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        switch (statement)
        {
            case QueryStatement query:
                ValidateTriggerQuerySources(context, query, commonTableExpressions);
                break;
            case InsertStatement insert:
                if (insert.Source is not null)
                    ValidateTriggerQuerySources(context, insert.Source, commonTableExpressions);
                foreach (var expression in insert.Rows.SelectMany(row => row))
                    ValidateTriggerExpressionSources(context, expression, commonTableExpressions);
                foreach (var upsert in insert.Upsert?.Clauses() ?? [])
                {
                    foreach (var target in upsert.Target)
                    {
                        ValidateTriggerExpressionSources(
                            context,
                            target.Expression,
                            commonTableExpressions);
                    }
                    ValidateTriggerExpressionSources(
                        context,
                        upsert.TargetWhere,
                        commonTableExpressions);
                    if (upsert.Action is DoUpdateUpsertAction upsertUpdate)
                    {
                        foreach (var assignment in upsertUpdate.Assignments)
                            ValidateTriggerExpressionSources(context, assignment.Value, commonTableExpressions);
                        ValidateTriggerExpressionSources(context, upsertUpdate.Where, commonTableExpressions);
                    }
                }
                break;
            case UpdateStatement update:
                foreach (var expression in update.Assignments.Select(assignment => assignment.Value)
                             .Append(update.Where))
                {
                    ValidateTriggerExpressionSources(context, expression, commonTableExpressions);
                }
                break;
            case DeleteStatement delete:
                ValidateTriggerExpressionSources(context, delete.Where, commonTableExpressions);
                break;
        }
    }

    private void ValidateTriggerQuerySources(
        QueryContext context,
        QueryStatement query,
        HashSet<string> commonTableExpressions)
    {
        switch (query)
        {
            case SelectStatement select:
                ValidateTriggerSourceNames(context, select.Source, commonTableExpressions);
                foreach (var expression in select.Projections.Select(projection => projection.Expression)
                             .Append(select.Where)
                             .Concat(select.GroupBy)
                             .Append(select.Having)
                             .Concat(select.OrderBy.Select(term => term.Expression))
                             .Append(select.Limit)
                             .Append(select.Offset))
                {
                    ValidateTriggerExpressionSources(context, expression, commonTableExpressions);
                }
                foreach (var window in select.NamedWindows)
                {
                    foreach (var expression in window.Specification.PartitionBy
                                 .Concat(window.Specification.OrderBy.Select(term => term.Expression))
                                 .Append(window.Specification.Frame?.Start.Offset)
                                 .Append(window.Specification.Frame?.End.Offset))
                    {
                        ValidateTriggerExpressionSources(
                            context,
                            expression,
                            commonTableExpressions);
                    }
                }
                break;
            case ValuesClause values:
                foreach (var expression in values.Rows.SelectMany(row => row))
                    ValidateTriggerExpressionSources(context, expression, commonTableExpressions);
                break;
            case CompoundSelectStatement compound:
                foreach (var term in compound.Terms)
                    ValidateTriggerQuerySources(context, term, commonTableExpressions);
                foreach (var expression in compound.OrderBy.Select(term => term.Expression)
                             .Append(compound.Limit)
                             .Append(compound.Offset))
                {
                    ValidateTriggerExpressionSources(context, expression, commonTableExpressions);
                }
                break;
            case WithSelectStatement with:
                var withNames = new HashSet<string>(
                    commonTableExpressions,
                    StringComparer.OrdinalIgnoreCase);
                foreach (var commonTableExpression in with.CommonTableExpressions)
                {
                    withNames.Add(commonTableExpression.Name);
                    ValidateTriggerQuerySources(context, commonTableExpression.Query, withNames);
                }
                ValidateTriggerQuerySources(context, with.Query, withNames);
                break;
        }
    }

    private void ValidateTriggerSourceNames(
        QueryContext context,
        TableSource? source,
        HashSet<string> commonTableExpressions)
    {
        switch (source)
        {
            case null:
                return;
            case NamedTableSource named:
                if ((!named.IsSchemaQualified && !commonTableExpressions.Contains(named.Name))
                    && !context.Tables.ContainsKey(named.Name)
                    && context.Views?.ContainsKey(named.Name) != true
                    && !IsSchemaTable(named.Name))
                {
                    throw new EmbeddedSqlException($"no such table: {named.Name}");
                }
                return;
            case DerivedTableSource derived:
                ValidateTriggerQuerySources(context, derived.Query, commonTableExpressions);
                return;
            case TableValuedFunctionSource function:
                foreach (var argument in function.Arguments)
                    ValidateTriggerExpressionSources(context, argument, commonTableExpressions);
                return;
            case JoinTableSource join:
                ValidateTriggerSourceNames(context, join.Left, commonTableExpressions);
                ValidateTriggerSourceNames(context, join.Right, commonTableExpressions);
                ValidateTriggerExpressionSources(context, join.Condition, commonTableExpressions);
                return;
        }
    }

    private void ValidateTriggerExpressionSources(
        QueryContext context,
        Expression? expression,
        HashSet<string> commonTableExpressions)
    {
        foreach (var query in EnumerateExpressionQueries(expression))
            ValidateTriggerQuerySources(context, query, commonTableExpressions);
    }

    private static IEnumerable<QueryStatement> EnumerateExpressionQueries(Expression? expression)
    {
        switch (expression)
        {
            case null:
                yield break;
            case ScalarSubqueryExpression scalar:
                yield return scalar.Query;
                yield break;
            case ExistsExpression exists:
                yield return exists.Query;
                yield break;
            case InSubqueryExpression @in:
                foreach (var nested in EnumerateExpressionQueries(@in.Value))
                    yield return nested;
                yield return @in.Query;
                yield break;
            case FunctionExpression function:
                foreach (var argument in function.Arguments)
                {
                    foreach (var nested in EnumerateExpressionQueries(argument))
                        yield return nested;
                }
                foreach (var nested in EnumerateExpressionQueries(function.Filter))
                    yield return nested;
                if (function.Window is not null)
                {
                    foreach (var windowExpression in function.Window.PartitionBy
                                 .Concat(function.Window.OrderBy.Select(term => term.Expression))
                                 .Append(function.Window.Frame?.Start.Offset)
                                 .Append(function.Window.Frame?.End.Offset))
                    {
                        foreach (var nested in EnumerateExpressionQueries(windowExpression))
                            yield return nested;
                    }
                }
                yield break;
            case RowValueExpression rowValue:
                foreach (var value in rowValue.Values)
                {
                    foreach (var nested in EnumerateExpressionQueries(value))
                        yield return nested;
                }
                yield break;
            case CollationExpression collation:
                expression = collation.Expression;
                break;
            case CastExpression cast:
                expression = cast.Expression;
                break;
            case UnaryExpression unary:
                expression = unary.Operand;
                break;
            case CaseExpression @case:
                foreach (var child in new[] { @case.Operand, @case.Else }
                             .Concat(@case.Clauses.SelectMany(clause => new[] { clause.When, clause.Then })))
                {
                    foreach (var nested in EnumerateExpressionQueries(child))
                        yield return nested;
                }
                yield break;
            case LikeExpression like:
                foreach (var child in new[] { like.Value, like.Pattern, like.Escape })
                {
                    foreach (var nested in EnumerateExpressionQueries(child))
                        yield return nested;
                }
                yield break;
            case GlobExpression glob:
                foreach (var child in new[] { glob.Value, glob.Pattern })
                {
                    foreach (var nested in EnumerateExpressionQueries(child))
                        yield return nested;
                }
                yield break;
            case InExpression @in:
                foreach (var child in @in.Values.Prepend(@in.Value))
                {
                    foreach (var nested in EnumerateExpressionQueries(child))
                        yield return nested;
                }
                yield break;
            case BetweenExpression between:
                foreach (var child in new[] { between.Value, between.Lower, between.Upper })
                {
                    foreach (var nested in EnumerateExpressionQueries(child))
                        yield return nested;
                }
                yield break;
            case BinaryExpression binary:
                foreach (var child in new[] { binary.Left, binary.Right })
                {
                    foreach (var nested in EnumerateExpressionQueries(child))
                        yield return nested;
                }
                yield break;
            default:
                yield break;
        }

        foreach (var nested in EnumerateExpressionQueries(expression))
            yield return nested;
    }

    private static IEnumerable<ColumnExpression> EnumerateTriggerColumnExpressions(
        Expression? expression)
    {
        switch (expression)
        {
            case null:
                yield break;
            case ColumnExpression column:
                yield return column;
                yield break;
            case FunctionExpression function:
                foreach (var child in function.Arguments.Append(function.Filter))
                {
                    foreach (var column in EnumerateTriggerColumnExpressions(child))
                        yield return column;
                }
                if (function.Window is not null)
                {
                    foreach (var child in function.Window.PartitionBy
                                 .Concat(function.Window.OrderBy.Select(term => term.Expression))
                                 .Append(function.Window.Frame?.Start.Offset)
                                 .Append(function.Window.Frame?.End.Offset))
                    {
                        foreach (var column in EnumerateTriggerColumnExpressions(child))
                            yield return column;
                    }
                }
                yield break;
            case RowValueExpression rowValue:
                foreach (var value in rowValue.Values)
                {
                    foreach (var column in EnumerateTriggerColumnExpressions(value))
                        yield return column;
                }
                yield break;
            case CollationExpression collation:
                expression = collation.Expression;
                break;
            case CastExpression cast:
                expression = cast.Expression;
                break;
            case UnaryExpression unary:
                expression = unary.Operand;
                break;
            case CaseExpression @case:
                foreach (var child in new[] { @case.Operand, @case.Else }
                             .Concat(@case.Clauses.SelectMany(clause => new[] { clause.When, clause.Then })))
                {
                    foreach (var column in EnumerateTriggerColumnExpressions(child))
                        yield return column;
                }
                yield break;
            case LikeExpression like:
                foreach (var child in new[] { like.Value, like.Pattern, like.Escape })
                {
                    foreach (var column in EnumerateTriggerColumnExpressions(child))
                        yield return column;
                }
                yield break;
            case GlobExpression glob:
                foreach (var child in new[] { glob.Value, glob.Pattern })
                {
                    foreach (var column in EnumerateTriggerColumnExpressions(child))
                        yield return column;
                }
                yield break;
            case InExpression @in:
                foreach (var child in @in.Values.Prepend(@in.Value))
                {
                    foreach (var column in EnumerateTriggerColumnExpressions(child))
                        yield return column;
                }
                yield break;
            case InSubqueryExpression @in:
                expression = @in.Value;
                break;
            case BetweenExpression between:
                foreach (var child in new[] { between.Value, between.Lower, between.Upper })
                {
                    foreach (var column in EnumerateTriggerColumnExpressions(child))
                        yield return column;
                }
                yield break;
            case BinaryExpression binary:
                foreach (var child in new[] { binary.Left, binary.Right })
                {
                    foreach (var column in EnumerateTriggerColumnExpressions(child))
                        yield return column;
                }
                yield break;
            default:
                yield break;
        }

        foreach (var column in EnumerateTriggerColumnExpressions(expression))
            yield return column;
    }

    private static bool TriggerContainsAbortCapableExpression(TriggerDefinition trigger)
        => TriggerExpressionCanAbort(trigger.When)
            || trigger.Body.Any(TriggerStatementExpressionCanAbort);

    private static bool TriggerStatementExpressionCanAbort(ParsedStatement statement)
        => statement switch
        {
            InsertStatement insert => insert.Rows.SelectMany(row => row).Any(TriggerExpressionCanAbort)
                || TriggerQueryCanAbort(insert.Source)
                || insert.Returning?.Any(projection =>
                    TriggerExpressionCanAbort(projection.Expression)) == true
                || insert.Upsert?.Clauses().Any(upsert =>
                    upsert.Target.Any(target => TriggerExpressionCanAbort(target.Expression))
                    || TriggerExpressionCanAbort(upsert.TargetWhere)
                    || upsert.Action is DoUpdateUpsertAction update
                        && (update.Assignments.Any(assignment =>
                                TriggerExpressionCanAbort(assignment.Value))
                            || TriggerExpressionCanAbort(update.Where))) == true,
            UpdateStatement update => update.Assignments.Any(assignment =>
                    TriggerExpressionCanAbort(assignment.Value))
                || TriggerExpressionCanAbort(update.Where)
                || update.Returning?.Any(projection =>
                    TriggerExpressionCanAbort(projection.Expression)) == true
                || update.EffectiveOrderBy.Any(term =>
                    TriggerExpressionCanAbort(term.Expression))
                || TriggerExpressionCanAbort(update.Limit)
                || TriggerExpressionCanAbort(update.Offset),
            DeleteStatement delete => TriggerExpressionCanAbort(delete.Where)
                || delete.Returning?.Any(projection =>
                    TriggerExpressionCanAbort(projection.Expression)) == true
                || delete.EffectiveOrderBy.Any(term =>
                    TriggerExpressionCanAbort(term.Expression))
                || TriggerExpressionCanAbort(delete.Limit)
                || TriggerExpressionCanAbort(delete.Offset),
            QueryStatement query => TriggerQueryCanAbort(query),
            _ => false,
        };

    private static bool TriggerQueryCanAbort(QueryStatement? query)
        => query switch
        {
            null => false,
            SelectStatement select => select.Projections.Any(projection =>
                    TriggerExpressionCanAbort(projection.Expression))
                || TriggerSourceCanAbort(select.Source)
                || TriggerExpressionCanAbort(select.Where)
                || select.GroupBy.Any(TriggerExpressionCanAbort)
                || TriggerExpressionCanAbort(select.Having)
                || select.NamedWindows.Any(window =>
                    TriggerWindowCanAbort(window.Specification))
                || select.OrderBy.Any(term =>
                    TriggerExpressionCanAbort(term.Expression))
                || TriggerExpressionCanAbort(select.Limit)
                || TriggerExpressionCanAbort(select.Offset),
            ValuesClause values => values.Rows
                .SelectMany(row => row)
                .Any(TriggerExpressionCanAbort),
            CompoundSelectStatement compound => compound.Terms.Any(TriggerQueryCanAbort)
                || compound.OrderBy.Any(term =>
                    TriggerExpressionCanAbort(term.Expression))
                || TriggerExpressionCanAbort(compound.Limit)
                || TriggerExpressionCanAbort(compound.Offset),
            WithSelectStatement with => with.CommonTableExpressions.Any(common =>
                    TriggerQueryCanAbort(common.Query))
                || TriggerQueryCanAbort(with.Query),
            _ => false,
        };

    private static bool TriggerSourceCanAbort(TableSource? source)
        => source switch
        {
            null or NamedTableSource => false,
            // A table-valued function can raise (malformed JSON, a zero series step), so a
            // trigger body that calls one is conservatively treated as abort-capable.
            TableValuedFunctionSource => true,
            DerivedTableSource derived => TriggerQueryCanAbort(derived.Query),
            JoinTableSource join => TriggerSourceCanAbort(join.Left)
                || TriggerSourceCanAbort(join.Right)
                || TriggerExpressionCanAbort(join.Condition),
            _ => false,
        };

    private static bool TriggerWindowCanAbort(WindowSpecification window)
        => window.PartitionBy.Any(TriggerExpressionCanAbort)
            || window.OrderBy.Any(term =>
                TriggerExpressionCanAbort(term.Expression))
            || TriggerExpressionCanAbort(window.Frame?.Start.Offset)
            || TriggerExpressionCanAbort(window.Frame?.End.Offset);

    private static bool TriggerExpressionCanAbort(Expression? expression)
        => expression switch
        {
            null or LiteralExpression or ParameterExpression
                or ColumnExpression or StarExpression or QualifiedStarExpression => false,
            CurrentTimeExpression => true,
            RaiseExpression { Action: RaiseAction.Abort } => true,
            RaiseExpression => false,
            FunctionExpression function when function.Name.Equals(
                    "COALESCE",
                    StringComparison.OrdinalIgnoreCase)
                || function.Name.Equals("IFNULL", StringComparison.OrdinalIgnoreCase)
                => function.Arguments.Any(TriggerExpressionCanAbort),
            FunctionExpression => true,
            ScalarSubqueryExpression scalar => TriggerQueryCanAbort(scalar.Query),
            ExistsExpression exists => TriggerQueryCanAbort(exists.Query),
            InSubqueryExpression @in => TriggerExpressionCanAbort(@in.Value)
                || TriggerQueryCanAbort(@in.Query),
            RowValueExpression rowValue => rowValue.Values.Any(TriggerExpressionCanAbort),
            CollationExpression collation => !collation.Name.Equals(
                    "BINARY",
                    StringComparison.OrdinalIgnoreCase)
                && !collation.Name.Equals("NOCASE", StringComparison.OrdinalIgnoreCase)
                && !collation.Name.Equals("RTRIM", StringComparison.OrdinalIgnoreCase)
                || TriggerExpressionCanAbort(collation.Expression),
            CastExpression cast => TriggerExpressionCanAbort(cast.Expression),
            CaseExpression @case => TriggerExpressionCanAbort(@case.Operand)
                || @case.Clauses.Any(clause =>
                    TriggerExpressionCanAbort(clause.When)
                    || TriggerExpressionCanAbort(clause.Then))
                || TriggerExpressionCanAbort(@case.Else),
            LikeExpression => true,
            GlobExpression glob => TriggerExpressionCanAbort(glob.Value)
                || TriggerExpressionCanAbort(glob.Pattern),
            InExpression @in => TriggerExpressionCanAbort(@in.Value)
                || @in.Values.Any(TriggerExpressionCanAbort),
            BetweenExpression between => TriggerExpressionCanAbort(between.Value)
                || TriggerExpressionCanAbort(between.Lower)
                || TriggerExpressionCanAbort(between.Upper),
            UnaryExpression unary => TriggerExpressionCanAbort(unary.Operand),
            BinaryExpression binary => binary.Operator is BinaryOperator.JsonArrow
                    or BinaryOperator.JsonArrowText
                || TriggerExpressionCanAbort(binary.Left)
                || TriggerExpressionCanAbort(binary.Right),
            _ => false,
        };

    private bool TriggerGraphRequiresStatementRollback(
        QueryContext context,
        IReadOnlyList<TriggerDefinition> roots)
    {
        var visited = new HashSet<(string Name, InsertConflictAlgorithm? ConflictAlgorithm)>();
        return roots.Any(root => Visit(root, context));

        bool Visit(TriggerDefinition trigger, QueryContext triggerContext)
        {
            var key = (
                trigger.Name.ToUpperInvariant(),
                triggerContext.ConflictAlgorithmOverride);
            if (!visited.Add(key))
                return false;
            if (TriggerContainsAbortCapableExpression(trigger))
                return true;
            foreach (var statement in trigger.Body)
            {
                var statementContext = TriggerStatementContext(triggerContext, statement);
                if (TriggerStatementRequiresStatementRollback(statementContext, statement))
                    return true;
                if (GetBodyStatementTriggers(statementContext, statement).Any(nested =>
                        Visit(nested, statementContext)))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private static QueryContext TriggerStatementContext(
        QueryContext context,
        ParsedStatement statement)
        => statement is InsertStatement insert
            ? context with
            {
                ConflictAlgorithmOverride =
                    context.ConflictAlgorithmOverride ?? insert.ConflictAlgorithm,
            }
            : context;

    private bool TriggerStatementRequiresStatementRollback(
        QueryContext context,
        ParsedStatement statement)
    {
        switch (statement)
        {
            case InsertStatement insert when context.Tables.TryGetValue(insert.TableName, out var insertTable):
                var insertRequiresRollback = TriggerMutationRequiresStatementRollback(
                    context,
                    insertTable,
                    TriggerMutationKind.Insert)
                    || TriggerInsertUsesAbortCapableDefault(insert, insertTable)
                    || TriggerQueryReferencesAbortCapableView(
                        context,
                        insert.Source,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                var updateContext = context with
                {
                    ConflictAlgorithmOverride = InsertConflictAlgorithm.Abort,
                };
                return insertRequiresRollback || (insert.Upsert?.Clauses().Any(upsert =>
                    upsert.Action is DoUpdateUpsertAction upsertUpdate
                    && TriggerMutationRequiresStatementRollback(
                        updateContext,
                        insertTable,
                        TriggerMutationKind.Update,
                        PrepareUpdate(
                            new UpdateStatement(insert.TableName, upsertUpdate.Assignments, Where: null),
                            insertTable,
                            updateContext))) ?? false);
            case UpdateStatement update when context.Tables.TryGetValue(update.TableName, out var updateTable):
                return TriggerMutationRequiresStatementRollback(
                    context,
                    updateTable,
                    TriggerMutationKind.Update,
                    PrepareUpdate(update, updateTable, context));
            case DeleteStatement delete when context.Tables.TryGetValue(delete.TableName, out var deleteTable):
                return TriggerMutationRequiresStatementRollback(
                    context,
                    deleteTable,
                    TriggerMutationKind.Delete);
            case QueryStatement query:
                return TriggerQueryReferencesAbortCapableView(
                    context,
                    query,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            default:
                return false;
        }
    }

    private static bool TriggerInsertUsesAbortCapableDefault(
        InsertStatement insert,
        EmbeddedTable table)
    {
        if (insert.Columns is null)
            return false;
        var supplied = insert.Columns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return table.ColumnDefinitions.Any(column =>
            !column.IsGenerated
            && !supplied.Contains(column.Name)
            && TriggerExpressionCanAbort(column.DefaultExpression));
    }

    private static bool TriggerQueryReferencesAbortCapableView(
        QueryContext context,
        QueryStatement? query,
        HashSet<string> visited)
        => query switch
        {
            null or ValuesClause => false,
            SelectStatement select => TriggerSourceReferencesAbortCapableView(
                context,
                select.Source,
                visited),
            CompoundSelectStatement compound => compound.Terms.Any(term =>
                TriggerQueryReferencesAbortCapableView(context, term, visited)),
            WithSelectStatement with => with.CommonTableExpressions.Any(common =>
                    TriggerQueryReferencesAbortCapableView(context, common.Query, visited))
                || TriggerQueryReferencesAbortCapableView(context, with.Query, visited),
            _ => false,
        };

    private static bool TriggerSourceReferencesAbortCapableView(
        QueryContext context,
        TableSource? source,
        HashSet<string> visited)
        => source switch
        {
            null or TableValuedFunctionSource => false,
            NamedTableSource named when context.Views?.TryGetValue(named.Name, out var view) == true
                && visited.Add(named.Name)
                => TriggerQueryCanAbort(view.Query)
                    || TriggerQueryReferencesAbortCapableView(context, view.Query, visited),
            NamedTableSource => false,
            DerivedTableSource derived => TriggerQueryReferencesAbortCapableView(
                context,
                derived.Query,
                visited),
            JoinTableSource join => TriggerSourceReferencesAbortCapableView(
                    context,
                    join.Left,
                    visited)
                || TriggerSourceReferencesAbortCapableView(context, join.Right, visited),
            _ => false,
        };

    private void RejectUnboundedRecursiveTriggerCycles(
        QueryContext context,
        IReadOnlyList<TriggerDefinition> roots)
    {
        // Row-dependent WHEN clauses are the supported runtime termination guard.
        // Reject unguarded cycles conservatively before callbacks or mutations instead
        // of walking them to the runtime depth limit.
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
            Visit(root);

        void Visit(TriggerDefinition trigger)
        {
            if (visited.Contains(trigger.Name))
                return;
            if (!visiting.Add(trigger.Name))
                throw new EmbeddedSqlException("too many levels of trigger recursion");
            if (trigger.When is null
                || !HasRuntimeRecursionGuard(trigger.When, context))
            {
                foreach (var bodyStatement in trigger.Body)
                {
                    if (HasRuntimeBodyRecursionGuard(bodyStatement, context))
                        continue;
                    foreach (var nested in GetBodyStatementTriggers(context, bodyStatement))
                        Visit(nested);
                }
            }

            visiting.Remove(trigger.Name);
            visited.Add(trigger.Name);
        }
    }

    private bool HasRuntimeBodyRecursionGuard(
        ParsedStatement statement,
        QueryContext context)
    {
        if (statement is not InsertStatement insert)
            return false;
        if (insert.Source is SelectStatement { Where: { } predicate }
            && HasRuntimeRecursionGuard(predicate, context))
        {
            return true;
        }
        if (insert.Upsert?.Clauses().All(upsert => upsert.Action is DoNothingUpsertAction) == true)
            return true;
        var effectiveConflict = context.ConflictAlgorithmOverride ?? insert.ConflictAlgorithm;
        return effectiveConflict == InsertConflictAlgorithm.Ignore
            && context.Tables.TryGetValue(insert.TableName, out var table)
            && (table.PrimaryKeyColumns.Count != 0
                || table.TableUniqueConstraints.Count != 0
                || table.ColumnDefinitions.Any(column => column.PrimaryKey || column.Unique)
                || table.Indexes.Any(index => index.Unique));
    }

    private bool HasRuntimeRecursionGuard(
        Expression predicate,
        QueryContext context)
        => !IsStaticTriggerPredicate(predicate)
            || !IsTrue(Evaluate(predicate, EmptyParameters, row: null, context));

    private static bool IsStaticTriggerPredicate(Expression expression)
        => expression switch
        {
            LiteralExpression => true,
            UnaryExpression unary => IsStaticTriggerPredicate(unary.Operand),
            BinaryExpression binary => IsStaticTriggerPredicate(binary.Left)
                && IsStaticTriggerPredicate(binary.Right),
            CollationExpression collation when collation.Name.Equals(
                    "BINARY",
                    StringComparison.OrdinalIgnoreCase)
                || collation.Name.Equals("NOCASE", StringComparison.OrdinalIgnoreCase)
                || collation.Name.Equals("RTRIM", StringComparison.OrdinalIgnoreCase)
                => IsStaticTriggerPredicate(collation.Expression),
            CastExpression cast => IsStaticTriggerPredicate(cast.Expression),
            CaseExpression @case => (@case.Operand is null || IsStaticTriggerPredicate(@case.Operand))
                && @case.Clauses.All(clause =>
                    IsStaticTriggerPredicate(clause.When)
                    && IsStaticTriggerPredicate(clause.Then))
                && (@case.Else is null || IsStaticTriggerPredicate(@case.Else)),
            BetweenExpression between => IsStaticTriggerPredicate(between.Value)
                && IsStaticTriggerPredicate(between.Lower)
                && IsStaticTriggerPredicate(between.Upper),
            InExpression @in => IsStaticTriggerPredicate(@in.Value)
                && @in.Values.All(IsStaticTriggerPredicate),
            LikeExpression like => IsStaticTriggerPredicate(like.Value)
                && IsStaticTriggerPredicate(like.Pattern)
                && (like.Escape is null || IsStaticTriggerPredicate(like.Escape)),
            GlobExpression glob => IsStaticTriggerPredicate(glob.Value)
                && IsStaticTriggerPredicate(glob.Pattern),
            _ => false,
        };

    private IEnumerable<TriggerDefinition> GetBodyStatementTriggers(
        QueryContext context,
        ParsedStatement statement)
    {
        var direct = statement switch
        {
            InsertStatement insert => GetBodyInsertTriggers(context, insert),
            UpdateStatement update => GetBodyUpdateTriggers(context, update),
            DeleteStatement delete => GetRowTriggers(
                context,
                delete.TableName,
                TriggerTiming.Before,
                TriggerEvent.Delete)
                .Concat(GetRowTriggers(
                    context,
                    delete.TableName,
                    TriggerTiming.After,
                    TriggerEvent.Delete))
                .Concat(GetRowTriggers(
                    context,
                    delete.TableName,
                    TriggerTiming.InsteadOf,
                    TriggerEvent.Delete)),
            _ => [],
        };
        var mutation = GetDirectMutationEdge(statement);
        if (mutation is null)
            return direct;
        return direct.Concat(
            GetTransitiveForeignKeyActionMutationEdges(context, mutation)
                .SelectMany(action => GetMutationEdgeTriggers(context, action)));
    }

    private static TriggerMutationEdge? GetDirectMutationEdge(ParsedStatement statement)
        => statement switch
        {
            InsertStatement insert => new TriggerMutationEdge(insert.TableName, TriggerEvent.Insert),
            UpdateStatement update => new TriggerMutationEdge(
                update.TableName,
                TriggerEvent.Update,
                update.Assignments
                    .Select(assignment => assignment.Column)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)),
            DeleteStatement delete => new TriggerMutationEdge(delete.TableName, TriggerEvent.Delete),
            _ => null,
        };

    private IEnumerable<TriggerMutationEdge> GetForeignKeyActionMutationEdges(
        QueryContext context,
        TriggerMutationEdge parentMutation)
    {
        if (!context.ForeignKeysEnabled
            || parentMutation.Event == TriggerEvent.Insert
            || !context.Tables.TryGetValue(parentMutation.TableName, out var parentTable))
        {
            yield break;
        }

        HashSet<int>? assignedParentColumns = null;
        if (parentMutation.Event == TriggerEvent.Update)
        {
            assignedParentColumns = [];
            foreach (var column in parentMutation.UpdatedColumns ?? new HashSet<string>())
            {
                if (parentTable.TryGetColumnIndex(column, out var index))
                    assignedParentColumns.Add(index);
            }
        }

        foreach (var (childTableName, childTable) in context.Tables)
        {
            foreach (var foreignKey in childTable.ForeignKeys)
            {
                if (!string.Equals(
                        foreignKey.ParentTable,
                        parentMutation.TableName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parent = ResolveForeignKeyParent(context.Tables, childTableName, foreignKey);
                if (assignedParentColumns is not null
                    && !parent.ColumnIndices.Any(assignedParentColumns.Contains))
                {
                    continue;
                }

                var action = parentMutation.Event == TriggerEvent.Delete
                    ? foreignKey.OnDelete
                    : foreignKey.OnUpdate;
                switch (action)
                {
                    case ForeignKeyAction.Cascade when parentMutation.Event == TriggerEvent.Delete:
                        yield return new TriggerMutationEdge(childTableName, TriggerEvent.Delete);
                        break;
                    case ForeignKeyAction.Cascade:
                    case ForeignKeyAction.SetNull:
                    case ForeignKeyAction.SetDefault:
                        yield return new TriggerMutationEdge(
                            childTableName,
                            TriggerEvent.Update,
                            foreignKey.ChildColumns.ToHashSet(StringComparer.OrdinalIgnoreCase));
                        break;
                }
            }
        }
    }

    private IEnumerable<TriggerMutationEdge> GetTransitiveForeignKeyActionMutationEdges(
        QueryContext context,
        TriggerMutationEdge root)
    {
        var pending = new Queue<TriggerMutationEdge>(
            GetForeignKeyActionMutationEdges(context, root));
        var visited = new HashSet<(string TableName, TriggerEvent Event, string Columns)>();
        while (pending.Count > 0)
        {
            var mutation = pending.Dequeue();
            var key = (
                mutation.TableName.ToUpperInvariant(),
                mutation.Event,
                GetMutationColumnKey(mutation.UpdatedColumns));
            if (!visited.Add(key))
                continue;
            yield return mutation;
            foreach (var nested in GetForeignKeyActionMutationEdges(context, mutation))
                pending.Enqueue(nested);
        }
    }

    private static string GetMutationColumnKey(IReadOnlySet<string>? columns)
        => columns is null
            ? string.Empty
            : string.Join(
                "\u001f",
                columns
                    .Select(column => column.ToUpperInvariant())
                    .OrderBy(column => column, StringComparer.Ordinal));

    private static IEnumerable<TriggerDefinition> GetMutationEdgeTriggers(
        QueryContext context,
        TriggerMutationEdge mutation)
        => GetRowTriggers(
            context,
            mutation.TableName,
            TriggerTiming.Before,
            mutation.Event,
            mutation.UpdatedColumns)
            .Concat(GetRowTriggers(
                context,
                mutation.TableName,
                TriggerTiming.After,
                mutation.Event,
                mutation.UpdatedColumns))
            .Concat(GetRowTriggers(
                context,
                mutation.TableName,
                TriggerTiming.InsteadOf,
                mutation.Event,
                mutation.UpdatedColumns));

    private static IEnumerable<TriggerDefinition> GetBodyInsertTriggers(
        QueryContext context,
        InsertStatement insert)
    {
        IEnumerable<TriggerDefinition> triggers = GetRowTriggers(
            context,
            insert.TableName,
            TriggerTiming.Before,
            TriggerEvent.Insert)
            .Concat(GetRowTriggers(
                context,
                insert.TableName,
                TriggerTiming.After,
                TriggerEvent.Insert))
            .Concat(GetRowTriggers(
                context,
                insert.TableName,
                TriggerTiming.InsteadOf,
                TriggerEvent.Insert));
        foreach (var upsert in insert.Upsert?.Clauses() ?? [])
        {
            if (upsert.Action is DoUpdateUpsertAction update)
            {
                triggers = triggers.Concat(GetBodyUpdateTriggers(
                    context,
                    new UpdateStatement(insert.TableName, update.Assignments, update.Where)));
            }
        }

        var mayReplace = !context.InheritedTriggerConflict
            && insert.ConflictAlgorithm == InsertConflictAlgorithm.Replace
            || context.Tables.TryGetValue(insert.TableName, out var table)
                && table.HasNonDefaultConflictAlgorithms;
        if (context.RecursiveTriggersEnabled && mayReplace)
        {
            triggers = triggers
                .Concat(GetRowTriggers(
                    context,
                    insert.TableName,
                    TriggerTiming.Before,
                    TriggerEvent.Delete))
                .Concat(GetRowTriggers(
                    context,
                    insert.TableName,
                    TriggerTiming.After,
                    TriggerEvent.Delete));
        }

        return triggers;
    }

    private static IEnumerable<TriggerDefinition> GetBodyUpdateTriggers(
        QueryContext context,
        UpdateStatement update)
    {
        var columns = update.Assignments
            .Select(assignment => assignment.Column)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return GetRowTriggers(
            context,
            update.TableName,
            TriggerTiming.Before,
            TriggerEvent.Update,
            columns)
            .Concat(GetRowTriggers(
                context,
                update.TableName,
                TriggerTiming.After,
                TriggerEvent.Update,
                columns))
            .Concat(GetRowTriggers(
                context,
                update.TableName,
                TriggerTiming.InsteadOf,
                TriggerEvent.Update,
                columns));
    }

    private void ValidateTriggerStatement(
        ParsedStatement statement,
        TriggerEvent triggerEvent,
        string[] columns,
        bool hasRowid)
    {
        switch (statement)
        {
            case InsertStatement insert:
                foreach (var row in insert.Rows)
                {
                    foreach (var expression in row)
                        ValidateTriggerExpression(expression, triggerEvent, columns, hasRowid);
                }
                ValidateTriggerQuery(insert.Source, triggerEvent, columns, hasRowid);
                foreach (var upsert in insert.Upsert?.Clauses() ?? [])
                {
                    foreach (var target in upsert.Target)
                        ValidateTriggerExpression(target.Expression, triggerEvent, columns, hasRowid);
                    ValidateTriggerExpression(upsert.TargetWhere, triggerEvent, columns, hasRowid);
                    if (upsert.Action is DoUpdateUpsertAction upsertUpdate)
                    {
                        foreach (var assignment in upsertUpdate.Assignments)
                            ValidateTriggerExpression(assignment.Value, triggerEvent, columns, hasRowid);
                        ValidateTriggerExpression(upsertUpdate.Where, triggerEvent, columns, hasRowid);
                    }
                }
                ValidateTriggerProjections(insert.Returning, triggerEvent, columns, hasRowid);
                break;
            case UpdateStatement update:
                foreach (var assignment in update.Assignments)
                    ValidateTriggerExpression(assignment.Value, triggerEvent, columns, hasRowid);
                ValidateTriggerExpression(update.Where, triggerEvent, columns, hasRowid);
                ValidateTriggerProjections(update.Returning, triggerEvent, columns, hasRowid);
                foreach (var term in update.EffectiveOrderBy)
                    ValidateTriggerExpression(term.Expression, triggerEvent, columns, hasRowid);
                ValidateTriggerExpression(update.Limit, triggerEvent, columns, hasRowid);
                ValidateTriggerExpression(update.Offset, triggerEvent, columns, hasRowid);
                break;
            case DeleteStatement delete:
                ValidateTriggerExpression(delete.Where, triggerEvent, columns, hasRowid);
                ValidateTriggerProjections(delete.Returning, triggerEvent, columns, hasRowid);
                foreach (var term in delete.EffectiveOrderBy)
                    ValidateTriggerExpression(term.Expression, triggerEvent, columns, hasRowid);
                ValidateTriggerExpression(delete.Limit, triggerEvent, columns, hasRowid);
                ValidateTriggerExpression(delete.Offset, triggerEvent, columns, hasRowid);
                break;
            case QueryStatement query:
                ValidateTriggerQuery(query, triggerEvent, columns, hasRowid);
                break;
        }
    }

    private void ValidateTriggerQuery(
        QueryStatement? query,
        TriggerEvent triggerEvent,
        string[] columns,
        bool hasRowid)
    {
        switch (query)
        {
            case null:
                return;
            case SelectStatement select:
                ValidateTriggerProjections(select.Projections, triggerEvent, columns, hasRowid);
                ValidateTriggerSource(select.Source, triggerEvent, columns, hasRowid);
                ValidateTriggerExpression(select.Where, triggerEvent, columns, hasRowid);
                foreach (var expression in select.GroupBy)
                    ValidateTriggerExpression(expression, triggerEvent, columns, hasRowid);
                ValidateTriggerExpression(select.Having, triggerEvent, columns, hasRowid);
                foreach (var window in select.NamedWindows)
                {
                    ValidateTriggerWindow(
                        window.Specification,
                        triggerEvent,
                        columns,
                        hasRowid);
                }
                foreach (var term in select.OrderBy)
                    ValidateTriggerExpression(term.Expression, triggerEvent, columns, hasRowid);
                ValidateTriggerExpression(select.Limit, triggerEvent, columns, hasRowid);
                ValidateTriggerExpression(select.Offset, triggerEvent, columns, hasRowid);
                return;
            case ValuesClause values:
                foreach (var row in values.Rows)
                {
                    foreach (var expression in row)
                        ValidateTriggerExpression(expression, triggerEvent, columns, hasRowid);
                }
                return;
            case CompoundSelectStatement compound:
                foreach (var term in compound.Terms)
                    ValidateTriggerQuery(term, triggerEvent, columns, hasRowid);
                foreach (var orderBy in compound.OrderBy)
                    ValidateTriggerExpression(orderBy.Expression, triggerEvent, columns, hasRowid);
                ValidateTriggerExpression(compound.Limit, triggerEvent, columns, hasRowid);
                ValidateTriggerExpression(compound.Offset, triggerEvent, columns, hasRowid);
                return;
            case WithSelectStatement with:
                foreach (var commonTableExpression in with.CommonTableExpressions)
                    ValidateTriggerQuery(commonTableExpression.Query, triggerEvent, columns, hasRowid);
                ValidateTriggerQuery(with.Query, triggerEvent, columns, hasRowid);
                return;
        }
    }

    private void ValidateTriggerWindow(
        WindowSpecification window,
        TriggerEvent triggerEvent,
        string[] columns,
        bool hasRowid)
    {
        foreach (var expression in window.PartitionBy)
            ValidateTriggerExpression(expression, triggerEvent, columns, hasRowid);
        foreach (var term in window.OrderBy)
            ValidateTriggerExpression(term.Expression, triggerEvent, columns, hasRowid);
        ValidateTriggerExpression(window.Frame?.Start.Offset, triggerEvent, columns, hasRowid);
        ValidateTriggerExpression(window.Frame?.End.Offset, triggerEvent, columns, hasRowid);
    }

    private void ValidateTriggerSource(
        TableSource? source,
        TriggerEvent triggerEvent,
        string[] columns,
        bool hasRowid)
    {
        switch (source)
        {
            case null:
            case NamedTableSource:
                return;
            case TableValuedFunctionSource function:
                foreach (var argument in function.Arguments)
                    ValidateTriggerExpression(argument, triggerEvent, columns, hasRowid);
                return;
            case DerivedTableSource derived:
                ValidateTriggerQuery(derived.Query, triggerEvent, columns, hasRowid);
                return;
            case JoinTableSource join:
                ValidateTriggerSource(join.Left, triggerEvent, columns, hasRowid);
                ValidateTriggerSource(join.Right, triggerEvent, columns, hasRowid);
                ValidateTriggerExpression(join.Condition, triggerEvent, columns, hasRowid);
                return;
        }
    }

    private void ValidateTriggerProjections(
        IEnumerable<Projection>? projections,
        TriggerEvent triggerEvent,
        string[] columns,
        bool hasRowid)
    {
        if (projections is null)
            return;
        foreach (var projection in projections)
            ValidateTriggerExpression(projection.Expression, triggerEvent, columns, hasRowid);
    }

    private void ValidateTriggerExpression(
        Expression? expression,
        TriggerEvent triggerEvent,
        string[] columns,
        bool hasRowid)
    {
        switch (expression)
        {
            case null:
            case LiteralExpression:
            case CurrentTimeExpression:
            case ParameterExpression:
            case RaiseExpression:
            case StarExpression:
            case QualifiedStarExpression:
                return;
            case ColumnExpression column when TriggerRowFrame.IsTriggerQualifier(column.Qualifier):
                var isOld = string.Equals(column.Qualifier, "OLD", StringComparison.OrdinalIgnoreCase);
                if (isOld && triggerEvent == TriggerEvent.Insert
                    || !isOld && triggerEvent == TriggerEvent.Delete)
                {
                    throw new EmbeddedSqlException($"no such column: {column.Name}");
                }

                var name = column.UnqualifiedName ?? column.Name;
                if (columns.Any(candidate =>
                        string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }
                if (hasRowid && EmbeddedTable.IsRowidAliasName(name))
                    return;
                throw new EmbeddedSqlException($"no such column: {column.Name}");
            case ColumnExpression:
                return;
            case FunctionExpression function:
                foreach (var argument in function.Arguments)
                    ValidateTriggerExpression(argument, triggerEvent, columns, hasRowid);
                ValidateTriggerExpression(function.Filter, triggerEvent, columns, hasRowid);
                if (function.Window is not null)
                {
                    foreach (var partition in function.Window.PartitionBy)
                        ValidateTriggerExpression(partition, triggerEvent, columns, hasRowid);
                    foreach (var orderBy in function.Window.OrderBy)
                        ValidateTriggerExpression(orderBy.Expression, triggerEvent, columns, hasRowid);
                    ValidateTriggerExpression(function.Window.Frame?.Start.Offset, triggerEvent, columns, hasRowid);
                    ValidateTriggerExpression(function.Window.Frame?.End.Offset, triggerEvent, columns, hasRowid);
                }
                return;
            case RowValueExpression rowValue:
                foreach (var value in rowValue.Values)
                    ValidateTriggerExpression(value, triggerEvent, columns, hasRowid);
                return;
            case ScalarSubqueryExpression subquery:
                ValidateTriggerQuery(subquery.Query, triggerEvent, columns, hasRowid);
                return;
            case ExistsExpression exists:
                ValidateTriggerQuery(exists.Query, triggerEvent, columns, hasRowid);
                return;
            case CollationExpression collation:
                ValidateTriggerExpression(collation.Expression, triggerEvent, columns, hasRowid);
                return;
            case CastExpression cast:
                ValidateTriggerExpression(cast.Expression, triggerEvent, columns, hasRowid);
                return;
            case CaseExpression @case:
                ValidateTriggerExpression(@case.Operand, triggerEvent, columns, hasRowid);
                foreach (var clause in @case.Clauses)
                {
                    ValidateTriggerExpression(clause.When, triggerEvent, columns, hasRowid);
                    ValidateTriggerExpression(clause.Then, triggerEvent, columns, hasRowid);
                }
                ValidateTriggerExpression(@case.Else, triggerEvent, columns, hasRowid);
                return;
            case LikeExpression like:
                ValidateTriggerExpression(like.Value, triggerEvent, columns, hasRowid);
                ValidateTriggerExpression(like.Pattern, triggerEvent, columns, hasRowid);
                ValidateTriggerExpression(like.Escape, triggerEvent, columns, hasRowid);
                return;
            case GlobExpression glob:
                ValidateTriggerExpression(glob.Value, triggerEvent, columns, hasRowid);
                ValidateTriggerExpression(glob.Pattern, triggerEvent, columns, hasRowid);
                return;
            case InExpression @in:
                ValidateTriggerExpression(@in.Value, triggerEvent, columns, hasRowid);
                foreach (var value in @in.Values)
                    ValidateTriggerExpression(value, triggerEvent, columns, hasRowid);
                return;
            case InSubqueryExpression @in:
                ValidateTriggerExpression(@in.Value, triggerEvent, columns, hasRowid);
                ValidateTriggerQuery(@in.Query, triggerEvent, columns, hasRowid);
                return;
            case BetweenExpression between:
                ValidateTriggerExpression(between.Value, triggerEvent, columns, hasRowid);
                ValidateTriggerExpression(between.Lower, triggerEvent, columns, hasRowid);
                ValidateTriggerExpression(between.Upper, triggerEvent, columns, hasRowid);
                return;
            case UnaryExpression unary:
                ValidateTriggerExpression(unary.Operand, triggerEvent, columns, hasRowid);
                return;
            case BinaryExpression binary:
                ValidateTriggerExpression(binary.Left, triggerEvent, columns, hasRowid);
                ValidateTriggerExpression(binary.Right, triggerEvent, columns, hasRowid);
                return;
        }
    }
}
