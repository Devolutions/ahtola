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
    public void SyntacticallyValidButUnknownLanguageFallsBackToRootOrdering()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("locale-unknown-language.db", fileSystem);
        using var connection = database.Connect();

        // "zzz" is not a registered BCP-47 language, but it is syntactically valid, so it must
        // resolve (root-equivalent ordering) rather than fail closed — mirroring Turso's own
        // fallback to the CLDR root collation for an unrecognized language.
        Query(connection, "SELECT 'a' < 'b' COLLATE 'zzz';")[0][0].AsInteger().Should().Be(1);

        // The "kn" keyword must still take effect under an unknown language.
        Query(connection, "SELECT '10' > '2' COLLATE 'zzz-u-kn-true';")[0][0].AsInteger().Should().Be(1);
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
