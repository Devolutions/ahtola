using System.Data;
using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// Pins how the disconnected ADO.NET surface interacts with the authorizer and trace handler.
/// <para>
/// The distinction these tests protect is deliberate and easy to erase by refactoring.
/// <see cref="SqliteConnection.GetSchema(string)"/> reads the catalog on the caller's behalf and
/// returns the stored DDL of every table, so it must stay subject to the authorizer: a
/// deny-by-default policy exists to sandbox a connection, and a schema call that bypassed it
/// would disclose every table and column name the policy was installed to hide. The reader's
/// column-metadata probes are the opposite case -- they describe a result set the caller has
/// already been authorized to read -- so they stay exempt, which is what
/// <c>SqliteConnection.SuspendHooks</c> is for. Routing <c>GetSchema</c> through that same
/// suspension would be a disclosure hole rather than a consistency fix.
/// </para>
/// </summary>
public sealed class ManagedSchemaAndAdapterAuthorizerTests
{
    private const int SqliteAuth = 23;

    [Test]
    public void GetSchemaStaysSubjectToTheAuthorizer()
    {
        using var connection = Open();
        connection.SetAuthorizer(_ => SqliteAuthorizerResult.Deny);

        foreach (var collection in new[] { "Tables", "Columns", "Indexes", "IndexColumns" })
        {
            var error = Assert.Throws<SqliteException>(() => connection.GetSchema(collection));
            error!.SqliteErrorCode.Should().Be(SqliteAuth, $"{collection} reads the catalog");
        }
    }

    [Test]
    public void TheStaticSchemaCollectionsDoNotConsultTheAuthorizer()
    {
        using var connection = Open();
        connection.SetAuthorizer(_ => SqliteAuthorizerResult.Deny);

        // These are constant tables. They describe the provider, not the database, so denying
        // them would report a policy violation for work that never touched a row.
        connection.GetSchema("MetaDataCollections").Rows.Count.Should().Be(6);
        connection.GetSchema("ReservedWords").Rows.Count.Should().BeGreaterThan(0);
    }

    [Test]
    public void ReaderColumnMetadataProbesStayExemptFromTheAuthorizer()
    {
        using var connection = Open();
        connection.SetAuthorizer(DenyCatalogReads);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name FROM person ORDER BY id";
        using var reader = command.ExecuteReader();

        var schema = reader.GetSchemaTable();
        schema.Should().NotBeNull();
        schema!.Rows.Count.Should().Be(2);
    }

    [Test]
    public void AdapterFillWorksWhileTheCatalogIsDeniedToTheApplication()
    {
        using var connection = Open();
        connection.SetAuthorizer(DenyCatalogReads);

        using var adapter = new AhtolaDataAdapter("SELECT id, name FROM person ORDER BY id", connection);
        var dataSet = new DataSet();

        // Fill maps the result schema before fetching rows. That mapping must not be charged to
        // the application's policy, or every adapter would break under a sandboxed connection.
        adapter.Fill(dataSet, "person").Should().Be(3);
        dataSet.Tables["person"]!.Columns.Cast<DataColumn>().Select(column => column.DataType)
            .Should().Equal(typeof(long), typeof(string));
    }

    [Test]
    public void ADeniedUpdateFailsLoudlyAndLeavesTheRowPending()
    {
        using var connection = Open();
        using var adapter = new AhtolaDataAdapter("SELECT id, name FROM person ORDER BY id", connection);
        using var builder = new AhtolaCommandBuilder(adapter);

        var dataSet = new DataSet();
        adapter.Fill(dataSet, "person");
        var table = dataSet.Tables["person"]!;
        table.Rows[0]["name"] = "ada lovelace";

        connection.SetAuthorizer(context =>
            context.Action == SqliteAuthorizerAction.Update ? SqliteAuthorizerResult.Deny : SqliteAuthorizerResult.Ok);

        var error = Assert.Throws<SqliteException>(() => adapter.Update(dataSet, "person"));
        error!.SqliteErrorCode.Should().Be(SqliteAuth);

        // The caller has to be able to retry, so the edit must survive the failure.
        table.GetChanges().Should().NotBeNull();
        connection.SetAuthorizer(null);
        ReadScalar(connection, "SELECT name FROM person WHERE id = 1").Should().Be("ada");
    }

    /// <summary>
    /// Documents the one case where a round trip reports success without persisting the row, so
    /// that it is an asserted limit rather than a lurking surprise. See the "Disconnected ADO.NET"
    /// section of <c>README.md</c>.
    /// </summary>
    [Test]
    public void AnIgnoredUpdateIsAcceptedByTheDataSetWithoutReachingTheDatabase()
    {
        using var connection = Open();
        using var adapter = new AhtolaDataAdapter("SELECT id, name FROM person ORDER BY id", connection);
        using var builder = new AhtolaCommandBuilder(adapter);

        var dataSet = new DataSet();
        adapter.Fill(dataSet, "person");
        var table = dataSet.Tables["person"]!;
        table.Rows[0]["name"] = "ada lovelace";

        connection.SetAuthorizer(context =>
            context.Action == SqliteAuthorizerAction.Update ? SqliteAuthorizerResult.Ignore : SqliteAuthorizerResult.Ok);

        // Ignore neutralizes the column assignment, but the statement still matches the row, so
        // the engine reports one affected row exactly as it does for an UPDATE that writes the
        // value it already held. A plain ExecuteNonQuery reports the same 1, so the adapter has
        // nothing to distinguish: it is believing an accurate matched-row count, not inventing
        // one. Anything better has to come from the engine reporting neutralized writes.
        adapter.Update(dataSet, "person").Should().Be(1);
        table.GetChanges().Should().BeNull();

        connection.SetAuthorizer(null);
        ReadScalar(connection, "SELECT name FROM person WHERE id = 1").Should().Be("ada");
    }

    [Test]
    public void AnIgnoredUpdateReportsTheSameRowCountThroughAPlainCommand()
    {
        // The companion to the test above: proves the row count comes from the engine's
        // authorizer handling rather than from the adapter or the command builder.
        using var connection = Open();
        connection.SetAuthorizer(context =>
            context.Action == SqliteAuthorizerAction.Update ? SqliteAuthorizerResult.Ignore : SqliteAuthorizerResult.Ok);

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE person SET name = 'ada lovelace' WHERE id = 1";
        command.ExecuteNonQuery().Should().Be(1);

        connection.SetAuthorizer(null);
        ReadScalar(connection, "SELECT name FROM person WHERE id = 1").Should().Be("ada");
    }

    [Test]
    public void GetSchemaReportsItsCatalogQueriesToTheTraceHandler()
    {
        using var connection = Open();
        var traced = new List<string>();
        connection.SetTraceHandler(traced.Add);

        connection.GetSchema("Columns");

        // Unlike the reader's decltype-equivalent probes, these statements are work the caller
        // explicitly asked for, so hiding them would under-report the connection's activity.
        traced.Should().Contain(sql => sql.Contains("sqlite_master", StringComparison.Ordinal));
        traced.Should().Contain(sql => sql.Contains("PRAGMA table_info", StringComparison.Ordinal));
    }

    private static SqliteAuthorizerResult DenyCatalogReads(SqliteAuthorizerContext context)
        => context.Argument0 is not null
           && context.Argument0.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)
            ? SqliteAuthorizerResult.Deny
            : SqliteAuthorizerResult.Ok;

    private static string? ReadScalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string;
    }

    private static SqliteConnection Open()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        foreach (var statement in new[]
                 {
                     "CREATE TABLE person(id INTEGER PRIMARY KEY, name TEXT NOT NULL)",
                     "INSERT INTO person(id, name) VALUES (1, 'ada'), (2, 'grace'), (3, 'alan')",
                 })
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }

        return connection;
    }
}
