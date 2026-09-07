using System.Globalization;
using Ahtola.Core;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Backwards compatibility for a database written by an older Turso/Ahtola build: before view
/// column lists were re-serialized with proper identifier quoting, <c>CREATE VIEW v([col one])
/// AS SELECT a FROM t</c> stored <c>CREATE VIEW v (col one) AS SELECT a FROM t</c> (unquoted) in
/// sqlite_schema. Real SQLite refuses to open such a database at all ("malformed database
/// schema"), which is why this is a Turso/Ahtola-only fixture; Turso keeps tolerating the row so
/// the database stays usable, the rest of the schema works, and only the broken view itself is
/// unavailable.
/// </summary>
/// <remarks>
/// Mirrors the three cases in
/// <c>conformance/sqlite-sqltests/turso/legacy-unquoted-view-columns.sqltest</c> (vendored
/// unchanged from <c>turso-src/sqlite/conformance/turso-sqltests/</c>), exercised directly here
/// instead of through the sqltest corpus harness: the harness has no <c>@database &lt;path&gt;
/// readonly</c> fixture support yet (<see cref="Ahtola.Tests.Sqltest.SqltestManagedRunner"/>
/// throws <see cref="NotSupportedException"/> for <c>SqltestDatabaseKind.Path</c>), so that file
/// is discovered by <c>SqltestCorpus</c> as an unsupported-harness case rather than exercised.
/// This test opens a private copy of the byte-identical fixture directly so the regression does
/// not depend on that workstream landing.
/// </remarks>
public sealed class LegacyUnquotedViewColumnsFixtureTests
{
    private static string FixturePath => Path.Combine(
        TestContext.CurrentContext.TestDirectory,
        "Fixtures",
        "testing_legacy_unquoted_view_columns.db");

    // A foreign read-only open skips ownership acquisition and the shared-memory lock
    // coordinator entirely, matching the sqltest corpus's `@database <path> readonly` contract
    // for a byte-identical, vendored static fixture that this process never writes to.
    private static EmbeddedDatabase OpenFixture()
        => EmbeddedDatabase.OpenFile(FixturePath, readOnly: true, foreignReadOnly: true);

    [Test]
    public void TheOrdinaryTableRemainsReadableDespiteTheBrokenViewRow()
    {
        using var database = OpenFixture();
        using var connection = database.Connect();

        ReadRows(connection, "SELECT a FROM t;").Should().Equal("42");
    }

    [Test]
    public void TheBrokenViewIsUnavailableWithADiagnosticInsteadOfBlockingTheWholeOpen()
    {
        using var database = OpenFixture();
        using var connection = database.Connect();

        Action query = () => ReadRows(connection, "SELECT * FROM v;");

        query.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*could not be loaded*");
    }

    [Test]
    public void TheBrokenViewRowIsStillListedInTheSchema()
    {
        using var database = OpenFixture();
        using var connection = database.Connect();

        ReadRows(connection, "SELECT name, type FROM sqlite_master ORDER BY name;")
            .Should()
            .Equal("t|table", "v|view");
    }

    // ---------------------------------------------------------------- helpers

    private static string[] ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<string>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var values = new List<string>();
            for (var column = 0; column < statement.ColumnCount; column++)
            {
                var value = statement.GetValue(column);
                values.Add(value.Kind switch
                {
                    SqlValueKind.Null => string.Empty,
                    SqlValueKind.Integer => value.AsInteger().ToString(CultureInfo.InvariantCulture),
                    SqlValueKind.Real => value.AsReal().ToString(CultureInfo.InvariantCulture),
                    _ => value.AsText(),
                });
            }

            rows.Add(string.Join("|", values));
        }

        return [.. rows];
    }
}
