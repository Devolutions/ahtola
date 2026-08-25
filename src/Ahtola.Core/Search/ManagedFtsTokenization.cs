using System.Buffers;
using System.Globalization;
using System.Text;

namespace Ahtola.Core.Search;

/// <summary>The tokenizers a managed FTS method index can be configured with.</summary>
internal enum ManagedFtsTokenizerKind
{
    /// <summary>Unicode letter/digit runs, NFD folded, marks stripped, lowercased. The default.</summary>
    Unicode61 = 0,

    /// <summary>ASCII alphanumeric runs, lowercased. Non-ASCII characters separate tokens.</summary>
    Ascii = 1,

    /// <summary>Whitespace-delimited runs, lowercased, punctuation retained inside the token.</summary>
    Whitespace = 2,

    /// <summary>Whitespace-delimited runs with no case folding at all.</summary>
    Raw = 3,

    /// <summary>Sliding character n-grams over the folded text, bounded by min_gram/max_gram.</summary>
    Ngram = 4,

    /// <summary>Sliding character 3-grams. Equivalent to <see cref="Ngram"/> with min=max=3.</summary>
    Trigram = 5,
}

/// <summary>How much per-posting detail an FTS index records.</summary>
internal enum ManagedFtsDetailLevel
{
    /// <summary>Term frequency, column mask and token positions. Phrase and NEAR are available.</summary>
    Full = 0,

    /// <summary>Term frequency and column mask only. Phrase and NEAR are rejected.</summary>
    Columns = 1,

    /// <summary>Presence only: no per-column attribution. Column filters, phrase and NEAR are rejected.</summary>
    None = 2,
}

/// <summary>Immutable tokenizer configuration resolved from the index's <c>WITH</c> clause.</summary>
internal sealed record ManagedFtsTokenizerOptions(
    ManagedFtsTokenizerKind Kind = ManagedFtsTokenizerKind.Unicode61,
    int MinGram = 3,
    int MaxGram = 3)
{
    /// <summary>Smallest accepted n-gram size.</summary>
    public const int MinimumGram = 1;

    /// <summary>Largest accepted n-gram size.</summary>
    public const int MaximumGram = 16;

    public static ManagedFtsTokenizerOptions Default { get; } = new();

    /// <summary>True when the tokenizer slices characters instead of splitting on separators.</summary>
    public bool IsGramTokenizer => Kind is ManagedFtsTokenizerKind.Ngram or ManagedFtsTokenizerKind.Trigram;

    /// <summary>The effective gram bounds, collapsing <c>trigram</c> onto its fixed 3/3 pair.</summary>
    public (int Min, int Max) EffectiveGrams
        => Kind switch
        {
            ManagedFtsTokenizerKind.Trigram => (3, 3),
            ManagedFtsTokenizerKind.Ngram => (MinGram, MaxGram),
            _ => (0, 0),
        };

    public static ManagedFtsTokenizerKind ParseKind(string name)
        => name.ToLowerInvariant() switch
        {
            "unicode61" or "simple" => ManagedFtsTokenizerKind.Unicode61,
            "ascii" => ManagedFtsTokenizerKind.Ascii,
            "whitespace" => ManagedFtsTokenizerKind.Whitespace,
            "raw" => ManagedFtsTokenizerKind.Raw,
            "ngram" => ManagedFtsTokenizerKind.Ngram,
            "trigram" => ManagedFtsTokenizerKind.Trigram,
            _ => throw new EmbeddedSqlException($"unknown fts tokenizer: {name}"),
        };

    public static string FormatKind(ManagedFtsTokenizerKind kind)
        => kind switch
        {
            ManagedFtsTokenizerKind.Unicode61 => "unicode61",
            ManagedFtsTokenizerKind.Ascii => "ascii",
            ManagedFtsTokenizerKind.Whitespace => "whitespace",
            ManagedFtsTokenizerKind.Raw => "raw",
            ManagedFtsTokenizerKind.Ngram => "ngram",
            ManagedFtsTokenizerKind.Trigram => "trigram",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
}

/// <summary>
/// One folded unit of source text: the folded characters plus the exact UTF-16 span of the source
/// they came from.
/// </summary>
/// <remarks>
/// Folding is per source rune, so a surrogate pair is never split and a combining mark never shifts
/// the offsets of the characters that follow it. That is what lets <c>fts_highlight</c> and
/// <c>fts_snippet</c> reproduce the original document byte for byte even when the tokenizer folded
/// away accents or changed a character's UTF-16 length.
/// </remarks>
internal readonly record struct ManagedFtsFoldedUnit(string Text, int SourceOffset, int SourceLength);

/// <summary>
/// Offset-preserving tokenization for every configured tokenizer. Offsets always refer to the
/// original UTF-16 source text so <c>fts_highlight</c> and <c>fts_snippet</c> can reproduce the
/// original document exactly.
/// </summary>
internal static class ManagedFtsTokenization
{
    /// <summary>Longest token retained, in UTF-16 code units. Longer tokens are truncated.</summary>
    public const int MaxTermLength = 256;

    public static IReadOnlyList<ManagedFtsToken> Tokenize(string text, ManagedFtsTokenizerOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);

        return options.Kind switch
        {
            ManagedFtsTokenizerKind.Unicode61 => Truncate(ManagedFtsTokenizer.Tokenize(text)),
            ManagedFtsTokenizerKind.Ascii => TokenizeRuns(text, IsAsciiTokenChar, lowercase: true),
            ManagedFtsTokenizerKind.Whitespace => TokenizeRuns(text, static value => !char.IsWhiteSpace(value), lowercase: true),
            ManagedFtsTokenizerKind.Raw => TokenizeRuns(text, static value => !char.IsWhiteSpace(value), lowercase: false),
            ManagedFtsTokenizerKind.Ngram or ManagedFtsTokenizerKind.Trigram => TokenizeGrams(text, options),
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };
    }

    /// <summary>
    /// Tokenizes one query term or phrase with the same folding the index used, so a bare term can
    /// never be compared against differently normalized index terms.
    /// </summary>
    /// <remarks>
    /// For a gram tokenizer a query is sliced at a single gram size, unlike an indexed document
    /// which is sliced at every size in <c>[min_gram, max_gram]</c>. Emitting one size keeps the
    /// resulting grams at consecutive positions, so the caller can match them as a phrase and get
    /// exact substring semantics instead of an unordered "contains all grams" approximation.
    /// </remarks>
    public static IReadOnlyList<string> TokenizeQueryText(string text, ManagedFtsTokenizerOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.IsGramTokenizer)
        {
            var tokens = Tokenize(text, options);
            var result = new string[tokens.Count];
            for (var index = 0; index < tokens.Count; index++)
                result[index] = tokens[index].Text;

            return result;
        }

        var (minGram, maxGram) = options.EffectiveGrams;
        var units = FoldWithOffsets(text);
        if (units.Count == 0)
            return [];

        // The largest configured size the query can fill: a shorter query cannot be sliced at all,
        // so it degrades to its folded text, which simply will not be present in a gram index.
        var size = Math.Min(maxGram, units.Count);
        if (size < minGram)
            return [Clamp(Concat(units, 0, units.Count))];

        var grams = new List<string>(Math.Max(units.Count - size + 1, 1));
        for (var start = 0; start + size <= units.Count; start++)
        {
            var gram = Concat(units, start, size);
            if (gram.AsSpan().Trim().Length == 0)
                continue;

            grams.Add(Clamp(gram));
        }

        return grams;
    }

    /// <summary>Normalizes a single query term with the same folding the tokenizer applies.</summary>
    public static string NormalizeTerm(string term, ManagedFtsTokenizerOptions options)
    {
        ArgumentNullException.ThrowIfNull(term);
        return options.Kind switch
        {
            ManagedFtsTokenizerKind.Raw => Clamp(term),
            ManagedFtsTokenizerKind.Whitespace or ManagedFtsTokenizerKind.Ascii => Clamp(term.ToLowerInvariant()),
            _ => Clamp(Fold(term)),
        };
    }

    /// <summary>
    /// Folds text one source rune at a time, recording the exact source span each folded unit came
    /// from. A rune that folds away entirely (a standalone combining mark) contributes no unit, so
    /// the units that follow keep their true source offsets.
    /// </summary>
    public static List<ManagedFtsFoldedUnit> FoldWithOffsets(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var units = new List<ManagedFtsFoldedUnit>(text.Length);
        var offset = 0;
        while (offset < text.Length)
        {
            var status = Rune.DecodeFromUtf16(text.AsSpan(offset), out _, out var consumed);
            if (status != OperationStatus.Done)
            {
                // An unpaired surrogate has no scalar value to fold; keep it as an opaque unit so
                // the offsets of everything after it stay exact.
                units.Add(new ManagedFtsFoldedUnit(text.Substring(offset, 1), offset, 1));
                offset++;
                continue;
            }

            var folded = FoldSpan(text.AsSpan(offset, consumed));
            if (folded.Length > 0)
                units.Add(new ManagedFtsFoldedUnit(folded, offset, consumed));

            offset += consumed;
        }

        return units;
    }

    private static string Concat(List<ManagedFtsFoldedUnit> units, int start, int count)
    {
        if (count == 1)
            return units[start].Text;

        var builder = new StringBuilder(count);
        for (var index = start; index < start + count; index++)
            builder.Append(units[index].Text);

        return builder.ToString();
    }

    private static bool IsAsciiTokenChar(char value) => char.IsAsciiLetterOrDigit(value);

    private static IReadOnlyList<ManagedFtsToken> Truncate(IReadOnlyList<ManagedFtsToken> tokens)
    {
        var needsTruncation = false;
        foreach (var token in tokens)
        {
            if (token.Text.Length > MaxTermLength)
            {
                needsTruncation = true;
                break;
            }
        }

        if (!needsTruncation)
            return tokens;

        var truncated = new List<ManagedFtsToken>(tokens.Count);
        foreach (var token in tokens)
        {
            truncated.Add(token.Text.Length > MaxTermLength
                ? token with { Text = token.Text[..MaxTermLength] }
                : token);
        }

        return truncated;
    }

    private static IReadOnlyList<ManagedFtsToken> TokenizeRuns(string text, Func<char, bool> isTokenChar, bool lowercase)
    {
        var tokens = new List<ManagedFtsToken>();
        var start = -1;
        var position = 0;
        for (var offset = 0; offset < text.Length; offset++)
        {
            if (isTokenChar(text[offset]))
            {
                if (start < 0)
                    start = offset;
                continue;
            }

            if (start >= 0)
            {
                tokens.Add(CreateRunToken(text, start, offset - start, position++, lowercase));
                start = -1;
            }
        }

        if (start >= 0)
            tokens.Add(CreateRunToken(text, start, text.Length - start, position, lowercase));

        return tokens;
    }

    private static ManagedFtsToken CreateRunToken(string text, int offset, int length, int position, bool lowercase)
    {
        var raw = text.Substring(offset, length);
        var normalized = Clamp(lowercase ? raw.ToLowerInvariant() : raw);
        return new ManagedFtsToken(normalized, offset, length, position);
    }

    /// <summary>
    /// Slices folded units into every configured gram size. A gram's position is the index of the
    /// unit it starts at, so grams of one size sit at consecutive positions and phrase adjacency
    /// reproduces exact substring matching.
    /// </summary>
    private static IReadOnlyList<ManagedFtsToken> TokenizeGrams(string text, ManagedFtsTokenizerOptions options)
    {
        var (minGram, maxGram) = options.EffectiveGrams;
        ValidateGramBounds(minGram, maxGram);

        var units = FoldWithOffsets(text);
        var tokens = new List<ManagedFtsToken>();
        for (var size = minGram; size <= maxGram; size++)
        {
            for (var start = 0; start + size <= units.Count; start++)
            {
                var gram = Concat(units, start, size);
                if (gram.AsSpan().Trim().Length == 0)
                    continue;

                var first = units[start];
                var last = units[start + size - 1];
                tokens.Add(new ManagedFtsToken(
                    Clamp(gram),
                    first.SourceOffset,
                    last.SourceOffset + last.SourceLength - first.SourceOffset,
                    start));
            }
        }

        return tokens;
    }

    /// <summary>Validates gram bounds with the one diagnostic both the parser and DDL use.</summary>
    public static void ValidateGramBounds(int minGram, int maxGram)
    {
        if (minGram < ManagedFtsTokenizerOptions.MinimumGram
            || maxGram < minGram
            || maxGram > ManagedFtsTokenizerOptions.MaximumGram)
        {
            throw new EmbeddedSqlException(
                $"fts ngram tokenizer requires {ManagedFtsTokenizerOptions.MinimumGram} <= min_gram <= max_gram <= {ManagedFtsTokenizerOptions.MaximumGram}");
        }
    }

    /// <summary>Folds a whole string. Offsets are not preserved; use <see cref="FoldWithOffsets"/> when they matter.</summary>
    private static string Fold(string value)
    {
        var units = FoldWithOffsets(value);
        if (units.Count == 0)
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var unit in units)
            builder.Append(unit.Text);

        return builder.ToString();
    }

    private static string FoldSpan(ReadOnlySpan<char> value)
    {
        var decomposed = value.ToString().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString().ToLowerInvariant();
    }

    private static string Clamp(string value)
        => value.Length > MaxTermLength ? value[..MaxTermLength] : value;
}
