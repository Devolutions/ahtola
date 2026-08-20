using Ahtola.Core;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Direct unit tests for <see cref="ManagedReplicaSchemaDdlText"/>'s quote/comment-aware
/// scanning, in particular that quoted identifiers are recognized as structural tokens rather
/// than being classified (and silently skipped) as trivia alongside whitespace and comments.
/// </summary>
public sealed class ManagedReplicaSchemaDdlTextTests
{
    [Test]
    public void SplitCreateTableColumnsRecognizesADoubleQuotedTableAndColumnName()
    {
        var columns = ManagedReplicaSchemaDdlText.SplitCreateTableColumns(
            "CREATE TABLE \"my table\" (\"my col\" TEXT, y INTEGER)");

        columns.Should().NotBeNull();
        columns!.Select(c => c.Name).Should().Equal("my col", "y");
    }

    [Test]
    public void SplitCreateTableColumnsRecognizesABacktickQuotedColumnName()
    {
        var columns = ManagedReplicaSchemaDdlText.SplitCreateTableColumns(
            "CREATE TABLE t(`weird name` TEXT, y INTEGER)");

        columns.Should().NotBeNull();
        columns!.Select(c => c.Name).Should().Equal("weird name", "y");
    }

    [Test]
    public void SplitCreateTableColumnsRecognizesABracketQuotedColumnName()
    {
        var columns = ManagedReplicaSchemaDdlText.SplitCreateTableColumns(
            "CREATE TABLE t([bracket name] TEXT, y INTEGER)");

        columns.Should().NotBeNull();
        columns!.Select(c => c.Name).Should().Equal("bracket name", "y");
    }

    [Test]
    public void SplitCreateTableColumnsHandlesADoubledDoubleQuoteEscape()
    {
        var columns = ManagedReplicaSchemaDdlText.SplitCreateTableColumns(
            "CREATE TABLE t(\"a\"\"b\" TEXT)");

        columns.Should().NotBeNull();
        columns![0].Name.Should().Be("a\"b");
    }

    [Test]
    public void SplitCreateTableColumnsHandlesADoubledBacktickEscape()
    {
        var columns = ManagedReplicaSchemaDdlText.SplitCreateTableColumns(
            "CREATE TABLE t(`a``b` TEXT)");

        columns.Should().NotBeNull();
        columns![0].Name.Should().Be("a`b");
    }

    [Test]
    public void SplitCreateTableColumnsIgnoresParensAndCommasInsideAQuotedDefaultStringLiteral()
    {
        // A string literal default value containing '(' ',' ')' must not be misread as
        // additional column-list structure.
        var columns = ManagedReplicaSchemaDdlText.SplitCreateTableColumns(
            "CREATE TABLE t(x TEXT DEFAULT 'a(b,c)d', y INTEGER)");

        columns.Should().NotBeNull();
        columns!.Select(c => c.Name).Should().Equal("x", "y");
    }

    [Test]
    public void SplitCreateTableColumnsSkipsCommentsAndWhitespaceButNotQuotedTokens()
    {
        var columns = ManagedReplicaSchemaDdlText.SplitCreateTableColumns(
            "CREATE TABLE t(\n  -- a comment\n  \"quoted col\" TEXT, /* inline */ y INTEGER\n)");

        columns.Should().NotBeNull();
        columns!.Select(c => c.Name).Should().Equal("quoted col", "y");
    }

    [Test]
    public void TryReadIdentifierReadsAQuotedIdentifierEvenAfterLeadingWhitespace()
    {
        var ok = ManagedReplicaSchemaDdlText.TryReadIdentifier("   \"my col\" TEXT", 0, out var name, out var next);

        ok.Should().BeTrue();
        name.Should().Be("my col");
        "   \"my col\" TEXT"[next..].Should().Be(" TEXT");
    }

    [Test]
    public void TryParseAlterTableAddColumnRecognizesAQuotedTableAndColumnName()
    {
        var result = ManagedReplicaSchemaDdlText.TryParseAlterTableAddColumn(
            "ALTER TABLE \"my table\" ADD COLUMN \"new col\" TEXT");

        result.Should().NotBeNull();
        result!.Value.TableName.Should().Be("my table");
        result.Value.ColumnName.Should().Be("new col");
    }

    [Test]
    public void EnsureCreateTableIfNotExistsInsertsTheClauseWhenAbsent()
    {
        var rewritten = ManagedReplicaSchemaDdlText.EnsureCreateTableIfNotExists("CREATE TABLE t(x TEXT)");

        rewritten.Should().Be("CREATE TABLE IF NOT EXISTS t(x TEXT)");
    }

    [Test]
    public void EnsureCreateTableIfNotExistsLeavesAnExistingClauseUnchanged()
    {
        var sql = "CREATE TABLE IF NOT EXISTS t(x TEXT)";
        ManagedReplicaSchemaDdlText.EnsureCreateTableIfNotExists(sql).Should().Be(sql);
    }

    [Test]
    public void EnsureCreateTableIfNotExistsHandlesATemporaryTable()
    {
        var rewritten = ManagedReplicaSchemaDdlText.EnsureCreateTableIfNotExists("CREATE TEMP TABLE t(x TEXT)");

        rewritten.Should().Be("CREATE TEMP TABLE IF NOT EXISTS t(x TEXT)");
    }

    [Test]
    public void TryGetCreateTableShapeDetectsStrictAndWithoutRowid()
    {
        var shape = ManagedReplicaSchemaDdlText.TryGetCreateTableShape(
            "CREATE TABLE t(x TEXT PRIMARY KEY) STRICT, WITHOUT ROWID");

        shape.Should().NotBeNull();
        shape!.Value.Strict.Should().BeTrue();
        shape.Value.WithoutRowId.Should().BeTrue();
    }

    [Test]
    public void TryGetCreateTableShapeCapturesTableLevelConstraintsSeparatelyFromColumns()
    {
        var shape = ManagedReplicaSchemaDdlText.TryGetCreateTableShape(
            "CREATE TABLE t(x TEXT, y TEXT, PRIMARY KEY(x, y))");

        shape.Should().NotBeNull();
        shape!.Value.Columns.Select(c => c.Name).Should().Equal("x", "y");
        shape.Value.TableConstraints.Should().ContainSingle(c => c.Contains("PRIMARY KEY(x, y)"));
    }

    [Test]
    public void TryGetCreateTableShapeReturnsNullForACreateTableAsSelect()
    {
        ManagedReplicaSchemaDdlText.TryGetCreateTableShape("CREATE TABLE t AS SELECT 1").Should().BeNull();
    }

    [Test]
    public void IsColumnLevelPrimaryKeyDescendingDetectsTheBareForm()
    {
        ManagedReplicaSchemaDdlText.IsColumnLevelPrimaryKeyDescending("id INTEGER PRIMARY KEY DESC").Should().BeTrue();
    }

    [Test]
    public void IsColumnLevelPrimaryKeyDescendingIsFalseForAscOrUnspecified()
    {
        ManagedReplicaSchemaDdlText.IsColumnLevelPrimaryKeyDescending("id INTEGER PRIMARY KEY ASC").Should().BeFalse();
        ManagedReplicaSchemaDdlText.IsColumnLevelPrimaryKeyDescending("id INTEGER PRIMARY KEY").Should().BeFalse();
    }

    [Test]
    public void IsColumnLevelPrimaryKeyDescendingIsFalseWhenThereIsNoPrimaryKeyAtAll()
    {
        ManagedReplicaSchemaDdlText.IsColumnLevelPrimaryKeyDescending("id INTEGER").Should().BeFalse();
    }

    [Test]
    public void IsColumnLevelPrimaryKeyDescendingIgnoresDescInsideAQuotedDefaultValue()
    {
        ManagedReplicaSchemaDdlText.IsColumnLevelPrimaryKeyDescending(
            "id INTEGER PRIMARY KEY DEFAULT 'PRIMARY KEY DESC'").Should().BeFalse();
    }

    [Test]
    public void TryGetCreateTableShapeExposesTheColumnLevelDescConstraintOnItsOwnColumn()
    {
        var shape = ManagedReplicaSchemaDdlText.TryGetCreateTableShape(
            "CREATE TABLE t(id INTEGER PRIMARY KEY DESC, name TEXT)");

        shape.Should().NotBeNull();
        var idColumn = shape!.Value.Columns.Single(c => c.Name == "id");
        ManagedReplicaSchemaDdlText.IsColumnLevelPrimaryKeyDescending(idColumn.Definition).Should().BeTrue();
        shape.Value.TableConstraints.Should().BeEmpty();
    }
}
