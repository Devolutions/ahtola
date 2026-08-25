namespace Ahtola.Core.Search;

/// <summary>Explicit bounds so no query can be coaxed into unbounded work.</summary>
internal static class ManagedFtsLimits
{
    /// <summary>Maximum distinct terms one prefix wildcard may expand to.</summary>
    public const int MaxPrefixTerms = 4096;

    /// <summary>Maximum leaf terms (including phrase members) in one parsed query.</summary>
    public const int MaxQueryTerms = 256;

    /// <summary>Maximum parser recursion depth.</summary>
    public const int MaxQueryDepth = 64;

    /// <summary>Maximum rows a single materialized match may produce.</summary>
    public const int MaxMatchRows = 1_000_000;

    /// <summary>Maximum recorded token positions in one document.</summary>
    public const int MaxPositionsPerDocument = 1_000_000;

    /// <summary>Maximum NEAR distance accepted by the parser.</summary>
    public const int MaxNearDistance = 1024;

    /// <summary>
    /// Maximum merged source spans <c>fts_highlight</c>/<c>fts_snippet</c> may materialize for one
    /// value, so a pathological document cannot make either function allocate without bound.
    /// </summary>
    public const int MaxHighlightSpans = 1_000_000;

    /// <summary>Maximum prefix terms one highlight or snippet query may carry.</summary>
    public const int MaxHighlightPrefixTerms = 64;
}

/// <summary>Selects the query grammar exposed by the managed search surface.</summary>
internal enum ManagedFtsQuerySyntax
{
    Managed,
    SqliteFts5,
}

/// <summary>The extended managed FTS query grammar used by method indexes and managed FTS5.</summary>
/// <remarks>
/// A superset of <see cref="ManagedFtsQueryParser"/>. The additions are column filters
/// (<c>col:term</c>), initial-token anchors (<c>^term</c>) and bounded proximity groups.
/// </remarks>
internal abstract record ManagedFtsNode;

internal sealed record ManagedFtsNoMatchNode : ManagedFtsNode;

internal sealed record ManagedFtsOmittedNode : ManagedFtsNode;

internal sealed record ManagedFtsTermNode(string Text, bool IsPrefix, string? Column, bool AnchoredAtStart)
    : ManagedFtsNode;

internal sealed record ManagedFtsPhraseTerm(string Text, bool IsPrefix);

internal sealed record ManagedFtsPhraseNode(
    IReadOnlyList<ManagedFtsPhraseTerm> Terms,
    string? Column,
    bool AnchoredAtStart)
    : ManagedFtsNode;

internal sealed record ManagedFtsNearPhrase(IReadOnlyList<ManagedFtsPhraseTerm> Terms);

internal sealed record ManagedFtsNearNode(
    IReadOnlyList<ManagedFtsNearPhrase> Phrases,
    int Distance,
    string? Column,
    bool SqliteDistance = false)
    : ManagedFtsNode;

internal sealed record ManagedFtsAndNode(ManagedFtsNode Left, ManagedFtsNode Right) : ManagedFtsNode;

internal sealed record ManagedFtsOrNode(ManagedFtsNode Left, ManagedFtsNode Right) : ManagedFtsNode;

internal sealed record ManagedFtsNotNode(ManagedFtsNode Operand) : ManagedFtsNode;

/// <summary>
/// Recursive-descent parser for the extended managed FTS query grammar. Every recursion and term
/// count is bounded by <see cref="ManagedFtsLimits"/> so a hostile query cannot exhaust the stack
/// or the heap.
/// </summary>
internal sealed class ManagedFtsQueryLanguage
{
    private readonly string _text;
    private readonly ManagedFtsTokenizerOptions _options;
    private readonly Func<string, bool> _isKnownColumn;
    private readonly ManagedFtsQuerySyntax _syntax;
    private int _offset;
    private int _depth;
    private int _termCount;

    private ManagedFtsQueryLanguage(
        string text,
        ManagedFtsTokenizerOptions options,
        Func<string, bool> isKnownColumn,
        ManagedFtsQuerySyntax syntax)
    {
        _text = text;
        _options = options;
        _isKnownColumn = isKnownColumn;
        _syntax = syntax;
    }

    public static ManagedFtsNode Parse(
        string query,
        ManagedFtsTokenizerOptions options,
        Func<string, bool> isKnownColumn,
        ManagedFtsQuerySyntax syntax = ManagedFtsQuerySyntax.Managed)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(isKnownColumn);
        if (query.AsSpan().Trim().Length == 0)
            throw new EmbeddedSqlException("fts query is empty");

        var parser = new ManagedFtsQueryLanguage(query, options, isKnownColumn, syntax);
        var node = parser.ParseOr();
        parser.ExpectEnd();
        return NormalizeOmitted(node);
    }

    private ManagedFtsNode ParseOr()
    {
        using var _ = Descend();
        var expression = _syntax == ManagedFtsQuerySyntax.SqliteFts5
            ? ParseFts5ExplicitAnd()
            : ParseManagedAnd();
        while (TryReadKeyword("OR"))
        {
            var right = _syntax == ManagedFtsQuerySyntax.SqliteFts5
                ? ParseFts5ExplicitAnd()
                : ParseManagedAnd();
            expression = new ManagedFtsOrNode(
                _syntax == ManagedFtsQuerySyntax.SqliteFts5
                    ? NormalizeOmitted(expression)
                    : expression,
                _syntax == ManagedFtsQuerySyntax.SqliteFts5
                    ? NormalizeOmitted(right)
                    : right);
        }

        return expression;
    }

    private ManagedFtsNode ParseManagedAnd()
    {
        using var _ = Descend();
        var expression = ParseManagedUnary();
        while (true)
        {
            if (TryReadKeyword("AND"))
            {
                expression = new ManagedFtsAndNode(expression, ParseManagedUnary());
                continue;
            }

            if (!IsOperandStart())
                return expression;

            expression = new ManagedFtsAndNode(expression, ParseManagedUnary());
        }
    }

    private ManagedFtsNode ParseManagedUnary()
    {
        using var _ = Descend();
        SkipWhitespace();
        if (TryReadKeyword("NOT") || TryRead('-'))
            return new ManagedFtsNotNode(ParseManagedUnary());

        return ParsePrimary();
    }

    private ManagedFtsNode ParseFts5ExplicitAnd()
    {
        using var _ = Descend();
        var expression = ParseFts5Not();
        while (TryReadKeyword("AND"))
        {
            expression = new ManagedFtsAndNode(
                NormalizeOmitted(expression),
                NormalizeOmitted(ParseFts5Not()));
        }

        return expression;
    }

    private ManagedFtsNode ParseFts5Not()
    {
        using var _ = Descend();
        var expression = ParseFts5ImplicitAnd();
        while (TryReadKeyword("NOT"))
        {
            expression = new ManagedFtsAndNode(
                NormalizeOmitted(expression),
                new ManagedFtsNotNode(NormalizeOmitted(ParseFts5ImplicitAnd())));
        }

        return expression;
    }

    private ManagedFtsNode ParseFts5ImplicitAnd()
    {
        using var _ = Descend();
        var expression = ParsePrimary();
        while (IsFts5ImplicitOperandStart())
        {
            var right = ParsePrimary();
            expression = expression switch
            {
                ManagedFtsOmittedNode => right,
                _ when right is ManagedFtsOmittedNode => expression,
                _ => new ManagedFtsAndNode(expression, right),
            };
        }

        return expression;
    }

    private ManagedFtsNode ParsePrimary()
    {
        using var _ = Descend();
        SkipWhitespace();
        if (_syntax == ManagedFtsQuerySyntax.SqliteFts5
            && (IsKeywordAtOffset("AND")
                || IsKeywordAtOffset("OR")
                || IsKeywordAtOffset("NOT")))
        {
            throw Error("Expected an FTS term.");
        }
        if (TryRead('('))
        {
            var expression = ParseOr();
            if (!TryRead(')'))
                throw Error("Expected ')' to close FTS expression.");
            return expression;
        }

        var column = TryReadColumnPrefix();
        if (IsKeywordAtOffset("NEAR")
            && (_syntax != ManagedFtsQuerySyntax.SqliteFts5 || IsFts5NearAtOffset()))
            return ParseNear(column);

        var anchored = TryRead('^');
        if (_syntax == ManagedFtsQuerySyntax.SqliteFts5)
            return ParseFts5Phrase(column, anchored);

        if (TryRead('"'))
            return ParsePhrase(column, anchored);

        var term = ReadWord();
        if (term.Length == 0)
            throw Error("Expected an FTS term.");

        var prefix = TryRead('*');
        if (TryRead('*'))
            throw Error("An FTS prefix term can contain only one trailing '*'.");

        return BuildTermNode(term, prefix, column, anchored);
    }

    /// <summary>
    /// Normalizes a bare term through the index's own tokenizer, so a query term can never be
    /// compared against differently normalized index terms.
    /// </summary>
    /// <remarks>
    /// A term that tokenizes into several tokens becomes a phrase over those tokens: with the
    /// <c>ascii</c> tokenizer <c>foo-bar</c> is two tokens, and with a gram tokenizer a word is a
    /// run of overlapping grams. Matching them as an adjacent phrase reproduces exact substring
    /// semantics rather than an unordered "contains every gram" approximation. A gram tokenizer has
    /// no notion of a term prefix — every gram is already an interior slice — so a trailing
    /// wildcard collapses into the same substring match.
    /// </remarks>
    private ManagedFtsNode BuildTermNode(string term, bool prefix, string? column, bool anchored)
    {
        var tokens = ManagedFtsTokenization.TokenizeQueryText(term, _options);
        if (tokens.Count == 0)
        {
            // The tokenizer discarded every character (punctuation only, for example). Keep the
            // folded text so the term simply fails to match instead of silently matching everything.
            CountTerm();
            return new ManagedFtsTermNode(
                ManagedFtsTokenization.NormalizeTerm(term, _options),
                prefix,
                column,
                anchored);
        }

        if (tokens.Count == 1)
        {
            CountTerm();
            return new ManagedFtsTermNode(
                tokens[0],
                prefix && !_options.IsGramTokenizer,
                column,
                anchored);
        }

        if (prefix && !_options.IsGramTokenizer)
        {
            throw new EmbeddedSqlException(
                $"fts prefix term '{term}*' expands to {tokens.Count} tokens under the '{ManagedFtsTokenizerOptions.FormatKind(_options.Kind)}' tokenizer; quote it as a phrase instead");
        }

        var terms = new string[tokens.Count];
        for (var index = 0; index < tokens.Count; index++)
        {
            CountTerm();
            terms[index] = tokens[index];
        }

        return new ManagedFtsPhraseNode(
            terms.Select(static term => new ManagedFtsPhraseTerm(term, IsPrefix: false)).ToArray(),
            column,
            anchored);
    }

    private ManagedFtsNode ParseNear(string? column)
    {
        _offset += "NEAR".Length;
        return _syntax == ManagedFtsQuerySyntax.SqliteFts5
            ? ParseFts5Near(column)
            : ParseManagedNear(column);
    }

    private ManagedFtsNode ParseManagedNear(string? column)
    {
        var distance = 10;
        if (TryRead('/'))
        {
            var digits = ReadDigits();
            if (digits.Length == 0
                || !int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out distance)
                || distance < 0
                || distance > ManagedFtsLimits.MaxNearDistance)
            {
                throw Error($"NEAR distance must be between 0 and {ManagedFtsLimits.MaxNearDistance}.");
            }
        }

        if (!TryRead('('))
            throw Error("Expected '(' after NEAR.");

        var terms = new List<string>();
        while (true)
        {
            SkipWhitespace();
            if (TryRead(')'))
                break;

            var word = ReadWord();
            if (word.Length == 0)
                throw Error("Expected an FTS term inside NEAR.");

            // NEAR operands go through the same tokenizer as everything else; a word that produces
            // several tokens contributes each of them, so proximity is measured over real index
            // positions rather than a raw source substring that is not in the dictionary.
            var tokens = ManagedFtsTokenization.TokenizeQueryText(word, _options);
            if (tokens.Count == 0)
            {
                CountTerm();
                terms.Add(ManagedFtsTokenization.NormalizeTerm(word, _options));
                continue;
            }

            foreach (var token in tokens)
            {
                CountTerm();
                terms.Add(token);
            }
        }

        if (terms.Count < 2)
            throw Error("NEAR requires at least two terms.");

        return new ManagedFtsNearNode(
            terms
                .Select(static term => new ManagedFtsNearPhrase(
                    [new ManagedFtsPhraseTerm(term, IsPrefix: false)]))
                .ToArray(),
            distance,
            column);
    }

    private ManagedFtsNode ParseFts5Near(string? column)
    {
        if (!TryRead('('))
            throw Error("Expected '(' after NEAR.");

        var distance = 10;
        var phrases = new List<ManagedFtsNearPhrase>();
        var operandCount = 0;
        while (true)
        {
            SkipWhitespace();
            if (TryRead(')'))
                break;
            if (TryRead(','))
            {
                SkipWhitespace();
                var digits = ReadDigits();
                if (digits.Length == 0
                    || !int.TryParse(
                        digits,
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out distance)
                    || distance < 0
                    || distance > ManagedFtsLimits.MaxNearDistance)
                {
                    throw Error($"NEAR distance must be between 0 and {ManagedFtsLimits.MaxNearDistance}.");
                }
                if (!TryRead(')'))
                    throw Error("Expected ')' after NEAR distance.");
                break;
            }

            var phrase = ParseFts5NearPhrase();
            operandCount++;
            if (phrase.Terms.Count != 0)
                phrases.Add(phrase);
        }

        if (operandCount == 0)
            throw Error("NEAR requires at least one phrase.");
        if (phrases.Count == 0)
            return new ManagedFtsOmittedNode();

        return new ManagedFtsNearNode(phrases, distance, column, SqliteDistance: true);
    }

    private ManagedFtsNearPhrase ParseFts5NearPhrase()
    {
        var terms = ParseFts5PhraseAtom();
        while (TryRead('+'))
            terms.AddRange(ParseFts5PhraseAtom());

        return new ManagedFtsNearPhrase(terms);
    }

    private ManagedFtsNode ParseFts5Phrase(string? column, bool anchored)
    {
        var terms = ParseFts5PhraseAtom();
        while (TryRead('+'))
            terms.AddRange(ParseFts5PhraseAtom());

        return terms.Count switch
        {
            0 => new ManagedFtsOmittedNode(),
            1 => new ManagedFtsTermNode(terms[0].Text, terms[0].IsPrefix, column, anchored),
            _ => new ManagedFtsPhraseNode(terms, column, anchored),
        };
    }

    private List<ManagedFtsPhraseTerm> ParseFts5PhraseAtom()
    {
        var quoted = TryRead('"');
        if (!quoted
            && (IsKeywordAtOffset("AND")
                || IsKeywordAtOffset("OR")
                || IsKeywordAtOffset("NOT")))
        {
            throw Error("Expected an FTS phrase.");
        }

        var phraseText = quoted ? ReadQuotedText() : ReadFts5Bareword();
        if (!quoted && phraseText.Length == 0)
            throw Error("Expected an FTS phrase.");

        var prefix = TryRead('*') && !_options.IsGramTokenizer;
        if (TryRead('*'))
            throw Error("An FTS prefix term can contain only one trailing '*'.");

        var tokens = ManagedFtsTokenization.TokenizeQueryText(phraseText, _options);
        if (tokens.Count == 0)
            return [];

        var terms = new List<ManagedFtsPhraseTerm>(tokens.Count);
        for (var index = 0; index < tokens.Count; index++)
        {
            CountTerm();
            terms.Add(new ManagedFtsPhraseTerm(
                tokens[index],
                prefix && index == tokens.Count - 1));
        }

        return terms;
    }

    private ManagedFtsNode ParsePhrase(string? column, bool anchored)
    {
        var phraseText = ReadQuotedText();

        var prefix = TryRead('*');
        if (prefix && _syntax != ManagedFtsQuerySyntax.SqliteFts5)
            throw Error("A prefix wildcard is valid only after an unquoted FTS term.");

        var tokens = ManagedFtsTokenization.TokenizeQueryText(phraseText, _options);
        if (tokens.Count == 0)
            throw Error("An FTS phrase must contain at least one token.");

        if (tokens.Count == 1)
        {
            CountTerm();
            return new ManagedFtsTermNode(tokens[0], prefix, column, anchored);
        }

        var terms = new string[tokens.Count];
        for (var index = 0; index < tokens.Count; index++)
        {
            CountTerm();
            terms[index] = tokens[index];
        }

        return new ManagedFtsPhraseNode(
            terms
                .Select((term, index) => new ManagedFtsPhraseTerm(
                    term,
                    prefix && index == terms.Length - 1))
                .ToArray(),
            column,
            anchored);
    }

    private string? TryReadColumnPrefix()
    {
        SkipWhitespace();
        var probe = _offset;
        while (probe < _text.Length && IsWordChar(_text[probe]))
            probe++;

        if (probe == _offset || probe >= _text.Length || _text[probe] != ':')
            return null;

        var candidate = _text[_offset..probe];
        if (!_isKnownColumn(candidate))
            throw new EmbeddedSqlException($"no such fts column: {candidate}");

        _offset = probe + 1;
        return candidate;
    }

    private bool IsOperandStart()
    {
        SkipWhitespace();
        if (_offset >= _text.Length || _text[_offset] == ')')
            return false;

        return !IsKeywordAtOffset("OR");
    }

    private bool IsFts5ImplicitOperandStart()
    {
        SkipWhitespace();
        if (_offset >= _text.Length || _text[_offset] == ')')
            return false;

        return !IsKeywordAtOffset("AND")
            && !IsKeywordAtOffset("OR")
            && !IsKeywordAtOffset("NOT");
    }

    private string ReadWord()
    {
        SkipWhitespace();
        var start = _offset;
        while (_offset < _text.Length
            && !char.IsWhiteSpace(_text[_offset])
            && _text[_offset] is not ('(' or ')' or '"' or '*' or '-' or ':' or '^'))
        {
            _offset++;
        }

        return _text[start.._offset];
    }

    private string ReadFts5Bareword()
    {
        SkipWhitespace();
        var start = _offset;
        while (_offset < _text.Length && IsFts5BarewordCharacter(_text[_offset]))
            _offset++;

        return _text[start.._offset];
    }

    private string ReadQuotedText()
    {
        var start = _offset;
        while (_offset < _text.Length)
        {
            if (_text[_offset] != '"')
            {
                _offset++;
                continue;
            }

            if (_syntax == ManagedFtsQuerySyntax.SqliteFts5
                && _offset + 1 < _text.Length
                && _text[_offset + 1] == '"')
            {
                _offset += 2;
                continue;
            }

            var phraseText = _text[start.._offset];
            if (_syntax == ManagedFtsQuerySyntax.SqliteFts5)
                phraseText = phraseText.Replace("\"\"", "\"", StringComparison.Ordinal);
            _offset++;
            return phraseText;
        }

        throw Error("Unterminated FTS phrase.");
    }

    private string ReadDigits()
    {
        var start = _offset;
        while (_offset < _text.Length && char.IsAsciiDigit(_text[_offset]))
            _offset++;

        return _text[start.._offset];
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
            && _text.AsSpan(_offset, keyword.Length).Equals(
                keyword,
                _syntax == ManagedFtsQuerySyntax.SqliteFts5
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase)
            && (_offset + keyword.Length == _text.Length
                || !IsKeywordContinuation(_text[_offset + keyword.Length]));

    private bool IsFts5NearAtOffset()
    {
        var next = _offset + "NEAR".Length;
        while (next < _text.Length && char.IsWhiteSpace(_text[next]))
            next++;

        return next < _text.Length && _text[next] == '(';
    }

    private bool IsKeywordContinuation(char value)
        => _syntax == ManagedFtsQuerySyntax.SqliteFts5
            ? IsFts5BarewordCharacter(value)
            : IsWordChar(value);

    private static ManagedFtsNode NormalizeOmitted(ManagedFtsNode node)
        => node is ManagedFtsOmittedNode ? new ManagedFtsNoMatchNode() : node;

    /// <summary>
    /// The characters that can continue a bare term. This must agree with <see cref="ReadWord"/>
    /// and with <see cref="TryReadColumnPrefix"/>: if the underscore is a word character for term
    /// reading but not for keyword boundaries, <c>NOT_A_TERM</c> parses as the operator <c>NOT</c>
    /// followed by <c>_A_TERM</c> instead of the single term the tokenizer will actually produce.
    /// </summary>
    private static bool IsWordChar(char value) => char.IsLetterOrDigit(value) || value == '_';

    private static bool IsFts5BarewordCharacter(char value)
        => char.IsAsciiLetterOrDigit(value)
            || value == '_'
            || value == '\u001A'
            || value > '\u007F';

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

    private void ExpectEnd()
    {
        SkipWhitespace();
        if (_offset != _text.Length)
            throw Error("Unexpected token in FTS query.");
    }

    private void CountTerm()
    {
        if (++_termCount > ManagedFtsLimits.MaxQueryTerms)
            throw new EmbeddedSqlException($"fts query exceeds {ManagedFtsLimits.MaxQueryTerms} terms");
    }

    private DepthScope Descend()
    {
        if (++_depth > ManagedFtsLimits.MaxQueryDepth)
            throw new EmbeddedSqlException($"fts query exceeds {ManagedFtsLimits.MaxQueryDepth} nesting levels");

        return new DepthScope(this);
    }

    private EmbeddedSqlException Error(string message)
        => new($"{message} At character {_offset + 1}.");

    private readonly struct DepthScope(ManagedFtsQueryLanguage owner) : IDisposable
    {
        public void Dispose() => owner._depth--;
    }
}
