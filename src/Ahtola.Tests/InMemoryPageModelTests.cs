using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

/// <summary>
/// Covers the managed in-memory page model that backs <c>PRAGMA page_count</c>,
/// <c>PRAGMA freelist_count</c>, and <c>PRAGMA max_page_count</c> on <see cref="EmbeddedDatabase"/>
/// databases that have no file store. The model mirrors SQLite's pager: the page count is
/// a high-water mark that only grows, dropped b-tree roots move onto the freelist instead
/// of shrinking the database, new b-trees consume freelist pages first, and the first
/// committed mutation materializes the header page (moving the count off zero and locking
/// <c>PRAGMA page_size</c>).
/// </summary>
public sealed class InMemoryPageModelTests
{
    [Test]
    public void FirstMutationMaterializesTheHeaderPage()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Scalar(connection, "PRAGMA page_count").Should().Be(0L);

        Execute(connection, "CREATE TABLE t(a)");
        // SQLite writes the header page plus the table root at that commit.
        Scalar(connection, "PRAGMA page_count").Should().Be(2L);
    }

    [Test]
    public void TablesAndIndexesEachContributeAPage()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(a)");
        Execute(connection, "CREATE TABLE t2(b)");
        Scalar(connection, "PRAGMA page_count").Should().Be(3L);
        Execute(connection, "CREATE INDEX ix ON t(a)");
        Scalar(connection, "PRAGMA page_count").Should().Be(4L);
    }

    [Test]
    public void HeaderOnlyWriteMaterializesTheHeaderPage()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "PRAGMA user_version=5");
        Scalar(connection, "PRAGMA page_count").Should().Be(1L);
    }

    [Test]
    public void DropMovesPagesOntoTheFreelistWithoutShrinking()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(a)");
        Execute(connection, "CREATE TABLE t2(b)");
        Scalar(connection, "PRAGMA page_count").Should().Be(3L);

        Execute(connection, "DROP TABLE t2");
        // SQLite's header page count only grows; the freed root goes to the freelist.
        Scalar(connection, "PRAGMA page_count").Should().Be(3L);
        Scalar(connection, "PRAGMA freelist_count").Should().Be(1L);
    }

    [Test]
    public void NewTreesConsumeFreelistPagesBeforeGrowing()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(a)");
        Execute(connection, "CREATE TABLE t2(b)");
        Execute(connection, "DROP TABLE t2");

        Execute(connection, "CREATE TABLE t3(c)");
        // The new root reuses the freed page: the count stays at the high-water mark
        // and the freelist drains back to zero.
        Scalar(connection, "PRAGMA page_count").Should().Be(3L);
        Scalar(connection, "PRAGMA freelist_count").Should().Be(0L);
    }

    [Test]
    public void MaxPageCountRejectsGrowthBeyondTheLimit()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "PRAGMA max_page_count=1");
        Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection, "CREATE TABLE t(a)"));

        using var connection2 = new EmbeddedDatabase().Connect();
        Execute(connection2, "PRAGMA max_page_count=2");
        Execute(connection2, "CREATE TABLE t(a)");
        // Header + one root fit the limit; the second table does not.
        Assert.Throws<EmbeddedSqlException>(
            () => Execute(connection2, "CREATE TABLE t2(b)"));
    }

    [Test]
    public void FreelistPagesSatisfyNewTreesWithinTheLimit()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "PRAGMA max_page_count=3");
        Execute(connection, "CREATE TABLE t(a)");
        Execute(connection, "CREATE TABLE t2(b)");
        Execute(connection, "DROP TABLE t2");
        // The freelist page satisfies the new root without growing the database.
        Execute(connection, "CREATE TABLE t3(c)");
        Scalar(connection, "PRAGMA page_count").Should().Be(3L);
    }

    [Test]
    public void PageSizeChangeIsRejectedOnceMaterialized()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "PRAGMA page_size=8192");
        Scalar(connection, "PRAGMA page_size").Should().Be(8192L);

        // After the first mutation SQLite refuses further page_size changes
        // (the request silently keeps the current size).
        Execute(connection, "CREATE TABLE t(a)");
        Execute(connection, "PRAGMA page_size=4096");
        Scalar(connection, "PRAGMA page_size").Should().Be(8192L);
    }

    private static long Scalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
            return statement.GetValue(0).AsInteger();
        throw new InvalidOperationException($"Statement {sql} produced no row.");
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }
}
