using AwesomeAssertions;
using Ahtola.Tests.Sqltest;

namespace Ahtola.Tests;

[Category("CoverageExcluded")]
public class SqltestHarnessTests
{
    [Test]
    public void ParserRetainsCrossCheckIntegrityAsTypedMetadata()
    {
        var file = Parse(
            """
            @database :memory:

            @cross-check-integrity
            test checked {
                SELECT 1
            }
            expect {
                1
            }

            test unchecked {
                SELECT 2
            }
            expect {
                2
            }
            """);

        file.Tests.Single(test => test.Name == "checked").CrossCheckIntegrity.Should().BeTrue();
        file.Tests.Single(test => test.Name == "unchecked").CrossCheckIntegrity.Should().BeFalse();
    }

    [Test]
    public void DefaultFixtureDataMatchesTursoSeedAndIsDeterministic()
    {
        var first = SqltestDefaultDatabaseGenerator.GenerateData(
            SqltestDefaultDatabaseGenerator.DefaultUserCount);
        var second = SqltestDefaultDatabaseGenerator.GenerateData(
            SqltestDefaultDatabaseGenerator.DefaultUserCount);

        first.Should().BeEquivalentTo(second, options => options.WithStrictOrdering());
        first.Users.Should().HaveCount(10_000);
        first.Products.Should().HaveCount(11);
        first.Users[0].Should().Be(
            new SqltestGeneratedUser(
                "Dan",
                "Parker",
                "caden@example.org",
                "436.726.1331 x867",
                "Davonte Mountains",
                "Hodkiewicz side",
                "NJ",
                "758",
                31));
        first.Users[1].Should().Be(
            new SqltestGeneratedUser(
                "Chaz",
                "Zemlak",
                "kip@example.com",
                "(242) 091-6326 x60303",
                "Dibbert Street",
                "Noe mouth",
                "ND",
                "656",
                10));
        first.Users.Sum(static user => user.Age).Should().Be(504_576);
        first.Products[0].Name.Should().Be("hat");
        first.Products[0].Price.Should().BeApproximately(82.9389679823547, 1e-13);
        first.Products[1].Name.Should().Be("cap");
        first.Products[1].Price.Should().BeApproximately(32.2475410444338, 1e-13);
    }

    [Test]
    public void DefaultDatabaseVariantsHaveEquivalentShapeAndDistinctPrimaryKeyDeclarations()
    {
        var defaultPath = SqltestDefaultDatabaseGenerator.GetDefaultPath(noRowidAlias: false);
        var noAliasPath = SqltestDefaultDatabaseGenerator.GetDefaultPath(noRowidAlias: true);

        Query(defaultPath, "SELECT count(*) FROM users").Should().Equal("10000");
        Query(noAliasPath, "SELECT count(*) FROM users").Should().Equal("10000");
        Query(defaultPath, "SELECT count(*) FROM products").Should().Equal("11");
        Query(noAliasPath, "SELECT count(*) FROM products").Should().Equal("11");
        Query(defaultPath, "SELECT sql FROM sqlite_schema WHERE name = 'users'").Single()
            .Should().Contain("id INTEGER PRIMARY KEY");
        Query(noAliasPath, "SELECT sql FROM sqlite_schema WHERE name = 'users'").Single()
            .Should().Contain("id INT PRIMARY KEY").And.NotContain("id INTEGER PRIMARY KEY");
    }

    [Test]
    public void IntegrityCheckRequiresExactlyOneOkRowAndReportsCaseContext()
    {
        var file = Parse(
            """
            @database :memory:

            @cross-check-integrity
            test corrupt {
                SELECT 1
            }
            expect {
                1
            }
            """,
            "integrity/focused.sqltest");

        var outcome = SqltestManagedRunner.Run(
            file,
            file.Tests.Single(),
            static (_, _) => new SqltestIntegrityResult(["row 2 missing", "ok"], null));

        outcome.Matched.Should().BeFalse();
        outcome.Detail.Should()
            .Contain("integrity_check failed for integrity/focused.sqltest::corrupt")
            .And.Contain("expected exactly one row 'ok'")
            .And.Contain("2 row(s)");
    }

    [Test]
    public void IntegrityCheckSkipsExpectedErrorsAndReadOnlyFixturesLikeTurso()
    {
        var expectedError = Parse(
            """
            @database :memory:

            @cross-check-integrity
            test expected-error {
                SELECT missing_column
            }
            expect error {
            }
            """);
        var readOnly = Parse(
            """
            @database :default:

            @cross-check-integrity
            test readonly {
                SELECT 1
            }
            expect {
                1
            }
            """);
        var checks = 0;
        SqltestIntegrityCheck integrityCheck = (_, _) =>
        {
            checks++;
            return new SqltestIntegrityResult(["not ok"], null);
        };

        SqltestManagedRunner.Run(expectedError, expectedError.Tests.Single(), integrityCheck)
            .Matched.Should().BeTrue();
        SqltestManagedRunner.Run(readOnly, readOnly.Tests.Single(), integrityCheck)
            .Matched.Should().BeTrue();
        checks.Should().Be(0);
    }

    [Test]
    public void MultipleDatabaseDeclarationsRunAsIndependentVariants()
    {
        var file = Parse(
            """
            @database :memory:
            @database :temp:

            @cross-check-integrity
            test variants {
                SELECT 1
            }
            expect {
                1
            }
            """);
        var checks = 0;

        var outcome = SqltestManagedRunner.Run(
            file,
            file.Tests.Single(),
            (_, _) =>
            {
                checks++;
                return new SqltestIntegrityResult(["ok"], null);
            });

        outcome.Matched.Should().BeTrue();
        checks.Should().Be(2);
        SqltestCorpus.Classify(file, file.Tests.Single()).Status.Should().Be(SqltestCaseStatus.Runnable);
    }

    [Test]
    public void GeneratedDefaultsAreRunnableButUngeneratedPathsStayExplicitlyUnsupported()
    {
        var defaults = Parse(
            """
            @database :default:
            @database :default-no-rowid-alias:
            test variants { SELECT 1 }
            expect { 1 }
            """);
        var path = Parse(
            """
            @database database/custom.db readonly
            test path { SELECT 1 }
            expect { 1 }
            """);

        SqltestCorpus.Classify(defaults, defaults.Tests.Single()).Status
            .Should().Be(SqltestCaseStatus.Runnable);
        var pathClassification = SqltestCorpus.Classify(path, path.Tests.Single());
        pathClassification.Status.Should().Be(SqltestCaseStatus.UnsupportedHarness);
        pathClassification.Reason.Should().Contain("no equivalent managed generator");
    }

    [Test]
    public void UnboundedPlannerGapIsReportedAsAnExplicitHarnessLimitation()
    {
        var file = Parse(
            """
            @database :default:
            test four-way-inner-join { SELECT 1 }
            expect { 1 }
            """,
            "join/default.sqltest");

        var classification = SqltestCorpus.Classify(file, file.Tests.Single());

        classification.Status.Should().Be(SqltestCaseStatus.UnsupportedHarness);
        classification.Reason.Should().Contain("cannot be bounded in-process");
    }

    private static SqltestFile Parse(string source, string relativePath = "focused.sqltest")
        => SqltestParser.Parse(relativePath, source);

    private static IReadOnlyList<string> Query(string path, string sql)
    {
        using var database = Ahtola.Core.EmbeddedDatabase.OpenFile(path, readOnly: true);
        using var connection = database.Connect();
        using var statement = connection.Prepare(sql);
        var rows = new List<string>();
        while (statement.Step() == Ahtola.Core.StatementStepResult.Row)
        {
            var value = statement.GetValue(0);
            rows.Add(value.Kind switch
            {
                Ahtola.Core.SqlValueKind.Integer => value.AsInteger().ToString(),
                Ahtola.Core.SqlValueKind.Text => value.AsText(),
                _ => value.ToString() ?? string.Empty,
            });
        }
        return rows;
    }
}
