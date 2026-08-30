using Ahtola.Core.Compilation;
using Ahtola.Core.Execution;
using Ahtola.Core.Parsing;

namespace Ahtola.Core;

public sealed partial class EmbeddedDatabase
{
    private bool TryCompileVirtualTableSelect(
        SelectStatement select,
        SqlValue[] parameters,
        QueryContext context,
        SourceRow? outerRow,
        out CompiledSelect compiled)
    {
        compiled = null!;
        // Trigger pseudo-columns live in TriggerRow rather than the ordinary outer-row chain.
        // Keep trigger-body virtual scans on the evaluator until the compiled binding model can
        // carry NEW/OLD values into virtual-table filter arguments.
        if (context.InsideTrigger)
            return false;

        ManagedVirtualTable table;
        ManagedVirtualTableSchema schema;
        NamedTableSource planningSource;
        if (select.Source is NamedTableSource source
            && source.IndexDirective is null
            && TryGetVirtualTable(context, source, out var definition))
        {
            table = definition.Table;
            schema = table.Schema;
            planningSource = source;
        }
        else if (select.Source is TableValuedFunctionSource function)
        {
            var module = TableValuedFunctionRegistry.Resolve(function.Name);
            table = new TableValuedFunctionVirtualTable(module, function.Schema, context);
            schema = table.Schema;
            planningSource = new NamedTableSource(function.Name, function.Alias);
        }
        else
        {
            return false;
        }

        if (select.Projections.Count == 0
            || select.Distinct
            || select.GroupBy.Count != 0
            || select.Having is not null
            || select.Projections.Any(projection =>
                ContainsAggregate(projection.Expression)
                || ContainsWindowFunction(projection.Expression)
                || ContainsManagedFtsCursorFunction(projection.Expression))
            || select.Where is not null && ContainsManagedFtsCursorFunction(select.Where)
            || select.OrderBy.Any(term => ContainsManagedFtsCursorFunction(term.Expression)))
        {
            return false;
        }

        var resolvedOrderBy = ResolveOrderBy(select.OrderBy, select.Projections);
        var plannerInput = ExtractVirtualTablePlannerInput(
            planningSource,
            schema,
            select.Where,
            resolvedOrderBy,
            select.Limit,
            select.Offset,
            outerRow);
        if (select.Source is TableValuedFunctionSource functionSource)
        {
            var constraints = plannerInput.Constraints.ToList();
            var expressions = plannerInput.Expressions.ToList();
            var predicates = plannerInput.Predicates.ToList();
            var visibleCount = TableValuedFunctionRegistry.Resolve(functionSource.Name).Schema.VisibleColumns.Count;
            for (var index = 0; index < functionSource.Arguments.Count; index++)
            {
                var expression = functionSource.Arguments[index];
                if (!IsSafeVirtualTableArgument(expression))
                    return false;
                constraints.Add(new ManagedVirtualTableConstraint(
                    visibleCount + index,
                    ManagedVirtualTableConstraintOperator.Equal,
                    IsVirtualTableArgumentUsable(expression, outerRow)));
                expressions.Add(expression);
                predicates.Add(expression);
            }

            plannerInput = plannerInput with
            {
                Constraints = constraints,
                Expressions = expressions,
                Predicates = predicates,
            };
        }

        var plan = table.BestIndex(plannerInput.Constraints, plannerInput.OrderBy);
        plan.ValidateFor(plannerInput.Constraints);
        var consumesCompleteOrder = resolvedOrderBy.Count == 0
            || plan.OrderByConsumed && plannerInput.OrderBy.Count == resolvedOrderBy.Count;
        if (!consumesCompleteOrder)
            return false;

        var arguments = BuildVirtualTableFilterArguments(
            plan,
            plannerInput.Expressions,
            parameters,
            outerRow,
            context);
        var omittedPredicates = plannerInput.Predicates
            .Take(plannerInput.PredicateCount)
            .Where((_, index) => plan.ConstraintUsages[index].Omit)
            .ToArray();
        var outputColumns = GetOutputColumns(select.Source, context);
        var rawOutputColumns = GetRawOutputColumns(select.Source, context);
        var resultColumnCount = GetColumnNames(select.Projections, outputColumns, rawOutputColumns).Length;
        if (resultColumnCount == 0)
            return false;

        var tableColumns = schema.Columns.Select(static column => column.Name).ToArray();
        var qualifier = select.Source switch
        {
            NamedTableSource named => named.Alias ?? named.Name,
            TableValuedFunctionSource function => function.Alias ?? function.Name,
            _ => throw new InvalidOperationException("Unexpected virtual source."),
        };
        var qualifiedColumns = BuildQualifiedColumns(qualifier, tableColumns);
        var sourceOutputColumns = BuildOutputColumns(
            qualifier,
            schema.VisibleColumns.Select(static column => column.Name).ToArray(),
            select.Source);
        var columnDefinitions = GetSourceColumnDefinitions(select.Source, context);
        var qualifiedColumnDefinitions = GetSourceQualifiedColumnDefinitions(select.Source, context);

        SourceRow MaterializeVirtualRow(SqlValue[] record)
        {
            var values = record[..tableColumns.Length];
            var rowId = record[tableColumns.Length].AsInteger();
            return new SourceRow(
                tableColumns,
                values,
                qualifiedColumns,
                outerRow,
                sourceOutputColumns,
                rowId,
                qualifier,
                new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase)
                {
                    [qualifier] = rowId,
                },
                ColumnDefinitions: columnDefinitions,
                QualifiedColumnDefinitions: qualifiedColumnDefinitions);
        }

        var argumentCount = arguments.Count;
        var recordStart = argumentCount;
        var recordWidth = tableColumns.Length + 1;
        var outputStart = recordStart + recordWidth;
        var cursor = new Cursor(0);
        var instructions = new List<VdbeInstruction>();
        for (var index = 0; index < argumentCount; index++)
            instructions.Add(new LoadConstantInstruction(new Register(index), arguments[index]));
        instructions.Add(new VOpenInstruction(cursor));
        var filterIndex = instructions.Count;
        instructions.Add(new VFilterInstruction(
            cursor,
            plan,
            new RegisterRange(new Register(0), argumentCount),
            default));
        var loopStart = instructions.Count;
        for (var column = 0; column < tableColumns.Length; column++)
            instructions.Add(new VColumnInstruction(cursor, column, new Register(recordStart + column)));
        instructions.Add(new RowIdInstruction(cursor, new Register(recordStart + tableColumns.Length)));

        var residualIndex = -1;
        if (select.Where is not null && omittedPredicates.Length != EnumerateConjuncts(select.Where).Count())
        {
            residualIndex = instructions.Count;
            instructions.Add(new FilterRegistersInstruction(
                new RegisterRange(new Register(recordStart), recordWidth),
                record => IsVirtualTableResidualTrue(
                    select.Where,
                    omittedPredicates,
                    parameters,
                    MaterializeVirtualRow(record),
                    context),
                default,
                "evaluate virtual-table residual predicates"));
        }

        instructions.Add(new ProjectRegistersInstruction(
            new RegisterRange(new Register(recordStart), recordWidth),
            new RegisterRange(new Register(outputStart), resultColumnCount),
            record => EvaluateProjectionRow(
                select,
                MaterializeVirtualRow(record),
                parameters,
                context,
                outputColumns,
                rawOutputColumns),
            "project managed virtual-table row"));
        instructions.Add(new ResultRowInstruction(new RegisterRange(new Register(outputStart), resultColumnCount)));
        var nextIndex = instructions.Count;
        instructions.Add(new VNextInstruction(cursor, new ProgramCounter(loopStart)));
        var closeIndex = instructions.Count;
        instructions.Add(new CloseCursorInstruction(cursor));
        instructions.Add(new HaltInstruction());

        instructions[filterIndex] = new VFilterInstruction(
            cursor,
            plan,
            new RegisterRange(new Register(0), argumentCount),
            new ProgramCounter(closeIndex));
        if (residualIndex >= 0)
        {
            instructions[residualIndex] = ((FilterRegistersInstruction)instructions[residualIndex]) with
            {
                FalseTarget = new ProgramCounter(nextIndex),
            };
        }

        var program = new VdbeProgram(
            registerCount: outputStart + resultColumnCount,
            cursorCount: 1,
            instructions);
        if (select.Limit is not null || select.Offset is not null)
        {
            if (!TryResolveLimitOffset(select, parameters, context, outerRow, out var limit, out var offset))
                return false;
            program = LimitOffsetProgramBuilder.Apply(program, offset, limit);
        }

        compiled = new CompiledSelect(
            program,
            [new VdbeCursorSource([])],
            VirtualTableBindings: [new VdbeVirtualTableBinding(table)],
            StreamResults: true);
        return true;
    }

    private static bool ContainsManagedFtsCursorFunction(Expression expression)
        => IndexExpressionSemantics.ContainsFunction(
            expression,
            static (name, _) => name.Equals("bm25", StringComparison.OrdinalIgnoreCase)
                || name.Equals("highlight", StringComparison.OrdinalIgnoreCase)
                || name.Equals("snippet", StringComparison.OrdinalIgnoreCase));
}
