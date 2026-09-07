using Ahtola.Core;

namespace Ahtola.Tests.Sqltest;

/// <summary>
/// Builds the 12 <c>integrity_check/parity_*</c> corruption fixtures the pinned corpus
/// expects at <c>database/integrity_*.db</c>. Each fixture is produced by running ordinary
/// setup SQL through the managed engine's real write path (so the physical page layout is
/// whatever Ahtola actually produces, not a synthetic mock), then patching a handful of
/// bytes directly to introduce one specific, well-understood defect — mirroring the
/// generator Turso itself uses to build the same fixtures for its own sqltest corpus:
/// <c>turso-src/testing/sqltest/src/generator/mod.rs</c> (functions <c>generate_*_fixture</c>
/// and their <c>patch_*</c> byte-patch helpers). The corruption technique (locate a b-tree
/// page by root page number, walk the cell pointer array, decode record varints, flip one
/// byte) is the same one <c>Ahtola.Core/Storage</c> reads back, so <c>PRAGMA
/// integrity_check</c>/<c>quick_check</c> exercises the real format-parsing logic against
/// actually-corrupted bytes rather than an expected-result mock.
///
/// Page size and object root pages are read directly from the on-disk bytes
/// (<see cref="SqliteFixtureBytePatcher.ReadDbHeaderPageSize"/>/<see cref="SqliteFixtureBytePatcher.FindObjectRootPage"/>)
/// rather than via <c>PRAGMA page_size</c>/<c>SELECT rootpage FROM sqlite_schema</c>: Ahtola's
/// <c>sqlite_schema</c> projection always reports <c>rootpage 0</c> (a documented stub for the
/// in-memory read path), so a SQL-level lookup can never find the real physical page.
/// </summary>
internal static class SqltestIntegrityFixtureGenerator
{
    public static void RegisterInto(IDictionary<string, Action<string>> generators)
    {
        generators["database/integrity_missing_index_entry.db"] = path => GenerateMissingIndexEntry(
            path,
            """
            PRAGMA page_size=4096;
            CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT);
            CREATE INDEX idx_b ON t(b);
            INSERT INTO t VALUES (1,'a'),(2,'b'),(3,'c');
            """,
            "idx_b");

        generators["database/integrity_missing_expression_index_entry.db"] = path => GenerateMissingIndexEntry(
            path,
            """
            PRAGMA page_size=4096;
            CREATE TABLE t(a INTEGER PRIMARY KEY, d TEXT);
            CREATE INDEX idx_expr ON t(lower(d));
            INSERT INTO t VALUES (1,'Alpha'),(2,'Bravo'),(3,'Charlie');
            """,
            "idx_expr");

        generators["database/integrity_missing_partial_index_entry.db"] = path => GenerateMissingIndexEntry(
            path,
            """
            PRAGMA page_size=4096;
            CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT, c INTEGER);
            CREATE INDEX idx_partial ON t(b) WHERE c = 1;
            INSERT INTO t VALUES (1,'one',1),(2,'two',0),(3,'three',1),(4,'four',0);
            """,
            "idx_partial");

        generators["database/integrity_check_constraint_violation.db"] = GenerateCheckConstraintViolation;
        generators["database/integrity_check_constraint_violation_quick.db"] = GenerateCheckConstraintViolation;
        generators["database/integrity_not_null_violation.db"] = GenerateNotNullViolation;
        generators["database/integrity_non_unique_index_entry.db"] = GenerateNonUniqueIndexEntry;
        generators["database/integrity_missing_unique_index_entry.db"] = GenerateMissingUniqueIndexEntry;
        generators["database/integrity_freelist_count_mismatch.db"] = GenerateFreelistCountMismatch;
        generators["database/integrity_freelist_trunk_corrupt.db"] = GenerateFreelistTrunkCorrupt;
        generators["database/integrity_overflow_list_length_mismatch.db"] = GenerateOverflowListLengthMismatch;
        generators["database/integrity_gencol_not_null_violation.db"] = GenerateGencolNotNullViolation;
    }

    /// <summary>
    /// Drops the last cell from a b-tree page (mirrors
    /// <c>generate_missing_index_entry_fixture</c>): builds the table/index, checkpoints,
    /// finds the index's root page, then removes its last index-entry cell so a row is
    /// present in the table but missing from the index.
    /// </summary>
    private static void GenerateMissingIndexEntry(string path, string setupSql, string indexName)
    {
        Build(path, connection => ExecuteScript(connection, setupSql));

        var bytes = File.ReadAllBytes(path);
        var pageSize = SqliteFixtureBytePatcher.ReadDbHeaderPageSize(bytes);
        var rootPage = SqliteFixtureBytePatcher.FindObjectRootPage(bytes, pageSize, indexName);
        SqliteFixtureBytePatcher.DropLastCellFromBtreePage(bytes, pageSize, rootPage);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Mirrors <c>generate_check_constraint_violation_fixture</c>.</summary>
    private static void GenerateCheckConstraintViolation(string path)
    {
        Build(
            path,
            connection => ExecuteScript(
                connection,
                """
                PRAGMA page_size=4096;
                CREATE TABLE t(id INT PRIMARY KEY, b INTEGER CHECK(b > 0));
                INSERT INTO t VALUES(1, 2);
                """));

        var bytes = File.ReadAllBytes(path);
        var pageSize = SqliteFixtureBytePatcher.ReadDbHeaderPageSize(bytes);
        var rootPage = SqliteFixtureBytePatcher.FindObjectRootPage(bytes, pageSize, "t");
        SqliteFixtureBytePatcher.SetSecondTableColumnI8InFirstRow(bytes, pageSize, rootPage, -1);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Mirrors <c>generate_not_null_violation_fixture</c>.</summary>
    private static void GenerateNotNullViolation(string path)
    {
        Build(
            path,
            connection => ExecuteScript(
                connection,
                """
                PRAGMA page_size=4096;
                CREATE TABLE t(id INT PRIMARY KEY, b TEXT NOT NULL);
                INSERT INTO t VALUES(1, '');
                """));

        var bytes = File.ReadAllBytes(path);
        var pageSize = SqliteFixtureBytePatcher.ReadDbHeaderPageSize(bytes);
        var rootPage = SqliteFixtureBytePatcher.FindObjectRootPage(bytes, pageSize, "t");
        SqliteFixtureBytePatcher.SetSecondTableColumnToNullInFirstRow(bytes, pageSize, rootPage);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>
    /// Mirrors <c>generate_gencol_not_null_violation_fixture</c>: a virtual generated NOT
    /// NULL column evaluates to NULL because its base column is patched to NULL after insert.
    /// Ahtola does not require Turso's <c>experimental_generated_columns</c> opt-in.
    /// </summary>
    private static void GenerateGencolNotNullViolation(string path)
    {
        Build(
            path,
            connection => ExecuteScript(
                connection,
                """
                PRAGMA page_size=4096;
                CREATE TABLE t(a INTEGER, b GENERATED ALWAYS AS (a*2) VIRTUAL NOT NULL);
                INSERT INTO t(a) VALUES(0);
                """));

        var bytes = File.ReadAllBytes(path);
        var pageSize = SqliteFixtureBytePatcher.ReadDbHeaderPageSize(bytes);
        var rootPage = SqliteFixtureBytePatcher.FindObjectRootPage(bytes, pageSize, "t");
        SqliteFixtureBytePatcher.SetFirstTableColumnToNullInFirstRow(bytes, pageSize, rootPage);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Mirrors <c>generate_non_unique_index_entry_fixture</c>.</summary>
    private static void GenerateNonUniqueIndexEntry(string path)
    {
        Build(
            path,
            connection => ExecuteScript(
                connection,
                """
                PRAGMA page_size=4096;
                CREATE TABLE t(a INTEGER PRIMARY KEY, b INTEGER);
                CREATE UNIQUE INDEX idx_u ON t(b);
                INSERT INTO t VALUES (1,10),(2,20),(3,30);
                """));

        var bytes = File.ReadAllBytes(path);
        var pageSize = SqliteFixtureBytePatcher.ReadDbHeaderPageSize(bytes);
        var rootPage = SqliteFixtureBytePatcher.FindObjectRootPage(bytes, pageSize, "idx_u");
        // Collapse the b=30 key onto the existing b=20 key: the row for b=30 no longer has
        // a matching index entry of its own, and b=20 now has a duplicate.
        SqliteFixtureBytePatcher.ChangeFirstIndexKeyI8(bytes, pageSize, rootPage, from: 30, to: 20);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Mirrors <c>generate_missing_unique_index_entry_fixture</c>.</summary>
    private static void GenerateMissingUniqueIndexEntry(string path)
    {
        Build(
            path,
            connection => ExecuteScript(
                connection,
                """
                PRAGMA page_size=4096;
                CREATE TABLE t(a INTEGER PRIMARY KEY, b INTEGER);
                CREATE UNIQUE INDEX idx_u_missing ON t(b);
                INSERT INTO t VALUES (1,10),(2,20),(3,30);
                """));

        var bytes = File.ReadAllBytes(path);
        var pageSize = SqliteFixtureBytePatcher.ReadDbHeaderPageSize(bytes);
        var rootPage = SqliteFixtureBytePatcher.FindObjectRootPage(bytes, pageSize, "idx_u_missing");
        // Retarget the b=20 key to 25: no row has b=25, so the index is left with exactly one
        // fewer entry than the table, and no duplicate key is introduced.
        SqliteFixtureBytePatcher.ChangeFirstIndexKeyI8(bytes, pageSize, rootPage, from: 20, to: 25);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Mirrors <c>generate_freelist_count_mismatch_fixture</c>.</summary>
    private static void GenerateFreelistCountMismatch(string path)
    {
        BuildFreelistFixture(path);
        var bytes = File.ReadAllBytes(path);
        var (_, headerCount) = SqliteFixtureBytePatcher.ReadDbHeaderFreelistFields(bytes);
        SqliteFixtureBytePatcher.SetDbHeaderFreelistCount(bytes, checked(headerCount + 1));
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Mirrors <c>generate_freelist_trunk_corrupt_fixture</c>.</summary>
    private static void GenerateFreelistTrunkCorrupt(string path)
    {
        BuildFreelistFixture(path);
        var bytes = File.ReadAllBytes(path);
        var pageSize = SqliteFixtureBytePatcher.ReadDbHeaderPageSize(bytes);
        var (trunkPage, _) = SqliteFixtureBytePatcher.ReadDbHeaderFreelistFields(bytes);
        SqliteFixtureBytePatcher.SetFreelistTrunkLeafCount(bytes, pageSize, trunkPage);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>
    /// Common freelist setup shared by the two freelist fixtures: mirrors
    /// <c>generate_freelist_fixture_base</c> — insert enough large rows to allocate pages,
    /// then drop the table so those pages become the freelist. Validates (from the raw
    /// header bytes, not a PRAGMA) that pages were actually freed before returning.
    /// </summary>
    private static void BuildFreelistFixture(string path)
    {
        Build(
            path,
            connection =>
            {
                ExecuteScript(
                    connection,
                    """
                    PRAGMA page_size=4096;
                    CREATE TABLE t(a INTEGER PRIMARY KEY, b BLOB);
                    """);
                using var insert = connection.Prepare("INSERT INTO t VALUES (?1, zeroblob(3500))");
                for (var i = 1; i <= 200; i++)
                {
                    insert.Bind(1, SqlValue.Integer(i));
                    _ = insert.Step();
                    insert.Reset();
                }

                ExecuteScript(connection, "DROP TABLE t;");
            });

        var bytes = File.ReadAllBytes(path);
        var (headerTrunkPage, headerCount) = SqliteFixtureBytePatcher.ReadDbHeaderFreelistFields(bytes);
        if (headerTrunkPage <= 0)
            throw new InvalidOperationException($"fixture '{path}' did not create a freelist trunk page");
        if (headerCount <= 0)
            throw new InvalidOperationException($"fixture '{path}' has zero freelist count in database header");
    }

    /// <summary>Mirrors <c>generate_overflow_list_length_mismatch_fixture</c>.</summary>
    private static void GenerateOverflowListLengthMismatch(string path)
    {
        Build(
            path,
            connection => ExecuteScript(
                connection,
                """
                PRAGMA page_size=4096;
                CREATE TABLE t(a INTEGER PRIMARY KEY, b BLOB);
                INSERT INTO t VALUES (1, zeroblob(30000));
                """));

        var bytes = File.ReadAllBytes(path);
        var pageSize = SqliteFixtureBytePatcher.ReadDbHeaderPageSize(bytes);
        var rootPage = SqliteFixtureBytePatcher.FindObjectRootPage(bytes, pageSize, "t");
        SqliteFixtureBytePatcher.TruncateFirstOverflowChainForFirstTableRow(bytes, pageSize, rootPage);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>
    /// Opens a fresh file-backed database at <paramref name="path"/>, runs
    /// <paramref name="populate"/> against it, checkpoints so every page lands in the main
    /// file (WAL truncated to empty — no stale frames can shadow the subsequent direct byte
    /// patch), and closes it. The WAL/SHM sidecars are deliberately left in place: a missing
    /// SHM makes Ahtola's later read-only open fail (it will not synthesize one), and an
    /// empty (post-TRUNCATE) WAL carries no page data to go stale.
    /// </summary>
    private static void Build(string path, Action<EmbeddedConnection> populate)
    {
        using var database = EmbeddedDatabase.OpenFile(path);
        using var connection = database.Connect();
        populate(connection);
        Checkpoint(connection);
    }

    private static void Checkpoint(EmbeddedConnection connection)
    {
        using var statement = connection.Prepare("PRAGMA wal_checkpoint(TRUNCATE)");
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    private static void ExecuteScript(EmbeddedConnection connection, string sql)
    {
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
            {
                while (statement.Step() == StatementStepResult.Row)
                {
                }
            }
        }
    }
}
