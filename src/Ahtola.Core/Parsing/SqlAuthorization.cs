using Ahtola.Core.Parsing;

namespace Ahtola.Core;

/// <summary>
/// Walks a parsed statement at preparation time, reports each authorizable action to the
/// connection's authorizer callback, and applies SQLite's <c>SQLITE_IGNORE</c> rewrites.
/// </summary>
/// <remarks>
/// The walk mirrors SQLite's behavior in the ways that matter for an authorization decision:
/// views and triggers are expanded so a policy cannot be bypassed by reading a base table
/// through a view or writing to it from a trigger body, and the innermost view or trigger name
/// is reported as the fourth callback argument.
/// </remarks>
internal static class SqlAuthorization
{
    internal static ParsedStatement Apply(
        ParsedStatement statement,
        EmbeddedDatabase.SchemaCatalog main,
        EmbeddedDatabase.SchemaCatalog temp,
        Func<SqliteAuthorizerContext, SqliteAuthorizerResult> authorizer)
        => new Walker(main, temp, authorizer).Statement(statement);

    private sealed record Scope(
        string? Schema,
        string Name,
        string? Alias,
        IReadOnlyList<string> Columns,
        bool IsBaseTable);

    private sealed class Walker(
        EmbeddedDatabase.SchemaCatalog main,
        EmbeddedDatabase.SchemaCatalog temp,
        Func<SqliteAuthorizerContext, SqliteAuthorizerResult> authorizer)
    {
        private const int MaximumExpansionDepth = 32;

        private readonly List<List<Scope>> _frames = [];
        private readonly HashSet<string> _cteNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _expanding = new(StringComparer.OrdinalIgnoreCase);
        private string? _origin;
        private int _depth;

        internal ParsedStatement Statement(ParsedStatement statement)
        {
            switch (statement)
            {
                case QueryStatement query:
                    return Query(query);
                case InsertStatement insert:
                    return Insert(insert);
                case UpdateStatement update:
                    return Update(update);
                case DeleteStatement delete:
                    return Delete(delete);
                case WithDmlStatement withDml:
                    return WithDml(withDml);
                case ExplainStatement explain:
                    return explain with { Inner = Statement(explain.Inner) };
                case ExplainQueryPlanStatement explainPlan:
                    return explainPlan with { Inner = Statement(explainPlan.Inner) };
                default:
                    Schema(statement);
                    return statement;
            }
        }

        // ---------------------------------------------------------------- statements

        private void Schema(ParsedStatement statement)
        {
            switch (statement)
            {
                case CreateTableStatement create:
                    {
                        var (schema, name) = Split(create.Name);
                        Require(
                            IsTemp(schema) ? SqliteAuthorizerAction.CreateTempTable : SqliteAuthorizerAction.CreateTable,
                            name,
                            null,
                            schema ?? "main");
                        break;
                    }

                case CreateVirtualTableStatement createVirtual:
                    {
                        var (schema, name) = Split(createVirtual.Name);
                        Require(
                            SqliteAuthorizerAction.CreateVTable,
                            name,
                            createVirtual.ModuleName,
                            schema ?? "main");
                        break;
                    }

                case CreateTableAsSelectStatement createAs:
                    {
                        var (schema, name) = Split(createAs.Name);
                        Require(
                            createAs.Temporary || IsTemp(schema)
                                ? SqliteAuthorizerAction.CreateTempTable
                                : SqliteAuthorizerAction.CreateTable,
                            name,
                            null,
                            createAs.Temporary ? "temp" : schema ?? "main");
                        _ = Query(createAs.Query);
                        break;
                    }

                case DropTableStatement drop:
                    {
                        var (schema, name) = Split(drop.Name);
                        var isView = Catalog(schema).Views.ContainsKey(name);
                        Require(
                            isView
                                ? IsTemp(schema) ? SqliteAuthorizerAction.DropTempView : SqliteAuthorizerAction.DropView
                                : Catalog(schema).VirtualTables.ContainsKey(name)
                                    ? SqliteAuthorizerAction.DropVTable
                                : IsTemp(schema) ? SqliteAuthorizerAction.DropTempTable : SqliteAuthorizerAction.DropTable,
                            name,
                            null,
                            schema ?? "main");
                        break;
                    }

                case CreateIndexStatement createIndex:
                    {
                        var (schema, name) = Split(createIndex.Name);
                        var (_, table) = Split(createIndex.TableName);
                        Require(
                            IsTemp(schema) ? SqliteAuthorizerAction.CreateTempIndex : SqliteAuthorizerAction.CreateIndex,
                            name,
                            table,
                            schema ?? "main");
                        break;
                    }

                case DropIndexStatement dropIndex:
                    {
                        var (schema, name) = Split(dropIndex.Name);
                        Require(
                            IsTemp(schema) ? SqliteAuthorizerAction.DropTempIndex : SqliteAuthorizerAction.DropIndex,
                            name,
                            null,
                            schema ?? "main");
                        break;
                    }

                case CreateViewStatement createView:
                    {
                        var (schema, name) = Split(createView.Name);
                        Require(
                            IsTemp(schema) ? SqliteAuthorizerAction.CreateTempView : SqliteAuthorizerAction.CreateView,
                            name,
                            null,
                            schema ?? "main");
                        break;
                    }

                case DropViewStatement dropView:
                    {
                        var (schema, name) = Split(dropView.Name);
                        Require(
                            IsTemp(schema) ? SqliteAuthorizerAction.DropTempView : SqliteAuthorizerAction.DropView,
                            name,
                            null,
                            schema ?? "main");
                        break;
                    }

                case CreateTriggerStatement createTrigger:
                    {
                        var (schema, name) = Split(createTrigger.Name);
                        var (_, table) = Split(createTrigger.TableName);
                        Require(
                            IsTemp(schema)
                                ? SqliteAuthorizerAction.CreateTempTrigger
                                : SqliteAuthorizerAction.CreateTrigger,
                            name,
                            table,
                            schema ?? "main");
                        break;
                    }

                case DropTriggerStatement dropTrigger:
                    {
                        var (schema, name) = Split(dropTrigger.Name);
                        Require(
                            IsTemp(schema) ? SqliteAuthorizerAction.DropTempTrigger : SqliteAuthorizerAction.DropTrigger,
                            name,
                            null,
                            schema ?? "main");
                        break;
                    }

                case AlterTableAddColumnStatement alter:
                    AlterTable(alter.TableName);
                    break;
                case AlterTableRenameStatement alter:
                    AlterTable(alter.TableName);
                    break;
                case AlterTableRenameColumnStatement alter:
                    AlterTable(alter.TableName);
                    break;
                case AlterTableAlterColumnStatement alter:
                    AlterTable(alter.TableName);
                    break;
                case AlterTableDropColumnStatement alter:
                    AlterTable(alter.TableName);
                    break;

                case BeginStatement:
                    Require(SqliteAuthorizerAction.Transaction, "BEGIN", null, null);
                    break;
                case CommitStatement:
                    Require(SqliteAuthorizerAction.Transaction, "COMMIT", null, null);
                    break;
                case RollbackStatement:
                    Require(SqliteAuthorizerAction.Transaction, "ROLLBACK", null, null);
                    break;
                case SavepointStatement savepoint:
                    Require(SqliteAuthorizerAction.Savepoint, "BEGIN", savepoint.Name, null);
                    break;
                case ReleaseSavepointStatement release:
                    Require(SqliteAuthorizerAction.Savepoint, "RELEASE", release.Name, null);
                    break;
                case RollbackToSavepointStatement rollbackTo:
                    Require(SqliteAuthorizerAction.Savepoint, "ROLLBACK", rollbackTo.Name, null);
                    break;

                case AttachDatabaseStatement:
                    Require(SqliteAuthorizerAction.Attach, null, null, null);
                    break;
                case DetachDatabaseStatement detach:
                    Require(SqliteAuthorizerAction.Detach, detach.Alias, null, null);
                    break;
                case ReindexStatement reindex:
                    Require(SqliteAuthorizerAction.Reindex, reindex.Target, null, null);
                    break;
                case OptimizeIndexStatement optimize:
                    Require(SqliteAuthorizerAction.Reindex, optimize.IndexName, null, null);
                    break;
                case AnalyzeStatement analyze:
                    Require(SqliteAuthorizerAction.Analyze, analyze.Target, null, null);
                    break;

                default:
                    Pragma(statement);
                    break;
            }
        }

        private void AlterTable(string qualifiedName)
        {
            var (schema, name) = Split(qualifiedName);
            Require(SqliteAuthorizerAction.AlterTable, schema ?? "main", name, null);
        }

        private void Pragma(ParsedStatement statement)
        {
            var (name, argument) = statement switch
            {
                PragmaTableInfoStatement pragma => ("table_info", pragma.TableName),
                PragmaTableXInfoStatement pragma => ("table_xinfo", pragma.TableName),
                PragmaIndexListStatement pragma => ("index_list", pragma.TableName),
                PragmaIndexInfoStatement pragma => ("index_info", pragma.IndexName),
                PragmaIndexXInfoStatement pragma => ("index_xinfo", pragma.IndexName),
                PragmaForeignKeyListStatement pragma => ("foreign_key_list", pragma.TableName),
                PragmaForeignKeyCheckStatement pragma => ("foreign_key_check", pragma.TableName),
                PragmaIntegrityCheckStatement pragma =>
                    (pragma.Quick ? "quick_check" : "integrity_check", pragma.TableName),
                PragmaTableListStatement => ("table_list", (string?)null),
                PragmaDatabaseListStatement => ("database_list", null),
                PragmaEncodingStatement => ("encoding", null),
                PragmaQueryOnlyStatement pragma => ("query_only", Flag(pragma.Enabled)),
                PragmaForeignKeysStatement pragma => ("foreign_keys", Flag(pragma.Enabled)),
                PragmaDeferForeignKeysStatement pragma => ("defer_foreign_keys", Flag(pragma.Enabled)),
                PragmaRecursiveTriggersStatement pragma => ("recursive_triggers", Flag(pragma.Enabled)),
                PragmaCaptureDataChangesConnectionStatement pragma => (
                    "capture_data_changes_conn",
                    pragma.Value),
                PragmaSynchronousStatement pragma => ("synchronous", pragma.Value),
                PragmaLockingModeStatement pragma => ("locking_mode", pragma.Value),
                PragmaAutoVacuumStatement pragma => ("auto_vacuum", pragma.Value),
                PragmaDataSyncRetryStatement pragma => ("data_sync_retry", Flag(pragma.Enabled)),
                PragmaFullColumnNamesStatement pragma => ("full_column_names", Flag(pragma.Enabled)),
                PragmaShortColumnNamesStatement pragma => ("short_column_names", Flag(pragma.Enabled)),
                PragmaMvccCheckpointThresholdStatement pragma => (
                    "mvcc_checkpoint_threshold",
                    pragma.Value?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                PragmaMvccGcThresholdStatement pragma => (
                    "mvcc_gc_threshold",
                    pragma.Value?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                PragmaListTypesStatement => ("list_types", null),
                PragmaFunctionListStatement => ("function_list", null),
                PragmaModuleListStatement => ("module_list", null),
                PragmaHeaderIntegerStatement pragma => (
                    pragma.Kind switch
                    {
                        PragmaHeaderIntegerKind.UserVersion => "user_version",
                        PragmaHeaderIntegerKind.ApplicationId => "application_id",
                        _ => "schema_version",
                    },
                    pragma.Value?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                PragmaJournalModeStatement pragma => ("journal_mode", pragma.Mode),
                PragmaPageSizeStatement pragma => (
                    "page_size",
                    pragma.Value?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                PragmaPageCountStatement => ("page_count", null),
                PragmaFreelistCountStatement => ("freelist_count", null),
                _ => (null, null),
            };

            if (name is not null)
                Require(SqliteAuthorizerAction.Pragma, name, argument, null);
        }

        private static string? Flag(bool? enabled) => enabled is null ? null : enabled.Value ? "1" : "0";

        private ParsedStatement Insert(InsertStatement statement)
        {
            var (schema, table) = Split(statement.TableName);
            var decision = Ask(SqliteAuthorizerAction.Insert, table, null, schema ?? "main");
            if (decision == SqliteAuthorizerResult.Deny)
                throw Denied(SqliteAuthorizerAction.Insert, table, null);

            PushFrame();
            Declare(schema, table);
            var rows = statement.Rows.Select(row => row.Select(Expr).ToArray()).ToArray();
            var source = statement.Source is null ? null : Query(statement.Source);
            var returning = Projections(statement.Returning);
            var upsert = Upsert(statement.Upsert, schema, table);
            PopFrame();

            WalkTriggers(schema, table, TriggerEvent.Insert);

            var rewritten = statement with
            {
                Rows = rows,
                Source = source,
                Returning = returning,
                Upsert = upsert,
            };

            // SQLITE_IGNORE turns the insert into a no-op while leaving preparation successful.
            return decision == SqliteAuthorizerResult.Ignore
                ? rewritten with { Rows = [], Source = null, Upsert = null }
                : rewritten;
        }

        private UpsertClause? Upsert(UpsertClause? upsert, string? schema, string table)
        {
            if (upsert is null)
                return null;

            var targetWhere = upsert.TargetWhere is null ? null : Expr(upsert.TargetWhere);
            var action = upsert.Action;
            if (action is DoUpdateUpsertAction doUpdate)
            {
                var assignments = Assignments(doUpdate.Assignments, schema, table);
                action = doUpdate with
                {
                    Assignments = assignments,
                    Where = doUpdate.Where is null ? null : Expr(doUpdate.Where),
                };
            }

            return upsert with
            {
                Action = action,
                TargetWhere = targetWhere,
                Next = Upsert(upsert.Next, schema, table),
            };
        }

        private ParsedStatement Update(UpdateStatement statement)
        {
            var (schema, table) = Split(statement.TableName);
            PushFrame();
            Declare(schema, table);
            var assignments = Assignments(statement.Assignments, schema, table);
            var where = statement.Where is null ? null : Expr(statement.Where);
            var returning = Projections(statement.Returning);
            var orderBy = OrderBy(statement.OrderBy);
            PopFrame();

            WalkTriggers(schema, table, TriggerEvent.Update);

            return statement with
            {
                Assignments = assignments,
                Where = where,
                Returning = returning,
                OrderBy = orderBy,
            };
        }

        private IReadOnlyList<ColumnAssignment> Assignments(
            IReadOnlyList<ColumnAssignment> assignments,
            string? schema,
            string table)
        {
            var rewritten = new List<ColumnAssignment>(assignments.Count);
            foreach (var assignment in assignments)
            {
                var decision = Ask(SqliteAuthorizerAction.Update, table, assignment.Column, schema ?? "main");
                if (decision == SqliteAuthorizerResult.Deny)
                    throw Denied(SqliteAuthorizerAction.Update, table, assignment.Column);

                // SQLITE_IGNORE skips the assignment. Assigning the column to itself keeps the
                // statement well formed while leaving the stored value untouched.
                rewritten.Add(decision == SqliteAuthorizerResult.Ignore
                    ? new ColumnAssignment(assignment.Column, new ColumnExpression(assignment.Column))
                    : assignment with { Value = Expr(assignment.Value) });
            }

            return rewritten;
        }

        private ParsedStatement Delete(DeleteStatement statement)
        {
            var (schema, table) = Split(statement.TableName);
            // SQLite treats SQLITE_IGNORE on a DELETE as permission granted; it only disables the
            // truncate optimization, so the rows are still removed.
            Require(SqliteAuthorizerAction.Delete, table, null, schema ?? "main");

            PushFrame();
            Declare(schema, table);
            var where = statement.Where is null ? null : Expr(statement.Where);
            var returning = Projections(statement.Returning);
            var orderBy = OrderBy(statement.OrderBy);
            PopFrame();

            WalkTriggers(schema, table, TriggerEvent.Delete);

            return statement with { Where = where, Returning = returning, OrderBy = orderBy };
        }

        private ParsedStatement WithDml(WithDmlStatement statement)
        {
            var added = new List<string>();
            var ctes = new List<CommonTableExpression>(statement.CommonTableExpressions.Count);
            foreach (var cte in statement.CommonTableExpressions)
            {
                ctes.Add(cte.WritableBody is null
                    ? cte with { Query = Query(cte.Query) }
                    : cte with { WritableBody = Statement(cte.WritableBody) });
                if (_cteNames.Add(cte.Name))
                    added.Add(cte.Name);
            }

            try
            {
                return statement with
                {
                    CommonTableExpressions = ctes,
                    Dml = Statement(statement.Dml),
                };
            }
            finally
            {
                foreach (var name in added)
                    _cteNames.Remove(name);
            }
        }

        private void WalkTriggers(string? schema, string table, TriggerEvent triggerEvent)
        {
            if (_depth >= MaximumExpansionDepth)
                return;

            var triggers = Catalog(schema).Triggers.Values
                .Where(trigger => trigger.Event == triggerEvent
                    && string.Equals(
                        StripSchema(trigger.TableName),
                        table,
                        StringComparison.OrdinalIgnoreCase))
                .OrderBy(trigger => trigger.DeclarationOrder)
                .ToArray();
            foreach (var trigger in triggers)
            {
                if (!_expanding.Add(trigger.Name))
                    continue;

                var previousOrigin = _origin;
                _origin = trigger.Name;
                _depth++;
                try
                {
                    foreach (var body in trigger.Body)
                        _ = Statement(body);
                }
                finally
                {
                    _depth--;
                    _origin = previousOrigin;
                    _expanding.Remove(trigger.Name);
                }
            }
        }

        // ---------------------------------------------------------------- queries

        private QueryStatement Query(QueryStatement query)
        {
            switch (query)
            {
                case WithSelectStatement with:
                    {
                        var added = new List<string>();
                        var ctes = new List<CommonTableExpression>(with.CommonTableExpressions.Count);
                        foreach (var cte in with.CommonTableExpressions)
                        {
                            ctes.Add(cte.WritableBody is null
                                ? cte with { Query = Query(cte.Query) }
                                : cte with { WritableBody = Statement(cte.WritableBody) });
                            if (_cteNames.Add(cte.Name))
                                added.Add(cte.Name);
                        }

                        try
                        {
                            return with with { CommonTableExpressions = ctes, Query = Query(with.Query) };
                        }
                        finally
                        {
                            foreach (var name in added)
                                _cteNames.Remove(name);
                        }
                    }

                case CompoundSelectStatement compound:
                    return compound with
                    {
                        Terms = compound.Terms.Select(Query).ToArray(),
                        OrderBy = OrderBy(compound.OrderBy) ?? [],
                        Limit = compound.Limit is null ? null : Expr(compound.Limit),
                        Offset = compound.Offset is null ? null : Expr(compound.Offset),
                    };

                case ValuesClause values:
                    {
                        var decision = Ask(SqliteAuthorizerAction.Select, null, null, null);
                        if (decision == SqliteAuthorizerResult.Deny)
                            throw Denied(SqliteAuthorizerAction.Select, null, null);

                        var rows = values.Rows
                            .Select(row => (IReadOnlyList<Expression>)row.Select(Expr).ToArray())
                            .ToArray();
                        return decision == SqliteAuthorizerResult.Ignore
                            ? values with { Rows = [] }
                            : values with { Rows = rows };
                    }

                case SelectStatement select:
                    return Select(select);

                default:
                    return query;
            }
        }

        private QueryStatement Select(SelectStatement select)
        {
            var decision = Ask(SqliteAuthorizerAction.Select, null, null, null);
            if (decision == SqliteAuthorizerResult.Deny)
                throw Denied(SqliteAuthorizerAction.Select, null, null);

            PushFrame();
            try
            {
                var source = select.Source is null ? null : Source(select.Source);
                var projections = Projections(select.Projections) ?? select.Projections;
                var where = select.Where is null ? null : Expr(select.Where);
                var groupBy = select.GroupBy.Select(Expr).ToArray();
                var having = select.Having is null ? null : Expr(select.Having);
                var orderBy = OrderBy(select.OrderBy) ?? [];
                var limit = select.Limit is null ? null : Expr(select.Limit);
                var offset = select.Offset is null ? null : Expr(select.Offset);
                var rewritten = select with
                {
                    Source = source,
                    Projections = projections,
                    Where = where,
                    GroupBy = groupBy,
                    Having = having,
                    OrderBy = orderBy,
                    Limit = limit,
                    Offset = offset,
                };

                // SQLITE_IGNORE on a SELECT leaves preparation successful but yields no rows.
                return decision == SqliteAuthorizerResult.Ignore
                    ? rewritten with { Limit = new LiteralExpression(SqlValue.Integer(0)) }
                    : rewritten;
            }
            finally
            {
                PopFrame();
            }
        }

        private TableSource Source(TableSource source)
        {
            switch (source)
            {
                case NamedTableSource named:
                    {
                        var (schema, name) = Split(named.Name);
                        if (schema is null && !named.IsSchemaQualified && _cteNames.Contains(name))
                        {
                            Current.Add(new Scope(null, name, named.Alias, [], IsBaseTable: false));
                            return named;
                        }

                        var catalog = Catalog(schema);
                        if (catalog.Views.TryGetValue(name, out var view)
                            || (schema is null && main.Views.TryGetValue(name, out view)))
                        {
                            ExpandView(schema, view!);
                            Current.Add(new Scope(
                                schema,
                                name,
                                named.Alias,
                                view!.Columns ?? DescribeViewColumns(view),
                                IsBaseTable: false));
                            return named;
                        }

                        Declare(schema, name, named.Alias);
                        return named;
                    }

                case JoinTableSource join:
                    return join with
                    {
                        Left = Source(join.Left),
                        Right = Source(join.Right),
                        Condition = join.Condition is null ? null : Expr(join.Condition),
                    };

                case DerivedTableSource derived:
                    {
                        var query = Query(derived.Query);
                        Current.Add(new Scope(null, derived.Alias ?? string.Empty, derived.Alias, [], IsBaseTable: false));
                        return derived with { Query = query };
                    }

                case TableValuedFunctionSource function:
                    return function with
                    {
                        Arguments = [.. function.Arguments.Select(Expr)],
                    };

                default:
                    return source;
            }
        }

        private void ExpandView(string? schema, ViewDefinition view)
        {
            if (_depth >= MaximumExpansionDepth || !_expanding.Add(view.Name))
                return;

            var previousOrigin = _origin;
            _origin = view.Name;
            _depth++;
            try
            {
                _ = Query(view.Query);
            }
            finally
            {
                _depth--;
                _origin = previousOrigin;
                _expanding.Remove(view.Name);
            }

            _ = schema;
        }

        private static IReadOnlyList<string> DescribeViewColumns(ViewDefinition view)
            => view.Query is SelectStatement select
                ? select.Projections
                    .Select(projection => projection.Alias
                        ?? (projection.Expression is ColumnExpression column ? column.Name : null))
                    .Where(name => name is not null)
                    .Select(name => name!)
                    .ToArray()
                : [];

        private IReadOnlyList<Projection>? Projections(IReadOnlyList<Projection>? projections)
        {
            if (projections is null)
                return null;

            var rewritten = new List<Projection>(projections.Count);
            var changed = false;
            foreach (var projection in projections)
            {
                if (projection.Expression is StarExpression or QualifiedStarExpression)
                {
                    var qualifier = projection.Expression is QualifiedStarExpression qualified
                        ? qualified.Qualifier
                        : null;
                    if (TryExpandStar(projection, qualifier, rewritten))
                    {
                        changed = true;
                        continue;
                    }

                    rewritten.Add(projection);
                    continue;
                }

                var expression = Expr(projection.Expression);
                changed |= !ReferenceEquals(expression, projection.Expression);
                rewritten.Add(projection with { Expression = expression });
            }

            return changed ? rewritten : projections;
        }

        /// <summary>
        /// Reports a READ for every column a <c>*</c> covers. The star is only expanded into
        /// explicit projections when at least one column was ignored, so an all-allowed query
        /// keeps the original shape and the engine's own star expansion.
        /// </summary>
        private bool TryExpandStar(Projection projection, string? qualifier, List<Projection> target)
        {
            var scopes = Scopes()
                .Where(scope => scope.IsBaseTable
                    && (qualifier is null || Matches(scope, qualifier)))
                .ToArray();
            if (scopes.Length == 0)
                return false;

            var expanded = new List<Projection>();
            var ignored = false;
            foreach (var scope in scopes)
            {
                foreach (var column in scope.Columns)
                {
                    var decision = Ask(SqliteAuthorizerAction.Read, scope.Name, column, scope.Schema ?? "main");
                    if (decision == SqliteAuthorizerResult.Deny)
                        throw Denied(SqliteAuthorizerAction.Read, scope.Name, column);

                    ignored |= decision == SqliteAuthorizerResult.Ignore;
                    expanded.Add(new Projection(
                        decision == SqliteAuthorizerResult.Ignore
                            ? new LiteralExpression(SqlValue.Null)
                            : new ColumnExpression(column, scope.Alias ?? scope.Name),
                        column));
                }
            }

            if (!ignored)
                return false;

            _ = projection;
            target.AddRange(expanded);
            return true;
        }

        private IReadOnlyList<OrderByTerm>? OrderBy(IReadOnlyList<OrderByTerm>? terms)
            => terms?.Select(term => term with { Expression = Expr(term.Expression) }).ToArray();

        // ---------------------------------------------------------------- expressions

        private Expression Expr(Expression expression)
        {
            switch (expression)
            {
                case ColumnExpression column:
                    return Column(column);

                case FunctionExpression function:
                    {
                        var decision = Ask(SqliteAuthorizerAction.Function, null, function.Name, null);
                        if (decision == SqliteAuthorizerResult.Deny)
                            throw Denied(SqliteAuthorizerAction.Function, null, function.Name);
                        if (decision == SqliteAuthorizerResult.Ignore)
                            return new LiteralExpression(SqlValue.Null);

                        var arguments = function.Arguments.Select(Expr).ToArray();
                        return function with
                        {
                            Arguments = arguments,
                            Filter = function.Filter is null ? null : Expr(function.Filter),
                            Window = function.Window is null ? null : Window(function.Window),
                            AggregateOrderBy = OrderBy(function.AggregateOrderBy),
                            OrderedSetOrderBy = function.OrderedSetOrderBy is null
                                ? null
                                : function.OrderedSetOrderBy with
                                {
                                    Expression = arguments[0],
                                },
                        };
                    }

                case ScalarSubqueryExpression scalar:
                    return scalar with { Query = Subquery(scalar.Query) };
                case ExistsExpression exists:
                    return exists with { Query = Subquery(exists.Query) };
                case InSubqueryExpression inSubquery:
                    return inSubquery with
                    {
                        Value = Expr(inSubquery.Value),
                        Query = Subquery(inSubquery.Query),
                    };

                case BinaryExpression binary:
                    return binary with { Left = Expr(binary.Left), Right = Expr(binary.Right) };
                case UnaryExpression unary:
                    return unary with { Operand = Expr(unary.Operand) };
                case CollationExpression collation:
                    return collation with { Expression = Expr(collation.Expression) };
                case CastExpression cast:
                    return cast with { Expression = Expr(cast.Expression) };
                case CaseExpression caseExpression:
                    return caseExpression with
                    {
                        Operand = caseExpression.Operand is null ? null : Expr(caseExpression.Operand),
                        Clauses = caseExpression.Clauses
                            .Select(clause => clause with { When = Expr(clause.When), Then = Expr(clause.Then) })
                            .ToArray(),
                        Else = caseExpression.Else is null ? null : Expr(caseExpression.Else),
                    };
                case LikeExpression like:
                    return like with
                    {
                        Value = Expr(like.Value),
                        Pattern = Expr(like.Pattern),
                        Escape = like.Escape is null ? null : Expr(like.Escape),
                    };
                case GlobExpression glob:
                    return glob with { Value = Expr(glob.Value), Pattern = Expr(glob.Pattern) };
                case InExpression inExpression:
                    return inExpression with
                    {
                        Value = Expr(inExpression.Value),
                        Values = inExpression.Values.Select(Expr).ToArray(),
                    };
                case BetweenExpression between:
                    return between with
                    {
                        Value = Expr(between.Value),
                        Lower = Expr(between.Lower),
                        Upper = Expr(between.Upper),
                    };
                case RowValueExpression rowValue:
                    return rowValue with { Values = rowValue.Values.Select(Expr).ToArray() };

                default:
                    return expression;
            }
        }

        private WindowSpecification Window(WindowSpecification window)
            => window with
            {
                PartitionBy = window.PartitionBy.Select(Expr).ToArray(),
                OrderBy = OrderBy(window.OrderBy) ?? [],
            };

        private QueryStatement Subquery(QueryStatement query)
        {
            PushFrame();
            try
            {
                return Query(query);
            }
            finally
            {
                PopFrame();
            }
        }

        private Expression Column(ColumnExpression column)
        {
            // NEW/OLD references inside a trigger body are row values, not table reads.
            if (column.Qualifier is "NEW" or "new" or "OLD" or "old")
                return column;

            var scope = Resolve(column);
            if (scope is null)
                return column;

            var decision = Ask(SqliteAuthorizerAction.Read, scope.Name, column.Name, scope.Schema ?? "main");
            if (decision == SqliteAuthorizerResult.Deny)
                throw Denied(SqliteAuthorizerAction.Read, scope.Name, column.Name);

            // SQLITE_IGNORE makes the column read as NULL wherever it appears, including in a
            // WHERE clause, rather than failing the statement.
            return decision == SqliteAuthorizerResult.Ignore
                ? new LiteralExpression(SqlValue.Null)
                : column;
        }

        private Scope? Resolve(ColumnExpression column)
        {
            if (column.Qualifier is { } qualifier)
            {
                foreach (var scope in Scopes())
                {
                    if (Matches(scope, qualifier))
                        return scope.IsBaseTable ? scope : null;
                }

                return null;
            }

            foreach (var scope in Scopes())
            {
                if (!scope.IsBaseTable)
                {
                    // A derived table or CTE that exposes this name shadows the outer tables.
                    if (scope.Columns.Any(name => string.Equals(name, column.Name, StringComparison.OrdinalIgnoreCase)))
                        return null;
                    continue;
                }

                if (scope.Columns.Any(name => string.Equals(name, column.Name, StringComparison.OrdinalIgnoreCase)))
                    return scope;
            }

            return null;
        }

        private static bool Matches(Scope scope, string qualifier)
            => string.Equals(scope.Alias ?? scope.Name, qualifier, StringComparison.OrdinalIgnoreCase)
                || string.Equals(scope.Name, qualifier, StringComparison.OrdinalIgnoreCase);

        // ---------------------------------------------------------------- scopes

        private List<Scope> Current => _frames[^1];

        private IEnumerable<Scope> Scopes()
        {
            for (var frame = _frames.Count - 1; frame >= 0; frame--)
            {
                foreach (var scope in _frames[frame])
                    yield return scope;
            }
        }

        private void PushFrame() => _frames.Add([]);

        private void PopFrame() => _frames.RemoveAt(_frames.Count - 1);

        private void Declare(string? schema, string name, string? alias = null)
        {
            var catalog = Catalog(schema);
            if (!catalog.Tables.TryGetValue(name, out var table)
                && (schema is not null || !main.Tables.TryGetValue(name, out table)))
            {
                return;
            }

            Current.Add(new Scope(schema, name, alias, table!.Columns, IsBaseTable: true));
        }

        // ---------------------------------------------------------------- helpers

        private EmbeddedDatabase.SchemaCatalog Catalog(string? schema)
            => IsTemp(schema) ? temp : main;

        private static bool IsTemp(string? schema)
            => string.Equals(schema, "temp", StringComparison.OrdinalIgnoreCase);

        private static (string? Schema, string Name) Split(string qualifiedName)
            => ManagedSchemaName.TrySplit(qualifiedName, out var schema, out var name)
                ? (schema, name)
                : (null, name);

        private static string StripSchema(string qualifiedName) => Split(qualifiedName).Name;

        private SqliteAuthorizerResult Ask(
            SqliteAuthorizerAction action,
            string? argument0,
            string? argument1,
            string? database)
            => authorizer(new SqliteAuthorizerContext(action, argument0, argument1, database, _origin));

        private void Require(
            SqliteAuthorizerAction action,
            string? argument0,
            string? argument1,
            string? database)
        {
            if (Ask(action, argument0, argument1, database) == SqliteAuthorizerResult.Deny)
                throw Denied(action, argument0, argument1);
        }

        private static EmbeddedAuthorizationDeniedException Denied(
            SqliteAuthorizerAction action,
            string? argument0,
            string? argument1)
            => action == SqliteAuthorizerAction.Read && argument0 is not null && argument1 is not null
                ? new EmbeddedAuthorizationDeniedException($"access to {argument0}.{argument1} is prohibited")
                : new EmbeddedAuthorizationDeniedException("not authorized");
    }
}
