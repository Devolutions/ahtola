using System.Collections.Generic;

namespace Ahtola.Core;

public sealed partial class EmbeddedDatabase
{
    private SqlValue EvaluateStringAgg(
        FunctionExpression function,
        IReadOnlyList<SourceRow> rows,
        SqlValue[] parameters,
        QueryContext context)
    {
        // string_agg is group_concat with a mandatory separator.
        RequireAggregateArgumentCount("string_agg", function.Arguments, 2);
        return EvaluateGroupConcat(function, rows, parameters, context);
    }

    private SqlValue EvaluateArrayAgg(
        FunctionExpression function,
        IReadOnlyList<SourceRow> rows,
        SqlValue[] parameters,
        QueryContext context)
    {
        RequireAggregateArgumentCount("array_agg", function.Arguments, 1);
        if (rows.Count == 0)
            return SqlValue.Null;

        // Turso's AggFunc::ArrayAgg builds an ImmutableRecord from every input value, including
        // NULLs. ImmutableRecord uses SQLite's record payload format, so the blob remains usable
        // by Turso's array functions and other record-aware consumers.
        var values = new SqlValue[rows.Count];
        for (var index = 0; index < rows.Count; index++)
        {
            context.CheckInterrupt();
            values[index] = Evaluate(function.Arguments[0], parameters, rows[index], context);
        }

        return SqlValue.Blob(Storage.SqliteRecordCodec.Encode(values));
    }

    private SqlValue EvaluateJsonGroupArray(
        FunctionExpression function,
        IReadOnlyList<SourceRow> rows,
        SqlValue[] parameters,
        QueryContext context,
        bool binary = false)
    {
        RequireAggregateArgumentCount(binary ? "jsonb_group_array" : "json_group_array", function.Arguments, 1);

        // Unlike group_concat, json_group_array keeps NULL rows as JSON nulls.
        var items = new List<SqlValue>(rows.Count);
        foreach (var row in rows)
        {
            context.CheckInterrupt();
            items.Add(Evaluate(function.Arguments[0], parameters, row, context));
        }

        var result = SqliteJson.JsonArray(items);
        return binary ? SqliteJson.ToJsonb(result) : result;
    }

    private SqlValue EvaluateJsonGroupObject(
        FunctionExpression function,
        IReadOnlyList<SourceRow> rows,
        SqlValue[] parameters,
        QueryContext context,
        bool binary = false)
    {
        RequireAggregateArgumentCount(binary ? "jsonb_group_object" : "json_group_object", function.Arguments, 2);

        var members = new List<SqlValue>(checked(rows.Count * 2));
        foreach (var row in rows)
        {
            context.CheckInterrupt();
            var name = Evaluate(function.Arguments[0], parameters, row, context);
            var value = Evaluate(function.Arguments[1], parameters, row, context);

            // Labels are coerced to text so a non-text name still produces a well-formed object.
            members.Add(SqlValue.Text(name.Kind == SqlValueKind.Null ? string.Empty : ToSqlText(name)));
            members.Add(value);
        }

        var result = SqliteJson.JsonObject(members);
        return binary ? SqliteJson.ToJsonb(result) : result;
    }
}
