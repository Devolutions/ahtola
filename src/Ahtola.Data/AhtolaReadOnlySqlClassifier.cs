namespace Ahtola;

/// <summary>
/// Conservatively proves that a SQL script cannot mutate the database it runs
/// against, so a browser connection opened in synchronous read-mirror mode may
/// execute it without crossing the asynchronous OPFS boundary.
/// </summary>
/// <remarks>
/// <para>
/// The classifier is deliberately a prover, not a parser: it answers
/// "can this be proven read-only?" and returns <see langword="false"/> for
/// anything it cannot decide, including malformed input. Everything it accepts
/// is a script whose statements all begin with <c>SELECT</c>, <c>VALUES</c>, or
/// a <c>WITH</c> clause whose terminal statement is <c>SELECT</c>/<c>VALUES</c>,
/// and which contains no data-definition, data-modification, transaction,
/// attach/detach, <c>PRAGMA</c>, or <c>EXPLAIN</c> keyword at any nesting depth.
/// A writable common table expression is rejected because <c>INSERT</c>,
/// <c>UPDATE</c>, and <c>DELETE</c> are rejected wherever they appear.
/// </para>
/// <para>
/// Registered scalar functions, aggregates, and collations still run normally;
/// they cannot write the database through the executing command, so they do not
/// affect the proof. The classifier allocates nothing: it tokenizes over spans
/// and compares keywords ordinally, which keeps it usable on the synchronous
/// point-read hot path and free of reflection for NativeAOT and trimming.
/// </para>
/// </remarks>
internal static class AhtolaReadOnlySqlClassifier
{
    private static long s_classificationCount;

    /// <summary>
    /// The number of scripts classified since process start. Synchronous authorization is
    /// captured once per execution and carried on the resulting reader, so this counter lets a
    /// test prove that iterating a large result set never re-tokenizes the statement text.
    /// </summary>
    internal static long ClassificationCount => Interlocked.Read(ref s_classificationCount);

    /// <summary>
    /// Returns whether every statement in <paramref name="sql"/> is provably
    /// incapable of mutating the database.
    /// </summary>
    internal static bool IsProvenReadOnlyScript(string? sql)
        => sql is not null && IsProvenReadOnlyScript(sql.AsSpan());

    /// <summary>
    /// Returns whether every statement in <paramref name="sql"/> is provably
    /// incapable of mutating the database.
    /// </summary>
    internal static bool IsProvenReadOnlyScript(ReadOnlySpan<char> sql)
    {
        Interlocked.Increment(ref s_classificationCount);
        if (sql.IsWhiteSpace())
            return false;

        var tokenizer = new SqlTokenizer(sql);
        var statement = new StatementProof();
        var provedAnyStatement = false;

        while (tokenizer.MoveNext())
        {
            if (tokenizer.Kind == TokenKind.Invalid)
                return false;

            if (tokenizer.Kind == TokenKind.StatementSeparator)
            {
                // A separator inside parentheses is not a statement boundary and is
                // not valid SQLite either, so refuse to reason about it.
                if (tokenizer.Depth != 0)
                    return false;
                if (!statement.TryComplete(ref provedAnyStatement))
                    return false;

                statement = new StatementProof();
                continue;
            }

            if (!statement.Accept(in tokenizer))
                return false;
        }

        // Unbalanced parentheses leave the tokenizer nested; an unclosed group can
        // hide anything, so it is never proven.
        if (tokenizer.NestingDepth != 0)
            return false;

        return statement.TryComplete(ref provedAnyStatement) && provedAnyStatement;
    }

    /// <summary>
    /// Tracks the proof state of a single statement inside a script.
    /// </summary>
    private struct StatementProof
    {
        private WithState _withState;
        private bool _started;
        private bool _isWith;

        internal bool Accept(in SqlTokenizer tokenizer)
        {
            if (!_started)
            {
                _started = true;
                if (tokenizer.Kind != TokenKind.Word)
                    return false;
                if (tokenizer.Text.Equals("SELECT", StringComparison.OrdinalIgnoreCase)
                    || tokenizer.Text.Equals("VALUES", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (!tokenizer.Text.Equals("WITH", StringComparison.OrdinalIgnoreCase))
                    return false;

                _isWith = true;
                _withState = WithState.ExpectCteName;
                return true;
            }

            // Only unquoted words can be SQL keywords. String literals and quoted
            // identifiers are data or names and can never introduce a statement.
            if (tokenizer.Kind == TokenKind.Word && IsForbiddenKeyword(tokenizer.Text))
                return false;

            return !_isWith || AdvanceWith(in tokenizer);
        }

        internal bool TryComplete(ref bool provedAnyStatement)
        {
            if (!_started)
                return true;
            if (_isWith && _withState != WithState.Proven)
                return false;

            provedAnyStatement = true;
            return true;
        }

        /// <summary>
        /// Walks the depth-zero token stream of a <c>WITH</c> statement to prove its
        /// terminal statement is a <c>SELECT</c> or <c>VALUES</c>. Common table
        /// expression bodies and column lists sit at a deeper nesting level, so at
        /// depth zero they appear as an adjacent <c>(</c>/<c>)</c> pair.
        /// </summary>
        private bool AdvanceWith(in SqlTokenizer tokenizer)
        {
            if (_withState == WithState.Proven || tokenizer.Depth != 0)
                return true;

            switch (_withState)
            {
                case WithState.ExpectCteName:
                    if (tokenizer.Kind == TokenKind.Word
                        && tokenizer.Text.Equals("RECURSIVE", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                    if (tokenizer.Kind is not (TokenKind.Word or TokenKind.QuotedIdentifier))
                        return false;

                    _withState = WithState.ExpectAsOrColumns;
                    return true;

                case WithState.ExpectAsOrColumns:
                    if (tokenizer.IsPunctuation('('))
                    {
                        _withState = WithState.ExpectColumnsEnd;
                        return true;
                    }
                    if (tokenizer.Kind != TokenKind.Word
                        || !tokenizer.Text.Equals("AS", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    _withState = WithState.ExpectBodyStart;
                    return true;

                case WithState.ExpectColumnsEnd:
                    if (!tokenizer.IsPunctuation(')'))
                        return false;

                    _withState = WithState.ExpectAs;
                    return true;

                case WithState.ExpectAs:
                    if (tokenizer.Kind != TokenKind.Word
                        || !tokenizer.Text.Equals("AS", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    _withState = WithState.ExpectBodyStart;
                    return true;

                case WithState.ExpectBodyStart:
                    if (tokenizer.Kind == TokenKind.Word
                        && (tokenizer.Text.Equals("NOT", StringComparison.OrdinalIgnoreCase)
                            || tokenizer.Text.Equals("MATERIALIZED", StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                    if (!tokenizer.IsPunctuation('('))
                        return false;

                    _withState = WithState.ExpectBodyEnd;
                    return true;

                case WithState.ExpectBodyEnd:
                    if (!tokenizer.IsPunctuation(')'))
                        return false;

                    _withState = WithState.ExpectCommaOrTerminal;
                    return true;

                case WithState.ExpectCommaOrTerminal:
                    if (tokenizer.IsPunctuation(','))
                    {
                        _withState = WithState.ExpectCteName;
                        return true;
                    }
                    if (tokenizer.Kind != TokenKind.Word)
                        return false;
                    if (!tokenizer.Text.Equals("SELECT", StringComparison.OrdinalIgnoreCase)
                        && !tokenizer.Text.Equals("VALUES", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    _withState = WithState.Proven;
                    return true;

                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Keywords that can never appear in a statement that only reads. Words such
    /// as <c>replace</c> and <c>end</c> are excluded because they are a builtin
    /// function and the <c>CASE</c> terminator; statements that start with them
    /// are already rejected by the leading-keyword rule.
    /// </summary>
    private static bool IsForbiddenKeyword(ReadOnlySpan<char> word)
        => word.Length >= 3
           && (word.Equals("INSERT", StringComparison.OrdinalIgnoreCase)
               || word.Equals("UPDATE", StringComparison.OrdinalIgnoreCase)
               || word.Equals("DELETE", StringComparison.OrdinalIgnoreCase)
               || word.Equals("CREATE", StringComparison.OrdinalIgnoreCase)
               || word.Equals("DROP", StringComparison.OrdinalIgnoreCase)
               || word.Equals("ALTER", StringComparison.OrdinalIgnoreCase)
               || word.Equals("ATTACH", StringComparison.OrdinalIgnoreCase)
               || word.Equals("DETACH", StringComparison.OrdinalIgnoreCase)
               || word.Equals("PRAGMA", StringComparison.OrdinalIgnoreCase)
               || word.Equals("VACUUM", StringComparison.OrdinalIgnoreCase)
               || word.Equals("REINDEX", StringComparison.OrdinalIgnoreCase)
               || word.Equals("ANALYZE", StringComparison.OrdinalIgnoreCase)
               || word.Equals("BEGIN", StringComparison.OrdinalIgnoreCase)
               || word.Equals("COMMIT", StringComparison.OrdinalIgnoreCase)
               || word.Equals("ROLLBACK", StringComparison.OrdinalIgnoreCase)
               || word.Equals("SAVEPOINT", StringComparison.OrdinalIgnoreCase)
               || word.Equals("RELEASE", StringComparison.OrdinalIgnoreCase)
               || word.Equals("EXPLAIN", StringComparison.OrdinalIgnoreCase)
               || word.Equals("TRIGGER", StringComparison.OrdinalIgnoreCase)
               || word.Equals("RETURNING", StringComparison.OrdinalIgnoreCase)
               || word.Equals("TRUNCATE", StringComparison.OrdinalIgnoreCase)
               || word.Equals("GRANT", StringComparison.OrdinalIgnoreCase)
               || word.Equals("REVOKE", StringComparison.OrdinalIgnoreCase)
               || word.Equals("UPSERT", StringComparison.OrdinalIgnoreCase));

    private enum WithState
    {
        ExpectCteName,
        ExpectAsOrColumns,
        ExpectColumnsEnd,
        ExpectAs,
        ExpectBodyStart,
        ExpectBodyEnd,
        ExpectCommaOrTerminal,
        Proven,
    }

    private enum TokenKind
    {
        Invalid,
        Word,
        Number,
        StringLiteral,
        QuotedIdentifier,
        Parameter,
        Punctuation,
        StatementSeparator,
    }

    /// <summary>
    /// A minimal SQLite lexer that recognizes comments, string literals, every
    /// quoted-identifier form, bound parameters, and nesting depth. It reports
    /// <see cref="TokenKind.Invalid"/> rather than guessing on malformed input.
    /// </summary>
    private ref struct SqlTokenizer
    {
        private readonly ReadOnlySpan<char> _sql;
        private int _index;

        internal SqlTokenizer(ReadOnlySpan<char> sql)
        {
            _sql = sql;
            _index = 0;
        }

        /// <summary>The parenthesis nesting level after the current token.</summary>
        internal int NestingDepth { get; private set; }

        /// <summary>
        /// The nesting level the current token belongs to. A group's opening and
        /// closing parenthesis both report the level outside the group.
        /// </summary>
        internal int Depth { get; private set; }

        internal TokenKind Kind { get; private set; }

        internal ReadOnlySpan<char> Text { get; private set; }

        internal readonly bool IsPunctuation(char value)
            => Kind == TokenKind.Punctuation && Text.Length == 1 && Text[0] == value;

        internal bool MoveNext()
        {
            if (!SkipTrivia())
            {
                Kind = TokenKind.Invalid;
                Text = default;
                return true;
            }
            if (_index >= _sql.Length)
                return false;

            var start = _index;
            var current = _sql[_index];
            switch (current)
            {
                case ';':
                    _index++;
                    return Emit(TokenKind.StatementSeparator, start);
                case '(':
                    _index++;
                    Depth = NestingDepth;
                    NestingDepth++;
                    Kind = TokenKind.Punctuation;
                    Text = _sql.Slice(start, 1);
                    return true;
                case ')':
                    if (NestingDepth == 0)
                    {
                        _index++;
                        return Emit(TokenKind.Invalid, start);
                    }

                    _index++;
                    NestingDepth--;
                    return Emit(TokenKind.Punctuation, start);
                case '\'':
                    return ReadQuoted('\'', TokenKind.StringLiteral, start);
                case '"':
                    return ReadQuoted('"', TokenKind.QuotedIdentifier, start);
                case '`':
                    return ReadQuoted('`', TokenKind.QuotedIdentifier, start);
                case '[':
                    return ReadBracketIdentifier(start);
                case ':':
                case '@':
                case '$':
                case '?':
                    _index++;
                    while (_index < _sql.Length && IsIdentifierPart(_sql[_index]))
                        _index++;
                    return Emit(TokenKind.Parameter, start);
            }

            if (IsIdentifierStart(current))
            {
                while (_index < _sql.Length && IsIdentifierPart(_sql[_index]))
                    _index++;
                return Emit(TokenKind.Word, start);
            }

            if (char.IsAsciiDigit(current)
                || (current == '.' && _index + 1 < _sql.Length && char.IsAsciiDigit(_sql[_index + 1])))
            {
                while (_index < _sql.Length
                       && (char.IsAsciiDigit(_sql[_index])
                           || _sql[_index] is '.' or 'x' or 'X'
                           || char.IsAsciiHexDigit(_sql[_index])
                           || IsExponentSign(_index)))
                {
                    _index++;
                }

                return Emit(TokenKind.Number, start);
            }

            _index++;
            return Emit(TokenKind.Punctuation, start);
        }

        private readonly bool IsExponentSign(int index)
            => _sql[index] is '+' or '-'
               && index > 0
               && _sql[index - 1] is 'e' or 'E';

        private bool Emit(TokenKind kind, int start)
        {
            Kind = kind;
            Depth = NestingDepth;
            Text = _sql[start.._index];
            return true;
        }

        private bool ReadQuoted(char quote, TokenKind kind, int start)
        {
            _index++;
            while (_index < _sql.Length)
            {
                if (_sql[_index] != quote)
                {
                    _index++;
                    continue;
                }
                if (_index + 1 < _sql.Length && _sql[_index + 1] == quote)
                {
                    _index += 2;
                    continue;
                }

                _index++;
                return Emit(kind, start);
            }

            return Emit(TokenKind.Invalid, start);
        }

        private bool ReadBracketIdentifier(int start)
        {
            _index++;
            while (_index < _sql.Length)
            {
                if (_sql[_index] == ']')
                {
                    _index++;
                    return Emit(TokenKind.QuotedIdentifier, start);
                }

                _index++;
            }

            return Emit(TokenKind.Invalid, start);
        }

        /// <summary>
        /// Skips whitespace and comments. Returns <see langword="false"/> when a
        /// block comment is never closed, which makes the script unprovable.
        /// </summary>
        private bool SkipTrivia()
        {
            while (_index < _sql.Length)
            {
                var current = _sql[_index];
                if (char.IsWhiteSpace(current))
                {
                    _index++;
                    continue;
                }
                if (current == '-' && _index + 1 < _sql.Length && _sql[_index + 1] == '-')
                {
                    // A line comment ends at CR or LF, exactly like the production statement
                    // splitters (SqliteCommand's script tokenizer and ManagedReadOnlySqlGuard).
                    // Terminating on LF alone would swallow everything after a lone-CR newline —
                    // "SELECT 1 --c\r; INSERT ..." would then be classified as a bare SELECT while
                    // the executor still splits and runs the INSERT.
                    _index += 2;
                    while (_index < _sql.Length && _sql[_index] is not '\r' and not '\n')
                        _index++;
                    continue;
                }
                if (current == '/' && _index + 1 < _sql.Length && _sql[_index + 1] == '*')
                {
                    var terminator = _sql[(_index + 2)..].IndexOf("*/".AsSpan(), StringComparison.Ordinal);
                    if (terminator < 0)
                    {
                        _index = _sql.Length;
                        return false;
                    }

                    _index += terminator + 4;
                    continue;
                }

                break;
            }

            return true;
        }

        private static bool IsIdentifierStart(char value)
            => char.IsAsciiLetter(value) || value == '_' || value > 127;

        private static bool IsIdentifierPart(char value)
            => char.IsAsciiLetterOrDigit(value) || value is '_' or '$' || value > 127;
    }
}
