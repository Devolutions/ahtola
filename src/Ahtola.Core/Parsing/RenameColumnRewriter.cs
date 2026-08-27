using System.Text;

namespace Ahtola.Core.Parsing;

/// <summary>
/// Describes the schema visible to a rename rewrite. The delegates take names exactly as they
/// were written in the stored SQL (possibly schema qualified) so the rewriter never has to
/// know how the catalog normalizes identifiers.
/// </summary>
/// <param name="ResolveColumns">
/// Returns the column names of the table or view written as <c>name</c>, or <see langword="null"/>
/// when the object is unknown. An unknown source makes the rewriter conservative rather than wrong.
/// </param>
/// <param name="IsRenameTarget">
/// Reports whether the table written as <c>name</c> is the table whose column is being renamed.
/// </param>
internal sealed record RenameColumnSchema(
    Func<string, IReadOnlyList<string>?> ResolveColumns,
    Func<string, bool> IsRenameTarget);

/// <summary>
/// Raised when a stored schema object references the renamed column in a way SQLite refuses to
/// rewrite. The rename is rejected rather than left to corrupt the object.
/// </summary>
internal sealed class RenameColumnRewriteException(string message) : Exception(message);

/// <summary>
/// Rewrites references to a renamed column inside the stored SQL text of dependent schema
/// objects, the way SQLite's <c>alter.c</c> does: the statement is reparsed, each column
/// reference is resolved against real scope rules, and only the resolved identifier tokens are
/// edited. Nothing else in the text moves, so string literals, comments, and unrelated
/// identifiers that merely contain the old name survive untouched.
/// </summary>
internal static class RenameColumnRewriter
{
    /// <summary>
    /// Rewrites a full <c>CREATE VIEW</c> or <c>CREATE TRIGGER</c> statement.
    /// Returns <see langword="null"/> when the statement does not reference the renamed column.
    /// </summary>
    public static string? RewriteSchemaObject(
        string sql,
        string oldName,
        string newName,
        bool quoteNewName,
        RenameColumnSchema schema)
    {
        ParsedStatement statement;
        SqlSourceSpans spans;
        try
        {
            statement = SqlParser.ParseWithSpans(sql, out spans);
        }
        catch (EmbeddedSqlException)
        {
            // The object could not be reparsed, so its references cannot be resolved safely.
            // Leave the text alone; schema validation decides whether the rename is legal.
            return null;
        }

        var walker = new Walker(spans, oldName, newName, quoteNewName, schema);
        switch (statement)
        {
            case CreateViewStatement view:
                walker.WalkQuery(view.Query, null);
                break;
            case CreateTriggerStatement trigger:
                walker.WalkTrigger(trigger);
                break;
            default:
                return null;
        }

        return walker.Apply(sql);
    }

    /// <summary>
    /// Rewrites a bare expression fragment (a CHECK body, a generated-column expression, an
    /// index key, or a partial-index predicate) that is evaluated against a single table's row.
    /// Returns <see langword="null"/> when the fragment does not reference the renamed column.
    /// </summary>
    public static string? RewriteTableExpression(
        string sql,
        string tableName,
        IReadOnlyList<string> columns,
        string oldName,
        string newName,
        bool quoteNewName)
    {
        Expression expression;
        SqlSourceSpans spans;
        try
        {
            expression = SqlParser.ParseExpressionWithSpans(sql, out spans);
        }
        catch (EmbeddedSqlException)
        {
            return null;
        }

        var schema = new RenameColumnSchema(
            name => string.Equals(name, tableName, StringComparison.OrdinalIgnoreCase) ? columns : null,
            name => string.Equals(name, tableName, StringComparison.OrdinalIgnoreCase));
        var walker = new Walker(spans, oldName, newName, quoteNewName, schema);
        var scope = new Scope(null);
        scope.Bindings.Add(new Binding(tableName, columns, IsTarget: true, QualifiedOnly: false));
        walker.WalkExpression(expression, scope);
        return walker.Apply(sql);
    }

    /// <summary>
    /// Rewrites the stored <c>CREATE TABLE</c> text of the renamed column's own table: the
    /// column definition name, table-level PRIMARY KEY/UNIQUE lists, CHECK and generated-column
    /// expressions, and any self-referencing FOREIGN KEY parent column lists. Returns
    /// <see langword="null"/> when the text cannot be reparsed (forcing the caller back onto
    /// schema regeneration); the column definition name always requires an edit, so a parsed
    /// table always produces rewritten text.
    /// </summary>
    public static string? RewriteCreateTable(
        string sql,
        string tableName,
        IReadOnlyList<string> columns,
        string oldName,
        string newName,
        bool quoteNewName)
        => RewriteCreateTable(
            sql,
            tableName,
            columns,
            new RenameColumnSchema(
                name => string.Equals(name, tableName, StringComparison.OrdinalIgnoreCase) ? columns : null,
                name => string.Equals(name, tableName, StringComparison.OrdinalIgnoreCase)),
            oldName,
            newName,
            quoteNewName);

    /// <summary>
    /// Same as <see cref="RewriteCreateTable(string, string, IReadOnlyList{string}, string, string, bool)"/>
    /// but with an explicit rename schema, used when the edited table is not the rename target
    /// itself (a child table whose FOREIGN KEY names the renamed parent column).
    /// </summary>
    public static string? RewriteCreateTable(
        string sql,
        string tableName,
        IReadOnlyList<string> columns,
        RenameColumnSchema schema,
        string oldName,
        string newName,
        bool quoteNewName)
    {
        ParsedStatement parsed;
        SqlSourceSpans spans;
        try
        {
            parsed = SqlParser.ParseWithSpans(sql, out spans);
        }
        catch (EmbeddedSqlException)
        {
            return null;
        }

        if (parsed is not CreateTableStatement statement)
            return null;

        var walker = new Walker(spans, oldName, newName, quoteNewName, schema);
        walker.WalkCreateTable(statement, tableName, columns);
        return walker.Apply(sql);
    }

    /// <summary>
    /// Rewrites the stored <c>CREATE INDEX</c> text of an explicit index over the renamed
    /// column's table: key column names, key expressions, and the partial-index predicate.
    /// Returns the rewritten text, or the original text when the index does not reference the
    /// renamed column; <see langword="null"/> only when the text cannot be reparsed, which
    /// forces the caller back onto schema regeneration.
    /// </summary>
    public static string? RewriteCreateIndex(
        string sql,
        string tableName,
        IReadOnlyList<string> columns,
        string oldName,
        string newName,
        bool quoteNewName)
    {
        ParsedStatement parsed;
        SqlSourceSpans spans;
        try
        {
            parsed = SqlParser.ParseWithSpans(sql, out spans);
        }
        catch (EmbeddedSqlException)
        {
            return null;
        }

        if (parsed is not CreateIndexStatement statement)
            return null;

        var schema = new RenameColumnSchema(
            name => string.Equals(name, tableName, StringComparison.OrdinalIgnoreCase) ? columns : null,
            name => string.Equals(name, tableName, StringComparison.OrdinalIgnoreCase));
        var walker = new Walker(spans, oldName, newName, quoteNewName, schema);
        walker.WalkCreateIndex(statement, tableName, columns);
        return walker.Apply(sql) ?? sql;
    }

    /// <summary>
    /// Applies SQLite's replacement-token rule: the substituted identifier is quoted when the
    /// new name was written quoted in the ALTER statement or when the token being replaced was
    /// itself quoted.
    /// </summary>
    private static string FormatReplacement(string newName, bool quoteNewName, bool tokenWasQuoted)
    {
        if (!quoteNewName && !tokenWasQuoted)
            return newName;

        return "\"" + newName.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private sealed record Binding(
        string? Name,
        IReadOnlyList<string>? Columns,
        bool IsTarget,
        bool QualifiedOnly);

    private sealed class Scope(Scope? parent)
    {
        public Scope? Parent { get; } = parent;

        public List<Binding> Bindings { get; } = [];

        public List<string> OutputAliases { get; } = [];
    }

    private enum Resolution
    {
        /// <summary>The reference belongs to the renamed table and must be edited.</summary>
        Target,

        /// <summary>The reference belongs to some other source and must be left alone.</summary>
        Other,

        /// <summary>The reference cannot be resolved; the rename must be rejected.</summary>
        Unresolved,
    }

    private sealed class Walker(
        SqlSourceSpans spans,
        string oldName,
        string newName,
        bool quoteNewName,
        RenameColumnSchema schema)
    {
        private readonly List<SqlSourceSpan> _edits = [];
        private bool _skipExistsInResultExpressions;

        public string? Apply(string sql)
        {
            if (_edits.Count == 0)
                return null;

            var ordered = _edits
                .Distinct()
                .OrderByDescending(static span => span.Start)
                .ToArray();
            var builder = new StringBuilder(sql);
            foreach (var span in ordered)
            {
                builder.Remove(span.Start, span.End - span.Start);
                builder.Insert(span.Start, FormatReplacement(newName, quoteNewName, span.IsQuoted));
            }

            return builder.ToString();
        }

        public void WalkTrigger(CreateTriggerStatement trigger)
        {
            var triggerTable = trigger.TableName;
            var triggerColumns = schema.ResolveColumns(triggerTable);
            var isTargetTrigger = schema.IsRenameTarget(triggerTable);

            if (isTargetTrigger && trigger.UpdateOfColumns is { Count: > 0 })
            {
                var updateOfSpans = spans.GetList(trigger);
                if (updateOfSpans is not null)
                {
                    for (var index = 0; index < trigger.UpdateOfColumns.Count && index < updateOfSpans.Count; index++)
                    {
                        if (Matches(trigger.UpdateOfColumns[index]))
                            _edits.Add(updateOfSpans[index]);
                    }
                }
                else if (trigger.UpdateOfColumns.Any(Matches))
                {
                    throw Reject($"no such column: {oldName}");
                }
            }

            // Inside a trigger program only the NEW and OLD pseudo-rows are in scope, and they
            // can only be reached through their qualifier. A reference qualified by the trigger's
            // own table name does not resolve, exactly as SQLite reports.
            var scope = new Scope(null);
            scope.Bindings.Add(new Binding("NEW", triggerColumns, isTargetTrigger, QualifiedOnly: true));
            scope.Bindings.Add(new Binding("OLD", triggerColumns, isTargetTrigger, QualifiedOnly: true));

            _skipExistsInResultExpressions = true;
            try
            {
                if (trigger.When is not null)
                    WalkExpression(trigger.When, scope);

                foreach (var statement in trigger.Body)
                    WalkStatement(statement, scope);
            }
            finally
            {
                _skipExistsInResultExpressions = false;
            }
        }

        private void WalkStatement(ParsedStatement statement, Scope? outer)
        {
            switch (statement)
            {
                case QueryStatement query:
                    WalkQuery(query, outer);
                    break;
                case InsertStatement insert:
                    WalkInsert(insert, outer);
                    break;
                case UpdateStatement update:
                    WalkUpdate(update, outer);
                    break;
                case DeleteStatement delete:
                    WalkDelete(delete, outer);
                    break;
                case WithDmlStatement withDml:
                    {
                        var scope = new Scope(outer);
                        BindCommonTableExpressions(withDml.CommonTableExpressions, scope);
                        WalkStatement(withDml.Dml, scope);
                        break;
                    }
            }
        }

        private void WalkInsert(InsertStatement insert, Scope? outer)
        {
            if (insert.Columns is { Length: > 0 } && schema.IsRenameTarget(insert.TableName))
            {
                var columnSpans = spans.GetList(insert);
                if (columnSpans is not null)
                {
                    for (var index = 0; index < insert.Columns.Length && index < columnSpans.Count; index++)
                    {
                        if (Matches(insert.Columns[index]))
                            _edits.Add(columnSpans[index]);
                    }
                }
                else if (insert.Columns.Any(Matches))
                {
                    throw Reject($"no such column: {oldName}");
                }
            }

            foreach (var row in insert.Rows)
            {
                foreach (var value in row)
                    WalkExpression(value, outer);
            }

            if (insert.Source is not null)
                WalkQuery(insert.Source, outer);

            if (insert.Upsert is not null)
                WalkUpsert(insert.Upsert, insert.TableName, outer);

            WalkProjections(insert.Returning, TableScope(insert.TableName, outer));
        }

        private void WalkUpsert(UpsertClause upsert, string tableName, Scope? outer)
        {
            foreach (var clause in upsert.Clauses())
            {
                var scope = TableScope(tableName, outer);
                foreach (var target in clause.Target)
                {
                    if (target.Expression is not null)
                        WalkExpression(target.Expression, scope);
                }

                if (clause.TargetWhere is not null)
                    WalkExpression(clause.TargetWhere, scope);

                if (clause.Action is DoUpdateUpsertAction update)
                {
                    WalkAssignments(update.Assignments, tableName);
                    WalkAssignmentValues(update.Assignments, scope);
                    if (update.Where is not null)
                        WalkExpression(update.Where, scope);
                }
            }
        }

        private void WalkUpdate(UpdateStatement update, Scope? outer)
        {
            WalkAssignments(update.Assignments, update.TableName);
            var scope = TableScope(update.TableName, outer);
            // UPDATE...FROM introduces additional table sources whose columns may be
            // referenced in the SET values, WHERE, ORDER BY, and LIMIT. Bind them into the
            // same scope so qualified references (e.g. SET z = src.b FROM src) resolve and
            // get rewritten when src is the rename target.
            if (update.From is not null)
                BindSource(update.From, scope);

            WalkAssignmentValues(update.Assignments, scope);
            if (update.Where is not null)
                WalkExpression(update.Where, scope);

            WalkProjections(update.Returning, scope);
            WalkOrderBy(update.OrderBy, scope);
            WalkExpression(update.Limit, scope);
            WalkExpression(update.Offset, scope);
        }

        private void WalkDelete(DeleteStatement delete, Scope? outer)
        {
            var scope = TableScope(delete.TableName, outer);
            if (delete.Where is not null)
                WalkExpression(delete.Where, scope);

            WalkProjections(delete.Returning, scope);
            WalkOrderBy(delete.OrderBy, scope);
        }

        private void WalkAssignments(IReadOnlyList<ColumnAssignment> assignments, string tableName)
        {
            if (!schema.IsRenameTarget(tableName))
                return;

            foreach (var assignment in assignments)
            {
                if (!Matches(assignment.Column))
                    continue;

                var span = spans.GetName(assignment);
                if (span is null)
                    throw Reject($"no such column: {oldName}");

                _edits.Add(span.Value);
            }
        }

        // A row assignment shares one value expression across its column targets, so only the
        // first target walks it.
        private void WalkAssignmentValues(IReadOnlyList<ColumnAssignment> assignments, Scope scope)
        {
            foreach (var assignment in assignments)
            {
                if (assignment.ValueIndex == 0)
                    WalkExpression(assignment.Value, scope);
            }
        }

        private Scope TableScope(string tableName, Scope? outer)
        {
            var scope = new Scope(outer);
            scope.Bindings.Add(new Binding(
                UnqualifiedObjectName(tableName),
                schema.ResolveColumns(tableName),
                schema.IsRenameTarget(tableName),
                QualifiedOnly: false));
            return scope;
        }

        public void WalkQuery(QueryStatement query, Scope? outer)
        {
            switch (query)
            {
                case SelectStatement select:
                    WalkSelect(select, outer);
                    break;
                case ValuesClause values:
                    {
                        foreach (var row in values.Rows)
                        {
                            foreach (var value in row)
                                WalkExpression(value, outer);
                        }

                        break;
                    }
                case CompoundSelectStatement compound:
                    {
                        foreach (var term in compound.Terms)
                            WalkQuery(term, outer);

                        // A compound ORDER BY may name any term's result alias, so an alias from
                        // any branch keeps the ordering term an output reference.
                        var scope = new Scope(outer);
                        foreach (var term in compound.Terms)
                            CollectOutputAliases(term, scope);

                        WalkOrderBy(compound.OrderBy, scope);
                        WalkExpression(compound.Limit, scope);
                        WalkExpression(compound.Offset, scope);
                        break;
                    }
                case WithSelectStatement with:
                    {
                        var scope = new Scope(outer);
                        BindCommonTableExpressions(with.CommonTableExpressions, scope);
                        WalkQuery(with.Query, scope);
                        break;
                    }
            }
        }

        private void BindCommonTableExpressions(
            IReadOnlyList<CommonTableExpression> expressions,
            Scope scope)
        {
            foreach (var cte in expressions)
            {
                WalkStatement(cte.Body, scope);
                scope.Bindings.Add(new Binding(
                    cte.Name,
                    cte.Columns ?? TryComputeOutputColumns(cte.Body),
                    IsTarget: false,
                    QualifiedOnly: false));
            }
        }

        private IReadOnlyList<string>? TryComputeOutputColumns(ParsedStatement statement)
        {
            if (statement is QueryStatement query)
                return TryComputeOutputColumns(query);
            if (!EmbeddedDatabase.TryGetReturning(statement, out _, out var returning))
                return [];

            var names = new List<string>(returning.Count);
            foreach (var projection in returning)
            {
                if (projection.Alias is not null)
                    names.Add(projection.Alias);
                else if (projection.Expression is ColumnExpression column)
                    names.Add(column.Name);
                else
                    return null;
            }

            return names;
        }

        private void WalkSelect(SelectStatement select, Scope? outer)
        {
            var scope = new Scope(outer);
            if (select.Source is not null)
                BindSource(select.Source, scope);

            foreach (var projection in select.Projections)
            {
                if (_skipExistsInResultExpressions)
                    WalkResultExpression(projection.Expression, scope);
                else
                    WalkExpression(projection.Expression, scope);
                if (projection.Alias is not null)
                    scope.OutputAliases.Add(projection.Alias);
            }

            WalkExpression(select.Where, scope);
            foreach (var group in select.GroupBy)
                WalkExpression(group, scope);

            WalkExpression(select.Having, scope);
            foreach (var window in select.NamedWindows)
                WalkWindow(window.Specification, scope);

            WalkOrderBy(select.OrderBy, scope);
            WalkExpression(select.Limit, scope);
            WalkExpression(select.Offset, scope);
        }

        // SQLite validates EXISTS subqueries reached through a trigger result expression against
        // the candidate trigger after the rewrite pass instead of rewriting them eagerly.
        private void WalkResultExpression(Expression expression, Scope? scope)
            => WalkExpression(expression, scope, skipExistsSubqueries: true);

        private void WalkProjections(IReadOnlyList<Projection>? projections, Scope? scope)
        {
            if (projections is null)
                return;

            foreach (var projection in projections)
                WalkExpression(projection.Expression, scope);
        }

        private void WalkOrderBy(IReadOnlyList<OrderByTerm>? terms, Scope? scope)
        {
            if (terms is null)
                return;

            foreach (var term in terms)
            {
                // A bare ORDER BY term that names an explicit result alias is an output
                // reference, not a column reference, so it keeps the alias spelling.
                if (term.Expression is ColumnExpression { Qualifier: null } column
                    && scope is not null
                    && scope.OutputAliases.Any(alias => string.Equals(alias, column.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                WalkExpression(term.Expression, scope);
            }
        }

        private void BindSource(TableSource source, Scope scope)
        {
            switch (source)
            {
                case NamedTableSource named:
                    scope.Bindings.Add(new Binding(
                        named.Alias ?? UnqualifiedObjectName(named.Name),
                        schema.ResolveColumns(named.Name),
                        schema.IsRenameTarget(named.Name),
                        QualifiedOnly: false));
                    break;
                case DerivedTableSource derived:
                    WalkQuery(derived.Query, scope.Parent);
                    scope.Bindings.Add(new Binding(
                        derived.Alias,
                        TryComputeOutputColumns(derived.Query),
                        IsTarget: false,
                        QualifiedOnly: false));
                    break;
                case TableValuedFunctionSource function:
                    foreach (var argument in function.Arguments)
                        WalkExpression(argument, scope);

                    scope.Bindings.Add(new Binding(
                        function.Alias ?? function.Name,
                        TableValuedFunctionRegistry.TryResolve(function.Name, out var module)
                            ? module.Schema.AllColumns
                            : [],
                        IsTarget: false,
                        QualifiedOnly: false));
                    break;
                case JoinTableSource join:
                    BindSource(join.Left, scope);
                    BindSource(join.Right, scope);
                    if (join.Condition is not null)
                        WalkExpression(join.Condition, scope);

                    // USING(...) names a column that must exist under the same spelling on both
                    // sides of the join, so a rename can never keep it valid.
                    if (join.UsingColumns is not null && join.UsingColumns.Any(Matches))
                        throw Reject($"cannot join using column {oldName} - column not present in both tables");

                    break;
            }
        }

        private void WalkWindow(WindowSpecification? window, Scope? scope)
        {
            if (window is null)
                return;

            foreach (var partition in window.PartitionBy)
                WalkExpression(partition, scope);

            foreach (var term in window.OrderBy)
                WalkExpression(term.Expression, scope);

            if (window.Frame is not null)
            {
                WalkExpression(window.Frame.Start.Offset, scope);
                WalkExpression(window.Frame.End.Offset, scope);
            }
        }

        public void WalkExpression(
            Expression? expression,
            Scope? scope,
            bool skipExistsSubqueries = false)
        {
            switch (expression)
            {
                case null:
                case LiteralExpression:
                case ParameterExpression:
                case CurrentTimeExpression:
                case RaiseExpression:
                case StarExpression:
                case QualifiedStarExpression:
                    return;
                case ColumnExpression column:
                    ResolveColumnReference(column, scope);
                    return;
                case RowValueExpression row:
                    foreach (var value in row.Values)
                        WalkExpression(value, scope, skipExistsSubqueries);
                    return;
                case FunctionExpression function:
                    foreach (var argument in function.Arguments)
                        WalkExpression(argument, scope, skipExistsSubqueries);

                    WalkExpression(function.Filter, scope, skipExistsSubqueries);
                    if (function.OrderedSetOrderBy is { } orderedSetOrderBy)
                        WalkExpression(orderedSetOrderBy.Expression, scope, skipExistsSubqueries);
                    WalkWindow(function.Window, scope);
                    return;
                case ScalarSubqueryExpression scalar:
                    WalkQuery(scalar.Query, scope);
                    return;
                case ExistsExpression exists:
                    if (!skipExistsSubqueries)
                        WalkQuery(exists.Query, scope);
                    return;
                case CollationExpression collation:
                    WalkExpression(collation.Expression, scope, skipExistsSubqueries);
                    return;
                case CastExpression cast:
                    WalkExpression(cast.Expression, scope, skipExistsSubqueries);
                    return;
                case CaseExpression caseExpression:
                    WalkExpression(caseExpression.Operand, scope, skipExistsSubqueries);
                    foreach (var clause in caseExpression.Clauses)
                    {
                        WalkExpression(clause.When, scope, skipExistsSubqueries);
                        WalkExpression(clause.Then, scope, skipExistsSubqueries);
                    }

                    WalkExpression(caseExpression.Else, scope, skipExistsSubqueries);
                    return;
                case LikeExpression like:
                    WalkExpression(like.Value, scope, skipExistsSubqueries);
                    WalkExpression(like.Pattern, scope, skipExistsSubqueries);
                    WalkExpression(like.Escape, scope, skipExistsSubqueries);
                    return;
                case GlobExpression glob:
                    WalkExpression(glob.Value, scope, skipExistsSubqueries);
                    WalkExpression(glob.Pattern, scope, skipExistsSubqueries);
                    return;
                case InExpression inExpression:
                    WalkExpression(inExpression.Value, scope, skipExistsSubqueries);
                    foreach (var value in inExpression.Values)
                        WalkExpression(value, scope, skipExistsSubqueries);
                    return;
                case InSubqueryExpression inSubquery:
                    WalkExpression(inSubquery.Value, scope, skipExistsSubqueries);
                    WalkQuery(inSubquery.Query, scope);
                    return;
                case BetweenExpression between:
                    WalkExpression(between.Value, scope, skipExistsSubqueries);
                    WalkExpression(between.Lower, scope, skipExistsSubqueries);
                    WalkExpression(between.Upper, scope, skipExistsSubqueries);
                    return;
                case UnaryExpression unary:
                    WalkExpression(unary.Operand, scope, skipExistsSubqueries);
                    return;
                case BinaryExpression binary:
                    WalkExpression(binary.Left, scope, skipExistsSubqueries);
                    WalkExpression(binary.Right, scope, skipExistsSubqueries);
                    return;
                default:
                    throw Reject(
                        $"unsupported expression '{expression.GetType().Name}' while renaming column {oldName}");
            }
        }

        private void ResolveColumnReference(ColumnExpression column, Scope? scope)
        {
            var name = column.UnqualifiedName ?? column.Name;
            if (!Matches(name))
                return;

            switch (Resolve(column.Qualifier, name, scope))
            {
                case Resolution.Target:
                    {
                        var span = spans.GetName(column);
                        if (span is null)
                            throw Reject($"no such column: {FormatQualifiedName(column)}");

                        _edits.Add(span.Value);
                        return;
                    }
                case Resolution.Other:
                    return;
                default:
                    throw Reject($"no such column: {FormatQualifiedName(column)}");
            }
        }

        /// <summary>
        /// Formats a column reference for a "no such column" diagnostic, preserving a
        /// schema qualifier (<c>main.t.b</c>) the way SQLite does. <see cref="ColumnExpression.Name"/>
        /// already carries the table qualifier for one- or two-part references, so the schema
        /// is prepended only when present.
        /// </summary>
        private static string FormatQualifiedName(ColumnExpression column)
            => column.Schema is { } schema ? schema + "." + column.Name : column.Name;

        private Resolution Resolve(string? qualifier, string name, Scope? scope)
        {
            for (var current = scope; current is not null; current = current.Parent)
            {
                if (qualifier is not null)
                {
                    foreach (var binding in current.Bindings)
                    {
                        if (binding.Name is null
                            || !string.Equals(binding.Name, qualifier, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        // An unknown column list means the source could not be resolved, so the
                        // safe answer is "not the renamed table" and validation gets the last word.
                        if (binding.Columns is null)
                            return Resolution.Other;

                        return binding.Columns.Any(column => string.Equals(column, name, StringComparison.OrdinalIgnoreCase))
                            ? binding.IsTarget ? Resolution.Target : Resolution.Other
                            : Resolution.Unresolved;
                    }

                    continue;
                }

                foreach (var binding in current.Bindings)
                {
                    if (binding.QualifiedOnly)
                        continue;
                    if (binding.Columns is null)
                        return Resolution.Other;
                    if (binding.Columns.Any(column => string.Equals(column, name, StringComparison.OrdinalIgnoreCase)))
                        return binding.IsTarget ? Resolution.Target : Resolution.Other;
                }

                if (current.OutputAliases.Any(alias => string.Equals(alias, name, StringComparison.OrdinalIgnoreCase)))
                    return Resolution.Other;
            }

            return Resolution.Unresolved;
        }

        private IReadOnlyList<string>? TryComputeOutputColumns(QueryStatement query)
        {
            switch (query)
            {
                case SelectStatement select:
                    {
                        var names = new List<string>();
                        foreach (var projection in select.Projections)
                        {
                            if (projection.Alias is not null)
                            {
                                names.Add(projection.Alias);
                                continue;
                            }

                            if (projection.Expression is ColumnExpression column)
                            {
                                names.Add(column.UnqualifiedName ?? column.Name);
                                continue;
                            }

                            return null;
                        }

                        return names;
                    }
                case CompoundSelectStatement compound when compound.Terms.Count > 0:
                    return TryComputeOutputColumns(compound.Terms[0]);
                case WithSelectStatement with:
                    return TryComputeOutputColumns(with.Query);
                default:
                    return null;
            }
        }

        private void CollectOutputAliases(QueryStatement? query, Scope scope)
        {
            if (query is not SelectStatement select)
                return;

            foreach (var projection in select.Projections)
            {
                if (projection.Alias is not null)
                    scope.OutputAliases.Add(projection.Alias);
            }
        }

        public void WalkCreateTable(CreateTableStatement statement, string tableName, IReadOnlyList<string> columns)
        {
            var scope = new Scope(null);
            scope.Bindings.Add(new Binding(tableName, columns, IsTarget: true, QualifiedOnly: false));

            foreach (var column in statement.Columns)
            {
                if (Matches(column.Name))
                {
                    var span = spans.GetName(column);
                    if (span is not null)
                        _edits.Add(span.Value);
                }

                if (column.GenerationExpression is not null)
                    WalkExpression(column.GenerationExpression, scope);

                foreach (var check in column.CheckConstraints)
                    WalkExpression(check.Expression, scope);

                // A column-level REFERENCES names the parent's column, which is affected when
                // the referenced table is the rename target itself.
                foreach (var foreignKey in column.ForeignKeyConstraints)
                    WalkForeignKeyParentColumns(foreignKey);
            }

            foreach (var keyColumn in statement.PrimaryKeyColumns ?? [])
                EditConstraintColumnName(keyColumn);

            foreach (var unique in statement.UniqueConstraints ?? [])
            {
                foreach (var keyColumn in unique.Columns)
                    EditConstraintColumnName(keyColumn);
            }

            foreach (var check in statement.CheckConstraints ?? [])
                WalkExpression(check.Expression, scope);

            foreach (var foreignKey in statement.TableForeignKeys ?? [])
            {
                WalkForeignKeyChildColumns(foreignKey);
                WalkForeignKeyParentColumns(foreignKey);
            }
        }

        public void WalkCreateIndex(CreateIndexStatement statement, string tableName, IReadOnlyList<string> columns)
        {
            var scope = new Scope(null);
            scope.Bindings.Add(new Binding(tableName, columns, IsTarget: true, QualifiedOnly: false));

            foreach (var keyColumn in statement.Columns)
            {
                if (keyColumn.Name is not null)
                {
                    if (Matches(keyColumn.Name))
                    {
                        var span = spans.GetName(keyColumn);
                        if (span is not null)
                            _edits.Add(span.Value);
                    }
                }
                else
                {
                    WalkExpression(keyColumn.Expression, scope);
                }
            }

            WalkExpression(statement.Where, scope);
        }

        private void EditConstraintColumnName(TablePrimaryKeyColumn keyColumn)
        {
            if (!Matches(keyColumn.Name))
                return;

            var span = spans.GetName(keyColumn);
            if (span is not null)
                _edits.Add(span.Value);
        }

        private void WalkForeignKeyChildColumns(ForeignKeyDefinition foreignKey)
        {
            var childSpans = spans.GetList(foreignKey);
            for (var index = 0; index < foreignKey.ChildColumns.Count; index++)
            {
                if (!Matches(foreignKey.ChildColumns[index]))
                    continue;

                if (childSpans is not null && index < childSpans.Count)
                    _edits.Add(childSpans[index]);
            }
        }

        private void WalkForeignKeyParentColumns(ForeignKeyDefinition foreignKey)
        {
            if (!schema.IsRenameTarget(foreignKey.ParentTable))
                return;

            var parentSpans = spans.GetQualifierList(foreignKey);
            for (var index = 0; index < foreignKey.ParentColumns.Count; index++)
            {
                if (!Matches(foreignKey.ParentColumns[index]))
                    continue;

                if (parentSpans is not null && index < parentSpans.Count)
                    _edits.Add(parentSpans[index]);
            }
        }

        private bool Matches(string name) => string.Equals(name, oldName, StringComparison.OrdinalIgnoreCase);

        private static RenameColumnRewriteException Reject(string message) => new(message);
    }

    private static string UnqualifiedObjectName(string name)
        => ManagedSchemaName.TrySplit(name, out _, out var bare) ? bare : name;
}
