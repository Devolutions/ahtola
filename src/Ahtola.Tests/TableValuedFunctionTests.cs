using System.Diagnostics;
using System.Globalization;
using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

/// <summary>
/// Covers the FROM-clause table-valued-function seam: module resolution, the hidden
/// argument columns, the <c>json_each</c>/<c>json_tree</c> column set and traversal order,
/// and the <c>pragma_*</c> introspection family.
/// </summary>
public class TableValuedFunctionTests
{
    [Test]
    public void JsonEachExposesSqliteColumnSetWithHiddenArguments()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Columns(connection, "SELECT * FROM json_each('[1]');")
            .Should().Equal("key", "value", "type", "atom", "id", "parent", "fullkey", "path");

        // json and root are hidden: addressable by name, never expanded by *.
        Rows(connection, "SELECT json, root FROM json_each('[1,2]');")
            .Should().Equal(["[1,2]|$", "[1,2]|$"]);
        Rows(connection, "SELECT json, root FROM json_each('{\"a\":1}', '$.a');")
            .Should().Equal(["{\"a\":1}|$.a"]);
    }

    [Test]
    public void JsonEachMatchesSqliteTraversal()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Rows(connection, "SELECT key, value, type, atom, fullkey, path FROM json_each('[1,\"a\",null]');")
            .Should().Equal(
            [
                "0|1|integer|1|$[0]|$",
                "1|a|text|a|$[1]|$",
                "2||null||$[2]|$",
            ]);

        Rows(connection, "SELECT key, value, type, atom, fullkey, path FROM json_each('{\"a\":1,\"b\":[2]}');")
            .Should().Equal(
            [
                "a|1|integer|1|$.a|$",
                "b|[2]|array||$.b|$",
            ]);

        // A scalar root yields one keyless row whose fullkey is the root itself.
        Rows(connection, "SELECT key, value, fullkey, path FROM json_each('\"hi\"');")
            .Should().Equal(["|hi|$|$"]);

        Rows(connection, "SELECT count(*) FROM json_each(NULL);").Should().Equal(["0"]);
        Rows(connection, "SELECT count(*) FROM json_each('[1,2]', '$.missing');").Should().Equal(["0"]);
    }

    [Test]
    public void JsonTreeVisitsRootThenDescendsInPreOrder()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Rows(
                connection,
                "SELECT key, value, type, atom, id, parent, fullkey, path "
                + "FROM json_tree('{\"a\":1,\"b\":[2,3],\"c\":{\"d\":null}}');")
            .Should().Equal(
            [
                "|{\"a\":1,\"b\":[2,3],\"c\":{\"d\":null}}|object||0||$|$",
                "a|1|integer|1|1|0|$.a|$",
                "b|[2,3]|array||2|0|$.b|$",
                "0|2|integer|2|3|2|$.b[0]|$.b",
                "1|3|integer|3|4|2|$.b[1]|$.b",
                "c|{\"d\":null}|object||5|0|$.c|$",
                "d||null||6|5|$.c.d|$.c",
            ]);

        // The root row reports the last path element as its key and the parent path as path.
        Rows(connection, "SELECT key, fullkey, path FROM json_tree('{\"a\":{\"x\":1}}', '$.a');")
            .Should().Equal(["a|$.a|$", "x|$.a.x|$.a"]);
    }

    [Test]
    public void JsonPathKeysAreQuotedLikeSqlite()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        // SQLite leaves a label bare only when it starts with an ASCII letter and continues
        // with ASCII letters or digits; '_' and a leading digit force the quoted form.
        Rows(
                connection,
                "SELECT fullkey FROM json_each('{\"ab\":1,\"A9\":2,\"a_1\":3,\"9a\":4,\"\":5,\"a b\":6}');")
            .Should().Equal(["$.ab", "$.A9", "$.\"a_1\"", "$.\"9a\"", "$.\"\"", "$.\"a b\""]);
    }

    [Test]
    public void JsonTraversalRejectsMalformedInput()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Action malformed = () => Rows(connection, "SELECT * FROM json_each('not json');");
        malformed.Should().Throw<EmbeddedSqlException>().WithMessage("malformed JSON");

        Action badPath = () => Rows(connection, "SELECT * FROM json_each('[1]', 'a');");
        badPath.Should().Throw<EmbeddedSqlException>().WithMessage("bad JSON path: 'a'");
    }

    [Test]
    public void PragmaFunctionsExposeTheStatementResultColumns()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT NOT NULL DEFAULT 'x');");
        Execute(connection, "CREATE UNIQUE INDEX t_b ON t(b);");

        Columns(connection, "SELECT * FROM pragma_table_info('t');")
            .Should().Equal("cid", "name", "type", "notnull", "dflt_value", "pk");
        Rows(connection, "SELECT * FROM pragma_table_info('t');")
            .Should().Equal(["0|a|INTEGER|0||1", "1|b|TEXT|1|'x'|0"]);

        Columns(connection, "SELECT * FROM pragma_table_xinfo('t');")
            .Should().Equal("cid", "name", "type", "notnull", "dflt_value", "pk", "hidden");
        Columns(connection, "SELECT * FROM pragma_index_list('t');")
            .Should().Equal("seq", "name", "unique", "origin", "partial");
        Columns(connection, "SELECT * FROM pragma_index_info('t_b');")
            .Should().Equal("seqno", "cid", "name");
        Columns(connection, "SELECT * FROM pragma_index_xinfo('t_b');")
            .Should().Equal("seqno", "cid", "name", "desc", "coll", "key");
        Columns(connection, "SELECT * FROM pragma_foreign_key_list('t');")
            .Should().Equal("id", "seq", "table", "from", "to", "on_update", "on_delete", "match");
        Columns(connection, "SELECT * FROM pragma_table_list();")
            .Should().Equal("schema", "name", "type", "ncol", "wr", "strict");

        // The object-name argument stays addressable as the hidden 'arg' column.
        Rows(connection, "SELECT DISTINCT arg FROM pragma_table_info('t');").Should().Equal(["t"]);
        Rows(connection, "SELECT count(*) FROM pragma_table_info('no_such_table');").Should().Equal(["0"]);
    }

    [Test]
    public void PragmaTableInfoDescribesRegisteredModules()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Rows(connection, "SELECT name FROM pragma_table_info('generate_series');")
            .Should().Equal(["value"]);
        Rows(connection, "SELECT name, hidden FROM pragma_table_xinfo('json_each');")
            .Should().Equal(
            [
                "key|0", "value|0", "type|0", "atom|0", "id|0", "parent|0", "fullkey|0", "path|0",
                "json|1", "root|1",
            ]);
    }

    [Test]
    public void PragmaEponymousModulesExposeSchemaAndFunctionMetadata()
    {
        using var connection = new EmbeddedDatabase().Connect();

        Rows(connection, "PRAGMA table_info(sqlite_schema);")
            .Should().Equal(
            [
                "0|type|TEXT|0||0",
                "1|name|TEXT|0||0",
                "2|tbl_name|TEXT|0||0",
                "3|rootpage|INT|0||0",
                "4|sql|TEXT|0||0",
            ]);
        Rows(
                connection,
                "SELECT name, builtin, type, enc, narg, flags "
                + "FROM pragma_function_list WHERE name = 'abs';")
            .Should().Equal(["abs|1|s|utf8|1|2099200"]);
        Rows(connection, "SELECT name FROM pragma_module_list WHERE name = 'generate_series';")
            .Should().Equal(["generate_series"]);
        Rows(
                connection,
                "SELECT name FROM pragma_module_list "
                + "WHERE name IN ('fts5', 'rtree', 'rtree_i32') ORDER BY name;")
            .Should().Equal(["fts5", "rtree", "rtree_i32"]);
        Rows(connection, "SELECT name FROM pragma_function_list WHERE name = 'is_autocommit';")
            .Should().BeEmpty();
        Rows(connection, "PRAGMA table_info(sqlite_temp_schema);")
            .Should().HaveCount(5);
        Rows(connection, "PRAGMA main.table_info(sqlite_temp_schema);")
            .Should().BeEmpty();
        Rows(connection, "SELECT * FROM pragma_table_info('sqlite_temp_schema', 'main');")
            .Should().BeEmpty();

        Action unsupported = () => connection.Prepare("SELECT * FROM pragma_wal_checkpoint();");
        unsupported.Should().Throw<EmbeddedSqlException>().WithMessage("no such table: pragma_wal_checkpoint*");
    }

    [Test]
    public void PragmaFunctionListRetainsRegisteredFunctionsThroughCtas()
    {
        using var database = new EmbeddedDatabase();
        database.RegisterScalarFunction("registered_scalar", 0, _ => SqlValue.Integer(1));
        using var connection = database.Connect();

        Execute(
            connection,
            "CREATE TABLE function_snapshot AS "
            + "SELECT name FROM pragma_function_list WHERE name = 'registered_scalar';");
        Execute(
            connection,
            "CREATE TABLE series_snapshot AS "
            + "SELECT value FROM generate_series(2, 4);");
        Execute(
            connection,
            "CREATE TABLE table_snapshot AS "
            + "SELECT name FROM pragma_table_list() WHERE name = 'series_snapshot';");

        Rows(connection, "SELECT name FROM function_snapshot;")
            .Should().Equal(["registered_scalar"]);
        Rows(connection, "SELECT value FROM series_snapshot ORDER BY value;")
            .Should().Equal(["2", "3", "4"]);
        Rows(connection, "SELECT name FROM table_snapshot;")
            .Should().Equal(["series_snapshot"]);
    }

    [Test]
    public void GenerateSeriesAcceptsSqliteArgumentForms()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Rows(connection, "SELECT value FROM generate_series(1,5);").Should().Equal(["1", "2", "3", "4", "5"]);
        Rows(connection, "SELECT value FROM generate_series(1,10,3);").Should().Equal(["1", "4", "7", "10"]);
        Rows(connection, "SELECT value FROM generate_series(3,1,-1);").Should().Equal(["3", "2", "1"]);
        Rows(connection, "SELECT value FROM generate_series(5,1,-2);").Should().Equal(["5", "3", "1"]);
        Rows(connection, "SELECT value FROM generate_series(1) LIMIT 3;").Should().Equal(["1", "2", "3"]);
        Rows(connection, "SELECT start, stop, step FROM generate_series(1,2);").Should().Equal(["1|2|1", "1|2|1"]);
        Rows(connection, "SELECT start, stop, step FROM generate_series(1) LIMIT 1;")
            .Should().Equal(["1|4294967295|1"]);

        // A zero step counts by one rather than erroring or looping forever, and the
        // hidden step column reports the effective 1.
        Rows(connection, "SELECT value FROM generate_series(1,3,0);").Should().Equal(["1", "2", "3"]);
        Rows(connection, "SELECT start, stop, step FROM generate_series(1,3,0) LIMIT 1;")
            .Should().Equal(["1|3|1"]);
        Rows(connection, "SELECT count(*) FROM generate_series(3,1,0);").Should().Equal(["0"]);

        // The named form binds the same hidden slots as the positional form.
        Rows(connection, "SELECT value FROM generate_series WHERE start=1 AND stop=3;")
            .Should().Equal(["1", "2", "3"]);
        Rows(connection, "SELECT value FROM generate_series WHERE start=1 AND stop=10 AND step=3;")
            .Should().Equal(["1", "4", "7", "10"]);
    }

    /// <summary>
    /// A module row loop must observe cancellation promptly. <c>generate_series</c> is
    /// unbounded, so without an interrupt poll inside the loop the statement only fails once
    /// the entire series has been materialised.
    /// </summary>
    [Test]
    public void AnInFlightSeriesStopsWhenTheStatementIsCancelled()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        // The WHERE clause suppresses the source row cap, so the module materialises the
        // whole series rather than stopping at a LIMIT. Step rejects an already-cancelled
        // token up front, so the token has to fire while the row loop is running.
        using var statement = connection.Prepare(
            "SELECT count(*) FROM generate_series(1, 50000000) WHERE value > 2;");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var elapsed = Stopwatch.StartNew();
        Action step = () => statement.Step(cancellation.Token);
        step.Should().Throw<OperationCanceledException>();
        elapsed.Stop();

        // Enclosing machinery notices the token once the source finishes, so the exception
        // alone does not prove the loop yielded. Generating all 50,000,000 rows takes tens of
        // seconds, so completing well inside that is what shows cancellation was observed
        // mid-loop rather than after the fact.
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// SQLite lets a table-valued function argument reference a column of an earlier
    /// <c>FROM</c> entry (an implicit <c>LATERAL</c>), re-evaluating the source per outer
    /// row. The managed join must preserve that evaluation boundary rather than materializing
    /// the function once before the left row is available.
    /// </summary>
    [Test]
    public void CorrelatedModuleArgumentsReevaluateForEachLeftRow()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE t(a, b);");
        Execute(connection, "INSERT INTO t VALUES (1, '[7]'), (2, '[8,9]');");

        // A constant argument is unaffected: the module still runs for every outer row.
        Rows(connection, "SELECT value FROM t JOIN json_each('[7]') AS j;")
            .Should().Equal(["7", "7"]);

        Rows(connection, "SELECT value FROM t, json_each(t.b);")
            .Should().Equal(["7", "8", "9"]);
        Rows(connection, "SELECT value FROM t JOIN json_each(t.b) AS j;")
            .Should().Equal(["7", "8", "9"]);
        Rows(connection, "SELECT s.value FROM t JOIN generate_series(1, t.a) AS s;")
            .Should().Equal(["1", "1", "2"]);
    }

    [Test]
    public void PragmaTableInfoCanDependOnEitherJoinSide()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE users(id, name);");
        Execute(connection, "CREATE TABLE products(id, price);");

        var expected = new[] { "users|id", "users|name", "products|id", "products|price" };
        Rows(
                connection,
                "SELECT s.name, ti.name FROM sqlite_schema AS s, pragma_table_info(s.name) AS ti;")
            .Should().Equal(expected);
        Rows(
                connection,
                "SELECT s.name, ti.name FROM pragma_table_info(s.name) AS ti, sqlite_schema AS s;")
            .Should().Equal(expected);
        Rows(
                connection,
                "SELECT ti.name FROM (SELECT 'users' AS table_name) AS t "
                + "JOIN pragma_table_info AS ti ON ti.arg = lower(t.table_name);")
            .Should().Equal(["id", "name"]);
        Rows(connection, "SELECT ti.name FROM pragma_table_info AS ti WHERE arg = name;")
            .Should().BeEmpty();
    }

    [Test]
    public void FileBackedSchemaTablePreservesTableCreationOrder()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("schema-order.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE users(id, name);");
            Execute(connection, "CREATE TABLE products(id, price);");
        }

        using var reopened = EmbeddedDatabase.OpenFile("schema-order.db", fileSystem);
        using var reader = reopened.Connect();
        Rows(reader, "SELECT name FROM sqlite_schema WHERE type = 'table';")
            .Should().Equal(["users", "products"]);
    }

    [Test]
    public void UnregisteredModuleNamesAreRejected()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Action unknown = () => connection.Prepare("SELECT * FROM rtree_i32('a');");
        unknown.Should().Throw<EmbeddedSqlException>().WithMessage(
            "Managed table-valued source 'rtree_i32' is not supported: "
            + "no module registration, planner, or execution contract is available.*");

        Action tooMany = () => connection.Prepare("SELECT * FROM json_each('[1]','$',3);");
        tooMany.Should().Throw<EmbeddedSqlException>()
            .WithMessage("too many arguments on json_each() - max 2*");
    }

    /// <summary>
    /// SQLite's eponymous virtual tables can be named without parentheses, with the hidden
    /// argument columns supplied by WHERE equality terms instead.
    /// </summary>
    [Test]
    public void BareModuleNamesBindArgumentsFromWhereEqualities()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY, code TEXT UNIQUE);");
        Execute(
            connection,
            "CREATE TABLE child(parent_id REFERENCES parent(id), code_ref REFERENCES parent(code));");

        Rows(connection, "SELECT arg, \"from\", \"to\" FROM pragma_foreign_key_list WHERE arg = 'child' ORDER BY id, seq;")
            .Should().Equal(["child|code_ref|code", "child|parent_id|id"]);

        Rows(connection, "SELECT cid, name FROM pragma_table_info WHERE arg = 'parent';")
            .Should().Equal(["0|id", "1|code"]);

        Rows(connection, "SELECT count(*), max(value) FROM generate_series WHERE start = 4294967290;")
            .Should().Equal(["6|4294967295"]);

        Rows(connection, "SELECT key, value FROM json_each WHERE json = '[7,8]';")
            .Should().Equal(["0|7", "1|8"]);

        // A bare module name with no usable constraint reports no rows rather than failing.
        Rows(connection, "SELECT count(*) FROM pragma_table_info;").Should().Equal(["0"]);
        Rows(connection, "SELECT count(*) FROM generate_series(NULL, 5);").Should().Equal(["0"]);

        Rows(connection, "SELECT schema, name, type, ncol, wr, strict FROM pragma_table_list WHERE name = 'child';")
            .Should().Equal(["main|child|table|2|0|0"]);
    }

    [Test]
    public void PragmaTableListUsesTheConnectionSchemaSet()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "CREATE TEMP TABLE t(value TEXT);");

        Rows(
                connection,
                "SELECT schema, name, type, ncol, wr, strict FROM pragma_table_list "
                + "WHERE schema = 'temp';")
            .Should().Equal(["temp|sqlite_temp_schema|table|5|0|0", "temp|t|table|1|0|0"]);
        Rows(
                connection,
                "SELECT schema, name, type, ncol, wr, strict FROM pragma_table_list('t') ORDER BY schema;")
            .Should().Equal(["main|t|table|1|0|0", "temp|t|table|1|0|0"]);
    }

    /// <summary>
    /// A real table always wins over a module registration, so registering a module can
    /// never shadow user data.
    /// </summary>
    [Test]
    public void RealObjectsShadowBareModuleNames()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE generate_series(value TEXT);");
        Execute(connection, "INSERT INTO generate_series VALUES ('from the table');");
        Rows(connection, "SELECT value FROM generate_series;").Should().Equal(["from the table"]);

        // SQLite rejects the parenthesised form once a real table shadows the module name
        // ("'generate_series' is not a function"); the managed engine resolves the call site
        // at parse time and still reaches the module. Only the bare form consults the catalog.
        Rows(connection, "SELECT value FROM generate_series(1, 3);").Should().Equal(["1", "2", "3"]);

        Execute(connection, "CREATE VIEW pragma_table_list AS SELECT 'shadowed' AS name;");
        Rows(connection, "SELECT name FROM pragma_table_list;").Should().Equal(["shadowed"]);

        Rows(
            connection,
            "WITH json_each(key) AS (SELECT 'cte') SELECT key FROM json_each;")
            .Should().Equal(["cte"]);
    }

    private static void Execute(EmbeddedConnection connection, string sql)
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

    private static string[] Columns(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var count = statement.GetColumnCount();
        var columns = new string[count];
        for (var index = 0; index < count; index++)
            columns[index] = statement.GetColumnName(index);

        return columns;
    }

    private static string[] Rows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<string>();
        while (statement.Step() == StatementStepResult.Row)
        {
            rows.Add(string.Join(
                "|",
                Enumerable.Range(0, statement.GetColumnCount()).Select(index => Render(statement.GetValue(index)))));
        }

        return [.. rows];
    }

    private static string Render(SqlValue value)
        => value.Kind switch
        {
            SqlValueKind.Null => string.Empty,
            SqlValueKind.Integer => value.AsInteger().ToString(CultureInfo.InvariantCulture),
            SqlValueKind.Real => value.AsReal().ToString("0.####", CultureInfo.InvariantCulture),
            SqlValueKind.Text => value.AsText(),
            _ => Convert.ToHexString(value.AsBlob().Span),
        };
}
