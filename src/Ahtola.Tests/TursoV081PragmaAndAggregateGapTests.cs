using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

/// <summary>
/// v0.8.1 audit gaps in the SQL surface: the <c>stddev</c> aggregate (Turso
/// <c>core/percentile.rs</c>), the generic <c>pragma_*</c> table-valued functions (Turso
/// <c>core/pragma.rs</c> <c>PragmaVirtualTable</c>), <c>PRAGMA pragma_list</c>,
/// <c>mvcc_group_commit</c>, <c>fts_merge_threshold</c> and the <c>list_types</c> shape.
/// </summary>
public sealed class TursoV081PragmaAndAggregateGapTests
{
    [Test]
    public void StddevIsTheSampleStandardDeviationOfNumericValues()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(g TEXT, x)");
        Execute(connection, "INSERT INTO t VALUES ('a', 2), ('a', 4), ('a', '4'), ('a', 4.0), ('a', 5), ('a', 5), ('a', 7), ('a', 9), ('a', NULL), ('a', x'00'), ('b', 3)");

        // Text that parses as a number counts; NULL and BLOB are skipped, as in Turso.
        var stddev = Scalar(connection, "SELECT stddev(x) FROM t WHERE g = 'a'");
        stddev.Kind.Should().Be(SqlValueKind.Real);
        stddev.AsReal().Should().BeApproximately(Math.Sqrt(32d / 7d), 1e-12);

        // Fewer than two values gives NULL.
        Scalar(connection, "SELECT stddev(x) FROM t WHERE g = 'b'").Kind.Should().Be(SqlValueKind.Null);
        Scalar(connection, "SELECT stddev(x) FROM t WHERE 0").Kind.Should().Be(SqlValueKind.Null);

        var grouped = ReadRows(connection, "SELECT g, stddev(x) FROM t GROUP BY g ORDER BY g");
        grouped.Should().HaveCount(2);
        grouped[1][1].Kind.Should().Be(SqlValueKind.Null);

        var arity = () => Scalar(connection, "SELECT stddev(x, x) FROM t");
        arity.Should().Throw<EmbeddedSqlException>().WithMessage("*wrong number of arguments*stddev*");
    }

    [TestCase("application_id")]
    [TestCase("auto_vacuum")]
    [TestCase("busy_timeout")]
    [TestCase("count_changes")]
    [TestCase("database_list")]
    [TestCase("encoding")]
    [TestCase("foreign_keys")]
    [TestCase("freelist_count")]
    [TestCase("fts_merge_threshold")]
    [TestCase("ignore_check_constraints")]
    [TestCase("integrity_check")]
    [TestCase("list_types")]
    [TestCase("locking_mode")]
    [TestCase("max_page_count")]
    [TestCase("mvcc_checkpoint_threshold")]
    [TestCase("mvcc_gc_threshold")]
    [TestCase("page_count")]
    [TestCase("page_size")]
    [TestCase("query_only")]
    [TestCase("quick_check")]
    [TestCase("require_where")]
    [TestCase("schema_version")]
    [TestCase("synchronous")]
    [TestCase("temp_store")]
    [TestCase("user_version")]
    [TestCase("hexkey")]
    [TestCase("cipher")]
    [TestCase("capture_data_changes_conn")]
    public void PragmaFunctionMatchesTheStatementForm(string pragma)
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("pragma-function.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(value TEXT); INSERT INTO items VALUES ('a'), ('b');");
        Execute(connection, "PRAGMA user_version = 7; PRAGMA application_id = 11;");

        var statementRows = ReadRows(connection, $"PRAGMA {pragma}");
        var functionRows = ReadRows(connection, $"SELECT * FROM pragma_{pragma}");
        functionRows.Should().BeEquivalentTo(statementRows, options => options.WithStrictOrdering());
    }

    [Test]
    public void PragmaFunctionsReadTheCallingConnectionsSettings()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("pragma-connection.db", fileSystem);
        using var first = database.Connect();
        using var second = database.Connect();

        Execute(first, "PRAGMA foreign_keys = ON; PRAGMA fts_merge_threshold = 0; PRAGMA count_changes = 1;");
        Scalar(first, "SELECT foreign_keys FROM pragma_foreign_keys").Should().Be(SqlValue.Integer(1));
        Scalar(first, "SELECT fts_merge_threshold FROM pragma_fts_merge_threshold").Should().Be(SqlValue.Integer(0));
        Scalar(first, "SELECT count_changes FROM pragma_count_changes").Should().Be(SqlValue.Integer(1));

        Scalar(second, "SELECT foreign_keys FROM pragma_foreign_keys").Should().Be(SqlValue.Integer(0));
        Scalar(second, "SELECT fts_merge_threshold FROM pragma_fts_merge_threshold").Should().Be(SqlValue.Integer(32));
    }

    [Test]
    public void PragmaFunctionsAreReadOnlyAndRouteTheSchemaArgument()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("pragma-schema-main.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "ATTACH DATABASE 'pragma-schema-aux.db' AS aux;");
        Execute(connection, "CREATE TABLE aux.big(value BLOB); INSERT INTO aux.big VALUES (zeroblob(20000));");

        var mainPages = Scalar(connection, "SELECT page_count FROM pragma_page_count").AsInteger();
        var auxPages = Scalar(connection, "SELECT page_count FROM pragma_page_count('aux')").AsInteger();
        auxPages.Should().Be(ReadRows(connection, "PRAGMA aux.page_count")[0][0].AsInteger());
        auxPages.Should().BeGreaterThan(mainPages);
        Scalar(connection, "SELECT schema FROM pragma_page_count('aux')").Should().Be(SqlValue.Text("aux"));

        // A pragma function without an argument column cannot be used to set the pragma.
        var assign = () => ReadRows(connection, "SELECT * FROM pragma_user_version(5)");
        assign.Should().Throw<EmbeddedSqlException>();
        ReadRows(connection, "PRAGMA user_version")[0][0].Should().Be(SqlValue.Integer(0));
    }

    [Test]
    public void PragmaListNamesEveryRecognizedPragma()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        var names = ReadRows(connection, "PRAGMA pragma_list").Select(static row => row[0].AsText()).ToList();

        names.Should().StartWith(["application_id", "auto_vacuum", "busy_timeout"]);
        names.Should().Contain(["hexkey", "cipher", "mvcc_group_commit", "fts_merge_threshold", "vdbe_trace"]);
        names.Should().OnlyHaveUniqueItems();
        names.Should().NotContain("fullfsync", "Turso only defines it on Apple builds");

        // Every listed name is recognized rather than falling through to the no-op. Pragmas
        // that need an argument (table_info, ...) reject the bare form, which also proves it.
        foreach (var name in names)
        {
            Ahtola.Core.Parsing.ParsedStatement statement;
            try
            {
                statement = Ahtola.Core.Parsing.SqlParser.Parse(
                    $"PRAGMA {name}",
                    Ahtola.Core.Parsing.SqlParameterMap.Parse($"PRAGMA {name}"));
            }
            catch (EmbeddedSqlException)
            {
                continue;
            }

            if (name is not ("legacy_file_format" or "empty_result_callbacks" or "vdbe_trace"))
                statement.Should().NotBeOfType<Ahtola.Core.Parsing.PragmaNoOpStatement>(name);
        }

        var assign = () => Execute(connection, "PRAGMA pragma_list = 1");
        assign.Should().Throw<EmbeddedSqlException>();
    }

    [Test]
    public void AttachAndDetachAcceptStringAndExpressionNames()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("attach-names-main.db", fileSystem);
        using var connection = database.Connect();

        // A string literal is the name itself (Turso translate/attach.rs).
        Execute(connection, "ATTACH DATABASE 'attach-names-a.db' AS 'aux_a'");
        Execute(connection, "CREATE TABLE aux_a.items(id INTEGER)");
        Execute(connection, "DETACH DATABASE 'aux_a'");

        // Any other expression is evaluated, so a bound parameter can name the database.
        using (var attach = connection.Prepare("ATTACH DATABASE ?1 AS ?2"))
        {
            attach.Bind(1, SqlValue.Text("attach-names-a.db"));
            attach.Bind(2, SqlValue.Text("aux_" + "b"));
            attach.Step().Should().Be(StatementStepResult.Done);
        }
        ReadRows(connection, "SELECT name FROM aux_b.sqlite_schema").Should().ContainSingle();
        using (var detach = connection.Prepare("DETACH ?1"))
        {
            detach.Bind(1, SqlValue.Text("aux_b"));
            detach.Step().Should().Be(StatementStepResult.Done);
        }
        var gone = () => ReadRows(connection, "SELECT * FROM aux_b.items");
        gone.Should().Throw<EmbeddedSqlException>();

        Execute(connection, "ATTACH 'attach-names-a.db' AS 'aux' || '_c'");
        ReadRows(connection, "SELECT name FROM pragma_database_list WHERE name = 'aux_c'").Should().ContainSingle();
    }

    [Test]
    public void FtsMergeThresholdValidatesLikeTurso()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ReadRows(connection, "PRAGMA fts_merge_threshold")[0][0].Should().Be(SqlValue.Integer(32));
        Execute(connection, "PRAGMA fts_merge_threshold = 8");
        ReadRows(connection, "PRAGMA fts_merge_threshold")[0][0].Should().Be(SqlValue.Integer(8));
        var negative = () => Execute(connection, "PRAGMA fts_merge_threshold = -1");
        negative.Should().Throw<EmbeddedSqlException>()
            .WithMessage("fts_merge_threshold must be 0 (disabled) or a positive integer");
    }

    [Test]
    public void MvccGroupCommitRequiresMvccAndReportsThatCommitsAreNotBatched()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("group-commit.db", fileSystem);
        using var connection = database.Connect();

        var notMvcc = () => ReadRows(connection, "PRAGMA mvcc_group_commit");
        notMvcc.Should().Throw<EmbeddedSqlException>().WithMessage("MVCC not enabled");

        Execute(connection, "PRAGMA journal_mode = 'mvcc'");
        ReadRows(connection, "PRAGMA mvcc_group_commit")[0][0].Should().Be(SqlValue.Integer(0));
        Execute(connection, "PRAGMA mvcc_group_commit = OFF");
        var enable = () => Execute(connection, "PRAGMA mvcc_group_commit = ON");
        enable.Should().Throw<EmbeddedSqlException>().WithMessage("*not supported by the managed engine*");
    }

    [Test]
    public void ListTypesUsesTursoColumnsAndListsRegisteredTypes()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        connection.ExperimentalCustomTypesEnabled = true;
        Execute(connection, "CREATE TYPE counter BASE INTEGER");
        Execute(connection, "CREATE DOMAIN positive AS INTEGER DEFAULT 7 CHECK (value > 0)");

        var rows = ReadRows(connection, "PRAGMA list_types");
        rows.Select(static row => row[0].AsText())
            .Should().Equal("INTEGER", "REAL", "TEXT", "BLOB", "ANY", "counter", "positive");
        rows[5].Should().Equal(SqlValue.Text("counter"), SqlValue.Text("INTEGER"), SqlValue.Null, SqlValue.Null, SqlValue.Null, SqlValue.Null);
        rows[6].Should().Equal(SqlValue.Text("positive"), SqlValue.Text("INTEGER"), SqlValue.Null, SqlValue.Null, SqlValue.Text("7"), SqlValue.Null);
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        foreach (var part in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var statement = connection.Prepare(part);
            while (statement.Step() == StatementStepResult.Row)
            {
            }
        }
    }

    private static SqlValue Scalar(EmbeddedConnection connection, string sql)
        => ReadRows(connection, sql).Should().ContainSingle().Subject[0];

    private static List<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var row = new SqlValue[statement.GetColumnCount()];
            for (var index = 0; index < row.Length; index++)
                row[index] = statement.GetValue(index);
            rows.Add(row);
        }

        return rows;
    }
}
