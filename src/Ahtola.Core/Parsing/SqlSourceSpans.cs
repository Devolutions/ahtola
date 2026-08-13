namespace Ahtola.Core.Parsing;

/// <summary>
/// The exact source extent of an identifier token, including any surrounding quote
/// characters. <c>ALTER TABLE ... RENAME</c> edits stored schema SQL through these spans
/// so a rename never disturbs a string literal or an unrelated identifier that merely
/// contains the old name as a substring.
/// </summary>
internal readonly record struct SqlSourceSpan(int Start, int End, bool IsQuoted)
{
    public static SqlSourceSpan FromToken(SqlToken token) => new(token.Offset, token.End, token.IsQuoted);
}

/// <summary>
/// Identifier spans collected while parsing, keyed by the identity of the AST node that
/// owns them. The map is only populated for rename-driven parses, so ordinary statement
/// preparation pays nothing and the AST records keep their value semantics.
/// </summary>
internal sealed class SqlSourceSpans
{
    private readonly Dictionary<object, SqlSourceSpan> _names = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, SqlSourceSpan> _qualifiers = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, SqlSourceSpan[]> _lists = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, SqlSourceSpan[]> _qualifierLists = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, SqlSourceSpan> _definitionExtents = new(ReferenceEqualityComparer.Instance);

    public void RecordName(object node, SqlToken token) => _names[node] = SqlSourceSpan.FromToken(token);

    public void RecordName(object node, SqlSourceSpan span) => _names[node] = span;

    public void RecordQualifier(object node, SqlToken token)
        => _qualifiers[node] = SqlSourceSpan.FromToken(token);

    public void RecordList(object node, IReadOnlyList<SqlToken> tokens)
        => _lists[node] = tokens.Select(SqlSourceSpan.FromToken).ToArray();

    public void RecordQualifierList(object node, IReadOnlyList<SqlToken> tokens)
        => _qualifierLists[node] = tokens.Select(SqlSourceSpan.FromToken).ToArray();

    /// <summary>
    /// Records the removal extent of a column definition for <c>ALTER TABLE ... DROP COLUMN</c>
    /// surgery: the exact character range whose deletion leaves the remaining definitions
    /// separated the way SQLite leaves them (one comma absorbed per dropped definition).
    /// </summary>
    public void RecordDefinitionExtent(object node, SqlSourceSpan span)
        => _definitionExtents[node] = span;

    public SqlSourceSpan? GetName(object node)
        => _names.TryGetValue(node, out var span) ? span : null;

    public SqlSourceSpan? GetQualifier(object node)
        => _qualifiers.TryGetValue(node, out var span) ? span : null;

    public IReadOnlyList<SqlSourceSpan>? GetList(object node)
        => _lists.TryGetValue(node, out var spans) ? spans : null;

    public IReadOnlyList<SqlSourceSpan>? GetQualifierList(object node)
        => _qualifierLists.TryGetValue(node, out var spans) ? spans : null;

    public SqlSourceSpan? GetDefinitionExtent(object node)
        => _definitionExtents.TryGetValue(node, out var span) ? span : null;
}
