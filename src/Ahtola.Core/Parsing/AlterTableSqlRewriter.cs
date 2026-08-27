namespace Ahtola.Core.Parsing;

/// <summary>
/// Performs the surgical text edits that <c>ALTER TABLE ... ADD COLUMN</c> and
/// <c>ALTER TABLE ... DROP COLUMN</c> apply to the stored CREATE TABLE statement. SQLite keeps
/// the original statement text and edits tokens in place (sqlite3AlterAddColumn /
/// sqlite3AlterDropColumn in alter.c), so verbatim spacing, quoting, and comments survive.
/// Ahtola falls back to regenerating the text only when the stored text cannot be reparsed.
/// </summary>
internal static class AlterTableSqlRewriter
{
    /// <summary>
    /// Inserts the added column definition immediately after the last existing column
    /// definition, the way SQLite's token edit does: a table constraint may only follow column
    /// definitions, so the new column lands before the first table constraint (or before the
    /// closing parenthesis when there is none). Returns <see langword="null"/> when the stored
    /// text cannot be reparsed.
    /// </summary>
    public static string? InsertAddedColumn(string sql, string columnSql)
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
        if (spans.GetQualifier(statement) is not { } closeParen)
            return null;

        // When table constraints follow the columns, the last column's recorded extent ends
        // right after its separating comma; insert "column, " there to keep constraints last.
        if (statement.Columns.Count > 0
            && spans.GetDefinitionExtent(statement.Columns[^1]) is { } lastColumnExtent
            && lastColumnExtent.End < closeParen.Start)
        {
            return string.Concat(
                sql.AsSpan(0, lastColumnExtent.End),
                columnSql,
                ", ",
                sql.AsSpan(lastColumnExtent.End));
        }

        return string.Concat(
            sql.AsSpan(0, closeParen.Start),
            ", ",
            columnSql,
            sql.AsSpan(closeParen.Start));
    }

    /// <summary>
    /// Removes the stored definition of <paramref name="columnName"/> including exactly one
    /// separating comma, the way SQLite's DROP COLUMN edit does. Returns <see langword="null"/>
    /// when the stored text cannot be reparsed or the definition extent is unknown.
    /// </summary>
    public static string? RemoveColumn(string sql, string columnName)
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

        var column = statement.Columns.FirstOrDefault(
            candidate => string.Equals(candidate.Name, columnName, StringComparison.OrdinalIgnoreCase));
        if (column is null)
            return null;
        if (spans.GetDefinitionExtent(column) is not { } extent)
            return null;

        return string.Concat(sql.AsSpan(0, extent.Start), sql.AsSpan(extent.End));
    }

    /// <summary>
    /// Replaces the table-name token in the stored CREATE TABLE text. SQLite's RENAME TO edit
    /// always writes the new name double-quoted (sqlite3_rename_token in alter.c), so the
    /// replacement is quoted unconditionally. Returns <see langword="null"/> when the stored text
    /// cannot be reparsed.
    /// </summary>
    public static string? RenameTable(string sql, string newName)
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

        SqlSourceSpan? nameSpan = parsed switch
        {
            CreateTableStatement statement => spans.GetName(statement),
            CreateTableAsSelectStatement statement => spans.GetName(statement),
            _ => null,
        };
        if (nameSpan is not { } name)
            return null;

        var edits = new List<SqlSourceSpan> { name };
        if (parsed is CreateTableStatement table)
        {
            // A CHECK expression is evaluated against the renamed table's row, so table-qualified
            // references in it must follow the CREATE TABLE name token.
            var collector = new TableReferenceCollector(
                spans,
                table.Name,
                targetSchema: "main",
                includeUnqualifiedReferences: true);
            foreach (var column in table.Columns)
            {
                foreach (var check in column.CheckConstraints)
                    collector.CollectExpression(check.Expression);
            }
            foreach (var check in table.CheckConstraints ?? [])
                collector.CollectExpression(check.Expression);

            if (collector.Aborted)
                return null;

            edits.AddRange(collector.Spans);
        }

        var replacement = QuoteIdentifier(newName);
        foreach (var edit in edits.Distinct().OrderByDescending(static span => span.Start))
            sql = ReplaceSpan(sql, edit, replacement);
        return sql;
    }

    /// <summary>
    /// Rewrites table qualifiers in a standalone table-owned expression such as a CHECK body.
    /// This is deliberately expression-scoped: a CHECK cannot bind aliases or another table, so
    /// any matching qualifier identifies the table being renamed.
    /// </summary>
    public static string? RenameTableExpressionReferences(string sql, string oldName, string newName)
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

        var collector = new TableReferenceCollector(
            spans,
            oldName,
            targetSchema: "main",
            includeUnqualifiedReferences: true);
        collector.CollectExpression(expression);
        if (collector.Aborted)
            return null;
        if (collector.Spans.Count == 0)
            return sql;

        var replacement = QuoteIdentifier(newName);
        foreach (var span in collector.Spans.OrderByDescending(static span => span.Start))
            sql = ReplaceSpan(sql, span, replacement);
        return sql;
    }

    /// <summary>
    /// Rewrites the ON-clause table reference of a stored CREATE INDEX statement. Returns
    /// <see langword="null"/> when the stored text cannot be reparsed.
    /// </summary>
    public static string? RenameIndexTable(string sql, string newName)
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
        if (spans.GetQualifier(statement) is not { } tableName)
            return null;

        return ReplaceSpan(sql, tableName, QuoteIdentifier(newName));
    }

    /// <summary>
    /// Rewrites every foreign-key reference to <paramref name="oldParentName"/> in a stored
    /// CREATE TABLE statement so it points at <paramref name="newName"/>. Returns
    /// <see langword="null"/> when the stored text cannot be reparsed or any matching reference
    /// lacks a recorded token span.
    /// </summary>
    public static string? RenameForeignKeyParentTable(string sql, string oldParentName, string newName)
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

        var foreignKeys = statement.Columns
            .Where(column => column.ForeignKey is not null)
            .Select(column => column.ForeignKey!)
            .Concat(statement.TableForeignKeys ?? []);

        var targetSpans = new List<SqlSourceSpan>();
        foreach (var foreignKey in foreignKeys)
        {
            if (!string.Equals(foreignKey.ParentTable, oldParentName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (spans.GetName(foreignKey) is not { } parentSpan)
                return null;

            targetSpans.Add(parentSpan);
        }

        if (targetSpans.Count == 0)
            return sql;

        var replacement = QuoteIdentifier(newName);
        foreach (var span in targetSpans.OrderByDescending(span => span.Start))
            sql = ReplaceSpan(sql, span, replacement);

        return sql;
    }

    /// <summary>
    /// Rewrites every reference to <paramref name="oldName"/> in the stored text of a CREATE
    /// TRIGGER or CREATE VIEW statement so it follows an ALTER TABLE RENAME, mirroring SQLite's
    /// sqlite3_rename_trigger rewriting of dependent schema objects: the trigger's ON-clause
    /// table, DML target tables, FROM/JOIN table sources, and qualified column references
    /// (<c>t.col</c>, <c>t.*</c>). Returns the statement text unchanged when nothing matches,
    /// and <see langword="null"/> when the stored text cannot be reparsed or a matching
    /// reference lacks a recorded token span.
    /// </summary>
    public static string? RenameTableReferences(
        string sql,
        string oldName,
        string newName,
        string targetSchema = "main",
        bool includeUnqualifiedReferences = true)
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

        var collector = new TableReferenceCollector(
            spans,
            oldName,
            targetSchema,
            includeUnqualifiedReferences);
        switch (parsed)
        {
            case CreateTriggerStatement trigger:
                if (collector.IsRenameTarget(trigger.TableName))
                    collector.Add(spans.GetQualifier(trigger));
                collector.CollectExpression(trigger.When);
                foreach (var statement in trigger.Body)
                    collector.CollectStatement(statement);
                break;
            case CreateViewStatement view:
                collector.CollectQuery(view.Query);
                break;
            default:
                return sql;
        }

        if (collector.Aborted)
            return null;
        if (collector.Spans.Count == 0)
            return sql;

        var replacement = SqlIdentifierFormatter.QuoteIfNeeded(newName);
        foreach (var span in collector.Spans.OrderByDescending(span => span.Start))
            sql = ReplaceSpan(sql, span, replacement);

        return sql;
    }

    /// <summary>
    /// Collects the token spans of every table reference to the renamed table inside a stored
    /// trigger or view definition. A matching reference without a recorded span aborts the whole
    /// rewrite (the caller then leaves the dependent object untouched rather than corrupting it).
    /// </summary>
    private sealed class TableReferenceCollector(
        SqlSourceSpans spans,
        string oldName,
        string targetSchema,
        bool includeUnqualifiedReferences)
    {
        private readonly HashSet<int> _seenStarts = [];

        public List<SqlSourceSpan> Spans { get; } = [];

        public bool Aborted { get; private set; }

        public bool IsRenameTarget(string writtenName)
        {
            if (ManagedSchemaName.TrySplit(writtenName, out var schema, out var name))
            {
                return string.Equals(schema, targetSchema, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(name, oldName, StringComparison.OrdinalIgnoreCase);
            }

            return includeUnqualifiedReferences
                && string.Equals(writtenName, oldName, StringComparison.OrdinalIgnoreCase);
        }

        public void Add(SqlSourceSpan? span)
        {
            if (span is not { } found)
            {
                Aborted = true;
                return;
            }

            if (_seenStarts.Add(found.Start))
                Spans.Add(found);
        }

        public void CollectStatement(ParsedStatement statement)
        {
            if (Aborted)
                return;

            switch (statement)
            {
                case InsertStatement insert:
                    if (IsRenameTarget(insert.TableName))
                        Add(spans.GetName(insert));
                    foreach (var row in insert.Rows)
                    {
                        foreach (var value in row)
                            CollectExpression(value);
                    }
                    if (insert.Source is not null)
                        CollectQuery(insert.Source);
                    CollectProjections(insert.Returning);
                    CollectUpsert(insert.Upsert);
                    break;
                case UpdateStatement update:
                    if (IsRenameTarget(update.TableName))
                        Add(spans.GetName(update));
                    foreach (var assignment in update.Assignments)
                        CollectExpression(assignment.Value);
                    if (update.From is not null)
                        CollectTableSource(update.From);
                    CollectExpression(update.Where);
                    CollectProjections(update.Returning);
                    CollectOrderBy(update.EffectiveOrderBy);
                    CollectExpression(update.Limit);
                    CollectExpression(update.Offset);
                    break;
                case DeleteStatement delete:
                    if (IsRenameTarget(delete.TableName))
                        Add(spans.GetName(delete));
                    CollectExpression(delete.Where);
                    CollectProjections(delete.Returning);
                    CollectOrderBy(delete.EffectiveOrderBy);
                    CollectExpression(delete.Limit);
                    CollectExpression(delete.Offset);
                    break;
                case WithDmlStatement with:
                    CollectCommonTableExpressions(with.CommonTableExpressions);
                    CollectStatement(with.Dml);
                    break;
                default:
                    break;
            }
        }

        public void CollectQuery(QueryStatement query)
        {
            if (Aborted)
                return;

            switch (query)
            {
                case SelectStatement select:
                    CollectProjections(select.Projections);
                    if (select.Source is not null)
                        CollectTableSource(select.Source);
                    CollectExpression(select.Where);
                    foreach (var term in select.GroupBy)
                        CollectExpression(term);
                    CollectExpression(select.Having);
                    foreach (var window in select.NamedWindows)
                        CollectWindow(window.Specification);
                    CollectOrderBy(select.OrderBy);
                    CollectExpression(select.Limit);
                    CollectExpression(select.Offset);
                    break;
                case CompoundSelectStatement compound:
                    foreach (var term in compound.Terms)
                        CollectQuery(term);
                    CollectOrderBy(compound.OrderBy);
                    CollectExpression(compound.Limit);
                    CollectExpression(compound.Offset);
                    break;
                case WithSelectStatement with:
                    CollectCommonTableExpressions(with.CommonTableExpressions);
                    CollectQuery(with.Query);
                    break;
                case ValuesClause values:
                    foreach (var row in values.Rows)
                    {
                        foreach (var value in row)
                            CollectExpression(value);
                    }
                    break;
            }
        }

        private void CollectTableSource(TableSource source)
        {
            if (Aborted)
                return;

            switch (source)
            {
                case NamedTableSource named:
                    if (IsRenameTarget(named.Name))
                        Add(spans.GetName(named));
                    break;
                case JoinTableSource join:
                    CollectTableSource(join.Left);
                    CollectTableSource(join.Right);
                    CollectExpression(join.Condition);
                    break;
                case DerivedTableSource derived:
                    CollectQuery(derived.Query);
                    break;
                case TableValuedFunctionSource function:
                    foreach (var argument in function.Arguments)
                        CollectExpression(argument);
                    break;
            }
        }

        public void CollectExpression(Expression? expression)
        {
            if (expression is null || Aborted)
                return;

            switch (expression)
            {
                case ColumnExpression column:
                    if (includeUnqualifiedReferences
                        && column.Qualifier is not null
                        && string.Equals(column.Qualifier, oldName, StringComparison.OrdinalIgnoreCase))
                        Add(spans.GetQualifier(column));
                    break;
                case QualifiedStarExpression star:
                    if (includeUnqualifiedReferences
                        && string.Equals(star.Qualifier, oldName, StringComparison.OrdinalIgnoreCase))
                        Add(spans.GetQualifier(star));
                    break;
                case BinaryExpression binary:
                    CollectExpression(binary.Left);
                    CollectExpression(binary.Right);
                    break;
                case UnaryExpression unary:
                    CollectExpression(unary.Operand);
                    break;
                case FunctionExpression function:
                    foreach (var argument in function.Arguments)
                        CollectExpression(argument);
                    CollectExpression(function.Filter);
                    if (function.Window is not null)
                        CollectWindow(function.Window);
                    CollectOrderBy(function.AggregateOrderBy);
                    if (function.OrderedSetOrderBy is { } orderedSetOrderBy)
                        CollectExpression(orderedSetOrderBy.Expression);
                    break;
                case CaseExpression caseExpression:
                    CollectExpression(caseExpression.Operand);
                    foreach (var clause in caseExpression.Clauses)
                    {
                        CollectExpression(clause.When);
                        CollectExpression(clause.Then);
                    }
                    CollectExpression(caseExpression.Else);
                    break;
                case CastExpression cast:
                    CollectExpression(cast.Expression);
                    break;
                case CollationExpression collation:
                    CollectExpression(collation.Expression);
                    break;
                case LikeExpression like:
                    CollectExpression(like.Value);
                    CollectExpression(like.Pattern);
                    CollectExpression(like.Escape);
                    break;
                case GlobExpression glob:
                    CollectExpression(glob.Value);
                    CollectExpression(glob.Pattern);
                    break;
                case BetweenExpression between:
                    CollectExpression(between.Value);
                    CollectExpression(between.Lower);
                    CollectExpression(between.Upper);
                    break;
                case InExpression inList:
                    CollectExpression(inList.Value);
                    foreach (var value in inList.Values)
                        CollectExpression(value);
                    break;
                case InSubqueryExpression inSubquery:
                    CollectExpression(inSubquery.Value);
                    CollectQuery(inSubquery.Query);
                    break;
                case ExistsExpression exists:
                    CollectQuery(exists.Query);
                    break;
                case ScalarSubqueryExpression subquery:
                    CollectQuery(subquery.Query);
                    break;
                case RowValueExpression rowValue:
                    foreach (var value in rowValue.Values)
                        CollectExpression(value);
                    break;
                default:
                    break;
            }
        }

        private void CollectWindow(WindowSpecification window)
        {
            foreach (var term in window.PartitionBy)
                CollectExpression(term);
            CollectOrderBy(window.OrderBy);
            if (window.Frame is not { } frame)
                return;

            CollectExpression(frame.Start.Offset);
            CollectExpression(frame.End.Offset);
        }

        private void CollectUpsert(UpsertClause? upsert)
        {
            foreach (var clause in upsert?.Clauses() ?? [])
            {
                foreach (var target in clause.Target)
                    CollectExpression(target.Expression);
                CollectExpression(clause.TargetWhere);
                if (clause.Action is DoUpdateUpsertAction doUpdate)
                {
                    foreach (var assignment in doUpdate.Assignments)
                        CollectExpression(assignment.Value);
                    CollectExpression(doUpdate.Where);
                }
            }
        }

        private void CollectCommonTableExpressions(IReadOnlyList<CommonTableExpression> expressions)
        {
            foreach (var expression in expressions)
                CollectStatement(expression.Body);
        }

        private void CollectProjections(IReadOnlyList<Projection>? projections)
        {
            if (projections is null)
                return;

            foreach (var projection in projections)
                CollectExpression(projection.Expression);
        }

        private void CollectOrderBy(IReadOnlyList<OrderByTerm>? terms)
        {
            if (terms is null)
                return;

            foreach (var term in terms)
                CollectExpression(term.Expression);
        }
    }

    private static string ReplaceSpan(string sql, SqlSourceSpan span, string replacement)
        => string.Concat(sql.AsSpan(0, span.Start), replacement, sql.AsSpan(span.End));

    private static string QuoteIdentifier(string name)
        => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
