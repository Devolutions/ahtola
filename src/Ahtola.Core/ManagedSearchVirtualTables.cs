using System.Globalization;
using Ahtola.Core.Search;
using Ahtola.Core.Spatial;

namespace Ahtola.Core;

internal sealed class ManagedFts5Module : ManagedVirtualTableModule
{
    public static ManagedFts5Module Instance { get; } = new();

    public override string Name => "fts5";

    public override ManagedVirtualTable Create(ManagedVirtualTableCreateContext context)
        => new ManagedFts5Table(context.TableName, ParseDefinition(context));

    public override ManagedVirtualTable Create(
        ManagedVirtualTableCreateContext context,
        ManagedVirtualTablePersistencePayload payload)
    {
        var table = new ManagedFts5Table(context.TableName, ParseDefinition(context));
        table.RestorePersistencePayload(payload);
        return table;
    }

    private static ManagedFts5Definition ParseDefinition(ManagedVirtualTableCreateContext context)
    {
        if (context.Arguments.Count == 0)
            throw new EmbeddedSqlException("fts5 requires at least one column");

        var columns = new List<ManagedFts5Column>(context.Arguments.Count);
        var tokenizer = ManagedFtsTokenizerOptions.Default;
        var detail = ManagedFtsDetailLevel.Full;
        var columnSize = true;
        var prefixLengths = Array.Empty<int>();
        var options = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var argument in context.Arguments)
        {
            var trimmed = argument.Trim();
            var separator = trimmed.IndexOf('=');
            if (separator >= 0)
            {
                var name = trimmed[..separator].Trim();
                var value = Unquote(trimmed[(separator + 1)..].Trim());
                if (!options.Add(name))
                    throw new EmbeddedSqlException($"duplicate fts5 option: {name}");

                switch (name.ToLowerInvariant())
                {
                    case "tokenize":
                        tokenizer = ParseTokenizer(value);
                        break;
                    case "prefix":
                        prefixLengths = ParsePrefixLengths(value);
                        break;
                    case "detail":
                        detail = value.ToLowerInvariant() switch
                        {
                            "full" => ManagedFtsDetailLevel.Full,
                            "column" => ManagedFtsDetailLevel.Columns,
                            "none" => ManagedFtsDetailLevel.None,
                            _ => throw new EmbeddedSqlException($"unsupported fts5 detail option: {value}"),
                        };
                        break;
                    case "columnsize":
                        columnSize = value switch
                        {
                            "0" => false,
                            "1" => true,
                            _ => throw new EmbeddedSqlException("fts5 columnsize must be 0 or 1"),
                        };
                        break;
                    case "content":
                    case "content_rowid":
                        throw new EmbeddedSqlException(
                            "managed fts5 does not support contentless or external-content tables");
                    default:
                        throw new EmbeddedSqlException($"unsupported fts5 option: {name}");
                }

                continue;
            }

            var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var unindexed = parts.Length == 2
                && string.Equals(parts[1], "UNINDEXED", StringComparison.OrdinalIgnoreCase);
            if (parts.Length == 0
                || parts.Length > 2
                || parts.Length == 2 && !unindexed
                || !IsIdentifier(parts[0]))
            {
                throw new EmbeddedSqlException($"unsupported fts5 module argument: {argument}");
            }

            var column = parts[0];
            if (columns.Any(candidate => string.Equals(candidate.Name, column, StringComparison.OrdinalIgnoreCase)))
                throw new EmbeddedSqlException($"duplicate fts5 column name: {column}");

            columns.Add(new ManagedFts5Column(column, unindexed));
        }

        if (columns.Count == 0)
            throw new EmbeddedSqlException("fts5 requires at least one column");
        if (columns.Count > Indexing.ManagedIndexMethodLimits.MaxIndexedColumns)
        {
            throw new EmbeddedSqlException(
                $"managed fts5 supports at most {Indexing.ManagedIndexMethodLimits.MaxIndexedColumns} columns");
        }

        return new ManagedFts5Definition(columns, tokenizer, detail, columnSize, prefixLengths);
    }

    private static ManagedFtsTokenizerOptions ParseTokenizer(string value)
    {
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 1)
            throw new EmbeddedSqlException("managed fts5 tokenizer modifiers are not supported");

        return parts[0].ToLowerInvariant() switch
        {
            "unicode61" => ManagedFtsTokenizerOptions.Default,
            "ascii" => new ManagedFtsTokenizerOptions(ManagedFtsTokenizerKind.Ascii),
            "trigram" => new ManagedFtsTokenizerOptions(ManagedFtsTokenizerKind.Trigram),
            "porter" => throw new EmbeddedSqlException("managed fts5 does not support the porter tokenizer"),
            _ => throw new EmbeddedSqlException($"unsupported fts5 tokenizer: {parts[0]}"),
        };
    }

    private static int[] ParsePrefixLengths(string value)
    {
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Length > ManagedFtsTokenizerOptions.MaximumGram)
            throw new EmbeddedSqlException("fts5 prefix requires between 1 and 16 prefix lengths");

        var result = new int[parts.Length];
        var seen = new HashSet<int>();
        for (var index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                || length is < 1 or > 999
                || !seen.Add(length))
            {
                throw new EmbeddedSqlException($"invalid fts5 prefix length: {parts[index]}");
            }

            result[index] = length;
        }

        return result;
    }

    private static string Unquote(string value)
    {
        if (value.Length < 2)
            return value;

        var quote = value[0];
        if ((quote is '\'' or '"' or '`') && value[^1] == quote)
            return value[1..^1].Replace(new string(quote, 2), quote.ToString(), StringComparison.Ordinal);
        if (quote == '[' && value[^1] == ']')
            return value[1..^1].Replace("]]", "]", StringComparison.Ordinal);

        return value;
    }

    private static bool IsIdentifier(string value)
        => value.All(static character => char.IsLetterOrDigit(character) || character == '_')
            && !char.IsDigit(value[0]);
}

internal sealed record ManagedFts5Column(string Name, bool Unindexed);

internal sealed record ManagedFts5Definition(
    IReadOnlyList<ManagedFts5Column> Columns,
    ManagedFtsTokenizerOptions Tokenizer,
    ManagedFtsDetailLevel Detail,
    bool ColumnSize,
    IReadOnlyList<int> PrefixLengths);

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

    public override ManagedVirtualTable Create(
        ManagedVirtualTableCreateContext context,
        ManagedVirtualTablePersistencePayload payload)
    {
        var table = Create(context);
        ((ManagedRTreeTable)table).RestorePersistencePayload(payload);
        return table;
    }

    private static bool IsIdentifier(string value)
        => value.All(static character => char.IsLetterOrDigit(character) || character == '_')
            && !char.IsDigit(value[0]);
}

internal sealed class ManagedFts5Table : ManagedVirtualTable
{
    private const int MatchPlan = 1;
    private const int PersistenceVersion = 1;

    private readonly ManagedFts5Definition _definition;
    private readonly string[] _columnNames;
    private string _tableName;
    private ManagedVirtualTableSchema _schema;
    private readonly Dictionary<long, SqlValue[]> _rows = [];
    private readonly ManagedFtsSearchIndex _index;
    private Dictionary<long, SqlValue[]>? _transactionSnapshot;
    private long? _transactionNextRowId;
    private long _nextRowId = 1;

    public ManagedFts5Table(string tableName, ManagedFts5Definition definition)
    {
        _definition = definition;
        _columnNames = definition.Columns.Select(static column => column.Name).ToArray();
        ValidateTableName(tableName);

        var weights = new double[_columnNames.Length];
        Array.Fill(weights, 1.0);
        _index = new ManagedFtsSearchIndex(
            _columnNames.Length,
            definition.Tokenizer,
            weights,
            definition.Detail,
            // SQLite computes token counts on demand when columnsize=0. Ahtola has no docsize
            // shadow table, so retaining derived lengths preserves the observable BM25 result.
            columnSize: true,
            scoringProfile: ManagedFtsScoringProfile.SqliteFts5)
        {
            ColumnIndexResolver = ResolveColumnIndex,
        };

        _tableName = tableName;
        _schema = CreateSchema(tableName);
    }

    internal string TableName => _tableName;

    internal IReadOnlyList<string> ColumnNames => _columnNames;

    internal ManagedFtsTokenizerOptions Tokenizer => _definition.Tokenizer;

    internal bool IsIndexedColumn(int columnIndex) => !_definition.Columns[columnIndex].Unindexed;

    private void ValidateTableName(string tableName)
    {
        if (string.Equals(tableName, "rank", StringComparison.OrdinalIgnoreCase)
            || _columnNames.Contains(tableName, StringComparer.OrdinalIgnoreCase))
        {
            throw new EmbeddedSqlException("an fts5 column cannot have the same name as its table");
        }
        if (_columnNames.Contains("rank", StringComparer.OrdinalIgnoreCase))
            throw new EmbeddedSqlException("an fts5 column cannot be named rank");
        if (_columnNames.Contains("rowid", StringComparer.OrdinalIgnoreCase))
            throw new EmbeddedSqlException("reserved fts5 column name: rowid");
    }

    private int TableColumnIndex => _columnNames.Length;

    private int RankColumnIndex => _columnNames.Length + 1;

    private ManagedVirtualTableSchema CreateSchema(string tableName)
        => new(
            _columnNames
                .Select(static name => new ManagedVirtualTableColumn(name, ManagedVirtualTableAffinity.Text))
                .Append(new ManagedVirtualTableColumn(tableName, ManagedVirtualTableAffinity.Text, IsHidden: true))
                .Append(new ManagedVirtualTableColumn("rank", ManagedVirtualTableAffinity.Real, IsHidden: true)));

    public override ManagedVirtualTableSchema Schema => _schema;

    public override ManagedVirtualTablePlan BestIndex(
        IReadOnlyList<ManagedVirtualTableConstraint> constraints,
        IReadOnlyList<ManagedVirtualTableOrderBy> orderBy)
    {
        var usages = new ManagedVirtualTableConstraintUsage[constraints.Count];
        var matchedColumns = new List<int>();
        var argumentIndex = 1;
        for (var index = 0; index < usages.Length; index++)
        {
            var constraint = constraints[index];
            if (constraint.Usable
                && constraint.Operator == ManagedVirtualTableConstraintOperator.Match
                && constraint.ColumnIndex >= 0
                && constraint.ColumnIndex <= TableColumnIndex)
            {
                usages[index] = new ManagedVirtualTableConstraintUsage(argumentIndex++, Omit: true);
                matchedColumns.Add(constraint.ColumnIndex);
                continue;
            }

            usages[index] = ManagedVirtualTableConstraintUsage.Unused;
        }

        if (matchedColumns.Count != 0)
        {
            var rankOrder = orderBy.Count == 1 && orderBy[0].ColumnIndex == RankColumnIndex
                ? orderBy[0].Descending ? "desc" : "asc"
                : "none";
            return new ManagedVirtualTablePlan(
                usages,
                indexNumber: MatchPlan,
                indexString: string.Join(',', matchedColumns) + "|" + rankOrder,
                orderByConsumed: rankOrder != "none",
                estimatedCost: 10,
                estimatedRows: Math.Max(1, _rows.Count / 4));
        }

        return new ManagedVirtualTablePlan(usages, estimatedCost: Math.Max(1, _rows.Count), estimatedRows: _rows.Count);
    }

    public override ManagedVirtualTableCursor Open() => new Cursor(this);

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

        var tableArgument = arguments[TableColumnIndex + 2];
        var rankArgument = arguments[RankColumnIndex + 2];
        if (tableArgument.Kind != SqlValueKind.Null)
            return ExecuteCommand(arguments, oldRowId, newRowId, tableArgument, rankArgument);
        if (rankArgument.Kind != SqlValueKind.Null)
            throw new EmbeddedSqlException("managed fts5 does not support rank configuration");

        var rowId = newRowId ?? oldRowId ?? AllocateRowId();
        if (oldRowId is null && newRowId is not null && _rows.ContainsKey(rowId))
            throw new EmbeddedSqlException($"constraint failed: rowid {rowId} already exists");
        if (oldRowId is { } replaced
            && replaced != rowId
            && _rows.ContainsKey(rowId))
        {
            throw new EmbeddedSqlException($"constraint failed: rowid {rowId} already exists");
        }
        if (oldRowId is { } previous && previous != rowId)
            Remove(previous);

        var values = arguments.Skip(2).Take(_columnNames.Length).ToArray();
        _rows[rowId] = values;
        UpsertIndex(rowId, values);
        _nextRowId = Math.Max(_nextRowId, rowId == long.MaxValue ? long.MaxValue : rowId + 1);
        return rowId;
    }

    private long AllocateRowId()
    {
        if (_rows.Count == 0)
            return 1;

        var maximum = _rows.Keys.Max();
        if (maximum < long.MaxValue)
            return Math.Max(1, maximum + 1);

        for (var attempt = 0; attempt < 100; attempt++)
        {
            var candidate = Random.Shared.NextInt64(1, long.MaxValue);
            if (!_rows.ContainsKey(candidate))
                return candidate;
        }

        throw new EmbeddedSqlException("database or disk is full");
    }

    private long? ExecuteCommand(
        IReadOnlyList<SqlValue> arguments,
        long? oldRowId,
        long? newRowId,
        SqlValue tableArgument,
        SqlValue rankArgument)
    {
        if (oldRowId is not null
            || newRowId is not null
            || tableArgument.Kind != SqlValueKind.Text
            || rankArgument.Kind != SqlValueKind.Null
            || arguments.Skip(2).Take(_columnNames.Length).Any(static value => value.Kind != SqlValueKind.Null))
        {
            throw new EmbeddedSqlException("invalid managed fts5 command row");
        }

        switch (tableArgument.AsText().ToLowerInvariant())
        {
            case "optimize":
                _index.Compact();
                return null;
            case "rebuild":
                RebuildIndex();
                return null;
            case "delete-all":
                throw new EmbeddedSqlException(
                    "'delete-all' may only be used with a contentless or external content fts5 table");
            default:
                throw new EmbeddedSqlException($"unsupported managed fts5 command: {tableArgument.AsText()}");
        }
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

    public override ManagedVirtualTablePersistencePayload GetPersistencePayload()
    {
        var writer = new ManagedVirtualTablePayloadWriter();
        writer.WriteInt32(_columnNames.Length);
        writer.WriteInt64(_nextRowId);
        writer.WriteInt32(_rows.Count);
        foreach (var (rowId, values) in _rows.OrderBy(static entry => entry.Key))
        {
            writer.WriteInt64(rowId);
            foreach (var value in values)
                ManagedVirtualTablePayloadValues.Write(writer, value);
        }

        return new ManagedVirtualTablePersistencePayload(PersistenceVersion, writer.ToArray());
    }

    public override void Rename(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new EmbeddedSqlException("virtual table name cannot be empty");

        ValidateTableName(newName);
        _tableName = newName;
        _schema = CreateSchema(_tableName);
    }

    internal void RestorePersistencePayload(ManagedVirtualTablePersistencePayload payload)
    {
        if (payload.Version != PersistenceVersion)
        {
            throw new EmbeddedSqlException(
                $"unsupported fts5 managed virtual-table persistence payload version {payload.Version}");
        }

        var reader = new ManagedVirtualTablePayloadReader(payload.Bytes.Span);
        if (reader.ReadCount() != _columnNames.Length)
            throw new EmbeddedSqlException("fts5 persistence payload column count does not match the declaration");
        var nextRowId = reader.ReadInt64();
        if (nextRowId < 1)
            throw new EmbeddedSqlException("fts5 persistence payload has an invalid next rowid");

        var rows = new Dictionary<long, SqlValue[]>();
        var count = reader.ReadCount();
        for (var index = 0; index < count; index++)
        {
            var rowId = reader.ReadInt64();
            var values = new SqlValue[_columnNames.Length];
            for (var column = 0; column < values.Length; column++)
                values[column] = ManagedVirtualTablePayloadValues.Read(ref reader);
            if (!rows.TryAdd(rowId, values))
                throw new EmbeddedSqlException("fts5 persistence payload contains duplicate rowids");
            if (rowId >= nextRowId && rowId != long.MaxValue)
                throw new EmbeddedSqlException("fts5 persistence payload has an invalid next rowid");
        }
        reader.RequireEnd();

        _rows.Clear();
        foreach (var (rowId, values) in rows)
            _rows.Add(rowId, values);
        _nextRowId = nextRowId;
        RebuildIndex();
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
            UpsertIndex(rowId, values);
    }

    private static Dictionary<long, SqlValue[]> CloneRows(IReadOnlyDictionary<long, SqlValue[]> source)
        => source.ToDictionary(static entry => entry.Key, static entry => entry.Value.ToArray());

    private void UpsertIndex(long rowId, SqlValue[] values)
    {
        var indexedValues = values.ToArray();
        for (var index = 0; index < _definition.Columns.Count; index++)
        {
            if (_definition.Columns[index].Unindexed)
                indexedValues[index] = SqlValue.Null;
        }

        _index.Upsert(rowId, values, indexedValues);
    }

    private int? ResolveColumnIndex(string name)
    {
        for (var index = 0; index < _columnNames.Length; index++)
        {
            if (string.Equals(_columnNames[index], name, StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return null;
    }

    internal SqlValue GetColumnValue(long rowId, int columnIndex)
    {
        if (columnIndex < 0 || columnIndex >= _columnNames.Length)
            throw new EmbeddedSqlException($"fts5 column index {columnIndex} is out of range");
        return _rows[rowId][columnIndex];
    }

    internal double Score(ManagedFtsNode? query, long rowId, IReadOnlyList<double>? weights = null)
        => query is null ? 0.0 : -_index.Score(query, rowId, weights);

    internal IReadOnlyList<ManagedFtsHit> Search(
        ManagedFtsNode query,
        IReadOnlyList<double> weights)
        => _index.Search(query, weights);

    internal int SelectSnippetColumn(ManagedFtsNode? query, long rowId, int window)
    {
        if (query is null)
            return 0;

        var bestColumn = 0;
        var bestMatches = -1;
        for (var column = 0; column < _columnNames.Length; column++)
        {
            if (!IsIndexedColumn(column))
                continue;

            var matches = ManagedFtsFunctions.ScoreFts5Snippet(
                _rows[rowId][column],
                query,
                _columnNames[column],
                window,
                _definition.Tokenizer);
            if (matches > bestMatches)
            {
                bestColumn = column;
                bestMatches = matches;
            }
        }

        return bestColumn;
    }

    private ManagedFtsNode ParseQuery(SqlValue value, int columnIndex)
    {
        if (value.Kind != SqlValueKind.Text)
            throw new EmbeddedSqlException("fts5 MATCH requires a text query");

        var query = ManagedFtsQueryLanguage.Parse(
            value.AsText(),
            _definition.Tokenizer,
            name => ResolveColumnIndex(name) is not null,
            ManagedFtsQuerySyntax.SqliteFts5);
        return columnIndex == TableColumnIndex
            ? query
            : ApplyDefaultColumn(query, _columnNames[columnIndex]);
    }

    private static ManagedFtsNode ApplyDefaultColumn(ManagedFtsNode node, string column)
        => node switch
        {
            ManagedFtsNoMatchNode noMatch => noMatch,
            ManagedFtsTermNode term => ApplyDefaultColumn(term, column),
            ManagedFtsPhraseNode phrase => ApplyDefaultColumn(phrase, column),
            ManagedFtsNearNode near => ApplyDefaultColumn(near, column),
            ManagedFtsAndNode and => new ManagedFtsAndNode(
                ApplyDefaultColumn(and.Left, column),
                ApplyDefaultColumn(and.Right, column)),
            ManagedFtsOrNode or => new ManagedFtsOrNode(
                ApplyDefaultColumn(or.Left, column),
                ApplyDefaultColumn(or.Right, column)),
            ManagedFtsNotNode not => new ManagedFtsNotNode(ApplyDefaultColumn(not.Operand, column)),
            _ => throw new ArgumentOutOfRangeException(nameof(node)),
        };

    private static ManagedFtsNode ApplyDefaultColumn(ManagedFtsTermNode term, string column)
        => term.Column is null
            ? term with { Column = column }
            : ColumnsEqual(term.Column, column) ? term : new ManagedFtsNoMatchNode();

    private static ManagedFtsNode ApplyDefaultColumn(ManagedFtsPhraseNode phrase, string column)
        => phrase.Column is null
            ? phrase with { Column = column }
            : ColumnsEqual(phrase.Column, column) ? phrase : new ManagedFtsNoMatchNode();

    private static ManagedFtsNode ApplyDefaultColumn(ManagedFtsNearNode near, string column)
        => near.Column is null
            ? near with { Column = column }
            : ColumnsEqual(near.Column, column) ? near : new ManagedFtsNoMatchNode();

    private static bool ColumnsEqual(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private sealed class Cursor(ManagedFts5Table table)
        : ManagedVirtualTableCursor, IManagedFts5Cursor
    {
        private IReadOnlyList<Match> _matches = [];
        private int _position;
        private ManagedFts5ScoreCache? _scoreCache;

        public override bool Filter(ManagedVirtualTablePlan plan, IReadOnlyList<SqlValue> arguments)
        {
            _matches = plan.IndexNumber == MatchPlan
                ? MatchRows(plan, arguments)
                : table._rows
                    .OrderBy(static entry => entry.Key)
                    .Select(static entry => new Match(entry.Key, entry.Value, null, null))
                    .ToArray();
            _position = 0;
            _scoreCache = _matches.FirstOrDefault()?.Query is { } query
                ? new ManagedFts5ScoreCache(table, query)
                : null;
            return _matches.Count != 0;
        }

        public override void Next() => _position++;

        public override bool Eof => _position >= _matches.Count;

        public override SqlValue Column(int columnIndex)
        {
            if (columnIndex < 0 || columnIndex >= table.Schema.Columns.Count)
                throw new ArgumentOutOfRangeException(nameof(columnIndex));
            if (columnIndex < table._columnNames.Length)
                return _matches[_position].Values[columnIndex];
            if (columnIndex == table.TableColumnIndex)
                return SqlValue.Integer(_matches[_position].Query is null ? 1 : 2);

            return _matches[_position].Rank is { } rank ? SqlValue.Real(rank) : SqlValue.Null;
        }

        public override long RowId => _matches[_position].RowId;

        public ManagedFts5SourceBinding CurrentBinding
            => new(table, RowId, _matches[_position].Query, _matches[_position].Rank, _scoreCache);

        private IReadOnlyList<Match> MatchRows(
            ManagedVirtualTablePlan plan,
            IReadOnlyList<SqlValue> arguments)
        {
            var encoded = plan.IndexString?.Split('|') ?? [];
            if (encoded.Length != 2)
                throw new EmbeddedSqlException("invalid fts5 filter plan");
            var columns = encoded[0]
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.Parse(value, CultureInfo.InvariantCulture))
                .ToArray();
            var descending = encoded[1] == "desc";
            if (arguments.Count != columns.Length)
                throw new EmbeddedSqlException("fts5 filter argument count does not match the selected plan");

            ManagedFtsNode? query = null;
            for (var index = 0; index < arguments.Count; index++)
            {
                var parsed = table.ParseQuery(arguments[index], columns[index]);
                query = query is null ? parsed : new ManagedFtsAndNode(query, parsed);
            }

            var hits = table._index.Search(query!);
            if (descending)
            {
                hits = hits
                    .OrderBy(static hit => hit.Score)
                    .ThenBy(static hit => hit.RowId)
                    .ToArray();
            }

            return hits
                .Select(hit => new Match(hit.RowId, table._rows[hit.RowId], -hit.Score, query))
                .ToArray();
        }

        private sealed record Match(long RowId, SqlValue[] Values, double? Rank, ManagedFtsNode? Query);
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
            SqlValueKind.Real when TryReadIntegralReal(value.AsReal(), out var realRowId) => realRowId,
            SqlValueKind.Text when TryReadIntegralText(value.AsText(), out var textRowId) => textRowId,
            _ => throw new EmbeddedSqlException($"{name} must be an integer or NULL"),
        };

    private static bool TryReadIntegralText(string text, out long rowId)
    {
        var trimmed = EmbeddedTable.TrimAsciiWhitespace(text);
        if (long.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out rowId))
            return true;
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var real))
            return TryReadIntegralReal(real, out rowId);

        rowId = 0;
        return false;
    }

    private static bool TryReadIntegralReal(double real, out long rowId)
    {
        const double MaximumExclusive = 9_223_372_036_854_775_808.0;
        if (double.IsFinite(real)
            && real > long.MinValue
            && real < MaximumExclusive
            && real == Math.Truncate(real))
        {
            rowId = (long)real;
            return true;
        }

        rowId = 0;
        return false;
    }
}

internal interface IManagedFts5Cursor
{
    ManagedFts5SourceBinding CurrentBinding { get; }
}

internal sealed record ManagedFts5SourceBinding(
    ManagedFts5Table Table,
    long RowId,
    ManagedFtsNode? Query,
    double? Rank,
    ManagedFts5ScoreCache? ScoreCache);

internal sealed class ManagedFts5ScoreCache(
    ManagedFts5Table table,
    ManagedFtsNode query)
{
    private readonly List<(double[] Weights, IReadOnlyDictionary<long, double> Scores)> _weightedScores = [];

    public double Score(long rowId, IReadOnlyList<double> weights)
    {
        foreach (var cached in _weightedScores)
        {
            if (cached.Weights.SequenceEqual(weights))
                return cached.Scores.TryGetValue(rowId, out var score) ? score : 0.0;
        }

        var copiedWeights = weights.ToArray();
        var scores = table.Search(query, copiedWeights)
            .ToDictionary(static hit => hit.RowId, static hit => -hit.Score);
        _weightedScores.Add((copiedWeights, scores));
        return scores.TryGetValue(rowId, out var resolved) ? resolved : 0.0;
    }
}

internal sealed class ManagedRTreeTable : ManagedVirtualTable
{
    private const int ConstraintPlan = 1;
    private const int PersistenceVersion = 1;

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
        if (oldRowId is { } replaced
            && replaced != rowId
            && _rows.ContainsKey(rowId))
        {
            throw new EmbeddedSqlException($"constraint failed: rowid {rowId} already exists");
        }
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

    public override ManagedVirtualTablePersistencePayload GetPersistencePayload()
    {
        var writer = new ManagedVirtualTablePayloadWriter();
        writer.WriteInt32(_schema.Columns.Count);
        writer.WriteInt64(_nextRowId);
        writer.WriteInt32(_rows.Count);
        foreach (var (rowId, bounds) in _rows.OrderBy(static entry => entry.Key))
        {
            writer.WriteInt64(rowId);
            for (var coordinate = 0; coordinate < _schema.Columns.Count - 1; coordinate++)
            {
                var value = coordinate % 2 == 0
                    ? bounds.Minimum(coordinate / 2)
                    : bounds.Maximum(coordinate / 2);
                if (_requireIntegerCoordinates)
                    writer.WriteInt32(checked((int)value));
                else
                    writer.WriteDouble(value);
            }
        }

        return new ManagedVirtualTablePersistencePayload(PersistenceVersion, writer.ToArray());
    }

    internal void RestorePersistencePayload(ManagedVirtualTablePersistencePayload payload)
    {
        if (payload.Version != PersistenceVersion)
        {
            throw new EmbeddedSqlException(
                $"unsupported {(_requireIntegerCoordinates ? "rtree_i32" : "rtree")} managed virtual-table persistence payload version {payload.Version}");
        }

        var reader = new ManagedVirtualTablePayloadReader(payload.Bytes.Span);
        if (reader.ReadCount() != _schema.Columns.Count)
            throw new EmbeddedSqlException("rtree persistence payload column count does not match the declaration");
        var nextRowId = reader.ReadInt64();
        if (nextRowId < 1)
            throw new EmbeddedSqlException("rtree persistence payload has an invalid next rowid");

        var rows = new Dictionary<long, ManagedRTreeBounds>();
        var count = reader.ReadCount();
        for (var index = 0; index < count; index++)
        {
            var rowId = reader.ReadInt64();
            var coordinates = new double[_schema.Columns.Count - 1];
            for (var coordinate = 0; coordinate < coordinates.Length; coordinate++)
            {
                coordinates[coordinate] = _requireIntegerCoordinates
                    ? reader.ReadInt32()
                    : reader.ReadDouble();
                if (!double.IsFinite(coordinates[coordinate]))
                    throw new EmbeddedSqlException("rtree persistence payload contains a non-finite coordinate");
            }

            ManagedRTreeBounds bounds;
            try
            {
                bounds = new ManagedRTreeBounds(coordinates);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                throw new EmbeddedSqlException("rtree persistence payload contains invalid bounds", exception);
            }
            if (!rows.TryAdd(rowId, bounds))
                throw new EmbeddedSqlException("rtree persistence payload contains duplicate rowids");
            if (rowId >= nextRowId)
                throw new EmbeddedSqlException("rtree persistence payload has an invalid next rowid");
        }
        reader.RequireEnd();

        _rows.Clear();
        foreach (var (rowId, bounds) in rows)
            _rows.Add(rowId, bounds);
        _nextRowId = nextRowId;
        RebuildIndex();
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
