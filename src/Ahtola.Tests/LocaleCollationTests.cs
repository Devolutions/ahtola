using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Collation;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

/// <summary>
/// Focused coverage for BCP-47 locale collation tags (<c>COLLATE 'fr-FR'</c>,
/// <c>COLLATE 'es-u-co-trad'</c>, etc.), beyond the pinned upstream cases already
/// exercised by <c>conformance/sqlite-sqltests/turso/collate.sqltest</c>
/// (<see cref="ManagedSqltestConformanceTests"/>). See
/// <see cref="LocaleCollationTag"/>, <see cref="LocaleCollationWeights"/>, and
/// <see cref="LocaleCollationRegistry"/> for the implementation and the
/// empirical citations behind each design choice (in particular: why the
/// <c>ks</c> keyword is parsed but has no effect on comparison strength).
/// </summary>
public sealed class LocaleCollationTests
{
    [Test]
    [TestCase("not a locale!!")]
    [TestCase("en-u-ks-bogus9value")]
    [TestCase("en-u-kf-sideways")]
    [TestCase("en-u-kn-maybe")]
    [TestCase("-leading-hyphen")]
    [TestCase("zzz")] // syntactically valid but fictional: not distinguished from a real
                      // unimplemented language, so it fails closed too (see below).
    [TestCase("zzz-u-kn-true")]
    public void MalformedOrOutOfEnumerationLocaleTagFailsClosed(string tag)
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile($"locale-invalid-{Math.Abs(tag.GetHashCode())}.db", fileSystem);
        using var connection = database.Connect();

        Action select = () => Query(connection, $"SELECT 'a' = 'b' COLLATE '{tag}';");
        select.Should().Throw<EmbeddedSqlException>()
            .WithMessage($"*no such collation sequence*{tag}*");
    }

    [Test]
    [TestCase("sv")] // Swedish: å/ä/ö sort as distinct primary letters after 'z', not as accented a/o.
    [TestCase("sv-SE")]
    [TestCase("tr")] // Turkish: dotted/dotless I forms are distinct base letters, not a case pair.
    [TestCase("tr-TR")]
    [TestCase("de-u-co-phonebk")] // German phonebook: expands ö/ü/ä into two collation elements.
    [TestCase("de")] // Bare German is also rejected: not a profile this port has verified end-to-end,
                     // even though a spot check suggested default umlaut handling might coincide
                     // with the generic Latin table for some inputs -- that is not the same as a
                     // proven profile, so it stays out of the accepted allowlist.
    [TestCase("fr-CA")] // French Canadian: a real, distinct BCP-47 region this port has not verified;
                       // only fr-FR is an accepted profile.
    [TestCase("fr-BE")]
    [TestCase("fr")] // Bare French (no region) is also rejected: only the exact fr-FR profile
                     // used by the pinned upstream test was verified.
    [TestCase("es-MX")] // Other Spanish regions besides bare "es" are not verified.
    [TestCase("en-US")] // Other English regions besides bare "en" are not verified.
    [TestCase("es-u-co-phonebk")] // Any co value besides "trad" is rejected, even for Spanish.
    [TestCase("en-u-co-trad")] // "trad" itself is only valid paired with Spanish.
    public void KnownUnsupportedOrUnverifiedLocaleProfileFailsClosed(string tag)
    {
        // These are all REAL, well-formed BCP-47 profiles with known or plausible collation
        // behavior this port has not implemented/verified -- they must be rejected outright,
        // never silently approximated with the generic Latin/root comparator (see
        // LocaleCollationTag's accepted-profile allowlist and its remarks).
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile($"locale-unsupported-profile-{Math.Abs(tag.GetHashCode())}.db", fileSystem);
        using var connection = database.Connect();

        Action select = () => Query(connection, $"SELECT 'a' = 'b' COLLATE '{tag}';");
        select.Should().Throw<EmbeddedSqlException>()
            .WithMessage($"*no such collation sequence*{tag}*");
    }

    [Test]
    [TestCase("en-u-zzzzzzzz-kf-upper")] // arbitrary ignored "attribute" subtag before a keyword
    [TestCase("en-u-abc-kf-upper")]      // shorter (3-char) attribute -- still rejected
    [TestCase("en-x-aaaaaaaa")]          // private-use singleton extension
    [TestCase("en-x-a")]
    [TestCase("en-u-zz-abcdefgh")]       // unrecognized 2-letter keyword with an arbitrary type value
    [TestCase("en-u-qq-true")]
    [TestCase("en-t-abc")]               // a non-"u" singleton extension besides private-use
    public void PreviouslyTolerated_ButSemanticallyIgnored_Bcp47ConstructsAreNowRejected(string tag)
    {
        // A review (2026-09-07) found that tolerating these constructs -- while each is
        // individually legal, generic BCP-47 syntax -- let an unbounded family of DISTINCT raw
        // tag strings all resolve successfully to an otherwise-ordinary accepted profile (e.g.
        // varying the ignored attribute or private-use suffix text), reopening the same
        // unbounded-cache-growth concern the rejected-name fix closed, just via inputs that
        // still "worked". LocaleCollationTag.TryParse now rejects all of these outright instead
        // of silently skipping/tolerating them. See LocaleCollationTag and
        // LocaleCollationRegistry's remarks for the complete fix (this rejection, plus caching
        // by canonical semantic identity rather than raw input text).
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile($"locale-ignored-construct-{Math.Abs(tag.GetHashCode())}.db", fileSystem);
        using var connection = database.Connect();

        Action select = () => Query(connection, $"SELECT 'a' = 'b' COLLATE '{tag}';");
        select.Should().Throw<EmbeddedSqlException>()
            .WithMessage($"*no such collation sequence*{tag}*");
    }

    [Test]
    [TestCase("en")]
    [TestCase("es")]
    [TestCase("es-u-co-trad")]
    [TestCase("fr-FR")]
    [TestCase("en-u-kf-upper")]
    [TestCase("en-u-kn-true")]
    [TestCase("en-u-ks-level2")]
    public void AcceptedProfileParsesAndResolvesSuccessfully(string tag)
    {
        // Completeness check for the allowlist itself: every profile this port claims to
        // support must actually parse and resolve to a working comparator, not just avoid
        // throwing for the rejected cases above.
        LocaleCollationTag.TryParse(tag, out var parsed).Should().BeTrue($"'{tag}' is an accepted profile");
        parsed.Should().NotBeNull();
        LocaleCollationRegistry.TryResolve(tag, out var compare).Should().BeTrue();
        compare.Should().NotBeNull();
        compare!("a", "a").Should().Be(0);

        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile($"locale-accepted-profile-{Math.Abs(tag.GetHashCode())}.db", fileSystem);
        using var connection = database.Connect();
        Query(connection, $"SELECT 'a' = 'a' COLLATE '{tag}';")[0][0].AsInteger().Should().Be(1);
    }

    [Test]
    public void LocaleTagIsCaseInsensitiveLikeBcp47Requires()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("locale-case-insensitive.db", fileSystem);
        using var connection = database.Connect();

        Query(connection, "SELECT 'e' < char(233) COLLATE 'FR-fr';")[0][0].AsInteger().Should().Be(1);
        Query(connection, "SELECT 'A' < 'a' COLLATE 'EN-U-KF-UPPER';")[0][0].AsInteger().Should().Be(1);
    }

    [Test]
    public void TraditionalSpanishDigraphOrderingGeneralizesBeyondThePinnedWords()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("locale-es-trad-generalizes.db", fileSystem);
        using var connection = database.Connect();

        // "ll" sorts as a unit strictly between "l"+anything-but-l and "m" — verified upstream
        // against the pinned icu_collator 2.3.1 (see LocaleCollator remarks / session research):
        // es-u-co-trad orders ["polz", "pollo", "poma"].
        Query(
                connection,
                "WITH w(v) AS (VALUES ('poma'), ('pollo'), ('polz')) SELECT v FROM w ORDER BY v COLLATE 'es-u-co-trad';")
            .Select(row => row[0].AsText())
            .Should().Equal("polz", "pollo", "poma");

        // "ch" sorts as a unit strictly between "c"+anything-but-h and "d", the same tailoring
        // applied to a second digraph, not a special case of "ll".
        Query(
                connection,
                "WITH w(v) AS (VALUES ('dado'), ('chico'), ('cuzco')) SELECT v FROM w ORDER BY v COLLATE 'es-u-co-trad';")
            .Select(row => row[0].AsText())
            .Should().Equal("cuzco", "chico", "dado");

        // Without co=trad, "ll"/"ch" are ordinary letter sequences again (plain alphabetical).
        Query(
                connection,
                "WITH w(v) AS (VALUES ('poma'), ('pollo'), ('polz')) SELECT v FROM w ORDER BY v COLLATE 'es';")
            .Select(row => row[0].AsText())
            .Should().Equal("pollo", "polz", "poma");
    }

    [Test]
    public void NumericCollationHandlesArbitraryLengthDigitRunsWithoutOverflow()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("locale-numeric-overflow.db", fileSystem);
        using var connection = database.Connect();

        var huge = new string('9', 25); // far beyond long.MaxValue's 19 digits.
        var hugePlusOne = "1" + new string('0', 25); // one digit longer, so numerically larger.

        Query(connection, $"SELECT '{huge}' < '{hugePlusOne}' COLLATE 'en-u-kn-true';")[0][0].AsInteger()
            .Should().Be(1);
        Query(connection, $"SELECT '{huge}' = '{huge}' COLLATE 'en-u-kn-true';")[0][0].AsInteger()
            .Should().Be(1);

        // Numeric collation is scoped to runs of digits mid-string too, not just whole-value
        // comparisons.
        Query(connection, "SELECT 'a10b' > 'a2b' COLLATE 'en-u-kn-true';")[0][0].AsInteger().Should().Be(1);
        Query(connection, "SELECT 'a10b' > 'a2b' COLLATE 'en';")[0][0].AsInteger().Should().Be(0);
    }

    [Test]
    [TestCase("fr-FR")]
    [TestCase("es-u-co-trad")]
    [TestCase("en-u-kf-upper")]
    [TestCase("en-u-kn-true-kf-upper-ks-level2")]
    public void OrderingIsReflexiveAntisymmetricAndTransitiveForARepresentativeStringSet(string tag)
    {
        LocaleCollationRegistry.TryResolve(tag, out var compare).Should().BeTrue();
        compare.Should().NotBeNull();

        string[] values =
        [
            "", "a", "A", "e", "é", "è", "E", "É", "o", "ò", "O", "ll", "l", "lz", "m",
            "ch", "c", "cz", "d", "1", "2", "10", "20", "100", "z", "Z", "polvo", "pollo",
        ];

        foreach (var value in values)
            compare!(value, value).Should().Be(0, $"'{value}' must compare equal to itself under {tag}");

        foreach (var left in values)
        {
            foreach (var right in values)
            {
                var forward = Math.Sign(compare!(left, right));
                var backward = Math.Sign(compare(right, left));
                forward.Should().Be(-backward, $"'{left}' vs '{right}' under {tag} must be antisymmetric");
            }
        }

        var sorted = (string[])values.Clone();
        Array.Sort(sorted, (left, right) => compare!(left, right));
        for (var i = 0; i < sorted.Length - 1; i++)
        {
            for (var j = i + 1; j < sorted.Length; j++)
            {
                compare!(sorted[i], sorted[j]).Should().BeLessThanOrEqualTo(
                    0,
                    $"sorted position {i} ('{sorted[i]}') must not follow position {j} ('{sorted[j]}') under {tag}");
            }
        }
    }

    [Test]
    public void PersistedIndexWithLocaleCollationSurvivesReopenAndMutationWithoutReregistration()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "locale-index-reopen.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE locale_words(id INTEGER PRIMARY KEY, word TEXT);");
            Execute(connection, "CREATE INDEX locale_words_es_trad ON locale_words(word COLLATE 'es-u-co-trad');");
            Execute(connection, "INSERT INTO locale_words VALUES (1, 'pollo'), (2, 'polvo'), (3, 'zorro');");

            Query(connection, "SELECT id FROM locale_words WHERE word > 'polvo' COLLATE 'es-u-co-trad' ORDER BY id;")
                .Select(row => row[0].AsInteger())
                .Should().Equal(1, 3);
        }

        // A locale collation is a pure function of its BCP-47 tag, never an application-registered
        // callback, so — unlike CustomCollationIndexTests's custom collations — a reopened
        // connection needs no re-registration step at all for the persisted index to keep working.
        using (var reopened = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var reopenedConnection = reopened.Connect())
        {
            Query(reopenedConnection, "SELECT id FROM locale_words WHERE word > 'polvo' COLLATE 'es-u-co-trad' ORDER BY id;")
                .Select(row => row[0].AsInteger())
                .Should().Equal(1, 3);

            // Mutate after reopen: insert a row that lands between the digraph-tailored entries,
            // and delete one, then confirm the durable index still orders/filters correctly.
            Execute(reopenedConnection, "INSERT INTO locale_words VALUES (4, 'polz');");
            Execute(reopenedConnection, "DELETE FROM locale_words WHERE id = 3;");

            Query(reopenedConnection, "SELECT id FROM locale_words WHERE word > 'polvo' COLLATE 'es-u-co-trad' ORDER BY id;")
                .Select(row => row[0].AsInteger())
                .Should().Equal(1, 4);

            Execute(reopenedConnection, "REINDEX locale_words_es_trad;");
            Query(reopenedConnection, "SELECT id FROM locale_words WHERE word > 'polvo' COLLATE 'es-u-co-trad' ORDER BY id;")
                .Select(row => row[0].AsInteger())
                .Should().Equal(1, 4);
        }
    }

    [Test]
    [TestCase("fr-FR")]
    [TestCase("en")]
    [TestCase("es")]
    public void PrecomposedAccentedLetterEqualsItsCanonicallyDecomposedSpelling(string tag)
    {
        // The specific defect a review found: comparing 'e' followed by a standalone
        // combining acute accent (U+0301) to the precomposed 'é' (U+00E9) must report
        // them equal, not merely "close" -- verified against the pinned icu_collator
        // 2.3.1 (see LocaleCollationWeights remarks).
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile($"locale-nfd-equiv-{tag}.db", fileSystem);
        using var connection = database.Connect();

        Query(connection, $"SELECT char(233) = 'e' || char(769) COLLATE '{tag}';")[0][0].AsInteger()
            .Should().Be(1, "precomposed é (U+00E9) must equal decomposed e+combining-acute (U+0301)");
        Query(connection, $"SELECT 'e' || char(769) < 'e' || char(776) COLLATE '{tag}';")[0][0].AsInteger()
            .Should().Be(1, "decomposed e+acute must still order correctly relative to decomposed e+diaeresis");

        // A broader set of precomposed/decomposed pairs (grave, circumflex, cedilla, tilde).
        var pairs = new (int Precomposed, int Base, int Mark)[]
        {
            (0x00E8, 'e', 0x0300), // è = e + grave
            (0x00EA, 'e', 0x0302), // ê = e + circumflex
            (0x00E7, 'c', 0x0327), // ç = c + cedilla
            (0x00FC, 'u', 0x0308), // ü = u + diaeresis
        };
        foreach (var (precomposed, baseLetter, mark) in pairs)
        {
            Query(connection, $"SELECT char({precomposed}) = char({(int)baseLetter}) || char({mark}) COLLATE '{tag}';")[0][0]
                .AsInteger().Should().Be(1, $"U+{precomposed:X4} must equal its decomposed spelling under {tag}");
        }
    }

    [Test]
    public void PrecomposedAndDecomposedSpellingsCompareEqualViaTheSharedCompareDelegate()
    {
        // WriteSortKey (a separate byte-key encoding) was removed after a review found
        // it broke the lexical contract for same-prefix/different-length inputs (see
        // LocaleCollator's removal note). Persisted index ordering and equality both
        // go through this single Compare delegate instead -- verify it treats
        // canonically equivalent spellings as equal directly, not through a sort key.
        LocaleCollationRegistry.TryResolve("fr-FR", out var compare).Should().BeTrue();
        compare.Should().NotBeNull();
        compare!("\u00e9", "e\u0301").Should().Be(0);
        compare("\u00e8", "e\u0300").Should().Be(0);
    }

    [Test]
    public void SpanishEnyeIsADistinctPrimaryLetterBetweenNAndO()
    {
        // Verified against the pinned collator for BOTH es (modern/default) and
        // es-u-co-trad: ñ is a genuine distinct primary letter positioned between all
        // n-prefixed sequences and 'o' -- not merely a secondary-level accent on 'n'.
        // "nz" < "ña" (which would be false under a plain secondary-only accent model,
        // since z's primary weight exceeds a's) proves this, for both collation types.
        foreach (var tag in new[] { "es", "es-u-co-trad" })
        {
            var fileSystem = new InMemoryFileSystem();
            using var database = EmbeddedDatabase.OpenFile($"locale-enye-{tag}.db", fileSystem);
            using var connection = database.Connect();

            Query(connection, $"SELECT 'nz' < 'ña' COLLATE '{tag}';")[0][0].AsInteger().Should().Be(1);
            Query(connection, $"SELECT 'ñz' < 'oa' COLLATE '{tag}';")[0][0].AsInteger().Should().Be(1);
            Query(connection, $"SELECT 'n' < 'ñ' COLLATE '{tag}';")[0][0].AsInteger().Should().Be(1);
            Query(connection, $"SELECT 'ñ' < 'o' COLLATE '{tag}';")[0][0].AsInteger().Should().Be(1);

            Query(
                    connection,
                    $"WITH w(v) AS (VALUES ('ana'), ('aña'), ('anzo'), ('año')) SELECT v FROM w ORDER BY v COLLATE '{tag}';")
                .Select(row => row[0].AsText())
                .Should().Equal("ana", "anzo", "aña", "año");

            // The decomposed spelling ("n" + combining tilde) must receive the SAME
            // Spanish tailoring as the precomposed ñ codepoint.
            Query(connection, $"SELECT 'nz' < 'n' || char(771) || 'a' COLLATE '{tag}';")[0][0].AsInteger()
                .Should().Be(1, "decomposed ñ (n + combining tilde) must be tailored identically to precomposed ñ");
        }
    }

    [Test]
    public void SpanishEnyeDoesNotApplyToOtherLanguages()
    {
        // Outside Spanish, ñ must behave as an ordinary accented 'n' (secondary
        // difference only) -- verified against the pinned collator for 'en'.
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("locale-enye-not-spanish.db", fileSystem);
        using var connection = database.Connect();

        Query(connection, "SELECT 'nz' > 'ña' COLLATE 'en';")[0][0].AsInteger()
            .Should().Be(1, "under a non-Spanish tag, ñ must NOT receive the distinct-primary-letter tailoring");
    }

    [Test]
    public void VerifiedAsciiPunctuationOrderIsNotCodepointOrder()
    {
        // A prior version of this port fell back to code point order for ASCII
        // punctuation; that was verified WRONG against the pinned collator (e.g. '-'
        // sorts before ',' under real UCA, which code point order contradicts).
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("locale-punctuation-order.db", fileSystem);
        using var connection = database.Connect();

        Query(connection, "SELECT '-' < ',' COLLATE 'en';")[0][0].AsInteger()
            .Should().Be(1, "verified UCA order places '-' before ',' -- code point order would say the opposite");
        Query(connection, "SELECT ' ' < '_' COLLATE 'en';")[0][0].AsInteger().Should().Be(1);
        Query(connection, "SELECT '_' < '-' COLLATE 'en';")[0][0].AsInteger().Should().Be(1);

        // Punctuation sorts before digits, which sort before letters (verified
        // reordering-group boundaries).
        Query(connection, "SELECT '-' < '5' COLLATE 'en';")[0][0].AsInteger().Should().Be(1);
        Query(connection, "SELECT '5' < 'a' COLLATE 'en';")[0][0].AsInteger().Should().Be(1);

        // The full verified order round-trips through ORDER BY.
        var expected = new[] { " ", "_", "-", ",", ";", ":", "!", "?", ".", "'", "\"", "(", ")", "[", "]", "{", "}",
            "@", "*", "/", "\\", "&", "#", "%", "`", "^", "+", "<", "=", ">", "|", "~", "$" };
        var values = string.Join(", ", expected.Select(ch => $"('{ch.Replace("'", "''")}')"));
        Query(connection, $"WITH w(v) AS (VALUES {values}) SELECT v FROM w ORDER BY v COLLATE 'en';")
            .Select(row => row[0].AsText())
            .Should().Equal(expected);
    }

    [Test]
    [TestCase(0x4E2D)] // Han '中'
    [TestCase(0x03B1)] // Greek alpha
    [TestCase(0x0430)] // Cyrillic 'а'
    [TestCase(0x00E6)] // æ (no real decomposition equivalence -- verified against the pinned collator)
    [TestCase(0x00DF)] // ß (no real decomposition equivalence)
    [TestCase(0x00F8)] // ø (no verified weight-table position; deliberately excluded)
    [TestCase(0x0009)] // TAB control character
    public void OutOfRepertoireCharacterFailsClosedRatherThanSilentlyCodepointSorting(int codepoint)
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile($"locale-unsupported-char-{codepoint:X4}.db", fileSystem);
        using var connection = database.Connect();

        // Matches the existing propagate-raw-exception contract application-defined
        // collation callbacks already use (see CustomCollationIndexTests
        // .CallbackExceptionPropagatesThroughIndexSeek): the failure is not wrapped
        // into an EmbeddedSqlException, it surfaces as-is.
        Action select = () => Query(connection, $"SELECT char({codepoint}) = char({codepoint}) COLLATE 'en';");
        select.Should().Throw<NotSupportedException>();
    }

    [Test]
    public void UnsupportedCharacterFailsClosedInPersistedIndexWrite()
    {
        // The fail-closed behavior must also protect the durable index writer, not
        // just ad hoc scalar comparisons: inserting a second, out-of-repertoire row
        // that must be positioned against an existing row in a locale-collated index
        // must fail rather than silently accepting a codepoint-ordered (and
        // therefore wrong) key.
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("locale-unsupported-index-write.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(v TEXT);");
        Execute(connection, "CREATE INDEX t_v ON t(v COLLATE 'en');");
        Execute(connection, "INSERT INTO t VALUES ('a');");

        Action insert = () => Execute(connection, "INSERT INTO t VALUES (char(0x4E2D));");
        insert.Should().Throw<NotSupportedException>();
    }

    [Test]
    public void SamePrefixDifferentLengthComparesShorterAsLess()
    {
        // Regression counterexample for the WriteSortKey defect a review found (see
        // LocaleCollator's removal note): "ab" and "ab " tie on their shared 2-element
        // prefix ('a','b') but differ in element count (a trailing space is its own
        // punctuation element), so the shorter string -- being a true tied prefix --
        // must sort first. This must hold through the single Compare delegate that
        // now backs every comparison and persisted-index ordering path.
        LocaleCollationRegistry.TryResolve("en", out var compare).Should().BeTrue();
        compare.Should().NotBeNull();
        compare!("ab", "ab ").Should().BeLessThan(0);
        compare("ab ", "ab").Should().BeGreaterThan(0);
        compare("ab", "ab").Should().Be(0);

        // The same property must hold end to end through SQL comparisons and a
        // persisted, locale-collated index ORDER BY -- not just the bare delegate.
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("locale-prefix-length-tiebreak.db", fileSystem);
        using var connection = database.Connect();
        Query(connection, "SELECT 'ab' < 'ab ' COLLATE 'en';")[0][0].AsInteger().Should().Be(1);

        Execute(connection, "CREATE TABLE t(v TEXT);");
        Execute(connection, "CREATE INDEX t_v ON t(v COLLATE 'en');");
        Execute(connection, "INSERT INTO t VALUES ('ab '), ('ab'), ('ac');");
        Query(connection, "SELECT v FROM t ORDER BY v COLLATE 'en';")
            .Select(row => row[0].AsText())
            .Should().Equal("ab", "ab ", "ac");
    }

    [Test]
    public void RejectedArbitraryNamesAreNeverCached()
    {
        // A review found the registry cache had no bound: every distinct rejected
        // name (structurally malformed, or syntactically valid but outside the
        // accepted-profile allowlist) was cached forever, letting a stream of
        // distinct garbage COLLATE names grow the process-wide dictionary without
        // limit. Only successful (accepted-profile) resolutions may be cached now;
        // verify a large batch of distinct rejected names leaves the cache
        // completely unaffected.
        var before = LocaleCollationRegistry.CachedEntryCount;
        for (var i = 0; i < 500; i++)
        {
            LocaleCollationRegistry.TryResolve($"not-a-real-locale-{i}-garbage", out var compare)
                .Should().BeFalse();
            compare.Should().BeNull();
        }

        LocaleCollationRegistry.CachedEntryCount.Should().Be(
            before,
            "rejected names must never grow the cache, regardless of how many distinct ones are queried");

        // A subsequent resolution of an ALREADY-rejected name is recomputed (not
        // remembered as a permanent failure) rather than, say, throwing a stale
        // cached exception -- confirm it still correctly fails every time.
        LocaleCollationRegistry.TryResolve("not-a-real-locale-0-garbage", out var stillRejected).Should().BeFalse();
        stillRejected.Should().BeNull();

        // A genuinely accepted profile still gets cached normally (the allowlist
        // itself is finite, so this is bounded growth, unlike arbitrary rejections).
        // Uses a tag not exercised elsewhere in this test class, and checks
        // idempotency (a second resolution of the same tag does not grow the
        // cache further) rather than an absolute count relative to other tests,
        // since LocaleCollationRegistry is a process-wide singleton shared with
        // every other test in this run.
        const string uniqueAcceptedTag = "es-u-co-trad-kf-upper-kn-true-ks-level4";
        var beforeAccepted = LocaleCollationRegistry.CachedEntryCount;
        LocaleCollationRegistry.TryResolve(uniqueAcceptedTag, out var accepted).Should().BeTrue();
        accepted.Should().NotBeNull();
        LocaleCollationRegistry.CachedEntryCount.Should().Be(beforeAccepted + 1);

        LocaleCollationRegistry.TryResolve(uniqueAcceptedTag, out var acceptedAgain).Should().BeTrue();
        acceptedAgain.Should().NotBeNull();
        LocaleCollationRegistry.CachedEntryCount.Should().Be(
            beforeAccepted + 1,
            "resolving the same accepted tag again must not grow the cache further");
    }

    [Test]
    public void ThousandSemanticallyEquivalentAcceptedSpellingsShareOneCacheEntry()
    {
        // A follow-up review (2026-09-07) found the previous fix incomplete: even restricting
        // caching to successfully-ACCEPTED tags left the cache unbounded, because
        // LocaleCollationTag.TryParse (at the time) still tolerated arbitrary ignored BCP-47
        // attributes, unrecognized keywords, and a private-use suffix -- all now rejected
        // outright (see PreviouslyTolerated_ButSemanticallyIgnored_Bcp47ConstructsAreNowRejected)
        // -- and, independent of that, BCP-47 keyword ORDER is legitimately unconstrained and
        // can't simply be rejected. Generate 1000 distinct RAW spellings of the exact same
        // semantic profile (varying keyword order among co/kf/kn, an inert ks value, and random
        // per-character casing) and verify they all resolve successfully to equivalently-behaving
        // comparators while adding only ONE new cache entry -- the canonical semantic identity,
        // never one per raw spelling.
        const string canonicalIdentity = "es-u-co-trad-kf-lower-kn-true";
        var keywordOrders = new[]
        {
            "co-trad-kf-lower-kn-true",
            "kf-lower-co-trad-kn-true",
            "kn-true-co-trad-kf-lower",
            "co-trad-kn-true-kf-lower",
            "kf-lower-kn-true-co-trad",
            "kn-true-kf-lower-co-trad",
        };
        var ksSuffixes = new[] { "", "-ks-level1", "-ks-level2", "-ks-level3", "-ks-level4", "-ks-identic" };

        var before = LocaleCollationRegistry.CachedEntryCount;
        var resolvedCount = 0;
        for (var i = 0; i < 1000; i++)
        {
            var order = keywordOrders[i % keywordOrders.Length];
            var ks = ksSuffixes[(i / keywordOrders.Length) % ksSuffixes.Length];
            var raw = RandomizeCase($"es-u-{order}{ks}", seed: i);

            LocaleCollationRegistry.TryResolve(raw, out var compare)
                .Should().BeTrue($"'{raw}' is a legal spelling of the same accepted profile");
            compare.Should().NotBeNull();
            // "pollo" > "polvo" under the Spanish traditional digraph tailoring must hold
            // regardless of how this particular spelling of the profile was written.
            compare!("pollo", "polvo").Should().BeGreaterThan(0);
            resolvedCount++;
        }

        resolvedCount.Should().Be(1000);

        LocaleCollationRegistry.CachedEntryCount.Should().Be(
            before + 1,
            $"every one of the 1000 spellings must collapse onto the single canonical entry '{canonicalIdentity}'");
    }

    private static string RandomizeCase(string value, int seed)
    {
        var random = new Random(seed);
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsAsciiLetter(chars[i]) && random.Next(2) == 0)
                chars[i] = char.ToUpperInvariant(chars[i]);
        }

        return new string(chars);
    }

    [Test]
    [TestCase("level1")]
    [TestCase("level2")]
    [TestCase("level3")]
    [TestCase("level4")]
    [TestCase("identic")]
    public void KsValueNeverSuppressesCaseOrAccentDistinctionsForAnyEnumeratedValue(string ksValue)
    {
        // Broader evidence matrix for the "ks is parsed but inert" claim (see
        // LocaleCollationTag remarks): every enumerated ks value must still
        // distinguish a primary difference, a secondary (accent) difference, AND a
        // tertiary (case) difference -- not just the two values a prior version of
        // this port's comments cited as examples.
        var tag = $"en-u-ks-{ksValue}";
        LocaleCollationRegistry.TryResolve(tag, out var compare).Should().BeTrue();
        compare.Should().NotBeNull();
        compare!("a", "b").Should().BeLessThan(0, $"primary difference under ks={ksValue}");
        compare("a", "\u00e1").Should().BeLessThan(0, $"accent (secondary) difference under ks={ksValue}");
        compare("a", "A").Should().BeLessThan(0, $"case (tertiary) difference under ks={ksValue}");

        // Composed with kf=upper, case ordering must still correctly reverse under
        // every ks value (not just verified in isolation).
        LocaleCollationRegistry.TryResolve($"en-u-kf-upper-ks-{ksValue}", out var compareUpperFirst).Should().BeTrue();
        compareUpperFirst.Should().NotBeNull();
        compareUpperFirst!("a", "A").Should().BeGreaterThan(0, $"kf=upper composed with ks={ksValue}");
    }

    [Test]
    [TestCase("9", "10")]
    [TestCase("99", "100")]
    [TestCase("a9b", "a10b")]
    [TestCase("file9", "file10")]
    [TestCase("v1.9", "v1.10")]
    public void NumericFoldingOrdersConsistentlyAloneAndComposedWithKfAndKs(string smaller, string larger)
    {
        // Broader evidence matrix for kn=true numeric folding: each case is checked
        // both in isolation and under the exact compound tag the pinned upstream
        // sqltest corpus uses, so numeric folding is verified to compose correctly
        // with case-first and the (inert) strength keyword, not just standalone.
        LocaleCollationRegistry.TryResolve("en-u-kn-true", out var plain).Should().BeTrue();
        plain.Should().NotBeNull();
        plain!(smaller, larger).Should().BeLessThan(0);

        LocaleCollationRegistry.TryResolve("en-u-kn-true-kf-upper-ks-level2", out var compound).Should().BeTrue();
        compound.Should().NotBeNull();
        compound!(smaller, larger).Should().BeLessThan(0);
    }

    [Test]
    [TestCase("0", "00")]
    [TestCase("007", "7")]
    [TestCase("0001", "1")]
    public void NumericFoldingTreatsLeadingZeroVariantsAsEqualAloneAndComposedWithKfAndKs(string left, string right)
    {
        // Leading zeros must fold to the SAME numeric magnitude (equal), both in
        // isolation and under the exact compound tag the pinned upstream sqltest
        // corpus uses.
        LocaleCollationRegistry.TryResolve("en-u-kn-true", out var plain).Should().BeTrue();
        plain.Should().NotBeNull();
        plain!(left, right).Should().Be(0);

        LocaleCollationRegistry.TryResolve("en-u-kn-true-kf-upper-ks-level2", out var compound).Should().BeTrue();
        compound.Should().NotBeNull();
        compound!(left, right).Should().Be(0);
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static List<SqlValue[]> Query(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var values = new SqlValue[statement.GetColumnCount()];
            for (var ordinal = 0; ordinal < values.Length; ordinal++)
                values[ordinal] = statement.GetValue(ordinal);
            rows.Add(values);
        }

        return rows;
    }
}

