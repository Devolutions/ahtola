using System.Globalization;
using System.Text;

namespace Ahtola.Core.Search;

/// <summary>
/// A deterministic, managed tokenizer for the subset of FTS query processing shared by
/// the future FTS module and its query planner.
/// </summary>
internal static class ManagedFtsTokenizer
{
    public static IReadOnlyList<ManagedFtsToken> Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var tokens = new List<ManagedFtsToken>();
        var tokenStart = -1;
        var position = 0;

        for (var offset = 0; offset < text.Length;)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(offset), out var rune, out var consumed)
                != System.Buffers.OperationStatus.Done)
                throw new ArgumentException("Text contains an invalid UTF-16 sequence.", nameof(text));

            if (IsTokenRune(rune) || (tokenStart >= 0 && IsCombiningMark(rune)))
            {
                tokenStart = tokenStart < 0 ? offset : tokenStart;
            }
            else if (tokenStart >= 0)
            {
                tokens.Add(CreateToken(text, tokenStart, offset - tokenStart, position++));
                tokenStart = -1;
            }

            offset += consumed;
        }

        if (tokenStart >= 0)
            tokens.Add(CreateToken(text, tokenStart, text.Length - tokenStart, position));

        return tokens;
    }

    private static bool IsTokenRune(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        return Rune.IsLetterOrDigit(rune)
            || category is UnicodeCategory.LetterNumber or UnicodeCategory.OtherLetter;
    }

    private static bool IsCombiningMark(Rune rune)
        => Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;

    private static ManagedFtsToken CreateToken(string source, int offset, int length, int position)
        => new(Normalize(source.AsSpan(offset, length)), offset, length, position);

    private static string Normalize(ReadOnlySpan<char> value)
    {
        var decomposed = value.ToString().Normalize(NormalizationForm.FormD);
        var normalized = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            normalized.Append(character);
        }

        return normalized.ToString().ToLowerInvariant();
    }
}

/// <summary>
/// A token normalized with <see cref="ManagedFtsTokenizer"/>. Offsets refer to the original
/// UTF-16 source text so snippets and highlighting can reproduce the original document.
/// </summary>
internal sealed record ManagedFtsToken(string Text, int Offset, int Length, int Position);

/// <summary>
/// Parses the managed FTS query subset: terms, quoted phrases, a trailing prefix wildcard,
/// and the AND, OR, and NOT boolean operators. Adjacent operands imply AND.
/// </summary>
internal static class ManagedFtsQueryParser
{
    public static ManagedFtsQuery Parse(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var parser = new Parser(query);
        var result = parser.ParseOr();
        parser.ExpectEnd();
        return result;
    }

    private sealed class Parser(string text)
    {
        private readonly string _text = text;
        private int _offset;

        public ManagedFtsQuery ParseOr()
        {
            var expression = ParseAnd();
            while (TryReadKeyword("OR"))
                expression = new ManagedFtsOr(expression, ParseAnd());

            return expression;
        }

        private ManagedFtsQuery ParseAnd()
        {
            var expression = ParseUnary();
            while (true)
            {
                if (TryReadKeyword("AND"))
                {
                    expression = new ManagedFtsAnd(expression, ParseUnary());
                    continue;
                }

                if (!IsOperandStart())
                    return expression;

                expression = new ManagedFtsAnd(expression, ParseUnary());
            }
        }

        private ManagedFtsQuery ParseUnary()
        {
            if (TryReadKeyword("NOT") || TryRead('-'))
                return new ManagedFtsNot(ParseUnary());

            return ParsePrimary();
        }

        private ManagedFtsQuery ParsePrimary()
        {
            SkipWhitespace();
            if (TryRead('('))
            {
                var expression = ParseOr();
                if (!TryRead(')'))
                    throw Error("Expected ')' to close FTS expression.");
                return expression;
            }

            if (TryRead('"'))
                return ParsePhrase();

            var term = ReadWord();
            if (term.Length == 0)
                throw Error("Expected an FTS term.");

            var prefix = TryRead('*');
            if (TryRead('*'))
                throw Error("An FTS prefix term can contain only one trailing '*'.");

            return new ManagedFtsTerm(NormalizeTerm(term), prefix);
        }

        private ManagedFtsQuery ParsePhrase()
        {
            var start = _offset;
            while (_offset < _text.Length && _text[_offset] != '"')
                _offset++;

            if (_offset == _text.Length)
                throw Error("Unterminated FTS phrase.");

            var phraseText = _text[start.._offset];
            _offset++;

            if (TryRead('*'))
                throw Error("A prefix wildcard is valid only after an unquoted FTS term.");

            var tokens = ManagedFtsTokenizer.Tokenize(phraseText)
                .Select(static token => token.Text)
                .ToArray();
            if (tokens.Length == 0)
                throw Error("An FTS phrase must contain at least one token.");

            return new ManagedFtsPhrase(tokens);
        }

        private bool IsOperandStart()
        {
            SkipWhitespace();
            if (_offset >= _text.Length || _text[_offset] == ')')
                return false;

            return !IsKeywordAtOffset("OR");
        }

        private string ReadWord()
        {
            SkipWhitespace();
            var start = _offset;
            while (_offset < _text.Length
                && !char.IsWhiteSpace(_text[_offset])
                && _text[_offset] is not ('(' or ')' or '"' or '*' or '-'))
            {
                _offset++;
            }

            return _text[start.._offset];
        }

        private static string NormalizeTerm(string term)
        {
            var tokens = ManagedFtsTokenizer.Tokenize(term);
            if (tokens.Count != 1 || tokens[0].Length != term.Length)
                throw new ArgumentException($"'{term}' is not a valid FTS term.");

            return tokens[0].Text;
        }

        private bool TryReadKeyword(string keyword)
        {
            SkipWhitespace();
            if (!IsKeywordAtOffset(keyword))
                return false;

            _offset += keyword.Length;
            return true;
        }

        private bool IsKeywordAtOffset(string keyword)
            => _offset + keyword.Length <= _text.Length
                && _text.AsSpan(_offset, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase)
                && (_offset + keyword.Length == _text.Length
                    || !char.IsLetterOrDigit(_text[_offset + keyword.Length]));

        private bool TryRead(char value)
        {
            SkipWhitespace();
            if (_offset >= _text.Length || _text[_offset] != value)
                return false;

            _offset++;
            return true;
        }

        private void SkipWhitespace()
        {
            while (_offset < _text.Length && char.IsWhiteSpace(_text[_offset]))
                _offset++;
        }

        public void ExpectEnd()
        {
            SkipWhitespace();
            if (_offset != _text.Length)
                throw Error("Unexpected token in FTS query.");
        }

        private FormatException Error(string message) => new($"{message} At character {_offset + 1}.");
    }
}

internal abstract record ManagedFtsQuery;

internal sealed record ManagedFtsTerm(string Text, bool IsPrefix) : ManagedFtsQuery;

internal sealed record ManagedFtsPhrase(IReadOnlyList<string> Terms) : ManagedFtsQuery;

internal sealed record ManagedFtsAnd(ManagedFtsQuery Left, ManagedFtsQuery Right) : ManagedFtsQuery;

internal sealed record ManagedFtsOr(ManagedFtsQuery Left, ManagedFtsQuery Right) : ManagedFtsQuery;

internal sealed record ManagedFtsNot(ManagedFtsQuery Operand) : ManagedFtsQuery;

/// <summary>
/// In-memory document and posting store for a managed FTS module. It intentionally owns no
/// catalog or transaction state: the virtual-table foundation supplies durable rows and invokes
/// <see cref="Upsert"/> or <see cref="Remove"/> inside its transaction callbacks.
/// </summary>
internal sealed class ManagedFtsIndex
{
    private readonly Dictionary<long, ManagedFtsDocument> _documents = [];
    private readonly Dictionary<string, Dictionary<long, int>> _postings =
        new(StringComparer.Ordinal);

    public int Count => _documents.Count;

    public void Clear()
    {
        _documents.Clear();
        _postings.Clear();
    }

    public void Upsert(long rowId, IReadOnlyList<string?> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        Remove(rowId);

        var document = new ManagedFtsDocument(
            columns.Select(static value => ManagedFtsTokenizer.Tokenize(value ?? string.Empty)).ToArray());
        _documents.Add(rowId, document);

        foreach (var token in document.Columns.SelectMany(static column => column))
        {
            if (!_postings.TryGetValue(token.Text, out var rows))
            {
                rows = [];
                _postings.Add(token.Text, rows);
            }

            rows.TryGetValue(rowId, out var count);
            rows[rowId] = count + 1;
        }
    }

    public bool Remove(long rowId)
    {
        if (!_documents.Remove(rowId, out var document))
            return false;

        foreach (var token in document.Columns.SelectMany(static column => column))
        {
            var rows = _postings[token.Text];
            if (--rows[rowId] == 0)
                rows.Remove(rowId);
            if (rows.Count == 0)
                _postings.Remove(token.Text);
        }

        return true;
    }

    public IReadOnlyList<ManagedFtsSearchResult> Search(ManagedFtsQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var matches = new List<ManagedFtsSearchResult>();
        foreach (var (rowId, document) in _documents.OrderBy(static pair => pair.Key))
        {
            if (Matches(query, document))
                matches.Add(new ManagedFtsSearchResult(rowId, CountMatches(query, document)));
        }

        return matches;
    }

    private bool Matches(ManagedFtsQuery query, ManagedFtsDocument document)
        => query switch
        {
            ManagedFtsTerm term => MatchesTerm(term, document),
            ManagedFtsPhrase phrase => MatchesPhrase(phrase, document),
            ManagedFtsAnd and => Matches(and.Left, document) && Matches(and.Right, document),
            ManagedFtsOr or => Matches(or.Left, document) || Matches(or.Right, document),
            ManagedFtsNot not => !Matches(not.Operand, document),
            _ => throw new ArgumentOutOfRangeException(nameof(query)),
        };

    private static bool MatchesTerm(ManagedFtsTerm term, ManagedFtsDocument document)
        => document.Columns.SelectMany(static column => column)
            .Any(token => term.IsPrefix
                ? token.Text.StartsWith(term.Text, StringComparison.Ordinal)
                : string.Equals(token.Text, term.Text, StringComparison.Ordinal));

    private static bool MatchesPhrase(ManagedFtsPhrase phrase, ManagedFtsDocument document)
    {
        foreach (var column in document.Columns)
        {
            for (var start = 0; start <= column.Count - phrase.Terms.Count; start++)
            {
                var matches = true;
                for (var offset = 0; offset < phrase.Terms.Count; offset++)
                {
                    if (!string.Equals(column[start + offset].Text, phrase.Terms[offset], StringComparison.Ordinal))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                    return true;
            }
        }

        return false;
    }

    private static int CountMatches(ManagedFtsQuery query, ManagedFtsDocument document)
        => query switch
        {
            ManagedFtsTerm term => document.Columns.SelectMany(static column => column)
                .Count(token => term.IsPrefix
                    ? token.Text.StartsWith(term.Text, StringComparison.Ordinal)
                    : string.Equals(token.Text, term.Text, StringComparison.Ordinal)),
            ManagedFtsPhrase phrase => MatchesPhrase(phrase, document) ? 1 : 0,
            ManagedFtsAnd and => CountMatches(and.Left, document) + CountMatches(and.Right, document),
            ManagedFtsOr or => CountMatches(or.Left, document) + CountMatches(or.Right, document),
            ManagedFtsNot => 0,
            _ => throw new ArgumentOutOfRangeException(nameof(query)),
        };

    private sealed record ManagedFtsDocument(IReadOnlyList<IReadOnlyList<ManagedFtsToken>> Columns);
}

internal sealed record ManagedFtsSearchResult(long RowId, int MatchCount);
