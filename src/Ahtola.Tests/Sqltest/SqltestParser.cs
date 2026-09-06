using System.Globalization;
using System.Text;

namespace Ahtola.Tests.Sqltest;

internal enum SqltestDatabaseKind
{
    Memory,
    TempFile,
    Path,
    Default,
    DefaultNoRowidAlias,
}

internal sealed record SqltestDatabase(SqltestDatabaseKind Kind, string? Path, bool ReadOnly)
{
    public string DisplayName => Kind switch
    {
        SqltestDatabaseKind.Memory => ":memory:",
        SqltestDatabaseKind.TempFile => ":temp:",
        SqltestDatabaseKind.Default => ":default:",
        SqltestDatabaseKind.DefaultNoRowidAlias => ":default-no-rowidalias:",
        _ => Path ?? "<path>",
    };
}

internal enum SqltestExpectationKind
{
    Exact,
    Pattern,
    Unordered,
    Error,

    /// <summary>
    /// Matrix expansion: the expectation is produced at run time by the bundled SQLite
    /// oracle — the managed engine must return the same rows, or both must reject.
    /// </summary>
    Oracle,
}

internal sealed record SqltestExpectation(
    SqltestExpectationKind Kind,
    IReadOnlyList<string> Rows,
    string? Pattern);

/// <summary>A <c>@skip</c>/<c>@skip-if</c> directive. A null condition skips unconditionally.</summary>
internal sealed record SqltestSkip(string Reason, string? Condition);

internal sealed record SqltestCase(
    string Name,
    string Sql,
    SqltestExpectation Expectation,
    IReadOnlyList<string> Setups,
    IReadOnlyList<SqltestSkip> Skips,
    string? Backend,
    IReadOnlyList<string> Requires,
    bool CrossCheckIntegrity);

/// <summary>A <c>@var</c> decorator: a substitution name and the values it expands over.</summary>
internal sealed record SqltestMatrixVar(string Name, IReadOnlyList<string> Values);

/// <summary>
/// A <c>matrix</c> block: one SQL template expanded over the cross-product of its
/// <c>@var</c> decorators. Each expansion becomes an individual case named
/// <c>&lt;matrix&gt;[slug]...</c> and is verified at run time against the bundled
/// SQLite oracle, mirroring <c>ast.rs::MatrixCase</c>.
/// </summary>
internal sealed record SqltestMatrixCase(
    string Name,
    string SqlTemplate,
    IReadOnlyList<SqltestMatrixVar> Vars,
    IReadOnlyList<string> Setups,
    IReadOnlyList<SqltestSkip> Skips,
    string? Backend,
    IReadOnlyList<string> Requires,
    bool CrossCheckIntegrity)
{
    /// <summary>
    /// Expands the cross-product of all variables into concrete cases. Substitution
    /// replaces longer variable names first so <c>$start</c> is never clobbered by a
    /// variable named <c>$s</c> (mirrors <c>MatrixCase::expand</c>). When two values of the
    /// same variable slug identically (e.g. the operators <c>=</c> and <c>&lt;=</c>), the
    /// later expansion is suffixed <c>~N</c> so every case name stays unique for the
    /// name-keyed conformance lookup.
    /// </summary>
    public IReadOnlyList<(string Name, string Sql)> Expand()
    {
        var order = Enumerable.Range(0, Vars.Count)
            .OrderByDescending(index => Vars[index].Name.Length)
            .ToList();
        var expansions = new List<(string Name, string Sql)>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var indices = new int[Vars.Count];
        while (true)
        {
            var sql = SqlTemplate;
            foreach (var variableIndex in order)
            {
                var variable = Vars[variableIndex];
                sql = sql.Replace($"${variable.Name}", variable.Values[indices[variableIndex]]);
            }

            var name = new StringBuilder(Name);
            for (var variableIndex = 0; variableIndex < Vars.Count; variableIndex++)
            {
                name.Append('[').Append(Slug(Vars[variableIndex].Values[indices[variableIndex]])).Append(']');
            }

            var uniqueName = name.ToString();
            if (!seenNames.Add(uniqueName))
            {
                var suffix = 2;
                while (!seenNames.Add($"{uniqueName}~{suffix}"))
                    suffix++;
                uniqueName = $"{uniqueName}~{suffix}";
            }

            expansions.Add((uniqueName, sql));

            // Advance the odometer.
            var position = Vars.Count;
            while (position > 0)
            {
                position--;
                indices[position]++;
                if (indices[position] < Vars[position].Values.Count)
                    break;
                indices[position] = 0;
            }

            if (position == 0 && indices[0] == 0)
                return expansions;
        }
    }

    /// <summary>Turns a variable value into a stable, readable name fragment (mirrors <c>slug</c>).</summary>
    private static string Slug(string value)
    {
        var slug = new StringBuilder(value.Length);
        var lastWasSeparator = true;
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                slug.Append(character);
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator)
            {
                slug.Append('-');
                lastWasSeparator = true;
            }
        }

        while (slug.Length > 0 && slug[^1] == '-')
            slug.Length--;

        return slug.Length == 0 ? "none" : slug.ToString();
    }
}

internal sealed record SqltestFile(
    string RelativePath,
    IReadOnlyList<SqltestDatabase> Databases,
    IReadOnlyDictionary<string, string> Setups,
    IReadOnlyList<SqltestCase> Tests,
    IReadOnlyList<SqltestSkip> GlobalSkips,
    IReadOnlyList<string> GlobalRequires)
{
    public IReadOnlyList<SqltestMatrixCase> Matrices { get; init; } = [];

    /// <summary>
    /// Expands every matrix into concrete <c>Oracle</c>-expectation cases so discovery,
    /// classification, and expected-failure tracking observe them like ordinary tests.
    /// </summary>
    public IReadOnlyList<SqltestCase> ExpandedMatrixTests()
    {
        var cases = new List<SqltestCase>();
        foreach (var matrix in Matrices)
        {
            foreach (var (expansionName, sql) in matrix.Expand())
            {
                cases.Add(new SqltestCase(
                    expansionName,
                    sql,
                    new SqltestExpectation(SqltestExpectationKind.Oracle, [], null),
                    matrix.Setups,
                    matrix.Skips,
                    matrix.Backend,
                    matrix.Requires,
                    matrix.CrossCheckIntegrity));
            }
        }

        return cases;
    }
}

internal sealed class SqltestParseException(string message) : Exception(message);

/// <summary>
/// Parser for the repository's <c>.sqltest</c> DSL, mirroring
/// <c>testing/sqltest/src/parser</c> so the managed engine observes the same
/// directives, setups, and expectation semantics as the Rust runner.
/// </summary>
internal static class SqltestParser
{
    private const int ExprDepthLimit = 100;

    public static SqltestFile Parse(string relativePath, string source)
        => new Reader(relativePath, Tokenize(source.ReplaceLineEndings("\n"))).ParseFile();

    private enum TokenKind
    {
        Directive,
        Location,
        Block,
        Word,
        Text,
        Newline,
    }

    private readonly record struct Token(TokenKind Kind, string Value);

    private static List<Token> Tokenize(string source)
    {
        var tokens = new List<Token>();
        var index = 0;
        while (index < source.Length)
        {
            var current = source[index];
            if (current is ' ' or '\t' or '\r')
            {
                index++;
                continue;
            }

            if (current == '\n')
            {
                tokens.Add(new Token(TokenKind.Newline, "\n"));
                index++;
                continue;
            }

            if (current == '#')
            {
                while (index < source.Length && source[index] != '\n')
                    index++;
                continue;
            }

            if (current == '{')
            {
                tokens.Add(new Token(TokenKind.Block, ReadBlock(source, ref index)));
                continue;
            }

            if (current == '"')
            {
                tokens.Add(new Token(TokenKind.Text, ReadQuoted(source, ref index)));
                continue;
            }

            if (current == '@')
            {
                var directiveStart = index++;
                while (index < source.Length && (char.IsLetterOrDigit(source[index]) || source[index] is '_' or '-'))
                    index++;
                tokens.Add(new Token(TokenKind.Directive, source[directiveStart..index]));
                continue;
            }

            if (current == ':')
            {
                var locationStart = index++;
                while (index < source.Length && source[index] is not (':' or '\n'))
                    index++;
                if (index >= source.Length || source[index] != ':')
                    throw new SqltestParseException("Unterminated database location specifier.");
                index++;
                tokens.Add(new Token(TokenKind.Location, source[locationStart..index]));
                continue;
            }

            var wordStart = index;
            while (index < source.Length &&
                   (char.IsLetterOrDigit(source[index]) || source[index] is '_' or '-' or '.' or '/'))
            {
                index++;
            }

            if (index == wordStart)
                throw new SqltestParseException($"Unexpected character '{current}'.");
            tokens.Add(new Token(TokenKind.Word, source[wordStart..index]));
        }

        return tokens;
    }

    /// <summary>
    /// Reads a brace-delimited block, dropping the structural newline that follows the
    /// opening brace and the one that precedes the closing brace, and honoring the DSL's
    /// <c>\{</c>, <c>\}</c>, and <c>\\</c> escapes.
    /// </summary>
    private static string ReadBlock(string source, ref int index)
    {
        index++;
        var depth = 1;
        var content = new StringBuilder();
        var first = true;
        while (index < source.Length)
        {
            var current = source[index];
            if (first)
            {
                first = false;
                if (current == '\n')
                {
                    index++;
                    continue;
                }
            }

            if (current == '\\' && index + 1 < source.Length && source[index + 1] is '{' or '}' or '\\')
            {
                content.Append(source[index + 1]);
                index += 2;
                continue;
            }

            if (current == '{')
                depth++;

            if (current == '}')
            {
                depth--;
                if (depth == 0)
                {
                    index++;
                    if (content.Length > 0 && content[^1] == '\n')
                        content.Length--;
                    return ExpandMacros(content.ToString());
                }
            }

            content.Append(current);
            index++;
        }

        throw new SqltestParseException("Unterminated block.");
    }

    private static string ReadQuoted(string source, ref int index)
    {
        index++;
        var content = new StringBuilder();
        while (index < source.Length)
        {
            var current = source[index];
            if (current == '\\' && index + 1 < source.Length)
            {
                content.Append(source[index + 1]);
                index += 2;
                continue;
            }

            if (current == '"')
            {
                index++;
                return content.ToString();
            }

            content.Append(current);
            index++;
        }

        throw new SqltestParseException("Unterminated string.");
    }

    private static string ExpandMacros(string content)
    {
        var expanded = new StringBuilder(content.Length);
        var rest = content;
        while (true)
        {
            var start = rest.IndexOf("{{", StringComparison.Ordinal);
            if (start < 0)
                break;

            expanded.Append(rest, 0, start);
            var afterStart = rest[(start + 2)..];
            var end = afterStart.IndexOf("}}", StringComparison.Ordinal);
            if (end < 0)
                throw new SqltestParseException("Unterminated block macro.");

            var body = afterStart[..end].Trim();
            if (body.StartsWith("repeat:", StringComparison.Ordinal))
            {
                var arguments = body["repeat:".Length..];
                var separator = arguments.IndexOf(':');
                if (separator < 0)
                    throw new SqltestParseException("Repeat macro is missing text to repeat.");

                var repeated = arguments[(separator + 1)..];
                var count = EvaluateMacroCount(arguments[..separator]);
                for (var iteration = 0; iteration < count; iteration++)
                    expanded.Append(repeated);
            }
            else
            {
                expanded.Append(EvaluateMacroCount(body).ToString(CultureInfo.InvariantCulture));
            }

            rest = afterStart[(end + 2)..];
        }

        expanded.Append(rest);
        return expanded.ToString();
    }

    private static int EvaluateMacroCount(string expression)
    {
        var compact = new string(expression.Where(static character => !char.IsWhiteSpace(character)).ToArray());
        if (compact == "expr_depth_limit")
            return ExprDepthLimit;
        if (compact.StartsWith("expr_depth_limit+", StringComparison.Ordinal))
            return ExprDepthLimit + int.Parse(compact["expr_depth_limit+".Length..], CultureInfo.InvariantCulture);
        if (compact.StartsWith("expr_depth_limit-", StringComparison.Ordinal))
            return ExprDepthLimit - int.Parse(compact["expr_depth_limit-".Length..], CultureInfo.InvariantCulture);
        return int.Parse(compact, CultureInfo.InvariantCulture);
    }

    private sealed class Reader(string relativePath, List<Token> tokens)
    {
        private int _position;
        private readonly List<SqltestMatrixCase> _matrixCases = [];

        public SqltestFile ParseFile()
        {
            var databases = new List<SqltestDatabase>();
            var setups = new Dictionary<string, string>(StringComparer.Ordinal);
            var tests = new List<SqltestCase>();
            var globalSkips = new List<SqltestSkip>();
            var globalRequires = new List<string>();

            while (true)
            {
                SkipNewlines();
                if (Peek() is not { } token)
                    break;

                switch (token)
                {
                    case { Kind: TokenKind.Directive, Value: "@database" }:
                        Advance();
                        databases.Add(ParseDatabase());
                        break;
                    case { Kind: TokenKind.Directive, Value: "@skip-file" }:
                        Advance();
                        globalSkips.Add(new SqltestSkip(ExpectText(), null));
                        break;
                    case { Kind: TokenKind.Directive, Value: "@skip-file-if" }:
                        Advance();
                        var fileCondition = ExpectWord();
                        globalSkips.Add(new SqltestSkip(ExpectText(), fileCondition));
                        break;
                    case { Kind: TokenKind.Directive, Value: "@requires-file" }:
                        Advance();
                        globalRequires.Add(ExpectWord());
                        _ = ExpectText();
                        break;
                    case { Kind: TokenKind.Word, Value: "setup" }:
                        Advance();
                        setups[ExpectWord()] = ExpectBlock().Trim();
                        break;
                    default:
                        if (ParseCase() is { } parsed)
                            tests.Add(parsed);
                        break;
                }
            }

            return new SqltestFile(relativePath, databases, setups, tests, globalSkips, globalRequires)
            {
                Matrices = _matrixCases,
            };
        }

        private SqltestDatabase ParseDatabase()
        {
            var token = Peek() ?? throw new SqltestParseException("Expected database specifier.");
            Advance();
            return token switch
            {
                { Kind: TokenKind.Location, Value: ":memory:" } =>
                    new SqltestDatabase(SqltestDatabaseKind.Memory, null, false),
                { Kind: TokenKind.Location, Value: ":temp:" } =>
                    new SqltestDatabase(SqltestDatabaseKind.TempFile, null, false),
                { Kind: TokenKind.Location, Value: ":default:" } =>
                    new SqltestDatabase(SqltestDatabaseKind.Default, null, true),
                { Kind: TokenKind.Location, Value: ":default-no-rowidalias:" } =>
                    new SqltestDatabase(SqltestDatabaseKind.DefaultNoRowidAlias, null, true),
                { Kind: TokenKind.Location, Value: ":default-no-rowid-alias:" } =>
                    new SqltestDatabase(SqltestDatabaseKind.DefaultNoRowidAlias, null, true),
                { Kind: TokenKind.Word } =>
                    new SqltestDatabase(SqltestDatabaseKind.Path, token.Value, ConsumeReadOnly()),
                _ => throw new SqltestParseException($"Unexpected database specifier '{token.Value}'."),
            };
        }

        private bool ConsumeReadOnly()
        {
            if (Peek() is not { Kind: TokenKind.Word, Value: "readonly" })
                return false;
            Advance();
            return true;
        }

        private SqltestCase? ParseCase()
        {
            var setups = new List<string>();
            var skips = new List<SqltestSkip>();
            var requires = new List<string>();
            string? backend = null;
            var crossCheckIntegrity = false;
            var matrixVars = new List<SqltestMatrixVar>();

            while (Peek() is { Kind: TokenKind.Directive } directive)
            {
                Advance();
                switch (directive.Value)
                {
                    case "@setup":
                        setups.Add(ExpectWord());
                        break;
                    case "@skip":
                        skips.Add(new SqltestSkip(ExpectText(), null));
                        break;
                    case "@skip-if":
                        var condition = ExpectWord();
                        skips.Add(new SqltestSkip(ExpectText(), condition));
                        break;
                    case "@requires":
                        requires.Add(ExpectWord());
                        _ = ExpectText();
                        break;
                    case "@backend":
                        backend = ExpectWord();
                        break;
                    case "@cross-check-integrity":
                        crossCheckIntegrity = true;
                        break;
                    case "@var":
                        var variableName = ExpectWord();
                        if (matrixVars.Any(variable => variable.Name == variableName))
                            throw new SqltestParseException($"duplicate @var name: {variableName}");
                        var valueBlock = ExpectBlock();
                        matrixVars.Add(new SqltestMatrixVar(
                            variableName,
                            valueBlock.Split('|').Select(static value => value.Trim()).ToArray()));
                        break;
                    default:
                        throw new SqltestParseException($"Unexpected directive '{directive.Value}' in {relativePath}.");
                }

                SkipNewlines();
            }

            var keyword = Peek() ?? throw new SqltestParseException($"Expected test or snapshot in {relativePath}.");
            if (keyword is { Kind: TokenKind.Word, Value: "snapshot" or "snapshot-eqp" })
            {
                // Snapshot cases assert Ahtola's own bytecode listing, which the managed
                // engine deliberately does not reproduce, so they are not discovered.
                Advance();
                _ = ExpectWord();
                _ = ExpectBlock();
                return null;
            }

            if (keyword is { Kind: TokenKind.Word, Value: "matrix" })
            {
                Advance();
                var matrixName = ExpectWord();
                if (matrixVars.Count == 0)
                    throw new SqltestParseException($"Matrix '{matrixName}' in {relativePath} requires at least one @var.");
                var sqlTemplate = ExpectBlock().Trim();
                SkipNewlines();
                if (Peek() is { Kind: TokenKind.Word, Value: "expect" })
                    throw new SqltestParseException(
                        $"Matrix '{matrixName}' in {relativePath} takes no expect blocks; every expansion is verified against the SQLite oracle at run time.");
                _matrixCases.Add(new SqltestMatrixCase(
                    matrixName,
                    sqlTemplate,
                    matrixVars,
                    setups,
                    skips,
                    backend,
                    requires,
                    crossCheckIntegrity));
                return null;
            }

            if (matrixVars.Count != 0)
                throw new SqltestParseException($"@var decorators are only valid before a matrix in {relativePath}.");

            if (keyword is not { Kind: TokenKind.Word, Value: "test" })
                throw new SqltestParseException($"Expected 'test' in {relativePath}, got '{keyword.Value}'.");

            Advance();
            var name = ExpectWord();
            var sql = ExpectBlock().Trim();
            SkipNewlines();

            SqltestExpectation? expectation = null;
            while (Peek() is { Kind: TokenKind.Word, Value: "expect" })
            {
                Advance();
                var backendQualified = Peek() is { Kind: TokenKind.Directive };
                if (backendQualified)
                    Advance();

                var parsed = ParseExpectation();
                if (!backendQualified)
                    expectation = parsed;

                SkipNewlines();
            }

            if (expectation is null)
                throw new SqltestParseException($"Test '{name}' in {relativePath} has no default expect block.");

            return new SqltestCase(
                name,
                sql,
                expectation,
                setups,
                skips,
                backend,
                requires,
                crossCheckIntegrity);
        }

        private SqltestExpectation ParseExpectation()
        {
            if (Peek() is { Kind: TokenKind.Word } modifier)
            {
                switch (modifier.Value)
                {
                    case "error":
                        Advance();
                        var errorPattern = ExpectBlock().Trim();
                        return new SqltestExpectation(
                            SqltestExpectationKind.Error,
                            [],
                            errorPattern.Length == 0 ? null : errorPattern);
                    case "pattern":
                        Advance();
                        return new SqltestExpectation(SqltestExpectationKind.Pattern, [], ExpectBlock().Trim());
                    case "unordered":
                        Advance();
                        var unordered = ExpectBlock()
                            .Trim()
                            .Split('\n')
                            .Select(static row => row.Trim())
                            .Where(static row => row.Length != 0)
                            .ToArray();
                        return new SqltestExpectation(SqltestExpectationKind.Unordered, unordered, null);
                    case "raw":
                        Advance();
                        return new SqltestExpectation(SqltestExpectationKind.Exact, ExpectBlock().Split('\n'), null);
                }
            }

            var rows = ExpectBlock().Split('\n').Select(static row => row.Trim()).ToArray();
            return new SqltestExpectation(SqltestExpectationKind.Exact, rows, null);
        }

        private string ExpectWord()
        {
            if (Peek() is not { Kind: TokenKind.Word } token)
                throw new SqltestParseException($"Expected an identifier in {relativePath}.");
            Advance();
            return token.Value;
        }

        private string ExpectText()
        {
            if (Peek() is not { Kind: TokenKind.Text } token)
                throw new SqltestParseException($"Expected a quoted string in {relativePath}.");
            Advance();
            return token.Value;
        }

        private string ExpectBlock()
        {
            if (Peek() is not { Kind: TokenKind.Block } token)
                throw new SqltestParseException($"Expected a block in {relativePath}.");
            Advance();
            return token.Value;
        }

        private Token? Peek() => _position < tokens.Count ? tokens[_position] : null;

        private void Advance() => _position++;

        private void SkipNewlines()
        {
            while (_position < tokens.Count && tokens[_position].Kind == TokenKind.Newline)
                _position++;
        }
    }
}
