using System.Globalization;
using Ahtola.Core.Parsing;

namespace Ahtola.Core;

public sealed partial class EmbeddedDatabase
{
    /// <summary>
    /// Runs one of the introspection PRAGMA statements on behalf of a
    /// <c>pragma_*</c> table-valued function so the statement form and the function form
    /// can never report different columns or rows.
    /// </summary>
    internal static ExecutionResult ExecuteIntrospectionPragma(
        ParsedStatement statement,
        QueryContext context)
    {
        var tables = context.Tables;
        return statement switch
        {
            PragmaTableInfoStatement tableInfo => ExecutePragmaTableInfo(tableInfo, context),
            PragmaTableXInfoStatement tableXInfo => ExecutePragmaTableXInfo(tableXInfo, context),
            PragmaIndexListStatement indexList => ExecutePragmaIndexList(indexList, tables),
            PragmaIndexInfoStatement indexInfo => ExecutePragmaIndexInfo(indexInfo, tables),
            PragmaIndexXInfoStatement indexXInfo => ExecutePragmaIndexXInfo(indexXInfo, tables),
            PragmaForeignKeyListStatement foreignKeyList => ExecutePragmaForeignKeyList(foreignKeyList, tables),
            PragmaTableListStatement tableList => ExecutePragmaTableList(
                new SchemaCatalog(
                    tables,
                    new Dictionary<string, ViewDefinition>(
                        context.Views ?? new Dictionary<string, ViewDefinition>(),
                        StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, TriggerDefinition>(
                        context.Triggers ?? new Dictionary<string, TriggerDefinition>(),
                        StringComparer.OrdinalIgnoreCase)),
                tableList.Schema ?? "main",
                tableList.Filter),
            _ => throw new EmbeddedSqlException(
                $"Unsupported introspection pragma {statement.GetType().Name}."),
        };
    }

    /// <summary>
    /// Column metadata for a registered table-valued function, so
    /// <c>PRAGMA table_info(json_each)</c> reports the module's declared columns the way
    /// SQLite reports a virtual table's declared columns.
    /// </summary>
    private static bool TryDescribeTableValuedFunction(
        string name,
        bool includeHidden,
        out SqlValue[][] rows)
    {
        if (!TableValuedFunctionRegistry.TryResolve(name, out var module))
        {
            rows = [];
            return false;
        }

        var schema = module.Schema;
        var columns = includeHidden ? schema.AllColumns : schema.VisibleColumns;
        var result = new SqlValue[columns.Count][];
        for (var index = 0; index < columns.Count; index++)
        {
            var row = new List<SqlValue>
            {
                SqlValue.Integer(index),
                SqlValue.Text(columns[index]),
                SqlValue.Text(string.Empty),
                SqlValue.Integer(0),
                SqlValue.Null,
                SqlValue.Integer(0),
            };
            if (includeHidden)
                row.Add(SqlValue.Integer(index >= schema.VisibleColumns.Count ? 1 : 0));
            result[index] = [.. row];
        }

        rows = result;
        return true;
    }

    /// <summary>
    /// Resolves the parenthesis-free spelling of a table-valued function and binds the
    /// module's hidden argument columns from WHERE equality terms, so
    /// <c>FROM pragma_table_info WHERE arg = 't'</c> is the same call as
    /// <c>FROM pragma_table_info('t')</c>, exactly as SQLite's eponymous virtual tables
    /// behave. A real table, view or common table expression always wins over a module
    /// registration, so registering a module can never shadow user data.
    /// </summary>
    private static SelectStatement BindTableValuedFunctionSources(SelectStatement statement, QueryContext context)
    {
        if (statement.Source is null)
            return statement;

        var source = BindBareTableValuedFunctions(statement.Source, context);
        if (statement.Where is not null)
        {
            var terms = new List<Expression>();
            CollectConjuncts(statement.Where, terms);
            source = BindTableValuedFunctionArguments(source, terms);
        }

        return ReferenceEquals(source, statement.Source) ? statement : statement with { Source = source };
    }

    private static TableSource BindBareTableValuedFunctions(TableSource source, QueryContext context)
    {
        switch (source)
        {
            case NamedTableSource named when TryBindBareTableValuedFunction(named, context, out var function):
                return function;
            case JoinTableSource join:
                var left = BindBareTableValuedFunctions(join.Left, context);
                var right = BindBareTableValuedFunctions(join.Right, context);
                return ReferenceEquals(left, join.Left) && ReferenceEquals(right, join.Right)
                    ? join
                    : join with { Left = left, Right = right };
            default:
                return source;
        }
    }

    private static bool TryBindBareTableValuedFunction(
        NamedTableSource named,
        QueryContext context,
        out TableValuedFunctionSource function)
    {
        function = null!;
        if (named.IndexDirective is not null
            || IsSchemaTable(named.Name)
            || context.Tables.ContainsKey(named.Name)
            || IsCommonTableExpression(named, context)
            || TryGetView(context, named.Name, out _))
        {
            return false;
        }

        var qualified = ManagedSchemaName.TrySplit(named.Name, out var schema, out var name);
        if (!TableValuedFunctionRegistry.IsRegistered(name))
            return false;

        function = new TableValuedFunctionSource(name, [], named.Alias, qualified ? schema : null);
        return true;
    }

    private static TableSource BindTableValuedFunctionArguments(TableSource source, IReadOnlyList<Expression> terms)
    {
        switch (source)
        {
            case TableValuedFunctionSource function:
                return BindHiddenArguments(function, terms);
            case JoinTableSource join:
                var left = BindTableValuedFunctionArguments(join.Left, terms);
                var right = BindTableValuedFunctionArguments(join.Right, terms);
                return ReferenceEquals(left, join.Left) && ReferenceEquals(right, join.Right)
                    ? join
                    : join with { Left = left, Right = right };
            default:
                return source;
        }
    }

    private static TableSource BindHiddenArguments(
        TableValuedFunctionSource function,
        IReadOnlyList<Expression> terms)
    {
        if (!TableValuedFunctionRegistry.TryResolve(function.Name, out var module))
            return function;

        var hidden = module.Schema.HiddenColumns;
        if (function.Arguments.Count >= hidden.Count)
            return function;

        var qualifier = function.Alias ?? function.Name;
        var bound = new Expression?[hidden.Count];
        var highest = -1;
        for (var index = function.Arguments.Count; index < hidden.Count; index++)
        {
            if (!TryFindHiddenArgument(terms, qualifier, hidden[index], out var value))
                continue;

            bound[index] = value;
            highest = index;
        }

        if (highest < 0)
            return function;

        // Arguments are positional, so an unbound slot below a bound one becomes an explicit
        // NULL. Every module already treats a NULL argument as "not supplied".
        var arguments = new List<Expression>(function.Arguments);
        for (var index = function.Arguments.Count; index <= highest; index++)
            arguments.Add(bound[index] ?? new LiteralExpression(SqlValue.Null));

        return function with { Arguments = arguments };
    }

    private static bool TryFindHiddenArgument(
        IReadOnlyList<Expression> terms,
        string qualifier,
        string column,
        out Expression value)
    {
        foreach (var term in terms)
        {
            if (term is not BinaryExpression { Operator: BinaryOperator.Equal } equality)
                continue;

            if (IsHiddenColumnReference(equality.Left, qualifier, column)
                && IsConstantArgument(equality.Right))
            {
                value = equality.Right;
                return true;
            }

            if (IsHiddenColumnReference(equality.Right, qualifier, column)
                && IsConstantArgument(equality.Left))
            {
                value = equality.Left;
                return true;
            }
        }

        value = null!;
        return false;
    }

    private static bool IsHiddenColumnReference(Expression expression, string qualifier, string column)
        => expression is ColumnExpression reference
            && string.Equals(reference.UnqualifiedName ?? reference.Name, column, StringComparison.OrdinalIgnoreCase)
            && (reference.Qualifier is null
                || string.Equals(reference.Qualifier, qualifier, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Only argument expressions that cannot reference a row are bound, so binding never
    /// changes when a constraint is evaluated.
    /// </summary>
    private static bool IsConstantArgument(Expression expression)
        => expression switch
        {
            LiteralExpression => true,
            ParameterExpression => true,
            UnaryExpression unary => IsConstantArgument(unary.Operand),
            _ => false,
        };

    private static void CollectConjuncts(Expression expression, List<Expression> terms)
    {
        if (expression is BinaryExpression { Operator: BinaryOperator.And } conjunction)
        {
            CollectConjuncts(conjunction.Left, terms);
            CollectConjuncts(conjunction.Right, terms);
            return;
        }

        terms.Add(expression);
    }

    /// <summary>
    /// Depth-first traversal used by the <c>json_each</c> and <c>json_tree</c> modules.
    /// </summary>
    internal static IReadOnlyList<SqlValue[]> TraverseJson(SqlValue json, string rootPath, bool recursive)
        => SqliteJson.Traverse(json, rootPath, recursive);

    internal static string RequireJsonRootPath(SqlValue value) => SqliteJson.RootPathOf(value);

    private static partial class SqliteJson
    {
        /// <summary>
        /// Produces the eight visible <c>json_each</c>/<c>json_tree</c> columns
        /// (<c>key, value, type, atom, id, parent, fullkey, path</c>) for one call.
        /// <paramref name="recursive"/> selects <c>json_tree</c>, which additionally emits a
        /// row for the root node itself and then descends depth-first in pre-order.
        /// </summary>
        internal static IReadOnlyList<SqlValue[]> Traverse(SqlValue json, string rootPath, bool recursive)
        {
            var document = ParseOrThrow(json);
            if (!TryNavigateForTraversal(document, rootPath, out var target, out var rootKey, out var parentPath))
                return [];

            var rows = new List<SqlValue[]>();
            if (recursive)
            {
                // json_tree numbers rows in emission order with the root at zero. SQLite emits
                // internal byte offsets here, which are not reproducible outside its own parser.
                var nextId = 0L;
                AppendTreeRows(target, rootKey, SqlValue.Null, rootPath, parentPath, ref nextId, rows);
            }
            else
            {
                var nextId = 1L;
                AppendEachRows(target, rootPath, ref nextId, rows);
            }

            return rows;
        }

        internal static string RootPathOf(SqlValue value) => RequirePathText(value);

        private static void AppendEachRows(JNode node, string rootPath, ref long nextId, List<SqlValue[]> rows)
        {
            switch (node.Kind)
            {
                case JKind.Array:
                    for (var index = 0; index < node.Items!.Count; index++)
                    {
                        rows.Add(BuildTraversalRow(
                            SqlValue.Integer(index),
                            node.Items[index],
                            nextId++,
                            SqlValue.Null,
                            rootPath + "[" + index.ToString(CultureInfo.InvariantCulture) + "]",
                            rootPath));
                    }

                    break;
                case JKind.Object:
                    foreach (var member in node.Members!)
                    {
                        rows.Add(BuildTraversalRow(
                            SqlValue.Text(member.Key),
                            member.Value,
                            nextId++,
                            SqlValue.Null,
                            rootPath + PathKeySuffix(member.RawKey),
                            rootPath));
                    }

                    break;
                default:
                    // A scalar root yields a single keyless row whose fullkey is the root itself.
                    rows.Add(BuildTraversalRow(
                        SqlValue.Null,
                        node,
                        nextId++,
                        SqlValue.Null,
                        rootPath,
                        rootPath));
                    break;
            }
        }

        private static void AppendTreeRows(
            JNode node,
            SqlValue key,
            SqlValue parentId,
            string fullKey,
            string path,
            ref long nextId,
            List<SqlValue[]> rows)
        {
            var id = nextId++;
            rows.Add(BuildTraversalRow(key, node, id, parentId, fullKey, path));
            var ownId = SqlValue.Integer(id);
            switch (node.Kind)
            {
                case JKind.Array:
                    for (var index = 0; index < node.Items!.Count; index++)
                    {
                        AppendTreeRows(
                            node.Items[index],
                            SqlValue.Integer(index),
                            ownId,
                            fullKey + "[" + index.ToString(CultureInfo.InvariantCulture) + "]",
                            fullKey,
                            ref nextId,
                            rows);
                    }

                    break;
                case JKind.Object:
                    foreach (var member in node.Members!)
                    {
                        AppendTreeRows(
                            member.Value,
                            SqlValue.Text(member.Key),
                            ownId,
                            fullKey + PathKeySuffix(member.RawKey),
                            fullKey,
                            ref nextId,
                            rows);
                    }

                    break;
            }
        }

        private static SqlValue[] BuildTraversalRow(
            SqlValue key,
            JNode node,
            long id,
            SqlValue parentId,
            string fullKey,
            string path)
        {
            var isContainer = node.Kind is JKind.Array or JKind.Object;
            var value = NodeToSql(node);
            return
            [
                key,
                value,
                SqlValue.Text(TypeName(node)),
                isContainer ? SqlValue.Null : value,
                SqlValue.Integer(id),
                parentId,
                SqlValue.Text(fullKey),
                SqlValue.Text(path),
            ];
        }

        /// <summary>
        /// Renders an object member as a path step. SQLite leaves the label unquoted only when
        /// it starts with an ASCII letter and continues with ASCII letters or digits; anything
        /// else keeps the verbatim JSON token, escapes included.
        /// </summary>
        private static string PathKeySuffix(string rawKey)
        {
            if (rawKey.Length > 2 && rawKey[0] == '"' && char.IsAsciiLetter(rawKey[1]))
            {
                var bare = true;
                for (var index = 2; index < rawKey.Length - 1; index++)
                {
                    if (!char.IsAsciiLetterOrDigit(rawKey[index]))
                    {
                        bare = false;
                        break;
                    }
                }

                if (bare)
                    return "." + rawKey[1..^1];
            }

            return "." + rawKey;
        }

        /// <summary>
        /// Resolves the root path like <see cref="Navigate"/> while also reporting the last
        /// path step, which <c>json_tree</c> uses as the root row's key and parent path.
        /// </summary>
        private static bool TryNavigateForTraversal(
            JNode root,
            string path,
            out JNode target,
            out SqlValue lastKey,
            out string parentPath)
        {
            target = root;
            lastKey = SqlValue.Null;
            parentPath = path;
            if (path.Length == 0 || path[0] != '$')
                throw BadPath(path);

            var current = root;
            var i = 1;
            var lastStepStart = -1;
            while (i < path.Length)
            {
                var stepStart = i;
                var c = path[i];
                if (c == '.')
                {
                    i++;
                    string keyName;
                    if (i < path.Length && path[i] == '"')
                    {
                        var parser = new Parser(path, i);
                        var strNode = parser.ParseString();
                        if (strNode is null)
                            throw BadPath(path);
                        i = parser.Pos;
                        keyName = strNode.Str;
                    }
                    else
                    {
                        var start = i;
                        while (i < path.Length && path[i] != '.' && path[i] != '[')
                            i++;
                        if (i == start)
                            throw BadPath(path);
                        keyName = path.Substring(start, i - start);
                    }

                    if (current.Kind != JKind.Object)
                        return false;

                    JNode? match = null;
                    foreach (var member in current.Members!)
                    {
                        if (string.Equals(member.Key, keyName, StringComparison.Ordinal))
                        {
                            match = member.Value;
                            break;
                        }
                    }

                    if (match is null)
                        return false;

                    current = match;
                    lastKey = SqlValue.Text(keyName);
                }
                else if (c == '[')
                {
                    if (current.Kind != JKind.Array)
                        return false;

                    i++;
                    long length = current.Items!.Count;
                    var fromEnd = false;
                    long value;
                    if (i < path.Length && path[i] == '#')
                    {
                        fromEnd = true;
                        i++;
                        if (i < path.Length && path[i] == '-')
                        {
                            i++;
                            if (!ReadDigits(path, ref i, out value))
                                throw BadPath(path);
                        }
                        else
                        {
                            value = 0;
                        }
                    }
                    else if (i < path.Length && char.IsAsciiDigit(path[i]))
                    {
                        if (!ReadDigits(path, ref i, out value))
                            throw BadPath(path);
                    }
                    else
                    {
                        throw BadPath(path);
                    }

                    if (i >= path.Length || path[i] != ']')
                        throw BadPath(path);
                    i++;

                    var actual = fromEnd ? length - value : value;
                    if (actual < 0 || actual >= length)
                        return false;

                    current = current.Items[(int)actual];
                    lastKey = SqlValue.Integer(actual);
                }
                else
                {
                    throw BadPath(path);
                }

                lastStepStart = stepStart;
            }

            target = current;
            parentPath = lastStepStart < 0 ? path : path[..lastStepStart];
            return true;
        }
    }
}
