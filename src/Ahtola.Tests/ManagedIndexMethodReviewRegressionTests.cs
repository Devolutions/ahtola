using Ahtola.Core;
using Ahtola.Core.Indexing;
using Ahtola.Core.Search;
using AwesomeAssertions;
using static Ahtola.Tests.ManagedIndexMethodTestHarness;

namespace Ahtola.Tests;

/// <summary>
/// Concrete reproducers for the managed FTS / index-method review findings, each paired with a
/// negative control so a future regression cannot pass by disabling the feature entirely.
/// </summary>
public sealed class ManagedIndexMethodReviewRegressionTests
{
    // ---------------------------------------------------------------------------------------
    // Finding 1: postings must be generation stamped so a reused rowid cannot keep stale terms.
    // ---------------------------------------------------------------------------------------

    [Test]
    public void UpdatingARowRetiresItsPreviousTerms()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);
        Execute(connection, CreateFtsIndex);
        Execute(connection, "INSERT INTO docs VALUES (1, 'alpha heading', 'alpha body');");

        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'alpha');")
            .Should().Equal(1);

        Execute(connection, "UPDATE docs SET title = 'beta heading', body = 'beta body' WHERE id = 1;");

        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'alpha');")
            .Should().BeEmpty("the previous image's terms must not stay live for a reused rowid");
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'beta');")
            .Should().Equal(1);
    }

    [Test]
    public void DeletingAndReinsertingTheSameRowidDoesNotResurrectOldTerms()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);
        Execute(connection, CreateFtsIndex);
        Execute(connection, "INSERT INTO docs VALUES (7, 'gamma', 'gamma body');");
        Execute(connection, "DELETE FROM docs WHERE id = 7;");
        Execute(connection, "INSERT INTO docs VALUES (7, 'delta', 'delta body');");

        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'gamma');")
            .Should().BeEmpty();
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'delta');")
            .Should().Equal(7);
    }

    [Test]
    public void ReplaceAndUpsertRetireTheReplacedImage()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);
        Execute(connection, CreateFtsIndex);
        Execute(connection, "INSERT INTO docs VALUES (1, 'first', 'first body');");
        Execute(connection, "INSERT OR REPLACE INTO docs VALUES (1, 'second', 'second body');");

        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'first');").Should().BeEmpty();
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'second');").Should().Equal(1);

        Execute(
            connection,
            "INSERT INTO docs VALUES (1, 'third', 'third body') ON CONFLICT(id) DO UPDATE SET title = excluded.title, body = excluded.body;");

        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'second');").Should().BeEmpty();
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'third');").Should().Equal(1);
    }

    [Test]
    public void PostingGenerationsAreVisibleAtTheIndexLevel()
    {
        var index = new ManagedFtsSearchIndex(1, ManagedFtsTokenizerOptions.Default, [1.0]);
        var alpha = ManagedFtsQueryLanguage.Parse("alpha", ManagedFtsTokenizerOptions.Default, static _ => true);
        var beta = ManagedFtsQueryLanguage.Parse("beta", ManagedFtsTokenizerOptions.Default, static _ => true);

        index.Upsert(1, [], [SqlValue.Text("alpha")]);
        index.Matches(alpha, 1).Should().BeTrue();

        // No compaction in between: the superseded posting is still physically present and must be
        // invisible purely because its generation no longer matches the live document.
        index.Upsert(1, [], [SqlValue.Text("beta")]);
        index.TombstonedPostings.Should().BeGreaterThan(0);
        index.Matches(alpha, 1).Should().BeFalse();
        index.Matches(beta, 1).Should().BeTrue();
    }

    [Test]
    public void AFailedUpsertLeavesTheIndexUnchanged()
    {
        var index = new ManagedFtsSearchIndex(1, ManagedFtsTokenizerOptions.Default, [1.0]);
        var alpha = ManagedFtsQueryLanguage.Parse("alpha", ManagedFtsTokenizerOptions.Default, static _ => true);
        index.Upsert(1, [], [SqlValue.Text("alpha")]);

        // Wrong column count: the failure has to happen before any state is mutated, or the old
        // image would already have been removed.
        var act = () => index.Upsert(1, [], [SqlValue.Text("beta"), SqlValue.Text("extra")]);
        act.Should().Throw<ArgumentException>();

        index.Matches(alpha, 1).Should().BeTrue();
        index.DocumentCount.Should().Be(1);
    }

    // ---------------------------------------------------------------------------------------
    // Finding 2: score-only ordering must keep rows the method did not rank.
    // ---------------------------------------------------------------------------------------

    [Test]
    public void ScoreOrderedLimitKeepsUnrankedRowsWhenThereAreFewerHitsThanTheLimit()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedSparseCorpus(connection);

        // Exactly one document mentions 'needle'; a LIMIT of 5 must still yield five rows.
        var withIndex = QueryIntegers(
            connection,
            "SELECT id FROM docs ORDER BY fts_score(title, body, 'needle') DESC LIMIT 5;");
        withIndex.Should().HaveCount(5);
        withIndex[0].Should().Be(1);

        Execute(connection, "DROP INDEX docs_fts;");
        QueryIntegers(connection, "SELECT id FROM docs ORDER BY fts_score(title, body, 'needle') DESC LIMIT 5;")
            .Should().HaveCount(5);
    }

    [Test]
    public void ScoreOrderingWithoutALimitKeepsEveryBaseRow()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedSparseCorpus(connection);

        var total = QueryIntegers(connection, "SELECT count(*) FROM docs;")[0];
        QueryIntegers(connection, "SELECT id FROM docs ORDER BY fts_score(title, body, 'needle') DESC;")
            .Should().HaveCount((int)total);
    }

    [Test]
    public void MatchPatternsStillFilter()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedSparseCorpus(connection);

        // Negative control for the previous two: a MATCH predicate genuinely removes rows, so the
        // zero-score retention rule must not leak into it.
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'needle');")
            .Should().Equal(1);
    }

    // ---------------------------------------------------------------------------------------
    // Finding 3: scalar binding follows the resolved source, not column-name similarity.
    // ---------------------------------------------------------------------------------------

    [Test]
    public void AnUnrelatedTablesIndexDoesNotChangeScalarBehavior()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE plain(id INTEGER PRIMARY KEY, title TEXT, body TEXT);");
        Execute(connection, "INSERT INTO plain VALUES (1, 'shared word', 'shared word body');");

        var before = QueryReals(connection, "SELECT fts_score(title, body, 'shared') FROM plain;");

        // A method index on a *different* table with identically named columns must not touch the
        // meaning of this call.
        Execute(connection, "CREATE TABLE notes(id INTEGER PRIMARY KEY, title TEXT, body TEXT);");
        Execute(connection, "CREATE INDEX notes_fts ON notes USING fts (title, body);");
        Execute(connection, "INSERT INTO notes VALUES (1, 'shared word', 'shared word body'), (2, 'shared', 'shared');");

        QueryReals(connection, "SELECT fts_score(title, body, 'shared') FROM plain;").Should().Equal(before);
    }

    [Test]
    public void JoinedRowsScoreTheirOwnSource()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);
        Execute(connection, CreateFtsIndex);
        Execute(
            connection,
            """
            INSERT INTO docs VALUES
              (1, 'needle here', 'needle body'),
              (2, 'nothing', 'nothing at all');
            """);
        Execute(connection, "CREATE TABLE tags(doc_id INTEGER, tag TEXT);");
        Execute(connection, "INSERT INTO tags VALUES (1, 'a'), (2, 'b');");

        var scores = QueryReals(
            connection,
            "SELECT fts_score(d.title, d.body, 'needle') FROM docs AS d JOIN tags AS t ON t.doc_id = d.id ORDER BY d.id;");

        scores.Should().HaveCount(2);
        scores[0].Should().BeGreaterThan(0.0);
        scores[1].Should().Be(0.0);
    }

    [Test]
    public void AnAliasedSourceStillBindsToItsOwnIndex()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);
        Execute(connection, CreateFtsIndex);
        Execute(connection, "INSERT INTO docs VALUES (1, 'needle', 'needle body'), (2, 'other', 'other body');");

        QueryReals(connection, "SELECT fts_score(d.title, d.body, 'needle') FROM docs AS d ORDER BY d.id;")
            .Should().HaveCount(2);
        QueryIntegers(connection, "SELECT d.id FROM docs AS d WHERE fts_match(d.title, d.body, 'needle');")
            .Should().Equal(1);
    }

    // ---------------------------------------------------------------------------------------
    // Finding 4: a shadowing scalar callback suppresses method planning and index-aware scalars.
    // ---------------------------------------------------------------------------------------

    [Test]
    public void AShadowingScalarCallbackSuppressesMethodPlanning()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedSparseCorpus(connection);

        // Negative control: the index is used before the callback exists.
        ExplainDetail(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'needle');")
            .Should().Contain("INDEX METHOD");

        connection.RegisterScalarFunction("fts_match", -1, static _ => SqlValue.Integer(1));

        ExplainDetail(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'needle');")
            .Should().NotContain("INDEX METHOD");

        // The user's callback wins for every row, which is exactly what shadowing means.
        var total = QueryIntegers(connection, "SELECT count(*) FROM docs;")[0];
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'needle');")
            .Should().HaveCount((int)total);
    }

    [Test]
    public void AShadowingScalarCallbackSuppressesCorpusAwareScoring()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedSparseCorpus(connection);

        connection.RegisterScalarFunction("fts_score", -1, static _ => SqlValue.Real(42.0));

        QueryReals(connection, "SELECT fts_score(title, body, 'needle') FROM docs WHERE id = 1;")
            .Should().Equal(42.0);
        ExplainDetail(connection, "SELECT id FROM docs ORDER BY fts_score(title, body, 'needle') DESC LIMIT 3;")
            .Should().NotContain("INDEX METHOD");
    }

    // ---------------------------------------------------------------------------------------
    // Finding 5: Turso exposes every FTS scalar as deterministic.
    // ---------------------------------------------------------------------------------------

    [Test]
    public void DeterministicRegistryAllowsStoredScoringExceptForLiveCheckConstraints()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);

        Execute(connection, "CREATE INDEX score_expression ON docs(fts_score(title, body, 'x'));");
        Execute(connection, "CREATE INDEX score_partial ON docs(id) WHERE fts_score(title, body, 'x') > 0;");
        Execute(
            connection,
            "CREATE TABLE gen(id INTEGER PRIMARY KEY, a TEXT, b TEXT, s REAL GENERATED ALWAYS AS (fts_score(a, b, 'x')) VIRTUAL);");
        ShouldThrow(
            connection,
            "CREATE TABLE chk(id INTEGER PRIMARY KEY, a TEXT, b TEXT, CHECK (fts_score(a, b, 'x') >= 0));")
            .Message.Should().Contain("non-deterministic functions are prohibited in CHECK constraints");
    }

    [Test]
    public void SchemaExpressionsStillAcceptRowLocalMatching()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        // Negative control: fts_match is a pure function of its arguments and stays usable.
        Execute(
            connection,
            "CREATE TABLE gen(id INTEGER PRIMARY KEY, a TEXT, b TEXT, m INTEGER GENERATED ALWAYS AS (fts_match(a, b, 'x')) VIRTUAL);");
        Execute(connection, "INSERT INTO gen(id, a, b) VALUES (1, 'x y', 'z');");
        QueryIntegers(connection, "SELECT m FROM gen;").Should().Equal(1);
    }

    // ---------------------------------------------------------------------------------------
    // Finding 6: attachments are cached only after publication and dropped on every failure.
    // ---------------------------------------------------------------------------------------

    [Test]
    public void AFailedCreateLeavesNoCachedAttachment()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);
        Execute(connection, "INSERT INTO docs VALUES (1, 'alpha', 'alpha body');");

        ShouldThrow(connection, "CREATE INDEX docs_fts ON docs USING fts (title, body) WITH (bogus = 1);")
            .Message.Should().Contain("unknown fts index parameter");

        // A cached half-built attachment would make this second attempt reuse the rejected options.
        Execute(connection, "CREATE INDEX docs_fts ON docs USING fts (title, body) WITH (tokenizer = 'unicode61');");
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'alpha');").Should().Equal(1);
    }

    [Test]
    public void RecreatingAnIndexUnderTheSameNameAppliesTheNewOptions()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);
        Execute(connection, "CREATE INDEX docs_fts ON docs USING fts (title, body) WITH (tokenizer = 'unicode61');");
        Execute(connection, "INSERT INTO docs VALUES (1, 'Ünïcode', 'accented body');");

        // The explicitly named Ahtola unicode61 extension folds accents.
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'unicode');").Should().Equal(1);

        Execute(connection, "DROP INDEX docs_fts;");
        Execute(connection, "CREATE INDEX docs_fts ON docs USING fts (title, body) WITH (tokenizer = 'raw');");

        // raw does not fold at all: the same query must now miss, proving the new options took hold
        // instead of a stale attachment being resurrected under the reused name.
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'unicode');").Should().BeEmpty();
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'Ünïcode');").Should().Equal(1);
    }

    // ---------------------------------------------------------------------------------------
    // Finding 7: REINDEX and OPTIMIZE publish atomically.
    // ---------------------------------------------------------------------------------------

    [Test]
    public void ReindexRebuildsWithoutLosingRows()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedCorpus(connection);

        Execute(connection, "REINDEX docs;");

        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'fox') ORDER BY id;")
            .Should().Equal(1, 3);
    }

    [Test]
    public void OptimizePublishesACompactedIndexAtomically()
    {
        var configuration = new ManagedIndexMethodConfiguration(
            "t",
            "t_fts",
            [new ManagedIndexMethodColumn("body", 0)],
            []);
        var attachment = (ManagedFtsIndexAttachment)ManagedIndexMethodRegistry.Resolve("fts").Attach(configuration);
        var source = ArrayManagedIndexSource.FromText((1, "alpha"), (2, "beta"), (3, "gamma"));
        using var cursor = attachment.Open(source);
        cursor.OpenRead();

        source.Remove(2);
        cursor.OpenRead();
        var before = attachment.Index;

        cursor.Optimize();

        attachment.Index.Should().NotBeSameAs(before, "optimize publishes a freshly built posting set");
        attachment.Index.DocumentCount.Should().Be(2);
        attachment.Index.TombstonedPostings.Should().Be(0);
    }

    // ---------------------------------------------------------------------------------------
    // Finding 8: state envelopes decode only for method declarations and are bounded first.
    // ---------------------------------------------------------------------------------------

    [Test]
    public void AnOrdinaryIndexCommentIsNeverTreatedAsAStateEnvelope()
    {
        var ordinary = "CREATE INDEX \"i\" ON \"t\" (\"body\") /*ahtola-index-method:1:AQID*/";

        ManagedIndexMethodStateSql.TrySplit(
                ordinary,
                ManagedIndexMethodSemantics.IsMethodIndexDeclaration,
                out var declaration,
                out var version,
                out var state)
            .Should().BeFalse();
        declaration.Should().Be(ordinary);
        version.Should().Be(0);
        state.Should().BeEmpty();
    }

    [Test]
    public void AMethodIndexEnvelopeIsSplit()
    {
        var declaration = "CREATE INDEX \"i\" ON \"t\" USING fts (\"body\")";
        var encoded = ManagedIndexMethodStateSql.Append(declaration, 1, [1, 2, 3]);

        ManagedIndexMethodStateSql.TrySplit(
                encoded,
                ManagedIndexMethodSemantics.IsMethodIndexDeclaration,
                out var parsed,
                out var version,
                out var state)
            .Should().BeTrue();
        parsed.Should().Be(declaration);
        version.Should().Be(1);
        state.Should().Equal([1, 2, 3]);
    }

    [Test]
    public void AnOversizedEncodedEnvelopeIsRejectedBeforeItIsDecoded()
    {
        var declaration = "CREATE INDEX \"i\" ON \"t\" USING fts (\"body\")";
        var payload = new string('A', ManagedIndexMethodLimits.MaxStateEncodedChars + 4);
        var oversized = declaration + " /*ahtola-index-method:1:" + payload + "*/";

        var act = () => ManagedIndexMethodStateSql.Split(oversized);
        act.Should().Throw<EmbeddedSqlException>().WithMessage("*exceeds its maximum size*");
    }

    [Test]
    public void AnEmptyEnvelopePayloadIsRejected()
    {
        var declaration = "CREATE INDEX \"i\" ON \"t\" USING fts (\"body\")";
        var act = () => ManagedIndexMethodStateSql.Split(declaration + " /*ahtola-index-method:1:*/");
        act.Should().Throw<EmbeddedSqlException>().WithMessage("*state is empty*");
    }

    [Test]
    public void EveryStateFieldIsValidatedAgainstTheIndexDefinition()
    {
        var attachment = AttachFts("body", ("tokenizer", SqlValue.Text("ngram")), ("min_gram", SqlValue.Integer(2)), ("max_gram", SqlValue.Integer(4)));

        // Round trip is accepted.
        attachment.LoadState(ManagedFtsIndexMethod.StateVersion, attachment.SaveState());

        Mutate(attachment, buffer => buffer[4] = (byte)ManagedFtsTokenizerKind.Ascii)
            .Should().Throw<EmbeddedSqlException>().WithMessage("*tokenizer does not match*");
        Mutate(attachment, buffer => buffer[4] = 200)
            .Should().Throw<EmbeddedSqlException>().WithMessage("*unknown tokenizer*");
        Mutate(attachment, buffer => buffer[5] = 9)
            .Should().Throw<EmbeddedSqlException>().WithMessage("*gram bounds do not match*");
        Mutate(attachment, buffer => buffer[13] = 9)
            .Should().Throw<EmbeddedSqlException>().WithMessage("*unknown detail level*");
        Mutate(attachment, buffer => buffer[14] = 7)
            .Should().Throw<EmbeddedSqlException>().WithMessage("*invalid columnsize flag*");
        Mutate(attachment, buffer => buffer[14] = 0)
            .Should().Throw<EmbeddedSqlException>().WithMessage("*columnsize does not match*");
        Mutate(
                attachment,
                buffer => BitConverter.GetBytes(double.NaN).CopyTo(buffer, 15))
            .Should().Throw<EmbeddedSqlException>().WithMessage("*weight*");

        var empty = () => attachment.LoadState(ManagedFtsIndexMethod.StateVersion, []);
        empty.Should().Throw<EmbeddedSqlException>().WithMessage("*empty state*");
    }

    // ---------------------------------------------------------------------------------------
    // Finding 9: WITH options are implemented or rejected, never silently ignored.
    // ---------------------------------------------------------------------------------------

    [Test]
    public void GramOptionsAreOnlyAcceptedForGramTokenizers()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);

        ShouldThrow(connection, "CREATE INDEX i1 ON docs USING fts (title, body) WITH (min_gram = 2);")
            .Message.Should().Contain("require the 'ngram' or 'trigram' tokenizer");
        ShouldThrow(
            connection,
            "CREATE INDEX i2 ON docs USING fts (title, body) WITH (tokenizer = 'trigram', min_gram = 2);")
            .Message.Should().Contain("fixed gram size");
        ShouldThrow(
            connection,
            "CREATE INDEX i3 ON docs USING fts (title, body) WITH (tokenizer = 'ngram', min_gram = 0);")
            .Message.Should().Contain("min_gram <= max_gram");

        Execute(connection, "CREATE INDEX i4 ON docs USING fts (title, body) WITH (tokenizer = 'ngram', min_gram = 2, max_gram = 3);");
    }

    [Test]
    public void DetailAndColumnSizeSemanticsAreHonoredOrRejected()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);
        Execute(connection, "INSERT INTO docs VALUES (1, 'quick brown', 'the quick brown fox');");

        Execute(connection, "CREATE INDEX docs_cols ON docs USING fts (title, body) WITH (detail = 'columns');");
        ShouldThrow(connection, "SELECT id FROM docs WHERE fts_match(title, body, '\"quick brown\"');")
            .Message.Should().Contain("does not record positions");

        Execute(connection, "DROP INDEX docs_cols;");
        Execute(connection, "CREATE INDEX docs_none ON docs USING fts (title, body) WITH (detail = 'none');");
        ShouldThrow(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'title:quick');")
            .Message.Should().Contain("does not record column attribution");

        ShouldThrow(connection, "CREATE INDEX docs_bad ON docs USING fts (title, body) WITH (columnsize = 2);")
            .Message.Should().Contain("'columnsize' must be 0 or 1");
        ShouldThrow(connection, "CREATE INDEX docs_dup ON docs USING fts (title, body) WITH (detail = 'full', detail = 'none');")
            .Message.Should().Contain("Duplicate index method parameter");
    }

    [Test]
    public void ColumnSizeZeroChangesScoring()
    {
        var normalized = ScoreWith(columnSize: true);
        var unnormalized = ScoreWith(columnSize: false);

        normalized.Should().BeGreaterThan(0.0);
        unnormalized.Should().BeGreaterThan(0.0);
        unnormalized.Should().NotBe(normalized, "columnsize = 0 must actually disable length normalization");

        static double ScoreWith(bool columnSize)
        {
            var index = new ManagedFtsSearchIndex(
                1,
                ManagedFtsTokenizerOptions.Default,
                [1.0],
                ManagedFtsDetailLevel.Full,
                columnSize);
            index.Upsert(1, [], [SqlValue.Text("alpha")]);
            index.Upsert(2, [], [SqlValue.Text("beta beta beta beta beta beta")]);
            return index.Score(
                ManagedFtsQueryLanguage.Parse("alpha", ManagedFtsTokenizerOptions.Default, static _ => true),
                1);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Finding 10: offsets survive folding, combining marks, surrogate pairs and gram slicing.
    // ---------------------------------------------------------------------------------------

    [Test]
    public void HighlightPreservesSourceTextThroughUnicodeFolding()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);
        Execute(connection, CreateFtsIndex);
        Execute(connection, "INSERT INTO docs VALUES (1, 'x', 'Ame\u0301lie 😀 rocks');");

        var highlighted = QueryTexts(
            connection,
            "SELECT fts_highlight(body, '[', ']', 'rocks') FROM docs WHERE id = 1;")[0];

        highlighted.Should().Be("Ame\u0301lie 😀 [rocks]");
    }

    [Test]
    public void GramTokenizationKeepsExactSourceSpans()
    {
        var options = new ManagedFtsTokenizerOptions(ManagedFtsTokenizerKind.Trigram);
        const string text = "a\u0301😀bc";

        var tokens = ManagedFtsTokenization.Tokenize(text, options);

        tokens.Should().NotBeEmpty();
        foreach (var token in tokens)
        {
            token.Offset.Should().BeGreaterThanOrEqualTo(0);
            (token.Offset + token.Length).Should().BeLessThanOrEqualTo(text.Length);

            // Slicing a surrogate pair in half would produce an unpaired surrogate here.
            var slice = text.Substring(token.Offset, token.Length);
            char.IsLowSurrogate(slice[0]).Should().BeFalse();
            char.IsHighSurrogate(slice[^1]).Should().BeFalse();
        }
    }

    [Test]
    public void SnippetOnAGramIndexReproducesTheSource()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);
        Execute(connection, "CREATE INDEX docs_fts ON docs USING fts (title, body) WITH (tokenizer = 'trigram');");
        Execute(connection, "INSERT INTO docs VALUES (1, 'x', 'naïve café 😀 tail');");

        var snippet = QueryTexts(
            connection,
            "SELECT fts_snippet(body, 'caf', '<', '>', '…', 64) FROM docs WHERE id = 1;")[0];

        snippet.Replace("<", string.Empty, StringComparison.Ordinal)
            .Replace(">", string.Empty, StringComparison.Ordinal)
            .Replace("…", string.Empty, StringComparison.Ordinal)
            .Should().Be("naïve café 😀 tail");
    }

    // ---------------------------------------------------------------------------------------
    // Finding 11: prefix expansion counts live terms only.
    // ---------------------------------------------------------------------------------------

    [Test]
    public void PrefixExpansionCountsLiveTermsOnly()
    {
        var index = new ManagedFtsSearchIndex(1, ManagedFtsTokenizerOptions.Default, [1.0]);
        var total = ManagedFtsLimits.MaxPrefixTerms + 200;
        for (var id = 1; id <= total; id++)
            index.Upsert(id, [], [SqlValue.Text($"pfx{id}")]);

        // Retire everything above the limit. The terms are still physically present as tombstones
        // and must not be counted against the expansion budget.
        for (var id = ManagedFtsLimits.MaxPrefixTerms; id <= total; id++)
            index.Remove(id);

        var query = ManagedFtsQueryLanguage.Parse("pfx*", ManagedFtsTokenizerOptions.Default, static _ => true);
        var hits = index.Search(query);

        hits.Should().HaveCount(ManagedFtsLimits.MaxPrefixTerms - 1);
    }

    [Test]
    public void PrefixExpansionStillRejectsGenuinelyOversizedExpansions()
    {
        var index = new ManagedFtsSearchIndex(1, ManagedFtsTokenizerOptions.Default, [1.0]);
        for (var id = 1; id <= ManagedFtsLimits.MaxPrefixTerms + 10; id++)
            index.Upsert(id, [], [SqlValue.Text($"pfx{id}")]);

        var query = ManagedFtsQueryLanguage.Parse("pfx*", ManagedFtsTokenizerOptions.Default, static _ => true);
        var act = () => index.Search(query);
        act.Should().Throw<EmbeddedSqlException>().WithMessage("*expands to more than*");
    }

    // ---------------------------------------------------------------------------------------
    // Finding 12: revision-aware maintenance, and a cold cursor prices its rebuild.
    // ---------------------------------------------------------------------------------------

    [Test]
    public void AColdCursorPricesTheRebuildItWillBeForcedToDo()
    {
        var attachment = AttachFts("body");
        var source = ArrayManagedIndexSource.FromText(
            Enumerable.Range(1, 500).Select(id => ((long)id, $"term{id}")).ToArray());
        using var cursor = attachment.Open(source);
        attachment.Definition.TryFindPattern(ManagedIndexPatternShape.Match, out var pattern).Should().BeTrue();

        var cold = cursor.EstimateCost(new ManagedIndexMethodCostContext(pattern, 500, null, []));
        cold.Should().NotBeNull();
        cold!.Value.EstimatedCost.Should().BeGreaterThanOrEqualTo(500);

        cursor.OpenRead();
        var warm = cursor.EstimateCost(new ManagedIndexMethodCostContext(pattern, 500, null, []));
        warm.Should().NotBeNull();
        warm!.Value.EstimatedCost.Should().BeLessThan(cold.Value.EstimatedCost);
    }

    [Test]
    public void AnUnchangedSourceIsNotReconciledAgain()
    {
        var attachment = AttachFts("body");
        var source = ArrayManagedIndexSource.FromText((1, "alpha"), (2, "beta"));
        using var cursor = attachment.Open(source);

        cursor.OpenRead();
        source.RebuildNotifications.Should().Be(1);

        cursor.OpenRead();
        cursor.OpenRead();
        source.RebuildNotifications.Should().Be(1, "an unchanged revision must cost nothing");

        source.Upsert(3, SqlValue.Text("gamma"));
        cursor.OpenRead();
        source.RebuildNotifications.Should().Be(2);
    }

    [Test]
    public void TheMutationJournalRefusesRangesItCannotProveComplete()
    {
        var journal = new ManagedIndexMethodJournal(revision: 0);

        journal.Record(rowId: 10, revision: 1);
        journal.Record(rowId: 11, revision: 2);
        journal.TryGetDelta(sinceRevision: 0, currentRevision: 2)!.ChangedRowIds.Should().Equal(10, 11);
        journal.TryGetDelta(sinceRevision: 1, currentRevision: 2)!.ChangedRowIds.Should().Equal(11);

        // A revision the journal never saw is not covered.
        journal.TryGetDelta(sinceRevision: 0, currentRevision: 3).Should().BeNull();

        // A gap poisons every range until a rebuild re-establishes the baseline.
        journal.Record(rowId: 12, revision: 5);
        journal.TryGetDelta(sinceRevision: 0, currentRevision: 5).Should().BeNull();

        journal.ResetBaseline(revision: 5);
        journal.Record(rowId: 13, revision: 6);
        journal.TryGetDelta(sinceRevision: 5, currentRevision: 6)!.ChangedRowIds.Should().Equal(13);
        journal.TryGetDelta(sinceRevision: 4, currentRevision: 6).Should().BeNull();
    }

    // ---------------------------------------------------------------------------------------
    // Finding 13: keyword boundaries and bare-term normalization follow the tokenizer.
    // ---------------------------------------------------------------------------------------

    [Test]
    public void UnderscoresDoNotSplitAKeywordFromATerm()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);
        Execute(connection, "CREATE INDEX docs_fts ON docs USING fts (title, body) WITH (tokenizer = 'whitespace');");
        Execute(connection, "INSERT INTO docs VALUES (1, 'x', 'not_a_term here'), (2, 'y', 'here alone');");

        // 'NOT_A_TERM' must read as one term, not as the NOT operator applied to '_A_TERM'.
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'not_a_term');")
            .Should().Equal(1);
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'NOT_A_TERM');")
            .Should().BeEmpty();

        // Negative control: a real NOT with a boundary after it still negates.
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'here NOT not_a_term') ORDER BY id;")
            .Should().Equal(2);
    }

    [Test]
    public void BareTermsGoThroughTheConfiguredTokenizer()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);
        Execute(connection, "CREATE INDEX docs_fts ON docs USING fts (title, body) WITH (tokenizer = 'ascii');");
        Execute(connection, "INSERT INTO docs VALUES (1, 'x', 'foo.bar baz'), (2, 'y', 'bar foo');");

        // Under the ascii tokenizer 'foo.bar' is two adjacent tokens, so it must behave as a phrase
        // over those tokens rather than as one literal term that is nowhere in the dictionary.
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'foo.bar');")
            .Should().Equal(1);

        // Negative control: the reversed order is not adjacent, so the phrase must not match.
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'bar.foo');")
            .Should().Equal(2);
    }

    [Test]
    public void AGramIndexMatchesWholeWordQueries()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);
        Execute(connection, "CREATE INDEX docs_fts ON docs USING fts (title, body) WITH (tokenizer = 'trigram');");
        Execute(connection, "INSERT INTO docs VALUES (1, 'x', 'the quick brown fox'), (2, 'y', 'lazy dog');");

        // The index only holds 3-grams; a bare word has to be sliced the same way to match at all.
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'quick');").Should().Equal(1);
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, '\"uick br\"');").Should().Equal(1);
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'zebra');").Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------
    // Finding 16: the indexed path preserves scalar type and error semantics.
    // ---------------------------------------------------------------------------------------

    [Test]
    public void ANumericQueryArgumentErrorsOnBothPaths()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedSparseCorpus(connection);

        // Indexed path.
        ExplainDetail(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'needle');")
            .Should().Contain("INDEX METHOD");
        ShouldThrow(connection, "SELECT id FROM docs WHERE fts_match(title, body, 123);")
            .Message.Should().Contain("requires a text query");

        Execute(connection, "DROP INDEX docs_fts;");
        ShouldThrow(connection, "SELECT id FROM docs WHERE fts_match(title, body, 123);")
            .Message.Should().Contain("requires a text query");
    }

    [Test]
    public void ANullQueryArgumentMatchesNothingOnBothPaths()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedSparseCorpus(connection);

        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, NULL);").Should().BeEmpty();
        Execute(connection, "DROP INDEX docs_fts;");
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, NULL);").Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------

    private static ManagedFtsIndexAttachment AttachFts(
        string column,
        params (string Name, SqlValue Value)[] parameters)
    {
        var configuration = new ManagedIndexMethodConfiguration(
            "t",
            "t_fts",
            [new ManagedIndexMethodColumn(column, 0)],
            parameters.Select(entry => new ManagedIndexMethodParameter(entry.Name, entry.Value)).ToArray());
        return (ManagedFtsIndexAttachment)ManagedIndexMethodRegistry.Resolve("fts").Attach(configuration);
    }

    private static Action Mutate(ManagedFtsIndexAttachment attachment, Action<byte[]> corrupt)
    {
        var state = attachment.SaveState();
        corrupt(state);
        return () => attachment.LoadState(ManagedFtsIndexMethod.StateVersion, state);
    }

    private static string ExplainDetail(EmbeddedConnection connection, string sql)
    {
        var rows = Query(connection, "EXPLAIN QUERY PLAN " + sql);
        return rows.Count == 0 ? string.Empty : rows[^1][3].AsText();
    }

    /// <summary>A corpus where exactly one document matches, so unranked rows are observable.</summary>
    private static void SeedSparseCorpus(EmbeddedConnection connection)
    {
        Execute(connection, CreateDocuments);
        Execute(connection, CreateFtsIndex);
        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO docs(id, title, body) VALUES (1, 'needle title', 'needle body text');");
        for (var id = 2; id <= 400; id++)
            Execute(connection, $"INSERT INTO docs(id, title, body) VALUES ({id}, 'filler{id}', 'plain body text');");

        Execute(connection, "COMMIT;");
    }
}
