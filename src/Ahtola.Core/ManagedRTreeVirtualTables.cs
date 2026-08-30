using System.Globalization;
using Ahtola.Core.Execution;
using Ahtola.Core.Parsing;
using Ahtola.Core.Spatial;

namespace Ahtola.Core;

internal sealed class ManagedRTreeModule : ManagedRTreeModuleBase
{
    public static ManagedRTreeModule Instance { get; } = new();

    private ManagedRTreeModule() : base("rtree", integerCoordinates: false) { }
}

internal sealed class ManagedRTreeI32Module : ManagedRTreeModuleBase
{
    public static ManagedRTreeI32Module Instance { get; } = new();

    private ManagedRTreeI32Module() : base("rtree_i32", integerCoordinates: true) { }
}

internal abstract class ManagedRTreeModuleBase(string name, bool integerCoordinates) : ManagedVirtualTableModule
{
    private const int MaximumColumns = 100;
    private const int MaximumDimensions = 5;

    public override string Name { get; } = name;

    public override ManagedVirtualTable Create(ManagedVirtualTableCreateContext context)
    {
        var definition = ParseDefinition(context.Arguments);
        return new ManagedRTreeTable(context.TableName, definition, integerCoordinates);
    }

    public override ManagedVirtualTable Create(
        ManagedVirtualTableCreateContext context,
        ManagedVirtualTablePersistencePayload payload)
    {
        var table = (ManagedRTreeTable)Create(context);
        table.RestorePersistencePayload(payload);
        return table;
    }

    private static ManagedRTreeDefinition ParseDefinition(IReadOnlyList<string> arguments)
    {
        if (arguments.Count < 3)
            throw new EmbeddedSqlException("Too few columns for an rtree table");
        if (arguments.Count > MaximumColumns)
            throw new EmbeddedSqlException("Too many columns for an rtree table");

        var columns = new List<string>(arguments.Count);
        var auxiliaryColumns = 0;
        var sawAuxiliary = false;
        foreach (var argument in arguments)
        {
            var (column, auxiliary) = ParseColumnArgument(argument);
            if (auxiliary)
            {
                sawAuxiliary = true;
                auxiliaryColumns++;
            }
            else if (sawAuxiliary)
            {
                throw new EmbeddedSqlException("Auxiliary rtree columns must be last");
            }

            if (columns.Any(existing => string.Equals(existing, column, StringComparison.OrdinalIgnoreCase)))
                throw new EmbeddedSqlException($"duplicate column name: {column}");
            columns.Add(column);
        }

        var coordinateColumns = columns.Count - 1 - auxiliaryColumns;
        if (coordinateColumns < 2)
            throw new EmbeddedSqlException("Too few columns for an rtree table");
        if (coordinateColumns > MaximumDimensions * 2)
            throw new EmbeddedSqlException("Too many columns for an rtree table");
        if ((coordinateColumns & 1) != 0)
            throw new EmbeddedSqlException("Wrong number of columns for an rtree table");

        return new ManagedRTreeDefinition(columns.ToArray(), coordinateColumns / 2, auxiliaryColumns);
    }

    private static (string Name, bool Auxiliary) ParseColumnArgument(string argument)
    {
        var span = argument.AsSpan().Trim();
        var auxiliary = false;
        if (!span.IsEmpty && span[0] == '+')
        {
            auxiliary = true;
            span = span[1..].TrimStart();
        }

        if (span.IsEmpty)
            throw new EmbeddedSqlException("near \")\": syntax error");

        string name;
        if (span[0] is '"' or '\'' or '`' or '[')
        {
            var opening = span[0];
            var closing = opening == '[' ? ']' : opening;
            var builder = new System.Text.StringBuilder();
            var index = 1;
            var closed = false;
            while (index < span.Length)
            {
                var character = span[index++];
                if (character != closing)
                {
                    builder.Append(character);
                    continue;
                }

                if (index < span.Length && span[index] == closing)
                {
                    builder.Append(closing);
                    index++;
                    continue;
                }

                closed = true;
                break;
            }

            if (!closed)
                throw new EmbeddedSqlException("unterminated quoted identifier");
            name = builder.ToString();
        }
        else
        {
            var length = 0;
            while (length < span.Length
                   && !char.IsWhiteSpace(span[length])
                   && span[length] is not '(' and not ')' and not ',')
            {
                length++;
            }
            if (length == 0)
                throw new EmbeddedSqlException($"invalid rtree column declaration: {argument}");
            name = span[..length].ToString();
        }

        if (name.Length == 0)
            throw new EmbeddedSqlException("zero-length delimited identifier");
        return (name, auxiliary);
    }
}

internal sealed record ManagedRTreeDefinition(string[] Columns, int Dimensions, int AuxiliaryColumns)
{
    public int CoordinateColumns => Dimensions * 2;
}

internal sealed class ManagedRTreeTable : ManagedVirtualTable
{
    private const int FullScanPlan = 0;
    private const int RowIdPlan = 1;
    private const int CoordinatePlan = 2;
    private const int PersistenceVersion = 2;
    private const double RoundTowardsZero = 1.0 - (1.0 / 8_388_608.0);
    private const double RoundAwayFromZero = 1.0 + (1.0 / 8_388_608.0);

    private readonly bool _integerCoordinates;
    private readonly ManagedRTreeDefinition _definition;
    private readonly ManagedVirtualTableSchema _schema;
    private readonly Dictionary<long, Row> _rows = [];
    private ManagedRTreeIndex _index = new();
    private Dictionary<long, Row>? _transactionSnapshot;
    private string _tableName;

    public ManagedRTreeTable(
        string tableName,
        ManagedRTreeDefinition definition,
        bool integerCoordinates)
    {
        _tableName = tableName;
        _definition = definition;
        _integerCoordinates = integerCoordinates;
        _schema = new ManagedVirtualTableSchema(definition.Columns.Select((column, index) =>
        {
            if (index == 0)
                return new ManagedVirtualTableColumn(column, ManagedVirtualTableAffinity.Integer, DeclaredType: "INT");
            if (index <= definition.CoordinateColumns)
            {
                return new ManagedVirtualTableColumn(
                    column,
                    integerCoordinates ? ManagedVirtualTableAffinity.Integer : ManagedVirtualTableAffinity.Real,
                    DeclaredType: integerCoordinates ? "INT" : "REAL");
            }

            return new ManagedVirtualTableColumn(column, DeclaredType: string.Empty);
        }));
    }

    public override ManagedVirtualTableSchema Schema => _schema;

    public override ManagedVirtualTablePlan BestIndex(
        IReadOnlyList<ManagedVirtualTableConstraint> constraints,
        IReadOnlyList<ManagedVirtualTableOrderBy> orderBy)
    {
        var usages = new ManagedVirtualTableConstraintUsage[constraints.Count];
        var encoded = new List<string>();
        var argumentIndex = 1;
        var hasRowIdEquality = false;
        for (var index = 0; index < constraints.Count; index++)
        {
            var constraint = constraints[index];
            if (!constraint.Usable
                || !IsSupportedOperator(constraint.Operator)
                || constraint.ColumnIndex > _definition.CoordinateColumns)
            {
                usages[index] = ManagedVirtualTableConstraintUsage.Unused;
                continue;
            }

            // -1 is SQLite's planner column number for the rowid pseudo-column. Column 0 is
            // the declared id and is the authoritative alias of that same key.
            if (constraint.ColumnIndex < -1)
            {
                usages[index] = ManagedVirtualTableConstraintUsage.Unused;
                continue;
            }

            usages[index] = new ManagedVirtualTableConstraintUsage(argumentIndex++, Omit: true);
            encoded.Add($"{constraint.ColumnIndex}:{(int)constraint.Operator}");
            hasRowIdEquality |= constraint.ColumnIndex is -1 or 0
                && constraint.Operator is (ManagedVirtualTableConstraintOperator.Equal
                    or ManagedVirtualTableConstraintOperator.Is);
        }

        var indexNumber = encoded.Count == 0
            ? FullScanPlan
            : hasRowIdEquality
                ? RowIdPlan
                : CoordinatePlan;
        var estimatedRows = hasRowIdEquality
            ? Math.Min(1, _rows.Count)
            : encoded.Count == 0
                ? _rows.Count
                : Math.Max(1, _rows.Count / 4);
        return new ManagedVirtualTablePlan(
            usages,
            indexNumber,
            encoded.Count == 0 ? null : string.Join(';', encoded),
            estimatedCost: hasRowIdEquality ? 1 : Math.Max(1, estimatedRows),
            estimatedRows: estimatedRows);
    }

    public override ManagedVirtualTableCursor Open()
        => new Cursor(_rows, _index, _definition, _integerCoordinates);

    public override long? Update(IReadOnlyList<SqlValue> arguments)
        => Update(arguments, ManagedVirtualTableConflictMode.Abort).RowId;

    public override ManagedVirtualTableUpdateResult Update(
        IReadOnlyList<SqlValue> arguments,
        ManagedVirtualTableConflictMode conflictMode)
    {
        ManagedFts5Table.ValidateUpdateArguments(arguments, _schema.Columns.Count);
        var oldRowId = ManagedFts5Table.ReadRowId(arguments[0], "old rowid");
        var proposedRowId = ManagedFts5Table.ReadRowId(arguments[1], "new rowid");
        if (oldRowId is not null && proposedRowId is null)
        {
            var removed = Remove(oldRowId.Value);
            return new ManagedVirtualTableUpdateResult(null, removed);
        }

        var coordinates = new double[_definition.CoordinateColumns];
        for (var coordinate = 0; coordinate < coordinates.Length; coordinate++)
        {
            coordinates[coordinate] = _integerCoordinates
                ? ToIntegerCoordinate(arguments[coordinate + 3])
                : ToRealCoordinate(arguments[coordinate + 3], minimum: (coordinate & 1) == 0);
        }

        ManagedRTreeBounds bounds;
        try
        {
            bounds = new ManagedRTreeBounds(coordinates);
        }
        catch (ArgumentOutOfRangeException)
        {
            if (conflictMode == ManagedVirtualTableConflictMode.Ignore)
                return new ManagedVirtualTableUpdateResult(null, Changed: false);

            var dimension = FindInvertedDimension(coordinates);
            var minimumName = _definition.Columns[1 + (dimension * 2)];
            var maximumName = _definition.Columns[2 + (dimension * 2)];
            throw CreateConstraintException(
                $"rtree constraint failed: {_tableName}.({minimumName}<={maximumName})",
                conflictMode == ManagedVirtualTableConflictMode.Replace
                    ? ManagedVirtualTableConflictMode.Abort
                    : conflictMode);
        }

        var suppliedId = arguments[2].Kind == SqlValueKind.Null
            ? (long?)null
            : ToSqliteInt64(arguments[2]);
        // The declared id is authoritative. SQLite passes an independently assigned rowid in
        // argv[1], but rtree ignores it and returns the declared/allocated id from xUpdate.
        var rowId = suppliedId ?? AllocateRowId();
        var conflictsWithAnotherRow = _rows.ContainsKey(rowId)
            && (oldRowId is null || oldRowId.Value != rowId);
        if (conflictsWithAnotherRow)
        {
            if (conflictMode == ManagedVirtualTableConflictMode.Ignore)
                return new ManagedVirtualTableUpdateResult(null, Changed: false);
            if (conflictMode == ManagedVirtualTableConflictMode.Replace)
                Remove(rowId);
            else
                throw CreateConstraintException(
                    $"UNIQUE constraint failed: {_tableName}.{_definition.Columns[0]}",
                    conflictMode);
        }

        if (oldRowId is { } previous)
            Remove(previous);

        var auxiliary = new SqlValue[_definition.AuxiliaryColumns];
        for (var index = 0; index < auxiliary.Length; index++)
            auxiliary[index] = arguments[3 + _definition.CoordinateColumns + index];

        var row = new Row(bounds, auxiliary);
        _rows[rowId] = row;
        _index.Upsert(rowId, bounds);
        return new ManagedVirtualTableUpdateResult(rowId);
    }

    public override void Begin()
    {
        if (_transactionSnapshot is null)
            _transactionSnapshot = new Dictionary<long, Row>(_rows);
    }

    public override void Commit() => _transactionSnapshot = null;

    public override void Rollback()
    {
        if (_transactionSnapshot is null)
            return;

        _rows.Clear();
        foreach (var entry in _transactionSnapshot)
            _rows.Add(entry.Key, entry.Value);
        _transactionSnapshot = null;
        RebuildIndex();
    }

    public override void Rename(string newName) => _tableName = newName;

    public override IReadOnlyList<string> CheckIntegrity()
    {
        var problems = new List<string>();
        if (_definition.Dimensions is < 1 or > 5)
            problems.Add($"R-Tree {_tableName} has an invalid dimension count");

        foreach (var (rowId, row) in _rows)
        {
            if (row.Bounds.Dimensions != _definition.Dimensions)
                problems.Add($"R-Tree {_tableName} rowid {rowId} has an invalid dimension count");
            if (row.Auxiliary.Length != _definition.AuxiliaryColumns)
                problems.Add($"R-Tree {_tableName} rowid {rowId} has an invalid auxiliary-column count");
            for (var coordinate = 0; coordinate < row.Bounds.CoordinateCount; coordinate++)
            {
                var value = row.Bounds.Coordinate(coordinate);
                if (!_integerCoordinates && !IsCanonicalSingle(value))
                    problems.Add($"R-Tree {_tableName} rowid {rowId} has a non-canonical float32 coordinate");
                if (_integerCoordinates && value is < int.MinValue or > int.MaxValue)
                    problems.Add($"R-Tree {_tableName} rowid {rowId} has an out-of-range int32 coordinate");
            }
        }

        foreach (var problem in _index.Validate())
            problems.Add($"{_tableName}: {problem}");
        if (_index.Count != _rows.Count)
            problems.Add($"R-Tree {_tableName} row/tree counts differ ({_rows.Count} != {_index.Count})");
        foreach (var (rowId, row) in _rows)
        {
            if (!_index.TryGet(rowId, out var indexed))
                problems.Add($"R-Tree {_tableName} rowid {rowId} is missing from the spatial index");
            else if (!row.Bounds.ValueEquals(indexed))
                problems.Add($"R-Tree {_tableName} rowid {rowId} has mismatched spatial-index bounds");
        }
        return problems;
    }

    public override ManagedVirtualTablePersistencePayload GetPersistencePayload()
    {
        var writer = new ManagedVirtualTablePayloadWriter();
        writer.WriteInt32(_schema.Columns.Count);
        writer.WriteInt32(_definition.Dimensions);
        writer.WriteInt32(_definition.AuxiliaryColumns);
        writer.WriteInt32(_integerCoordinates ? 1 : 0);
        writer.WriteInt32(_rows.Count);
        foreach (var (rowId, row) in _rows.OrderBy(static entry => entry.Key))
        {
            writer.WriteInt64(rowId);
            for (var coordinate = 0; coordinate < _definition.CoordinateColumns; coordinate++)
            {
                var value = row.Bounds.Coordinate(coordinate);
                writer.WriteInt32(_integerCoordinates
                    ? checked((int)value)
                    : BitConverter.SingleToInt32Bits((float)value));
            }
            foreach (var auxiliary in row.Auxiliary)
                ManagedVirtualTablePayloadValues.Write(writer, auxiliary);
        }

        return new ManagedVirtualTablePersistencePayload(PersistenceVersion, writer.ToArray());
    }

    internal void RestorePersistencePayload(ManagedVirtualTablePersistencePayload payload)
    {
        var rows = payload.Version switch
        {
            1 => ReadVersionOne(payload.Bytes.Span),
            PersistenceVersion => ReadVersionTwo(payload.Bytes.Span),
            _ => throw new EmbeddedSqlException(
                $"unsupported {(_integerCoordinates ? "rtree_i32" : "rtree")} managed virtual-table persistence payload version {payload.Version}"),
        };

        _rows.Clear();
        foreach (var (rowId, row) in rows)
            _rows.Add(rowId, row);
        RebuildIndex();
    }

    private Dictionary<long, Row> ReadVersionOne(ReadOnlySpan<byte> bytes)
    {
        var reader = new ManagedVirtualTablePayloadReader(bytes);
        if (reader.ReadCount() != _schema.Columns.Count)
            throw new EmbeddedSqlException("rtree persistence payload column count does not match the declaration");
        var legacyNextRowId = reader.ReadInt64();
        if (legacyNextRowId < 1)
            throw new EmbeddedSqlException("rtree persistence payload has an invalid next rowid");
        var count = reader.ReadCount();
        var rows = new Dictionary<long, Row>();
        for (var index = 0; index < count; index++)
        {
            var rowId = reader.ReadInt64();
            var coordinates = new double[_definition.CoordinateColumns];
            for (var coordinate = 0; coordinate < coordinates.Length; coordinate++)
            {
                coordinates[coordinate] = _integerCoordinates
                    ? reader.ReadInt32()
                    : CanonicalizeLegacyCoordinate(reader.ReadDouble(), minimum: (coordinate & 1) == 0);
            }
            AddRestoredRow(rows, rowId, coordinates, new SqlValue[_definition.AuxiliaryColumns]);
            if (rowId >= legacyNextRowId)
                throw new EmbeddedSqlException("rtree persistence payload has an invalid next rowid");
        }
        reader.RequireEnd();
        return rows;
    }

    private Dictionary<long, Row> ReadVersionTwo(ReadOnlySpan<byte> bytes)
    {
        var reader = new ManagedVirtualTablePayloadReader(bytes);
        if (reader.ReadCount() != _schema.Columns.Count
            || reader.ReadCount() != _definition.Dimensions
            || reader.ReadCount() != _definition.AuxiliaryColumns
            || reader.ReadCount() != (_integerCoordinates ? 1 : 0))
        {
            throw new EmbeddedSqlException("rtree persistence payload shape does not match the declaration");
        }

        var count = reader.ReadCount();
        var rows = new Dictionary<long, Row>();
        for (var index = 0; index < count; index++)
        {
            var rowId = reader.ReadInt64();
            var coordinates = new double[_definition.CoordinateColumns];
            for (var coordinate = 0; coordinate < coordinates.Length; coordinate++)
            {
                if (_integerCoordinates)
                {
                    coordinates[coordinate] = reader.ReadInt32();
                }
                else
                {
                    var value = BitConverter.Int32BitsToSingle(reader.ReadInt32());
                    if (float.IsNaN(value))
                        throw new EmbeddedSqlException("rtree persistence payload contains a NaN coordinate");
                    coordinates[coordinate] = value;
                }
            }

            var auxiliary = new SqlValue[_definition.AuxiliaryColumns];
            for (var auxiliaryIndex = 0; auxiliaryIndex < auxiliary.Length; auxiliaryIndex++)
                auxiliary[auxiliaryIndex] = ManagedVirtualTablePayloadValues.Read(ref reader);
            AddRestoredRow(rows, rowId, coordinates, auxiliary);
        }
        reader.RequireEnd();
        return rows;
    }

    private static void AddRestoredRow(
        Dictionary<long, Row> rows,
        long rowId,
        double[] coordinates,
        SqlValue[] auxiliary)
    {
        ManagedRTreeBounds bounds;
        try
        {
            bounds = new ManagedRTreeBounds(coordinates);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new EmbeddedSqlException("rtree persistence payload contains invalid bounds", exception);
        }

        if (!rows.TryAdd(rowId, new Row(bounds, auxiliary)))
            throw new EmbeddedSqlException("rtree persistence payload contains duplicate rowids");
    }

    private static double CanonicalizeLegacyCoordinate(double value, bool minimum)
    {
        if (double.IsNaN(value))
            throw new EmbeddedSqlException("rtree persistence payload contains a NaN coordinate");
        return RoundRealCoordinate(value, minimum);
    }

    private static bool IsCanonicalSingle(double value)
        => float.IsInfinity((float)value)
            ? double.IsInfinity(value) && Math.Sign(value) == Math.Sign((float)value)
            : (double)(float)value == value;

    private static int FindInvertedDimension(IReadOnlyList<double> coordinates)
    {
        for (var index = 0; index < coordinates.Count; index += 2)
        {
            if (coordinates[index] > coordinates[index + 1])
                return index / 2;
        }
        return 0;
    }

    private static bool IsSupportedOperator(ManagedVirtualTableConstraintOperator operation)
        => operation is ManagedVirtualTableConstraintOperator.Equal
            or ManagedVirtualTableConstraintOperator.NotEqual
            or ManagedVirtualTableConstraintOperator.Is
            or ManagedVirtualTableConstraintOperator.IsNot
            or ManagedVirtualTableConstraintOperator.GreaterThan
            or ManagedVirtualTableConstraintOperator.GreaterThanOrEqual
            or ManagedVirtualTableConstraintOperator.LessThan
            or ManagedVirtualTableConstraintOperator.LessThanOrEqual;

    private static double ToRealCoordinate(SqlValue value, bool minimum)
    {
        var numeric = EmbeddedDatabase.ApplySqliteNumericAffinity(value);
        var real = numeric.Kind == SqlValueKind.Integer ? numeric.AsInteger() : numeric.AsReal();
        return RoundRealCoordinate(real, minimum);
    }

    private static double RoundRealCoordinate(double value, bool minimum)
    {
        var result = (float)value;
        if (minimum && result > value)
            result = (float)(value * (value < 0 ? RoundAwayFromZero : RoundTowardsZero));
        else if (!minimum && result < value)
            result = (float)(value * (value < 0 ? RoundTowardsZero : RoundAwayFromZero));
        return result;
    }

    private static int ToIntegerCoordinate(SqlValue value)
        => unchecked((int)ToSqliteInt64(value));

    private static long ToSqliteInt64(SqlValue value)
    {
        var numeric = EmbeddedDatabase.ApplySqliteNumericAffinity(value);
        return numeric.Kind == SqlValueKind.Integer
            ? numeric.AsInteger()
            : ToSqliteInt64(numeric.AsReal());
    }

    private static long ToSqliteInt64(double value)
    {
        if (double.IsNaN(value))
            return 0;
        if (value >= long.MaxValue)
            return long.MaxValue;
        if (value <= long.MinValue)
            return long.MinValue;
        return (long)Math.Truncate(value);
    }

    private long AllocateRowId()
    {
        if (_rows.Count == 0)
            return 1;

        var maximum = _rows.Keys.Max();
        if (maximum < long.MaxValue)
            return maximum + 1;

        for (var attempt = 0; attempt < 100; attempt++)
        {
            var candidate = Random.Shared.NextInt64(1, long.MaxValue);
            if (!_rows.ContainsKey(candidate))
                return candidate;
        }

        throw new EmbeddedSqlException("database or disk is full");
    }

    private bool Remove(long rowId)
    {
        var removed = _rows.Remove(rowId);
        if (removed)
            _index.Remove(rowId);
        return removed;
    }

    private void RebuildIndex()
    {
        _index = new ManagedRTreeIndex();
        foreach (var (rowId, row) in _rows.OrderBy(static entry => entry.Key))
            _index.Upsert(rowId, row.Bounds);
    }

    private static EmbeddedSqlException CreateConstraintException(
        string message,
        ManagedVirtualTableConflictMode conflictMode)
    {
        var algorithm = conflictMode switch
        {
            ManagedVirtualTableConflictMode.Rollback => InsertConflictAlgorithm.Rollback,
            ManagedVirtualTableConflictMode.Fail => InsertConflictAlgorithm.Fail,
            ManagedVirtualTableConflictMode.Ignore => InsertConflictAlgorithm.Ignore,
            ManagedVirtualTableConflictMode.Replace => InsertConflictAlgorithm.Replace,
            _ => InsertConflictAlgorithm.Abort,
        };
        return new EmbeddedSqlException(message, SqliteResultCode.Constraint, algorithm);
    }

    internal sealed record Row(ManagedRTreeBounds Bounds, SqlValue[] Auxiliary);

    private sealed class Cursor(
        IReadOnlyDictionary<long, Row> rows,
        ManagedRTreeIndex index,
        ManagedRTreeDefinition definition,
        bool integerCoordinates) : ManagedVirtualTableCursor
    {
        private IReadOnlyList<KeyValuePair<long, Row>> _matches = [];
        private int _position;

        public override bool Filter(ManagedVirtualTablePlan plan, IReadOnlyList<SqlValue> arguments)
        {
            var encoded = DecodePlan(plan, arguments.Count);
            IEnumerable<long>? candidates = null;
            var spatial = new List<ManagedRTreeSearchConstraint>();
            for (var index = 0; index < encoded.Count; index++)
            {
                var constraint = encoded[index];
                var argument = arguments[index];
                if (constraint.Column is -1 or 0)
                {
                    if (constraint.Operator is ManagedVirtualTableConstraintOperator.Equal
                            or ManagedVirtualTableConstraintOperator.Is
                        && TryGetExactRowId(argument, out var exactRowId))
                    {
                        var exact = rows.ContainsKey(exactRowId) ? [exactRowId] : Array.Empty<long>();
                        candidates = candidates is null
                            ? exact
                            : candidates.Where(candidate => candidate == exactRowId);
                    }
                    else if (constraint.Operator is ManagedVirtualTableConstraintOperator.Equal
                                 or ManagedVirtualTableConstraintOperator.Is)
                    {
                        candidates = [];
                    }
                    else
                    {
                        candidates = (candidates ?? rows.Keys)
                            .Where(rowId => MatchesRowId(rowId, constraint.Operator, argument));
                    }
                    continue;
                }

                var converted = ConvertFilterValue(constraint.Operator, argument);
                if (converted.AlwaysFalse)
                {
                    _matches = [];
                    _position = 0;
                    return false;
                }
                if (!converted.AlwaysTrue)
                {
                    spatial.Add(new ManagedRTreeSearchConstraint(
                        constraint.Column - 1,
                        converted.Operator,
                        converted.Value));
                }
            }

            if (spatial.Count != 0)
            {
                var spatialIds = index.Search(spatial).ToHashSet();
                candidates = candidates is null
                    ? spatialIds
                    : candidates.Where(spatialIds.Contains);
            }

            _matches = (candidates ?? rows.Keys)
                .Distinct()
                .OrderBy(static rowId => rowId)
                .Select(rowId => new KeyValuePair<long, Row>(rowId, rows[rowId]))
                .ToArray();
            _position = 0;
            return _matches.Count != 0;
        }

        public override void Next() => _position++;

        public override bool Eof => _position >= _matches.Count;

        public override SqlValue Column(int columnIndex)
        {
            if (columnIndex < 0 || columnIndex >= definition.Columns.Length)
                throw new ArgumentOutOfRangeException(nameof(columnIndex));
            if (columnIndex == 0)
                return SqlValue.Integer(RowId);
            if (columnIndex <= definition.CoordinateColumns)
            {
                var value = _matches[_position].Value.Bounds.Coordinate(columnIndex - 1);
                return integerCoordinates ? SqlValue.Integer((long)value) : SqlValue.Real(value);
            }

            return _matches[_position].Value.Auxiliary[columnIndex - definition.CoordinateColumns - 1];
        }

        public override long RowId => _matches[_position].Key;

        private static List<EncodedConstraint> DecodePlan(ManagedVirtualTablePlan plan, int argumentCount)
        {
            var result = new List<EncodedConstraint>();
            var encoded = plan.IndexString?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [];
            if (encoded.Length != argumentCount)
                throw new EmbeddedSqlException("rtree filter argument count does not match the selected plan");
            foreach (var item in encoded)
            {
                var parts = item.Split(':');
                if (parts.Length != 2
                    || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var column)
                    || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var operation)
                    || !IsSupportedOperator((ManagedVirtualTableConstraintOperator)operation))
                {
                    throw new EmbeddedSqlException("invalid rtree filter plan");
                }
                result.Add(new EncodedConstraint(column, (ManagedVirtualTableConstraintOperator)operation));
            }
            return result;
        }

        private static ConvertedConstraint ConvertFilterValue(
            ManagedVirtualTableConstraintOperator operation,
            SqlValue value)
        {
            if (value.Kind == SqlValueKind.Null)
            {
                return operation == ManagedVirtualTableConstraintOperator.IsNot
                    ? ConvertedConstraint.True
                    : ConvertedConstraint.False;
            }

            var numeric = EmbeddedTable.ApplyColumnAffinity(ColumnAffinity.Numeric, value);
            if (numeric.Kind is not (SqlValueKind.Integer or SqlValueKind.Real))
            {
                return operation is ManagedVirtualTableConstraintOperator.LessThan
                        or ManagedVirtualTableConstraintOperator.LessThanOrEqual
                        or ManagedVirtualTableConstraintOperator.NotEqual
                        or ManagedVirtualTableConstraintOperator.IsNot
                    ? ConvertedConstraint.True
                    : ConvertedConstraint.False;
            }

            var convertedOperation = operation;
            double convertedValue;
            if (numeric.Kind == SqlValueKind.Integer)
            {
                var integer = numeric.AsInteger();
                convertedValue = integer;
                if (integer >= 1L << 48 || integer <= -(1L << 48))
                {
                    if (convertedOperation == ManagedVirtualTableConstraintOperator.LessThan)
                        convertedOperation = ManagedVirtualTableConstraintOperator.LessThanOrEqual;
                    else if (convertedOperation == ManagedVirtualTableConstraintOperator.GreaterThan)
                        convertedOperation = ManagedVirtualTableConstraintOperator.GreaterThanOrEqual;
                }
            }
            else
            {
                convertedValue = numeric.AsReal();
            }

            return new ConvertedConstraint(convertedOperation, convertedValue);
        }

        private static bool MatchesRowId(
            long rowId,
            ManagedVirtualTableConstraintOperator operation,
            SqlValue value)
        {
            if (value.Kind == SqlValueKind.Null)
                return operation == ManagedVirtualTableConstraintOperator.IsNot;

            var numeric = EmbeddedTable.ApplyColumnAffinity(ColumnAffinity.Numeric, value);
            if (numeric.Kind is not (SqlValueKind.Integer or SqlValueKind.Real))
            {
                return operation is ManagedVirtualTableConstraintOperator.LessThan
                    or ManagedVirtualTableConstraintOperator.LessThanOrEqual
                    or ManagedVirtualTableConstraintOperator.NotEqual
                    or ManagedVirtualTableConstraintOperator.IsNot;
            }

            var comparison = numeric.Kind == SqlValueKind.Integer
                ? rowId.CompareTo(numeric.AsInteger())
                : CompareIntegerToReal(rowId, numeric.AsReal());
            return operation switch
            {
                ManagedVirtualTableConstraintOperator.Equal or ManagedVirtualTableConstraintOperator.Is => comparison == 0,
                ManagedVirtualTableConstraintOperator.NotEqual or ManagedVirtualTableConstraintOperator.IsNot => comparison != 0,
                ManagedVirtualTableConstraintOperator.LessThan => comparison < 0,
                ManagedVirtualTableConstraintOperator.LessThanOrEqual => comparison <= 0,
                ManagedVirtualTableConstraintOperator.GreaterThan => comparison > 0,
                ManagedVirtualTableConstraintOperator.GreaterThanOrEqual => comparison >= 0,
                _ => false,
            };
        }

        private static bool TryGetExactRowId(SqlValue value, out long rowId)
        {
            if (value.Kind == SqlValueKind.Null)
            {
                rowId = default;
                return false;
            }

            var numeric = EmbeddedTable.ApplyColumnAffinity(ColumnAffinity.Numeric, value);
            if (numeric.Kind == SqlValueKind.Integer)
            {
                rowId = numeric.AsInteger();
                return true;
            }
            if (numeric.Kind == SqlValueKind.Real)
            {
                var real = numeric.AsReal();
                if (double.IsFinite(real)
                    && real >= long.MinValue
                    && real < 9_223_372_036_854_775_808.0)
                {
                    var integer = (long)real;
                    if (real == integer)
                    {
                        rowId = integer;
                        return true;
                    }
                }
            }

            rowId = default;
            return false;
        }

        private static int CompareIntegerToReal(long integer, double real)
        {
            if (double.IsPositiveInfinity(real))
                return -1;
            if (double.IsNegativeInfinity(real))
                return 1;
            if (real >= 9_223_372_036_854_775_808.0)
                return -1;
            if (real < long.MinValue)
                return 1;

            var truncated = (long)real;
            var comparison = integer.CompareTo(truncated);
            if (comparison != 0)
                return comparison;
            return real == truncated ? 0 : real > truncated ? -1 : 1;
        }

        private readonly record struct EncodedConstraint(
            int Column,
            ManagedVirtualTableConstraintOperator Operator);

        private readonly record struct ConvertedConstraint(
            ManagedVirtualTableConstraintOperator Operator,
            double Value,
            bool AlwaysTrue = false,
            bool AlwaysFalse = false)
        {
            public static ConvertedConstraint True => new(default, default, AlwaysTrue: true);
            public static ConvertedConstraint False => new(default, default, AlwaysFalse: true);
        }
    }
}
