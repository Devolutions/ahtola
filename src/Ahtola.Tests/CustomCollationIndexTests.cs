using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

/// <summary>
/// Focused coverage for the custom-collation index phase: registered application-defined
/// collations (<see cref="EmbeddedDatabase.RegisterCollation"/>) must be usable to create,
/// maintain, reopen, REINDEX, plan, and directly seek persisted secondary indexes across
/// autocommit, classic transaction overlays, and MVCC, without ever materializing a durable
/// index solely because its collation is custom. Every scenario proving direct-seek eligibility
/// asserts both the zero-materialization contract
/// (<see cref="VdbeJoinIndexSeekMetrics.DurableCursorPlans"/> greater than zero,
/// <see cref="VdbeJoinIndexSeekMetrics.IndexRowsMaterialized"/> equal to zero) and an
/// <c>EXPLAIN QUERY PLAN</c> "SEARCH ... USING INDEX ..." row; every scenario proving an index is
/// unavailable/dirty asserts the corresponding absence of any "SEARCH " plan row.
/// </summary>
public sealed class CustomCollationIndexTests
{
    private static Func<string, string, int> ReverseOrdinal
        => static (left, right) => string.CompareOrdinal(right, left);

    private static Func<string, string, int> CaseInsensitiveCustom
        => static (left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase);

    [Test]
    public void CreateUseAndReopenAfterReregistrationSeeksDirectly()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "custom-collation-create-reopen.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            database.RegisterCollation("reverse_text", ReverseOrdinal);
            Execute(connection, "CREATE TABLE outer_items(k TEXT);");
            Execute(connection, "CREATE TABLE inner_items(k TEXT COLLATE reverse_text, payload TEXT);");
            Execute(connection, "CREATE INDEX inner_items_k ON inner_items(k);");
            Execute(connection, "INSERT INTO outer_items VALUES ('b'), ('c');");
            Execute(connection, "INSERT INTO inner_items VALUES ('b', 'two'), ('c', 'three');");
            SeedNoiseRows(connection);
            Execute(connection, "ANALYZE;");

            AssertDirectSeek(database, connection, JoinSql, "two", "three");
        }

        // Reopening and re-registering the same callback before touching the index at all must
        // find it already clean: nothing about durable reopen should force a REINDEX.
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        reopened.RegisterCollation("reverse_text", ReverseOrdinal);
        AssertDirectSeek(reopened, reopenedConnection, JoinSql, "two", "three");
    }

    [Test]
    public void ReopenBeforeRegistrationFallsBackThenRegisteringRestoresDirectSeek()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "custom-collation-reopen-before-register.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            database.RegisterCollation("reverse_text", ReverseOrdinal);
            Execute(connection, "CREATE TABLE outer_items(k TEXT);");
            Execute(connection, "CREATE TABLE inner_items(k TEXT COLLATE reverse_text, payload TEXT);");
            Execute(connection, "CREATE INDEX inner_items_k ON inner_items(k);");
            Execute(connection, "INSERT INTO outer_items VALUES ('b'), ('c');");
            Execute(connection, "INSERT INTO inner_items VALUES ('b', 'two'), ('c', 'three');");
            Execute(connection, "ANALYZE;");
        }

        // A fresh connection that has not registered the callback yet must never seek the index
        // (that would silently treat it as BINARY-ordered) but a read-only scan must still work.
        using (var reopened = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var reopenedConnection = reopened.Connect())
        {
            AssertNoSearch(reopened, reopenedConnection, JoinSql, "two", "three");

            // Registering the callback on this same connection, without any REINDEX, must make
            // the already-correctly-ordered durable index eligible for direct seeks again.
            reopened.RegisterCollation("reverse_text", ReverseOrdinal);
            AssertDirectSeek(reopened, reopenedConnection, JoinSql, "two", "three");
        }
    }

    [Test]
    public void MissingCallbackFailsWritesWithNoSuchCollationSequence()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("custom-collation-missing-write.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, value TEXT);");

        // Never registered on this connection at all: CREATE INDEX must fail closed with the
        // SQLite-style message, never silently fall back to BINARY, and must not leave a partial
        // index behind.
        Action createIndex = () => Execute(
            connection,
            "CREATE INDEX items_value ON items(value COLLATE never_registered);");
        createIndex.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*no such collation sequence*never_registered*");
        Query(connection, "PRAGMA index_list(items);").Should().BeEmpty();
    }

    [Test]
    public void CallbackReplacementMarksIndexDirtyUntilReindexRestoresDirectSeek()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("custom-collation-replace-reindex.db", fileSystem);
        using var connection = database.Connect();
        database.RegisterCollation("swap_text", ReverseOrdinal);
        Execute(connection, "CREATE TABLE outer_items(k TEXT);");
        Execute(connection, "CREATE TABLE inner_items(k TEXT COLLATE swap_text, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_items_k ON inner_items(k);");
        Execute(connection, "INSERT INTO outer_items VALUES ('b'), ('c');");
        Execute(connection, "INSERT INTO inner_items VALUES ('b', 'two'), ('c', 'three');");
        Execute(connection, "ANALYZE;");
        AssertDirectSeek(database, connection, JoinSql, "two", "three");

        // Replacing the callback with one that produces a genuinely different order for the same
        // durable bytes must make the index provably stale: it must be declined for planning
        // until REINDEX rebuilds it, never trusted on the old proof.
        database.RegisterCollation("swap_text", static (left, right) => string.CompareOrdinal(left, right));
        AssertNoSearch(database, connection, JoinSql, "two", "three");

        Execute(connection, "REINDEX inner_items_k;");
        AssertDirectSeek(database, connection, JoinSql, "two", "three");
    }

    [Test]
    public void ReindexByTableAndByCollationNameBothRestoreDirectSeek()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("custom-collation-reindex-targets.db", fileSystem);
        using var connection = database.Connect();
        database.RegisterCollation("swap_text", ReverseOrdinal);
        Execute(connection, "CREATE TABLE outer_items(k TEXT);");
        Execute(connection, "CREATE TABLE inner_items(k TEXT COLLATE swap_text, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_items_k ON inner_items(k);");
        Execute(connection, "INSERT INTO outer_items VALUES ('b'), ('c');");
        Execute(connection, "INSERT INTO inner_items VALUES ('b', 'two'), ('c', 'three');");
        Execute(connection, "ANALYZE;");

        database.RegisterCollation("swap_text", static (left, right) => string.CompareOrdinal(left, right));
        AssertNoSearch(database, connection, JoinSql, "two", "three");
        Execute(connection, "REINDEX inner_items;");
        AssertDirectSeek(database, connection, JoinSql, "two", "three");

        database.RegisterCollation("swap_text", ReverseOrdinal);
        AssertNoSearch(database, connection, JoinSql, "two", "three");
        Execute(connection, "REINDEX swap_text;");
        AssertDirectSeek(database, connection, JoinSql, "two", "three");
    }

    [Test]
    public void UnregisterCollationFallsBackUntilReRegistered()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("custom-collation-unregister.db", fileSystem);
        using var connection = database.Connect();
        database.RegisterCollation("reverse_text", ReverseOrdinal);
        Execute(connection, "CREATE TABLE outer_items(k TEXT);");
        Execute(connection, "CREATE TABLE inner_items(k TEXT COLLATE reverse_text, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_items_k ON inner_items(k);");
        Execute(connection, "INSERT INTO outer_items VALUES ('b'), ('c');");
        Execute(connection, "INSERT INTO inner_items VALUES ('b', 'two'), ('c', 'three');");
        Execute(connection, "ANALYZE;");
        AssertDirectSeek(database, connection, JoinSql, "two", "three");

        database.UnregisterCollation("reverse_text").Should().BeTrue();
        AssertNoSearch(database, connection, JoinSql, "two", "three");

        database.RegisterCollation("reverse_text", ReverseOrdinal);
        AssertDirectSeek(database, connection, JoinSql, "two", "three");
    }

    [Test]
    public void OverridingABuiltInCollationDoesNotAffectAnUnrelatedBinaryIndex()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("custom-collation-override-safety.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_items(k TEXT, nc TEXT);");
        Execute(connection, "CREATE TABLE inner_items(k TEXT, nc TEXT COLLATE NOCASE, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_items_k ON inner_items(k);");
        Execute(connection, "CREATE INDEX inner_items_nc ON inner_items(nc);");
        Execute(connection, "INSERT INTO outer_items VALUES ('b', 'B'), ('c', 'C');");
        Execute(connection, "INSERT INTO inner_items VALUES ('b', 'B', 'two'), ('c', 'C', 'three');");
        Execute(connection, "ANALYZE;");

        const string binarySql =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_k ON outer_items.k = inner_items.k
            ORDER BY outer_items.k;
            """;
        const string nocaseSql =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_nc ON outer_items.nc = inner_items.nc
            ORDER BY outer_items.nc;
            """;

        // Before any override is registered, both are plain built-in-collation indexes.
        AssertDirectSeek(database, connection, binarySql, "two", "three");
        AssertDirectSeek(database, connection, nocaseSql, "two", "three");

        // Overriding NOCASE with a callback that disagrees with the durable order (NOCASE's
        // physical order here happens to already match ordinal-ignore-case, so instead register a
        // callback with genuinely different semantics to prove staleness) must never affect the
        // unrelated BINARY index, which needs no callback and no revalidation at all.
        database.RegisterCollation("NOCASE", static (left, right) => -string.Compare(left, right, StringComparison.OrdinalIgnoreCase));
        AssertDirectSeek(database, connection, binarySql, "two", "three");
        AssertNoSearch(database, connection, nocaseSql, "two", "three");

        Execute(connection, "REINDEX inner_items_nc;");
        AssertDirectSeek(database, connection, nocaseSql, "two", "three");
        // The BINARY index was never touched by any of this and remains clean throughout.
        AssertDirectSeek(database, connection, binarySql, "two", "three");
    }

    [Test]
    public void PartialIndexWithCustomCollationSeeksDirectly()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("custom-collation-partial.db", fileSystem);
        using var connection = database.Connect();
        database.RegisterCollation("reverse_text", ReverseOrdinal);
        Execute(connection, "CREATE TABLE outer_items(k TEXT);");
        Execute(
            connection,
            "CREATE TABLE inner_items(k TEXT COLLATE reverse_text, payload TEXT, active INTEGER);");
        Execute(connection, "CREATE INDEX inner_items_k ON inner_items(k) WHERE active = 1;");
        Execute(connection, "INSERT INTO outer_items VALUES ('b'), ('c');");
        Execute(
            connection,
            "INSERT INTO inner_items VALUES ('b', 'two', 1), ('c', 'excluded', 0), ('c', 'three', 1);");
        Execute(connection, "ANALYZE;");

        const string sql =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_k
                ON outer_items.k = inner_items.k AND inner_items.active = 1
            ORDER BY outer_items.k;
            """;
        AssertDirectSeek(database, connection, sql, "two", "three");
    }

    [Test]
    public void ExpressionIndexWithCustomCollationSeeksDirectly()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("custom-collation-expression.db", fileSystem);
        using var connection = database.Connect();
        database.RegisterCollation("reverse_text", ReverseOrdinal);
        Execute(connection, "CREATE TABLE outer_items(k TEXT);");
        Execute(connection, "CREATE TABLE inner_items(k TEXT, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_items_expr ON inner_items((k || '') COLLATE reverse_text);");
        Execute(connection, "INSERT INTO outer_items VALUES ('b'), ('c');");
        // A custom-collation equality is never hashable (requirement 6), so the only way to avoid
        // an O(n*m) nested loop is the index seek. With just two rows a side, the cost-based
        // planner's page-rounded scan cost is cheaper than any seek (same as an ordinary index on
        // a tiny table -- see DurableReopenUsesPagerSeekForProvenExpressionIndex), so this adds
        // enough noise rows that the seek is genuinely cheaper, matching that existing convention
        // for unforced (no INDEXED BY) cost-based index-seek tests.
        Execute(
            connection,
            "INSERT INTO inner_items VALUES ('b', 'two'), ('c', 'three'), "
                + string.Join(
                    ", ",
                    Enumerable.Range(1, 500).Select(value => $"('noise{value}', 'n{value}')"))
                + ";");
        Execute(connection, "ANALYZE;");

        const string sql =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items
                ON outer_items.k = (inner_items.k || '') COLLATE reverse_text
            ORDER BY outer_items.k;
            """;
        AssertDirectSeek(database, connection, sql, "two", "three");
    }

    [Test]
    public void PartialIndexPredicateWithCustomCollateComparisonBuildsWritesAndReopensCorrectly()
    {
        // Distinct from PartialIndexWithCustomCollationSeeksDirectly above: there the WHERE
        // predicate (`active = 1`) never touches collation at all, and the COLLATE clause lives
        // on the indexed column's own declared type. Here the partial index's WHERE predicate
        // itself performs a COLLATE comparison, which only the private index-expression evaluator
        // (EmbeddedFileStore._indexExpressionEvaluator) resolves while qualifying rows -- both
        // while building the index and while maintaining it incrementally on INSERT -- so it must
        // route "ci_text" through the owning connection's registered callback rather than fail
        // closed with "no such collation sequence" regardless of registration.
        var fileSystem = new InMemoryFileSystem();
        const string path = "custom-collation-partial-predicate-collate.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            database.RegisterCollation("ci_text", CaseInsensitiveCustom);
            Execute(connection, "CREATE TABLE outer_items(k TEXT);");
            Execute(connection, "CREATE TABLE inner_items(k TEXT, payload TEXT, tag TEXT);");
            Execute(
                connection,
                "CREATE INDEX inner_items_k ON inner_items(k) WHERE tag COLLATE ci_text = 'KEEP';");
            Execute(connection, "INSERT INTO outer_items VALUES ('b'), ('c');");
            Execute(
                connection,
                "INSERT INTO inner_items VALUES ('b', 'two', 'keep'), ('c', 'excluded', 'drop'), "
                    + "('c', 'three', 'Keep');");
            Execute(connection, "ANALYZE;");

            const string sql =
                """
                SELECT inner_items.payload
                FROM outer_items
                JOIN inner_items INDEXED BY inner_items_k
                    ON outer_items.k = inner_items.k AND inner_items.tag COLLATE ci_text = 'KEEP'
                ORDER BY outer_items.k;
                """;
            AssertDirectSeek(database, connection, sql, "two", "three");

            // Incremental maintenance: a row that newly qualifies for the predicate on INSERT
            // must also have its predicate evaluated through the same resolved callback.
            Execute(connection, "INSERT INTO inner_items VALUES ('b', 'four', 'KEEP');");
            const string sqlAfterInsert =
                """
                SELECT inner_items.payload
                FROM outer_items
                JOIN inner_items INDEXED BY inner_items_k
                    ON outer_items.k = inner_items.k AND inner_items.tag COLLATE ci_text = 'KEEP'
                ORDER BY outer_items.k, inner_items.payload;
                """;
            AssertDirectSeek(database, connection, sqlAfterInsert, "four", "two", "three");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        reopened.RegisterCollation("ci_text", CaseInsensitiveCustom);
        const string sqlAfterReopen =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_k
                ON outer_items.k = inner_items.k AND inner_items.tag COLLATE ci_text = 'KEEP'
            ORDER BY outer_items.k, inner_items.payload;
            """;
        AssertDirectSeek(reopened, reopenedConnection, sqlAfterReopen, "four", "two", "three");
    }

    [Test]
    public void ExpressionIndexKeyComputationWithInternalCustomCollateComparisonBuildsWritesAndReopensCorrectly()
    {
        // Distinct from ExpressionIndexWithCustomCollationSeeksDirectly above: there the COLLATE
        // clause is a trailing modifier on the whole indexed expression, extracted as the index
        // column's own declared collation (IndexExpressionSemantics.GetCollationName) and never
        // exercises the private evaluator's comparison path at all -- computing `k || ''` needs no
        // collation lookup. Here the stored key itself is the *result* of a COLLATE comparison, so
        // computing it (both while building the index and while maintaining it incrementally on
        // INSERT) must route "ci_text" through the private index-expression evaluator
        // (EmbeddedFileStore._indexExpressionEvaluator) against the owning connection's registered
        // callback.
        //
        // The outer probe deliberately uses a BLOB-affinity column (no declared type): per finding
        // #2, an arbitrary (non-provably-typed) expression index seek is only eligible against a
        // no-affinity/BLOB outer operand, since a boolean-comparison expression like
        // `tag COLLATE ci_text = 'KEEP'` produces INTEGER 0/1 -- not statically provable TEXT --
        // and a NUMERIC- or TEXT-affinity outer probe must still decline the seek.
        //
        // The CREATE INDEX statement adds a second, trailing "COLLATE ci_text" wrapping the whole
        // indexed expression, distinct from the one embedded inside it. GetExplicitCollation
        // (EmbeddedDatabase.cs) recurses into a comparison's operands when resolving the ON
        // clause's own collation, so it discovers the embedded "ci_text" and threads it into the
        // join term as EqualitySeekCollation -- CanBindIndexColumn only binds a seek once that
        // matches the candidate index column's own declared collation
        // (IndexExpressionSemantics.GetCollationName), which is read from the trailing modifier,
        // not the embedded one. Without the trailing wrapper the declared collation defaults to
        // BINARY, mismatches the discovered "ci_text", and the seek is correctly declined (falling
        // back to the safe evaluator). The trailing wrapper does not change which rows qualify --
        // the stored key is still the same INTEGER 0/1 comparison result -- it only lets the two
        // independently-resolved collation strings agree so the seek path is exercised here too.
        var fileSystem = new InMemoryFileSystem();
        const string path = "custom-collation-expression-internal-collate.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            database.RegisterCollation("ci_text", CaseInsensitiveCustom);
            Execute(connection, "CREATE TABLE outer_items(flag BLOB);");
            Execute(connection, "CREATE TABLE inner_items(tag TEXT, payload TEXT);");
            Execute(
                connection,
                "CREATE INDEX inner_items_expr ON inner_items((tag COLLATE ci_text = 'KEEP') COLLATE ci_text);");
            Execute(connection, "INSERT INTO outer_items VALUES (1);");
            // Noise rows so the cost-based planner genuinely prefers the seek, matching the
            // unforced (no INDEXED BY) convention used by ExpressionIndexWithCustomCollationSeeksDirectly.
            Execute(
                connection,
                "INSERT INTO inner_items VALUES ('keep', 'two'), ('drop', 'excluded'), "
                    + "('Keep', 'three'), "
                    + string.Join(
                        ", ",
                        Enumerable.Range(1, 500).Select(value => $"('noise{value}', 'n{value}')"))
                    + ";");
            Execute(connection, "ANALYZE;");

            // The outer equality's own collation is plain BINARY (comparing a BLOB against the
            // expression's INTEGER 0/1 result) -- unlike ExpressionIndexWithCustomCollationSeeksDirectly
            // above, COLLATE does not wrap the whole comparison here, so the cost-based planner
            // correctly treats this join as hashable and prefers a hash-equijoin over the seek on
            // 500+ noise rows. INDEXED BY forces the seek the same way
            // PartialIndexPredicateWithCustomCollateComparisonBuildsWritesAndReopensCorrectly does,
            // without changing which plan is *correct* -- both still route the expression's
            // internal COLLATE through the private evaluator.
            const string sql =
                """
                SELECT inner_items.payload
                FROM outer_items
                JOIN inner_items INDEXED BY inner_items_expr
                    ON outer_items.flag = (inner_items.tag COLLATE ci_text = 'KEEP')
                ORDER BY inner_items.payload;
                """;
            AssertDirectSeek(database, connection, sql, "three", "two");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        reopened.RegisterCollation("ci_text", CaseInsensitiveCustom);
        const string sqlAfterReopen =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_expr
                ON outer_items.flag = (inner_items.tag COLLATE ci_text = 'KEEP')
            ORDER BY inner_items.payload;
            """;
        AssertDirectSeek(reopened, reopenedConnection, sqlAfterReopen, "three", "two");
    }

    [Test]
    public void PartialIndexPredicateWithOverriddenBinaryCollationBuildsWritesAndAgreesWithScan()
    {
        // Finding #1 regression: the private index-expression/partial-predicate evaluator
        // (EmbeddedFileStore._indexExpressionEvaluator) must consult the active external
        // collation resolver (see _externalCollationResolver / BuildCollationResolver) BEFORE the
        // hard-coded BINARY/NOCASE/RTRIM fallback, because an application override of a *built-in*
        // name is otherwise unreachable there -- unlike a custom name such as "ci_text" above,
        // which never matches the hard-coded checks anyway and so could not have exposed this
        // ordering bug. Overriding BINARY with a case-insensitive comparator -- the opposite of
        // BINARY's real ordinal semantics -- makes 'keep'/'Keep' newly qualify against 'KEEP', an
        // outcome only the fix can produce.
        //
        // The index is built (BuildIndexTree) over an already-populated table, forcing the
        // private evaluator to decide every existing row's membership; a further INSERT
        // afterward exercises the same evaluator's incremental-maintenance path. Both the direct
        // seek (INDEXED BY, trusting the pre-built index's own membership decision) and a plain
        // NOT INDEXED scan (re-evaluating the predicate row-by-row through ordinary ...
        // EmbeddedDatabase.Compare, which already correctly consults the override via its own
        // per-connection _collations registry) must agree: before the fix, the seek path would
        // silently disagree with the scan path because it was built using the real, ordinal
        // BINARY instead of the override.
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile(
            "custom-collation-partial-predicate-overridden-binary.db", fileSystem);
        using var connection = database.Connect();
        database.RegisterCollation(
            "BINARY",
            static (left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));

        Execute(connection, "CREATE TABLE outer_items(k TEXT);");
        Execute(connection, "CREATE TABLE inner_items(k TEXT, payload TEXT, tag TEXT);");
        Execute(connection, "INSERT INTO outer_items VALUES ('b'), ('c');");
        Execute(
            connection,
            "INSERT INTO inner_items VALUES ('b', 'two', 'keep'), ('c', 'excluded', 'drop'), "
                + "('c', 'three', 'Keep');");
        Execute(
            connection,
            "CREATE INDEX inner_items_k ON inner_items(k) WHERE tag COLLATE BINARY = 'KEEP';");
        Execute(connection, "ANALYZE;");

        const string seekSql =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_k
                ON outer_items.k = inner_items.k AND inner_items.tag COLLATE BINARY = 'KEEP'
            ORDER BY outer_items.k, inner_items.payload;
            """;
        const string scanSql =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items NOT INDEXED
                ON outer_items.k = inner_items.k AND inner_items.tag COLLATE BINARY = 'KEEP'
            ORDER BY outer_items.k, inner_items.payload;
            """;
        AssertDirectSeek(database, connection, seekSql, "two", "three");
        ReadRows(connection, scanSql).Select(row => row[0].AsText()).Should().Equal("two", "three");

        // Incremental maintenance (a write): a row that newly qualifies for the predicate on
        // INSERT must also have its predicate evaluated through the overridden callback, not the
        // hard-coded ordinal fallback -- and must still agree with the plain scan.
        Execute(connection, "INSERT INTO inner_items VALUES ('b', 'four', 'KEEP');");
        AssertDirectSeek(database, connection, seekSql, "four", "two", "three");
        ReadRows(connection, scanSql).Select(row => row[0].AsText())
            .Should().Equal("four", "two", "three");
    }

    [Test]
    public void ExpressionIndexKeyComputationWithOverriddenNocaseCollationBuildsWritesAndAgreesWithScan()
    {
        // Finding #1 regression, expression-index-key variant of the BINARY test above: the
        // private index-expression evaluator must also consult the external resolver before the
        // hard-coded NOCASE fallback when computing a stored expression-index key. Overriding
        // NOCASE with a case-*sensitive* (ordinal) comparator -- the opposite of NOCASE's real
        // semantics -- makes 'keep' newly disqualify against 'KEEP' while the exact-case 'KEEP'
        // row still qualifies, an outcome only the fix can produce. As above, the index is built
        // over an already-populated table (exercising BuildIndexTree), a further INSERT exercises
        // incremental maintenance, and both the direct seek and a NOT INDEXED scan must agree.
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile(
            "custom-collation-expression-overridden-nocase.db", fileSystem);
        using var connection = database.Connect();
        database.RegisterCollation("NOCASE", static (left, right) => string.CompareOrdinal(left, right));

        Execute(connection, "CREATE TABLE outer_items(flag BLOB);");
        Execute(connection, "CREATE TABLE inner_items(tag TEXT, payload TEXT);");
        Execute(connection, "INSERT INTO outer_items VALUES (1);");
        // Noise rows so the cost-based planner genuinely prefers the seek, matching the unforced
        // (no INDEXED BY) convention used elsewhere in this file.
        Execute(
            connection,
            "INSERT INTO inner_items VALUES ('keep', 'two'), ('drop', 'excluded'), "
                + "('KEEP', 'three'), "
                + string.Join(
                    ", ",
                    Enumerable.Range(1, 500).Select(value => $"('noise{value}', 'n{value}')"))
                + ";");
        Execute(
            connection,
            "CREATE INDEX inner_items_expr ON inner_items((tag COLLATE NOCASE = 'KEEP') COLLATE NOCASE);");
        Execute(connection, "ANALYZE;");

        const string seekSql =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_expr
                ON outer_items.flag = (inner_items.tag COLLATE NOCASE = 'KEEP')
            ORDER BY inner_items.payload;
            """;
        const string scanSql =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items NOT INDEXED
                ON outer_items.flag = (inner_items.tag COLLATE NOCASE = 'KEEP')
            ORDER BY inner_items.payload;
            """;
        // Under the overridden (case-sensitive) NOCASE, only the exact-case 'KEEP' row qualifies;
        // the real, un-overridden NOCASE would also match 'keep', which would wrongly surface
        // "two" here if either evaluation path missed the override.
        AssertDirectSeek(database, connection, seekSql, "three");
        ReadRows(connection, scanSql).Select(row => row[0].AsText()).Should().Equal("three");

        // Incremental maintenance (a write): a newly-inserted row's expression-index key must
        // also be computed through the overridden callback, and must still agree with the scan.
        Execute(connection, "INSERT INTO inner_items VALUES ('KEEP', 'four');");
        AssertDirectSeek(database, connection, seekSql, "four", "three");
        ReadRows(connection, scanSql).Select(row => row[0].AsText()).Should().Equal("four", "three");
    }

    [Test]
    public void PartialIndexPredicateCallbackReplacementMarksIndexDirtyUntilReindexRestoresDirectSeek()
    {
        // Distinct from CallbackReplacementMarksIndexDirtyUntilReindexRestoresDirectSeek above:
        // there the replaced collation qualifies the indexed column itself
        // (IndexExpressionSemantics.GetCollationName). Here it is embedded only inside the
        // partial index's WHERE predicate
        // (IndexExpressionSemantics.CollectEmbeddedCollationNames) -- finding #2 requires
        // IsCustomCollationIndexPlanReady to track that dependency too, since replacing the
        // callback can change which rows the predicate actually admits (membership), not merely
        // reorder an already-fixed row set the way a column-level replacement would. Before the
        // fix, IsCustomCollationIndexPlanReady only ever inspected index.Columns (all plain
        // BINARY here), so it would have kept trusting this index's stale membership proof and
        // offered it for SEARCH with the wrong row set instead of falling back until REINDEX.
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile(
            "custom-collation-partial-predicate-replace-reindex.db", fileSystem);
        using var connection = database.Connect();
        database.RegisterCollation("ci_text", CaseInsensitiveCustom);
        Execute(connection, "CREATE TABLE outer_items(k TEXT);");
        Execute(connection, "CREATE TABLE inner_items(k TEXT, payload TEXT, tag TEXT);");
        Execute(
            connection,
            "CREATE INDEX inner_items_k ON inner_items(k) WHERE tag COLLATE ci_text = 'KEEP';");
        Execute(connection, "INSERT INTO outer_items VALUES ('b'), ('c'), ('d');");
        Execute(
            connection,
            "INSERT INTO inner_items VALUES ('b', 'two', 'keep'), ('c', 'excluded', 'drop'), "
                + "('c', 'three', 'Keep'), ('d', 'four', 'KEEP');");
        Execute(connection, "ANALYZE;");

        const string sql =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_k
                ON outer_items.k = inner_items.k AND inner_items.tag COLLATE ci_text = 'KEEP'
            ORDER BY outer_items.k;
            """;
        // Under case-insensitive ci_text, 'keep'/'Keep'/'KEEP' all satisfy the predicate.
        AssertDirectSeek(database, connection, sql, "two", "three", "four");

        // Replace ci_text with a genuinely different (case-sensitive) comparison: only the exact
        // 'KEEP' row now satisfies the predicate, so the index's membership proof from before the
        // replacement is provably stale. It must be declined for planning until REINDEX rebuilds
        // it, never trusted just because every indexed-term (column-level) collation is still
        // plain BINARY and unaffected by this replacement.
        database.RegisterCollation("ci_text", static (left, right) => string.CompareOrdinal(left, right));
        AssertNoSearch(database, connection, sql, "four");

        Execute(connection, "REINDEX inner_items_k;");
        AssertDirectSeek(database, connection, sql, "four");
    }

    [Test]
    public void ExpressionIndexKeyCallbackReplacementMarksIndexDirtyUntilReindexRestoresDirectSeek()
    {
        // Distinct from CallbackReplacementMarksIndexDirtyUntilReindexRestoresDirectSeek above:
        // there the replaced collation is the index column's own trailing declared collation
        // (IndexExpressionSemantics.GetCollationName), so the pre-existing column-level
        // (GetCollationName) loop in IsCustomCollationIndexPlanReady already catches it on its own
        // -- that does not prove the embedded-name loop
        // (IndexExpressionSemantics.CollectEmbeddedCollationNames) is load-bearing. A naive
        // "AND-compound" variant with two *custom* collation names doesn't prove it either: for a
        // join-seek to bind at all, GetExplicitCollation's single discovered name (here, the AND's
        // left operand, since BinaryExpression is `Left ?? Right` short-circuiting) must equal the
        // index column's declared (trailing) collation for CanBindIndexColumn to accept the seek --
        // and EvaluateCollationNameReadinessLocked classifies *any* name with an active custom
        // callback as NeedsValidation unconditionally, so merely having a *registered custom*
        // trailing collation already forces the column-level loop to demand the full
        // RevalidateIndexOrderAndContent proof, which independently (and correctly) re-derives
        // every key against whatever callbacks are *currently* active -- catching staleness in the
        // right AND operand regardless of whether the embedded-name loop ever ran.
        //
        // To isolate the embedded loop, the discovered/declared collation must stay classified
        // Skip (never a registered custom callback) while a *different*, genuinely custom name is
        // buried where GetExplicitCollation's short-circuit can never reach it. "BINARY" is a
        // built-in name this test never registers a callback for, so EvaluateCollationNameReadinessLocked("BINARY")
        // is always Skip. The stored key is `(tag COLLATE BINARY = 'KEEP') AND (other_col COLLATE
        // flag_collation = 'YES')`, trailed by `COLLATE BINARY` (so the ON clause's discovered
        // collation -- "BINARY", found from the AND's left operand -- matches the index column's
        // declared collation and the seek can bind). The column-level (GetCollationName) loop only
        // ever inspects "BINARY" (Skip); it never even learns "flag_collation" exists, because that
        // name is not the column's own declared collation and is never discoverable via
        // GetExplicitCollation's short-circuit. CollectEmbeddedCollationNames, by contrast, walks
        // BinaryExpression.Left *and* .Right unconditionally, so it is the *only* mechanism able to
        // find "flag_collation" at all -- without it, IsCustomCollationIndexPlanReady would return
        // true (Skip only) and never even attempt RevalidateIndexOrderAndContent, trusting a stale
        // AND result for rows whose left operand is already true.
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile(
            "custom-collation-expression-and-embedded-replace-reindex.db", fileSystem);
        using var connection = database.Connect();
        database.RegisterCollation("flag_collation", CaseInsensitiveCustom);
        Execute(connection, "CREATE TABLE outer_items(flag BLOB);");
        Execute(connection, "CREATE TABLE inner_items(tag TEXT, other_col TEXT, payload TEXT);");
        Execute(
            connection,
            "CREATE INDEX inner_items_expr ON inner_items("
                + "((tag COLLATE BINARY = 'KEEP') AND (other_col COLLATE flag_collation = 'YES')) "
                + "COLLATE BINARY);");
        Execute(connection, "INSERT INTO outer_items VALUES (1);");
        Execute(
            connection,
            "INSERT INTO inner_items VALUES "
                + "('KEEP', 'YES', 'exact'), ('KEEP', 'yes', 'lower'), ('KEEP', 'Yes', 'mixed'), "
                + "('drop', 'YES', 'excluded'), "
                + string.Join(
                    ", ",
                    Enumerable.Range(1, 500)
                        .Select(value => $"('noise{value}', 'noise{value}', 'n{value}')"))
                + ";");
        Execute(connection, "ANALYZE;");

        const string sql =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_expr
                ON outer_items.flag = (
                    (inner_items.tag COLLATE BINARY = 'KEEP')
                    AND (inner_items.other_col COLLATE flag_collation = 'YES'))
            ORDER BY inner_items.payload;
            """;
        // Under case-insensitive flag_collation, 'YES'/'yes'/'Yes' all satisfy the right operand.
        AssertDirectSeek(database, connection, sql, "exact", "lower", "mixed");

        // Replace flag_collation with a genuinely different (case-sensitive) comparison: only the
        // exact 'YES' row still satisfies the right AND operand, so the stored keys computed
        // under the old callback are provably stale. The index must be declined for planning
        // until REINDEX recomputes every key, never trusted on the old key proof -- even though
        // no join-seek-matching code path ever discovers "flag_collation" from this ON clause, and
        // the index's own declared collation ("BINARY") never needed revalidation.
        database.RegisterCollation("flag_collation", static (left, right) => string.CompareOrdinal(left, right));
        AssertNoSearch(database, connection, sql, "exact");

        Execute(connection, "REINDEX inner_items_expr;");
        AssertDirectSeek(database, connection, sql, "exact");
    }

    [Test]
    public void PartialIndexPredicateMissingCollateCallbackFailsClosedOnlyWhenARowIsEvaluated()
    {
        // Proves the fail-closed contract is genuinely lazy for predicate/expression-embedded
        // COLLATE clauses (unlike the eager, column-level MissingCallbackFailsWritesWithNoSuchCollationSequence
        // case above): creating the index over an empty table must succeed even though the
        // collation was never registered on this connection, since nothing has evaluated the
        // predicate yet. The first INSERT of a row that requires evaluating it must then fail
        // closed with the SQLite-style message, never silently fall back to BINARY, and must not
        // leave a partial row behind.
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile(
            "custom-collation-partial-predicate-missing.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE inner_items(k TEXT, tag TEXT);");
        Execute(
            connection,
            "CREATE INDEX inner_items_k ON inner_items(k) WHERE tag COLLATE never_registered = 'KEEP';");

        Action insert = () => Execute(connection, "INSERT INTO inner_items VALUES ('a', 'KEEP');");
        insert.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*no such collation sequence*never_registered*");
        Query(connection, "SELECT k FROM inner_items;").Should().BeEmpty();
    }

    [Test]
    public void WithoutRowidSecondaryIndexWithCustomCollationSeeksDirectly()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("custom-collation-without-rowid-secondary.db", fileSystem);
        using var connection = database.Connect();
        database.RegisterCollation("reverse_text", ReverseOrdinal);
        Execute(connection, "CREATE TABLE outer_items(k TEXT);");
        Execute(
            connection,
            "CREATE TABLE inner_items(id TEXT PRIMARY KEY, k TEXT COLLATE reverse_text, payload TEXT) WITHOUT ROWID;");
        Execute(connection, "CREATE INDEX inner_items_k ON inner_items(k);");
        Execute(connection, "INSERT INTO outer_items VALUES ('b'), ('c');");
        Execute(connection, "INSERT INTO inner_items VALUES ('id-1', 'b', 'two'), ('id-2', 'c', 'three');");
        Execute(connection, "ANALYZE;");

        AssertDirectSeek(database, connection, JoinSql, "two", "three");
    }

    [Test]
    public void WithoutRowidPrimaryKeyCustomCollationIsRejectedExplicitly()
    {
        // Retained boundary: a WITHOUT ROWID table's own primary key must stay built-in-collation
        // only, because catalog loading fundamentally needs its comparator before any callback
        // can possibly be registered. This must fail closed with an explicit, distinct message
        // rather than silently degrading to BINARY or succeeding structurally and misbehaving.
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("custom-collation-without-rowid-pk.db", fileSystem);
        using var connection = database.Connect();
        database.RegisterCollation("reverse_text", ReverseOrdinal);

        Action createTable = () => Execute(
            connection,
            "CREATE TABLE entry(id TEXT COLLATE reverse_text PRIMARY KEY, payload TEXT) WITHOUT ROWID;");
        createTable.Should().Throw<EmbeddedSqlException>();
        Query(connection, "SELECT name FROM sqlite_master WHERE name = 'entry';").Should().BeEmpty();
    }

    [Test]
    public void ClassicTransactionOverlayHonorsCustomCollationIndexSeek()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("custom-collation-transaction-overlay.db", fileSystem);
        using var connection = database.Connect();
        database.RegisterCollation("reverse_text", ReverseOrdinal);
        Execute(connection, "CREATE TABLE outer_items(k TEXT);");
        Execute(connection, "CREATE TABLE inner_items(k TEXT COLLATE reverse_text, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_items_k ON inner_items(k);");
        Execute(connection, "INSERT INTO outer_items VALUES ('a'), ('b');");
        Execute(connection, "INSERT INTO inner_items VALUES ('a', 'one');");
        SeedNoiseRows(connection);
        Execute(connection, "ANALYZE;");

        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO inner_items VALUES ('b', 'two-local');");

        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, JoinSql).Select(row => row[0].AsText())
            .Should().Equal("one", "two-local");
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().Be(1);
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);

        Execute(connection, "ROLLBACK;");
    }

    [Test]
    public void MvccTransactionHonorsCustomCollationIndexSeek()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("custom-collation-mvcc.db", fileSystem);
        using var connection = database.Connect();
        database.RegisterCollation("reverse_text", ReverseOrdinal);
        Execute(connection, "CREATE TABLE outer_items(k TEXT);");
        Execute(connection, "CREATE TABLE inner_items(k TEXT COLLATE reverse_text, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_items_k ON inner_items(k);");
        Execute(connection, "INSERT INTO outer_items VALUES ('a'), ('b');");
        Execute(connection, "INSERT INTO inner_items VALUES ('a', 'one');");
        SeedNoiseRows(connection);
        ExecutePragma(connection, "PRAGMA journal_mode=mvcc;");
        Execute(connection, "ANALYZE;");

        Execute(connection, "BEGIN CONCURRENT;");
        Execute(connection, "INSERT INTO inner_items VALUES ('b', 'two-local');");

        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, JoinSql).Select(row => row[0].AsText())
            .Should().Equal("one", "two-local");
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().BeGreaterThan(0);
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);

        Execute(connection, "COMMIT;");
    }

    [Test]
    public void DuplicateEquivalenceClassRowsArePreservedAndAllReturnedByCustomCollationSeek()
    {
        // A custom collation that folds several distinct byte sequences to the same ordering
        // class (case-insensitive here) must never cause the durable secondary index to drop or
        // merge duplicates: every row that compares equal under the collation must still be
        // stored and returned.
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("custom-collation-duplicates.db", fileSystem);
        using var connection = database.Connect();
        database.RegisterCollation(
            "fold_case",
            static (left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));
        Execute(connection, "CREATE TABLE outer_items(k TEXT);");
        Execute(connection, "CREATE TABLE inner_items(k TEXT COLLATE fold_case, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_items_k ON inner_items(k);");
        Execute(connection, "INSERT INTO outer_items VALUES ('KEY');");
        Execute(
            connection,
            "INSERT INTO inner_items VALUES ('key', 'p1'), ('KEY', 'p2'), ('Key', 'p3');");
        Execute(connection, "ANALYZE;");

        // The equality's collating function follows SQLite's real precedence rule (datatype3.html
        // §7.1 rule 2): when both operands are columns, the LEFT operand's column collation wins.
        // outer_items.k has no declared COLLATE (defaults to BINARY), so putting it on the left
        // would force a byte-exact BINARY comparison and defeat the point of this test; naming
        // inner_items.k (COLLATE fold_case) first makes its collation govern the comparison, which
        // is also what makes the durable index's own fold_case ordering the correct seek strategy.
        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, "SELECT inner_items.payload FROM outer_items "
                + "JOIN inner_items INDEXED BY inner_items_k ON inner_items.k = outer_items.k "
                + "ORDER BY inner_items.payload;")
            .Select(row => row[0].AsText())
            .Should().Equal("p1", "p2", "p3");
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().BeGreaterThan(0);
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);
    }

    [Test]
    public void CallbackExceptionPropagatesThroughIndexSeek()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("custom-collation-callback-exception.db", fileSystem);
        using var connection = database.Connect();
        database.RegisterCollation(
            "explosive",
            (_, _) => throw new InvalidOperationException("boom"));
        Execute(connection, "CREATE TABLE outer_items(k TEXT);");
        Execute(connection, "CREATE TABLE inner_items(k TEXT COLLATE explosive, payload TEXT);");
        Execute(connection, "INSERT INTO outer_items VALUES ('a'), ('b');");
        Execute(connection, "INSERT INTO inner_items VALUES ('a', 'one'), ('b', 'two');");

        Action createIndex = () => Execute(connection, "CREATE INDEX inner_items_k ON inner_items(k);");
        createIndex.Should().Throw<InvalidOperationException>().WithMessage("boom");
    }

    [Test]
    public void DirectSeekMatchesMicrosoftDataSqliteForCustomCollation()
    {
        string[] setup =
        [
            "CREATE TABLE outer_items(k TEXT);",
            "CREATE TABLE inner_items(k TEXT COLLATE reverse_text, payload TEXT);",
            "CREATE INDEX inner_items_k ON inner_items(k);",
            "INSERT INTO outer_items VALUES ('a'), ('b'), ('c');",
            "INSERT INTO inner_items VALUES ('a', 'one'), ('b', 'two'), ('c', 'three');",
            "ANALYZE;",
        ];

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        sqlite.CreateCollation("reverse_text", (left, right) => string.CompareOrdinal(right, left));
        foreach (var statement in setup)
        {
            using var command = sqlite.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }

        using var query = sqlite.CreateCommand();
        query.CommandText = JoinSql;
        using var reader = query.ExecuteReader();
        var sqliteRows = new List<string?>();
        while (reader.Read())
            sqliteRows.Add(reader.IsDBNull(0) ? null : reader.GetString(0));

        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("custom-collation-differential.db", fileSystem);
        using var connection = database.Connect();
        database.RegisterCollation("reverse_text", ReverseOrdinal);
        foreach (var statement in setup)
            Execute(connection, statement);

        var managedRows = ReadRows(connection, JoinSql).Select(row => row[0].AsText()).ToList();
        managedRows.Should().Equal(sqliteRows);
    }

    [Test]
    public void UnregisteringAnAgreeingNocaseOverrideKeepsUniqueIndexValidWithoutReindex()
    {
        // Complements CallbackReplacementMarksIndexDirtyUntilReindexRestoresDirectSeek's "genuinely
        // different order -> REINDEX required" branch: here the override's semantics happen to
        // AGREE with the built-in NOCASE it replaces (both fold 'a'/'A' together, in the same
        // order), so revalidating against the restored built-in on unregister must find the
        // durable index still valid without ever requiring a REINDEX.
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("custom-collation-nocase-agree-unregister.db", fileSystem);
        using var connection = database.Connect();
        database.RegisterCollation(
            "NOCASE",
            static (left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));
        Execute(connection, "CREATE TABLE outer_items(k TEXT);");
        Execute(connection, "CREATE TABLE inner_items(k TEXT COLLATE NOCASE, payload TEXT);");
        Execute(connection, "CREATE UNIQUE INDEX inner_items_k ON inner_items(k);");
        Execute(connection, "INSERT INTO outer_items VALUES ('b'), ('c');");
        Execute(connection, "INSERT INTO inner_items VALUES ('b', 'two'), ('c', 'three');");
        SeedNoiseRows(connection);
        Execute(connection, "ANALYZE;");
        AssertDirectSeek(database, connection, JoinSql, "two", "three");

        // The UNIQUE constraint must reject a case-fold duplicate while the override is active.
        Action insertDuplicateWhileOverridden = () =>
            Execute(connection, "INSERT INTO inner_items VALUES ('B', 'dup');");
        insertDuplicateWhileOverridden.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*UNIQUE constraint failed*");

        // Unregistering restores the real built-in NOCASE. Because it agrees with the override on
        // both order and equivalence classes, the index must revalidate clean immediately -- no
        // REINDEX required in between -- and stay directly seekable.
        database.UnregisterCollation("NOCASE").Should().BeTrue();
        AssertDirectSeek(database, connection, JoinSql, "two", "three");

        // The UNIQUE constraint must still correctly reject the same duplicate under the restored
        // built-in semantics.
        Action insertDuplicateAfterUnregister = () =>
            Execute(connection, "INSERT INTO inner_items VALUES ('B', 'dup');");
        insertDuplicateAfterUnregister.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*UNIQUE constraint failed*");
    }

    [Test]
    public void PartiallyRegisteredCollationsIsolateUnavailableIndexPerIndexNotGlobally()
    {
        // Deferred custom-collation validation must be scoped per index/table, never gated on
        // whether *any* collation resolver exists at all: with two custom collations A and B only
        // A registered on this connection, writes to unrelated tables and direct use of A must
        // both work normally while B stays unavailable and is never binary-compared.
        var fileSystem = new InMemoryFileSystem();
        const string path = "custom-collation-partial-registration.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            database.RegisterCollation("collation_a", ReverseOrdinal);
            database.RegisterCollation(
                "collation_b",
                static (left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));

            Execute(connection, "CREATE TABLE outer_a(k TEXT);");
            Execute(connection, "CREATE TABLE inner_a(k TEXT COLLATE collation_a, payload TEXT);");
            Execute(connection, "CREATE INDEX inner_a_k ON inner_a(k);");
            Execute(connection, "CREATE TABLE outer_b(k TEXT);");
            Execute(connection, "CREATE TABLE inner_b(k TEXT COLLATE collation_b, payload TEXT);");
            Execute(connection, "CREATE INDEX inner_b_k ON inner_b(k);");
            Execute(connection, "CREATE TABLE plain(id INTEGER PRIMARY KEY, value TEXT);");

            Execute(connection, "INSERT INTO outer_a VALUES ('b'), ('c');");
            Execute(connection, "INSERT INTO inner_a VALUES ('b', 'two'), ('c', 'three');");
            Execute(connection, "INSERT INTO outer_b VALUES ('b'), ('c');");
            Execute(connection, "INSERT INTO inner_b VALUES ('b', 'BEE'), ('c', 'CEE');");
            Execute(connection, "INSERT INTO plain VALUES (1, 'x');");
            Execute(connection, "ANALYZE;");
        }

        // Reopen with only collation_a registered: collation_b is never registered on this
        // connection at all.
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        reopened.RegisterCollation("collation_a", ReverseOrdinal);

        // Writes to a table wholly unrelated to either custom collation must succeed.
        Execute(reopenedConnection, "INSERT INTO plain VALUES (2, 'y');");
        Query(reopenedConnection, "SELECT value FROM plain ORDER BY id;")
            .Select(row => row[0].AsText())
            .Should().Equal("x", "y");

        // Direct use of the registered collation A still works: writes to its own table and a
        // direct index seek both succeed exactly as if B did not exist in the same catalog.
        const string joinA =
            """
            SELECT inner_a.payload
            FROM outer_a
            JOIN inner_a INDEXED BY inner_a_k ON outer_a.k = inner_a.k
            ORDER BY outer_a.k;
            """;
        Execute(reopenedConnection, "INSERT INTO outer_a VALUES ('d');");
        Execute(reopenedConnection, "INSERT INTO inner_a VALUES ('d', 'four');");
        AssertDirectSeek(reopened, reopenedConnection, joinA, "two", "three", "four");

        // B stays unavailable for planning: its join must never be offered a direct seek, but the
        // full-scan fallback must still return correct rows.
        const string joinB =
            """
            SELECT inner_b.payload
            FROM outer_b
            JOIN inner_b INDEXED BY inner_b_k ON outer_b.k = inner_b.k
            ORDER BY outer_b.k;
            """;
        AssertNoSearch(reopened, reopenedConnection, joinB, "BEE", "CEE");

        // B's own callback must never be assumed or substituted with a binary comparison: any
        // operation that would actually need to reorder or physically touch B's durable index --
        // writing a new row into its table, or a REINDEX targeting it -- must fail closed with the
        // real "no such collation sequence" error rather than silently using byte order.
        Action insertIntoB = () => Execute(reopenedConnection, "INSERT INTO inner_b VALUES ('a', 'AYE');");
        insertIntoB.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*no such collation sequence*collation_b*");
        Action reindexB = () => Execute(reopenedConnection, "REINDEX inner_b_k;");
        reindexB.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*no such collation sequence*collation_b*");

        // The failed write must not have partially applied: B's table is exactly as it was.
        Query(reopenedConnection, "SELECT payload FROM inner_b ORDER BY rowid;")
            .Select(row => row[0].AsText())
            .Should().Equal("BEE", "CEE");
    }

    [Test]
    public void TargetedReindexOfOneIndexNeverRequiresAnUnrelatedCollationsCallback()
    {
        // A targeted REINDEX of index_a must rebuild only index_a's tree, in one pager/WAL
        // transaction, without ever requiring collation_b's callback and without falling back to
        // ForceFullCatalogRewrite -- leaving index_b's tree byte-for-byte untouched.
        var fileSystem = new InMemoryFileSystem();
        const string path = "custom-collation-targeted-reindex-ab.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            database.RegisterCollation("collation_a", ReverseOrdinal);
            database.RegisterCollation(
                "collation_b",
                static (left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));

            Execute(connection, "CREATE TABLE outer_a(k TEXT);");
            Execute(connection, "CREATE TABLE inner_a(k TEXT COLLATE collation_a, payload TEXT);");
            Execute(connection, "CREATE INDEX inner_a_k ON inner_a(k);");
            Execute(connection, "CREATE TABLE outer_b(k TEXT);");
            Execute(connection, "CREATE TABLE inner_b(k TEXT COLLATE collation_b, payload TEXT);");
            Execute(connection, "CREATE INDEX inner_b_k ON inner_b(k);");

            Execute(connection, "INSERT INTO outer_a VALUES ('b'), ('c');");
            Execute(connection, "INSERT INTO inner_a VALUES ('b', 'two'), ('c', 'three');");
            Execute(connection, "INSERT INTO outer_b VALUES ('b'), ('c');");
            Execute(connection, "INSERT INTO inner_b VALUES ('b', 'BEE'), ('c', 'CEE');");
            Execute(connection, "ANALYZE;");
        }

        // Replace collation_a with a genuinely different order so index_a is provably stale and
        // needs REINDEX; collation_b is never registered on this connection at all.
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        reopened.RegisterCollation("collation_a", static (left, right) => string.CompareOrdinal(left, right));

        const string joinA =
            """
            SELECT inner_a.payload
            FROM outer_a
            JOIN inner_a INDEXED BY inner_a_k ON outer_a.k = inner_a.k
            ORDER BY outer_a.k;
            """;
        const string joinB =
            """
            SELECT inner_b.payload
            FROM outer_b
            JOIN inner_b INDEXED BY inner_b_k ON outer_b.k = inner_b.k
            ORDER BY outer_b.k;
            """;

        AssertNoSearch(reopened, reopenedConnection, joinA, "two", "three");
        AssertNoSearch(reopened, reopenedConnection, joinB, "BEE", "CEE");

        // A targeted REINDEX naming index_a only must rebuild its tree and succeed without ever
        // requiring collation_b's callback.
        Execute(reopenedConnection, "REINDEX inner_a_k;");
        AssertDirectSeek(reopened, reopenedConnection, joinA, "two", "three");

        // ...and must leave B's tree completely untouched: still unavailable for direct seek (no
        // callback registered) and its rows still intact.
        AssertNoSearch(reopened, reopenedConnection, joinB, "BEE", "CEE");

        // Structural proof that B's durable tree was never rewritten by the targeted REINDEX of A:
        // simply registering B's original callback (no REINDEX, no full rewrite) must immediately
        // find its physical order still valid.
        reopened.RegisterCollation(
            "collation_b",
            static (left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));
        AssertDirectSeek(reopened, reopenedConnection, joinB, "BEE", "CEE");
    }

    [Test]
    public void OverriddenBinaryCollationTargetedReindexNeverComparesOldOrderAndLeavesUnavailableSiblingUntouched()
    {
        // forceDeferCustomCollation's structural traversal (used to free a targeted REINDEX's old
        // pages) must disable ordering validation for EVERY leading term -- including a plain,
        // unspecified-collation column -- because a built-in name can itself be overridden via
        // RegisterCollation("BINARY", ...) (see RegisterCollation's remark on
        // _everOverriddenBuiltInCollations) and no longer agree with the tree's existing physical
        // order. Deferring only genuinely custom-named terms would let this pass invoke the
        // newly-overridden BINARY delegate against the OLD (still really-ascending) tree during
        // what must be a decode/validate-structure-only walk, misreporting a merely-stale tree as
        // corrupt. An unrelated table indexed under a genuinely unavailable custom collation must
        // also never be required or touched by any of this.
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile(
            "custom-collation-overridden-binary-targeted-reindex.db", fileSystem);
        using var connection = database.Connect();
        database.RegisterCollation("collation_b", ReverseOrdinal);

        Execute(connection, "CREATE TABLE outer_a(k TEXT);");
        Execute(connection, "CREATE TABLE inner_a(k TEXT, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_a_k ON inner_a(k);");
        Execute(connection, "CREATE TABLE outer_b(k TEXT);");
        Execute(connection, "CREATE TABLE inner_b(k TEXT COLLATE collation_b, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_b_k ON inner_b(k);");

        Execute(connection, "INSERT INTO outer_a VALUES ('b'), ('c');");
        Execute(connection, "INSERT INTO inner_a VALUES ('b', 'two'), ('c', 'three');");
        Execute(connection, "INSERT INTO outer_b VALUES ('b'), ('c');");
        Execute(connection, "INSERT INTO inner_b VALUES ('b', 'BEE'), ('c', 'CEE');");
        Execute(connection, "ANALYZE;");

        // ORDER BY rowid rather than k: once BINARY itself is overridden below, sorting by the
        // default-collation column would flip along with it, which is irrelevant noise for this
        // regression -- only the seek-eligibility and join-correctness of inner_a_k matter here.
        const string joinA =
            """
            SELECT inner_a.payload
            FROM outer_a
            JOIN inner_a INDEXED BY inner_a_k ON outer_a.k = inner_a.k
            ORDER BY outer_a.rowid;
            """;
        const string joinB =
            """
            SELECT inner_b.payload
            FROM outer_b
            JOIN inner_b INDEXED BY inner_b_k ON outer_b.k = inner_b.k
            ORDER BY outer_b.rowid;
            """;

        AssertDirectSeek(database, connection, joinA, "two", "three");
        AssertDirectSeek(database, connection, joinB, "BEE", "CEE");

        // Override the built-in BINARY collation with a genuinely different order: inner_a_k's
        // durable tree, built under the real BINARY order, is now provably stale -- and unrelated
        // collation_b is made unavailable at the same time (unregistered, so its own durable
        // index is intact but nothing has bound its callback right now).
        database.RegisterCollation("BINARY", ReverseOrdinal);
        database.UnregisterCollation("collation_b").Should().BeTrue();
        AssertNoSearch(database, connection, joinA, "two", "three");
        AssertNoSearch(database, connection, joinB, "BEE", "CEE");

        // A targeted REINDEX of inner_a_k must succeed -- decoding/freeing its old tree's pages
        // structurally only, never comparing the old physical order under the overridden BINARY
        // delegate -- and must never require collation_b's callback.
        Execute(connection, "REINDEX inner_a_k;");
        AssertDirectSeek(database, connection, joinA, "two", "three");

        // inner_b's tree must remain completely untouched by any of this: still unavailable for
        // direct seek, still intact, and immediately seekable again once collation_b is
        // re-registered, with no REINDEX of its own.
        AssertNoSearch(database, connection, joinB, "BEE", "CEE");
        database.RegisterCollation("collation_b", ReverseOrdinal);
        AssertDirectSeek(database, connection, joinB, "BEE", "CEE");
    }

    [Test]
    public void ExplicitTransactionPreservesTargetedReindexOfOneIndexWithUnavailableSiblingCollation()
    {
        // TargetedIndexRebuild must survive an explicit multi-statement transaction:
        // BEGIN; REINDEX inner_a_k; COMMIT; must still resolve and apply the targeted rebuild at
        // actual commit time (TransactionDatabaseState.TargetedIndexRebuildNames /
        // CommitTransaction's ResolveTargetedIndexRebuild), never requiring collation_b's
        // callback and never touching inner_b_k's durable tree.
        var fileSystem = new InMemoryFileSystem();
        const string path = "custom-collation-explicit-transaction-targeted-reindex.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            database.RegisterCollation("collation_a", ReverseOrdinal);
            database.RegisterCollation(
                "collation_b",
                static (left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));

            Execute(connection, "CREATE TABLE outer_a(k TEXT);");
            Execute(connection, "CREATE TABLE inner_a(k TEXT COLLATE collation_a, payload TEXT);");
            Execute(connection, "CREATE INDEX inner_a_k ON inner_a(k);");
            Execute(connection, "CREATE TABLE outer_b(k TEXT);");
            Execute(connection, "CREATE TABLE inner_b(k TEXT COLLATE collation_b, payload TEXT);");
            Execute(connection, "CREATE INDEX inner_b_k ON inner_b(k);");

            Execute(connection, "INSERT INTO outer_a VALUES ('b'), ('c');");
            Execute(connection, "INSERT INTO inner_a VALUES ('b', 'two'), ('c', 'three');");
            Execute(connection, "INSERT INTO outer_b VALUES ('b'), ('c');");
            Execute(connection, "INSERT INTO inner_b VALUES ('b', 'BEE'), ('c', 'CEE');");
            Execute(connection, "ANALYZE;");
        }

        // Replace collation_a with a genuinely different order so inner_a_k is provably stale;
        // collation_b is never registered on this connection at all.
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        reopened.RegisterCollation("collation_a", static (left, right) => string.CompareOrdinal(left, right));

        const string joinA =
            """
            SELECT inner_a.payload
            FROM outer_a
            JOIN inner_a INDEXED BY inner_a_k ON outer_a.k = inner_a.k
            ORDER BY outer_a.k;
            """;
        const string joinB =
            """
            SELECT inner_b.payload
            FROM outer_b
            JOIN inner_b INDEXED BY inner_b_k ON outer_b.k = inner_b.k
            ORDER BY outer_b.k;
            """;

        AssertNoSearch(reopened, reopenedConnection, joinA, "two", "three");
        AssertNoSearch(reopened, reopenedConnection, joinB, "BEE", "CEE");

        Execute(reopenedConnection, "BEGIN;");
        Execute(reopenedConnection, "REINDEX inner_a_k;");
        Execute(reopenedConnection, "COMMIT;");

        AssertDirectSeek(reopened, reopenedConnection, joinA, "two", "three");
        // ...and must leave B's tree completely untouched by the transaction's commit.
        AssertNoSearch(reopened, reopenedConnection, joinB, "BEE", "CEE");

        reopened.RegisterCollation(
            "collation_b",
            static (left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));
        AssertDirectSeek(reopened, reopenedConnection, joinB, "BEE", "CEE");
    }

    [Test]
    public void SavepointRollbackToRestoresTargetedReindexTrackingThenFreshReindexStillCommitsCorrectly()
    {
        // ROLLBACK TO must restore the transaction's tracked targeted-rebuild index names to
        // exactly the savepoint's snapshot rather than leaving stale entries behind: a REINDEX
        // issued inside a savepoint, undone via ROLLBACK TO, then reissued and actually committed
        // must still resolve and apply cleanly, and an unrelated unavailable sibling collation
        // must never be required by any of it.
        var fileSystem = new InMemoryFileSystem();
        const string path = "custom-collation-savepoint-rollback-targeted-reindex.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            database.RegisterCollation("collation_a", ReverseOrdinal);
            database.RegisterCollation(
                "collation_b",
                static (left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));

            Execute(connection, "CREATE TABLE outer_a(k TEXT);");
            Execute(connection, "CREATE TABLE inner_a(k TEXT COLLATE collation_a, payload TEXT);");
            Execute(connection, "CREATE INDEX inner_a_k ON inner_a(k);");
            Execute(connection, "CREATE TABLE outer_b(k TEXT);");
            Execute(connection, "CREATE TABLE inner_b(k TEXT COLLATE collation_b, payload TEXT);");
            Execute(connection, "CREATE INDEX inner_b_k ON inner_b(k);");

            Execute(connection, "INSERT INTO outer_a VALUES ('b'), ('c');");
            Execute(connection, "INSERT INTO inner_a VALUES ('b', 'two'), ('c', 'three');");
            Execute(connection, "INSERT INTO outer_b VALUES ('b'), ('c');");
            Execute(connection, "INSERT INTO inner_b VALUES ('b', 'BEE'), ('c', 'CEE');");
            Execute(connection, "ANALYZE;");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        reopened.RegisterCollation("collation_a", static (left, right) => string.CompareOrdinal(left, right));

        const string joinA =
            """
            SELECT inner_a.payload
            FROM outer_a
            JOIN inner_a INDEXED BY inner_a_k ON outer_a.k = inner_a.k
            ORDER BY outer_a.k;
            """;
        const string joinB =
            """
            SELECT inner_b.payload
            FROM outer_b
            JOIN inner_b INDEXED BY inner_b_k ON outer_b.k = inner_b.k
            ORDER BY outer_b.k;
            """;

        AssertNoSearch(reopened, reopenedConnection, joinA, "two", "three");

        Execute(reopenedConnection, "BEGIN;");
        Execute(reopenedConnection, "SAVEPOINT sp;");
        Execute(reopenedConnection, "REINDEX inner_a_k;");
        Execute(reopenedConnection, "ROLLBACK TO sp;");
        Execute(reopenedConnection, "REINDEX inner_a_k;");
        Execute(reopenedConnection, "COMMIT;");

        AssertDirectSeek(reopened, reopenedConnection, joinA, "two", "three");
        AssertNoSearch(reopened, reopenedConnection, joinB, "BEE", "CEE");

        reopened.RegisterCollation(
            "collation_b",
            static (left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));
        AssertDirectSeek(reopened, reopenedConnection, joinB, "BEE", "CEE");
    }

    [Test]
    public void ExplicitTransactionUnionsMultipleTargetedReindexStatementsAndLeavesUnavailableSiblingUntouched()
    {
        // A single explicit transaction that issues REINDEX for two different indexes must union
        // both into the eventual commit's targeted-rebuild set (TransactionDatabaseState
        // .TargetedIndexRebuildNames accumulates across every statement in the transaction), so
        // both end up correctly rebuilt in the same commit -- while a third, wholly unrelated
        // index whose collation is never registered on this connection stays untouched.
        var fileSystem = new InMemoryFileSystem();
        const string path = "custom-collation-transaction-union-targeted-reindex.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            database.RegisterCollation("collation_a", ReverseOrdinal);
            database.RegisterCollation("collation_c", ReverseOrdinal);
            database.RegisterCollation(
                "collation_b",
                static (left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));

            Execute(connection, "CREATE TABLE outer_a(k TEXT);");
            Execute(connection, "CREATE TABLE inner_a(k TEXT COLLATE collation_a, payload TEXT);");
            Execute(connection, "CREATE INDEX inner_a_k ON inner_a(k);");
            Execute(connection, "CREATE TABLE outer_c(k TEXT);");
            Execute(connection, "CREATE TABLE inner_c(k TEXT COLLATE collation_c, payload TEXT);");
            Execute(connection, "CREATE INDEX inner_c_k ON inner_c(k);");
            Execute(connection, "CREATE TABLE outer_b(k TEXT);");
            Execute(connection, "CREATE TABLE inner_b(k TEXT COLLATE collation_b, payload TEXT);");
            Execute(connection, "CREATE INDEX inner_b_k ON inner_b(k);");

            Execute(connection, "INSERT INTO outer_a VALUES ('b'), ('c');");
            Execute(connection, "INSERT INTO inner_a VALUES ('b', 'two'), ('c', 'three');");
            Execute(connection, "INSERT INTO outer_c VALUES ('b'), ('c');");
            Execute(connection, "INSERT INTO inner_c VALUES ('b', 'twoC'), ('c', 'threeC');");
            Execute(connection, "INSERT INTO outer_b VALUES ('b'), ('c');");
            Execute(connection, "INSERT INTO inner_b VALUES ('b', 'BEE'), ('c', 'CEE');");
            Execute(connection, "ANALYZE;");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        // Replace both a and c with genuinely different orders; b is never registered on this
        // connection at all.
        reopened.RegisterCollation("collation_a", static (left, right) => string.CompareOrdinal(left, right));
        reopened.RegisterCollation("collation_c", static (left, right) => string.CompareOrdinal(left, right));

        const string joinA =
            """
            SELECT inner_a.payload
            FROM outer_a
            JOIN inner_a INDEXED BY inner_a_k ON outer_a.k = inner_a.k
            ORDER BY outer_a.k;
            """;
        const string joinC =
            """
            SELECT inner_c.payload
            FROM outer_c
            JOIN inner_c INDEXED BY inner_c_k ON outer_c.k = inner_c.k
            ORDER BY outer_c.k;
            """;
        const string joinB =
            """
            SELECT inner_b.payload
            FROM outer_b
            JOIN inner_b INDEXED BY inner_b_k ON outer_b.k = inner_b.k
            ORDER BY outer_b.k;
            """;

        AssertNoSearch(reopened, reopenedConnection, joinA, "two", "three");
        AssertNoSearch(reopened, reopenedConnection, joinC, "twoC", "threeC");
        AssertNoSearch(reopened, reopenedConnection, joinB, "BEE", "CEE");

        Execute(reopenedConnection, "BEGIN;");
        Execute(reopenedConnection, "REINDEX inner_a_k;");
        Execute(reopenedConnection, "REINDEX inner_c_k;");
        Execute(reopenedConnection, "COMMIT;");

        AssertDirectSeek(reopened, reopenedConnection, joinA, "two", "three");
        AssertDirectSeek(reopened, reopenedConnection, joinC, "twoC", "threeC");
        AssertNoSearch(reopened, reopenedConnection, joinB, "BEE", "CEE");

        reopened.RegisterCollation(
            "collation_b",
            static (left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));
        AssertDirectSeek(reopened, reopenedConnection, joinB, "BEE", "CEE");
    }

    [Test]
    public void UnavailableCustomCollationIndexNeverBlocksUnrelatedCreateTableOrSiblingCreateIndex()
    {
        // Generalized full-rewrite persistence must preserve an unavailable custom-collation
        // index's root page and page subtree byte-for-byte rather than requiring BuildIndexTree
        // (which needs a live comparator) for it: a wholly unrelated CREATE TABLE, and a sibling
        // CREATE INDEX on an entirely separate table, must both succeed while inner_b_k's own
        // collation is unavailable -- neither touches inner_b's table, so its collation must
        // never be resolved by any full catalog rewrite either statement triggers.
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile(
            "custom-collation-unavailable-does-not-block-ddl.db", fileSystem);
        using var connection = database.Connect();
        database.RegisterCollation("collation_b", ReverseOrdinal);

        Execute(connection, "CREATE TABLE outer_b(k TEXT);");
        Execute(connection, "CREATE TABLE inner_b(k TEXT COLLATE collation_b, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_b_k ON inner_b(k);");
        Execute(connection, "INSERT INTO outer_b VALUES ('b'), ('c');");
        Execute(connection, "INSERT INTO inner_b VALUES ('b', 'BEE'), ('c', 'CEE');");
        Execute(connection, "ANALYZE;");

        const string joinB =
            """
            SELECT inner_b.payload
            FROM outer_b
            JOIN inner_b INDEXED BY inner_b_k ON outer_b.k = inner_b.k
            ORDER BY outer_b.k;
            """;
        AssertDirectSeek(database, connection, joinB, "BEE", "CEE");

        // Make collation_b unavailable on this connection: inner_b_k becomes dirty/unavailable
        // but its durable tree remains fully intact.
        database.UnregisterCollation("collation_b").Should().BeTrue();
        AssertNoSearch(database, connection, joinB, "BEE", "CEE");

        // A wholly unrelated CREATE TABLE, and a sibling CREATE INDEX (ia) on an entirely
        // separate table, must both succeed even though inner_b_k (ib) is unavailable right now.
        Execute(connection, "CREATE TABLE unrelated(id INTEGER PRIMARY KEY, value TEXT);");
        Execute(connection, "INSERT INTO unrelated VALUES (1, 'x');");
        Execute(connection, "CREATE TABLE sibling(k TEXT, payload TEXT);");
        Execute(connection, "CREATE INDEX ia ON sibling(k);");
        Execute(connection, "INSERT INTO sibling VALUES ('m', 'ehm');");

        Query(connection, "SELECT value FROM unrelated;")
            .Select(row => row[0].AsText()).Should().Equal("x");
        Query(connection, "SELECT payload FROM sibling WHERE k = 'm';")
            .Select(row => row[0].AsText()).Should().Equal("ehm");

        // inner_b's tree must have been preserved byte-for-byte through however many full catalog
        // rewrites the DDL above triggered: still unavailable for direct seek, still intact, and
        // immediately seekable again the moment collation_b is re-registered -- with no REINDEX.
        AssertNoSearch(database, connection, joinB, "BEE", "CEE");
        database.RegisterCollation("collation_b", ReverseOrdinal);
        AssertDirectSeek(database, connection, joinB, "BEE", "CEE");
    }

    [Test]
    public void UnavailableEmbeddedPartialPredicateCollationIndexNeverBlocksUnrelatedCreateTableOrSiblingCreateIndex()
    {
        // Distinct from UnavailableCustomCollationIndexNeverBlocksUnrelatedCreateTableOrSiblingCreateIndex
        // above: there the unavailable collation qualifies the indexed column itself
        // (CreateIndexComparer / GetIndexCollation), which TryPreserveUnavailableIndexTrees already
        // detected directly even before finding #3's fix. Here the collation is embedded only
        // inside the partial index's WHERE predicate (IndexExpressionSemantics.
        // CollectEmbeddedCollationNames / EmbeddedFileStore.TryFindUnresolvedEmbeddedIndexCollation),
        // invisible to CreateIndexComparer, so preservation must be driven by the embedded-collation
        // probe instead: without it, an unrelated full-catalog rewrite would fall through to
        // BuildIndexTree for this index, which evaluates the predicate through the (missing)
        // callback and throws "no such collation sequence" instead of leaving this index's durable
        // tree alone.
        var fileSystem = new InMemoryFileSystem();
        const string path = "custom-collation-unavailable-embedded-predicate-does-not-block-ddl.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            database.RegisterCollation("ci_text", CaseInsensitiveCustom);
            Execute(connection, "CREATE TABLE outer_items(k TEXT);");
            Execute(connection, "CREATE TABLE inner_items(k TEXT, payload TEXT, tag TEXT);");
            Execute(
                connection,
                "CREATE INDEX inner_items_k ON inner_items(k) WHERE tag COLLATE ci_text = 'KEEP';");
            Execute(connection, "INSERT INTO outer_items VALUES ('b'), ('c');");
            Execute(
                connection,
                "INSERT INTO inner_items VALUES ('b', 'two', 'keep'), ('c', 'excluded', 'drop'), "
                    + "('c', 'three', 'Keep');");
            Execute(connection, "ANALYZE;");

            const string sqlBeforeReopen =
                """
                SELECT inner_items.payload
                FROM outer_items
                JOIN inner_items INDEXED BY inner_items_k
                    ON outer_items.k = inner_items.k AND inner_items.tag COLLATE ci_text = 'KEEP'
                ORDER BY outer_items.k;
                """;
            AssertDirectSeek(database, connection, sqlBeforeReopen, "two", "three");
        }

        // Reopen with ci_text never registered on this connection at all: inner_items_k becomes
        // fully unavailable/dirty (structural validation must defer, per
        // ValidateStoredIndex/TryFindUnresolvedEmbeddedIndexCollation), but its durable tree
        // remains fully intact. Note that -- unlike the column-level collation_b case above --
        // actually *evaluating* this predicate (even via a plan-only scan fallback) also requires
        // ci_text to be resolvable, since the predicate itself, not merely the index, embeds the
        // COLLATE reference: PartialIndexPredicateMissingCollateCallbackFailsClosedOnlyWhenARowIsEvaluated
        // already covers that fail-closed evaluation behavior. This test only checks the query
        // *plan* (which never evaluates row data) and reads that avoid the predicate entirely, so
        // it stays isolated to finding #3's actual concern: durable-tree preservation across
        // unrelated full-catalog rewrites.
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();

        const string joinSql =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_k
                ON outer_items.k = inner_items.k AND inner_items.tag COLLATE ci_text = 'KEEP'
            ORDER BY outer_items.k;
            """;
        const string collationSafeSql = "SELECT k, payload, tag FROM inner_items ORDER BY k, payload;";
        var originalRows = new[] { "b|two|keep", "c|excluded|drop", "c|three|Keep" };

        AssertPlanHasNoSearch(reopenedConnection, joinSql);
        Query(reopenedConnection, collationSafeSql)
            .Select(row => $"{row[0].AsText()}|{row[1].AsText()}|{row[2].AsText()}")
            .Should().Equal(originalRows);

        // A wholly unrelated CREATE TABLE, and a sibling CREATE INDEX (ia) on an entirely separate
        // table, must both succeed even though inner_items_k's embedded predicate collation is
        // unavailable right now: neither touches inner_items' table, so ci_text must never be
        // resolved by any full catalog rewrite either statement triggers.
        Execute(reopenedConnection, "CREATE TABLE unrelated(id INTEGER PRIMARY KEY, value TEXT);");
        Execute(reopenedConnection, "INSERT INTO unrelated VALUES (1, 'x');");
        Execute(reopenedConnection, "CREATE TABLE sibling(k TEXT, payload TEXT);");
        Execute(reopenedConnection, "CREATE INDEX ia ON sibling(k);");
        Execute(reopenedConnection, "INSERT INTO sibling VALUES ('m', 'ehm');");

        Query(reopenedConnection, "SELECT value FROM unrelated;")
            .Select(row => row[0].AsText()).Should().Equal("x");
        Query(reopenedConnection, "SELECT payload FROM sibling WHERE k = 'm';")
            .Select(row => row[0].AsText()).Should().Equal("ehm");

        // inner_items_k's tree must have been preserved byte-for-byte through however many full
        // catalog rewrites the DDL above triggered: still unavailable for direct seek, its rows
        // still intact, and immediately seekable again the moment ci_text is registered -- with
        // no REINDEX.
        AssertPlanHasNoSearch(reopenedConnection, joinSql);
        Query(reopenedConnection, collationSafeSql)
            .Select(row => $"{row[0].AsText()}|{row[1].AsText()}|{row[2].AsText()}")
            .Should().Equal(originalRows);
        reopened.RegisterCollation("ci_text", CaseInsensitiveCustom);
        AssertDirectSeek(reopened, reopenedConnection, joinSql, "two", "three");
    }

    [Test]
    public void UnavailableCustomCollationIndexSurvivesNewSiblingIndexOnItsOwnTable()
    {
        // Finding #2 regression: unavailable-custom-index preservation must be decided per index
        // (row/tree storage unchanged for the owning table, plus this specific EmbeddedIndex
        // instance unchanged), never by requiring the whole table's index *list count* to match
        // the previous commit. CREATE INDEX new_binary here only appends a new EmbeddedIndex
        // instance to inner_b.Indexes -- it never touches inner_b's own RowStore -- so
        // inner_b_k's own preservation must not be defeated merely because inner_b grows a
        // sibling index of its own, unlike UnavailableCustomCollationIndexNeverBlocksUnrelated-
        // CreateTableOrSiblingCreateIndex above, whose sibling CREATE INDEX targets an entirely
        // different table.
        var fileSystem = new InMemoryFileSystem();
        const string path = "custom-collation-unavailable-survives-same-table-sibling-index.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            database.RegisterCollation("collation_b", ReverseOrdinal);
            Execute(connection, "CREATE TABLE outer_b(k TEXT);");
            Execute(connection, "CREATE TABLE inner_b(k TEXT COLLATE collation_b, payload TEXT);");
            Execute(connection, "CREATE INDEX inner_b_k ON inner_b(k);");
            Execute(connection, "INSERT INTO outer_b VALUES ('b'), ('c');");
            Execute(connection, "INSERT INTO inner_b VALUES ('b', 'BEE'), ('c', 'CEE');");
            // Noise rows seeded now, while collation_b is still registered, so inner_b already has
            // enough rows for the cost-based planner to prefer a seek (matching the SeedNoiseRows
            // convention) once collation_b becomes unavailable below: nothing further is ever
            // inserted into inner_b after this point, so its row storage stays provably unchanged
            // and inner_b_k remains eligible for byte-for-byte preservation throughout the test.
            Execute(
                connection,
                "INSERT INTO inner_b VALUES "
                    + string.Join(
                        ", ",
                        Enumerable.Range(1, 500).Select(value => $"('noise{value:0000}', 'n{value}')"))
                    + ";");
            Execute(connection, "ANALYZE;");
        }

        // Reopen with collation_b never registered on this connection at all: inner_b_k becomes
        // fully unavailable/dirty, but its durable tree remains fully intact.
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();

        const string joinB =
            """
            SELECT inner_b.payload
            FROM outer_b
            JOIN inner_b INDEXED BY inner_b_k ON outer_b.k = inner_b.k
            ORDER BY outer_b.k;
            """;
        AssertNoSearch(reopened, reopenedConnection, joinB, "BEE", "CEE");

        // CREATE INDEX new_binary is a sibling of inner_b_k on THE SAME table (inner_b), not on a
        // separate table. Before the fix, preservation of an unavailable index was (wrongly)
        // gated on the *table's* whole index-list count matching the previous commit; adding
        // new_binary changes that count, so inner_b_k -- despite being completely unrelated to
        // and untouched by this statement -- would be routed through BuildIndexTree, which needs
        // the missing collation_b callback and throws "no such collation sequence" instead of
        // leaving inner_b_k's durable tree alone.
        Execute(reopenedConnection, "CREATE INDEX new_binary ON inner_b(payload);");

        // inner_b_k's tree must have been preserved byte-for-byte across the same-table sibling
        // CREATE INDEX above: still unavailable for direct seek, its rows still intact, and
        // immediately seekable again the moment collation_b is re-registered -- with no REINDEX.
        AssertNoSearch(reopened, reopenedConnection, joinB, "BEE", "CEE");

        // new_binary itself, meanwhile, must already be a fully-built, directly-seekable index:
        // it never depended on collation_b at all, so it must work immediately, before
        // collation_b is ever re-registered. A plain single-table forced access path is used
        // here rather than AssertDirectSeek's JOIN-reordering proof, since that rewrite
        // additionally requires ANALYZE statistics that cannot be gathered while collation_b
        // remains unregistered (ANALYZE sorts statistic keys for every index on the connection,
        // including the still-unavailable inner_b_k); a single-table INDEXED BY access path is
        // not subject to that join-order cost decision at all.
        AssertSingleTableSearch(
            reopenedConnection,
            "SELECT k FROM inner_b INDEXED BY new_binary WHERE payload = 'BEE';",
            "new_binary",
            "b");
        AssertSingleTableSearch(
            reopenedConnection,
            "SELECT k FROM inner_b INDEXED BY new_binary WHERE payload = 'CEE';",
            "new_binary",
            "c");

        // Reopen a second time: both indexes must persist correctly through a full close/reopen
        // cycle. new_binary must still be directly seekable with no dependency on collation_b;
        // inner_b_k must still be unavailable-but-intact until collation_b is registered again,
        // at which point it becomes directly seekable too -- with no REINDEX ever required for
        // either index.
        using var reopenedAgain = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedAgainConnection = reopenedAgain.Connect();

        const string payloadLookupBee =
            "SELECT k FROM inner_b INDEXED BY new_binary WHERE payload = 'BEE';";
        const string payloadLookupCee =
            "SELECT k FROM inner_b INDEXED BY new_binary WHERE payload = 'CEE';";
        AssertSingleTableSearch(reopenedAgainConnection, payloadLookupBee, "new_binary", "b");
        AssertSingleTableSearch(reopenedAgainConnection, payloadLookupCee, "new_binary", "c");
        AssertNoSearch(reopenedAgain, reopenedAgainConnection, joinB, "BEE", "CEE");

        reopenedAgain.RegisterCollation("collation_b", ReverseOrdinal);
        AssertDirectSeek(reopenedAgain, reopenedAgainConnection, joinB, "BEE", "CEE");
        AssertSingleTableSearch(reopenedAgainConnection, payloadLookupBee, "new_binary", "b");
        AssertSingleTableSearch(reopenedAgainConnection, payloadLookupCee, "new_binary", "c");
    }

    [Test]
    public void AlterColumnRebuildWithCollidingRevisionAndIndexReferencesFailsClosedInsteadOfPreservingStaleTree()
    {
        // EmbeddedTable.CreateWithAlteredColumn (used for ALTER TABLE ... ALTER COLUMN) builds a
        // brand-new RowStore and re-adds every existing row into it one-by-one, and also carries
        // the table's existing EmbeddedIndex instances forward by reference. With exactly two pure
        // INSERTs and no prior UPDATE/DELETE on either table, the rebuilt table's Rows.Revision
        // (== row count) trivially collides with the pre-ALTER table's Revision, and items_tag is
        // still the identical index-object reference -- the two proofs a naive "this table is
        // unchanged since the last durable commit" check would rely on to cheaply preserve an
        // unavailable index's durable tree byte-for-byte instead of revalidating it. Rows.Revision
        // and index-reference equality alone are not collision-resistant enough to detect this:
        // only a distinct RowStore.LineageId (assigned fresh on every genuinely new RowStore, and
        // carried forward only by Clone()'s same-statement working-copy path) proves the table
        // was *not* actually rebuilt. Retyping tag to NUMERIC (dropping its COLLATE clause, which
        // is legal even with swap_text unavailable) re-coerces the existing '10'/'20' TEXT values
        // into integers, so items_tag's durable tree -- still keyed on the old TEXT bytes -- would
        // silently go stale if it were preserved as-is instead of being revalidated and, since
        // swap_text truly cannot be resolved on this connection, failing closed.
        var fileSystem = new InMemoryFileSystem();
        const string path = "custom-collation-alter-column-lineage.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            database.RegisterCollation("swap_text", ReverseOrdinal);
            Execute(connection, "CREATE TABLE items(tag TEXT COLLATE swap_text, payload TEXT);");
            Execute(connection, "CREATE INDEX items_tag ON items(tag);");
            Execute(connection, "INSERT INTO items VALUES ('10', 'one'), ('20', 'two');");
            Execute(connection, "ANALYZE;");
        }

        // Reopen with swap_text never registered on this connection at all (items_tag is fully
        // unavailable), but register an unrelated collation so the store's resolver is non-null:
        // deferred validation is scoped per-index, never gated on whether *any* resolver exists
        // (see PartiallyRegisteredCollationsIsolateUnavailableIndexPerIndexNotGlobally).
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        reopened.RegisterCollation("unrelated_collation", ReverseOrdinal);

        // The ALTER must run inside an explicit transaction: EmbeddedDatabase.CommitTransaction
        // threads a non-null PragmaHeaderMetadata through EmbeddedFileStore.Persist whenever the
        // transaction contains schema changes, which is what routes this commit into the
        // full-catalog-rewrite path (PersistCore's own GetIndexDefinitions/TryPreserveUnavailableIndexTrees
        // call, correctly threading the real previousTables) instead of the bounded-table-leaf-mutation
        // fast path -- whose own first HasCurrentSchemaShape probe never receives previousTables at all
        // and so would fail closed unconditionally the moment any resolver is registered, independent of
        // this test's actual LineageId regression. Statement execution inside an explicit transaction
        // only rewrites the in-memory working catalog, so the ALTER itself must not throw -- the
        // durable-persistence attempt (and the expected failure) happens at COMMIT.
        Execute(reopenedConnection, "BEGIN;");
        Execute(reopenedConnection, "ALTER TABLE items ALTER COLUMN tag TO tag NUMERIC;");
        Action commit = () => Execute(reopenedConnection, "COMMIT;");
        commit.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*no such collation sequence*swap_text*");
        Execute(reopenedConnection, "ROLLBACK;");

        // The failed ALTER must not have partially applied, and must never have persisted a
        // rebuilt table with items_tag's stale pre-ALTER tree copied forward: reopening with
        // swap_text registered again must find items exactly as originally written, with its
        // original TEXT values, original schema, and an intact, still directly-seekable index.
        using var reopenedAgain = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedAgainConnection = reopenedAgain.Connect();
        reopenedAgain.RegisterCollation("swap_text", ReverseOrdinal);

        Query(reopenedAgainConnection, "SELECT sql FROM sqlite_schema WHERE name = 'items';")
            .Select(row => row[0].AsText())
            .Should().Equal("CREATE TABLE items(tag TEXT COLLATE swap_text, payload TEXT)");
        Query(reopenedAgainConnection, "SELECT tag, payload FROM items ORDER BY rowid;")
            .Select(row => $"{row[0].AsText()}|{row[1].AsText()}")
            .Should().Equal("10|one", "20|two");
        Query(reopenedAgainConnection, "SELECT payload FROM items INDEXED BY items_tag WHERE tag = '10';")
            .Select(row => row[0].AsText())
            .Should().Equal("one");
    }

    [Test]
    public void ConcurrentRegisterCollationDuringQueriesNeverObservesANewComparatorWithAnOldCleanProof()
    {
        // RegisterCollation must publish the callback-dictionary write, the registry-version
        // bump, the file-store resolver rebind, and the _customCollationIndexClean invalidation
        // as one atomic unit under _gate: a concurrently running query must never observe a mix
        // where the dictionary already holds a new comparator while the clean-cache/registry
        // version still describe the previous one (which would let a stale "clean" proof stand
        // in for the new comparator's real semantics). Stress this under real concurrent load,
        // alternating between two genuinely different orderings, so any torn publish would
        // surface as either an exception or an incorrect/incomplete join result.
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("custom-collation-register-atomicity.db", fileSystem);
        using (var setupConnection = database.Connect())
        {
            database.RegisterCollation("swap_text", ReverseOrdinal);
            Execute(setupConnection, "CREATE TABLE outer_items(k TEXT);");
            Execute(setupConnection, "CREATE TABLE inner_items(k TEXT COLLATE swap_text, payload TEXT);");
            Execute(setupConnection, "CREATE INDEX inner_items_k ON inner_items(k);");
            Execute(setupConnection, "INSERT INTO outer_items VALUES ('b'), ('c');");
            Execute(setupConnection, "INSERT INTO inner_items VALUES ('b', 'two'), ('c', 'three');");
        }

        var forward = (Func<string, string, int>)(static (left, right) => string.CompareOrdinal(left, right));
        var reverse = ReverseOrdinal;

        const string sql =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items INDEXED BY inner_items_k ON outer_items.k = inner_items.k;
            """;

        const int iterations = 2_000;
        var registrar = Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++)
                database.RegisterCollation("swap_text", i % 2 == 0 ? forward : reverse);
        });

        var reader = Task.Run(() =>
        {
            using var connection = database.Connect();
            for (var i = 0; i < iterations; i++)
            {
                // Equality is unaffected by which of the two orderings is bound (both report 0
                // exactly for equal values), so the correct answer is always these two rows in
                // some order -- never a torn mix such as a duplicate, a missing row, or a value
                // that belongs to neither collation's valid answer.
                var payloads = ReadRows(connection, sql).Select(row => row[0].AsText()).ToList();
                payloads.Should().BeEquivalentTo(new[] { "two", "three" });
            }
        });

        Task.WaitAll(registrar, reader);
    }

    [Test]
    public void ReentrantCollationReplacementDuringValidationForcesFreshRevalidation()
    {
        // The collation-registry version is monotonic: registration/replacement/unregister all
        // increment it, and IsCustomCollationIndexPlanReady must capture the committed generation
        // and registry version *before* revalidating and only publish its cache if both are still
        // unchanged *after*. A collation callback that reentrantly replaces its own registration
        // mid-comparison -- which is legal, since an ordinary comparator invocation carries no
        // recursive-trigger-callback guard -- must therefore force a discard-and-retry rather than
        // publish a cache entry keyed to a registry version that no longer matches reality.
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("custom-collation-reentrant-replace.db", fileSystem);
        using var connection = database.Connect();

        var reentrantArmed = false;
        var reenteredDuringValidation = false;
        Func<string, string, int> comparer = null!;
        comparer = (left, right) =>
        {
            if (reentrantArmed && !reenteredDuringValidation)
            {
                reenteredDuringValidation = true;
                // Re-registering the same callback mid-comparison bumps the registry version
                // while this very validation pass is still in flight: the "before" version this
                // pass captured can no longer match the "after" version it rechecks, so the
                // otherwise-clean answer it was about to cache must be discarded and retried.
                database.RegisterCollation("reentrant_text", comparer);
            }

            return string.CompareOrdinal(right, left);
        };

        database.RegisterCollation("reentrant_text", comparer);
        Execute(connection, "CREATE TABLE outer_items(k TEXT);");
        Execute(connection, "CREATE TABLE inner_items(k TEXT COLLATE reentrant_text, payload TEXT);");
        Execute(connection, "CREATE INDEX inner_items_k ON inner_items(k);");
        Execute(connection, "INSERT INTO outer_items VALUES ('b'), ('c');");
        Execute(connection, "INSERT INTO inner_items VALUES ('b', 'two'), ('c', 'three');");
        SeedNoiseRows(connection);
        Execute(connection, "ANALYZE;");
        AssertDirectSeek(database, connection, JoinSql, "two", "three");

        // Force a genuinely fresh validation pass: re-registering the same callback bumps the
        // registry version and invalidates any already-cached "clean" answer, so the next seek's
        // validation must actually re-run the comparator from scratch, where the reentrant
        // replacement is armed to fire on its very first invocation.
        database.RegisterCollation("reentrant_text", comparer);
        reentrantArmed = true;
        AssertDirectSeek(database, connection, JoinSql, "two", "three");

        reenteredDuringValidation.Should().BeTrue(
            "the reentrant replacement must actually have fired during validation for this regression to be meaningful");
    }

    [Test]
    public void ExplicitCollateOnBothOperandsUsesLeftOperandPrecedenceNotTheIndexedSide()
    {
        // SQLite's real precedence rule (datatype3.html section 7.1, rule 1 in this engine's
        // terms): when both operands of an equality carry an explicit COLLATE, the LEFT operand's
        // always wins -- regardless of which operand an expression index happens to be built over.
        // Two collations with genuinely different equality semantics on the same byte pairs make a
        // wrong ("whichever operand is indexed wins") resolution observably flip which rows match.
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("custom-collation-explicit-precedence.db", fileSystem);
        using var connection = database.Connect();
        database.RegisterCollation(
            "case_sensitive_exact",
            static (left, right) => string.CompareOrdinal(left, right));
        database.RegisterCollation(
            "fold_case",
            static (left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));

        Execute(connection, "CREATE TABLE outer_items(k TEXT);");
        Execute(connection, "CREATE TABLE inner_items(k TEXT, payload TEXT);");
        // The expression index is built under fold_case -- the RIGHT-hand operand's explicit
        // collation in the join predicate below -- so a buggy "whichever operand is indexed wins"
        // resolution would pick fold_case; the correct, SQLite-compatible resolution must instead
        // use the LEFT operand's explicit case_sensitive_exact.
        Execute(connection, "CREATE INDEX inner_items_expr ON inner_items((k || '') COLLATE fold_case);");
        Execute(connection, "INSERT INTO outer_items VALUES ('A'), ('B'), ('c');");
        Execute(
            connection,
            "INSERT INTO inner_items VALUES ('a', 'wrong-if-fold-wins'), ('B', 'exact-match'), "
                + "('C', 'wrong-if-fold-wins-2'), "
                + string.Join(
                    ", ",
                    Enumerable.Range(1, 500).Select(value => $"('noise{value:0000}', 'n{value}')"))
                + ";");
        Execute(connection, "ANALYZE;");

        const string sql =
            """
            SELECT inner_items.payload
            FROM outer_items
            JOIN inner_items
                ON outer_items.k COLLATE case_sensitive_exact = (inner_items.k || '') COLLATE fold_case
            ORDER BY outer_items.k;
            """;

        // Under the correct left-operand precedence (case_sensitive_exact), 'A' <> 'a' and
        // 'c' <> 'C': only the exact-case pair ('B', 'B') matches. A buggy resolution of the
        // indexed (right) operand's fold_case instead would incorrectly match both mismatched-case
        // pairs too, and could do so via a wrong seek rather than the correct fallback comparison.
        ReadRows(connection, sql).Select(row => row[0].AsText())
            .Should().Equal("exact-match");
    }

    private const string JoinSql =
        """
        SELECT inner_items.payload
        FROM outer_items
        JOIN inner_items INDEXED BY inner_items_k ON outer_items.k = inner_items.k
        ORDER BY outer_items.k;
        """;

    private static void AssertDirectSeek(
        EmbeddedDatabase database,
        EmbeddedConnection connection,
        string sql,
        params string[] expectedPayloads)
    {
        ReadRows(connection, "EXPLAIN QUERY PLAN " + sql)
            .Select(row => row[3].AsText())
            .Should().Contain(value => value.StartsWith("SEARCH ", StringComparison.Ordinal));

        database.ResetJoinOrderDiagnostics();
        ReadRows(connection, sql).Select(row => row[0].AsText()).Should().Equal(expectedPayloads);
        database.JoinIndexSeekMetrics.DurableCursorPlans.Should().BeGreaterThan(0);
        database.JoinIndexSeekMetrics.IndexRowsMaterialized.Should().Be(0);
    }

    /// <summary>
    /// Like <see cref="AssertDirectSeek"/>, but for a plain single-table access path (an
    /// <c>INDEXED BY</c> forced index with no join partner) rather than a durable-pager-seek join
    /// rewrite. Single-table index selection is not subject to the cost-based join-order
    /// decision that backs <see cref="AssertDirectSeek"/>'s <see
    /// cref="VdbeJoinIndexSeekMetrics"/> assertions, so it needs neither <c>ANALYZE</c> statistics
    /// nor <see cref="SeedNoiseRows"/>-sized tables to be honored: an <c>INDEXED BY</c> hint on a
    /// single table always compiles to a "SEARCH ... USING INDEX ..." plan naming that index.
    /// </summary>
    private static void AssertSingleTableSearch(
        EmbeddedConnection connection,
        string sql,
        string indexName,
        params string[] expectedResults)
    {
        ReadRows(connection, "EXPLAIN QUERY PLAN " + sql)
            .Select(row => row[3].AsText())
            .Should()
            .Contain(value =>
                value.StartsWith($"SEARCH ", StringComparison.Ordinal)
                && value.Contains($"USING INDEX {indexName}", StringComparison.Ordinal));

        ReadRows(connection, sql).Select(row => row[0].AsText()).Should().Equal(expectedResults);
    }

    private static void AssertNoSearch(
        EmbeddedDatabase database,
        EmbeddedConnection connection,
        string sql,
        params string[] expectedPayloads)
    {
        ReadRows(connection, "EXPLAIN QUERY PLAN " + sql)
            .Select(row => row[3].AsText())
            .Should().NotContain(value => value.StartsWith("SEARCH ", StringComparison.Ordinal));

        // The rows must still be correct via whatever scan-based plan was chosen instead: falling
        // back away from a direct seek must never change query results.
        ReadRows(connection, sql).Select(row => row[0].AsText()).Should().Equal(expectedPayloads);
    }

    /// <summary>
    /// Like <see cref="AssertNoSearch"/>, but checks only the query plan, never executing <paramref
    /// name="sql"/> itself. Use this when <paramref name="sql"/>'s predicate embeds a COLLATE
    /// reference to a collation that is not currently resolvable on <paramref name="connection"/>:
    /// evaluating the predicate at all (even via a scan-based fallback plan) would then throw
    /// "no such collation sequence", independent of whether the index built over it is available
    /// for planning.
    /// </summary>
    private static void AssertPlanHasNoSearch(EmbeddedConnection connection, string sql)
    {
        ReadRows(connection, "EXPLAIN QUERY PLAN " + sql)
            .Select(row => row[3].AsText())
            .Should().NotContain(value => value.StartsWith("SEARCH ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Fills <c>inner_items</c> with enough rows that the cost-based join-order planner sees a
    /// real benefit to an index seek over a full scan, matching the row-count convention used by
    /// the direct-seek proofs in <c>IndexSeekJoinDirectAccessTests</c>. A 2-row table is too small
    /// for the planner to bother rewriting the join order at all, which would otherwise make the
    /// whole statement decline compilation (an unrelated, pre-existing size heuristic, not a
    /// custom-collation limitation).
    /// </summary>
    private static void SeedNoiseRows(EmbeddedConnection connection)
    {
        var noise = string.Join(
            ", ",
            Enumerable.Range(1, 500).Select(value => $"('noise{value:0000}', 'n{value}')"));
        Execute(connection, $"INSERT INTO inner_items VALUES {noise};");
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    /// <summary>
    /// Like <see cref="Execute"/>, but for pragmas such as <c>PRAGMA journal_mode=mvcc;</c> that --
    /// like real SQLite -- report their (possibly newly set) value as a single result row before
    /// completing, rather than going straight to <see cref="StatementStepResult.Done"/>.
    /// </summary>
    private static void ExecutePragma(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    private static List<SqlValue[]> Query(EmbeddedConnection connection, string sql)
        => ReadRows(connection, sql);

    private static List<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
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
