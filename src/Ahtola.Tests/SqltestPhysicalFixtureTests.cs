using Ahtola.Tests.Sqltest;
using AwesomeAssertions;

namespace Ahtola.Tests;

[Category("CoverageExcluded")]
public class SqltestPhysicalFixtureTests
{
    [TestCase("integrity_check/parity_corrupt_index.sqltest", "Stored index 'idx_b'")]
    [TestCase("integrity_check/parity_corrupt_expression_index.sqltest", "Stored index 'idx_expr'")]
    [TestCase("integrity_check/parity_corrupt_partial_index.sqltest", "Stored index 'idx_partial'")]
    [TestCase("integrity_check/parity_non_unique_index.sqltest", "duplicate non-NULL keys")]
    [TestCase("integrity_check/parity_missing_unique_index.sqltest", "does not match table")]
    [TestCase("integrity_check/parity_freelist_count_mismatch.sqltest", "freelist header declares")]
    [TestCase("integrity_check/parity_freelist_trunk_corrupt.sqltest", "exceeding capacity")]
    [TestCase("integrity_check/parity_overflow_list_length_mismatch.sqltest", "overflow chain ends")]
    [TestCase("integrity_check/parity_gencol_not_null_violation.sqltest", "NOT NULL constraint failed")]
    [TestCase("turso/legacy-unquoted-view-columns.sqltest", "Expected RightParen")]
    public void FixtureConstructionSucceedsBeforeExpectedEngineRejection(
        string relativePath, string diagnostic)
    {
        var discovered = SqltestCorpus.Cases.First(candidate => candidate.RelativePath == relativePath);
        discovered.Status.Should().Be(SqltestCaseStatus.Runnable);
        var file = SqltestCorpus.LoadFile(relativePath, discovered.FullPath);
        var fixture = file.Databases.Single();
        fixture.Path.Should().NotBeNullOrEmpty();

        var path = SqltestPhysicalFixtures.Materialize(fixture.Path!);
        File.Exists(path).Should().BeTrue();

        var test = file.Tests.Single(candidate => candidate.Name == discovered.TestName);
        var outcome = SqltestManagedRunner.Run(file, test);
        outcome.Matched.Should().BeFalse();
        outcome.Detail.Should().Contain("database failed to open:").And.Contain(diagnostic);
    }
}
