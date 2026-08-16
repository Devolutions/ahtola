using System.Globalization;
using Ahtola.Core.Search;
using Ahtola.Core.Spatial;

namespace Ahtola.Core;

internal sealed class ManagedFts5Module : ManagedVirtualTableModule
{
    public static ManagedFts5Module Instance { get; } = new();

    public override string Name => "fts5";

    public override ManagedVirtualTable Create(ManagedVirtualTableCreateContext context)
        => new ManagedFts5Table(ParseColumnNames(context));

    private static IReadOnlyList<string> ParseColumnNames(ManagedVirtualTableCreateContext context)
    {
        if (context.Arguments.Count == 0)
            throw new EmbeddedSqlException("fts5 requires at least one column");

        var columns = new List<string>(context.Arguments.Count);
        foreach (var argument in context.Arguments)
        {
            var column = argument.Trim();
            if (column.Length == 0 || column.Contains('=') || !IsIdentifier(column))
                throw new EmbeddedSqlException($"unsupported fts5 module argument: {argument}");
            if (columns.Contains(column, StringComparer.OrdinalIgnoreCase))
                throw new EmbeddedSqlException($"duplicate fts5 column name: {column}");

            columns.Add(column);
        }

        return columns;
    }

    private static bool IsIdentifier(string value)
        => value.All(static character => char.IsLetterOrDigit(character) || character == '_')
            && !char.IsDigit(value[0]);
}

internal sealed class ManagedRTreeModule : ManagedRTreeModuleBase
{
    public static ManagedRTreeModule Instance { get; } = new();

    private ManagedRTreeModule() : base("rtree", requireIntegerCoordinates: false) { }
}

internal sealed class ManagedRTreeI32Module : ManagedRTreeModuleBase
{
    public static ManagedRTreeI32Module Instance { get; } = new();

    private ManagedRTreeI32Module() : base("rtree_i32", requireIntegerCoordinates: true) { }
}

internal abstract class ManagedRTreeModuleBase(string name, bool requireIntegerCoordinates) : ManagedVirtualTableModule
{
    private readonly bool _requireIntegerCoordinates = requireIntegerCoordinates;

    public override string Name { get; } = name;

    public override ManagedVirtualTable Create(ManagedVirtualTableCreateContext context)
    {
        if (context.Arguments.Count < 3 || context.Arguments.Count % 2 == 0)
        {
            throw new EmbeddedSqlException(
                $"{Name} requires an id column followed by a min/max pair for every dimension");
        }

        var columns = context.Arguments.Select(static argument => argument.Trim()).ToArray();
        if (columns.Any(static column => column.Length == 0 || !IsIdentifier(column)))
            throw new EmbeddedSqlException($"{Name} module arguments must be column names");
        if (columns.Select(static column => column).Distinct(StringComparer.OrdinalIgnoreCase).Count() != columns.Length)
            throw new EmbeddedSqlException($"{Name} module column names must be unique");

        return new ManagedRTreeTable(columns, _requireIntegerCoordinates);
    }

    private static bool IsIdentifier(string value)
        => value.All(static character => char.IsLetterOrDigit(character) || character == '_')
            && !char.IsDigit(value[0]);
}

internal sealed class ManagedFts5Table : ManagedVirtualTable
{
    private const int MatchPlan = 1;

    private readonly string[] _columnNames;
    private readonly ManagedVirtualTableSchema _schema;
    private readonly Dictionary<long, SqlValue[]> _rows = [];
    private readonly ManagedFtsIndex _index = new();
    private Dictionary<long, SqlValue[]>? _transactionSnapshot;
    private long? _transactionNextRowId;
    private long _nextRowId = 1;

    public ManagedFts5Table(IReadOnlyList<string> columnNames)
    {
        _columnNames = [.. columnNames];
        _schema = new ManagedVirtualTableSchema(
            _columnNames.Select(static name => new ManagedVirtualTableColumn(
                name,
                ManagedVirtualTableAffinity.Text)));
    }

    public override ManagedVirtualTableSchema Schema => _schema;

    public override ManagedVirtualTablePlan BestIndex(
        IReadOnlyList<ManagedVirtualTableConstraint> constraints,
        IReadOnlyList<ManagedVirtualTableOrderBy> orderBy)
    {
        var usages = new ManagedVirtualTableConstraintUsage[constraints.Count];
        for (var index = 0; index < usages.Length; index++)
        {
            if (constraints[index].Usable
                && constraints[index].Operator == ManagedVirtualTableConstraintOperator.Match)
            {
                usages[index] = new ManagedVirtualTableConstraintUsage(1, Omit: true);
                return new ManagedVirtualTablePlan(
                    usages,
                    indexNumber: MatchPlan,
                    indexString: "fts5-match",
                    estimatedCost: 10,
                    estimatedRows: Math.Max(1, _rows.Count / 4));
            }

            usages[index] = ManagedVirtualTableConstraintUsage.Unused;
        }

        return new ManagedVirtualTablePlan(usages, estimatedCost: Math.Max(1, _rows.Count), estimatedRows: _rows.Count);
    }

    public override ManagedVirtualTableCursor Open() => new Cursor(_rows, _index);

    public override long? Update(IReadOnlyList<SqlValue> arguments)
    {
        ValidateUpdateArguments(arguments, _schema.Columns.Count);
        var oldRowId = ReadRowId(arguments[0], "old rowid");
        var newRowId = ReadRowId(arguments[1], "new rowid");
        if (oldRowId is not null && newRowId is null)
        {
            Remove(oldRowId.Value);
            return null;
        }

        var rowId = newRowId ?? oldRowId ?? _nextRowId++;
        if (oldRowId is { } previous && previous != rowId)
            Remove(previous);

        var values = arguments.Skip(2).Take(_columnNames.Length).ToArray();
        _rows[rowId] = values;
        _index.Upsert(rowId, values.Select(ToText).ToArray());
        _nextRowId = Math.Max(_nextRowId, checked(rowId + 1));
        return rowId;
    }

    public override void Begin()
    {
        if (_transactionSnapshot is not null)
            return;

        _transactionSnapshot = CloneRows(_rows);
        _transactionNextRowId = _nextRowId;
    }

    public override void Commit()
    {
        _transactionSnapshot = null;
        _transactionNextRowId = null;
    }

    public override void Rollback()
    {
        if (_transactionSnapshot is null)
            return;

        _rows.Clear();
        foreach (var (rowId, values) in _transactionSnapshot)
            _rows.Add(rowId, [.. values]);
        RebuildIndex();
        _transactionSnapshot = null;
        _nextRowId = _transactionNextRowId!.Value;
        _transactionNextRowId = null;
    }

    private void Remove(long rowId)
    {
        _rows.Remove(rowId);
        _index.Remove(rowId);
    }

    private void RebuildIndex()
    {
        _index.Clear();
        foreach (var (rowId, values) in _rows)
            _index.Upsert(rowId, values.Select(ToText).ToArray());
    }

    private static Dictionary<long, SqlValue[]> CloneRows(IReadOnlyDictionary<long, SqlValue[]> source)
        => source.ToDictionary(static entry => entry.Key, static entry => entry.Value.ToArray());

    private static string? ToText(SqlValue value)
        => value.Kind switch
        {
            SqlValueKind.Null => null,
            SqlValueKind.Text => value.AsText(),
            SqlValueKind.Integer => value.AsInteger().ToString(CultureInfo.InvariantCulture),
            SqlValueKind.Real => value.AsReal().ToString("R", CultureInfo.InvariantCulture),
            SqlValueKind.Blob => System.Text.Encoding.UTF8.GetString(value.AsBlob().Span),
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private sealed class Cursor(
        IReadOnlyDictionary<long, SqlValue[]> rows,
        ManagedFtsIndex index) : ManagedVirtualTableCursor
    {
        private IReadOnlyList<KeyValuePair<long, SqlValue[]>> _matches = [];
        private int _position;

        public override bool Filter(ManagedVirtualTablePlan plan, IReadOnlyList<SqlValue> arguments)
        {
            _matches = plan.IndexNumber == MatchPlan
                ? MatchRows(arguments)
                : rows.OrderBy(static entry => entry.Key).ToArray();
            _position = 0;
            return _matches.Count != 0;
        }

        public override void Next() => _position++;

        public override bool Eof => _position >= _matches.Count;

        public override SqlValue Column(int columnIndex)
            => _matches[_position].Value[columnIndex];

        public override long RowId => _matches[_position].Key;

        private IReadOnlyList<KeyValuePair<long, SqlValue[]>> MatchRows(IReadOnlyList<SqlValue> arguments)
        {
            if (arguments.Count != 1 || arguments[0].Kind != SqlValueKind.Text)
                throw new EmbeddedSqlException("fts5 MATCH requires one text query");

            return index.Search(ManagedFtsQueryParser.Parse(arguments[0].AsText()))
                .Select(match => new KeyValuePair<long, SqlValue[]>(match.RowId, rows[match.RowId]))
                .ToArray();
        }
    }

    internal static void ValidateUpdateArguments(IReadOnlyList<SqlValue> arguments, int columnCount)
    {
        if (arguments.Count != columnCount + 2)
            throw new EmbeddedSqlException($"virtual table update expected {columnCount + 2} values");
    }

    internal static long? ReadRowId(SqlValue value, string name)
        => value.Kind switch
        {
            SqlValueKind.Null => null,
            SqlValueKind.Integer => value.AsInteger(),
            _ => throw new EmbeddedSqlException($"{name} must be an integer or NULL"),
        };
}

internal sealed class ManagedRTreeTable : ManagedVirtualTable
{
    private const int ConstraintPlan = 1;

    private readonly bool _requireIntegerCoordinates;
    private readonly ManagedVirtualTableSchema _schema;
    private readonly Dictionary<long, ManagedRTreeBounds> _rows = [];
    private ManagedRTreeIndex _index = new();
    private Dictionary<long, ManagedRTreeBounds>? _transactionSnapshot;
    private long? _transactionNextRowId;
    private long _nextRowId = 1;

    public ManagedRTreeTable(IReadOnlyList<string> columns, bool requireIntegerCoordinates)
    {
        _requireIntegerCoordinates = requireIntegerCoordinates;
        _schema = new ManagedVirtualTableSchema(columns.Select((name, index) => new ManagedVirtualTableColumn(
            name,
            index == 0
                ? ManagedVirtualTableAffinity.Integer
                : requireIntegerCoordinates
                    ? ManagedVirtualTableAffinity.Integer
                    : ManagedVirtualTableAffinity.Real)));
    }

    public override ManagedVirtualTableSchema Schema => _schema;

    public override ManagedVirtualTablePlan BestIndex(
        IReadOnlyList<ManagedVirtualTableConstraint> constraints,
        IReadOnlyList<ManagedVirtualTableOrderBy> orderBy)
    {
        var usages = new ManagedVirtualTableConstraintUsage[constraints.Count];
        var encodedConstraints = new List<string>();
        var argumentIndex = 1;
        for (var index = 0; index < constraints.Count; index++)
        {
            var constraint = constraints[index];
            if (constraint.Usable
                && constraint.ColumnIndex >= 1
                && constraint.ColumnIndex < _schema.Columns.Count
                && IsRangeOperator(constraint.Operator))
            {
                usages[index] = new ManagedVirtualTableConstraintUsage(argumentIndex++, Omit: true);
                encodedConstraints.Add($"{constraint.ColumnIndex}:{(int)constraint.Operator}");
            }
            else
            {
                usages[index] = ManagedVirtualTableConstraintUsage.Unused;
            }
        }

        return new ManagedVirtualTablePlan(
            usages,
            indexNumber: encodedConstraints.Count == 0 ? 0 : ConstraintPlan,
            indexString: encodedConstraints.Count == 0 ? null : string.Join(';', encodedConstraints),
            estimatedCost: encodedConstraints.Count == 0 ? Math.Max(1, _rows.Count) : Math.Max(1, _rows.Count / 4d),
            estimatedRows: encodedConstraints.Count == 0 ? _rows.Count : Math.Max(1, _rows.Count / 4));
    }

    public override ManagedVirtualTableCursor Open() => new Cursor(_index, _schema.Columns.Count, _requireIntegerCoordinates);

    public override long? Update(IReadOnlyList<SqlValue> arguments)
    {
        ManagedFts5Table.ValidateUpdateArguments(arguments, _schema.Columns.Count);
        var oldRowId = ManagedFts5Table.ReadRowId(arguments[0], "old rowid");
        var newRowId = ManagedFts5Table.ReadRowId(arguments[1], "new rowid");
        if (oldRowId is not null && newRowId is null)
        {
            Remove(oldRowId.Value);
            return null;
        }

        var suppliedId = ManagedFts5Table.ReadRowId(arguments[2], "rtree id");
        var rowId = newRowId ?? suppliedId ?? oldRowId ?? _nextRowId++;
        if (suppliedId is not null && suppliedId != rowId)
            throw new EmbeddedSqlException("rtree id must match rowid");
        if (oldRowId is { } previous && previous != rowId)
            Remove(previous);

        var coordinates = new double[_schema.Columns.Count - 1];
        for (var index = 0; index < coordinates.Length; index++)
            coordinates[index] = ToCoordinate(arguments[index + 3]);

        var bounds = new ManagedRTreeBounds(coordinates);
        _rows[rowId] = bounds;
        _index.Upsert(rowId, bounds);
        _nextRowId = Math.Max(_nextRowId, checked(rowId + 1));
        return rowId;
    }

    public override void Begin()
    {
        if (_transactionSnapshot is not null)
            return;

        _transactionSnapshot = new Dictionary<long, ManagedRTreeBounds>(_rows);
        _transactionNextRowId = _nextRowId;
    }

    public override void Commit()
    {
        _transactionSnapshot = null;
        _transactionNextRowId = null;
    }

    public override void Rollback()
    {
        if (_transactionSnapshot is null)
            return;

        _rows.Clear();
        foreach (var entry in _transactionSnapshot)
            _rows.Add(entry.Key, entry.Value);
        RebuildIndex();
        _transactionSnapshot = null;
        _nextRowId = _transactionNextRowId!.Value;
        _transactionNextRowId = null;
    }

    private static bool IsRangeOperator(ManagedVirtualTableConstraintOperator value)
        => value is ManagedVirtualTableConstraintOperator.Equal
            or ManagedVirtualTableConstraintOperator.GreaterThan
            or ManagedVirtualTableConstraintOperator.GreaterThanOrEqual
            or ManagedVirtualTableConstraintOperator.LessThan
            or ManagedVirtualTableConstraintOperator.LessThanOrEqual;

    private double ToCoordinate(SqlValue value)
    {
        if (_requireIntegerCoordinates)
        {
            if (value.Kind != SqlValueKind.Integer)
                throw new EmbeddedSqlException("rtree_i32 coordinates must be integers");

            var integerCoordinate = value.AsInteger();
            if (integerCoordinate is < int.MinValue or > int.MaxValue)
                throw new EmbeddedSqlException("rtree_i32 coordinates must fit in a signed 32-bit integer");
            return integerCoordinate;
        }

        var coordinate = value.Kind switch
        {
            SqlValueKind.Integer => value.AsInteger(),
            SqlValueKind.Real => value.AsReal(),
            _ => throw new EmbeddedSqlException("rtree coordinates must be numeric"),
        };
        if (!double.IsFinite(coordinate))
            throw new EmbeddedSqlException("rtree coordinates must be finite");
        return coordinate;
    }

    private void Remove(long rowId)
    {
        _rows.Remove(rowId);
        _index.Remove(rowId);
    }

    private void RebuildIndex()
    {
        _index = new ManagedRTreeIndex();
        foreach (var (rowId, bounds) in _rows)
            _index.Upsert(rowId, bounds);
    }

    private sealed class Cursor(
        ManagedRTreeIndex index,
        int columnCount,
        bool requireIntegerCoordinates) : ManagedVirtualTableCursor
    {
        private IReadOnlyList<KeyValuePair<long, ManagedRTreeBounds>> _matches = [];
        private int _position;

        public override bool Filter(ManagedVirtualTablePlan plan, IReadOnlyList<SqlValue> arguments)
        {
            _matches = index.Snapshot();
            if (plan.IndexNumber == ConstraintPlan)
                ApplyConstraints(plan, arguments);
            _position = 0;
            return _matches.Count != 0;
        }

        public override void Next() => _position++;

        public override bool Eof => _position >= _matches.Count;

        public override SqlValue Column(int columnIndex)
        {
            if (columnIndex == 0)
                return SqlValue.Integer(RowId);
            if (columnIndex < 0 || columnIndex >= columnCount)
                throw new ArgumentOutOfRangeException(nameof(columnIndex));

            var value = _matches[_position].Value.Minimum((columnIndex - 1) / 2);
            if (columnIndex % 2 == 0)
                value = _matches[_position].Value.Maximum((columnIndex - 1) / 2);

            return requireIntegerCoordinates
                ? SqlValue.Integer(checked((long)value))
                : SqlValue.Real(value);
        }

        public override long RowId => _matches[_position].Key;

        private void ApplyConstraints(ManagedVirtualTablePlan plan, IReadOnlyList<SqlValue> arguments)
        {
            var encoded = plan.IndexString?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [];
            if (encoded.Length != arguments.Count)
                throw new EmbeddedSqlException("rtree filter argument count does not match the selected plan");

            for (var index = 0; index < encoded.Length; index++)
            {
                var parts = encoded[index].Split(':');
                if (parts.Length != 2
                    || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var column)
                    || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var operatorValue))
                {
                    throw new EmbeddedSqlException("invalid rtree filter plan");
                }

                var value = ToFilterCoordinate(arguments[index]);
                var dimension = (column - 1) / 2;
                var minimum = column % 2 == 1;
                var @operator = (ManagedVirtualTableConstraintOperator)operatorValue;
                _matches = _matches.Where(row => Compare(
                    minimum ? row.Value.Minimum(dimension) : row.Value.Maximum(dimension),
                    @operator,
                    value)).ToArray();
            }
        }

        private double ToFilterCoordinate(SqlValue value)
        {
            if (requireIntegerCoordinates)
            {
                if (value.Kind != SqlValueKind.Integer)
                    throw new EmbeddedSqlException("rtree_i32 filter coordinates must be integers");

                var coordinate = value.AsInteger();
                if (coordinate is < int.MinValue or > int.MaxValue)
                    throw new EmbeddedSqlException("rtree_i32 filter coordinates must fit in a signed 32-bit integer");
                return coordinate;
            }

            return value.Kind switch
            {
                SqlValueKind.Integer => value.AsInteger(),
                SqlValueKind.Real => value.AsReal(),
                _ => throw new EmbeddedSqlException("rtree filter coordinates must be numeric"),
            };
        }

        private static bool Compare(
            double left,
            ManagedVirtualTableConstraintOperator @operator,
            double right)
            => @operator switch
            {
                ManagedVirtualTableConstraintOperator.Equal => left == right,
                ManagedVirtualTableConstraintOperator.GreaterThan => left > right,
                ManagedVirtualTableConstraintOperator.GreaterThanOrEqual => left >= right,
                ManagedVirtualTableConstraintOperator.LessThan => left < right,
                ManagedVirtualTableConstraintOperator.LessThanOrEqual => left <= right,
                _ => throw new EmbeddedSqlException("unsupported rtree filter operator"),
            };
    }
}
