using Ahtola.Core;

namespace Ahtola.Core.Parsing;

internal static class SqlScript
{
    public static IReadOnlyList<string> Split(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var statements = new List<string>();
        var start = 0;
        var firstTokenInStatement = true;
        var header = TriggerHeader.None;
        var inTriggerBody = false;
        var triggerBodyAtStatementStart = false;
        var lexer = new SqlLexer(sql);

        while (lexer.Current.Kind != TokenKind.End)
        {
            var token = lexer.Current;
            if (token.Kind == TokenKind.Semicolon)
            {
                if (inTriggerBody)
                {
                    triggerBodyAtStatementStart = true;
                }
                else
                {
                    AddStatement(sql, start, token.Offset, statements);
                    start = token.Offset + 1;
                    firstTokenInStatement = true;
                    header = TriggerHeader.None;
                }

                lexer.Next();
                continue;
            }

            if (inTriggerBody)
            {
                if (triggerBodyAtStatementStart && IsKeyword(token, "END"))
                    inTriggerBody = false;
                else
                    triggerBodyAtStatementStart = false;
            }
            else if (firstTokenInStatement)
            {
                firstTokenInStatement = false;
                header = IsKeyword(token, "CREATE") ? TriggerHeader.ExpectTrigger : TriggerHeader.NotTrigger;
            }
            else
            {
                header = AdvanceTriggerHeader(
                    header,
                    token,
                    sql,
                    ref inTriggerBody,
                    ref triggerBodyAtStatementStart);
            }

            lexer.Next();
        }

        AddStatement(sql, start, sql.Length, statements);
        return statements;
    }

    private static TriggerHeader AdvanceTriggerHeader(
        TriggerHeader header,
        SqlToken token,
        string sql,
        ref bool inTriggerBody,
        ref bool triggerBodyAtStatementStart)
    {
        switch (header)
        {
            case TriggerHeader.ExpectTrigger:
                if (IsKeyword(token, "TEMP") || IsKeyword(token, "TEMPORARY"))
                    return TriggerHeader.ExpectTrigger;
                return IsKeyword(token, "TRIGGER") ? TriggerHeader.ExpectNameOrIf : TriggerHeader.NotTrigger;
            case TriggerHeader.ExpectNameOrIf:
                if (IsKeyword(token, "IF"))
                    return TriggerHeader.ExpectNot;

                return IsIdentifier(token) ? TriggerHeader.SeekOn : TriggerHeader.NotTrigger;
            case TriggerHeader.ExpectNot:
                return IsKeyword(token, "NOT") ? TriggerHeader.ExpectExists : TriggerHeader.NotTrigger;
            case TriggerHeader.ExpectExists:
                return IsKeyword(token, "EXISTS") ? TriggerHeader.ExpectName : TriggerHeader.NotTrigger;
            case TriggerHeader.ExpectName:
                return IsIdentifier(token) ? TriggerHeader.SeekOn : TriggerHeader.NotTrigger;
            case TriggerHeader.SeekOn:
                return IsKeyword(token, "ON") ? TriggerHeader.ExpectTable : TriggerHeader.SeekOn;
            case TriggerHeader.ExpectTable:
                return IsIdentifier(token) ? TriggerHeader.AfterTableName : TriggerHeader.NotTrigger;
            case TriggerHeader.AfterTableName:
                if (token.Kind == TokenKind.Dot)
                    return TriggerHeader.ExpectTableLocal;
                if (!IsKeyword(token, "BEGIN"))
                    return TriggerHeader.SeekBegin;
                inTriggerBody = true;
                triggerBodyAtStatementStart = true;
                return TriggerHeader.None;
            case TriggerHeader.ExpectTableLocal:
                return IsIdentifier(token) ? TriggerHeader.SeekBegin : TriggerHeader.NotTrigger;
            case TriggerHeader.SeekBegin:
                if (!IsKeyword(token, "BEGIN"))
                    return TriggerHeader.SeekBegin;

                inTriggerBody = true;
                triggerBodyAtStatementStart = true;
                return TriggerHeader.None;
            default:
                return header;
        }
    }

    private static bool IsKeyword(SqlToken token, string keyword)
        => token.Kind == TokenKind.Identifier
            && !token.IsQuoted
            && token.Text.Equals(keyword, StringComparison.OrdinalIgnoreCase);

    private static bool IsIdentifier(SqlToken token) => token.Kind == TokenKind.Identifier;

    private static bool IsQuotedIdentifier(string sql, SqlToken token)
        => token.Offset < sql.Length && sql[token.Offset] is '"' or '[' or '`';

    private static bool IsTriggerEvent(SqlToken token)
        => IsKeyword(token, "INSERT") || IsKeyword(token, "UPDATE") || IsKeyword(token, "DELETE");

    private enum TriggerHeader
    {
        None,
        NotTrigger,
        ExpectTrigger,
        ExpectNameOrIf,
        ExpectNot,
        ExpectExists,
        ExpectName,
        SeekOn,
        ExpectTable,
        AfterTableName,
        ExpectTableLocal,
        SeekBegin,
    }

    private static void AddStatement(string sql, int start, int end, List<string> statements)
    {
        var statement = sql[start..end].Trim();
        if (statement.Length != 0 && new SqlLexer(statement).Current.Kind != TokenKind.End)
            statements.Add(statement);
    }
}

internal sealed class SqlLexer
{
    private readonly string _sql;
    private int _offset;

    public SqlLexer(string sql)
    {
        _sql = sql;
        Current = ReadToken();
    }

    public SqlToken Current { get; private set; }

    public void Next() => Current = ReadToken();

    public LexerState Snapshot() => new(_offset, Current);

    public void Restore(LexerState state)
    {
        _offset = state.Offset;
        Current = state.Token;
    }

    private SqlToken ReadToken()
    {
        var token = ReadTokenWithoutExtent();
        return token with { End = _offset };
    }

    private SqlToken ReadTokenWithoutExtent()
    {
        SkipWhitespaceAndComments();
        if (_offset == _sql.Length)
            return new SqlToken(TokenKind.End, string.Empty, _offset);

        var start = _offset;
        var current = _sql[_offset++];
        return current switch
        {
            '(' => new SqlToken(TokenKind.LeftParen, "(", start),
            ')' => new SqlToken(TokenKind.RightParen, ")", start),
            ',' => new SqlToken(TokenKind.Comma, ",", start),
            '.' when _offset < _sql.Length && char.IsAsciiDigit(_sql[_offset]) => ReadNumber(start, startsWithDecimalPoint: true),
            '.' => new SqlToken(TokenKind.Dot, ".", start),
            ';' => new SqlToken(TokenKind.Semicolon, ";", start),
            '+' => new SqlToken(TokenKind.Plus, "+", start),
            '-' => ReadMinusOrJsonArrow(start),
            '*' => new SqlToken(TokenKind.Asterisk, "*", start),
            '/' => new SqlToken(TokenKind.Slash, "/", start),
            '%' => new SqlToken(TokenKind.Percent, "%", start),
            '~' => new SqlToken(TokenKind.BitwiseNot, "~", start),
            '&' => new SqlToken(TokenKind.BitwiseAnd, "&", start),
            '=' when ConsumeCharacter('=') => new SqlToken(TokenKind.Equal, "==", start),
            '=' => new SqlToken(TokenKind.Equal, "=", start),
            '!' when ConsumeCharacter('=') => new SqlToken(TokenKind.NotEqual, "!=", start),
            '<' when ConsumeCharacter('=') => new SqlToken(TokenKind.LessThanOrEqual, "<=", start),
            '>' when ConsumeCharacter('=') => new SqlToken(TokenKind.GreaterThanOrEqual, ">=", start),
            '<' when ConsumeCharacter('>') => new SqlToken(TokenKind.NotEqual, "<>", start),
            '<' when ConsumeCharacter('<') => new SqlToken(TokenKind.ShiftLeft, "<<", start),
            '>' when ConsumeCharacter('>') => new SqlToken(TokenKind.ShiftRight, ">>", start),
            '<' => new SqlToken(TokenKind.LessThan, "<", start),
            '>' => new SqlToken(TokenKind.GreaterThan, ">", start),
            '|' when ConsumeCharacter('|') => new SqlToken(TokenKind.Concatenate, "||", start),
            '|' => new SqlToken(TokenKind.BitwiseOr, "|", start),
            '\'' => ReadString(start),
            '"' => ReadQuotedIdentifier(start, '"'),
            '[' => ReadQuotedIdentifier(start, ']'),
            '`' => ReadQuotedIdentifier(start, '`'),
            '?' or ':' or '@' or '$' => ReadParameter(start),
            'x' or 'X' when _offset < _sql.Length && _sql[_offset] == '\'' => ReadBlob(start),
            _ when char.IsAsciiDigit(current) => ReadNumber(start),
            _ when IsIdentifierStart(current) => ReadIdentifier(start),
            _ => throw new EmbeddedSqlException($"Unexpected SQL character '{current}' at offset {start}."),
        };
    }

    private SqlToken ReadString(int start)
    {
        var value = new System.Text.StringBuilder();
        while (_offset < _sql.Length)
        {
            var current = _sql[_offset++];
            if (current != '\'')
            {
                value.Append(current);
                continue;
            }

            if (_offset < _sql.Length && _sql[_offset] == '\'')
            {
                value.Append('\'');
                _offset++;
                continue;
            }

            return new SqlToken(TokenKind.String, value.ToString(), start);
        }

        throw new EmbeddedSqlException($"Unterminated SQL string at offset {start}.");
    }

    private SqlToken ReadMinusOrJsonArrow(int start)
    {
        if (!ConsumeCharacter('>'))
            return new SqlToken(TokenKind.Minus, "-", start);

        return ConsumeCharacter('>')
            ? new SqlToken(TokenKind.JsonArrowText, "->>", start)
            : new SqlToken(TokenKind.JsonArrow, "->", start);
    }

    private SqlToken ReadQuotedIdentifier(int start, char closingCharacter)
    {
        var value = new System.Text.StringBuilder();
        while (_offset < _sql.Length)
        {
            var current = _sql[_offset++];
            if (current != closingCharacter)
            {
                value.Append(current);
                continue;
            }

            if (_offset < _sql.Length && _sql[_offset] == closingCharacter)
            {
                value.Append(closingCharacter);
                _offset++;
                continue;
            }

            return new SqlToken(TokenKind.Identifier, value.ToString(), start, IsQuoted: true);
        }

        throw new EmbeddedSqlException($"Unterminated quoted identifier at offset {start}.");
    }

    private SqlToken ReadBlob(int start)
    {
        _offset++;
        var valueStart = _offset;
        while (_offset < _sql.Length && _sql[_offset] != '\'')
            _offset++;

        if (_offset == _sql.Length)
            throw new EmbeddedSqlException($"Unterminated SQL blob at offset {start}.");

        var value = _sql[valueStart.._offset];
        _offset++;
        if (value.Length % 2 != 0 || !value.All(char.IsAsciiHexDigit))
            throw new EmbeddedSqlException($"Invalid SQL blob literal at offset {start}.");

        return new SqlToken(TokenKind.Blob, value, start);
    }

    private SqlToken ReadParameter(int start)
    {
        ConsumeParameterIdentifier();
        if (_sql[start] == '$')
        {
            while (_offset + 1 < _sql.Length && _sql[_offset] == ':' && _sql[_offset + 1] == ':')
            {
                _offset += 2;
                ConsumeParameterIdentifier();
            }

            if (_offset < _sql.Length && _sql[_offset] == '(')
            {
                _offset++;
                ConsumeParameterIdentifier();
                if (_offset < _sql.Length && _sql[_offset] == ')')
                    _offset++;
            }
        }

        return new SqlToken(TokenKind.Parameter, _sql[start.._offset], start);
    }

    private void ConsumeParameterIdentifier()
    {
        while (_offset < _sql.Length
               && (char.IsAsciiLetterOrDigit(_sql[_offset]) || _sql[_offset] is '_' or '$'))
        {
            _offset++;
        }
    }

    private SqlToken ReadNumber(int start, bool startsWithDecimalPoint = false)
    {
        var sawSeparator = false;
        if (!startsWithDecimalPoint
            && _sql[start] == '0'
            && _offset + 1 < _sql.Length
            && _sql[_offset] is 'x' or 'X'
            && char.IsAsciiHexDigit(_sql[_offset + 1]))
        {
            _offset++;
            ConsumeDigitRun(hex: true, hasPrecedingDigit: false, ref sawSeparator);
            return new SqlToken(TokenKind.Integer, NumericLiteralText(start, sawSeparator), start);
        }

        ConsumeDigitRun(hex: false, hasPrecedingDigit: true, ref sawSeparator);

        var isReal = startsWithDecimalPoint;
        if (!startsWithDecimalPoint && _offset < _sql.Length && _sql[_offset] == '.')
        {
            isReal = true;
            _offset++;
            ConsumeDigitRun(hex: false, hasPrecedingDigit: false, ref sawSeparator);
        }

        if (_offset < _sql.Length && _sql[_offset] is 'e' or 'E')
        {
            var exponentStart = _offset + 1;
            if (exponentStart < _sql.Length && _sql[exponentStart] is '+' or '-')
                exponentStart++;
            if (exponentStart < _sql.Length && char.IsAsciiDigit(_sql[exponentStart]))
            {
                isReal = true;
                _offset = exponentStart + 1;
                ConsumeDigitRun(hex: false, hasPrecedingDigit: true, ref sawSeparator);
            }
        }

        return new SqlToken(
            isReal ? TokenKind.Real : TokenKind.Integer,
            NumericLiteralText(start, sawSeparator),
            start);
    }

    private string NumericLiteralText(int start, bool sawSeparator)
    {
        var text = _sql[start.._offset];
        return sawSeparator ? text.Replace("_", string.Empty) : text;
    }

    /// <summary>
    /// Consumes a run of digits that may contain SQLite 3.47 digit separators — single
    /// underscores placed between two digits of the run — mirroring Turso's
    /// <c>eat_while_number_digit</c>/<c>eat_while_number_hexdigit</c>
    /// (sqlite/parser/src/lexer.rs). A separator without a digit on either side makes
    /// the literal malformed (<c>1__2</c>, <c>0xFF_</c>), which throws instead of ending
    /// the scan so the spelling surfaces as a tokenization error rather than turning the
    /// trailing text into an identifier.
    /// </summary>
    private void ConsumeDigitRun(bool hex, bool hasPrecedingDigit, ref bool sawSeparator)
    {
        var lastWasDigit = hasPrecedingDigit;
        while (_offset < _sql.Length)
        {
            var current = _sql[_offset];
            var isDigit = hex ? char.IsAsciiHexDigit(current) : char.IsAsciiDigit(current);
            if (isDigit)
            {
                _offset++;
                lastWasDigit = true;
                continue;
            }

            if (current != '_')
                return;

            var nextOffset = _offset + 1;
            var nextIsDigit = nextOffset < _sql.Length
                && (hex ? char.IsAsciiHexDigit(_sql[nextOffset]) : char.IsAsciiDigit(_sql[nextOffset]));
            if (!lastWasDigit || !nextIsDigit)
                throw new EmbeddedSqlException($"Invalid digit separator in numeric literal at offset {_offset}.");

            _offset++;
            sawSeparator = true;
            lastWasDigit = false;
        }
    }

    private SqlToken ReadIdentifier(int start)
    {
        while (_offset < _sql.Length && IsIdentifierContinue(_sql[_offset]))
            _offset++;

        return new SqlToken(TokenKind.Identifier, _sql[start.._offset], start);
    }

    private void SkipWhitespaceAndComments()
    {
        while (_offset < _sql.Length)
        {
            if (char.IsWhiteSpace(_sql[_offset]))
            {
                _offset++;
                continue;
            }
            if (_offset + 1 < _sql.Length && _sql[_offset] == '-' && _sql[_offset + 1] == '-')
            {
                _offset += 2;
                while (_offset < _sql.Length && _sql[_offset] is not '\r' and not '\n')
                    _offset++;
                continue;
            }
            if (_offset + 1 < _sql.Length && _sql[_offset] == '/' && _sql[_offset + 1] == '*')
            {
                var start = _offset;
                _offset += 2;
                var terminated = false;
                while (_offset + 1 < _sql.Length)
                {
                    if (_sql[_offset] == '*' && _sql[_offset + 1] == '/')
                    {
                        _offset += 2;
                        terminated = true;
                        break;
                    }

                    _offset++;
                }

                if (!terminated)
                    throw new EmbeddedSqlException($"Unterminated SQL comment at offset {start}.");

                continue;
            }

            return;
        }
    }

    private bool ConsumeCharacter(char expected)
    {
        if (_offset >= _sql.Length || _sql[_offset] != expected)
            return false;

        _offset++;
        return true;
    }

    // SQLite's tokenizer treats every non-ASCII character (byte >= 0x80) as an
    // identifier character, so 'Café' is a plain identifier; case folding stays
    // ASCII-only at name-resolution time.
    private static bool IsIdentifierStart(char value) => char.IsAsciiLetter(value) || value == '_' || value >= 0x80;

    private static bool IsIdentifierContinue(char value) => char.IsAsciiLetterOrDigit(value) || value is '_' or '$' || value >= 0x80;
}

/// <summary>
/// A lexed token. <paramref name="Offset"/> and <see cref="End"/> delimit the token's
/// exact source extent (including any surrounding quote characters), which
/// <c>ALTER TABLE ... RENAME</c> uses to edit stored schema SQL in place the way SQLite
/// does instead of re-rendering it from the parse tree.
/// </summary>
internal readonly record struct SqlToken(
    TokenKind Kind,
    string Text,
    int Offset,
    bool IsQuoted = false)
{
    public int End { get; init; } = Offset;
}

internal readonly record struct LexerState(int Offset, SqlToken Token);

internal enum TokenKind
{
    End,
    Identifier,
    Integer,
    Real,
    String,
    Blob,
    Parameter,
    LeftParen,
    RightParen,
    Comma,
    Dot,
    Semicolon,
    Plus,
    Minus,
    JsonArrow,
    JsonArrowText,
    Asterisk,
    Slash,
    Percent,
    BitwiseNot,
    BitwiseAnd,
    BitwiseOr,
    ShiftLeft,
    ShiftRight,
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    Concatenate,
}
