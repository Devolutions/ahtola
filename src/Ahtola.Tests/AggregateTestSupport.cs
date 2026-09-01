using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// Shared aggregate semantics and comparers for the aggregate opcode, program-builder,
// and EXPLAIN tests. The delegates model the exact null/type contracts the executor
// relies on the caller to supply (COUNT ignoring NULLs, SUM of no rows being NULL,
// MIN/MAX ignoring NULLs), including inverse operations where the aggregate can remove
// a prior step without retaining the whole input.
internal static class AggregateTestSupport
{
    // COUNT(*): counts every row regardless of argument values; empty input yields 0.
    public static VdbeAggregate CountStar() => new()
    {
        Name = "count",
        CreateContext = () => 0L,
        Accumulate = (context, _) => (long)context! + 1L,
        Inverse = (context, _) => (long)context! - 1L,
        Finalize = context => SqlValue.Integer((long)context!),
    };

    // COUNT(x): counts rows whose argument is non-NULL; empty input yields 0.
    public static VdbeAggregate Count() => new()
    {
        Name = "count",
        CreateContext = () => 0L,
        Accumulate = (context, arguments) =>
            arguments[0].Kind == SqlValueKind.Null ? context : (long)context! + 1L,
        Inverse = (context, arguments) =>
            arguments[0].Kind == SqlValueKind.Null ? context : (long)context! - 1L,
        Finalize = context => SqlValue.Integer((long)context!),
    };

    // SUM(x): integer running total over non-NULL integers; no non-NULL value yields NULL.
    public static VdbeAggregate Sum() => new()
    {
        Name = "sum",
        CreateContext = () => new SumState(),
        Accumulate = (context, arguments) =>
        {
            var state = (SumState)context!;
            if (arguments[0].Kind == SqlValueKind.Integer)
            {
                state.Count++;
                state.Sum += arguments[0].AsInteger();
            }

            return state;
        },
        Inverse = (context, arguments) =>
        {
            var state = (SumState)context!;
            if (arguments[0].Kind == SqlValueKind.Integer)
            {
                state.Count--;
                state.Sum -= arguments[0].AsInteger();
            }

            return state;
        },
        Finalize = context =>
        {
            var state = (SumState)context!;
            return state.Count > 0 ? SqlValue.Integer(state.Sum) : SqlValue.Null;
        },
    };

    // AVG(x): mean of non-NULL integers as a real; no non-NULL value yields NULL.
    public static VdbeAggregate Avg() => new()
    {
        Name = "avg",
        CreateContext = () => new AvgState(),
        Accumulate = (context, arguments) =>
        {
            var state = (AvgState)context!;
            if (arguments[0].Kind == SqlValueKind.Integer)
            {
                state.Count++;
                state.Sum += arguments[0].AsInteger();
            }

            return state;
        },
        Inverse = (context, arguments) =>
        {
            var state = (AvgState)context!;
            if (arguments[0].Kind == SqlValueKind.Integer)
            {
                state.Count--;
                state.Sum -= arguments[0].AsInteger();
            }

            return state;
        },
        Finalize = context =>
        {
            var state = (AvgState)context!;
            return state.Count == 0 ? SqlValue.Null : SqlValue.Real(state.Sum / state.Count);
        },
    };

    // MIN(x): smallest non-NULL value; no non-NULL value yields NULL.
    public static VdbeAggregate Min() => new()
    {
        Name = "min",
        CreateContext = () => new ExtremumState(),
        Accumulate = (context, arguments) =>
        {
            var state = (ExtremumState)context!;
            var value = arguments[0];
            if (value.Kind != SqlValueKind.Null && (!state.HasValue || Compare(value, state.Value) < 0))
            {
                state.HasValue = true;
                state.Value = value;
            }

            return state;
        },
        Finalize = context =>
        {
            var state = (ExtremumState)context!;
            return state.HasValue ? state.Value : SqlValue.Null;
        },
    };

    // MAX(x): largest non-NULL value; no non-NULL value yields NULL.
    public static VdbeAggregate Max() => new()
    {
        Name = "max",
        CreateContext = () => new ExtremumState(),
        Accumulate = (context, arguments) =>
        {
            var state = (ExtremumState)context!;
            var value = arguments[0];
            if (value.Kind != SqlValueKind.Null && (!state.HasValue || Compare(value, state.Value) > 0))
            {
                state.HasValue = true;
                state.Value = value;
            }

            return state;
        },
        Finalize = context =>
        {
            var state = (ExtremumState)context!;
            return state.HasValue ? state.Value : SqlValue.Null;
        },
    };

    // Orders full scanned rows by the given group columns so groups sort contiguously,
    // placing NULL keys first (SQLite's default ascending NULL ordering).
    public static VdbeRowComparer OrderByColumns(params int[] columns) => (left, right) =>
    {
        foreach (var column in columns)
        {
            var order = Compare(left[column], right[column]);
            if (order != 0)
                return order;
        }

        return 0;
    };

    // Case-insensitive ordering of full scanned rows by a single text column.
    public static VdbeRowComparer OrderByTextNoCase(int column) => (left, right) =>
        string.Compare(left[column].AsText(), right[column].AsText(), StringComparison.OrdinalIgnoreCase);

    // Group equality over every key-tuple position; NULL keys group together because
    // SqlValue equality treats two NULLs as equal.
    public static VdbeGroupComparer GroupKeysEqual() => (left, right) =>
    {
        if (left.Length != right.Length)
            return false;

        for (var i = 0; i < left.Length; i++)
        {
            if (!left[i].Equals(right[i]))
                return false;
        }

        return true;
    };

    // Case-insensitive group equality over a single text key.
    public static VdbeGroupComparer GroupTextNoCase() => (left, right) =>
        string.Equals(left[0].AsText(), right[0].AsText(), StringComparison.OrdinalIgnoreCase);

    private static int Compare(SqlValue left, SqlValue right)
    {
        var leftNull = left.Kind == SqlValueKind.Null;
        var rightNull = right.Kind == SqlValueKind.Null;
        if (leftNull || rightNull)
            return leftNull == rightNull ? 0 : leftNull ? -1 : 1;

        if (left.Kind == SqlValueKind.Text && right.Kind == SqlValueKind.Text)
            return string.CompareOrdinal(left.AsText(), right.AsText());

        return ToDouble(left).CompareTo(ToDouble(right));
    }

    private static double ToDouble(SqlValue value) => value.Kind switch
    {
        SqlValueKind.Integer => value.AsInteger(),
        SqlValueKind.Real => value.AsReal(),
        _ => throw new InvalidOperationException($"Cannot order value of kind {value.Kind}."),
    };

    private sealed class SumState
    {
        public long Count;
        public long Sum;
    }

    private sealed class AvgState
    {
        public long Count;
        public double Sum;
    }

    private sealed class ExtremumState
    {
        public bool HasValue;
        public SqlValue Value;
    }
}
