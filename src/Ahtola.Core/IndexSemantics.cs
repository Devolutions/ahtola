using Ahtola.Core.Parsing;
using Ahtola.Core.Storage;

namespace Ahtola.Core;

internal static class EmbeddedIndexFactory
{
    public static EmbeddedIndex Create(
        string tableName,
        EmbeddedTable table,
        CreateIndexStatement statement)
    {
        if (statement.Columns.Count == 0)
            throw new EmbeddedSqlException($"Index '{statement.Name}' has no key columns.");

        var columns = new EmbeddedIndexColumn[statement.Columns.Count];
        for (var position = 0; position < statement.Columns.Count; position++)
        {
            var term = statement.Columns[position];
            if (!term.IsExpression)
            {
                var name = term.Name
                    ?? throw new EmbeddedSqlException($"Index '{statement.Name}' has an invalid column term.");
                var columnIndex = Array.FindIndex(
                    table.Columns,
                    candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));
                if (columnIndex < 0)
                    throw new EmbeddedSqlException($"no such column: {name}");

                columns[position] = new EmbeddedIndexColumn(
                    table.Columns[columnIndex],
                    columnIndex,
                    term.Collation ?? table.ColumnDefinitions[columnIndex].Collation,
                    term.Descending,
                    MethodParameters: term.MethodParameters);
                continue;
            }

            var expression = term.Expression
                ?? throw new EmbeddedSqlException($"Index '{statement.Name}' has an invalid expression term.");

            // SQLite/Turso backwards-compat quirk: a standalone string literal as an index
            // term is interpreted as a column name, not a string constant (turso-src
            // core/translate/index.rs resolve_index_column). Parentheses are already peeled
            // at parse time, so a deeply wrapped literal still lands here.
            if (expression is LiteralExpression { Value.Kind: SqlValueKind.Text } literal)
            {
                var literalColumn = literal.Value.AsText();
                var literalColumnIndex = Array.FindIndex(
                    table.Columns,
                    candidate => string.Equals(candidate, literalColumn, StringComparison.OrdinalIgnoreCase));
                if (literalColumnIndex < 0)
                    throw new EmbeddedSqlException($"no such column: {literalColumn}");

                columns[position] = new EmbeddedIndexColumn(
                    table.Columns[literalColumnIndex],
                    literalColumnIndex,
                    term.Collation ?? table.ColumnDefinitions[literalColumnIndex].Collation,
                    term.Descending,
                    MethodParameters: term.MethodParameters);
                continue;
            }

            var expressionSql = term.ExpressionSql;
            if (string.IsNullOrWhiteSpace(expressionSql))
                throw new EmbeddedSqlException($"Index '{statement.Name}' has an unreconstructable expression term.");

            columns[position] = new EmbeddedIndexColumn(
                expressionSql,
                -2,
                term.Collation,
                term.Descending,
                expression,
                expressionSql,
                term.MethodParameters);
        }

        var definition = new EmbeddedIndex(
            statement.Name,
            statement.Unique,
            columns,
            Where: statement.Where,
            WhereSql: statement.WhereSql,
            Sql: statement.Sql,
            Method: statement.Method,
            MethodParameters: statement.MethodParameters);
        IndexExpressionSemantics.ValidateDefinition(tableName, table, definition);
        if (definition.IsMethodIndex)
            ManagedIndexMethodSemantics.ValidateDefinition(tableName, table, definition);
        return definition;
    }
}

internal static class IndexSqlFormatter
{
    public static string BuildCreateIndexSql(string tableName, EmbeddedIndex index)
    {
        var terms = index.Columns.Select(term =>
        {
            var definition = term.IsExpression
                ? term.ExpressionSql
                    ?? throw new EmbeddedSqlException(
                        $"Index '{index.Name}' has an unreconstructable expression term.")
                : SqlIdentifierFormatter.QuoteIfNeeded(term.Name);
            if (!term.IsExpression && term.Collation is { } collation)
                definition += " COLLATE " + FormatIdentifier(collation);
            if (term.Descending)
                definition += " DESC";
            if (index.Method is not null && term.MethodParameters is { Count: > 0 })
            {
                definition += " WITH ("
                    + Indexing.ManagedIndexMethodParameterFormatter.Format(term.MethodParameters)
                    + ")";
            }
            return definition;
        });
        var unique = index.Unique ? "UNIQUE " : string.Empty;
        var where = index.Where is null
            ? string.Empty
            : " WHERE " + (index.WhereSql
                ?? throw new EmbeddedSqlException(
                    $"Partial index '{index.Name}' has an unreconstructable WHERE predicate."));
        var using_ = index.Method is null
            ? string.Empty
            : " USING " + SqlIdentifierFormatter.QuoteIfNeeded(index.Method);
        var with = index.Method is null || index.MethodParameters is not { Count: > 0 }
            ? string.Empty
            : " WITH (" + Indexing.ManagedIndexMethodParameterFormatter.Format(index.MethodParameters) + ")";
        return $"CREATE {unique}INDEX {SqlIdentifierFormatter.QuoteIfNeeded(index.Name)} ON {SqlIdentifierFormatter.QuoteIfNeeded(tableName)}{using_} "
            + $"({string.Join(", ", terms)}){with}{where}";
    }

    private static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string FormatIdentifier(string identifier)
        => identifier.Length > 0
            && (char.IsAsciiLetter(identifier[0]) || identifier[0] == '_')
            && identifier.Skip(1).All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
                ? identifier
                : QuoteIdentifier(identifier);
}

internal static class IndexExpressionSemantics
{
    public static void ValidateRoundTrip(
        string tableName,
        EmbeddedTable table,
        EmbeddedIndex index)
    {
        if (index.Origin != EmbeddedIndexOrigin.Explicit)
            return;

        var sql = IndexSqlFormatter.BuildCreateIndexSql(tableName, index);
        if (SqlParser.Parse(sql, SqlParameterMap.Parse(sql)) is not CreateIndexStatement statement)
            throw new EmbeddedSqlException($"Index '{index.Name}' cannot be reconstructed.");
        var reconstructed = EmbeddedIndexFactory.Create(tableName, table, statement);
        if (index.Unique != reconstructed.Unique
            || index.Columns.Count != reconstructed.Columns.Count
            || !string.Equals(index.Method, reconstructed.Method, StringComparison.OrdinalIgnoreCase)
            || !MethodParametersEqual(index.MethodParameters, reconstructed.MethodParameters)
            || !ExpressionsEqual(index.Where, reconstructed.Where))
        {
            throw new EmbeddedSqlException($"Index '{index.Name}' cannot be reconstructed losslessly.");
        }

        for (var position = 0; position < index.Columns.Count; position++)
        {
            var left = index.Columns[position];
            var right = reconstructed.Columns[position];
            if (left.IsExpression != right.IsExpression
                || left.Descending != right.Descending
                || !string.Equals(
                    GetCollationName(table, left),
                    GetCollationName(table, right),
                    StringComparison.OrdinalIgnoreCase)
                || !MethodParametersEqual(left.MethodParameters, right.MethodParameters)
                || (left.IsExpression
                    ? !ExpressionsEqual(left.Expression, right.Expression)
                    : left.ColumnIndex != right.ColumnIndex))
            {
                throw new EmbeddedSqlException($"Index '{index.Name}' cannot be reconstructed losslessly.");
            }
        }
    }

    public static void ValidateDefinition(
        string tableName,
        EmbeddedTable table,
        EmbeddedIndex index)
    {
        if (string.IsNullOrWhiteSpace(index.Name))
            throw new EmbeddedSqlException($"Cannot create an unnamed index on table '{tableName}'.");
        if (index.Columns.Count == 0)
            throw new EmbeddedSqlException($"Index '{index.Name}' has no key columns.");

        foreach (var term in index.Columns)
        {
            if (term.IsExpression)
            {
                if (term.ColumnIndex != -2 || string.IsNullOrWhiteSpace(term.ExpressionSql))
                {
                    throw new EmbeddedSqlException(
                        $"Index '{index.Name}' has inconsistent expression metadata.");
                }

                ValidateExpression(tableName, table, term.Expression!, "index expressions");
            }
            else
            {
                if (term.ColumnIndex < 0 || term.ColumnIndex >= table.Columns.Length)
                {
                    throw new EmbeddedSqlException(
                        $"Index '{index.Name}' has an invalid column reference.");
                }
                if (!string.Equals(
                        table.Columns[term.ColumnIndex],
                        term.Name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new EmbeddedSqlException(
                        $"Index '{index.Name}' has inconsistent column metadata.");
                }
            }

            ValidateCollation(index.Name, GetCollationName(table, term));
        }

        if ((index.Where is null) != (index.WhereSql is null))
        {
            throw new EmbeddedSqlException(
                $"Partial index '{index.Name}' has unreconstructable predicate metadata.");
        }
        if (index.Where is not null)
            ValidateExpression(tableName, table, index.Where, "partial index WHERE clauses");
    }

    public static bool Qualifies(
        EmbeddedIndex index,
        EmbeddedTable table,
        SqlValue[] row,
        long? rowId,
        Func<Expression, EmbeddedTable, SqlValue[], long?, SqlValue> evaluate)
        => index.Where is null || EmbeddedDatabase.IsTrue(evaluate(index.Where, table, row, rowId));

    public static SqlValue[] ProjectKey(
        EmbeddedIndex index,
        EmbeddedTable table,
        SqlValue[] row,
        long? rowId,
        Func<Expression, EmbeddedTable, SqlValue[], long?, SqlValue> evaluate)
    {
        var key = new SqlValue[index.Columns.Count];
        for (var position = 0; position < index.Columns.Count; position++)
        {
            var term = index.Columns[position];
            key[position] = term.IsExpression
                ? evaluate(term.Expression!, table, row, rowId)
                : row[term.ColumnIndex];
        }

        return key;
    }

    public static string GetCollationName(EmbeddedTable table, EmbeddedIndexColumn term)
        => term.Collation
            ?? (term.IsExpression ? null : table.ColumnDefinitions[term.ColumnIndex].Collation)
            ?? "BINARY";

    internal static bool MethodParametersEqual(
        IReadOnlyList<Indexing.ManagedIndexMethodParameter>? left,
        IReadOnlyList<Indexing.ManagedIndexMethodParameter>? right)
    {
        if ((left is null || left.Count == 0) && (right is null || right.Count == 0))
            return true;
        if (left is null || right is null || left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index].Name, right[index].Name, StringComparison.OrdinalIgnoreCase)
                || !left[index].Value.Equals(right[index].Value))
            {
                return false;
            }
        }

        return true;
    }

    public static bool ExpressionsEqual(Expression? left, Expression? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left is null || right is null || left.GetType() != right.GetType())
            return false;

        return (left, right) switch
        {
            (LiteralExpression a, LiteralExpression b) => a.Value.Equals(b.Value),
            (CurrentTimeExpression a, CurrentTimeExpression b) => a.Kind == b.Kind,
            (ParameterExpression a, ParameterExpression b) => a.Index == b.Index,
            (ColumnExpression a, ColumnExpression b) => string.Equals(
                a.UnqualifiedName ?? a.Name,
                b.UnqualifiedName ?? b.Name,
                StringComparison.OrdinalIgnoreCase),
            (FunctionExpression a, FunctionExpression b) =>
                string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
                && a.CountStar == b.CountStar
                && a.Distinct == b.Distinct
                && ExpressionsEqual(a.Filter, b.Filter)
                && WindowsEqual(a.Window, b.Window)
                && ExpressionListsEqual(a.Arguments, b.Arguments),
            (CollationExpression a, CollationExpression b) =>
                string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
                && ExpressionsEqual(a.Expression, b.Expression),
            (CastExpression a, CastExpression b) =>
                string.Equals(a.TypeName, b.TypeName, StringComparison.OrdinalIgnoreCase)
                && ExpressionsEqual(a.Expression, b.Expression),
            (CaseExpression a, CaseExpression b) =>
                ExpressionsEqual(a.Operand, b.Operand)
                && CaseClausesEqual(a.Clauses, b.Clauses)
                && ExpressionsEqual(a.Else, b.Else),
            (LikeExpression a, LikeExpression b) =>
                a.Negated == b.Negated
                && ExpressionsEqual(a.Value, b.Value)
                && ExpressionsEqual(a.Pattern, b.Pattern)
                && ExpressionsEqual(a.Escape, b.Escape),
            (GlobExpression a, GlobExpression b) =>
                a.Negated == b.Negated
                && ExpressionsEqual(a.Value, b.Value)
                && ExpressionsEqual(a.Pattern, b.Pattern),
            (InExpression a, InExpression b) =>
                a.Negated == b.Negated
                && ExpressionsEqual(a.Value, b.Value)
                && ExpressionListsEqual(a.Values, b.Values),
            (BetweenExpression a, BetweenExpression b) =>
                a.Negated == b.Negated
                && ExpressionsEqual(a.Value, b.Value)
                && ExpressionsEqual(a.Lower, b.Lower)
                && ExpressionsEqual(a.Upper, b.Upper),
            (UnaryExpression a, UnaryExpression b) =>
                a.Operator == b.Operator && ExpressionsEqual(a.Operand, b.Operand),
            (BinaryExpression a, BinaryExpression b) =>
                a.Operator == b.Operator
                && ExpressionsEqual(a.Left, b.Left)
                && ExpressionsEqual(a.Right, b.Right),
            (StarExpression, StarExpression) => true,
            (QualifiedStarExpression a, QualifiedStarExpression b) =>
                string.Equals(a.Qualifier, b.Qualifier, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    public static bool PredicateImplies(
        Expression? queryPredicate,
        Expression? indexPredicate,
        string tableName,
        string? alias)
    {
        if (indexPredicate is null)
            return true;
        if (queryPredicate is null)
            return false;

        var queryTerms = SplitConjuncts(queryPredicate);
        foreach (var required in SplitConjuncts(indexPredicate))
        {
            if (!queryTerms.Any(candidate =>
                    UsesOnlySourceColumns(candidate, tableName, alias)
                    && PredicateTermsEqual(candidate, required)))
                return false;
        }

        return true;
    }

    private static bool UsesOnlySourceColumns(
        Expression expression,
        string tableName,
        string? alias)
    {
        return expression switch
        {
            ColumnExpression { Qualifier: null } => true,
            ColumnExpression column => string.Equals(
                column.Qualifier,
                alias ?? tableName,
                StringComparison.OrdinalIgnoreCase),
            FunctionExpression function => function.Arguments.All(argument =>
                    UsesOnlySourceColumns(argument, tableName, alias))
                && (function.Filter is null
                    || UsesOnlySourceColumns(function.Filter, tableName, alias)),
            CollationExpression collation => UsesOnlySourceColumns(
                collation.Expression,
                tableName,
                alias),
            CastExpression cast => UsesOnlySourceColumns(cast.Expression, tableName, alias),
            CaseExpression @case => (@case.Operand is null
                    || UsesOnlySourceColumns(@case.Operand, tableName, alias))
                && @case.Clauses.All(clause =>
                    UsesOnlySourceColumns(clause.When, tableName, alias)
                    && UsesOnlySourceColumns(clause.Then, tableName, alias))
                && (@case.Else is null
                    || UsesOnlySourceColumns(@case.Else, tableName, alias)),
            LikeExpression like => UsesOnlySourceColumns(like.Value, tableName, alias)
                && UsesOnlySourceColumns(like.Pattern, tableName, alias)
                && (like.Escape is null
                    || UsesOnlySourceColumns(like.Escape, tableName, alias)),
            GlobExpression glob => UsesOnlySourceColumns(glob.Value, tableName, alias)
                && UsesOnlySourceColumns(glob.Pattern, tableName, alias),
            InExpression @in => UsesOnlySourceColumns(@in.Value, tableName, alias)
                && @in.Values.All(value => UsesOnlySourceColumns(value, tableName, alias)),
            InSubqueryExpression or ScalarSubqueryExpression or ExistsExpression => false,
            BetweenExpression between => UsesOnlySourceColumns(between.Value, tableName, alias)
                && UsesOnlySourceColumns(between.Lower, tableName, alias)
                && UsesOnlySourceColumns(between.Upper, tableName, alias),
            UnaryExpression unary => UsesOnlySourceColumns(unary.Operand, tableName, alias),
            BinaryExpression binary => UsesOnlySourceColumns(binary.Left, tableName, alias)
                && UsesOnlySourceColumns(binary.Right, tableName, alias),
            RowValueExpression rowValue => rowValue.Values.All(value =>
                UsesOnlySourceColumns(value, tableName, alias)),
            _ => true,
        };
    }

    public static bool PredicateTermsEqual(Expression candidate, Expression required)
    {
        if (ExpressionsEqual(candidate, required))
            return true;

        return candidate is BinaryExpression { Operator: BinaryOperator.Equal } candidateEqual
            && required is BinaryExpression { Operator: BinaryOperator.Equal } requiredEqual
            && ExpressionsEqual(candidateEqual.Left, requiredEqual.Right)
            && ExpressionsEqual(candidateEqual.Right, requiredEqual.Left);
    }

    public static bool ContainsFunction(
        Expression expression,
        Func<string, int, bool> predicate)
    {
        switch (expression)
        {
            case FunctionExpression function:
                if (predicate(function.Name, function.Arguments.Count))
                    return true;
                return function.Arguments.Any(argument => ContainsFunction(argument, predicate))
                    || function.Filter is not null && ContainsFunction(function.Filter, predicate);
            case CollationExpression collation:
                return ContainsFunction(collation.Expression, predicate);
            case CastExpression cast:
                return ContainsFunction(cast.Expression, predicate);
            case CaseExpression @case:
                return @case.Operand is not null && ContainsFunction(@case.Operand, predicate)
                    || @case.Clauses.Any(clause =>
                        ContainsFunction(clause.When, predicate)
                        || ContainsFunction(clause.Then, predicate))
                    || @case.Else is not null && ContainsFunction(@case.Else, predicate);
            case LikeExpression like:
                return ContainsFunction(like.Value, predicate)
                    || ContainsFunction(like.Pattern, predicate)
                    || like.Escape is not null && ContainsFunction(like.Escape, predicate);
            case GlobExpression glob:
                return ContainsFunction(glob.Value, predicate)
                    || ContainsFunction(glob.Pattern, predicate);
            case InExpression @in:
                return ContainsFunction(@in.Value, predicate)
                    || @in.Values.Any(value => ContainsFunction(value, predicate));
            case InSubqueryExpression @in:
                return ContainsFunction(@in.Value, predicate);
            case BetweenExpression between:
                return ContainsFunction(between.Value, predicate)
                    || ContainsFunction(between.Lower, predicate)
                    || ContainsFunction(between.Upper, predicate);
            case UnaryExpression unary:
                return ContainsFunction(unary.Operand, predicate);
            case BinaryExpression binary:
                return ContainsFunction(binary.Left, predicate)
                    || ContainsFunction(binary.Right, predicate);
            default:
                return false;
        }
    }

    private static void ValidateExpression(
        string tableName,
        EmbeddedTable table,
        Expression expression,
        string context)
    {
        switch (expression)
        {
            case LiteralExpression:
                return;
            case CurrentTimeExpression:
                throw new EmbeddedSqlException($"non-deterministic functions are prohibited in {context}");
            case ColumnExpression column:
                if (!IsIndexTableColumn(tableName, column))
                    throw new EmbeddedSqlException($"the \".\" operator is prohibited in {context}");
                var name = column.UnqualifiedName ?? column.Name;
                var columnIndex = Array.FindIndex(
                    table.Columns,
                    candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));
                if (columnIndex < 0)
                {
                    // A bare TRUE/FALSE keyword that matches no column is the integer literal 1/0.
                    if (column.BooleanKeyword is not null)
                        return;

                    throw new EmbeddedSqlException($"no such column: {name}");
                }
                if (table.ColumnDefinitions[columnIndex].Collation is { } columnCollation)
                    ValidateCollation("expression", columnCollation);
                return;
            case ParameterExpression:
                throw new EmbeddedSqlException($"parameters are prohibited in {context}");
            case ScalarSubqueryExpression or ExistsExpression or InSubqueryExpression:
                throw new EmbeddedSqlException($"subqueries are prohibited in {context}");
            case StarExpression or QualifiedStarExpression:
                throw new EmbeddedSqlException($"cannot use '*' in {context}");
            case FunctionExpression function:
                if (function.Window is not null
                    || function.Filter is not null
                    || function.Distinct
                    || !IsDeterministicBuiltin(function))
                {
                    throw new EmbeddedSqlException(
                        $"non-deterministic functions are prohibited in {context}");
                }
                foreach (var argument in function.Arguments)
                    ValidateExpression(tableName, table, argument, context);
                return;
            case CollationExpression collation:
                ValidateCollation("expression", collation.Name);
                ValidateExpression(tableName, table, collation.Expression, context);
                return;
            case CastExpression cast:
                ValidateExpression(tableName, table, cast.Expression, context);
                return;
            case CaseExpression @case:
                if (@case.Operand is not null)
                    ValidateExpression(tableName, table, @case.Operand, context);
                foreach (var clause in @case.Clauses)
                {
                    ValidateExpression(tableName, table, clause.When, context);
                    ValidateExpression(tableName, table, clause.Then, context);
                }
                if (@case.Else is not null)
                    ValidateExpression(tableName, table, @case.Else, context);
                return;
            case LikeExpression like:
                ValidateExpression(tableName, table, like.Value, context);
                ValidateExpression(tableName, table, like.Pattern, context);
                if (like.Escape is not null)
                    ValidateExpression(tableName, table, like.Escape, context);
                return;
            case GlobExpression glob:
                ValidateExpression(tableName, table, glob.Value, context);
                ValidateExpression(tableName, table, glob.Pattern, context);
                return;
            case InExpression @in:
                ValidateExpression(tableName, table, @in.Value, context);
                foreach (var value in @in.Values)
                    ValidateExpression(tableName, table, value, context);
                return;
            case BetweenExpression between:
                ValidateExpression(tableName, table, between.Value, context);
                ValidateExpression(tableName, table, between.Lower, context);
                ValidateExpression(tableName, table, between.Upper, context);
                return;
            case UnaryExpression unary:
                ValidateExpression(tableName, table, unary.Operand, context);
                return;
            case BinaryExpression binary:
                ValidateExpression(tableName, table, binary.Left, context);
                ValidateExpression(tableName, table, binary.Right, context);
                return;
            default:
                throw new EmbeddedSqlException($"expression is prohibited in {context}");
        }
    }

    private static bool IsIndexTableColumn(string tableName, ColumnExpression column)
    {
        if (column.Qualifier is null)
            return true;

        var localTableName = ManagedSchemaName.TrySplit(tableName, out _, out var localName)
            ? localName
            : tableName;
        return string.Equals(column.Qualifier, localTableName, StringComparison.OrdinalIgnoreCase)
            && (column.Schema is null
                || string.Equals(column.Schema, "main", StringComparison.OrdinalIgnoreCase)
                || string.Equals(column.Schema, "temp", StringComparison.OrdinalIgnoreCase));
    }

    // Mirrors Turso's registry-driven check (Func::resolve_function(name, argc)
    // .is_deterministic()): an index-expression call is usable when it resolves to
    // a built-in *scalar* implementation that is deterministic. Aggregate and
    // window implementations never qualify (MIN/MAX resolve to scalars only with
    // two or more arguments), and the non-deterministic built-in set is excluded
    // by the registry lookup.
    private static bool IsDeterministicBuiltin(FunctionExpression function)
    {
        if (!SqliteBuiltinFunctions.AcceptsCall(
                function.Name,
                function.Arguments.Count,
                function.CountStar)
            || EmbeddedDatabase.IsBuiltInAggregate(function)
            || EmbeddedDatabase.IsManagedPercentileAggregate(function.Name)
            || SqliteBuiltinFunctions.IsWindowOnly(function.Name))
        {
            return false;
        }

        // Date/time functions are deterministic only when they cannot read the
        // wall clock or the local timezone (mirrors generated-column validation).
        return EmbeddedTable.IsDeterministicSchemaFunction(function);
    }

    private static void ValidateCollation(string indexName, string name)
    {
        var collation = SqliteKeyCollation.FromName(name);
        if (!collation.IsSupportedByManagedIndexWriter)
        {
            throw new EmbeddedSqlException(
                $"Index '{indexName}' uses application-defined collation '{name}', "
                + "which is not a supported SQLite built-in collation.");
        }
    }

    public static IReadOnlyList<Expression> SplitConjuncts(Expression expression)
    {
        var terms = new List<Expression>();
        Add(expression, terms);
        return terms;

        static void Add(Expression candidate, List<Expression> output)
        {
            if (candidate is BinaryExpression { Operator: BinaryOperator.And } and)
            {
                Add(and.Left, output);
                Add(and.Right, output);
            }
            else
            {
                output.Add(candidate);
            }
        }
    }

    private static bool ExpressionListsEqual(
        IReadOnlyList<Expression> left,
        IReadOnlyList<Expression> right)
    {
        if (left.Count != right.Count)
            return false;
        for (var index = 0; index < left.Count; index++)
        {
            if (!ExpressionsEqual(left[index], right[index]))
                return false;
        }

        return true;
    }

    private static bool CaseClausesEqual(
        IReadOnlyList<CaseClause> left,
        IReadOnlyList<CaseClause> right)
    {
        if (left.Count != right.Count)
            return false;
        for (var index = 0; index < left.Count; index++)
        {
            if (!ExpressionsEqual(left[index].When, right[index].When)
                || !ExpressionsEqual(left[index].Then, right[index].Then))
            {
                return false;
            }
        }

        return true;
    }

    private static bool WindowsEqual(WindowSpecification? left, WindowSpecification? right)
        => left is null && right is null;
}
